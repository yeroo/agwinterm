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

    private static Program EnsureQuickHost()
    {
        if (_quickHost is not null) return _quickHost;
        var host = new Program { Id = "quick", WinName = "Quick terminal", _isQuickWindow = true, _sidebarW = 0 };
        host.Boot(_hInstance);
        return _quickHost = host;
    }

    // Acquire the replacement BEFORE releasing the current chord. Two alternating IDs avoid
    // RegisterHotKey's same-ID accumulation rule; conflicts cannot silently unbind the old key.
    private static string? SetQuickHotkey(string value)
    {
        if (!QuickHotkey.TryParse(value, out var chord))
            return "error: quick-terminal-hotkey requires Ctrl/Alt, optional Shift, and a letter/digit, F1-F11 or backtick";
        if (chord == _registeredQuickChord) return null;
        var host = EnsureQuickHost();
        int next = _quickHotkeyId == 0x514 ? 0x515 : 0x514;
        if (chord.Key != 0 && !RegisterHotKey(host._hwnd, next, chord.Modifiers | 0x4000, chord.Key))
            return $"error: quick-terminal-hotkey unavailable (Win32 {Marshal.GetLastWin32Error()}); previous binding retained";
        if (_quickHotkeyId != 0 && !UnregisterHotKey(host._hwnd, _quickHotkeyId))
        {
            if (chord.Key != 0) UnregisterHotKey(host._hwnd, next);
            return "error: could not release previous quick-terminal-hotkey; config unchanged";
        }
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
        if (pin) host._quickPinned = true; // an API show also pins an already-visible human summon
        if (host._quickVisible) return;
        host._quickAutoHiddenAt = 0;
        host._quickPreviousForeground = GetForegroundWindow();
        host.PositionQuick();
        if (host._quick is { S.HasExited: true }) { host._quick.S.Dispose(); host._quick = null; }
        host._quick ??= host.CreatePane("quick:" + Guid.NewGuid().ToString("N")[..6],
            new Workspace { Id = "", Name = "" }, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), (float)_config.FontSize);
        host._cover = host._quick; host._coverKind = 2; host.SyncSession(); host.RegridCover();
        host._quickVisible = true;
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
        if (_quickHotkeyId != 0) UnregisterHotKey(host._hwnd, _quickHotkeyId);
        _quickHotkeyId = 0; _registeredQuickChord = default;
        host._quick?.S.Dispose(); host._quick = null; host._cover = null; host._session = null;
        DestroyWindow(host._hwnd);
        host._brush?.Dispose(); host._rt?.Dispose();
        _quickHost = null;
    }
}
