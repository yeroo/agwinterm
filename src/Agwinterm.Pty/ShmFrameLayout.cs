using System.Buffers.Binary;

namespace Agwinterm.Pty;

/// <summary>
/// Why a rejection happened. Every failure in the shared-frame path is a value, not an
/// exception: the bytes come from another process, so a malformed mapping is ordinary
/// input and must answer <c>{"ok":false,...}</c> rather than tear down the connection.
/// </summary>
public enum ShmFrameError
{
    None = 0,
    /// <summary>The mapped view is smaller than the fixed header.</summary>
    ShortView,
    /// <summary>First four bytes are not <see cref="ShmFrameLayout.Magic"/>.</summary>
    BadMagic,
    /// <summary>Layout version is not one this build understands.</summary>
    UnsupportedVersion,
    /// <summary>Slot count is below 2 or above <see cref="ShmFrameLayout.MaxSlots"/>.</summary>
    BadSlotCount,
    /// <summary>Slot index is negative or not below the header's slot count.</summary>
    SlotOutOfRange,
    /// <summary>Pixel offset overlaps the header, or is not a sane file offset.</summary>
    BadPixelOffset,
    /// <summary>Slot stride is non-positive, or the slot array overflows a long.</summary>
    BadSlotStride,

    // Below: rejections only <see cref="ShmFrameReader"/> can make, because they need the mapping
    // name or the length of the actual mapped view rather than the header alone.

    /// <summary>Name is outside the <see cref="ShmFrameReader.NamePrefix"/> namespace.</summary>
    NameRejected,
    /// <summary>No such mapping — normally a producer that exited.</summary>
    MappingNotFound,
    /// <summary>The mapping exists but could not be opened or viewed for reading.</summary>
    MappingUnreadable,
    /// <summary>Slot width or height is non-positive or above <see cref="ShmFrameReader.MaxDimension"/>.</summary>
    BadDimensions,
    /// <summary>Slot stride is below <c>width * 4</c>, so a row would read into the next row.</summary>
    StrideTooSmall,
    /// <summary><c>height * stride</c> exceeds the header's slot stride, so slots would overlap.</summary>
    SlotOverflow,
    /// <summary>The slot's byte range is not entirely inside the mapped view.</summary>
    OutOfView,
    /// <summary>Format is not one of the 4-bytes-per-pixel formats this transport carries.</summary>
    UnsupportedFormat,
    /// <summary>The tightly-packed frame exceeds <see cref="ShmFrameReader.MaxFrameBytes"/>.</summary>
    FrameTooLarge,
    /// <summary>The frame would exceed the remaining copy budget for its control request.</summary>
    CopyBudgetExceeded,
    /// <summary>The request sequence is negative; only zero has the "current slot" meaning.</summary>
    BadSequence,
    /// <summary>The requested sequence is ahead of the header's published <c>ready</c>.</summary>
    FrameNotPublished,
    /// <summary>A positive sequence names a different slot than <c>seq % slotCount</c>.</summary>
    SequenceSlotMismatch,
    /// <summary>The request's geometry contradicts the slot descriptor's.</summary>
    GeometryMismatch,
}

/// <summary>
/// One slot's pixel geometry, as published by the producer in the header. Width and height
/// are pixels, <paramref name="Stride"/> is bytes per row (may exceed <c>Width * 4</c> for
/// alignment padding), and <paramref name="Format"/> is a <c>KittyFormat</c> value — normally
/// <c>Bgra</c>, since that is what both Chromium and Direct2D speak.
/// </summary>
public readonly record struct ShmSlotDescriptor(int Width, int Height, int Stride, int Format);

/// <summary>
/// The decoded fixed header of a shared-frame mapping. See <c>docs/specs/image-frameshm.md</c>
/// for the normative wire description; this type is only its in-memory form.
/// </summary>
/// <param name="Version">Layout version. Only <see cref="ShmFrameLayout.Version"/> is accepted.</param>
/// <param name="SlotCount">Number of pixel slots, 2..<see cref="ShmFrameLayout.MaxSlots"/>.</param>
/// <param name="Flags">Reserved; producers write 0 and readers ignore unknown bits.</param>
/// <param name="SlotStride">Bytes between the start of consecutive slots.</param>
/// <param name="PixelOffset">Byte offset of slot 0's pixels, at or after the header.</param>
/// <param name="Ready">
/// Publish sequence of the most recently completed frame; 0 means nothing has been published.
/// The slot holding it is <c>Ready % SlotCount</c> — see <see cref="ShmFrameLayout.SlotForSequence"/>.
/// </param>
/// <param name="Slots">Per-slot geometry, <see cref="SlotCount"/> entries.</param>
public sealed record ShmFrameHeader(
    uint Version,
    int SlotCount,
    uint Flags,
    long SlotStride,
    long PixelOffset,
    long Ready,
    ShmSlotDescriptor[] Slots);

