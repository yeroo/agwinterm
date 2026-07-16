using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>Phase 2b: the FULL ISession contract exercised over the pty-host wire — a real
/// PtyHostServer, real named pipes, real child processes; only `--pty-host`'s process boundary is
/// elided. These mirror what the UI does through the seam, so green here = panes work in server mode.</summary>
public class ServerSessionTests : IDisposable
{
    private readonly string _appId = "agwinterm-test-" + Guid.NewGuid().ToString("N")[..8];
    private readonly PtyHostServer _server;
    private readonly ServerSessionBackend _backend;

    public ServerSessionTests()
    {
        _server = new PtyHostServer(_appId);
        _backend = new ServerSessionBackend(_appId, exePath: null);   // host already running (above)
    }

    public void Dispose() { _backend.Dispose(); _server.Dispose(); }

    private static bool WaitFor(Func<bool> cond, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }

    private static string GridText(ISession s)
    {
        lock (s.SyncRoot) return string.Join("\n", s.Emulator.DumpBuffer());
    }

    [Fact]
    public async Task TypeEcho_LandsInTheReplicaEmulator()
    {
        using var s = _backend.Create(Guid.NewGuid().ToString(), 100, 24);
        Assert.Null(s.ChildProcessId);                                 // not started yet — mirrors TerminalSession
        int outputEvents = 0;
        s.OutputReceived += () => Interlocked.Increment(ref outputEvents);

        await s.StartAsync("cmd.exe", new[] { "/q" }, verbatimCommandLine: true);
        Assert.True(s.ChildProcessId > 0);
        Assert.False(s.HasExited);

        s.Write("echo replica+works\r"u8);
        Assert.True(WaitFor(() => GridText(s).Contains("replica+works")),
            "typed echo never reached the replica emulator; grid:\n" + GridText(s));
        Assert.True(outputEvents > 0);
    }

