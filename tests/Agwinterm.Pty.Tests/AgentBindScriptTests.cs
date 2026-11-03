using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// <see cref="AgentHooks.AgentBindScript"/> run for real under powershell.exe, as the agents run their
/// SessionStart hooks: the agent in argv, the event JSON on stdin, the report on a named pipe this fake
/// server answers. The host walks the process tree from the reported PID before it replies, so the script
/// must still be running when its request arrives and must wait for the answer (#316). The SessionEnd
/// script, <see cref="AgentHooks.AgentUnbindScript"/>, is run through the same harness (#368).
/// </summary>
public class AgentBindScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agwinterm-agent-bind-" + Guid.NewGuid().ToString("N"));
    private readonly string _script;
    private readonly string _unbindScript;

    public AgentBindScriptTests()
    {
        Directory.CreateDirectory(_dir);
        _script = Path.Combine(_dir, "agwinterm-agent-bind.ps1");
        File.WriteAllText(_script, AgentHooks.AgentBindScript);
        _unbindScript = Path.Combine(_dir, "agwinterm-agent-unbind.ps1");
        File.WriteAllText(_unbindScript, AgentHooks.AgentUnbindScript);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Rollout(string source)
    {
        string path = Path.Combine(_dir, $"rollout-{source}.jsonl");
        File.WriteAllText(path, "{\"type\":\"session_meta\",\"payload\":{\"source\":\"" + source + "\"}}\n");
        return path;
    }

    private static string Event(string sessionId, string cwd, string? transcript = null)
    {
        var o = new Dictionary<string, object?> { ["session_id"] = sessionId, ["cwd"] = cwd, ["hook_event_name"] = "SessionStart", ["source"] = "startup" };
        if (transcript is not null) o["transcript_path"] = transcript;
        return JsonSerializer.Serialize(o);
    }

    private sealed record Report(JsonElement Request, bool HookAliveWhenReceived, int HookPid);

    /// <summary>Run the script; return what it sent (null when it sent nothing).</summary>
    private Report? Run(string agent, string stdin, Dictionary<string, string?>? env = null, bool inAgwinterm = true, string? script = null)
    {
        string pipeName = "agwinterm-test-bind-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Process? process = null;
        Task<(string? line, bool alive)> received = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            string? line = await new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true).ReadLineAsync();
            // Hold the reply a moment, as the host's snapshot does: a script that did not wait would be gone.
            await Task.Delay(500);
            bool alive = process is { HasExited: false };
            try { await new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true }.WriteLineAsync("{\"ok\":true,\"result\":\"binding\"}"); }
            catch (IOException) { /* a script that did not wait has hung up: `alive` already says so */ }
            return (line, alive);
        });

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script ?? _script, agent }) start.ArgumentList.Add(a);
        start.Environment["AGWINTERM_PIPE"] = pipeName;
        foreach (var k in new[] { "AGWINTERM_SESSION_ID", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ATTENDED", "DEVIN_PROJECT_DIR" }) start.Environment.Remove(k);
        if (inAgwinterm) start.Environment["AGWINTERM_SESSION_ID"] = "pane-x";
        foreach (var (k, v) in env ?? new()) start.Environment[k] = v;

        process = Process.Start(start)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        string stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "the hook script did not exit");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", stdout);   // an agent reads a hook's stdout: it must stay silent

        if (!received.Wait(TimeSpan.FromSeconds(1))) return null;
        var (line, alive) = received.Result;
        using var doc = JsonDocument.Parse(line!);
        return new Report(doc.RootElement.Clone(), alive, process.Id);
    }

    [Fact]
    public void ReportsTheSessionTheCwdAndItsOwnPidAndWaitsForTheReply()
    {
        var r = Run("claude", Event("36145f11-e132-4691-ab98-ae5c4445c46b", @"C:\src\it's ""here"""),
            new() { ["CLAUDE_CODE_ENTRYPOINT"] = "cli", ["CLAUDE_CODE_SESSION_ATTENDED"] = "1" });
        Assert.NotNull(r);
        Assert.Equal("session.bind", r!.Request.GetProperty("cmd").GetString());
        Assert.Equal("pane-x", r.Request.GetProperty("target").GetString());
        var args = r.Request.GetProperty("args");
        Assert.Equal("claude", args.GetProperty("agent").GetString());
        Assert.Equal("36145f11-e132-4691-ab98-ae5c4445c46b", args.GetProperty("resume").GetString());
        Assert.Equal(@"C:\src\it's ""here""", args.GetProperty("cwd").GetString());   // JSON-escaped, not spliced
        Assert.Equal(r.HookPid, args.GetProperty("pid").GetInt32());
        Assert.True(r.HookAliveWhenReceived, "the hook exited before agwinterm could walk up from its PID");
    }

    [Fact]
    public void CodexTuiSessionIsReported()
    {
        var r = Run("codex", Event("01a0ce4b-7125-7dd1-b5a1-a99d8f14402c", @"C:\src\main-pr", Rollout("cli")));
        Assert.Equal("codex", r!.Request.GetProperty("args").GetProperty("agent").GetString());
    }

    [Theory]
    [InlineData("sdk-cli", "0")]   // `claude -p` run from an agent's tool shell
    [InlineData("cli", "0")]
    [InlineData("sdk-cli", null)]
    public void HeadlessClaudeReportsNothing(string entrypoint, string? attended)
        => Assert.Null(Run("claude", Event("abc", @"C:\w"),
            new() { ["CLAUDE_CODE_ENTRYPOINT"] = entrypoint, ["CLAUDE_CODE_SESSION_ATTENDED"] = attended }));

    /// <summary>Devin 3000.11's SessionStart, as observed: no cwd, the session id is a word pair.</summary>
    private const string DevinEvent = "{\"hook_event_name\":\"SessionStart\",\"source\":\"startup\",\"session_id\":\"holy-antler\"}";

    [Fact]
    public void DevinSessionIsReportedWithItsProjectDirectory()
    {
        var r = Run("devin", DevinEvent, new() { ["DEVIN_PROJECT_DIR"] = @"C:\src\it's" });
        var args = r!.Request.GetProperty("args");
        Assert.Equal("devin", args.GetProperty("agent").GetString());
        Assert.Equal("holy-antler", args.GetProperty("resume").GetString());
        Assert.Equal(@"C:\src\it's", args.GetProperty("cwd").GetString());
    }

    [Fact]
    public void ClaudesHookRunByDevinReportsNothing()
        => Assert.Null(Run("claude", DevinEvent, new() { ["DEVIN_PROJECT_DIR"] = @"C:\src\proj" }));

    [Fact]
    public void CodexExecReportsNothing() => Assert.Null(Run("codex", Event("01a0", @"C:\w", Rollout("exec"))));

    [Fact]
    public void OutsideAgwintermReportsNothing() => Assert.Null(Run("claude", Event("abc", @"C:\w"), inAgwinterm: false));

    private static string EndEvent(string? sessionId, string? reason)
    {
        var o = new Dictionary<string, object?> { ["cwd"] = @"C:\w", ["transcript_path"] = @"C:\w\transcript.jsonl", ["hook_event_name"] = "SessionEnd" };
        if (sessionId is not null) o["session_id"] = sessionId;
        if (reason is not null) o["reason"] = reason;
        return JsonSerializer.Serialize(o);
    }

    private static readonly Dictionary<string, string?> Tui = new() { ["CLAUDE_CODE_ENTRYPOINT"] = "cli", ["CLAUDE_CODE_SESSION_ATTENDED"] = "1" };

    private Report? RunUnbind(string stdin, Dictionary<string, string?>? env = null, bool inAgwinterm = true)
        => Run("claude", stdin, env ?? Tui, inAgwinterm, _unbindScript);

    [Fact]
    public void UserExitReportsTheEndedSessionAndWaitsForTheReply()
    {
        var r = RunUnbind(EndEvent("36145f11-e132-4691-ab98-ae5c4445c46b", "prompt_input_exit"));
        Assert.NotNull(r);
        // Its own verb: a host that predates it answers "unknown command", where session.bind would take
        // `agent` alone for a relaunch line and store it over the pane's resume line.
        Assert.Equal("session.ended", r!.Request.GetProperty("cmd").GetString());
        Assert.Equal("pane-x", r.Request.GetProperty("target").GetString());
        var args = r.Request.GetProperty("args");
        Assert.Equal("claude", args.GetProperty("agent").GetString());
        Assert.Equal("36145f11-e132-4691-ab98-ae5c4445c46b", args.GetProperty("session").GetString());
        Assert.False(args.TryGetProperty("resume", out _));
        Assert.True(r.HookAliveWhenReceived, "the hook exited before agwinterm had answered");
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("resume")]
    [InlineData("logout")]
    [InlineData("other")]
    [InlineData("Prompt_Input_Exit")]
    [InlineData("prompt_input_exit ")]
    [InlineData(null)]
    public void AnyOtherReasonReportsNothing(string? reason) => Assert.Null(RunUnbind(EndEvent("abc", reason)));

    [Fact]
    public void AnEventWithoutASessionIdReportsNothing() => Assert.Null(RunUnbind(EndEvent(null, "prompt_input_exit")));

    [Theory]
    [InlineData("sdk-cli", "0")]
    [InlineData("cli", "0")]
    [InlineData("sdk-cli", null)]
    public void HeadlessClaudeUnbindsNothing(string entrypoint, string? attended)
        => Assert.Null(RunUnbind(EndEvent("abc", "prompt_input_exit"),
            new() { ["CLAUDE_CODE_ENTRYPOINT"] = entrypoint, ["CLAUDE_CODE_SESSION_ATTENDED"] = attended }));

    [Fact]
    public void ClaudesEndHookRunByDevinReportsNothing()
        => Assert.Null(RunUnbind(EndEvent("abc", "prompt_input_exit"), new() { ["DEVIN_PROJECT_DIR"] = @"C:\src\proj" }));

    [Fact]
    public void OutsideAgwintermUnbindsNothing() => Assert.Null(RunUnbind(EndEvent("abc", "prompt_input_exit"), inAgwinterm: false));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"reason\":\"prompt_input_exit\"")]
    public void MalformedEventExitsQuietly(string stdin) => Assert.Null(RunUnbind(stdin));

    [Fact]
    public void TheSessionIdIsJsonEscapedNotSpliced()
    {
        string id = "a\"},\"cmd\":\"session.close\",\"x\":{\"y\":\"b";
        var r = RunUnbind(EndEvent(id, "prompt_input_exit"));
        Assert.Equal("session.ended", r!.Request.GetProperty("cmd").GetString());
        Assert.Equal(id, r.Request.GetProperty("args").GetProperty("session").GetString());
    }
}
