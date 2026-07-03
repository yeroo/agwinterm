using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>
/// The Settings window: a native Win32 tabbed dialog (General / Appearance / Notifications /
/// Agent Status / Key Mapping) matching agterm's preferences. Every control writes its key through
/// <see cref="ConfigSetInternal"/> — the same path as the <c>config.set</c> verb — so changes persist
/// to agwinterm.conf and apply live. Opened from the title-bar gear, the palette, or <c>settings.open</c>.
/// </summary>
internal partial class Program
{
    private const string SettingsClass = "AgwintermSettings";
    private static IntPtr _settingsHwnd, _settingsTab;
    private static WndProc? _settingsProc;
    private static IntPtr _settingsFont, _settingsBold;
    private static bool _settingsClassReg, _settingsCommonInit;
    private static bool _settingsSyncing;                 // suppress control notifications during a programmatic refresh
    private static IntPtr _custColors;                    // 16-slot custom-colors buffer for ChooseColor

    private enum SCK { Check, Combo, Slider, Color, Sound, Path, Multiline, Static }

    private sealed class SC
    {
        public int Id, Tab, Min, Max;
        public string Key = "";
        public SCK Kind;
        public IntPtr Hwnd, Aux;                          // Aux: slider value-label / path browse-button
        public string[] Opts = Array.Empty<string>();     // combo display strings
        public string[] Vals = Array.Empty<string>();     // combo stored values (parallel to Opts)
    }
    private static readonly List<SC> _sctls = new();
    private static readonly Dictionary<IntPtr, IntPtr> _swatchBrush = new(); // swatch hwnd -> HBRUSH
    private static int _settingsTabCur;

    private static readonly string[] TabNames = { "General", "Appearance", "Notifications", "Agent Status", "Key Mapping" };

    // Action-button ids (kept out of the auto id range).
    private const int ID_CLOSE = 1, ID_RESET_STATUS = 3001, ID_RELOAD_KEYMAP = 3002, ID_OPEN_CFG = 3003, ID_CHOOSE_DIR = 3004;

    private void OpenSettingsWindow()
    {
        if (_settingsHwnd != IntPtr.Zero) { SetForegroundWindow(_settingsHwnd); return; }
        IntPtr hInst = GetModuleHandleW(null);
        if (!_settingsCommonInit)
        {
            var icc = new INITCOMMONCONTROLSEX { dwSize = Marshal.SizeOf<INITCOMMONCONTROLSEX>(), dwICC = ICC_TAB_CLASSES | ICC_BAR_CLASSES | ICC_STANDARD_CLASSES };
            InitCommonControlsEx(ref icc);
            _settingsCommonInit = true;
        }
        if (!_settingsClassReg)
        {
            _settingsProc = SettingsProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _settingsProc,
                hInstance = hInst,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                hbrBackground = (IntPtr)16,                // COLOR_BTNFACE+1 (dialog gray)
                lpszClassName = SettingsClass,
            };
            RegisterClassExW(ref wc);
            _settingsClassReg = true;
        }
        if (_settingsFont == IntPtr.Zero) _settingsFont = CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        if (_settingsBold == IntPtr.Zero) _settingsBold = CreateFontW(-12, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        if (_custColors == IntPtr.Zero) _custColors = Marshal.AllocHGlobal(16 * sizeof(int));

        const int w = 480, h = 600;
        int x = CW_USEDEFAULT, y = CW_USEDEFAULT;
        if (GetWindowRect(_hwnd, out RECT pr)) { x = pr.left + ((pr.right - pr.left) - w) / 2; y = pr.top + ((pr.bottom - pr.top) - h) / 2; }
        _settingsHwnd = CreateWindowExW(0, SettingsClass, "agwinterm — Settings",
            WS_POPUP | WS_CAPTION | WS_SYSMENU, x, y, w, h, _hwnd, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_settingsHwnd == IntPtr.Zero) return;

        BuildTabs(hInst);
        BuildControls(hInst);
        RefreshSettingsControls();
        ShowTab(0);
        ShowWindow(_settingsHwnd, SW_SHOW);
        SetForegroundWindow(_settingsHwnd);
    }

