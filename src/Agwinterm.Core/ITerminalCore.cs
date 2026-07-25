namespace Agwinterm.Core;

/// <summary>
/// The emulator surface a pane consumes — extracted so the managed
/// <see cref="TerminalEmulator"/> and the Rust-backed <see cref="RustTerminalCore"/>
/// are interchangeable behind <c>emulator-core = managed | rust</c>. Member names
/// deliberately mirror TerminalEmulator exactly, so existing consumers compile
/// unchanged against the interface.
/// </summary>
public interface ITerminalCore
{
    // ---- content ----
    ScreenBuffer Screen { get; }
    void Feed(ReadOnlySpan<byte> bytes);
    void Resize(int cols, int rows);

    // ---- cursor / screens ----
    int CursorRow { get; }
    int CursorCol { get; }
    bool CursorVisible { get; }
    bool IsAltScreen { get; }
    int ScrollTop { get; }
    int ScrollBottom { get; }

    // ---- modes ----
    bool MouseReporting { get; }
    bool MouseReportsMotion { get; }
    bool MouseSgr { get; }
    bool MouseClick { get; }
    bool MouseDrag { get; }
    bool MouseMotion { get; }
    bool BracketedPaste { get; }
    bool FocusReporting { get; }
    bool SynchronizedOutput { get; }
    bool Win32InputMode { get; }
    int CursorShape { get; }
    int KeyboardFlags { get; }

    // ---- scrollback ----
    int ScrollbackMax { get; set; }
    int HistoryCount { get; }
    long ScrollGeneration { get; }
    Cell GetHistoryCell(int historyRow, int col);
    void SeedScrollback(IReadOnlyList<string> lines);

    // ---- marks / identity ----
    IReadOnlyList<TerminalEmulator.ShellMark> Marks { get; }
    string Title { get; }
    string Cwd { get; }

    // ---- host actions (notifications, clipboard, PTY responses). The Rust core
    // does not invoke these yet (phase-2 gap; documented in the config text). ----
    IHostActions? Host { get; set; }

    // ---- images. The Rust core reports none and ignores placement calls in the
    // experimental phase (phase-2 gap; kitty/sixel panes need the managed core). ----
    IReadOnlyDictionary<int, KittyImage> Images { get; }
    IReadOnlyList<ImagePlacement> Placements { get; }
    void ClearPlacements();
    bool HasImage(int id);
    void SetImageData(int id, KittyFormat format, int width, int height, byte[] data);
    void PlaceImage(int id, int row, int col, int cols, int rows,
        int srcX = 0, int srcY = 0, int srcW = 0, int srcH = 0);
    bool PlaceSixel(byte[] data);
    int CellPixelWidth { get; set; }
    int CellPixelHeight { get; set; }

    // ---- dumps (persistence / reattach) ----
    string DumpRow(int row);
    string DumpHistoryRow(int index);
    IReadOnlyList<string> DumpBuffer();
    string DumpModes();
}
