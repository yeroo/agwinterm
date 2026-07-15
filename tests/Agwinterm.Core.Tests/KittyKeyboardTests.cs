using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class KittyKeyboardTests
{
    private static TerminalEmulator Feed(string s)
    {
        var t = new TerminalEmulator(40, 5);
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    [Fact]
    public void Push_SetsFlags()
    {
        var t = Feed("\x1b[>1u");         // enable "disambiguate escape codes"
        Assert.Equal(1, t.KeyboardFlags);
    }

    [Fact]
    public void Push_Pop_RestoresPrevious()
    {
        var t = Feed("\x1b[>1u");         // push 1
        t.Feed(Encoding.UTF8.GetBytes("\x1b[>5u")); // push 5
        Assert.Equal(5, t.KeyboardFlags);
        t.Feed(Encoding.UTF8.GetBytes("\x1b[<1u")); // pop 1
        Assert.Equal(1, t.KeyboardFlags);
    }

    [Fact]
    public void Set_ReplacesFlags()
    {
        var t = Feed("\x1b[>1u");         // push 1
        t.Feed(Encoding.UTF8.GetBytes("\x1b[=15;1u")); // set to 15 (mode 1 = replace)
        Assert.Equal(15, t.KeyboardFlags);
    }

    [Fact]
    public void Set_OrAndModes()
    {
        var t = Feed("\x1b[>1u");
        t.Feed(Encoding.UTF8.GetBytes("\x1b[=4;2u"));  // or-in 4 -> 5
        Assert.Equal(5, t.KeyboardFlags);
        t.Feed(Encoding.UTF8.GetBytes("\x1b[=1;3u"));  // and-not 1 -> 4
        Assert.Equal(4, t.KeyboardFlags);
    }

    [Fact]
    public void Query_RespondsWithCurrentFlags()
    {
        var t = new TerminalEmulator(40, 5);
        var host = new RecordingHost(); t.Host = host;
        t.Feed(Encoding.UTF8.GetBytes("\x1b[>7u"));   // enable flags 7
        t.Feed(Encoding.UTF8.GetBytes("\x1b[?u"));    // query
        Assert.Equal("\x1b[?7u", host.Responses.Single());
    }

    [Fact]
    public void DefaultFlagsAreZero()
    {
        var t = new TerminalEmulator(40, 5);
        Assert.Equal(0, t.KeyboardFlags);
    }
}
