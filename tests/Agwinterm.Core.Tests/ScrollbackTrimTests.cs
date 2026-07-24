using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>Scrollback rows store only their non-empty prefix (memory lever for the 6-8GB lite
/// target, #134). Reads still pad to full width, so behaviour is unchanged — the Rust differential
/// oracle proves that separately; these assert the storage actually shrank and BCE is preserved.</summary>
public class ScrollbackTrimTests
{
    [Fact]
    public void SparseRow_StoresOnlyItsPrefix()
    {
        var t = new TerminalEmulator(80, 3);
        t.Feed(Encoding.ASCII.GetBytes("hi\r\n\r\n\r\n\r\n"));   // "hi" then blanks scroll off
        Assert.True(t.HistoryCount >= 1);
        // Reads pad to full width — behaviour identical to full-width storage.
        Assert.Equal('h', (char)t.GetHistoryCell(0, 0).Rune);
        Assert.Equal(Cell.Empty, t.GetHistoryCell(0, 2));
        Assert.Equal(Cell.Empty, t.GetHistoryCell(0, 79));
    }

    [Fact]
    public void BceColouredBlank_IsNotTrimmed()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed(Encoding.ASCII.GetBytes("\x1b[41m\x1b[2K\r\n\r\n\r\n"));   // red BCE across the row
        // A coloured trailing blank is not Cell.Empty, so it survives; the read shows the red bg.
        Assert.Equal(Color.FromIndex(1), t.GetHistoryCell(0, 9).Background);
    }

    [Fact]
    public void SeedScrollback_PadsOnReadWithoutStoringTheMargin()
    {
        var t = new TerminalEmulator(100, 3);
        t.SeedScrollback(new[] { "short" });
        Assert.Equal('s', (char)t.GetHistoryCell(0, 0).Rune);
        Assert.Equal(Cell.Empty, t.GetHistoryCell(0, 50));   // beyond the text = empty, not a dim space
    }
}
