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
    public void ScrollGeneration_BumpsOnScroll_NotOnInPlaceRewrite()
    {
        var t = new TerminalEmulator(10, 3);
        // Fill the 3 visible rows without overflowing — no scroll yet.
        Feed(t, "L1\r\nL2\r\nL3");
        long g0 = t.ScrollGeneration;

        // In-place repaint: move the cursor home and rewrite the same region (what a TUI like Claude Code
        // does every frame). CUP to 1;1, overwrite rows — this must NOT scroll, so the generation is flat.
        Feed(t, "\x1b[1;1HR1\x1b[2;1HR2\x1b[3;1HR3");
        Assert.Equal(g0, t.ScrollGeneration); // selection would survive this

        // A genuine newline at the bottom row scrolls one line into history — generation advances.
        Feed(t, "\r\nNEW");
        Assert.True(t.ScrollGeneration > g0);
    }

    [Fact]
    public void ScrollGeneration_KeepsClimbingAfterCap()
    {
        var t = new TerminalEmulator(10, 3) { ScrollbackMax = 50 };
        for (int i = 0; i < 400; i++) Feed(t, "X\r\n");
        // HistoryCount saturates near the cap, but the monotonic generation reflects every real scroll.
        Assert.InRange(t.HistoryCount, 50, 50 + 512);
        Assert.InRange(t.ScrollGeneration, 390, 400); // ~398 real scrolls (3-row screen), far above the 50-row cap
    }

    [Fact]
    public void ScrollbackCap_DropsOldest()
    {
        var t = new TerminalEmulator(10, 3) { ScrollbackMax = 100 };
        for (int i = 1; i <= 700; i++) Feed(t, $"L{i}\r\n");
        // ~698 scrolls but bounded near the cap (batched trim allows a little slack), far below 698.
        Assert.InRange(t.HistoryCount, 100, 100 + 512);
        // WHICH rows went, not only how many. Distinct content per line, because "X" everywhere let
        // a trim from the wrong end - or of the wrong size - pass unnoticed.
        Assert.StartsWith("L", HistoryRow(t, 0, 10));
        int oldest = int.Parse(HistoryRow(t, 0, 10).Substring(1));
        int newest = int.Parse(HistoryRow(t, t.HistoryCount - 1, 10).Substring(1));
        Assert.Equal(t.HistoryCount - 1, newest - oldest);   // a contiguous run: the FRONT was trimmed
    }

    [Fact]
    public void EvictionCount_IsScrollGenerationMinusHistoryGrowth()
    {
        // The identity the panes' selection tracking is built on: every line that scrolled either
        // stayed in history or fell off the front, so
        //     evicted = (delta ScrollGeneration) - (delta HistoryCount)
        // and the rows that went are the OLDEST. If the trim end or its size ever changed, a
        // selection would silently follow the wrong text and paste something never selected.
        var t = new TerminalEmulator(10, 3) { ScrollbackMax = 100 };
        for (int i = 1; i <= 800; i++) Feed(t, $"L{i}\r\n");   // trimming is BATCHED (cap + slack),
        //                                                        so hundreds of lines pass before any drop
        long gen0 = t.ScrollGeneration;
        int hist0 = t.HistoryCount;
        int oldest0 = int.Parse(HistoryRow(t, 0, 10).Substring(1));

        for (int i = 801; i <= 1600; i++) Feed(t, $"L{i}\r\n");
        long evicted = (t.ScrollGeneration - gen0) - (t.HistoryCount - hist0);
        Assert.True(evicted > 0, $"gen {gen0}->{t.ScrollGeneration}, hist {hist0}->{t.HistoryCount}: nothing was evicted");

        // The line that sat at history index `evicted` is now at index 0 - exactly the oldest
        // `evicted` rows were dropped, from the front.
        Assert.Equal($"L{oldest0 + evicted}", HistoryRow(t, 0, 10));
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
