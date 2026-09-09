using System.Runtime.InteropServices;
using Agwinterm.Core;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private bool _isQuickWindow;
    private static Program? _quickHost;
    private bool _quickVisible, _quickPinned;
    private long _quickAutoHiddenAt;
    private IntPtr _quickPreviousForeground;
    private static QuickHotkey _registeredQuickChord;
    private static int _quickHotkeyId;
    private static readonly HashSet<int> _quickHotkeyReservations = new();
    private const uint QuickHotkeyMessage = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr hwnd);

    private static Program EnsureQuickHost()
    {
        if (_quickHost is not null) return _quickHost;
        var host = new Program { Id = "quick", WinName = "Quick terminal", _isQuickWindow = true, _sidebarW = 0 };
        host.Boot(_hInstance);
        return _quickHost = host;
    }

    // Acquire the replacement BEFORE releasing the current chord. Two alternating IDs avoid
    // RegisterHotKey's same-ID accumulation rule; conflicts cannot silently unbind the old key.
    private static string? SetQuickHotkey(string value, Action? persist = null)
    {
        if (!QuickHotkey.TryParse(value, out var chord))
            return "error: quick-terminal-hotkey requires Ctrl/Alt, optional Shift, and a letter/digit, F1-F11 or backtick";
        var host = EnsureQuickHost();
        foreach (int extra in _quickHotkeyReservations.Where(id => id != _quickHotkeyId).ToArray())
        {
            if (!UnregisterHotKey(host._hwnd, extra)) return "error: previous provisional hotkey cleanup is incomplete";
            _quickHotkeyReservations.Remove(extra);
        }
        if (chord == _registeredQuickChord) { persist?.Invoke(); return null; }
        int next = _quickHotkeyId == 0x514 ? 0x515 : 0x514;
        if (chord.Key != 0 && !RegisterHotKey(host._hwnd, next, chord.Modifiers | 0x4000, chord.Key))
            return $"error: quick-terminal-hotkey unavailable (Win32 {Marshal.GetLastWin32Error()}); previous binding retained";
        if (chord.Key != 0) _quickHotkeyReservations.Add(next);
        try { persist?.Invoke(); } // old registration remains reserved throughout the atomic save
        catch (Exception ex)
        {
            bool clean = chord.Key == 0 || UnregisterHotKey(host._hwnd, next);
            if (clean) _quickHotkeyReservations.Remove(next);
            return "error: could not save quick-terminal-hotkey: " + ex.Message
                + (clean ? "; previous binding retained" : "; provisional hotkey cleanup failed (reservation retained for retry/shutdown)");
        }
        if (_quickHotkeyId != 0 && !UnregisterHotKey(host._hwnd, _quickHotkeyId))
        {
            // Persistence succeeded, but Windows refused cleanup. Keep both ids tracked and
            // report partial application explicitly; never claim that the saved file is unchanged.
            _registeredQuickChord = chord;
            _quickHotkeyId = chord.Key == 0 ? 0 : next;
            _config.QuickTerminalHotkey = value;
            return "error: hotkey config saved, but previous OS reservation could not be released; restart to retry cleanup";
        }
        _quickHotkeyReservations.Remove(_quickHotkeyId);
        _quickHotkeyId = chord.Key == 0 ? 0 : next;
        _registeredQuickChord = chord;
        return null;
    }

    private void PositionQuick()
    {
        GetCursorPos(out POINT pointer);
        IntPtr monitor = MonitorFromPoint(pointer, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) return;
        var r = info.rcWork;
        var f = QuickTerminalGeometry.Frame(r.left, r.top, r.right - r.left, r.bottom - r.top, _config.QuickTerminalSize);
        SetWindowPos(_hwnd, new IntPtr(-1), f.X, f.Y, f.Width, f.Height, SWP_NOACTIVATE);
        RegridCover();
    }

    private void SummonQuick(bool pin, bool global = false)
    {
        var host = EnsureQuickHost();
        if (!pin && !global && !host._quickVisible && host._quickAutoHiddenAt != 0
            && Environment.TickCount64 - host._quickAutoHiddenAt < 300) return;
        if (host._quickVisible) { if (pin) host._quickPinned = true; return; }
        host._quickAutoHiddenAt = 0;
        host._quickPreviousForeground = GetForegroundWindow();
        host.PositionQuick();
        if (host._quick is { S.HasExited: true }) { host._quick.S.Dispose(); host._quick = null; }
        host._quick ??= host.CreatePane("quick:" + Guid.NewGuid().ToString("N")[..6],
            new Workspace { Id = "", Name = "" }, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), (float)_config.FontSize);
        host._cover = host._quick; host._coverKind = 2; host.SyncSession(); host.RegridCover();
        host._quickPinned = pin; host._quickVisible = true;
        ShowWindow(host._hwnd, SW_SHOWNOACTIVATE);
        if (!pin) SetForegroundWindow(host._hwnd); // human summons only; scripts do not seize focus
        host.RequestRedraw();
    }

    private void DismissQuick(bool blur = false)
    {
        if (!_isQuickWindow) { _quickHost?.DismissQuick(blur); return; }
        if (!_quickVisible) return;
        bool restoreFocus = !blur && GetForegroundWindow() == _hwnd;
        _quickVisible = false; _quickPinned = false;
        _quickAutoHiddenAt = blur ? Environment.TickCount64 : 0;
        ReleaseQuickKeys(); CancelLeader(); CloseSearch();
        _selecting = false; _selPane = null; StopSelAutoscroll();
        if (GetCapture() == _hwnd) ReleaseCapture();
        ShowWindow(_hwnd, 0 /* SW_HIDE */);
        if (restoreFocus && _quickPreviousForeground != IntPtr.Zero && IsWindow(_quickPreviousForeground))
            SetForegroundWindow(_quickPreviousForeground);
        _quickPreviousForeground = IntPtr.Zero;
    }

    private static void DestroyQuickHost()
    {
        if (_quickHost is not { } host) return;
        foreach (int id in _quickHotkeyReservations) UnregisterHotKey(host._hwnd, id);
        _quickHotkeyReservations.Clear();
        _quickHotkeyId = 0; _registeredQuickChord = default;
        host._quick?.S.Dispose(); host._quick = null; host._cover = null; host._session = null;
        DestroyWindow(host._hwnd);
        host._brush?.Dispose(); host._rt?.Dispose();
        _quickHost = null;
    }

    private void ReleaseQuickKeys()
    {
        int[] keys = _win32KeysDown.ToArray();
        _win32KeysDown.Clear(); _kittyAteChar = false;
        // Exit can race focus loss. Releasing best-effort key state must never prevent hiding
        // the panel or dropping its system caret when the PTY has already closed.
        try
        {
            if (_quick is { } p && !p.S.HasExited && p.S.Emulator.Win32InputMode)
                foreach (int vk in keys)
                    p.S.Write(System.Text.Encoding.UTF8.GetBytes($"\x1b[{vk};{MapVirtualKeyW((uint)vk, 0)};0;0;0;1_"));
        }
        catch (InvalidOperationException) { }
        catch (System.IO.IOException) { }
    }

    private bool QuickActionAllowed(string action)
    {
        if (action.StartsWith("command:", StringComparison.OrdinalIgnoreCase))
            return _commands.Any(c => string.Equals(c.Label, action[8..], StringComparison.OrdinalIgnoreCase)
                && c.Mode is "send" or "detached");
        return action is "quick_terminal" or "close_cover" or "close_pane" or "close_session"
            or "select_all" or "copy_selection" or "paste" or "mark_mode" or "toggle_search"
            or "previous_prompt" or "next_prompt" or "toggle_read_only";
    }
}
