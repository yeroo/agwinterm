using System.Runtime.InteropServices;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>
/// Differential oracle for the Rust port of the emulator core (native/agwinterm-core): the C#
/// implementation is the reference; the Rust crate must agree EXACTLY, stage by stage, before it
/// takes over. Tests here run only when the crate has been built (cargo build --release) — absent
/// dll = skipped (the Rust port is opt-in until CI builds it).
/// </summary>
public class RustParityTests
{
    private static readonly nint Lib = TryLoad();

    private static nint TryLoad()
    {
        // Walk up from the test bin dir to the repo root (the dir holding Agwinterm.slnx).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agwinterm.slnx"))) dir = dir.Parent;
        if (dir is null) return 0;
        string dll = Path.Combine(dir.FullName, "native", "target", "release", "agwinterm_core.dll");
        return File.Exists(dll) && NativeLibrary.TryLoad(dll, out nint h) ? h : 0;
    }

    private static T Fn<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Lib, name));

    private delegate uint AbiVersion();
    private delegate byte WcwidthOf(uint cp);

    [Fact]
    public void AbiVersion_Matches()
    {
        if (Lib == 0) return;   // crate not built — differential run is opt-in
        Assert.Equal(RustEmulatorCore.RequiredAbi, Fn<AbiVersion>("agwcore_abi_version")());
    }

    [Fact]
    public void Wcwidth_AgreesForEveryCodepoint()
    {
        if (Lib == 0) return;   // crate not built — differential run is opt-in
        var native = Fn<WcwidthOf>("agwcore_wcwidth");
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            int expected = Wcwidth.Of(cp);
            int actual = native((uint)cp);
            if (expected != actual)   // assert only on mismatch: 1.1M Assert.Equal calls are slow
                Assert.Fail($"wcwidth mismatch at U+{cp:X4}: C#={expected} rust={actual}");
        }
    }

    // ---- Module 2: cell + screen buffer ----

    /// <summary>Mirror of the crate's #[repr(C)] FfiCell — deliberately flat i32/u32 fields so the
    /// layout is unambiguous on both sides of the ABI.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FfiCell
    {
        public int Rune;
        public uint Fg, Bg, Attrs, Width;
        public uint FgKind, FgIndex, FgRgb;
        public uint BgKind, BgIndex, BgRgb;

        public static FfiCell From(Cell c) => new()
        {
            Rune = c.Rune,
            Fg = Pack(c.Foreground), Bg = Pack(c.Background),
            Attrs = (uint)c.Attributes, Width = c.Width,
            FgKind = (uint)c.FgSpec.Kind, FgIndex = c.FgSpec.Index, FgRgb = Pack(c.FgSpec.Rgb),
            BgKind = (uint)c.BgSpec.Kind, BgIndex = c.BgSpec.Index, BgRgb = Pack(c.BgSpec.Rgb),
        };

        public bool SameAs(Cell c) =>
            Rune == c.Rune && Fg == Pack(c.Foreground) && Bg == Pack(c.Background)
            && Attrs == (uint)c.Attributes && Width == c.Width
            && FgKind == (uint)c.FgSpec.Kind && FgIndex == c.FgSpec.Index && FgRgb == Pack(c.FgSpec.Rgb)
            && BgKind == (uint)c.BgSpec.Kind && BgIndex == c.BgSpec.Index && BgRgb == Pack(c.BgSpec.Rgb);

        private static uint Pack(Color c) => ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    }

    private delegate uint ColorFromIndex(byte i);
    private delegate nint ScreenNew(uint cols, uint rows);
    private delegate void ScreenFree(nint p);
    private unsafe delegate bool ScreenGet(nint p, uint row, uint col, FfiCell* @out);
    private unsafe delegate bool ScreenSet(nint p, uint row, uint col, FfiCell* cell);
    private delegate bool ScreenClear(nint p);
    private delegate bool ScreenMoveRows(nint p, uint src, uint dst, uint count);
    private unsafe delegate bool ScreenFillRow(nint p, uint row, FfiCell* cell);
    private delegate bool ScreenResize(nint p, uint cols, uint rows);

    [Fact]
    public void ColorFromIndex_AgreesForAll256()
    {
        if (Lib == 0) return;
        var native = Fn<ColorFromIndex>("agwcore_color_from_index");
        for (int i = 0; i <= 255; i++)
        {
            var c = Color.FromIndex(i);
            uint expected = ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            Assert.True(expected == native((byte)i), $"palette mismatch at {i}");
        }
    }

    /// <summary>The screen-buffer oracle: a seeded random op sequence (set / fill / move / resize /
    /// clear) driven through BOTH implementations, comparing every cell after every op.</summary>
    [Fact]
    public unsafe void ScreenBuffer_RandomOps_FullGridAgreement()
    {
        if (Lib == 0) return;
        var sNew = Fn<ScreenNew>("agwcore_screen_new");
        var sFree = Fn<ScreenFree>("agwcore_screen_free");
        var sGet = Fn<ScreenGet>("agwcore_screen_get");
        var sSet = Fn<ScreenSet>("agwcore_screen_set");
        var sClear = Fn<ScreenClear>("agwcore_screen_clear");
        var sMove = Fn<ScreenMoveRows>("agwcore_screen_move_rows");
        var sFill = Fn<ScreenFillRow>("agwcore_screen_fill_row");
        var sResize = Fn<ScreenResize>("agwcore_screen_resize");

        var rng = new Random(20260723);   // fixed seed — failures reproduce
        var cs = new ScreenBuffer(20, 10);
        nint rs = sNew(20, 10);
        Assert.NotEqual(0, rs);
        try
        {
            for (int op = 0; op < 2000; op++)
            {
                switch (rng.Next(12))
                {
                    case 0: // resize (rare-ish but crucial: top-left anchoring must agree)
                        int nc = rng.Next(1, 40), nr = rng.Next(1, 30);
                        cs.Resize(nc, nr);
                        Assert.True(sResize(rs, (uint)nc, (uint)nr));
                        break;
                    case 1:
                        cs.Clear();
                        Assert.True(sClear(rs));
                        break;
                    case 2 or 3: // fill a row
                    {
                        int row = rng.Next(cs.Rows);
                        var cell = RandomCell(rng);
                        cs.FillRow(row, cell);
                        var f = FfiCell.From(cell);
                        Assert.True(sFill(rs, (uint)row, &f));
                        break;
                    }
                    case 4 or 5: // move rows (valid ranges only — C# throws where FFI returns false)
                    {
                        if (cs.Rows < 2) break;
                        int count = rng.Next(1, cs.Rows);
                        int src = rng.Next(cs.Rows - count + 1);
                        int dst = rng.Next(cs.Rows - count + 1);
                        cs.MoveRows(src, dst, count);
                        Assert.True(sMove(rs, (uint)src, (uint)dst, (uint)count));
                        break;
                    }
                    default: // set a random cell
                    {
                        int row = rng.Next(cs.Rows), col = rng.Next(cs.Cols);
                        var cell = RandomCell(rng);
                        cs[row, col] = cell;
                        var f = FfiCell.From(cell);
                        Assert.True(sSet(rs, (uint)row, (uint)col, &f));
                        break;
                    }
                }
                // Full-grid compare after every op — first divergence pinpoints the op class.
                for (int r = 0; r < cs.Rows; r++)
                    for (int c = 0; c < cs.Cols; c++)
                    {
                        FfiCell got;
                        Assert.True(sGet(rs, (uint)r, (uint)c, &got), $"native get failed at {r},{c} after op {op}");
                        if (!got.SameAs(cs[r, c]))
                            Assert.Fail($"grid divergence at ({r},{c}) after op {op}: C# rune={cs[r, c].Rune} native rune={got.Rune}");
                    }
            }
        }
        finally { sFree(rs); }
    }

    // ---- Module 3: VT parser event-stream oracle ----

    /// <summary>Records every parser callback in the canonical encoding shared with the Rust
    /// recorder (see agwcore_vtparse_events): first divergence = exact event + payload.</summary>
    private sealed class RecordingPerformer : IParserPerformer
    {
        private readonly System.Text.StringBuilder _sb = new();
        public override string ToString() => _sb.ToString();
        private void Push(string s) { if (_sb.Length > 0) _sb.Append('\n'); _sb.Append(s); }

        public void Print(char ch) => Push($"P:{(int)ch:X4}");
        public void Execute(byte control) => Push($"E:{control:X2}");
        public void EscDispatch(char final) => Push($"ESC:{(int)final:X2}");
        public void CsiDispatch(char final, IReadOnlyList<int> parameters, char prefix)
            => Push($"CSI:{(int)final:X2}:{(int)prefix:X2}:{string.Join(",", parameters)}");
        public void OscDispatch(int command, string text) => Push($"OSC:{command}:{text}");
        public void ApcDispatch(string data) => Push($"APC:{data}");
        public void DcsDispatch(byte[] data) => Push("DCS:" + Convert.ToHexString(data).ToLowerInvariant());
    }

    private unsafe delegate byte* VtParseEvents(byte* bytes, uint len, uint* outLen);
    private unsafe delegate void FreeBuf(byte* ptr, uint len);

    private unsafe string NativeEvents(byte[] input)
    {
        var parse = Fn<VtParseEvents>("agwcore_vtparse_events");
        var free = Fn<FreeBuf>("agwcore_free_buf");
        fixed (byte* p = input.Length == 0 ? new byte[1] : input)
        {
            uint outLen;
            byte* buf = parse(p, (uint)input.Length, &outLen);
            if (buf == null) return "";
            try { return System.Text.Encoding.UTF8.GetString(buf, (int)outLen); }
            finally { free(buf, outLen); }
        }
    }

    private void AssertParserParity(byte[] input, string label)
    {
        var rec = new RecordingPerformer();
        new VtParser(rec).Feed(input);
        string cs = rec.ToString(), rust = NativeEvents(input);
        if (cs != rust)
        {
            // Pinpoint the first divergent event for the failure message.
            var a = cs.Split('\n'); var b = rust.Split('\n');
            int i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            Assert.Fail($"parser divergence [{label}] at event {i}:\n  C#:   {(i < a.Length ? a[i] : "<end>")}\n  rust: {(i < b.Length ? b[i] : "<end>")}");
        }
    }

    [Fact]
    public void VtParser_CuratedSequences_EventStreamsAgree()
    {
        if (Lib == 0) return;
        string[] curated =
        {
            "plain text",
            "\x1b[1;31mred bold\x1b[0m",
            "\x1b[38;2;10;20;30m\x1b[48;5;104mx",
            "\x1b[?25l\x1b[?1049h\x1b[?2004h\x1b[?1000;1006h",
            "\x1b[H\x1b[2J\x1b[3;7H\x1b[K",
            "\x1b]0;title with — unicode 🚀\x07",
            "\x1b]7;file://PC/C:/work\x1b\\",
            "\x1b]8;;https://x.test\x1b\\link\x1b]8;;\x1b\\",
            "\x1b]9;4;1;50\x07",
            "\x1b_Ga=T,f=100;AAAA\x1b\\",
            "\x1bP0;0;8q\"1;1;10;10#0;2;0;0;0-\x1b\\",
            "\x1bP payload with BEL terminator\x07",
            "🚀 emoji and 中文 and é and …",
            "\x1b[1;\r2H",                       // control mid-CSI
            "\x1b[99999999999999999999m",        // param overflow (unchecked wrap)
            "\x1b[;;m\x1b[;5;m",                 // empty params
            "\x1b[<64;10;20M\x1b[>0c\x1b[=1n",   // every private prefix
            "\x1b[1 q\x1b[4 t",                  // intermediates (space) then final
            "\x1b[1:2:3m",                       // colon → CsiIgnore path
            "\x1b[12\x1b[3m",                    // ESC restarts mid-CSI
            "\x1b]0;interrupted\x1b[31m",        // ESC + non-backslash after OSC = reprocess
            "\x1bM78c",        // plain ESC dispatches (: "\x1b7" would parse as U+01B7)
            "78c",             // ESC 7/8/c dispatches (\u form: "\x1b7" is the U+01B7 trap)
            "partial utf8: \xE4\xB8",            // dangling multibyte at end (no flush — stays pending)
            "bad utf8: \xE4\xB8\x41 and \x80 and \xFF",
            "overlong: \xC0\x80",                // overlong NUL — C# does NOT reject
        };
        foreach (var s in curated)
        {
            // Latin-1 byte mapping for the raw \xNN escapes; real UTF-8 cases pass through as-is.
            byte[] bytes = s.Any(c => c > 0xFF)
                ? System.Text.Encoding.UTF8.GetBytes(s)
                : s.Select(c => (byte)c).ToArray();
            AssertParserParity(bytes, s.Length > 24 ? s[..24] : s);
        }
    }

    [Fact]
    public void VtParser_RandomByteSoup_EventStreamsAgree()
    {
        if (Lib == 0) return;
        var rng = new Random(20260724);   // fixed seed — failures reproduce
        for (int stream = 0; stream < 200; stream++)
        {
            var buf = new byte[2048];
            rng.NextBytes(buf);
            if (stream % 2 == 1)
                // ESC-biased soup: hammer the state machine's transitions, not just Ground.
                for (int i = 0; i < buf.Length; i += 3 + rng.Next(5))
                    buf[i] = (byte)"\x1b[]_P;m0123456789?<=>:\x07\\"[rng.Next(24)];
            AssertParserParity(buf, $"soup#{stream}");
        }
    }

    // ---- Module 4: emulator full-state oracle ----

    private delegate nint EmuNew(uint cols, uint rows);
    private delegate void EmuFree(nint p);
    private unsafe delegate bool EmuFeed(nint p, byte* bytes, uint len);
    private delegate bool EmuResize(nint p, uint cols, uint rows);
    private unsafe delegate byte* EmuStateDump(nint p, uint* outLen);

    /// <summary>The canonical state dump, built from the C# emulator's public surface — must match
    /// agwcore_emu_state_dump byte-for-byte.</summary>
    private static string CsStateDump(TerminalEmulator e)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"cursor:{e.CursorRow},{e.CursorCol},{(e.CursorVisible ? 1 : 0)}\n");
        sb.Append($"region:{e.ScrollTop},{e.ScrollBottom}\n");
        sb.Append($"alt:{(e.IsAltScreen ? 1 : 0)}\n");
        sb.Append($"mouse:{(e.MouseClick ? 1 : 0)}{(e.MouseDrag ? 1 : 0)}{(e.MouseMotion ? 1 : 0)}{(e.MouseSgr ? 1 : 0)}\n");
        sb.Append($"paste:{(e.BracketedPaste ? 1 : 0)}\n");
        sb.Append($"focus:{(e.FocusReporting ? 1 : 0)}\n");
        sb.Append($"sync:{(e.SynchronizedOutput ? 1 : 0)}\n");
        sb.Append($"cursorshape:{e.CursorShape}\n");
        sb.Append($"kbd:{e.KeyboardFlags}\n");
        sb.Append($"title:{e.Title}\n");
        sb.Append($"cwd:{e.Cwd}\n");
        sb.Append($"gen:{e.ScrollGeneration}\n");
        foreach (var m in e.Marks)
            sb.Append($"mark:{m.PromptLine},{m.CommandLine},{m.OutputLine},{m.EndLine},{(m.ExitCode?.ToString() ?? "none")}\n");
        foreach (var kv in e.Images.OrderBy(k => k.Key))
        {
            ulong hash = 0xcbf29ce484222325;   // FNV-1a 64, matching the Rust dump
            foreach (byte b in kv.Value.Data) { hash ^= b; hash *= 0x100000001b3; }
            sb.Append($"img:{kv.Key},{(int)kv.Value.Format},{kv.Value.Width},{kv.Value.Height},{kv.Value.Data.Length},{hash:x16}\n");
        }
        foreach (var p in e.Placements)
            sb.Append($"pl:{p.ImageId},{p.Row},{p.Col},{p.Cols},{p.Rows},{p.SrcX},{p.SrcY},{p.SrcW},{p.SrcH}\n");
        int cols = e.Screen.Cols, rows = e.Screen.Rows;
        for (int h = 0; h < e.HistoryCount; h++)
        {
            sb.Append($"h{h}:");
            for (int c = 0; c < cols; c++) { if (c > 0) sb.Append(' '); DumpCell(sb, e.GetHistoryCell(h, c)); }
            sb.Append('\n');
        }
        for (int r = 0; r < rows; r++)
        {
            sb.Append($"g{r}:");
            for (int c = 0; c < cols; c++) { if (c > 0) sb.Append(' '); DumpCell(sb, e.Screen[r, c]); }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void DumpCell(System.Text.StringBuilder sb, Cell c)
    {
        static uint Pk(Color x) => ((uint)x.R << 16) | ((uint)x.G << 8) | x.B;
        sb.Append($"{c.Rune:X}.{Pk(c.Foreground):X6}.{Pk(c.Background):X6}.{(uint)c.Attributes:X}.{c.Width}");
        sb.Append($".{(byte)c.FgSpec.Kind}{c.FgSpec.Index:X2}.{Pk(c.FgSpec.Rgb):X6}");
        sb.Append($".{(byte)c.BgSpec.Kind}{c.BgSpec.Index:X2}.{Pk(c.BgSpec.Rgb):X6}");
    }

    private unsafe void AssertEmulatorParity(int cols, int rows, IEnumerable<byte[]> feeds, string label,
        (int Cols, int Rows)? resizeMidway = null)
    {
        var emuNew = Fn<EmuNew>("agwcore_emu_new");
        var emuFree = Fn<EmuFree>("agwcore_emu_free");
        var emuFeed = Fn<EmuFeed>("agwcore_emu_feed");
        var emuResize = Fn<EmuResize>("agwcore_emu_resize");
        var emuDump = Fn<EmuStateDump>("agwcore_emu_state_dump");
        var freeBuf = Fn<FreeBuf>("agwcore_free_buf");

        var cs = new TerminalEmulator(cols, rows);
        nint rs = emuNew((uint)cols, (uint)rows);
        Assert.NotEqual(0, rs);
        try
        {
            int step = 0; var list = feeds.ToList();
            foreach (var chunk in list)
            {
                cs.Feed(chunk);
                fixed (byte* bp = chunk.Length == 0 ? new byte[1] : chunk)
                    Assert.True(emuFeed(rs, bp, (uint)chunk.Length));
                if (resizeMidway is { } rm && step == list.Count / 2)
                {
                    cs.Resize(rm.Cols, rm.Rows);
                    Assert.True(emuResize(rs, (uint)rm.Cols, (uint)rm.Rows));
                }
                // Compare the FULL state after every chunk — first divergence names the chunk.
                string want = CsStateDump(cs);
                uint outLen;
                byte* buf = emuDump(rs, &outLen);
                string got = buf == null ? "" : System.Text.Encoding.UTF8.GetString(buf, (int)outLen);
                if (buf != null) freeBuf(buf, outLen);
                if (want != got)
                {
                    var a = want.Split('\n'); var b = got.Split('\n');
                    int i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
                    string al = i < a.Length ? a[i] : "<end>", bl = i < b.Length ? b[i] : "<end>";
                    Assert.Fail($"emulator divergence [{label}] chunk {step}, line {i}:\n  C#:   {Trunc(al)}\n  rust: {Trunc(bl)}");
                }
                step++;
            }
        }
        finally { emuFree(rs); }

        static string Trunc(string s) => s.Length > 220 ? s[..220] + "…" : s;
    }

    private static byte[] B(string s) =>
        s.Any(c => c > 0xFF) ? System.Text.Encoding.UTF8.GetBytes(s) : s.Select(c => (byte)c).ToArray();

    [Fact]
    public void Emulator_CuratedScenarios_FullStateAgrees()
    {
        if (Lib == 0) return;
        var scenarios = new (string Label, string[] Feeds)[]
        {
            ("wrap+scroll", new[] { "aaaaabbbbbcccccddddd", "eee\r\nfff\r\n", "ggg\r\nhhh\r\niii\r\n" }),
            ("wide-edge", new[] { "abc中", "中中中", "x中\r\n", "🚀🚀" }),
            ("controls", new[] { "ab\bX\tY\rZ", "\t\t\t\t\t\t\t\t\t\t\t\t" }),
            ("sgr", new[] { "\x1b[1;3;4;9;31;44mX", "\x1b[38;5;104;48;2;10;20;30mY", "\x1b[38;5;999mZ",
                            "\x1b[22;23;24;27;29;39;49mW", "\x1b[mV", "\x1b[38;2;300;256;257mQ" }),
            ("cursor", new[] { "\x1b[5;10H*", "\x1b[2A\x1b[3B\x1b[4C\x1b[5D*", "\x1b[99;99H*", "\x1b[0;0H*",
                               "\x1b[G\x1b[15d*", "\x1b[3G*" }),
            ("erase-bce", new[] { "\x1b[41m", "fill\x1b[2J", "\x1b[3;3Habcdef\x1b[3;5H\x1b[K", "\x1b[1K",
                                  "\x1b[3X", "\x1b[J", "\x1b[1J", "\x1b[3J", "\x1b[9K" }),
            ("region", new[] { "\x1b[3;8r", "\x1b[5;1Hone\ntwo\n", "\x1b[8;1Hbottom\n\n\n", "\x1b[1;1H\x1bM\x1bM",
                               "\x1b[2S\x1b[3T", "\x1b[0;0r", "\x1b[100;1r" }),
            ("il-dl-ich-dch", new[] { "\x1b[4;10r\x1b[5;1HAAAA\r\nBBBB\r\nCCCC", "\x1b[5;1H\x1b[2L", "\x1b[2M",
                                      "\x1b[99L", "\x1b[6;2H\x1b[3@", "\x1b[2P", "\x1b[99@", "\x1b[99P" }),
            ("alt-screen", new[] { "main1\r\nmain2", "\x1b[?1049h", "ALT", "\x1b[?1049l", "\x1b[?47h", "x",
                                   "\x1b[?47l", "\x1b[?1047h", "\x1b[?1047l" }),
            ("save-restore", new[] { "\x1b[5;5H\x1b7", "\x1b[1;1Hmoved", "\x1b8*", "\x1bE!" }),
            ("modes", new[] { "\x1b[?25l", "\x1b[?1000;1002;1006h", "\x1b[?1003h", "\x1b[?2004h",
                              "\x1b[?1002l", "\x1b[?12;7727h", "\x1b[?25h" }),
            ("osc", new[] { "\x1b]0;my title 🚀\x07", "\x1b]2;other\x1b\\", "\x1b]7;file://h/c/x\x07",
                            "\x1b]0;ctrlchars!\x07", "\x1b]777;notify;t;b\x07", "\x1b]52;c;aGk=\x07" }),
            ("ftcs", new[] { "\x1b]133;A\x07prompt$ ", "\x1b]133;B\x07cmd\r\n", "\x1b]133;C\x07out\r\n",
                             "\x1b]133;D;0\x07", "\x1b]133;A\x07", "\x1b]133;A\x07", "\x1b]133;D;bad\x07" }),
            ("kitty-kbd", new[] { "\x1b[>1u", "\x1b[=5;2u", "\x1b[=1;3u", "\x1b[<1u", "\x1b[<99u", "\x1b[=7u", "\x1b[>31u\x1b[<u" }),
            ("scroll-history", new[] { "1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7\r\n8\r\n9\r\n10\r\n11\r\n12\r\n",
                                       "\x1b[2;5r\r\n\r\n\r\n\r\n\r\n", "\x1b[r\r\n\r\n" }),
            ("unknown-seqs", new[] { "\x1b[13;37z", "\x1b[>1;2c", "\x1bZ", "\x1b[?1234h", "\x1b[?9999l", "\x07" }),
        };
        foreach (var (label, feeds) in scenarios)
        {
            AssertEmulatorParity(80, 24, feeds.Select(B), label);
            AssertEmulatorParity(20, 6, feeds.Select(B), label + "@20x6");
        }
    }

    [Fact]
    public void Emulator_ResizeMidStream_FullStateAgrees()
    {
        if (Lib == 0) return;
        var feeds = new[] { "one\r\ntwo\r\nthree\r\n\x1b[2;4r\x1b[31mfour", "\x1b[5;5Hafter", "more\r\ntext\r\n" };
        AssertEmulatorParity(40, 12, feeds.Select(B), "resize-shrink", resizeMidway: (25, 8));
        AssertEmulatorParity(20, 6, feeds.Select(B), "resize-grow", resizeMidway: (60, 20));
    }

    [Fact]
    public void Emulator_ImageScenarios_FullStateAgrees()
    {
        if (Lib == 0) return;
        var scenarios = new (string Label, string[] Feeds)[]
        {
            ("sixel-basic", new[] { "\x1bP0;0;8q#1~~~\x1b\\", "after", "\x1bPq#0;2;100;0;0!5~-~~\x07" }),
            ("sixel-scrolloff", new[] { "\x1b[24;1H", "\x1bPq#2~~~~\x1b\\", "\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n" }),
            ("sixel-hostile", new[] { "\x1bP\"1;1;2000000000;2000000000q#1~\x1b\\", "\x1bPq!2000000000?\x1b\\", "\x1bPnotasixel\x1b\\" }),
            ("kitty-basic", new[] { "\x1b_Ga=T,i=5,f=32,s=1,v=1,c=2,r=1;AAAA\x1b\\", "\x1b_Ga=p,i=5;\x1b\\", "\x1b_Ga=d,i=5;\x1b\\" }),
            ("kitty-chunked", new[] { "\x1b_Ga=T,i=9,f=24,m=1;AAAA\x1b\\", "\x1b_Gm=1;BBBB\x1b\\", "\x1b_Gm=0;CCCC\x1b\\" }),
            ("kitty-edge", new[] { "\x1b_Ga=T,i=7;!!!\x1b\\", "\x1b_Gx=1\x1b\\", "\x1b_notG\x1b\\", "\x1b_Ga=d;\x1b\\", "\x1b_Ga=q,i=3;AAAA\x1b\\" }),
            ("images-altscreen", new[] { "\x1bPq#1~~~\x1b\\", "\x1b[?1049h", "\x1bPq#3~~\x1b\\", "\x1b[?1049l" }),
        };
        foreach (var (label, feeds) in scenarios)
            AssertEmulatorParity(80, 24, feeds.Select(B), label);
    }

    [Fact]
    public void Emulator_RandomSoup_FullStateAgrees()
    {
        if (Lib == 0) return;
        var rng = new Random(20260725);
        for (int stream = 0; stream < 60; stream++)
        {
            var chunks = new List<byte[]>();
            for (int k = 0; k < 4; k++)
            {
                var buf = new byte[512];
                rng.NextBytes(buf);
                if (stream % 2 == 1)
                    for (int i = 0; i < buf.Length; i += 2 + rng.Next(4))
                        buf[i] = (byte)"\x1b[]m0123456789;?hlHJKrSTLMPX@ABCD"[rng.Next(33)];
                // Module 5 done: DCS/APC flow through both sides now — no introducer guards.
                int digits = 0;   // cap runs of digits: CSI 999999999 S would loop-scroll for minutes
                for (int i = 0; i < buf.Length; i++)
                {
                    if (buf[i] >= (byte)'0' && buf[i] <= (byte)'9') { if (++digits > 4) buf[i] = (byte)';'; }
                    else digits = 0;
                }
                chunks.Add(buf);
            }
            AssertEmulatorParity(stream % 3 == 0 ? 132 : 80, stream % 3 == 0 ? 50 : 24, chunks, $"soup#{stream}");
        }
    }

    private static Cell RandomCell(Random rng)
    {
        var fgSpec = rng.Next(3) switch
        {
            0 => ColorSpec.Default,
            1 => ColorSpec.Indexed(rng.Next(256)),
            _ => ColorSpec.FromRgb(new Color((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256))),
        };
        return new Cell(
            rng.Next(3) == 0 ? 0x1F600 + rng.Next(80) : ' ' + rng.Next(94),
            new Color((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)),
            new Color((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)),
            (CellAttributes)rng.Next(64),
            (byte)rng.Next(3),
            fgSpec,
            ColorSpec.Indexed(rng.Next(256)));
    }
}
