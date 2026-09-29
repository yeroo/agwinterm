using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>DumpScreen is what a pty-host sends a reattaching client when the bundled ConPTY does not
/// repaint the viewport itself (#339): fed to a fresh emulator it must reproduce the screen.</summary>
public class DumpScreenTests
{
    [Fact]
    public void DumpScreen_RedrawsTheScreenOnAFreshEmulator()
    {
        var t = new TerminalEmulator(12, 4);
        t.Feed(Encoding.UTF8.GetBytes(
            "\x1b[1;31mred\x1b[0m \x1b[4;38;5;200mpink\x1b[0m\r\n\x1b[44m  bg  \x1b[0m\r\n" +
            "\x1b[3;38;2;1;2;3m中x\x1b[0m\x1b[2;9H"));
        var replay = new TerminalEmulator(12, 4);
        replay.Feed(Encoding.ASCII.GetBytes("garbage that must be cleared"));
        replay.Feed(Encoding.UTF8.GetBytes(t.DumpScreen()));

        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 12; c++)
                Assert.True(t.Screen[r, c] == replay.Screen[r, c], $"cell {r},{c}: {t.Screen[r, c]} vs {replay.Screen[r, c]}");
        Assert.Equal((t.CursorRow, t.CursorCol), (replay.CursorRow, replay.CursorCol));
    }

    [Fact]
    public void DumpScreen_KeepsThePenThePendingWrapAndTheMargins()
    {
        // A full-width row leaves the cursor past the edge with a red pen, inside a scroll region:
        // output after the replay must land where, and look how, it does on the source.
        var t = new TerminalEmulator(6, 5);
        t.Feed(Encoding.ASCII.GetBytes("\x1b[2;4r\x1b[4;1H\x1b[1;31mabcdef"));
        var replay = new TerminalEmulator(6, 5);
        replay.Feed(Encoding.UTF8.GetBytes(t.DumpScreen()));
        foreach (string feed in new[] { "gh", "\r\n\n\nij\x1b[42m k" })
        {
            t.Feed(Encoding.ASCII.GetBytes(feed));
            replay.Feed(Encoding.ASCII.GetBytes(feed));
            for (int r = 0; r < 5; r++)
                for (int c = 0; c < 6; c++)
                    Assert.True(t.Screen[r, c] == replay.Screen[r, c], $"cell {r},{c}: {t.Screen[r, c]} vs {replay.Screen[r, c]}");
            Assert.Equal((t.CursorRow, t.CursorCol), (replay.CursorRow, replay.CursorCol));
        }
        Assert.Equal((1, 3), (replay.ScrollTop, replay.ScrollBottom));
    }
}
