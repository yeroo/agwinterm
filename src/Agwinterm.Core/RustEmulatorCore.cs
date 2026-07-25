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
    public const uint RequiredAbi = 11;

    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
        public uint Cols, Rows, CursorRow, CursorCol, CursorVisible, IsAltScreen, HistoryCount;
        public long ScrollGeneration;
        public uint MouseClick, MouseDrag, MouseMotion, MouseSgr, BracketedPaste;
        public int KeyboardFlags;
        public uint ScrollTop, ScrollBottom, MarkCount, FocusReporting, SynchronizedOutput;
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
    private delegate byte* EmuTakeHostActionsFn(nint p, uint* outLen);
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
    private static EmuTakeHostActionsFn _takeHostActions = null!;
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
            _takeHostActions = Get<EmuTakeHostActionsFn>("agwcore_emu_take_host_actions");
            _freeBuf = Get<FreeBufFn>("agwcore_free_buf");
            _marks = Get<EmuMarksFn>("agwcore_emu_marks");
            _seed = Get<EmuSeedFn>("agwcore_emu_seed_scrollback");
            _placementCount = Get<EmuPlacementCountFn>("agwcore_emu_placement_count");
            _copyPlacements = Get<EmuCopyPlacementsFn>("agwcore_emu_copy_placements");
            _imageMetas = Get<EmuImageMetasFn>("agwcore_emu_image_metas");
            _copyImageData = Get<EmuCopyImageDataFn>("agwcore_emu_copy_image_data");
            _hasImage = Get<EmuHasImageFn>("agwcore_emu_has_image");
            _clearPlacements = Get<EmuClearPlacementsFn>("agwcore_emu_clear_placements");
            _setImageData = Get<EmuSetImageDataFn>("agwcore_emu_set_image_data");
            _placeImage = Get<EmuPlaceImageFn>("agwcore_emu_place_image");
            _placeSixel = Get<EmuPlaceSixelFn>("agwcore_emu_place_sixel");
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

    // ---- images (ABI v8) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct NativePlacement
    {
        public int ImageId;
        public long Row, Col;
        public int Cols, Rows, SrcX, SrcY, SrcW, SrcH;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeImageMeta
    {
        public int Id, Format, Width, Height;
        public uint DataLen;
    }

    private delegate uint EmuPlacementCountFn(nint p);
    private delegate uint EmuCopyPlacementsFn(nint p, NativePlacement* o, uint cap);
    private delegate uint EmuImageMetasFn(nint p, NativeImageMeta* o, uint cap);
    private delegate uint EmuCopyImageDataFn(nint p, int id, byte* o, uint cap);
    private delegate bool EmuHasImageFn(nint p, int id);
    private delegate void EmuClearPlacementsFn(nint p);
    private delegate bool EmuSetImageDataFn(nint p, int id, int format, int width, int height, byte* data, uint len);
    private delegate bool EmuPlaceImageFn(nint p, int id, long row, long col, int cols, int rows, int sx, int sy, int sw, int sh);
    private delegate bool EmuPlaceSixelFn(nint p, byte* data, uint len);
    private static EmuPlacementCountFn _placementCount = null!;
    private static EmuCopyPlacementsFn _copyPlacements = null!;
    private static EmuImageMetasFn _imageMetas = null!;
    private static EmuCopyImageDataFn _copyImageData = null!;
    private static EmuHasImageFn _hasImage = null!;
    private static EmuClearPlacementsFn _clearPlacements = null!;
    private static EmuSetImageDataFn _setImageData = null!;
    private static EmuPlaceImageFn _placeImage = null!;
    private static EmuPlaceSixelFn _placeSixel = null!;

    public NativePlacement[] GetPlacements()
    {
        uint count = _placementCount(_handle);
        if (count == 0) return Array.Empty<NativePlacement>();
        var arr = new NativePlacement[count];
        uint n; fixed (NativePlacement* p = arr) n = _copyPlacements(_handle, p, count);
        return n == count ? arr : arr[..(int)n];
    }

    public NativeImageMeta[] GetImageMetas()
    {
        // Images are few; grow the buffer until the returned count fits inside it.
        for (int cap = 16; ; cap *= 2)
        {
            var arr = new NativeImageMeta[cap];
            uint n; fixed (NativeImageMeta* p = arr) n = _imageMetas(_handle, p, (uint)cap);
            if (n < cap) return n == 0 ? Array.Empty<NativeImageMeta>() : arr[..(int)n];
        }
    }

    public byte[] GetImageData(int id, int dataLen)
    {
        if (dataLen <= 0) return Array.Empty<byte>();
        var buf = new byte[dataLen];
        uint n; fixed (byte* p = buf) n = _copyImageData(_handle, id, p, (uint)dataLen);
        return n == dataLen ? buf : buf[..(int)n];
    }

    public bool HasImage(int id) => _hasImage(_handle, id);
    public void ClearPlacements() => _clearPlacements(_handle);
    public void SetImageData(int id, int format, int width, int height, byte[] data)
    {
        fixed (byte* p = data.Length == 0 ? new byte[1] : data) _setImageData(_handle, id, format, width, height, p, (uint)data.Length);
    }
    public void PlaceImage(int id, int row, int col, int cols, int rows, int sx, int sy, int sw, int sh)
        => _placeImage(_handle, id, row, col, cols, rows, sx, sy, sw, sh);
    public bool PlaceSixel(byte[] data)
    {
        fixed (byte* p = data.Length == 0 ? new byte[1] : data) return _placeSixel(_handle, p, (uint)data.Length);
    }

    private string GetText(uint which)
    {
        uint len;
        byte* buf = _getText(_handle, which, &len);
        if (buf == null) return "";
        try { return System.Text.Encoding.UTF8.GetString(buf, (int)len); }
        finally { _freeBuf(buf, len); }
    }

    /// <summary>Drain the queued host actions (the IHostActions seam) as the raw native blob
    /// and CLEAR the queue. Returns an empty array when nothing was queued this feed (the
    /// common case — the native side returns null then). See RustTerminalCore for the layout.</summary>
    public byte[] TakeHostActions()
    {
        uint len;
        byte* buf = _takeHostActions(_handle, &len);
        if (buf == null) return Array.Empty<byte>();
        try { var arr = new byte[len]; Marshal.Copy((nint)buf, arr, 0, (int)len); return arr; }
        finally { _freeBuf(buf, len); }
    }

    public void Dispose()
    {
        if (_handle != 0) { _free(_handle); _handle = 0; }
        GC.SuppressFinalize(this);
    }

    ~RustEmulatorCore() { if (_handle != 0) _free(_handle); }
}
