using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class AltScreenTests
{
    private static TerminalEmulator Feed(string s)
    {
        var t = new TerminalEmulator(20, 4);
        t.Feed(Encoding.ASCII.GetBytes(s));
        return t;
    }

    [Fact]
    public void Enter1049_SwitchesToBlankAltScreen()
    {
        var t = Feed("main\x1b[?1049h");
        Assert.True(t.IsAltScreen);
        Assert.Equal("", t.DumpRow(0)); // alt buffer is blank
    }

    [Fact]
    public void Leave1049_RestoresMainContent()
    {
        var t = Feed("main text\x1b[?1049halt stuff\x1b[?1049l");
        Assert.False(t.IsAltScreen);
        Assert.Equal("main text", t.DumpRow(0)); // main buffer preserved
    }

    [Fact]
    public void Enter1049_SavesCursor_LeaveRestores()
    {
        // cursor at col 4 on main, enter alt (home), leave -> cursor restored to row0/col4
        var t = Feed("main\x1b[?1049h\x1b[?1049l");
        Assert.Equal(0, t.CursorRow);
        Assert.Equal(4, t.CursorCol);
    }

    [Fact]
    public void Mode47_SwitchesBuffersWithoutCursorSave()
    {
        var t = Feed("hi\x1b[?47h");
        Assert.True(t.IsAltScreen);
        t.Feed(Encoding.ASCII.GetBytes("\x1b[?47l"));
        Assert.False(t.IsAltScreen);
        Assert.Equal("hi", t.DumpRow(0));
    }
}
