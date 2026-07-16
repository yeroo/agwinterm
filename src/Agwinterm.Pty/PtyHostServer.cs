using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>
/// The pty-host: a headless server that OWNS terminal sessions so they survive UI restarts and
/// crashes (#105, Phase 2a). Runs inside <c>Agwinterm.Win32.exe --pty-host</c> (or in-process for
/// tests — the pipes are real either way).
///
/// Wire model:
///  - ONE control pipe (<c>&lt;appId&gt;-ptyhost</c>): newline-JSON request/response, same style as
///    the control API. Verbs: hello (version handshake), create, attach, detach, resize, kill,
///    list, shutdown.
///  - Per ATTACH, one full-duplex DATA pipe (name returned by attach): client→host bytes are child
///    stdin (keystrokes verbatim), host→client bytes are raw ConPTY output. One attached client per
///    session; a new attach supersedes the previous data pipe. The data pipe closing from the host
///    side means the child exited (ask <c>list</c> for the code); closing from the client side is a
///    detach — the session keeps running.
///
/// Reattach v1 (deliberately simple, mirrors the existing restart-restore semantics): the attach
/// response carries the host emulator's scrollback as PLAIN TEXT (client seeds it dimmed, like
/// buffer restore) plus the mode state as synthesized VT (<see cref="Agwinterm.Core.TerminalEmulator.DumpModes"/>);
/// then a ConPTY resize-jiggle makes the child's TUI repaint the live screen in full color. Full
/// attributed-grid serialization can replace the text seed later without a protocol change.
/// </summary>
public sealed class PtyHostServer : IDisposable
{
    public const int ProtocolVersion = 1;
    public static string ControlPipeName(string appId) => appId + "-ptyhost";

    private readonly string _appId;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();                       // guards _sessions
    private readonly Dictionary<string, Hosted> _sessions = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Hosted
    {
        public required string Id;
        public required TerminalSession S;
        public readonly object DataLock = new();                 // guards Data + writes to it
        public NamedPipeServerStream? Data;                      // the currently-attached client
    }

