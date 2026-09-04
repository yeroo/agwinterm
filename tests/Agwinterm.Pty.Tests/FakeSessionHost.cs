using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>An in-memory <see cref="ISessionHost"/> for testing the control-API JSON contract without the
/// Win32 UI: real workspace→session state + window flags, so tree/window.state/lifecycle verbs can be
/// asserted end-to-end. Peripheral verbs return sensible constants.</summary>
internal sealed class FakeSessionHost : ISessionHost
{
    internal sealed class Sess
    {
        public string Id = "", Name = "";
        /// <summary>Status and its age, aggregated from the panes in the app's single pass
        /// (<see cref="StatusAggregate.WinnerAndChangedAt"/>) — so the tree's status and its age
        /// always describe the same reading of the same pane. <see cref="Status"/> alone is for
        /// tests that ask about the status only.</summary>
        public (AgentStatus Status, long ChangedAt) StatusAndChangedAt => StatusAggregate.WinnerAndChangedAt(Panes);
        public AgentStatus Status => StatusAndChangedAt.Status;
        public bool Flagged, Overlay, ReadOnly;
        public string? AgentResume;
        public int Notifications, PaneCount = 1, FocusedPane, OverlaySize;
        public List<double> Ratios = new() { 1.0 };
        /// <summary>The session's panes, so the tree can aggregate status + its age the way the app
        /// does. One pane unless a test splits it via <see cref="AddPane"/>.</summary>
        public readonly List<ISession> Panes = new() { new TerminalSession(80, 24) };
        /// <summary>Pane ids, parallel to <see cref="Panes"/>. The first pane SHARES the session id,
        /// exactly as the app does it (Program.Sessions.cs: "first pane shares the session id
        /// (control-API back-compat)"); a split pane gets its own. That sharing is why an id target
        /// reaches pane 0 whatever <see cref="FocusedPane"/> says, and only a NAME reaches the
        /// focused pane — the asymmetry qa/control-read.md pins.</summary>
        public readonly List<string> PaneIds = new();
        /// <summary>session.restore pins, keyed by pane id — what the app keeps in Pane.RestoreCommand.
        /// The tree's <c>restoreCommands</c> is built from here, so a test reads a pin back the way a
        /// caller does, and a refused call is asserted to have left this empty.</summary>
        public readonly Dictionary<string, string> RestorePins = new();
        /// <summary>Scratch / overlay / quick COVERS over this session, kept the way the app keeps them:
        /// NOT in <see cref="Panes"/> / <see cref="PaneIds"/> / PaneCount / the tree (the app holds
        /// them in Ses.Scratch / Ses.Overlay / _quick, and Tree() walks s.Panes only), but reachable
        /// as a target by EVERY verb — Resolve and Find fall through to them after the real panes and
        /// the name arm, as the app's FindPaneBy does — and refused by session.restore. Added with
        /// <see cref="AddCoverPane"/>. Putting a cover into the pane lists desynchronised the
        /// index-paired PaneIds / RestoreCommands (revmux r2 of P2); keeping it out of the resolvers
        /// made session.type on a cover fail here while the app types (r3).</summary>
        public readonly List<(string Id, ISession Pane)> CoverPanes = new();
        /// <summary>A scratch cover over this session, id "&lt;session id&gt;:scratch:&lt;n&gt;" as the app
        /// spells it (Program.Sessions.cs). Returns the pane id.</summary>
        public string AddCoverPane()
        {
            string id = Id + ":scratch:" + Guid.NewGuid().ToString("N")[..6];
            CoverPanes.Add((id, new TerminalSession(80, 24)));
            return id;
        }
        /// <summary>Split this session: adds a pane and returns it, for the multi-pane status cases.</summary>
        public ISession AddPane()
        {
            var p = new TerminalSession(80, 24);
            Panes.Add(p); PaneIds.Add("p" + Guid.NewGuid().ToString("N")[..8]); PaneCount = Panes.Count;
            return p;
        }
        /// <summary>Seals pane 0's id to the session id once <see cref="Id"/> is set.</summary>
        public Sess Seed() { if (PaneIds.Count == 0) PaneIds.Add(Id); else PaneIds[0] = Id; return this; }
    }
    internal sealed class Ws { public string Id = "", Name = ""; public List<Sess> Sessions = new(); }

