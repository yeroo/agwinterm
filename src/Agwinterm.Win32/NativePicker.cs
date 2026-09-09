using System.Runtime.InteropServices;
using System.Text;
using Agwinterm.Core;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>Owned modeless native controls. Only its owner UI thread calls this object.</summary>
internal sealed class NativePicker : IDisposable
{
    private const string ClassName = "Agwinterm.NativePicker";
    private const uint NcDestroy = 0x82, GetDlgCode = 0x87;
    private const uint LbAdd = 0x180, LbReset = 0x184, LbGetSel = 0x188, LbSetSel = 0x186;
    private static readonly WndProc RootProc = WindowProc;
    private static readonly Dictionary<IntPtr, NativePicker> Roots = new();
    private static readonly Dictionary<IntPtr, NativePicker> Children = new();
    private static NativePicker? _creating;
    private static bool _registered;
    private readonly WndProc _childProc;
    private readonly Dictionary<IntPtr, IntPtr> _original = new();
    private readonly IntPtr _owner;
    private readonly Action<PickOutcome> _finish;
    private readonly Action _activated;
    private readonly PickSelection _selection;
    private IntPtr _hwnd, _edit, _list, _accept, _cancel, _label, _font;
    private bool _ended, _disposing;
    private int _dpi = 96;
    public PendingPick Request { get; }

