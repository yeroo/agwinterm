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

/// <summary>Chrome: sidebar, inline rename, context menus, drag-reorder, palettes, title bar, box glyphs.</summary>
internal partial class Program
{
    // ---- Sidebar (custom Direct2D outline: workspaces -> sessions) ----

    // Chrome colours are DERIVED from the active theme (see RecomputeChrome), so a theme switch
    // recolours the whole window, not just the terminal cells. Defaults match the old dark chrome.
    private Color4 SbBg = new(0.055f, 0.063f, 0.071f, 1f);
    private Color4 SbHighlight = new(0.16f, 0.20f, 0.25f, 1f);
    private Color4 SbHeaderText = new(0.75f, 0.78f, 0.82f, 1f);
    private Color4 SbActiveText = new(1f, 1f, 1f, 1f);
    private Color4 SbDimText = new(0.60f, 0.63f, 0.67f, 1f);

    // Status-glyph colors are user-configurable (Agent Status settings tab); defaults match the originals.
    private Color4 StatusDot(AgentStatus s) => s switch
    {
        AgentStatus.Active => HexColor4(_config.StatusColorActive, new(60 / 255f, 140 / 255f, 255 / 255f, 1f)),
        AgentStatus.Blocked => HexColor4(_config.StatusColorBlocked, new(240 / 255f, 160 / 255f, 40 / 255f, 1f)),
        AgentStatus.Completed => HexColor4(_config.StatusColorCompleted, new(60 / 255f, 200 / 255f, 90 / 255f, 1f)),
        _ => new(90 / 255f, 96 / 255f, 102 / 255f, 1f),
    };