    internal readonly List<Ws> Workspaces = new();
    internal Ws ActiveWs;
    internal Sess? ActiveSess;
    internal bool SidebarVisible = true, QuickVisible, Broadcast;
    /// <summary>The app's _sidebarWShown: the width the sidebar has when visible, kept while hidden.</summary>
    internal int SidebarW = SidebarWidths.Default;
    internal readonly Dictionary<string, string> Config = new();
    private int _idSeq;

    public FakeSessionHost()
    {
        var w = new Ws { Id = "w1", Name = "workspace 1" };
        var s = new Sess { Id = "s1", Name = "session 1" }.Seed();
        w.Sessions.Add(s);
        Workspaces.Add(w);
        ActiveWs = w; ActiveSess = s;
    }

    private Sess? Find(string? t) =>
        t is null or "active" ? ActiveSess
        : Workspaces.SelectMany(w => w.Sessions).FirstOrDefault(s =>
            s.Id == t || s.Id.StartsWith(t) || s.PaneIds.Any(p => p == t || p.StartsWith(t)))
          ?? FindCover(t)?.s;   // a cover id addresses the session it covers, as in the app
    // The app's FindWs: null/""/"active" = the active workspace, else an id or an id PREFIX — and
    // never a name. The fake used to match names here too, so a test that placed a session by
    // `--workspace <name>` passed against the fake while the app refused it: the same double-drift
    // P1's verification found in Resolve. Names are reached only through workspaceName on NewSession.
    private Ws? FindWs(string? t) =>
        string.IsNullOrEmpty(t) || t == "active" ? ActiveWs
        : Workspaces.FirstOrDefault(w => w.Id == t) ?? Workspaces.FirstOrDefault(w => w.Id.StartsWith(t, StringComparison.Ordinal));

    // A pane, not a session, and in the app's order: active surface, then ANY pane by id or id
    // prefix, then a session by name -> its FOCUSED pane. Because pane 0 shares the session id, an
    // id target lands on pane 0 and never on the focused pane; that is deliberate, and it is the
    // guarantee session.text / session.type already rely on (the pane you CHECK is the pane you
    // then type into). Per-session panes are what let the tree's statusChangedAt aggregate for real.
    public ISession? Resolve(string? target)
    {
        if (target is null or "active") return Focused(ActiveSess);
        if (FindPane(target) is { } hit) return hit.s.Panes[hit.pane];
        return FindCover(target)?.pane;   // after the real panes and the name arm: the app's FindPaneBy order
    }

