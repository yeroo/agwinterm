using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Agwinterm.Win32;

/// <summary>
/// Stage B of Default Terminal delegation (T2-13): the out-of-process COM server that Windows' console
/// host (conhost/OpenConsole) hands a console session off to. When agwinterm is the registered default
/// terminal, launching any console app makes conhost <c>CoCreateInstance</c> our CLSID and call
/// <see cref="ITerminalHandoff3.EstablishPtyHandoff"/>; we create the PTY pipes, keep our ends, and hand
/// them to the app via <see cref="OnHandoff"/> (→ <c>TerminalSession.Attach</c>).
///
/// Requires OpenConsoleProxy.dll registered for the interface IIDs (the system_handle marshalling of the
/// pipe/process handles) — a Stage C packaging task. Modern conhost only ever calls ITerminalHandoff3.
/// </summary>
internal static partial class DefTerm
{
    /// <summary>agwinterm's own DelegationTerminal CLSID (registered under CLSID\{..}\LocalServer32).</summary>
    internal static readonly Guid Clsid = new("AE4D2C1F-6B3A-4E8D-9C7F-1A2B3C4D5E6F");

    /// <summary>Raised (on a COM thread) when conhost hands off a console session. The app wires this to
    /// create a session and call <c>TerminalSession.Attach</c> on the UI thread.</summary>
    internal static Action<HandoffArgs>? OnHandoff;

    private static readonly StrategyBasedComWrappers ComWrappers = new();
    private static uint _cookie;

    /// <summary>Register the class factory so a running agwinterm receives handoffs (REGCLS_MULTIPLEUSE).
    /// Call once at startup on the instance that should own default-terminal handoffs.</summary>
    internal static void RegisterServer()
    {
        if (_cookie != 0) return;
        var factory = new HandoffFactory();
        nint unk = ComWrappers.GetOrCreateComInterfaceForObject(factory, CreateComInterfaceFlags.None);
        try
        {
            Guid clsid = Clsid;
            int hr = CoRegisterClassObject(in clsid, unk, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out _cookie);
            if (hr < 0) _cookie = 0;
        }
        finally { Marshal.Release(unk); }
    }

    internal static void RevokeServer()
    {
        if (_cookie != 0) { CoRevokeClassObject(_cookie); _cookie = 0; }
    }

    internal static nint MakeComObject(object o) => ComWrappers.GetOrCreateComInterfaceForObject(o, CreateComInterfaceFlags.None);

    private const int CLSCTX_LOCAL_SERVER = 0x4;
    private const int REGCLS_MULTIPLEUSE = 0x1;

    [LibraryImport("ole32.dll")] private static partial int CoRegisterClassObject(in Guid clsid, nint unk, int ctx, int flags, out uint cookie);
    [LibraryImport("ole32.dll")] private static partial int CoRevokeClassObject(uint cookie);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial int GetProcessId(nint process);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreatePipe(out nint read, out nint write, nint sa, int size);
}

/// <summary>Handles/PID delivered to the app when a console session is handed off. <c>conOut</c> is where
/// we read the console's VT output; <c>conIn</c> is where we write user input; <c>signal</c> is the
/// ConPTY signal pipe (resize); <c>client</c> is the console app process (for exit detection).</summary>
internal readonly record struct HandoffArgs(nint ConOut, nint ConIn, nint Signal, nint Client, int ClientPid, nint StartupInfo);

/// <summary>MIDL <c>TERMINAL_STARTUP_INFO</c> — title/icon/show-window for the handed-off session.
/// BSTR fields are just pointers here; we only peek at the title.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TerminalStartupInfo
{
    public nint pszTitle, pszIconPath;
    public int iconIndex;
    public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public ushort wShowWindow;
}

/// <summary>The current default-terminal handoff contract. Only method after IUnknown; the terminal
/// creates the in/out pipes and returns them ([out] HANDLE*). GUID matches WT's ITerminalHandoff3.</summary>
[GeneratedComInterface]
[Guid("6F23DA90-15C5-4203-9DB0-64E73F1B1B00")]
internal partial interface ITerminalHandoff3
{
    void EstablishPtyHandoff(out nint @in, out nint @out, nint signal, nint reference, nint server, nint client, nint startupInfo);
}

[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, in Guid riid, out nint ppv);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

[GeneratedComClass]
internal partial class TerminalHandoffImpl : ITerminalHandoff3
{
    public void EstablishPtyHandoff(out nint @in, out nint @out, nint signal, nint reference, nint server, nint client, nint startupInfo)
    {
        // Terminal creates the PTY pipes (v3). conhost READS user input from *in and WRITES VT to *out;
        // we keep the opposite ends.
        DefTerm.CreatePipe(out nint inRead, out nint inWrite, 0, 0);   // *in = inRead (conhost reads); we write inWrite
        DefTerm.CreatePipe(out nint outRead, out nint outWrite, 0, 0); // *out = outWrite (conhost writes); we read outRead
        @in = inRead;
        @out = outWrite;
        int pid = client != 0 ? DefTerm.GetProcessId(client) : 0;
        DefTerm.OnHandoff?.Invoke(new HandoffArgs(outRead, inWrite, signal, client, pid, startupInfo));
    }
}

[GeneratedComClass]
internal partial class HandoffFactory : IClassFactory
{
    public int CreateInstance(nint outer, in Guid riid, out nint ppv)
    {
        ppv = 0;
        if (outer != 0) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
        nint unk = DefTerm.MakeComObject(new TerminalHandoffImpl());
        try { Guid iid = riid; return Marshal.QueryInterface(unk, in iid, out ppv); }
        finally { Marshal.Release(unk); }
    }

    public int LockServer(bool fLock) => 0; // S_OK
}
