using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>Full-fidelity buffer persistence (M2 phase 3): serialize → restore preserves every
/// cell's colour and attributes (vs the old plain-text path that reseeded flat dim text).</summary>
public class BufferPersistTests
{
    private static TerminalEmulator Feed(int cols, int rows, string s)
    {
        var t = new TerminalEmulator(cols, rows);
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    [Fact]
    public void RoundTrip_PreservesColorsAndAttributes()
    {
        // Colourful, styled content that scrolls into history.
        var src = Feed(40, 4,
            "\x1b[1;31mbold-red\x1b[0m plain \x1b[38;5;208morange\x1b[0m\r\n" +
            "\x1b[4;32munderline-green\x1b[0m\r\n" +
            "\x1b[7minverse\x1b[0m \x1b[3mitalic\x1b[0m\r\n" +
            "\x1b[48;2;10;20;30mtruecolor-bg\x1b[0m\r\n" +
            "more\r\n");

        byte[] blob = BufferPersist.Serialize(src);
        Assert.True(BufferPersist.TryParse(blob, out var pbuf));

        // Restore into a fresh emulator and compare the reconstructed history to the source's
        // full buffer (history + visible), cell for cell.
        var dst = new TerminalEmulator(40, 4);
        BufferPersist.Restore(dst, pbuf);

        var expected = new List<Cell[]>();
        for (int h = 0; h < src.HistoryCount; h++)
        {
            var row = new Cell[40];
            for (int c = 0; c < 40; c++) row[c] = src.GetHistoryCell(h, c);
            expected.Add(row);
        }
        for (int r = 0; r < src.Screen.Rows; r++)
        {
            var row = new Cell[40];
            for (int c = 0; c < 40; c++) row[c] = src.Screen[r, c];
            // Drop trailing all-empty rows the same way Serialize does.
            expected.Add(row);
        }
        while (expected.Count > 0 && expected[^1].All(c => c == Cell.Empty)) expected.RemoveAt(expected.Count - 1);

        Assert.Equal(expected.Count, dst.HistoryCount);
        for (int h = 0; h < expected.Count; h++)
            for (int c = 0; c < 40; c++)
                Assert.Equal(expected[h][c], dst.GetHistoryCell(h, c));
    }

    [Fact]
    public void Serialize_IsCompact_ForSparseContent()
    {
        // A mostly-blank 200-col buffer must NOT cost 200 cells/row (proto3 default omission +
        // per-row trim). One short line in a wide, tall terminal.
        var t = new TerminalEmulator(200, 50);
        t.Feed(Encoding.ASCII.GetBytes("hi\r\n"));
        byte[] blob = BufferPersist.Serialize(t);
        Assert.True(blob.Length < 200, $"sparse buffer blob unexpectedly large: {blob.Length} bytes");
    }

    [Fact]
    public void TryParse_RejectsGarbage()
    {
        Assert.False(BufferPersist.TryParse(new byte[] { 0xFF, 0xFF, 0x01, 0x02 }, out _));
    }

    [Fact]
    public void Restore_TruncatesRowsWiderThanTerminal()
    {
        var wide = Feed(120, 3, new string('x', 120) + "\r\n\r\n");
        byte[] blob = BufferPersist.Serialize(wide);
        Assert.True(BufferPersist.TryParse(blob, out var pbuf));

        var narrow = new TerminalEmulator(40, 3);   // restore into a narrower terminal
        BufferPersist.Restore(narrow, pbuf);
        // The 120-wide row is truncated to 40; reading col 39 works, col 40+ pads empty.
        Assert.Equal('x', (char)narrow.GetHistoryCell(0, 39).Rune);
        Assert.Equal(Cell.Empty, narrow.GetHistoryCell(0, 40));
    }
}
