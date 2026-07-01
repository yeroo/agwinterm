using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class TerminalEmulatorTests
{
    private static TerminalEmulator Feed(int cols, int rows, string s)
    {
        var t = new TerminalEmulator(cols, rows);
        t.Feed(Encoding.ASCII.GetBytes(s));
        return t;
    }

    [Fact]
    public void PrintsTextAndAdvancesCursor()
    {
        var t = Feed(10, 2, "abc");
        Assert.Equal("abc", t.DumpRow(0));
        Assert.Equal(0, t.CursorRow);
        Assert.Equal(3, t.CursorCol);
    }

    [Fact]
    public void CarriageReturnAndLineFeed()
    {
        var t = Feed(10, 3, "ab\r\nc");
        Assert.Equal("ab", t.DumpRow(0));
        Assert.Equal("c", t.DumpRow(1));
        Assert.Equal(1, t.CursorRow);
        Assert.Equal(1, t.CursorCol);
    }

    [Fact]
    public void Backspace()
    {
        var t = Feed(10, 2, "ab\bc");
        Assert.Equal("ac", t.DumpRow(0)); // c overwrites b
        Assert.Equal(2, t.CursorCol);
    }

    [Fact]
    public void Tab_AdvancesToNextMultipleOfEight()
    {
        var t = Feed(20, 2, "a\tb");
        Assert.Equal(0, t.CursorRow);
        Assert.Equal(9, t.CursorCol); // tab from col1 -> col8, then 'b' -> col9
        Assert.Equal('b', t.Screen[0, 8].Rune);
    }

    [Fact]
    public void LineFeedAtBottom_Scrolls()
    {
        var t = Feed(10, 2, "x\r\ny\r\nz");
        Assert.Equal("y", t.DumpRow(0)); // scrolled up
        Assert.Equal("z", t.DumpRow(1));
        Assert.Equal(1, t.CursorRow);
    }

    [Fact]
    public void PrintWrapsAtRightEdge()
    {
        var t = Feed(3, 2, "abcd");
        Assert.Equal("abc", t.DumpRow(0));
        Assert.Equal("d", t.DumpRow(1));
    }
}
