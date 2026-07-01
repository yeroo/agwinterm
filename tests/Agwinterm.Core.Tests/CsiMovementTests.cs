using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class CsiMovementTests
{
    private static TerminalEmulator Feed(int cols, int rows, string s)
    {
        var t = new TerminalEmulator(cols, rows);
        t.Feed(Encoding.ASCII.GetBytes(s));
        return t;
    }

    [Fact]
    public void Cup_MovesCursorOneBased()
    {
        var t = Feed(10, 5, "\x1b[3;4H");
        Assert.Equal(2, t.CursorRow); // row 3 (1-based) -> index 2
        Assert.Equal(3, t.CursorCol);
    }

    [Fact]
    public void Cup_NoParams_GoesHome()
    {
        var t = Feed(10, 5, "abc\x1b[H");
        Assert.Equal(0, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void CursorForwardAndUp()
    {
        var t = Feed(10, 5, "\x1b[2;2H\x1b[3C\x1b[1A");
        Assert.Equal(0, t.CursorRow); // row2 -1
        Assert.Equal(4, t.CursorCol); // col2 +3 (col index 1 +3 = 4)
    }

    [Fact]
    public void EraseLineToRight()
    {
        var t = Feed(10, 2, "abcdef\x1b[3G\x1b[0K");
        Assert.Equal("ab", t.DumpRow(0)); // from col3 to end cleared
    }

    [Fact]
    public void EraseDisplayAll()
    {
        var t = Feed(10, 2, "abc\r\ndef\x1b[2J");
        Assert.Equal("", t.DumpRow(0));
        Assert.Equal("", t.DumpRow(1));
    }
}
