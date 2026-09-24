using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

// The Rust backend test temporarily installs the process-wide session core factory.
[CollectionDefinition("Styled text backends", DisableParallelization = true)]
public class StyledTextBackendsCollection { }

[Collection("Styled text backends")]
public class SurfaceTextStylesTests
{
    private static void Feed(ISession session, string text) => session.Inject(Encoding.UTF8.GetBytes(text));

    private static JsonElement Read(ControlServer server, object? args = null, string? target = null)
    {
        using var doc = JsonDocument.Parse(server.Dispatch(JsonSerializer.Serialize(new
        {
            cmd = "session.text", target, args = args ?? new { styles = true },
        })));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), doc.RootElement.GetRawText());
        return doc.RootElement.GetProperty("result").Clone();
    }

    private static JsonElement[] Runs(JsonElement result, int index = 0) =>
        result.GetProperty("rows")[index].GetProperty("runs").EnumerateArray().ToArray();

    private static void AssertPlainParity(ControlServer server, object? args = null, object? plainArgs = null)
    {
        string styled = string.Join('\n', Read(server, args).GetProperty("rows").EnumerateArray()
            .Select(row => string.Concat(row.GetProperty("runs").EnumerateArray().Select(run => run.GetProperty("text").GetString()))));
        Assert.Equal(Read(server, plainArgs ?? new { styles = false }).GetString(), styled);
    }

    [Theory]
    [InlineData("> \u001b[2msuggestion\u001b[0m", true)]
    [InlineData("> draft", false)]
    public void SuggestionAndDraftHaveDifferentAttributes(string input, bool faint)
    {
        using var session = new TerminalSession(40, 4);
        using var server = new ControlServer(session);
        Feed(session, input + "\r\u001b[2C");
        var result = Read(server);
        var runs = Runs(result);
        Assert.Equal(faint ? 2 : 1, runs.Length);
        Assert.Equal(faint, runs[^1].GetProperty("faint").GetBoolean());
        Assert.Equal(faint ? "suggestion" : "> draft", runs[^1].GetProperty("text").GetString());
        Assert.Equal(0, runs[0].GetProperty("col").GetInt32());
        Assert.Equal(faint ? 2 : 7, runs[0].GetProperty("width").GetInt32());
        Assert.Equal(40, result.GetProperty("cols").GetInt32());
        Assert.Equal(0, result.GetProperty("cursor").GetProperty("row").GetInt32());
        Assert.Equal(2, result.GetProperty("cursor").GetProperty("col").GetInt32());
        // Half-open grid overlap also works when the caret is INSIDE the plain draft run.
        var suffix = runs.Where(run => run.GetProperty("col").GetInt32() + run.GetProperty("width").GetInt32() > 2);
        Assert.Equal(faint, suffix.All(run => run.GetProperty("faint").GetBoolean()));
        foreach (string flag in new[] { "bold", "italic", "underline", "inverse", "strike" })
            Assert.False(runs[^1].GetProperty(flag).GetBoolean());
        Assert.Equal("default", runs[^1].GetProperty("fg").GetString());
        Assert.Equal("default", runs[^1].GetProperty("bg").GetString());
        AssertPlainParity(server);
    }

    [Fact]
    public void FlagsAndColourSpecsArePreservedAndEqualStylesMerge()
    {
        using var session = new TerminalSession(40, 4);
        using var server = new ControlServer(session);
        Feed(session, "\u001b[1;2;3;4;7;9;38;5;244;48;2;1;2;255mAB\u001b[38;5;244mC" +
            "\u001b[0;38;2;17;34;51;48;5;9mD\u001b[0mE\u001b[31mF");
        var runs = Runs(Read(server));
        Assert.Equal(4, runs.Length);
        Assert.Equal("ABC", runs[0].GetProperty("text").GetString());
        Assert.Equal(3, runs[0].GetProperty("width").GetInt32());
        foreach (string flag in new[] { "faint", "bold", "italic", "underline", "inverse", "strike" })
            Assert.True(runs[0].GetProperty(flag).GetBoolean());
        Assert.Equal("idx:244", runs[0].GetProperty("fg").GetString());
        Assert.Equal("#0102ff", runs[0].GetProperty("bg").GetString());
        Assert.Equal("#112233", runs[1].GetProperty("fg").GetString());
        Assert.Equal("idx:9", runs[1].GetProperty("bg").GetString());
        Assert.Equal("default", runs[2].GetProperty("fg").GetString());
        Assert.Equal("idx:1", runs[3].GetProperty("fg").GetString());
        AssertPlainParity(server);
    }

    [Fact]
    public void WideAndAstralGlyphsUseGridExtentsWithoutSpacerText()
    {
        using var session = new TerminalSession(40, 4);
        using var server = new ControlServer(session);
        Feed(session, "界😀\u001b[2mhint\u001b[0m\r\u001b[4C");
        var result = Read(server);
        var runs = Runs(result);
        Assert.Equal("界😀", runs[0].GetProperty("text").GetString());
        Assert.Equal(4, runs[0].GetProperty("width").GetInt32());
        Assert.Equal(4, runs[1].GetProperty("col").GetInt32());
        Assert.Equal(4, runs[1].GetProperty("width").GetInt32());
        Assert.Equal(4, result.GetProperty("cursor").GetProperty("col").GetInt32());
        AssertPlainParity(server);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\u001b[2m   ")]
    [InlineData("\u001b[41m   ")]
    [InlineData("\u001b[2m\u3000")]
    [InlineData("\u00a0")]
    public void TrailingWhitespaceTrimsEvenWhenStyled(string suffix)
    {
        using var session = new TerminalSession(40, 4);
        using var server = new ControlServer(session);
        Feed(session, "\u001b[2m界" + suffix);
        var runs = Runs(Read(server));
        Assert.Single(runs);
        Assert.Equal("界", runs[0].GetProperty("text").GetString());
        Assert.Equal(2, runs[0].GetProperty("width").GetInt32());
        AssertPlainParity(server);
    }

    [Fact]
    public void EmptyRowsTrimOnlyAtEndAndCursorCanBeOnAnOmittedRow()
    {
        using var session = new TerminalSession(40, 5);
        using var server = new ControlServer(session);
        Assert.Empty(Read(server).GetProperty("rows").EnumerateArray());
        Feed(session, "\r\nx\r\n\r\ny\r\n\u001b[2m  ");
        var result = Read(server);
        Assert.Equal(4, result.GetProperty("rows").GetArrayLength());
        Assert.Empty(Runs(result, 0));
        Assert.Empty(Runs(result, 2));
        Assert.Equal(4, result.GetProperty("cursor").GetProperty("row").GetInt32());
        AssertPlainParity(server);
    }

    [Theory]
    [InlineData(0, 0, 3)]
    [InlineData(2, 1, 2)]
    [InlineData(4, -1, 4)]
    [InlineData(100, -2, 5)]
    public void LinesSelectTheSameHistoryAndScreenRows(int lines, int firstRow, int count)
    {
        using var session = new TerminalSession(20, 3);
        using var server = new ControlServer(session);
        Feed(session, "\u001b[2mone\u001b[0m\r\ntwo\r\nthree\r\nfour\r\nfive");
        var args = new { styles = true, lines };
        var result = Read(server, args);
        Assert.Equal(count, result.GetProperty("rows").GetArrayLength());
        Assert.Equal(firstRow, result.GetProperty("rows")[0].GetProperty("row").GetInt32());
        AssertPlainParity(server, args, new { lines });
        var all = Read(server, new { styles = true, all = true });
        Assert.Equal(-2, all.GetProperty("rows")[0].GetProperty("row").GetInt32());
        Assert.True(Runs(all)[0].GetProperty("faint").GetBoolean());
        AssertPlainParity(server, new { styles = true, all = true }, new { all = true });
    }

    [Fact]
    public void OmittedAndFalseStylesKeepTheExactPlainEnvelope()
    {
        using var session = new TerminalSession(40, 4);
        using var server = new ControlServer(session);
        Feed(session, "\u001b[2mhello\u001b[0m   ");
        const string expected = "{\"ok\":true,\"result\":\"hello\"}";
        Assert.Equal(expected, server.Dispatch("{\"cmd\":\"session.text\"}"));
        Assert.Equal(expected, server.Dispatch("{\"cmd\":\"session.text\",\"args\":{\"styles\":false}}"));
    }

    [Fact]
    public void InvalidRangeAndUnknownTargetStillRefuse()
    {
        using var session = new TerminalSession(40, 4);
        using var server = new ControlServer(session);
        using var conflict = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.text\",\"args\":{\"styles\":true,\"all\":true,\"lines\":0}}"));
        Assert.False(conflict.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(OverlayPanes.AllWithLines, conflict.RootElement.GetProperty("error").GetString());
        using var hostServer = new ControlServer(new FakeSessionHost());
        using var unknown = JsonDocument.Parse(hostServer.Dispatch("{\"cmd\":\"session.text\",\"target\":\"missing\",\"args\":{\"styles\":true}}"));
        Assert.False(unknown.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("no session", unknown.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Object, Read(hostServer, target: "s1").ValueKind);
    }

    [Fact]
    public void ServerReplicaReturnsItsOwnStylesAndCursor()
    {
        using var backend = new ServerSessionBackend("agwinterm-test-" + Guid.NewGuid().ToString("N"), exePath: null);
        using var session = new ServerSession(backend, "pane-1", 40, 4);
        using var server = new ControlServer(session);
        Feed(session, "> \u001b[2;38;5;244mhint\u001b[0m\r\u001b[2C");
        var result = Read(server);
        Assert.True(Runs(result)[1].GetProperty("faint").GetBoolean());
        Assert.Equal("idx:244", Runs(result)[1].GetProperty("fg").GetString());
        Assert.Equal(2, result.GetProperty("cursor").GetProperty("col").GetInt32());
        AssertPlainParity(server);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliSendsStylesAndPrintsTheResultOrRequestedEnvelope(bool envelope)
    {
        using var session = new TerminalSession(40, 4);
        string pipe = "agwinterm-test-styles-" + Guid.NewGuid().ToString("N");
        using var server = new ControlServer(session, pipe);
        Feed(session, "> \u001b[2mhint");
        server.Start();
        var args = new List<string> { "session", "text", "--styles", "--all", "--pipe", pipe, "--target", "active" };
        if (envelope) args.Add("--json");
        var (exitCode, stdout, stderr) = await RunCtl(args);
        Assert.True(exitCode == 0, $"CLI exit {exitCode}: {stderr}");
        Assert.Equal("", stderr);
        using var doc = JsonDocument.Parse(stdout);
        var result = doc.RootElement;
        if (envelope)
        {
            Assert.True(result.GetProperty("ok").GetBoolean());
            result = result.GetProperty("result");
        }
        Assert.True(Runs(result)[1].GetProperty("faint").GetBoolean());
        Assert.Equal("hint", Runs(result)[1].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"true\"")]
    [InlineData("\"1\"")]
    public void OverlayTextRefusesStylesBeforeReadingTheHost(string value)
    {
        using var server = new ControlServer(new FakeSessionHost());
        // No overlay exists: reaching the host would return "no overlay", not this refusal.
        using var doc = JsonDocument.Parse(server.Dispatch(
            "{\"cmd\":\"session.overlay\",\"args\":{\"action\":\"text\",\"styles\":" + value + "}}"));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(OverlayPanes.StylesRefusal, doc.RootElement.GetProperty("error").GetString());
        using var plain = JsonDocument.Parse(server.Dispatch(
            "{\"cmd\":\"session.overlay\",\"args\":{\"action\":\"text\",\"styles\":false}}"));
        Assert.Equal("no overlay", plain.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliRefusesOverlayStylesBeforeConnecting(bool pane)
    {
        // A unique, unserved pipe makes a connection attempt fail; exit 2 and this refusal
        // prove that the CLI rejected the option locally, before sending anything.
        var args = new List<string> { "session", "overlay", "text", "--styles", "--pipe",
            "agwinterm-test-unserved-" + Guid.NewGuid().ToString("N") };
        if (pane) args.AddRange(["--pane", "left"]);
        var (exitCode, stdout, stderr) = await RunCtl(args);
        Assert.Equal(2, exitCode);
        Assert.Equal("", stdout);
        Assert.Equal(OverlayPanes.StylesRefusal + Environment.NewLine, stderr);
        Assert.Contains("session text --styles --target <overlay-id>", stderr);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCtl(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "agwintermctl.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await stdout, await stderr);
    }

    public sealed class NativeCoreFactAttribute : FactAttribute
    {
        public NativeCoreFactAttribute()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agwinterm.slnx"))) dir = dir.Parent;
            if (dir is null || !RustEmulatorCore.TryLoad(Path.Combine(dir.FullName, "native", "target", "release", "agwinterm_core.dll"), out _))
                Skip = "Native Rust core is not built or available locally";
        }
    }

    [NativeCoreFact]
    public void RustBackendMatchesManagedStylesIncludingHistory()
    {
        const string input = "> \u001b[2;38;5;244mhint\u001b[0m\r\n界😀\r\n\u001b[1;3;4;7;9;48;2;1;2;3mlast\u001b[0m";
        using var managed = new TerminalSession(40, 2);
        using var managedServer = new ControlServer(managed);
        Feed(managed, input);
        var old = TerminalSession.CoreFactory;
        try
        {
            TerminalSession.CoreFactory = (cols, rows) => new RustTerminalCore(cols, rows);
            using var rust = new TerminalSession(40, 2);
            using var rustServer = new ControlServer(rust);
            Feed(rust, input);
            var args = new { styles = true, all = true };
            Assert.Equal(Read(managedServer, args).GetRawText(), Read(rustServer, args).GetRawText());
            AssertPlainParity(rustServer, args, new { all = true });
        }
        finally { TerminalSession.CoreFactory = old; }
    }
}
