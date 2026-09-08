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
    /// the in-process session raises it once, with a best-effort code (the kill and the handle close
    /// are adjacent, so the watcher usually reads 0, not the kill's 1), the server session does not
    /// (it left; it did not see the child die) — every listener guards on tree membership, and the
    /// overlay path waits on the pane's own completion source, which a close completes as "closed"
    /// on both.</summary>
    event Action<int>? Exited;

    // ---- I/O ----
    /// <summary>Feed bytes into the emulator as terminal OUTPUT (display injection). The payload's
    /// own bytes are not typed into the child — this is not a way to deliver bytes to a program —
    /// but the emulator runs them through the same parser as the child's output (the pump's output
    /// dump, raw-output forward and exit settle count see the child's bytes alone; the parser's
    /// callbacks, the VT log included, run for the payload too), so the rule is: a payload can do
    /// whatever the child's output can. That comes in four kinds, illustrated here, not enumerated.
    /// (1) A terminal query (<c>CSI ? u</c>, <c>DECRQM</c>, <c>OSC 11 ?</c> …) is answered on the
    /// child's input, and answering counts as pane activity — a Blocked/Completed status clears.
    /// (2) A mode it sets changes what the pane sends or paints later, until something clears it
    /// again: focus, mouse, bracketed-paste and key-encoding modes change the child's input;
    /// synchronized output (<c>?2026</c>) holds repaints. (3) The screen state the pane reads back
    /// moves. For the selection pin of this contract that is, exhaustively, the alt-screen flag
    /// (any alt-screen mode: <c>?47</c>, <c>?1047</c>, <c>?1049</c>) and the scroll generation and
    /// history count (any output that scrolls — plain text included); otherwise, for example, the
    /// title (<c>OSC 0</c>/<c>2</c>), the reported cwd (<c>OSC 7</c>, <c>OSC 9;9</c>: shown in the
    /// title bar, used by a scratch pane, an overlay, a duplicate and
    /// <c>new-session-directory = current</c>; a split keeps its launch directory), the per-pane
    /// background (<c>OSC 11</c>/<c>111</c>) and the shell marks <c>session output</c> reads
    /// (<c>OSC 133</c>). (4) Every host action (<c>IHostActions</c>) fires as if the child had
    /// asked: a clipboard-write request (<c>OSC 52</c>, subject to host policy), the bell, a
    /// notification (<c>OSC 9</c>, <c>OSC 777</c> — its control-API event always, badge and sound
    /// per focus and config), taskbar progress (<c>OSC 9;4</c>), a VT-log line for an unhandled
    /// sequence or a denied clipboard write. Read-only does not gate any of it. This is the one
    /// statement of that; the other docs point here.</summary>
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
