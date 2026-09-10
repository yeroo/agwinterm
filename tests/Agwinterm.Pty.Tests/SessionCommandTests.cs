using System.Diagnostics;
using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class SessionCommandTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("powershell", false)]
    [InlineData("powershell", true)]
    public void PowerShellKeepsCodeAndInteractivePrompt(string? mode, bool wait)
    {
        const string code = "Remove-Item -LiteralPath 'old file'; Write-Output \"☃ $env:TEMP\" # tail";
        Assert.True(SessionCommand.TryCreate(code, mode, wait, null, out var launch, out _));
        Assert.Equal("powershell.exe", launch!.App);
        Assert.Equal(new[] { "-NoLogo", "-NoExit", "-Command", code }, launch.Args);
    }

    [Fact]
    public void DirectUsesWindowsArgumentQuotingWithoutInterpretingShellSyntax()
    {
        Assert.True(SessionCommand.TryCreate("  \"C:\\Program Files\\tool.exe\" \"\" \"a b\" \"C:\\tail\\\\\" \"say \\\"hi\\\"\" ; $x &", "direct", false, null,
            out var launch, out _));
        Assert.Equal("C:\\Program Files\\tool.exe", launch!.App);
        Assert.Equal(new[] { "", "a b", "C:\\tail\\", "say \"hi\"", ";", "$x", "&" }, launch.Args);
        Assert.True(SessionCommand.TryCreate(TerminalSession.QuoteArg(launch.App) + " " + string.Join(' ', launch.QuotedArgs),
            "direct", false, null, out var roundTrip, out _));
        Assert.Equal(launch.App, roundTrip!.App);
        Assert.Equal(launch.Args, roundTrip.Args);
    }

    [Theory]
    [InlineData("", "direct", false, null)]
    [InlineData("  ", null, true, null)]
    [InlineData("echo ok", "", false, null)]
    [InlineData("echo ok", "cmd", false, null)]
    [InlineData("echo ok", "direct", true, null)]
    [InlineData("echo ok", null, false, "cmd")]
    [InlineData("\"\" arg", "direct", false, null)]
    [InlineData("echo\0wrong", null, false, null)]
    public void InvalidLaunchIsRefused(string command, string? mode, bool wait, string? profile)
    {
        Assert.False(SessionCommand.TryCreate(command, mode, wait, profile, out var launch, out var error));
        Assert.Null(launch);
        Assert.EndsWith("; nothing created", error);
    }

    [Fact]
    public void HostLimitsDoNotTruncateCommands()
    {
        Assert.True(SessionCommand.TryCreate(new string('x', 2047), null, false, null, out _, out _));
        Assert.False(SessionCommand.TryCreate(new string('x', 2048), null, false, null, out _, out _));
        Assert.False(SessionCommand.TryCreate(new string('☃', 683), null, false, null, out _, out _));
        Assert.True(SessionCommand.TryCreate("tool " + string.Join(' ', Enumerable.Repeat("x", 16)), "direct", false, null, out _, out _));
        Assert.False(SessionCommand.TryCreate("tool " + string.Join(' ', Enumerable.Repeat("x", 17)), "direct", false, null, out _, out _));
    }

    [Theory]
    [InlineData("\"wrong\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void ServerRefusalCannotCreateRequestedWorkspace(string modeJson)
    {
        var server = new ControlServer(new FakeSessionHost());
        string before = server.Dispatch("{\"cmd\":\"tree\"}");
        string request = "{\"cmd\":\"session.new\",\"args\":{\"command\":\"echo ok\",\"workspace-name\":\"new workspace\",\"create-workspace\":true,\"command-mode\":" + modeJson + "}}";
        using var response = JsonDocument.Parse(server.Dispatch(request));
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(before, server.Dispatch("{\"cmd\":\"tree\"}"));
    }

    [Theory]
    [InlineData("Write-Output ('COMMAND-' + 'READY'); $v='quoted \"value\"'; Write-Output $v", "quoted \"value\"")]
    [InlineData("Write-Output ('COMMAND-' + 'READY'); Write-Error 'intentional failure'", null)]
    public async Task PowerShellCommandCompletesAndAcceptsMoreInput(string command, string? expected)
    {
        Assert.True(SessionCommand.TryCreate(command, null, false, null, out var launch, out _));
        using var child = Start(launch!);
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        // A second command executes only after the startup command has completed, proving the
        // startup command was not simply typed into a racing prompt or made the shell exit.
        await child.StandardInput.WriteLineAsync("Write-Output ('FOLLOWUP-' + 'READY')");
        await child.StandardInput.WriteLineAsync("exit 0");
        child.StandardInput.Close();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var text = await output;
            Assert.Contains("COMMAND-READY", text);
            Assert.Contains("FOLLOWUP-READY", text);
            if (expected is not null) Assert.Contains(expected, text);
            else Assert.Contains("intentional failure", await errors);
            Assert.Equal(0, child.ExitCode);
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(); } }
    }

    [Theory]
    [InlineData("exit 7", null, 7)]
    [InlineData("cmd.exe /d /c exit 9", "direct", 9)]
    public async Task ExplicitExitEndsTheProcess(string command, string? mode, int exitCode)
    {
        Assert.True(SessionCommand.TryCreate(command, mode, false, null, out var launch, out _));
        using var child = Start(launch!);
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(exitCode, child.ExitCode);
            await Task.WhenAll(output, errors);
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(); } }
    }

    private static Process Start(SessionCommand launch)
    {
        var start = new ProcessStartInfo(launch.App)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Tests must not execute the user's profile. Preserve the production command/argv.
        if (launch.App == "powershell.exe") start.ArgumentList.Add("-NoProfile");
        foreach (var arg in launch.Args) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
}
