using Agwinterm.Core;
using System.Reflection;

namespace Agwinterm.Pty.Tests;

public class SessionInputTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ActualSendBroadcastDoesNotLetActiveProtectionBlockSibling(bool closed, bool command)
    {
        using var active = new InputSession { InputClosed = closed };
        using var live = new InputSession();
        var program = LoadProgram();
        // No Program constructor: no HWND, host, timers, or shared state is created.
        var app = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(program);
        object Make(string nested) => Activator.CreateInstance(program.GetNestedType(nested, BindingFlags.NonPublic)!, true)!;
        static void Set(object o, string name, object value) => o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);
        static System.Collections.IList List(object o, string name) => (System.Collections.IList)o.GetType().GetField(name)!.GetValue(o)!;
        var ws = Make("Workspace"); var ses = Make("Ses");
        var pane = Make("Pane"); var sibling = Make("Pane");
        Set(pane, "S", active); Set(pane, "ReadOnly", !closed); Set(pane, "HasSel", true); Set(pane, "ScrollOffset", 5);
        Set(sibling, "S", live); Set(ses, "Ws", ws);
        List(ses, "Panes").Add(pane); List(ses, "Panes").Add(sibling); List(ws, "Sessions").Add(ses);
        Set(app, "_active", ses); Set(app, "_session", active); Set(app, "_broadcast", true);
        Set(app, "_workspaces", Activator.CreateInstance(program.GetField("_workspaces", BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType)!);
        var method = program.GetMethod(command ? "RunCommandText" : "Send", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = method.Invoke(app, command ? ["payload", "send"] : ["payload", true]);
        if (command) Assert.Contains("do not retry blindly", Assert.IsType<string>(result));
        else Assert.False(Assert.IsType<bool>(result));
        Assert.Equal(0, active.Attempts); Assert.Equal(command ? "payload\r" : "payload", live.Input);
        Assert.True((bool)pane.GetType().GetField("HasSel")!.GetValue(pane)!);
        Assert.Equal(5, pane.GetType().GetField("ScrollOffset")!.GetValue(pane));
    }

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
        var type = LoadProgram().GetNestedType("PaneHost", BindingFlags.NonPublic)!;
        session.Emulator.Host = (IHostActions)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [null, null, session], null)!;
        // Kitty flags query requests a real host reply; the marker follows in the SAME Feed call.
        session.Emulator.Feed("\x1b[?uFINAL-after-query"u8);
        Assert.Contains("FINAL-after-query", session.Emulator.DumpRow(0));
        Assert.Equal(closed ? 0 : 1, session.Attempts);
    }

    private static Type LoadProgram()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "Agwinterm.Win32"))) root = Path.GetDirectoryName(root);
        var path = Directory.GetFiles(Path.Combine(root!, "src", "Agwinterm.Win32", "bin"), "Agwinterm.Win32.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        return Assembly.LoadFrom(path).GetType("Agwinterm.Win32.Program", true)!;
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
