using System.Buffers.Binary;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// Header encode/decode for the <c>image.frameshm</c> shared mapping. Every field here arrives
/// from another process, so the rejection cases are the point — see docs/specs/image-frameshm.md.
/// </summary>
public class ShmFrameLayoutTests
{
    private const long PixelOffset = ShmFrameLayout.HeaderSize;
    private const long SlotStride = 64 * 32 * 4;

    private static ShmFrameHeader SampleHeader(int slotCount = 2, long ready = 0) => new(
        ShmFrameLayout.Version, slotCount, Flags: 0, SlotStride, PixelOffset, ready,
        Enumerable.Range(0, slotCount)
            .Select(i => new ShmSlotDescriptor(64, 32, 64 * 4, 132 + i))
            .ToArray());

    private static byte[] Encode(ShmFrameHeader h)
    {
        var buf = new byte[ShmFrameLayout.HeaderSize];
        ShmFrameLayout.Write(buf, h);
        return buf;
    }

    [Fact]
    public void RoundTripsEveryHeaderField()
    {
        var header = new ShmFrameHeader(
            ShmFrameLayout.Version, SlotCount: 3, Flags: 0, SlotStride: 8192, PixelOffset: 512,
            Ready: 7,
            [
                new ShmSlotDescriptor(1920, 1080, 7680, 132),
                new ShmSlotDescriptor(640, 480, 2560, 32),
                new ShmSlotDescriptor(1, 1, 4, 24),
            ]);

        Assert.True(ShmFrameLayout.TryRead(Encode(header), out var read, out var error));
        Assert.Equal(ShmFrameError.None, error);
        Assert.NotNull(read);
        // Field by field: ShmFrameHeader's synthesized equality compares Slots by reference.
        Assert.Equal(header.Version, read.Version);
        Assert.Equal(header.SlotCount, read.SlotCount);
        Assert.Equal(header.Flags, read.Flags);
        Assert.Equal(header.SlotStride, read.SlotStride);
        Assert.Equal(header.PixelOffset, read.PixelOffset);
        Assert.Equal(header.Ready, read.Ready);
        Assert.Equal(header.Slots, read.Slots);
    }

