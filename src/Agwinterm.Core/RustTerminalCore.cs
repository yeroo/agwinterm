using System.IO;
using System.Text;

namespace Agwinterm.Core;

/// <summary>
/// The Rust emulator behind the <see cref="ITerminalCore"/> seam (`emulator-core = rust`,
/// EXPERIMENTAL). The Rust core is authoritative; a managed mirror (grid + history +
/// scalars) is refreshed after every Feed/Resize with ONE bulk interop copy, so all
/// reads (renderer snapshots, selection, dumps) stay lock-cheap managed accesses with
/// unchanged semantics. All calls run under the session lock per the ISession contract.
///
/// Images (kitty/sixel) are surfaced (ABI v8) via <see cref="Images"/> / <see cref="Placements"/>,
/// and host actions are wired (ABI v9): the Rust core queues them during feed and this adapter
/// drains them after each Sync and forwards to <see cref="Host"/> (OSC 52 clipboard, OSC 9/777
/// notifications, OSC 9;4 progress, kitty keyboard-query replies, and the Unhandled debug tap) —
/// so `emulator-core = rust` is now at parity with the managed core for the host seam.
/// </summary>
public sealed class RustTerminalCore : ITerminalCore, IDisposable
{
    private readonly RustEmulatorCore _rust;
    private readonly ScreenBuffer _mirror;
    private readonly List<Cell[]> _histMirror = new();
    private RustEmulatorCore.NativeCell[] _gridBuf;
    private RustEmulatorCore.NativeCell[] _rowBuf;
    private RustEmulatorCore.Info _info;
    private TerminalEmulator.ShellMark[] _marks = Array.Empty<TerminalEmulator.ShellMark>();
    private string _title = "", _cwd = "";

    public RustTerminalCore(int cols, int rows)
    {
        _rust = new RustEmulatorCore(cols, rows);
        _mirror = new ScreenBuffer(cols, rows);
        _gridBuf = new RustEmulatorCore.NativeCell[cols * rows];
        _rowBuf = new RustEmulatorCore.NativeCell[cols];
        Sync();
    }

    private void Sync()
    {
        _info = _rust.GetInfo();
        int cols = (int)_info.Cols, rows = (int)_info.Rows;
        if (_mirror.Cols != cols || _mirror.Rows != rows)
        {
            _mirror.Resize(cols, rows);
            _gridBuf = new RustEmulatorCore.NativeCell[cols * rows];
            _rowBuf = new RustEmulatorCore.NativeCell[cols];
        }
        if (_rust.CopyGrid(_gridBuf))
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    _mirror[r, c] = _gridBuf[r * cols + c].ToCell();

        int target = (int)_info.HistoryCount;
        if (target < _histMirror.Count) _histMirror.Clear();   // trimmed — refetch all
        for (int h = _histMirror.Count; h < target; h++)
        {
            if (!_rust.CopyHistoryRow(h, _rowBuf)) break;
            var row = new Cell[cols];
            for (int c = 0; c < cols; c++) row[c] = _rowBuf[c].ToCell();
            _histMirror.Add(row);
        }

        _title = _rust.Title;
        _cwd = _rust.Cwd;
        _marks = _rust.GetMarks();
        SyncImages();       // stream-delivered kitty/sixel become visible on the next frame
        DrainHostActions(); // OSC 52 clipboard / notifications / progress / query replies / taps
    }

    // ---- content ----
    public ScreenBuffer Screen => _mirror;
    public void Feed(ReadOnlySpan<byte> bytes) { _rust.Feed(bytes); Sync(); }
    public void Resize(int cols, int rows) { _rust.Resize(cols, rows); Sync(); }

    // ---- cursor / screens ----
    public int CursorRow => (int)_info.CursorRow;
    public int CursorCol => (int)_info.CursorCol;
    public bool CursorVisible => _info.CursorVisible != 0;
    public bool IsAltScreen => _info.IsAltScreen != 0;
    public int ScrollTop => (int)_info.ScrollTop;
    public int ScrollBottom => (int)_info.ScrollBottom;

    // ---- modes ----
    public bool MouseClick => _info.MouseClick != 0;
    public bool MouseDrag => _info.MouseDrag != 0;
    public bool MouseMotion => _info.MouseMotion != 0;
    public bool MouseSgr => _info.MouseSgr != 0;
    public bool MouseReporting => MouseClick || MouseDrag || MouseMotion;
    public bool MouseReportsMotion => MouseDrag || MouseMotion;
    public bool BracketedPaste => _info.BracketedPaste != 0;
    public int KeyboardFlags => _info.KeyboardFlags;

    // ---- scrollback ----
    public int ScrollbackMax { get; set; } = 5000;   // native side owns the real cap (same default)
    public int HistoryCount => _histMirror.Count;
    public long ScrollGeneration => _info.ScrollGeneration;

    public Cell GetHistoryCell(int historyRow, int col)
    {
        if ((uint)historyRow >= (uint)_histMirror.Count) return Cell.Empty;
        var row = _histMirror[historyRow];
        return (uint)col < (uint)row.Length ? row[col] : Cell.Empty;
    }

    public void SeedScrollback(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        _rust.SeedScrollback(string.Join('\n', lines));
        _histMirror.Clear();   // counts jumped — refetch from scratch
        Sync();
    }

    // ---- marks / identity ----
    public IReadOnlyList<TerminalEmulator.ShellMark> Marks => _marks;
    public string Title => _title;
    public string Cwd => _cwd;

