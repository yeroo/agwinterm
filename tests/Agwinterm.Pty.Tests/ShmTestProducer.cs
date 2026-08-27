using System.IO.MemoryMappedFiles;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// A minimal in-process stand-in for a real <c>image.frameshm</c> producer: it owns a mapping,
/// writes whole frames into the slot the sequence names, and publishes with the release store the
/// spec requires. Nothing here is production code — it exists so the tests exercise the same bytes
/// a Chromium-side producer would write. Shared by the in-process verb tests
/// (<see cref="ControlServerFrameShmTests"/>) and the pipe integration tests
/// (<see cref="FrameShmPipeIntegrationTests"/>), so both drive one definition of "a well-behaved
/// producer" — a second copy would be free to drift out of agreement with the spec.
/// </summary>
internal sealed class ShmTestProducer : IDisposable
{
    public const int DefaultWidth = 8;
    public const int DefaultHeight = 4;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _slotCount;
    private long _seq;

    public string Name { get; }
    public int SlotCount => _slotCount;
    public long Seq => _seq;
    public int Slot => ShmFrameLayout.SlotForSequence(_seq, _slotCount);
    public (int Width, int Height, int Stride) Geometry { get; }

    public ShmTestProducer(int slotCount = 2, int width = DefaultWidth, int height = DefaultHeight,
                           int stride = 0, string tag = "ctl")
    {
        if (stride == 0) stride = width * 4;
        _slotCount = slotCount;
        Name = ShmFrameReader.NamePrefix + tag + "-" + Guid.NewGuid().ToString("N");

        long slotStride = (long)height * stride;
        long size = ShmFrameLayout.HeaderSize + slotStride * slotCount;
        _mmf = MemoryMappedFile.CreateNew(Name, size);
        _view = _mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);

        var header = new ShmFrameHeader(
            ShmFrameLayout.Version, slotCount, Flags: 0, slotStride,
            ShmFrameLayout.HeaderSize, Ready: 0,
            Enumerable.Range(0, slotCount)
                .Select(_ => new ShmSlotDescriptor(width, height, stride, (int)Core.KittyFormat.Bgra))
                .ToArray());
        var bytes = new byte[ShmFrameLayout.HeaderSize];
        ShmFrameLayout.Write(bytes, header);
        _view.WriteArray(0, bytes, 0, bytes.Length);

        Geometry = (width, height, stride);
    }

    /// <summary>
    /// Fills the next sequence's slot with <see cref="Frame"/>'s pattern and publishes it,
    /// returning the sequence number. Fill first, release fence, then store `ready` — the
    /// order the spec makes normative.
    /// </summary>
    public long Publish(byte tag)
    {
        long seq = _seq + 1;
        int slot = ShmFrameLayout.SlotForSequence(seq, _slotCount);
        var (w, h, stride) = Geometry;
        byte[] pixels = Frame(tag, w, h, stride);
        _view.WriteArray(ShmFrameLayout.HeaderSize + slot * (long)h * stride, pixels, 0, pixels.Length);

        var ready = new byte[ShmFrameLayout.HeaderSize];
        _view.ReadArray(0, ready, 0, ready.Length);
        ShmFrameLayout.WriteReadyRelease(ready, seq);
        _view.WriteArray(0, ready, 0, ready.Length);
        _seq = seq;
        return seq;
    }

    /// <summary>Padded BGRA whose every pixel carries the frame's tag, so a copy is identifiable.</summary>
    public static byte[] Frame(byte tag, int width = DefaultWidth, int height = DefaultHeight, int stride = 0)
    {
        if (stride == 0) stride = width * 4;
        var buf = new byte[height * stride];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int p = y * stride + x * 4;
                buf[p] = (byte)x;        // B
                buf[p + 1] = (byte)y;    // G
                buf[p + 2] = tag;        // R — the frame identity
                buf[p + 3] = 255;        // A
            }
        return buf;
    }

    /// <summary>The tightly-packed bytes the reader is expected to hand the emulator.</summary>
    public static byte[] Packed(byte tag, int width = DefaultWidth, int height = DefaultHeight)
        => Frame(tag, width, height, width * 4);

    public void Dispose() { _view.Dispose(); _mmf.Dispose(); }
}
