using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>
/// The integration adapter, validated headless: RustEmulatorCore (the class the UI will
/// consume behind `emulator-core = rust`) driven side-by-side with the managed
/// TerminalEmulator — grid snapshots, info scalars, history rows, and text properties
/// must agree through the ADAPTER's own code path (bulk copies, string marshaling),
/// not just the oracle's dump. Skips when the crate isn't built locally; CI builds it.
/// </summary>
public class RustEmulatorCoreTests
{
    private static readonly bool Available = Probe();

    private static bool Probe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agwinterm.slnx"))) dir = dir.Parent;
        if (dir is null) return false;
        string dll = Path.Combine(dir.FullName, "native", "agwinterm-core", "target", "release", "agwinterm_core.dll");
        return RustEmulatorCore.TryLoad(dll, out _);
    }

    private static void AssertAdapterParity(int cols, int rows, params string[] feeds)
    {
        var cs = new TerminalEmulator(cols, rows);
        using var rust = new RustEmulatorCore(cols, rows);
        foreach (var feed in feeds)
        {
            byte[] bytes = feed.Any(c => c > 0xFF)
                ? System.Text.Encoding.UTF8.GetBytes(feed)
                : feed.Select(c => (byte)c).ToArray();
            cs.Feed(bytes);
            rust.Feed(bytes);
        }

        var info = rust.GetInfo();
        Assert.Equal((uint)cs.Screen.Cols, info.Cols);
        Assert.Equal((uint)cs.Screen.Rows, info.Rows);
        Assert.Equal((uint)cs.CursorRow, info.CursorRow);
        Assert.Equal((uint)cs.CursorCol, info.CursorCol);
        Assert.Equal(cs.CursorVisible, info.CursorVisible != 0);
        Assert.Equal(cs.IsAltScreen, info.IsAltScreen != 0);
        Assert.Equal((uint)cs.HistoryCount, info.HistoryCount);
        Assert.Equal(cs.ScrollGeneration, info.ScrollGeneration);
        Assert.Equal(cs.BracketedPaste, info.BracketedPaste != 0);
        Assert.Equal(cs.MouseSgr, info.MouseSgr != 0);
        Assert.Equal(cs.KeyboardFlags, info.KeyboardFlags);
        Assert.Equal(cs.Title, rust.Title);
        Assert.Equal(cs.Cwd, rust.Cwd);
        Assert.Equal(cs.DumpModes(), rust.DumpModes());

        var grid = new RustEmulatorCore.NativeCell[cs.Screen.Cols * cs.Screen.Rows];
        Assert.True(rust.CopyGrid(grid));
        for (int r = 0; r < cs.Screen.Rows; r++)
            for (int c = 0; c < cs.Screen.Cols; c++)
            {
                Cell want = cs.Screen[r, c], got = grid[r * cs.Screen.Cols + c].ToCell();
                if (want != got)
                    Assert.Fail($"grid cell ({r},{c}): C# rune={want.Rune} rust rune={got.Rune}");
            }

        var hrow = new RustEmulatorCore.NativeCell[cs.Screen.Cols];
        for (int h = 0; h < cs.HistoryCount; h++)
        {
            Assert.True(rust.CopyHistoryRow(h, hrow));
            for (int c = 0; c < cs.Screen.Cols; c++)
                Assert.Equal(cs.GetHistoryCell(h, c), hrow[c].ToCell());
        }
    }

    [Fact]
    public void Adapter_ScreenAndScrollback_Agree()
    {
        if (!Available) return;
        AssertAdapterParity(40, 10,
            "\x1b[1;31mhello 🚀 中文\x1b[0m\r\n",
            "line2\r\nline3\r\nline4\r\nline5\r\nline6\r\nline7\r\nline8\r\nline9\r\nline10\r\nline11\r\nline12\r\n",
            "\x1b]0;adapter test\x07\x1b]7;file://x/y\x07",
            "\x1b[?25l\x1b[?2004h\x1b[?1006h\x1b[>3u");
    }

    [Fact]
    public void Adapter_ResizeAndAltScreen_Agree()
    {
        if (!Available) return;
        var cs = new TerminalEmulator(30, 8);
        using var rust = new RustEmulatorCore(30, 8);
        foreach (var f in new[] { "before resize\r\n", "\x1b[?1049h", "alt content", "\x1b[?1049l" })
        {
            var b = f.Select(c => (byte)c).ToArray();
            cs.Feed(b); rust.Feed(b);
        }
        cs.Resize(50, 15);
        rust.Resize(50, 15);
        var b2 = "after resize"u8.ToArray();
        cs.Feed(b2); rust.Feed(b2);

        var info = rust.GetInfo();
        Assert.Equal((50u, 15u), (info.Cols, info.Rows));
        var grid = new RustEmulatorCore.NativeCell[50 * 15];
        Assert.True(rust.CopyGrid(grid));
        for (int r = 0; r < 15; r++)
            for (int c = 0; c < 50; c++)
                Assert.Equal(cs.Screen[r, c], grid[r * 50 + c].ToCell());
    }

    [Fact]
    public void TryLoad_WrongPath_FailsLoudly()
    {
        Assert.False(RustEmulatorCore.TryLoad(@"C:\nope\missing.dll", out var err) && !RustEmulatorCore.Loaded);
        if (!RustEmulatorCore.Loaded) Assert.NotNull(err);
    }
}