/// <summary>
/// Encodes and decodes the fixed header of an <c>image.frameshm</c> shared-memory mapping:
/// a 256-byte header followed by <see cref="ShmFrameHeader.SlotCount"/> pixel slots. The
/// producer fills a slot completely, then publishes it by storing its sequence number into
/// <c>ready</c> with a release barrier; the reader loads <c>ready</c> with an acquire barrier
/// and copies from the slot it names, so a half-written slot is never published.
///
/// A slot may only be reused after the request for the preceding sequence in that same slot has
/// returned. That is a property of the producer, not of this layout — see the spec's
/// "Producer obligations".
///
/// All fields are little-endian, matching every platform this ships on.
/// </summary>
public static class ShmFrameLayout
{
    /// <summary>Bytes 'A','G','S','F' ("AGwinterm Shared Frame"), read as a little-endian uint32.</summary>
    public const uint Magic = 0x4653_4741;

    /// <summary>The only layout version this build accepts.</summary>
    public const uint Version = 1;

    /// <summary>Fixed header size in bytes. Pixel data may start here or later.</summary>
    public const int HeaderSize = 256;

    /// <summary>Upper bound on slot count; the descriptor table is sized for it.</summary>
    public const int MaxSlots = 8;

    /// <summary>A producer must publish at least two slots for the alternation to mean anything.</summary>
    public const int MinSlots = 2;

    // Fixed-header field offsets.
    private const int MagicOffset = 0;
    private const int VersionOffset = 4;
    private const int SlotCountOffset = 8;
    private const int FlagsOffset = 12;
    private const int SlotStrideOffset = 16;
    private const int PixelOffsetOffset = 24;

    /// <summary>Byte offset of the 8-byte <c>ready</c> publish sequence.</summary>
    public const int ReadyOffset = 32;

    /// <summary>Byte offset of the per-slot descriptor table.</summary>
    public const int DescriptorTableOffset = 64;

    /// <summary>Size of one slot descriptor: width, height, stride, format, each int32.</summary>
    public const int DescriptorSize = 16;

    /// <summary>Byte offset of slot <paramref name="slot"/>'s descriptor within the header.</summary>
    public static int DescriptorOffset(int slot) => DescriptorTableOffset + slot * DescriptorSize;

    /// <summary>The slot a frame with publish sequence <paramref name="seq"/> occupies.</summary>
    public static int SlotForSequence(long seq, int slotCount)
        => slotCount <= 0 ? 0 : (int)(((seq % slotCount) + slotCount) % slotCount);

