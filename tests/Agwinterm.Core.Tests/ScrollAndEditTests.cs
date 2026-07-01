using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

// Escape sequences use the ESC constant below rather than \x1b literals, because
// C#'s \x hex escape is greedy: "\x1b7" parses as the single char U+1B7, not ESC + '7'.
// The real PTY sends raw bytes, so this only affects test literals.
public class ScrollAndEditTests
{
    private const string ESC = "";

    private static TerminalEmulator Feed(int cols, int rows, string s)
    {
        var t = new TerminalEmulator(cols, rows);
        t.Feed(Encoding.ASCII.GetBytes(s));
        return t;
    }

    [Fact]
    public void SaveRestoreCursor_Esc78()
    {
        var t = Feed(20, 5, $"{ESC}[3;5H{ESC}7{ESC}[1;1H{ESC}8");
        Assert.Equal(2, t.CursorRow); // restored to row 3 (idx 2)
        Assert.Equal(4, t.CursorCol); // col 5 (idx 4)
    }

    [Fact]
    public void ReverseIndex_AtTop_ScrollsDown()
    {
        var t = Feed(10, 3, $"a\r\nb\r\nc{ESC}[1;1H{ESC}M");
        Assert.Equal("", t.DumpRow(0));
        Assert.Equal("a", t.DumpRow(1));
        Assert.Equal("b", t.DumpRow(2));
    }

    [Fact]
    public void ScrollRegion_ConfinesLineFeedScroll()
    {
        // Region rows 2..3 (1-based). Write x,y,z down the region; oldest scrolls out.
        var t = Feed(10, 4, $"top{ESC}[2;3r{ESC}[2;1Hx\r\ny\r\nz");
        Assert.Equal("top", t.DumpRow(0)); // outside region, untouched
        Assert.Equal("y", t.DumpRow(1));
        Assert.Equal("z", t.DumpRow(2));
    }

    [Fact]
    public void InsertLines_PushesDownWithinRegion()
    {
        var t = Feed(10, 4, $"r0\r\nr1\r\nr2\r\nr3{ESC}[2;1H{ESC}[L");
        Assert.Equal("r0", t.DumpRow(0));
        Assert.Equal("", t.DumpRow(1));   // inserted blank
        Assert.Equal("r1", t.DumpRow(2)); // pushed down
    }

    [Fact]
    public void DeleteLines_PullsUp()
    {
        var t = Feed(10, 4, $"r0\r\nr1\r\nr2\r\nr3{ESC}[2;1H{ESC}[M");
        Assert.Equal("r0", t.DumpRow(0));
        Assert.Equal("r2", t.DumpRow(1)); // r1 deleted, r2 pulled up
        Assert.Equal("r3", t.DumpRow(2));
    }

    [Fact]
    public void InsertChars_ShiftsRight()
    {
        var t = Feed(10, 2, $"abcde{ESC}[1;2H{ESC}[2@");
        Assert.Equal("a  bcde", t.DumpRow(0)); // 2 blanks inserted at col 2
    }

    [Fact]
    public void DeleteChars_ShiftsLeft()
    {
        var t = Feed(10, 2, $"abcde{ESC}[1;2H{ESC}[2P");
        Assert.Equal("ade", t.DumpRow(0)); // 'bc' deleted
    }

    [Fact]
    public void EraseChars_BlanksInPlace_CursorUnmoved()
    {
        // ECH erases N cells from the cursor without shifting or moving the cursor.
        var t = Feed(10, 2, $"abcde{ESC}[1;2H{ESC}[2X");
        Assert.Equal("a  de", t.DumpRow(0)); // 'b','c' blanked, 'd','e' stay in place
    }

    [Fact]
    public void CursorVisibility_Mode25()
    {
        var t = Feed(10, 2, $"{ESC}[?25l");
        Assert.False(t.CursorVisible);
        t.Feed(Encoding.ASCII.GetBytes($"{ESC}[?25h"));
        Assert.True(t.CursorVisible);
    }

    [Fact]
    public void VerticalPositionAbsolute_VPA()
    {
        var t = Feed(10, 5, $"{ESC}[4d");
        Assert.Equal(3, t.CursorRow); // row 4 (idx 3)
    }
}
