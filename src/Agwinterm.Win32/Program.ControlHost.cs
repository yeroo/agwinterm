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
        public TerminalSession? Resolve(string? target)
        {
            if (string.IsNullOrEmpty(target) || target == "active") return ActiveSurface()?.S;
            lock (_workspaces)
            {
                var panes = _workspaces.SelectMany(w => w.Sessions).SelectMany(s => s.Panes).ToList();
                var p = panes.FirstOrDefault(x => x.Id == target) ?? panes.FirstOrDefault(x => x.Id.StartsWith(target));
                if (p is not null) return p.S;   // target any pane by id (e.g. docxy's AGWINTERM_SESSION_ID)
            }
            return Find(target)?.S;
        }

        public IReadOnlyList<WorkspaceSnapshot> Tree()
        {
            lock (_workspaces)
                return _workspaces.Select(w => new WorkspaceSnapshot(
                    w.Id, w.Name, _active is not null && ReferenceEquals(_active.Ws, w),
                    w.Sessions.Select(s => new SessionSnapshot(s.Id, s.Name, ReferenceEquals(s, _active), AggStatus(s),
                        s.Overlay is not null, UnreadOf(s), s.Flagged, s.BgPath is not null,
                        FocusedPane: Math.Clamp(s.Active, 0, Math.Max(0, s.Panes.Count - 1)), PaneCount: s.Panes.Count,
                        StatusBlink: AggBlink(s), OverlaySize: s.OverlaySizePercent,
                        SplitRatios: s.Panes.Select(p => (double)p.Ratio).ToList(),
                        PaneIds: s.Panes.Select(p => p.Id).ToList())).ToList()
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

        public string NewSession(string? name, string? cwd, string? workspace, string? command = null,
            string? workspaceName = null, bool createWorkspace = false, string? profile = null)
        {
            string id = Guid.NewGuid().ToString();
            Post(() =>
            {
                Workspace ws;
                if (!string.IsNullOrEmpty(workspace))
                    lock (_workspaces)
                        ws = _workspaces.FirstOrDefault(w => w.Id == workspace)
                             ?? _workspaces.FirstOrDefault(w => w.Id.StartsWith(workspace)) ?? ActiveWorkspace();
                else if (!string.IsNullOrEmpty(workspaceName))
                {
                    Workspace? byName;
                    lock (_workspaces) byName = _workspaces.FirstOrDefault(w => string.Equals(w.Name, workspaceName, StringComparison.OrdinalIgnoreCase));
                    ws = byName ?? (createWorkspace ? CreateWorkspace(Guid.NewGuid().ToString(), workspaceName) : ActiveWorkspace());
                }
                else ws = ActiveWorkspace();
                CreateSession(id, name, cwd, ws, makeActive: true, command: command, profileName: profile);
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
            // A specific pane id (split / scratch / overlay / quick terminal) zooms just that pane;
            // a session id (or null = active) zooms the session's active pane, as before.
            if (!string.IsNullOrEmpty(target) && FindPaneById(target!) is { } hit)
            { Post(() => ZoomPane(hit, delta)); return true; }
            var ses = Find(target);
            if (ses is null) return false;
            Post(() => ChangeFontSizeOf(ses, delta));
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

        public string SidebarState() => InvokeOnUi(() =>
            (_sidebarW > 0 ? "visible" : "hidden") + " " + (_sidebarMode == SidebarMode.Flagged ? "flagged" : "tree"));

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
            var s = Resolve(target);
            if (s is null) return "";
            Pane? pane;
            lock (_workspaces)
                pane = _workspaces.SelectMany(w => w.Sessions).SelectMany(x => x.Panes)
                                  .FirstOrDefault(p => ReferenceEquals(p.S, s));
            if (pane is null) return "";
            TerminalEmulator.ShellMark? m;
            lock (pane.S.SyncRoot) m = pane.S.Emulator.Marks.LastOrDefault(x => x.EndLine >= 0);
            if (m is null) return "no completed command marks (FTCS wrap not active?)";
            // Output begins after the command's input row: 133;C if the shell emits it, else the
            // line after 133;B (the input row), else after the prompt.
            int from = m.OutputLine >= 0 ? m.OutputLine : m.CommandLine >= 0 ? m.CommandLine + 1 : m.PromptLine + 1;
            return from > m.EndLine - 1 ? "" : RowsText(pane, from, m.EndLine - 1);
        }

        public string SessionCopy(string? target)
        {
            var s = Resolve(target);
            if (s is null) return "";
            Pane? pane;
            lock (_workspaces)
                pane = _workspaces.SelectMany(w => w.Sessions).SelectMany(x => x.Panes)
                                  .FirstOrDefault(p => ReferenceEquals(p.S, s));
            return pane is not null ? SelectionText(pane) : ""; // reads under the session lock; safe off-UI-thread
        }

        // Selection/clipboard control API. Clipboard + selection are UI-thread concepts, so hop on-thread.
        public string SelectionAll(string? target) => InvokeOnUi(() =>
        {
            var p = PaneForTarget(target); if (p is null) return "no session";
            SelectAll(p); return p.HasSel ? "selected all" : "empty";
        });

        public string SelectionCopy(string? target) => InvokeOnUi(() =>
        {
            var p = PaneForTarget(target); if (p is null) return "no session";
            if (!p.HasSel) return "no selection";
            string t = SelectionText(p); CopySelection(p); return $"copied {t.Length} chars";
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
            FinalizeSelection(p);
            return _config.CopyOnSelect ? (p.HasSel ? "finalized (copied)" : "finalized (empty)") : "finalized (copy-on-select off)";
        });

        public string SessionPaste(string? target, string? text) => InvokeOnUi(() =>
        {
            var p = PaneForTarget(target); if (p is null) return "no session";
            PasteTextInto(p, text ?? ClipboardGet());
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

        public string SessionOverlay(string? target, string action, string? command, int sizePercent, bool wait, bool block)
        {
            switch (action)
            {
                case "result":
                    return _lastOverlayExit;
                case "close":
                {
                    var ses = FindSesForTarget(target);
                    if (ses?.Overlay is null) return "no overlay";
                    Post(() => CloseOverlayOf(ses));
                    return "closed";
                }
                case "resize":
                {
                    var ses = FindSesForTarget(target);
                    if (ses?.Overlay is null) return "no overlay";
                    int sp = Math.Clamp(sizePercent, 0, 100);   // 0 = full content region; 1..100 = centered panel
                    Post(() => { ses.OverlaySizePercent = sp; if (ReferenceEquals(_ovlOwner, ses)) RegridCover(); RequestRedraw(); });
                    return $"resized {sp}%";
                }
                default: // "open"
                {
                    if (string.IsNullOrWhiteSpace(command)) return "no command";
                    if (block)
                    {
                        // Open on the UI thread, then wait (on this pipe thread) for the program to exit.
                        InvokeOnUi(() => { var s = FindSesForTarget(target); return s is null ? "no session" : OverlayOpen(s, command!, sizePercent, false); });
                        _overlayDone.Wait();
                        return _lastOverlayExit;   // "exit N"
                    }
                    return InvokeOnUi(() => { var s = FindSesForTarget(target); return s is null ? "no session" : OverlayOpen(s, command!, sizePercent, wait); });
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

        public string AdoptClaude() => InvokeOnUi(() => AdoptClaudeSessions());

        public string RestartClaudeYolo(string? target) => InvokeOnUi(() =>
        {
            var p = string.IsNullOrEmpty(target) ? ActiveSurface() : (FindPaneById(target!)?.pane ?? ActiveSurface());
            return p is null ? "no pane" : RestartClaudeYolo(p);
        });

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

    /// <summary>Resolve a control-API target ("active"/null/id/prefix) to its owning session.</summary>
    /// <summary>Resolve a control-API target to a specific pane: the active surface for "active"/empty,
    /// else a pane by (prefix) id, else the target session's active pane.</summary>
    private Pane? PaneForTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target == "active") return ActiveSurface();
        lock (_workspaces)
        {
            var panes = _workspaces.SelectMany(w => w.Sessions).SelectMany(s => s.Panes).ToList();
            var p = panes.FirstOrDefault(x => x.Id == target) ?? panes.FirstOrDefault(x => x.Id.StartsWith(target));
            if (p is not null) return p;
        }
        return FindSesForTarget(target)?.ActivePane;
    }

    private Ses? FindSesForTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target == "active") return _active;
        lock (_workspaces)
        {
            var all = _workspaces.SelectMany(w => w.Sessions).ToList();
            foreach (var s in all)
                if (s.Id == target || s.Panes.Any(p => p.Id == target)) return s;
            foreach (var s in all)
                if (s.Id.StartsWith(target) || s.Panes.Any(p => p.Id.StartsWith(target))) return s;
        }
        return null;
    }
}