    private void BuildTabs(IntPtr hInst)
    {
        _settingsTab = CreateWindowExW(0, "SysTabControl32", "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            6, 6, 468 - 20, 560, _settingsHwnd, IntPtr.Zero, hInst, IntPtr.Zero);
        SendMessageW(_settingsTab, WM_SETFONT, _settingsFont, (IntPtr)1);
        IntPtr pti = Marshal.AllocHGlobal(Marshal.SizeOf<TCITEMW>());
        for (int i = 0; i < TabNames.Length; i++)
        {
            IntPtr txt = Marshal.StringToHGlobalUni(TabNames[i]);
            var ti = new TCITEMW { mask = TCIF_TEXT, pszText = txt, cchTextMax = TabNames[i].Length };
            Marshal.StructureToPtr(ti, pti, false);
            SendMessageW(_settingsTab, TCM_INSERTITEMW, (IntPtr)i, pti);
            Marshal.FreeHGlobal(txt);
        }
        Marshal.FreeHGlobal(pti);
    }

    // --- control-building helpers (parented to the settings window; shown/hidden per tab) ---
    private int _bid;                 // auto control id
    private int[] _ty = new int[8];   // per-tab running y

    private const int LX = 24, LW = 180, CX = 210, CW = 226, ROW = 30, TOP = 44;

    private SC Add(SC c) { _sctls.Add(c); return c; }

    private void Section(IntPtr hInst, int tab, string text)
    {
        int y = _ty[tab]; _ty[tab] += 26;
        var s = CreateWindowExW(0, "STATIC", text, WS_CHILD, LX - 8, y + 6, 400, 18, _settingsHwnd, IntPtr.Zero, hInst, IntPtr.Zero);
        SendMessageW(s, WM_SETFONT, _settingsBold, (IntPtr)1);
        Add(new SC { Tab = tab, Kind = SCK.Static, Hwnd = s });
    }

    private void Label(IntPtr hInst, int tab, int y, string text)
    {
        var s = CreateWindowExW(0, "STATIC", text, WS_CHILD, LX, y + 5, LW, 18, _settingsHwnd, IntPtr.Zero, hInst, IntPtr.Zero);
        SendMessageW(s, WM_SETFONT, _settingsFont, (IntPtr)1);
        Add(new SC { Tab = tab, Kind = SCK.Static, Hwnd = s });
    }

    private void AddCheck(IntPtr hInst, int tab, string key, string label)
    {
        int y = _ty[tab]; _ty[tab] += ROW; int id = ++_bid + 2000;
        var b = CreateWindowExW(0, "BUTTON", label, WS_CHILD | WS_TABSTOP | BS_AUTOCHECKBOX, LX, y + 3, LW + CW, 22, _settingsHwnd, (IntPtr)id, hInst, IntPtr.Zero);
        SendMessageW(b, WM_SETFONT, _settingsFont, (IntPtr)1);
        Add(new SC { Id = id, Tab = tab, Key = key, Kind = SCK.Check, Hwnd = b });
    }

    private void AddCombo(IntPtr hInst, int tab, string key, string label, string[] opts, string[]? vals = null)
    {
        int y = _ty[tab]; _ty[tab] += ROW; int id = ++_bid + 2000;
        Label(hInst, tab, y, label);
        var cb = CreateWindowExW(0, "COMBOBOX", "", WS_CHILD | WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWNLIST | CBS_HASSTRINGS, CX, y + 2, CW, 300, _settingsHwnd, (IntPtr)id, hInst, IntPtr.Zero);
        foreach (var o in opts) SendMessageW(cb, CB_ADDSTRING, IntPtr.Zero, o);
        SendMessageW(cb, WM_SETFONT, _settingsFont, (IntPtr)1);
        Add(new SC { Id = id, Tab = tab, Key = key, Kind = key == "blocked-sound" ? SCK.Sound : SCK.Combo, Hwnd = cb, Opts = opts, Vals = vals ?? opts });
    }

    private void AddSlider(IntPtr hInst, int tab, string key, string label, int min, int max)
    {
        int y = _ty[tab]; _ty[tab] += ROW; int id = ++_bid + 2000;
        Label(hInst, tab, y, label);
        var tb = CreateWindowExW(0, "msctls_trackbar32", "", WS_CHILD | WS_TABSTOP | TBS_HORZ | TBS_AUTOTICKS, CX, y + 2, CW - 46, 24, _settingsHwnd, (IntPtr)id, hInst, IntPtr.Zero);
        SendMessageW(tb, TBM_SETRANGE, (IntPtr)1, (IntPtr)((min & 0xFFFF) | (max << 16)));
        var val = CreateWindowExW(0, "STATIC", "", WS_CHILD, CX + CW - 42, y + 5, 42, 18, _settingsHwnd, IntPtr.Zero, hInst, IntPtr.Zero);
        SendMessageW(val, WM_SETFONT, _settingsFont, (IntPtr)1);
        Add(new SC { Id = id, Tab = tab, Key = key, Kind = SCK.Slider, Hwnd = tb, Aux = val, Min = min, Max = max });
    }

    private void AddColor(IntPtr hInst, int tab, int id, string key, string label)
    {
        int y = _ty[tab]; _ty[tab] += ROW;
        Label(hInst, tab, y, label);
        var sw = CreateWindowExW(0, "STATIC", "", WS_CHILD | 0x00000100 /*SS_NOTIFY*/ | 0x00000001 /*SS_SUNKEN? no*/, CX, y + 3, 60, 20, _settingsHwnd, (IntPtr)id, hInst, IntPtr.Zero);
        Add(new SC { Id = id, Tab = tab, Key = key, Kind = SCK.Color, Hwnd = sw });
    }

    private void AddPath(IntPtr hInst, int tab, string key, string label)
    {
        int y = _ty[tab]; _ty[tab] += ROW; int id = ++_bid + 2000;
        Label(hInst, tab, y, label);
        var ed = CreateWindowExW(0, "EDIT", "", WS_CHILD | WS_TABSTOP | ES_AUTOHSCROLL | 0x00800000, CX, y + 2, CW - 66, 22, _settingsHwnd, (IntPtr)id, hInst, IntPtr.Zero);
        SendMessageW(ed, WM_SETFONT, _settingsFont, (IntPtr)1);
        var br = CreateWindowExW(0, "BUTTON", "Choose…", WS_CHILD | WS_TABSTOP, CX + CW - 60, y + 1, 60, 24, _settingsHwnd, (IntPtr)ID_CHOOSE_DIR, hInst, IntPtr.Zero);
        SendMessageW(br, WM_SETFONT, _settingsFont, (IntPtr)1);
        Add(new SC { Id = id, Tab = tab, Key = key, Kind = SCK.Path, Hwnd = ed, Aux = br });
    }

    private IntPtr Button(IntPtr hInst, int tab, int id, string label, int wdt = 130)
    {
        int y = _ty[tab]; _ty[tab] += ROW + 4;
        var b = CreateWindowExW(0, "BUTTON", label, WS_CHILD | WS_TABSTOP, LX, y + 2, wdt, 26, _settingsHwnd, (IntPtr)id, hInst, IntPtr.Zero);
        SendMessageW(b, WM_SETFONT, _settingsFont, (IntPtr)1);
        Add(new SC { Id = id, Tab = tab, Kind = SCK.Static, Hwnd = b });
        return b;
    }

    private void BuildControls(IntPtr hInst)
    {
        _sctls.Clear(); _swatchBrush.Clear(); _bid = 0;
        for (int i = 0; i < _ty.Length; i++) _ty[i] = TOP;

        // ---- General ----
        Section(hInst, 0, "Mouse");
        AddSlider(hInst, 0, "scroll-speed", "Scroll speed", 1, 10);
        AddCheck(hInst, 0, "right-click-paste", "Right-click pastes the clipboard");
        AddCheck(hInst, 0, "copy-on-select", "Copy selection to clipboard automatically");
        Section(hInst, 0, "Sessions");
        AddCombo(hInst, 0, "new-session-dir-mode", "New sessions open in",
            new[] { "Home directory", "Current session's dir", "Custom directory" }, new[] { "home", "current", "custom" });
        AddPath(hInst, 0, "new-session-dir", "Custom directory");
        AddCheck(hInst, 0, "restore-commands", "Restore running commands on restart");
        AddCheck(hInst, 0, "confirm-close-session", "Confirm before closing a session");
        Section(hInst, 0, "Shell");
        AddCheck(hInst, 0, "shell-integration", "Shell integration (live working directory)");

        // ---- Appearance ----
        Section(hInst, 1, "Terminal");
        string[] fonts = { "Default", "MesloLGSDZ Nerd Font", "Cascadia Mono", "Cascadia Code", "Consolas", "JetBrains Mono", "Fira Code", "Hack", "Source Code Pro", "Courier New", "Lucida Console" };
        AddCombo(hInst, 1, "font-family", "Font", fonts);
        AddSlider(hInst, 1, "font-size", "Font size", 8, 32);
        AddCombo(hInst, 1, "theme", "Theme", _allThemes.Select(t => t.Name).ToArray());
        AddCombo(hInst, 1, "cursor-style", "Cursor style", new[] { "bar", "block", "underline" });
        AddCheck(hInst, 1, "cursor-blink", "Blink the cursor");
        Section(hInst, 1, "Window");
        AddCheck(hInst, 1, "compact-toolbar", "Compact toolbar (shorter title bar)");
        AddSlider(hInst, 1, "window-opacity", "Window opacity", 30, 100);
        AddSlider(hInst, 1, "sidebar-tint", "Sidebar tint", -100, 100);
        Section(hInst, 1, "Panes");
        AddSlider(hInst, 1, "inactive-pane-dim", "Inactive pane mute", 0, 100);

        // ---- Notifications ----
        Section(hInst, 2, "Notifications");
        AddCheck(hInst, 2, "desktop-notifications", "Show desktop notifications (OS tray)");
        AddCheck(hInst, 2, "notification-badges", "Show unread badges on sidebar rows");
        AddCheck(hInst, 2, "attention-button", "Show the title-bar attention indicator");

        // ---- Agent Status ----
        Section(hInst, 3, "Colors");
        AddColor(hInst, 3, 2101, "status-color-active", "Active");
        AddColor(hInst, 3, 2102, "status-color-blocked", "Blocked");
        AddColor(hInst, 3, 2103, "status-color-completed", "Completed");
        Section(hInst, 3, "Sound");
        AddCombo(hInst, 3, "blocked-sound", "Blocked sound",
            new[] { "None", "beep", "asterisk", "exclamation", "hand", "question", "Custom .wav…" },
            new[] { "None", "beep", "asterisk", "exclamation", "hand", "question", "__custom__" });
        Button(hInst, 3, ID_RESET_STATUS, "Reset to defaults");

        // ---- Key Mapping ----
        Section(hInst, 4, "Config Directory");
        Label(hInst, 4, _ty[4], System.IO.Path.GetDirectoryName(KeymapPath) ?? ""); _ty[4] += ROW;
        Button(hInst, 4, ID_OPEN_CFG, "Open Folder", 110);
        Button(hInst, 4, ID_RELOAD_KEYMAP, "Reload", 110);
        Section(hInst, 4, "Diagnostics");
        {
            int y = _ty[4];
            var ml = CreateWindowExW(0, "EDIT", "", WS_CHILD | WS_VSCROLL | 0x00800000 | ES_MULTILINE | ES_READONLY,
                LX, y + 2, 420, 180, _settingsHwnd, (IntPtr)(++_bid + 2000), hInst, IntPtr.Zero);
            SendMessageW(ml, WM_SETFONT, _settingsFont, (IntPtr)1);
            Add(new SC { Tab = 4, Kind = SCK.Multiline, Key = "__diag__", Hwnd = ml });
        }

        // Close button (all tabs).
        var close = CreateWindowExW(0, "BUTTON", "Close", WS_CHILD | WS_VISIBLE | WS_TABSTOP, 480 - 110, 600 - 66, 90, 26, _settingsHwnd, (IntPtr)ID_CLOSE, hInst, IntPtr.Zero);
        SendMessageW(close, WM_SETFONT, _settingsFont, (IntPtr)1);
    }

    private void ShowTab(int tab)
    {
        _settingsTabCur = tab;
        SendMessageW(_settingsTab, TCM_SETCURSEL, (IntPtr)tab, IntPtr.Zero);
        foreach (var c in _sctls)
        {
            bool vis = c.Tab == tab;
            ShowWindow(c.Hwnd, vis ? SW_SHOW : 0);
            if (c.Aux != IntPtr.Zero) ShowWindow(c.Aux, vis ? SW_SHOW : 0);
        }
    }

    private static string SoundDisplay(string v) =>
        string.IsNullOrWhiteSpace(v) || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? "None" : v;

    /// <summary>Push current config values into the open Settings controls (no-op when closed).</summary>
    private void RefreshSettingsControls()
    {
        if (_settingsHwnd == IntPtr.Zero) return;
        _settingsSyncing = true;
        try
        {
            foreach (var c in _sctls)
            {
                switch (c.Kind)
                {
                    case SCK.Check:
                        SendMessageW(c.Hwnd, BM_SETCHECK, (IntPtr)(ConfigValue(c.Key) is "true" or "yes" or "on" or "1" ? 1 : 0), IntPtr.Zero);
                        break;
                    case SCK.Combo:
                    {
                        string v = ConfigValue(c.Key);
                        int idx = Array.FindIndex(c.Vals, o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase));
                        if (idx < 0 && c.Key is "theme" or "font-family" && v.Length > 0) // value not in list → add it
                        { SendMessageW(c.Hwnd, CB_ADDSTRING, IntPtr.Zero, v); c.Opts = c.Opts.Append(v).ToArray(); c.Vals = c.Vals.Append(v).ToArray(); idx = c.Vals.Length - 1; }
                        SendMessageW(c.Hwnd, CB_SETCURSEL, (IntPtr)idx, IntPtr.Zero);
                        break;
                    }
                    case SCK.Sound:
                    {
                        string disp = SoundDisplay(ConfigValue(c.Key));
                        int idx = Array.FindIndex(c.Vals, o => string.Equals(o, disp, StringComparison.OrdinalIgnoreCase));
                        if (idx < 0) // a custom path/alias → add it as an item and select
                        { SendMessageW(c.Hwnd, CB_ADDSTRING, IntPtr.Zero, disp); c.Opts = c.Opts.Append(disp).ToArray(); c.Vals = c.Vals.Append(disp).ToArray(); idx = c.Vals.Length - 1; }
                        SendMessageW(c.Hwnd, CB_SETCURSEL, (IntPtr)idx, IntPtr.Zero);
                        break;
                    }
                    case SCK.Slider:
                    {
                        int.TryParse(ConfigValue(c.Key), out int v);
                        v = Math.Clamp(v, c.Min, c.Max);
                        SendMessageW(c.Hwnd, TBM_SETPOS, (IntPtr)1, (IntPtr)v);
                        SetWindowTextW(c.Aux, SliderLabel(c.Key, v));
                        break;
                    }
                    case SCK.Color:
                        SetSwatch(c.Hwnd, ConfigValue(c.Key));
                        break;
                    case SCK.Path:
                        SetWindowTextW(c.Hwnd, ConfigValue(c.Key));
                        break;
                    case SCK.Multiline:
                        SetWindowTextW(c.Hwnd, _keymapDiag.Length == 0 ? "No issues." : string.Join("\r\n", _keymapDiag));
                        break;
                }
            }
        }
        finally { _settingsSyncing = false; }
    }

