namespace Agwinterm.Core;

/// <summary>Pixel format of a transmitted Kitty image payload.</summary>
public enum KittyFormat
{
    Rgb = 24,
    Rgba = 32,
    Png = 100,

    /// <summary>
    /// Raw BGRA8, straight (non-premultiplied) alpha. Not a Kitty wire format: the protocol
    /// defines only 24/32/100, so this value sits deliberately outside that range and can never
    /// be produced by parsing an APC sequence (<see cref="KittyFormats.ParseWireFormat"/> gates
    /// both parsers). It exists because the pixels arriving through <c>image.frameshm</c> are
    /// already BGRA at both ends of the pipe - Chromium's <c>paint</c> callback hands out BGRA
    /// and Direct2D wants <c>B8G8R8A8_UNORM</c> - so carrying BGRA end to end removes a
    /// full-frame channel swizzle per frame, which at 1920x1080x30fps is not free.
    /// </summary>
    Bgra = 132,
}

/// <summary>Format helpers shared by the APC parsers and the renderer's upload path.</summary>
public static class KittyFormats
{
    /// <summary>
    /// True only for the three formats the Kitty graphics protocol can carry on the wire.
    /// The APC parsers take <c>f=</c> from untrusted terminal output, so anything else - including
    /// the host-only <see cref="KittyFormat.Bgra"/> - must not be reachable that way.
    /// </summary>
    public static bool IsWireFormat(int f) => f is 24 or 32 or 100;

    /// <summary>
    /// Map an APC <c>f=</c> value to a format, falling back to <see cref="KittyFormat.Rgba"/>
    /// (the protocol default) for anything outside the wire range.
    /// </summary>
    public static KittyFormat ParseWireFormat(int f)
        => IsWireFormat(f) ? (KittyFormat)f : KittyFormat.Rgba;

    /// <summary>
    /// True when converting this format to the renderer's BGRA target has to swap the red and
    /// blue channels. <see cref="KittyFormat.Bgra"/> is already in target order, so it takes the
    /// no-swizzle route.
    /// </summary>
    public static bool NeedsChannelSwap(KittyFormat format) => format != KittyFormat.Bgra;
}

/// <summary>
/// A decoded Kitty graphics image: raw transmitted bytes (PNG container or raw
/// RGB/RGBA pixels) plus its declared format and pixel dimensions. The renderer
/// turns <see cref="Data"/> into a GPU texture.
/// </summary>
public sealed record KittyImage(int Id, KittyFormat Format, int Width, int Height, byte[] Data);

/// <summary>
/// Placement of an image on the grid: the image id and the cell (row, col) the
/// image's top-left anchors to. <see cref="Cols"/>/<see cref="Rows"/> are the cell
/// span to scale the image into (Kitty c=/r=); 0 means use the image's native pixels.
/// Scrolls with the grid like text.
/// </summary>
public sealed record ImagePlacement(
    int ImageId, int Row, int Col, int Cols = 0, int Rows = 0,
    int SrcX = 0, int SrcY = 0, int SrcW = 0, int SrcH = 0);
