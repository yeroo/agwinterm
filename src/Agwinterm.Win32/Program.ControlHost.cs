using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DCommon;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;
using Color = Agwinterm.Core.Color;

namespace Agwinterm.Win32;

/// <summary>Control-API host: the IWindowHost + ISessionHost bridges the control server drives.</summary>
internal partial class Program
{
    // ---- IWindowHost bridge (Wave F1b): app-level window management for the control API. Content
    // verbs resolve through ResolveWindow(--window); window.* verbs act on the library. These use
    // static library state + Frontmost, so they work regardless of which instance the server holds. ----
    public ISessionHost? ResolveWindow(string? selector)
    {
        if (string.IsNullOrEmpty(selector) || selector == "active") return Frontmost;
        return ResolveOpen(selector);
    }

    public IReadOnlyList<WindowSnapshot> Windows()
    {
        lock (_windowIndex)
            return _windowIndex.Select(m => new WindowSnapshot(m.Id, m.Name, m.IsOpen, m.Id == _frontmostId)).ToList();
    }

    public string WindowNew(string? name)
    {
        string id = Guid.NewGuid().ToString();
        Frontmost.Post(() =>
        {
            var m = new WinMeta { Id = id, Name = name ?? "", IsOpen = true };
            CascadeGeometry(m);
            lock (_windowIndex) _windowIndex.Add(m);
            var win = CreateWindowInstance(m);
            // The new window becomes frontmost; set both the static instance and the id together so the
            // library's "active" flag and un-scoped (--window active) content verbs stay consistent even
            // if the OS doesn't deliver WM_ACTIVATE (e.g. created while another process holds foreground).
            Frontmost = win; _frontmostId = id;
            SetForegroundWindow(win._hwnd);
            SaveIndex();
        });
        return id;
    }

    public bool WindowSelect(string? selector)
    {
        var p = ResolveOpen(selector);
        if (p is null) return false;
        Frontmost.Post(() => { if (IsIconic(p._hwnd)) ShowWindow(p._hwnd, SW_RESTORE); SetForegroundWindow(p._hwnd); });
        return true;
    }

    public bool WindowClose(string? selector)
    {
        var p = ResolveOpen(selector);
        if (p is null) return false;
        Frontmost.Post(() => DestroyWindow(p._hwnd)); // WM_DESTROY does teardown + index bookkeeping
        return true;
    }

    public bool WindowDelete(string? selector)
    {
        var target = ResolveMeta(selector);
        if (target is null) return false;
        lock (_windowIndex) if (_windowIndex.Count <= 1) return false; // never delete the last window
        Frontmost.Post(() =>
        {
            Program? open; lock (_windowIndex) _byId.TryGetValue(target.Id, out open);
            if (open is not null) DestroyWindow(open._hwnd);
            lock (_windowIndex)
            {
                _windowIndex.RemoveAll(m => m.Id == target.Id);
                if (_frontmostId == target.Id) _frontmostId = _windowIndex.FirstOrDefault(m => m.IsOpen)?.Id ?? _windowIndex.FirstOrDefault()?.Id;
            }
            SweepWindowBackgrounds(target.Id); // remove watermark files for that window's sessions
            try { File.Delete(Path.Combine(AppDir, "windows", target.Id + ".json")); } catch { }
            SaveIndex();
        });
        return true;
    }

    public bool WindowRename(string? selector, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var target = ResolveMeta(selector);
        if (target is null) return false;
        Frontmost.Post(() =>
        {
            lock (_windowIndex) target.Name = name;
            if (_byId.TryGetValue(target.Id, out var p)) { p.WinName = name; p.RequestRedraw(); }
            SaveIndex();
        });
        return true;
    }

    public bool WindowResize(string? selector, int w, int h)
    {
        var p = ResolveOpen(selector);
        if (p is null || w <= 0 || h <= 0) return false;
        Frontmost.Post(() => { SetWindowPos(p._hwnd, IntPtr.Zero, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE); p.SaveState(); });
        return true;
    }

    public bool WindowMove(string? selector, int x, int y)
    {
        var p = ResolveOpen(selector);
        if (p is null) return false;
        Frontmost.Post(() => { SetWindowPos(p._hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); p.SaveState(); });
        return true;
    }

    public bool WindowZoom(string? selector)
    {
        var p = ResolveOpen(selector);
        if (p is null) return false;
        Frontmost.Post(() => { ShowWindow(p._hwnd, IsZoomed(p._hwnd) ? SW_RESTORE : SW_MAXIMIZE); p.SaveState(); });
        return true;
    }

    private static Program? ResolveOpen(string? selector)
    {
        lock (_windowIndex)
        {
            if (string.IsNullOrEmpty(selector) || selector == "active") return Frontmost;
            if (_byId.TryGetValue(selector, out var exact)) return exact;
            foreach (var kv in _byId) if (kv.Key.StartsWith(selector)) return kv.Value;
        }
        return null;
    }

