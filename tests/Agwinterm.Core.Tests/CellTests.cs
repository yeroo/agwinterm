using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class CellTests
{
    [Fact]
    public void Empty_IsSpaceWithDefaults()
    {
        var c = Cell.Empty;
        Assert.Equal(' ', c.Rune);
        Assert.Equal(Color.DefaultForeground, c.Foreground);
        Assert.Equal(Color.DefaultBackground, c.Background);
        Assert.Equal(CellAttributes.None, c.Attributes);
    }

    [Fact]
    public void FromIndex_BasicAnsiColors()
    {
        Assert.Equal(new Color(0, 0, 0), Color.FromIndex(0));       // black
        Assert.Equal(new Color(205, 0, 0), Color.FromIndex(1));     // red
        Assert.Equal(new Color(229, 229, 229), Color.FromIndex(7)); // white
    }

    [Fact]
    public void FromIndex_GrayscaleRamp()
    {
        // index 232 = first grayscale step (8,8,8)
        Assert.Equal(new Color(8, 8, 8), Color.FromIndex(232));
    }
}
