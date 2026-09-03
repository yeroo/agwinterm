using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>Contract tests for <c>statusChangedAt</c> — epoch seconds of a session's last status
/// WRITE, carried on every tree node. The caller's question is "is that agent's hook still alive",
/// so the tests pin the two things that answer it: a re-assert of the SAME status re-stamps, and the
/// age reported belongs to the pane whose status the tree is showing.</summary>
public class StatusChangedAtTests
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static JsonElement Tree(ControlServer s) =>
        JsonDocument.Parse(s.Dispatch("{\"cmd\":\"tree\"}")).RootElement.GetProperty("result");

    private static JsonElement Node(ControlServer s, string id) =>
        Tree(s).GetProperty("workspaces").EnumerateArray()
            .SelectMany(w => w.GetProperty("sessions").EnumerateArray())
            .First(n => n.GetProperty("id").GetString() == id);

    [Fact]
    public void AWrittenStatus_ReportsAPlausibleEpoch()
    {
        using var session = new TerminalSession(80, 24);
        session.SetStatus(AgentStatus.Active);

        // Seconds since 1970, now-ish — not milliseconds, not ticks, not zero.
        Assert.InRange(session.StatusChangedAt, Now - 5, Now + 5);
    }

    [Fact]
    public void NeverWritten_ReportsItsOwnAge_NotZero()
    {
        using var session = new TerminalSession(80, 24);

        Assert.Equal(AgentStatus.Idle, session.Status);
        Assert.InRange(session.StatusChangedAt, Now - 5, Now + 5);
    }

    [Fact]
    public void ReassertingTheSameStatus_MovesTheTimestamp()
    {
        using var session = new TerminalSession(80, 24);
        session.SetStatus(AgentStatus.Active);
        session.BackdateStatus(Now - 600);   // as if the hook last reported ten minutes ago

        session.SetStatus(AgentStatus.Active);   // the same status again: a live hook re-asserting

        Assert.InRange(session.StatusChangedAt, Now - 5, Now + 5);
    }

    [Fact]
    public void ANoOpWrite_DoesNotFireStatusChanged()
    {
        using var session = new TerminalSession(80, 24);
        session.SetStatus(AgentStatus.Active);
        int fired = 0;
        session.StatusChanged += () => fired++;

        session.SetStatus(AgentStatus.Active);

        Assert.Equal(0, fired);                                       // the repaint has no reason to run
        Assert.InRange(session.StatusChangedAt, Now - 5, Now + 5);    // the clock still moved
    }

    [Fact]
    public void ServerSession_StampsTheSameWay()
    {
        // Status is a UI-process concept the host knows nothing about, so no host is needed here:
        // an unstarted ServerSession never touches the wire.
        using var backend = new ServerSessionBackend("agwinterm-test-" + Guid.NewGuid().ToString("N")[..8], exePath: null);
        using var session = new ServerSession(backend, "pane-1", 80, 24);
        session.SetStatus(AgentStatus.Blocked);
        session.BackdateStatus(Now - 600);
        session.SetStatus(AgentStatus.Blocked);

        Assert.InRange(session.StatusChangedAt, Now - 5, Now + 5);
    }

    // ---- tree JSON ----

    [Fact]
    public void TreeCarriesTheField_EvenForAnIdleSession()
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);

        var n = Node(server, "s1");
        Assert.Equal("idle", n.GetProperty("status").GetString());
        // Always present: a consumer distinguishing "absent" from "old" gains nothing from omission.
        Assert.InRange(n.GetProperty("statusChangedAt").GetInt64(), Now - 5, Now + 5);
    }

    [Fact]
    public void TheSingleSessionConvenienceHost_CarriesTheRealStamp()
    {
        // new ControlServer(ISession) wraps the session in SingleSessionHost, whose Tree builds its
        // one snapshot positionally — the only place a forgotten named argument silently reports 0.
        using var session = new TerminalSession(80, 24);
        using var server = new ControlServer(session);

        var n = Node(server, "single");
        Assert.InRange(n.GetProperty("statusChangedAt").GetInt64(), Now - 5, Now + 5);

        session.SetStatus(AgentStatus.Blocked);
        session.BackdateStatus(Now - 90);
        Assert.Equal(Now - 90, Node(server, "single").GetProperty("statusChangedAt").GetInt64());
    }

    [Fact]
    public void TreeStampMovesWhenTheStatusVerbWrites()
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        ((TerminalSession)host.ActiveSess!.Panes[0]).BackdateStatus(Now - 600);
        Assert.InRange(Node(server, "s1").GetProperty("statusChangedAt").GetInt64(), Now - 700, Now - 500);

        server.Dispatch("{\"cmd\":\"session.status\",\"target\":\"s1\",\"args\":{\"status\":\"active\"}}");

        var n = Node(server, "s1");
        Assert.Equal("active", n.GetProperty("status").GetString());
        Assert.InRange(n.GetProperty("statusChangedAt").GetInt64(), Now - 5, Now + 5);
    }

    [Fact]
    public void WhenPanesDisagree_TheWINNINGPanesStampIsReported()
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        var s = host.ActiveSess!;
        var quiet = (TerminalSession)s.Panes[0];
        var loud = (TerminalSession)s.AddPane();

        quiet.SetStatus(AgentStatus.Idle);              // fresh, but loses the severity contest
        loud.SetStatus(AgentStatus.Blocked);
        loud.BackdateStatus(Now - 3600);                // blocked an hour ago, and still blocked

        var n = Node(server, "s1");
        Assert.Equal("blocked", n.GetProperty("status").GetString());
        // The age must describe the status actually shown, so it is the blocked pane's, an hour old.
        Assert.InRange(n.GetProperty("statusChangedAt").GetInt64(), Now - 3700, Now - 3500);
    }

    [Fact]
    public void WhenPanesTieAtTheWinningSeverity_TheFreshestWins()
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        var s = host.ActiveSess!;
        var stale = (TerminalSession)s.Panes[0];
        var fresh = (TerminalSession)s.AddPane();

        stale.SetStatus(AgentStatus.Active);
        stale.BackdateStatus(Now - 3600);
        fresh.SetStatus(AgentStatus.Active);            // same severity, reported just now

        // A session is as fresh as its freshest contributor to the winning status.
        Assert.InRange(Node(server, "s1").GetProperty("statusChangedAt").GetInt64(), Now - 5, Now + 5);
    }

    [Fact]
    public void StatusAndItsAgeAlwaysDescribeTheSamePane()
    {
        // The rule is one implementation (StatusAggregate), used by both the app and the fake host.
        using var a = new TerminalSession(80, 24);
        using var b = new TerminalSession(80, 24);
        a.SetStatus(AgentStatus.Completed);
        a.BackdateStatus(1_000_000);
        b.SetStatus(AgentStatus.Active);
        b.BackdateStatus(2_000_000);
        var panes = new ISession[] { a, b };

        Assert.Equal((AgentStatus.Completed, 1_000_000L), StatusAggregate.WinnerAndChangedAt(panes));   // completed beats active
        Assert.Equal(AgentStatus.Completed, StatusAggregate.Winner(panes));
    }

    // Statuses are written without a lock. A two-scan aggregation (find the winning severity, then
    // find a pane that still has it) can see the winner cleared between its scans and report the
    // won status with statusChangedAt 0 — "silent since 1970". This pane flips from blocked to
    // idle after its first read, which is exactly the interleaving; a single pass must still pair
    // the blocked status it saw with that pane's stamp.
    [Fact]
    public void AStatusClearedDuringAggregation_StillPairsWithItsOwnStamp()
    {
        var racing = new RacingStatusSession(AgentStatus.Blocked, then: AgentStatus.Idle, changedAt: 1_700_000_000);
        using var steady = new TerminalSession(80, 24);
        steady.SetStatus(AgentStatus.Active);
        steady.BackdateStatus(1_600_000_000);

        var (status, at) = StatusAggregate.WinnerAndChangedAt(new ISession[] { racing, steady });

        Assert.Equal(AgentStatus.Blocked, status);
        Assert.Equal(1_700_000_000, at);
        Assert.Equal(1, racing.StatusReads);
    }

    /// <summary>An ISession whose status is one thing on the first read and another after it —
    /// the unlocked concurrent write, made deterministic. Only the status members are real.</summary>
    private sealed class RacingStatusSession : ISession
    {
        private readonly AgentStatus _first, _then;
        public int StatusReads;
        public RacingStatusSession(AgentStatus first, AgentStatus then, long changedAt)
        { _first = first; _then = then; StatusChangedAt = changedAt; }

        public AgentStatus Status => StatusReads++ == 0 ? _first : _then;
        public long StatusChangedAt { get; }

        private static Exception NotPartOfThisTest() => new NotSupportedException("status members only");
        public ITerminalCore Emulator => throw NotPartOfThisTest();
        public int Cols => throw NotPartOfThisTest();
        public int Rows => throw NotPartOfThisTest();
        public int? ChildProcessId => null;
        public object SyncRoot { get; } = new();
        public event Action? OutputReceived { add { } remove { } }
        public bool Blink => false;
        public bool AutoReset => false;
        public event Action? StatusChanged { add { } remove { } }
        public event Action<string?>? SoundRequested { add { } remove { } }
        public void SetStatus(AgentStatus status, bool blink = false, bool autoReset = false, bool sound = false, string? soundName = null)
            => throw NotPartOfThisTest();
        public void NotifyActivity() => throw NotPartOfThisTest();
        public Task<int> RunAsync(string app, string[] commandLine, bool verbatimCommandLine = false, CancellationToken ct = default)
            => throw NotPartOfThisTest();
        public Task StartAsync(string app, string[] commandLine, bool verbatimCommandLine = false,
            IReadOnlyDictionary<string, string>? extraEnv = null, string? cwd = null, bool deElevate = false,
            bool freshEnv = true, CancellationToken ct = default) => throw NotPartOfThisTest();
        public void Attach(Microsoft.Win32.SafeHandles.SafeFileHandle conOut, Microsoft.Win32.SafeHandles.SafeFileHandle conIn,
            Microsoft.Win32.SafeHandles.SafeFileHandle signal, IntPtr clientProcess, int pid) => throw NotPartOfThisTest();
        public int? ExitCode => null;
        public bool HasExited => false;
        public event Action<int>? Exited { add { } remove { } }
        public void Inject(ReadOnlySpan<byte> bytes) => throw NotPartOfThisTest();
        public void MutateLocked(Action<ITerminalCore> mutate) => throw NotPartOfThisTest();
        public void Write(ReadOnlySpan<byte> bytes) => throw NotPartOfThisTest();
        public void Resize(int cols, int rows) => throw NotPartOfThisTest();
        public string SnapshotRow(int row) => throw NotPartOfThisTest();
        public (int Row, int Col) SnapshotCursor() => throw NotPartOfThisTest();
        public void Detach() { }
        public void Dispose() { }
    }
}
