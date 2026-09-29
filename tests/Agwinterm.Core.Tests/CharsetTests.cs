using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>Character-set designation (SCS) and SO/SI. ncurses sends ESC ( B in every sgr0 and draws boxes
/// with ESC ( 0; the inbox conhost translated both, a bundled ConPTY passes them through (#339).</summary>
public class CharsetTests
{
    private static string Row(TerminalEmulator t, int r, int n) =>
        string.Concat(Enumerable.Range(0, n).Select(c => char.ConvertFromUtf32(t.Screen[r, c].Rune)));

    [Fact]
    public void CharsetDesignation_PrintsNothing_AndDrawsDecLines()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed(Encoding.ASCII.GetBytes("\x1b(Ba\x1b(0lqkx\x1b(Bq\r\n\x1b)0\x0eqx\x0fq"));
        Assert.Equal("a┌─┐│q", Row(t, 0, 6));
        Assert.Equal("─│q   ", Row(t, 1, 6));
        // The reattach modes carry the sets: G1 is still line drawing, G0 and the shift are back.
        string modes = t.DumpModes();
        Assert.Contains("\x1b)0", modes, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b(0", modes, StringComparison.Ordinal);
        Assert.DoesNotContain('\x0e', modes);
    }

    [Fact]
    public void ReattachReplay_KeepsGlyphsDrawnBeforeACharsetChange()
    {
        // A pty-host sends the modes, then the screen. An ASCII q already on screen must stay a q though
        // G0 is now line drawing, and the app's next q must draw a line on both sides.
        var t = new TerminalEmulator(8, 2);
        t.Feed(Encoding.ASCII.GetBytes("q\x1b)0\x0e\x1b(0"));
        var replay = new TerminalEmulator(8, 2);
        replay.Feed(Encoding.UTF8.GetBytes(t.DumpModes()));
        replay.Feed(Encoding.UTF8.GetBytes(t.DumpScreen()));
        t.Feed(Encoding.ASCII.GetBytes("q\x0fq"));
        replay.Feed(Encoding.ASCII.GetBytes("q\x0fq"));
        for (int c = 0; c < 3; c++) Assert.True(t.Screen[0, c] == replay.Screen[0, c], $"cell 0,{c}");
        Assert.Equal("q─", Row(t, 0, 2));
    }

    [Theory]
    [InlineData("\u001b[?1049h\u001b(0", "q")]
    [InlineData("\u001b(0\u001b[?1049h\u001b(B", "─")]
    public void ReattachOnTheAltScreen_RestoresThePreAltCharacterSets(string source, string expected)
    {
        // After the replica takes the modes and the screen, leaving the alt screen must bring back what
        // the source had before entering it.
        var t = new TerminalEmulator(8, 2);
        t.Feed(Encoding.ASCII.GetBytes(source));
        var replay = new TerminalEmulator(8, 2);
        replay.Feed(Encoding.UTF8.GetBytes(t.DumpModes()));
        replay.Feed(Encoding.UTF8.GetBytes(t.DumpScreen()));
        t.Feed(Encoding.ASCII.GetBytes("\u001b[?1049l\r\nq"));
        replay.Feed(Encoding.ASCII.GetBytes("\u001b[?1049l\r\nq"));
        Assert.Equal(expected, Row(t, 1, 1));
        Assert.True(t.Screen[1, 0] == replay.Screen[1, 0], $"{t.Screen[1, 0]} vs {replay.Screen[1, 0]}");
    }

    [Fact]
    public void DecscDecrc_SaveAndRestoreTheCharacterSets()
    {
        var t = new TerminalEmulator(8, 2);
        t.Feed(Encoding.ASCII.GetBytes("\u001b(0\u001b7\u001b(B\u000e\u001b8q"));   // \u: "\x1b7" would be U+01B7
        Assert.Equal("─", Row(t, 0, 1));
    }
}
