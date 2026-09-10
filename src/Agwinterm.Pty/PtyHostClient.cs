using System.IO.Pipes;
using Agwinterm.Pty.Proto;
using Google.Protobuf;
using RequestCommand = Agwinterm.Pty.Proto.Request.CmdOneofCase;

namespace Agwinterm.Pty;

/// <summary>Result of <see cref="PtyHostClient.Attach"/>: the connected data stream plus everything
/// a fresh view needs to reconstruct the session (see PtyHostServer for the reattach model).</summary>
public sealed record PtyHostAttachment(
    Stream Data, int Cols, int Rows, int? ChildPid, bool HasExited, int? ExitCode,
    string Modes, IReadOnlyList<string> Scrollback, byte[]? ScrollbackBlob = null, string CreationTicket = "") : IDisposable
{
    public void Dispose() => Data.Dispose();
}

/// <summary>One hosted session as reported by <c>list</c>.</summary>
public sealed record PtyHostSessionInfo(
    string Id, int Cols, int Rows, int? ChildPid, bool HasExited, int? ExitCode, string Title, bool Attached, string CreationTicket = "");

/// <summary>
/// Client for the pty-host control pipe, protocol v2 (protobuf frames; schema =
/// proto/ptyhost.proto). Thread-safe: control requests are serialized over one pipe connection.
/// Drives BOTH hosts (C# and Rust) identically — that identity is the compatibility oracle.
/// </summary>
public sealed class PtyHostClient : IDisposable
{
    private readonly Stream _pipe;
    private readonly object _io = new();
    private bool _unusable;
    public bool IsUsable => !Volatile.Read(ref _unusable);
    public uint CreationRevision { get; private set; }

    internal PtyHostClient(Stream pipe, uint creationRevision = 0) { _pipe = pipe; CreationRevision = creationRevision; }

