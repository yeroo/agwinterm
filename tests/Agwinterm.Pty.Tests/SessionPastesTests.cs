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

    [Fact]
    public void TheFailedAndExitedRefusals_AreWordedAsDocumented()
    {
        // Pins the WORDS only. The host's try/catch around PasteTextInto (the refusal that names
        // the exception instead of the sync handler's empty ok:true, round 9 of #256) has no unit
        // test: FakeSessionHost.SessionPaste re-implements the rule and never writes, so a throwing
        // fake pane would pin the fake's own branch and leave the real catch deletable. The live
        // suite proves the exited arm; the catch arm is argued from the code (round 10).
        Assert.Equal("paste failed: Session not started.", SessionPastes.Failed("Session not started."));
        Assert.Equal("the pane's process has exited", SessionPastes.ExitedPane);
    }
}
