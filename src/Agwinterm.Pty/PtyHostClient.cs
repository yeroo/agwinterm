using System.IO.Pipes;
using Agwinterm.Pty.Proto;
using Google.Protobuf;

namespace Agwinterm.Pty;

/// <summary>Result of <see cref="PtyHostClient.Attach"/>: the connected data stream plus everything
/// a fresh view needs to reconstruct the session (see PtyHostServer for the reattach model).</summary>
public sealed record PtyHostAttachment(
    Stream Data, int Cols, int Rows, int? ChildPid, bool HasExited, int? ExitCode,
    string Modes, IReadOnlyList<string> Scrollback) : IDisposable
{
    public void Dispose() => Data.Dispose();
}

/// <summary>One hosted session as reported by <c>list</c>.</summary>
public sealed record PtyHostSessionInfo(
    string Id, int Cols, int Rows, int? ChildPid, bool HasExited, int? ExitCode, string Title, bool Attached);

/// <summary>
/// Client for the pty-host control pipe, protocol v2 (protobuf frames; schema =
/// proto/ptyhost.proto). Thread-safe: control requests are serialized over one pipe connection.
/// Drives BOTH hosts (C# and Rust) identically — that identity is the compatibility oracle.
/// </summary>
public sealed class PtyHostClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly object _io = new();

    private PtyHostClient(NamedPipeClientStream pipe) => _pipe = pipe;

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
        pipe.Connect(timeoutMs);
        var client = new PtyHostClient(pipe);
        try
        {
            var reply = client.Request(new Request { Hello = new Hello { Protocol = PtyHostServer.ProtocolVersion } });
            if (reply.Hello.Protocol != PtyHostServer.ProtocolVersion)
                throw new InvalidOperationException("pty-host protocol mismatch");
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>Create a session on the host (not attached yet — call <see cref="Attach"/>).</summary>
    public string Create(string id, int cols, int rows, string app, string[] args,
        string? cwd = null, IReadOnlyDictionary<string, string>? env = null, bool verbatim = false, bool deElevate = false,
        bool freshEnv = true)
    {
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
        };
        create.Args.AddRange(args);
        if (env is not null) foreach (var kv in env) create.Env[kv.Key] = kv.Value;
        return Request(new Request { Create = create }).Create.Id;
    }

    /// <summary>Attach to a session: returns its state snapshot plus the connected data stream
    /// (write = child stdin, read = raw ConPTY output; EOF = child exited or superseded).
    /// <paramref name="repaint"/> asks the host for the ConPTY resize-jiggle — pass true when
    /// reattaching an existing view, false right after <see cref="Create"/>.</summary>
    public PtyHostAttachment Attach(string id, bool repaint = false, int timeoutMs = 5000)
    {
        var reply = Request(new Request { Attach = new Attach { Id = id, Repaint = repaint } }).Attach;
        // Async handle: the reader (ServerSession.ReadLoop, or a test) must be able to bail out
        // mid-read via cancellation — a SYNC handle can't be unblocked by closing our own end
        // (SafeHandle ref-counting keeps the OS handle open under a blocked read). The #118 rule
        // still applies: cancel the pending read FIRST, and only the reader disposes the pipe.
        var data = new NamedPipeClientStream(".", reply.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        data.Connect(timeoutMs);
        return new PtyHostAttachment(
            data, (int)reply.Cols, (int)reply.Rows,
            reply.ChildPid != 0 ? (int)reply.ChildPid : null,
            reply.HasExited,
            reply.HasExited ? reply.ExitCode : null,
            reply.Modes, reply.Scrollback);
    }

    public void Resize(string id, int cols, int rows)
        => Request(new Request { Resize = new Resize { Id = id, Cols = (uint)cols, Rows = (uint)rows } });

    public void Detach(string id)
        => Request(new Request { Detach = new SessionRef { Id = id } });

    public void Kill(string id)
        => Request(new Request { Kill = new SessionRef { Id = id } });

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
                s.Title, s.Attached));
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
            byte[] payload = req.ToByteArray();
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
        }
        if (reply.Ok) return reply;
        throw new InvalidOperationException("pty-host: " + (reply.Error.Length > 0 ? reply.Error : "unknown error"));
    }

    public void Dispose()
    {
        lock (_io) _pipe.Dispose();
    }
}
