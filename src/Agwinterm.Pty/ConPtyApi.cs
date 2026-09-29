using System.Runtime.InteropServices;

namespace Agwinterm.Pty;

/// <summary>
/// The pseudoconsole functions a session is created with: either the inbox conhost's (kernel32) or
/// those of the conpty.dll shipped beside the exe (NuGet Microsoft.Windows.Console.ConPTY). The
/// shipped ConPTY forwards an app's color, device-attribute and cursor queries to the terminal and
/// its output unchanged, where the inbox conhost answers or rewrites them (#339). Each connection
/// keeps the instance it was created with, so a later <see cref="Select"/> only affects new sessions.
/// </summary>
public sealed unsafe class ConPtyApi
{
    private readonly delegate* unmanaged[Stdcall]<Coord, nint, nint, uint, nint*, int> _create;
    private readonly delegate* unmanaged[Stdcall]<nint, Coord, int> _resize;
    private readonly delegate* unmanaged[Stdcall]<nint, void> _close;

    /// <summary>True for the shipped conpty.dll, false for the inbox conhost.</summary>
    public bool Bundled { get; }

    /// <summary>Which ConPTY this is and, for the inbox one, why it was chosen.</summary>
    public string Description { get; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Coord { public short X, Y; }

    private ConPtyApi(nint create, nint resize, nint close, bool bundled, string description)
    {
        _create = (delegate* unmanaged[Stdcall]<Coord, nint, nint, uint, nint*, int>)create;
        _resize = (delegate* unmanaged[Stdcall]<nint, Coord, int>)resize;
        _close = (delegate* unmanaged[Stdcall]<nint, void>)close;
        Bundled = bundled;
        Description = description;
    }

    private static volatile ConPtyApi? _current;

    /// <summary>The ConPTY new sessions use. The inbox conhost until <see cref="Select"/> runs, so
    /// headless contexts (tests, tools) keep today's behavior unless they opt in.</summary>
    public static ConPtyApi Current => _current ??= Inbox("no selection was made");

    /// <summary>Choose the ConPTY for sessions created from now on: the shipped one in
    /// <paramref name="dir"/> (default: this process's base directory) unless
    /// <paramref name="bundled"/> is false (<c>conpty = inbox</c>) or it is missing, then the inbox
    /// conhost.</summary>
    public static ConPtyApi Select(bool bundled, string? dir = null) => _current = Resolve(bundled, dir ?? AppContext.BaseDirectory);

    internal static ConPtyApi Resolve(bool bundled, string dir)
    {
        if (!bundled) return Inbox("conpty = inbox");
        string? why = TryLoadBundled(dir, out var loaded);
        return loaded ?? Inbox(why!);
    }

    private static ConPtyApi Inbox(string why)
    {
        nint k32 = NativeLibrary.Load("kernel32.dll");
        return new ConPtyApi(NativeLibrary.GetExport(k32, "CreatePseudoConsole"),
            NativeLibrary.GetExport(k32, "ResizePseudoConsole"), NativeLibrary.GetExport(k32, "ClosePseudoConsole"),
            bundled: false, $"inbox conhost ({why})");
    }

    /// <summary>conpty.dll starts the OpenConsole.exe next to it (or in x64\) and silently falls back
    /// to the inbox conhost when there is none, so its absence is checked here, where it can be
    /// reported. Returns why the shipped ConPTY is unusable, or null with <paramref name="api"/> set.</summary>
    private static string? TryLoadBundled(string dir, out ConPtyApi? api)
    {
        api = null;
        string dll = Path.Combine(dir, "conpty.dll");
        if (!File.Exists(dll)) return $"no {dll}";
        if (!File.Exists(Path.Combine(dir, "OpenConsole.exe")) && !File.Exists(Path.Combine(dir, "x64", "OpenConsole.exe")))
            return $"no OpenConsole.exe beside {dll}";
        if (!NativeLibrary.TryLoad(dll, out nint module)) return $"{dll} did not load";
        if (!NativeLibrary.TryGetExport(module, "CreatePseudoConsole", out nint create)
            || !NativeLibrary.TryGetExport(module, "ResizePseudoConsole", out nint resize)
            || !NativeLibrary.TryGetExport(module, "ClosePseudoConsole", out nint close))
            return $"{dll} lacks the pseudoconsole exports";
        api = new ConPtyApi(create, resize, close, bundled: true, $"{dll} (OpenConsole)");
        return null;
    }

    internal int Create(short cols, short rows, nint input, nint output, out nint hPC)
    {
        nint h;
        int hr = _create(new Coord { X = cols, Y = rows }, input, output, 0, &h);
        hPC = h;
        return hr;
    }

    internal void Resize(nint hPC, short cols, short rows) => _resize(hPC, new Coord { X = cols, Y = rows });

    internal void Close(nint hPC) => _close(hPC);
}