    /// <summary>Whether a pty-host is answering for this app id (cheap probe, no handshake).</summary>
    public static bool IsRunning(string appId)
    {
        try
        {
            using var probe = new NamedPipeClientStream(".", PtyHostServer.ControlPipeName(appId), PipeDirection.InOut);
            probe.Connect(200);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Connect and handshake. Throws on timeout or protocol mismatch — a version mismatch
    /// must fail loudly at the seam, never surface as garbled sessions later.</summary>
    public static PtyHostClient Connect(string appId, int timeoutMs = 3000)
    {
        // Deliberately a SYNCHRONOUS pipe handle (no PipeOptions.Asynchronous): every control
        // request is sync request/response under a lock, and a sync operation on an async handle
        // secretly runs overlapped IO — closing the pipe with such an op in flight is the crash
        // class behind issue #118. A sync handle keeps this path on plain blocking syscalls.
        var pipe = new NamedPipeClientStream(".", PtyHostServer.ControlPipeName(appId), PipeDirection.InOut);
        var client = new PtyHostClient(pipe);
        try
        {
            pipe.Connect(timeoutMs);
            var reply = client.Request(new Request { Hello = new Hello { Protocol = PtyHostServer.ProtocolVersion } });
            if (reply.Hello.Protocol != PtyHostServer.ProtocolVersion)
                throw new InvalidOperationException("pty-host protocol mismatch");
            client.CreationRevision = reply.Hello.CreationRevision;
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>Create a session on the host (not attached yet — call <see cref="Attach"/>).</summary>
    public string Create(string id, int cols, int rows, string app, string[] args,
        string? cwd = null, IReadOnlyDictionary<string, string>? env = null, bool verbatim = false, bool deElevate = false,
        bool freshEnv = true, string creationTicket = "")
    {
        if (cols < 0 || rows < 0 || cols > 10000 || rows > 10000)
            throw new ArgumentOutOfRangeException(nameof(cols), "create cols/rows must be in 0..10000 (zero selects defaults)");
        var create = new Create
        {
            Id = id,
            Cols = (uint)cols,
            Rows = (uint)rows,
            App = app,
            Cwd = cwd ?? "",
            Verbatim = verbatim,
            DeElevate = deElevate,
            FreshEnvOff = !freshEnv,
            CreationTicket = creationTicket,
        };
        create.Args.AddRange(args);
        if (env is not null) foreach (var kv in env) create.Env[kv.Key] = kv.Value;
        return Request(new Request { Create = create }).Create.Id;
    }

    public string PrepareCreate(string id)
    {
        if (CreationRevision < 1) throw new NotSupportedException("Host does not support exact create reconciliation");
        var reply = Request(new Request { PrepareCreate = new PrepareCreate { Id = id } }).Creation;
        if (reply is null || reply.Id != id || reply.Phase != CreationPhase.CreationPrepared ||
            reply.Ticket.Length != 32 || reply.Ticket.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new IOException("pty-host returned an invalid creation ticket");
        return reply.Ticket;
    }

    public CreationPhase QueryCreate(string id, string ticket) => CreationState(id, ticket, false);
    public CreationPhase CancelCreate(string id, string ticket) => CreationState(id, ticket, true);
    private CreationPhase CreationState(string id, string ticket, bool cancel)
    {
        if (CreationRevision < 1) throw new NotSupportedException("Host does not support exact create reconciliation");
        var reference = new CreationRef { Id = id, Ticket = ticket };
        var reply = Request(cancel ? new Request { CancelCreate = reference } : new Request { QueryCreate = reference }).Creation;
        if (reply is null || reply.Id != id || reply.Ticket != ticket || !Enum.IsDefined(reply.Phase))
            throw new IOException("pty-host returned a mismatched creation receipt");
        return reply.Phase;
    }

    /// <summary>Attach to a session: returns its state snapshot plus the connected data stream
    /// (write = child stdin, read = raw ConPTY output; EOF = child exited or superseded).
    /// <paramref name="repaint"/> asks the host for the ConPTY resize-jiggle — pass true when
    /// reattaching an existing view, false right after <see cref="Create"/>.</summary>
    public PtyHostAttachment Attach(string id, bool repaint = false, int timeoutMs = 5000, string creationTicket = "")
    {
        var reply = Request(new Request { Attach = new Attach { Id = id, Repaint = repaint, CreationTicket = creationTicket } }).Attach;
        if (creationTicket.Length > 0 && reply.CreationTicket != creationTicket) throw new IOException("pty-host attachment incarnation changed");
        // Async handle: the reader (ServerSession.ReadLoop, or a test) must be able to bail out
        // mid-read via cancellation — a SYNC handle can't be unblocked by closing our own end
        // (SafeHandle ref-counting keeps the OS handle open under a blocked read). The #118 rule
        // still applies: cancel the pending read FIRST, and only the reader disposes the pipe.
        var data = new NamedPipeClientStream(".", reply.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { data.Connect(timeoutMs); } catch { data.Dispose(); throw; }
        return new PtyHostAttachment(
            data, (int)reply.Cols, (int)reply.Rows,
            reply.ChildPid != 0 ? (int)reply.ChildPid : null,
            reply.HasExited,
            reply.HasExited ? reply.ExitCode : null,
            reply.Modes, reply.Scrollback,
            reply.ScrollbackBlob.IsEmpty ? null : reply.ScrollbackBlob.ToByteArray(), reply.CreationTicket);
    }

    public void Resize(string id, int cols, int rows, string creationTicket = "")
    {
        if (!PtyResizeTransaction.Valid(unchecked((uint)cols), unchecked((uint)rows)))
            throw new ArgumentOutOfRangeException(nameof(cols), "resize cols/rows must be in 1..10000");
        Request(new Request { Resize = new Resize { Id = id, Cols = (uint)cols, Rows = (uint)rows, CreationTicket = creationTicket } });
    }

    public void Detach(string id, string creationTicket = "")
        => Request(new Request { Detach = new SessionRef { Id = id, CreationTicket = creationTicket } });

    public void Kill(string id, string creationTicket = "")
        => Request(new Request { Kill = new SessionRef { Id = id, CreationTicket = creationTicket } });

    public IReadOnlyList<PtyHostSessionInfo> List()
    {
        var reply = Request(new Request { List = new List() }).List;
        var result = new List<PtyHostSessionInfo>(reply.Sessions.Count);
        foreach (var s in reply.Sessions)
            result.Add(new PtyHostSessionInfo(
                s.Id, (int)s.Cols, (int)s.Rows,
                s.ChildPid != 0 ? (int)s.ChildPid : null,
                s.HasExited,
                s.HasExited ? s.ExitCode : null,
                s.Title, s.Attached, s.CreationTicket));
        return result;
    }

    /// <summary>Ask the host process to tear down every session and exit.</summary>
    public void Shutdown()
    {
        try { Request(new Request { Shutdown = new Shutdown() }); }
        catch (IOException) { /* host died mid-reply (incl. EOF) — that IS the outcome */ }
    }

    /// <summary>One framed request/response over the control pipe. Throws on transport errors and
    /// on <c>ok:false</c> replies (the error message travels in the exception).</summary>
    private Reply Request(Request req)
    {
        Reply reply;
        lock (_io)
        {
            if (_unusable) throw new IOException("pty-host connection was retired; requests must not be replayed");
            byte[] payload = req.ToByteArray();
            try
            {
                Span<byte> len = stackalloc byte[4];
                BitConverter.TryWriteBytes(len, payload.Length);
                _pipe.Write(len);
                _pipe.Write(payload);
                _pipe.Flush();

                var lenBuf = new byte[4];
                _pipe.ReadExactly(lenBuf);
                int n = BitConverter.ToInt32(lenBuf);
                if (n < 0 || n > 16 * 1024 * 1024) throw new IOException("pty-host sent a garbled frame");
                var buf = new byte[n];
                _pipe.ReadExactly(buf);
                reply = Reply.Parser.ParseFrom(buf);
                if (reply.Ok) ValidateReply(req, reply);
            }
            catch
            {
                _unusable = true;
                try { _pipe.Dispose(); } catch { }
                throw;
            }
        }
        if (reply.Ok) return reply;
        throw new InvalidOperationException("pty-host: " + (reply.Error.Length > 0 ? reply.Error : "unknown error"));
    }

    public void Dispose()
    {
        lock (_io) { _unusable = true; _pipe.Dispose(); }
    }

    // Validate while owning _io, so a semantically invalid success retires the connection before
    // another caller can issue work on it. A complete, explicit refusal remains reusable.
    private static void ValidateReply(Request req, Reply reply)
    {
        var expected = req.CmdCase switch
        {
            RequestCommand.Hello => Reply.BodyOneofCase.Hello,
            RequestCommand.Create => Reply.BodyOneofCase.Create,
            RequestCommand.Attach => Reply.BodyOneofCase.Attach,
            RequestCommand.List => Reply.BodyOneofCase.List,
            RequestCommand.PrepareCreate or RequestCommand.QueryCreate or RequestCommand.CancelCreate
                => Reply.BodyOneofCase.Creation,
            _ => Reply.BodyOneofCase.None,
        };
        if (reply.BodyCase != expected) throw new IOException("pty-host returned an unexpected reply body");
        if (req.Create is { } create && (create.Id.Length > 0 && reply.Create.Id != create.Id ||
            create.CreationTicket.Length > 0 && reply.Create.CreationTicket != create.CreationTicket))
            throw new IOException("pty-host returned a mismatched create receipt");
        if (req.Attach is { } attach && attach.CreationTicket.Length > 0 && reply.Attach.CreationTicket != attach.CreationTicket)
            throw new IOException("pty-host attachment incarnation changed");
        if (req.PrepareCreate is { } prepare && (reply.Creation.Id != prepare.Id ||
            reply.Creation.Phase != CreationPhase.CreationPrepared || reply.Creation.Ticket.Length != 32 ||
            reply.Creation.Ticket.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new IOException("pty-host returned an invalid creation ticket");
        var reference = req.QueryCreate ?? req.CancelCreate;
        if (reference is not null && (reply.Creation.Id != reference.Id || reply.Creation.Ticket != reference.Ticket ||
            !Enum.IsDefined(reply.Creation.Phase)))
            throw new IOException("pty-host returned a mismatched creation receipt");
    }
}
