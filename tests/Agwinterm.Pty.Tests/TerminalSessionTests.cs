using System.Linq;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class TerminalSessionTests
{
    [Fact]
    public async Task RealProcess_OutputLandsInGrid()
    {
        using var session = new TerminalSession(80, 25);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        int exit = await session.RunAsync("cmd.exe", new[] { "/c", "echo", "AGWMARKER123" }, verbatimCommandLine: true, cts.Token);

        var screen = string.Join("\n", Enumerable.Range(0, 25).Select(session.SnapshotRow));
        Assert.Contains("AGWMARKER123", screen);
        Assert.Equal(0, exit);
    }
}