    private static WinMeta? ResolveMeta(string? selector)
    {
        lock (_windowIndex)
        {
            if (string.IsNullOrEmpty(selector) || selector == "active")
                return _windowIndex.FirstOrDefault(m => m.Id == _frontmostId) ?? _windowIndex.FirstOrDefault();
            return _windowIndex.FirstOrDefault(m => m.Id == selector) ?? _windowIndex.FirstOrDefault(m => m.Id.StartsWith(selector));
        }
    }

    private static void CascadeGeometry(WinMeta m)
    {
        try
        {
            if (Frontmost is not null && GetWindowRect(Frontmost._hwnd, out RECT r))
            { m.X = r.left + 32; m.Y = r.top + 32; m.W = Math.Max(400, r.right - r.left); m.H = Math.Max(300, r.bottom - r.top); }
        }
        catch { }
    }

    // ---- ISessionHost bridge (Program is the host) so the control server / agwintermctl drive
    // this window. Un-scoped verbs act on this instance; the seam for future --window targeting. ----
    public ISession? Resolve(string? target)
    {
        if (string.IsNullOrEmpty(target) || target == "active") return ActiveSurface()?.S;
        // Pane ids include split panes and every auxiliary cover. In particular, a ctl launched
        // inside scratch/overlay/quick inherits that cover's id as AGWINTERM_SESSION_ID.
        return FindControlPane(target)?.pane.S;
    }

    public IReadOnlyList<WorkspaceSnapshot> Tree()
    {
        lock (_workspaces)
            return _workspaces.Select(w => new WorkspaceSnapshot(
                w.Id, w.Name, _active is not null && ReferenceEquals(_active.Ws, w),
                w.Sessions.Select(s =>
                {
                    var (status, statusChangedAt) = AggStatusAndAt(s);
                    return new SessionSnapshot(s.Id, s.Name, ReferenceEquals(s, _active), status,
                        s.Overlay is not null, UnreadOf(s), s.Flagged, s.BgPath is not null,
                        FocusedPane: Math.Clamp(s.Active, 0, Math.Max(0, s.Panes.Count - 1)), PaneCount: s.Panes.Count,
                        StatusBlink: AggBlink(s), OverlaySize: s.OverlaySizePercent,
                        SplitRatios: s.Panes.Select(p => (double)p.Ratio).ToList(),
                        PaneIds: s.Panes.Select(p => p.Id).ToList(),
                        RestoreCommands: s.Panes.Select(p => p.RestoreCommand ?? "").ToList(),
                        StatusChangedAt: statusChangedAt);
                }).ToList()
            )).ToList();
    }

    // Read-back snapshot: plain field reads + a Win32 query, safe from the pipe thread (worst case slightly stale).
    public WindowStateSnapshot WindowState()
    {
        var a = _active;
        return new WindowStateSnapshot(
            SidebarVisible: _sidebarW > 0, Fullscreen: _fullscreen, Maximized: IsZoomed(_hwnd),
            QuickTerminalVisible: _coverKind == 2 && _cover is not null && ReferenceEquals(_cover, _quick),
            ActiveWorkspace: a?.Ws.Name, ActiveSession: a is null ? null : (a.CustomName ?? a.Name));
    }

    /// <summary>
    /// Live cell + pane metrics for `session.metrics`, in DEVICE pixels. Measured on the UI thread:
    /// <see cref="Metrics"/> memoises text formats in a static dictionary the render path writes to,
    /// so reading it from the pipe thread would race a font rebuild — and taking the layout and the
    /// cell size in one hop also keeps them describing the same instant.
    ///
    /// The numbers are the ones the renderer actually draws with (<see cref="PaneLayout"/> +
    /// <c>Metrics(pane.FontSize)</c>), converted DIP→device by <see cref="Scale"/>. The exact grid
    /// extent accumulates the fractional cell advance before rounding, avoiding per-cell error.
    /// </summary>
    public PaneMetricsSnapshot? PaneMetrics(string? target)
    {
        PaneMetricsSnapshot? snap = null;
        InvokeOnUi(() => { snap = MeasurePane(target); return ""; });
        return snap;
    }

    private PaneMetricsSnapshot? MeasurePane(string? target)
    {
        // Same resolution order as Resolve(). A pane targeted by its AGWINTERM_PANE_ID must land
        // on ITS column, while an exact session id must beat a derived scratch/overlay id prefix.
        Ses? ses = null; Pane? pane = null;
        if (string.IsNullOrEmpty(target) || target == "active")
        {
            ses = _active;
            pane = ActiveSurface();
        }
        else if (FindControlPane(target) is { } hit)
        {
            ses = hit.ses;
            pane = hit.pane;
        }
        if (pane is null || (ses is null && !ReferenceEquals(pane, _cover))) return null;

        var (_, cwDip, chDip) = Metrics(pane.FontSize);
        bool laidOut;
        if (ReferenceEquals(pane, _cover))
        {
            var (_, _, w, h) = CoverRect();
            laidOut = w > 0 && h > 0;
        }
        else
        {
            laidOut = false;
            foreach (var (p, _, _, w, h) in PaneLayout(ses!))
                if (ReferenceEquals(p, pane)) { laidOut = w > 0 && h > 0; break; }
        }
        return laidOut
            ? PaneMetricsSnapshot.FromDipGrid(pane.S.Cols, pane.S.Rows, cwDip, chDip, Scale)
            : null;
    }

