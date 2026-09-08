using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>The payload rule of `session paste`, with no clipboard (round 8 of #256): explicit text
/// wins and is never trimmed; the clipboard is read only when the text is empty; nothing from
/// either is "nothing to paste".</summary>
public class SessionPastesTests
{
    [Fact]
    public void ExplicitText_IsThePayload_AndTheClipboardIsNotRead()
    {
        bool read = false;
        Assert.Equal("x", SessionPastes.Payload("x", () => { read = true; return "clip"; }));
        Assert.False(read);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\r\n")]
    [InlineData("\n\n")]
    public void WhitespaceAndNewlines_AreInput_NotEmpty(string text)
    {
        Assert.Equal(text, SessionPastes.Payload(text, () => throw new InvalidOperationException("not read")));
        Assert.Equal(SessionPastes.Pasted, SessionPastes.Reply(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoText_TakesTheClipboard(string? text)
    {
        Assert.Equal("clip", SessionPastes.Payload(text, () => "clip"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoText_AndAClipboardThatGivesNothing_IsNothingToPaste(string? text)
    {
        // "" is what the app's ClipboardGet answers for an empty clipboard, a non-text one and one it
        // could not open or read alike; the reply says no text was sent, not which of those it was.
        Assert.Equal("", SessionPastes.Payload(text, () => ""));
        Assert.Equal("", SessionPastes.Payload(text, () => null!));
        Assert.Equal(SessionPastes.Nothing, SessionPastes.Reply(""));
    }
}
