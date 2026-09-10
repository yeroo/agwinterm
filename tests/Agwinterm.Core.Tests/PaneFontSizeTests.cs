namespace Agwinterm.Core.Tests;

public class PaneFontSizeTests
{
    [Fact]
    public void ZoomReturningToDefaultIsStillExplicitUntilReset()
    {
        var up = PaneFontSize.Zoom(14, 14, 1);
        var back = PaneFontSize.Zoom(up.Size, 14, -1);
        Assert.Equal((14f, true), back);
        Assert.Equal((14f, true), PaneFontSize.Restore(back.Size, 20, back.Zoomed));
        Assert.Equal((20f, false), PaneFontSize.Zoom(back.Size, 20, 0));
    }

    [Theory]
    [InlineData(14, 20, false, 20, false)]
    [InlineData(14, 20, true, 14, true)]
    [InlineData(20, 20, null, 20, false)]
    [InlineData(14, 20, null, 14, true)]
    [InlineData(0, 20, null, 20, false)]
    [InlineData(0, 20, true, 20, false)]
    public void RestorePreservesExplicitZoomAndUpdatesInheritedSize(float saved, float def, bool? zoom, float expected, bool expectedZoom)
        => Assert.Equal((expected, expectedZoom), PaneFontSize.Restore(saved, def, zoom));
}
