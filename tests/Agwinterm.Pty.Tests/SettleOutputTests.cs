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