    private static string SliderLabel(string key, int v) => key switch
    {
        "scroll-speed" => v + "x",
        "window-opacity" => v + "%",
        _ => v.ToString(),
    };

    private static void SetSwatch(IntPtr sw, string hex)
    {
        var h = (hex ?? "").Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        int r = 0, g = 0, b = 0;
        try { if (h.Length >= 6) { r = Convert.ToInt32(h[..2], 16); g = Convert.ToInt32(h.Substring(2, 2), 16); b = Convert.ToInt32(h.Substring(4, 2), 16); } } catch { }
        if (_swatchBrush.TryGetValue(sw, out var old)) DeleteObject(old);
        _swatchBrush[sw] = CreateSolidBrush(RGB(r, g, b));
        InvalidateRect(sw, IntPtr.Zero, true);
    }

    private static IntPtr SettingsProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NOTIFY:
            {
                var nm = Marshal.PtrToStructure<NMHDR>(lParam);
                if (nm.hwndFrom == _settingsTab && nm.code == TCN_SELCHANGE)
                    Frontmost.ShowTab((int)SendMessageW(_settingsTab, TCM_GETCURSEL, IntPtr.Zero, IntPtr.Zero));
                break;
            }
            case WM_CTLCOLORSTATIC:
                if (_swatchBrush.TryGetValue(lParam, out var br)) return br;   // paint color swatches
                break;
            case WM_HSCROLL:
            {
                var c = _sctls.FirstOrDefault(x => x.Hwnd == lParam && x.Kind == SCK.Slider);
                if (c is not null)
                {
                    int pos = (int)SendMessageW(c.Hwnd, TBM_GETPOS, IntPtr.Zero, IntPtr.Zero);
                    SetWindowTextW(c.Aux, SliderLabel(c.Key, pos));
                    Frontmost.ConfigSetInternal(c.Key, pos.ToString());
                }
                return IntPtr.Zero;
            }
            case WM_COMMAND:
            {
                int cid = LoWord(wParam), code = HiWord(wParam);
                if (cid == ID_CLOSE) { DestroyWindow(hWnd); return IntPtr.Zero; }
                if (cid == ID_RESET_STATUS) { Frontmost.ResetAgentStatusSettings(); return IntPtr.Zero; }
                if (cid == ID_RELOAD_KEYMAP) { Frontmost.ReloadKeymap(); Frontmost.RefreshSettingsControls(); return IntPtr.Zero; }
                if (cid == ID_OPEN_CFG) { var dir = System.IO.Path.GetDirectoryName(KeymapPath); if (dir is not null) { System.IO.Directory.CreateDirectory(dir); ShellExecuteW(hWnd, "open", dir, null, null, 5); } return IntPtr.Zero; }
                if (cid == ID_CHOOSE_DIR) { var d = Frontmost.PickFolder(); if (d is not null) { Frontmost.ConfigSetInternal("new-session-dir", d); Frontmost.ConfigSetInternal("new-session-dir-mode", "custom"); } return IntPtr.Zero; }
                if (_settingsSyncing) break;

                var ctl = _sctls.FirstOrDefault(x => x.Id == cid && x.Id != 0);
                if (ctl is null) break;
                switch (ctl.Kind)
                {
                    case SCK.Check when code == BN_CLICKED:
                        Frontmost.ConfigSetInternal(ctl.Key, SendMessageW(ctl.Hwnd, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero ? "true" : "false");
                        break;
                    case SCK.Color when code == 0 /*STN_CLICKED*/:
                        Frontmost.PickColor(ctl);
                        break;
                    case SCK.Combo when code == CBN_SELCHANGE:
                    {
                        int sel = (int)SendMessageW(ctl.Hwnd, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
                        if (sel >= 0 && sel < ctl.Vals.Length) Frontmost.ConfigSetInternal(ctl.Key, ctl.Vals[sel]);
                        break;
                    }
                    case SCK.Sound when code == CBN_SELCHANGE:
                        Frontmost.OnSoundChanged(ctl);
                        break;
                    case SCK.Path when code == EN_KILLFOCUS:
                    {
                        var sb = new StringBuilder(512); GetWindowTextW(ctl.Hwnd, sb, sb.Capacity);
                        Frontmost.ConfigSetInternal(ctl.Key, sb.ToString());
                        break;
                    }
                }
                return IntPtr.Zero;
            }
            case WM_CLOSE:
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case 0x0082: // WM_NCDESTROY
                foreach (var b in _swatchBrush.Values) DeleteObject(b);
                _swatchBrush.Clear();
                _settingsHwnd = IntPtr.Zero; _settingsTab = IntPtr.Zero; _sctls.Clear();
                break;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>Open the Windows color picker for a status-color swatch and persist the chosen hex.</summary>
    private void PickColor(SC c)
    {
        var cur = ConfigValue(c.Key).Trim().TrimStart('#');
        int init = 0;
        try { if (cur.Length >= 6) init = RGB(Convert.ToInt32(cur[..2], 16), Convert.ToInt32(cur.Substring(2, 2), 16), Convert.ToInt32(cur.Substring(4, 2), 16)); } catch { }
        var cc = new CHOOSECOLOR { lStructSize = Marshal.SizeOf<CHOOSECOLOR>(), hwndOwner = _settingsHwnd, rgbResult = init, lpCustColors = _custColors, Flags = CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR };
        if (!ChooseColorW(ref cc)) return;
        int rr = cc.rgbResult & 0xFF, gg = (cc.rgbResult >> 8) & 0xFF, bb = (cc.rgbResult >> 16) & 0xFF;
        ConfigSetInternal(c.Key, $"#{rr:X2}{gg:X2}{bb:X2}");
        SetSwatch(c.Hwnd, ConfigValue(c.Key));
    }

    /// <summary>Blocked-sound combo change: None→clear, Custom→file picker, else set + preview.</summary>
    private void OnSoundChanged(SC c)
    {
        int sel = (int)SendMessageW(c.Hwnd, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        if (sel < 0 || sel >= c.Vals.Length) return;
        string val = c.Vals[sel];
        if (val == "__custom__")
        {
            var wav = PickWav();
            if (wav is null) { RefreshSettingsControls(); return; }
            ConfigSetInternal("blocked-sound", wav);
            PlayStatusSound(wav);
            RefreshSettingsControls();
            return;
        }
        ConfigSetInternal("blocked-sound", val == "None" ? "" : val);
        if (val != "None") PlayStatusSound(val);   // immediate preview
    }

    private string? PickWav()
    {
        IntPtr buf = Marshal.AllocHGlobal(520 * 2);
        try
        {
            Marshal.WriteInt16(buf, 0, 0);
            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(), hwndOwner = _settingsHwnd,
                lpstrFilter = "WAV files\0*.wav\0All files\0*.*\0\0",
                lpstrFile = buf, nMaxFile = 520, lpstrTitle = "Choose a .wav sound",
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER,
            };
            return GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(buf) : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Reset the Agent-Status tab: status colors + blocked sound back to defaults.</summary>
    private void ResetAgentStatusSettings()
    {
        ConfigSetInternal("status-color-active", "#3C8CFF");
        ConfigSetInternal("status-color-blocked", "#F0A028");
        ConfigSetInternal("status-color-completed", "#3CC85A");
        ConfigSetInternal("blocked-sound", "");
        RefreshSettingsControls();
    }
}
