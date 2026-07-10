using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DCommon;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;
using Color = Agwinterm.Core.Color;

namespace Agwinterm.Win32;

/// <summary>Services: notifications, taskbar progress, config, theming, omp, persistence, fullscreen.</summary>
internal partial class Program
{
    // ---- Screen-reader output announcements (UIA notification events; T2-14 Stage 1.5) ----

    /// <summary>Speak the active pane's NEW output since the last announcement (debounced via
    /// UiaAnnounceTimer; only runs while a UIA client listens). Tracks an absolute buffer line
    /// (history + cursor row) per pane; the delta rows are read as plain text. Alt-screen apps
    /// (full-screen TUIs redrawing in place) are skipped — line diffs there are meaningless.</summary>
    private void AnnounceNewOutput()
    {
        if (!Uia.ClientsListening) return;
        var p = ActiveSurface();
        if (p is null) return;
        string text;
        lock (p.S.SyncRoot)
        {
            var em = p.S.Emulator;
            if (em.IsAltScreen) { p.UiaAnnouncedAbs = -1; return; }
            int curAbs = em.HistoryCount + em.CursorRow;
            int prev = p.UiaAnnouncedAbs;
            p.UiaAnnouncedAbs = curAbs;
            if (prev < 0 || prev >= curAbs) return;             // first sight / scroll trim — set baseline only
            int lines = Math.Min(curAbs - prev, 40);            // cap floods; the reader can't keep up anyway
            var sb = new StringBuilder();
            for (int abs = curAbs - lines; abs < curAbs; abs++)
            {
                string row = abs < em.HistoryCount ? em.DumpHistoryRow(abs) : em.DumpRow(abs - em.HistoryCount);
                if (row.Length > 0) sb.Append(row).Append('\n');
            }
            text = sb.ToString();
        }
        Uia.RaiseTextChanged();   // let the reader's text model re-read (navigation)
        Uia.Announce(text);       // and speak the new lines directly (reliable across readers)
    }

    // ---- Notifications (OSC 9 / OSC 777 / notify) ----

    /// <summary>Find the session that owns a pane (any pane, its scratch, or its overlay; or the quick cover).</summary>
    private Ses? OwningSes(Pane p)
    {
        lock (_workspaces)
            foreach (var w in _workspaces)
                foreach (var s in w.Sessions)
                    if (s.Panes.Contains(p) || ReferenceEquals(s.Scratch, p) || ReferenceEquals(s.Overlay, p))
                        return s;
        return null;
    }

    /// <summary>A pane raised a desktop notification. Runs on the UI thread (marshaled from the pump).</summary>
    private void OnNotified(Pane p, string title, string body)
    {
        var ses = OwningSes(p);
        // Count it against the session unless you're looking at that pane right now (app focused AND it's
        // the active surface). If the app is in the background, even the active pane accrues a badge — which
        // then clears on refocus (agterm #164).
        if (!ReferenceEquals(p, ActiveSurface()) || !_windowActive) p.Unread++;
        string label = string.IsNullOrEmpty(title) ? body : $"{title}: {body}";
        _toastText = label.Length == 0 ? "(notification)" : label;
        _toastTarget = ses;                       // clicking the banner jumps to the raising session
        SetTimer(_hwnd, (IntPtr)2, 4000, IntPtr.Zero);
        if (_config.DesktopNotifications) TrayNotify(title, body);
        RequestRedraw();
    }

    /// <summary>Total unread notifications across a session's panes (for the sidebar badge).</summary>
    private static int UnreadOf(Ses s) { int n = 0; foreach (var p in s.Panes) n += p.Unread; return n; }

    /// <summary>Session status for display: the most attention-worthy status across ALL panes
    /// (Blocked &gt; Completed &gt; Active &gt; Idle) — a background pane's state must not be invisible.</summary>
    private static AgentStatus AggStatus(Ses s)
    {
        static int Sev(AgentStatus a) => a switch { AgentStatus.Blocked => 3, AgentStatus.Completed => 2, AgentStatus.Active => 1, _ => 0 };
        AgentStatus best = AgentStatus.Idle;
        foreach (var p in s.Panes) if (Sev(p.S.Status) > Sev(best)) best = p.S.Status;
        return best;
    }

    /// <summary>True if any pane of the session has an attention-worthy status with blink set.</summary>
    private static bool AggBlink(Ses s)
    {
        foreach (var p in s.Panes)
            if (p.S.Blink && p.S.Status is AgentStatus.Blocked or AgentStatus.Active or AgentStatus.Completed) return true;
        return false;
    }

    private static void ClearUnread(Ses s) { foreach (var p in s.Panes) p.Unread = 0; if (s.Scratch is not null) s.Scratch.Unread = 0; if (s.Overlay is not null) s.Overlay.Unread = 0; }

    // ---- Taskbar progress (OSC 9;4, ConEmu/Windows Terminal convention) ----
    // Last-writer-wins across sessions: the most recent report drives the window's taskbar icon.
    private ITaskbarList3? _taskbar;

    private void OnProgress(int state, int value)
    {
        try
        {
            if (_taskbar is null)
            {
                _taskbar = (ITaskbarList3)new TaskbarListCom();
                _taskbar.HrInit();
            }
            int flags = state switch { 1 => TBPF_NORMAL, 2 => TBPF_ERROR, 3 => TBPF_INDETERMINATE, 4 => TBPF_PAUSED, _ => TBPF_NOPROGRESS };
            _taskbar.SetProgressState(_hwnd, flags);
            if (flags is TBPF_NORMAL or TBPF_ERROR or TBPF_PAUSED)
                _taskbar.SetProgressValue(_hwnd, (ulong)value, 100);
        }
        catch { /* taskbar progress is cosmetic */ }
    }

