using System.Runtime.InteropServices;

namespace Agwinterm.Core;

/// <summary>
/// Adapter over the Rust emulator core (native/agwinterm-core). Wraps the opaque
/// Terminal handle and exposes the surface a pane needs: Feed, Resize, bulk grid
/// snapshots (one interop copy per frame, mirroring the renderer's existing
/// snapshot-under-lock pattern), history rows, and the scalar/mode state.
///
/// Loading is explicit and fail-loud: <see cref="TryLoad"/> probes the dll and
/// verifies the ABI handshake (same philosophy as the pty-host protocol) —
/// a mismatch refuses to load rather than half-working. The eventual
/// `emulator-core = rust` config path calls this and falls back to the managed
/// emulator when unavailable.
/// </summary>
public sealed unsafe class RustEmulatorCore : IDisposable
{
    public const uint RequiredAbi = 7;

    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
        public uint Cols, Rows, CursorRow, CursorCol, CursorVisible, IsAltScreen, HistoryCount;
        public long ScrollGeneration;
        public uint MouseClick, MouseDrag, MouseMotion, MouseSgr, BracketedPaste;
        public int KeyboardFlags;
        public uint ScrollTop, ScrollBottom, MarkCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeCell
    {
        public int Rune;
        public uint Fg, Bg, Attrs, Width;
        public uint FgKind, FgIndex, FgRgb;
        public uint BgKind, BgIndex, BgRgb;

        /// <summary>Convert to the managed Cell (colors unpacked from 0x00RRGGBB).</summary>
        public Cell ToCell() => new(
            Rune, Unpack(Fg), Unpack(Bg), (CellAttributes)Attrs, (byte)Width,
            new ColorSpec((ColorSpecKind)FgKind, (byte)FgIndex, Unpack(FgRgb)),
            new ColorSpec((ColorSpecKind)BgKind, (byte)BgIndex, Unpack(BgRgb)));

        private static Color Unpack(uint v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private delegate uint AbiVersionFn();
    private delegate nint EmuNewFn(uint cols, uint rows);
    private delegate void EmuFreeFn(nint p);
    private delegate bool EmuFeedFn(nint p, byte* bytes, uint len);
    private delegate bool EmuResizeFn(nint p, uint cols, uint rows);
    private delegate bool EmuInfoFn(nint p, Info* info);
    private delegate bool EmuCopyGridFn(nint p, NativeCell* cells, uint cap);
    private delegate bool EmuCopyHistoryRowFn(nint p, uint row, NativeCell* cells, uint cap);
    private delegate byte* EmuGetTextFn(nint p, uint which, uint* outLen);
    private delegate void FreeBufFn(byte* ptr, uint len);

    private static nint _lib;
    private static EmuNewFn _new = null!;
    private static EmuFreeFn _free = null!;
    private static EmuFeedFn _feed = null!;
    private static EmuResizeFn _resize = null!;
    private static EmuInfoFn _info = null!;
    private static EmuCopyGridFn _copyGrid = null!;
    private static EmuCopyHistoryRowFn _copyHistory = null!;
    private static EmuGetTextFn _getText = null!;
    private static FreeBufFn _freeBuf = null!;

    /// <summary>Load the native core from <paramref name="dllPath"/> and verify the ABI.
    /// Idempotent; returns false (with a reason) when missing or mismatched.</summary>
    public static bool TryLoad(string dllPath, out string? error)
    {
        error = null;
        if (_lib != 0) return true;
        if (!File.Exists(dllPath)) { error = "native core not found: " + dllPath; return false; }
        if (!NativeLibrary.TryLoad(dllPath, out nint lib)) { error = "failed to load " + dllPath; return false; }
        try
        {
            T Get<T>(string name) where T : Delegate
                => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
            uint abi = Get<AbiVersionFn>("agwcore_abi_version")();
            if (abi != RequiredAbi)
            {
                error = $"native core ABI {abi} != required {RequiredAbi}";
                NativeLibrary.Free(lib);
                return false;
            }
            _new = Get<EmuNewFn>("agwcore_emu_new");
            _free = Get<EmuFreeFn>("agwcore_emu_free");
            _feed = Get<EmuFeedFn>("agwcore_emu_feed");
            _resize = Get<EmuResizeFn>("agwcore_emu_resize");
            _info = Get<EmuInfoFn>("agwcore_emu_info");
            _copyGrid = Get<EmuCopyGridFn>("agwcore_emu_copy_grid");
            _copyHistory = Get<EmuCopyHistoryRowFn>("agwcore_emu_copy_history_row");
            _getText = Get<EmuGetTextFn>("agwcore_emu_get_text");
            _freeBuf = Get<FreeBufFn>("agwcore_free_buf");
            _marks = Get<EmuMarksFn>("agwcore_emu_marks");
            _seed = Get<EmuSeedFn>("agwcore_emu_seed_scrollback");
            _lib = lib;
            return true;
        }
        catch (Exception ex)
        {
            error = "native core export missing: " + ex.Message;
            NativeLibrary.Free(lib);
            return false;
        }
    }

    public static bool Loaded => _lib != 0;

    private nint _handle;

    public RustEmulatorCore(int cols, int rows)
    {
        if (!Loaded) throw new InvalidOperationException("RustEmulatorCore.TryLoad first");
        _handle = _new((uint)cols, (uint)rows);
        if (_handle == 0) throw new ArgumentOutOfRangeException(nameof(cols));
    }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        fixed (byte* p = bytes) _feed(_handle, p, (uint)bytes.Length);
    }

    public void Resize(int cols, int rows)
    {
        if (cols > 0 && rows > 0) _resize(_handle, (uint)cols, (uint)rows);
    }

    public Info GetInfo()
    {
        Info i;
        _info(_handle, &i);
        return i;
    }

    /// <summary>Bulk-copy the visible grid into <paramref name="cells"/> (row-major,
    /// cols*rows). The renderer's one-interop-per-frame snapshot.</summary>
    public bool CopyGrid(NativeCell[] cells)
    {
        fixed (NativeCell* p = cells) return _copyGrid(_handle, p, (uint)cells.Length);
    }

    public bool CopyHistoryRow(int row, NativeCell[] cells)
    {
        fixed (NativeCell* p = cells) return _copyHistory(_handle, (uint)row, p, (uint)cells.Length);
    }

    public string Title => GetText(0);
    public string Cwd => GetText(1);
    public string DumpModes() => GetText(2);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeMark
    {
        public long PromptLine, CommandLine, OutputLine, EndLine;
        public uint HasExit;
        public int ExitCode;
    }

    private delegate uint EmuMarksFn(nint p, NativeMark* marks, uint cap);
    private delegate bool EmuSeedFn(nint p, byte* text, uint len);
    private static EmuMarksFn _marks = null!;
    private static EmuSeedFn _seed = null!;

    /// <summary>All FTCS marks, converted to the managed mark type.</summary>
    public TerminalEmulator.ShellMark[] GetMarks()
    {
        uint count = GetInfo().MarkCount;
        if (count == 0) return Array.Empty<TerminalEmulator.ShellMark>();
        var native = new NativeMark[count];
        uint n;
        fixed (NativeMark* p = native) n = _marks(_handle, p, count);
        var result = new TerminalEmulator.ShellMark[n];
        for (int i = 0; i < n; i++)
            result[i] = new TerminalEmulator.ShellMark
            {
                PromptLine = (int)native[i].PromptLine,
                CommandLine = (int)native[i].CommandLine,
                OutputLine = (int)native[i].OutputLine,
                EndLine = (int)native[i].EndLine,
                ExitCode = native[i].HasExit != 0 ? native[i].ExitCode : null,
            };
        return result;
    }

    public void SeedScrollback(string joinedLines)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(joinedLines);
        fixed (byte* p = bytes.Length == 0 ? new byte[1] : bytes) _seed(_handle, p, (uint)bytes.Length);
    }

    private string GetText(uint which)
    {
        uint len;
        byte* buf = _getText(_handle, which, &len);
        if (buf == null) return "";
        try { return System.Text.Encoding.UTF8.GetString(buf, (int)len); }
        finally { _freeBuf(buf, len); }
    }

    public void Dispose()
    {
        if (_handle != 0) { _free(_handle); _handle = 0; }
        GC.SuppressFinalize(this);
    }

    ~RustEmulatorCore() { if (_handle != 0) _free(_handle); }
}
