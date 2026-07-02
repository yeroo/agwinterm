using System;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>
/// The Settings window: a native Win32 popup with standard controls (EDIT / checkbox / COMBOBOX).
/// Every control writes its key through <see cref="ConfigSetInternal"/> — the same path as the
/// <c>config.set</c> control verb — so changes persist to agwinterm.conf and apply live.
/// Opened from the title-bar gear, the action palette ("Settings…"), or <c>settings.open</c>.
/// </summary>
internal partial class Program
{
    private const string SettingsClass = "AgwintermSettings";
    private static IntPtr _settingsHwnd;
    private static WndProc? _settingsProc;
    private static IntPtr _settingsFont;
    private static bool _settingsClassReg;
    private static bool _settingsSyncing;               // suppress control notifications during programmatic refresh

    private enum SKind { Edit, Check, Combo }
    private sealed record SCtl(int Id, string Key, SKind Kind, string[] Options)
    {
        public IntPtr Hwnd;
    }
    private static readonly System.Collections.Generic.List<SCtl> _settingsCtls = new();

    // (label, key, kind, combo-options). A null label => the checkbox provides its own caption.
    private static readonly (string label, string key, SKind kind, string[] opts, bool header)[] SettingsRows =
    {
        ("Appearance", "", SKind.Edit, new string[0], true),
        ("Theme", "theme", SKind.Combo, new string[0], false),                                  // filled from _allThemes
        ("Font family", "font-family", SKind.Edit, new string[0], false),
        ("Font size", "font-size", SKind.Edit, new string[0], false),
        ("Cursor style", "cursor-style", SKind.Combo, new[] { "bar", "block", "underline" }, false),
        ("Blink the cursor", "cursor-blink", SKind.Check, new string[0], false),
        ("Window opacity (30-100)", "window-opacity", SKind.Edit, new string[0], false),
        ("Sidebar tint (-100..100)", "sidebar-tint", SKind.Edit, new string[0], false),
        ("Inactive-pane dim (0-100)", "inactive-pane-dim", SKind.Edit, new string[0], false),
        ("Behavior", "", SKind.Edit, new string[0], true),
        ("Scroll speed (1-10)", "scroll-speed", SKind.Edit, new string[0], false),
        ("Scrollback lines *", "scrollback-lines", SKind.Edit, new string[0], false),
        ("New-session directory", "new-session-dir", SKind.Edit, new string[0], false),
        ("Blocked sound", "blocked-sound", SKind.Edit, new string[0], false),
        ("Paste on right-click", "right-click-paste", SKind.Check, new string[0], false),
        ("Copy on select", "copy-on-select", SKind.Check, new string[0], false),
        ("Desktop notifications", "desktop-notifications", SKind.Check, new string[0], false),
        ("Shell integration (live cwd) *", "shell-integration", SKind.Check, new string[0], false),
        ("Restore running commands *", "restore-commands", SKind.Check, new string[0], false),
    };

    private void OpenSettingsWindow()
    {
        if (_settingsHwnd != IntPtr.Zero) { SetForegroundWindow(_settingsHwnd); return; }
        IntPtr hInst = GetModuleHandleW(null);
        if (!_settingsClassReg)
        {
            _settingsProc = SettingsProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = 0,
                lpfnWndProc = _settingsProc,
                hInstance = hInst,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                hbrBackground = (IntPtr)16,                 // COLOR_BTNFACE+1 (standard dialog gray)
                lpszClassName = SettingsClass,
            };
            RegisterClassExW(ref wc);
            _settingsClassReg = true;
        }
        if (_settingsFont == IntPtr.Zero)
            _settingsFont = CreateFontW(-13, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

        const int w = 470, h = 680;
        int x = CW_USEDEFAULT, y = CW_USEDEFAULT;
        if (GetWindowRect(_hwnd, out RECT pr)) { x = pr.left + ((pr.right - pr.left) - w) / 2; y = pr.top + ((pr.bottom - pr.top) - h) / 2; }
        _settingsHwnd = CreateWindowExW(0, SettingsClass, "agwinterm — Settings",
            WS_POPUP | WS_CAPTION | WS_SYSMENU, x, y, w, h, _hwnd, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_settingsHwnd == IntPtr.Zero) return;

        BuildSettingsControls(hInst);
        RefreshSettingsControls();
        ShowWindow(_settingsHwnd, SW_SHOW);
        SetForegroundWindow(_settingsHwnd);
    }

