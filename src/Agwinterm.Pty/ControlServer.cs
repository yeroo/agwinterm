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

    private enum FrameSource { File, SharedMemory }
    private sealed record FrameCacheEntry(FrameSource Source, string Identity, long Token, KittyImage Image);

    /// <summary>Largest composition accepted by one <c>image.frameshm</c> request.</summary>
    internal const int MaxSharedFrameImages = 64;

    /// <summary>Largest aggregate pixel copy staged by one <c>image.frameshm</c> request.</summary>
    internal const long MaxSharedFrameRequestBytes = ShmFrameReader.MaxFrameBytes;

    /// <summary>Most shared-frame phase-1 copies allowed to run at once.</summary>
    internal const int MaxConcurrentSharedFrameRequests = 2;

    /// <summary>Largest number of images a shared-frame commit may leave retained in a session.</summary>
    internal const int MaxRetainedSharedFrameImages = 256;

    /// <summary>Largest aggregate source-pixel payload a shared-frame commit may leave retained.</summary>
    internal const long MaxRetainedSharedFrameBytes = ShmFrameReader.MaxFrameBytes;

    // Named-pipe clients run concurrently. Bounding the number of phase-1 readers makes the
    // per-request byte limit a process-wide bound too, rather than letting an arbitrary number of
    // individually valid requests allocate their full allowance at the same time. Two preserves
    // independent clients and the cache-race checks while putting a finite ceiling on staging.
    private readonly SemaphoreSlim _sharedFrameReaders = new(
        initialCount: MaxConcurrentSharedFrameRequests,
        maxCount: MaxConcurrentSharedFrameRequests);
    private readonly long _sharedFrameRequestByteLimit = MaxSharedFrameRequestBytes;
    private readonly long _retainedSharedFrameByteLimit = MaxRetainedSharedFrameBytes;
    private readonly int _retainedSharedFrameImageLimit = MaxRetainedSharedFrameImages;

    /// <summary>Test scheduling seam: invoked after phase 1 and before the render lock.</summary>
    internal Action? SharedFramePrepared { get; set; }

    // One cache covers both frame transports. The source and identity prevent unrelated file
    // signatures or producer sequences from aliasing, while the image reference detects
    // replacement through image.show or terminal Kitty output before a nominal hit is trusted.
    private readonly ConditionalWeakTable<ISession, Dictionary<int, FrameCacheEntry>> _frameState = new();

    // Cache entries may be discarded when another image path replaces their image reference, but
    // accepted-sequence ordering must survive that invalidation. Keep the newest positive shared
    // sequence independently, one current mapping identity per image id.
    private readonly ConditionalWeakTable<ISession, Dictionary<int, (string Identity, long Token)>>
        _acceptedSharedFrameSequences = new();

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

    /// <summary>Test seam for exercising the aggregate staging limit without allocating it.</summary>
    internal ControlServer(ISession session, long sharedFrameRequestByteLimit, string pipeName = "agwinterm")
        : this(new SingleSessionHost(session), pipeName)
    {
        if (sharedFrameRequestByteLimit < 0 || sharedFrameRequestByteLimit > MaxSharedFrameRequestBytes)
            throw new ArgumentOutOfRangeException(nameof(sharedFrameRequestByteLimit));
        _sharedFrameRequestByteLimit = sharedFrameRequestByteLimit;
    }

    /// <summary>Test seam for exercising retained-image limits without large allocations.</summary>
    internal ControlServer(
        ISession session,
        long sharedFrameRequestByteLimit,
        long retainedSharedFrameByteLimit,
        int retainedSharedFrameImageLimit,
        string pipeName = "agwinterm")
        : this(session, sharedFrameRequestByteLimit, pipeName)
    {
        if (retainedSharedFrameByteLimit < 0 || retainedSharedFrameByteLimit > MaxRetainedSharedFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(retainedSharedFrameByteLimit));
        if (retainedSharedFrameImageLimit < 0 || retainedSharedFrameImageLimit > MaxRetainedSharedFrameImages)
            throw new ArgumentOutOfRangeException(nameof(retainedSharedFrameImageLimit));
        _retainedSharedFrameByteLimit = retainedSharedFrameByteLimit;
        _retainedSharedFrameImageLimit = retainedSharedFrameImageLimit;
    }

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
                case "session.new":
                    return Ok(host.NewSession(GetString(args, "name"), GetString(args, "cwd"), GetString(args, "workspace"),
                    GetString(args, "command"), GetString(args, "workspace-name"), GetBool(args, "create-workspace"), GetString(args, "profile"), GetBool(args, "no-select"), GetBool(args, "wait")));
                case "session.duplicate": return Ok(host.DuplicateSession(target));
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
                case "workspace.collapse": return host.WorkspaceCollapse(target, expand: false) ? Ok("collapsed") : Err("workspace not found");
                case "workspace.expand": return host.WorkspaceCollapse(target, expand: true) ? Ok("expanded") : Err("workspace not found");
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
                case "session.restore":
                    return host.SessionRestore(target, GetString(args, "command") ?? "") ? Ok("pinned") : Err("session not found");
                // OkRaw, not Ok: Events() already returns JSON. Ok() would serialize it AGAIN, so
                // .result arrived as a STRING of JSON and a caller had to parse it a second time —
                // while tree and window.list, built the same way, return objects. The conformance
                // contract caught it as a divergence from agliteterm, which had it right.
                case "events": return OkRaw(host.Events(GetInt(args, "since", 0), GetInt(args, "limit", 0)));
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
                "session.metrics" => HandleSessionMetrics(host, s, target),
                "image.show" => HandleImageShow(s, args),
                "image.sixel" => HandleImageSixel(s, args),
                "image.clear" => HandleImageClear(s),
                "image.frame" => HandleImageFrame(s, args),
                "image.frameshm" => HandleImageFrameShm(s, args),
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

    /// <summary>
    /// session.metrics: the pane's live cell size and pixel box, for a producer that has to size a
    /// frame buffer to the pane (image.frameshm's consumer sizes every frame from this).
    ///
    /// OkRaw, not Ok — an object like tree and window.state, camelCase like window.state's
    /// sidebarVisible. Pixel fields are DEVICE pixels: the frame the producer hands over is device
    /// pixels, and a DIP cell size would come out short by the DPI scale on any non-96 monitor,
    /// which does not look like a units bug — it looks like a blurry pane.
    ///
    /// A host that cannot measure (headless, or a target with no pane in the layout) answers zeros
    /// rather than an error: the consumer reads a zero cell size as "no metrics" and falls back,
    /// where ok:false would read as a broken terminal. cols/rows still come from the session, which
    /// every host knows.
    /// </summary>
    private static string HandleSessionMetrics(ISessionHost host, ISession s, string? target)
    {
        var m = host.PaneMetrics(target);
        int cols = m is { Cols: > 0 } ? m.Cols : s.Cols;
        int rows = m is { Rows: > 0 } ? m.Rows : s.Rows;
        var sb = new StringBuilder("{\"cols\":").Append(cols)
            .Append(",\"rows\":").Append(rows)
            .Append(",\"cellWidth\":").Append(Math.Max(0, m?.CellWidth ?? 0))
            .Append(",\"cellHeight\":").Append(Math.Max(0, m?.CellHeight ?? 0))
            .Append(",\"widthPx\":").Append(Math.Max(0, m?.WidthPx ?? 0))
            .Append(",\"heightPx\":").Append(Math.Max(0, m?.HeightPx ?? 0))
            .Append('}');
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
        => HandleImageFrame(s, args, retryInvalidCache: true);

    private string HandleImageFrame(ISession s, JsonElement args, bool retryInvalidCache)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return Err("image.frame requires args.images array");

        var state = _frameState.GetOrCreateValue(s);
        // Phase 1 (OFF the render lock): resolve placements and read+own the pixel bytes for any
        // image whose content changed. The expensive file read stays out of the lock, and we pass
        // raw PNG bytes straight to the emulator — no base64 encode here and no base64 decode under
        // the lock (the renderer decodes PNG asynchronously on its own thread).
        var ops = new List<(int id, int row, int col, int cols, int rows, int sx, int sy, int sw, int sh,
                            string identity, long token, byte[]? data, FrameCacheEntry? cached)>();
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
            FrameCacheEntry? cacheEntry = null;
            bool cached = sig != 0 && IsCurrentFrameCacheHit(
                s, state, id, FrameSource.File, path, sig, out cacheEntry);

            byte[]? data = null;
            if (!cached)
            {
                try { data = File.ReadAllBytes(path); } catch { data = null; }
                if (data is not null) { transmits++; readBytes += data.Length; }
            }
            ops.Add((id, row, col, cols, rows, sx, sy, sw, sh, path, sig, data, cacheEntry));
            count++;
        }

        // Phase 2 (BRIEF lock): swap placements and register any new pixels — dictionary/list
        // updates only, microseconds, so a big image appearing never stalls the paint thread.
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool invalidCache = false;
        s.MutateLocked(em =>
        {
            // A child can replace an id between phase 1's cache check and this lock. Abort before
            // clearing placements; the one automatic retry below will transmit the pixels again.
            foreach (var op in ops.Where(op => op.data is null && op.cached is not null))
                if (!em.Images.TryGetValue(op.id, out var image) || !ReferenceEquals(image, op.cached!.Image))
                { invalidCache = true; return; }

            em.ClearPlacements();
            var updates = new List<(int id, FrameCacheEntry entry)>();
            foreach (var op in ops)
            {
                if (op.data is not null)
                {
                    em.SetImageData(op.id, KittyFormat.Png, 0, 0, op.data);
                    if (op.token != 0 && em.Images.TryGetValue(op.id, out var image))
                        updates.Add((op.id, new FrameCacheEntry(FrameSource.File, op.identity, op.token, image)));
                }
                else if (!em.HasImage(op.id))
                    continue; // never transmitted and no cached copy -> nothing to place
                em.PlaceImage(op.id, op.row, op.col, op.cols, op.rows, op.sx, op.sy, op.sw, op.sh);
            }
            lock (state)
                foreach (var update in updates) state[update.id] = update.entry;
        });
        if (invalidCache)
        {
            RemoveInvalidCacheEntries(state, ops.Select(op => (op.id, op.cached)));
            return retryInvalidCache
                ? HandleImageFrame(s, args, retryInvalidCache: false)
                : Err("image.frame cache changed while the frame was prepared; retry");
        }
        if (_perfLog is not null)
            Perf($"frame images={count} transmits={transmits} readKB={readBytes / 1024} lockMs={System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F2}");
        return Ok($"frame:{count}/{transmits}");
    }

    /// <summary>
    /// Shared-memory sibling of <see cref="HandleImageFrame"/>. Pixels arrive through a producer's
    /// named mapping instead of a file, so a browser-rate producer pays one memcpy per frame rather
    /// than a PNG encode plus a disk round-trip. The structure is deliberately identical: phase 1
    /// validates the args and copies every frame out of the mapping <b>off</b> the render lock,
    /// phase 2 takes a brief lock for the placement swap only.
    ///
    /// Everything in the args comes from another process, so nothing is trusted and nothing throws:
    /// a bad name, a dead producer, a slot that overruns the view all answer <c>{"ok":false,...}</c>
    /// with the session left exactly as it was, because a rejected frame must not half-apply.
    /// See <c>docs/specs/image-frameshm.md</c> for the normative contract.
    /// </summary>
    private string HandleImageFrameShm(ISession s, JsonElement args)
    {
        _sharedFrameReaders.Wait();
        try { return HandleImageFrameShm(s, args, retryInvalidCache: true); }
        finally { _sharedFrameReaders.Release(); }
    }

    private string HandleImageFrameShm(ISession s, JsonElement args, bool retryInvalidCache)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return Err("image.frameshm requires args.images array");
        if (images.GetArrayLength() > MaxSharedFrameImages)
            return Err($"image.frameshm accepts at most {MaxSharedFrameImages} images per request");

        var state = _frameState.GetOrCreateValue(s);
        var acceptedSequences = _acceptedSharedFrameSequences.GetOrCreateValue(s);

        // Phase 1 (OFF the render lock): validate, then copy each changed frame out of its mapping.
        // Nothing is applied yet - a failure anywhere below returns before phase 2 runs, so a
        // rejected request never leaves the pane holding half a frame.
        var ops = new List<(int id, int row, int col, int cols, int rows, int sx, int sy, int sw, int sh,
                            string identity, long token, ShmFrame? frame, FrameCacheEntry? cached)>();
        int count = 0, transmits = 0;
        long readBytes = 0;
        foreach (var img in images.EnumerateArray())
        {
            if (img.ValueKind != JsonValueKind.Object)
                return Err("image.frameshm: each entry of images must be an object");

            string? name = GetString(img, "name");
            if (name is null) return Err("image.frameshm: each image requires a string 'name'");

            string? bad = null;
            if (!TryNum(img, "id", count + 1, out int id, ref bad) ||
                !TryNum(img, "slot", 0, out int slot, ref bad) ||
                !TryNum(img, "seq", 0, out long seq, ref bad) ||
                !TryNum(img, "width", 0, out int width, ref bad) ||
                !TryNum(img, "height", 0, out int height, ref bad) ||
                !TryNum(img, "stride", 0, out int stride, ref bad) ||
                !TryNum(img, "format", 0, out int format, ref bad) ||
                !TryNum(img, "row", 0, out int row, ref bad) ||
                !TryNum(img, "col", 0, out int col, ref bad) ||
                !TryNum(img, "cols", 0, out int cols, ref bad) ||
                !TryNum(img, "rows", 0, out int rows, ref bad) ||
                !TryNum(img, "sx", 0, out int sx, ref bad) ||
                !TryNum(img, "sy", 0, out int sy, ref bad) ||
                !TryNum(img, "sw", 0, out int sw, ref bad) ||
                !TryNum(img, "sh", 0, out int sh, ref bad))
                return Err("image.frameshm: " + bad);

            // The (id, name, seq) cache, the shm analogue of image.frame's content signature: a
            // repeated sequence from the same producer means "nothing changed, just re-place it".
            // Cache hits still validate the live mapping and header below; only the pixel copy is
            // skipped. Seq 0 means "read whatever is in the slot" and is never cacheable.
            FrameCacheEntry? cacheEntry = null;
            bool cached = seq > 0 && IsCurrentFrameCacheHit(
                s, state, id, FrameSource.SharedMemory, name, seq, out cacheEntry);

            ShmFrame? frame = null;
            var request = new ShmFrameRequest(name, slot, seq, width, height, stride, format);
            if (cached)
            {
                if (!ShmFrameReader.TryValidateFrame(request, out var error))
                    return Err("image.frameshm: " + ShmFrameReader.Describe(error));
            }
            else
            {
                long remainingBytes = _sharedFrameRequestByteLimit - readBytes;
                if (!ShmFrameReader.TryReadFrame(request, remainingBytes, out frame, out var error))
                    return Err("image.frameshm: " + ShmFrameReader.Describe(error));
                transmits++;
                readBytes += frame.Pixels.Length;
            }
            ops.Add((id, row, col, cols, rows, sx, sy, sw, sh, name, seq, frame, cacheEntry));
            count++;
        }

        SharedFramePrepared?.Invoke();

        // Phase 2 (BRIEF lock): dictionary/list swaps only, exactly as image.frame does - the
        // megabytes were already copied above, so the paint thread stalls for microseconds.
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool invalidCache = false;
        string? commitError = null;
        s.MutateLocked(em =>
        {
            // Two pipe connections may finish their off-lock copies in either order. Once a newer
            // positive sequence for this id and mapping has committed, an older request must not
            // replace it. Check under the same session/cache lock used for the eventual update.
            lock (acceptedSequences)
            {
                var requestLatest = new Dictionary<(int id, string identity), long>();
                foreach (var op in ops.Where(op => op.token > 0))
                {
                    var key = (op.id, op.identity);
                    long latest = requestLatest.GetValueOrDefault(key);
                    if (latest == 0 && acceptedSequences.TryGetValue(op.id, out var accepted) &&
                        accepted.Identity == op.identity)
                        latest = accepted.Token;
                    if (op.token < latest)
                    {
                        commitError = $"image.frameshm: sequence {op.token} for id {op.id} was superseded by {latest}";
                        return;
                    }
                    requestLatest[key] = op.token;
                }
            }

            foreach (var op in ops.Where(op => op.frame is null && op.cached is not null))
                if (!em.Images.TryGetValue(op.id, out var image) || !ReferenceEquals(image, op.cached!.Image))
                { invalidCache = true; return; }

            if (!FitsRetainedSharedFrameBudget(
                em,
                ops.Where(op => op.frame is not null)
                    .Select(op => (op.id, bytes: op.frame!.Pixels.LongLength)),
                out commitError))
                return;

            em.ClearPlacements();
            var updates = new List<(int id, FrameCacheEntry entry)>();
            foreach (var op in ops)
            {
                if (op.frame is not null)
                {
                    em.SetImageData(op.id, (KittyFormat)op.frame.Format, op.frame.Width, op.frame.Height, op.frame.Pixels);
                    if (op.token > 0 && em.Images.TryGetValue(op.id, out var image))
                        updates.Add((op.id, new FrameCacheEntry(
                            FrameSource.SharedMemory, op.identity, op.token, image)));
                }
                else if (!em.HasImage(op.id))
                    continue; // cache said "unchanged" but nothing was ever transmitted -> nothing to place
                em.PlaceImage(op.id, op.row, op.col, op.cols, op.rows, op.sx, op.sy, op.sw, op.sh);
            }
            // Commit cache state only after the whole all-or-nothing frame has validated and applied.
            lock (state)
                foreach (var update in updates) state[update.id] = update.entry;
            lock (acceptedSequences)
                foreach (var op in ops.Where(op => op.token > 0))
                    acceptedSequences[op.id] = (op.identity, op.token);
        });
        if (invalidCache)
        {
            RemoveInvalidCacheEntries(state, ops.Select(op => (op.id, op.cached)));
            return retryInvalidCache
                ? HandleImageFrameShm(s, args, retryInvalidCache: false)
                : Err("image.frameshm cache changed while the frame was prepared; retry");
        }
        if (commitError is not null) return Err(commitError);
        if (_perfLog is not null)
            Perf($"frameshm images={count} transmits={transmits} readKB={readBytes / 1024} lockMs={System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F2}");
        return Ok($"frame:{count}/{transmits}");
    }

    private bool FitsRetainedSharedFrameBudget(
        ITerminalCore em,
        IEnumerable<(int id, long bytes)> replacements,
        out string? error)
    {
        error = null;
        var finalReplacements = new Dictionary<int, long>();
        foreach (var replacement in replacements)
            finalReplacements[replacement.id] = replacement.bytes;
        if (finalReplacements.Count == 0) return true;

        int currentCount = em.Images.Count;
        long currentBytes = 0;
        foreach (var image in em.Images.Values)
            currentBytes = checked(currentBytes + image.Data.LongLength);

        int projectedCount = currentCount;
        long projectedBytes = currentBytes;
        foreach (var (id, bytes) in finalReplacements)
        {
            if (em.Images.TryGetValue(id, out var existing))
                projectedBytes -= existing.Data.LongLength;
            else
                projectedCount++;
            projectedBytes = checked(projectedBytes + bytes);
        }

        // A session already over a limit through another image path may still replace an existing
        // frame without making the situation worse. Shared frames may never increase the excess.
        if (projectedCount > _retainedSharedFrameImageLimit && projectedCount > currentCount)
        {
            error = $"image.frameshm: retained image limit of {_retainedSharedFrameImageLimit} would be exceeded";
            return false;
        }
        if (projectedBytes > _retainedSharedFrameByteLimit && projectedBytes > currentBytes)
        {
            error = $"image.frameshm: retained pixel budget of {_retainedSharedFrameByteLimit} bytes would be exceeded";
            return false;
        }
        return true;
    }

    private static bool IsCurrentFrameCacheHit(
        ISession session,
        Dictionary<int, FrameCacheEntry> state,
        int id,
        FrameSource source,
        string identity,
        long token,
        out FrameCacheEntry? entry)
    {
        FrameCacheEntry candidate;
        lock (state)
        {
            if (!state.TryGetValue(id, out var cached) || cached.Source != source ||
                cached.Identity != identity || cached.Token != token)
            { entry = null; return false; }
            candidate = cached;
        }
        entry = candidate;

        bool current = false;
        session.MutateLocked(em =>
            current = em.Images.TryGetValue(id, out var image) && ReferenceEquals(image, candidate.Image));
        if (current) return true;

        lock (state)
            if (state.TryGetValue(id, out var found) && ReferenceEquals(found, candidate)) state.Remove(id);
        entry = null;
        return false;
    }

    private static void RemoveInvalidCacheEntries(
        Dictionary<int, FrameCacheEntry> state,
        IEnumerable<(int id, FrameCacheEntry? entry)> candidates)
    {
        lock (state)
            foreach (var (id, entry) in candidates)
                if (entry is not null && state.TryGetValue(id, out var found) && ReferenceEquals(found, entry))
                    state.Remove(id);
    }

    /// <summary>
    /// Reads an optional integer arg, rejecting a non-number rather than letting
    /// <see cref="JsonElement.TryGetInt64"/> throw "requires an element of type 'Number'" from
    /// somewhere the caller cannot tell which field was wrong. A missing key takes
    /// <paramref name="def"/>; a string, a float or an out-of-range value sets
    /// <paramref name="error"/> and returns false.
    /// </summary>
    private static bool TryNum(JsonElement el, string key, long def, out long value, ref string? error)
    {
        value = def;
        if (!el.TryGetProperty(key, out var v)) return true;
        if (v.ValueKind != JsonValueKind.Number)
        { error = $"'{key}' must be a JSON number, not {v.ValueKind.ToString().ToLowerInvariant()}"; return false; }
        if (!v.TryGetInt64(out long n)) { error = $"'{key}' must be a whole number"; return false; }
        value = n;
        return true;
    }

    /// <summary>32-bit <see cref="TryNum(JsonElement, string, long, out long, ref string?)"/>.</summary>
    private static bool TryNum(JsonElement el, string key, int def, out int value, ref string? error)
    {
        value = def;
        if (!TryNum(el, key, (long)def, out long n, ref error)) return false;
        if (n < int.MinValue || n > int.MaxValue) { error = $"'{key}' is out of range for a 32-bit integer"; return false; }
        value = (int)n;
        return true;
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
