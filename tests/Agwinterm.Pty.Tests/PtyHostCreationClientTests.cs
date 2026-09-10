using Agwinterm.Pty.Proto;
using Google.Protobuf;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class PtyHostCreationClientTests
{
    private const string Ticket = "0123456789abcdef0123456789abcdef";

    // Private in-memory framing only: no pipe, host, console, registry or desktop.
    private sealed class ExchangeStream(Func<Request, byte[]> reply) : Stream
    {
        private readonly MemoryStream _writes = new();
        private MemoryStream _reads = new();
        public readonly List<Request> Requests = new();
        public bool Disposed;
        public override void Flush()
        {
            var frame = _writes.ToArray(); _writes.SetLength(0);
            Assert.Equal(frame.Length - 4, BitConverter.ToInt32(frame));
            var request = Request.Parser.ParseFrom(frame.AsSpan(4)); Requests.Add(request);
            _reads.Dispose(); _reads = new MemoryStream(reply(request));
        }
        public override int Read(byte[] b, int o, int n) => _reads.Read(b, o, n);
        public override int Read(Span<byte> b) => _reads.Read(b);
        public override void Write(byte[] b, int o, int n) => _writes.Write(b, o, n);
        public override void Write(ReadOnlySpan<byte> b) => _writes.Write(b);
        protected override void Dispose(bool disposing) { Disposed = true; _writes.Dispose(); _reads.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
    }

    private static byte[] Frame(Reply reply)
    {
        var bytes = new byte[4 + reply.CalculateSize()];
        BitConverter.TryWriteBytes(bytes, reply.CalculateSize()); reply.WriteTo(bytes.AsSpan(4));
        return bytes;
    }

    [Fact]
    public void LostCreateReplyRetiresConnectionAndCleanupCarriesTheKnownTicket()
    {
        var stream = new ExchangeStream(request => request.CmdCase == Request.CmdOneofCase.PrepareCreate
            ? Frame(new Reply { Ok = true, Creation = new CreationReply { Id = "pane", Ticket = Ticket, Phase = CreationPhase.CreationPrepared } })
            : Array.Empty<byte>()); // the server consumed Create, but its reply never arrived
        using var client = new PtyHostClient(stream, 1);
        Assert.Equal(Ticket, client.PrepareCreate("pane"));
        Assert.Throws<EndOfStreamException>(() => client.Create("pane", 80, 24, "cmd.exe", [], creationTicket: Ticket));
        Assert.False(client.IsUsable); Assert.True(stream.Disposed);
        Assert.Throws<IOException>(() => client.List());
        Assert.Equal(2, stream.Requests.Count); Assert.Equal(Ticket, stream.Requests[1].Create.CreationTicket);
        var recovery = new ExchangeStream(request => Frame(new Reply
        { Ok = true, Creation = new CreationReply { Id = request.CancelCreate.Id, Ticket = request.CancelCreate.Ticket, Phase = CreationPhase.CreationUnknown } }));
        using var fresh = new PtyHostClient(recovery, 1);
        Assert.Equal(CreationPhase.CreationUnknown, fresh.CancelCreate("pane", Ticket));
        Assert.Equal(Request.CmdOneofCase.CancelCreate, Assert.Single(recovery.Requests).CmdCase);
    }

    [Fact]
    public void LostPreparationReplySendsNoCreate()
    {
        var stream = new ExchangeStream(_ => []); using var client = new PtyHostClient(stream, 1);
        Assert.Throws<EndOfStreamException>(() => client.PrepareCreate("pane"));
        Assert.Equal(Request.CmdOneofCase.PrepareCreate, Assert.Single(stream.Requests).CmdCase);
        Assert.False(client.IsUsable);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void PartialOrMalformedRepliesCannotBeConsumedByTheNextRequest(int kind)
    {
        var stream = new ExchangeStream(_ => kind switch
        { 0 => new byte[] { 5, 0 }, 1 => new byte[] { 5, 0, 0, 0, 1 }, _ => new byte[] { 1, 0, 0, 0, 255 } });
        using var client = new PtyHostClient(stream);
        Assert.ThrowsAny<Exception>(() => client.List()); Assert.False(client.IsUsable);
        Assert.Throws<IOException>(() => client.List()); Assert.Single(stream.Requests);
    }

    [Fact]
    public void ACompleteRefusalDoesNotDamageFraming()
    {
        int calls = 0;
        var stream = new ExchangeStream(_ => Frame(++calls == 1 ? new Reply { Error = "refused" }
            : new Reply { Ok = true, List = new ListReply() }));
        using var client = new PtyHostClient(stream);
        Assert.Throws<InvalidOperationException>(() => client.Kill("pane")); Assert.True(client.IsUsable);
        Assert.Empty(client.List()); Assert.Equal(2, calls);
    }

    [Fact]
    public void LegacyCapabilityRefusesTicketOperationsBeforeWriting()
    {
        var stream = new ExchangeStream(_ => throw new Exception("must not reach wire"));
        using var client = new PtyHostClient(stream);
        Assert.Throws<NotSupportedException>(() => client.PrepareCreate("pane"));
        Assert.Throws<NotSupportedException>(() => client.CancelCreate("pane", Ticket));
        Assert.Throws<NotSupportedException>(() => client.QueryCreate("pane", Ticket));
        Assert.Empty(stream.Requests); Assert.True(client.IsUsable);
    }

    [Fact]
    public void LifecycleRequestsCarryTheirExpectedIncarnation()
    {
        var stream = new ExchangeStream(_ => Frame(new Reply { Ok = true }));
        using var client = new PtyHostClient(stream, 1);
        client.Resize("pane", 80, 24, Ticket); client.Detach("pane", Ticket); client.Kill("pane", Ticket);
        Assert.Equal(Ticket, stream.Requests[0].Resize.CreationTicket);
        Assert.Equal(Ticket, stream.Requests[1].Detach.CreationTicket);
        Assert.Equal(Ticket, stream.Requests[2].Kill.CreationTicket);
    }

    [Fact]
    public void PendingCancellationIsNotReportedAsCompletedCleanup()
    {
        var stream = new ExchangeStream(_ => Frame(new Reply { Ok = true, Creation = new CreationReply
            { Id = "pane", Ticket = Ticket, Phase = CreationPhase.CreationCancelling } }));
        using var client = new PtyHostClient(stream, 1);
        Assert.Equal(CreationPhase.CreationCancelling, client.CancelCreate("pane", Ticket));
    }

    [Theory]
    [InlineData("body")] [InlineData("id")] [InlineData("ticket")]
    [InlineData("prepare")] [InlineData("phase")] [InlineData("attach")]
    public void MalformedSuccessfulReceiptRetiresBeforeAnotherRequest(string kind)
    {
        var stream = new ExchangeStream(_ => Frame(kind switch
        {
            "body" => new Reply { Ok = true },
            "id" => new Reply { Ok = true, Create = new CreateReply { Id = "reused", CreationTicket = Ticket } },
            "ticket" => new Reply { Ok = true, Create = new CreateReply { Id = "pane", CreationTicket = new string('a', 32) } },
            "prepare" => new Reply { Ok = true, Creation = new CreationReply { Id = "pane", Ticket = "bad", Phase = CreationPhase.CreationPrepared } },
            "phase" => new Reply { Ok = true, Creation = new CreationReply { Id = "pane", Ticket = Ticket, Phase = (CreationPhase)99 } },
            _ => new Reply { Ok = true, Attach = new AttachReply { CreationTicket = new string('a', 32) } },
        }));
        using var client = new PtyHostClient(stream, 1);
        Assert.Throws<IOException>(() =>
        {
            if (kind == "prepare") client.PrepareCreate("pane");
            else if (kind == "phase") client.QueryCreate("pane", Ticket);
            else if (kind == "attach") client.Attach("pane", creationTicket: Ticket);
            else client.Create("pane", 80, 24, "cmd.exe", [], creationTicket: Ticket);
        });
        Assert.False(client.IsUsable); Assert.True(stream.Disposed);
        Assert.Throws<IOException>(() => client.List()); Assert.Single(stream.Requests);
    }
}
