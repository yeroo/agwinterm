using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>Tests for `agwintermctl version`. The load-bearing case is the one where nothing is
/// listening: a diagnostic that dies when the diagnosed thing is down is useless exactly when it is
/// needed, so the CLI half must still be produced and the exit must still be success.</summary>
public class VersionReportTests
{
    // A pipe nothing can plausibly be serving, so the "no app" path is the real path, not a mock.
    private static string DeadPipe() => "agwinterm-test-nobody-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void CliHalf_IsProduced_WithNoAppRunning()
    {
        var r = VersionReport.Build(DeadPipe(), timeoutMs: 200);

        Assert.False(r.AppAvailable);
        Assert.Null(r.App);
        Assert.False(string.IsNullOrWhiteSpace(r.CliVersion));
        Assert.False(string.IsNullOrWhiteSpace(r.CliPath));

        string text = VersionReport.RenderText(r);
        Assert.Contains(r.CliVersion, text);
        Assert.Contains(r.CliPath, text);
        Assert.Contains("unavailable", text);
    }

    [Fact]
    public void PipeName_AppearsInTheOutput_EvenWhenNothingAnswers()
    {
        string pipe = DeadPipe();
        var r = VersionReport.Build(pipe, timeoutMs: 200);

        // The pipe it *tried* is half the diagnosis — "which app did I reach" is unanswerable without it.
        Assert.Contains(pipe, VersionReport.RenderText(r));
        Assert.Contains(pipe, VersionReport.RenderJson(r));
        Assert.Equal(@"\\.\pipe\" + pipe, r.PipePath);
    }

    [Fact]
    public void TextForm_IsTwoGreppableLines()
    {
        var r = VersionReport.Build(DeadPipe(), timeoutMs: 200);

        var lines = VersionReport.RenderText(r).Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("cli ", lines[0]);
        Assert.StartsWith("app ", lines[1]);
    }

    [Fact]
    public void JsonForm_Parses()
    {
        string pipe = DeadPipe();
        var r = VersionReport.Build(pipe, timeoutMs: 200);

        using var doc = JsonDocument.Parse(VersionReport.RenderJson(r));
        var root = doc.RootElement;

        var cli = root.GetProperty("cli");
        Assert.Equal(r.CliVersion, cli.GetProperty("version").GetString());
        Assert.Equal(r.CliPath, cli.GetProperty("path").GetString());

        var app = root.GetProperty("app");
        Assert.False(app.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, app.GetProperty("version").ValueKind);
        Assert.Equal(pipe, app.GetProperty("pipe").GetString());
    }

    [Fact]
    public void AppHalf_ReportsTheServingApp_WhenOneAnswers()
    {
        string pipe = "agwinterm-test-version-" + Guid.NewGuid().ToString("N");
        using var session = new TerminalSession(80, 24);
        using var server = new ControlServer(session, pipe);
        server.Start();

        var r = VersionReport.Build(pipe, timeoutMs: 3000);

        Assert.True(r.AppAvailable);
        Assert.StartsWith("agwinterm ", r.App);          // the shape `ping` already returns
        Assert.Contains(r.App!, VersionReport.RenderText(r));
        Assert.DoesNotContain("unavailable", VersionReport.RenderText(r));

        using var doc = JsonDocument.Parse(VersionReport.RenderJson(r));
        var app = doc.RootElement.GetProperty("app");
        Assert.True(app.GetProperty("available").GetBoolean());
        Assert.Equal(r.App, app.GetProperty("version").GetString());
    }
}