    [Fact]
    public void WriteEmitsTheDocumentedMagicAndVersionBytes()
    {
        var buf = Encode(SampleHeader());

        // The spec pins these bytes; a producer in another language writes them by hand.
        Assert.Equal("AGSF"u8.ToArray(), buf[..4]);
        Assert.Equal(ShmFrameLayout.Magic, BinaryPrimitives.ReadUInt32LittleEndian(buf));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4)));
    }

    [Fact]
    public void WriteZeroesDescriptorsPastTheSlotCount()
    {
        var buf = Encode(SampleHeader(slotCount: 2));
        for (int i = 2; i < ShmFrameLayout.MaxSlots; i++)
        {
            var d = buf.AsSpan(ShmFrameLayout.DescriptorOffset(i), ShmFrameLayout.DescriptorSize);
            Assert.True(d.TrimStart((byte)0).IsEmpty, $"descriptor {i} not zeroed");
        }
    }

    [Fact]
    public void WriteRejectsADescriptorCountThatDisagreesWithSlotCount()
    {
        var mismatched = SampleHeader() with { SlotCount = 3 };
        Assert.Throws<ArgumentException>(() => ShmFrameLayout.Write(new byte[ShmFrameLayout.HeaderSize], mismatched));
    }

    [Fact]
    public void WriteRejectsAShortDestination()
        => Assert.Throws<ArgumentException>(
            () => ShmFrameLayout.Write(new byte[ShmFrameLayout.HeaderSize - 1], SampleHeader()));

    [Fact]
    public void TryReadRejectsAViewShorterThanTheHeader()
    {
        var buf = Encode(SampleHeader());
        Assert.False(ShmFrameLayout.TryRead(buf.AsSpan(0, ShmFrameLayout.HeaderSize - 1), out var read, out var error));
        Assert.Equal(ShmFrameError.ShortView, error);
        Assert.Null(read);
    }

    [Fact]
    public void TryReadRejectsBadMagic()
    {
        var buf = Encode(SampleHeader());
        BinaryPrimitives.WriteUInt32LittleEndian(buf, ShmFrameLayout.Magic ^ 1);
        Assert.False(ShmFrameLayout.TryRead(buf, out _, out var error));
        Assert.Equal(ShmFrameError.BadMagic, error);
    }

    [Fact]
    public void TryReadRejectsAnAllZeroView()
    {
        // The common real case: a mapping created but never initialised.
        Assert.False(ShmFrameLayout.TryRead(new byte[ShmFrameLayout.HeaderSize], out _, out var error));
        Assert.Equal(ShmFrameError.BadMagic, error);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void TryReadRejectsAnUnknownVersion(uint version)
    {
        var buf = Encode(SampleHeader());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), version);
        Assert.False(ShmFrameLayout.TryRead(buf, out _, out var error));
        Assert.Equal(ShmFrameError.UnsupportedVersion, error);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ShmFrameLayout.MaxSlots + 1)]
    [InlineData(int.MaxValue)]
    public void TryReadRejectsAnOutOfRangeSlotCount(int slotCount)
    {
        var buf = Encode(SampleHeader());
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), slotCount);
        Assert.False(ShmFrameLayout.TryRead(buf, out _, out var error));
        Assert.Equal(ShmFrameError.BadSlotCount, error);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)] // slotStride * slotCount would overflow into a negative offset
    public void TryReadRejectsAnUnusableSlotStride(long slotStride)
    {
        var buf = Encode(SampleHeader());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16), slotStride);
        Assert.False(ShmFrameLayout.TryRead(buf, out _, out var error));
        Assert.Equal(ShmFrameError.BadSlotStride, error);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-8L)]
    [InlineData(ShmFrameLayout.HeaderSize - 1L)] // pixels would overlap the header
    [InlineData(long.MaxValue)]
    public void TryReadRejectsAPixelOffsetThatIsNotPastTheHeader(long pixelOffset)
    {
        var buf = Encode(SampleHeader());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24), pixelOffset);
        Assert.False(ShmFrameLayout.TryRead(buf, out _, out var error));
        Assert.Equal(ShmFrameError.BadPixelOffset, error);
    }

    [Fact]
    public void TryReadAcceptsPixelsStartingExactlyAtTheHeaderEnd()
    {
        var buf = Encode(SampleHeader());
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24), ShmFrameLayout.HeaderSize);
        Assert.True(ShmFrameLayout.TryRead(buf, out var read, out _));
        Assert.Equal(ShmFrameLayout.HeaderSize, read!.PixelOffset);
    }

    [Fact]
    public void SlotOffsetsFollowPixelOffsetAndStride()
    {
        var header = SampleHeader(slotCount: 3);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(ShmFrameLayout.TryGetSlotOffset(header, i, out long offset, out var error));
            Assert.Equal(ShmFrameError.None, error);
            Assert.Equal(PixelOffset + i * SlotStride, offset);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(ShmFrameLayout.MaxSlots)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void SlotAccessorsRejectAnOutOfRangeSlotIndex(int slot)
    {
        var header = SampleHeader(slotCount: 2);

        Assert.False(ShmFrameLayout.TryGetSlotOffset(header, slot, out long offset, out var offsetError));
        Assert.Equal(ShmFrameError.SlotOutOfRange, offsetError);
        Assert.Equal(0, offset);

        Assert.False(ShmFrameLayout.TryGetSlot(header, slot, out var descriptor, out var slotError));
        Assert.Equal(ShmFrameError.SlotOutOfRange, slotError);
        Assert.Equal(default, descriptor);
    }

    [Fact]
    public void TryGetSlotReturnsThatSlotsGeometry()
    {
        var header = SampleHeader(slotCount: 2);
        Assert.True(ShmFrameLayout.TryGetSlot(header, 1, out var descriptor, out _));
        Assert.Equal(new ShmSlotDescriptor(64, 32, 64 * 4, 133), descriptor);
    }

    [Fact]
    public void RequiredSizeCoversEverySlot()
        => Assert.Equal(PixelOffset + SlotStride * 3, ShmFrameLayout.RequiredSize(SampleHeader(slotCount: 3)));

    [Theory]
    [InlineData(0L, 2, 0)]
    [InlineData(1L, 2, 1)]
    [InlineData(2L, 2, 0)]
    [InlineData(3L, 2, 1)]
    [InlineData(7L, 3, 1)]
    [InlineData(long.MaxValue, 2, 1)]
    public void SlotForSequenceAlternates(long seq, int slotCount, int expected)
        => Assert.Equal(expected, ShmFrameLayout.SlotForSequence(seq, slotCount));

    [Fact]
    public void ReadyRoundTripsThroughTheReleaseAcquirePair()
    {
        var buf = Encode(SampleHeader());
        Assert.Equal(0, ShmFrameLayout.ReadReadyAcquire(buf));

        ShmFrameLayout.WriteReadyRelease(buf, 42);
        Assert.Equal(42, ShmFrameLayout.ReadReadyAcquire(buf));
        Assert.True(ShmFrameLayout.TryRead(buf, out var read, out _));
        Assert.Equal(42, read!.Ready);
    }

    [Fact]
    public void ReadReadyOnAShortViewIsZeroRatherThanAnException()
        => Assert.Equal(0, ShmFrameLayout.ReadReadyAcquire(new byte[ShmFrameLayout.HeaderSize - 1]));

    [Fact]
    public void WriteReadyRejectsAShortView()
        => Assert.Throws<ArgumentException>(
            () => ShmFrameLayout.WriteReadyRelease(new byte[ShmFrameLayout.HeaderSize - 1], 1));
}
