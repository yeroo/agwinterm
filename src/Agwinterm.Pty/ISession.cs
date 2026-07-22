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
    TerminalEmulator Emulator { get; }
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
    /// <summary>Raised (background thread) when the child process exits, with its exit code.</summary>
    event Action<int>? Exited;

    // ---- I/O ----
    /// <summary>Feed bytes into the EMULATOR only (display injection; never reaches the shell).</summary>
    void Inject(ReadOnlySpan<byte> bytes);
    /// <summary>Run a mutation against the emulator under <see cref="SyncRoot"/>.</summary>
    void MutateLocked(Action<TerminalEmulator> mutate);
    /// <summary>Send bytes to the shell's stdin (real keystrokes).</summary>
    void Write(ReadOnlySpan<byte> bytes);
    void Resize(int cols, int rows);
    /// <summary>Thread-safe text snapshot of one visible row.</summary>
    string SnapshotRow(int row);

    /// <summary>Release the UI's hold WITHOUT necessarily killing (#105, Phase 2c): a server-backed
    /// session detaches — the child keeps running in the pty-host for a later adoption. An
    /// in-process session cannot outlive its process, so there this equals <see cref="IDisposable.Dispose"/>.
    /// App-quit paths call this; explicit pane close still calls Dispose.</summary>
    void Detach();
}