    // ---- host actions (ABI v9): the Rust core queues them during feed; DrainHostActions()
    // (called from Sync) decodes the native blob and forwards each to Host, matching the
    // managed emulator's IHostActions call sites. ----
    public IHostActions? Host { get; set; }

    private void DrainHostActions()
    {
        byte[] blob = _rust.TakeHostActions();   // always drains the native queue (even if Host is null)
        if (blob.Length == 0) return;
        var host = Host;
        if (host is null) return;                // nothing to forward to; queue already cleared
        using var br = new BinaryReader(new MemoryStream(blob, writable: false));
        uint count = br.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            switch (br.ReadByte())
            {
                case 1: { string title = ReadStr(br), body = ReadStr(br); host.Notify(title, body); break; }
                case 2: { int state = br.ReadInt32(), value = br.ReadInt32(); host.Progress(state, value); break; }
                case 3: host.ClipboardWrite(ReadStr(br)); break;
                case 4: host.Respond(ReadStr(br)); break;
                case 5: { string kind = ReadStr(br), detail = ReadStr(br); host.Unhandled(kind, detail); break; }
                default: return;                 // unknown tag — blob is corrupt; stop rather than misread
            }
        }
    }

    private static string ReadStr(BinaryReader br)
    {
        int len = (int)br.ReadUInt32();
        return Encoding.UTF8.GetString(br.ReadBytes(len));
    }

    // ---- images (ABI v8): surfaced from the Rust core; KittyImage pixels cached per id so the
    // renderer uploads a texture only once, refreshed on Sync when the id set changes. ----
    private readonly Dictionary<int, KittyImage> _imageCache = new();
    private List<ImagePlacement> _placements = new();

    private void SyncImages()
    {
        var metas = _rust.GetImageMetas();
        // Add newly-transmitted images (fetch pixels once); drop images the core no longer holds.
        var live = new HashSet<int>(metas.Length);
        foreach (var m in metas)
        {
            live.Add(m.Id);
            if (!_imageCache.ContainsKey(m.Id))
                _imageCache[m.Id] = new KittyImage(m.Id, (KittyFormat)m.Format, m.Width, m.Height,
                    _rust.GetImageData(m.Id, (int)m.DataLen));
        }
        if (_imageCache.Count > live.Count)
            foreach (var id in _imageCache.Keys.Where(k => !live.Contains(k)).ToList())
                _imageCache.Remove(id);

        var pls = _rust.GetPlacements();
        var list = new List<ImagePlacement>(pls.Length);
        foreach (var p in pls)
            list.Add(new ImagePlacement(p.ImageId, (int)p.Row, (int)p.Col, p.Cols, p.Rows, p.SrcX, p.SrcY, p.SrcW, p.SrcH));
        _placements = list;
    }

    public IReadOnlyDictionary<int, KittyImage> Images => _imageCache;
    public IReadOnlyList<ImagePlacement> Placements => _placements;
    public void ClearPlacements() { _rust.ClearPlacements(); _placements = new(); }
    public bool HasImage(int id) => _rust.HasImage(id);
    public void SetImageData(int id, KittyFormat format, int width, int height, byte[] data)
    {
        _rust.SetImageData(id, (int)format, width, height, data);
        SyncImages();
    }
    public void PlaceImage(int id, int row, int col, int cols, int rows,
        int srcX = 0, int srcY = 0, int srcW = 0, int srcH = 0)
    {
        _rust.PlaceImage(id, row, col, cols, rows, srcX, srcY, srcW, srcH);
        SyncImages();
    }
    public bool PlaceSixel(byte[] data)
    {
        bool ok = _rust.PlaceSixel(data);
        if (ok) { Sync(); }   // sixel advances the cursor + scrolls, so refresh everything
        return ok;
    }
    // Cell pixel size for sixel layout — mirror to the Rust core is not needed (defaults match);
    // exposed for interface parity.
    public int CellPixelWidth { get; set; } = 8;
    public int CellPixelHeight { get; set; } = 18;

    // ---- dumps (same conventions as TerminalEmulator) ----
    public string DumpRow(int row)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < _mirror.Cols; c++)
        {
            Cell cell = _mirror[row, c];
            if (cell.Width == 0) continue;
            if (cell.Rune > 0xFFFF) sb.Append(char.ConvertFromUtf32(cell.Rune));
            else sb.Append((char)cell.Rune);
        }
        return sb.ToString().TrimEnd();
    }

    public string DumpHistoryRow(int index)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < _mirror.Cols; c++)
        {
            Cell cell = GetHistoryCell(index, c);
            if (cell.Width == 0) continue;
            if (cell.Rune > 0xFFFF) sb.Append(char.ConvertFromUtf32(cell.Rune));
            else sb.Append((char)cell.Rune);
        }
        return sb.ToString().TrimEnd();
    }

    public IReadOnlyList<string> DumpBuffer()
    {
        var lines = new List<string>();
        for (int h = 0; h < _histMirror.Count; h++)
        {
            var sb = new StringBuilder();
            foreach (var cell in _histMirror[h])
            {
                if (cell.Width == 0) continue;
                if (cell.Rune > 0xFFFF) sb.Append(char.ConvertFromUtf32(cell.Rune));
                else sb.Append(cell.Rune == 0 ? ' ' : (char)cell.Rune);
            }
            lines.Add(sb.ToString().TrimEnd());
        }
        for (int r = 0; r < _mirror.Rows; r++) lines.Add(DumpRow(r));
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    public string DumpModes() => _rust.DumpModes();

    public void Dispose() => _rust.Dispose();
}
