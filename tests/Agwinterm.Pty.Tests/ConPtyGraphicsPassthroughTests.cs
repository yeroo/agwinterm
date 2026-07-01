using System.IO;
using System.Text;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// Diagnostic: determines whether ConPTY forwards a Kitty graphics APC sequence
/// emitted by a child process, or strips it. The outcome decides the docxy
/// integration approach (inline-via-ConPTY vs. control-API injection).
/// </summary>
public class ConPtyGraphicsPassthroughTests
{
    [Fact]
    public async Task ConPty_ForwardsKittyApc_FromChildProcess()
    {
        // Build a minimal Kitty APC: 1x1 RGB image, payload bytes {1,2,3} -> base64 "AQID".
        //   ESC _ G a=T,i=1,f=24,s=1,v=1 ; AQID ESC \
        var apc = new List<byte> { 0x1b };
        apc.AddRange(Encoding.ASCII.GetBytes("_Ga=T,i=1,f=24,s=1,v=1;AQID"));
        apc.Add(0x1b);
        apc.Add((byte)'\\');

        string file = Path.Combine(Path.GetTempPath(), "agw_conpty_apc.bin");
        File.WriteAllBytes(file, apc.ToArray());

        using var session = new TerminalSession(80, 24);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // `cmd /c type <file>` emits the raw bytes (all <= 0x7f, no Ctrl-Z) to stdout.
        await session.RunAsync("cmd.exe", new[] { "/c", "type", file }, verbatimCommandLine: true, cts.Token);

        int images = session.Emulator.Images.Count;

        // FINDING (Windows 11 build 26200): ConPTY STRIPS Kitty graphics APC sequences from
        // child output — nothing reaches our parser. This is why graphics from a child program
        // (e.g. docxy) must be delivered out-of-band via the control API -> TerminalSession.Inject,
        // not the PTY stream. If this assertion ever fails (images>=1), ConPTY gained passthrough
        // and inline-Kitty-through-ConPTY (Path B) has become viable.
        Assert.Equal(0, images);
    }
}
