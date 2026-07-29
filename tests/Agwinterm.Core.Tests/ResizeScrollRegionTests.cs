using System.Text;

namespace Agwinterm.Core.Tests;

/// <summary>
/// A resize to the geometry already in effect must be a no-op.
///
/// Hosts call Resize on events that need not have changed the size — agwinterm-lite re-syncs both
/// panes on every session switch — and the margin reset inside Resize then clears DECSTBM under a
/// full-screen TUI that still believes its margins are set. Nothing tells the app to redraw (the
/// size never changed, so there is no SIGWINCH), so the scrambled screen just stays there. That was
/// the "render artefacts when I switch between sessions" report.
/// </summary>
public class ResizeScrollRegionTests
{
    private static TerminalEmulator Emu(int cols, int rows, string vt)
    {
        var e = new TerminalEmulator(cols, rows);
        e.Feed(Encoding.UTF8.GetBytes(vt));
        return e;
    }

    [Fact]
    public void ResizeToSameGeometry_KeepsScrollRegion()
    {
        var e = Emu(20, 10, "\x1b[3;8r");          // DECSTBM rows 3..8 (0-based 2..7)
        Assert.Equal(2, e.ScrollTop);
        Assert.Equal(7, e.ScrollBottom);

        e.Resize(20, 10);

        Assert.Equal(2, e.ScrollTop);
        Assert.Equal(7, e.ScrollBottom);
    }

    [Fact]
    public void ResizeToNewGeometry_StillResetsScrollRegion()
    {
        var e = Emu(20, 10, "\x1b[3;8r");

        e.Resize(20, 12);                           // a real change resets the margins (xterm behaviour)

        Assert.Equal(0, e.ScrollTop);
        Assert.Equal(11, e.ScrollBottom);
    }

    [Fact]
    public void ResizeToSameGeometry_KeepsCursorAndContent()
    {
        var e = Emu(20, 10, "hello\x1b[5;7H");
        int row = e.CursorRow, col = e.CursorCol;

        e.Resize(20, 10);

        Assert.Equal(row, e.CursorRow);
        Assert.Equal(col, e.CursorCol);
        Assert.Equal('h', (char)e.Screen[0, 0].Rune);
    }
}