    private void BuildSettingsControls(IntPtr hInst)
    {
        _settingsCtls.Clear();
        int id = 1000, y = 12;
        const int labelX = 16, labelW = 210, ctlX = 232, ctlW = 210, rowH = 30;

        foreach (var row in SettingsRows)
        {
            if (row.header)
            {
                var hdr = CreateWindowExW(0, "STATIC", row.label, WS_CHILD | WS_VISIBLE,
                    labelX, y + 6, 400, 20, _settingsHwnd, IntPtr.Zero, hInst, IntPtr.Zero);
                SendMessageW(hdr, WM_SETFONT, _settingsFont, (IntPtr)1);
                y += 30;
                continue;
            }

            int cid = id++;
            IntPtr ctl;
            if (row.kind == SKind.Check)
            {
                ctl = CreateWindowExW(0, "BUTTON", row.label, WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                    labelX, y + 4, labelW + ctlW, 22, _settingsHwnd, (IntPtr)cid, hInst, IntPtr.Zero);
            }
            else
            {
                var lbl = CreateWindowExW(0, "STATIC", row.label, WS_CHILD | WS_VISIBLE,
                    labelX, y + 6, labelW, 20, _settingsHwnd, IntPtr.Zero, hInst, IntPtr.Zero);
                SendMessageW(lbl, WM_SETFONT, _settingsFont, (IntPtr)1);
                if (row.kind == SKind.Combo)
                {
                    ctl = CreateWindowExW(0, "COMBOBOX", "", WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWNLIST | CBS_HASSTRINGS,
                        ctlX, y + 2, ctlW, 240, _settingsHwnd, (IntPtr)cid, hInst, IntPtr.Zero);
                    string[] opts = row.key == "theme" ? _allThemes.Select(t => t.Name).ToArray() : row.opts;
                    foreach (var o in opts) SendMessageW(ctl, CB_ADDSTRING, IntPtr.Zero, o);
                    _settingsCtls.Add(new SCtl(cid, row.key, SKind.Combo, opts) { Hwnd = ctl });
                    SendMessageW(ctl, WM_SETFONT, _settingsFont, (IntPtr)1);
                    y += rowH;
                    continue;
                }
                ctl = CreateWindowExW(0, "EDIT", "", WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL | 0x00800000 /*WS_BORDER*/,
                    ctlX, y + 2, ctlW, 22, _settingsHwnd, (IntPtr)cid, hInst, IntPtr.Zero);
            }
            SendMessageW(ctl, WM_SETFONT, _settingsFont, (IntPtr)1);
            _settingsCtls.Add(new SCtl(cid, row.key, row.kind, row.opts) { Hwnd = ctl });
            y += rowH;
        }

        // Footnote + Close button.
        var note = CreateWindowExW(0, "STATIC", "* applies to new sessions", WS_CHILD | WS_VISIBLE,
            labelX, y + 6, 300, 20, _settingsHwnd, IntPtr.Zero, hInst, IntPtr.Zero);
        SendMessageW(note, WM_SETFONT, _settingsFont, (IntPtr)1);
        var close = CreateWindowExW(0, "BUTTON", "Close", WS_CHILD | WS_VISIBLE | WS_TABSTOP,
            ctlX + 120, y + 2, 90, 26, _settingsHwnd, (IntPtr)1, hInst, IntPtr.Zero);   // id 1 = IDOK/close
        SendMessageW(close, WM_SETFONT, _settingsFont, (IntPtr)1);
    }

    /// <summary>Push current config values into the open Settings controls (no-op when closed).</summary>
    private void RefreshSettingsControls()
    {
        if (_settingsHwnd == IntPtr.Zero) return;
        _settingsSyncing = true;
        try
        {
            foreach (var c in _settingsCtls)
            {
                string v = ConfigValue(c.Key);
                switch (c.Kind)
                {
                    case SKind.Edit: SetWindowTextW(c.Hwnd, v); break;
                    case SKind.Check: SendMessageW(c.Hwnd, BM_SETCHECK, (IntPtr)(v is "true" or "yes" or "on" or "1" ? 1 : 0), IntPtr.Zero); break;
                    case SKind.Combo:
                        int idx = Array.FindIndex(c.Options, o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase));
                        SendMessageW(c.Hwnd, CB_SETCURSEL, (IntPtr)idx, IntPtr.Zero);
                        break;
                }
            }
        }
        finally { _settingsSyncing = false; }
    }

    private static IntPtr SettingsProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_COMMAND:
            {
                int cid = LoWord(wParam);
                int code = HiWord(wParam);
                if (cid == 1) { DestroyWindow(hWnd); return IntPtr.Zero; } // Close
                if (_settingsSyncing) break;
                var c = _settingsCtls.FirstOrDefault(x => x.Id == cid);
                if (c is null) break;
                if (c.Kind == SKind.Check && code == BN_CLICKED)
                {
                    bool on = SendMessageW(c.Hwnd, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero;
                    Frontmost.ConfigSetInternal(c.Key, on ? "true" : "false");
                }
                else if (c.Kind == SKind.Combo && code == CBN_SELCHANGE)
                {
                    int sel = (int)SendMessageW(c.Hwnd, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
                    if (sel >= 0 && sel < c.Options.Length) Frontmost.ConfigSetInternal(c.Key, c.Options[sel]);
                }
                else if (c.Kind == SKind.Edit && code == EN_KILLFOCUS)
                {
                    var sb = new StringBuilder(512);
                    GetWindowTextW(c.Hwnd, sb, sb.Capacity);
                    Frontmost.ConfigSetInternal(c.Key, sb.ToString());
                }
                return IntPtr.Zero;
            }
            case WM_CLOSE:
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case 0x0082: // WM_NCDESTROY
                _settingsHwnd = IntPtr.Zero;
                _settingsCtls.Clear();
                break;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