    /// <summary>The one resolver behind <see cref="Resolve"/> and <see cref="SessionRestore"/>, as
    /// FindControlPane is behind both in the app: the pane index a target lands on, and its session.
    /// "active" is not a pane here — the verbs that default to it do so before calling this.</summary>
    private (Sess s, int pane)? FindPane(string target)
    {
        if (FindPaneById(target) is { } byId) return byId;
        var sessions = Workspaces.SelectMany(w => w.Sessions).ToList();
        var named = sessions.Where(s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count == 1 ? (named[0], Math.Clamp(named[0].FocusedPane, 0, named[0].Panes.Count - 1)) : null;   // an ambiguous name resolves to nothing
    }

    /// <summary>A cover pane by exact id or id prefix — the tail of the app's FindPaneBy, after the
    /// real panes. Resolve and Find reach it (every content verb, as in the app); session.restore
    /// reaches it to refuse; the tree never lists it.</summary>
    private (Sess s, string id, ISession pane)? FindCover(string target)
    {
        foreach (var s in Workspaces.SelectMany(w => w.Sessions))
            foreach (var (id, pane) in s.CoverPanes)
                if (id == target || id.StartsWith(target, StringComparison.Ordinal)) return (s, id, pane);
        return null;
    }

    /// <summary>The app's FindPaneById: a pane by exact id, then by id prefix, and never by a
    /// session name. This is how a <c>caller</c> on session.new resolves (the value is a pane's own
    /// AGWINTERM_SESSION_ID, never a name), so the fake must not reach a pane by name there either.</summary>
    private (Sess s, int pane)? FindPaneById(string id)
    {
        var sessions = Workspaces.SelectMany(w => w.Sessions).ToList();
        foreach (var pred in new Func<string, bool>[] { pid => pid == id, pid => pid.StartsWith(id, StringComparison.Ordinal) })
            foreach (var s in sessions)
                for (int i = 0; i < s.Panes.Count; i++)
                    if (i < s.PaneIds.Count && pred(s.PaneIds[i])) return (s, i);
        return null;
    }

    /// <summary>The workspace a session lives in — what the app reads as <c>Ses.Ws</c>.</summary>
    internal Ws WorkspaceOf(Sess s) => Workspaces.First(w => w.Sessions.Contains(s));

    private static ISession? Focused(Sess? s) =>
        s is null ? null : s.Panes[Math.Clamp(s.FocusedPane, 0, s.Panes.Count - 1)];

    public IReadOnlyList<WorkspaceSnapshot> Tree() => Workspaces.Select(w => new WorkspaceSnapshot(
        w.Id, w.Name, ReferenceEquals(w, ActiveWs),
        w.Sessions.Select(s =>
        {
            var (status, statusChangedAt) = s.StatusAndChangedAt;   // one reading, as Program.ControlHost.Tree does
            return new SessionSnapshot(s.Id, s.Name, ReferenceEquals(s, ActiveSess), status,
                s.Overlay, s.Notifications, s.Flagged, false, s.FocusedPane, s.PaneCount, false, s.OverlaySize, s.Ratios,
                PaneIds: s.PaneIds,
                RestoreCommands: s.PaneIds.Select(id => s.RestorePins.TryGetValue(id, out var pin) ? pin : "").ToList(),   // "" = none, as the app's snapshot spells it; parallel to PaneIds, covers in neither
                StatusChangedAt: statusChangedAt);
        }).ToList())).ToList();

    public WindowStateSnapshot WindowState() =>
        new(SidebarVisible, Fullscreen: false, Maximized: false, QuickVisible, ActiveWs.Name, ActiveSess?.Name);

    // Stands in for the real host's live measurement: the test moves these the way a font-size change
    // or a resize would, and asserts session.metrics reports the NEW numbers rather than a snapshot.
    internal int CellW = 9, CellH = 19, PaneW = 1188, PaneH = 703;
    internal bool Measurable = true;
    internal readonly Dictionary<string, PaneMetricsSnapshot> MetricsBySession = new();
    public PaneMetricsSnapshot? PaneMetrics(string? target)
    {
        var session = Find(target);
        if (!Measurable || session is null) return null;
        return MetricsBySession.TryGetValue(session.Id, out var metrics)
            ? metrics
            : new PaneMetricsSnapshot(session.Panes[0].Cols, session.Panes[0].Rows, CellW, CellH, PaneW, PaneH);
    }

    // Mirrors Program.ControlHost.NewSession step for step: --workspace is an id / id-prefix and an
    // unknown one is REFUSED; --workspace-name is a case-insensitive name, unknown is refused unless
    // --create-workspace, which creates it; both refusals happen before a session (or an id) exists.
    // The pair of flags is refused by the server first; a direct caller passing both gets the id,
    // as in the app. The wording is the shared SessionNewWorkspaces, so a test asserts the app's text.
    // With neither flag: the CALLER's pane's workspace (caller = a pane id, exact or prefix, the way
    // the app's FindPaneById reads it), and only when there is no caller or it no longer resolves,
    // the active workspace — the last answer, not the first (task 5a).
    public string NewSession(string? name, string? cwd, string? workspace, string? command = null,
        string? workspaceName = null, bool createWorkspace = false, string? profile = null, bool noSelect = false, bool wait = false,
        string? caller = null)
    {
        Ws? w;
        if (!string.IsNullOrEmpty(workspace))
        {
            w = FindWs(workspace);
            if (w is null) return ISessionHost.RefusePrefix + SessionNewWorkspaces.UnknownId(workspace);
        }
        else if (!string.IsNullOrEmpty(workspaceName))
        {
            w = Workspaces.FirstOrDefault(x => string.Equals(x.Name, workspaceName, StringComparison.OrdinalIgnoreCase));
            if (w is null)
            {
                if (!createWorkspace) return ISessionHost.RefusePrefix + SessionNewWorkspaces.UnknownName(workspaceName);
                w = new Ws { Id = "w" + (++_idSeq + 100), Name = workspaceName }; Workspaces.Add(w);
            }
        }
        else if (!string.IsNullOrEmpty(caller) && FindPaneById(caller) is { } callerPane) w = WorkspaceOf(callerPane.s);
        else w = ActiveWs;   // no caller, or a stale one: not refused
        var s = new Sess { Id = "s" + (++_idSeq + 100), Name = string.IsNullOrEmpty(name) ? $"session {w.Sessions.Count + 1}" : name }.Seed();
        w.Sessions.Add(s);
        if (!noSelect) { ActiveSess = s; ActiveWs = w; }
        return s.Id;
    }

    public string DuplicateSession(string? target)
    {
        var src = Find(target) ?? ActiveSess;
        var w = src is null ? ActiveWs : Workspaces.First(x => x.Sessions.Contains(src));
        var s = new Sess { Id = "s" + (++_idSeq + 100), Name = $"session {w.Sessions.Count + 1}" }.Seed();
        w.Sessions.Add(s); ActiveSess = s; ActiveWs = w;
        return s.Id;
    }
    public bool WorkspaceCollapse(string? target, bool expand) { var w = FindWs(target) ?? ActiveWs; return w is not null; }
    // Real, not a stub: the pin is stored per pane and read back through the tree, so the verb's
    // reply ("which pane got it") can be checked against what a later caller would see.
    public RestorePinTarget? SessionRestore(string target, string? command)
    {
        if (string.IsNullOrEmpty(target) || target == "active") return null;
        // The app's cover refusal, word for word: a pin on a scratch/overlay/quick pane is lost at
        // restart. Covers are checked after the real panes, as the app's FindPaneBy walks s.Panes
        // before Ses.Scratch / Ses.Overlay.
        if (FindPane(target) is not { } hit)
        {
            if (FindCover(target) is { } cover)
                return new RestorePinTarget(cover.id, cover.s.Id,
                    Refusal: $"'{cover.id}' is a scratch/overlay/quick pane, which is never restored; a pin there would be lost at the next restart. Nothing pinned.");
            return null;
        }
        string paneId = hit.s.PaneIds[hit.pane];
        if (command is null) hit.s.RestorePins.Remove(paneId); else hit.s.RestorePins[paneId] = command;
        return new RestorePinTarget(paneId, hit.s.Id);
    }
    public string Events(long since, int limit) => "{\"cursor\":0,\"events\":[]}";

    public bool SelectSession(string target) { var s = Find(target); if (s is null) return false; ActiveSess = s; ActiveWs = Workspaces.First(w => w.Sessions.Contains(s)); return true; }
    public bool CloseSession(string target)
    {
        var s = Find(target); if (s is null) return false;
        var w = Workspaces.First(x => x.Sessions.Contains(s)); w.Sessions.Remove(s);
        if (ReferenceEquals(ActiveSess, s)) ActiveSess = Workspaces.SelectMany(x => x.Sessions).FirstOrDefault();
        return true;
    }
    public string NewWorkspace(string? name) { var w = new Ws { Id = "w" + (++_idSeq + 100), Name = string.IsNullOrEmpty(name) ? $"workspace {Workspaces.Count + 1}" : name }; Workspaces.Add(w); return w.Id; }
    public string ProfilesList() => "default";
    public string ProfilesReload() => "1 profile loaded";
    public bool SetFontSize(string? target, string op) => Find(target) is not null;
    public bool Dashboard(bool close, string? ids, int fontSize) => true;

    public void SessionGo(string dir) { }
    public bool SessionReorder(string? target, string dir) => Find(target) is not null;
    public bool SessionToWorkspace(string? target, string workspace) { var s = Find(target); var w = FindWs(workspace); if (s is null || w is null) return false; Workspaces.First(x => x.Sessions.Contains(s)).Sessions.Remove(s); w.Sessions.Add(s); return true; }
    public bool SessionRename(string? target, string name) { var s = Find(target); if (s is null || string.IsNullOrWhiteSpace(name)) return false; s.Name = name; return true; }
    public bool SessionSeen(string? target) { var s = Find(target); if (s is null) return false; s.Notifications = 0; return true; }
    public string SidebarState() => $"{(SidebarVisible ? "visible" : "hidden")} tree {SidebarW}";
    // Mirrors the app: no clamp, because ControlServer.TrySidebarWidth refuses out-of-range before the
    // host is reached. The range check is the fake's own tripwire — a test that drives the host
    // directly with a bad width must blow up, not see a silently-stored value.
    public SidebarWidthSnapshot SidebarWidth(int? set)
    {
        if (set is { } w)
        {
            if (!SidebarWidths.InRange(w)) throw new ArgumentOutOfRangeException(nameof(set), w, "the server should have refused this");
            SidebarW = w;
        }
        return new SidebarWidthSnapshot(SidebarW, SidebarVisible);
    }
    public string BroadcastOp(string op) { Broadcast = op switch { "on" => true, "off" => false, "toggle" => !Broadcast, _ => Broadcast }; return Broadcast ? "on" : "off"; }
    public string ReadOnlyOp(string? target, string op) { var s = Find(target); if (s is null) return "off"; s.ReadOnly = op switch { "on" => true, "off" => false, "toggle" => !s.ReadOnly, _ => s.ReadOnly }; return s.ReadOnly ? "on" : "off"; }
    public string SessionOutput(string? target) => "";
    public bool WorkspaceRename(string? target, string name) { var w = FindWs(target); if (w is null || string.IsNullOrWhiteSpace(name)) return false; w.Name = name; return true; }
    public bool WorkspaceDelete(string? target) { var w = FindWs(target); if (w is null || Workspaces.Count <= 1) return false; Workspaces.Remove(w); if (ReferenceEquals(ActiveWs, w)) { ActiveWs = Workspaces[0]; ActiveSess = ActiveWs.Sessions.FirstOrDefault(); } return true; }
    public bool WorkspaceSelect(string? target) { var w = FindWs(target); if (w is null) return false; ActiveWs = w; ActiveSess = w.Sessions.FirstOrDefault(); return true; }
    public bool WorkspaceReorder(string? target, string dir) => FindWs(target) is not null;
    public bool Split(string? target, string op)
    {
        // Mirrors the app: the target resolves like every other session verb (null/"active",
        // a session or pane id, or a prefix), and the split lands on THAT session - not on
        // whichever one is active. Toggle is modelled too, since the app's default op is toggle.
        var s = Find(target);
        if (s is null) return false;
        s.PaneCount = op switch { "on" => 2, "off" => 1, _ => s.PaneCount > 1 ? 1 : 2 };
        s.Ratios = s.PaneCount == 1 ? new() { 1.0 } : new() { 0.5, 0.5 };
        return true;
    }
    public void FocusPaneDir(string dir) { if (ActiveSess is { PaneCount: > 1 }) ActiveSess.FocusedPane ^= 1; }
    public void ResizeSplit(double? ratio, int growLeft, int growRight) { if (ActiveSess is { PaneCount: > 1 } && ratio is { } r) ActiveSess.Ratios = new() { r, 1 - r }; }
    public IReadOnlyList<string> ThemeList() => new[] { "dark", "light" };
    public bool ThemeSet(string name) => name is "dark" or "light";
    public string KeymapReload() => "reloaded";
    public string RestoreClear() => "cleared";
    /// <summary>Like the app's SidebarOpInternal switch: show/hide/toggle move visibility, the rest
    /// (expand/collapse/mode:*) are handled without visible state here. Anything else is a bug —
    /// the server refuses unknown ops (and folds on/off) before this is called — so it throws
    /// rather than falling through the way the app used to.</summary>
    public void SidebarOp(string op)
    {
        switch (op)
        {
            case "show": SidebarVisible = true; break;
            case "hide": SidebarVisible = false; break;
            case "toggle": SidebarVisible = !SidebarVisible; break;
            case "expand" or "collapse" or "mode:tree" or "mode:flagged" or "mode:toggle": break;
            default: throw new ArgumentException($"the server should have refused sidebar op '{op}'", nameof(op));
        }
    }
    public string ConfigSet(string key, string value) { Config[key] = value; return "set"; }
    public string ConfigGet(string key) => Config.TryGetValue(key, out var v) ? v : "";
    public string ConfigList() => string.Join("\n", Config.Select(kv => $"{kv.Key} = {kv.Value}"));
    public string SettingsOpen() => "opened";
    public string SessionCopy(string? target) => "";
    public string SelectionAll(string? target) => Find(target) is not null ? "selected" : "no session";
    public string SelectionCopy(string? target) => "";
    public string SelectionClear(string? target) => "cleared";
    public string SelectionFinalize(string? target) => "";
    public string SessionPaste(string? target, string? text) => Find(target) is not null ? "pasted" : "no session";
    public string SessionSearch(string? target, string? query, string? action) => "no matches";
    public bool SessionScratch(string? target, string op) => Find(target) is not null;
    public void Quick(string op) { QuickVisible = op switch { "on" => true, "off" => false, "toggle" => !QuickVisible, _ => QuickVisible }; }
    // Mirrors the app: no clamp, because ControlServer.TryOverlaySize refuses out-of-range before the
    // host is reached. The range check here is the fake's own tripwire — a test that drives the host
    // directly with a bad size must see a refusal, not a silently-coerced panel.
    public string SessionOverlay(string? target, string action, string? command, int sizePercent, bool wait, bool block)
    {
        // As the app, in the app's ORDER and WORDING (the suite asserts wording, so the two hosts
        // must not drift): open checks the command before the session; a named target that resolves
        // to no session is refused by open, resize and close alike; close on a session that exists
        // and has no overlay is idempotent ok ("no overlay" — the conformance contract closes with
        // nothing open); resize with no overlay open is a refusal.
        const string noSession = ISessionHost.RefusePrefix + "no session matches that target; nothing opened, resized or closed";
        var s = Find(target);
        bool named = !string.IsNullOrEmpty(target) && target != "active";
        if (sizePercent is < 0 or > 100) return ISessionHost.RefusePrefix + $"size-percent {sizePercent} is outside 0..100";
        switch (action)
        {
            case "close":
                if (s is null) return named ? noSession : "no overlay";
                if (!s.Overlay) return "no overlay";
                s.Overlay = false; s.OverlaySize = 0; return "closed";
            case "resize":
                if (s is null) return noSession;
                if (!s.Overlay) return ISessionHost.RefusePrefix + "no overlay to resize on that target; open one first";
                s.OverlaySize = sizePercent; return $"resized {s.OverlaySize}%";
            default:
                if (string.IsNullOrWhiteSpace(command)) return ISessionHost.RefusePrefix + "overlay open needs a command; nothing opened";
                if (s is null) return noSession;
                s.Overlay = true; s.OverlaySize = sizePercent; return s.Id;
        }
    }
    public bool Notify(string? target, string? title, string body) { var s = Find(target); if (s is null) return false; s.Notifications++; return true; }
    public bool SessionFlag(string? target, string op) { if (op == "clear") { foreach (var s in Workspaces.SelectMany(w => w.Sessions)) s.Flagged = false; return true; } var x = Find(target); if (x is null) return false; x.Flagged = op switch { "on" => true, "off" => false, "toggle" => !x.Flagged, _ => x.Flagged }; return true; }
    public bool SessionBind(string? target, string agent) { var s = Find(target); if (s is null) return false; s.AgentResume = string.IsNullOrWhiteSpace(agent) || agent == "none" ? null : agent; return true; }
    public string AdoptClaude() { int n = 0; foreach (var s in Workspaces.SelectMany(w => w.Sessions)) { s.AgentResume = "claude --resume x"; n++; } return $"adopted {n}"; }
    public string RestartClaudeYolo(string? target) { var s = Find(target); if (s is null) return "no pane"; s.AgentResume = "claude --resume x --dangerously-skip-permissions"; return "restarting Claude in YOLO mode (resumed)"; }
    public string UpdateClaude() => "updating Claude Code…";
    public string UpdateApp() => "updating agwinterm…";
    public void WorkspaceFocus(string op) { }
    public string SessionBackground(string? target, string action, string? path, int opacity, string? mode) => Find(target) is not null ? "ok" : "no session";
    public string SessionSwitch(string op) => ActiveSess?.Name ?? "";
    public string CommandRun(string nameOrCommand, string? mode) => $"{mode ?? "new"}: {nameOrCommand}";
    public string CommandList() => "";
    public string CommandLeader(string op) => "idle";
    public string OmpSet(string nameOrPath, bool persist) => "ok";
}
