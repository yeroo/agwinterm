using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>Queries the inbox conhost used to answer itself. A shipped ConPTY forwards them to the
/// terminal (#339), so the emulator answers them: OSC 10/11 colors, DA1, DA2, DSR and CPR.</summary>
public class QueryReplyTests
{
    private static RecordingHost Feed(string s, int cols = 10, int rows = 5)
    {
        var host = new RecordingHost { DefaultColors = (new Color(0xcc, 0xcc, 0xcc), new Color(0x12, 0x34, 0x56)) };
        var t = new TerminalEmulator(cols, rows) { Host = host };
        t.Feed(Encoding.ASCII.GetBytes(s));
        return host;
    }

    [Fact]
    public void OscColorQueries_ReportTheHostTheme()
    {
        var host = Feed("\x1b]10;?\x1b\\\x1b]11;?\x07");
        Assert.Equal(new[] { "\x1b]10;rgb:cccc/cccc/cccc\x1b\\", "\x1b]11;rgb:1212/3434/5656\x1b\\" }, host.Responses);
    }

    [Fact]
    public void OscColorQueries_WithoutATheme_AnswerOnlyAnAppSetBackground()
    {
        var host = new RecordingHost { DefaultColors = null };
        var t = new TerminalEmulator(10, 2) { Host = host };
        t.Feed(Encoding.ASCII.GetBytes("\x1b]10;?\x1b\\\x1b]11;?\x07"));
        Assert.Empty(host.Responses);
        t.Feed(Encoding.ASCII.GetBytes("\x1b]11;#ff8000\x07\x1b]11;?\x07\x1b]10;?\x07"));
        Assert.Equal(new[] { "\x1b]11;rgb:ffff/8080/0000\x1b\\" }, host.Responses);
    }

    [Fact]
    public void ThemeColors_FormatAndParseRoundTrip_MalformedIsNull()
    {
        var theme = (new Color(0xcc, 0xcc, 0xcc), new Color(0x12, 0x34, 0x56));
        Assert.Equal("cccccc;123456", ThemeColors.Format(theme.Item1, theme.Item2));
        Assert.Equal(theme, ThemeColors.Parse("cccccc;123456"));
        Assert.Null(ThemeColors.Parse(null));
        Assert.Null(ThemeColors.Parse("cccccc"));
        Assert.Null(ThemeColors.Parse("cccccc;12345g"));
    }

    [Fact]
    public void Osc11Query_ReportsAnAppSetBackground_UntilOsc111()
    {
        var host = Feed("\x1b]11;#ff8000\x07\x1b]11;?\x07\x1b]111\x07\x1b]11;?\x07");
        Assert.Equal(new[] { "\x1b]11;rgb:ffff/8080/0000\x1b\\", "\x1b]11;rgb:1212/3434/5656\x1b\\" }, host.Responses);
    }

    [Fact]
    public void DeviceAttributeStatusAndCursorQueries_AreAnswered()
    {
        var host = Feed("\x1b[c\x1b[0c\x1b[>c\x1b[5n\x1b[3;4H\x1b[6n\x1b[?6n");
        Assert.Equal(new[]
        {
            TerminalEmulator.Da1Reply, TerminalEmulator.Da1Reply, TerminalEmulator.Da2Reply,
            "\x1b[0n", "\x1b[3;4R", "\x1b[?3;4;1R",
        }, host.Responses);
    }

    [Fact]
    public void Cpr_AfterAPendingWrap_ReportsTheLastColumn()
    {
        var host = Feed("0123456789\x1b[6n");
        Assert.Equal(new[] { "\x1b[1;10R" }, host.Responses);
    }
}
