using System.Buffers.Binary;
using System.Text;

namespace Agwinterm.Core.Tests;

/// <summary>
/// Golden validation of the COMMITTED .agbf font packs in lite/assets — the exact bytes the
/// installer ships. Guards against a corrupted regeneration slipping into a release: header
/// sanity, CRC-32, sorted index, atlas bounds, and the coverage each family promises.
/// Format: fonts/generate.py docstring (v1, little-endian).
/// </summary>
public class AgbfPackTests
{
    private static readonly string AssetsDir = FindAssetsDir();

    private static string FindAssetsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "lite", "assets")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, "lite", "assets");
    }

    private readonly record struct Rec(uint Cp, uint Off, short Bx, short By, ushort W, ushort H, byte CellW, byte Flags);

    private sealed record Pack(byte[] Bytes, int Strike, ushort CellW, ushort CellH, ushort Baseline,
                               uint AtlasOff, uint AtlasLen, string Family, Rec[] Recs);

    private static Pack Load(string name)
    {
        var path = Path.Combine(AssetsDir, name);
        Assert.True(File.Exists(path), $"missing committed pack: {path}");
        var b = File.ReadAllBytes(path);
        Assert.True(b.Length > 172, "truncated header");
        Assert.Equal("AGBF", Encoding.ASCII.GetString(b, 0, 4));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
        var strike = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
        var cellW = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(12));
        var cellH = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(14));
        var baseline = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(16));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24));
        var recordsOff = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(28));
        var atlasOff = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32));
        var atlasLen = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(36));
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40));
        var family = Encoding.UTF8.GetString(b, 44, 64).TrimEnd('\0');

        Assert.Equal(172u, recordsOff);
        Assert.Equal(172u + count * 20u, atlasOff);
        Assert.Equal((long)atlasOff + atlasLen, b.LongLength);
        Assert.Equal(crc, Crc32(b.AsSpan(172)));

        var recs = new Rec[count];
        for (var i = 0; i < count; i++)
        {
            var o = (int)recordsOff + i * 20;
            recs[i] = new Rec(
                BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)),
                BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 4)),
                BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o + 8)),
                BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o + 10)),
                BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 12)),
                BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 14)),
                b[o + 16], b[o + 17]);
        }
        return new Pack(b, strike, cellW, cellH, baseline, atlasOff, atlasLen, family, recs);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var by in data)
        {
            crc ^= by;
            for (var k = 0; k < 8; k++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
        }
        return ~crc;
    }

    public static TheoryData<string, int, bool> AllPacks() => new()
    {
        { "agwin-bitmap-14.agbf", 14, false }, { "agwin-bitmap-16.agbf", 16, false },
        { "agwin-bitmap-18.agbf", 18, false }, { "agwin-bitmap-20.agbf", 20, false },
        { "agwin-bitmap-complete-14.agbf", 14, true }, { "agwin-bitmap-complete-16.agbf", 16, true },
        { "agwin-bitmap-complete-18.agbf", 18, true }, { "agwin-bitmap-complete-20.agbf", 20, true },
    };

    [Theory]
    [MemberData(nameof(AllPacks))]
    public void Header_Index_And_Atlas_AreConsistent(string name, int strike, bool complete)
    {
        var p = Load(name);
        Assert.Equal(strike, p.Strike);
        Assert.Equal(complete ? "AGWin Bitmap Complete" : "AGWin Bitmap", p.Family);
        Assert.InRange(p.CellW, 1, 64);
        Assert.True(p.CellH > p.CellW, "terminal cells are taller than wide");
        Assert.InRange(p.Baseline, 1, p.CellH);

        for (var i = 1; i < p.Recs.Length; i++)
            Assert.True(p.Recs[i - 1].Cp < p.Recs[i].Cp, $"index not strictly sorted at #{i}");

        foreach (var r in p.Recs)
        {
            Assert.True(r.CellW is 1 or 2, $"U+{r.Cp:X4}: cellWidth {r.CellW}");
            var stride = (r.Flags & 2) != 0 ? (r.W + 7) / 8 : r.W;
            Assert.True(r.Off + (long)stride * r.H <= p.AtlasLen, $"U+{r.Cp:X4}: atlas overrun");
        }
    }

    [Theory]
    [MemberData(nameof(AllPacks))]
    public void RequiredCoverage_IsPresent(string name, int strike, bool complete)
    {
        _ = strike;
        var p = Load(name);
        var byCp = p.Recs.ToDictionary(r => r.Cp);
        // Both families: synthesized box/blocks/Powerline, Cyrillic, replacement char.
        foreach (var cp in new uint[] { 0x2500, 0x2502, 0x2554, 0x253C, 0x2588, 0x2591, 0xE0B0, 0x0410, 0x044F, 0xFFFD })
            Assert.True(byCp.ContainsKey(cp), $"missing required glyph U+{cp:X4}");
        foreach (var cp in new uint[] { 0x2500, 0x2588, 0xE0B0 })
            Assert.Equal(1, byCp[cp].Flags & 1);   // synthesized from cell geometry

        if (!complete)
        {
            Assert.All(p.Recs, r => Assert.Equal(1, r.CellW));
            Assert.All(p.Recs, r => Assert.Equal(0, r.Flags & 2));   // curated family is all 8-bit
            return;
        }
        // Complete: BMP fallback — CJK/kana/hangul are wide 1-bit Unifont glyphs, Hebrew/IPA narrow.
        foreach (var (cp, wide) in new (uint, byte)[] { (0x4E2D, 2), (0x3042, 2), (0xAC00, 2), (0x05D0, 1), (0x0250, 1) })
        {
            Assert.True(byCp.TryGetValue(cp, out var r), $"missing fallback glyph U+{cp:X4}");
            Assert.Equal(wide, r.CellW);
            Assert.Equal(2, r.Flags & 2);
            Assert.Equal(4, r.Flags & 4);
        }
        Assert.True(p.Recs.Length > 60000, $"complete pack holds the BMP, got {p.Recs.Length} glyphs");
    }
}
