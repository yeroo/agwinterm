namespace Agwinterm.Core;

public readonly record struct MouseCoordinates(int Column, int Row, int PixelX, int PixelY);

/// <summary>
/// Encodes terminal mouse reports after the host has mapped a pointer to zero-based cell and pixel
/// coordinates. Keeping this policy in Core makes the ?1016 branch testable without a Win32 window.
/// </summary>
public static class MouseReport
{
    /// <summary>
    /// Map the Win32 host's DIP layout point and client-device point into one pane. Keeping the
    /// conversion beside the encoder makes DPI scaling, split-pane origins and edge clamps testable
    /// without creating a native window.
    /// </summary>
    public static MouseCoordinates MapCoordinates(
        int dipX, int dipY, int deviceX, int deviceY,
        float paneXDip, float paneYDip, float cellWidthDip, float cellHeightDip,
        float dpiScale, int columns, int rows)
    {
        int maxColumn = Math.Max(0, columns - 1), maxRow = Math.Max(0, rows - 1);
        int column = cellWidthDip > 0
            ? Math.Clamp((int)((dipX - paneXDip) / cellWidthDip), 0, maxColumn)
            : 0;
        int row = cellHeightDip > 0
            ? Math.Clamp((int)((dipY - paneYDip) / cellHeightDip), 0, maxRow)
            : 0;

        int paneX = (int)MathF.Round(paneXDip * dpiScale);
        int paneY = (int)MathF.Round(paneYDip * dpiScale);
        int maxPixelX = Math.Max(0, (int)MathF.Round(columns * cellWidthDip * dpiScale) - 1);
        int maxPixelY = Math.Max(0, (int)MathF.Round(rows * cellHeightDip * dpiScale) - 1);
        int pixelX = Math.Clamp(deviceX - paneX, 0, maxPixelX);
        int pixelY = Math.Clamp(deviceY - paneY, 0, maxPixelY);
        return new MouseCoordinates(column, row, pixelX, pixelY);
    }

    public static string EncodePointer(
        int button, int dipX, int dipY, int deviceX, int deviceY,
        float paneXDip, float paneYDip, float cellWidthDip, float cellHeightDip,
        float dpiScale, int columns, int rows, bool press, bool sgr, bool sgrPixels)
    {
        var p = MapCoordinates(
            dipX, dipY, deviceX, deviceY, paneXDip, paneYDip, cellWidthDip, cellHeightDip,
            dpiScale, columns, rows);
        return Encode(button, p.Column, p.Row, p.PixelX, p.PixelY, press, sgr, sgrPixels);
    }

    /// <summary>
    /// Encode one mouse event. SGR-Pixels (?1016) uses one-based pixel coordinates and the SGR
    /// response shape even when ?1006 was not set separately. Otherwise the existing SGR or legacy
    /// cell-coordinate encoding is returned byte-for-byte.
    /// </summary>
    public static string Encode(int button, int column, int row, int pixelX, int pixelY,
        bool press, bool sgr, bool sgrPixels)
    {
        if (sgrPixels)
            return $"\x1b[<{button};{pixelX + 1};{pixelY + 1}{(press ? 'M' : 'm')}";
        if (sgr)
            return $"\x1b[<{button};{column + 1};{row + 1}{(press ? 'M' : 'm')}";
        return "\x1b[M" + (char)(32 + (press ? button : 3)) + (char)(33 + column) + (char)(33 + row);
    }
}
