using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class ScrollbackTests
{
    private static void Feed(TerminalEmulator t, string s) => t.Feed(Encoding.ASCII.GetBytes(s));

    private static string HistoryRow(TerminalEmulator t, int row, int cols)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < cols; c++) sb.Append((char)t.GetHistoryCell(row, c).Rune);
        return sb.ToString().TrimEnd();
    }

    [Fact]
    public void MainScreenScroll_PushesEvictedRowsToHistory()
    {
        var t = new TerminalEmulator(10, 3);
        for (int i = 1; i <= 8; i++) Feed(t, $"L{i}\r\n");
        // 3 visible rows, 8 newlines → 6 full-screen scrolls → 6 rows in history, oldest first.
        Assert.Equal(6, t.HistoryCount);
        Assert.Equal("L1", HistoryRow(t, 0, 10));            // oldest evicted line
        Assert.Equal("L6", HistoryRow(t, t.HistoryCount - 1, 10)); // newest evicted line
    }

    [Fact]
    public void ScrollbackCap_DropsOldest()
    {
        var t = new TerminalEmulator(10, 3) { ScrollbackMax = 100 };
        for (int i = 1; i <= 700; i++) Feed(t, $"X\r\n");
        // ~698 scrolls but bounded near the cap (batched trim allows a little slack), far below 698.
        Assert.InRange(t.HistoryCount, 100, 100 + 512);
    }

    [Fact]
    public void AltScreen_DoesNotRecordHistory()
    {
        var t = new TerminalEmulator(10, 3);
        Feed(t, "\x1b[?1049h");                 // enter alternate screen
        for (int i = 0; i < 10; i++) Feed(t, "A\r\n");
        Assert.Equal(0, t.HistoryCount);
    }

    [Fact]
    public void Clear_DoesNotRecordHistory()
    {
        var t = new TerminalEmulator(10, 3);
        Feed(t, "\x1b[2J\x1b[2J");               // erase display twice
        Assert.Equal(0, t.HistoryCount);
    }

    [Fact]
    public void ScrollbackDisabled_KeepsNoHistory()
    {
        var t = new TerminalEmulator(10, 3) { ScrollbackMax = 0 };
        for (int i = 0; i < 20; i++) Feed(t, "Y\r\n");
        Assert.Equal(0, t.HistoryCount);
    }
}