    // The workspace is resolved HERE, synchronously, before an id exists — as SessionToWorkspace and
    // WorkspaceSelect already do through FindWs. Before P2 the id was minted first, returned at once,
    // and the posted lambda resolved the workspace afterwards with a silent fallback to the active
    // one: the reply was committed before the question was asked, so an unknown --workspace could
    // only ever "succeed". Now an unknown one is refused (decision 1; SessionNewWorkspaces) and
    // nothing is posted, so no session exists behind an ok:false reply. Only the creation itself
    // (and, with --create-workspace, the new workspace) still runs on the UI thread.
    // --workspace beside --workspace-name is refused by the server before this is reached; if a
    // direct caller passes both anyway, the id wins, exactly as it did before P2.
    // With NEITHER named, the answer is the CALLER's workspace (task 5a): `caller` is the pane that
    // ran `session new` (the CLI sends its AGWINTERM_SESSION_ID), so an agent gets sessions next to
    // itself however the user has clicked around meanwhile. Only a missing or stale caller reaches
    // the active workspace, and that too is read here, synchronously, rather than inside the Post.
    public string NewSession(string? name, string? cwd, string? workspace, string? command = null,
        string? workspaceName = null, bool createWorkspace = false, string? profile = null, bool noSelect = false, bool wait = false,
        string? caller = null)
    {
        Workspace? ws = null;
        string? newWorkspaceName = null;   // set = create this workspace on the UI thread, then the session in it
        if (!string.IsNullOrEmpty(workspace))
        {
            ws = FindWs(workspace);   // id, id-prefix, or "active"; never a name
            if (ws is null) return ISessionHost.RefusePrefix + SessionNewWorkspaces.UnknownId(workspace);
        }
        else if (!string.IsNullOrEmpty(workspaceName))
        {
            lock (_workspaces) ws = _workspaces.FirstOrDefault(w => string.Equals(w.Name, workspaceName, StringComparison.OrdinalIgnoreCase));
            if (ws is null)
            {
                if (!createWorkspace) return ISessionHost.RefusePrefix + SessionNewWorkspaces.UnknownName(workspaceName);
                newWorkspaceName = workspaceName;
            }
        }
        else
        {
            // Nothing named: the workspace of the caller's own pane. A scratch/overlay pane belongs
            // to the session it covers; the quick terminal belongs to no workspace and falls through.
            // A caller that does not resolve — the pane was closed after it typed the command, a
            // script run from an unrelated shell, a pane in another window — is NOT refused: that
            // would break a working script in order to fix a preference.
            if (!string.IsNullOrEmpty(caller) && FindPaneById(caller) is { ses: { } owner }) ws = owner.Ws;
            // The active workspace is the LAST answer, not the first. "Active" is a global the UI
            // rewrites on every click, every selection and every workspace.new over the API, so an
            // agent creating several sessions used to scatter them wherever the user had last
            // clicked. Reading it here rather than in the Post at least pins it to the moment of the
            // call instead of to whenever the UI thread gets round to the lambda.
            ws ??= ActiveWorkspace();
        }
        string id = Guid.NewGuid().ToString();
        Post(() =>
        {
            // The resolve above ran on the pipe thread; this runs on the UI thread some milliseconds
            // later, and in between the user (or a posted workspace.delete) can have removed `ws`
            // from the list. CreateSession would still add the session to that detached Workspace
            // object: not in the tree, not findable by any verb, yet SetActive would make it the
            // active session - a phantom behind an id the caller already holds. The id is committed,
            // so a refusal is no longer possible; the active workspace is the least-wrong home for a
            // session that has to exist somewhere the caller can see it.
            bool gone;
            lock (_workspaces) gone = ws is not null && !_workspaces.Contains(ws);
            if (gone) ws = ActiveWorkspace();
            Workspace target;
            if (ws is not null) target = ws;
            else
            {
                // The name lookup ran on the pipe thread and MISSED; the create happens here. Two
                // `--workspace-name build --create-workspace` calls dispatched while the UI thread
                // was busy (a modal menu, a drag, a heavy repaint) both missed up there, and before
                // P2 hoisted the lookup they could not both create, because the whole resolve-or-
                // create ran in this lambda and lambdas are serial on the UI thread. So look the
                // name up AGAIN here, where every create happens and nothing can interleave: the
                // second lambda finds the first one's workspace and reuses it. (revmux r1 Major.)
                lock (_workspaces) target = _workspaces.FirstOrDefault(w => string.Equals(w.Name, newWorkspaceName, StringComparison.OrdinalIgnoreCase))!;
                target ??= CreateWorkspace(Guid.NewGuid().ToString(), newWorkspaceName!);
            }
            // --no-select creates the session in the background, leaving the current focus/selection (agterm #250).
            CreateSession(id, name, cwd, target, makeActive: !noSelect, command: command, profileName: profile, wait: wait);
        });
        return id;
    }

