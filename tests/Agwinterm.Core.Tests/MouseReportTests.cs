using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class MouseReportTests
{
    [Fact]
    public void SgrPixelsUsesOneBasedPixelCoordinates()
    {
        string report = MouseReport.Encode(
            button: 0, column: 4, row: 2, pixelX: 47, pixelY: 51,
            press: true, sgr: true, sgrPixels: true);

        Assert.Equal("\x1b[<0;48;52M", report);
    }

    [Fact]
    public void SgrPixelsSelectsSgrShapeWithoutASeparate1006Mode()
    {
        string report = MouseReport.Encode(
            button: 0, column: 4, row: 2, pixelX: 47, pixelY: 51,
            press: false, sgr: false, sgrPixels: true);

        Assert.Equal("\x1b[<0;48;52m", report);
    }

    [Fact]
    public void Without1016SgrEncodingRemainsCellBased()
    {
        string report = MouseReport.Encode(
            button: 0, column: 4, row: 2, pixelX: 47, pixelY: 51,
            press: true, sgr: true, sgrPixels: false);

        Assert.Equal("\x1b[<0;5;3M", report);
    }

    [Fact]
    public void Without1016LegacyEncodingIsByteIdentical()
    {
        string press = MouseReport.Encode(
            button: 0, column: 4, row: 2, pixelX: 47, pixelY: 51,
            press: true, sgr: false, sgrPixels: false);
        string release = MouseReport.Encode(
            button: 0, column: 4, row: 2, pixelX: 47, pixelY: 51,
            press: false, sgr: false, sgrPixels: false);

        Assert.Equal("\x1b[M" + (char)32 + (char)37 + (char)35, press);
        Assert.Equal("\x1b[M" + (char)35 + (char)37 + (char)35, release);
    }

    [Fact]
    public void SubCellMovementIsDistinctOnlyUnder1016()
    {
        string PixelReport(int pixelX, bool sgrPixels) => MouseReport.Encode(
            button: 32, column: 4, row: 2, pixelX: pixelX, pixelY: 51,
            press: true, sgr: true, sgrPixels: sgrPixels);

        Assert.NotEqual(PixelReport(41, true), PixelReport(47, true));
        Assert.Equal(PixelReport(41, false), PixelReport(47, false));
    }

    [Fact]
    public void PointerMappingUsesDpiScaledPaneOriginsOnBothAxes()
    {
        var p = MouseReport.MapCoordinates(
            dipX: 111, dipY: 72, deviceX: 167, deviceY: 108,
            paneXDip: 100, paneYDip: 50, cellWidthDip: 8, cellHeightDip: 20,
            dpiScale: 1.5f, columns: 10, rows: 4);

        Assert.Equal(new MouseCoordinates(Column: 1, Row: 1, PixelX: 17, PixelY: 33), p);
    }

    [Fact]
    public void PointerMappingClampsCellsAndPixelsToThePaneEdges()
    {
        var before = MouseReport.MapCoordinates(
            0, 0, 0, 0, 100, 50, 8, 20, 1.25f, 10, 4);
        var after = MouseReport.MapCoordinates(
            1000, 1000, 1000, 1000, 100, 50, 8, 20, 1.25f, 10, 4);

        Assert.Equal(new MouseCoordinates(0, 0, 0, 0), before);
        Assert.Equal(new MouseCoordinates(9, 3, 99, 99), after);
    }

    [Fact]
    public void WheelClientDeviceCoordinatesUseTheSamePixelMappingAsButtonEvents()
    {
        string report = MouseReport.EncodePointer(
            button: 64, dipX: 46, dipY: 30, deviceX: 69, deviceY: 45,
            paneXDip: 40, paneYDip: 20, cellWidthDip: 8, cellHeightDip: 16,
            dpiScale: 1.5f, columns: 12, rows: 6,
            press: true, sgr: false, sgrPixels: true);

        Assert.Equal("\x1b[<64;10;16M", report);
    }
}
