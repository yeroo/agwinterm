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
        Assert.True(c.PasteProtection);   // WT/Ghostty default: warn on risky pastes
        Assert.True(c.ClipboardWrite);    // OSC 52 writes allowed by default
    }

    [Fact]
    public void ClipboardPolicyKeys_Parse()
    {
        var c = TerminalConfig.Parse("paste-protection = false\nclipboard-write = false\n");
        Assert.False(c.PasteProtection);
        Assert.False(c.ClipboardWrite);
        var on = TerminalConfig.Parse("paste-protection = on\nclipboard-write = yes\n");
        Assert.True(on.PasteProtection);
        Assert.True(on.ClipboardWrite);
    }

    [Fact]
    public void Template_DocumentsClipboardPolicyKeys()
    {
        Assert.Contains("paste-protection", TerminalConfig.DefaultText);
        Assert.Contains("clipboard-write", TerminalConfig.DefaultText);
    }

    [Fact]
    public void NotificationFlash_ParsesValidValuesRejectsUnknown()
    {
        Assert.Equal("none", TerminalConfig.Parse("").NotificationFlash);   // default: off (agterm #215)
        Assert.Equal("once", TerminalConfig.Parse("notification-flash = once").NotificationFlash);
        Assert.Equal("until-focused", TerminalConfig.Parse("notification-flash = until-focused").NotificationFlash);
        Assert.Equal("none", TerminalConfig.Parse("notification-flash = bogus").NotificationFlash);
        Assert.Contains("notification-flash", TerminalConfig.DefaultText);
    }

    [Fact]
    public void ClaudeUpdateCheck_ParsesAndIsDocumented()
    {
        Assert.True(TerminalConfig.Parse("").ClaudeUpdateCheck);   // default: awareness on, update stays manual
        Assert.False(TerminalConfig.Parse("claude-update-check = false").ClaudeUpdateCheck);
        Assert.True(TerminalConfig.Parse("claude-update-check = on").ClaudeUpdateCheck);
        Assert.Contains("claude-update-check", TerminalConfig.DefaultText);
    }

    [Fact]
    public void SessionHost_ParsesValidValuesRejectsUnknown()
    {
        Assert.Equal("in-process", TerminalConfig.Parse("").SessionHost);   // default: today's model
        Assert.Equal("server", TerminalConfig.Parse("session-host = server").SessionHost);
        Assert.Equal("in-process", TerminalConfig.Parse("session-host = bogus").SessionHost);
        Assert.Contains("session-host", TerminalConfig.DefaultText);
    }

    [Fact]
    public void UpdateCheck_ParsesAndIsDocumented()
    {
        Assert.True(TerminalConfig.Parse("").UpdateCheck);         // default: awareness on, applying stays manual
        Assert.False(TerminalConfig.Parse("update-check = false").UpdateCheck);
        Assert.Contains("update-check", TerminalConfig.DefaultText);
        // The two knobs are independent.
        var c = TerminalConfig.Parse("update-check = false\nclaude-update-check = true\n");
        Assert.False(c.UpdateCheck);
        Assert.True(c.ClaudeUpdateCheck);
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
