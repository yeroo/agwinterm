using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>#246: the in-process session's exit watchers settle the pump (one 50 ms window with
/// no new bytes, at most 500 ms) BEFORE HasExited/Exited say the session ended — conhost flushes
/// the last output after the child is gone, so "exited" was reaching handlers ahead of the last
/// line. The assert is on the grid the handler read AT the event.</summary>
public class SettleOutputTests
{
    private static string GridText(ISession s)
    {
        lock (s.SyncRoot) return string.Join("\n", s.Emulator.DumpBuffer());
    }

    [Fact]
    public async Task InProcess_Exited_FiresAfterTheLastOutputSettled()
    {
        using var s = new TerminalSession(80, 24);
        var seen = new TaskCompletionSource<(int Code, string Grid)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Exited += code => seen.TrySetResult((code, GridText(s)));
        await s.StartAsync("cmd.exe", new[] { "/q", "/c", "echo settle-marker-246 & exit 3" }, verbatimCommandLine: true);
        var (exit, grid) = await seen.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(3, exit);
        Assert.Contains("settle-marker-246", grid);
        Assert.True(s.HasExited);
        Assert.Equal(3, s.ExitCode);
    }
}

/// <summary>The barrier itself, without a shell: an attached session whose "client" is a process that
/// is ALREADY gone (so the watcher's WaitForExit returns at once) while the console-output pipe keeps
/// delivering chunks every ~10 ms for ~150 ms — the conhost-flushes-after-exit shape, made
/// deterministic. Only a settle that waits on the byte counter lets the last chunk land before
/// Exited; neuter the counter (or the call) and the event fires after one 50 ms window with the
/// later chunks missing. The tests above cannot tell those apart (revmux r1).</summary>
public class SettleOutputBarrierTests
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr r, out IntPtr w, IntPtr sa, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle h, byte[] b, uint n, out uint written, IntPtr o);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [Fact]
    public async Task Exited_WaitsForChunksStillArrivingAfterTheClientIsGone()
    {
        using var gone = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        gone.WaitForExit();
        IntPtr h = OpenProcess(0x00100000 | 0x1000, false, gone.Id);   // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION; AttachedPty closes it
        Assert.NotEqual(IntPtr.Zero, h);
        Assert.True(CreatePipe(out IntPtr outR, out IntPtr outW, IntPtr.Zero, 0));
        Assert.True(CreatePipe(out IntPtr inR, out IntPtr inW, IntPtr.Zero, 0));
        using var outWrite = new SafeFileHandle(outW, true);
        using var inRead = new SafeFileHandle(inR, true);

        using var s = new TerminalSession(60, 20);
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Exited += _ => { lock (s.SyncRoot) seen.TrySetResult(string.Join("\n", s.Emulator.DumpBuffer())); };
        // A dedicated thread, not a pool task: under the parallel suite a queued task can wait longer
        // than the 50 ms window before its first write, and an empty grid at the event would then be
        // the test's own scheduling, not the barrier's. The first chunk goes out before Attach.
        static void Chunk(SafeFileHandle h, int i) { var b = Encoding.ASCII.GetBytes($"late-{i}\r\n"); WriteFile(h, b, (uint)b.Length, out _, IntPtr.Zero); }
        Chunk(outWrite, 1);
        var writer = new Thread(() => { for (int i = 2; i <= 12; i++) { Thread.Sleep(10); Chunk(outWrite, i); } }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        writer.Start();
        s.Attach(new SafeFileHandle(outR, true), new SafeFileHandle(inW, true), new SafeFileHandle(IntPtr.Zero, false), h, gone.Id);
        string grid = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        writer.Join();
        Assert.Contains("late-12", grid);
    }
}
