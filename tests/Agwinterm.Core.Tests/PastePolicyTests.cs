using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>Paste-protection classifier: what warrants a confirmation before bytes reach the shell.</summary>
public class PastePolicyTests
{
    // ---- Safe pastes: never prompt ----

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("git commit -m \"fix: quotes are fine\"")]
    [InlineData("col1\tcol2\tcol3")]                       // tabs are ordinary text
    [InlineData("ünïcode — emoji 🚀 fullwidth ｗｉｄｅ")]     // non-ASCII is not a control char
    public void PlainSingleLineText_NeverWarns(string text)
    {
        Assert.False(PastePolicy.NeedsWarning(text, bracketedPaste: false));
        Assert.False(PastePolicy.NeedsWarning(text, bracketedPaste: true));
    }

    // ---- Newlines: the "shell runs each line immediately" hazard ----

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\r\nline2")]
    [InlineData("line1\rline2")]
    [InlineData("cmd\n")]           // a single trailing newline still executes the line
    public void MultiLine_WarnsWithoutBracketedPaste(string text)
        => Assert.True(PastePolicy.NeedsWarning(text, bracketedPaste: false));

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\r\nline2")]
    [InlineData("a\rb\rc\r")]
    public void MultiLine_SafeUnderBracketedPaste(string text)
        => Assert.False(PastePolicy.NeedsWarning(text, bracketedPaste: true));

    // ---- Control characters: escape injection, dangerous even under bracketed paste ----

    [Theory]
    [InlineData("evil\x1b[201~rm -rf ~\x1b[200~")]  // smuggled bracketed-paste terminator
    [InlineData("bell\a")]
    [InlineData("del\x7f")]
    [InlineData("\x01ctrl-a")]
    public void ControlCharacters_WarnEvenUnderBracketedPaste(string text)
    {
        Assert.True(PastePolicy.NeedsWarning(text, bracketedPaste: false));
        Assert.True(PastePolicy.NeedsWarning(text, bracketedPaste: true));
    }

    // ---- Line counting for the prompt message ----

    [Theory]
    [InlineData("", 1)]
    [InlineData("one line", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\r\nb", 2)]       // CRLF is ONE break, not two
    [InlineData("a\rb", 2)]
    [InlineData("a\n", 2)]
    [InlineData("a\r\nb\nc\rd", 4)]
    public void LineCount_CountsBreaksPlusOne(string text, int expected)
        => Assert.Equal(expected, PastePolicy.LineCount(text));
}
