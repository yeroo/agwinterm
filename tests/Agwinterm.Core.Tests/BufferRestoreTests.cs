using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class BufferRestoreTests
{
    [Fact]
    public void DumpBuffer_CapturesScrollbackAndVisible()
    {
        var t = new TerminalEmulator(20, 3);
        // Print 5 lines into a 3-row screen: 2 scroll into history, 3 stay visible.
        for (int i = 1; i <= 5; i++) t.Feed(Encoding.UTF8.GetBytes($"line{i}\r\n"));
        var buf = t.DumpBuffer();
        Assert.Contains("line1", buf);
        Assert.Contains("line5", buf);
        Assert.True(buf.Count >= 5);
    }

    [Fact]
    public void SeedScrollback_AddsToHistoryAboveLiveScreen()
    {
        var t = new TerminalEmulator(20, 3);
        t.SeedScrollback(new[] { "restored-A", "restored-B" });
        Assert.Equal(2, t.HistoryCount);
        Assert.Equal("restored-A", t.GetHistoryCell(0, 0) is var c0 ? RowText(t, 0) : "");
        Assert.Equal("restored-B", RowText(t, 1));
        // Live screen is untouched (blank).
        Assert.Equal("", t.DumpRow(0));
    }

    private static string RowText(TerminalEmulator t, int historyRow)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < 20; c++) { var ch = t.GetHistoryCell(historyRow, c).Rune; if (ch == 0) break; sb.Append((char)ch); }
        return sb.ToString().TrimEnd();
    }
}
