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
        var owner = ResolveOpen(PickOwnerId(window, true) ?? throw new InvalidOperationException("picker window not found or ambiguous"))
            ?? throw new InvalidOperationException("picker window not found");
        var call = new PickOpenCall<string>();
        if (!owner.Post(() => call.Run(() => owner.OpenPickCore(spec, follow, () => call.Pending), owner.AbortPick)))
            throw new InvalidOperationException(owner.NothingApplied());
        try { return call.Task.WaitAsync(TimeSpan.FromSeconds(10), owner._uiGone.Token).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (!call.Withdraw()) return call.Task.GetAwaiter().GetResult(); // publication won the deadline race
            throw new InvalidOperationException("picker open withdrawn; any in-flight native construction will be discarded", ex);
        }
    }

    private static string? PickOwnerId(string? selector, bool openOnly)
    {
        lock (_windowIndex)
            return PickWindowSelector.Resolve(selector, _windowIndex.Where(m => !openOnly || m.IsOpen).Select(m => m.Id), _frontmostId ?? "");
    }

    private void AbortPick(string id)
    {
        Picks.Abort(id);
        if (_nativePick?.Request.Id == id) { var ui = _nativePick; _nativePick = null; ui.Dispose(); RequestRedraw(); }
    }

    private string OpenPickCore(PickSpec spec, bool follow, Func<bool> wanted)
    {
        if (!wanted()) throw new InvalidOperationException("picker open was withdrawn");
        if (_nativePick is not null) throw new InvalidOperationException("pick already pending");
        if (_setOpen || _editing is not null || _menuHwnd != IntPtr.Zero || _dashboardOpen || _dragging || GetCapture() != IntPtr.Zero)
            throw new InvalidOperationException("another modal UI operation owns this window");
        var request = Picks.Open(Id, spec) ?? throw new InvalidOperationException("pick already pending");
        bool initialized = false;
        try
        {
            ClosePalette(); CancelLeader(); ExitChromeFocus(announce: false); DismissHoverTip();
            _nativePick = new NativePicker(_hwnd, request, outcome =>
            {
                if (initialized) Picks.Resolve(request.Id, outcome); else Picks.Abort(request.Id);
                var ui = _nativePick;
                if (ui?.Request.Id == request.Id) { _nativePick = null; ui.Dispose(); }
                RequestRedraw();
            }, () => { Frontmost = this; _frontmostId = Id; }, Perf);
            if (!wanted()) throw new InvalidOperationException("picker open was withdrawn");
            _nativePick.Show(follow);
            initialized = true;
            RequestRedraw();
            return request.Id;
        }
        catch
        {
            AbortPick(request.Id);
            throw;
        }
    }

    private static LocatedPick LocatePick(string id, string? window)
    {
        var found = Picks.Find(id) ?? throw new InvalidOperationException("unknown pick: " + id);
        if (window is not null)
        {
            if (window == "quick" || PickOwnerId(window, false) != found.Window)
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
        try { owner.InvokeOnUiQueued(() =>
        {
            if (Picks.Resolve(id, new("cancelled")) && owner._nativePick?.Request.Id == id)
            { var ui = owner._nativePick; owner._nativePick = null; ui.Dispose(); owner.RequestRedraw(); }
            return true;
        }); }
        catch when (owner._uiGone.IsCancellationRequested && Picks.Find(id)?.Outcome.Result is "picked" or "custom" or "cancelled")
        { /* Owner shutdown already settled this exact retained picker. */ }
    }

    private void CloseWindowPicker()
    {
        Picks.CloseWindow(Id);
        var ui = _nativePick; _nativePick = null; ui?.Dispose();
    }

    private bool PickerMessage(uint msg, IntPtr w, IntPtr l)
    {
        if (_nativePick is not { } picker) return false;
        switch (PickInputPolicy.Route(msg))
        {
            case PickInputRoute.Ignore: return true;
            case PickInputRoute.Drop: DragFinish(w); return true;
            case PickInputRoute.Keyboard: picker.Forward(msg, w, l); return true;
            case PickInputRoute.Focus: picker.FocusIfForeground(); return true;
            default: return false;
        }
    }
}
