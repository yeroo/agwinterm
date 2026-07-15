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

    [Fact]
    public void WrapperScript_RoutesStatusToItsOwnPane()
    {
        // The status push must carry a target = its own AGWINTERM_SESSION_ID; otherwise the server
        // routes to the *focused* pane and multi-session status lands on the wrong dot.
        Assert.Contains("\"target\":\"' + $env:AGWINTERM_SESSION_ID + '\"", AgentHooks.WrapperScript);
        Assert.Contains("\"target\":\"' + $env:AGWINTERM_SESSION_ID + '\"", AgentHooks.CodexNotifyScript);
        Assert.Contains("\"target\":\"' + $env:AGWINTERM_SESSION_ID + '\"", GenericAgentInstaller.Block);
    }

    [Fact]
    public void ClaudeLauncher_BindsAndResumesBySessionId()
    {
        string block = ClaudeIntegration.Block;
        Assert.Contains("function global:claude", block);
        Assert.Contains("--session-id $sid", block);   // fresh session bound to the pane id
        Assert.Contains("--resume $sid", block);        // resume when the transcript already exists
        Assert.Contains("session.bind", block);          // marks the pane resumable in agwinterm
        Assert.Contains("$env:AGWINTERM -eq '1'", block); // only active inside agwinterm
    }
}
