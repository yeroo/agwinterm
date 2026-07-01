using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class OscTests
{
    private static TerminalEmulator Feed(string s)
    {
        var t = new TerminalEmulator(40, 3);
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    [Fact]
    public void Osc2_SetsTitle()
    {
        var t = Feed("\x1b]2;My Session\x07");
        Assert.Equal("My Session", t.Title);
    }

    [Fact]
    public void Osc0_SetsTitle()
    {
        var t = Feed("\x1b]0;Another\x07");
        Assert.Equal("Another", t.Title);
    }

    [Fact]
    public void Osc7_SetsCwd()
    {
        var t = Feed("\x1b]7;file:///c:/work/repo\x1b\\");
        Assert.Equal("file:///c:/work/repo", t.Cwd);
    }

    [Fact]
    public void OscDoesNotEmitPrintableCharacters()
    {
        var t = Feed("\x1b]0;hidden\x07visible");
        Assert.Equal("visible", t.DumpRow(0));
    }
}