    /// <summary>
    /// Serializes <paramref name="header"/> into the first <see cref="HeaderSize"/> bytes of
    /// <paramref name="dest"/>, zeroing the reserved and unused-descriptor regions. Used by
    /// tests and by any in-process producer; real producers live in other processes and write
    /// these bytes themselves against the spec.
    /// </summary>
    public static void Write(Span<byte> dest, ShmFrameHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (dest.Length < HeaderSize)
            throw new ArgumentException($"header needs {HeaderSize} bytes, got {dest.Length}", nameof(dest));
        if (header.Slots.Length != header.SlotCount)
            throw new ArgumentException(
                $"slot count {header.SlotCount} does not match {header.Slots.Length} descriptors", nameof(header));

        var h = dest[..HeaderSize];
        h.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(h[MagicOffset..], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(h[VersionOffset..], header.Version);
        BinaryPrimitives.WriteInt32LittleEndian(h[SlotCountOffset..], header.SlotCount);
        BinaryPrimitives.WriteUInt32LittleEndian(h[FlagsOffset..], header.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(h[SlotStrideOffset..], header.SlotStride);
        BinaryPrimitives.WriteInt64LittleEndian(h[PixelOffsetOffset..], header.PixelOffset);
        BinaryPrimitives.WriteInt64LittleEndian(h[ReadyOffset..], header.Ready);
        for (int i = 0; i < header.Slots.Length; i++)
        {
            var d = h[DescriptorOffset(i)..];
            BinaryPrimitives.WriteInt32LittleEndian(d, header.Slots[i].Width);
            BinaryPrimitives.WriteInt32LittleEndian(d[4..], header.Slots[i].Height);
            BinaryPrimitives.WriteInt32LittleEndian(d[8..], header.Slots[i].Stride);
            BinaryPrimitives.WriteInt32LittleEndian(d[12..], header.Slots[i].Format);
        }
    }

    /// <summary>
    /// Decodes and sanity-checks the fixed header. Everything here is checkable from the header
    /// alone; sizing it against the actual mapped view is the reader's job, since only the reader
    /// knows how long the view is.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> src, out ShmFrameHeader? header, out ShmFrameError error)
    {
        header = null;
        if (src.Length < HeaderSize) { error = ShmFrameError.ShortView; return false; }

        if (BinaryPrimitives.ReadUInt32LittleEndian(src[MagicOffset..]) != Magic)
        { error = ShmFrameError.BadMagic; return false; }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(src[VersionOffset..]);
        if (version != Version) { error = ShmFrameError.UnsupportedVersion; return false; }

        int slotCount = BinaryPrimitives.ReadInt32LittleEndian(src[SlotCountOffset..]);
        if (slotCount < MinSlots || slotCount > MaxSlots) { error = ShmFrameError.BadSlotCount; return false; }

        long slotStride = BinaryPrimitives.ReadInt64LittleEndian(src[SlotStrideOffset..]);
        // The whole slot array has to be addressable, or SlotOffset would overflow into a
        // negative offset that then "passes" a naive bounds check.
        if (slotStride <= 0 || slotStride > long.MaxValue / slotCount)
        { error = ShmFrameError.BadSlotStride; return false; }

        long pixelOffset = BinaryPrimitives.ReadInt64LittleEndian(src[PixelOffsetOffset..]);
        if (pixelOffset < HeaderSize || pixelOffset > long.MaxValue - slotStride * slotCount)
        { error = ShmFrameError.BadPixelOffset; return false; }

        var slots = new ShmSlotDescriptor[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            var d = src[DescriptorOffset(i)..];
            slots[i] = new ShmSlotDescriptor(
                BinaryPrimitives.ReadInt32LittleEndian(d),
                BinaryPrimitives.ReadInt32LittleEndian(d[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(d[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(d[12..]));
        }

        header = new ShmFrameHeader(
            version, slotCount,
            BinaryPrimitives.ReadUInt32LittleEndian(src[FlagsOffset..]),
            slotStride, pixelOffset,
            BinaryPrimitives.ReadInt64LittleEndian(src[ReadyOffset..]),
            slots);
        error = ShmFrameError.None;
        return true;
    }

    /// <summary>Byte offset of a slot's pixels, or <see cref="ShmFrameError.SlotOutOfRange"/>.</summary>
    public static bool TryGetSlotOffset(ShmFrameHeader header, int slot, out long offset, out ShmFrameError error)
    {
        ArgumentNullException.ThrowIfNull(header);
        offset = 0;
        if (slot < 0 || slot >= header.SlotCount) { error = ShmFrameError.SlotOutOfRange; return false; }
        offset = header.PixelOffset + slot * header.SlotStride;
        error = ShmFrameError.None;
        return true;
    }

    /// <summary>Descriptor for a slot, or <see cref="ShmFrameError.SlotOutOfRange"/>.</summary>
    public static bool TryGetSlot(ShmFrameHeader header, int slot, out ShmSlotDescriptor descriptor, out ShmFrameError error)
    {
        ArgumentNullException.ThrowIfNull(header);
        descriptor = default;
        if (slot < 0 || slot >= header.SlotCount) { error = ShmFrameError.SlotOutOfRange; return false; }
        descriptor = header.Slots[slot];
        error = ShmFrameError.None;
        return true;
    }

    /// <summary>Total bytes a mapping with this header must have for every slot to be addressable.</summary>
    public static long RequiredSize(ShmFrameHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return header.PixelOffset + header.SlotStride * header.SlotCount;
    }

    /// <summary>
    /// Loads <c>ready</c> with acquire semantics: the fence after the load stops the pixel
    /// reads that follow from being hoisted above it, so the slot contents seen are at least
    /// as new as the sequence number that named them.
    /// </summary>
    public static long ReadReadyAcquire(ReadOnlySpan<byte> view)
    {
        if (view.Length < HeaderSize) return 0;
        long seq = BinaryPrimitives.ReadInt64LittleEndian(view[ReadyOffset..]);
        Interlocked.MemoryBarrier();
        return seq;
    }

    /// <summary>
    /// Stores <c>ready</c> with release semantics: the fence before the store keeps the pixel
    /// writes from sinking below it, so a reader that sees the sequence sees the whole slot.
    /// Test producers use this; real producers implement the same fence in their own language.
    /// </summary>
    public static void WriteReadyRelease(Span<byte> view, long seq)
    {
        if (view.Length < HeaderSize)
            throw new ArgumentException($"header needs {HeaderSize} bytes, got {view.Length}", nameof(view));
        Interlocked.MemoryBarrier();
        BinaryPrimitives.WriteInt64LittleEndian(view[ReadyOffset..], seq);
    }
}