    public bool SelectSession(string target)
    {
        var ses = Find(target);
        if (ses is null) return false;
        Post(() => SetActive(ses));
        return true;
    }

    public bool CloseSession(string target)
    {
        var ses = Find(target);
        if (ses is null) return false;
        Post(() => CloseSessionInternal(ses));
        return true;
    }

    public string NewWorkspace(string? name)
    {
        string id = Guid.NewGuid().ToString();
        Post(() => CreateWorkspace(id, name));
        return id;
    }

    public bool SetFontSize(string? target, string op)
    {
        int delta = op switch { "inc" => 1, "dec" => -1, _ => 0 }; // reset otherwise
        if (string.IsNullOrEmpty(target) || target == "active")
        {
            var ses = Find(target);
            if (ses is null) return false;
            Post(() => ChangeFontSizeOf(ses, delta));
            return true;
        }
        if (FindControlPane(target) is not { } targetPane) return false;
        Post(() => ZoomPane(targetPane, delta));
        return true;
    }

    /// <summary>Control-API dashboard: open over explicit session ids (comma/space separated; empty =
    /// most-recently-used), close it, and optionally pin a font size. (agterm #202 CLI.)</summary>
    public bool Dashboard(bool close, string? ids, int fontSize)
    {
        Post(() =>
        {
            if (close) { CloseDashboard(); return; }
            List<Ses>? list = null;
            if (!string.IsNullOrWhiteSpace(ids))
                list = ids!.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(id => Find(id)).Where(s => s is not null).Cast<Ses>().ToList();
            OpenDashboard(list, fontSize);
        });
        return true;
    }

    // ---- Wave A1 verbs ----
    public void SessionGo(string dir) => Post(() => SessionGoInternal(dir));

    public bool SessionReorder(string? target, string dir)
    {
        var s = Find(target);
        if (s is null) return false;
        Post(() => { lock (_workspaces) ReorderInList(s.Ws.Sessions, s, dir); RequestRedraw(); SaveState(); });
        return true;
    }

    public bool SessionToWorkspace(string? target, string workspace)
    {
        var s = Find(target); var ws = FindWs(workspace);
        if (s is null || ws is null) return false;
        Post(() => MoveSession(s, ws));
        return true;
    }

    public bool SessionRename(string? target, string name)
    {
        var ses = FindSesForTarget(target);
        if (ses is null || string.IsNullOrWhiteSpace(name)) return false;
        Post(() => { ses.Name = name; ses.CustomName = name; RequestRedraw(); SaveState(); }); // CustomName drives the title bar
        return true;
    }

    public bool WorkspaceRename(string? target, string name)
    {
        var ws = FindWs(target);
        if (ws is null || string.IsNullOrWhiteSpace(name)) return false;
        Post(() => { ws.Name = name; RequestRedraw(); SaveState(); });
        return true;
    }

    // Clone a session by target (agterm #234) — new session, same cwd + profile. Returns its new id.
    public string DuplicateSession(string? target)
    {
        string id = Guid.NewGuid().ToString();
        Post(() => DuplicateSession(FindSesForTarget(target), id));
        return id;
    }

    // Collapse/expand a single workspace by id (agterm #272). target null/empty = the active workspace.
    public bool WorkspaceCollapse(string? target, bool expand)
    {
        var ws = string.IsNullOrEmpty(target) ? ActiveWorkspace() : FindWs(target);
        if (ws is null) return false;
        Post(() => { lock (_workspaces) ws.Expanded = expand; RequestRedraw(); SaveState(); });
        return true;
    }

    public bool WorkspaceDelete(string? target)
    {
        var ws = FindWs(target);
        if (ws is null) return false;
        Post(() => DeleteWorkspace(ws));
        return true;
    }

    public bool WorkspaceSelect(string? target)
    {
        var ws = FindWs(target);
        if (ws is null) return false;
        Post(() => { var s = ws.Sessions.FirstOrDefault(); if (s is not null) SetActive(s); });
        return true;
    }

    public bool WorkspaceReorder(string? target, string dir)
    {
        var ws = FindWs(target);
        if (ws is null) return false;
        Post(() => { lock (_workspaces) ReorderInList(_workspaces, ws, dir); RequestRedraw(); SaveState(); });
        return true;
    }

    public void Split(string op) => Post(() => SplitOp(op));

    public void FocusPaneDir(string dir)
    {
        int delta = dir switch
        {
            "left" => -1,
            "right" => 1,
            _ => (_active is not null && _active.Active == 0) ? 1 : -1, // "other"
        };
        Post(() => FocusPane(delta));
    }

    public void ResizeSplit(double? ratio, int growLeft, int growRight)
        => Post(() => ResizeActiveSplitInternal(ratio, growLeft, growRight));

    public IReadOnlyList<string> ThemeList() => _allThemes.Select(t => t.Name).ToList();

