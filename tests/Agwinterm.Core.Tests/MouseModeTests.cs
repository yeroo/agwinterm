using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class MouseModeTests
{
    private static readonly string ESC = ((char)27).ToString();

    private static TerminalEmulator Feed(string s)
    {
        var t = new TerminalEmulator(80, 24);
        t.Feed(Encoding.ASCII.GetBytes(s));
        return t;
    }

    [Fact]
    public void DefaultsOff()
    {
        var t = new TerminalEmulator(80, 24);
        Assert.False(t.MouseReporting);
        Assert.False(t.MouseSgr);
        Assert.False(t.MouseSgrPixels);
        Assert.False(t.MouseReportsMotion);
    }

    [Fact]
    public void Enable1000And1006()
    {
        var t = Feed($"{ESC}[?1000h{ESC}[?1006h");
        Assert.True(t.MouseReporting);
        Assert.True(t.MouseSgr);
        Assert.False(t.MouseReportsMotion); // 1000 is press/release only
    }

    [Fact]
    public void DragMode1002_ReportsMotion()
    {
        var t = Feed($"{ESC}[?1002h");
        Assert.True(t.MouseReporting);
        Assert.True(t.MouseReportsMotion);
    }

    [Fact]
    public void Disable_TurnsOff()
    {
        var t = Feed($"{ESC}[?1000h{ESC}[?1006h{ESC}[?1000l{ESC}[?1006l");
        Assert.False(t.MouseReporting);
        Assert.False(t.MouseSgr);
    }

    [Fact]
    public void CrosstermBundle_EnablesReportingAndSgr()
    {
        // crossterm's EnableMouseCapture sends 1000;1002;1003;1006.
        var t = Feed($"{ESC}[?1000h{ESC}[?1002h{ESC}[?1003h{ESC}[?1006h");
        Assert.True(t.MouseReporting);
        Assert.True(t.MouseReportsMotion);
        Assert.True(t.MouseSgr);
    }

    [Fact]
    public void SgrPixels1016_SetResetAndDumpModes()
    {
        var t = Feed($"{ESC}[?1016h");
        Assert.True(t.MouseSgrPixels);
        Assert.Contains($"{ESC}[?1016h", t.DumpModes());

        t.Feed(Encoding.ASCII.GetBytes($"{ESC}[?1016l"));
        Assert.False(t.MouseSgrPixels);
        Assert.DoesNotContain($"{ESC}[?1016h", t.DumpModes());
    }

    [Fact]
    public void SgrPixels1016_DecrqmReportsResetThenSet()
    {
        var host = new RecordingHost();
        var t = new TerminalEmulator(80, 24) { Host = host };

        t.Feed(Encoding.ASCII.GetBytes($"{ESC}[?1016$p{ESC}[?1016h{ESC}[?1016$p"));

        Assert.Equal(new[] { $"{ESC}[?1016;2$y", $"{ESC}[?1016;1$y" }, host.Responses);
        Assert.Empty(host.Unhandleds);
    }
}