    /// <summary>Show an OS desktop notification via a Shell_NotifyIcon tray balloon (no AUMID/shortcut needed).</summary>
    private void TrayNotify(string title, string body)
    {
        try
        {
            var d = new NOTIFYICONDATAW
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_INFO | (_trayAdded ? 0u : (NIF_ICON | NIF_TIP)),
                hIcon = _trayAdded ? IntPtr.Zero : LoadIconW(IntPtr.Zero, (IntPtr)32512), // IDI_APPLICATION
                szTip = "agwinterm",
                szInfoTitle = string.IsNullOrEmpty(title) ? "agwinterm" : title,
                szInfo = body.Length == 0 ? " " : body,
                dwInfoFlags = 0,
            };
            if (!_trayAdded) { _trayAdded = Shell_NotifyIconW(NIM_ADD, ref d); }
            Shell_NotifyIconW(NIM_MODIFY, ref d);
        }
        catch { /* balloon is best-effort; the in-app banner + badge are the reliable surface */ }
    }

    private void RemoveTrayIcon()
    {
        if (!_trayAdded) return;
        try { var d = new NOTIFYICONDATAW { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATAW>(), hWnd = _hwnd, uID = 1 }; Shell_NotifyIconW(NIM_DELETE, ref d); } catch { }
        _trayAdded = false;
    }

    private static string SafeCwd(Ses s) { lock (s.S.SyncRoot) return s.S.Emulator.Cwd; }
    private static string SafeCwd(Pane p) { lock (p.S.SyncRoot) return p.S.Emulator.Cwd; }

    /// <summary>An existing directory for a session's "current" dir: live OSC-7 cwd if valid, else its launch dir.</summary>
    private static string? CurrentDirOf(Ses s)
    {
        var live = SafeCwd(s);
        if (!string.IsNullOrWhiteSpace(live)) { var p = PrettyCwd(live); if (Directory.Exists(p)) return p; }
        return s.ActivePane.StartCwd;
    }

    /// <summary>The path shown in the title bar for a session: live OSC 7 cwd if the shell reports it,
    /// else the pane's launch dir, else the process cwd — so a real path always shows, out of the box.</summary>
    private static string TitleCwd(Ses s)
    {
        string live = SafeCwd(s);
        if (!string.IsNullOrWhiteSpace(live)) return PrettyCwd(live);
        string? start = s.ActivePane.StartCwd;
        return string.IsNullOrWhiteSpace(start) ? Environment.CurrentDirectory : start!;
    }

    /// <summary>Title-bar display name (agterm precedence): custom name → program OSC title → cwd basename → app name.</summary>
    private static string SessionDisplayName(Ses s)
    {
        if (!string.IsNullOrWhiteSpace(s.CustomName)) return s.CustomName!;
        // A program's OSC title (e.g. "claude") — but ignore the shell's default console title,
        // which on Windows is the bare exe path (…\powershell.exe) or an absolute path (noise).
        string osc = s.ActivePane.S.Emulator.Title;
        bool oscMeaningful = !string.IsNullOrWhiteSpace(osc)
            && !osc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !(osc.Length >= 2 && osc[1] == ':' && osc.Contains('\\')); // not a bare C:\… path
        if (oscMeaningful) return osc;
        // Single-row title = the full current path (agterm-style "just the path"), home collapsed to ~.
        string cwd = PrettyCwd(TitleCwd(s)).TrimEnd('\\', '/');
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(home))
        {
            if (string.Equals(cwd, home, StringComparison.OrdinalIgnoreCase)) return "~";
            if (cwd.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase)) return "~" + cwd[home.Length..];
        }
        return string.IsNullOrWhiteSpace(cwd) ? "agwinterm" : cwd;
    }

    private static string PrettyCwd(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string s = raw;
        if (s.StartsWith("file://")) { s = s[7..]; int slash = s.IndexOf('/'); if (slash > 0) s = s[slash..]; }
        try { s = Uri.UnescapeDataString(s); } catch { }
        return s.TrimStart('/').Replace('/', '\\');
    }

    private static float MeasureText(string s, IDWriteTextFormat fmt)
    {
        using var tl = _dwrite.CreateTextLayout(s, fmt, 4096f, 100f);
        return tl.Metrics.Width;
    }

    // Segoe Fluent Icons glyphs (present on Win11).
    private const string GlyphSidebar = "";  // GlobalNavButton (hamburger)
    private const string GlyphBell = "";     // Ringer
    private const string GlyphTerminal = ""; // CommandPrompt (quick terminal)
    private const string GlyphGear = "";     // Settings
    private static readonly string GlyphAdd = ((char)0xE710).ToString();   // Add (add-session)
    private static readonly string GlyphClose = ((char)0xE8BB).ToString(); // ChromeClose (pill x)

    private void DrawTitleBar(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        _titleButtons.Clear();
        if (ToolbarHidden) return;   // hidden toolbar: no chrome at all (full-bleed terminal)
        int cw = ClientW();
        brush.Color = ChromeBg;
        rt.FillRectangle(new Rect(0, 0, cw, TitleBarH), brush);

        // 1. App-mark logo at the far left (vector rendition of the app icon).
        DrawLogo(rt, brush, 10f, TitleBarH / 2f);

        // 2. Sidebar toggle at the sidebar column's right edge (tracks _sidebarW), or right of the logo when hidden.
        float togW = 36f;
        float togX = _sidebarW > 0 ? MathF.Max(34f, _sidebarW - togW - 2f) : 34f;
        {
            var c = ChromeBtnBg(rt, brush, togX, 0, togW, TitleBarH, "toggle", _titleButtons, ChromeText);
            brush.Color = c;
            rt.DrawText(GlyphSidebar, _iconFont, new Rect(togX, 0, togW, TitleBarH), brush);
        }

        // 6. Right group (pinned left of the caption buttons): scratch, split, | , quick-terminal, gear.
        float bw = 38f;
        float rgRight = cw - 3 * CaptionBtnW - 6f;   // right edge of the gear
        float gearX = rgRight - bw;
        float quickX = gearX - bw;
        float divX = quickX - 5f;                    // hairline divider in the gap
        float splitX = quickX - 10f - bw;
        float scratchX = splitX - bw;

        // 3. Title at the terminal's leading edge (right of the sidebar): a SINGLE centered row
        // showing the path (agterm-style): custom name -> program OSC title -> full cwd path.
        string title = _active is not null ? SessionDisplayName(_active) : "agwinterm";
        // 4. Attention bell (can be hidden via settings; when hidden it reserves no space).
        bool showBell = _config.AttentionButton;
        float titleX = _sidebarW > 0 ? _sidebarW + 10f : togX + togW + 8f;
        float bellW = showBell ? 34f : 0f, bellGap = showBell ? 8f : 0f;
        float titleAvail = scratchX - 14f - bellW - bellGap - titleX;
        float titleMeasured = MeasureText(title, _uiFont);
        float titleW = MathF.Max(30f, MathF.Min(titleMeasured, titleAvail));
        brush.Color = ChromeText;
        rt.DrawText(title, _uiTitle, new Rect(titleX, 0f, titleW, TitleBarH), brush);  // one vertically-centered, ellipsized row
        float pillX = titleX + titleW + 10f;
        void Pill(string label, Color4 bg)   // small status pill after the title
        {
            float w = MeasureText(label, _uiSmall) + 14f;
            brush.Color = bg;
            rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(pillX, (TitleBarH - 20f) / 2f, w, 20f), RadiusX = 5f, RadiusY = 5f }, brush);
            brush.Color = new Color4(1f, 1f, 1f, 1f);
            rt.DrawText(label, _uiSmall, new Rect(pillX + 7f, (TitleBarH - 20f) / 2f + 2f, w, 16f), brush);
            pillX += w + 6f;
        }
        if (_broadcast) Pill("BROADCAST", new Color4(0.85f, 0.25f, 0.25f, 0.95f));  // typing fans out to the workspace
        if (ActiveSurface() is { ReadOnly: true }) Pill("READ-ONLY", new Color4(0.45f, 0.45f, 0.5f, 0.95f));

        // dim = nothing, plain = active/completed, blocked-color = any blocked (uses the configured status color).
        if (showBell)
        {
            float bellX = MathF.Min(titleX + titleW + bellGap, scratchX - bellW - 14f);
            var (bellBlocked, bellActive) = AttentionState();
            var bellBase = bellBlocked ? StatusDot(AgentStatus.Blocked) : (bellActive ? ChromeText : ChromeDim);
            if ((bellBlocked || bellActive) && !_cursorOn && AnyBlinkAttention())
                bellBase = new Color4(bellBase.R, bellBase.G, bellBase.B, 0.30f); // pulse when a blinking session needs attention
            var c = ChromeBtnBg(rt, brush, bellX, 0, bellW, TitleBarH, "attention", _titleButtons, bellBase);
            DrawBellGlyph(rt, brush, bellX + bellW / 2f, TitleBarH / 2f, c);
        }

        // scratch (rounded rectangle; filled when active)
        bool scratchOn = _coverKind == 1;
        {
            var c = ChromeBtnBg(rt, brush, scratchX, 0, bw, TitleBarH, "scratch", _titleButtons, scratchOn ? ChromeAccent : ChromeDim);
            DrawScratchGlyph(rt, brush, scratchX + bw / 2f, TitleBarH / 2f, c, scratchOn);
        }
        // split (two panes, reflects split state)
        bool splitOn = _active is not null && _active.Panes.Count > 1;
        {
            var c = ChromeBtnBg(rt, brush, splitX, 0, bw, TitleBarH, "split", _titleButtons, splitOn ? ChromeAccent : ChromeDim);
            DrawSplitGlyph(rt, brush, splitX + bw / 2f, TitleBarH / 2f, c, splitOn);
        }
        // hairline divider between per-session toggles and the window-level quick terminal
        brush.Color = WithA(ChromeText, 0.25f);
        rt.DrawLine(new System.Numerics.Vector2(divX, 12f), new System.Numerics.Vector2(divX, TitleBarH - 12f), brush, 1f);
        // quick terminal (accent when active)
        bool quickOn = _coverKind == 2;
        {
            var c = ChromeBtnBg(rt, brush, quickX, 0, bw, TitleBarH, "quick terminal", _titleButtons, quickOn ? ChromeAccent : ChromeDim);
            brush.Color = c;
            rt.DrawText(GlyphTerminal, _iconFont, new Rect(quickX, 0, bw, TitleBarH), brush);
        }
        // gear (settings) — kept per user's choice
        {
            var c = ChromeBtnBg(rt, brush, gearX, 0, bw, TitleBarH, "settings", _titleButtons, ChromeDim);
            brush.Color = c;
            rt.DrawText(GlyphGear, _iconFont, new Rect(gearX, 0, bw, TitleBarH), brush);
        }

        DrawCaption(rt, brush, cw);
    }

    /// <summary>Vector app-mark: cyan terminal chevron + block cursor + green agent-status dot (matches the app icon).</summary>
    private void DrawLogo(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float x, float cy)
    {
        var cyan = new Color4(0x38 / 255f, 0xD7 / 255f, 0xF0 / 255f, 1f);
        var cyanLt = new Color4(0x8A / 255f, 0xE2 / 255f, 0xF2 / 255f, 1f);
        var green = new Color4(0x3D / 255f, 0xDC / 255f, 0x84 / 255f, 1f);
        brush.Color = cyan;
        float top = cy - 6f, mid = cy, bot = cy + 6f, lx = x + 1f, rx = x + 7f;
        rt.DrawLine(new System.Numerics.Vector2(lx, top), new System.Numerics.Vector2(rx, mid), brush, 2.2f);
        rt.DrawLine(new System.Numerics.Vector2(rx, mid), new System.Numerics.Vector2(lx, bot), brush, 2.2f);
        brush.Color = cyanLt;
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(x + 10f, cy + 1f, 7f, 4.5f), RadiusX = 1.5f, RadiusY = 1.5f }, brush);
        brush.Color = green;
        rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(x + 15.5f, cy - 5f), 2.6f, 2.6f), brush);
    }

    /// <summary>Scratch glyph: a rounded rectangle (outline, or filled when active).</summary>
    private void DrawScratchGlyph(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float cx, float cy, Color4 color, bool filled)
    {
        brush.Color = color;
        var r = new RoundedRectangle { Rect = new Rect(cx - 7f, cy - 5.5f, 14f, 11f), RadiusX = 2.5f, RadiusY = 2.5f };
        if (filled) rt.FillRoundedRectangle(r, brush); else rt.DrawRoundedRectangle(r, brush, 1.4f);
    }

    /// <summary>Attention bell glyph (plain outline bell + clapper). Never slashed; color conveys state.</summary>
    private void DrawBellGlyph(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float cx, float cy, Color4 color)
    {
        brush.Color = color;
        var g = _d2d.CreatePathGeometry();
        using (var sink = g.Open())
        {
            sink.BeginFigure(new System.Numerics.Vector2(cx - 5.5f, cy + 3.5f), FigureBegin.Hollow); // bottom-left rim
            sink.AddLine(new System.Numerics.Vector2(cx - 4f, cy - 1.5f));                            // left wall
            sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = new System.Numerics.Vector2(cx - 4f, cy - 6.2f), Point2 = new System.Numerics.Vector2(cx, cy - 6.2f) });
            sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = new System.Numerics.Vector2(cx + 4f, cy - 6.2f), Point2 = new System.Numerics.Vector2(cx + 4f, cy - 1.5f) });
            sink.AddLine(new System.Numerics.Vector2(cx + 5.5f, cy + 3.5f));                           // right wall to rim
            sink.EndFigure(FigureEnd.Closed);                                                          // bottom rim
            sink.Close();
        }
        rt.DrawGeometry(g, brush, 1.4f);
        g.Dispose();
        rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(cx, cy + 5.4f), 1.4f, 1.4f), brush);   // clapper
        rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(cx, cy - 6.6f), 1.1f, 1.1f), brush);   // top nub
    }

    /// <summary>Split glyph: two side-by-side panes (right pane filled when split is active).</summary>
    private void DrawSplitGlyph(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float cx, float cy, Color4 color, bool active)
    {
        brush.Color = color;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = new Rect(cx - 7f, cy - 5.5f, 14f, 11f), RadiusX = 2.5f, RadiusY = 2.5f }, brush, 1.4f);
        rt.DrawLine(new System.Numerics.Vector2(cx, cy - 5.5f), new System.Numerics.Vector2(cx, cy + 5.5f), brush, 1.2f);
        if (active) rt.FillRectangle(new Rect(cx + 1f, cy - 4f, 5f, 8f), brush);
    }

    /// <summary>New-workspace glyph: a card with a plus.</summary>
    private void DrawNewWorkspaceGlyph(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float cx, float cy, Color4 color)
    {
        brush.Color = color;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = new Rect(cx - 7f, cy - 6f, 10f, 10f), RadiusX = 2f, RadiusY = 2f }, brush, 1.3f);
        rt.DrawLine(new System.Numerics.Vector2(cx - 3.5f, cy + 5.5f), new System.Numerics.Vector2(cx + 4.5f, cy + 5.5f), brush, 1.3f);
        rt.DrawLine(new System.Numerics.Vector2(cx + 4.5f, cy - 3f), new System.Numerics.Vector2(cx + 4.5f, cy + 1f), brush, 1.4f);
        rt.DrawLine(new System.Numerics.Vector2(cx + 2.5f, cy - 1f), new System.Numerics.Vector2(cx + 6.5f, cy - 1f), brush, 1.4f);
    }

    /// <summary>Flag glyph (pennant); filled when the flagged view is active.</summary>
    private void DrawFlagGlyph(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float cx, float cy, Color4 color, bool filled)
    {
        brush.Color = color;
        rt.DrawLine(new System.Numerics.Vector2(cx - 5f, cy - 6f), new System.Numerics.Vector2(cx - 5f, cy + 6f), brush, 1.4f);
        var g = _d2d.CreatePathGeometry();
        using (var sink = g.Open())
        {
            sink.BeginFigure(new System.Numerics.Vector2(cx - 5f, cy - 6f), filled ? FigureBegin.Filled : FigureBegin.Hollow);
            sink.AddLine(new System.Numerics.Vector2(cx + 6f, cy - 4f));
            sink.AddLine(new System.Numerics.Vector2(cx - 5f, cy - 1f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        if (filled) rt.FillGeometry(g, brush); else rt.DrawGeometry(g, brush, 1.3f);
        g.Dispose();
    }

    private void DrawCaption(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, int cw)
    {
        int pressedIdx = _capPressed == HTMINBUTTON ? 1 : _capPressed == HTMAXBUTTON ? 2 : _capPressed == HTCLOSE ? 3 : 0;
        for (int i = 1; i <= 3; i++) // 1 min, 2 max/restore, 3 close (left to right)
        {
            float x0 = cw - (4 - i) * CaptionBtnW;
            bool hot = _hoverCaption == i || pressedIdx == i;
            if (hot)
            {
                brush.Color = i == 3 ? new Color4(0.86f, 0.15f, 0.18f, 1f) : SbHighlight;
                rt.FillRectangle(new Rect(x0, 0, CaptionBtnW, TitleBarH), brush);
            }
            brush.Color = ((_hoverCaption == 3 || pressedIdx == 3) && i == 3) ? new Color4(1f, 1f, 1f, 1f) : ChromeText;
            float cx = x0 + CaptionBtnW / 2f, cy = TitleBarH / 2f;
            if (i == 1) rt.DrawLine(new System.Numerics.Vector2(cx - 5, cy), new System.Numerics.Vector2(cx + 5, cy), brush, 1f);
            else if (i == 2)
            {
                if (IsZoomed(_hwnd))
                {
                    rt.DrawRectangle(new Rect(cx - 5, cy - 3, 8, 8), brush, 1f);
                    rt.DrawLine(new System.Numerics.Vector2(cx - 2, cy - 3), new System.Numerics.Vector2(cx - 2, cy - 5), brush, 1f);
                    rt.DrawLine(new System.Numerics.Vector2(cx - 2, cy - 5), new System.Numerics.Vector2(cx + 5, cy - 5), brush, 1f);
                    rt.DrawLine(new System.Numerics.Vector2(cx + 5, cy - 5), new System.Numerics.Vector2(cx + 5, cy + 3), brush, 1f);
                    rt.DrawLine(new System.Numerics.Vector2(cx + 5, cy + 3), new System.Numerics.Vector2(cx + 3, cy + 3), brush, 1f);
                }
                else rt.DrawRectangle(new Rect(cx - 5, cy - 5, 10, 10), brush, 1f);
            }
            else
            {
                rt.DrawLine(new System.Numerics.Vector2(cx - 5, cy - 5), new System.Numerics.Vector2(cx + 5, cy + 5), brush, 1f);
                rt.DrawLine(new System.Numerics.Vector2(cx - 5, cy + 5), new System.Numerics.Vector2(cx + 5, cy - 5), brush, 1f);
            }
        }
    }

    private void DrawToast(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (_toastText is null) return;
        int cw = ClientW(), ch = ClientH();
        float tw = MeasureText(_toastText, _uiFont) + 32f, th = 34f;
        float cx = _sidebarW + ((cw - _sidebarW) - tw) / 2f;
        float ty = ch - th - 24f;
        _toastRect = new Rect(cx, ty, tw, th);   // recorded for click-to-jump hit-testing
        brush.Color = Mix(ChromeBg, ChromeText, 0.14f);
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(cx, ty, tw, th), RadiusX = 8f, RadiusY = 8f }, brush);
        brush.Color = ChromeText;
        rt.DrawText(_toastText, _uiFont, new Rect(cx + 16f, ty, tw - 24f, th), brush);
    }

    /// <summary>While a leader sequence is pending, a small pill hint (bottom-left of the content region).</summary>
    private void DrawLeaderHint(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (!_leaderPending) return;
        string txt = $"leader  {_leader}  —  press a key…";
        float tw = MeasureText(txt, _uiFont) + 32f, th = 34f;
        float x = _sidebarW + 24f, y = ClientH() - th - 24f;
        brush.Color = new Color4(0.22f, 0.17f, 0.33f, 1f);   // purple-ish so it reads distinctly from a toast
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(x, y, tw, th), RadiusX = 8f, RadiusY = 8f }, brush);
        brush.Color = new Color4(1f, 1f, 1f, 1f);             // fixed light text (pill bg is always dark)
        rt.DrawText(txt, _uiFont, new Rect(x + 16f, y, tw - 24f, th), brush);
    }

    // ---- Config (mirrors the WinUI shell) ----

    // ---- Theming ----

    private void ApplyTheme(Theme t) { _theme = t; RecomputeChrome(); RequestRedraw(); }

    /// <summary>Resolve the effective theme when "follow Windows light/dark" is on: pick theme-light or
    /// theme-dark per the current OS setting; when off, fall back to the single manual <c>theme</c>.
    /// Called at startup, on WM_SETTINGCHANGE (ImmersiveColorSet), and when any theme key changes.</summary>
    private void ApplySystemTheme()
    {
        if (!_config.ThemeFollowSystem) { ApplyTheme(FindTheme(_config.Theme)); return; }
        string want = WindowsPrefersLightTheme() ? _config.ThemeLight : _config.ThemeDark;
        if (!string.IsNullOrWhiteSpace(want)) ApplyTheme(FindTheme(want));  // unset side → leave current
    }

    /// <summary>True when Windows apps are set to light mode (HKCU …Personalize\AppsUseLightTheme=1).
    /// Missing value or any error → treated as dark, the Windows default.</summary>
    private static bool WindowsPrefersLightTheme()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>Apply the window-opacity config via a layered window (LWA_ALPHA). 100% removes the layered style.</summary>
    private void ApplyWindowOpacity()
    {
        if (_hwnd == IntPtr.Zero) return;
        int pct = System.Math.Clamp(_config.WindowOpacity, 30, 100);
        long ex = (long)GetWindowLongPtrW(_hwnd, GWL_EXSTYLE);
        if (pct >= 100)
        {
            if ((ex & WS_EX_LAYERED) != 0) SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, (IntPtr)(ex & ~WS_EX_LAYERED));
            return;
        }
        if ((ex & WS_EX_LAYERED) == 0) SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_LAYERED));
        SetLayeredWindowAttributes(_hwnd, 0, (byte)(pct * 255 / 100), LWA_ALPHA);
    }

    private void CommitTheme(Theme t)
    {
        ApplyTheme(t);
        _config.Theme = t.Name;
        _themeBeforePreview = null;
        try { SaveThemeConfig(t.Name); } catch { }
    }

    /// <summary>Live-preview the theme under the selection while the theme picker is open.</summary>
    private void PreviewSelectedTheme()
    {
        if (_palette != PaletteKind.Themes) return;
        if (_palSel >= 0 && _palSel < _palItems.Count && _palItems[_palSel].Data is Theme th) ApplyTheme(th);
    }

    // ---- Prompt preview: the omp/starship pickers render the highlighted theme's prompt in the
    // scratch terminal (the palette skips its scrim + sits lower so the scratch stays visible). ----

    private const int PromptPreviewTimer = 9;   // debounce: rendering a prompt spawns a process
    private bool _previewScratchOpened;         // we opened the scratch for the picker — close it with the palette

    private void SchedulePromptPreview()
    {
        if (_palette is PaletteKind.Omp or PaletteKind.Starship)
            SetTimer(_hwnd, (IntPtr)PromptPreviewTimer, 350, IntPtr.Zero);
    }

    private void PromptPreviewTick()
    {
        KillTimer(_hwnd, (IntPtr)PromptPreviewTimer);
        if (_palette is not (PaletteKind.Omp or PaletteKind.Starship)) return;
        if (_palSel < 0 || _palSel >= _palItems.Count || _palItems[_palSel].Data is not string sel) return;
        var ses = _active;
        if (ses is null) return;
        bool scratchShowing = _coverKind == 1 && ses.Scratch is not null && ReferenceEquals(_cover, ses.Scratch);
        if (!scratchShowing) { ShowScratch(ses); _previewScratchOpened = true; }
        if (ses.Scratch is not { } sc) return;
        const string utf8 = "chcp 65001 >$null; $OutputEncoding=[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; ";   // UTF-8 for native writes AND PS-captured output
        string cmd = _palette == PaletteKind.Omp
            ? utf8 + "clear; oh-my-posh print primary --config '" + sel.Replace("'", "''") + "'\r"
            : StarshipPreviewCmd(sel);
        if (cmd.Length == 0) return;
        sc.S.NotifyActivity();
        sc.S.Write(Encoding.UTF8.GetBytes(cmd));
    }

    private string StarshipPreviewCmd(string preset)
    {
        string? cfg = preset.Length == 0 ? null : Agwinterm.Pty.StarshipPresets.ConfigFor(preset, AppDir);
        if (preset.Length > 0 && cfg is null) return "";
        string set = cfg is not null
            ? "$env:STARSHIP_CONFIG='" + cfg.Replace("'", "''") + "'"
            : "Remove-Item Env:STARSHIP_CONFIG -ErrorAction SilentlyContinue";
        return "chcp 65001 >$null; $OutputEncoding=[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + set + "; clear; starship prompt\r";
    }

    // ---- oh-my-posh scheme changer (in-app extension) ----

    /// <summary>Apply an oh-my-posh theme (name or .omp.json path) live to the active session's shell,
    /// re-running oh-my-posh init and RE-APPLYING our OSC-7 prompt wrap (init redefines `prompt`, which
    /// would drop the wrap). Persists to config when asked so new sessions launch with it.</summary>
    public string OmpSet(string nameOrPath, bool persist)
    {
        string? path = Agwinterm.Pty.OmpThemes.Resolve(nameOrPath);
        if (path is null) return "oh-my-posh theme not found: " + nameOrPath;
        Post(() => ApplyOmp(path, persist));
        return "oh-my-posh theme set: " + nameOrPath;
    }

    private void ApplyOmp(string path, bool persist)
    {
        if (ActiveSurface() is { } pane)
        {
            string q = path.Replace("'", "''");
            // Re-init oh-my-posh with the chosen config (which redefines `prompt`), then wrap that NEW
            // prompt to keep emitting OSC 7 so the title's live cwd survives the switch. One clean line.
            string line =
                "oh-my-posh init pwsh --config '" + q + "' | Invoke-Expression; " +
                "$__o=$function:prompt; function global:prompt { " +
                "[Console]::Write(\"$([char]27)]7;file://$env:COMPUTERNAME/$(((Get-Location).ProviderPath -replace '\\\\','/'))$([char]7)\"); " +
                "& $__o }\r";
            pane.S.NotifyActivity();
            pane.S.Write(Encoding.UTF8.GetBytes(line));
            RequestRedraw();
        }
        if (persist) { try { WriteConfigKey("omp-theme", path); } catch { } _config.OmpTheme = path; }
    }

    /// <summary>Apply a starship preset ("" = starship default) live to the active session's shell
    /// (re-init + re-wrap, mirroring ApplyOmp) and persist so new sessions launch with it.</summary>
    private void ApplyStarship(string preset, bool persist)
    {
        string? cfgPath = Agwinterm.Pty.StarshipPresets.ConfigFor(preset, AppDir);
        if (preset.Length > 0 && cfgPath is null) { ShowToast("starship preset failed: " + preset); return; }
        if (ActiveSurface() is { } pane)
        {
            string setCfg = cfgPath is not null
                ? "$env:STARSHIP_CONFIG='" + cfgPath.Replace("'", "''") + "'; "
                : "Remove-Item Env:STARSHIP_CONFIG -ErrorAction SilentlyContinue; ";
            string line = setCfg +
                "Invoke-Expression (&starship init powershell); " +
                "$__o=$function:prompt; function global:prompt { " +
                "[Console]::Write(\"$([char]27)]7;file://$env:COMPUTERNAME/$(((Get-Location).ProviderPath -replace '\\\\','/'))$([char]7)\"); " +
                "& $__o }\r";
            pane.S.NotifyActivity();
            pane.S.Write(Encoding.UTF8.GetBytes(line));
            RequestRedraw();
        }
        if (persist) { try { WriteConfigKey("starship-theme", preset); } catch { } _config.StarshipTheme = preset; }
    }

    /// <summary>Refresh the process PATH from the registry (Machine + User) so an engine installed
    /// moments ago (winget) is detectable — and inherited by new sessions — without an app restart.</summary>
    private static void RefreshEnvPath()
    {
        try
        {
            string machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
            string user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var current = new HashSet<string>((Environment.GetEnvironmentVariable("Path") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            var add = machine.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Concat(user.Split(';', StringSplitOptions.RemoveEmptyEntries))
                .Where(p => !current.Contains(p)).ToList();
            if (add.Count > 0)
                Environment.SetEnvironmentVariable("Path", Environment.GetEnvironmentVariable("Path") + ";" + string.Join(';', add));
        }
        catch { }
    }

    /// <summary>Install a prompt engine via winget in an overlay terminal (live progress, any key
    /// closes when done). Re-open the picker afterwards — PATH is refreshed on open.</summary>
    private void InstallPromptEngine(string engine)
    {
        var ses = _active;
        if (ses is null) { ShowToast("open a session first"); return; }
        string id = engine == "starship" ? "Starship.Starship" : "JanDeDobbeleer.OhMyPosh";
        OverlayOpen(ses, $"winget install --id {id} -e --accept-source-agreements --accept-package-agreements", 70, wait: true);
        ShowToast($"installing {(engine == "starship" ? "starship" : "oh-my-posh")} — re-open the picker when it finishes");
    }

    /// <summary>Install a Nerd Font via oh-my-posh's font tool (engine-agnostic: starship themes
    /// need the same glyphs) in an overlay, and point font-family at the new family. When omp isn't
    /// installed yet, installs it first — it IS the font tool.</summary>
    private void InstallNerdFont(string slug, string family)
    {
        var ses = _active;
        if (ses is null) { ShowToast("open a session first"); return; }
        RefreshEnvPath();
        if (Agwinterm.Pty.OmpThemes.List().Count == 0 && !CommandExists("oh-my-posh"))
        {
            InstallPromptEngine("omp");   // omp is the font installer; re-pick the font afterwards
            ShowToast("installing oh-my-posh first (it provides the font installer) — then re-pick the font");
            return;
        }
        OverlayOpen(ses, $"oh-my-posh font install {slug}", 70, wait: true);
        ConfigSetInternal("font-family", family);
        ShowToast($"installing {family} — the terminal switches to it once the overlay finishes");
    }

    /// <summary>oh-my-posh present? Theme folders OR the exe itself — the Microsoft Store install
    /// has no POSH_THEMES_PATH/winget themes dir, yet is fully usable (init + font tool).</summary>
    private static bool OmpAvailable() => Agwinterm.Pty.OmpThemes.List().Count > 0 || CommandExists("oh-my-posh");

    /// <summary>Download oh-my-posh's official themes pack (GitHub release asset) into an
    /// agwinterm-local dir the theme scanner reads — for installs that ship no themes on disk.</summary>
    private void DownloadOmpThemes()
    {
        ShowToast("downloading oh-my-posh themes pack…");
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm", "omp-themes");
                Directory.CreateDirectory(dir);
                using var http = new System.Net.Http.HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("agwinterm");
                byte[] bytes = await http.GetByteArrayAsync("https://github.com/JanDeDobbeleer/oh-my-posh/releases/latest/download/themes.zip");
                string zip = Path.Combine(dir, "themes.zip");
                await File.WriteAllBytesAsync(zip, bytes);
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
                File.Delete(zip);
                int n = Directory.GetFiles(dir, "*.omp.json").Length;
                Post(() => ShowToast($"{n} oh-my-posh themes downloaded — open “oh-my-posh Theme…” again"));
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                Post(() => ShowToast("themes download failed: " + msg));
            }
        });
    }

    private static bool CommandExists(string exe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return false; }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Switch the prompt engine (omp | starship | vanilla): persists, re-inits the active
    /// session's shell for omp/starship (vanilla applies to new sessions only), and keeps the legacy
    /// omp-integration key in sync.</summary>
    private void ApplyPromptEngine(string engine)
    {
        _config.PromptEngine = engine;
        try { WriteConfigKey("prompt-engine", engine); WriteConfigKey("omp-integration", engine == "omp" ? "true" : "false"); } catch { }
        _config.OmpIntegration = engine == "omp";
        switch (engine)
        {
            case "starship" when Agwinterm.Pty.StarshipPresets.Available():
                ApplyStarship(_config.StarshipTheme, persist: false);
                ShowToast("prompt engine: starship — pick a preset via “Starship Theme…”");
                break;
            case "omp" when OmpAvailable():
                if (Agwinterm.Pty.OmpThemes.Resolve(_config.OmpTheme) is { } p) ApplyOmp(p, persist: false);
                else if (ActiveSurface() is { } op)   // no theme configured (e.g. Store install, no themes dir): init with omp's default/env theme
                {
                    op.S.NotifyActivity();
                    op.S.Write(Encoding.UTF8.GetBytes(
                        "oh-my-posh init pwsh | Invoke-Expression; " +
                        "$__o=$function:prompt; function global:prompt { " +
                        "[Console]::Write(\"$([char]27)]7;file://$env:COMPUTERNAME/$(((Get-Location).ProviderPath -replace '\\\\','/'))$([char]7)\"); " +
                        "& $__o }\r"));
                }
                ShowToast("prompt engine: oh-my-posh — pick a theme via “oh-my-posh Theme…”");
                break;
            case "vanilla":
                if (ActiveSurface() is { } vp)   // live: reset to the stock prompt right away
                {
                    vp.S.NotifyActivity();
                    vp.S.Write(Encoding.UTF8.GetBytes(VanillaPromptReset + "\r"));
                }
                ShowToast("prompt engine: vanilla — plain PowerShell prompt");
                break;
            case "profile":
                ShowToast("prompt engine: profile — new sessions get no injection ($PROFILE rules)");
                break;
            default:
                ShowToast($"prompt engine set to {engine} (engine not detected — install it first)");
                break;
        }
    }

    private static Theme FindTheme(string name)
        => _allThemes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Theme.Default;

    /// <summary>Persist the chosen theme by rewriting (or appending) the `theme =` line in the config.</summary>
    private static void SaveThemeConfig(string name) => WriteConfigKey("theme", name);

    /// <summary>All config keys the Settings window / config verbs manage.</summary>
    private static readonly string[] ConfigKeys =
    {
        "font-family", "font-size", "cursor-style", "cursor-blink", "cursor-blink-ms", "theme",
        "theme-follow-system", "theme-dark", "theme-light",
        "scrollback-lines", "inactive-pane-dim", "unfocused-dim", "builtin-glyphs", "ligatures", "window-opacity", "sidebar-tint", "scroll-speed",
        "new-session-dir", "right-click-paste", "copy-on-select", "word-delimiters", "desktop-notifications", "shell-integration",
        "restore-commands", "restore-buffer", "blocked-sound", "omp-theme", "omp-integration", "prompt-engine", "starship-theme",
        "new-session-dir-mode", "confirm-close-session", "compact-toolbar", "toolbar-mode", "notification-badges",
        "attention-button", "status-color-active", "status-color-blocked", "status-color-completed",
    };

    /// <summary>Rewrite (or append) a single `key = value` line in agwinterm.conf, preserving the rest.</summary>
    private static void WriteConfigKey(string key, string value)
    {
        string path = ConfigPath;
        string text = File.Exists(path) ? File.ReadAllText(path) : TerminalConfig.DefaultText;
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        int idx = lines.FindIndex(l =>
        {
            var t = l.TrimStart();
            int eq = l.IndexOf('=');
            return !t.StartsWith("#") && eq > 0 && string.Equals(l[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase);
        });
        string ln = $"{key} = {value}";
        if (idx >= 0) lines[idx] = ln; else { lines.Add(ln); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join(Environment.NewLine, lines));
    }

    /// <summary>Current value of a config key as a string (for config get/list + the Settings window).</summary>
    private static string ConfigValue(string key) => key switch
    {
        "font-family" => _config.FontFamily,
        "font-size" => _config.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "cursor-style" => _config.CursorStyle.ToString().ToLowerInvariant(),
        "cursor-blink" => _config.CursorBlink ? "true" : "false",
        "cursor-blink-ms" => _config.CursorBlinkMs.ToString(),
        "theme" => _config.Theme,
        "theme-follow-system" => _config.ThemeFollowSystem ? "true" : "false",
        "theme-dark" => _config.ThemeDark,
        "theme-light" => _config.ThemeLight,
        "scrollback-lines" => _config.Scrollback.ToString(),
        "inactive-pane-dim" => _config.InactivePaneDim.ToString(),
        "unfocused-dim" => _config.UnfocusedDim.ToString(),
        "builtin-glyphs" => _config.BuiltinGlyphs ? "true" : "false",
        "ligatures" => _config.Ligatures ? "true" : "false",
        "window-opacity" => _config.WindowOpacity.ToString(),
        "sidebar-tint" => _config.SidebarTint.ToString(),
        "scroll-speed" => _config.ScrollSpeed.ToString(),
        "new-session-dir" => _config.NewSessionDir,
        "right-click-paste" => _config.RightClickPaste ? "true" : "false",
        "copy-on-select" => _config.CopyOnSelect ? "true" : "false",
        "word-delimiters" => _config.WordDelimiters,
        "desktop-notifications" => _config.DesktopNotifications ? "true" : "false",
        "shell-integration" => _config.ShellIntegration ? "true" : "false",
        "restore-commands" => _config.RestoreCommands ? "true" : "false",
        "restore-buffer" => _config.RestoreBuffer ? "true" : "false",
        "blocked-sound" => _config.BlockedSound,
        "omp-theme" => _config.OmpTheme,
        "omp-integration" => _config.OmpIntegration ? "true" : "false",
        "prompt-engine" => _config.PromptEngine,
        "starship-theme" => _config.StarshipTheme,
        "new-session-dir-mode" => _config.NewSessionDirMode,
        "confirm-close-session" => _config.ConfirmCloseSession ? "true" : "false",
        "compact-toolbar" => _config.CompactToolbar ? "true" : "false",
        "toolbar-mode" => ToolbarModeResolved,
        "notification-badges" => _config.NotificationBadges ? "true" : "false",
        "attention-button" => _config.AttentionButton ? "true" : "false",
        "status-color-active" => _config.StatusColorActive,
        "status-color-blocked" => _config.StatusColorBlocked,
        "status-color-completed" => _config.StatusColorCompleted,
        _ => "",
    };

    /// <summary>Persist + apply a config key live. Runs on the UI thread. Returns an ack string.</summary>
    private string ConfigSetInternal(string key, string value)
    {
        key = key.Trim().ToLowerInvariant();
        if (Array.IndexOf(ConfigKeys, key) < 0) return "error: unknown key '" + key + "'";
        WriteConfigKey(key, value.Trim());
        _config = TerminalConfig.Load(ConfigPath);       // reparse so clamping/validation is centralized
        if (key == "theme") _theme = FindTheme(_config.Theme);
        if (key is "theme" or "theme-follow-system" or "theme-dark" or "theme-light") ApplySystemTheme();
        if (key == "cursor-blink-ms" && _hwnd != IntPtr.Zero) SetTimer(_hwnd, (IntPtr)1, (uint)_config.CursorBlinkMs, IntPtr.Zero);
        RecomputeChrome();
        ApplyWindowOpacity();
        if (key is "compact-toolbar" or "toolbar-mode")   // title-bar height changed → reflow the terminal grid
        { if (_active is not null) RegridSession(_active); if (_cover is not null) RegridCover(); }
        if (key is "font-family" or "font-size") RebuildFont();   // apply live to the running window
        RequestRedraw();
        RefreshSettingsControls();                        // keep an open Settings window in sync
        bool deferred = key is "scrollback-lines" or "shell-integration" or "restore-commands";
        return $"{key} = {ConfigValue(key)}" + (deferred ? "  (applies to new sessions)" : "");
    }

    /// <summary>
    /// Built-in themes, then the curated ghostty files bundled next to the exe (themes/),
    /// then any user files in %LOCALAPPDATA%\agwinterm\themes\. Deduped by name
    /// (case-insensitive), first-seen wins — so a compiled built-in beats a bundled/user
    /// file of the same name, and bundled beats user.
    /// </summary>
    private static List<Theme> LoadThemes()
    {
        var builtins = new List<Theme>
        {
            Theme.Default, // "default"
            Make("Solarized Dark",
                new[]{"#073642","#dc322f","#859900","#b58900","#268bd2","#d33682","#2aa198","#eee8d5",
                      "#002b36","#cb4b16","#586e75","#657b83","#839496","#6c71c4","#93a1a1","#fdf6e3"},
                "#839496","#002b36","#93a1a1"),
            Make("Solarized Light",
                new[]{"#073642","#dc322f","#859900","#b58900","#268bd2","#d33682","#2aa198","#eee8d5",
                      "#002b36","#cb4b16","#586e75","#657b83","#839496","#6c71c4","#93a1a1","#fdf6e3"},
                "#657b83","#fdf6e3","#586e75"),
            Make("Nord",
                new[]{"#3b4252","#bf616a","#a3be8c","#ebcb8b","#81a1c1","#b48ead","#88c0d0","#e5e9f0",
                      "#4c566a","#bf616a","#a3be8c","#ebcb8b","#81a1c1","#b48ead","#8fbcbb","#eceff4"},
                "#d8dee9","#2e3440","#d8dee9"),
            Make("Gruvbox Dark",
                new[]{"#282828","#cc241d","#98971a","#d79921","#458588","#b16286","#689d6a","#a89984",
                      "#928374","#fb4934","#b8bb26","#fabd2f","#83a598","#d3869b","#8ec07c","#ebdbb2"},
                "#ebdbb2","#282828","#ebdbb2"),
            Make("Tokyo Night",
                new[]{"#15161e","#f7768e","#9ece6a","#e0af68","#7aa2f7","#bb9af7","#7dcfff","#a9b1d6",
                      "#414868","#f7768e","#9ece6a","#e0af68","#7aa2f7","#bb9af7","#7dcfff","#c0caf5"},
                "#c0caf5","#1a1b26","#c0caf5"),
            Make("One Dark",
                new[]{"#282c34","#e06c75","#98c379","#e5c07b","#61afef","#c678dd","#56b6c2","#abb2bf",
                      "#545862","#e06c75","#98c379","#e5c07b","#61afef","#c678dd","#56b6c2","#c8ccd4"},
                "#abb2bf","#282c34","#528bff"),
        };

        var list = new List<Theme>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(Theme t) { if (seen.Add(t.Name)) list.Add(t); }

        foreach (var t in builtins) Add(t);

        // Bundled themes ship in themes/ next to the exe; user themes live under LOCALAPPDATA.
        string bundledDir = Path.Combine(AppContext.BaseDirectory, "themes");
        string userDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm", "themes");
        foreach (var dir in new[] { bundledDir, userDir })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir))
                    if (ParseGhosttyTheme(f) is Theme th) Add(th);
            }
            catch { }
        }

        // Single-file portable exe: no themes\ dir on disk — the same curated themes are embedded
        // in the assembly (disk copies above win the name dedup when both exist).
        try
        {
            var asm = typeof(Program).Assembly;
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith("theme:", StringComparison.Ordinal)) continue;
                using var s = asm.GetManifestResourceStream(res);
                if (s is null) continue;
                using var r = new StreamReader(s);
                var lines = new List<string>();
                while (r.ReadLine() is { } ln) lines.Add(ln);
                if (ParseGhosttyTheme(res["theme:".Length..], lines) is Theme th) Add(th);
            }
        }
        catch { }
        return list;
    }

    private static Theme Make(string name, string[] hex16, string fg, string bg, string cursor) => new()
    {
        Name = name,
        Palette = hex16.Select(Hex).ToArray(),
        DefaultForeground = Hex(fg),
        DefaultBackground = Hex(bg),
        Cursor = Hex(cursor),
    };

    private static Color Hex(string h)
    {
        h = h.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length < 6) return new Color(0, 0, 0);
        try { return new Color(Convert.ToByte(h[..2], 16), Convert.ToByte(h.Substring(2, 2), 16), Convert.ToByte(h.Substring(4, 2), 16)); }
        catch { return new Color(0, 0, 0); }
    }

    /// <summary>Parse a ghostty-format theme file (palette = N=#rrggbb, background/foreground/cursor-color).</summary>
    private static Theme? ParseGhosttyTheme(string path)
    {
        try { return ParseGhosttyTheme(Path.GetFileNameWithoutExtension(path), File.ReadAllLines(path)); }
        catch { return null; }
    }

    private static Theme? ParseGhosttyTheme(string name, IEnumerable<string> lines)
    {
        try
        {
            var pal = Theme.DefaultPalette();
            Color fg = Color.DefaultForeground, bg = Color.DefaultBackground, cur = new(222, 222, 230);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('='); if (eq <= 0) continue;
                string key = line[..eq].Trim().ToLowerInvariant(); string val = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "palette": int c = val.IndexOf('='); if (c > 0 && int.TryParse(val[..c].Trim(), out int pi) && pi is >= 0 and < 16) pal[pi] = Hex(val[(c + 1)..]); break;
                    case "background": bg = Hex(val); break;
                    case "foreground": fg = Hex(val); break;
                    case "cursor-color": cur = Hex(val); break;
                }
            }
            return new Theme { Name = name, Palette = pal, DefaultForeground = fg, DefaultBackground = bg, Cursor = cur };
        }
        catch { return null; }
    }

    // ---- Persistence: workspace/session tree + selection + sidebar state ----

    private sealed class PaneState { public string Id { get; set; } = ""; public string Cwd { get; set; } = ""; public float FontSize { get; set; } public float Ratio { get; set; } = 1f; public string Command { get; set; } = ""; public List<string>? Buffer { get; set; } }
    // Cwd/FontSize kept for backward-compat with pre-splits state.json (one pane per session).
    private sealed class SessionState { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string? CustomName { get; set; } public string? Profile { get; set; } public int Active { get; set; } public bool Flagged { get; set; } public List<PaneState> Panes { get; set; } = new(); public string Cwd { get; set; } = ""; public float FontSize { get; set; }
        // Wave F2: background watermark (BgFile = the copied file's name under backgrounds\; null = none).
        public string? BgFile { get; set; } public int BgOpacity { get; set; } = 15; public string BgMode { get; set; } = "fit"; }
    private sealed class WorkspaceState { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public bool Expanded { get; set; } = true; public List<SessionState> Sessions { get; set; } = new(); }
    private sealed class AppState
    {
        public List<WorkspaceState> Workspaces { get; set; } = new();
        public string? ActiveId { get; set; }
        public float SidebarWidth { get; set; } = SidebarWFull;
        public bool SidebarVisible { get; set; } = true;
        // Window geometry (restore rect; 0 width = unset). WindowMaximized reopens maximized.
        public int WindowX { get; set; }
        public int WindowY { get; set; }
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }
        // Wave D1: sidebar view mode ("tree"|"flagged") + focused workspace id (null = show all).
        public string SidebarMode { get; set; } = "tree";
        public string? FocusedWorkspaceId { get; set; }
        // Ctrl+Tab MRU session order (most recent first); restored on relaunch.
        public List<string> Mru { get; set; } = new();
    }

    private static readonly JsonSerializerOptions _stateJson = new() { WriteIndented = true };

    private static string AppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm");
    private static string WindowsIndexPath => Path.Combine(AppDir, "windows.json");
    private static string LegacyStatePath => Path.Combine(AppDir, "state.json");
    private static string BackgroundsDir => Path.Combine(AppDir, "backgrounds"); // copied per-session watermark images
    // Per-window tree snapshot. Multi-window (F1b): each window persists to windows/<id>.json.
    private string StatePath => Path.Combine(AppDir, "windows", Id + ".json");

    /// <summary>Load the window library index; migrate a legacy single-window state.json; ensure it's non-empty.</summary>
    private static void LoadOrMigrateIndex()
    {
        lock (_windowIndex)
        {
            _windowIndex.Clear();
            try
            {
                if (File.Exists(WindowsIndexPath))
                {
                    var idx = JsonSerializer.Deserialize<WindowsIndexFile>(File.ReadAllText(WindowsIndexPath));
                    if (idx?.Windows is { Count: > 0 })
                    {
                        _windowIndex.AddRange(idx.Windows.Where(m => !string.IsNullOrEmpty(m.Id)));
                        _frontmostId = idx.Frontmost;
                    }
                }
            }
            catch { _windowIndex.Clear(); }

            if (_windowIndex.Count == 0)
            {
                // Migrate a legacy state.json into windows/<id>.json, or seed a fresh window.
                var m = new WinMeta { Id = Guid.NewGuid().ToString(), Name = "", IsOpen = true };
                try
                {
                    if (File.Exists(LegacyStatePath))
                    {
                        var st = JsonSerializer.Deserialize<AppState>(File.ReadAllText(LegacyStatePath));
                        if (st is not null)
                        {
                            m.X = st.WindowX; m.Y = st.WindowY; m.W = st.WindowWidth; m.H = st.WindowHeight; m.Max = st.WindowMaximized;
                        }
                        Directory.CreateDirectory(Path.Combine(AppDir, "windows"));
                        File.Copy(LegacyStatePath, Path.Combine(AppDir, "windows", m.Id + ".json"), overwrite: true);
                        try { File.Move(LegacyStatePath, LegacyStatePath + ".migrated", overwrite: true); } catch { }
                    }
                }
                catch { }
                _windowIndex.Add(m);
                _frontmostId = m.Id;
            }
            if (!_windowIndex.Any(w => w.IsOpen)) _windowIndex[0].IsOpen = true;
            if (_frontmostId is null || !_windowIndex.Any(w => w.Id == _frontmostId))
                _frontmostId = _windowIndex.First(w => w.IsOpen).Id;
        }
    }

    /// <summary>Persist the window library index (atomic). Best-effort.</summary>
    private static void SaveIndex()
    {
        try
        {
            List<WinMeta> copy;
            lock (_windowIndex) copy = _windowIndex.Select(m => new WinMeta { Id = m.Id, Name = m.Name, IsOpen = m.IsOpen, X = m.X, Y = m.Y, W = m.W, H = m.H, Max = m.Max }).ToList();
            var idx = new WindowsIndexFile { Version = 1, Frontmost = _frontmostId, Windows = copy };
            Directory.CreateDirectory(AppDir);
            string tmp = WindowsIndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(idx, _stateJson));
            File.Move(tmp, WindowsIndexPath, overwrite: true);
        }
        catch { }
    }

    /// <summary>Create a Program instance for a library entry and boot its window (UI thread). Seeds if no tree file exists.</summary>
    private static Program CreateWindowInstance(WinMeta m)
    {
        var win = new Program { Id = m.Id, WinName = m.Name };
        if (m.W > 0 && m.H > 0) { win._geoX = m.X; win._geoY = m.Y; win._geoW = m.W; win._geoH = m.H; win._geoMax = m.Max; win._geoValid = true; }
        lock (_windowIndex) { _byId[m.Id] = win; if (!_windowIndex.Any(x => x.Id == m.Id)) _windowIndex.Add(m); m.IsOpen = true; }
        win.Boot(_hInstance);
        return win;
    }

    private List<Pane> PanesOf(Ses s) { lock (_workspaces) return s.Panes.ToList(); }

    private static string DenylistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm", "restore-denylist.conf");

    /// <summary>Exe/process names (no extension) that restore-commands never re-runs. Seeds a starter file.</summary>
    private static HashSet<string> LoadDenylist()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "powershell", "pwsh", "cmd", "conhost", "wsl", "ssh", "bash", "oh-my-posh", "git", "windowsterminal" };
        try
        {
            string path = DenylistPath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path,
                    "# agwinterm restore-denylist: process/exe names (no extension) NOT re-run on restart.\n" +
                    "# One per line; '#' starts a comment. Defaults cover shells and prompt helpers.\n" +
                    "powershell\npwsh\ncmd\nconhost\nwsl\nssh\nbash\noh-my-posh\ngit\nWindowsTerminal\n");
            }
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                set.Add(StripExe(line));
            }
        }
        catch { }
        return set;
    }

    private static string StripExe(string name)
    {
        name = name.Trim().Trim('"');
        int slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    /// <summary>
    /// Best-effort foreground-command capture: for each shell PID, the command line of its most
    /// recently started non-denylisted child. One CIM process snapshot for all panes; ~1s at quit.
    /// </summary>
    private static Dictionary<int, string> CaptureForegroundCommands(IEnumerable<int> shellPids)
    {
        var result = new Dictionary<int, string>();
        var pids = shellPids.Distinct().ToHashSet();
        if (pids.Count == 0) return result;
        var deny = LoadDenylist();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CommandLine,@{n='C';e={if($_.CreationDate){$_.CreationDate.Ticks}else{0}}} | ConvertTo-Json -Compress\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return result;
            string json = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(4000)) { try { proc.Kill(); } catch { } return result; }
            if (string.IsNullOrWhiteSpace(json)) return result;

            using var doc = JsonDocument.Parse(json);
            var rows = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };
            var byParent = new Dictionary<int, List<(long created, string cmd)>>();
            foreach (var e in rows)
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                int ppid = e.TryGetProperty("ParentProcessId", out var pv) && pv.TryGetInt32(out var pp) ? pp : -1;
                if (!pids.Contains(ppid)) continue;
                string name = e.TryGetProperty("Name", out var nv) && nv.ValueKind == JsonValueKind.String ? (nv.GetString() ?? "") : "";
                string cmd = e.TryGetProperty("CommandLine", out var cv) && cv.ValueKind == JsonValueKind.String ? (cv.GetString() ?? "") : "";
                long created = e.TryGetProperty("C", out var tv) && tv.ValueKind == JsonValueKind.Number && tv.TryGetInt64(out var t) ? t : 0;
                if (cmd.Length == 0 || deny.Contains(StripExe(name))) continue;
                if (!byParent.TryGetValue(ppid, out var list)) byParent[ppid] = list = new();
                list.Add((created, cmd));
            }
            foreach (var (ppid, list) in byParent)
                result[ppid] = list.OrderByDescending(x => x.created).First().cmd;
        }
        catch { }
        return result;
    }

    /// <summary>Snapshot the tree/selection/sidebar to disk atomically. No-op while restoring; ignores IO errors.
    /// <paramref name="captureCommands"/> (quit only) captures each pane's foreground command when restore-commands is on.</summary>
    private void SaveState(bool captureCommands = false)
    {
        if (_restoring) return;
        try
        {
            // Snapshot rows under the workspaces lock, then read each cwd (which locks the
            // session) OUTSIDE that lock to keep lock ordering consistent.
            List<(string id, string name, bool expanded, List<Ses> sessions)> rows;
            lock (_workspaces)
                rows = _workspaces.Select(w => (w.Id, w.Name, w.Expanded, w.Sessions.ToList())).ToList();
            string? activeId = _active?.Id;

            // Foreground-command capture (opt-in, quit only): one process snapshot for all panes.
            Dictionary<int, string> cmdByPid = (captureCommands && _config.RestoreCommands)
                ? CaptureForegroundCommands(rows.SelectMany(r => r.sessions).SelectMany(s => PanesOf(s))
                    .Select(p => p.S.ChildProcessId).Where(id => id is > 0).Select(id => id!.Value))
                : new Dictionary<int, string>();

            var st = new AppState
            {
                ActiveId = activeId,
                SidebarWidth = _sidebarW > 0 ? _sidebarW : SidebarWFull,
                SidebarVisible = _sidebarW > 0,
                SidebarMode = _sidebarMode == SidebarMode.Flagged ? "flagged" : "tree",
                FocusedWorkspaceId = _focusedWorkspaceId,
                Mru = _mru.ToList(),   // Ctrl+Tab recency order survives relaunch
            };
            CaptureGeometry(st);
            foreach (var (id, name, expanded, sessions) in rows)
            {
                var wss = new WorkspaceState { Id = id, Name = name, Expanded = expanded };
                foreach (var s in sessions)
                {
                    var ss = new SessionState { Id = s.Id, Name = s.Name, CustomName = s.CustomName, Profile = s.ProfileName, Active = s.Active, Flagged = s.Flagged,
                        BgFile = s.BgPath is null ? null : Path.GetFileName(s.BgPath), BgOpacity = s.BgOpacity, BgMode = s.BgMode };
                    List<Pane> panes;
                    lock (_workspaces) panes = s.Panes.ToList();
                    foreach (var p in panes)
                    {
                        string live = PrettyCwd(SafeCwd(p));                       // OSC 7 cwd if the shell reports it
                        string cwd = live.Length > 0 ? live : (p.StartCwd ?? ""); // else the launch dir
                        string cmd = (p.S.ChildProcessId is int pid && cmdByPid.TryGetValue(pid, out var c)) ? c : "";
                        List<string>? buf = null;
                        if (_config.RestoreBuffer)
                            try { lock (p.S.SyncRoot) buf = p.S.Emulator.DumpBuffer().TakeLast(500).ToList(); } catch { }  // cap the saved lines
                        ss.Panes.Add(new PaneState { Id = p.Id, Cwd = cwd, FontSize = p.FontSize, Ratio = p.Ratio, Command = cmd, Buffer = buf });
                    }
                    wss.Sessions.Add(ss);
                }
                st.Workspaces.Add(wss);
            }

            string path = StatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(st, _stateJson));
            File.Move(tmp, path, overwrite: true); // atomic replace so a crash never leaves a truncated file

            // Mirror name + geometry into the window-library entry so windows.json can position the
            // window at next launch without loading the per-window tree first.
            lock (_windowIndex)
            {
                var meta = _windowIndex.FirstOrDefault(m => m.Id == Id);
                if (meta is not null)
                {
                    meta.Name = WinName;
                    if (st.WindowWidth > 0) { meta.X = st.WindowX; meta.Y = st.WindowY; meta.W = st.WindowWidth; meta.H = st.WindowHeight; meta.Max = st.WindowMaximized; }
                }
            }
            SaveIndex();
        }
        catch { /* persistence is best-effort */ }
    }

    // Window geometry loaded from state.json at startup (applied at window creation).
    private bool _geoValid;
    private int _geoX, _geoY, _geoW, _geoH;
    private bool _geoMax;
    private bool _wasMaximized;
    private bool _windowActive = true;   // window focus state (drives unfocused-dim)

    // ---- Fullscreen (F11 / toggle_fullscreen): borderless over the whole monitor ----
    private bool _fullscreen;
    private WINDOWPLACEMENT _fsPrev;

    private void ToggleFullscreen()
    {
        long style = (long)GetWindowLongPtrW(_hwnd, GWL_STYLE);
        if (!_fullscreen)
        {
            _fsPrev = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(_hwnd, ref _fsPrev);
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST), ref mi);
            SetWindowLongPtrW(_hwnd, GWL_STYLE, (IntPtr)((style & ~(long)WS_OVERLAPPEDWINDOW) | WS_POPUP));
            SetWindowPos(_hwnd, IntPtr.Zero, mi.rcMonitor.left, mi.rcMonitor.top,
                mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top,
                SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOZORDER);
            _fullscreen = true;
        }
        else
        {
            SetWindowLongPtrW(_hwnd, GWL_STYLE, (IntPtr)((style & ~(long)WS_POPUP) | WS_OVERLAPPEDWINDOW));
            SetWindowPlacement(_hwnd, ref _fsPrev);
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            _fullscreen = false;
        }
        RequestRedraw();
    }

    /// <summary>Fill AppState with the window's restore rect + maximized flag (GetWindowPlacement).</summary>
    private void CaptureGeometry(AppState st)
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(_hwnd, ref wp)) return;
            var r = wp.rcNormalPosition;                 // restore rect even when maximized
            st.WindowX = r.left; st.WindowY = r.top;
            st.WindowWidth = r.right - r.left; st.WindowHeight = r.bottom - r.top;
            st.WindowMaximized = wp.showCmd == SW_MAXIMIZE; // SW_SHOWMAXIMIZED == 3
        }
        catch { }
    }

    /// <summary>Rebuild the tree from state.json via the normal Create* paths. Returns false to fall back to a default.</summary>
    private bool TryRestoreState()
    {
        AppState? st;
        try
        {
            string path = StatePath;
            if (!File.Exists(path)) return false;
            st = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path));
        }
        catch
        {
            try { File.Move(StatePath, StatePath + ".bad", overwrite: true); } catch { } // keep the corrupt file for inspection
            return false;
        }
        if (st?.Workspaces is null || !st.Workspaces.Any(w => w.Sessions is { Count: > 0 })) return false;

        _restoring = true;
        try
        {
            _sidebarW = st.SidebarVisible ? (st.SidebarWidth > 0 ? st.SidebarWidth : SidebarWFull) : 0;
            foreach (var w in st.Workspaces)
            {
                // Recreate every saved workspace, including empty ones (agterm keeps empty workspaces).
                // StableWorkspaceId folds any duplicate id already restored this pass onto a fresh one.
                var ws = CreateWorkspace(StableWorkspaceId(w.Id), w.Name);
                foreach (var s in w.Sessions ?? new List<SessionState>())
                {
                    // Back-compat: a pre-splits session has no Panes — synthesize one from its Cwd/FontSize.
                    var pl = (s.Panes is { Count: > 0 })
                        ? s.Panes
                        : new List<PaneState> { new() { Id = s.Id, Cwd = s.Cwd, FontSize = s.FontSize, Ratio = 1f } };
                    var first = pl[0];
                    var ses = CreateSession(
                        StableSessionId(s.Id),
                        string.IsNullOrWhiteSpace(s.Name) ? null : s.Name,
                        string.IsNullOrWhiteSpace(first.Cwd) ? null : first.Cwd,
                        ws, makeActive: s.Id == st.ActiveId,
                        fontSize: first.FontSize > 0 ? first.FontSize : (float?)null,
                        profileName: string.IsNullOrWhiteSpace(s.Profile) ? null : s.Profile);
                    for (int i = 1; i < pl.Count; i++)
                        AppendPane(ses,
                            string.IsNullOrEmpty(pl[i].Id) ? Guid.NewGuid().ToString() : pl[i].Id,
                            string.IsNullOrWhiteSpace(pl[i].Cwd) ? null : pl[i].Cwd,
                            pl[i].FontSize > 0 ? pl[i].FontSize : (float)_config.FontSize);
                    lock (_workspaces)
                    {
                        for (int i = 0; i < pl.Count && i < ses.Panes.Count; i++)
                            ses.Panes[i].Ratio = pl[i].Ratio > 0 ? pl[i].Ratio : 1f;
                        ses.Active = Math.Clamp(s.Active, 0, ses.Panes.Count - 1);
                    }
                    // Buffer-content restore: seed each pane's scrollback (dimmed) above the fresh shell.
                    if (_config.RestoreBuffer)
                        lock (_workspaces)
                            for (int i = 0; i < pl.Count && i < ses.Panes.Count; i++)
                                if (pl[i].Buffer is { Count: > 0 } b)
                                {
                                    var seed = new List<string>(b) { "──── restored ────" };
                                    var pane = ses.Panes[i];
                                    lock (pane.S.SyncRoot) pane.S.Emulator.SeedScrollback(seed);
                                }
                    ses.Flagged = s.Flagged;
                    ses.CustomName = string.IsNullOrWhiteSpace(s.CustomName) ? null : s.CustomName;
                    if (!string.IsNullOrEmpty(s.BgFile)) // restore the watermark if its copied file still exists
                    {
                        string bg = Path.Combine(BackgroundsDir, s.BgFile!);
                        if (File.Exists(bg)) { ses.BgPath = bg; ses.BgOpacity = s.BgOpacity; ses.BgMode = string.IsNullOrWhiteSpace(s.BgMode) ? "fit" : s.BgMode; }
                    }
                    if (ReferenceEquals(_active, ses)) _session = ses.S;
                    RegridSession(ses);

                    // Opt-in: re-run each pane's captured foreground command once the shell is ready.
                    if (_config.RestoreCommands)
                    {
                        var deny = LoadDenylist();
                        for (int i = 0; i < pl.Count && i < ses.Panes.Count; i++)
                        {
                            string cmd = pl[i].Command ?? "";
                            if (cmd.Length == 0) continue;
                            string lead = StripExe(cmd.TrimStart('"').Split(' ', 2)[0]);
                            if (deny.Contains(lead)) continue;
                            var pane = ses.Panes[i];
                            // Prefix the call operator: a captured command line starts with a quoted exe
                            // path, which pwsh would otherwise parse as a bare string literal.
                            string run = "& " + cmd;
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(2500); // let the shell/profile settle; pwsh buffers stdin until ready
                                try { pane.S.Write(System.Text.Encoding.UTF8.GetBytes(run + "\r")); } catch { }
                            });
                        }
                    }
                }
                lock (_workspaces) ws.Expanded = w.Expanded; // CreateSession forces Expanded=true; restore the saved state
            }
        }
        finally { _restoring = false; }

        // Restore sidebar mode + workspace focus (Wave D1). Focus that no longer resolves is dropped.
        _sidebarMode = string.Equals(st.SidebarMode, "flagged", StringComparison.OrdinalIgnoreCase) ? SidebarMode.Flagged : SidebarMode.Tree;
        _focusedWorkspaceId = st.FocusedWorkspaceId;
        if (st.Mru is { Count: > 0 }) { _mru.Clear(); _mru.AddRange(st.Mru); } // EnsureMru prunes dead ids on first walk
        if (_focusedWorkspaceId is not null)
            lock (_workspaces) if (!_workspaces.Any(w => w.Id == _focusedWorkspaceId)) _focusedWorkspaceId = null;

        if (_active is null) { var f = AllSessions().FirstOrDefault(); if (f is not null) SetActive(f); }
        if (AllSessions().Count == 0) return false; // nothing usable came back -> default
        SaveState();
        RequestRedraw();
        return true;
    }

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm", "agwinterm.conf");

    private static TerminalConfig LoadOrCreateConfig()
    {
        try
        {
            string path = ConfigPath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, TerminalConfig.DefaultText);
            }
            return TerminalConfig.Load(path);
        }
        catch { return new TerminalConfig(); }
    }
}
