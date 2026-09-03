using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>
/// One image's worth of a shared-frame request, as it arrives in the <c>image.frameshm</c> JSON
/// args. Everything here comes from another process. <paramref name="Seq"/>,
/// <paramref name="Width"/>, <paramref name="Height"/>, <paramref name="Stride"/> and
/// <paramref name="Format"/> are optional cross-checks: zero means "trust the slot descriptor",
/// any other value must match it exactly, so a producer whose args and header disagree is told
/// so instead of silently getting the header's version of the truth.
/// </summary>
public readonly record struct ShmFrameRequest(
    string? Name,
    int Slot,
    long Seq = 0,
    int Width = 0,
    int Height = 0,
    int Stride = 0,
    int Format = 0);

/// <summary>
/// A frame copied out of a producer's mapping. <paramref name="Pixels"/> is tightly packed —
/// exactly <c>Width * 4 * Height</c> bytes, with the slot's row padding removed — which is the
/// shape <c>ITerminalCore.SetImageData</c> wants. It is owned by the caller and aliases nothing
/// in the mapped view, so the producer may exit the moment the call returns.
/// </summary>
public sealed record ShmFrame(int Width, int Height, int Format, byte[] Pixels);

/// <summary>
/// Opens a producer's named shared-memory mapping and copies one published frame out of it.
///
/// Every number involved — the name, the slot index, the dimensions, the stride, the offsets —
/// arrives from another process and feeds a pointer computation, so nothing is trusted: the
/// header is validated against the actual mapped view length before a single pixel byte is read,
/// and every rejection is a <see cref="ShmFrameError"/> value rather than an exception, so the
/// control server can answer <c>{"ok":false,...}</c> and keep the connection.
///
/// The mapping is opened per call rather than cached. A producer may tear down and recreate a
/// mapping under the same name between frames, and a cached handle would then be reading a
/// section nobody writes to any more. The open costs a syscall; the frame copy costs megabytes,
/// so the cache would buy nothing worth that failure mode.
///
/// See <c>docs/specs/image-frameshm.md</c> for the normative contract.
/// </summary>
public static class ShmFrameReader
{
    /// <summary>
    /// The only namespace and prefix a request may name. <c>Local\</c> keeps the object inside the
    /// caller's logon session, and the fixed suffix keeps a request from naming an arbitrary
    /// pre-existing kernel object. Pinned verbatim in the spec, so producers can hardcode it.
    /// </summary>
    public const string NamePrefix = @"Local\agwinterm-frame-";

    /// <summary>Longest name tail accepted after <see cref="NamePrefix"/>.</summary>
    public const int MaxNameSuffixLength = 128;

    /// <summary>Largest accepted width or height, in pixels. Far above any real display.</summary>
    public const int MaxDimension = 16384;

    /// <summary>Largest accepted tightly-packed frame. 1920x1080 BGRA is 8 MB; this is 32x that.</summary>
    public const long MaxFrameBytes = 256L * 1024 * 1024;

    /// <summary>Bytes per pixel in every format this transport accepts.</summary>
    private const int BytesPerPixel = 4;

