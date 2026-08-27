namespace Agwinterm.Core;

/// <summary>
/// Encodes terminal mouse reports after the host has mapped a pointer to zero-based cell and pixel
/// coordinates. Keeping this policy in Core makes the ?1016 branch testable without a Win32 window.
/// </summary>
public static class MouseReport
{
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
