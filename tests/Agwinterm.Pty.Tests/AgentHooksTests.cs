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

    [Fact]
    public void ClaudeLauncher_PersistsDangerouslySkipPermissionsInBinding()
    {
        // A YOLO-launched claude must restore as YOLO: the wrapper detects the flag and binds the
        // full relaunch command, which the restore path types verbatim.
        string block = ClaudeIntegration.Block;
        Assert.Contains("'--dangerously-skip-permissions'", block);                       // flag detection
        Assert.Contains("'claude --dangerously-skip-permissions'", block);                // bound relaunch command
        Assert.Contains("\"agent\":\"' + $agent + '\"", block);                           // bind carries the command
    }

    [Fact]
    public void GenericAgentRegex_CoversPiWithWordBoundarySafety()
    {
        // agterm #208: Pi agent status. The bridge matches '^\s*(RE)\b', so 'pi' must be in the
        // default set — and the \b anchor means 'pip install' must NOT light the status.
        Assert.Contains("|pi'", GenericAgentInstaller.Block);
        string re = System.Text.RegularExpressions.Regex.Match(GenericAgentInstaller.Block,
            @"AGWINTERM_AGENT_RE = '([^']+)'").Groups[1].Value;
        Assert.Matches(@"^\s*(" + re + @")\b", "pi do something");
        Assert.DoesNotMatch(@"^\s*(" + re + @")\b", "pip install requests");
    }

    [Fact]
    public void GenericAgentInstaller_SharesStaleBlockReplacement()
    {
        // Same Missing/Current/Stale/Corrupted machinery as the claude launcher (ProfileBlocks).
        string begin = "# >>> agwinterm generic agent status >>>";
        Assert.StartsWith(begin, GenericAgentInstaller.Block);
        string stale = "# before\n" + begin + "\nold bridge\n# <<< agwinterm generic agent status <<<\n# after\n";
        Assert.Equal(ProfileBlockState.Stale, ProfileBlocks.Inspect(stale,
            begin, "# <<< agwinterm generic agent status <<<", GenericAgentInstaller.Block, out string updated));
        Assert.Contains(GenericAgentInstaller.Block, updated);
        Assert.DoesNotContain("old bridge", updated);
    }

    [Fact]
    public void InspectBlock_MissingCurrentStaleCorrupted()
    {
        // Missing: no sentinel at all.
        Assert.Equal(ClaudeIntegration.BlockState.Missing, ClaudeIntegration.InspectBlock("# my profile\n", out _));

        // Current: exactly the shipped block.
        string profile = "# above\n" + ClaudeIntegration.Block + "\n# below\n";
        Assert.Equal(ClaudeIntegration.BlockState.Current, ClaudeIntegration.InspectBlock(profile, out _));

        // Stale: an older wrapper between the same sentinels gets replaced in place, neighbors intact.
        string stale = "# above\n# >>> agwinterm claude integration >>>\nold wrapper\n# <<< agwinterm claude integration <<<\n# below\n";
        Assert.Equal(ClaudeIntegration.BlockState.Stale, ClaudeIntegration.InspectBlock(stale, out string updated));
        Assert.Contains(ClaudeIntegration.Block, updated);
        Assert.DoesNotContain("old wrapper", updated);
        Assert.StartsWith("# above\n", updated);
        Assert.EndsWith("\n# below\n", updated);

        // Corrupted: begin sentinel without end — leave alone, report.
        Assert.Equal(ClaudeIntegration.BlockState.Corrupted,
            ClaudeIntegration.InspectBlock("# >>> agwinterm claude integration >>>\nhalf a block", out _));
    }
}
