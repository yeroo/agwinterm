using System.Diagnostics;
using Agwinterm.Core;
using Microsoft.Win32.SafeHandles;

namespace Agwinterm.Pty;

/// <summary>
/// The pty-host session backend (#105, Phase 2b): sessions live in the <c>--pty-host</c> process;
/// the UI holds <see cref="ServerSession"/> handles. Selected by <c>session-host = server</c>.
/// Spawns the host on demand (same exe, host role) and connects lazily — construction is cheap and
/// never touches the wire; the first <see cref="Create"/> does.
/// </summary>
public sealed class ServerSessionBackend : ISessionBackend, IDisposable
{
    private readonly string _appId;
    private readonly string? _exePath;
    private readonly object _lock = new();
    private PtyHostClient? _client;

    /// <param name="appId">Instance id — names the host's control pipe.</param>
    /// <param name="exePath">The agwinterm exe to spawn with <c>--pty-host</c> when no host is
    /// running; null = require an already-running host (tests).</param>
    public ServerSessionBackend(string appId, string? exePath)
    {
        _appId = appId;
        _exePath = exePath;
    }

    public string Name => "server";

    public ISession Create(string id, int cols, int rows)
    {
        EnsureClient();   // connect/spawn NOW so an unreachable host fails here, where callers can fall back
        return new ServerSession(this, id, cols, rows);
    }

    internal PtyHostClient Client => EnsureClient();

    private PtyHostClient EnsureClient()
    {
        lock (_lock)
        {
            if (_client is not null) return _client;
            if (!PtyHostClient.IsRunning(_appId))
            {
                if (_exePath is null || !File.Exists(_exePath))
                    throw new InvalidOperationException("no pty-host is running and no exe to spawn one");
                var psi = new ProcessStartInfo(_exePath, $"--pty-host --pipe \"{_appId}\"")
                { UseShellExecute = false, CreateNoWindow = true };
                Process.Start(psi);
                for (int i = 0; i < 50 && !PtyHostClient.IsRunning(_appId); i++) Thread.Sleep(100);
            }
            return _client = PtyHostClient.Connect(_appId);
        }
    }

    public void Dispose()
    {
        lock (_lock) { _client?.Dispose(); _client = null; }
    }
}

/// <summary>
/// An <see cref="ISession"/> whose terminal lives in the pty-host process. The UI-side
/// <see cref="Emulator"/> is a REPLICA fed by the host's raw ConPTY stream (the host keeps the
/// authoritative one); input, resize, and lifecycle travel over the host protocol.
///
/// Lifecycle: <see cref="Dispose"/> KILLS the hosted session (explicit pane close);
/// <see cref="Detach"/> releases it alive (app quit) for a later <see cref="TryAdopt"/> — that
/// split is what lets shells survive UI restarts, updates, and crashes (#105, Phase 2c).
/// </summary>
public sealed class ServerSession : ISession
{
    private readonly object _sync = new();
    private readonly ServerSessionBackend _backend;
    private readonly string _id;   // = the pane id; names the hosted session (the adoption key)
    private Stream? _data;
    private int? _childPid;
    private volatile bool _started;
    private volatile bool _disposed;

    public TerminalEmulator Emulator { get; }
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int? ChildProcessId => _childPid;
    public object SyncRoot => _sync;
    public event Action? OutputReceived;

    public int? ExitCode { get; private set; }
    public bool HasExited { get; private set; }
    public event Action<int>? Exited;

    internal ServerSession(ServerSessionBackend backend, string id, int cols, int rows)
    {
        _backend = backend;
        _id = id;
        Cols = cols;
        Rows = rows;
        Emulator = new TerminalEmulator(cols, rows);
    }

    /// <summary>True when this handle reconnected to a SURVIVING hosted session instead of spawning
    /// a fresh shell — restore must then skip buffer seeding, agent-resume typing, and
    /// restore-commands (the real thing is still running; typing a resume into it would be chaos).</summary>
    public bool Adopted { get; private set; }

    // ---- Agent status: a UI-process concept (set via the control API), tracked locally exactly
    // like TerminalSession — the host knows nothing about it. Kept duplicated (25 lines) rather
    // than shared so the two implementations stay independently obvious.
    public AgentStatus Status { get; private set; } = AgentStatus.Idle;
    public bool Blink { get; private set; }
    public bool AutoReset { get; private set; }
    public event Action? StatusChanged;
    public event Action<string?>? SoundRequested;

    public void SetStatus(AgentStatus status, bool blink = false, bool autoReset = false,
        bool sound = false, string? soundName = null)
    {
        bool newBlink = blink && status != AgentStatus.Idle;
        bool newAuto = autoReset && status != AgentStatus.Idle;
        bool changed = Status != status || Blink != newBlink || AutoReset != newAuto;
        Status = status;
        Blink = newBlink;
        AutoReset = newAuto;
        if (sound && status != AgentStatus.Idle)
            try { SoundRequested?.Invoke(soundName); } catch { }
        if (changed) StatusChanged?.Invoke();
    }

    public void NotifyActivity()
    {
        if (Status is AgentStatus.Blocked or AgentStatus.Completed)
            SetStatus(AgentStatus.Idle);
    }

    // ---- Lifecycle ----