    public PtyHostServer(string appId)
    {
        _appId = appId;
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
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                {
                    if (line.Length == 0) continue;
                    await writer.WriteLineAsync(Dispatch(line).AsMemory(), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    /// <summary>Handle one request line; returns the JSON response line. Public for testing.</summary>
    public string Dispatch(string requestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            string cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
            return cmd switch
            {
                "hello" => HandleHello(root),
                "create" => HandleCreate(root),
                "attach" => HandleAttach(root),
                "detach" => WithSession(root, h => { CloseData(h); return Ok(); }),
                "resize" => HandleResize(root),
                "kill" => HandleKill(root),
                "list" => HandleList(),
                "shutdown" => HandleShutdown(),
                _ => Err($"unknown command '{cmd}'"),
            };
        }
        catch (JsonException) { return Err("invalid JSON"); }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private string HandleHello(JsonElement root)
    {
        // The handshake is the protocol's forward-compat seam: a client offering a DIFFERENT major
        // version is refused loudly (never half-understood), and the reply names ours so the client
        // can decide (e.g. a newer UI telling the user to let the old host drain).
        int theirs = root.TryGetProperty("protocol", out var p) && p.TryGetInt32(out int v) ? v : -1;
        return theirs == ProtocolVersion
            ? Ok(w => { w.WriteNumber("protocol", ProtocolVersion); w.WriteNumber("pid", Environment.ProcessId); })
            : Err($"protocol mismatch: host={ProtocolVersion} client={theirs}");
    }

    private string HandleCreate(JsonElement root)
    {
        string id = GetString(root, "id") ?? Guid.NewGuid().ToString();
        int cols = GetInt(root, "cols", 120), rows = GetInt(root, "rows", 30);
        string app = GetString(root, "app") ?? "";
        if (app.Length == 0) return Err("create needs args.app");
        string[] args = root.TryGetProperty("args", out var av) && av.ValueKind == JsonValueKind.Array
            ? av.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : Array.Empty<string>();
        string? cwd = GetString(root, "cwd");
        bool verbatim = root.TryGetProperty("verbatim", out var vb) && vb.ValueKind == JsonValueKind.True;
        Dictionary<string, string>? env = null;
        if (root.TryGetProperty("env", out var ev) && ev.ValueKind == JsonValueKind.Object)
        {
            env = new Dictionary<string, string>();
            foreach (var kv in ev.EnumerateObject()) env[kv.Name] = kv.Value.GetString() ?? "";
        }

        var session = new TerminalSession(cols, rows);
        var hosted = new Hosted { Id = id, S = session };
        lock (_lock)
        {
            if (_sessions.ContainsKey(id)) { session.Dispose(); return Err($"session '{id}' already exists"); }
            _sessions[id] = hosted;
        }
        // Forward raw output to whichever client is attached; child exit closes the data pipe (EOF
        // is the client's exit signal — the code is in `list`).
        session.RawOutput += chunk =>
        {
            lock (hosted.DataLock)
            {
                var d = hosted.Data;
                if (d is null) return;
                try { d.Write(chunk); d.Flush(); }
                catch { CloseDataLocked(hosted); }   // client vanished mid-write → plain detach
            }
        };
        session.Exited += _ => CloseData(hosted);
        _ = session.StartAsync(app, args, verbatimCommandLine: verbatim, extraEnv: env, cwd: cwd);
        return Ok(w => w.WriteString("id", id));
    }

    private string HandleAttach(JsonElement root) => WithSession(root, hosted =>
    {
        bool repaint = root.TryGetProperty("repaint", out var rp) && rp.ValueKind == JsonValueKind.True;
        string dataName = ControlPipeName(_appId) + "-d-" + Guid.NewGuid().ToString("N")[..8];
        var data = new NamedPipeServerStream(dataName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // Snapshot content + modes UNDER the session lock, before any new output can race the seed.
        List<string> scrollback = new();
        string modes;
        var s = hosted.S;
        lock (s.SyncRoot)
        {
            for (int i = 0; i < s.Emulator.HistoryCount; i++) scrollback.Add(s.Emulator.DumpHistoryRow(i));
            modes = s.Emulator.DumpModes();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await data.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
            }
            catch { data.Dispose(); return; }              // client never came — abandon this attach
            lock (hosted.DataLock) { CloseDataLocked(hosted); hosted.Data = data; }
            if (hosted.S.HasExited) { CloseData(hosted); return; }   // exited while attaching → immediate EOF
            if (repaint) JiggleRepaint(hosted.S);
            await PumpInputAsync(hosted, data).ConfigureAwait(false);
        });

        return Ok(w =>
        {
            w.WriteString("pipe", dataName);
            w.WriteNumber("cols", s.Cols); w.WriteNumber("rows", s.Rows);
            if (s.ChildProcessId is int pid) w.WriteNumber("childPid", pid);
            w.WriteBoolean("hasExited", s.HasExited);
            if (s.ExitCode is int ec) w.WriteNumber("exitCode", ec);
            w.WriteString("modes", modes);
            w.WriteStartArray("scrollback");
            foreach (var line in scrollback) w.WriteStringValue(line);
            w.WriteEndArray();
        });
    });

    /// <summary>Client→host side of a data pipe: bytes are the child's stdin. EOF/error = detach;
    /// the session keeps running unattached.</summary>
    private static async Task PumpInputAsync(Hosted hosted, NamedPipeServerStream data)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int n = await data.ReadAsync(buf).ConfigureAwait(false);
                if (n <= 0) break;
                try { hosted.S.Write(buf.AsSpan(0, n)); } catch { break; }   // child gone
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        lock (hosted.DataLock) if (ReferenceEquals(hosted.Data, data)) CloseDataLocked(hosted);
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

    private string HandleResize(JsonElement root) => WithSession(root, h =>
    {
        int cols = GetInt(root, "cols", 0), rows = GetInt(root, "rows", 0);
        if (cols <= 0 || rows <= 0) return Err("resize needs cols/rows");
        h.S.Resize(cols, rows);
        return Ok();
    });

    private string HandleKill(JsonElement root) => WithSession(root, h =>
    {
        lock (_lock) _sessions.Remove(h.Id);
        CloseData(h);
        try { h.S.Dispose(); } catch { }
        return Ok();
    });

    private string HandleList()
    {
        List<Hosted> all;
        lock (_lock) all = _sessions.Values.ToList();
        return Ok(w =>
        {
            w.WriteStartArray("sessions");
            foreach (var h in all)
            {
                w.WriteStartObject();
                w.WriteString("id", h.Id);
                w.WriteNumber("cols", h.S.Cols); w.WriteNumber("rows", h.S.Rows);
                if (h.S.ChildProcessId is int pid) w.WriteNumber("childPid", pid);
                w.WriteBoolean("hasExited", h.S.HasExited);
                if (h.S.ExitCode is int ec) w.WriteNumber("exitCode", ec);
                lock (h.S.SyncRoot) w.WriteString("title", h.S.Emulator.Title);
                bool attached; lock (h.DataLock) attached = h.Data is not null;
                w.WriteBoolean("attached", attached);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    private string HandleShutdown()
    {
        // Tear down AFTER the reply has a moment to flush — disposing inline races the response
        // off the pipe (the client would see EOF instead of the ack).
        _ = Task.Run(async () => { await Task.Delay(100); Dispose(); });
        return Ok();
    }

    private string WithSession(JsonElement root, Func<Hosted, string> act)
    {
        string? id = GetString(root, "id");
        if (id is null) return Err("missing id");
        Hosted? h;
        lock (_lock) _sessions.TryGetValue(id, out h);
        return h is null ? Err($"no session '{id}'") : act(h);
    }

    private void CloseData(Hosted h) { lock (h.DataLock) CloseDataLocked(h); }
    private static void CloseDataLocked(Hosted h)
    {
        try { h.Data?.Dispose(); } catch { }
        h.Data = null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        List<Hosted> all;
        lock (_lock) { all = _sessions.Values.ToList(); _sessions.Clear(); }
        foreach (var h in all) { CloseData(h); try { h.S.Dispose(); } catch { } }
        _done.TrySetResult();
    }

    // ---- JSON helpers (same shapes as the control API) ----
    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int GetInt(JsonElement e, string name, int fallback)
        => e.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : fallback;

    private static string Ok(Action<Utf8JsonWriter>? body = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", true);
            body?.Invoke(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Err(string message)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", false);
            w.WriteString("error", message);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
