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
    private static UiaProvider? _provider;   // keep a managed ref alive
    private static nint _providerSimple;     // QI'd IRawElementProviderSimple* handed to UIA

    /// <summary>Supplies the current visible terminal text (set by the app) for the provider's Name.</summary>
    internal static Func<string>? GetVisibleText;

    /// <summary>Handle WM_GETOBJECT: return our root UIA provider when UIA asks for it.</summary>
    internal static nint OnGetObject(nint hwnd, nint wParam, nint lParam)
    {
        if ((int)lParam != UiaRootObjectId) return 0;
        if (_providerSimple == 0)
        {
            _provider = new UiaProvider(hwnd);
            nint unk = ComWrappers.GetOrCreateComInterfaceForObject(_provider, CreateComInterfaceFlags.None);
            try
            {
                // Hand UIA a real IRawElementProviderSimple* (not the raw IUnknown* — vtables differ).
                Guid iid = IID_IRawElementProviderSimple;
                if (Marshal.QueryInterface(unk, in iid, out _providerSimple) < 0) _providerSimple = 0;
            }
            finally { Marshal.Release(unk); }
        }
        return _providerSimple != 0 ? UiaReturnRawElementProvider(hwnd, wParam, lParam, _providerSimple) : 0;
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

/// <summary>Minimal server-side UIA element: IRawElementProviderSimple only (control type + name). The
/// GUID is the standard IRawElementProviderSimple IID; method order matches its COM vtable exactly.</summary>
[GeneratedComInterface]
[Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
internal partial interface IRawElementProviderSimple
{
    ProviderOptions GetProviderOptions();
    nint GetPatternProvider(int patternId);
    void GetPropertyValue(int propertyId, nint pRetVal);   // pRetVal is a caller-allocated VARIANT*
    nint GetHostRawElementProvider();                       // IRawElementProviderSimple** (null = none)
}

[GeneratedComClass]
internal partial class UiaProvider : IRawElementProviderSimple
{
    private readonly nint _hwnd;
    public UiaProvider(nint hwnd) => _hwnd = hwnd;

    // Direct (non-marshalled) calls on UIA's thread; our reads are guarded by the session lock.
    public ProviderOptions GetProviderOptions() => ProviderOptions.ServerSideProvider;

    public nint GetPatternProvider(int patternId) => 0; // no control patterns yet (ITextProvider next)

    // Return the HWND host provider so UIA properly *hosts* this element: without it the provider is
    // unparented — Narrator can't anchor its focus rectangle (it lands somewhere random) and
    // notification/property events don't route reliably. UiaHostProviderFromHwnd supplies the window's
    // default bounding rectangle + the event-routing plumbing.
    public nint GetHostRawElementProvider()
        => UiaHostProviderFromHwnd(_hwnd, out nint host) >= 0 ? host : 0;

    [LibraryImport("uiautomationcore.dll")] private static partial int UiaHostProviderFromHwnd(nint hwnd, out nint provider);

    public void GetPropertyValue(int propertyId, nint pRetVal)
    {
        VariantInit(pRetVal);   // default VT_EMPTY (= "not supported", UIA falls back)
        object? val = propertyId switch
        {
            UIA_ControlTypePropertyId => UIA_DocumentControlTypeId,
            UIA_NamePropertyId => SafeText(),
            UIA_IsContentElementPropertyId or UIA_IsControlElementPropertyId or UIA_IsKeyboardFocusablePropertyId => true,
            UIA_LocalizedControlTypePropertyId => "terminal",
            _ => null,
        };
        if (val is not null) Marshal.GetNativeVariantForObject(val, pRetVal);
    }

    private static string SafeText() { try { return Uia.GetVisibleText?.Invoke() ?? "terminal"; } catch { return "terminal"; } }

    [LibraryImport("oleaut32.dll")] private static partial void VariantInit(nint pvarg);

    private const int UIA_ControlTypePropertyId = 30003, UIA_NamePropertyId = 30005,
        UIA_IsControlElementPropertyId = 30016, UIA_IsContentElementPropertyId = 30017,
        UIA_IsKeyboardFocusablePropertyId = 30009, UIA_LocalizedControlTypePropertyId = 30004;
    private const int UIA_DocumentControlTypeId = 50030;
}