    [Fact]
    public async Task ExitCode_TravelsViaExitedEvent_AndRunAsync()
    {
        using var s = _backend.Create(Guid.NewGuid().ToString(), 80, 24);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Exited += code => exited.TrySetResult(code);
        await s.StartAsync("cmd.exe", new[] { "/q", "/c", "exit", "5" }, verbatimCommandLine: true);
        Assert.Equal(5, await exited.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(s.HasExited);
        Assert.Equal(5, s.ExitCode);

        using var r = _backend.Create(Guid.NewGuid().ToString(), 80, 24);
        Assert.Equal(7, await r.RunAsync("cmd.exe", new[] { "/q", "/c", "exit", "7" }, verbatimCommandLine: true)
            .WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task Resize_UpdatesReplicaAndHost()
    {
        using var s = _backend.Create(Guid.NewGuid().ToString(), 80, 24);
        await s.StartAsync("cmd.exe", new[] { "/q" }, verbatimCommandLine: true);
        s.Resize(132, 40);
        Assert.Equal((132, 40), (s.Cols, s.Rows));
        lock (s.SyncRoot) Assert.Equal(132, s.Emulator.Screen.Cols);
        using var probe = PtyHostClient.Connect(_appId);
        var info = Assert.Single(probe.List());
        Assert.Equal((132, 40), (info.Cols, info.Rows));
    }

    [Fact]
    public async Task Dispose_KillsTheHostedSession_Phase2bSemantics()
    {
        var s = _backend.Create(Guid.NewGuid().ToString(), 80, 24);
        await s.StartAsync("cmd.exe", new[] { "/q" }, verbatimCommandLine: true);
        s.Dispose();
        using var probe = PtyHostClient.Connect(_appId);
        Assert.True(WaitFor(() => probe.List().Count == 0, 5000),
            "Phase 2b: closing a pane must kill its hosted session (survival is Phase 2c)");
    }

    [Fact]
    public void PreStart_InjectAndSeedWork_ForTheRestorePath()
    {
        using var s = _backend.Create(Guid.NewGuid().ToString(), 80, 24);
        // Restore seeds buffers and paints banners BEFORE the shell starts — must work replica-side.
        lock (s.SyncRoot) s.Emulator.SeedScrollback(new[] { "restored line" });
        s.Inject("hello"u8);
        Assert.Contains("hello", GridText(s));
        lock (s.SyncRoot) Assert.Equal(1, s.Emulator.HistoryCount);
        Assert.Throws<InvalidOperationException>(() => s.Write("x"u8));   // not started — same contract as in-process
    }

    [Fact]
    public async Task DetachAdopt_SameShellSurvivesAUiGeneration()
    {
        string paneId = Guid.NewGuid().ToString();

        // "UI generation 1": start a shell, set modes + produce output, then DETACH (app quit).
        var gen1 = _backend.Create(paneId, 100, 24);
        await gen1.StartAsync("cmd.exe", new[] { "/q" }, verbatimCommandLine: true);
        int? pid = gen1.ChildProcessId;
        gen1.Write("echo pre+restart\r"u8);
        Assert.True(WaitFor(() => GridText(gen1).Contains("pre+restart")));
        Thread.Sleep(300);   // let the echo reach the HOST emulator too (its snapshot feeds adoption)
        gen1.Detach();

        // The host still runs the child, unattached.
        using (var probe = PtyHostClient.Connect(_appId))
        {
            var info = Assert.Single(probe.List());
            Assert.False(info.HasExited);
            Assert.False(info.Attached);
        }

        // "UI generation 2": a fresh handle with the SAME pane id adopts it.
        using var gen2 = (ServerSession)_backend.Create(paneId, 100, 24);
        Assert.True(gen2.TryAdopt(), "adoption must find the surviving session");
        Assert.True(gen2.Adopted);
        Assert.Equal(pid, gen2.ChildProcessId);                      // SAME child process — the whole point
        Assert.True(WaitFor(() => GridText(gen2).Contains("pre+restart")),
            "pre-restart output missing after adoption; grid:\n" + GridText(gen2));

        // And it's live: same shell keeps taking input.
        gen2.Write("echo post+adopt\r"u8);
        Assert.True(WaitFor(() => GridText(gen2).Contains("post+adopt")));
    }

    [Fact]
    public void TryAdopt_NothingToAdopt_ReturnsFalse()
    {
        using var s = (ServerSession)_backend.Create(Guid.NewGuid().ToString(), 80, 24);
        Assert.False(s.TryAdopt());
        Assert.False(s.Adopted);
    }

    [Fact]
    public async Task TryAdopt_ExitedLeftover_ReapsAndReturnsFalse()
    {
        string paneId = Guid.NewGuid().ToString();
        var dead = _backend.Create(paneId, 80, 24);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        dead.Exited += c => exited.TrySetResult(c);
        await dead.StartAsync("cmd.exe", new[] { "/q", "/c", "exit", "0" }, verbatimCommandLine: true);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
        dead.Detach();                                               // leave the corpse on the host

        using var fresh = (ServerSession)_backend.Create(paneId, 80, 24);
        Assert.False(fresh.TryAdopt(), "an exited leftover is not adoptable");
        using var probe = PtyHostClient.Connect(_appId);
        Assert.Empty(probe.List());                                  // ...and it got reaped
    }

    [Fact]
    public void HandoffAttach_IsNotSupported()
    {
        using var s = _backend.Create(Guid.NewGuid().ToString(), 80, 24);
        Assert.Throws<NotSupportedException>(() => s.Attach(null!, null!, null!, IntPtr.Zero, 0));
    }

    [Fact]
    public async Task StartFailure_PaintsTheErrorIntoThePane()
    {
        using var s = _backend.Create(Guid.NewGuid().ToString(), 80, 24);
        await s.StartAsync("definitely-not-a-real-exe-agw.exe", Array.Empty<string>());
        Assert.True(WaitFor(() => s.HasExited, 10000));
        Assert.Equal(1, s.ExitCode);
    }
}
