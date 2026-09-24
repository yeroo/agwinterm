using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// <see cref="AgentHooks.CodexHookScript"/> run for real under powershell.exe, as Codex runs it: the
/// status in argv, the event JSON on stdin, the push on a named pipe this fake server listens on.
/// </summary>
public class CodexHookScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agwinterm-codex-hook-" + Guid.NewGuid().ToString("N"));
    private readonly string _script;

    public CodexHookScriptTests()
    {
        Directory.CreateDirectory(_dir);
        _script = Path.Combine(_dir, "agwinterm-codex-hook.ps1");
        File.WriteAllText(_script, AgentHooks.CodexHookScript);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>A rollout whose first line is the session_meta Codex writes before any hook runs.</summary>
    private string Rollout(string source)
    {
        string path = Path.Combine(_dir, $"rollout-{source}.jsonl");
        File.WriteAllText(path, "{\"type\":\"session_meta\",\"payload\":{\"source\":\"" + source + "\",\"cwd\":\"C:\\\\w\"}}\n");
        return path;
    }

    private static string Event(string name, string? lastMessage = null, string? transcript = null)
    {
        var o = new Dictionary<string, object?> { ["session_id"] = "01a0-test", ["hook_event_name"] = name };
        if (lastMessage is not null) o["last_assistant_message"] = lastMessage;
        if (transcript is not null) o["transcript_path"] = transcript;
        return JsonSerializer.Serialize(o);
    }

    /// <summary>Run the script and return the status it pushed, or null when it pushed nothing.</summary>
    private string? Run(string state, string stdin, bool inAgwinterm = true)
    {
        string pipeName = "agwinterm-test-codex-hook-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task<string?> received = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            return await new StreamReader(server, Encoding.UTF8).ReadLineAsync();
        });

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", _script, state }) start.ArgumentList.Add(a);
        start.Environment["AGWINTERM_PIPE"] = pipeName;
        if (inAgwinterm) start.Environment["AGWINTERM_SESSION_ID"] = "pane-x";
        else start.Environment.Remove("AGWINTERM_SESSION_ID");

        using var process = Process.Start(start)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        string stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "the hook script did not exit");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", stdout);   // Codex reads a hook's stdout as a decision: it must stay silent

        if (!received.Wait(TimeSpan.FromSeconds(1))) return null;
        using var push = JsonDocument.Parse(received.Result!);
        Assert.Equal("session.status", push.RootElement.GetProperty("cmd").GetString());
        Assert.Equal("pane-x", push.RootElement.GetProperty("target").GetString());
        return push.RootElement.GetProperty("args").GetProperty("status").GetString();
    }

    [Fact]
    public void ToolUsePushesActive() => Assert.Equal("active", Run("active", Event("PostToolUse", transcript: Rollout("cli"))));

    [Fact]
    public void PermissionRequestPushesBlocked() => Assert.Equal("blocked", Run("blocked", Event("PermissionRequest")));

    [Fact]
    public void StopOnAStatementIsCompleted() => Assert.Equal("completed", Run("stop", Event("Stop", "Done, tests pass.")));

    [Fact]
    public void StopOnAQuestionIsBlocked() => Assert.Equal("blocked", Run("stop", Event("Stop", "Shall I push it?  ")));

    [Fact]
    public void NestedExecRunPushesNothing() => Assert.Null(Run("active", Event("PostToolUse", transcript: Rollout("exec"))));

    [Fact]
    public void OutsideAgwintermPushesNothing() => Assert.Null(Run("active", Event("PostToolUse"), inAgwinterm: false));
}