    /// <summary>
    /// True if <paramref name="name"/> is a mapping name this reader will open: the exact
    /// <see cref="NamePrefix"/> followed by 1..<see cref="MaxNameSuffixLength"/> characters drawn
    /// from <c>[A-Za-z0-9._-]</c>. Excluding the backslash is the point of the character set — it
    /// is what stops a suffix from walking back out of the <c>Local\</c> namespace.
    /// </summary>
    public static bool IsAllowedName([NotNullWhen(true)] string? name)
    {
        if (name is null || !name.StartsWith(NamePrefix, StringComparison.Ordinal)) return false;
        string suffix = name[NamePrefix.Length..];
        if (suffix.Length is 0 || suffix.Length > MaxNameSuffixLength) return false;
        foreach (char c in suffix)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Opens the mapping named by <paramref name="request"/>, validates it, and copies the
    /// requested slot's pixels into a fresh tightly-packed buffer. Returns false with a typed
    /// <paramref name="error"/> for every rejection, including a mapping that does not exist
    /// because the producer died — that is ordinary input here, not an exceptional condition.
    /// </summary>
    public static bool TryReadFrame(
        in ShmFrameRequest request,
        [NotNullWhen(true)] out ShmFrame? frame,
        out ShmFrameError error)
        => TryReadFrame(request, MaxFrameBytes, out frame, out error);

    /// <summary>
    /// Opens, validates and copies a frame provided it also fits within
    /// <paramref name="copyBudgetBytes"/>. The control server passes the bytes remaining in the
    /// current composition so a request is rejected before an over-budget array is allocated.
    /// </summary>
    public static bool TryReadFrame(
        in ShmFrameRequest request,
        long copyBudgetBytes,
        [NotNullWhen(true)] out ShmFrame? frame,
        out ShmFrameError error)
        => TryAccessFrame(request, copyPixels: true, copyBudgetBytes, out frame, out error);

    /// <summary>
    /// Validates the live mapping and requested header fields without copying pixel bytes. Cache
    /// hits use this path so a dead or malformed mapping cannot bypass the reader's checks merely
    /// by repeating an accepted sequence.
    /// </summary>
    public static bool TryValidateFrame(in ShmFrameRequest request, out ShmFrameError error)
        => TryAccessFrame(request, copyPixels: false, copyBudgetBytes: 0, out _, out error);

    private static bool TryAccessFrame(
        in ShmFrameRequest request,
        bool copyPixels,
        long copyBudgetBytes,
        out ShmFrame? frame,
        out ShmFrameError error)
    {
        frame = null;
        if (!IsAllowedName(request.Name)) { error = ShmFrameError.NameRejected; return false; }

        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            try
            {
                mmf = MemoryMappedFile.OpenExisting(request.Name, MemoryMappedFileRights.Read);
                view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            }
            catch (FileNotFoundException) { error = ShmFrameError.MappingNotFound; return false; }
            catch (UnauthorizedAccessException) { error = ShmFrameError.MappingUnreadable; return false; }
            catch (ArgumentException) { error = ShmFrameError.NameRejected; return false; }
            catch (IOException) { error = ShmFrameError.MappingUnreadable; return false; }

            return TryAccessFrame(view, request, copyPixels, copyBudgetBytes, out frame, out error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A producer can shrink, close or corrupt its mapping at any instant, including
            // between the checks below and the copy that follows them. Whatever that surfaces as,
            // it is a failed frame and not a failed session.
            error = ShmFrameError.MappingUnreadable;
            frame = null;
            return false;
        }
        finally
        {
            view?.Dispose();
            mmf?.Dispose();
        }
    }

    /// <summary>
    /// The validate-and-copy half, against an already-open view. Split out so tests can drive it
    /// with a view they built themselves, and so the open path above owns nothing but lifetime.
    /// </summary>
    internal static bool TryReadFrame(
        MemoryMappedViewAccessor view,
        in ShmFrameRequest request,
        [NotNullWhen(true)] out ShmFrame? frame,
        out ShmFrameError error)
        => TryAccessFrame(view, request, copyPixels: true, MaxFrameBytes, out frame, out error);

    private static bool TryAccessFrame(
        MemoryMappedViewAccessor view,
        in ShmFrameRequest request,
        bool copyPixels,
        long copyBudgetBytes,
        out ShmFrame? frame,
        out ShmFrameError error)
    {
        ArgumentNullException.ThrowIfNull(view);
        frame = null;

        long viewLength = view.Capacity;
        var handle = view.SafeMemoryMappedViewHandle;
        if (viewLength < ShmFrameLayout.HeaderSize) { error = ShmFrameError.ShortView; return false; }

        if (request.Seq < 0) { error = ShmFrameError.BadSequence; return false; }

        // This must be an atomic acquire load directly from shared memory. Reading ready from the
        // copied header would neither prevent a torn int64 nor acquire the descriptor/pixel writes.
        long ready = ShmFrameLayout.ReadReadyAcquire(view);

        // Descriptor and pixel reads must follow the acquire. The producer publishes both before
        // its release store, and an acquire cannot retroactively order a header copy made earlier.
        Span<byte> headerBytes = stackalloc byte[ShmFrameLayout.HeaderSize];
        handle.ReadSpan(0, headerBytes);
        if (!ShmFrameLayout.TryRead(headerBytes, out var header, out error) || header is null) return false;

        if (request.Seq > 0 && ready < request.Seq)
        { error = ShmFrameError.FrameNotPublished; return false; }

        if (!ShmFrameLayout.TryGetSlot(header, request.Slot, out var slot, out error)) return false;
        if (request.Seq > 0 && request.Slot != ShmFrameLayout.SlotForSequence(request.Seq, header.SlotCount))
        { error = ShmFrameError.SequenceSlotMismatch; return false; }
        if (!ShmFrameLayout.TryGetSlotOffset(header, request.Slot, out long slotOffset, out error)) return false;

        if (slot.Width <= 0 || slot.Height <= 0 ||
            slot.Width > MaxDimension || slot.Height > MaxDimension)
        { error = ShmFrameError.BadDimensions; return false; }

        if (!IsSupportedFormat(slot.Format)) { error = ShmFrameError.UnsupportedFormat; return false; }

        long rowBytes = (long)slot.Width * BytesPerPixel;
        if (slot.Stride < rowBytes) { error = ShmFrameError.StrideTooSmall; return false; }

        long frameBytes = rowBytes * slot.Height;
        if (frameBytes > MaxFrameBytes) { error = ShmFrameError.FrameTooLarge; return false; }
        if (copyPixels && (copyBudgetBytes < 0 || frameBytes > copyBudgetBytes))
        { error = ShmFrameError.CopyBudgetExceeded; return false; }

        long slotBytes = (long)slot.Height * slot.Stride;
        if (slotBytes > header.SlotStride) { error = ShmFrameError.SlotOverflow; return false; }
        if (slotOffset < ShmFrameLayout.HeaderSize || slotBytes > viewLength - slotOffset)
        { error = ShmFrameError.OutOfView; return false; }

        // Args and header must agree. They are two statements of the same fact by the same
        // producer; a disagreement means one of them is stale, and guessing which is worse than
        // saying so.
        if ((request.Width != 0 && request.Width != slot.Width) ||
            (request.Height != 0 && request.Height != slot.Height) ||
            (request.Stride != 0 && request.Stride != slot.Stride) ||
            (request.Format != 0 && request.Format != slot.Format))
        { error = ShmFrameError.GeometryMismatch; return false; }

        if (!copyPixels)
        {
            error = ShmFrameError.None;
            return true;
        }

        var pixels = new byte[frameBytes];
        if (slot.Stride == rowBytes)
            handle.ReadSpan((ulong)slotOffset, pixels.AsSpan());
        else
        {
            // Padded rows: copy row by row and drop the padding, so the emulator never has to
            // know about a stride.
            for (int y = 0; y < slot.Height; y++)
                handle.ReadSpan(
                    (ulong)(slotOffset + y * (long)slot.Stride),
                    pixels.AsSpan((int)(y * rowBytes), (int)rowBytes));
        }

        frame = new ShmFrame(slot.Width, slot.Height, slot.Format, pixels);
        error = ShmFrameError.None;
        return true;
    }

    /// <summary>
    /// Only the two 4-bytes-per-pixel formats. The stride and size arithmetic above assumes 4 bpp,
    /// so accepting <c>Rgb = 24</c> would leave every bound a third too large.
    /// </summary>
    private static bool IsSupportedFormat(int format)
        => format == (int)KittyFormat.Bgra || format == (int)KittyFormat.Rgba;

    /// <summary>A one-line reason suitable for an <c>{"ok":false,"error":...}</c> reply.</summary>
    public static string Describe(ShmFrameError error) => error switch
    {
        ShmFrameError.None => "ok",
        ShmFrameError.ShortView => "mapping is smaller than the frame header",
        ShmFrameError.BadMagic => "mapping does not start with the AGSF magic",
        ShmFrameError.UnsupportedVersion => $"unsupported layout version (expected {ShmFrameLayout.Version})",
        ShmFrameError.BadSlotCount => $"slotCount must be {ShmFrameLayout.MinSlots}..{ShmFrameLayout.MaxSlots}",
        ShmFrameError.SlotOutOfRange => "slot index is outside the header's slotCount",
        ShmFrameError.BadPixelOffset => "pixelOffset overlaps the header or is out of range",
        ShmFrameError.BadSlotStride => "slotStride is not a usable byte stride",
        ShmFrameError.NameRejected => $@"mapping name must start with {NamePrefix} and use [A-Za-z0-9._-] after it",
        ShmFrameError.MappingNotFound => "no such mapping (producer gone?)",
        ShmFrameError.MappingUnreadable => "mapping could not be opened for reading",
        ShmFrameError.BadDimensions => $"slot width and height must be 1..{MaxDimension}",
        ShmFrameError.StrideTooSmall => "slot stride is smaller than width * 4",
        ShmFrameError.SlotOverflow => "slot pixels do not fit within slotStride",
        ShmFrameError.OutOfView => "slot pixels lie outside the mapped view",
        ShmFrameError.UnsupportedFormat => "format must be 132 (BGRA) or 32 (RGBA)",
        ShmFrameError.FrameTooLarge => $"frame exceeds {MaxFrameBytes} bytes",
        ShmFrameError.CopyBudgetExceeded => "images exceed the image.frameshm request byte budget",
        ShmFrameError.BadSequence => "requested seq must be zero or positive",
        ShmFrameError.FrameNotPublished => "requested seq is newer than the mapping's ready sequence",
        ShmFrameError.SequenceSlotMismatch => "requested slot does not match seq % slotCount",
        ShmFrameError.GeometryMismatch => "request geometry disagrees with the slot descriptor",
        _ => "invalid shared frame",
    };
}
