using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>Pty-host integration tests (#105, Phase 2a): a real PtyHostServer over real named pipes
/// hosting real child processes — only the process boundary of `--pty-host` is elided.</summary>
public class PtyHostTests : IDisposable
{
    private readonly string _appId = "agwinterm-test-" + Guid.NewGuid().ToString("N")[..8];
    private readonly PtyHostServer _server;

    public PtyHostTests() => _server = new PtyHostServer(_appId);
    public void Dispose() => _server.Dispose();

    /// <summary>Type a line and make sure it took (raw-stream twin of ServerSessionTests.TypeLine):
    /// input during cmd/conhost console init can be silently discarded, and `cmd /q` prints no
    /// prompt on newer Windows — so retype every ~2.5s until the echo shows up in the stream.</summary>
    private static string TypeUntilEcho(Stream data, string line, string marker, int timeoutMs = 20000)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\r");
        var all = new System.Text.StringBuilder();
        var buf = new byte[16 * 1024];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long nextTypeAt = 0;
        using var cts = new CancellationTokenSource();
        Task<int>? pending = null;
        try
        {
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (sw.ElapsedMilliseconds >= nextTypeAt)
                {
                    data.Write(bytes); data.Flush();
                    nextTypeAt = sw.ElapsedMilliseconds + 2500;
                }
                pending ??= data.ReadAsync(buf, 0, buf.Length, cts.Token);
                if (!pending.Wait(250)) continue;             // keep ONE read in flight across retries
                int n = pending.Result; pending = null;
                if (n <= 0) break;
                all.Append(System.Text.Encoding.UTF8.GetString(buf, 0, n));
                if (all.ToString().Contains(marker, StringComparison.Ordinal)) break;
            }
        }
        finally { DrainPending(ref pending, cts); }           // never dispose the pipe with a read in flight (#118)
        return all.ToString();
    }

    private static string ReadUntil(Stream data, Func<string, bool> done, int timeoutMs = 15000)
    {
        var all = new System.Text.StringBuilder();
        var buf = new byte[16 * 1024];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        Task<int>? read = null;
        try
        {
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                read = data.ReadAsync(buf, 0, buf.Length, cts.Token);
                if (!read.Wait(Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds))) break;
                int n = read.Result; read = null;
                if (n <= 0) break;
                all.Append(System.Text.Encoding.UTF8.GetString(buf, 0, n));
                if (done(all.ToString())) return all.ToString();
            }
        }
        finally { DrainPending(ref read, cts); }              // never dispose the pipe with a read in flight (#118)
        return all.ToString();
    }

    /// <summary>Cancel an in-flight read and WAIT for it to finish, so the caller's `using` can
    /// dispose the pipe without an overlapped op outstanding (the #118 crash class).</summary>
    private static void DrainPending(ref Task<int>? pending, CancellationTokenSource cts)
    {
        if (pending is null) return;
        cts.Cancel();
        try { pending.Wait(5000); } catch (AggregateException) { }    // canceled/IO-error = drained
        pending = null;
    }

    [Fact]
    public void Hello_RejectsProtocolMismatch()
    {
        Assert.Contains("\"ok\":true", _server.Dispatch($"{{\"cmd\":\"hello\",\"protocol\":{PtyHostServer.ProtocolVersion}}}"));
        Assert.Contains("protocol mismatch", _server.Dispatch("{\"cmd\":\"hello\",\"protocol\":999}"));
        Assert.Contains("protocol mismatch", _server.Dispatch("{\"cmd\":\"hello\"}"));
    }

    [Fact]
    public void CreateAttachTypeReadKill_RoundTrips()
    {
        using var client = PtyHostClient.Connect(_appId);
        string id = client.Create(Guid.NewGuid().ToString(), 100, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        using var att = client.Attach(id);
        Assert.False(att.HasExited);
        Assert.True(att.ChildPid > 0);

        // Type through the data pipe; the echo must come back as raw ConPTY output.
        string seen = TypeUntilEcho(att.Data, "echo host+work", "host+work");
        Assert.Contains("host+work", seen);

        client.Kill(id);
        Assert.Empty(client.List());
    }

    [Fact]
    public void DetachedSessionSurvives_ReattachSeedsScrollbackAndStreams()
    {
        using var client = PtyHostClient.Connect(_appId);
        string id = client.Create(Guid.NewGuid().ToString(), 100, 24, "cmd.exe", new[] { "/q" }, verbatim: true);

        using (var first = client.Attach(id))
        {
            Assert.Contains("before-detach", TypeUntilEcho(first.Data, "echo before-detach", "before-detach"));
        }   // disposing the data pipe = detach; the shell must keep running

        var info = Assert.Single(client.List());
        Assert.False(info.HasExited);
        Assert.False(info.Attached);

        // Output printed while NOBODY is attached must not be lost — it lands in the host emulator...
        Thread.Sleep(300);

        // ...and a reattach hands it back: the scrollback+grid snapshot contains the earlier echo.
        using var second = client.Attach(id, repaint: true);
        var emu = new TerminalEmulator(second.Cols, second.Rows);
        emu.SeedScrollback(second.Scrollback);
        emu.Feed(System.Text.Encoding.UTF8.GetBytes(second.Modes));
        bool inHistory = second.Scrollback.Any(l => l.Contains("before-detach"));
        // The live viewport arrives via the repaint jiggle on the new data pipe.
        string live = ReadUntil(second.Data, s => s.Contains("before-detach"), timeoutMs: 8000);
        Assert.True(inHistory || live.Contains("before-detach"),
            $"echo lost across detach/reattach (history={inHistory}, live={live.Length} chars)");

        // The revived stream is fully interactive.
        second.Data.Write("echo after-reattach\r"u8.ToArray()); second.Data.Flush();
        Assert.Contains("after-reattach", ReadUntil(second.Data, s => s.Contains("after-reattach")));

        client.Kill(id);
    }

    [Fact]
    public void ChildExit_ClosesDataPipe_AndListReportsCode()
    {
        using var client = PtyHostClient.Connect(_appId);
        string id = client.Create(Guid.NewGuid().ToString(), 80, 24, "cmd.exe", new[] { "/q", "/c", "exit", "42" }, verbatim: true);
        using var att = client.Attach(id);
        // EOF on the data pipe is the exit signal...
        ReadUntil(att.Data, _ => false, timeoutMs: 10000);
        // ...and the exit code travels via list.
        var info = Assert.Single(client.List());
        Assert.True(info.HasExited);
        Assert.Equal(42, info.ExitCode);
        client.Kill(id);
    }

    [Fact]
    public void Resize_ReachesTheSession()
    {
        using var client = PtyHostClient.Connect(_appId);
        string id = client.Create(Guid.NewGuid().ToString(), 80, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        client.Resize(id, 132, 40);
        var info = Assert.Single(client.List());
        Assert.Equal((132, 40), (info.Cols, info.Rows));
        client.Kill(id);
    }

    [Fact]
    public void DuplicateCreate_IsRefused()
    {
        using var client = PtyHostClient.Connect(_appId);
        string id = client.Create("fixed-id", 80, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        Assert.Throws<InvalidOperationException>(() => client.Create("fixed-id", 80, 24, "cmd.exe", new[] { "/q" }));
        client.Kill(id);
    }

    [Fact]
    public void Shutdown_KillsSessionsAndCompletes()
    {
        var server = new PtyHostServer(_appId + "-sd");
        using var client = PtyHostClient.Connect(_appId + "-sd");
        client.Create(Guid.NewGuid().ToString(), 80, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        client.Shutdown();
        Assert.True(server.Completion.Wait(5000), "shutdown must complete the host");
    }
}

public class DumpModesTests
{
    [Fact]
    public void DumpModes_RoundTripsToAFreshEmulator()
    {
        var a = new TerminalEmulator(80, 24);
        a.Feed("\x1b[?1002h\x1b[?1006h\x1b[?2004h\x1b[?25l\x1b]0;my title\x07\x1b]7;file://PC/C:/work\x07"u8);
        var b = new TerminalEmulator(80, 24);
        b.Feed(System.Text.Encoding.UTF8.GetBytes(a.DumpModes()));
        Assert.True(b.MouseReporting);
        Assert.True(b.MouseReportsMotion);
        Assert.True(b.MouseSgr);
        Assert.True(b.BracketedPaste);
        Assert.False(b.CursorVisible);
        Assert.Equal("my title", b.Title);
        Assert.Equal("file://PC/C:/work", b.Cwd);
        Assert.False(b.IsAltScreen);

        // And a clean terminal dumps (nearly) nothing.
        Assert.Equal("", new TerminalEmulator(80, 24).DumpModes());
    }

    [Fact]
    public void DumpModes_CarriesAltScreen()
    {
        var a = new TerminalEmulator(80, 24);
        a.Feed("\x1b[?1049h"u8);
        var b = new TerminalEmulator(80, 24);
        b.Feed(System.Text.Encoding.UTF8.GetBytes(a.DumpModes()));
        Assert.True(b.IsAltScreen);
    }
}
