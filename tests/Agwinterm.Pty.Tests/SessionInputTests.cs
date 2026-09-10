using Agwinterm.Core;
using System.Reflection;

namespace Agwinterm.Pty.Tests;

public class SessionInputTests
{
    [Fact]
    public void BroadcastContinuesPastClosedProtectedAndFailingPanes()
    {
        using var closed = new InputSession { InputClosed = true };
        using var failing = new InputSession { FailWrite = true };
        using var protectedPane = new InputSession(); using var live = new InputSession();
        Assert.False(SessionInput.Broadcast([(closed, false), (failing, false), (protectedPane, true), (live, false)], "payload"u8));
        Assert.Equal(0, closed.Attempts); Assert.Equal(1, failing.Attempts); Assert.Equal(0, protectedPane.Attempts);
        Assert.Equal("payload", live.Input); Assert.Equal(1, live.Attempts);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualPaneHostQueryReplyCannotAbortFollowingOutput(bool closed)
    {
        using var session = new InputSession { InputClosed = closed, FailWrite = true };
        string? root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "Agwinterm.Win32"))) root = Path.GetDirectoryName(root);
        var path = Directory.GetFiles(Path.Combine(root!, "src", "Agwinterm.Win32", "bin"), "Agwinterm.Win32.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        var type = Assembly.LoadFrom(path).GetType("Agwinterm.Win32.Program+PaneHost", true)!;
        session.Emulator.Host = (IHostActions)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [null, null, session], null)!;
        // Kitty flags query requests a real host reply; the marker follows in the SAME Feed call.
        session.Emulator.Feed("\x1b[?uFINAL-after-query"u8);
        Assert.Contains("FINAL-after-query", session.Emulator.DumpRow(0));
        Assert.Equal(closed ? 0 : 1, session.Attempts);
    }

    private sealed class InputSession : ISession
    {
        public bool InputClosed { get; init; }
        public bool FailWrite; public int Attempts; public string Input = "";
        public ITerminalCore Emulator { get; } = new TerminalEmulator(80, 24);
        public int Cols => 80; public int Rows => 24; public int? ChildProcessId => null;
        public object SyncRoot { get; } = new();
        public AgentStatus Status => AgentStatus.Idle; public bool Blink => false; public bool AutoReset => false;
        public long StatusChangedAt => 0; public bool HasExited => false; public int? ExitCode => null;
        public event Action? OutputReceived { add { } remove { } }
        public event Action? StatusChanged { add { } remove { } }
        public event Action<string?>? SoundRequested { add { } remove { } }
        public event Action<int>? Exited { add { } remove { } }
        public void NotifyActivity() { }
        public void SetStatus(AgentStatus status, bool blink = false, bool autoReset = false, bool sound = false, string? soundName = null) { }
        public void Write(ReadOnlySpan<byte> bytes) { Attempts++; if (FailWrite) throw new IOException("closed during write"); Input += System.Text.Encoding.UTF8.GetString(bytes); }
        public Task StartAsync(string app, string[] commandLine, bool verbatimCommandLine = false, IReadOnlyDictionary<string, string>? extraEnv = null, string? cwd = null, bool deElevate = false, bool freshEnv = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RunAsync(string app, string[] commandLine, bool verbatimCommandLine = false, CancellationToken ct = default) => throw new NotSupportedException();
        public void Attach(Microsoft.Win32.SafeHandles.SafeFileHandle conOut, Microsoft.Win32.SafeHandles.SafeFileHandle conIn, Microsoft.Win32.SafeHandles.SafeFileHandle signal, IntPtr clientProcess, int pid) => throw new NotSupportedException();
        public void Inject(ReadOnlySpan<byte> bytes) => Emulator.Feed(bytes);
        public void MutateLocked(Action<ITerminalCore> mutate) => mutate(Emulator);
        public void Resize(int cols, int rows) => Emulator.Resize(cols, rows);
        public string SnapshotRow(int row) => Emulator.DumpRow(row);
        public (int Row, int Col) SnapshotCursor() => (Emulator.CursorRow, Emulator.CursorCol);
        public void Detach() { } public void Dispose() { }
    }
}
