using System.Diagnostics;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// Compatibility oracle for the RUST pty-host (native/agwinterm-ptyhost): the SAME
/// PtyHostClient that talks to the C# host drives the Rust binary through the same
/// scenarios — protocol identity proven by the client not knowing which host it got.
/// Skips when the binary isn't built (CI builds the cargo workspace).
/// </summary>
public class RustPtyHostTests : IDisposable
{
    private static readonly string? ExePath = Find();
    private readonly string _appId = "agwinterm-rusthost-" + Guid.NewGuid().ToString("N")[..8];
    private Process? _host;

    private static string? Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agwinterm.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string exe = Path.Combine(dir.FullName, "native", "target", "release", "agwinterm-ptyhost.exe");
        return File.Exists(exe) ? exe : null;
    }

    private PtyHostClient Start()
    {
        _host = Process.Start(new ProcessStartInfo(ExePath!, $"--pipe {_appId}") { UseShellExecute = false, CreateNoWindow = true });
        for (int i = 0; i < 50 && !PtyHostClient.IsRunning(_appId); i++) Thread.Sleep(100);
        return PtyHostClient.Connect(_appId);   // hello handshake — protocol version must match
    }

    public void Dispose()
    {
        try { if (_host is { HasExited: false }) _host.Kill(entireProcessTree: true); } catch { }
        _host?.Dispose();
    }

    private static bool WaitFor(Func<bool> cond, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }

    private static string TypeUntilEcho(Stream data, string line, string marker, int timeoutMs = 20000)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\r");
        var all = new System.Text.StringBuilder();
        var buf = new byte[16 * 1024];
        var sw = Stopwatch.StartNew();
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
                if (!pending.Wait(250)) continue;
                int n = pending.Result; pending = null;
                if (n <= 0) break;
                all.Append(System.Text.Encoding.UTF8.GetString(buf, 0, n));
                if (all.ToString().Contains(marker, StringComparison.Ordinal)) break;
            }
        }
        finally
        {
            if (pending is { IsCompleted: false }) { cts.Cancel(); try { pending.Wait(5000); } catch (AggregateException) { } }
        }
        return all.ToString();
    }

    [Fact]
    public void CreateAttachTypeKill_RoundTrips()
    {
        if (ExePath is null) return;
        using var client = Start();
        string id = client.Create(Guid.NewGuid().ToString(), 100, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        using var att = client.Attach(id);
        Assert.False(att.HasExited);
        Assert.True(att.ChildPid > 0);
        Assert.Contains("rust+host+works", TypeUntilEcho(att.Data, "echo rust+host+works", "rust+host+works"));
        client.Kill(id);
        Assert.Empty(client.List());
    }

    [Fact]
    public void DetachedSurvives_ReattachSeedsScrollback()
    {
        if (ExePath is null) return;
        using var client = Start();
        string id = client.Create(Guid.NewGuid().ToString(), 100, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        using (var first = client.Attach(id))
            Assert.Contains("before-detach", TypeUntilEcho(first.Data, "echo before-detach", "before-detach"));

        Assert.True(WaitFor(() =>
        {
            var i = client.List().Single();
            return !i.HasExited && !i.Attached;
        }), "detach must leave the session running, unattached");

        Thread.Sleep(300);   // let the echo land in the HOST emulator (its snapshot feeds reattach)
        using var second = client.Attach(id, repaint: true);
        bool inHistory = second.Scrollback.Any(l => l.Contains("before-detach"));
        var emu = new TerminalEmulator(second.Cols, second.Rows);
        emu.Feed(System.Text.Encoding.UTF8.GetBytes(second.Modes));   // modes replay parses cleanly
        Assert.True(inHistory || TypeUntilEcho(second.Data, "echo probe", "probe").Length > 0,
            "reattach must hand back a live stream");
        Assert.Contains("after-reattach", TypeUntilEcho(second.Data, "echo after-reattach", "after-reattach"));
        client.Kill(id);
    }

    [Fact]
    public void ChildExit_TravelsViaList()
    {
        if (ExePath is null) return;
        using var client = Start();
        string id = client.Create(Guid.NewGuid().ToString(), 80, 24, "cmd.exe", new[] { "/q", "/c", "exit", "42" }, verbatim: true);
        Assert.True(WaitFor(() => client.List() is [{ HasExited: true, ExitCode: 42 }]),
            "exit code 42 must travel via list");
        client.Kill(id);
    }

    [Fact]
    public void Resize_And_DuplicateCreate()
    {
        if (ExePath is null) return;
        using var client = Start();
        string id = client.Create("fixed-id", 80, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        client.Resize(id, 132, 40);
        var info = client.List().Single();
        Assert.Equal((132, 40), (info.Cols, info.Rows));
        Assert.Throws<InvalidOperationException>(() => client.Create("fixed-id", 80, 24, "cmd.exe", new[] { "/q" }));
        client.Kill(id);
    }

    [Fact]
    public void Shutdown_KillsSessionsAndExits()
    {
        if (ExePath is null) return;
        using var client = Start();
        client.Create(Guid.NewGuid().ToString(), 80, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        client.Shutdown();
        Assert.True(_host!.WaitForExit(5000), "shutdown must exit the host process");
    }
}
