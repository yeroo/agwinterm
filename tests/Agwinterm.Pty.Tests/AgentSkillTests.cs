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
}
