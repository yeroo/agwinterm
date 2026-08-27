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
}
