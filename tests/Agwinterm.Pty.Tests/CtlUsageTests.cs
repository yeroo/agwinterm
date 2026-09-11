using System.Text;
using Agwinterm.Ctl;
using Xunit;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// The three ways `agwintermctl` used to answer a caller mistake with success.
///
/// `--help` was INTERPRETED, not parsed: the splitter files any unknown --flag in a dictionary that
/// most verbs never read, so `session close --help` built a real session.close whose target fell
/// back to "active" and closed the session the user was looking at. `session text --pane right`
/// dropped --pane and read the focused pane, reporting ok with the WRONG pane's buffer. And a reply
/// decoded from the UTF-8 pipe was re-encoded into the console code page on the way out, so a script
/// capturing stdout got cp437 - lossily, for anything cp437 has no room for.
/// </summary>
public class CtlUsageTests
{
    // ---- --help is answered, never sent ----

    [Theory]
    [InlineData("--help")]
    [InlineData("--HELP")]                       // options are matched case-insensitively everywhere else
    public void HelpIsRecognised(string spelling) =>
        Assert.True(CtlUsage.IsHelpRequest(new[] { "session", "close", spelling }));

    [Fact]
    public void HelpIsRecognisedAloneAndFirst()
    {
        Assert.True(CtlUsage.IsHelpRequest(new[] { "--help" }));
        Assert.True(CtlUsage.IsHelpRequest(new[] { "--help", "session", "close" }));
    }

    [Fact]
    public void NoArgumentsIsNotHelp() =>
        Assert.False(CtlUsage.IsHelpRequest(Array.Empty<string>()));

    [Theory]
    [InlineData("session", "close")]
    [InlineData("session", "text")]
    [InlineData("tree", "--json")]
    public void OrdinaryInvocationsAreNotHelp(string a, string b) =>
        Assert.False(CtlUsage.IsHelpRequest(new[] { a, b }));

    /// <summary>The short spellings are POSITIONALS to the splitter, which makes them legitimate
    /// text for `session type` and a legitimate name for `session rename`. Swallowing them here
    /// would break a caller that means them literally.</summary>
    [Theory]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    [InlineData("help")]
    public void ShortSpellingsStayLiteralText(string word) =>
        Assert.False(CtlUsage.IsHelpRequest(new[] { "session", "type", word }));

    [Fact]
    public void HelpTextCoversTheVerbsItPromises()
    {
        Assert.False(string.IsNullOrWhiteSpace(CtlUsage.Long));
        foreach (var verb in new[] { "session new", "session close", "session split", "session text",
                                     "surface cursor", "install hooks" })
            Assert.Contains(verb, CtlUsage.Long);
        Assert.StartsWith("usage: agwintermctl", CtlUsage.Short);
    }

    // ---- session text refuses what it does not read ----

    [Fact]
    public void TextAcceptsItsOwnOptionsAndTheGlobalSelectors()
    {
        Assert.True(CtlUsage.TryTextOptions(new[] { "all" }, out var r1));
        Assert.Null(r1);
        Assert.True(CtlUsage.TryTextOptions(new[] { "lines", "target" }, out var r2));
        Assert.Null(r2);
        Assert.True(CtlUsage.TryTextOptions(FrameShmCli.GlobalValuedOptions, out var r3));
        Assert.Null(r3);
        Assert.True(CtlUsage.TryTextOptions(Array.Empty<string>(), out var r4));
        Assert.Null(r4);
    }

    /// <summary>The one that mattered: --pane is real on `session overlay`, so the refusal names
    /// both spellings that do work rather than only saying no.</summary>
    [Fact]
    public void TextRefusesPaneAndSaysWhatToUseInstead()
    {
        Assert.False(CtlUsage.TryTextOptions(new[] { "pane" }, out var refusal));
        Assert.NotNull(refusal);
        Assert.Contains("--target <pane-id>", refusal);
        Assert.Contains("session overlay text --pane", refusal);
        Assert.Contains("Nothing read.", refusal);
    }

    [Fact]
    public void TextRefusesAnyOtherUnknownOptionNamingIt()
    {
        Assert.False(CtlUsage.TryTextOptions(new[] { "targt" }, out var refusal));
        Assert.NotNull(refusal);
        Assert.Contains("--targt", refusal);
        Assert.Contains("Nothing read.", refusal);
    }

    [Fact]
    public void TextRefusalNamesTheFirstUnknownOptionOnly()
    {
        Assert.False(CtlUsage.TryTextOptions(new[] { "all", "pane", "targt" }, out var refusal));
        Assert.NotNull(refusal);
        Assert.Contains("--pane", refusal);
        Assert.DoesNotContain("--targt", refusal);
    }

    // ---- stdout speaks the pipe's encoding when it is not a console ----

    [Fact]
    public void RedirectedStdoutIsUtf8WithoutABom()
    {
        var buffer = new MemoryStream();
        var writer = CtlStdout.Utf8Writer(buffer, redirected: true);
        Assert.NotNull(writer);
        writer!.Write("─café");                     // a box-drawing dash and an accent
        Assert.Equal(Encoding.UTF8.GetBytes("─café"), buffer.ToArray());
    }

    /// <summary>Null means "leave Console.Out alone": a human at a cp437 console keeps the
    /// rendering they have, and this process never touches the code page its parent shell shares.</summary>
    [Fact]
    public void AConsoleIsLeftAlone() =>
        Assert.Null(CtlStdout.Utf8Writer(new MemoryStream(), redirected: false));

    [Fact]
    public void Utf8WriterAutoflushesBecauseTheProcessExitsWithoutDisposingIt()
    {
        var buffer = new MemoryStream();
        var writer = CtlStdout.Utf8Writer(buffer, redirected: true);
        writer!.WriteLine("ok");
        Assert.NotEmpty(buffer.ToArray());
    }
}