    /// <summary>Parse "#RRGGBB" (or "#RGB") to a Color4; returns <paramref name="fallback"/> on any failure.</summary>
    private static Color4 HexColor4(string hex, Color4 fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length < 6) return fallback;
        try { return new Color4(Convert.ToInt32(h[..2], 16) / 255f, Convert.ToInt32(h.Substring(2, 2), 16) / 255f, Convert.ToInt32(h.Substring(4, 2), 16) / 255f, 1f); }
        catch { return fallback; }
    }

    private void DrawSidebar(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        _sidebarRows.Clear();
        _footerButtons.Clear();
        if (_sidebarW <= 0) return;

        brush.Color = SbBg;
        rt.FillRectangle(new Rect(0, TitleBarH, _sidebarW, ClientH()), brush);

        float rowsBottom = ClientH() - FooterH; // stop the list above the footer toolbar
        float rowH = _cellH + 8f;
        float y = TitleBarH + PadY;

        if (_sidebarMode == SidebarMode.Flagged) { DrawFlaggedList(rt, brush, ref y, rowH, rowsBottom); }
        else { DrawTreeList(rt, brush, ref y, rowH, rowsBottom); }

        if (_dragging && _dragItem is not null) DrawDropIndicator(rt, brush);
        DrawSidebarFooter(rt, brush);
    }

    /// <summary>Tree mode: the workspace→session outline (or, when a workspace is focused, only that one + a "show all" banner).</summary>
    private void DrawTreeList(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, ref float y, float rowH, float rowsBottom)
    {
        List<Workspace> wss;
        lock (_workspaces) wss = _workspaces.ToList();

        Workspace? focused = _focusedWorkspaceId is null ? null : wss.FirstOrDefault(w => w.Id == _focusedWorkspaceId);
        if (focused is not null)
        {
            // "Focused" banner: a full-width accent strip; clicking it clears the focus (back to all workspaces).
            brush.Color = Mix(SbBg, ChromeAccent, 0.32f);
            rt.FillRectangle(new Rect(0, y, _sidebarW, rowH), brush);
            brush.Color = Mix(ChromeAccent, new Color4(1f, 1f, 1f, 1f), 0.35f);
            rt.DrawText("‹  Focused — show all", _uiSmall, new Rect(12f, y, _sidebarW - 20f, rowH), brush);
            _sidebarRows.Add((y, y + rowH, false, _showAllMarker));
            y += rowH;
            wss = new List<Workspace> { focused };
        }

        foreach (var ws in wss)
        {
            if (y + rowH > rowsBottom) break;
            List<Ses> sessions;
            bool expanded;
            lock (_workspaces) { sessions = ws.Sessions.ToList(); expanded = ws.Expanded; }

            brush.Color = SbHeaderText;
            rt.DrawText(expanded ? "▾" : "▸", _format, TextRect(6f, y, 18f, rowH), brush); // chevron (mono, top-aligned)
            if (!ReferenceEquals(_editing, ws)) // the rename box covers the name while editing
                rt.DrawText(ws.Name, _uiFont, new Rect(24f, y, _sidebarW - 48f, rowH), brush);
            rt.DrawText(sessions.Count.ToString(), _uiSmall, new Rect(_sidebarW - 28f, y, 22f, rowH), brush);
            _sidebarRows.Add((y, y + rowH, true, ws));
            y += rowH;

            if (!expanded) continue;
            foreach (var s in sessions)
            {
                if (y + rowH > rowsBottom) break;
                DrawSessionRow(rt, brush, s, y, rowH);
                y += rowH;
            }
        }
    }

    /// <summary>Flagged mode: a flat working-set of every flagged session across all workspaces (no headers).</summary>
    private void DrawFlaggedList(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, ref float y, float rowH, float rowsBottom)
    {
        brush.Color = SbHeaderText;
        rt.DrawText("FLAGGED", _uiSmall, new Rect(12f, y, _sidebarW - 20f, rowH), brush);
        y += rowH;

        var flagged = AllSessions().Where(s => s.Flagged).ToList();
        if (flagged.Count == 0)
        {
            brush.Color = SbDimText;
            rt.DrawText("No flagged sessions", _uiSmall, new Rect(16f, y, _sidebarW - 24f, rowH), brush); y += rowH;
            if (y + rowH <= rowsBottom) rt.DrawText("Ctrl+Shift+F flags the active one", _uiSmall, new Rect(16f, y, _sidebarW - 24f, rowH), brush);
            return;
        }
        foreach (var s in flagged)
        {
            if (y + rowH > rowsBottom) break;
            DrawSessionRow(rt, brush, s, y, rowH);
            y += rowH;
        }
    }

    /// <summary>Draw one session row (shared by tree + flagged modes): highlight, flag marker, name, unread badge, status dot.</summary>
    private void DrawSessionRow(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, Ses s, float y, float rowH)
    {
        bool active = ReferenceEquals(_active, s);
        if (active)
        {
            brush.Color = SbHighlight;
            rt.FillRectangle(new Rect(0, y, _sidebarW, rowH), brush);
        }
        // F6 focus ring: the sidebar zone's focused row (keyboard navigation, without the mouse).
        if (_chromeFocus && ReferenceEquals(_focusRow, s))
        {
            brush.Color = ChromeAccent;
            rt.DrawRectangle(new Rect(1.5f, y + 1.5f, _sidebarW - 3f, rowH - 3f), brush, 2f);
        }
        // Flag marker in the left gutter (before the name, which starts at x=26).
        if (s.Flagged)
        {
            brush.Color = new Color4(0.96f, 0.76f, 0.26f, 1f); // amber flag
            rt.DrawText("", _iconSmall, new Rect(6f, y, 18f, rowH), brush);
        }
        // Elevated (admin) sessions carry a ⚡ before the name (mixed elevated/non-elevated windows).
        float nameX = 26f;
        if (s.Elevated)
        {
            brush.Color = new Color4(0.98f, 0.82f, 0.25f, 1f); // gold lightning
            rt.DrawText("⚡", _uiSmall, new Rect(23f, y, 16f, rowH), brush);
            nameX = 40f;
        }
        bool isDrag = _dragging && ReferenceEquals(s, _dragItem);
        brush.Color = isDrag ? new Color4(0.5f, 0.53f, 0.57f, 0.45f) : (active ? SbActiveText : SbDimText);
        if (!ReferenceEquals(_editing, s)) // the rename box covers the name while editing
            rt.DrawText(s.Name, _uiFont, new Rect(nameX, y, _sidebarW - nameX - 22f, rowH), brush);
        // Unread-notification count badge, just left of the status circle (can be hidden; the count still tracks).
        int unread = UnreadOf(s);
        if (unread > 0 && _config.NotificationBadges)
        {
            string bn = unread > 99 ? "99+" : unread.ToString();
            float bw = MeasureText(bn, _uiSmall) + 10f, bx = _sidebarW - 30f - bw;
            brush.Color = new Color4(0.90f, 0.30f, 0.24f, 1f); // notification red pill
            rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(bx, y + rowH / 2f - 8f, bw, 16f), RadiusX = 8f, RadiusY = 8f }, brush);
            brush.Color = new Color4(1f, 1f, 1f, 1f);
            rt.DrawText(bn, _uiSmall, new Rect(bx + 5f, y + rowH / 2f - 8f, bw - 8f, 16f), brush);
        }
        // Status circle right-aligned in the row (agterm layout); pulse it if blink was requested.
        // Pane-aware: the dot shows the most attention-worthy status across ALL the session's panes.
        var dot = StatusDot(AggStatus(s));
        if (AggBlink(s) && !_cursorOn) dot = new Color4(dot.R, dot.G, dot.B, 0.22f);
        brush.Color = dot;
        rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(_sidebarW - 16f, y + rowH / 2f), 4.5f, 4.5f), brush);
        _sidebarRows.Add((y, y + rowH, false, s));
    }

    /// <summary>Sidebar footer (agterm layout): new-workspace, add-session menu | spacer | focus pill, flag toggle.</summary>
    private void DrawSidebarFooter(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        float y = ClientH() - FooterH;
        brush.Color = ChromeBg;
        rt.FillRectangle(new Rect(0, y, _sidebarW, FooterH), brush);

        float bwid = 34f, bx = 6f;
        // New Workspace (vector card + plus)
        {
            var c = ChromeBtnBg(rt, brush, bx, y, bwid, FooterH, "new-workspace", _footerButtons, ChromeDim);
            DrawNewWorkspaceGlyph(rt, brush, bx + bwid / 2f, y + FooterH / 2f, c);
        }
        bx += bwid;
        // Add Session (opens a menu: New Session / Open Directory…)
        {
            var c = ChromeBtnBg(rt, brush, bx, y, bwid, FooterH, "add-session", _footerButtons, ChromeDim);
            brush.Color = c;
            rt.DrawText(GlyphAdd, _iconFont, new Rect(bx, y, bwid, FooterH), brush);
        }

        // Flag / flagged-view toggle (far right)
        float fx = _sidebarW - bwid - 4f;
        bool flagged = _sidebarMode == SidebarMode.Flagged;
        {
            var baseC = flagged ? new Color4(0.96f, 0.76f, 0.26f, 1f) : ChromeDim; // amber when active
            var c = ChromeBtnBg(rt, brush, fx, y, bwid, FooterH, "flag", _footerButtons, baseC);
            DrawFlagGlyph(rt, brush, fx + bwid / 2f, y + FooterH / 2f, c, flagged);
        }

        // Focus pill (only when a workspace is focused): shows its name + ✕, click clears focus.
        if (_focusedWorkspaceId is not null)
        {
            Workspace? fw; lock (_workspaces) fw = _workspaces.FirstOrDefault(w => w.Id == _focusedWorkspaceId);
            if (fw is not null)
            {
                string label = fw.Name.Length > 12 ? fw.Name[..12] + "…" : fw.Name;
                float tw = MeasureText(label, _uiSmall);
                float pw = tw + 34f, ph = 20f;
                float px = MathF.Max(bx + bwid + 6f, fx - pw - 8f), py = y + (FooterH - ph) / 2f;
                var c = ChromeBtnBg(rt, brush, px, py - (FooterH - ph) / 2f + 0f, pw, FooterH, "unfocus", _footerButtons, ChromeDim);
                brush.Color = WithA(ChromeAccent, 0.22f);
                rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(px, py, pw, ph), RadiusX = 10f, RadiusY = 10f }, brush);
                brush.Color = c;
                rt.DrawText(label, _uiSmall, new Rect(px + 10f, py, tw + 4f, ph), brush);
                rt.DrawText(GlyphClose, _iconFont, new Rect(px + pw - 20f, py, 18f, ph), brush); // ChromeClose (✕)
            }
        }
    }

    // DrawText layout rect is Left/Top/Right/Bottom; vertically centre the glyph cell in the row.
    private Rect TextRect(float x, float y, float w, float rowH)
        => new(x, y + (rowH - _cellH) / 2f, x + w, y + (rowH + _cellH) / 2f);

    private void SidebarClick(int mx, int my)
    {
        foreach (var (y0, y1, isWs, item) in _sidebarRows)
        {
            if (my < y0 || my >= y1) continue;
            if (ReferenceEquals(item, _showAllMarker)) { _focusedWorkspaceId = null; RequestRedraw(); SaveState(); }
            else if (isWs && item is Workspace ws) { lock (_workspaces) ws.Expanded = !ws.Expanded; RequestRedraw(); SaveState(); }
            else if (!isWs && item is Ses s) SetActive(s);
            return;
        }
    }

    private object? RowAt(int my)
    {
        foreach (var (y0, y1, _, item) in _sidebarRows) if (my >= y0 && my < y1) return item;
        return null;
    }

    private static void GetCursorScreen(out int x, out int y) { GetCursorPos(out POINT p); x = p.x; y = p.y; }

    // ---- Inline rename (native child EDIT overlaid on the sidebar row) ----

    private void StartRename(object item)
    {
        if (item is not (Ses or Workspace)) return; // e.g. the "show all" focus banner isn't renamable
        if (_editHwnd != IntPtr.Zero) CommitRename();
        bool isWs = item is Workspace;
        float ry0 = -1, ry1 = -1;
        foreach (var (y0, y1, _, it) in _sidebarRows) if (ReferenceEquals(it, item)) { ry0 = y0; ry1 = y1; break; }
        if (ry0 < 0) { RequestRedraw(); return; } // row not currently visible
        string name = item is Ses s ? s.Name : ((Workspace)item).Name;
        EnsureEditGdi();
        // Fill the whole row (matches the highlight band); a left text-margin puts the text exactly
        // where the row name is drawn, so nothing shifts when editing starts.
        // The single-line EDIT centres its text ~1px higher and its glyph sits ~1px right of the
        // margin vs DirectWrite; offsets tuned by pixel-measuring the box against an unedited row.
        int leftMargin = isWs ? 23 : 25;
        int ey = (int)ry0 + 4, eh = (int)(ry1 - ry0);
        _editHwnd = CreateWindowExW(0, "EDIT", name, WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
            0, ey, (int)_sidebarW, eh, _hwnd, (IntPtr)EDIT_ID, GetModuleHandleW(null), IntPtr.Zero);
        if (_editHwnd == IntPtr.Zero) return;
        SendMessageW(_editHwnd, WM_SETFONT, _editFont, (IntPtr)1);
        SendMessageW(_editHwnd, EM_SETMARGINS, (IntPtr)(EC_LEFTMARGIN | EC_RIGHTMARGIN), (IntPtr)(leftMargin | (8 << 16)));
        SendMessageW(_editHwnd, (uint)EM_SETSEL, IntPtr.Zero, (IntPtr)(-1)); // select all
        SetFocus(_editHwnd);
        _editProc = EditProc; // keep alive
        _editOrigProc = SetWindowLongPtrW(_editHwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_editProc));
        _editing = item;
        RequestRedraw();
    }

    // Subclass of the EDIT: commit on Enter, cancel on Escape, and swallow those chars (no beep).
    private static IntPtr EditProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var w = Frontmost; // the rename EDIT belongs to the frontmost window
        if (msg == WM_KEYDOWN)
        {
            if ((int)wParam == VK_RETURN) { w.CommitRename(); return IntPtr.Zero; }
            if ((int)wParam == VK_ESCAPE) { w.CancelRename(); return IntPtr.Zero; }
        }
        else if (msg == WM_CHAR)
        {
            int c = (int)wParam;
            if (c == VK_RETURN || c == VK_ESCAPE || c == '\t') return IntPtr.Zero;
        }
        return CallWindowProcW(w._editOrigProc, hwnd, msg, wParam, lParam);
    }

    private void CommitRename()
    {
        if (_editHwnd == IntPtr.Zero) return;
        var h = _editHwnd; var item = _editing;
        _editHwnd = IntPtr.Zero; _editing = null; // clear first: EN_KILLFOCUS during destroy is then a no-op
        var sb = new StringBuilder(256);
        GetWindowTextW(h, sb, 256);
        string name = sb.ToString().Trim();
        if (name.Length > 0)
        {
            if (item is Ses s) { s.Name = name; s.CustomName = name; } // CustomName drives the title bar
            else if (item is Workspace w) w.Name = name;
        }
        DestroyEditWindow(h);
        RequestRedraw();
        SaveState();
    }

    private void CancelRename()
    {
        if (_editHwnd == IntPtr.Zero) return;
        var h = _editHwnd; _editHwnd = IntPtr.Zero; _editing = null;
        DestroyEditWindow(h);
        RequestRedraw();
    }

    private void DestroyEditWindow(IntPtr h)
    {
        if (_editOrigProc != IntPtr.Zero) { SetWindowLongPtrW(h, GWLP_WNDPROC, _editOrigProc); _editOrigProc = IntPtr.Zero; }
        DestroyWindow(h);
        SetFocus(_hwnd);
    }

    // ---- Context menus ----

    /// <summary>Sidebar right-click menu at screen point (sx, sy): a themed popup window (Menu.cs)
    /// that — like a native menu — can extend beyond the main window's bounds.</summary>
    private void ShowContextMenuWindow(object item, int sx, int sy) => ShowMenuWindow(BuildContextItems(item), sx, sy);

    /// <summary>The context-menu items for a sidebar row (mirrors agterm's session/workspace menus).</summary>
    private List<PalItem> BuildContextItems(object item)
    {
        var list = new List<PalItem>();
        void A(string label, string hint, Action run) => list.Add(new PalItem { Label = label, Hint = hint, Search = label, Run = run });
        if (item is Ses ses)
        {
            if (_profileCfg.Profiles.Count > 1)
                foreach (var p in _profileCfg.Profiles)
                { var nm = p.Name; A($"New Session — {nm}", "", () => CreateSession(Guid.NewGuid().ToString(), null, null, ses.Ws, true, profileName: nm)); }
            else A("New Session", "", () => CreateSession(Guid.NewGuid().ToString(), null, null, ses.Ws, true));
            if (IsElevated()) A("New Non-Elevated Session", "", () => CreateSession(Guid.NewGuid().ToString(), null, null, ses.Ws, true, deElevate: true));
            A("Open Directory…", "", () => { var d = PickFolder(); if (d is not null) CreateSession(Guid.NewGuid().ToString(), null, d, ses.Ws, true); });
            A("Rename", "F2", () => StartRename(ses));
            A(ses.Flagged ? "Unflag Session" : "Flag Session", "", () => FlagOp(ses, "toggle"));
            List<Workspace> targets;
            lock (_workspaces) targets = _workspaces.Where(w => !ReferenceEquals(w, ses.Ws)).ToList();
            foreach (var t in targets) A($"Move to — {t.Name}", "", () => MoveSession(ses, t));
            if (AggStatus(ses) != AgentStatus.Idle) A("Clear Status", "", () => { foreach (var p in ses.Panes) p.S.SetStatus(AgentStatus.Idle); RequestRedraw(); });
            list.Add(MenuSeparator());
            A("Close Session", "", () => { if (ConfirmCloseOk()) CloseSessionInternal(ses); });
        }
        else if (item is Workspace cws)
        {
            bool wsFocused = _focusedWorkspaceId == cws.Id;
            int wsCount; lock (_workspaces) wsCount = _workspaces.Count;
            if (_profileCfg.Profiles.Count > 1)
                foreach (var p in _profileCfg.Profiles)
                { var nm = p.Name; A($"New Session — {nm}", "", () => CreateSession(Guid.NewGuid().ToString(), null, null, cws, true, profileName: nm)); }
            else A("New Session", "", () => CreateSession(Guid.NewGuid().ToString(), null, null, cws, true));
            if (IsElevated()) A("New Non-Elevated Session", "", () => CreateSession(Guid.NewGuid().ToString(), null, null, cws, true, deElevate: true));
            A("Open Directory…", "", () => { var d = PickFolder(); if (d is not null) CreateSession(Guid.NewGuid().ToString(), null, d, cws, true); });
            A("Rename", "", () => StartRename(cws));
            A(wsFocused ? "Unfocus" : "Focus", "", () => WorkspaceFocusOp(wsFocused ? "off" : "on", cws.Id));
            if (wsCount > 1) { list.Add(MenuSeparator()); A("Delete Workspace", "", () => DeleteWorkspace(cws)); }
        }
        return list;
    }

    private void MoveSession(Ses ses, Workspace target)
    {
        lock (_workspaces) { ses.Ws.Sessions.Remove(ses); target.Sessions.Add(ses); ses.Ws = target; target.Expanded = true; }
        SetActive(ses);
    }

    // ---- Sidebar drag-reorder (in-memory) ----

    private void DropDrag(object item, int my)
    {
        if (item is Ses s) DropSession(s, my);
        else if (item is Workspace w) DropWorkspace(w, my);
    }

    /// <summary>The workspace whose region contains <paramref name="my"/> (last header at/above it; first if above all).</summary>
    private Workspace? TargetWorkspaceAt(int my)
    {
        Workspace? cur = null;
        foreach (var (y0, _, isWs, it) in _sidebarRows)
        {
            if (!isWs || it is not Workspace w) continue;
            if (cur is null) cur = w;   // default to the first workspace
            if (y0 <= my) cur = w;      // the last header at/above my wins
        }
        return cur;
    }

    private void DropSession(Ses drag, int my)
    {
        lock (_workspaces)
        {
            var target = TargetWorkspaceAt(my);
            if (target is null) return;
            // Insert index = visible target-ws session rows (excluding the dragged one) above my.
            int idx = 0;
            foreach (var (y0, y1, isWs, it) in _sidebarRows)
                if (!isWs && it is Ses r && ReferenceEquals(r.Ws, target) && !ReferenceEquals(r, drag)
                    && (y0 + y1) / 2f < my) idx++;
            drag.Ws.Sessions.Remove(drag);
            idx = Math.Clamp(idx, 0, target.Sessions.Count);
            target.Sessions.Insert(idx, drag);
            drag.Ws = target;
            target.Expanded = true;
        }
        SetActive(drag);
    }

    private void DropWorkspace(Workspace drag, int my)
    {
        lock (_workspaces)
        {
            int idx = 0;
            foreach (var (y0, y1, isWs, it) in _sidebarRows)
                if (isWs && it is Workspace w && !ReferenceEquals(w, drag) && (y0 + y1) / 2f < my) idx++;
            _workspaces.Remove(drag);
            idx = Math.Clamp(idx, 0, _workspaces.Count);
            _workspaces.Insert(idx, drag);
        }
        RequestRedraw();
        SaveState();
    }

    /// <summary>Pixel Y of the insertion line for the in-progress drag (-1 if none).</summary>
    private float DropIndicatorY()
    {
        if (_dragItem is Workspace)
        {
            float lastY1 = -1;
            foreach (var (y0, y1, isWs, it) in _sidebarRows)
                if (isWs && it is Workspace w && !ReferenceEquals(w, _dragItem))
                {
                    if (_dragY < (y0 + y1) / 2f) return y0;
                    lastY1 = y1;
                }
            return lastY1;
        }
        if (_dragItem is Ses)
        {
            var target = TargetWorkspaceAt(_dragY);
            if (target is null) return -1;
            float headerBottom = -1, lastRowY1 = -1;
            foreach (var (y0, y1, isWs, it) in _sidebarRows)
            {
                if (isWs && it is Workspace w && ReferenceEquals(w, target)) headerBottom = y1;
                else if (!isWs && it is Ses r && ReferenceEquals(r.Ws, target) && !ReferenceEquals(r, _dragItem))
                {
                    if (_dragY < (y0 + y1) / 2f) return y0;
                    lastRowY1 = y1;
                }
            }
            return lastRowY1 > 0 ? lastRowY1 : headerBottom;
        }
        return -1;
    }

    private void DrawDropIndicator(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        float lineY = DropIndicatorY();
        if (lineY >= 0)
        {
            brush.Color = new Color4(0.30f, 0.60f, 0.98f, 1f);
            rt.FillRectangle(new Rect(0, lineY - 1f, _sidebarW, 2f), brush);
        }
        string label = _dragItem is Ses s ? s.Name : _dragItem is Workspace w ? w.Name : "";
        if (label.Length > 0)
        {
            brush.Color = new Color4(1f, 1f, 1f, 0.92f);
            rt.DrawText(label, _uiFont, new Rect(24f, _dragY - 9f, _sidebarW - 8f, _dragY + 11f), brush);
        }
    }

    private void DeleteWorkspace(Workspace ws)
    {
        lock (_workspaces) if (_workspaces.Count <= 1) return; // agterm: can't delete the last workspace
        List<Ses> sessions;
        bool hadActive = _active is not null && ReferenceEquals(_active.Ws, ws);
        lock (_workspaces)
        {
            sessions = ws.Sessions.ToList();
            CaptureClosedWorkspace(ws, sessions);   // remember it so Reopen Closed can bring the whole workspace back
            ws.Sessions.Clear();
            _workspaces.Remove(ws);
            if (_workspaces.Count == 0) _workspaces.Add(new Workspace { Id = Guid.NewGuid().ToString(), Name = "workspace 1" });
        }
        foreach (var s in sessions) { try { s.S.Dispose(); } catch { } }
        if (hadActive)
        {
            var next = AllSessions().FirstOrDefault();
            if (next is not null) SetActive(next);
            else CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), true);
        }
        RequestRedraw();
        SaveState();
    }

    /// <summary>Modal folder picker (native shell). Returns the chosen path or null.</summary>
    private string? PickFolder()
    {
        var bi = new BROWSEINFO
        {
            hwndOwner = _hwnd,
            lpszTitle = "Open Directory",
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE,
        };
        IntPtr pidl = SHBrowseForFolderW(ref bi);
        if (pidl == IntPtr.Zero) return null;
        var sb = new StringBuilder(260);
        bool ok = SHGetPathFromIDListW(pidl, sb);
        CoTaskMemFree(pidl);
        return ok && sb.Length > 0 ? sb.ToString() : null;
    }

    // ---- Agent attention (title-bar bell + jump-to-next) ----

    private (bool blocked, bool active) AttentionState()
    {
        bool blocked = false, active = false;
        foreach (var s in AllSessions())
        {
            var st = AggStatus(s);
            if (st == AgentStatus.Blocked) blocked = true;
            else if (st is AgentStatus.Active or AgentStatus.Completed) active = true;
        }
        return (blocked, active);
    }

    private void GoToNextAttention(int dir)
    {
        var list = AllSessions().Where(s => AggStatus(s) is AgentStatus.Blocked or AgentStatus.Completed).ToList();
        if (list.Count == 0) { ShowToast("no sessions need attention"); return; }
        int idx = _active is not null ? list.IndexOf(_active) : -1;
        int next = idx < 0 ? (dir > 0 ? 0 : list.Count - 1) : ((idx + dir) % list.Count + list.Count) % list.Count;
        SetActive(list[next]);
    }

    /// <summary>True if any attention-worthy session has its blink flag set (drives the bell pulse).</summary>
    private bool AnyBlinkAttention()
    {
        foreach (var s in AllSessions())
            if (AggBlink(s)) return true;
        return false;
    }

    // ---- Agent-status sounds (winmm / System.Media; all playback is async / off the UI thread) ----

    [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);
    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_ALIAS = 0x00010000, SND_FILENAME = 0x00020000;
    // MessageBeep types (standard system-sound events; async, no extra assembly needed).
    private const uint MB_OK = 0x0, MB_ICONHAND = 0x10, MB_ICONQUESTION = 0x20, MB_ICONEXCLAMATION = 0x30, MB_ICONASTERISK = 0x40;

    /// <summary>Play the config's blocked-sound (silent when unset / "off" / "none").</summary>
    private static void PlayBlockedSound()
    {
        string bs = _config.BlockedSound;
        if (string.IsNullOrWhiteSpace(bs) || bs.Equals("off", StringComparison.OrdinalIgnoreCase)
            || bs.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        PlayStatusSound(bs);
    }

    /// <summary>Play a sound spec: null/"default" => system alert; a known system-sound name; a .wav path;
    /// otherwise a Windows sound-event alias. Never throws.</summary>
    private static void PlayStatusSound(string? spec)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(spec) || spec.Equals("default", StringComparison.OrdinalIgnoreCase))
            { MessageBeep(MB_ICONASTERISK); return; }
            switch (spec.ToLowerInvariant())
            {
                case "beep": MessageBeep(MB_OK); return;
                case "asterisk": MessageBeep(MB_ICONASTERISK); return;
                case "exclamation": MessageBeep(MB_ICONEXCLAMATION); return;
                case "hand" or "error" or "critical": MessageBeep(MB_ICONHAND); return;
                case "question": MessageBeep(MB_ICONQUESTION); return;
            }
            if (spec.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(spec))
            { PlaySound(spec, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT); return; }
            // Treat anything else as a Windows sound-event alias (e.g. "SystemNotification").
            PlaySound(spec, IntPtr.Zero, SND_ASYNC | SND_ALIAS | SND_NODEFAULT);
        }
        catch { /* audio is best-effort */ }
    }

    // ---- Command palette (⌃P sessions / ⌃⇧P actions / ⌃⇧I attention) ----

    private static readonly Color4 PalScrim = new(0f, 0f, 0f, 0.45f);
    private Color4 PalBg = new(0.11f, 0.12f, 0.145f, 1f);
    private Color4 PalBorder = new(0.30f, 0.34f, 0.42f, 1f);
    private Color4 PalSel = new(0.20f, 0.30f, 0.46f, 1f);

    private void TogglePalette(PaletteKind kind)
    {
        if (_palette == kind) { ClosePalette(); return; }
        if (kind == PaletteKind.Themes) _themeBeforePreview = _theme;
        // Engine/font pickers detect installed tools: pick up a just-finished winget install.
        if (kind is PaletteKind.PromptEngine or PaletteKind.Fonts or PaletteKind.Starship) RefreshEnvPath();
        _palette = kind; _palQuery = ""; _palSel = 0;
        BuildPaletteItems();
        FilterPalette();          // calls PreviewSelectedTheme() at its end
        RequestRedraw();
    }

    private void ClosePalette()
    {
        KillTimer(_hwnd, (IntPtr)PromptPreviewTimer);
        // If the prompt picker opened the scratch just for its preview, put it away again. Runs
        // BEFORE any item.Run (RunPaletteSelection closes first), so apply targets the real pane.
        if (_previewScratchOpened) { _previewScratchOpened = false; if (_coverKind == 1) HideCover(); }
        _palette = PaletteKind.None;
        _palAll.Clear(); _palItems.Clear(); _palRows.Clear();
        RequestRedraw();
    }

    private void BuildPaletteItems()
    {
        _palAll.Clear();
        switch (_palette)
        {
            case PaletteKind.Windows:
            {
                foreach (var w in Windows())
                {
                    var sel = w.Id;
                    string label = string.IsNullOrEmpty(w.Name) ? w.Id[..Math.Min(8, w.Id.Length)] : w.Name;
                    _palAll.Add(new PalItem
                    {
                        Label = label + (w.Active ? "  (active)" : ""),
                        Secondary = (w.Open ? "open" : "closed") + "  ·  " + w.Id[..Math.Min(8, w.Id.Length)],
                        Search = $"{label} {w.Id}",
                        Run = () => WindowSelect(sel),
                    });
                }
                if (_palAll.Count == 0) _palAll.Add(new PalItem { Label = "No windows", Run = null });
                break;
            }
            case PaletteKind.Sessions:
            {
                foreach (var s in AllSessions())
                {
                    var sx = s;
                    string cwd = PrettyCwd(SafeCwd(sx));
                    _palAll.Add(new PalItem
                    {
                        Label = sx.Name,
                        Secondary = cwd.Length > 0 ? $"{sx.Ws.Name}  ·  {cwd}" : sx.Ws.Name,
                        Search = $"{sx.Name} {sx.Ws.Name} {cwd}",
                        Dot = AggStatus(sx),
                        Run = () => { lock (_workspaces) sx.Ws.Expanded = true; SetActive(sx); },
                    });
                }
                break;
            }
            case PaletteKind.Actions:
            {
                void A(string label, string hint, Action run) => _palAll.Add(new PalItem { Label = label, Hint = hint, Search = label, Run = run });
                A("New Session", "Ctrl+Shift+T", () => CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), true));
                A("New Session…", "", () => TogglePalette(PaletteKind.NewSession));   // pick a shell profile
                if (_closedSessions.Count > 0 || _closedWorkspaces.Count > 0)
                {
                    long sSeq = _closedSessions.Count > 0 ? _closedSessions[^1].Seq : -1;
                    long wSeq = _closedWorkspaces.Count > 0 ? _closedWorkspaces[^1].Seq : -1;
                    string top = wSeq > sSeq ? $"workspace {_closedWorkspaces[^1].Name}" : _closedSessions[^1].Display;
                    A($"Reopen Closed ({top})", "Ctrl+Shift+R", ReopenMostRecent);
                    // Recent closed sessions, then closed workspaces (most-recent first), each reopenable directly.
                    for (int ci = _closedSessions.Count - 1; ci >= 0 && ci >= _closedSessions.Count - 8; ci--)
                    { var cs = _closedSessions[ci]; A($"Reopen Session — {cs.Display}", "", () => ReopenClosedSession(cs)); }
                    for (int wi = _closedWorkspaces.Count - 1; wi >= 0 && wi >= _closedWorkspaces.Count - 8; wi--)
                    { var cw = _closedWorkspaces[wi]; A($"Reopen Workspace — {cw.Name} ({cw.Sessions.Count})", "", () => ReopenClosedWorkspace(cw)); }
                }
                A("New Workspace", "Ctrl+Shift+N", () => CreateWorkspace(Guid.NewGuid().ToString(), null));
                A("New Window", "Ctrl+Alt+N", () => WindowNew(null));
                A("Toggle Fullscreen", "F11", ToggleFullscreen);
                A("Toggle Broadcast Input", "", ToggleBroadcast);   // typing fans out to the whole workspace
                A("Mark Mode (keyboard select)", "Ctrl+Shift+M", ToggleMarkMode);
                A("Toggle Read-Only Pane", "", () => { if (ActiveSurface() is { } rp) { rp.ReadOnly = !rp.ReadOnly; ShowToast(rp.ReadOnly ? "pane read-only ON" : "pane read-only off"); RequestRedraw(); } });
                A("Close Window", "", () => DestroyWindow(_hwnd));
                A("Switch Window…", "", () => TogglePalette(PaletteKind.Windows));
                A("Rename Active Session", "F2", () => { if (_active is not null) StartRename(_active); });
                A("Close Pane / Session", "Ctrl+Shift+W", CloseActivePane);
                A("Split Pane", "Ctrl+D", () => SplitOp("toggle"));
                A("Focus Left Pane", "Ctrl+Alt+Left", () => FocusPane(-1));
                A("Focus Right Pane", "Ctrl+Alt+Right", () => FocusPane(1));
                A("Delete Active Workspace", "", () => { if (_active is not null) DeleteWorkspace(_active.Ws); });
                A("Flag / Unflag Session", "Ctrl+Shift+F", () => { if (_active is not null) FlagOp(_active, "toggle"); });
                A("Show Flagged / All Sessions", "", ToggleFlaggedView);
                A("Focus Workspace", "", () => WorkspaceFocusOp("toggle"));
                A("Toggle Sidebar", "", ToggleSidebar);
                A("Select All", "Ctrl+Shift+A", () => { if (ActiveSurface() is { } p) SelectAll(p); });
                A("Copy Selection", "Ctrl+C", () => { if (ActiveSurface() is { } p && p.HasSel) CopySelection(p); });
                A("Paste", "Ctrl+V", () => { if (ActiveSurface() is { } p) PasteInto(p); });
                A("Next Session", "Ctrl+Tab", () => CycleSession(1));
                A("Previous Session", "Ctrl+Shift+Tab", () => CycleSession(-1));
                A("Next Attention", "Ctrl+Alt+Down", () => GoToNextAttention(1));
                A("Previous Attention", "Ctrl+Alt+Up", () => GoToNextAttention(-1));
                A("Increase Font Size", "Ctrl+=", () => ChangeFontSize(1));
                A("Decrease Font Size", "Ctrl+-", () => ChangeFontSize(-1));
                A("Reset Font Size", "Ctrl+0", () => ChangeFontSize(0));
                A("Scratch Terminal", "Ctrl+J", () => { if (_active is not null) ScratchOp(_active, "toggle"); });
                A("Quick Terminal", "Ctrl+`", () => QuickOp("toggle"));
                A("Select Theme…", "", () => TogglePalette(PaletteKind.Themes));
                A("Prompt Engine…", "", () => TogglePalette(PaletteKind.PromptEngine));   // omp | starship | vanilla
                // Theme picker for the ACTIVE engine only (and only when that engine is installed).
                if (_config.PromptEngine == "omp" && OmpAvailable())
                    A("oh-my-posh Theme…", "", () => TogglePalette(PaletteKind.Omp));
                if (_config.PromptEngine == "starship" && Agwinterm.Pty.StarshipPresets.Available())
                    A("Starship Theme…", "", () => TogglePalette(PaletteKind.Starship));
                A("Install Nerd Font…", "", () => TogglePalette(PaletteKind.Fonts));   // omp/starship themes need one
                A("Settings…", "", OpenSettingsWindow);
                A("Custom Commands…", "Ctrl+Shift+O", () => TogglePalette(PaletteKind.Custom));
                // Opt-in integrations (agterm's Help-menu trio + shell) — the installer stays minimal.
                A("Install Command-Line Tool (PATH)", "", InstallCli);
                A("Install Agent Status Hooks", "", InstallHooks);
                A("Install Agent Skill", "", InstallSkill);
                A("Install Shell Integration", "", InstallShellIntegration);
                A("Reload Keymap", "", ReloadKeymap);
                break;
            }
            case PaletteKind.Custom:
            {
                if (_commands.Count == 0)
                {
                    _palAll.Add(new PalItem { Label = "No custom commands", Secondary = "define them in keymap.conf", Run = null });
                    break;
                }
                foreach (var c in _commands)
                {
                    var cc = c;
                    string sec = cc.Mode == "send" ? cc.Text : $"[{cc.Mode}]  {cc.Text}";
                    _palAll.Add(new PalItem { Label = cc.Label, Secondary = sec, Search = $"{cc.Label} {cc.Text}", Run = () => RunCustomCommand(cc) });
                }
                break;
            }
            case PaletteKind.Themes:
            {
                foreach (var th in _allThemes)
                {
                    var tx = th;
                    _palAll.Add(new PalItem { Label = tx.Name, Search = tx.Name, Data = tx, Run = () => CommitTheme(tx) });
                }
                break;
            }
            case PaletteKind.Omp:
            {
                var themes = Agwinterm.Pty.OmpThemes.List();
                if (themes.Count == 0)
                {
                    // The Store install ships no themes dir — offer the official pack (GitHub release).
                    _palAll.Add(new PalItem { Label = "Download themes pack…",
                        Secondary = "no themes on disk — fetch the official oh-my-posh themes from GitHub",
                        Search = "download themes pack", Run = DownloadOmpThemes });
                    break;
                }
                foreach (var (nm, pth) in themes)
                {
                    var p = pth; // applies live + persists so new sessions keep it
                    _palAll.Add(new PalItem { Label = nm, Search = nm, Data = p, Run = () => ApplyOmp(p, persist: true) });
                }
                // Our downloaded pack in use: offer a refresh (new omp releases add/update themes).
                if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm", "omp-themes")))
                    _palAll.Add(new PalItem { Label = "Update themes pack…",
                        Secondary = "re-download the official themes from the latest oh-my-posh release",
                        Search = "update refresh download themes pack", Run = DownloadOmpThemes });
                break;
            }
            case PaletteKind.Starship:
            {
                var presets = Agwinterm.Pty.StarshipPresets.List();
                if (presets.Count == 0)
                {
                    _palAll.Add(new PalItem { Label = "No starship presets found",
                        Secondary = "install starship (https://starship.rs) so `starship preset --list` works", Run = null });
                    break;
                }
                _palAll.Add(new PalItem { Label = "default", Secondary = "starship's built-in config",
                    Search = "default", Data = "", Run = () => ApplyStarship("", persist: true) });
                foreach (var name in presets)
                {
                    var n = name; // applies live + persists so new sessions keep it
                    _palAll.Add(new PalItem { Label = n, Search = n, Data = n, Run = () => ApplyStarship(n, persist: true) });
                }
                break;
            }
            case PaletteKind.PromptEngine:
            {
                // Detected engine -> selecting switches to it; undetected -> selecting installs it
                // (winget in an overlay terminal), then re-opening the picker offers the switch.
                void E(string id, string label, bool detected, string readySecondary) => _palAll.Add(new PalItem
                {
                    Label = label + (_config.PromptEngine == id ? "  (current)" : detected ? "" : "  — install…"),
                    Secondary = detected ? readySecondary : "not detected — select to install via winget (overlay shows progress)",
                    Search = label + " install",
                    Run = detected ? () => ApplyPromptEngine(id) : () => InstallPromptEngine(id),
                });
                E("omp", "oh-my-posh", OmpAvailable(), "theme picker + injection into new sessions");
                E("starship", "Starship", Agwinterm.Pty.StarshipPresets.Available(), "preset picker + injection into new sessions");
                _palAll.Add(new PalItem
                {
                    Label = "Vanilla" + (_config.PromptEngine == "vanilla" ? "  (current)" : ""),
                    Secondary = "plain \"PS C:\\>\" prompt — overrides profile prompt customizations",
                    Search = "vanilla plain default",
                    Run = () => ApplyPromptEngine("vanilla"),
                });
                _palAll.Add(new PalItem
                {
                    Label = "Profile" + (_config.PromptEngine == "profile" ? "  (current)" : ""),
                    Secondary = "no injection at all — whatever your $PROFILE sets up rules",
                    Search = "profile none leave alone",
                    Run = () => ApplyPromptEngine("profile"),
                });
                _palAll.Add(new PalItem
                {
                    Label = "Install Nerd Font…",
                    Secondary = "omp/starship themes need a Nerd Font for their glyphs",
                    Search = "install nerd font",
                    Run = () => TogglePalette(PaletteKind.Fonts),
                });
                break;
            }
            case PaletteKind.Fonts:
            {
                // Popular Nerd Fonts, installed via oh-my-posh's font tool (works for starship too —
                // fonts are engine-agnostic). Selecting installs in an overlay AND points font-family
                // at the new family so the terminal uses it once installed.
                foreach (var (slug, family) in new[]
                {
                    ("meslo", "MesloLGM Nerd Font"),
                    ("jetbrainsmono", "JetBrainsMono Nerd Font"),
                    ("cascadiacode", "CaskaydiaCove Nerd Font"),
                    ("firacode", "FiraCode Nerd Font"),
                    ("hack", "Hack Nerd Font"),
                })
                {
                    var (s, f) = (slug, family);
                    bool current = string.Equals(_config.FontFamily, f, StringComparison.OrdinalIgnoreCase);
                    _palAll.Add(new PalItem
                    {
                        Label = f + (current ? "  (current font)" : ""),
                        Secondary = $"oh-my-posh font install {s} + set font-family",
                        Search = f + " " + s,
                        Run = () => InstallNerdFont(s, f),
                    });
                }
                break;
            }
            case PaletteKind.NewSession:
            {
                foreach (var p in _profileCfg.Profiles)
                {
                    var name = p.Name;
                    string cmd = p.Command + (p.Args is { Length: > 0 } aa ? " " + string.Join(" ", aa) : "");
                    bool def = name.Equals(_profileCfg.Default, StringComparison.OrdinalIgnoreCase);
                    _palAll.Add(new PalItem
                    {
                        Label = name + (def ? "  (default)" : ""),
                        Secondary = cmd,
                        Search = name,
                        Run = () => CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), true, profileName: name),
                    });
                }
                _palAll.Add(new PalItem
                {
                    Label = "Open Directory…",
                    Secondary = "pick a folder for a new session",
                    Search = "open directory folder",
                    Run = () => { var d = PickFolder(); if (d is not null) CreateSession(Guid.NewGuid().ToString(), null, d, ActiveWorkspace(), true); },
                });
                break;
            }
            case PaletteKind.Attention:
            {
                var att = AllSessions().Where(s => AggStatus(s) != AgentStatus.Idle)
                    .OrderBy(s => AggStatus(s) == AgentStatus.Blocked ? 0 : AggStatus(s) == AgentStatus.Active ? 1 : 2)
                    .ToList();
                if (att.Count == 0) { _palAll.Add(new PalItem { Label = "No sessions need attention", Run = null }); break; }
                foreach (var s in att)
                {
                    var sx = s;
                    _palAll.Add(new PalItem
                    {
                        Label = sx.Name,
                        Secondary = $"{sx.Ws.Name}  ·  {AggStatus(sx).ToString().ToLowerInvariant()}",
                        Search = $"{sx.Name} {sx.Ws.Name}",
                        Dot = AggStatus(sx),
                        Run = () => { lock (_workspaces) sx.Ws.Expanded = true; SetActive(sx); },
                    });
                }
                break;
            }
        }
    }

    private void FilterPalette()
    {
        _palItems.Clear();
        if (_palQuery.Length == 0) { _palItems.AddRange(_palAll); }
        else
        {
            string q = _palQuery.ToLowerInvariant();
            _palItems.AddRange(_palAll
                .Select(it => (it, sc: FuzzyScore(q, it.Search.ToLowerInvariant())))
                .Where(x => x.sc >= 0)
                .OrderByDescending(x => x.sc).ThenBy(x => x.it.Label, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.it));
        }
        if (_palSel >= _palItems.Count) _palSel = Math.Max(0, _palItems.Count - 1);
        PreviewSelectedTheme();
        SchedulePromptPreview();
    }

    /// <summary>Subsequence fuzzy score: -1 = no match; higher = better (contiguity + earliness).</summary>
    private static int FuzzyScore(string q, string text)
    {
        if (q.Length == 0) return 0;
        int ti = 0, score = 0, streak = 0, first = -1;
        foreach (char qc in q)
        {
            int found = text.IndexOf(qc, ti);
            if (found < 0) return -1;
            if (first < 0) first = found;
            if (found == ti) { streak++; score += 8 + streak * 2; } else { streak = 0; score += 1; }
            ti = found + 1;
        }
        return score + Math.Max(0, 12 - first);
    }

    private bool PaletteKeyDown(int vk)
    {
        switch (vk)
        {
            case VK_ESCAPE:
                if (_palette == PaletteKind.Themes && _themeBeforePreview is not null) ApplyTheme(_themeBeforePreview);
                ClosePalette(); return true;
            case VK_RETURN: RunPaletteSelection(); return true;
            case VK_UP: if (_palSel > 0) { _palSel--; PreviewSelectedTheme(); SchedulePromptPreview(); RequestRedraw(); } return true;
            case VK_DOWN: if (_palSel < _palItems.Count - 1) { _palSel++; PreviewSelectedTheme(); SchedulePromptPreview(); RequestRedraw(); } return true;
            case VK_BACK: if (_palQuery.Length > 0) { _palQuery = _palQuery[..^1]; _palSel = 0; FilterPalette(); RequestRedraw(); } return true;
        }
        return true; // swallow everything else from the terminal; printable arrives via WM_CHAR
    }

    private void RunPaletteSelection()
    {
        Action? run = (_palSel >= 0 && _palSel < _palItems.Count) ? _palItems[_palSel].Run : null;
        ClosePalette();
        run?.Invoke();
    }

    private void PaletteClick(int mx, int my)
    {
        foreach (var r in _palRows)
            if (my >= r.y0 && my < r.y1) { _palSel = r.idx; RunPaletteSelection(); return; }
        bool inPanel = mx >= _palPanel.X && mx < _palPanel.X + _palPanel.Width && my >= _palPanel.Y && my < _palPanel.Y + _palPanel.Height;
        if (!inPanel)
        {
            if (_palette == PaletteKind.Themes && _themeBeforePreview is not null) ApplyTheme(_themeBeforePreview);
            ClosePalette(); // click outside closes; inside (padding/query) is ignored
        }
    }

    private void DrawPalette(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (_palette == PaletteKind.None) return;
        _palRows.Clear();
        int cw = ClientW(), ch = ClientH();
        // The prompt pickers preview into the scratch terminal behind the panel: skip the scrim and
        // drop the panel lower so the rendered prompt (top of the scratch) stays readable.
        bool promptPreview = _palette is PaletteKind.Omp or PaletteKind.Starship;
        if (!promptPreview)
        {
            brush.Color = PalScrim;
            rt.FillRectangle(new Rect(0, 0, cw, ch), brush);
        }

        const float queryH = 42f, rowH = 40f;
        const int maxRows = 12;
        float pw = MathF.Min(560f, cw - 80f);
        float px = (cw - pw) / 2f;
        // Cap the row count so the panel always fits inside the window (the list scrolls with the
        // selection when there are more items than fit).
        int fitRows = Math.Max(1, (int)((ch - TitleBarH - 16f - queryH - 8f) / rowH));
        if (promptPreview) fitRows = Math.Max(1, (int)((ch * 0.62f - queryH - 8f) / rowH));
        int shown = Math.Min(_palItems.Count, Math.Min(maxRows, fitRows));
        float ph = queryH + Math.Max(1, shown) * rowH + 8f;
        float py = MathF.Max(TitleBarH + 16f, ch * (promptPreview ? 0.36f : 0.14f));
        py = MathF.Min(py, MathF.Max(TitleBarH + 8f, ch - ph - 8f));   // short window: pull the panel up
        _palPanel = new Rect(px, py, pw, ph);

        brush.Color = PalBg;
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = _palPanel, RadiusX = 10f, RadiusY = 10f }, brush);
        brush.Color = PalBorder;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = _palPanel, RadiusX = 10f, RadiusY = 10f }, brush, 1f);

        // Query line (placeholder when empty) + blinking caret.
        string placeholder = _palette switch { PaletteKind.Sessions => "Go to session…", PaletteKind.Actions => "Run action…", PaletteKind.Themes => "Select theme…", PaletteKind.Omp => "oh-my-posh theme…", PaletteKind.Starship => "starship preset…", PaletteKind.PromptEngine => "Prompt engine…", PaletteKind.Fonts => "Install Nerd Font…", PaletteKind.Custom => "Run command…", PaletteKind.Windows => "Switch window…", PaletteKind.NewSession => "New session — pick a shell…", _ => "Attention" };
        brush.Color = _palQuery.Length > 0 ? ChromeText : ChromeDim;
        rt.DrawText(_palQuery.Length > 0 ? _palQuery : placeholder, _uiFont, new Rect(px + 16f, py + 9f, pw - 32f, queryH - 10f), brush);
        if (_cursorOn && _palQuery.Length > 0)
        {
            float qx = px + 16f + MeasureText(_palQuery, _uiFont) + 1f;
            brush.Color = ChromeText;
            rt.DrawLine(new System.Numerics.Vector2(qx, py + 11f), new System.Numerics.Vector2(qx, py + queryH - 10f), brush, 1.2f);
        }
        brush.Color = ChromeBorder;
        rt.DrawLine(new System.Numerics.Vector2(px + 8f, py + queryH), new System.Numerics.Vector2(px + pw - 8f, py + queryH), brush, 1f);

        int start = _palSel >= shown ? _palSel - shown + 1 : 0;
        for (int i = 0; i < shown; i++)
        {
            int idx = start + i;
            if (idx >= _palItems.Count) break;
            var it = _palItems[idx];
            float ry = py + queryH + i * rowH;
            if (idx == _palSel) { brush.Color = PalSel; rt.FillRectangle(new Rect(px + 4f, ry, pw - 8f, rowH), brush); }
            float tx = px + 16f;
            if (it.Dot is AgentStatus ds) { brush.Color = StatusDot(ds); rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(px + 16f, ry + rowH / 2f), 4.5f, 4.5f), brush); tx = px + 30f; }
            bool hasSub = it.Secondary.Length > 0;
            brush.Color = it.Run is null ? ChromeDim : (idx == _palSel ? SbActiveText : ChromeText);
            float lw = pw - (tx - px) - (it.Hint.Length > 0 ? 80f : 20f);
            rt.DrawText(it.Label, _uiFont, new Rect(tx, ry + (hasSub ? 3f : 0f), lw, hasSub ? 20f : rowH), brush, DrawTextOptions.Clip);
            if (hasSub) { brush.Color = ChromeDim; rt.DrawText(it.Secondary, _uiSmall, new Rect(tx, ry + 20f, pw - (tx - px) - 20f, 16f), brush, DrawTextOptions.Clip); }
            if (it.Hint.Length > 0) { brush.Color = ChromeDim; float hw = MeasureText(it.Hint, _uiSmall); rt.DrawText(it.Hint, _uiSmall, new Rect(px + pw - 16f - hw, ry + (rowH - 16f) / 2f, hw + 2f, 16f), brush); }
            _palRows.Add((ry, ry + rowH, idx));
        }
    }

    // ---- Custom title bar / status bar (frameless chrome) ----

    private Color4 ChromeBg = new(0.043f, 0.051f, 0.059f, 1f);
    private Color4 ChromeText = new(0.92f, 0.93f, 0.95f, 1f);
    private Color4 ChromeDim = new(0.55f, 0.58f, 0.62f, 1f);
    private Color4 ChromeAccent = new(0.30f, 0.55f, 0.95f, 1f);   // theme accent (selection, active markers, focus)
    private Color4 ChromeBorder = new(0.22f, 0.24f, 0.28f, 1f);   // dividers / separators

    // ---- Chrome palette derivation (all chrome colours track the active theme) ----

    private static float Lum(Color4 c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
    private static Color4 Mix(Color4 a, Color4 b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);
    private static Color4 WithA(Color4 c, float a) => new(c.R, c.G, c.B, a);
    /// <summary>amt &gt; 0 lightens (toward white), amt &lt; 0 darkens (toward black).</summary>
    private static Color4 Shade(Color4 c, float amt)
        => amt >= 0 ? Mix(c, new Color4(1f, 1f, 1f, 1f), amt) : Mix(c, new Color4(0f, 0f, 0f, 1f), -amt);

    /// <summary>Recompute all chrome colours from the active theme + sidebar-tint. Called on load and every theme/config change.</summary>
    private void RecomputeChrome()
    {
        var bg = C4(_theme.DefaultBackground);
        var fg = C4(_theme.DefaultForeground);
        bool dark = Lum(bg) < 0.5f;
        _chromeDark = dark;

        // Panels sit at distinct luminance layers relative to the terminal background.
        ChromeBg = Shade(bg, dark ? -0.42f : -0.06f);   // title bar + footer toolbar
        var sidebar = Shade(bg, dark ? -0.28f : -0.09f);
        float tint = System.Math.Clamp(_config.SidebarTint, -100, 100) / 100f;
        SbBg = Shade(sidebar, tint * (dark ? 0.6f : 0.4f)); // sidebar-tint nudges the shade
        PalBg = Shade(bg, dark ? 0.07f : -0.04f);         // overlays/HUD pop slightly above the terminal

        // Accent: the theme's bright-blue (index 12), falling back to blue (4) then a safe blue, kept vivid.
        var accent = C4(_theme.Palette.Length > 12 ? _theme.Palette[12] : _theme.DefaultForeground);
        if (Lum(accent) < 0.22f && _theme.Palette.Length > 4) accent = C4(_theme.Palette[4]);
        if (Lum(accent) < 0.18f) accent = new Color4(0.30f, 0.55f, 0.95f, 1f);
        ChromeAccent = accent;

        ChromeText = Mix(fg, bg, 0.04f);
        ChromeDim = Mix(fg, bg, dark ? 0.42f : 0.40f);
        SbActiveText = fg;
        SbHeaderText = Mix(fg, bg, 0.20f);
        SbDimText = ChromeDim;
        SbHighlight = Mix(SbBg, fg, dark ? 0.14f : 0.14f);
        ChromeBorder = Mix(bg, fg, dark ? 0.20f : 0.26f);
        PalBorder = Mix(PalBg, fg, dark ? 0.28f : 0.30f);
        PalSel = Mix(PalBg, accent, dark ? 0.45f : 0.32f);
    }

    /// <summary>A cell codepoint as a drawable string (astral -> surrogate pair).</summary>
    private static string RuneStr(int cp) => cp > 0xFFFF ? char.ConvertFromUtf32(cp) : ((char)cp).ToString();

    /// <summary>Vector-draw a box-drawing / block-element codepoint filling the cell exactly, for
    /// seamless borders and crisp blocks at any size. Returns false if cp isn't a handled glyph
    /// (falls back to the font). Covers light single lines (incl. heavy/double as light), block
    /// elements, shades, and eighth blocks — the set that dominates TUI chrome.</summary>
    private bool DrawBoxGlyph(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, int cp, float x, float y, float w, float h, Color4 color)
    {
        float t = MathF.Max(1f, MathF.Round(h / 12f));   // line thickness
        float cx = x + w / 2f, cy = y + h / 2f;
        void H(float x0, float x1) { brush.Color = color; rt.FillRectangle(new Rect(x0, cy - t / 2f, x1 - x0, t), brush); }
        void V(float y0, float y1) { brush.Color = color; rt.FillRectangle(new Rect(cx - t / 2f, y0, t, y1 - y0), brush); }
        void Fill(float x0, float y0, float x1, float y1, float a = 1f) { brush.Color = new Color4(color.R, color.G, color.B, a); rt.FillRectangle(new Rect(x0, y0, x1 - x0, y1 - y0), brush); }

        switch (cp)
        {
            // ---- light single lines ----
            case 0x2500 or 0x2501: H(x, x + w); return true;              // ─ ━
            case 0x2502 or 0x2503: V(y, y + h); return true;              // │ ┃
            case 0x250C or 0x250D or 0x250E or 0x250F: H(cx, x + w); V(cy, y + h); return true;  // ┌
            case 0x2510 or 0x2511 or 0x2512 or 0x2513: H(x, cx + t / 2f); V(cy, y + h); return true;  // ┐
            case 0x2514 or 0x2515 or 0x2516 or 0x2517: H(cx, x + w); V(y, cy + t / 2f); return true;  // └
            case 0x2518 or 0x2519 or 0x251A or 0x251B: H(x, cx + t / 2f); V(y, cy + t / 2f); return true;  // ┘
            case >= 0x251C and <= 0x2523: V(y, y + h); H(cx, x + w); return true;  // ├ family
            case >= 0x2524 and <= 0x252B: V(y, y + h); H(x, cx + t / 2f); return true;  // ┤ family
            case >= 0x252C and <= 0x2533: H(x, x + w); V(cy, y + h); return true;  // ┬ family
            case >= 0x2534 and <= 0x253B: H(x, x + w); V(y, cy + t / 2f); return true;  // ┴ family
            case >= 0x253C and <= 0x254B: H(x, x + w); V(y, y + h); return true;  // ┼ family
            case 0x2550: H(x, x + w); return true;                       // ═ (double as single)
            case 0x2551: V(y, y + h); return true;                       // ║
            // ---- block elements ----
            case 0x2588: Fill(x, y, x + w, y + h); return true;          // █ full
            case 0x2580: Fill(x, y, x + w, cy); return true;             // ▀ upper half
            case 0x2584: Fill(x, cy, x + w, y + h); return true;         // ▄ lower half
            case 0x258C: Fill(x, y, cx, y + h); return true;             // ▌ left half
            case 0x2590: Fill(cx, y, x + w, y + h); return true;         // ▐ right half
            case 0x2591: Fill(x, y, x + w, y + h, 0.25f); return true;   // ░ light shade
            case 0x2592: Fill(x, y, x + w, y + h, 0.50f); return true;   // ▒ medium shade
            case 0x2593: Fill(x, y, x + w, y + h, 0.75f); return true;   // ▓ dark shade
            // lower eighth blocks ▁▂▃▄▅▆▇ (2581..2587): fill bottom n/8
            case >= 0x2581 and <= 0x2587: { float frac = (cp - 0x2580) / 8f; Fill(x, y + h * (1 - frac), x + w, y + h); return true; }
            // left eighth blocks ▉▊▋▌▍▎▏ (2589..258F): fill left (8-n)/8 .. actually 2589=7/8 down to 258F=1/8
            case >= 0x2589 and <= 0x258F: { float frac = (0x2590 - cp) / 8f; Fill(x, y, x + w * frac, y + h); return true; }
            // upper/right eighth-ish and quadrants: fall back to font for the rare rest
            default: return false;
        }
    }

    private int ClientW() { GetClientRect(_hwnd, out RECT rc); return rc.right - rc.left; }
    private int ClientH() { GetClientRect(_hwnd, out RECT rc); return rc.bottom - rc.top; }

    private bool InContent(IntPtr lParam)
    {
        int x = LoWord(lParam), y = HiWord(lParam);
        return x >= (int)_sidebarW && y >= (int)TitleBarH;
    }

    /// <summary>WM_NCCALCSIZE: reclaim the OS caption into the client so we draw our own title bar.</summary>
    private void AdjustClientRect(IntPtr hwnd, IntPtr lParam)
    {
        // When maximized, inset by the frame so content isn't pushed off-screen / under the taskbar.
        if (IsZoomed(hwnd))
        {
            var rc = Marshal.PtrToStructure<RECT>(lParam);
            int fx = GetSystemMetrics(SM_CXFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
            int fy = GetSystemMetrics(SM_CYFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
            rc.left += fx; rc.right -= fx; rc.top += fy; rc.bottom -= fy;
            Marshal.StructureToPtr(rc, lParam, false);
        }
        // Otherwise leave the proposed rect: client fills the whole window (borderless); resize via NCHITTEST.
    }

    /// <summary>WM_NCHITTEST: resize borders, caption buttons (system-handled), draggable caption, else client.</summary>
    private int HitTest(IntPtr hwnd, int sx, int sy)
    {
        var pt = new POINT { x = sx, y = sy };
        ScreenToClient(hwnd, ref pt);
        int cw = ClientW(), ch = ClientH();
        const int B = 8;
        bool hidden = ToolbarHidden;
        if (!IsZoomed(hwnd))
        {
            bool top = pt.y < B, bot = pt.y >= ch - B, left = pt.x < B, right = pt.x >= cw - B;
            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bot && left) return HTBOTTOMLEFT;
            if (bot && right) return HTBOTTOMRIGHT;
            if (top) return hidden ? HTCAPTION : HTTOP;   // hidden: top edge drags (no chrome to grab)
            if (bot) return HTBOTTOM;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
        }
        if (hidden)   // maximized/full-bleed: a thin top strip still drags + double-click-zooms
            return pt.y < 6 ? HTCAPTION : HTCLIENT;
        if (pt.y < (int)TitleBarH)
        {
            int cap = CaptionButtonAt(pt.x, cw);
            if (cap != _hoverCaption) { _hoverCaption = cap; RequestRedraw(); }
            if (cap == 3) return HTCLOSE;
            if (cap == 2) return HTMAXBUTTON;
            if (cap == 1) return HTMINBUTTON;
            foreach (var b in _titleButtons) if (pt.x >= b.x0 && pt.x < b.x1) return HTCLIENT; // our action buttons
            return HTCAPTION; // draggable
        }
        if (_hoverCaption != 0) { _hoverCaption = 0; RequestRedraw(); }
        return HTCLIENT;
    }

    private int CaptionButtonAt(int x, int cw)
    {
        if (x >= cw - (int)CaptionBtnW) return 3;              // close
        if (x >= cw - 2 * (int)CaptionBtnW) return 2;         // max/restore
        if (x >= cw - 3 * (int)CaptionBtnW) return 1;         // min
        return 0;
    }

    private string? ChromeHit(List<(float x0, float x1, string action)> buttons, int mx)
    {
        foreach (var b in buttons) if (mx >= b.x0 && mx < b.x1) return b.action;
        return null;
    }

    /// <summary>Update the hovered chrome button from a client-area move; arms the fade timer on change.</summary>
    private void UpdateChromeHover(int mx, int my)
    {
        string? hit = null;
        if (my < (int)TitleBarH) hit = ChromeHit(_titleButtons, mx);
        else if (mx < (int)_sidebarW && my >= ClientH() - (int)FooterH) hit = ChromeHit(_footerButtons, mx);
        if (hit == _hotBtn) return;
        _hotBtn = hit;
        if (hit is not null)
        {
            _hotPaint = hit; _hotAlpha = 1f;   // light instantly on hover-in
            if (Uia.ClientsListening) Uia.Announce(ChromeButtonLabel(hit) + " button");   // speak the hovered button
            _tipText = null;
            SetTimer(_hwnd, (IntPtr)TipTimer, 550, IntPtr.Zero);    // tooltip after a short hover dwell
        }
        else
        {
            KillTimer(_hwnd, (IntPtr)TipTimer);
            _tipText = null;
            SetTimer(_hwnd, (IntPtr)HoverTimer, 15, IntPtr.Zero);   // fade out when leaving
        }
        RequestRedraw();
    }

    // ---- Hover tooltips for chrome buttons ----
    private const int TipTimer = 12;      // WM_TIMER id: show the tip after a hover dwell
    private string? _tipText;             // visible tooltip text (null = none)

    /// <summary>TipTimer fired: show the tooltip for the still-hovered button.</summary>
    private void TipTick()
    {
        KillTimer(_hwnd, (IntPtr)TipTimer);
        if (_hotBtn is null) return;
        _tipText = ChromeButtonLabel(_hotBtn);
        RequestRedraw();
    }

    /// <summary>Draw the hover tooltip near its button (below title-bar buttons, above footer ones).</summary>
    private void DrawButtonTip(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (_tipText is null || _hotBtn is null) return;
        float bx0 = -1, bx1 = -1; bool footer = false;
        foreach (var b in _titleButtons) if (b.action == _hotBtn) { bx0 = b.x0; bx1 = b.x1; }
        if (bx0 < 0) foreach (var b in _footerButtons) if (b.action == _hotBtn) { bx0 = b.x0; bx1 = b.x1; footer = true; }
        if (bx0 < 0) return;
        float tw = MeasureText(_tipText, _uiSmall) + 16f, th = 22f;
        float tx = Math.Clamp((bx0 + bx1) / 2f - tw / 2f, 4f, ClientW() - tw - 4f);
        float ty = footer ? ClientH() - FooterH - th - 6f : TitleBarH + 6f;
        brush.Color = Mix(PalBg, ChromeText, 0.10f);
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(tx, ty, tw, th), RadiusX = 5f, RadiusY = 5f }, brush);
        brush.Color = PalBorder;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = new Rect(tx, ty, tw, th), RadiusX = 5f, RadiusY = 5f }, brush, 1f);
        brush.Color = ChromeText;
        rt.DrawText(_tipText, _uiSmallCenter, new Rect(tx, ty, tw, th), brush);
    }

    /// <summary>Friendly spoken name for a chrome button action id (screen-reader hover announcements).</summary>
    private static string ChromeButtonLabel(string a) => a switch
    {
        "toggle" => "Toggle sidebar",
        "add-session" => "New session",
        "new-workspace" => "New workspace",
        "attention" => "Next attention",
        "scratch" => "Scratch terminal",
        "split" => "Split pane",
        "quick terminal" => "Quick terminal",
        "flag" => "Flagged view",
        "unfocus" => "Unfocus workspace",
        "settings" => "Settings",
        _ => a,
    };

    /// <summary>Ease the hover-fill alpha toward the target; stop the timer once settled.</summary>
    private void HoverTick()
    {
        float target = _hotBtn is not null ? 1f : 0f;
        const float step = 0.20f;
        if (_hotAlpha < target) _hotAlpha = MathF.Min(target, _hotAlpha + step);
        else if (_hotAlpha > target) _hotAlpha = MathF.Max(target, _hotAlpha - step);
        if (_hotAlpha == target) { KillTimer(_hwnd, (IntPtr)HoverTimer); if (target == 0f) _hotPaint = null; }
        RequestRedraw();
    }

    /// <summary>Paint a chrome button's hover/press background + record its hit-box; returns the tinted icon colour.</summary>
    private Color4 ChromeBtnBg(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush,
        float x, float y, float w, float h, string id, List<(float x0, float x1, string action)> list, Color4 baseColor)
    {
        list.Add((x, x + w, id));
        float a = id == _hotPaint ? _hotAlpha : 0f;
        bool pressed = _pressBtn == id && _hotBtn == id;
        const float padY = 5f;
        var r = new Rect(x + 3f, y + padY, w - 6f, h - 2f * padY); // XYWH
        if (pressed)
        {
            brush.Color = WithA(ChromeText, _chromeDark ? 0.30f : 0.22f);
            rt.FillRoundedRectangle(new RoundedRectangle { Rect = r, RadiusX = 6f, RadiusY = 6f }, brush);
        }
        else if (a > 0.01f)
        {
            brush.Color = WithA(ChromeText, (_chromeDark ? 0.20f : 0.14f) * a);
            rt.FillRoundedRectangle(new RoundedRectangle { Rect = r, RadiusX = 6f, RadiusY = 6f }, brush);
        }
        float bright = pressed ? 1f : a;
        return bright > 0f ? Mix(baseColor, ChromeText, bright * 0.85f) : baseColor;
    }

    private void ChromeAction(string a)
    {
        switch (a)
        {
            case "toggle": ToggleSidebar(); break;
            case "add-session": TogglePalette(PaletteKind.NewSession); break;   // themed picker: profiles + Open Directory…
            case "new-workspace": CreateWorkspace(Guid.NewGuid().ToString(), null); break;
            case "attention": GoToNextAttention(1); break;
            case "scratch": if (_active is not null) ScratchOp(_active, "toggle"); break;
            case "split": SplitOp("toggle"); break;   // title-bar split button toggles the split on/off
            case "quick terminal": QuickOp("toggle"); break;
            case "flag": ToggleFlaggedView(); break;   // footer flag button toggles the flagged working-set view
            case "unfocus": _focusedWorkspaceId = null; RequestRedraw(); SaveState(); break;
            case "settings": OpenSettingsWindow(); break;
            default: ShowToast(a + " not implemented yet"); break;
        }
    }

    private void ToggleSidebar()
    {
        _sidebarW = _sidebarW > 0 ? 0 : SidebarWFull;
        if (_active is not null) RegridSession(_active);
        if (_cover is not null) RegridCover();
        RequestRedraw();
        SaveState();
    }

    private void ShowToast(string text)
    {
        _toastText = text;
        _toastTarget = null;
        SetTimer(_hwnd, (IntPtr)2, 1900, IntPtr.Zero);
        RequestRedraw();
    }
}
