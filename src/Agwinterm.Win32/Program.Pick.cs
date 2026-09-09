using Agwinterm.Core;
using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program : IPickHost
{
    private static readonly PickRegistry Picks = new();
    private NativePicker? _nativePick;

    public string OpenPick(PickSpec spec, string? window, bool follow)
    {
        if (window == "quick") throw new InvalidOperationException("quick terminal has no picker");
        var owner = ResolveOpen(window) ?? throw new InvalidOperationException("picker window not found");
        return owner.InvokeOnUiQueued(() => owner.OpenPickCore(spec, follow));
    }

    private string OpenPickCore(PickSpec spec, bool follow)
    {
        if (_nativePick is not null) throw new InvalidOperationException("pick already pending");
        if (_setOpen || _editing is not null || _menuHwnd != IntPtr.Zero || _dashboardOpen || _dragging)
            throw new InvalidOperationException("another modal UI operation owns this window");
        var request = Picks.Open(Id, spec) ?? throw new InvalidOperationException("pick already pending");
        try
        {
            ClosePalette(); CancelLeader(); ExitChromeFocus(announce: false); DismissHoverTip();
            _nativePick = new NativePicker(_hwnd, request, outcome =>
            {
                Picks.Resolve(request.Id, outcome);
                var ui = _nativePick;
                if (ui?.Request.Id == request.Id) { _nativePick = null; ui.Dispose(); }
                RequestRedraw();
            }, () => { Frontmost = this; _frontmostId = Id; });
            _nativePick.Show(follow);
            RequestRedraw();
            return request.Id;
        }
        catch
        {
            Picks.Resolve(request.Id, new("cancelled"));
            var ui = _nativePick; _nativePick = null; ui?.Dispose();
            throw;
        }
    }

    private static LocatedPick LocatePick(string id, string? window)
    {
        var found = Picks.Find(id) ?? throw new InvalidOperationException("unknown pick: " + id);
        if (window is not null)
        {
            if (window == "quick" || ResolveMeta(window) is not { } target || target.Id != found.Window)
                throw new InvalidOperationException("pick does not belong to the selected window");
        }
        return found;
    }

    public PickOutcome ReadPick(string id, string? window) => LocatePick(id, window).Outcome;

    public void CancelPick(string id, string? window)
    {
        var found = LocatePick(id, window);
        if (found.Outcome.Result != "pending") return;
        var owner = ResolveOpen(found.Window);
        if (owner is null) return; // WM_DESTROY settled and retained it after the lookup
        owner.InvokeOnUiQueued(() =>
        {
            if (Picks.Resolve(id, new("cancelled")) && owner._nativePick?.Request.Id == id)
            { var ui = owner._nativePick; owner._nativePick = null; ui.Dispose(); owner.RequestRedraw(); }
            return true;
        });
    }

    private void CloseWindowPicker()
    {
        Picks.CloseWindow(Id);
        var ui = _nativePick; _nativePick = null; ui?.Dispose();
    }

    private bool PickerMessage(uint msg, IntPtr w, IntPtr l)
    {
        if (_nativePick is not { } picker) return false;
        if (msg is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP or WM_CHAR or WM_SYSCHAR)
        { picker.Forward(msg, w, l); return true; }
        if (msg is WM_SETFOCUS or WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK or WM_RBUTTONDOWN or WM_RBUTTONUP
            or WM_MBUTTONDOWN or WM_MBUTTONUP or WM_MOUSEWHEEL or WM_CONTEXTMENU)
        { picker.FocusIfForeground(); return true; }
        return false;
    }
}
