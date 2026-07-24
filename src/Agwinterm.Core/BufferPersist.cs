using Agwinterm.Core.Persist;
using Google.Protobuf;

namespace Agwinterm.Core;

/// <summary>
/// Full-fidelity buffer persistence (M2 phase 3): serialize the scrollback + visible grid to a
/// protobuf blob that restores every cell's colour and attributes, and reseed from it. Replaces the
/// plain-text reopen/restore path (which reseeded flat dim text). Blob is per-cell but compact —
/// proto3 omits default fields, so a run of plain default cells costs almost nothing, and rows are
/// trimmed to their content (reads pad with <see cref="Cell.Empty"/>).
/// </summary>
public static class BufferPersist
{
    public const uint Version = 1;

    private static uint Pack(Color c) => ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    private static Color Unpack(uint v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    private static PCell ToP(Cell c) => new()
    {
        Rune = c.Rune,
        Fg = Pack(c.Foreground),
        Bg = Pack(c.Background),
        Attrs = (uint)c.Attributes,
        Width = c.Width,
        FgKind = (uint)c.FgSpec.Kind,
        FgIndex = c.FgSpec.Index,
        FgRgb = Pack(c.FgSpec.Rgb),
        BgKind = (uint)c.BgSpec.Kind,
        BgIndex = c.BgSpec.Index,
        BgRgb = Pack(c.BgSpec.Rgb),
    };

    private static Cell FromP(PCell p) => new(
        p.Rune, Unpack(p.Fg), Unpack(p.Bg), (CellAttributes)p.Attrs, (byte)p.Width,
        new ColorSpec((ColorSpecKind)p.FgKind, (byte)p.FgIndex, Unpack(p.FgRgb)),
        new ColorSpec((ColorSpecKind)p.BgKind, (byte)p.BgIndex, Unpack(p.BgRgb)));

    private static bool RowNeeded(IReadOnlyList<PCell> _) => true;

    /// <summary>Capture the last <paramref name="maxRows"/> buffer rows (scrollback + visible) as an
    /// attributed blob. Trailing empty cells per row are dropped; the whole thing round-trips exactly.</summary>
    public static byte[] Serialize(TerminalEmulator emu, int maxRows = 500)
    {
        var buf = new PBuffer { Version = Version, Cols = (uint)emu.Screen.Cols };
        int cols = emu.Screen.Cols;

        var rows = new List<PRow>();
        void AddRow(Func<int, Cell> cellAt)
        {
            int len = cols;
            while (len > 0 && cellAt(len - 1) == Cell.Empty) len--;   // trim trailing empties
            var pr = new PRow();
            for (int c = 0; c < len; c++) pr.Cells.Add(ToP(cellAt(c)));
            rows.Add(pr);
        }

        for (int h = 0; h < emu.HistoryCount; h++)
        {
            int hi = h;
            AddRow(c => emu.GetHistoryCell(hi, c));
        }
        for (int r = 0; r < emu.Screen.Rows; r++)
        {
            int rr = r;
            AddRow(c => emu.Screen[rr, c]);
        }
        // Drop trailing all-empty rows, then cap to maxRows (keep the newest).
        while (rows.Count > 0 && rows[^1].Cells.Count == 0) rows.RemoveAt(rows.Count - 1);
        if (rows.Count > maxRows) rows.RemoveRange(0, rows.Count - maxRows);
        buf.Rows.AddRange(rows);
        return buf.ToByteArray();
    }

    /// <summary>Try to parse a persisted blob; false if it isn't a v1 <see cref="PBuffer"/>.</summary>
    public static bool TryParse(byte[] blob, out PBuffer buffer)
    {
        buffer = new PBuffer();
        try
        {
            buffer = PBuffer.Parser.ParseFrom(blob);
            return buffer.Version == Version;
        }
        catch (InvalidProtocolBufferException) { return false; }
    }

    /// <summary>Reseed the scrollback from a persisted blob with full colour/attributes. Rows wider
    /// than the current terminal are truncated; narrower rows pad naturally on read.</summary>
    public static void Restore(TerminalEmulator emu, PBuffer buffer)
    {
        var rows = new List<Cell[]>(buffer.Rows.Count);
        foreach (var pr in buffer.Rows)
        {
            var row = new Cell[pr.Cells.Count];
            for (int c = 0; c < pr.Cells.Count; c++) row[c] = FromP(pr.Cells[c]);
            rows.Add(row);
        }
        emu.SeedScrollbackAttributed(rows);
    }
}
