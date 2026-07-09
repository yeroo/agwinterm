using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Agwinterm.Win32;

/// <summary>
/// UIA accessibility (T2-14): a minimal UI Automation provider so screen readers (Narrator, NVDA) can
/// see agwinterm as a document control and read the visible terminal content. Exposed via WM_GETOBJECT.
/// Raw source-generated COM — NO WPF/UIAutomationProvider dependency (which would bloat the exe). A full
/// ITextProvider (line/caret navigation) is the follow-on stage.
/// </summary>
internal static partial class Uia
{
    private static readonly StrategyBasedComWrappers ComWrappers = new();
    private static UiaRoot? _root;           // keep the singleton root alive
    private static nint _providerSimple;     // QI'd IRawElementProviderSimple* of the root (handed to UIA)
    private static nint _fragmentRoot;       // QI'd IRawElementProviderFragmentRoot* of the root

    /// <summary>Supplies the current visible terminal text (set by the app) for the terminal element's Name.</summary>
    internal static Func<string>? GetVisibleText;

    /// <summary>Handle WM_GETOBJECT: return our root UIA fragment when UIA asks for it.</summary>
    internal static nint OnGetObject(nint hwnd, nint wParam, nint lParam)
    {
        if ((int)lParam != UiaRootObjectId) return 0;
        if (_providerSimple == 0)
        {
            _treeHwnd = hwnd;
            _root = new UiaRoot(hwnd);
            nint unk = ComWrappers.GetOrCreateComInterfaceForObject(_root, CreateComInterfaceFlags.None);
            try
            {
                // Hand UIA a real IRawElementProviderSimple* (not the raw IUnknown* — vtables differ).
                Guid iid = IID_IRawElementProviderSimple;
                if (Marshal.QueryInterface(unk, in iid, out _providerSimple) < 0) _providerSimple = 0;
                Guid fr = IID_IRawElementProviderFragmentRoot;
                if (Marshal.QueryInterface(unk, in fr, out _fragmentRoot) < 0) _fragmentRoot = 0;
            }
            finally { Marshal.Release(unk); }
        }
        return _providerSimple != 0 ? UiaReturnRawElementProvider(hwnd, wParam, lParam, _providerSimple) : 0;
    }

    /// <summary>The fragment root (IRawElementProviderFragmentRoot*), AddRef'd — each fragment's FragmentRoot.</summary>
    internal static nint FragmentRootPtr()
    {
        if (_fragmentRoot != 0) Marshal.AddRef(_fragmentRoot);
        return _fragmentRoot;
    }

    /// <summary>The terminal element (IRawElementProviderSimple*), AddRef'd — text ranges' GetEnclosingElement.</summary>
    internal static nint RootProvider() => AsInterface(new UiaTerminal(_treeHwnd), IID_IRawElementProviderSimple);

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
    internal static void Announce(string text)
    {
        if (_providerSimple == 0 || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            // NotificationKind_Other = 4, NotificationProcessing_All = 2 (queue, don't drop).
            UiaRaiseNotificationEvent(_providerSimple, 4, 2, text, "agwinterm-announce");
        }
        catch { }
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