    [DllImport("user32.dll")] private static extern IntPtr GetNextDlgTabItem(IntPtr dialog, IntPtr control, bool previous);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enabled);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeout, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string family);

    public NativePicker(IntPtr owner, PendingPick request, Action<PickOutcome> finish, Action activated)
    {
        _owner = owner; Request = request; _finish = finish; _activated = activated;
        _selection = new(request.Spec); _childProc = ChildProc;
    }

    public void Show(bool follow)
    {
        if (!_registered)
        {
            var cls = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = RootProc,
                hInstance = GetModuleHandleW(null), hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                hbrBackground = (IntPtr)6 /* COLOR_WINDOW + 1 */, lpszClassName = ClassName };
            if (RegisterClassExW(ref cls) == 0) throw new InvalidOperationException("picker class registration failed");
            _registered = true;
        }
        _dpi = (int)GetDpiForWindow(_owner); if (_dpi <= 0) _dpi = 96;
        GetWindowRect(_owner, out var ownerRect);
        var monitor = MonitorFromRect(ref ownerRect, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) throw new InvalidOperationException("picker monitor unavailable");
        var area = info.rcWork;
        int width = Math.Min(Scale(640), area.right - area.left), height = Math.Min(Scale(420), area.bottom - area.top);
        int x = Math.Clamp(ownerRect.left + (ownerRect.right - ownerRect.left - width) / 2, area.left, area.right - width);
        int y = Math.Clamp(ownerRect.top + Scale(60), area.top, area.bottom - height);
        _creating = this;
        try
        {
            _hwnd = CreateWindowExW(0x10000 /* WS_EX_CONTROLPARENT */, ClassName, "agwinterm picker", WS_CAPTION | WS_SYSMENU | WS_POPUP,
                x, y, width, height, _owner, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        }
        finally { _creating = null; }
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("picker window creation failed");
        _label = Child("STATIC", Request.Spec.Prompt ?? "Select…", 0x80 /* SS_NOPREFIX */, 100);
        _edit = Child("EDIT", "", WS_TABSTOP | ES_AUTOHSCROLL | 0x800000 /* WS_BORDER */, 101);
        _list = Child("LISTBOX", "", WS_TABSTOP | WS_VSCROLL | 1 /* LBS_NOTIFY */ | 0x100 /* LBS_NOINTEGRALHEIGHT */ | 0x800000, 102);
        _accept = Child("BUTTON", "Select", WS_TABSTOP | 1 /* default push button */, 1);
        _cancel = Child("BUTTON", "Cancel", WS_TABSTOP, 2);
        foreach (var child in new[] { _edit, _list, _accept, _cancel })
        {
            Children[child] = this;
            _original[child] = SetWindowLongPtrW(child, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_childProc));
            if (_original[child] == IntPtr.Zero) throw new InvalidOperationException("picker control subclass failed");
        }
        SendMessageW(_edit, 0xc5 /* EM_SETLIMITTEXT */, (IntPtr)PickSpec.MaxFieldLength, IntPtr.Zero);
        UpdateFont(); Layout();
        SetWindowTextW(_edit, Request.Spec.Query ?? "");
        Refilter();
        if (_ended || _hwnd == IntPtr.Zero) throw new InvalidOperationException("picker closed during creation");
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
        if (follow) SetForegroundWindow(_hwnd);
        FocusIfForeground();
    }

    private IntPtr Child(string cls, string text, uint style, int id)
    {
        var child = CreateWindowExW(0, cls, text, WS_CHILD | WS_VISIBLE | style, 0, 0, 1, 1, _hwnd, (IntPtr)id, GetModuleHandleW(null), IntPtr.Zero);
        if (child == IntPtr.Zero) throw new InvalidOperationException("picker control creation failed");
        return child;
    }

    private int Scale(int n) => Math.Max(1, (int)Math.Round(n * _dpi / 96.0));
    private void UpdateFont()
    {
        var font = CreateFontW(-Scale(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        if (font == IntPtr.Zero) throw new InvalidOperationException("picker font creation failed");
        foreach (var h in new[] { _label, _edit, _list, _accept, _cancel }) if (h != IntPtr.Zero) SendMessageW(h, WM_SETFONT, font, (IntPtr)1);
        if (_font != IntPtr.Zero) DeleteObject(_font);
        _font = font;
    }

    private void Layout()
    {
        if (_cancel == IntPtr.Zero) return;
        GetClientRect(_hwnd, out var r);
        int pad = Scale(12), full = Math.Max(1, r.right - 2 * pad), bottom = Math.Max(pad, r.bottom - pad - Scale(30));
        Place(_label, pad, pad, full, Scale(24));
        Place(_edit, pad, pad + Scale(26), full, Scale(28));
        Place(_list, pad, pad + Scale(62), full, Math.Max(1, bottom - pad - Scale(70)));
        Place(_accept, Math.Max(pad, r.right - pad - Scale(204)), bottom, Scale(96), Scale(30));
        Place(_cancel, Math.Max(pad, r.right - pad - Scale(96)), bottom, Scale(96), Scale(30));
    }

    private static void Place(IntPtr h, int x, int y, int w, int height)
        => SetWindowPos(h, IntPtr.Zero, x, y, w, height, SWP_NOZORDER | SWP_NOACTIVATE);

    private void Refilter()
    {
        if (_list == IntPtr.Zero) return;
        var query = new StringBuilder(PickSpec.MaxFieldLength + 1);
        GetWindowTextW(_edit, query, query.Capacity);
        _selection.SetQuery(query.ToString());
        SendMessageW(_list, LbReset, IntPtr.Zero, IntPtr.Zero);
        foreach (int index in _selection.Matches)
        {
            var item = Request.Spec.Items[index];
            string text = item.Label + (string.IsNullOrEmpty(item.Subtitle) ? "" : " — " + item.Subtitle);
            if (SendMessageW(_list, LbAdd, IntPtr.Zero, text).ToInt64() < 0) throw new InvalidOperationException("picker list allocation failed");
        }
        if (_selection.Custom && SendMessageW(_list, LbAdd, IntPtr.Zero, "Use \"" + _selection.Query.Trim() + "\"").ToInt64() < 0)
            throw new InvalidOperationException("picker list allocation failed");
        SendMessageW(_list, LbSetSel, (IntPtr)(_selection.Count == 0 ? -1 : 0), IntPtr.Zero);
        EnableWindow(_accept, _selection.Count > 0);
    }

    private void Choose()
    {
        _selection.Select((int)SendMessageW(_list, LbGetSel, IntPtr.Zero, IntPtr.Zero));
        if (_selection.Choose() is { } outcome) End(outcome);
    }

    public void FocusIfForeground()
    {
        if (_ended || _edit == IntPtr.Zero) return;
        IntPtr front = GetForegroundWindow();
        if (front == _owner || front == _hwnd) SetFocus(_edit);
    }

    public void Forward(uint msg, IntPtr key, IntPtr detail)
    {
        if (!_ended && _edit != IntPtr.Zero) SendMessageW(_edit, msg, key, detail);
    }

    private void End(PickOutcome outcome)
    {
        if (_ended) return;
        _ended = true;
        try { _finish(outcome); } finally { Dispose(); }
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (!Roots.TryGetValue(hwnd, out var picker))
        {
            picker = _creating;
            if (picker is null) return DefWindowProcW(hwnd, msg, w, l);
            picker._hwnd = hwnd; Roots[hwnd] = picker;
        }
        try
        {
            switch (msg)
            {
                case WM_CLOSE: picker.End(new("cancelled")); return IntPtr.Zero;
                case WM_COMMAND:
                    int id = (int)((long)w & 0xffff), code = (int)(((long)w >> 16) & 0xffff);
                    if (id == 101 && code == 0x300) picker.Refilter();
                    else if ((id == 1 && code == 0) || (id == 102 && code == 2)) picker.Choose();
                    else if (id == 2 && code == 0) picker.End(new("cancelled"));
                    return IntPtr.Zero;
                case WM_SIZE: picker.Layout(); break;
                case WM_ACTIVATE:
                    if (((long)w & 0xffff) != 0) { picker._activated(); picker.FocusIfForeground(); }
                    break;
                case WM_SETFOCUS: picker.FocusIfForeground(); return IntPtr.Zero;
                case WM_DPICHANGED:
                    picker._dpi = (int)((long)w & 0xffff);
                    var rect = Marshal.PtrToStructure<RECT>(l);
                    SetWindowPos(hwnd, IntPtr.Zero, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top, SWP_NOZORDER | SWP_NOACTIVATE);
                    picker.UpdateFont(); picker.Layout(); break;
                case NcDestroy:
                    Roots.Remove(hwnd); picker._hwnd = IntPtr.Zero;
                    if (picker._font != IntPtr.Zero) { DeleteObject(picker._font); picker._font = IntPtr.Zero; }
                    picker.End(new("cancelled")); break;
            }
            return DefWindowProcW(hwnd, msg, w, l);
        }
        catch { picker.End(new("cancelled")); return IntPtr.Zero; }
    }

    private IntPtr ChildProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (!_original.TryGetValue(hwnd, out var original)) return DefWindowProcW(hwnd, msg, w, l);
        try
        {
            if (msg == GetDlgCode) return CallWindowProcW(original, hwnd, msg, w, l) | (IntPtr)4; // own Tab/Enter/Escape, including direct messages
            if (msg == WM_KEYDOWN)
            {
                int key = (int)w;
                if (key == VK_ESCAPE) { End(new("cancelled")); return IntPtr.Zero; }
                if (key == VK_RETURN) { if (hwnd == _cancel) End(new("cancelled")); else Choose(); return IntPtr.Zero; }
                if (key == VK_TAB) { SetFocus(GetNextDlgTabItem(_hwnd, hwnd, (GetKeyState(VK_SHIFT) & 0x8000) != 0)); return IntPtr.Zero; }
                if (hwnd == _edit && key == 65 && (GetKeyState(VK_CONTROL) & 0x8000) != 0)
                { SendMessageW(_edit, EM_SETSEL, IntPtr.Zero, (IntPtr)(-1)); return IntPtr.Zero; }
                if ((hwnd == _edit || hwnd == _list) && key is VK_UP or VK_DOWN)
                {
                    _selection.Select((int)SendMessageW(_list, LbGetSel, IntPtr.Zero, IntPtr.Zero));
                    _selection.Move(key == VK_UP ? -1 : 1);
                    SendMessageW(_list, LbSetSel, (IntPtr)_selection.Selected, IntPtr.Zero); return IntPtr.Zero;
                }
            }
            if (msg == WM_CHAR && (int)w is VK_RETURN or VK_ESCAPE or VK_TAB) return IntPtr.Zero;
            if (msg == NcDestroy) { Children.Remove(hwnd); _original.Remove(hwnd); }
            return CallWindowProcW(original, hwnd, msg, w, l);
        }
        catch { End(new("cancelled")); return IntPtr.Zero; }
    }

    public void Dispose()
    {
        if (_disposing) return;
        _disposing = true;
        bool restore = _hwnd != IntPtr.Zero && GetForegroundWindow() == _hwnd;
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        foreach (var child in _original.Keys) Children.Remove(child);
        _original.Clear();
        if (_font != IntPtr.Zero) { DeleteObject(_font); _font = IntPtr.Zero; }
        if (restore && IsWindow(_owner) && GetForegroundWindow() == _owner) SetFocus(_owner);
        GC.KeepAlive(_childProc);
    }
}
