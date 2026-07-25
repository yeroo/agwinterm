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
        string dll = Path.Combine(dir.FullName, "native", "target", "release", "agwinterm_core.dll");
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

    [Fact]
    public void Adapter_SurfacesSixelImages_LikeManagedCore()
    {
        if (!Available) return;
        // A tiny sixel: "Pq" introducer, colour 1 select, three full sixels, ST.
        byte[] sixel = System.Text.Encoding.ASCII.GetBytes("\x1bPq#1~~~\x1b\\");
        var cs = new TerminalEmulator(20, 6);
        using var rust = new RustTerminalCore(20, 6);
        cs.Feed(sixel);
        rust.Feed(sixel);

        // Both must decode ONE image + ONE placement, with matching pixel dimensions and data.
        Assert.Single(cs.Placements);
        Assert.Single(rust.Placements);
        Assert.Equal(cs.Placements[0].Cols, rust.Placements[0].Cols);
        Assert.Equal(cs.Placements[0].Rows, rust.Placements[0].Rows);

        var csImg = cs.Images.Values.Single();
        var rustImg = rust.Images.Values.Single();
        Assert.Equal(csImg.Width, rustImg.Width);
        Assert.Equal(csImg.Height, rustImg.Height);
        Assert.Equal(csImg.Data.Length, rustImg.Data.Length);
        Assert.Equal(csImg.Data, rustImg.Data);   // identical RGBA — the whole point
    }

    private sealed class RecordingHost : IHostActions
    {
        public readonly List<string> Log = new();
        public void Notify(string title, string body) => Log.Add($"Notify|{title}|{body}");
        public void Progress(int state, int value) => Log.Add($"Progress|{state}|{value}");
        public void ClipboardWrite(string text) => Log.Add($"Clipboard|{text}");
        public void Respond(string reply) => Log.Add($"Respond|{reply}");
        public void Unhandled(string kind, string detail) => Log.Add($"Unhandled|{kind}|{detail}");
        public void Bell() => Log.Add("Bell");
    }

    [Fact]
    public void Adapter_HostActions_MatchManagedCore()
    {
        if (!Available) return;
        // Exercise every IHostActions member through the VT stream: OSC 9 notify, OSC 9;4 progress,
        // OSC 777 notify, OSC 52 clipboard (base64 "aGk=" -> "hi"), the kitty keyboard-flags query
        // (Respond), and one each of the Unhandled taps (CSI, C0 BEL, ESC).
        string script =
            "\x1b]9;build done\x07" +
            "\x1b]9;4;1;42\x07" +
            "\x1b]777;notify;Title;Body;x\x07" +
            "\x1b]52;c;aGk=\x07" +
            "\x1b[?u" +
            "\x1b[99z" +
            "\x07" +
            "\x1bH";
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(script);

        var mgHost = new RecordingHost();
        var rsHost = new RecordingHost();
        var cs = new TerminalEmulator(40, 10) { Host = mgHost };
        using var rust = new RustTerminalCore(40, 10) { Host = rsHost };
        cs.Feed(bytes);
        rust.Feed(bytes);

        // Same actions, same order, same payloads — through the adapter's drain path, not the oracle.
        Assert.Equal(mgHost.Log, rsHost.Log);
        // And it genuinely fired (guard against "both empty" passing vacuously).
        Assert.Contains("Clipboard|hi", rsHost.Log);
        Assert.Contains("Respond|\x1b[?0u", rsHost.Log);
        Assert.Contains("Progress|1|42", rsHost.Log);
    }

    [Fact]
    public void Adapter_FocusReporting_TracksMode_LikeManagedCore()
    {
        if (!Available) return;
        var cs = new TerminalEmulator(20, 5);
        using var rust = new RustTerminalCore(20, 5);
        Assert.False(cs.FocusReporting);
        Assert.False(rust.FocusReporting);

        var on = System.Text.Encoding.ASCII.GetBytes("\x1b[?1004h");
        cs.Feed(on); rust.Feed(on);
        Assert.True(cs.FocusReporting);
        Assert.True(rust.FocusReporting);                 // surfaced via the adapter's Info snapshot
        Assert.Equal(cs.DumpModes(), rust.DumpModes());   // and re-synthesized identically for reattach
        Assert.Contains("\x1b[?1004h", rust.DumpModes());

        var off = System.Text.Encoding.ASCII.GetBytes("\x1b[?1004l");
        cs.Feed(off); rust.Feed(off);
        Assert.False(cs.FocusReporting);
        Assert.False(rust.FocusReporting);
        Assert.Equal(cs.DumpModes(), rust.DumpModes());
    }

    [Fact]
    public void Adapter_SynchronizedOutput_ModeAndDecrqm_MatchManagedCore()
    {
        if (!Available) return;
        var mgHost = new RecordingHost();
        var rsHost = new RecordingHost();
        var cs = new TerminalEmulator(20, 5) { Host = mgHost };
        using var rust = new RustTerminalCore(20, 5) { Host = rsHost };
        void Feed(string s) { var b = System.Text.Encoding.ASCII.GetBytes(s); cs.Feed(b); rust.Feed(b); }

        Feed("\x1b[?2026$p");                              // DECRQM while reset -> reports 2 (reset)
        Assert.False(cs.SynchronizedOutput);
        Assert.False(rust.SynchronizedOutput);

        Feed("\x1b[?2026h");                               // enter synchronized output
        Assert.True(cs.SynchronizedOutput);
        Assert.True(rust.SynchronizedOutput);              // surfaced via the adapter Info snapshot
        Assert.DoesNotContain("2026", rust.DumpModes());   // transient — NOT persisted for reattach
        Assert.Equal(cs.DumpModes(), rust.DumpModes());
        Feed("\x1b[?2026$p");                              // DECRQM while set -> reports 1 (set)

        Feed("\x1b[?2026l");                               // leave synchronized output
        Assert.False(cs.SynchronizedOutput);
        Assert.False(rust.SynchronizedOutput);

        // Same DECRPM replies, same order, through both cores.
        Assert.Equal(mgHost.Log, rsHost.Log);
        Assert.Equal(new[] { "Respond|\x1b[?2026;2$y", "Respond|\x1b[?2026;1$y" }, rsHost.Log);
    }

    [Fact]
    public void Adapter_Bell_FiresThroughHost_LikeManagedCore()
    {
        if (!Available) return;
        var mgHost = new RecordingHost();
        var rsHost = new RecordingHost();
        var cs = new TerminalEmulator(20, 5) { Host = mgHost };
        using var rust = new RustTerminalCore(20, 5) { Host = rsHost };
        byte[] bytes = { 0x07, (byte)'x', 0x07 };   // BEL, a printable, BEL
        cs.Feed(bytes); rust.Feed(bytes);
        Assert.Equal(mgHost.Log, rsHost.Log);
        Assert.Equal(2, rsHost.Log.Count(l => l == "Bell"));   // BEL no longer falls through to the C0 tap
    }

    [Fact]
    public void Adapter_CursorShape_Decscusr_MatchManagedCore()
    {
        if (!Available) return;
        var cs = new TerminalEmulator(20, 5);
        using var rust = new RustTerminalCore(20, 5);
        Assert.Equal(0, cs.CursorShape);
        Assert.Equal(0, rust.CursorShape);

        // CSI 5 SP q -> blinking bar (the SP intermediate is dropped by the parser).
        var bar = System.Text.Encoding.ASCII.GetBytes("\x1b[5 q");
        cs.Feed(bar); rust.Feed(bar);
        Assert.Equal(5, cs.CursorShape);
        Assert.Equal(5, rust.CursorShape);                 // surfaced via the adapter Info snapshot
        Assert.Contains("\x1b[5 q", rust.DumpModes());     // persisted for reattach
        Assert.Equal(cs.DumpModes(), rust.DumpModes());

        // Out-of-range values are ignored; 0 resets to the terminal default.
        var bogus = System.Text.Encoding.ASCII.GetBytes("\x1b[9 q");
        cs.Feed(bogus); rust.Feed(bogus);
        Assert.Equal(5, cs.CursorShape);
        Assert.Equal(5, rust.CursorShape);

        var reset = System.Text.Encoding.ASCII.GetBytes("\x1b[0 q");
        cs.Feed(reset); rust.Feed(reset);
        Assert.Equal(0, cs.CursorShape);
        Assert.Equal(0, rust.CursorShape);
        Assert.Equal(cs.DumpModes(), rust.DumpModes());
    }

    [Fact]
    public void Adapter_DirectImageApi_RoundTrips()
    {
        if (!Available) return;
        using var rust = new RustTerminalCore(10, 4);
        Assert.False(rust.HasImage(7));
        rust.SetImageData(7, KittyFormat.Rgba, 2, 2, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
        Assert.True(rust.HasImage(7));
        rust.PlaceImage(7, 1, 2, 3, 3);
        var p = Assert.Single(rust.Placements);
        Assert.Equal((7, 1, 2, 3, 3), (p.ImageId, p.Row, p.Col, p.Cols, p.Rows));
        Assert.Equal(16, rust.Images[7].Data.Length);
        rust.ClearPlacements();
        Assert.Empty(rust.Placements);
    }
}
