using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class SgrTests
{
    private static TerminalEmulator Feed(string s)
    {
        var t = new TerminalEmulator(20, 3);
        t.Feed(Encoding.ASCII.GetBytes(s));
        return t;
    }

    [Fact]
    public void BoldRedForeground()
    {
        var t = Feed("\x1b[1;31mX");
        var cell = t.Screen[0, 0];
        Assert.Equal('X', cell.Rune);
        Assert.True(cell.Attributes.HasFlag(CellAttributes.Bold));
        Assert.Equal(Color.FromIndex(1), cell.Foreground);
    }

    [Fact]
    public void ResetClearsPen()
    {
        var t = Feed("\x1b[31mA\x1b[0mB");
        Assert.Equal(Color.FromIndex(1), t.Screen[0, 0].Foreground);
        Assert.Equal(Color.DefaultForeground, t.Screen[0, 1].Foreground);
    }

    [Fact]
    public void TrueColorForeground()
    {
        var t = Feed("\x1b[38;2;10;20;30mZ");
        Assert.Equal(new Color(10, 20, 30), t.Screen[0, 0].Foreground);
    }

    [Fact]
    public void IndexedBackground()
    {
        var t = Feed("\x1b[48;5;4mZ");
        Assert.Equal(Color.FromIndex(4), t.Screen[0, 0].Background);
    }

    [Fact]
    public void AttributeOffCodes_ClearIndividualAttrs()
    {
        // inverse on, then off (27): later cell must NOT be inverse.
        var t = Feed("\x1b[7mA\x1b[27mB");
        Assert.True(t.Screen[0, 0].Attributes.HasFlag(CellAttributes.Inverse));
        Assert.False(t.Screen[0, 1].Attributes.HasFlag(CellAttributes.Inverse));
    }

    [Fact]
    public void NormalIntensity_22_ClearsBoldAndDim()
    {
        var t = Feed("\x1b[1mA\x1b[2mB\x1b[22mC");
        Assert.True(t.Screen[0, 0].Attributes.HasFlag(CellAttributes.Bold));
        Assert.False(t.Screen[0, 2].Attributes.HasFlag(CellAttributes.Bold));
        Assert.False(t.Screen[0, 2].Attributes.HasFlag(CellAttributes.Dim));
    }
}
