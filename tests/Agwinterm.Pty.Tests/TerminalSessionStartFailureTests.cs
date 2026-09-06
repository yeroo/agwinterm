using System.Text;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>An in-process session whose spawn FAILS (the cwd is gone) ends the way a pty-host
/// session's failed start does: the reason painted in the pane, ExitCode 1, HasExited true, and
/// StartAsync returning normally — not a faulted task with a pane that never ends (#227 r2).</summary>
public class TerminalSessionStartFailureTests
{
    [Fact]
    public async Task StartAsync_WithAMissingCwd_EndsAsExit1InsteadOfFaulting()
    {
        string cwd = Path.Combine(Path.GetTempPath(), "agwinterm-gone-" + Guid.NewGuid().ToString("N"));
        using var s = new TerminalSession(100, 24);
        bool exitedRaised = false;
        s.Exited += _ => exitedRaised = true;
        await s.StartAsync("cmd.exe", new[] { "/c", "exit 0" }, verbatimCommandLine: true, cwd: cwd);
        Assert.True(s.HasExited);
        Assert.Equal(1, s.ExitCode);
        Assert.False(exitedRaised);   // the pane keeps its surface to show the failure; watchers use HasExited
        var sb = new StringBuilder();
        for (int r = 0; r < s.Rows; r++) sb.AppendLine(s.SnapshotRow(r));
        Assert.Contains("could not start", sb.ToString());
    }
}
