using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// <see cref="ShmFrameReader"/> against real <see cref="MemoryMappedFile"/>s. The rejections are
/// the point: every field here arrives from another process and feeds a pointer computation, so a
/// lying producer must get <c>false</c> plus a reason and never an exception or an overrun.
/// </summary>
public class ShmFrameReaderTests : IDisposable
{
    private const int Width = 8;
    private const int Height = 4;
    private const int Stride = Width * 4;
    private const long SlotStride = Stride * Height;
    private const long PixelOffset = ShmFrameLayout.HeaderSize;

    private readonly List<MemoryMappedFile> _open = [];

    public void Dispose()
    {
        foreach (var m in _open) m.Dispose();
        _open.Clear();
        GC.SuppressFinalize(this);
    }

    private static string FreshName() => ShmFrameReader.NamePrefix + "test-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Creates a mapping of <paramref name="capacity"/> bytes, hands its bytes to
    /// <paramref name="fill"/>, and keeps it open for the test's lifetime — the section dies with
    /// the last handle, so the reader would find nothing if we let go here.
    /// </summary>
    private string CreateMapping(long capacity, Action<Span<byte>> fill)
    {
        string name = FreshName();
        var mmf = MemoryMappedFile.CreateNew(name, capacity);
        _open.Add(mmf);
        using (var view = mmf.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite))
        {
            var bytes = new byte[capacity];
            fill(bytes);
            view.WriteArray(0, bytes, 0, bytes.Length);
        }
        return name;
    }

    private static ShmFrameHeader Header(
        int slotCount = 2,
        long ready = 1,
        long slotStride = SlotStride,
        long pixelOffset = PixelOffset,
        int width = Width,
        int height = Height,
        int stride = Stride,
        int format = (int)KittyFormat.Bgra,
        uint version = ShmFrameLayout.Version) => new(
            version, slotCount, Flags: 0, slotStride, pixelOffset, ready,
            Enumerable.Range(0, slotCount)
                .Select(_ => new ShmSlotDescriptor(width, height, stride, format))
                .ToArray());

    /// <summary>A recognisable pattern: each pixel encodes its own (x, y).</summary>
    private static byte[] Pattern(int width, int height, int stride, byte tag)
    {
        var buf = new byte[height * stride];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int p = y * stride + x * 4;
                buf[p] = (byte)x;
                buf[p + 1] = (byte)y;
                buf[p + 2] = tag;
                buf[p + 3] = 255;
            }
        return buf;
    }

    /// <summary>A mapping holding <paramref name="header"/> and a pattern in every slot.</summary>
    private string CreateFrameMapping(ShmFrameHeader header, byte tag = 0x7f, long? capacity = null)
    {
        long size = capacity ?? ShmFrameLayout.RequiredSize(header);
        return CreateMapping(size, span =>
        {
            ShmFrameLayout.Write(span, header);
            for (int i = 0; i < header.SlotCount; i++)
            {
                var d = header.Slots[i];
                // Only fill slots whose geometry is actually fillable. Several tests hand the
                // reader a header that lies — a stride below width*4, dimensions that overflow —
                // and the lie belongs in the header, not in a scribble over the mapping.
                if (d.Width <= 0 || d.Height <= 0 || d.Stride < (long)d.Width * 4) continue;
                if ((long)d.Height * d.Stride > header.SlotStride) continue;
                long off = header.PixelOffset + i * header.SlotStride;
                if (off < ShmFrameLayout.HeaderSize || off + (long)d.Height * d.Stride > span.Length) continue;
                Pattern(d.Width, d.Height, d.Stride, (byte)(tag + i)).CopyTo(span[(int)off..]);
            }
        });
    }

    // ---- name validation ------------------------------------------------------------------

    [Theory]
    [InlineData(@"Local\agwinterm-frame-browser-1")]
    [InlineData(@"Local\agwinterm-frame-a")]
    [InlineData(@"Local\agwinterm-frame-a.b_c-1")]
    public void AcceptsNamesInsideThePrefix(string name) => Assert.True(ShmFrameReader.IsAllowedName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"Local\agwinterm-frame-")]                 // prefix with no suffix
    [InlineData(@"Global\agwinterm-frame-x")]               // wrong namespace
    [InlineData(@"agwinterm-frame-x")]                      // no namespace at all
    [InlineData(@"local\agwinterm-frame-x")]                // spec pins the literal prefix
    [InlineData(@"Local\agwinterm-frame-a\..\..\other")]    // backslash walks out of the namespace
    [InlineData(@"Local\agwinterm-frame-a b")]              // space
    [InlineData(@"Local\other-object")]
    [InlineData(@"Local\SM0:1234:120:WilError_03")]         // a real pre-existing kernel object
    public void RejectsNamesOutsideThePrefix(string? name) => Assert.False(ShmFrameReader.IsAllowedName(name));

    [Fact]
    public void RejectsAnOverlongNameSuffix()
    {
        Assert.True(ShmFrameReader.IsAllowedName(ShmFrameReader.NamePrefix + new string('a', ShmFrameReader.MaxNameSuffixLength)));
        Assert.False(ShmFrameReader.IsAllowedName(ShmFrameReader.NamePrefix + new string('a', ShmFrameReader.MaxNameSuffixLength + 1)));
    }

    [Fact]
    public void ReadingANameOutsideThePrefixFailsWithoutOpeningAnything()
    {
        var request = new ShmFrameRequest(@"Global\anything", 0);
        Assert.False(ShmFrameReader.TryReadFrame(request, out var frame, out var error));
        Assert.Null(frame);
        Assert.Equal(ShmFrameError.NameRejected, error);
    }

    // ---- the success case -----------------------------------------------------------------

    [Fact]
    public void ReadsKnownBytesOutOfARealMapping()
    {
        string name = CreateFrameMapping(Header(ready: 1));

        Assert.True(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, Slot: 1, Seq: 1), out var frame, out var error));
        Assert.Equal(ShmFrameError.None, error);
        Assert.NotNull(frame);
        Assert.Equal(Width, frame.Width);
        Assert.Equal(Height, frame.Height);
        Assert.Equal((int)KittyFormat.Bgra, frame.Format);
        Assert.Equal(1, frame.Seq);
        Assert.Equal(Width * Height * 4, frame.Pixels.Length);
        // Slot 1's tag, and the (x, y) pattern, byte for byte.
        Assert.Equal(Pattern(Width, Height, Stride, 0x80), frame.Pixels);
    }

    [Fact]
    public void ReadsTheRequestedSlotNotTheFirstOne()
    {
        string name = CreateFrameMapping(Header(slotCount: 3, ready: 2), tag: 0x10);

        Assert.True(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, Slot: 0), out var first, out _));
        Assert.True(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, Slot: 2), out var third, out _));
        Assert.Equal(0x10, first!.Pixels[2]);
        Assert.Equal(0x12, third!.Pixels[2]);
    }

    [Fact]
    public void DropsRowPaddingSoPixelsComeBackTightlyPacked()
    {
        const int padded = Stride + 32;
        var header = Header(stride: padded, slotStride: padded * Height);
        string name = CreateFrameMapping(header);

        Assert.True(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, Slot: 0, Stride: padded), out var frame, out var error));
        Assert.Equal(ShmFrameError.None, error);
        Assert.Equal(Width * Height * 4, frame!.Pixels.Length);
        Assert.Equal(Pattern(Width, Height, Stride, 0x7f), frame.Pixels);
    }

    [Fact]
    public void AcceptsRgbaAsWellAsBgra()
    {
        string name = CreateFrameMapping(Header(format: (int)KittyFormat.Rgba));

        Assert.True(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out var frame, out _));
        Assert.Equal((int)KittyFormat.Rgba, frame!.Format);
    }

    [Fact]
    public void ReadsAFreshFrameAfterTheProducerRepublishes()
    {
        string name = CreateFrameMapping(Header(ready: 1));
        // Producer overwrites slot 0 and bumps ready, exactly as the spec's publish step does.
        using (var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite))
        using (var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite))
        {
            var pixels = Pattern(Width, Height, Stride, 0xAB);
            view.WriteArray(PixelOffset, pixels, 0, pixels.Length);
            var seq = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(seq, 2);
            view.WriteArray(ShmFrameLayout.ReadyOffset, seq, 0, seq.Length);
        }

        Assert.True(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, Slot: 0, Seq: 2), out var frame, out _));
        Assert.Equal(2, frame!.Seq);
        Assert.Equal(0xAB, frame.Pixels[2]);
    }

    // ---- header rejections, through a real mapping ------------------------------------------

    [Fact]
    public void RejectsAViewShorterThanTheHeader()
    {
        // Driven through the internal overload on purpose: Windows rounds a section up to a page,
        // so a real mapping is never under 4 KB and this guard is unreachable from the open path.
        // It still has to hold, because it is what makes the header read below it in-bounds.
        string name = CreateFrameMapping(Header());
        using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        using var view = mmf.CreateViewAccessor(0, ShmFrameLayout.HeaderSize - 1, MemoryMappedFileAccess.Read);

        Assert.False(ShmFrameReader.TryReadFrame(view, new ShmFrameRequest(name, 0), out var frame, out var error));
        Assert.Null(frame);
        Assert.Equal(ShmFrameError.ShortView, error);
    }

    [Fact]
    public void RejectsABadMagic()
    {
        string name = CreateFrameMapping(Header());
        Poke(name, 0, 0xDEADBEEF);

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out _, out var error));
        Assert.Equal(ShmFrameError.BadMagic, error);
    }

    [Fact]
    public void RejectsAnUnknownVersion()
    {
        string name = CreateFrameMapping(Header());
        Poke(name, 4, ShmFrameLayout.Version + 1);

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out _, out var error));
        Assert.Equal(ShmFrameError.UnsupportedVersion, error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void RejectsASlotIndexOutsideTheSlotCount(int slot)
    {
        string name = CreateFrameMapping(Header(slotCount: 2));

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, slot), out _, out var error));
        Assert.Equal(ShmFrameError.SlotOutOfRange, error);
    }

    // ---- geometry rejections ----------------------------------------------------------------

    [Fact]
    public void RejectsAStrideBelowFourBytesPerPixel()
    {
        // A short stride would make row y read into row y+1's pixels — in bounds, but wrong.
        string name = CreateFrameMapping(Header(stride: Stride - 4));

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out _, out var error));
        Assert.Equal(ShmFrameError.StrideTooSmall, error);
    }

    [Theory]
    [InlineData(0, Height)]
    [InlineData(Width, 0)]
    [InlineData(-1, Height)]
    [InlineData(Width, -1)]
    [InlineData(int.MinValue, Height)]
    [InlineData(ShmFrameReader.MaxDimension + 1, Height)]
    [InlineData(Width, ShmFrameReader.MaxDimension + 1)]
    public void RejectsNonPositiveAndOversizedDimensions(int width, int height)
    {
        // Capacity stays small: the point is that the numbers are refused before any copy is sized
        // from them, so the mapping never has to be as large as the header claims.
        var header = Header(width: width, height: height);
        string name = CreateFrameMapping(header, capacity: ShmFrameLayout.HeaderSize + SlotStride * 2);

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out _, out var error));
        Assert.Equal(ShmFrameError.BadDimensions, error);
    }

    [Fact]
    public void RejectsDimensionsThatOverflowIntArithmetic()
    {
        // width * height * 4 overflows int32 and lands on a small positive number; the dimension
        // bound catches it long before the multiply matters.
        var header = Header(width: 0x10000, height: 0x10000, stride: 0x40000);
        string name = CreateFrameMapping(header, capacity: ShmFrameLayout.HeaderSize + SlotStride * 2);

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out _, out var error));
        Assert.Equal(ShmFrameError.BadDimensions, error);
    }

    [Fact]
    public void RejectsASlotWhosePixelsDoNotFitItsSlotStride()
    {
        // Descriptor says 4 rows of 32 bytes but the slots are only 3 rows apart, so slot 0's
        // last row is slot 1's first row.
        var header = Header(slotStride: Stride * (Height - 1));
        string name = CreateFrameMapping(header, capacity: ShmFrameLayout.HeaderSize + SlotStride * 4);

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out _, out var error));
        Assert.Equal(ShmFrameError.SlotOverflow, error);
    }

    [Fact]
    public void RejectsASlotThatRunsPastTheEndOfTheView()
    {
        // A truthful, self-consistent header over a mapping that is simply too small — the case a
        // producer hits by sizing its CreateNew wrong, and the one a liar uses to read our heap.
        // Slot 1 sits a megabyte in, far past any page rounding, so the bound is what rejects it.
        var header = Header(slotCount: 2, slotStride: 1L << 20);
        string name = CreateFrameMapping(header, capacity: 4096);

        Assert.True(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out _, out var ok));
        Assert.Equal(ShmFrameError.None, ok);

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 1), out _, out var error));
        Assert.Equal(ShmFrameError.OutOfView, error);
    }

    [Fact]
    public void RejectsAPixelOffsetInsideTheHeader()
    {
        var header = Header(pixelOffset: 8);
        string name = CreateFrameMapping(header, capacity: ShmFrameLayout.HeaderSize + SlotStride * 2);

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out _, out var error));
        Assert.Equal(ShmFrameError.BadPixelOffset, error);
    }

    [Fact]
    public void RejectsAnUnsupportedFormat()
    {
        // PNG and RGB are real KittyFormat values, but neither is four bytes per pixel, so every
        // stride and size bound above would be wrong for them.
        foreach (int format in new[] { (int)KittyFormat.Png, (int)KittyFormat.Rgb, 0, -7 })
        {
            string name = CreateFrameMapping(Header(format: format));
            Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out _, out var error));
            Assert.Equal(ShmFrameError.UnsupportedFormat, error);
        }
    }

    [Fact]
    public void RejectsArgsThatContradictTheSlotDescriptor()
    {
        string name = CreateFrameMapping(Header());

        foreach (var request in new[]
        {
            new ShmFrameRequest(name, 0, Width: Width + 1),
            new ShmFrameRequest(name, 0, Height: Height + 1),
            new ShmFrameRequest(name, 0, Stride: Stride + 4),
            new ShmFrameRequest(name, 0, Format: (int)KittyFormat.Rgba),
        })
        {
            Assert.False(ShmFrameReader.TryReadFrame(request, out _, out var error));
            Assert.Equal(ShmFrameError.GeometryMismatch, error);
        }
    }

    [Fact]
    public void RejectsNegativeOptionalGeometryRatherThanTreatingItAsOmitted()
    {
        string name = CreateFrameMapping(Header());

        foreach (var request in new[]
        {
            new ShmFrameRequest(name, 0, Width: -1),
            new ShmFrameRequest(name, 0, Height: -1),
            new ShmFrameRequest(name, 0, Stride: -1),
            new ShmFrameRequest(name, 0, Format: -1),
        })
        {
            Assert.False(ShmFrameReader.TryReadFrame(request, out _, out var error));
            Assert.Equal(ShmFrameError.GeometryMismatch, error);
        }
    }

    [Fact]
    public void RejectsANegativeSequenceRatherThanTreatingItLikeCurrent()
    {
        string name = CreateFrameMapping(Header());

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0, Seq: -1), out _, out var error));
        Assert.Equal(ShmFrameError.BadSequence, error);
    }

    [Fact]
    public void RejectsASequenceTheProducerHasNotPublishedYet()
    {
        string name = CreateFrameMapping(Header(ready: 3));

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0, Seq: 4), out _, out var error));
        Assert.Equal(ShmFrameError.FrameNotPublished, error);

        // Anything already published, or no sequence at all, is fine.
        Assert.True(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 1, Seq: 3), out _, out _));
        Assert.True(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out _, out _));
    }

    [Fact]
    public void RejectsAPositiveSequencePairedWithAnotherValidSlot()
    {
        string name = CreateFrameMapping(Header(slotCount: 3, ready: 4));

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, Slot: 2, Seq: 4), out _, out var error));
        Assert.Equal(ShmFrameError.SequenceSlotMismatch, error);

        Assert.True(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, Slot: 1, Seq: 4), out _, out error));
        Assert.Equal(ShmFrameError.None, error);
    }

    [Fact]
    public void EnforcesThePackedFrameLimitBeforeTrustingTheMappedExtent()
    {
        const int width = ShmFrameReader.MaxDimension;
        const int heightAtLimit = (int)(ShmFrameReader.MaxFrameBytes / (width * 4L));
        const int stride = width * 4;

        string atLimit = CreateFrameMapping(
            Header(ready: 0, width: width, height: heightAtLimit, stride: stride,
                   slotStride: (long)heightAtLimit * stride),
            capacity: 4096);
        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(atLimit, 0), out _, out var atLimitError));
        Assert.Equal(ShmFrameError.OutOfView, atLimitError);

        string aboveLimit = CreateFrameMapping(
            Header(ready: 0, width: width, height: heightAtLimit + 1, stride: stride,
                   slotStride: (long)(heightAtLimit + 1) * stride),
            capacity: 4096);
        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(aboveLimit, 0), out _, out var aboveLimitError));
        Assert.Equal(ShmFrameError.FrameTooLarge, aboveLimitError);
    }

    [Fact]
    public void EnforcesTheCallersCopyBudgetBeforeAllocatingPixels()
    {
        string name = CreateFrameMapping(Header());
        long frameBytes = Width * Height * 4L;

        Assert.False(ShmFrameReader.TryReadFrame(
            new ShmFrameRequest(name, 0), frameBytes - 1, out var frame, out var error));

        Assert.Null(frame);
        Assert.Equal(ShmFrameError.CopyBudgetExceeded, error);
    }

    // ---- a producer that is gone --------------------------------------------------------------

    [Fact]
    public void TreatsAMissingMappingAsAnOrdinaryFailure()
    {
        var request = new ShmFrameRequest(FreshName(), 0);

        Assert.False(ShmFrameReader.TryReadFrame(request, out var frame, out var error));
        Assert.Null(frame);
        Assert.Equal(ShmFrameError.MappingNotFound, error);
    }

    [Fact]
    public void TreatsAMappingClosedBetweenFramesAsAnOrdinaryFailure()
    {
        string name = CreateFrameMapping(Header());
        Assert.True(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out _, out _));

        // The producer exits: the last handle goes, and the section goes with it.
        Dispose();

        Assert.False(ShmFrameReader.TryReadFrame(new ShmFrameRequest(name, 0), out _, out var error));
        Assert.Equal(ShmFrameError.MappingNotFound, error);
    }

    [Fact]
    public void EveryErrorHasItsOwnDescription()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ShmFrameError e in Enum.GetValues<ShmFrameError>())
        {
            string text = ShmFrameReader.Describe(e);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.True(seen.Add(text), $"{e} shares its description with another error");
        }
    }

    /// <summary>Overwrites a uint32 header field in place, the way a lying producer would.</summary>
    private static void Poke(string name, long offset, uint value)
    {
        using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        view.WriteArray(offset, buf, 0, buf.Length);
    }
}