    public async Task StartAsync(string app, string[] commandLine, bool verbatimCommandLine = false,
        IReadOnlyDictionary<string, string>? extraEnv = null, string? cwd = null, bool deElevate = false,
        bool freshEnv = true, CancellationToken ct = default)
    {
        try
        {
            await Task.Run(() =>
            {
                var client = _backend.Client;
                client.Create(_id, Cols, Rows, app, commandLine, cwd, extraEnv,
                    verbatim: verbatimCommandLine, deElevate: deElevate, freshEnv: freshEnv);
                var att = client.Attach(_id);
                _data = att.Data;
                _childPid = att.ChildPid;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Same shape as TerminalSession's de-elevate failure: surface it IN the pane instead of
            // leaving a dead surface (or crashing an unobserved fire-and-forget task).
            var msg = $"\r\n\x1b[31m[agwinterm] pty-host session failed to start:\x1b[0m\r\n  {ex.Message}\r\n";
            lock (_sync) Emulator.Feed(System.Text.Encoding.UTF8.GetBytes(msg));
            OutputReceived?.Invoke();
            ExitCode = 1; HasExited = true;
            return;
        }
        _started = true;
        _ = Task.Factory.StartNew(ReadLoop, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public async Task<int> RunAsync(string app, string[] commandLine, bool verbatimCommandLine = false, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exited += code => tcs.TrySetResult(code);                    // subscribe BEFORE start: no exit race
        await StartAsync(app, commandLine, verbatimCommandLine, ct: ct).ConfigureAwait(false);
        if (HasExited) return ExitCode ?? 0;                         // failed to start / exited during attach
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Adopt a surviving hosted session with this pane's id (after a UI restart or crash):
    /// attach with a repaint, seed the replica from the host's snapshot (scrollback text + modes),
    /// and go live — the child process keeps running untouched. False = nothing to adopt, launch
    /// fresh; an exited leftover is reaped here so the fresh launch starts clean.</summary>
    public bool TryAdopt()
    {
        PtyHostAttachment att;
        try { att = _backend.Client.Attach(_id, repaint: true); }
        catch { return false; }                       // no such session (or host unreachable)
        if (att.HasExited)
        {
            att.Dispose();
            try { _backend.Client.Kill(_id); } catch { }
            return false;
        }
        lock (_sync)
        {
            Emulator.SeedScrollback(att.Scrollback);
            Emulator.Feed(System.Text.Encoding.UTF8.GetBytes(att.Modes));
        }
        _data = att.Data;
        _childPid = att.ChildPid;
        Adopted = true;
        _started = true;
        _ = Task.Factory.StartNew(ReadLoop, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        OutputReceived?.Invoke();
        return true;
    }

    /// <summary>Default-terminal handoff hands us raw HANDLES — they only mean something in this
    /// process. Handoff panes are pinned to the in-process backend at the creation site.</summary>
    public void Attach(SafeFileHandle conOut, SafeFileHandle conIn, SafeFileHandle signal, IntPtr clientProcess, int pid)
        => throw new NotSupportedException("default-terminal handoff sessions run in-process");

    /// <summary>Mirror of TerminalSession's pump, fed by the host's data pipe instead of ConPTY.</summary>
    private void ReadLoop()
    {
        var data = _data!;
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = data.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                lock (_sync) Emulator.Feed(buffer.AsSpan(0, n));
                OutputReceived?.Invoke();
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        OnStreamEnded();
    }

    /// <summary>Data-pipe EOF: the child exited, the session was killed, or the host died — ask the
    /// host which (the exit code travels via <c>list</c>). A locally-initiated dispose is none of
    /// these. An EOF for a session the host says is still alive is a supersede/detach: not an exit.</summary>
    private void OnStreamEnded()
    {
        if (_disposed || HasExited) return;
        int? code = null;
        try
        {
            var info = _backend.Client.List().FirstOrDefault(i => i.Id == _id);
            if (info is not null && !info.HasExited) return;         // detached, still running (2c territory)
            code = info?.ExitCode;
        }
        catch { }                                                    // host gone → exited, code unknown
        ExitCode = code;
        HasExited = true;
        try { Exited?.Invoke(code ?? 0); } catch { }
    }

    // ---- I/O ----

    public void Inject(ReadOnlySpan<byte> bytes)
    {
        lock (_sync) Emulator.Feed(bytes);
        OutputReceived?.Invoke();
    }

    public void MutateLocked(Action<TerminalEmulator> mutate)
    {
        lock (_sync) mutate(Emulator);
        OutputReceived?.Invoke();
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        var data = _data ?? throw new InvalidOperationException("Session not started.");
        data.Write(bytes);
        data.Flush();
    }

    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;
        lock (_sync) Emulator.Resize(cols, rows);
        Cols = cols;
        Rows = rows;
        if (_started && !HasExited)
            try { _backend.Client.Resize(_id, cols, rows); } catch { }   // host gone → EOF path reports it
    }

    public string SnapshotRow(int row)
    {
        lock (_sync) return Emulator.DumpRow(row);
    }

    /// <summary>Stop viewing WITHOUT killing: close the data pipe (the host sees a detach) and
    /// leave the child running for a later <see cref="TryAdopt"/>. The app-quit path.</summary>
    public void Detach()
    {
        _disposed = true;                                            // EOF now means "we left", not "it died"
        try { _data?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_started)
            try { _backend.Client.Kill(_id); } catch { }             // explicit close = kill (pane closed)
        try { _data?.Dispose(); } catch { }
    }
}
