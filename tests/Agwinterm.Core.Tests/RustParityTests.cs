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
        string dll = Path.Combine(dir.FullName, "native", "agwinterm-core", "target", "release", "agwinterm_core.dll");
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
        Assert.Equal(2u, Fn<AbiVersion>("agwcore_abi_version")());
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
