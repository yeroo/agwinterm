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
}
