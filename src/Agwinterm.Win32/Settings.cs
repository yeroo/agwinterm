using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>
/// The Settings panel: a themed surface drawn in Direct2D inside our own window (like the command
/// palette), with custom widgets — no native Win32 chrome. Five tabs (General / Appearance /
/// Notifications / Agent Status / Key Mapping) matching agterm. Every widget writes its key through
/// <see cref="ConfigSetInternal"/> (the same path as the <c>config.set</c> verb) so changes persist to
/// agwinterm.conf and apply live. Opened from the title-bar gear, the palette, or <c>settings.open</c>.
/// </summary>
internal partial class Program
{
    private enum SW { Section, Toggle, Slider, Dropdown, Color, Sound, Path, Button, Info, Diag, Profile }

    private sealed class SetRow
    {
        public SW Kind;
        public int Tab, Min, Max;
        public string Key = "", Label = "";
        public string[] Opts = Array.Empty<string>();
        public string[] Vals = Array.Empty<string>();
        public Action? OnClick;
        // computed each frame (absolute px); Hit is the clickable widget area, W a secondary button (Path "Choose…")
        public float Hx0, Hy0, Hx1, Hy1;
        public float Bx0, By0, Bx1, By1;      // secondary button (Path)
        public bool Vis;
    }

    private bool _setOpen;
    private int _setTab;
    private float _setScroll;
    private readonly List<SetRow> _setRows = new();
    private SetRow? _setFocus;   // keyboard-focused row (Tab / arrows), drawn with a focus rectangle
    private int _setNav = -1;    // >=0: keyboard focus is on tab-header[_setNav] instead of a row
    private bool _setFocusKb;    // draw the focus rectangle only when focus was last moved by keyboard
    private ID2D1StrokeStyle? _focusStroke;   // cached dotted stroke for the classic keyboard-focus rect

    private static bool Interactive(SW k) =>
        k is SW.Toggle or SW.Slider or SW.Dropdown or SW.Color or SW.Sound or SW.Path or SW.Button or SW.Profile;
    private List<SetRow> FocusableRows() => _setRows.FindAll(r => r.Tab == _setTab && Interactive(r.Kind));
    private readonly float[] _navHit = new float[6 * 2];   // per-tab nav row y0,y1
    private Rect _setCard;
    private float _setCloseX0, _setCloseY0, _setCloseX1, _setCloseY1;
    private float _setPaneTop, _setPaneBottom, _setContentH;
    private SetRow? _setDragRow;                            // slider being dragged
    private static IntPtr _custColors;                      // 16-slot custom-colors buffer for ChooseColor

    // Dropdown popup state.
    private SetRow? _ddRow;
    private readonly List<int> _ddFiltered = new();
    private string _ddQuery = "";
    private int _ddSel;
    private float _ddScroll;
    private Rect _ddPanel;
    private readonly List<(float y0, float y1, int idx)> _ddRows = new();

    private static readonly string[] SetTabNames = { "General", "Appearance", "Notifications", "Agent Status", "Key Mapping", "Profiles" };

    /// <summary>Open the themed Settings panel (entry point for the gear / palette / settings.open verb).</summary>
    private void OpenSettingsWindow()
    {
        if (_palette != PaletteKind.None) ClosePalette();
        BuildSettingsRows();
        _setOpen = true; _setTab = 0; _setScroll = 0; _ddRow = null; _setDragRow = null;
        _setFocus = FocusableRows().FirstOrDefault();
        _setNav = -1; _setFocusKb = false;   // no focus rectangle until the keyboard is used
        Uia.Announce("Settings, General tab. Tab to move, Space to change, Escape to close.");
        RequestRedraw();
    }

    private void CloseSettings()
    {
        _setOpen = false; _ddRow = null; _setDragRow = null;
        Uia.Announce("Settings closed");
        RequestRedraw();
    }

    /// <summary>Kept for the config-write path; the D2D panel re-reads config on draw, so just repaint.</summary>
    private void RefreshSettingsControls() { if (_setOpen) RequestRedraw(); }

