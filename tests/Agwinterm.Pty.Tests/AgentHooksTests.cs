using System.Text.Json.Nodes;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgentHooksTests
{
    private const string Wrapper = @"C:\Users\x\AppData\Local\agwinterm\agwinterm-agent-status.ps1";

    [Fact]
    public void Merge_IntoEmpty_AddsFourEvents()
    {
        string? json = AgentHooks.MergeClaudeSettings(null, Wrapper);
        Assert.NotNull(json);
        var root = JsonNode.Parse(json!)!.AsObject();
        var hooks = root["hooks"]!.AsObject();
        Assert.True(hooks.ContainsKey("UserPromptSubmit"));
        Assert.True(hooks.ContainsKey("PostToolUse"));
        Assert.True(hooks.ContainsKey("Stop"));
        Assert.True(hooks.ContainsKey("Notification"));
        // The wrapper path and the right state appear.
        Assert.Contains("completed", hooks["Stop"]!.ToJsonString());
        Assert.Contains("permission_prompt", hooks["Notification"]!.ToJsonString());
        Assert.Contains("blocked", hooks["Notification"]!.ToJsonString());
    }

    [Fact]
    public void Merge_IsIdempotent()
    {
        string once = AgentHooks.MergeClaudeSettings(null, Wrapper)!;
        string twice = AgentHooks.MergeClaudeSettings(once, Wrapper)!;
        var stop = JsonNode.Parse(twice)!.AsObject()["hooks"]!.AsObject()["Stop"]!.AsArray();
        Assert.Single(stop); // not duplicated on re-install
    }

    [Fact]
    public void Merge_PreservesExistingUnrelatedSettings()
    {
        string existing = "{\"model\":\"opus\",\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"echo hi\"}]}]}}";
        string merged = AgentHooks.MergeClaudeSettings(existing, Wrapper)!;
        var root = JsonNode.Parse(merged)!.AsObject();
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        // Existing Stop entry kept, ours appended.
        var stop = root["hooks"]!.AsObject()["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count);
    }

    [Fact]
    public void Merge_RefusesMalformedFile()
    {
        Assert.Null(AgentHooks.MergeClaudeSettings("{ not valid json", Wrapper));
    }
}