    public bool ThemeSet(string name)
    {
        if (!_allThemes.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
        Post(() => CommitTheme(FindTheme(name)));
        return true;
    }

    public string KeymapReload() { Post(ReloadKeymap); return "keymap reload requested"; }

    public string ConfigSet(string key, string value) => InvokeOnUi(() => ConfigSetInternal(key, value));
    public string ConfigGet(string key) => InvokeOnUi(() => ConfigValue(key.Trim().ToLowerInvariant()));
    public string ConfigList() => InvokeOnUi(() => string.Join("\n", ConfigKeys.Select(k => $"{k} = {ConfigValue(k)}")));
    public string SettingsOpen() { Post(OpenSettingsWindow); return "settings opened"; }

    public string ProfilesList() => InvokeOnUi(() => string.Join("\n", _profileCfg.Profiles.Select(p =>
        $"{(p.Name.Equals(_profileCfg.Default, StringComparison.OrdinalIgnoreCase) ? "*" : " ")} {p.Name}\t{p.Command}{(p.Args is { Length: > 0 } a ? " " + string.Join(" ", a) : "")}")));
    public string ProfilesReload() => InvokeOnUi(() => { _profileCfg = Agwinterm.Pty.ShellProfiles.Load(AppDir); RequestRedraw(); return $"{_profileCfg.Profiles.Count} profiles loaded"; });

    public string RestoreClear()
    {
        try { if (File.Exists(StatePath)) { File.Delete(StatePath); return "restore state cleared"; } return "no restore state"; }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    public void SidebarOp(string op) => Post(() => SidebarOpInternal(op));

    // "<visible|hidden> <tree|flagged> <width>": the width is _sidebarWShown, the one in effect when
    // shown — beside "hidden" it is the width the next show will use, not a claim that it is on screen.
    public string SidebarState() => InvokeOnUi(() =>
        (_sidebarW > 0 ? "visible" : "hidden") + " " + (_sidebarMode == SidebarMode.Flagged ? "flagged" : "tree")
        + " " + (int)_sidebarWShown);

    // sidebar.width. No clamp: ControlServer.TrySidebarWidth has refused anything outside
    // SidebarWidths.Min..Max, so a value here is one the chrome can draw. A set while visible goes
    // through SidebarWidthChanged, the same re-layout ToggleSidebar uses; a set while hidden only
    // updates the remembered width (and persists it), and the snapshot's Visible=false is what the
    // server turns into "remembered, not applied". InvokeOnUi, not Post: the reply must describe the
    // width after the change, not the width at the moment the request was queued.
    public Agwinterm.Pty.SidebarWidthSnapshot SidebarWidth(int? set)
    {
        Agwinterm.Pty.SidebarWidthSnapshot? snap = null;
        InvokeOnUi(() =>
        {
            if (set is { } w)
            {
                _sidebarWShown = w;
                if (_sidebarW > 0) { _sidebarW = w; SidebarWidthChanged(); }
                else SaveState();
            }
            snap = new Agwinterm.Pty.SidebarWidthSnapshot((int)_sidebarWShown, _sidebarW > 0);
            return "";
        });
        return snap ?? new Agwinterm.Pty.SidebarWidthSnapshot((int)_sidebarWShown, _sidebarW > 0);
    }

    public string BroadcastOp(string op) => InvokeOnUi(() =>
    {
        bool want = op switch { "on" => true, "off" => false, "state" or "get" => _broadcast, _ => !_broadcast };
        if (op is not ("state" or "get") && want != _broadcast) ToggleBroadcast();
        return _broadcast ? "on" : "off";
    });

    public string ReadOnlyOp(string? target, string op) => InvokeOnUi(() =>
    {
        var p = PaneForTarget(target); if (p is null) return "no session";
        bool want = op switch { "on" => true, "off" => false, "state" or "get" => p.ReadOnly, _ => !p.ReadOnly };
        if (op is not ("state" or "get")) { p.ReadOnly = want; RequestRedraw(); }
        return p.ReadOnly ? "on" : "off";
    });

    public bool SessionSeen(string? target)
    {
        var ses = FindSesForTarget(target);
        if (ses is null) return false;
        Post(() => { ClearUnread(ses); RequestRedraw(); });
        return true;
    }

    /// <summary>Plain text of the LAST COMPLETED command's output (FTCS marks) — the
    /// agent-workflow primitive: "give me what that command printed".</summary>
    public string SessionOutput(string? target)
    {
        var pane = PaneForTarget(target);
        if (pane is null) return "";
        TerminalEmulator.ShellMark? m;
        lock (pane.S.SyncRoot) m = pane.S.Emulator.Marks.LastOrDefault(x => x.EndLine >= 0);
        if (m is null) return "no completed command marks (FTCS wrap not active?)";
        // Output begins after the command's input row: 133;C if the shell emits it, else the
        // line after 133;B (the input row), else after the prompt.
        int from = m.OutputLine >= 0 ? m.OutputLine : m.CommandLine >= 0 ? m.CommandLine + 1 : m.PromptLine + 1;
        return from > m.EndLine - 1 ? "" : RowsText(pane, from, m.EndLine - 1);
    }

    public string SessionCopy(string? target) => InvokeOnUi(() =>
    {
        var pane = PaneForTarget(target);
        // On the UI thread: reading a selection RECONCILES it (eviction may have renumbered or
        // invalidated it), and selection state belongs to this thread.
        return pane is not null ? SelectionText(pane) : "";
    });

    // Selection/clipboard control API. Clipboard + selection are UI-thread concepts, so hop on-thread.
    public string SelectionAll(string? target) => InvokeOnUi(() =>
    {
        var p = PaneForTarget(target); if (p is null) return "no session";
        SelectAll(p); return HasLiveSel(p) ? "selected all" : "empty";
    });

    public string SelectionCopy(string? target) => InvokeOnUi(() =>
    {
        var p = PaneForTarget(target); if (p is null) return "no session";
        if (!HasLiveSel(p)) return "no selection";
        // Report what actually happened: a live selection over cells a TUI has blanked copies
        // nothing and leaves the clipboard alone, and an agent acting on this reply must not be
        // told otherwise. Length is not the measure — SelectionText joins rows with CRLF whether
        // or not a row held text.
        return CopySelection(p, out string copied) ? $"copied {copied.Length} chars" : "nothing to copy";
    });

    public string SelectionClear(string? target) => InvokeOnUi(() =>
    {
        var p = PaneForTarget(target); if (p is null) return "no session";
        p.ClearSel(); RequestRedraw(); return "cleared";
    });

    // Test/observability hook: run the same finalize path a mouse-up runs (honors copy-on-select).
    public string SelectionFinalize(string? target) => InvokeOnUi(() =>
    {
        var p = PaneForTarget(target); if (p is null) return "no session";
        // Ask the copy what happened rather than inferring it from a surviving selection: since
        // FinalizeSelection passes clear:false, the selection survives either way, so the
        // "(empty)" arm was unreachable and a declined copy was reported as a copy.
        bool copied = FinalizeSelection(p);
        return _config.CopyOnSelect ? (copied ? "finalized (copied)" : "finalized (empty)") : "finalized (copy-on-select off)";
    });

    public string SessionPaste(string? target, string? text) => InvokeOnUi(() =>
    {
        var p = PaneForTarget(target); if (p is null) return "no session";
        PasteTextInto(p, text ?? ClipboardGet(), interactive: false);   // scripted: never prompt (agents)
        return "pasted";
    });

    // Search operates on the active pane's find bar (a UI-thread concept); run it synchronously
    // on the UI thread and return the "N of M" status. (target is accepted for API shape; v1
    // searches the active session.)
    public string SessionSearch(string? target, string? query, string? action) => InvokeOnUi(() =>
    {
        if (_active is null) return "no session";
        if (action == "close") { CloseSearch(); return "closed"; }
        if (!_searchActive) _searchActive = true;
        if (!string.IsNullOrEmpty(query)) { _searchQuery = query!; RecomputeSearch(); _searchCur = 0; ScrollToMatch(); }
        else if (action == "next") SearchStep(1);
        else if (action == "prev") SearchStep(-1);
        else { RecomputeSearch(); ScrollToMatch(); }
        RequestRedraw();
        return SearchStatus();
    });

    public bool SessionScratch(string? target, string op)
    {
        var ses = FindSesForTarget(target);
        if (ses is null) return false;
        Post(() => ScratchOp(ses, op));
        return true;
    }

    public void Quick(string op) => Post(() => QuickOp(op));

    /// <summary>An overlay covers a whole SESSION. When the caller named one pane of a split, that
    /// is not what they asked for - say so rather than widen it in silence and blank the pane the
    /// user was reading. (Found for real: a review TUI aimed at the right pane took the left one
    /// too, and nothing in the reply said it would.) A pane in a single-pane session is refused
    /// nothing: there, covering the session covers exactly that pane.</summary>
    private string? OverlayTargetRefusal(string? target)
    {
        if (string.IsNullOrEmpty(target) || target == "active") return null;
        var ses = FindSesForTarget(target);
        if (ses is null || ses.Panes.Count <= 1) return null;   // one pane: covering the session covers it
        // A string that names the SESSION keeps session behaviour; only a pane id is refused.
        if (ses.Id == target || ses.Id.StartsWith(target, StringComparison.Ordinal)) return null;
        if (!ses.Panes.Any(p => p.Id == target || p.Id.StartsWith(target, StringComparison.Ordinal))) return null;
        return ISessionHost.RefusePrefix +
               $"'{target}' names one pane of a {ses.Panes.Count}-pane session; session.overlay covers " +
               $"the whole session. Pass the session id ({ses.Id}) to cover it, or omit --target.";
    }

    public string SessionOverlay(string? target, string action, string? command, int sizePercent, bool wait, bool block)
    {
        if (action != "result" && OverlayTargetRefusal(target) is { } refusal) return refusal;
        switch (action)
        {
            case "result":
                return _lastOverlayExit;
            case "close":
                {
                    // Idempotent, and deliberately NOT a refusal: a caller closing an overlay wants
                    // "no overlay open" to be true afterwards, and it is. The conformance contract
                    // runs `session overlay close` with nothing open and expects ok (control-api.json,
                    // the session.overlay step), so this one stays a plain string. Resize and open
                    // below are different: nothing the caller asked for happened.
                    var ses = FindSesForTarget(target);
                    if (ses?.Overlay is null) return "no overlay";
                    Post(() => CloseOverlayOf(ses));
                    return "closed";
                }
            case "resize":
                {
                    var ses = FindSesForTarget(target);
                    // A refusal, not a string: before P2's review this came back as ok:true "no
                    // overlay", and a script branching on ok proceeded as if the resize had happened.
                    if (ses?.Overlay is null) return ISessionHost.RefusePrefix + "no overlay to resize on that target; open one first";
                    // No clamp: ControlServer.TryOverlaySize refused anything outside 0..100 before
                    // this ran, so the N reported is always the N the caller asked for. A clamp here
                    // could only hide a bug upstream now. 0 = full content region; 1..100 = centered panel.
                    int sp = sizePercent;
                    Post(() => { ses.OverlaySizePercent = sp; if (ReferenceEquals(_ovlOwner, ses)) RegridCover(); RequestRedraw(); });
                    return $"resized {sp}%";
                }
            default: // "open"
                {
                    // Both refusals (same reason as resize above): nothing was opened.
                    if (string.IsNullOrWhiteSpace(command)) return ISessionHost.RefusePrefix + "overlay open needs a command; nothing opened";
                    const string noSession = ISessionHost.RefusePrefix + "no session matches that target; nothing opened";
                    if (block)
                    {
                        // Open on the UI thread, then wait (on this pipe thread) for the program to exit.
                        string opened = InvokeOnUi(() => { var s = FindSesForTarget(target); return s is null ? noSession : OverlayOpen(s, command!, sizePercent, false); });
                        if (opened.StartsWith(ISessionHost.RefusePrefix, StringComparison.Ordinal)) return opened;
                        _overlayDone.Wait();
                        return _lastOverlayExit;   // "exit N"
                    }
                    return InvokeOnUi(() => { var s = FindSesForTarget(target); return s is null ? noSession : OverlayOpen(s, command!, sizePercent, wait); });
                }
        }
    }

    public bool Notify(string? target, string? title, string body)
    {
        var ses = FindSesForTarget(target);
        if (ses is null) return false;
        Post(() => OnNotified(ses.ActivePane, title ?? "", body));
        return true;
    }

    public bool SessionFlag(string? target, string op)
    {
        if (op == "clear") { Post(() => FlagOp(null, "clear")); return true; } // clear is global; no target needed
        var ses = FindSesForTarget(target);
        if (ses is null) return false;
        Post(() => FlagOp(ses, op));
        return true;
    }

    // Bind (or clear) a resumable agent on a specific pane, keyed by the pane id the caller used as
    // its AGWINTERM_SESSION_ID. Persisted so restart re-launches the agent (which resumes itself).
    public bool SessionBind(string? target, string agent)
    {
        if (string.IsNullOrEmpty(target)) return false;
        var hit = FindPaneById(target!);
        if (hit is null) return false;
        string? val = string.IsNullOrWhiteSpace(agent) || agent.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : agent.ToLowerInvariant();
        Post(() => { hit.Value.pane.AgentResume = val; SaveState(); });
        return true;
    }

    // Pin (or clear, command = null) a restore command on a specific pane (agterm #271). Persisted so
    // restart always re-runs it. Resolves through FindControlPane, the path every other content verb
    // uses (exact pane, exact session, pane prefix, session prefix/name) rather than the FindPaneById
    // it used before P2. While pane 0 still exists the two agree for every target the old resolver
    // accepted: pane 0 shares its session's id, so the exact-session step only repeats the exact-
    // pane hit, and the prefix step is the same predicate in the same order. Two things changed. A
    // session NAME now reaches that session's focused pane instead of being refused. And once pane 0
    // has been CLOSED in a split (split panes get fresh ids, and closing the focused pane can remove
    // index 0), a session id or id-prefix that the old resolver either refused or steered onto a
    // scratch/overlay cover pane ("<id>:scratch:…" matched the prefix step) now resolves to the
    // session's active pane — the better answer in both cases. The empty / "active" refusal and the
    // ""/"none" folding live in ControlServer, where the fake host can exercise them.
    //
    // Resolve, validate, write and save happen in ONE UI-thread hop: resolved on the pipe thread
    // and written in a later Post, the pane could exit or be closed by another client in between,
    // the removal would run first, the write would land on a detached pane, SaveState would
    // serialise no pin — and the caller would already hold a structured "pinned" naming that pane.
    // Returns the pane the pin landed on, so the reply can name it; null = nothing matched, and
    // nothing was pinned.
    public RestorePinTarget? SessionRestore(string target, string? command)
    {
        if (string.IsNullOrEmpty(target) || target == "active") return null;
        RestorePinTarget? result = null;
        InvokeOnUi(() =>
        {
            var hit = FindControlPane(target);
            if (hit is null) return "";
            // A scratch / overlay / quick cover is not in the saved tree, so a pin on one would answer
            // ok and then vanish at the next restart — the exact "succeeded, did nothing" this batch
            // exists to end. Refuse it with the pane named, and pin nothing.
            if (hit.Value.cover)
            {
                result = new RestorePinTarget(hit.Value.pane.Id, hit.Value.ses?.Id ?? hit.Value.pane.Id,
                    Refusal: $"'{hit.Value.pane.Id}' is a scratch/overlay/quick pane, which is never restored; a pin there would be lost at the next restart. Nothing pinned.");
                return "";
            }
            hit.Value.pane.RestoreCommand = command;
            SaveState();
            result = new RestorePinTarget(hit.Value.pane.Id, hit.Value.ses!.Id);
            return "";
        });
        return result;
    }

    // ---- Control-API event bus (agterm #273): a bounded, cursor-polled log of status / notification /
    // session-lifecycle / tree-change events. Poll with `events --since <cursor>`; the reply carries the
    // new max cursor. Instance-scoped (this window). Emitted from any thread; polled from the pipe. ----
    private readonly object _evtLock = new();
    private readonly Queue<(long Seq, string Type, string? Session, string? Info)> _evtLog = new();
    private long _evtSeq;

    internal void EmitEvent(string type, string? session = null, string? info = null)
    {
        lock (_evtLock)
        {
            _evtLog.Enqueue((++_evtSeq, type, session, info));
            while (_evtLog.Count > 1000) _evtLog.Dequeue();   // bounded history
        }
    }

    public string Events(long since, int limit)
    {
        (long Seq, string Type, string? Session, string? Info)[] items;
        long cursor;
        lock (_evtLock)
        {
            cursor = _evtSeq;
            var q = _evtLog.Where(e => e.Seq > since);
            if (limit > 0) q = q.Take(limit);
            items = q.ToArray();
        }
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"cursor\":").Append(cursor).Append(",\"events\":[");
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var e = items[i];
            sb.Append("{\"seq\":").Append(e.Seq)
              .Append(",\"type\":").Append(System.Text.Json.JsonSerializer.Serialize(e.Type));
            if (e.Session is not null) sb.Append(",\"session\":").Append(System.Text.Json.JsonSerializer.Serialize(e.Session));
            if (e.Info is not null) sb.Append(",\"info\":").Append(System.Text.Json.JsonSerializer.Serialize(e.Info));
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    public string AdoptClaude() => InvokeOnUi(() => AdoptClaudeSessions());

    public string RestartClaudeYolo(string? target) => InvokeOnUi(() =>
    {
        var p = PaneForTarget(target) ?? ActiveSurface();
        return p is null ? "no pane" : RestartClaudeYolo(p);
    });

    public string UpdateClaude() => InvokeOnUi(() => UpdateClaudeCode());

    public string UpdateApp() => InvokeOnUi(() => UpdateAgwinterm());

    public string SessionBackground(string? target, string action, string? path, int opacity, string? mode) => InvokeOnUi(() =>
    {
        var ses = FindSesForTarget(target);
        if (ses is null) return "no session";
        if (action == "clear") { ClearBackground(ses); return "cleared"; }
        if (string.IsNullOrWhiteSpace(path)) return "background set needs a path";
        return SetBackground(ses, path!, opacity, mode);
    });

    public void WorkspaceFocus(string op) => Post(() => WorkspaceFocusOp(op));

    public string SessionSwitch(string op) => InvokeOnUi(() => SwitchOp(op));

    public string CommandRun(string nameOrCommand, string? mode) => InvokeOnUi(() =>
    {
        var cmd = _commands.FirstOrDefault(c => string.Equals(c.Label, nameOrCommand, StringComparison.OrdinalIgnoreCase));
        string text = cmd?.Text ?? nameOrCommand;
        // A configured command uses its mode unless overridden; a raw command defaults to a new session.
        string useMode = mode ?? cmd?.Mode ?? "new";
        string expanded = RunCommandText(text, useMode);
        return $"{useMode}: {expanded}";
    });

    public string CommandList() => InvokeOnUi(CommandListText);

    public string CommandLeader(string op) => InvokeOnUi(() => LeaderOp(op));

    /// <summary>Resolve a control target to a pane using the shared exact-pane, exact-session,
    /// pane-prefix, session-prefix/name ordering.</summary>
    private Pane? PaneForTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target == "active") return ActiveSurface();
        return FindControlPane(target)?.pane;
    }

    /// <summary>Resolve a control-API target ("active"/null/session or pane id/prefix) to its owning session.</summary>
    private Ses? FindSesForTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target == "active") return _active;
        return FindControlPane(target)?.ses;
    }
}
