using System.IO.Pipes;
using Agwinterm.Core;
using Agwinterm.Pty.Proto;
using Google.Protobuf;

namespace Agwinterm.Pty;

/// <summary>
/// The pty-host: a headless server that OWNS terminal sessions so they survive UI restarts and
/// crashes (#105, Phase 2a). Runs inside <c>Agwinterm.Win32.exe --pty-host</c> (or in-process for
/// tests — the pipes are real either way).
///
/// Wire model, protocol v2 (protobuf; schema = proto/ptyhost.proto, shared by the C# host, the
/// Rust host, the C# client, and the C++ lite client — JSON removed by decision on #134):
///  - ONE control pipe (<c>&lt;appId&gt;-ptyhost</c>): 4-byte little-endian length prefix + encoded
///    Request/Reply, strict request/response. Verbs: hello (hard version handshake), create,
///    attach, detach, resize, kill, list, shutdown; creation revision 1 adds prepare-create,
///    query-create and cancel-create.
///  - Per ATTACH, one full-duplex DATA pipe (name in the attach reply): raw bytes both ways —
///    client→host is child stdin, host→client is raw ConPTY output. One attached client per
///    session; a new attach supersedes. Host-side close = child exited (code via list); client-side
///    close = detach, the session keeps running.
///
/// Reattach v1 semantics unchanged: plain-text scrollback + DumpModes in the attach reply, then a
/// ConPTY resize-jiggle repaints the live viewport.
/// </summary>
public sealed class PtyHostServer : IDisposable
{
    public const int ProtocolVersion = 2;
    public static string ControlPipeName(string appId) => appId + "-ptyhost";

    private readonly string _appId;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CreationTickets<Hosted> _creations = new();
    private object _lock => _creations.Gate;                     // guards sessions + creation publication
    private readonly Dictionary<string, Hosted> _sessions = new(StringComparer.OrdinalIgnoreCase);
    internal Action<int?>? BeforeCreationPublication; // deterministic test barrier; never supplied by wire/environment

    private sealed class Hosted
    {
        public required string Id;
        public required TerminalSession S;
        public required CreationTickets<Hosted>.Entry Creation;
        public readonly object DataLock = new();                 // guards Data + writes to it
        public readonly PtyResizeTransaction Resize = new();     // real resize and the complete repaint jiggle
        public DataChannel? Data;                                // the currently-attached client
    }

    /// <summary>A data pipe plus its read-cancellation source. Ownership rule (issue #118): the
    /// input pump is the ONLY code that disposes a connected pipe, and only after its pending
    /// ReadAsync has completed — everyone else just cancels. Disposing a pipe with overlapped IO
    /// in flight lets the completion fire against freed IOCP state (native AV, no managed trace).</summary>
    private sealed class DataChannel
    {
        public required NamedPipeServerStream Pipe;
        public readonly CancellationTokenSource Cancel = new();
    }

    public PtyHostServer(string appId)
    {
        _appId = appId;
        _ = RetryPendingCleanupAsync();
        _ = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>Completes when a client asks for <c>shutdown</c> — the host process exits then.</summary>
    public Task Completion => _done.Task;

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                ControlPipeName(_appId), PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try { await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            catch (IOException) { pipe.Dispose(); continue; }
            _ = HandleControlClientAsync(pipe, ct);
        }
    }

    private async Task HandleControlClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            var lenBuf = new byte[4];
            try
            {
                while (true)
                {
                    // Framed read: 4-byte LE length + payload. The read keeps ct (a cancelled
                    // read is one awaited op, consumed before dispose).
                    await pipe.ReadExactlyAsync(lenBuf, ct).ConfigureAwait(false);
                    int len = BitConverter.ToInt32(lenBuf);
                    if (len < 0 || len > 16 * 1024 * 1024) break;   // garbled frame → drop the client
                    var payload = new byte[len];
                    await pipe.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

                    Request req;
                    try { req = Request.Parser.ParseFrom(payload); }
                    catch (InvalidProtocolBufferException) { req = new Request(); }
                    var reply = Dispatch(req);

                    // The reply write deliberately takes NO cancellation token (#118, dump-proven):
                    // an abandoned in-flight write + dispose faults the IOCP poller. Replies are
                    // small; letting them finish closes the race window.
                    byte[] frame = new byte[4 + reply.CalculateSize()];
                    BitConverter.TryWriteBytes(frame, reply.CalculateSize());
                    reply.WriteTo(frame.AsSpan(4));
                    await pipe.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
                    await pipe.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (EndOfStreamException) { }
            catch (IOException) { }
        }
    }

