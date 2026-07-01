using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>
/// Named-pipe control server (agterm's control-API equivalent). Accepts newline-delimited
/// JSON requests {"cmd":..,"target"?:..,"args"?:{..}} and replies {"ok":bool,"result"|"error":..}.
/// Commands route to a session resolved from `target` (id / unique-prefix / "active" / null).
/// </summary>
public sealed class ControlServer : IDisposable
{
    private readonly ISessionHost _host;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;

    // Per-session record of the last content signature transmitted for each image id, so an
    // unchanged image can be re-placed (a=p) instead of re-transmitted (a=T) on every frame.
    private readonly ConditionalWeakTable<TerminalSession, Dictionary<int, long>> _txState = new();

    public ControlServer(ISessionHost host, string pipeName = "agwinterm")
    {
        _host = host;
        _pipeName = pipeName;
    }

    /// <summary>Convenience: serve a single fixed session (tests / simple hosts).</summary>
    public ControlServer(TerminalSession session, string pipeName = "agwinterm")
        : this(new SingleSessionHost(session), pipeName) { }

    public string PipeName => _pipeName;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try { await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            catch (IOException) { pipe.Dispose(); continue; }

            _ = HandleClientAsync(pipe, ct);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
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
            string? target = root.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            JsonElement args = root.TryGetProperty("args", out var a) ? a : default;

            switch (cmd)
            {
                case "ping": return Ok("agwinterm");
                case "install.hooks": return Ok(AgentHooks.Install());
                case "install.skill": return Ok(AgentSkill.Install());
                case "tree": return HandleTree();
                case "session.new": return Ok(_host.NewSession(GetString(args, "name"), GetString(args, "cwd"), GetString(args, "workspace")));
                case "session.select": return _host.SelectSession(target ?? "active") ? Ok("selected") : Err("session not found");
                case "session.close": return _host.CloseSession(target ?? "active") ? Ok("closed") : Err("session not found");
                case "workspace.new": return Ok(_host.NewWorkspace(GetString(args, "name")));
                case "font": return _host.SetFontSize(target, GetString(args, "op") ?? "") ? Ok("font") : Err("session not found");
            }

            // Session-targeted commands.
            var s = _host.Resolve(target);
            if (s is null) return Err("no session");
            return cmd switch
            {
                "session.write" => HandleWrite(s, args),
                "session.type" => HandleType(s, args),
                "session.status" => HandleStatus(s, args),
                "image.show" => HandleImageShow(s, args),
                "image.clear" => HandleImageClear(s),
                "image.frame" => HandleImageFrame(s, args),
                _ => Err($"unknown command '{cmd}'"),
            };
        }
        catch (JsonException ex) { return Err("invalid json: " + ex.Message); }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private string HandleTree()
    {
        var sb = new StringBuilder("{\"workspaces\":[");
        var tree = _host.Tree();
        for (int w = 0; w < tree.Count; w++)
        {
            var ws = tree[w];
            if (w > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(JsonSerializer.Serialize(ws.Id))
              .Append(",\"name\":").Append(JsonSerializer.Serialize(ws.Name))
              .Append(",\"active\":").Append(ws.Active ? "true" : "false")
              .Append(",\"sessions\":[");
            for (int i = 0; i < ws.Sessions.Count; i++)
            {
                var n = ws.Sessions[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(JsonSerializer.Serialize(n.Id))
                  .Append(",\"name\":").Append(JsonSerializer.Serialize(n.Name))
                  .Append(",\"active\":").Append(n.Active ? "true" : "false")
                  .Append(",\"status\":").Append(JsonSerializer.Serialize(n.Status.ToString().ToLowerInvariant()))
                  .Append('}');
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        return OkRaw(sb.ToString());
    }

    private static string HandleWrite(TerminalSession s, JsonElement args)
    {
        s.Inject(Encoding.UTF8.GetBytes(GetString(args, "text") ?? ""));
        return Ok("written");
    }

    private static string HandleType(TerminalSession s, JsonElement args)
    {
        string text = (GetString(args, "text") ?? "").Replace("\r\n", "\r").Replace('\n', '\r');
        s.Write(Encoding.UTF8.GetBytes(text));
        return Ok("typed");
    }

    private static string HandleStatus(TerminalSession s, JsonElement args)
    {
        string st = (GetString(args, "status") ?? "").ToLowerInvariant();
        if (st.Length == 0) return Err("session.status requires args.status");
        AgentStatus status = st switch
        {
            "active" => AgentStatus.Active,
            "blocked" => AgentStatus.Blocked,
            "completed" or "complete" or "done" => AgentStatus.Completed,
            _ => AgentStatus.Idle,
        };
        s.SetStatus(status);
        return Ok(status.ToString().ToLowerInvariant());
    }

    private static string HandleImageShow(TerminalSession s, JsonElement args)
    {
        string? path = GetString(args, "path");
        if (path is null || !File.Exists(path)) return Err("image file not found: " + (path ?? "<null>"));
        int row = GetInt(args, "row", 0), col = GetInt(args, "col", 0), id = GetInt(args, "id", 0);
        string b64 = Convert.ToBase64String(File.ReadAllBytes(path));
        var sb = new StringBuilder();
        sb.Append('\x1b').Append('[').Append(row + 1).Append(';').Append(col + 1).Append('H');
        sb.Append('\x1b').Append("_Gf=100,a=T,i=").Append(id).Append(';').Append(b64).Append('\x1b').Append('\\');
        s.Inject(Encoding.ASCII.GetBytes(sb.ToString()));
        return Ok("shown");
    }

    private static string HandleImageClear(TerminalSession s)
    {
        s.Inject(Encoding.ASCII.GetBytes("\x1b_Ga=d\x1b\\"));
        return Ok("cleared");
    }

    private string HandleImageFrame(TerminalSession s, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return Err("image.frame requires args.images array");

        var state = _txState.GetOrCreateValue(s);
        // Phase 1 (OFF the render lock): resolve placements and read+own the pixel bytes for any
        // image whose content changed. The expensive file read stays out of the lock, and we pass
        // raw PNG bytes straight to the emulator — no base64 encode here and no base64 decode under
        // the lock (the renderer decodes PNG asynchronously on its own thread).
        var ops = new List<(int id, int row, int col, int cols, int rows, int sx, int sy, int sw, int sh, byte[]? data)>();
        int count = 0, transmits = 0;
        long readBytes = 0;
        foreach (var img in images.EnumerateArray())
        {
            string? path = GetString(img, "path");
            if (path is null || !File.Exists(path)) continue;
            int row = GetInt(img, "row", 0), col = GetInt(img, "col", 0);
            int cols = GetInt(img, "cols", 0), rows = GetInt(img, "rows", 0), id = GetInt(img, "id", count + 1);
            // Optional pixel source crop: lets a cached texture be scrolled by moving the visible
            // window (a=p re-place) instead of re-transmitting cropped pixels each step.
            int sx = GetInt(img, "sx", 0), sy = GetInt(img, "sy", 0), sw = GetInt(img, "sw", 0), sh = GetInt(img, "sh", 0);

            long sig = ContentSignature(path);
            bool transmit;
            lock (state) transmit = sig == 0 || !(state.TryGetValue(id, out var prev) && prev == sig);

            byte[]? data = null;
            if (transmit)
            {
                try { data = File.ReadAllBytes(path); } catch { data = null; }
                if (data is not null) { lock (state) state[id] = sig; transmits++; readBytes += data.Length; }
            }
            ops.Add((id, row, col, cols, rows, sx, sy, sw, sh, data));
            count++;
        }

        // Phase 2 (BRIEF lock): swap placements and register any new pixels — dictionary/list
        // updates only, microseconds, so a big image appearing never stalls the paint thread.
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        s.MutateLocked(em =>
        {
            em.ClearPlacements();
            foreach (var op in ops)
            {
                if (op.data is not null)
                    em.SetImageData(op.id, KittyFormat.Png, 0, 0, op.data);
                else if (!em.HasImage(op.id))
                    continue; // never transmitted and no cached copy -> nothing to place
                em.PlaceImage(op.id, op.row, op.col, op.cols, op.rows, op.sx, op.sy, op.sw, op.sh);
            }
        });
        if (_perfLog is not null)
            Perf($"frame images={count} transmits={transmits} readKB={readBytes / 1024} lockMs={System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F2}");
        return Ok($"frame:{count}/{transmits}");
    }

    private static readonly string? _perfLog = Environment.GetEnvironmentVariable("AGWINTERM_PERF");
    private static void Perf(string m) { if (_perfLog is not null) try { File.AppendAllText(_perfLog, "[ctl] " + m + "\n"); } catch { } }

    /// <summary>Cheap content signature (no full read): last-write time, length, and path.</summary>
    private static long ContentSignature(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.LastWriteTimeUtc.Ticks ^ ((long)fi.Length << 1) ^ (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path);
        }
        catch { return 0; }
    }

    private static string? GetString(JsonElement args, string key)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int GetInt(JsonElement args, string key, int def)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : def;

    private static string Ok(string result) => $"{{\"ok\":true,\"result\":{JsonSerializer.Serialize(result)}}}";
    private static string OkRaw(string rawResult) => $"{{\"ok\":true,\"result\":{rawResult}}}";
    private static string Err(string error) => $"{{\"ok\":false,\"error\":{JsonSerializer.Serialize(error)}}}";

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
