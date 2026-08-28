using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Ctl;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// <c>image.frameshm</c> across a real named pipe, not through <see cref="ControlServer.Dispatch"/>.
/// <see cref="ControlServerFrameShmTests"/> already covers the verb's behaviour; what these add is
/// the transport a producer actually uses — a <see cref="System.IO.MemoryMappedFiles.MemoryMappedFile"/>
/// published through <see cref="ControlServer.Start"/>'s accept loop with the request line built by
/// the ctl's own arg builder. The parts that only exist end to end are here: that the request
/// survives the round trip as JSON <em>numbers</em>, that a connection is reusable across frames,
/// and that a rejection answers on the same connection instead of tearing it down.
///
/// The live counterpart is Task 6 of docs/plans/20260821-image-frameshm-command.md, run against a
/// dev instance; the throughput it measured is recorded in docs/specs/image-frameshm.md.
/// </summary>
public class FrameShmPipeIntegrationTests : IDisposable
{
    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            try { _open[i].Dispose(); } catch (IOException) { /* a closed pipe end is not a failure */ }
        }
        _open.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>A live server on a pipe name unique to the test, plus the session it serves.</summary>
    private (TerminalSession session, string pipeName) StartServer()
    {
        var session = new TerminalSession(80, 24);
        string pipeName = "agwinterm-test-shm-" + Guid.NewGuid().ToString("N");
        var server = new ControlServer(session, pipeName);
        server.Start();
        _open.Add(server);
        return (session, pipeName);
    }

    /// <summary>
    /// A connected control client. Deliberately the same shape as <c>Agwinterm.Ctl</c>'s: one line
    /// out, one line back, UTF-8 without a BOM.
    /// </summary>
    private sealed class Client : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public Client(string pipeName)
        {
            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            _pipe.Connect(5000);
            _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        }

        public string Send(string requestJson)
        {
            _writer.WriteLine(requestJson);
            return _reader.ReadLine() ?? throw new IOException("control pipe closed without a reply");
        }

        public void Dispose() { _writer.Dispose(); _reader.Dispose(); _pipe.Dispose(); }
    }

    private Client Connect(string pipeName)
    {
        var c = new Client(pipeName);
        _open.Add(c);
        return c;
    }

    private ShmTestProducer NewProducer(int slotCount = 2)
    {
        var p = new ShmTestProducer(slotCount, tag: "pipe");
        _open.Add(p);
        return p;
    }

    /// <summary>
    /// Builds the request line the ctl would send for one <c>images[]</c> entry, through
    /// <see cref="FrameShmCli.TryBuildArgs"/> — so the CLI's parsing is inside the integration
    /// path rather than re-implemented next to it.
    /// </summary>
    private static string CtlRequest(ShmTestProducer p, long seq, int slot, params (string Flag, string Value)[] flags)
    {
        var options = new Dictionary<string, string>
        {
            ["slot"] = slot.ToString(),
            ["seq"] = seq.ToString(),
            ["width"] = p.Geometry.Width.ToString(),
            ["height"] = p.Geometry.Height.ToString(),
            ["stride"] = p.Geometry.Stride.ToString(),
            ["format"] = ((int)KittyFormat.Bgra).ToString(),
        };
        foreach (var (flag, value) in flags) options[flag] = value;

        Assert.True(FrameShmCli.TryBuildArgs([p.Name], options, out var cargs, out string? error), error);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["cmd"] = "image.frameshm",
            ["args"] = cargs,
        });
    }

    private static JsonElement ReplyOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCtl(params string[] args)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "agwintermctl.exe");
        Assert.True(File.Exists(exe), $"agwintermctl apphost was not copied to {AppContext.BaseDirectory}");

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start agwintermctl");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(10_000);
        if (!exited) process.Kill(entireProcessTree: true);
        Assert.True(exited, "agwintermctl did not exit within 10 seconds");
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    // ---- the frame arrives ---------------------------------------------------------------------

    [Fact]
    public void APublishedFrameArrivesThroughTheControlPipeWithItsPixelsAndPlacement()
    {
        var (session, pipeName) = StartServer();
        var client = Connect(pipeName);
        var p = NewProducer();

        long seq = p.Publish(0xA7);
        string reply = client.Send(CtlRequest(p, seq, p.Slot, ("id", "7"), ("row", "2"), ("col", "3"),
                                              ("cols", "10"), ("rows", "5")));

        var root = ReplyOf(reply);
        Assert.True(root.GetProperty("ok").GetBoolean(), reply);
        Assert.Equal("frame:1/1", root.GetProperty("result").GetString());

        // The emulator holds the producer's bytes, tightly packed and still BGRA.
        var img = session.Emulator.Images[7];
        Assert.Equal(KittyFormat.Bgra, img.Format);
        Assert.Equal(p.Geometry.Width, img.Width);
        Assert.Equal(p.Geometry.Height, img.Height);
        Assert.Equal(ShmTestProducer.Packed(0xA7, p.Geometry.Width, p.Geometry.Height), img.Data);

        var placement = Assert.Single(session.Emulator.Placements);
        Assert.Equal(7, placement.ImageId);
        Assert.Equal(2, placement.Row);
        Assert.Equal(3, placement.Col);
        Assert.Equal(10, placement.Cols);
        Assert.Equal(5, placement.Rows);
    }

    [Fact]
    public void TheRealCtlCommandRoutesAFrameThroughThePipe()
    {
        var (session, pipeName) = StartServer();
        var p = NewProducer();
        long seq = p.Publish(0xA8);

        var result = RunCtl(
            "image", "frameshm", p.Name,
            "--slot", p.Slot.ToString(), "--seq", seq.ToString(),
            "--width", p.Geometry.Width.ToString(), "--height", p.Geometry.Height.ToString(),
            "--stride", p.Geometry.Stride.ToString(), "--format", ((int)KittyFormat.Bgra).ToString(),
            "--id", "7", "--row", "2", "--col", "3", "--cols", "10", "--rows", "5",
            "--pipe", pipeName);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("frame:1/1", result.StdOut.Trim());
        Assert.Equal("", result.StdErr);
        Assert.Equal(ShmTestProducer.Packed(0xA8, p.Geometry.Width, p.Geometry.Height),
                     session.Emulator.Images[7].Data);
        var placement = Assert.Single(session.Emulator.Placements);
        Assert.Equal((7, 2, 3, 10, 5),
                     (placement.ImageId, placement.Row, placement.Col, placement.Cols, placement.Rows));
    }

    [Fact]
    public void SuccessiveFramesOnOneConnectionEachLandAndTheLastOneWins()
    {
        var (session, pipeName) = StartServer();
        var client = Connect(pipeName);
        var p = NewProducer();

        // A producer that serialises on the reply, which is the obligation the spec states: six
        // frames over two slots, each acknowledged before the next is painted.
        byte last = 0;
        for (int i = 0; i < 6; i++)
        {
            last = (byte)(0x10 + i);
            long seq = p.Publish(last);
            var root = ReplyOf(client.Send(CtlRequest(p, seq, p.Slot)));
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal("frame:1/1", root.GetProperty("result").GetString());
        }

        Assert.Equal(ShmTestProducer.Packed(last, p.Geometry.Width, p.Geometry.Height),
                     session.Emulator.Images[1].Data);
        Assert.Single(session.Emulator.Placements);
    }

    [Fact]
    public void ARepeatedSeqIsServedFromTheCacheOverThePipeToo()
    {
        var (_, pipeName) = StartServer();
        var client = Connect(pipeName);
        var p = NewProducer();

        long seq = p.Publish(0x5C);
        Assert.Equal("frame:1/1", ReplyOf(client.Send(CtlRequest(p, seq, p.Slot))).GetProperty("result").GetString());
        // Same (id, seq): the megabyte copy is skipped and only the placement is redone.
        Assert.Equal("frame:1/0", ReplyOf(client.Send(CtlRequest(p, seq, p.Slot, ("row", "4"))))
                                      .GetProperty("result").GetString());
    }

    // ---- a rejection is an ordinary reply, not a broken connection ------------------------------

    [Fact]
    public void ARejectedFrameAnswersOnTheSameConnectionAndTheNextFrameStillWorks()
    {
        var (session, pipeName) = StartServer();
        var client = Connect(pipeName);
        var p = NewProducer();

        // A slot the header does not have. Rejected by the reader, not by an exception that would
        // take the connection with it.
        long seq = p.Publish(0x0B);
        var bad = ReplyOf(client.Send(CtlRequest(p, seq, slot: 5)));
        Assert.False(bad.GetProperty("ok").GetBoolean());
        Assert.NotEqual("", bad.GetProperty("error").GetString());
        Assert.Empty(session.Emulator.Placements);
        Assert.Empty(session.Emulator.Images);

        // The same client, same connection, immediately afterwards.
        var good = ReplyOf(client.Send(CtlRequest(p, seq, p.Slot)));
        Assert.True(good.GetProperty("ok").GetBoolean());
        Assert.Equal(ShmTestProducer.Packed(0x0B, p.Geometry.Width, p.Geometry.Height),
                     session.Emulator.Images[1].Data);
    }

    [Fact]
    public void AProducerKilledMidFrameIsAnOrdinaryFailureNotACrash()
    {
        var (session, pipeName) = StartServer();
        var client = Connect(pipeName);
        var p = NewProducer();

        long seq = p.Publish(0x77);
        string request = CtlRequest(p, seq, p.Slot);
        Assert.True(ReplyOf(client.Send(request)).GetProperty("ok").GetBoolean());

        // Begin the next frame, then die before the slot is complete or ready is published. The
        // half-written bytes must never replace the accepted frame.
        var interrupted = p.WritePartialUnpublishedFrame(0x88);
        p.Dispose();
        _open.Remove(p);

        // Re-sending the *same* (id, seq) still succeeds — the cache answers before anything is
        // opened, so a dead producer's last frame stays re-placeable. Worth pinning: it is the
        // reason the failing case below has to bump the sequence.
        var cached = ReplyOf(client.Send(request));
        Assert.True(cached.GetProperty("ok").GetBoolean(), request);
        Assert.Equal("frame:1/0", cached.GetProperty("result").GetString());

        // A request that would have named the interrupted paint finds no mapping and is an ordinary
        // error reply on this connection. The accepted pixels stay in the emulator.
        var gone = ReplyOf(client.Send(CtlRequest(p, interrupted.Seq, interrupted.Slot)));
        Assert.False(gone.GetProperty("ok").GetBoolean());
        Assert.Equal(ShmTestProducer.Packed(0x77, p.Geometry.Width, p.Geometry.Height),
                     session.Emulator.Images[1].Data);
        Assert.True(ReplyOf(client.Send("{\"cmd\":\"ping\"}")).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void TheCtlBuiltRequestCarriesNumbersNotStringsOnTheWire()
    {
        var p = NewProducer();
        string request = CtlRequest(p, seq: 1, slot: 1, ("id", "7"), ("cols", "10"));

        // The regression this guards is silent and total: ControlServer's GetInt throws on a
        // string, so a quoted number would reject every frame with "requires an element of type
        // 'Number'". Asserting on the serialized text is the only place that shape is visible.
        Assert.Contains("\"slot\":1", request);
        Assert.Contains("\"seq\":1", request);
        Assert.Contains("\"id\":7", request);
        Assert.Contains("\"cols\":10", request);
        Assert.Contains("\"format\":132", request);
        Assert.DoesNotContain("\"slot\":\"", request);
        Assert.DoesNotContain("\"seq\":\"", request);
    }
}
