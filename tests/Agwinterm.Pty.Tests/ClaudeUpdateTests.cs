using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class ClaudeUpdateTests
{
    [Theory]
    [InlineData("2.1.14 (Claude Code)", "2.1.14")]
    [InlineData("Claude Code 1.0.55", "1.0.55")]
    [InlineData("2.0.0-beta.3\n", "2.0.0-beta.3")]
    [InlineData("no version here", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseVersion_ExtractsDottedVersion(string? output, string? expected)
        => Assert.Equal(expected, ClaudeUpdate.ParseVersion(output));

    [Fact]
    public void LatestFromRegistryJson_ReadsVersionField()
    {
        Assert.Equal("2.1.15", ClaudeUpdate.LatestFromRegistryJson(
            "{\"name\":\"@anthropic-ai/claude-code\",\"version\":\"2.1.15\"}"));
        Assert.Null(ClaudeUpdate.LatestFromRegistryJson("{\"name\":\"x\"}"));
        Assert.Null(ClaudeUpdate.LatestFromRegistryJson("not json"));
    }

    [Theory]
    [InlineData("2.0.10", "2.0.9", true)]          // numeric per-segment, not lexicographic
    [InlineData("2.1.0", "2.0.99", true)]
    [InlineData("3.0.0", "2.9.9", true)]
    [InlineData("2.0.9", "2.0.9", false)]          // equal is not newer
    [InlineData("2.0.8", "2.0.9", false)]          // a downgrade is never "newer"
    [InlineData("2.0.10-beta.1", "2.0.9", true)]   // prerelease tail ignored for ordering
    [InlineData("weird", "2.0.9", false)]          // unparseable never nags / never triggers
    [InlineData("2.0.10", null, false)]
    [InlineData(null, "2.0.9", false)]
    public void IsNewer_ComparesNumerically(string? candidate, string? current, bool expected)
        => Assert.Equal(expected, ClaudeUpdate.IsNewer(candidate, current));
}
