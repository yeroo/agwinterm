using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class TerminalConfigTests
{
    [Fact]
    public void Defaults_WhenEmpty()
    {
        var c = TerminalConfig.Parse("");
        Assert.Equal("MesloLGSDZ Nerd Font", c.FontFamily);
        Assert.Equal(16, c.FontSize);
        Assert.Equal(CursorStyle.Bar, c.CursorStyle);
        Assert.True(c.CursorBlink);
        Assert.Equal(530, c.CursorBlinkMs);
    }

    [Fact]
    public void ParsesValues_IgnoresCommentsAndUnknown()
    {
        var c = TerminalConfig.Parse(
            "# a comment\nfont-family = Cascadia Mono\nfont-size=18\ncursor-style = block\ncursor-blink = off\nbogus = xyz\n");
        Assert.Equal("Cascadia Mono", c.FontFamily);
        Assert.Equal(18, c.FontSize);
        Assert.Equal(CursorStyle.Block, c.CursorStyle);
        Assert.False(c.CursorBlink);
    }

    [Fact]
    public void CursorStyle_Aliases()
    {
        Assert.Equal(CursorStyle.Bar, TerminalConfig.Parse("cursor-style=beam").CursorStyle);
        Assert.Equal(CursorStyle.Underline, TerminalConfig.Parse("cursor-style=underscore").CursorStyle);
        Assert.Equal(CursorStyle.Block, TerminalConfig.Parse("cursor-style=box").CursorStyle);
    }

    [Fact]
    public void DefaultText_IsParseableToDefaults()
    {
        var c = TerminalConfig.Parse(TerminalConfig.DefaultText);
        Assert.Equal(CursorStyle.Bar, c.CursorStyle);
        Assert.True(c.CursorBlink);
        Assert.Equal(16, c.FontSize);
    }
}
