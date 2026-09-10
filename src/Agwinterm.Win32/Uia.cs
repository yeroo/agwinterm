using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Agwinterm.Win32;

/// <summary>
/// UIA accessibility (T2-14): a minimal UI Automation provider so screen readers (Narrator, NVDA) can
/// see agwinterm as a document control and read the visible terminal content. Exposed via WM_GETOBJECT.
/// Raw source-generated COM — no WPF/UIAutomationProvider dependency. Includes the fragment tree,
/// invoke pattern and ITextProvider/ITextRangeProvider for line/caret navigation.
/// </summary>
internal partial class Uia : IDisposable
{
    private static readonly StrategyBasedComWrappers ComWrappers = new();
    // One context per Program window. Retained COM fragments/ranges keep this context, never a
    // process-wide "current window". Closing it severs callbacks and retires its root references.
    private readonly object _gate = new();
    private static int _nextIdentity;
    internal int Identity { get; } = Interlocked.Increment(ref _nextIdentity);
    private volatile bool _closed;
    internal bool IsClosed => _closed;
    private UiaRoot? _root;
    private nint _providerSimple, _fragmentRoot;
    internal Func<string>? GetVisibleText;

    internal void EnsureAlive()
    {
        if (_closed) throw new COMException("The accessibility window is closed.", unchecked((int)0x80040201));
    }

    internal string VisibleText()
    {
        EnsureAlive();
        string text = GetVisibleText?.Invoke() ?? "terminal";
        EnsureAlive();
        return text;
    }

    /// <summary>Handle WM_GETOBJECT for this window only.</summary>
    internal nint OnGetObject(nint hwnd, nint wParam, nint lParam)
    {
        if ((int)lParam != UiaRootObjectId) return 0;
        nint provider;
        lock (_gate)
        {
            if (_closed) return 0;
            if (_providerSimple == 0)
            {
                _treeHwnd = hwnd;
                _root = new UiaRoot(this, hwnd);
                _providerSimple = AsInterface(_root, IID_IRawElementProviderSimple);
                if (_fragmentRoot == 0) _fragmentRoot = AsInterface(_root, IID_IRawElementProviderFragmentRoot);
            }
            provider = _providerSimple;
            if (provider != 0) Marshal.AddRef(provider);
        }
        // UIA can reenter the provider. Never hold _gate across a UIA call or an app callback.
        try { return provider != 0 ? UiaReturnRawElementProvider(hwnd, wParam, lParam, provider) : 0; }
        finally { if (provider != 0) Marshal.Release(provider); }
    }

    internal nint FragmentRootPtr()
    {
        lock (_gate)
        {
            EnsureAlive();
            if (_fragmentRoot != 0) Marshal.AddRef(_fragmentRoot);
            return _fragmentRoot;
        }
    }

    internal nint RootProvider()
    {
        EnsureAlive();
        return AsInterface(new UiaTerminal(this), IID_IRawElementProviderSimple);
    }

    private nint BorrowProvider()
    {
        lock (_gate)
        {
            if (_closed || _providerSimple == 0) return 0;
            Marshal.AddRef(_providerSimple);
            return _providerSimple;
        }
    }

    public void Dispose()
    {
        nint provider, root;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            GetVisibleText = null; GetTree = null; GetTextSnapshot = null;
            OnSetFocus = null; OnInvoke = null;
            provider = _providerSimple; root = _fragmentRoot;
            _providerSimple = _fragmentRoot = _treeHwnd = 0;
            _root = null;
        }
        if (provider != 0) Marshal.Release(provider);
        if (root != 0) Marshal.Release(root);
    }

    private static readonly Guid IID_IRawElementProviderSimple = new("d6dd68d1-86fd-4332-8666-9abedea2d24c");
    private const int UiaRootObjectId = -25;
    [LibraryImport("uiautomationcore.dll")] private static partial nint UiaReturnRawElementProvider(nint hwnd, nint wParam, nint lParam, nint provider);

    /// <summary>True while a UIA client (Narrator, NVDA, …) is subscribed — gates all announcement work.</summary>
    internal static bool ClientsListening
    {
        get { try { return UiaClientsAreListening(); } catch { return false; } }
    }

    /// <summary>Push text to the active screen reader via a UIA notification event (new terminal output,
    /// settings interactions). Cheap alternative to a full ITextProvider: the reader simply speaks the
    /// string. No-op until the provider exists (first WM_GETOBJECT) or when nothing is listening.</summary>
    internal void Announce(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        nint provider = BorrowProvider();
        if (provider == 0) return;
        try
        {
            // NotificationKind_Other = 4, NotificationProcessing_All = 2 (queue, don't drop).
            UiaRaiseNotificationEvent(provider, 4, 2, text, "agwinterm-announce");
        }
        catch { }
        finally { Marshal.Release(provider); }
    }

    [LibraryImport("uiautomationcore.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UiaClientsAreListening();

    [LibraryImport("uiautomationcore.dll")]
    private static partial int UiaRaiseNotificationEvent(nint provider, int kind, int processing,
        [MarshalUsing(typeof(BStrStringMarshaller))] string displayString,
        [MarshalUsing(typeof(BStrStringMarshaller))] string activityId);
}

[Flags]
internal enum ProviderOptions { ClientSideProvider = 0x1, ServerSideProvider = 0x2, NonClientAreaProvider = 0x4, OverrideProvider = 0x8, ProviderOwnsSetFocus = 0x10, UseComThreading = 0x20 }

/// <summary>Server-side UIA element base (control type + name + patterns). GUID is the standard
/// IRawElementProviderSimple IID; method order matches its COM vtable exactly.</summary>
[GeneratedComInterface]
[Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
internal partial interface IRawElementProviderSimple
{
    ProviderOptions GetProviderOptions();
    nint GetPatternProvider(int patternId);
    void GetPropertyValue(int propertyId, nint pRetVal);   // pRetVal is a caller-allocated VARIANT*
    nint GetHostRawElementProvider();                       // IRawElementProviderSimple** (null = none)
}
