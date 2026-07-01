using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class ThemeTests
{
    private static TerminalEmulator Feed(string s)
    {
        var t = new TerminalEmulator(20, 3);
        t.Feed(Encoding.ASCII.GetBytes(s));
        return t;
    }

    [Fact]
    public void Sgr_SetsColorSpecs()
    {
        var t = Feed("\x1b[31mA\x1b[38;2;10;20;30mB\x1b[39mC\x1b[48;5;4mD");
        Assert.Equal(new ColorSpec(ColorSpecKind.Indexed, 1, default), t.Screen[0, 0].FgSpec);      // 31 -> indexed 1
        Assert.Equal(ColorSpec.FromRgb(new Color(10, 20, 30)), t.Screen[0, 1].FgSpec);              // 38;2 -> rgb
        Assert.Equal(ColorSpec.Default, t.Screen[0, 2].FgSpec);                                     // 39 -> default
        Assert.Equal(ColorSpec.Indexed(4), t.Screen[0, 3].BgSpec);                                  // 48;5;4 -> indexed 4
    }

    [Fact]
    public void EmptyCell_SpecsAreDefault()
    {
        Assert.Equal(ColorSpec.Default, Cell.Empty.FgSpec);
        Assert.Equal(ColorSpec.Default, Cell.Empty.BgSpec);
    }

    [Fact]
    public void DefaultTheme_ResolvesToLegacyRgb()
    {
        var th = Theme.Default;
        // Resolving a cell's spec through the default theme must equal its baked RGB (Foreground/Background).
        var t = Feed("\x1b[31;44mX");
        var cell = t.Screen[0, 0];
        Assert.Equal(cell.Foreground, th.ResolveFg(cell.FgSpec));
        Assert.Equal(cell.Background, th.ResolveBg(cell.BgSpec));
        Assert.Equal(Color.FromIndex(1), th.ResolveFg(ColorSpec.Indexed(1)));
        Assert.Equal(Color.DefaultForeground, th.ResolveFg(ColorSpec.Default));
        Assert.Equal(Color.DefaultBackground, th.ResolveBg(ColorSpec.Default));
        Assert.Equal(new Color(1, 2, 3), th.ResolveFg(ColorSpec.FromRgb(new Color(1, 2, 3))));
        Assert.Equal(Color.FromIndex(200), th.ResolveFg(ColorSpec.Indexed(200))); // 16-255 unthemed
    }

    [Fact]
    public void CustomTheme_RemapsIndexedAndDefault()
    {
        var pal = Theme.DefaultPalette();
        pal[1] = new Color(1, 1, 1); // remap "red"
        var th = new Theme { Palette = pal, DefaultBackground = new Color(250, 250, 250) };
        Assert.Equal(new Color(1, 1, 1), th.ResolveFg(ColorSpec.Indexed(1)));
        Assert.Equal(new Color(250, 250, 250), th.ResolveBg(ColorSpec.Default));
    }
}
