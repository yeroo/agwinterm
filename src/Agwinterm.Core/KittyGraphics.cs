namespace Agwinterm.Core;

/// <summary>Pixel format of a transmitted Kitty image payload.</summary>
public enum KittyFormat
{
    Rgb = 24,
    Rgba = 32,
    Png = 100,
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
public sealed record ImagePlacement(int ImageId, int Row, int Col, int Cols = 0, int Rows = 0);
