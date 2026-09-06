using Agwinterm.Core;
using Microsoft.Win32.SafeHandles;

namespace Agwinterm.Pty;

/// <summary>
/// The session handle the UI consumes — everything a pane needs from a live terminal session,
/// independent of WHERE that session runs. Today the only implementation is
/// <see cref="TerminalSession"/> (in-process ConPTY); the pty-host server backend (#105) will add
/// a client-side implementation whose <see cref="Emulator"/> is a replica fed from the host
/// process. This seam is the Phase-1 groundwork: code against it, never against the concrete type.
///
/// Threading contract (matches <see cref="TerminalSession"/> today): events are raised on
/// background threads; renderers must hold <see cref="SyncRoot"/> while reading the emulator grid;
/// <see cref="MutateLocked"/>/<see cref="Inject"/> take that lock internally.
/// </summary>
public interface ISession : IDisposable
{
    /// <summary>The screen model this session renders from. In-process: the live emulator; a
    /// server backend supplies a client-side replica with the same contract.</summary>
    ITerminalCore Emulator { get; }
    int Cols { get; }
    int Rows { get; }

    /// <summary>PID of the spawned shell process (null before start), for foreground-command capture.</summary>
    int? ChildProcessId { get; }

    /// <summary>Lock held while the emulator is mutated; renderers lock this while reading the grid.</summary>
    object SyncRoot { get; }

    /// <summary>Raised (background thread) after each chunk of output is fed to the emulator.</summary>
    event Action? OutputReceived;

    // ---- Agent status (push-based, via the control API) ----
    AgentStatus Status { get; }
    bool Blink { get; }
    bool AutoReset { get; }
    event Action? StatusChanged;
    event Action<string?>? SoundRequested;
    /// <summary>Unix epoch SECONDS at which this session's status was last WRITTEN (not merely
    /// changed) — the liveness clock behind the tree's <c>statusChangedAt</c>. Initialised at
    /// construction, so a session whose status was never written reports its own age rather than 0.</summary>
    long StatusChangedAt { get; }
    void SetStatus(AgentStatus status, bool blink = false, bool autoReset = false,
        bool sound = false, string? soundName = null);
    void NotifyActivity();

    // ---- Lifecycle ----
    /// <summary>Spawn <paramref name="app"/> and pump until it exits; returns the exit code.</summary>
    Task<int> RunAsync(string app, string[] commandLine, bool verbatimCommandLine = false, CancellationToken ct = default);
    /// <summary>Spawn an interactive shell and pump in the background until exit or dispose.
    /// <paramref name="freshEnv"/> (default): the child's base environment is rebuilt from the
    /// registry at spawn (new installs visible without an app restart — see
    /// <see cref="FreshEnvironment"/>); false = inherit the spawning process's env snapshot.</summary>
    Task StartAsync(string app, string[] commandLine, bool verbatimCommandLine = false,
        IReadOnlyDictionary<string, string>? extraEnv = null, string? cwd = null, bool deElevate = false,
        bool freshEnv = true, CancellationToken ct = default);
    /// <summary>Adopt an externally-created pseudoconsole (default-terminal handoff). Inherently
    /// handle-based: a server backend must duplicate the handles into the host process (Phase 2).</summary>
    void Attach(SafeFileHandle conOut, SafeFileHandle conIn, SafeFileHandle signal, IntPtr clientProcess, int pid);
    int? ExitCode { get; }
    bool HasExited { get; }
    /// <summary>Raised (background thread) when the child process exits, with its exit code — after
    /// the output it wrote last has settled into the emulator, so a handler that reads the grid reads
    /// a complete one (#246; see <see cref="TerminalSession"/>'s settle window). Not raised for a
    /// start that failed (the pane keeps its surface to show the reason; watchers use
    /// <see cref="HasExited"/>). After a LOCAL <see cref="IDisposable.Dispose"/> the backends differ:
    /// the in-process session raises it once with the kill's exit code, the server session does not
    /// (it left; it did not see the child die) — every listener guards on tree membership, and the
    /// overlay path waits on the pane's own completion source, which a close completes as "closed"
    /// on both.</summary>
    event Action<int>? Exited;

    // ---- I/O ----
    /// <summary>Feed bytes into the EMULATOR only (display injection; never reaches the shell).</summary>
    void Inject(ReadOnlySpan<byte> bytes);
    /// <summary>Run a mutation against the emulator under <see cref="SyncRoot"/>.</summary>
    void MutateLocked(Action<ITerminalCore> mutate);
    /// <summary>Send bytes to the shell's stdin (real keystrokes).</summary>
    void Write(ReadOnlySpan<byte> bytes);
    void Resize(int cols, int rows);
    /// <summary>Thread-safe text snapshot of one visible row.</summary>
    string SnapshotRow(int row);
    /// <summary>Thread-safe snapshot of the caret position (0-based row/col), taken under
    /// <see cref="SyncRoot"/> exactly like <see cref="SnapshotRow"/>. The column is the emulator's,
    /// so after a print into the last column it EQUALS the width — the wrap is deferred to the next
    /// print, and both cores keep it that way on purpose — which means it is not always a valid index
    /// into a row. A snapshot, not a live view: the pair is consistent with itself, and stale the
    /// moment the lock is released. A server-backed session answers from its replica emulator, so
    /// this never round-trips.</summary>
    (int Row, int Col) SnapshotCursor();

    /// <summary>Release the UI's hold WITHOUT necessarily killing (#105, Phase 2c): a server-backed
    /// session detaches — the child keeps running in the pty-host for a later adoption. An
    /// in-process session cannot outlive its process, so there this equals <see cref="IDisposable.Dispose"/>.
    /// App-quit paths call this; explicit pane close still calls Dispose.</summary>
    void Detach();
}
