using Agwinterm.Core;
using Porta.Pty;

namespace Agwinterm.Pty;

/// <summary>
/// Wires a ConPTY-backed child process to a headless <see cref="TerminalEmulator"/>:
/// spawns the process, pumps its output into the emulator on a reader loop, and
/// exposes a thread-safe view of the resulting grid.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private readonly object _sync = new();
    private IPtyConnection? _connection;

    public TerminalEmulator Emulator { get; }
    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// <summary>PID of the spawned shell process (null before start), for foreground-command capture.</summary>
    public int? ChildProcessId => _connection?.Pid;

    /// <summary>Raised (on a background thread) after each chunk of output is fed to the emulator.</summary>
    public event Action? OutputReceived;

    /// <summary>Push-based agent status (set via the control API), and a change notification.</summary>
    public AgentStatus Status { get; private set; } = AgentStatus.Idle;
    public event Action? StatusChanged;

    /// <summary>Pulse the status dot (and title-bar bell) while this status is live (agterm's blinking attention).</summary>
    public bool Blink { get; private set; }

    /// <summary>Clear the status back to idle when the session is next selected (agterm's auto-reset-on-select).</summary>
    public bool AutoReset { get; private set; }

    /// <summary>Raised (background thread) when a status change requests an audible cue. The argument is the
    /// requested sound spec (a system-sound name or a .wav path); null means "use the default alert sound".</summary>
    public event Action<string?>? SoundRequested;

    /// <summary>Set the agent status, optionally with a blink pulse, auto-reset-on-select, and an audible cue.</summary>
    public void SetStatus(AgentStatus status, bool blink = false, bool autoReset = false,
        bool sound = false, string? soundName = null)
    {
        // Blink / auto-reset only make sense for a live (non-idle) status.
        bool newBlink = blink && status != AgentStatus.Idle;
        bool newAuto = autoReset && status != AgentStatus.Idle;
        bool changed = Status != status || Blink != newBlink || AutoReset != newAuto;
        Status = status;
        Blink = newBlink;
        AutoReset = newAuto;
        if (sound && status != AgentStatus.Idle)
            try { SoundRequested?.Invoke(soundName); } catch { /* playback is best-effort */ }
        if (changed) StatusChanged?.Invoke();
    }

    /// <summary>User activity (typing/focus) clears a blocked/completed status, matching agterm.</summary>
    public void NotifyActivity()
    {
        if (Status is AgentStatus.Blocked or AgentStatus.Completed)
            SetStatus(AgentStatus.Idle);
    }

    /// <summary>Lock held while the emulator is mutated; renderers should lock this while reading the grid.</summary>
    public object SyncRoot => _sync;

    public TerminalSession(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
        Emulator = new TerminalEmulator(cols, rows);
    }

    /// <summary>Spawn <paramref name="app"/> and pump its output until it exits. Returns the exit code.</summary>
    /// <remarks>
    /// ConPTY's output pipe is NOT closed when the child exits (conhost keeps it open), so a
    /// read-to-EOF loop would block forever. Instead we detect process exit explicitly via
    /// <see cref="IPtyConnection.WaitForExit"/>, allow a short drain for buffered output, then
    /// cancel the reader.
    /// </remarks>
    public async Task<int> RunAsync(string app, string[] commandLine, bool verbatimCommandLine = false, CancellationToken ct = default)
    {
        var options = new PtyOptions
        {
            Name = "agwinterm",
            Cols = Cols,
            Rows = Rows,
            Cwd = Environment.CurrentDirectory,
            App = app,
            CommandLine = commandLine,
            VerbatimCommandLine = verbatimCommandLine,
        };

        _connection = await PtyProvider.SpawnAsync(options, ct).ConfigureAwait(false);

        using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task readerTask = PumpAsync(_connection.ReaderStream, readerCts.Token);

        // Wait for the child to exit on a background thread (WaitForExit is blocking).
        await Task.Run(() => _connection.WaitForExit(Timeout.Infinite), ct).ConfigureAwait(false);

        // Let the reader drain output already buffered in the pipe, then stop it.
        await Task.Delay(250, ct).ConfigureAwait(false);
        await readerCts.CancelAsync().ConfigureAwait(false);
        try { await readerTask.ConfigureAwait(false); } catch { /* reader cancelled */ }

        return _connection.ExitCode;
    }

    private async Task PumpAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) break;
                lock (_sync) Emulator.Feed(buffer.AsSpan(0, n));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    /// <summary>Spawn an interactive shell and pump its output in the background until it exits or is disposed.</summary>
    public async Task StartAsync(string app, string[] commandLine, bool verbatimCommandLine = false,
        IReadOnlyDictionary<string, string>? extraEnv = null, string? cwd = null, CancellationToken ct = default)
    {
        var options = new PtyOptions
        {
            Name = "agwinterm",
            Cols = Cols,
            Rows = Rows,
            Cwd = string.IsNullOrEmpty(cwd) ? Environment.CurrentDirectory : cwd,
            App = app,
            CommandLine = commandLine,
            VerbatimCommandLine = verbatimCommandLine,
        };

        if (extraEnv is not null)
        {
            // Copy the parent environment (so PATH etc. survive) then layer our additions.
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                env[(string)e.Key] = e.Value as string ?? "";
            foreach (var kv in extraEnv) env[kv.Key] = kv.Value;
            options.Environment = env;
        }

        _connection = await PtyProvider.SpawnAsync(options, ct).ConfigureAwait(false);
        var conn = _connection;
        _ = Task.Run(() => InteractivePumpAsync(conn.ReaderStream), ct);
        // Watch for the child exiting so overlays (and anyone else) get the real ConPTY exit code,
        // reliably even for programs that finish faster than a PID poll could catch them.
        _ = Task.Run(() =>
        {
            try { conn.WaitForExit(Timeout.Infinite); } catch { }
            int code = 0; try { code = conn.ExitCode; } catch { }
            ExitCode = code; HasExited = true;
            try { Exited?.Invoke(code); } catch { }
        }, ct);
    }

    /// <summary>The child's exit code once <see cref="HasExited"/> is true (null while still running).</summary>
    public int? ExitCode { get; private set; }
    public bool HasExited { get; private set; }
    /// <summary>Raised (on a background thread) when the child process exits, with its exit code.</summary>
    public event Action<int>? Exited;

    private static readonly string? DumpPath =
        Environment.GetEnvironmentVariable("AGWINTERM_DUMP") is { Length: > 0 } d ? d : null;

    private async Task InteractivePumpAsync(Stream stream)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (n <= 0) break;
                if (DumpPath is not null)
                    lock (_sync) System.IO.File.AppendAllBytes(DumpPath, buffer.AsSpan(0, n).ToArray());
                lock (_sync) Emulator.Feed(buffer.AsSpan(0, n));
                OutputReceived?.Invoke();
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Feed bytes directly into the emulator (NOT through the PTY). Used by the control
    /// API to inject content, and to deliver graphics sequences that ConPTY would strip
    /// from a child process's output.
    /// </summary>
    public void Inject(ReadOnlySpan<byte> bytes)
    {
        lock (_sync) Emulator.Feed(bytes);
        OutputReceived?.Invoke();
    }

    /// <summary>
    /// Mutate the emulator directly under the render lock, then signal a repaint. Used to place
    /// images without pushing a large base64 payload through the parser under the lock.
    /// </summary>
    public void MutateLocked(Action<TerminalEmulator> mutate)
    {
        lock (_sync) mutate(Emulator);
        OutputReceived?.Invoke();
    }

    /// <summary>Send bytes (e.g. keystrokes) to the child's input.</summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        var stream = _connection?.WriterStream ?? throw new InvalidOperationException("Session not started.");
        stream.Write(bytes);
        stream.Flush();
    }

    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;
        lock (_sync) Emulator.Resize(cols, rows);
        Cols = cols;
        Rows = rows;
        _connection?.Resize(cols, rows);
    }

    /// <summary>Thread-safe snapshot of a single grid row's text.</summary>
    public string SnapshotRow(int row)
    {
        lock (_sync) return Emulator.DumpRow(row);
    }

    public void Dispose()
    {
        try { _connection?.Kill(); } catch { /* already exited */ }
    }
}