    private static Reply Ok() => new() { Ok = true };
    private static Reply Err(string message) => new() { Ok = false, Error = message };

    /// <summary>Handle one request; returns the reply. Public for testing.</summary>
    public Reply Dispatch(Request req)
    {
        try
        {
            return req.CmdCase switch
            {
                Request.CmdOneofCase.Hello => HandleHello(req.Hello),
                Request.CmdOneofCase.Create => HandleCreate(req.Create),
                Request.CmdOneofCase.Attach => HandleAttach(req.Attach),
                Request.CmdOneofCase.Detach => WithSession(req.Detach.Id, h => { CloseData(h); return Ok(); }, req.Detach.CreationTicket),
                Request.CmdOneofCase.Resize => HandleResize(req.Resize),
                Request.CmdOneofCase.Kill => HandleKill(req.Kill),
                Request.CmdOneofCase.PrepareCreate => HandlePrepare(req.PrepareCreate),
                Request.CmdOneofCase.QueryCreate => HandleQuery(req.QueryCreate),
                Request.CmdOneofCase.CancelCreate => HandleCancel(req.CancelCreate),
                Request.CmdOneofCase.List => HandleList(),
                Request.CmdOneofCase.Shutdown => HandleShutdown(),
                _ => Err("unknown command"),
            };
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private Reply HandleHello(Hello h)
    {
        // The handshake is the protocol's forward-compat seam: a client offering a DIFFERENT
        // version is refused loudly (never half-understood), and the reply names ours.
        return h.Protocol == ProtocolVersion
            ? new Reply { Ok = true, Hello = new HelloReply { Protocol = ProtocolVersion, Pid = (uint)Environment.ProcessId, CreationRevision = 1 } }
            : Err($"protocol mismatch: host={ProtocolVersion} client={h.Protocol}");
    }

    private Reply HandleCreate(Create c)
    {
        if (c.Cols > 10000 || c.Rows > 10000) return Err("create cols/rows must not exceed 10000");
        string id = c.Id.Length > 0 ? c.Id : Guid.NewGuid().ToString();
        int cols = c.Cols > 0 ? (int)c.Cols : 120;
        int rows = c.Rows > 0 ? (int)c.Rows : 30;
        if (c.App.Length == 0) return Err("create needs app");
        Dictionary<string, string>? env = c.Env.Count > 0 ? new(c.Env) : null;

        CreationTickets<Hosted>.Entry creation;
        lock (_lock)
        {
            var entry = c.CreationTicket.Length == 0 ? _creations.Prepare(id, DateTimeOffset.UtcNow)
                : _creations.Find(id, c.CreationTicket, DateTimeOffset.UtcNow);
            if (entry is null) return Err("unknown, expired or unavailable creation ticket");
            creation = entry;
            if (c.CreationTicket.Length > 0 && entry.State == CreationTickets<Hosted>.Phase.Live)
                return CreatedReply(entry); // the original attempt, not a second spawn
            if (!_creations.Begin(entry)) return _sessions.ContainsKey(id)
                ? Err($"session '{id}' already exists") : Err("creation is pending or cancelled");
        }
        TerminalSession? session = null;
        Hosted? hosted = null;
        try
        {
            session = new TerminalSession(cols, rows);
            hosted = new Hosted { Id = id, S = session, Creation = creation };
            // Forward raw output to whichever client is attached; child exit closes the data pipe (EOF
            // is the client's exit signal — the code is in `list`).
            session.RawOutput += chunk =>
            {
                lock (hosted.DataLock)
                {
                    var d = hosted.Data;
                    if (d is null) return;
                    try { d.Pipe.Write(chunk); d.Pipe.Flush(); }
                    catch { CloseDataLocked(hosted); }   // client vanished mid-write → plain detach
                }
            };
            session.Exited += _ => CloseData(hosted);
            // Await the spawn so a failure (bad exe, bad cwd) travels back as the create's error — the
            // throwing form: StartAsync would paint the reason into THIS process's emulator, which no
            // client sees, and answer ok for a session that never ran (#227 r3).
            session.StartOrThrowAsync(c.App, c.Args.ToArray(), verbatimCommandLine: c.Verbatim,
                    extraEnv: env, cwd: c.Cwd.Length > 0 ? c.Cwd : null, deElevate: c.DeElevate,
                    freshEnv: !c.FreshEnvOff)
                .GetAwaiter().GetResult();
            BeforeCreationPublication?.Invoke(session.ChildProcessId);
            bool publish;
            lock (_lock)
            {
                publish = _creations.Complete(creation, hosted);
                if (publish) _sessions[id] = hosted;
            }
            if (!publish)
            {
                FinishCleanup(creation, hosted);
                return Err("creation cancelled; query the exact ticket for cleanup completion");
            }
            return CreatedReply(creation);
        }
        catch (Exception ex)
        {
            bool cleaned = true;
            try { if (hosted is not null) CloseData(hosted); cleaned = session?.DisposeHosted() ?? true; } catch { cleaned = false; }
            lock (_lock)
            {
                if (_sessions.TryGetValue(id, out var current) && ReferenceEquals(current, hosted)) _sessions.Remove(id);
                if (cleaned) _creations.Cleaned(creation);
                else if (hosted is not null) _creations.CleanupFailed(creation, hosted);
            }
            return Err("spawn failed: " + ex.Message);
        }
    }

    private static Reply CreatedReply(CreationTickets<Hosted>.Entry e) => new()
    { Ok = true, Create = new CreateReply { Id = e.Id, CreationTicket = e.Ticket } };

    private Reply HandlePrepare(PrepareCreate request)
    {
        lock (_lock)
        {
            var e = _creations.Prepare(request.Id, DateTimeOffset.UtcNow);
            return e is null ? Err("missing id, host stopping or preparation limit reached") : CreationReplyFor(request.Id, e.Ticket, e);
        }
    }

    private static Reply CreationReplyFor(string id, string ticket, CreationTickets<Hosted>.Entry? e) => new()
    {
        Ok = true,
        Creation = new CreationReply
        {
            Id = id,
            Ticket = ticket,
            Phase = e is null ? CreationPhase.CreationUnknown : e.State switch
            {
                CreationTickets<Hosted>.Phase.Prepared => CreationPhase.CreationPrepared,
                CreationTickets<Hosted>.Phase.Creating => CreationPhase.CreationCreating,
                CreationTickets<Hosted>.Phase.Live => CreationPhase.CreationLive,
                _ => CreationPhase.CreationCancelling
            }
        }
    };

    private Reply HandleQuery(CreationRef request)
    {
        lock (_lock) return CreationReplyFor(request.Id, request.Ticket, _creations.Find(request.Id, request.Ticket, DateTimeOffset.UtcNow));
    }

    private Reply HandleCancel(CreationRef request)
    {
        CreationTickets<Hosted>.Entry? e;
        Hosted? claimed;
        lock (_lock)
        {
            e = _creations.Find(request.Id, request.Ticket, DateTimeOffset.UtcNow);
            if (e is null) return CreationReplyFor(request.Id, request.Ticket, null);
            claimed = _creations.Cancel(e);
            if (claimed is not null && _sessions.TryGetValue(e.Id, out var current) && ReferenceEquals(current, claimed)) _sessions.Remove(e.Id);
        }
        if (claimed is not null) FinishCleanup(e, claimed);
        return HandleQuery(request);
    }

    private bool DisposeHosted(Hosted h)
    {
        try { CloseData(h); return h.S.DisposeHosted(); }
        catch { return false; } // keep the exact cleanup obligation queryable, never claim completion
    }

    private void FinishCleanup(CreationTickets<Hosted>.Entry e, Hosted h)
    {
        bool cleaned = DisposeHosted(h);
        lock (_lock)
        {
            if (cleaned) _creations.Cleaned(e);
            else _creations.CleanupFailed(e, h);
        }
    }

    private async Task RetryPendingCleanupAsync()
    {
        while (!_creations.Drained.IsCompleted)
        {
            await Task.Delay(500).ConfigureAwait(false);
            IReadOnlyList<(CreationTickets<Hosted>.Entry Attempt, Hosted Value)> pending;
            lock (_lock) pending = _creations.ClaimPendingCleanup();
            foreach (var (attempt, h) in pending) FinishCleanup(attempt, h);
        }
    }

    private Reply HandleAttach(Attach a) => WithSession(a.Id, hosted =>
    {
        bool repaint = a.Repaint;
        string dataName = ControlPipeName(_appId) + "-d-" + Guid.NewGuid().ToString("N")[..8];
        var data = new NamedPipeServerStream(dataName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // Snapshot content + modes UNDER the session lock, before any new output can race the seed.
        var reply = new AttachReply { Pipe = dataName, CreationTicket = hosted.Creation.Ticket };
        var s = hosted.S;
        lock (s.SyncRoot)
        {
            for (int i = 0; i < s.Emulator.HistoryCount; i++) reply.Scrollback.Add(s.Emulator.DumpHistoryRow(i));
            reply.Modes = s.Emulator.DumpModes();
            // Full-fidelity attributed HISTORY (the live screen arrives via the repaint jiggle, so
            // includeVisible=false). The client seeds from this instead of the plain text above.
            if (s.Emulator is TerminalEmulator te)
                reply.ScrollbackBlob = Google.Protobuf.ByteString.CopyFrom(
                    BufferPersist.Serialize(te, includeVisible: false));
        }
        reply.Cols = (uint)s.Cols;
        reply.Rows = (uint)s.Rows;
        reply.ChildPid = (uint)(s.ChildProcessId ?? 0);
        reply.HasExited = s.HasExited;
        reply.ExitCode = s.ExitCode ?? 0;

        _ = Task.Run(async () =>
        {
            try
            {
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await data.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
            }
            catch { data.Dispose(); return; }              // client never came (no reads yet) — safe to close here
            var ch = new DataChannel { Pipe = data };
            lock (hosted.DataLock) { CloseDataLocked(hosted); hosted.Data = ch; }
            if (hosted.S.HasExited)
            {
                // Exited while attaching → the client's EOF signal. No read is pending yet, so
                // closing directly here is safe — the pump never starts for this channel.
                lock (hosted.DataLock)
                {
                    if (ReferenceEquals(hosted.Data, ch)) hosted.Data = null;
                    try { ch.Pipe.Dispose(); } catch { }
                    ch.Cancel.Dispose();
                }
                return;
            }
            if (repaint) hosted.Resize.Run(() => JiggleRepaint(hosted.S));
            await PumpInputAsync(hosted, ch).ConfigureAwait(false);
        });

        return new Reply { Ok = true, Attach = reply };
    }, a.CreationTicket);

    /// <summary>Client→host side of a data pipe: bytes are the child's stdin. EOF/error = detach;
    /// the session keeps running unattached. This pump OWNS the pipe: it is the only code that
    /// disposes it, and only here — after its ReadAsync has completed (EOF, error, or the
    /// cancellation that CloseData signals) — so no overlapped read is ever in flight at close
    /// (issue #118).</summary>
    private static async Task PumpInputAsync(Hosted hosted, DataChannel ch)
    {
        try
        {
            await HostInputPump.CopyAsync(ch.Pipe, (bytes, length) => hosted.S.Write(bytes.AsSpan(0, length)), ch.Cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        // Under DataLock so a RawOutput forward can't be mid-write on the pipe we're closing.
        lock (hosted.DataLock)
        {
            if (ReferenceEquals(hosted.Data, ch)) hosted.Data = null;
            try { ch.Pipe.Dispose(); } catch { }
            ch.Cancel.Dispose();
        }
    }

    /// <summary>The classic ConPTY repaint trick: a real row-count change makes conhost re-emit the
    /// whole viewport (colors, cursor, alt screen) to the freshly-attached client. A plain re-send
    /// of the same size is deduped by ConPTY, hence the jiggle.</summary>
    private static void JiggleRepaint(TerminalSession s)
    {
        int cols = s.Cols, rows = s.Rows;
        try
        {
            s.Resize(cols, Math.Max(2, rows - 1));
            Thread.Sleep(60);                                    // let conhost process the first resize
            s.Resize(cols, rows);
        }
        catch { }
    }

    private Reply HandleResize(Resize r) => WithSession(r.Id, h =>
    {
        if (!PtyResizeTransaction.Valid(r.Cols, r.Rows)) return Err("resize cols/rows must be in 1..10000");
        h.Resize.Run(() => h.S.Resize((int)r.Cols, (int)r.Rows));
        return Ok();
    }, r.CreationTicket);

    private Reply HandleKill(SessionRef request)
    {
        string ticket;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(request.Id, out var hosted)) return Err($"no session '{request.Id}'");
            ticket = hosted.Creation.Ticket;
            if (request.CreationTicket.Length > 0 && request.CreationTicket != ticket) return Err("session incarnation changed");
        }
        var cancelled = HandleCancel(new CreationRef { Id = request.Id, Ticket = ticket });
        return cancelled.Creation.Phase == CreationPhase.CreationUnknown ? Ok() : Err("session cleanup is pending");
    }

    private Reply HandleList()
    {
        List<Hosted> all;
        lock (_lock) all = _sessions.Values.ToList();
        var list = new ListReply();
        foreach (var h in all)
        {
            var info = new SessionInfo
            {
                Id = h.Id,
                CreationTicket = h.Creation.Ticket,
                Cols = (uint)h.S.Cols,
                Rows = (uint)h.S.Rows,
                ChildPid = (uint)(h.S.ChildProcessId ?? 0),
                HasExited = h.S.HasExited,
                ExitCode = h.S.ExitCode ?? 0,
            };
            lock (h.S.SyncRoot) info.Title = h.S.Emulator.Title;
            lock (h.DataLock) info.Attached = h.Data is not null;
            list.Sessions.Add(info);
        }
        return new Reply { Ok = true, List = list };
    }

    private Reply HandleShutdown()
    {
        // Tear down AFTER the reply has a moment to flush — disposing inline races the response
        // off the pipe (the client would see EOF instead of the ack).
        _ = Task.Run(async () => { await Task.Delay(100); Dispose(); });
        return Ok();
    }

    private Reply WithSession(string id, Func<Hosted, Reply> act, string ticket = "")
    {
        if (id.Length == 0) return Err("missing id");
        Hosted? h;
        lock (_lock) _sessions.TryGetValue(id, out h);
        return h is null ? Err($"no session '{id}'") : ticket.Length > 0 && ticket != h.Creation.Ticket
            ? Err("session incarnation changed") : act(h);
    }

    /// <summary>Signal the attached client (if any) to go away. Cancels the pump's pending read —
    /// the pump then disposes the pipe (which is the client's EOF). Never disposes here: closing
    /// a pipe with its overlapped read still in flight is the #118 crash.</summary>
    private void CloseData(Hosted h) { lock (h.DataLock) CloseDataLocked(h); }
    private static void CloseDataLocked(Hosted h)
    {
        var ch = h.Data;
        h.Data = null;                                   // writers stop immediately
        if (ch is not null) try { ch.Cancel.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        IReadOnlyList<(CreationTickets<Hosted>.Entry Attempt, Hosted Value)> all;
        lock (_lock) { all = _creations.Stop(); _sessions.Clear(); }
        foreach (var (attempt, h) in all)
            FinishCleanup(attempt, h);
        // A creator may still own a child not yet published in _sessions. Keep the host alive
        // until that exact attempt finishes cleanup; cancellation acceptance is not completion.
        _ = CompleteShutdownWhenDrained();
    }

    private async Task CompleteShutdownWhenDrained()
    {
        await _creations.Drained.ConfigureAwait(false);
        _done.TrySetResult();
    }
}
