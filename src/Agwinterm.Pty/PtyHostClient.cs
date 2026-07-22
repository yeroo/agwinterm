using System.IO.Pipes;
using System.Text;
using System.Text.Json;

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
/// Client for the pty-host control pipe (#105, Phase 2a). Thread-safe: control requests are
/// serialized over one pipe connection. Used by tests today and by the server session backend
/// (Phase 2b) tomorrow.
/// </summary>
public sealed class PtyHostClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly object _io = new();

    private PtyHostClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
    }

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
        var pipe = new NamedPipeClientStream(".", PtyHostServer.ControlPipeName(appId), PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(timeoutMs);
        var client = new PtyHostClient(pipe);
        try
        {
            using var doc = client.Request($"{{\"cmd\":\"hello\",\"protocol\":{PtyHostServer.ProtocolVersion}}}");
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>Create a session on the host (not attached yet — call <see cref="Attach"/>).</summary>
    public string Create(string id, int cols, int rows, string app, string[] args,
        string? cwd = null, IReadOnlyDictionary<string, string>? env = null, bool verbatim = false, bool deElevate = false,
        bool freshEnv = true)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("cmd", "create");
            w.WriteString("id", id);
            w.WriteNumber("cols", cols); w.WriteNumber("rows", rows);
            w.WriteString("app", app);
            if (verbatim) w.WriteBoolean("verbatim", true);
            if (deElevate) w.WriteBoolean("deElevate", true);
            if (!freshEnv) w.WriteBoolean("freshEnv", false);   // absent = true (the default)
            w.WriteStartArray("args");
            foreach (var a in args) w.WriteStringValue(a);
            w.WriteEndArray();
            if (cwd is not null) w.WriteString("cwd", cwd);
            if (env is not null)
            {
                w.WriteStartObject("env");
                foreach (var kv in env) w.WriteString(kv.Key, kv.Value);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        using var doc = Request(Encoding.UTF8.GetString(ms.ToArray()));
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Attach to a session: returns its state snapshot plus the connected data stream
    /// (write = child stdin, read = raw ConPTY output; EOF = child exited or superseded).
    /// <paramref name="repaint"/> asks the host for the ConPTY resize-jiggle — pass true when
    /// reattaching an existing view, false right after <see cref="Create"/>.</summary>
    public PtyHostAttachment Attach(string id, bool repaint = false, int timeoutMs = 5000)
    {
        using var doc = Request($"{{\"cmd\":\"attach\",\"id\":{JsonSerializer.Serialize(id)},\"repaint\":{(repaint ? "true" : "false")}}}");
        var root = doc.RootElement;
        string pipeName = root.GetProperty("pipe").GetString()!;
        var scrollback = new List<string>();
        foreach (var e in root.GetProperty("scrollback").EnumerateArray()) scrollback.Add(e.GetString() ?? "");
        var data = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        data.Connect(timeoutMs);
        return new PtyHostAttachment(
            data,
            root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32(),
            root.TryGetProperty("childPid", out var p) ? p.GetInt32() : null,
            root.GetProperty("hasExited").GetBoolean(),
            root.TryGetProperty("exitCode", out var ec) ? ec.GetInt32() : null,
            root.GetProperty("modes").GetString() ?? "",
            scrollback);
    }

    public void Resize(string id, int cols, int rows)
        => Request($"{{\"cmd\":\"resize\",\"id\":{JsonSerializer.Serialize(id)},\"cols\":{cols},\"rows\":{rows}}}").Dispose();

    public void Detach(string id)
        => Request($"{{\"cmd\":\"detach\",\"id\":{JsonSerializer.Serialize(id)}}}").Dispose();

    public void Kill(string id)
        => Request($"{{\"cmd\":\"kill\",\"id\":{JsonSerializer.Serialize(id)}}}").Dispose();

    public IReadOnlyList<PtyHostSessionInfo> List()
    {
        using var doc = Request("{\"cmd\":\"list\"}");
        var list = new List<PtyHostSessionInfo>();
        foreach (var e in doc.RootElement.GetProperty("sessions").EnumerateArray())
            list.Add(new PtyHostSessionInfo(
                e.GetProperty("id").GetString()!,
                e.GetProperty("cols").GetInt32(), e.GetProperty("rows").GetInt32(),
                e.TryGetProperty("childPid", out var p) ? p.GetInt32() : null,
                e.GetProperty("hasExited").GetBoolean(),
                e.TryGetProperty("exitCode", out var ec) ? ec.GetInt32() : null,
                e.GetProperty("title").GetString() ?? "",
                e.GetProperty("attached").GetBoolean()));
        return list;
    }

    /// <summary>Ask the host process to tear down every session and exit.</summary>
    public void Shutdown()
    {
        try { Request("{\"cmd\":\"shutdown\"}").Dispose(); } catch (IOException) { /* host died mid-reply — that IS the outcome */ }
    }

    /// <summary>One request/response over the control pipe. Throws on transport errors and on
    /// <c>ok:false</c> replies (the error message travels in the exception).</summary>
    private JsonDocument Request(string line)
    {
        string? reply;
        lock (_io)
        {
            _writer.WriteLine(line);
            reply = _reader.ReadLine();
        }
        if (reply is null) throw new IOException("pty-host closed the control pipe");
        var doc = JsonDocument.Parse(reply);
        if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return doc;
        string err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "unknown error" : "unknown error";
        doc.Dispose();
        throw new InvalidOperationException("pty-host: " + err);
    }

    public void Dispose()
    {
        try { _reader.Dispose(); } catch { }
        try { _writer.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }
    }
}
