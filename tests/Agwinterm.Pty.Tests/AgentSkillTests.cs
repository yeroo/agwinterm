using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgentSkillTests
{
    [Fact]
    public void ImageSectionAdvertisesTheSharedMemoryProducerContract()
    {
        Assert.Contains(
            @"agwintermctl image frameshm <Local\agwinterm-frame-NAME>",
            AgentSkill.SkillMarkdown);
        Assert.Contains(
            "https://github.com/yeroo/agwinterm/blob/main/docs/specs/image-frameshm.md",
            AgentSkill.SkillMarkdown);
        Assert.Contains("agwintermctl session metrics [<pane-id>] --json", AgentSkill.SkillMarkdown);
        Assert.Contains("Ordinary", AgentSkill.SkillMarkdown);
        Assert.Contains("image files should use `image show`.", AgentSkill.SkillMarkdown);
    }

    /// <summary>P3: the two persistence verbs are advertised beside their neighbours (context beside
    /// rename, capture beside clear) and the skill says the three things a caller cannot learn from
    /// the reply alone — one line / what is refused, that a capture can take seconds, and that
    /// replayOnRestore is the toggle gating the replay, not the capture.</summary>
    [Fact]
    public void PersistenceVerbsAreAdvertisedWithTheirRules()
    {
        var md = AgentSkill.SkillMarkdown;
        Assert.Contains("agwintermctl session context <text> [--target ID]", md);
        Assert.Contains("agwintermctl restore capture [--target ID]", md);
        Assert.Contains("`tree --json` reads it back as `context`", md);
        Assert.Contains("`tree --json` as `capturedCommands`, keyed by pane id like `restoreCommands`", md);
        Assert.Contains("SECONDS", md);
        Assert.Contains("`replayOnRestore` is the", md);
        // Neighbours: context is listed right after rename, capture right after clear.
        Assert.True(md.IndexOf("session rename <new-name>", StringComparison.Ordinal)
            < md.IndexOf("session context <text>", StringComparison.Ordinal));
        Assert.True(md.IndexOf("agwintermctl restore clear`", StringComparison.Ordinal)
            < md.IndexOf("agwintermctl restore capture", StringComparison.Ordinal));
    }

    /// <summary>P4: the split verbs are advertised with the four things a caller cannot learn from a
    /// reply alone — that <c>session split</c> answers a pane id (and which one when already split),
    /// the axis vocabulary in the one sentence the CLI header and <c>ISessionHost.Split</c> also
    /// carry, that <c>split close</c> takes either side and refuses a one-pane session, and that a
    /// swap moves panes and never ids — in the order split, split close, swap, focus, resize.</summary>
    [Fact]
    public void SplitVerbsAreAdvertisedWithTheirRules()
    {
        var md = AgentSkill.SkillMarkdown;
        Assert.Contains("agwintermctl session split [on|off|toggle] [--axis vertical|horizontal] [--target <id>]", md);
        Assert.Contains("REPLIES WITH A PANE ID", md);
        Assert.Contains("ALSO when the session was already split", md);
        Assert.Contains(
            "The axis names the ARRANGEMENT, agterm's words: vertical = left/right panes (the default of a session never split), horizontal = top/bottom panes.",
            md);
        Assert.Contains("agwintermctl session split close [--target <id>]", md);
        Assert.Contains("EITHER side", md);
        Assert.Contains("`session close` is the verb that closes a session", md);
        Assert.Contains("agwintermctl session swap [--target <id>]", md);
        Assert.Contains("A swap moves panes, never ids", md);
        Assert.Contains("agwintermctl session focus [primary|split|left|right|top|bottom|other]", md);
        Assert.Contains("--grow-top N|--grow-bottom N", md);
        int split = md.IndexOf("agwintermctl session split [on|off|toggle]", StringComparison.Ordinal);
        int close = md.IndexOf("agwintermctl session split close", StringComparison.Ordinal);
        int swap = md.IndexOf("agwintermctl session swap", StringComparison.Ordinal);
        int focus = md.IndexOf("agwintermctl session focus [", StringComparison.Ordinal);
        int resize = md.IndexOf("agwintermctl session resize [", StringComparison.Ordinal);
        Assert.True(split < close && close < swap && swap < focus && focus < resize);
    }
}
