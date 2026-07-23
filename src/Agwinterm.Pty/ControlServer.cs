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
    private readonly IWindowHost? _windows;   // multi-window: resolves --window + serves window.* verbs (null = single-window)
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;

    // Per-session record of the last content signature transmitted for each image id, so an
    // unchanged image can be re-placed (a=p) instead of re-transmitted (a=T) on every frame.
    private readonly ConditionalWeakTable<ISession, Dictionary<int, long>> _txState = new();

    public ControlServer(ISessionHost host, string pipeName = "agwinterm")
    {
        _host = host;
        _pipeName = pipeName;
    }

    /// <summary>Multi-window server: content verbs resolve through <paramref name="windows"/> (--window), window.* act on it.</summary>
    public ControlServer(ISessionHost host, IWindowHost windows, string pipeName = "agwinterm")
    {
        _host = host;
        _windows = windows;
        _pipeName = pipeName;
    }

    /// <summary>Convenience: serve a single fixed session (tests / simple hosts).</summary>
    public ControlServer(ISession session, string pipeName = "agwinterm")
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
                    // No ct on the reply write — same #118 hazard as PtyHostServer's ack: a
                    // cancelled WriteLineAsync abandons its overlapped write and the dispose
                    // races the in-flight completion (native AV in the IOCP poller).
                    await writer.WriteLineAsync(Dispatch(line).AsMemory(), CancellationToken.None).ConfigureAwait(false);
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
            // --window <id|prefix|active>: content verbs act on the resolved window (default = frontmost).
            string? windowSel = root.TryGetProperty("window", out var wv) && wv.ValueKind == JsonValueKind.String ? wv.GetString() : null;

            // App-level, window-agnostic verbs first.
            switch (cmd)
            {
                case "ping": return Ok("agwinterm " + AppVersion());
                case "install.hooks": return Ok(AgentHooks.Install());
                case "install.skill": return Ok(AgentSkill.Install());
                case "install.shell": return Ok(ShellIntegrationInstaller.Install());
                case "install.cli": return Ok(GetBool(args, "remove") ? CliInstaller.Uninstall() : CliInstaller.Install());
                case "omp.list": return Ok(string.Join("\n", OmpThemes.List().Select(t => t.Name)));
                // ---- Wave F1b: window management (target = the window selector) ----
                case "window.new": return _windows is null ? Err("multi-window unavailable") : Ok(_windows.WindowNew(GetString(args, "name") ?? target));
                case "window.list": return _windows is null ? Err("multi-window unavailable") : HandleWindowList();
                case "window.select": return _windows is null ? Err("multi-window unavailable") : (_windows.WindowSelect(target) ? Ok("selected") : Err("window not found"));
                case "window.close": return _windows is null ? Err("multi-window unavailable") : (_windows.WindowClose(target) ? Ok("closed") : Err("window not found"));
                case "window.delete": return _windows is null ? Err("multi-window unavailable") : (_windows.WindowDelete(target) ? Ok("deleted") : Err("window not found / last window"));
                case "window.rename": return _windows is null ? Err("multi-window unavailable") : (_windows.WindowRename(target, GetString(args, "name") ?? "") ? Ok("renamed") : Err("window not found / blank name"));
                case "window.resize": return _windows is null ? Err("multi-window unavailable") : (_windows.WindowResize(target, GetInt(args, "w", 0), GetInt(args, "h", 0)) ? Ok("resized") : Err("window not found"));
                case "window.move": return _windows is null ? Err("multi-window unavailable") : (_windows.WindowMove(target, GetInt(args, "x", 0), GetInt(args, "y", 0)) ? Ok("moved") : Err("window not found"));
                case "window.zoom": return _windows is null ? Err("multi-window unavailable") : (_windows.WindowZoom(target) ? Ok("zoomed") : Err("window not found"));
            }

            // Content verbs act on the target window's host (default = frontmost).
            ISessionHost host = _windows is null ? _host : (_windows.ResolveWindow(windowSel) ?? _host);
            if (windowSel is not null && _windows is not null && _windows.ResolveWindow(windowSel) is null)
                return Err("window not found: " + windowSel);

            switch (cmd)
            {
                case "tree": return HandleTree(host);
                case "window.state": return HandleWindowState(host);
                case "session.new": return Ok(host.NewSession(GetString(args, "name"), GetString(args, "cwd"), GetString(args, "workspace"),
                    GetString(args, "command"), GetString(args, "workspace-name"), GetBool(args, "create-workspace"), GetString(args, "profile")));
                case "profiles.list": return Ok(host.ProfilesList());
                case "profiles.reload": return Ok(host.ProfilesReload());
                case "session.select": return host.SelectSession(target ?? "active") ? Ok("selected") : Err("session not found");
                case "session.close": return host.CloseSession(target ?? "active") ? Ok("closed") : Err("session not found");
                case "workspace.new": return Ok(host.NewWorkspace(GetString(args, "name")));
                case "font": return host.SetFontSize(target, GetString(args, "op") ?? "") ? Ok("font") : Err("session not found");
                case "dashboard": return host.Dashboard(GetBool(args, "close"), GetString(args, "ids"), GetInt(args, "font-size", 0)) ? Ok("dashboard") : Err("dashboard unavailable");

                // ---- Wave A1: verb parity ----
                case "session.go": host.SessionGo(GetString(args, "dir") ?? "next"); return Ok("go");
                case "session.move":
                    return (GetString(args, "workspace") is { } wsMove
                        ? host.SessionToWorkspace(target, wsMove)
                        : host.SessionReorder(target, GetString(args, "dir") ?? "down"))
                        ? Ok("moved") : Err("not found");
                case "session.rename": return host.SessionRename(target, GetString(args, "name") ?? "") ? Ok("renamed") : Err("session not found / blank name");
                case "session.seen": return host.SessionSeen(target) ? Ok("seen") : Err("session not found");
                case "broadcast": return Ok(host.BroadcastOp(GetString(args, "op") ?? "toggle"));
                case "session.readonly": return Ok(host.ReadOnlyOp(target, GetString(args, "op") ?? "toggle"));
                case "session.output": return Ok(host.SessionOutput(target)); // last completed command's output (FTCS)
                case "workspace.rename": return host.WorkspaceRename(target, GetString(args, "name") ?? "") ? Ok("renamed") : Err("workspace not found");
                case "workspace.delete": return host.WorkspaceDelete(target) ? Ok("deleted") : Err("workspace not found");
                case "workspace.select": return host.WorkspaceSelect(target) ? Ok("selected") : Err("workspace not found");
                case "workspace.move": return host.WorkspaceReorder(target, GetString(args, "dir") ?? "down") ? Ok("moved") : Err("workspace not found");
                case "session.split": host.Split(GetString(args, "op") ?? "toggle"); return Ok("split");
                case "session.focus": host.FocusPaneDir(GetString(args, "dir") ?? "right"); return Ok("focus");
                case "session.resize":
                    {
                        double? ratio = null;
                        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ratio", out var rv) && rv.TryGetDouble(out var rd)) ratio = rd;
                        host.ResizeSplit(ratio, GetInt(args, "grow-left", 0), GetInt(args, "grow-right", 0));
                        return Ok("resized");
                    }
                case "theme.list": return Ok(string.Join("\n", host.ThemeList()));
                case "theme.set": return host.ThemeSet(GetString(args, "name") ?? "") ? Ok("theme set") : Err("theme not found");
                case "keymap.reload": return Ok(host.KeymapReload());
                case "restore.clear": return Ok(host.RestoreClear());
                case "config.set": return Ok(host.ConfigSet(GetString(args, "key") ?? "", GetString(args, "value") ?? ""));
                case "config.get": return Ok(host.ConfigGet(GetString(args, "key") ?? ""));
                case "config.list": return Ok(host.ConfigList());
                case "settings.open": return Ok(host.SettingsOpen());
                case "sidebar":
                {
                    string op = GetString(args, "op") ?? "toggle";
                    if (op is "state" or "get") return Ok(host.SidebarState());   // read-back, no mutation
                    host.SidebarOp(op); return Ok("sidebar");
                }
                case "session.copy": return Ok(host.SessionCopy(target)); // selection text (host-side), "" if none
                case "selection.all": return Ok(host.SelectionAll(target));
                case "selection.copy": return Ok(host.SelectionCopy(target));      // -> Windows clipboard
                case "selection.clear": return Ok(host.SelectionClear(target));
                case "selection.finalize": return Ok(host.SelectionFinalize(target)); // copy-on-select path (testing)
                case "session.paste": return Ok(host.SessionPaste(target, GetString(args, "text")));
                case "session.search": return Ok(host.SessionSearch(target, GetString(args, "query"), GetString(args, "action")));
                case "session.scratch": return host.SessionScratch(target, GetString(args, "op") ?? "toggle") ? Ok("scratch") : Err("session not found");
                case "quick": host.Quick(GetString(args, "op") ?? "toggle"); return Ok("quick");
                case "session.overlay":
                    return Ok(host.SessionOverlay(target, GetString(args, "action") ?? "open",
                        GetString(args, "command"), GetInt(args, "size-percent", 0),
                        GetBool(args, "wait"), GetBool(args, "block")));
                case "notify":
                    return host.Notify(target, GetString(args, "title"), GetString(args, "body") ?? "")
                        ? Ok("notified") : Err("session not found");
                case "session.flag":
                    return host.SessionFlag(target, GetString(args, "op") ?? "toggle") ? Ok("flag") : Err("session not found");
                case "session.bind":
                    return host.SessionBind(target, GetString(args, "agent") ?? "claude") ? Ok("bound") : Err("session not found");
                case "claude.adopt":
                    return Ok(host.AdoptClaude());
                case "claude.yolo":
                    return Ok(host.RestartClaudeYolo(target));
                case "claude.update":
                    return Ok(host.UpdateClaude());
                case "app.update":
                    return Ok(host.UpdateApp());
                case "workspace.focus": host.WorkspaceFocus(GetString(args, "op") ?? "toggle"); return Ok("focus");
                case "session.background":
                    return Ok(host.SessionBackground(target, GetString(args, "action") ?? "set",
                        GetString(args, "path"), GetInt(args, "opacity", -1), GetString(args, "mode")));
                case "session.switch": return Ok(host.SessionSwitch(GetString(args, "op") ?? "advance"));
                case "command.run":
                {
                    string? nameOrCmd = GetString(args, "name") ?? GetString(args, "command");
                    if (string.IsNullOrWhiteSpace(nameOrCmd)) return Err("command.run needs args.name or args.command");
                    return Ok(host.CommandRun(nameOrCmd!, GetString(args, "mode")));
                }
                case "command.list": return Ok(host.CommandList());
                case "command.leader": return Ok(host.CommandLeader(GetString(args, "op") ?? "state"));
                case "omp.set": return Ok(host.OmpSet(GetString(args, "name") ?? "", GetBool(args, "persist")));
            }

            // Session-targeted commands.
            var s = host.Resolve(target);
            if (s is null) return Err("no session");
            return cmd switch
            {
                "session.write" => HandleWrite(s, args),
                "session.type" => HandleType(s, args),
                "session.text" => HandleText(s),
                "session.status" => HandleStatus(s, args),
                "image.show" => HandleImageShow(s, args),
                "image.sixel" => HandleImageSixel(s, args),
                "image.clear" => HandleImageClear(s),
                "image.frame" => HandleImageFrame(s, args),
                _ => Err($"unknown command '{cmd}'"),
            };
        }
        catch (JsonException ex) { return Err("invalid json: " + ex.Message); }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private string HandleWindowList()
    {
        var sb = new StringBuilder("{\"windows\":[");
        var wins = _windows!.Windows();
        for (int i = 0; i < wins.Count; i++)
        {
            var w = wins[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(JsonSerializer.Serialize(w.Id))
              .Append(",\"name\":").Append(JsonSerializer.Serialize(w.Name))
              .Append(",\"open\":").Append(w.Open ? "true" : "false")
              .Append(",\"active\":").Append(w.Active ? "true" : "false").Append('}');
        }
        sb.Append("]}");
        return OkRaw(sb.ToString());
    }

    private string HandleTree(ISessionHost host)
    {
        var sb = new StringBuilder("{\"workspaces\":[");
        var tree = host.Tree();
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
                  .Append(",\"status\":").Append(JsonSerializer.Serialize(n.Status.ToString().ToLowerInvariant()));
                if (n.Overlay) sb.Append(",\"overlay\":true");
                if (n.Flagged) sb.Append(",\"flagged\":true");
                if (n.Background) sb.Append(",\"background\":true");
                if (n.Notifications > 0) sb.Append(",\"notifications\":").Append(n.Notifications);
                if (n.StatusBlink) sb.Append(",\"statusBlink\":true");
                if (n.OverlaySize > 0) sb.Append(",\"overlaySize\":").Append(n.OverlaySize);
                if (n.PaneCount > 1)
                {
                    sb.Append(",\"paneCount\":").Append(n.PaneCount).Append(",\"focusedPane\":").Append(n.FocusedPane);
                    if (n.SplitRatios is { Count: > 0 })
                    {
                        sb.Append(",\"splitRatios\":[");
                        for (int r = 0; r < n.SplitRatios.Count; r++)
                        { if (r > 0) sb.Append(','); sb.Append(n.SplitRatios[r].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); }
                        sb.Append(']');
                    }
                    if (n.PaneIds is { Count: > 0 })
                    {
                        sb.Append(",\"paneIds\":[");
                        for (int r = 0; r < n.PaneIds.Count; r++)
                        { if (r > 0) sb.Append(','); sb.Append(JsonSerializer.Serialize(n.PaneIds[r])); }
                        sb.Append(']');
                    }
                }
                sb.Append('}');
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        return OkRaw(sb.ToString());
    }

    private static string HandleWindowState(ISessionHost host)
    {
        var s = host.WindowState();
        var sb = new StringBuilder("{");
        sb.Append("\"sidebarVisible\":").Append(s.SidebarVisible ? "true" : "false")
          .Append(",\"fullscreen\":").Append(s.Fullscreen ? "true" : "false")
          .Append(",\"maximized\":").Append(s.Maximized ? "true" : "false")
          .Append(",\"quickTerminalVisible\":").Append(s.QuickTerminalVisible ? "true" : "false");
        if (s.ActiveWorkspace is not null) sb.Append(",\"activeWorkspace\":").Append(JsonSerializer.Serialize(s.ActiveWorkspace));
        if (s.ActiveSession is not null) sb.Append(",\"activeSession\":").Append(JsonSerializer.Serialize(s.ActiveSession));
        sb.Append('}');
        return OkRaw(sb.ToString());
    }

    private static string HandleWrite(ISession s, JsonElement args)
    {
        s.Inject(Encoding.UTF8.GetBytes(GetString(args, "text") ?? ""));
        return Ok("written");
    }

    private static string HandleType(ISession s, JsonElement args)
    {
        string text = (GetString(args, "text") ?? "").Replace("\r\n", "\r").Replace('\n', '\r');
        s.Write(Encoding.UTF8.GetBytes(text));
        return Ok("typed");
    }

    /// <summary>Dump the target session's active-pane buffer as plain text (trailing blank lines trimmed).</summary>
    private static string HandleText(ISession s)
    {
        var sb = new StringBuilder();
        lock (s.SyncRoot)
        {
            var em = s.Emulator;
            for (int r = 0; r < em.Screen.Rows; r++) sb.Append(em.DumpRow(r)).Append('\n');
        }
        return Ok(sb.ToString().TrimEnd('\n'));
    }

    private static string HandleStatus(ISession s, JsonElement args)
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
        bool blink = GetBool(args, "blink");
        bool autoReset = GetBool(args, "auto-reset");
        // `sound` may be a bool (play the default alert) or a string (a system-sound name / .wav path).
        bool sound = false;
        string? soundName = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("sound", out var sv))
        {
            if (sv.ValueKind == JsonValueKind.True) sound = true;
            else if (sv.ValueKind == JsonValueKind.String)
            {
                string raw = sv.GetString() ?? "";
                if (raw is "false" or "off" or "no" or "0") { sound = false; }
                else { sound = true; if (raw is not ("" or "true" or "yes" or "1" or "default")) soundName = raw; }
            }
        }
        s.SetStatus(status, blink, autoReset, sound, soundName);
        return Ok(status.ToString().ToLowerInvariant());
    }

    private static string HandleImageShow(ISession s, JsonElement args)
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

    private static string HandleImageSixel(ISession s, JsonElement args)
    {
        string? path = GetString(args, "path");
        if (path is null || !File.Exists(path)) return Err("sixel file not found: " + (path ?? "<null>"));
        int row = GetInt(args, "row", -1), col = GetInt(args, "col", -1);
        // Inject straight into the emulator's parser — ConPTY strips DCS through the shell, so sixel is
        // delivered out-of-band here (same as Kitty images). Optional CUP positions it first.
        if (row >= 0 && col >= 0) s.Inject(Encoding.ASCII.GetBytes($"\x1b[{row + 1};{col + 1}H"));
        s.Inject(File.ReadAllBytes(path));
        return Ok("shown");
    }

    private static string HandleImageClear(ISession s)
    {
        s.Inject(Encoding.ASCII.GetBytes("\x1b_Ga=d\x1b\\"));
        return Ok("cleared");
    }

    private string HandleImageFrame(ISession s, JsonElement args)
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

    private static bool GetBool(JsonElement args, string key)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v)
           && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() is "true" or "1"));

    /// <summary>App version for `ping` — the entry assembly's informational version (stamped by the
    /// release build scripts via -p:Version; "1.0.0" in unstamped dev builds), without metadata.</summary>
    private static string AppVersion()
    {
        string v = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "dev";
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    private static string Ok(string result) => $"{{\"ok\":true,\"result\":{JsonSerializer.Serialize(result)}}}";
    private static string OkRaw(string rawResult) => $"{{\"ok\":true,\"result\":{rawResult}}}";
    private static string Err(string error) => $"{{\"ok\":false,\"error\":{JsonSerializer.Serialize(error)}}}";

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