    private void BuildSettingsRows()
    {
        _setRows.Clear();
        void Sec(int t, string s) => _setRows.Add(new SetRow { Kind = SW.Section, Tab = t, Label = s });
        void Tog(int t, string k, string l) => _setRows.Add(new SetRow { Kind = SW.Toggle, Tab = t, Key = k, Label = l });
        void Sld(int t, string k, string l, int lo, int hi) => _setRows.Add(new SetRow { Kind = SW.Slider, Tab = t, Key = k, Label = l, Min = lo, Max = hi });
        void Drop(int t, string k, string l, string[] o, string[]? v = null) => _setRows.Add(new SetRow { Kind = SW.Dropdown, Tab = t, Key = k, Label = l, Opts = o, Vals = v ?? o });
        void Col(int t, string k, string l) => _setRows.Add(new SetRow { Kind = SW.Color, Tab = t, Key = k, Label = l });
        void Btn(int t, string l, Action a) => _setRows.Add(new SetRow { Kind = SW.Button, Tab = t, Label = l, OnClick = a });

        // General
        Sec(0, "Mouse");
        Sld(0, "scroll-speed", "Scroll speed", 1, 10);
        Tog(0, "right-click-paste", "Right-click pastes clipboard");
        Tog(0, "copy-on-select", "Copy selection automatically");
        Tog(0, "copy-on-ctrl-c", "Ctrl+C copies the selection (else interrupts)");
        Sec(0, "Sessions");
        Drop(0, "new-session-dir-mode", "New sessions open in",
            new[] { "Home directory", "Current session's dir", "Custom directory" }, new[] { "home", "current", "custom" });
        _setRows.Add(new SetRow { Kind = SW.Path, Tab = 0, Key = "new-session-dir", Label = "Custom directory" });
        Tog(0, "restore-commands", "Restore running commands");
        Tog(0, "confirm-close-session", "Confirm before closing");
        Sec(0, "Shell");
        Tog(0, "shell-integration", "Shell integration (live cwd)");
        Tog(0, "omp-integration", "oh-my-posh integration");
        Sec(0, "Default Terminal");
        _setRows.Add(new SetRow
        {
            Kind = SW.Info, Tab = 0,
            Label = DefTerm.IsRegisteredDefault()
                ? "Console apps currently open in agwinterm."
                : "Console apps currently open in the Windows default (conhost / Windows Terminal).",
        });
        if (DefTerm.IsRegisteredDefault())
            Btn(0, "Restore Windows default", () => { ShowToast(DefTerm.RestoreWindowsDefault()); BuildSettingsRows(); RequestRedraw(); });
        else
            Btn(0, "Make agwinterm the default terminal", () => { ShowToast(DefTerm.RegisterAsDefault()); BuildSettingsRows(); RequestRedraw(); });

        // Appearance
        Sec(1, "Terminal");
        string[] fonts = { "Default", "MesloLGSDZ Nerd Font", "Cascadia Mono", "Cascadia Code", "Consolas", "JetBrains Mono", "Fira Code", "Hack", "Source Code Pro", "Courier New", "Lucida Console" };
        Drop(1, "font-family", "Font", fonts);
        Sld(1, "font-size", "Font size", 8, 32);
        Drop(1, "theme", "Theme", _allThemes.Select(t => t.Name).ToArray());
        Tog(1, "theme-follow-system", "Follow Windows light/dark");
        Drop(1, "theme-dark", "Dark theme", _allThemes.Select(t => t.Name).ToArray());
        Drop(1, "theme-light", "Light theme", _allThemes.Select(t => t.Name).ToArray());
        Drop(1, "cursor-style", "Cursor style", new[] { "bar", "block", "underline" });
        Tog(1, "cursor-blink", "Blink cursor");
        Sec(1, "Window");
        Tog(1, "compact-toolbar", "Compact toolbar");
        Sld(1, "window-opacity", "Window opacity", 30, 100);
        Sld(1, "sidebar-tint", "Sidebar tint", -100, 100);
        Sld(1, "sidebar-font-size", "Sidebar font size", 9, 20);
        Sec(1, "Panes");
        Sld(1, "inactive-pane-dim", "Inactive pane mute", 0, 100);

        // Notifications
        Sec(2, "Notifications");
        Tog(2, "desktop-notifications", "Desktop notifications (OS tray)");
        Tog(2, "notification-badges", "Unread badges on sidebar rows");
        Drop(2, "notification-flash", "Flash taskbar when in background", new[] { "None", "Once", "Until focused" }, new[] { "none", "once", "until-focused" });
        Tog(2, "attention-button", "Title-bar attention indicator");

        // Agent Status
        Sec(3, "Colors");
        Col(3, "status-color-active", "Active");
        Col(3, "status-color-blocked", "Blocked");
        Col(3, "status-color-completed", "Completed");
        Sec(3, "Sound");
        _setRows.Add(new SetRow
        {
            Kind = SW.Sound, Tab = 3, Key = "blocked-sound", Label = "Blocked sound",
            Opts = new[] { "None", "beep", "asterisk", "exclamation", "hand", "question", "Custom .wav…" },
            Vals = new[] { "None", "beep", "asterisk", "exclamation", "hand", "question", "__custom__" },
        });
        Btn(3, "Reset to defaults", ResetAgentStatusSettings);

        // Key Mapping
        Sec(4, "Config Directory");
        _setRows.Add(new SetRow { Kind = SW.Info, Tab = 4, Label = System.IO.Path.GetDirectoryName(KeymapPath) ?? "" });
        Btn(4, "Open Folder", OpenConfigFolder);
        Btn(4, "Reload keymap", () => { ReloadKeymap(); RequestRedraw(); });
        Sec(4, "Diagnostics");
        _setRows.Add(new SetRow { Kind = SW.Diag, Tab = 4 });

        // Profiles (shell chooser for new sessions)
        Sec(5, "Shell Profiles");
        foreach (var p in _profileCfg.Profiles)
        {
            string cmd = p.Command + (p.Args is { Length: > 0 } a ? " " + string.Join(" ", a) : "");
            _setRows.Add(new SetRow { Kind = SW.Profile, Tab = 5, Key = p.Name, Label = p.Name, Opts = new[] { cmd } });
        }
        Sec(5, "Manage");
        Btn(5, "Edit profiles.json…", EditProfilesJson);
        Btn(5, "Reload", () => { _profileCfg = Agwinterm.Pty.ShellProfiles.Load(AppDir); BuildSettingsRows(); RequestRedraw(); });
        _setRows.Add(new SetRow { Kind = SW.Info, Tab = 5, Label = "Click a profile to set the default; edit profiles.json then Reload to add your own." });
    }

    /// <summary>Open profiles.json in the default editor (seeding it first if needed).</summary>
    private void EditProfilesJson()
    {
        string p = Agwinterm.Pty.ShellProfiles.PathFor(AppDir);
        if (!System.IO.File.Exists(p)) Agwinterm.Pty.ShellProfiles.Load(AppDir);   // seed on first open
        ShellExecuteW(IntPtr.Zero, "open", p, null, null, 5);
    }

    /// <summary>Make a shell profile the default (persists to profiles.json) and refresh the tab.</summary>
    private void SetProfileDefault(string name)
    {
        _profileCfg = Agwinterm.Pty.ShellProfiles.SetDefault(AppDir, name);
        BuildSettingsRows();
        RequestRedraw();
    }

    private static void OpenConfigFolder()
    {
        var dir = System.IO.Path.GetDirectoryName(KeymapPath);
        if (dir is null) return;
        System.IO.Directory.CreateDirectory(dir);
        ShellExecuteW(IntPtr.Zero, "open", dir, null, null, 5);
    }

