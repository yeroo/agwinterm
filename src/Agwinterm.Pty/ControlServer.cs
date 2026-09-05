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
    private readonly record struct SharedFrameCommitState(
        string Identity, long LatestPositiveSequence, long Generation, bool Sequenced);

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
    // accepted-sequence ordering must survive that invalidation. Request generation orders mapping
    // switches and any pair involving the unsequenced seq-zero form; positive-only pairs use the
    // producer's sequence. Unlike retaining every historical name, this remains bounded per id.
    private readonly ConditionalWeakTable<ISession, Dictionary<int, SharedFrameCommitState>>
        _acceptedSharedFrameSequences = new();
    private long _sharedFrameRequestGeneration;

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
                {
                    // An unknown workspace is REFUSED, not swapped for the active one (P2, decision 1;
                    // SessionNewWorkspaces has the why). The host resolves the workspace before it
                    // mints an id, so a refusal leaves no session behind it; the pair of flags is
                    // refused here, before the host is asked, because the host cannot see both were
                    // given once one has won.
                    // `caller` is the pane that ran the command (the CLI sends its AGWINTERM_SESSION_ID);
                    // with no workspace named, the session lands in THAT pane's workspace, and only a
                    // missing or stale caller falls back to the active one (P2, task 5a). It is a
                    // separate arg and not the target on purpose: session.new is dispatched here, in
                    // the targetless block, and a target would move it into the resolved-session block
                    // below, where an unknown value is "session not found" rather than a fallback.
                    string? workspace = GetString(args, "workspace"), workspaceName = GetString(args, "workspace-name");
                    if (!string.IsNullOrEmpty(workspace) && !string.IsNullOrEmpty(workspaceName))
                        return Err(SessionNewWorkspaces.TwoSources(workspace, workspaceName));
                    string created = host.NewSession(GetString(args, "name"), GetString(args, "cwd"), workspace,
                        GetString(args, "command"), workspaceName, GetBool(args, "create-workspace"), GetString(args, "profile"), GetBool(args, "no-select"), GetBool(args, "wait"),
                        caller: GetString(args, "caller"));
                    return created.StartsWith(ISessionHost.RefusePrefix, StringComparison.Ordinal)
                        ? Err(created[ISessionHost.RefusePrefix.Length..])
                        : Ok(created);
                }
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
                case "session.context": return HandleSessionContext(host, target, args);
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
                case "session.split": return HandleSessionSplit(host, target, args);
                // session.split.close (P4): close the targeted pane, either side; the reply is the
                // survivor's id (a string, like every session.split reply). Every refusal is the
                // host's, from SplitCloseReply's wordings, and each closes nothing. No arg to read:
                // the verb takes only a target (null/"active" = the active session's focused pane).
                case "session.split.close": return HostReply(host.SplitClose(target));
                // The default direction is `other` — the one word that names a pane on either axis
                // (P4: `right` would be refused on a horizontal split).
                case "session.focus": return HostReply(host.FocusPaneDir(GetString(args, "dir") ?? "other"));
                case "session.resize":
                    {
                        double? ratio = null;
                        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ratio", out var rv) && rv.TryGetDouble(out var rd)) ratio = rd;
                        return HostReply(host.ResizeSplit(ratio, GetInt(args, "grow-left", 0), GetInt(args, "grow-right", 0),
                            GetInt(args, "grow-top", 0), GetInt(args, "grow-bottom", 0)));
                    }
                case "theme.list": return Ok(string.Join("\n", host.ThemeList()));
                case "theme.set": return host.ThemeSet(GetString(args, "name") ?? "") ? Ok("theme set") : Err("theme not found");
                case "keymap.reload": return Ok(host.KeymapReload());
                case "restore.clear": return Ok(host.RestoreClear());
                case "restore.capture": return HandleRestoreCapture(host, target);
                case "config.set": return Ok(host.ConfigSet(GetString(args, "key") ?? "", GetString(args, "value") ?? ""));
                case "config.get": return Ok(host.ConfigGet(GetString(args, "key") ?? ""));
                case "config.list": return Ok(host.ConfigList());
                case "settings.open": return Ok(host.SettingsOpen());
                case "sidebar":
                    {
                        string op = GetString(args, "op") ?? "toggle";
                        if (op is "state" or "get") return Ok(host.SidebarState());   // read-back, no mutation
                        if (op == "width") return HandleSidebarWidth(host, args);
                        // on/off are the conformance file's spelling (control-api.json's `sidebar on`
                        // step). Before P2 they "passed" only because the verb answered ok:true for ANY
                        // op, including ones the host's switch fell straight through; now they are real
                        // aliases, and an op the host cannot do is refused instead of acknowledged.
                        op = op switch { "on" => "show", "off" => "hide", _ => op };
                        if (Array.IndexOf(SidebarOps, op) < 0)
                            return Err($"sidebar: unknown op '{op}'. One of: show|hide|toggle|expand|collapse|state|width|mode tree|mode flagged|mode toggle (on/off = show/hide). Nothing changed.");
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
                {
                    // A refusal has to answer ok:false, or a caller that checks `ok` reads "I would
                    // not do that" as "done" — the exact dishonesty the refusal exists to end. The
                    // host marks one by prefixing REFUSE_PREFIX; everything else is a result string.
                    // size-percent is validated, not clamped. Before P2, `0`, `-5`, `150` and the
                    // string "sixty" (0 from GetInt) all opened a full-screen overlay and answered
                    // ok:true — three silent coercions between the shell and the panel.
                    if (!TryOverlaySize(args, out int sizePercent, out string? sizeErr)) return Err(sizeErr!);
                    string ovl = host.SessionOverlay(target, GetString(args, "action") ?? "open",
                        GetString(args, "command"), sizePercent,
                        GetBool(args, "wait"), GetBool(args, "block"));
                    return ovl.StartsWith(ISessionHost.RefusePrefix, StringComparison.Ordinal)
                        ? Err(ovl[ISessionHost.RefusePrefix.Length..])
                        : Ok(ovl);
                }
                case "notify":
                    return host.Notify(target, GetString(args, "title"), GetString(args, "body") ?? "")
                        ? Ok("notified") : Err("session not found");
                case "session.flag":
                    return host.SessionFlag(target, GetString(args, "op") ?? "toggle") ? Ok("flag") : Err("session not found");
                case "session.bind":
                    return host.SessionBind(target, GetString(args, "agent") ?? "claude") ? Ok("bound") : Err("session not found");
                case "session.restore": return HandleSessionRestore(host, target, args);
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
                "session.text" => HandleText(s, args),
                "session.status" => HandleStatus(s, args),
                "session.metrics" => HandleSessionMetrics(host, s, target),
                // surface.cursor — the caret COLUMN as a bare integer (agterm's shape, so a script
                // written against either product reads the same reply; a JSON object here would
                // diverge for no caller we have, and the row is not what "is the composer empty"
                // asks). Targeting follows Resolve, exactly as session.text/session.type do, so the
                // pane you CHECK is the pane you then type into: a pane id reports that pane, and a
                // session NAME reports its focused pane — a cursor is a per-pane thing, and focus is
                // the only non-arbitrary answer for a session-wide target. Note a session's id is
                // also its first pane's id, so an id target reports pane 0 regardless of focus
                // (pre-existing Resolve behaviour, verified in qa/control-read.md).
                "surface.cursor" => OkRaw(s.SnapshotCursor().Col.ToString(System.Globalization.CultureInfo.InvariantCulture)),
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
                  .Append(",\"status\":").Append(JsonSerializer.Serialize(n.Status.ToString().ToLowerInvariant()))
                  // Always emitted, even at 0 (unlike the flags below): a caller distinguishing
                  // "this agent is working" from "its hook died an hour ago" gains nothing from an
                  // absent field, and would have to guess which of the two absence meant.
                  .Append(",\"statusChangedAt\":").Append(n.StatusChangedAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (n.Overlay) sb.Append(",\"overlay\":true");
                if (n.Flagged) sb.Append(",\"flagged\":true");
                if (n.Background) sb.Append(",\"background\":true");
                if (n.Notifications > 0) sb.Append(",\"notifications\":").Append(n.Notifications);
                if (n.StatusBlink) sb.Append(",\"statusBlink\":true");
                if (n.OverlaySize > 0) sb.Append(",\"overlaySize\":").Append(n.OverlaySize);
                // context: the session.context read-back, emitted only when one is set — absent is
                // "none", the same spelling the flags above use for "no" (P3).
                if (n.Context is not null) sb.Append(",\"").Append(SessionContexts.Key).Append("\":").Append(JsonSerializer.Serialize(n.Context));
                AppendPaneMap(sb, "restoreCommands", n.RestoreCommands, n.PaneIds);
                // capturedCommands: the restore.capture read-back, same spelling — emitted only when
                // any pane's slot holds a capture (P3).
                AppendPaneMap(sb, RestoreCaptureReply.TreeKey, n.CapturedCommands, n.PaneIds);
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
                    // axis: ALWAYS while split, like focusedPane — absence would mean "this build has
                    // no axis", which tells a caller nothing (P4). Never for a single pane: the
                    // orientation of a split that does not exist is not a fact about the session.
                    sb.Append(",\"").Append(SplitAxes.Key).Append("\":").Append(JsonSerializer.Serialize(n.Axis ?? SplitAxes.Vertical));
                }
                sb.Append('}');
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        return OkRaw(sb.ToString());
    }

    /// <summary>
    /// A per-pane read-back map — <c>restoreCommands</c> for session.restore, <c>capturedCommands</c>
    /// for restore.capture (P3): an object keyed by PANE id (the id the verb's reply names) listing
    /// only the panes that carry a value. Omitted when no pane does, and a pane with none is simply
    /// absent — the same spelling the flags above use for "no". The snapshot carries one entry per
    /// pane ("" = none), parallel to PaneIds. AgentSkill promised <c>restoreCommands</c> long before
    /// it was on the wire, so a caller reconnecting to a running app had no way to ask which pane
    /// held what; the answer was only ever visible in the instant of the call.
    /// </summary>
    private static void AppendPaneMap(StringBuilder sb, string key, IReadOnlyList<string>? values, IReadOnlyList<string>? paneIds)
    {
        if (values is not { Count: > 0 } cmds || paneIds is not { Count: > 0 } ids) return;
        bool any = false;
        for (int r = 0; r < cmds.Count && r < ids.Count; r++)
        {
            if (string.IsNullOrEmpty(cmds[r])) continue;
            sb.Append(any ? "," : ",\"" + key + "\":{")
              .Append(JsonSerializer.Serialize(ids[r])).Append(':').Append(JsonSerializer.Serialize(cmds[r]));
            any = true;
        }
        if (any) sb.Append('}');
    }

    /// <summary>
    /// restore.capture: capture every real pane's foreground command (or one pane's, with a target)
    /// into its durable restore slot now, and report per pane what was captured (P3). The reply is
    /// <see cref="RestoreCaptureReply"/>'s object, OkRaw; the read-back is <c>tree</c>'s
    /// <c>capturedCommands</c>. Every refusal — an unknown target, a cover pane, a failed process
    /// query, a UI hop that could not run — is the host's, behind <see cref="RestoreCaptureResult.Refusal"/>
    /// or a throw, and each captures nothing for anyone. There is no server-side rule to apply: the
    /// verb takes no value, only a target.
    /// </summary>
    private static string HandleRestoreCapture(ISessionHost host, string? target)
    {
        if (target is not null && target.Length == 0) return Err(RestoreCaptureReply.EmptyTarget);   // present-but-empty is not "everything"
        var result = host.RestoreCapture(target);
        return result.Refusal is not null ? Err(result.Refusal) : OkRaw(RestoreCaptureReply.Build(result));
    }

    /// <summary>
    /// session.context: set or clear a session's one-line context and reply with the value IN EFFECT,
    /// as an object (<c>{session, context}</c>, OkRaw) — the read-back is <c>tree</c>'s <c>context</c>.
    /// Every refusal happens HERE, before the host is reached, through <see cref="SessionContexts"/>
    /// (the one wording the fake and the app share): text beside <c>clear</c> is two sources for one
    /// field; blank, a control character (named with its offset) and over-length are what the
    /// surfaces cannot draw. A refusal leaves the old context in place — the host is never called.
    /// The host's own refusal is "no session", the same condition rename refuses.
    /// </summary>
    /// <summary>
    /// session.split: the reply is the PANE ID the op produced or found (a bare string — the shipped
    /// conformance step on <c>split off</c> is a string type check, and a pane id is a string), see
    /// <see cref="ISessionHost.Split"/> for the per-op rule. <c>axis</c> is read STRICTLY: absent is
    /// "keep the session's orientation", a string must be one of <see cref="SplitAxes"/>' two words,
    /// and a non-string (a number, an object) is refused with the same wording rather than defaulted —
    /// a caller that sent <c>"axis": 1</c> meant something, and a vertical split with ok:true would
    /// be the silent-success class. Every refusal splits nothing.
    /// </summary>
    private static string HandleSessionSplit(ISessionHost host, string? target, JsonElement args)
    {
        string? axis = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(SplitAxes.Key, out var av))
        {
            if (av.ValueKind != JsonValueKind.String) return Err(SplitAxes.Refusal(av.GetRawText()));
            if (!SplitAxes.TryParse(av.GetString(), out axis, out string? axisRefusal)) return Err(axisRefusal!);
        }
        return HostReply(host.Split(target, GetString(args, "op") ?? "toggle", axis));
    }

    /// <summary>A host reply that is either a result string or <see cref="ISessionHost.RefusePrefix"/>
    /// + a refusal, turned into the wire's ok:true / ok:false.</summary>
    private static string HostReply(string reply)
        => reply.StartsWith(ISessionHost.RefusePrefix, StringComparison.Ordinal)
            ? Err(reply[ISessionHost.RefusePrefix.Length..])
            : Ok(reply);

    private static string HandleSessionContext(ISessionHost host, string? target, JsonElement args)
    {
        string? raw = GetString(args, SessionContexts.Key);
        bool clear = GetBool(args, "clear");
        if (clear && raw is not null) return Err(SessionContexts.TextAndClear);
        string? context = null;
        if (!clear)
        {
            if (!SessionContexts.TryNormalize(raw, out string text, out string? refusal)) return Err(refusal!);
            context = text;
        }
        string reply = host.SessionContext(target, context);
        return reply.StartsWith(ISessionHost.RefusePrefix, StringComparison.Ordinal)
            ? Err(reply[ISessionHost.RefusePrefix.Length..])
            : OkRaw(reply);
    }

    /// <summary>
    /// session.restore: pin (or clear) a per-pane restart command, and SAY WHERE IT LANDED. Before P2
    /// the reply was the constant "pinned", the host resolved the target through a path no other verb
    /// used, and the tree never emitted the field the skill file promised — so which pane took the pin
    /// was unknowable from the caller's side. Now the reply is
    /// <c>{action:"pinned"|"cleared", pane, session[, command]}</c>: <c>pane</c> is the pane the target
    /// resolved to (a session NAME lands on that session's focused pane, a session ID on its first pane,
    /// exactly as session.type does), <c>session</c> its owner. ""/"none" clear, and are reported as a
    /// clear rather than a pin of nothing. The target is mandatory: a pin outlives whatever pane
    /// happens to be active now, so "active" is not a sensible default and is refused rather than
    /// guessed. Every refusal pins nothing.
    /// </summary>
    private static string HandleSessionRestore(ISessionHost host, string? target, JsonElement args)
    {
        if (string.IsNullOrEmpty(target) || target == "active")
            return Err("session.restore needs a pane: pass --target <pane-id>. A pin outlives the pane that is active now, so there is no active-pane default (inside a session, AGWINTERM_SESSION_ID is that pane's id). Nothing pinned.");
        string raw = GetString(args, "command") ?? "";
        string? command = string.IsNullOrWhiteSpace(raw) || raw.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : raw;
        var hit = host.SessionRestore(target, command);
        if (hit is null) return Err($"no pane or session matches '{target}'. Nothing pinned.");
        if (hit.Refusal is not null) return Err(hit.Refusal);
        var sb = new StringBuilder("{\"action\":").Append(command is null ? "\"cleared\"" : "\"pinned\"")
            .Append(",\"pane\":").Append(JsonSerializer.Serialize(hit.PaneId))
            .Append(",\"session\":").Append(JsonSerializer.Serialize(hit.SessionId));
        if (command is not null) sb.Append(",\"command\":").Append(JsonSerializer.Serialize(command));
        sb.Append('}');
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

    /// <summary>Type text as if the user had. Newlines become CR (the Enter convention); every other
    /// control byte is REFUSED.
    ///
    /// Why refuse rather than strip: a control byte in typed text reaches the shell's line editor
    /// before anything parses it, and the damage is silent. agterm hardened this in v0.25.0 after a
    /// NUL truncated an injection and the call still answered ok - the shortened line kept its
    /// Return and ran. Stripping produces that same shortened line; refusing tells the caller its
    /// text was not what it thought.
    ///
    /// TAB stays (completion is typing). A caller that genuinely means the control byte - an escape
    /// sequence for a TUI, a lone ^C - passes allow-control and gets exactly what it asked for.
    ///
    /// It does NOT get sent to session.write, whatever an earlier version of this message said:
    /// session.write injects into the emulator and never reaches the shell (ISession.Inject), so as
    /// a way to deliver bytes to a program it does not work at all. Pointing a caller at a verb that
    /// silently cannot do the job is worse than the refusal it was meant to soften.</summary>
    private static string HandleType(ISession s, JsonElement args)
    {
        string text = (GetString(args, "text") ?? "").Replace("\r\n", "\r").Replace('\n', '\r');
        if (!GetBool(args, "allow-control") && FirstControlByte(text) is { } bad)
            return Err($"session.type refuses control byte 0x{(int)bad:X2} at index {text.IndexOf(bad)} " +
                       "(CR, LF and TAB are fine) - pass --allow-control if you mean it");
        s.Write(Encoding.UTF8.GetBytes(text));
        return Ok("typed");
    }

    /// <summary>The first C0/DEL control character that is not CR, LF or TAB, or null.</summary>
    private static char? FirstControlByte(string text)
    {
        foreach (char c in text)
            if ((c < ' ' || c == '\u007f') && c != '\r' && c != '\n' && c != '\t') return c;
        return null;
    }

    /// <summary>Dump the target session's active-pane buffer as plain text (trailing blank lines
    /// trimmed). `lines` reaches back into scrollback: the last N lines ending at the bottom of the
    /// visible screen, so an N larger than the screen height picks up history. Omitted (or 0) keeps
    /// the old meaning exactly - the visible screen.
    ///
    /// Scrollback matters because the interesting part is usually already gone: a launch banner, a
    /// version, an error printed before a full-screen app took the alt screen. An agent reading a
    /// pane it did not watch could reach none of it.</summary>
    private static string HandleText(ISession s, JsonElement args)
    {
        int want = GetInt(args, "lines", 0);
        var sb = new StringBuilder();
        lock (s.SyncRoot)
        {
            var em = s.Emulator;
            int rows = em.Screen.Rows, hist = em.HistoryCount;
            int take = want <= 0 ? rows : Math.Min(want, rows + hist);
            // Absolute numbering: [0, hist) is scrollback, then the live rows.
            for (int abs = hist + rows - take; abs < hist + rows; abs++)
                sb.Append(abs < hist ? em.DumpHistoryRow(abs) : em.DumpRow(abs - hist)).Append('\n');
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
            bool cached = sig != 0 && IsFrameCacheHit(
                state, id, FrameSource.File, path, sig, out cacheEntry);

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
        HashSet<int>? staleIds = null;
        s.MutateLocked(em =>
        {
            // Phase 1 trusted the cache dictionary alone; this is the only check that the emulator
            // still holds the cached image. A child can replace an id at any time, so abort before
            // clearing placements; the one automatic retry below will transmit the pixels again.
            // Every stale id is collected, not just the first: a live sibling keeps its cache hit,
            // so the retry (a full re-run of phase 1) re-reads only the stale ids plus whatever
            // already missed the cache on this attempt.
            foreach (var op in ops.Where(op => op.data is null && op.cached is not null))
                if (!em.Images.TryGetValue(op.id, out var image) || !ReferenceEquals(image, op.cached!.Image))
                    (staleIds ??= new()).Add(op.id);
            if (staleIds is not null) return;

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
        if (staleIds is not null)
        {
            RemoveInvalidCacheEntries(state, ops.Where(op => staleIds.Contains(op.id)).Select(op => (op.id, op.cached)));
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
        long requestGeneration = Interlocked.Increment(ref _sharedFrameRequestGeneration);
        _sharedFrameReaders.Wait();
        try { return HandleImageFrameShm(s, args, retryInvalidCache: true, requestGeneration); }
        finally { _sharedFrameReaders.Release(); }
    }

    private string HandleImageFrameShm(
        ISession s, JsonElement args, bool retryInvalidCache, long requestGeneration)
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
            // skipped, and phase 2 confirms the emulator still holds the image. Seq 0 means "read
            // whatever is in the slot" and is never cacheable.
            FrameCacheEntry? cacheEntry = null;
            bool cached = seq > 0 && IsFrameCacheHit(
                state, id, FrameSource.SharedMemory, name, seq, out cacheEntry);

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
        HashSet<int>? staleIds = null;
        string? commitError = null;
        s.MutateLocked(em =>
        {
            // Two pipe connections may finish their off-lock copies in either order. Once a newer
            // positive sequence for this id and mapping has committed, an older positive request
            // must not replace it. Sequence zero has no producer ordering token, so generation
            // orders any pair involving that uncached form as well as mapping switches. Check under
            // the same session/cache lock used for the update.
            lock (acceptedSequences)
            {
                var requestLatest = new Dictionary<(int id, string identity), long>();
                foreach (var op in ops)
                {
                    var key = (op.id, op.identity);
                    long latest = requestLatest.GetValueOrDefault(key);
                    if (acceptedSequences.TryGetValue(op.id, out var accepted))
                    {
                        if (accepted.Identity == op.identity)
                        {
                            if ((op.token == 0 || !accepted.Sequenced) &&
                                requestGeneration < accepted.Generation)
                            {
                                commitError = $"image.frameshm: request for id {op.id} was superseded by a newer request";
                                return;
                            }
                            if (op.token > 0 && latest == 0)
                                latest = accepted.LatestPositiveSequence;
                        }
                        else if (requestGeneration < accepted.Generation)
                        {
                            commitError = $"image.frameshm: request for id {op.id} was superseded by a newer mapping";
                            return;
                        }
                    }
                    if (op.token > 0 && op.token < latest)
                    {
                        commitError = $"image.frameshm: sequence {op.token} for id {op.id} was superseded by {latest}";
                        return;
                    }
                    if (op.token > 0)
                        requestLatest[key] = op.token;
                }
            }

            // The only liveness check for a cache hit (phase 1 read the dictionary alone): the
            // emulator must still hold the very image the entry was recorded against. All stale
            // ids are collected so a live sibling keeps its cache hit: the retry re-runs phase 1
            // and copies only the stale ids plus whatever already missed the cache this attempt.
            foreach (var op in ops.Where(op => op.frame is null && op.cached is not null))
                if (!em.Images.TryGetValue(op.id, out var image) || !ReferenceEquals(image, op.cached!.Image))
                    (staleIds ??= new()).Add(op.id);
            if (staleIds is not null) return;

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
                foreach (var op in ops)
                {
                    long generation = requestGeneration;
                    long latestPositiveSequence = op.token;
                    if (acceptedSequences.TryGetValue(op.id, out var accepted) &&
                        accepted.Identity == op.identity)
                    {
                        generation = Math.Max(generation, accepted.Generation);
                        latestPositiveSequence = Math.Max(
                            latestPositiveSequence, accepted.LatestPositiveSequence);
                    }
                    acceptedSequences[op.id] = new SharedFrameCommitState(
                        op.identity, latestPositiveSequence, generation, Sequenced: op.token > 0);
                }
        });
        if (staleIds is not null)
        {
            RemoveInvalidCacheEntries(state, ops.Where(op => staleIds.Contains(op.id)).Select(op => (op.id, op.cached)));
            return retryInvalidCache
                ? HandleImageFrameShm(s, args, retryInvalidCache: false, requestGeneration)
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

    /// <summary>
    /// Phase-1 cache lookup: does the pane already hold this exact content under this id? Answers
    /// from the cache dictionary alone and never takes the render lock — whether the emulator still
    /// holds the cached image is settled in phase 2, under the one lock that also applies the frame
    /// (a stale entry there aborts, is dropped, and the request is retried once with the pixels
    /// re-read). Probing here as well would cost one lock round-trip per image per frame for a
    /// check phase 2 has to repeat anyway.
    /// </summary>
    private static bool IsFrameCacheHit(
        Dictionary<int, FrameCacheEntry> state,
        int id,
        FrameSource source,
        string identity,
        long token,
        out FrameCacheEntry? entry)
    {
        lock (state)
        {
            if (!state.TryGetValue(id, out var cached) || cached.Source != source ||
                cached.Identity != identity || cached.Token != token)
            { entry = null; return false; }
            entry = cached;
            return true;
        }
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

    /// <summary>
    /// The strict reader for session.overlay's <c>size-percent</c>. Three cases, none folded into
    /// another: <b>absent</b> keeps today's meaning (0 = the full content region); <b>present and
    /// 1..100</b> is the centered panel size; <b>present and anything else</b> — 0, negative, above
    /// 100, a float, a string — is refused with the value and the range named. A caller who wrote
    /// <c>--size-percent 0</c> meaning "full" is told that omitting the flag is how to ask for that,
    /// because the refusal is the only place they will read it.
    /// </summary>
    internal static bool TryOverlaySize(JsonElement args, out int sizePercent, out string? error)
    {
        sizePercent = 0; error = null;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(OverlaySizeKey, out var v)) return true;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) && n >= 1 && n <= 100)
        { sizePercent = (int)n; return true; }
        error = $"{OverlaySizeKey} {v.GetRawText()} is not a whole number in 1..100; " +
                $"omit --{OverlaySizeKey} to use the full content region";
        return false;
    }
    internal const string OverlaySizeKey = "size-percent";

    /// <summary>Every op the hosts' SidebarOp switch handles (on/off are folded into show/hide before
    /// this is consulted). Anything else is refused; before P2 it was acknowledged and ignored.</summary>
    private static readonly string[] SidebarOps =
        { "show", "hide", "toggle", "expand", "collapse", "mode:tree", "mode:flagged", "mode:toggle" };

    /// <summary>
    /// sidebar.width: read the sidebar width, or set it and report the width ACTUALLY IN EFFECT
    /// afterwards. The reply is an object, not the word "sidebar":
    /// <c>{width, visible[, applied[, note]]}</c>. <c>width</c> is the sidebar's width in DIP — on
    /// screen when <c>visible</c>, otherwise the width the next show will use. On a set,
    /// <c>applied</c> says whether the divider moved now; a set while the sidebar is hidden is
    /// remembered (and persisted) but not applied, and <c>note</c> says so rather than reporting a
    /// width the user cannot see. A caller compares what it asked for with <c>width</c>: a
    /// legitimate difference (a host with a minimum of its own) is visible without pretending the
    /// number was honoured. Out of range is <b>refused</b> with the range named, the same rule
    /// --size-percent has, so the API has one answer to "out of range" rather than two; a refusal
    /// changes nothing.
    /// </summary>
    private static string HandleSidebarWidth(ISessionHost host, JsonElement args)
    {
        if (!TrySidebarWidth(args, out int? set, out string? err)) return Err(err!);
        var snap = host.SidebarWidth(set);
        var sb = new StringBuilder("{\"width\":").Append(snap.Width)
            .Append(",\"visible\":").Append(snap.Visible ? "true" : "false");
        if (set is not null)
        {
            sb.Append(",\"applied\":").Append(snap.Visible ? "true" : "false");
            if (!snap.Visible)
                sb.Append(",\"note\":\"sidebar is hidden: width remembered, not applied; it takes effect on the next `sidebar show`\"");
        }
        return OkRaw(sb.Append('}').ToString());
    }

    /// <summary>
    /// The strict reader for sidebar.width's <c>width</c>. Three cases, none folded into another:
    /// <b>absent</b> is a read; <b>present and <see cref="SidebarWidths.Min"/>..<see cref="SidebarWidths.Max"/></b>
    /// is a set; <b>present and anything else</b> — 0, negative, too wide, a float, a string — is
    /// refused with the value and the range named (<see cref="SidebarWidths.Refusal"/>). Same shape
    /// as <see cref="TryOverlaySize"/>: a non-number must not become 0 on the way in.
    /// </summary>
    internal static bool TrySidebarWidth(JsonElement args, out int? width, out string? error)
    {
        width = null; error = null;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(SidebarWidthKey, out var v)) return true;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) && SidebarWidths.InRange(n))
        { width = (int)n; return true; }
        error = SidebarWidths.Refusal(v.GetRawText());
        return false;
    }
    internal const string SidebarWidthKey = "width";

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

    /// <summary>App version for `ping` — the entry assembly's informational version, formatted by
    /// the same rule `agwintermctl version` applies to its own half (see
    /// <see cref="VersionReport.EntryAssemblyVersion"/>).</summary>
    private static string AppVersion() => VersionReport.EntryAssemblyVersion();

    private static string Ok(string result) => $"{{\"ok\":true,\"result\":{JsonSerializer.Serialize(result)}}}";
    private static string OkRaw(string rawResult) => $"{{\"ok\":true,\"result\":{rawResult}}}";
    private static string Err(string error) => $"{{\"ok\":false,\"error\":{JsonSerializer.Serialize(error)}}}";

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
