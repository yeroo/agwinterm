using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class QuickTerminalTests
{
    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("Ctrl+Alt+backtick", 3, 192)]
    [InlineData(" alt + shift + z ", 5, 90)]
    [InlineData("ctrl+0", 2, 48)]
    [InlineData("alt+f11", 1, 122)]
    public void SupportedChords(string text, uint mods, uint key)
    {
        Assert.True(QuickHotkey.TryParse(text, out var chord));
        Assert.Equal(new QuickHotkey(mods, key), chord);
    }

    [Theory]
    [InlineData("a")][InlineData("shift+a")][InlineData("win+a")]
    [InlineData("ctrl+f12")][InlineData("ctrl+ctrl+a")][InlineData("ctrl++a")]
    [InlineData("ctrl+a|alt+b")][InlineData("ctrl+")][InlineData("ctrl+printscreen")]
    public void UnsupportedChords(string text) => Assert.False(QuickHotkey.TryParse(text, out _));

    [Theory]
    [InlineData(40, 800, 400, -1400, 500)]
    [InlineData(90, 1800, 900, -1900, 250)]
    [InlineData(1, 800, 400, -1400, 500)]
    [InlineData(999, 1800, 900, -1900, 250)]
    public void MonitorGeometry(int percent, int width, int height, int x, int y)
        => Assert.Equal((x, y, width, height), QuickTerminalGeometry.Frame(-2000, 200, 2000, 1000, percent));

    [Fact] public void ConfigDefaultsAndBounds()
    {
        var c = TerminalConfig.Parse("");
        Assert.Equal(70, c.QuickTerminalSize); Assert.Equal("", c.QuickTerminalHotkey);
        Assert.Equal(40, TerminalConfig.Parse("quick-terminal-size = -1").QuickTerminalSize);
        Assert.Equal(90, TerminalConfig.Parse("quick-terminal-size = 999").QuickTerminalSize);
        Assert.Equal(70, TerminalConfig.Parse("quick-terminal-size = nope").QuickTerminalSize);
        Assert.Equal("ctrl+alt+q", TerminalConfig.Parse("quick-terminal-hotkey = ctrl+alt+q").QuickTerminalHotkey);
    }
}