    // ---- layout constants ----
    private const float SetHeader = 46f, SetNavW = 160f, SetRowH = 38f, SetSecH = 30f, SetPad = 22f;

    private void DrawSettingsPanel(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (!_setOpen) return;
        int cw = ClientW(), ch = ClientH();
        brush.Color = PalScrim;
        rt.FillRectangle(new Rect(0, 0, cw, ch), brush);

        float cardW = MathF.Min(680f, cw - 60f);
        float cardH = MathF.Min(700f, ch - 80f);   // tall enough for General incl. the Default Terminal section
        float cardX = (cw - cardW) / 2f;
        float cardY = MathF.Max(TitleBarH + 16f, (ch - cardH) / 2f);
        _setCard = new Rect(cardX, cardY, cardW, cardH);

        brush.Color = PalBg;
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = _setCard, RadiusX = 12f, RadiusY = 12f }, brush);
        brush.Color = PalBorder;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = _setCard, RadiusX = 12f, RadiusY = 12f }, brush, 1f);

        // Header + close button
        brush.Color = ChromeText;
        rt.DrawText("Settings", _uiFont, new Rect(cardX + SetPad, cardY, 200f, SetHeader), brush);
        float cbSz = 26f, cbX = cardX + cardW - cbSz - 12f, cbY = cardY + (SetHeader - cbSz) / 2f;
        _setCloseX0 = cbX; _setCloseY0 = cbY; _setCloseX1 = cbX + cbSz; _setCloseY1 = cbY + cbSz;
        brush.Color = ChromeDim;
        rt.DrawText(GlyphClose, _iconSmall, new Rect(cbX, cbY, cbSz, cbSz), brush);
        brush.Color = ChromeBorder;
        rt.DrawLine(new System.Numerics.Vector2(cardX + 1f, cardY + SetHeader), new System.Numerics.Vector2(cardX + cardW - 1f, cardY + SetHeader), brush, 1f);
        rt.DrawLine(new System.Numerics.Vector2(cardX + SetNavW, cardY + SetHeader), new System.Numerics.Vector2(cardX + SetNavW, cardY + cardH - 1f), brush, 1f);

        // Left nav
        float navY = cardY + SetHeader + 8f;
        for (int i = 0; i < SetTabNames.Length; i++)
        {
            float ry = navY + i * 34f;
            _navHit[i * 2] = ry; _navHit[i * 2 + 1] = ry + 32f;
            if (i == _setTab)
            {
                brush.Color = PalSel;
                rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(cardX + 8f, ry, SetNavW - 16f, 32f), RadiusX = 6f, RadiusY = 6f }, brush);
            }
            brush.Color = i == _setTab ? SbActiveText : ChromeDim;
            rt.DrawText(SetTabNames[i], _uiFont, new Rect(cardX + 20f, ry, SetNavW - 28f, 32f), brush);
            if (_setFocusKb && _setNav == i)   // keyboard focus on this tab header
            {
                brush.Color = ChromeText;
                rt.DrawRectangle(new Rect(cardX + 8f, ry, SetNavW - 16f, 32f), brush, 1.5f, FocusStroke(rt));
            }
        }

        // Right pane (clipped + scrolled)
        float paneX = cardX + SetNavW, paneW = cardW - SetNavW;
        _setPaneTop = cardY + SetHeader + 6f;
        _setPaneBottom = cardY + cardH - 6f;
        rt.PushAxisAlignedClip(new Rect(paneX, _setPaneTop, paneW, _setPaneBottom - _setPaneTop), AntialiasMode.Aliased);
        float y = _setPaneTop + 6f - _setScroll;
        float lblX = paneX + SetPad;
        float rightX = paneX + paneW - SetPad;
        foreach (var r in _setRows)
        {
            r.Vis = false;
            if (r.Tab != _setTab) continue;
            float rowH = r.Kind == SW.Section ? SetSecH : r.Kind == SW.Diag ? 150f : r.Kind == SW.Profile ? 46f : SetRowH;
            bool onscreen = y + rowH > _setPaneTop && y < _setPaneBottom;
            if (onscreen)
            {
                DrawRow(rt, brush, r, lblX, rightX, y, rowH, paneW);
                if (_setFocusKb && _setNav < 0 && ReferenceEquals(r, _setFocus))   // keyboard-only focus rect
                {
                    brush.Color = ChromeText;
                    rt.DrawRectangle(new Rect(paneX + 4f, y - 2f, paneW - 8f, rowH + 4f), brush, 1.5f, FocusStroke(rt));
                }
            }
            y += rowH + (r.Kind == SW.Section ? 2f : 4f);
        }
        _setContentH = (y + _setScroll) - (_setPaneTop + 6f);
        rt.PopAxisAlignedClip();

        if (_ddRow is not null) DrawDropdown(rt, brush);
    }

    /// <summary>Cached dotted stroke for the classic-Windows keyboard-focus rectangle (created lazily
    /// from the render target's factory).</summary>
    private ID2D1StrokeStyle FocusStroke(ID2D1HwndRenderTarget rt)
        => _focusStroke ??= rt.Factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            DashStyle = DashStyle.Dot,
            DashCap = CapStyle.Round,   // round caps so the zero-length "dots" actually render (flat caps → invisible)
        });

    private void DrawRow(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, SetRow r, float lblX, float rightX, float y, float rowH, float paneW)
    {
        if (r.Kind == SW.Section)
        {
            brush.Color = ChromeAccent;
            rt.DrawText(r.Label.ToUpperInvariant(), _uiSmall, new Rect(lblX, y + 8f, paneW - 2 * SetPad, 18f), brush);
            return;
        }
        r.Vis = true;
        if (r.Kind == SW.Profile)
        {
            bool isDef = string.Equals(r.Key, _profileCfg.Default, StringComparison.OrdinalIgnoreCase);
            float rr = 7f, rcx = rightX - rr, rcy = y + rowH / 2f;
            // name + command (dimmed), leaving room for the right-side radio/"Default"
            brush.Color = ChromeText;
            rt.DrawText(r.Label, _uiFont, new Rect(lblX, y + 3f, paneW - 2 * SetPad - 96f, 20f), brush);
            brush.Color = ChromeDim;
            rt.DrawText(r.Opts.Length > 0 ? r.Opts[0] : "", _uiSmall, new Rect(lblX, y + 24f, paneW - 2 * SetPad - 96f, 16f), brush);
            // radio (filled accent when default) + "Default" label
            if (isDef)
            {
                brush.Color = ChromeDim; float dw = MeasureText("Default", _uiSmall);
                rt.DrawText("Default", _uiSmall, new Rect(rcx - rr - 8f - dw, y, dw + 2f, rowH), brush);
            }
            brush.Color = isDef ? ChromeAccent : PalBorder;
            rt.DrawEllipse(new Ellipse(new System.Numerics.Vector2(rcx, rcy), rr, rr), brush, 1.5f);
            if (isDef) { brush.Color = ChromeAccent; rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(rcx, rcy), rr - 3f, rr - 3f), brush); }
            r.Hx0 = lblX - 8f; r.Hy0 = y; r.Hx1 = rightX + rr + 2f; r.Hy1 = y + rowH;
            return;
        }
        if (r.Kind is not (SW.Info or SW.Diag))   // Info/Diag draw their own text below (avoid a double-draw)
        {
            brush.Color = ChromeText;
            rt.DrawText(r.Label, _uiFont, new Rect(lblX, y, paneW - 2 * SetPad - 180f, rowH), brush);
        }

        switch (r.Kind)
        {
            case SW.Toggle:
            {
                bool on = IsOn(ConfigValue(r.Key));
                float tw = 40f, th = 22f, tx = rightX - tw, ty = y + (rowH - th) / 2f;
                brush.Color = on ? ChromeAccent : Mix(PalBg, ChromeText, 0.25f);
                rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(tx, ty, tw, th), RadiusX = th / 2f, RadiusY = th / 2f }, brush);
                brush.Color = new Color4(1f, 1f, 1f, 1f);
                float kr = th / 2f - 3f, kcx = on ? tx + tw - kr - 3f : tx + kr + 3f;
                rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(kcx, ty + th / 2f), kr, kr), brush);
                r.Hx0 = tx; r.Hy0 = ty; r.Hx1 = tx + tw; r.Hy1 = ty + th;
                break;
            }
            case SW.Slider:
            {
                float sw = 180f, sx = rightX - sw, scy = y + rowH / 2f;
                int.TryParse(ConfigValue(r.Key), out int v); v = Math.Clamp(v, r.Min, r.Max);
                float trackX0 = sx, trackX1 = sx + sw - 44f;
                float t = (float)(v - r.Min) / Math.Max(1, r.Max - r.Min);
                float thumbX = trackX0 + (trackX1 - trackX0) * t;
                brush.Color = Mix(PalBg, ChromeText, 0.22f);
                rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(trackX0, scy - 2f, trackX1 - trackX0, 4f), RadiusX = 2f, RadiusY = 2f }, brush);
                brush.Color = ChromeAccent;
                rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(trackX0, scy - 2f, MathF.Max(0f, thumbX - trackX0), 4f), RadiusX = 2f, RadiusY = 2f }, brush);
                rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(thumbX, scy), 7f, 7f), brush);
                brush.Color = ChromeDim;
                rt.DrawText(SliderLabel(r.Key, v), _uiSmall, new Rect(trackX1 + 8f, y, 40f, rowH), brush);
                r.Hx0 = trackX0 - 6f; r.Hy0 = y; r.Hx1 = trackX1 + 6f; r.Hy1 = y + rowH;
                break;
            }
            case SW.Dropdown:
            case SW.Sound:
            {
                float dw = 200f, dh = 26f, dx = rightX - dw, dy = y + (rowH - dh) / 2f;
                brush.Color = Mix(PalBg, ChromeText, 0.10f);
                rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(dx, dy, dw, dh), RadiusX = 6f, RadiusY = 6f }, brush);
                brush.Color = PalBorder;
                rt.DrawRoundedRectangle(new RoundedRectangle { Rect = new Rect(dx, dy, dw, dh), RadiusX = 6f, RadiusY = 6f }, brush, 1f);
                brush.Color = ChromeText;
                rt.DrawText(CurrentDropdownText(r), _uiSmall, new Rect(dx + 8f, dy, dw - 24f, dh), brush);
                brush.Color = ChromeDim;
                rt.DrawText(((char)0xE70D).ToString(), _iconSmall, new Rect(dx + dw - 20f, dy, 18f, dh), brush);  // ChevronDown
                r.Hx0 = dx; r.Hy0 = dy; r.Hx1 = dx + dw; r.Hy1 = dy + dh;
                break;
            }
            case SW.Color:
            {
                float w = 54f, h = 22f, x = rightX - w, cy = y + (rowH - h) / 2f;
                brush.Color = SwatchColor(ConfigValue(r.Key));
                rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(x, cy, w, h), RadiusX = 5f, RadiusY = 5f }, brush);
                brush.Color = PalBorder;
                rt.DrawRoundedRectangle(new RoundedRectangle { Rect = new Rect(x, cy, w, h), RadiusX = 5f, RadiusY = 5f }, brush, 1f);
                r.Hx0 = x; r.Hy0 = cy; r.Hx1 = x + w; r.Hy1 = cy + h;
                break;
            }
            case SW.Path:
            {
                float bw = 84f, bh = 26f, bx = rightX - bw, by = y + (rowH - bh) / 2f;
                float fx = lblX, fw = bx - 10f - fx;
                brush.Color = Mix(PalBg, ChromeText, 0.06f);
                rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(fx, by, fw, bh), RadiusX = 5f, RadiusY = 5f }, brush);
                brush.Color = ChromeDim;
                string p = ConfigValue(r.Key); if (p.Length == 0) p = "(not set)";
                rt.DrawText(p, _uiSmall, new Rect(fx + 8f, by, fw - 12f, bh), brush);
                DrawPanelButton(rt, brush, bx, by, bw, bh, "Choose…");
                r.Bx0 = bx; r.By0 = by; r.Bx1 = bx + bw; r.By1 = by + bh;
                r.Hx0 = r.Hy0 = r.Hx1 = r.Hy1 = 0; // label handled by button
                break;
            }
            case SW.Button:
            {
                float bw = MeasureText(r.Label, _uiSmall) + 32f, bh = 28f, bx = lblX, by = y + (rowH - bh) / 2f;
                DrawPanelButton(rt, brush, bx, by, bw, bh, r.Label);
                // buttons have no left label
                r.Hx0 = bx; r.Hy0 = by; r.Hx1 = bx + bw; r.Hy1 = by + bh;
                break;
            }
            case SW.Info:
            {
                brush.Color = ChromeDim;
                rt.DrawText(r.Label, _uiSmall, new Rect(lblX, y, paneW - 2 * SetPad, rowH), brush);
                r.Vis = false;
                break;
            }
            case SW.Diag:
            {
                var lines = _keymapDiag.Length == 0 ? new[] { "No issues." } : _keymapDiag;
                float ly = y;
                brush.Color = _keymapDiag.Length == 0 ? ChromeDim : new Color4(240 / 255f, 160 / 255f, 40 / 255f, 1f);
                foreach (var line in lines) { rt.DrawText(line, _uiSmall, new Rect(lblX, ly, paneW - 2 * SetPad, 18f), brush); ly += 18f; }
                r.Vis = false;
                break;
            }
        }
    }

    private void DrawPanelButton(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float x, float y, float w, float h, string label)
    {
        brush.Color = Mix(PalBg, ChromeText, 0.14f);
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(x, y, w, h), RadiusX = 6f, RadiusY = 6f }, brush);
        brush.Color = PalBorder;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = new Rect(x, y, w, h), RadiusX = 6f, RadiusY = 6f }, brush, 1f);
        brush.Color = ChromeText;
        // Centered format: the leading-aligned _uiSmall put the label flush against the button's left
        // edge (the width budget's padding all landed on the right).
        rt.DrawText(label, _uiSmallCenter, new Rect(x, y, w, h), brush);
    }

    private string CurrentDropdownText(SetRow r)
    {
        string v = ConfigValue(r.Key);
        if (r.Kind == SW.Sound) v = SoundDisplayName(v);
        int idx = Array.FindIndex(r.Vals, o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) return r.Opts[idx];
        return v.Length == 0 ? (r.Opts.Length > 0 ? r.Opts[0] : "") : v;   // custom value (theme/font/wav path)
    }

    private static string SoundDisplayName(string v) =>
        string.IsNullOrWhiteSpace(v) || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("none", StringComparison.OrdinalIgnoreCase) ? "None" : v;

    private static bool IsOn(string v) => v is "true" or "yes" or "on" or "1";

    private static string SliderLabel(string key, int v) => key switch
    {
        "scroll-speed" => v + "x",
        "window-opacity" => v + "%",
        _ => v.ToString(),
    };

    private Color4 SwatchColor(string hex) => HexColor4(hex, ChromeDim);

    // ---- dropdown popup ----

    private void OpenDropdown(SetRow r)
    {
        _ddRow = r; _ddQuery = ""; _ddSel = 0; _ddScroll = 0;
        FilterDropdown();
        // select the current value
        string cur = r.Kind == SW.Sound ? SoundDisplayName(ConfigValue(r.Key)) : ConfigValue(r.Key);
        int at = _ddFiltered.FindIndex(i => string.Equals(r.Vals[i], cur, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) _ddSel = at;
        RequestRedraw();
    }

    private void FilterDropdown()
    {
        _ddFiltered.Clear();
        if (_ddRow is null) return;
        string q = _ddQuery.ToLowerInvariant();
        for (int i = 0; i < _ddRow.Opts.Length; i++)
            if (q.Length == 0 || _ddRow.Opts[i].ToLowerInvariant().Contains(q)) _ddFiltered.Add(i);
        if (_ddSel >= _ddFiltered.Count) _ddSel = Math.Max(0, _ddFiltered.Count - 1);
    }

    private void DrawDropdown(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        var r = _ddRow!;
        _ddRows.Clear();
        const float rowH = 30f, qh = 30f;
        int maxRows = 12;
        float w = MathF.Max(220f, r.Hx1 - r.Hx0);
        float x = MathF.Min(r.Hx0, ClientW() - w - 8f);
        bool filterable = r.Opts.Length > 12;
        int shown = Math.Min(_ddFiltered.Count, maxRows);
        float h = (filterable ? qh : 0f) + Math.Max(1, shown) * rowH + 8f;
        float y = r.Hy1 + 2f;
        if (y + h > ClientH() - 8f) y = MathF.Max(_setPaneTop, r.Hy0 - h - 2f);
        _ddPanel = new Rect(x, y, w, h);

        brush.Color = Shade(PalBg, 0.06f);
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = _ddPanel, RadiusX = 8f, RadiusY = 8f }, brush);
        brush.Color = PalBorder;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = _ddPanel, RadiusX = 8f, RadiusY = 8f }, brush, 1f);

        float top = y + 4f;
        if (filterable)
        {
            brush.Color = _ddQuery.Length > 0 ? ChromeText : ChromeDim;
            rt.DrawText(_ddQuery.Length > 0 ? _ddQuery : "Type to filter…", _uiSmall, new Rect(x + 10f, top, w - 20f, qh - 6f), brush);
            brush.Color = ChromeBorder;
            rt.DrawLine(new System.Numerics.Vector2(x + 6f, top + qh - 4f), new System.Numerics.Vector2(x + w - 6f, top + qh - 4f), brush, 1f);
            top += qh;
        }
        int start = _ddSel >= maxRows ? _ddSel - maxRows + 1 : 0;
        rt.PushAxisAlignedClip(new Rect(x, top, w, shown * rowH + 2f), AntialiasMode.Aliased);
        for (int i = 0; i < shown; i++)
        {
            int fi = start + i; if (fi >= _ddFiltered.Count) break;
            int oi = _ddFiltered[fi];
            float ry = top + i * rowH;
            if (fi == _ddSel) { brush.Color = PalSel; rt.FillRectangle(new Rect(x + 4f, ry, w - 8f, rowH), brush); }
            brush.Color = fi == _ddSel ? SbActiveText : ChromeText;
            rt.DrawText(r.Opts[oi], _uiSmall, new Rect(x + 12f, ry, w - 20f, rowH), brush);
            _ddRows.Add((ry, ry + rowH, fi));
        }
        rt.PopAxisAlignedClip();
    }

    private void CommitDropdown(int filteredIdx)
    {
        var r = _ddRow!;
        if (filteredIdx < 0 || filteredIdx >= _ddFiltered.Count) { _ddRow = null; RequestRedraw(); return; }
        string val = r.Vals[_ddFiltered[filteredIdx]];
        _ddRow = null;
        if (r.Kind == SW.Sound)
        {
            if (val == "__custom__")
            {
                var wav = PickWav();
                if (wav is not null) { ConfigSetInternal("blocked-sound", wav); PlayStatusSound(wav); }
            }
            else { ConfigSetInternal("blocked-sound", val == "None" ? "" : val); if (val != "None") PlayStatusSound(val); }
        }
        else ConfigSetInternal(r.Key, val);
        Uia.Announce($"{r.Label}, {r.Opts[_ddFiltered[filteredIdx]]}");
        RequestRedraw();
    }

    // ---- input ----

    private void SettingsClick(int mx, int my)
    {
        if (_ddRow is not null)
        {
            if (mx >= _ddPanel.Left && mx <= _ddPanel.Right && my >= _ddPanel.Top && my <= _ddPanel.Bottom)
            {
                foreach (var (y0, y1, idx) in _ddRows) if (my >= y0 && my < y1) { CommitDropdown(idx); return; }
                return; // click inside popup chrome
            }
            _ddRow = null; RequestRedraw(); return; // click outside closes the popup
        }
        if (mx >= _setCloseX0 && mx <= _setCloseX1 && my >= _setCloseY0 && my <= _setCloseY1) { CloseSettings(); return; }
        // nav
        if (mx >= _setCard.Left + 8f && mx <= _setCard.Left + SetNavW - 8f)
            for (int i = 0; i < SetTabNames.Length; i++)
                if (my >= _navHit[i * 2] && my < _navHit[i * 2 + 1]) { _setTab = i; _setScroll = 0; _setFocus = FocusableRows().FirstOrDefault(); _setNav = -1; _setFocusKb = false; Uia.Announce(SetTabNames[i] + " tab"); RequestRedraw(); return; }
        // outside the card → dismiss
        if (mx < _setCard.Left || mx > _setCard.Right || my < _setCard.Top || my > _setCard.Bottom) { CloseSettings(); return; }
        // widgets (only within the visible pane)
        if (my < _setPaneTop || my > _setPaneBottom) return;
        foreach (var r in _setRows)
        {
            if (r.Tab != _setTab) continue;
            if (r.Kind == SW.Path && Hit(r.Bx0, r.By0, r.Bx1, r.By1, mx, my))
            { var d = PickFolder(); if (d is not null) { ConfigSetInternal("new-session-dir", d); ConfigSetInternal("new-session-dir-mode", "custom"); } return; }
            if (!r.Vis || !Hit(r.Hx0, r.Hy0, r.Hx1, r.Hy1, mx, my)) continue;
            _setFocus = r; _setNav = -1; _setFocusKb = false;   // mouse focus: sync selection but hide the focus rect
            switch (r.Kind)
            {
                case SW.Toggle:
                {
                    bool on = !IsOn(ConfigValue(r.Key));
                    ConfigSetInternal(r.Key, on ? "true" : "false");
                    Uia.Announce($"{r.Label}, {(on ? "on" : "off")}");
                    return;
                }
                case SW.Slider: _setDragRow = r; SetCapture(_hwnd); SliderTo(r, mx); return;
                case SW.Dropdown: case SW.Sound: OpenDropdown(r); return;
                case SW.Color: PickColorKey(r.Key); return;
                case SW.Profile: SetProfileDefault(r.Key); Uia.Announce($"{r.Key} is now the default profile"); return;
                case SW.Button: Uia.Announce(r.Label); r.OnClick?.Invoke(); return;
            }
        }
    }

    private static bool Hit(float x0, float y0, float x1, float y1, int mx, int my) => mx >= x0 && mx <= x1 && my >= y0 && my <= y1;

    private void SliderTo(SetRow r, int mx)
    {
        float trackX0 = r.Hx0 + 6f, trackX1 = r.Hx1 - 6f;
        float t = Math.Clamp((mx - trackX0) / MathF.Max(1f, trackX1 - trackX0), 0f, 1f);
        int v = r.Min + (int)MathF.Round(t * (r.Max - r.Min));
        if (v.ToString() != ConfigValue(r.Key)) ConfigSetInternal(r.Key, v.ToString());
        else RequestRedraw();
    }

    private void SettingsDrag(int mx) { if (_setDragRow is not null) SliderTo(_setDragRow, mx); }

    private void SettingsMouseUp()
    {
        if (_setDragRow is not null)
        {
            Uia.Announce($"{_setDragRow.Label}, {ConfigValue(_setDragRow.Key)}");
            _setDragRow = null; ReleaseCapture(); RequestRedraw();
        }
    }

    private void SettingsWheel(int dir)
    {
        if (_ddRow is not null)
        {
            _ddSel = Math.Clamp(_ddSel - dir, 0, Math.Max(0, _ddFiltered.Count - 1));
            RequestRedraw(); return;
        }
        float visible = _setPaneBottom - _setPaneTop - 12f;
        float maxScroll = MathF.Max(0f, _setContentH - visible);
        _setScroll = Math.Clamp(_setScroll - dir * 48f, 0f, maxScroll);
        RequestRedraw();
    }

    /// <summary>Panel key handling; returns true if the key was consumed.</summary>
    private bool SettingsKey(int vk)
    {
        if (_ddRow is not null)
        {
            switch (vk)
            {
                case VK_ESCAPE: _ddRow = null; RequestRedraw(); return true;
                case VK_UP: if (_ddSel > 0) { _ddSel--; RequestRedraw(); } return true;
                case VK_DOWN: if (_ddSel < _ddFiltered.Count - 1) { _ddSel++; RequestRedraw(); } return true;
                case VK_RETURN: CommitDropdown(_ddSel); return true;
                case VK_BACK: if (_ddQuery.Length > 0) { _ddQuery = _ddQuery[..^1]; FilterDropdown(); RequestRedraw(); } return true;
            }
            return true; // swallow everything while the popup is open
        }
        bool ctrl = KeyDown(VK_CONTROL), shift = KeyDown(VK_SHIFT);
        var rows = FocusableRows();
        int idx = _setFocus is not null ? rows.IndexOf(_setFocus) : -1;
        switch (vk)
        {
            case VK_ESCAPE: CloseSettings(); return true;
            case VK_TAB:
                if (ctrl) CycleSetTab(shift ? -1 : 1);
                else MoveSetFocusLinear(shift ? -1 : 1);   // Tab order: tab headers, then this tab's rows
                return true;
            case VK_DOWN: if (_setNav >= 0) MoveHeader(1); else MoveSetFocus(rows, idx, 1); return true;
            case VK_UP: if (_setNav >= 0) MoveHeader(-1); else MoveSetFocus(rows, idx, -1); return true;
            case VK_HOME:
                _setFocusKb = true;
                if (_setNav >= 0) { _setNav = 0; AfterHeaderFocus(); }
                else if (rows.Count > 0) { _setFocus = rows[0]; AfterSetFocus(); }
                return true;
            case VK_END:
                _setFocusKb = true;
                if (_setNav >= 0) { _setNav = SetTabNames.Length - 1; AfterHeaderFocus(); }
                else if (rows.Count > 0) { _setFocus = rows[^1]; AfterSetFocus(); }
                return true;
            case VK_PRIOR: CycleSetTab(-1); return true;   // PageUp → previous tab
            case VK_NEXT: CycleSetTab(1); return true;     // PageDown → next tab
            case VK_RETURN: case VK_SPACE:
                if (_setNav >= 0) ActivateHeader();        // Enter on a focused tab header switches to it
                else ActivateSetFocus();
                return true;
            case VK_LEFT: if (_setNav < 0) AdjustSetFocus(-1); return true;
            case VK_RIGHT: if (_setNav < 0) AdjustSetFocus(1); return true;
        }
        return true; // panel is modal-ish: swallow other keys so they don't reach the terminal
    }

    private void MoveSetFocus(List<SetRow> rows, int idx, int dir)
    {
        if (rows.Count == 0) return;
        _setFocusKb = true;
        int ni = idx < 0 ? (dir > 0 ? 0 : rows.Count - 1) : (idx + dir + rows.Count) % rows.Count;
        _setFocus = rows[ni];
        AfterSetFocus();
    }

    /// <summary>Linear Tab order: the tab headers first, then the current tab's rows, wrapping around —
    /// so Tab / Shift+Tab reach the headers (press Enter there to switch tabs) as well as the controls.</summary>
    private void MoveSetFocusLinear(int dir)
    {
        _setFocusKb = true;
        var rows = FocusableRows();
        int H = SetTabNames.Length, total = H + rows.Count;
        if (total == 0) return;
        int ri = _setFocus is not null ? rows.IndexOf(_setFocus) : -1;
        int pos = _setNav >= 0 ? _setNav : (ri >= 0 ? H + ri : 0);
        int np = ((pos + dir) % total + total) % total;
        if (np < H) { _setNav = np; AfterHeaderFocus(); }
        else { _setNav = -1; _setFocus = rows[np - H]; AfterSetFocus(); }
    }

    /// <summary>Move keyboard focus between tab headers (Up/Down while a header is focused).</summary>
    private void MoveHeader(int dir)
    {
        _setFocusKb = true;
        _setNav = (_setNav + dir + SetTabNames.Length) % SetTabNames.Length;
        AfterHeaderFocus();
    }

    private void AfterHeaderFocus()
    {
        Uia.Announce($"{SetTabNames[_setNav]} tab{(_setNav == _setTab ? ", selected" : "")}, press Enter to open");
        RequestRedraw();
    }

    /// <summary>Enter/Space on a focused tab header switches to that tab (focus stays on the header).</summary>
    private void ActivateHeader()
    {
        if (_setNav < 0) return;
        _setTab = _setNav;
        _setScroll = 0;
        _setFocus = FocusableRows().FirstOrDefault();
        Uia.Announce($"{SetTabNames[_setTab]} tab, selected");
        RequestRedraw();
    }

    private void CycleSetTab(int dir)
    {
        _setFocusKb = true;
        _setNav = -1;
        _setTab = (_setTab + dir + SetTabNames.Length) % SetTabNames.Length;
        _setScroll = 0;
        _setFocus = FocusableRows().FirstOrDefault();
        Uia.Announce($"{SetTabNames[_setTab]} tab");
        AfterSetFocus();
    }

    private void AfterSetFocus()
    {
        ScrollSetFocusIntoView();
        AnnounceSetFocus();
        RequestRedraw();
    }

    private void AnnounceSetFocus()
    {
        var r = _setFocus;
        if (r is null) return;
        string val = r.Kind switch
        {
            SW.Toggle => IsOn(ConfigValue(r.Key)) ? "on" : "off",
            SW.Slider => ConfigValue(r.Key),
            SW.Dropdown or SW.Sound => CurrentDropdownText(r),
            SW.Path => ConfigValue(r.Key) is { Length: > 0 } p ? p : "not set",
            SW.Profile => string.Equals(r.Key, _profileCfg.Default, StringComparison.OrdinalIgnoreCase) ? "default" : "",
            _ => "",
        };
        string kind = r.Kind switch
        {
            SW.Toggle => "toggle", SW.Slider => "slider", SW.Dropdown or SW.Sound => "dropdown",
            SW.Button => "button", SW.Color => "color", SW.Path => "folder", SW.Profile => "profile", _ => "",
        };
        Uia.Announce($"{r.Label}{(val.Length > 0 ? ", " + val : "")}, {kind}");
    }

    private void ActivateSetFocus()
    {
        var r = _setFocus;
        if (r is null) return;
        switch (r.Kind)
        {
            case SW.Toggle:
                bool on = !IsOn(ConfigValue(r.Key));
                ConfigSetInternal(r.Key, on ? "true" : "false");
                Uia.Announce($"{r.Label}, {(on ? "on" : "off")}");
                break;
            case SW.Dropdown: case SW.Sound: OpenDropdown(r); break;
            case SW.Color: PickColorKey(r.Key); break;
            case SW.Path:
                var d = PickFolder();
                if (d is not null) { ConfigSetInternal("new-session-dir", d); ConfigSetInternal("new-session-dir-mode", "custom"); }
                break;
            case SW.Profile: SetProfileDefault(r.Key); Uia.Announce($"{r.Key} is now the default profile"); break;
            case SW.Button: Uia.Announce(r.Label); r.OnClick?.Invoke(); break;
        }
        RequestRedraw();
    }

    private void AdjustSetFocus(int dir)
    {
        var r = _setFocus;
        if (r is null) return;
        if (r.Kind == SW.Slider)
        {
            int cur = int.TryParse(ConfigValue(r.Key), out var v) ? v : r.Min;
            int nv = Math.Clamp(cur + dir, r.Min, r.Max);
            if (nv != cur) { ConfigSetInternal(r.Key, nv.ToString()); Uia.Announce($"{r.Label}, {nv}"); }
            RequestRedraw();
        }
        else if (r.Kind is SW.Dropdown or SW.Sound) OpenDropdown(r);
    }

    private void ScrollSetFocusIntoView()
    {
        if (_setFocus is null) return;
        float y = 6f;
        foreach (var r in _setRows)
        {
            if (r.Tab != _setTab) continue;
            float rowH = r.Kind == SW.Section ? SetSecH : r.Kind == SW.Diag ? 150f : r.Kind == SW.Profile ? 46f : SetRowH;
            if (ReferenceEquals(r, _setFocus))
            {
                float viewH = _setPaneBottom - _setPaneTop - 12f;
                if (y - _setScroll < 0f) _setScroll = y;
                else if (y + rowH - _setScroll > viewH) _setScroll = y + rowH - viewH;
                _setScroll = Math.Clamp(_setScroll, 0f, Math.Max(0f, _setContentH - viewH));
                return;
            }
            y += rowH + (r.Kind == SW.Section ? 2f : 4f);
        }
    }

    private void PickColorKey(string key)
    {
        if (_custColors == IntPtr.Zero) _custColors = Marshal.AllocHGlobal(16 * sizeof(int));
        var cur = ConfigValue(key).Trim().TrimStart('#');
        int init = 0;
        try { if (cur.Length >= 6) init = RGB(Convert.ToInt32(cur[..2], 16), Convert.ToInt32(cur.Substring(2, 2), 16), Convert.ToInt32(cur.Substring(4, 2), 16)); } catch { }
        var cc = new CHOOSECOLOR { lStructSize = Marshal.SizeOf<CHOOSECOLOR>(), hwndOwner = _hwnd, rgbResult = init, lpCustColors = _custColors, Flags = CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR };
        if (!ChooseColorW(ref cc)) return;
        int rr = cc.rgbResult & 0xFF, gg = (cc.rgbResult >> 8) & 0xFF, bb = (cc.rgbResult >> 16) & 0xFF;
        ConfigSetInternal(key, $"#{rr:X2}{gg:X2}{bb:X2}");
    }

    private string? PickWav()
    {
        IntPtr buf = Marshal.AllocHGlobal(520 * 2);
        try
        {
            Marshal.WriteInt16(buf, 0, 0);
            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(), hwndOwner = _hwnd,
                lpstrFilter = "WAV files\0*.wav\0All files\0*.*\0\0",
                lpstrFile = buf, nMaxFile = 520, lpstrTitle = "Choose a .wav sound",
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER,
            };
            return GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(buf) : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private void ResetAgentStatusSettings()
    {
        ConfigSetInternal("status-color-active", "#3C8CFF");
        ConfigSetInternal("status-color-blocked", "#F0A028");
        ConfigSetInternal("status-color-completed", "#3CC85A");
        ConfigSetInternal("blocked-sound", "");
        RequestRedraw();
    }
}
