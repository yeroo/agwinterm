using System.Text;
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
        /// <summary>session.context — what the app keeps in Ses.Context. Read back through the tree,
        /// so a test asserts a set the way a caller does and a refusal the way it must: unchanged.</summary>
        public string? Context;
        public int Notifications, PaneCount = 1, FocusedPane, OverlaySize;
        public List<double> Ratios = new() { 1.0 };
        /// <summary>The split's orientation — what the app keeps in Ses.Axis: one of <see cref="SplitAxes"/>'
        /// words, vertical until a split says otherwise, kept across <c>off</c> (P4).</summary>
        public string Axis = SplitAxes.Vertical;
        /// <summary>The session's panes, so the tree can aggregate status + its age the way the app
        /// does. One pane unless a test splits it via <see cref="AddPane"/>.</summary>
        public readonly List<ISession> Panes = new() { new TerminalSession(80, 24) };
        /// <summary>Pane ids, parallel to <see cref="Panes"/>. AT MOST ONE pane carries the session id,
        /// as the app does it (Program.Sessions.cs, CreateSession: a fresh session's pane 0 IS the
        /// session id; a split pane gets its own): since P4's <c>session swap</c> that pane may sit on
        /// either side (a swap moves panes, never ids), and since <c>session split close</c> it may be
        /// gone (closing the carrier — by any path — leaves none; a later split mints a fresh id). That
        /// is why an id target reaches THAT pane whatever <see cref="FocusedPane"/> says while one
        /// carries it (the resolver's exact-pane-first order), and the FOCUSED pane while none does —
        /// the rule by condition that qa/control-read.md pins.</summary>
        public readonly List<string> PaneIds = new();
        /// <summary>session.restore pins, keyed by pane id — what the app keeps in Pane.RestoreCommand.
        /// The tree's <c>restoreCommands</c> is built from here, so a test reads a pin back the way a
        /// caller does, and a refused call is asserted to have left this empty.</summary>
        public readonly Dictionary<string, string> RestorePins = new();
        /// <summary>What each pane's shell is RUNNING, keyed by pane id — the fake's stand-in for the
        /// app's CIM process snapshot (the fake has no processes). A test seeds it; <see cref="RestoreCapture"/>
        /// reads it. A pane absent here has no non-denylisted child, and captures null.</summary>
        public readonly Dictionary<string, string> Foreground = new();
        /// <summary>The restore SLOT, keyed by pane id — what the app keeps in Pane.CapturedCommand.
        /// Written only by <see cref="RestoreCapture"/> (a capture of nothing REMOVES the entry, as the
        /// app writes null), read back through the tree's <c>capturedCommands</c>, so a refused call is
        /// asserted to have left it untouched.</summary>
        public readonly Dictionary<string, string> Captured = new();
        /// <summary>Scratch / overlay COVERS over this session, kept the way the app keeps them: NOT
        /// in <see cref="Panes"/> / <see cref="PaneIds"/> / PaneCount / the tree (the app holds them in
        /// Ses.Scratch / Ses.Overlay, and Tree() walks s.Panes only), and reachable as a target by
        /// exactly the resolvers that reach them in the app (#228 item 3): <see cref="Resolve"/> (every
        /// content verb — the app's FindControlPane walks FindPaneBy's cover tail) and
        /// <see cref="FindSes"/> (the app's FindSesForTarget family — rename, context, notify, flag,
        /// overlay, seen, scratch, split, background — where a cover id lands on the session it
        /// covers), both after the real panes and the name arm; session.restore reaches them to
        /// refuse. NOT <see cref="Find"/>, the app's session-only resolver behind select / close /
        /// reorder / move, which never reaches a pane or a cover. Added with <see cref="AddCoverPane"/>.
        /// Putting a cover into the pane lists desynchronised the index-paired PaneIds /
        /// RestoreCommands (revmux r2 of P2); keeping it out of the resolvers made session.type on a
        /// cover fail here while the app types (r3); letting Find reach it let session.close on a
        /// cover id close the covering session here while the app refuses (#228 item 3). The app's
        /// window-level quick terminal covers NO session and is not modelled: <see cref="AddCoverPane"/>
        /// mints only <c>:scratch:</c> ids.</summary>
        public readonly List<(string Id, ISession Pane)> CoverPanes = new();
        /// <summary>The PANE overlay slots (P5), keyed by pane id — what the app keeps in Pane.Overlay.
        /// Keyed by id like the pins and the captured slots, so a slot travels with its pane through a
        /// swap and goes with it through <see cref="RemovePane"/>, as the app's does by living on the
        /// Pane. An entry exists once anything was asked of that slot (its <see cref="PaneOverlaySlot.LastResult"/>
        /// outlives the term, as the app's does); <see cref="PaneOverlaySlot.Open"/> says whether one is up.
        /// The open term is ALSO in <see cref="CoverPanes"/> under its overlay id, so
        /// <c>--target &lt;overlay id&gt;</c> reaches it through the same resolvers a scratch cover uses.</summary>
        public readonly Dictionary<string, PaneOverlaySlot> PaneOverlays = new();
        /// <summary>The selection each SURFACE holds, keyed by the surface's id — a cover's id or a
        /// real pane's (<see cref="PaneIds"/>) — as the app's lives on its Pane and is read by
        /// SelectionText(pane). Written by <c>selection all</c> whenever the surface has a grid — the
        /// app's SelectAll decides by geometry, not content, so a blank pane holds its blank rows (the
        /// whole grid as SelectionText renders it: every history and screen row, trailing spaces
        /// dropped, CRLF between rows, so an 80x24 blank pane is 46 chars of CRLF) — read by
        /// <c>session copy</c> (a real pane's, or the active surface's) and <c>session overlay copy</c>
        /// (a cover's), dropped by <c>selection clear</c> and by <c>selection copy</c> (the app's
        /// CopySelection(clear: true), whether or not it had a character to copy), kept by
        /// <c>selection finalize</c>. Never the clipboard. A cover
        /// and the pane under it are two keys, which is what lets a test prove <c>session copy</c> and
        /// <c>overlay copy</c> read two different surfaces.</summary>
        public readonly Dictionary<string, string> Selections = new();
        /// <summary>A scratch cover over this session, id "&lt;session id&gt;:scratch:&lt;n&gt;" as the app
        /// spells it (Program.Sessions.cs). Returns the pane id.</summary>
        public string AddCoverPane()
        {
            string id = Id + ":scratch:" + Guid.NewGuid().ToString("N")[..6];
            CoverPanes.Add((id, new TerminalSession(80, 24)));
            return id;
        }
        /// <summary>The slot of pane <paramref name="index"/>, made on first ask.</summary>
        public PaneOverlaySlot SlotOf(int index)
        {
            string paneId = PaneIds[index];
            if (!PaneOverlays.TryGetValue(paneId, out var slot)) PaneOverlays[paneId] = slot = new PaneOverlaySlot();
            return slot;
        }
        /// <summary>The tree's <c>paneOverlays</c>: the open slots as <see cref="OverlayPanes"/>' words,
        /// in pane order — read off the pane list, so a swap is read back the way the app reads it.</summary>
        public List<string> PaneOverlayWords()
        {
            var words = new List<string>();
            for (int i = 0; i < PaneIds.Count; i++)
                if (PaneOverlays.TryGetValue(PaneIds[i], out var slot) && slot.Open) words.Add(OverlayPanes.Word(i));
            return words;
        }
        /// <summary>Close an open pane slot: the term leaves <see cref="CoverPanes"/> (its id resolves
        /// nowhere afterwards), its selection goes, the slot keeps its last result — the app's
        /// ClosePaneOverlay (task 2).</summary>
        public void ClosePaneOverlay(PaneOverlaySlot slot)
        {
            if (!slot.Open) return;
            CoverPanes.RemoveAll(c => c.Id == slot.Id);
            Selections.Remove(slot.Id!);
            slot.Id = null; slot.Term = null; slot.Exited = false; slot.Wait = false;
        }
        /// <summary>Split this session: adds a pane and returns it, for the multi-pane status cases.</summary>
        public ISession AddPane()
        {
            var p = new TerminalSession(80, 24);
            Panes.Add(p); PaneIds.Add("p" + Guid.NewGuid().ToString("N")[..8]); PaneCount = Panes.Count;
            return p;
        }
        /// <summary>Remove pane <paramref name="index"/> with its per-pane state (pin, slot, foreground) — in
        /// the app the Pane record and its shell go together. The survivor keeps its id whichever slot it
        /// was in, the ratio sequence collapses to a single 1.0 and focus lands on slot 0: the fake's one
        /// primitive behind <c>split off</c> (slot 1 goes) and <c>split close</c> (the targeted slot goes),
        /// as ClosePane is the app's.</summary>
        public void RemovePane(int index)
        {
            string gone = PaneIds[index];
            RestorePins.Remove(gone); Captured.Remove(gone); Foreground.Remove(gone);
            // The pane overlay dies with its pane (P5): the app's ClosePane disposes Pane.Overlay too.
            if (PaneOverlays.Remove(gone, out var slot)) ClosePaneOverlay(slot);
            Panes.RemoveAt(index); PaneIds.RemoveAt(index); PaneCount = Panes.Count;
            Ratios = PaneCount > 1 ? Enumerable.Repeat(1.0 / PaneCount, PaneCount).ToList() : new() { 1.0 };
            FocusedPane = Math.Clamp(index, 0, Panes.Count - 1);
        }
        /// <summary>Seals pane 0's id to the session id once <see cref="Id"/> is set.</summary>
        public Sess Seed() { if (PaneIds.Count == 0) PaneIds.Add(Id); else PaneIds[0] = Id; return this; }
    }
    /// <summary>A pane's overlay slot, the shape task 2 gives the app's Pane.Overlay: the term while
    /// one is open (null = empty), whether the caller asked to keep it after exit (<c>--wait</c>),
    /// whether its program has exited, and the LAST result — <c>exit N</c> once a program has exited
    /// in this slot, <see cref="OverlayPanes.NoResult"/> until then and again after an open (every
    /// open resets it, as the session-wide open resets the window-wide value).</summary>
    internal sealed class PaneOverlaySlot
    {
        public string? Id;
        public ISession? Term;
        public bool Wait, Exited;
        public int ExitCode;
        public string LastResult = OverlayPanes.NoResult;
        public bool Open => Term is not null;
    }
    internal sealed class Ws { public string Id = "", Name = ""; public List<Sess> Sessions = new(); }

    internal readonly List<Ws> Workspaces = new();
    internal Ws ActiveWs;
    internal Sess? ActiveSess;
    internal bool SidebarVisible = true, QuickVisible, Broadcast;
    /// <summary>The app's _sidebarWShown: the width the sidebar has when visible, kept while hidden.</summary>
    internal int SidebarW = SidebarWidths.Default;
    internal readonly Dictionary<string, string> Config = new();
    /// <summary>The app's TerminalConfig.CopyOnSelect, default false as the product's: <c>selection
    /// finalize</c> copies only when it is on, and says so either way.</summary>
    internal bool CopyOnSelect;
    /// <summary>Stand-in for the app's process query failing or timing out: restore.capture must then
    /// refuse (nothing written) rather than report "nothing running" for every pane.</summary>
    internal bool CaptureFails;
    /// <summary>The app's <c>restore-commands</c> toggle as the fake sees it: the config key, so a test
    /// drives it through <c>config.set</c> the way a caller does. Gates the REPLAY, never the capture.</summary>
    internal bool RestoreCommands => Config.TryGetValue("restore-commands", out var v) && v == "true";
    private int _idSeq;

    public FakeSessionHost()
    {
        var w = new Ws { Id = "w1", Name = "workspace 1" };
        var s = new Sess { Id = "s1", Name = "session 1" }.Seed();
        w.Sessions.Add(s);
        Workspaces.Add(w);
        ActiveWs = w; ActiveSess = s;
    }

    // The app's session-only Find (Program.Sessions.cs, "Resolve a session-only control target"):
    // null/""/"active" = the active session, else an exact session id, an id PREFIX, then a UNIQUE
    // name — never a split pane's own id, never a cover. Behind SelectSession, CloseSession,
    // SessionReorder and SessionToWorkspace, as in the app. Before #228 item 3 the fake's one Find
    // also matched pane ids and fell through to covers, so `session.close <cover id>` removed the
    // covering session here while the app answered "session not found".
    private Sess? Find(string? t)
    {
        if (string.IsNullOrEmpty(t) || t == "active") return ActiveSess;
        var all = Workspaces.SelectMany(w => w.Sessions).ToList();
        if (all.FirstOrDefault(s => s.Id == t) is { } exact) return exact;
        if (all.FirstOrDefault(s => s.Id.StartsWith(t, StringComparison.Ordinal)) is { } byPrefix) return byPrefix;
        var named = all.Where(s => string.Equals(s.Name, t, StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count == 1 ? named[0] : null;   // an ambiguous name resolves to nothing, as in the app
    }

    // The app's FindSesForTarget: the SESSION a pane-capable target lands on — FindControlPane's
    // order (exact pane, exact session, pane prefix, session prefix / name) and then its cover tail,
    // so a scratch / overlay cover id resolves to the session it covers. This is the resolver behind
    // rename, context, duplicate, split, seen, scratch, overlay, notify, flag and background in the
    // app, and it is why `session context` typed from a CLI inside a scratch pane (whose
    // AGWINTERM_SESSION_ID is the cover's id) lands on the session under it rather than being refused.
    private Sess? FindSes(string? t)
    {
        if (string.IsNullOrEmpty(t) || t == "active") return ActiveSess;
        return FindPane(t)?.s ?? FindCover(t)?.s;
    }
    // The app's FindWs: null/""/"active" = the active workspace, else an id or an id PREFIX — and
    // never a name. The fake used to match names here too, so a test that placed a session by
    // `--workspace <name>` passed against the fake while the app refused it: the same double-drift
    // P1's verification found in Resolve. Names are reached only through workspaceName on NewSession.
    private Ws? FindWs(string? t) =>
        string.IsNullOrEmpty(t) || t == "active" ? ActiveWs
        : Workspaces.FirstOrDefault(w => w.Id == t) ?? Workspaces.FirstOrDefault(w => w.Id.StartsWith(t, StringComparison.Ordinal));

    // A pane, not a session, and in the app's order: active surface, then ANY pane by id or id
    // prefix, then a session by name -> its FOCUSED pane. While a pane carries the session id (pane
    // 0 until a swap moves it; a restore keeps the order it saved), an id target lands on THAT pane
    // and never on the focused pane; that
    // is deliberate, and it is the guarantee session.text / session.type rely on (the pane you CHECK
    // is the pane you then type into). While no pane carries the id — the carrier was closed, by any
    // path — it resolves like a name, to the focused pane (P4, the session-id rule by condition).
    // Per-session panes are what let the tree's statusChangedAt aggregate for real.
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
        // The app's FindControlPane, arm for arm: exact pane, exact SESSION → its focused pane, pane
        // prefix, session prefix / unique name → its focused pane. While a pane carries the session id
        // (until the carrier was closed, by any path — `split close` on it, or `split off` on a swapped
        // session, whose collapse removes slot 1), the exact-pane arm takes the session id
        // first and the exact-session arm is never reached for it; once that pane is gone the session
        // id still resolves — to the survivor, the focused pane — exactly as win32-control.ps1's
        // "exact session id resolves content verbs to the surviving regular pane" pins for the app.
        var sessions = Workspaces.SelectMany(w => w.Sessions).ToList();
        static (Sess s, int pane) AtFocus(Sess s) => (s, Math.Clamp(s.FocusedPane, 0, s.Panes.Count - 1));
        if (FindPaneById(target, exactOnly: true) is { } exactPane) return exactPane;
        if (sessions.FirstOrDefault(s => s.Id == target) is { } exactSession) return AtFocus(exactSession);
        if (FindPaneById(target) is { } byPrefix) return byPrefix;
        if (sessions.FirstOrDefault(s => s.Id.StartsWith(target, StringComparison.Ordinal)) is { } sessionPrefix) return AtFocus(sessionPrefix);
        var named = sessions.Where(s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count == 1 ? AtFocus(named[0]) : null;   // an ambiguous name resolves to nothing
    }

    /// <summary>A cover pane by exact id or id prefix — the tail of the app's FindPaneBy, after the
    /// real panes. Resolve and FindSes reach it (every content verb and the FindSesForTarget family,
    /// as in the app); session.restore reaches it to refuse; Find and the tree never see it.</summary>
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
    private (Sess s, int pane)? FindPaneById(string id, bool exactOnly = false)
    {
        var sessions = Workspaces.SelectMany(w => w.Sessions).ToList();
        var preds = new List<Func<string, bool>> { pid => pid == id };
        if (!exactOnly) preds.Add(pid => pid.StartsWith(id, StringComparison.Ordinal));
        foreach (var pred in preds)
            foreach (var s in sessions)
                for (int i = 0; i < s.Panes.Count; i++)
                    if (i < s.PaneIds.Count && pred(s.PaneIds[i])) return (s, i);
        return null;
    }

    /// <summary>The workspace a session lives in — what the app reads as <c>Ses.Ws</c>.</summary>
    internal Ws WorkspaceOf(Sess s) => Workspaces.First(w => w.Sessions.Contains(s));

    /// <summary>The focused pane's SURFACE: its overlay term while that pane's slot is open, else the
    /// pane — the app's ActiveSurface rule (P5), so no target / "active" reaches a pane overlay the way
    /// it reaches a cover today, and the pane's own id reaches the shell underneath. (The session-wide
    /// slot has no term in the fake, so it does not take part.)</summary>
    private static ISession? Focused(Sess? s)
    {
        if (s is null) return null;
        int i = Math.Clamp(s.FocusedPane, 0, s.Panes.Count - 1);
        if (i < s.PaneIds.Count && s.PaneOverlays.TryGetValue(s.PaneIds[i], out var slot) && slot.Open) return slot.Term;
        return s.Panes[i];
    }

    public IReadOnlyList<WorkspaceSnapshot> Tree() => Workspaces.Select(w => new WorkspaceSnapshot(
        w.Id, w.Name, ReferenceEquals(w, ActiveWs),
        w.Sessions.Select(s =>
        {
            var (status, statusChangedAt) = s.StatusAndChangedAt;   // one reading, as Program.ControlHost.Tree does
            return new SessionSnapshot(s.Id, s.Name, ReferenceEquals(s, ActiveSess), status,
                s.Overlay, s.Notifications, s.Flagged, false, s.FocusedPane, s.PaneCount, false, s.OverlaySize, s.Ratios,
                PaneIds: s.PaneIds,
                RestoreCommands: s.PaneIds.Select(id => s.RestorePins.TryGetValue(id, out var pin) ? pin : "").ToList(),   // "" = none, as the app's snapshot spells it; parallel to PaneIds, covers in neither
                StatusChangedAt: statusChangedAt,
                Context: s.Context,
                CapturedCommands: s.PaneIds.Select(id => s.Captured.TryGetValue(id, out var c) ? c : "").ToList(),   // the slot, "" = none, parallel to PaneIds
                Axis: s.Axis,
                PaneOverlays: s.PaneOverlayWords());   // the open pane slots as words; empty = the tree omits the key (P5)
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
        var session = FindSes(target);   // the app measures through FindControlPane: pane-capable, cover-aware
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
        var src = FindSes(target) ?? ActiveSess;
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
    // Real, not a stub, and in the app's order: resolve (null/"" = every real pane in tree order,
    // "active" = the active session's focused pane, else the session.restore resolver — a cover is
    // refused, an unknown target is refused, both BEFORE the "query"), then the query (Foreground,
    // or a refusal when CaptureFails), then every slot write at once. A capture of nothing removes
    // the slot entry (the app writes null), so an earlier checkpoint is overridden, not kept.
    public RestoreCaptureResult RestoreCapture(string? target)
    {
        var snap = new List<(Sess s, string paneId)>();
        if (string.IsNullOrEmpty(target))
        {
            foreach (var s in Workspaces.SelectMany(w => w.Sessions))
                foreach (var id in s.PaneIds) snap.Add((s, id));
        }
        else if (target == "active")
        {
            if (ActiveSess is not { } a) return RestoreCaptureResult.Refuse(RestoreCaptureReply.UnknownTarget(target));
            snap.Add((a, a.PaneIds[Math.Clamp(a.FocusedPane, 0, a.PaneIds.Count - 1)]));
        }
        else if (FindPane(target) is { } hit) snap.Add((hit.s, hit.s.PaneIds[hit.pane]));
        else if (FindCover(target) is { } cover) return RestoreCaptureResult.Refuse(RestoreCaptureReply.CoverPane(cover.id));
        else return RestoreCaptureResult.Refuse(RestoreCaptureReply.UnknownTarget(target));

        if (CaptureFails) return RestoreCaptureResult.Refuse(RestoreCaptureReply.QueryFailed);

        var landed = new List<CapturedPane>();
        foreach (var (s, paneId) in snap)
        {
            if (s.Foreground.TryGetValue(paneId, out var running)) s.Captured[paneId] = running; else s.Captured.Remove(paneId);
            landed.Add(new CapturedPane(paneId, s.Id, s.Captured.TryGetValue(paneId, out var c) ? c : null));
        }
        return new RestoreCaptureResult(landed, RestoreCommands);
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
    public bool SetFontSize(string? target, string op) => FindSes(target) is not null;   // the app: Find, then FindControlPane — pane-capable overall
    public bool Dashboard(bool close, string? ids, int fontSize) => true;

    public void SessionGo(string dir) { }
    public bool SessionReorder(string? target, string dir) => Find(target) is not null;
    public bool SessionToWorkspace(string? target, string workspace) { var s = Find(target); var w = FindWs(workspace); if (s is null || w is null) return false; Workspaces.First(x => x.Sessions.Contains(s)).Sessions.Remove(s); w.Sessions.Add(s); return true; }
    public bool SessionRename(string? target, string name) { var s = FindSes(target); if (s is null || string.IsNullOrWhiteSpace(name)) return false; s.Name = name; return true; }
    // Real, not a stub: resolves as rename does (FindSes, so a cover id lands on its session and an
    // unknown target is the app's "session not found"), stores what the server already validated,
    // and replies with the value read back off the session — the app's InvokeOnUiQueued reply.
    public string SessionContext(string? target, string? context)
    {
        var s = FindSes(target);
        if (s is null) return ISessionHost.RefusePrefix + SessionContexts.NoSession;
        if (context is not null && SessionContexts.Validate(context) is { } bad) throw new ArgumentException("the server should have refused this: " + bad, nameof(context));   // the fake's tripwire, as SidebarWidth's
        s.Context = context;
        return SessionContexts.Reply(s.Id, s.Context);
    }
    public bool SessionSeen(string? target) { var s = FindSes(target); if (s is null) return false; s.Notifications = 0; return true; }
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
    public string ReadOnlyOp(string? target, string op) { var s = FindSes(target); if (s is null) return "off"; s.ReadOnly = op switch { "on" => true, "off" => false, "toggle" => !s.ReadOnly, _ => s.ReadOnly }; return s.ReadOnly ? "on" : "off"; }
    public string SessionOutput(string? target) => "";
    public bool WorkspaceRename(string? target, string name) { var w = FindWs(target); if (w is null || string.IsNullOrWhiteSpace(name)) return false; w.Name = name; return true; }
    public bool WorkspaceDelete(string? target) { var w = FindWs(target); if (w is null || Workspaces.Count <= 1) return false; Workspaces.Remove(w); if (ReferenceEquals(ActiveWs, w)) { ActiveWs = Workspaces[0]; ActiveSess = ActiveWs.Sessions.FirstOrDefault(); } return true; }
    public bool WorkspaceSelect(string? target) { var w = FindWs(target); if (w is null) return false; ActiveWs = w; ActiveSess = w.Sessions.FirstOrDefault(); return true; }
    public bool WorkspaceReorder(string? target, string dir) => FindWs(target) is not null;
    public string Split(string? target, string op, string? axis)
    {
        // Mirrors the app: the target resolves like every other session verb (null/"active",
        // a session or pane id, or a prefix), and the split lands on THAT session - not on
        // whichever one is active. Toggle is modelled too, since the app's default op is toggle.
        // The split pane is MINTED (AddPane), never counted — the reply is its id, read back off the
        // pane list after the op exactly as the app reads ses.Panes, so a test can check that the id
        // it got is the id the tree lists. `on` when on and `off` when off change nothing and answer
        // the pane in slot 1 / slot 0. The axis word is the server's to validate; here it is applied
        // as the app's SplitOp applies it: set on `on` / a splitting toggle (also a live
        // re-orientation when already split), ignored by `off`, kept across a collapse.
        var s = FindSes(target);
        if (s is null) return ISessionHost.RefusePrefix + "session not found";
        bool split = s.PaneCount > 1;
        bool want = op switch { "on" => true, "off" => false, _ => !split };
        if (want && axis is not null) s.Axis = axis;
        if (want && !split) { s.AddPane(); s.Ratios = new() { 0.5, 0.5 }; s.FocusedPane = 1; }   // the app focuses the new pane
        else if (!want && split)
        {
            // pane 0 survives (the app's CollapseToSinglePane → ClosePane on slot 1); the others go with their per-pane state
            while (s.PaneCount > 1) s.RemovePane(s.PaneCount - 1);
        }
        return s.PaneIds[s.PaneCount > 1 ? 1 : 0];
    }
    // session.split.close (P4), the app's rules: null/""/"active" = the active session's focused pane
    // (Ctrl+Shift+W's target); else FindPane's order (exact pane, exact session → focused, pane prefix,
    // session prefix / name → focused) — and while a pane carries the session id, the session id
    // names THAT pane, not the focused one (while none does, the focused one — this verb is one of
    // the paths that removes the carrier). A cover is refused (not a side of a split), an
    // unknown target is refused, a one-pane session is refused naming `session close`; each leaves
    // everything as it was. The reply is the survivor's id, read back off slot 0 after the removal, as
    // the app reads ses.Panes[0].
    public string SplitClose(string? target)
    {
        Sess? s; int idx;
        if (string.IsNullOrEmpty(target) || target == "active")
        {
            s = ActiveSess;
            if (s is null) return ISessionHost.RefusePrefix + SplitCloseReply.NoActiveSession;
            idx = Math.Clamp(s.FocusedPane, 0, s.Panes.Count - 1);
        }
        else if (FindPane(target) is { } hit) { s = hit.s; idx = hit.pane; }
        else if (FindCover(target) is { } cover) return ISessionHost.RefusePrefix + SplitCloseReply.CoverPane(cover.id);
        else return ISessionHost.RefusePrefix + SplitCloseReply.UnknownTarget(target);
        if (s.PaneCount <= 1) return ISessionHost.RefusePrefix + SplitCloseReply.SinglePane(s.Id);
        s.RemovePane(idx);
        return s.PaneIds[0];
    }
    // session.swap (P4), the app's SwapPanes: the target resolves as split close's does (null/""/"active"
    // = the active session; else FindPane's order — a session id, either pane's id, a prefix, or a name
    // — and the SESSION the hit belongs to is what is swapped; a cover and an unknown target are
    // refused; a one-pane session is refused). Then the pane order is reversed and focus follows the
    // pane; the RATIO SEQUENCE is kept — the fake's Ratios is a per-SLOT list, so keeping it untouched is
    // the same rule the app applies by exchanging the two panes' own shares; the axis is kept; every id
    // is kept, and the per-pane dictionaries (pins, slots, foregrounds) are keyed by pane id, so they
    // travel with their pane as the app's Pane fields do. The reply is read back off the session.
    public SwapResult Swap(string? target)
    {
        Sess? s;
        if (string.IsNullOrEmpty(target) || target == "active")
        {
            s = ActiveSess;
            if (s is null) return SwapResult.Refuse(SwapReply.NoActiveSession);
        }
        else if (FindPane(target) is { } hit) s = hit.s;
        else if (FindCover(target) is { } cover) return SwapResult.Refuse(SwapReply.CoverPane(cover.id));
        else return SwapResult.Refuse(SwapReply.UnknownTarget(target));
        if (s.PaneCount <= 1) return SwapResult.Refuse(SwapReply.SinglePane(s.Id));
        s.Panes.Reverse(); s.PaneIds.Reverse();
        s.FocusedPane = s.Panes.Count - 1 - Math.Clamp(s.FocusedPane, 0, s.Panes.Count - 1);
        return new SwapResult(s.Id, s.PaneIds.ToList(), s.FocusedPane, s.Axis);
    }
    // The same rules as the app's host: the words and their axis are SplitAxes' (a direction that
    // does not exist on the session's axis is refused with focus unmoved), one pane is refused.
    public string FocusPaneDir(string dir)
    {
        if (ActiveSess is not { PaneCount: > 1 } a) return ISessionHost.RefusePrefix + SplitAxes.NotSplit;
        if (!SplitAxes.TryFocusIndex(dir, a.Axis, a.FocusedPane, out int index, out string? refusal)) return ISessionHost.RefusePrefix + refusal;
        a.FocusedPane = index;
        return "focus";
    }
    /// <summary>The fake has no cells: a grow step moves the ratio by 0.02 per cell, clamped to the app's
    /// 0.05..0.95, which is enough for a test to see that a refused request moved nothing and an
    /// accepted one moved the divider the way it asked.</summary>
    public string ResizeSplit(double? ratio, int growLeft, int growRight, int growTop, int growBottom)
    {
        if (ActiveSess is not { PaneCount: > 1 } a) return ISessionHost.RefusePrefix + SplitAxes.NoDivider;
        if (!SplitAxes.TryGrow(a.Axis, growLeft, growRight, growTop, growBottom, out int shift, out string? refusal)) return ISessionHost.RefusePrefix + refusal;
        double first = ratio ?? Math.Clamp(a.Ratios[0] + shift * 0.02, 0.05, 0.95);
        a.Ratios = new() { first, 1 - first };
        return "resized";
    }
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
    // The selection verbs, session.copy and session.paste act on the SURFACE a target resolves to
    // (Surface: the app's PaneForTarget) and on that surface's entry in Sess.Selections, with the
    // app's replies. A target that resolves to no surface is the app's refusal on the five
    // (ISessionHost.SelectionAll), so a test against the fake asserts the app's ok:false; session.copy
    // never sees one — the server refuses it with the read verbs (Resolve), before this is called.
    private const string NoPane = ISessionHost.RefusePrefix + SessionContexts.NoSession;
    public string SessionCopy(string? target)
        => Surface(target) is { } sf ? (sf.s.Selections.TryGetValue(sf.id, out var sel) ? sel : "") : "";
    public string SelectionAll(string? target)
    {
        if (Surface(target) is not { } sf) return NoPane;
        // The app's SelectAll: HasSel = (hist + rows) > 0 && cols > 0 — geometry, never content, so a
        // blank pane is "selected all" and holds its blank rows. "empty" is a surface with no grid,
        // which a fake pane (80x24 from birth) never is; it is kept for the shape, not reached.
        string? text = WholeGrid(sf.pane);
        if (text is null) return "empty";
        sf.s.Selections[sf.id] = text; return "selected all";
    }
    /// <summary>What the app's SelectionText renders for a whole-grid selection: every history row and
    /// then the screen, each row's trailing spaces dropped, CRLF between rows and nothing after the
    /// last — NOT SurfaceText.Dump, which is LF-joined with trailing blank rows trimmed (a multi-row
    /// count in <c>copied N chars</c> would differ). Null for a grid with no cells.</summary>
    private static string? WholeGrid(ISession pane)
    {
        var sb = new StringBuilder();
        lock (pane.SyncRoot)
        {
            var em = pane.Emulator;
            int rows = em.Screen.Rows, cols = em.Screen.Cols, hist = em.HistoryCount;
            if (rows + hist <= 0 || cols <= 0) return null;
            for (int abs = 0; abs < hist + rows; abs++)
            {
                if (abs > 0) sb.Append("\r\n");
                sb.Append((abs < hist ? em.DumpHistoryRow(abs) : em.DumpRow(abs - hist)).TrimEnd(' '));
            }
        }
        return sb.ToString();
    }
    public string SelectionCopy(string? target)
    {
        if (Surface(target) is not { } sf) return NoPane;
        if (!sf.s.Selections.Remove(sf.id, out var sel)) return "no selection";
        // The app's CopySelection: the clipboard is set only when the text has a copyable character,
        // the selection is cleared either way; N counts UTF-16 units, as string.Length does.
        return HasCopyable(sel) ? $"copied {sel.Length} chars" : "nothing to copy";
    }
    public string SelectionClear(string? target)
    {
        if (Surface(target) is not { } sf) return NoPane;
        sf.s.Selections.Remove(sf.id); return "cleared";
    }
    // The app's three arms in its order: copy-on-select off answers before the selection is looked at
    // (FinalizeSelection short-circuits on the flag), so a default-configured host never says
    // "(copied)" or "(empty)"; a test that wants those arms sets CopyOnSelect first. "(copied)" is
    // CopySelection(clear: false) succeeding — the same whitespace rule as `selection copy`, the
    // selection kept — so a blank grid's selection finalizes "(empty)" and survives.
    public string SelectionFinalize(string? target)
        => Surface(target) is not { } sf ? NoPane
         : !CopyOnSelect ? "finalized (copy-on-select off)"
         : sf.s.Selections.TryGetValue(sf.id, out var sel) && HasCopyable(sel) ? "finalized (copied)" : "finalized (empty)";
    /// <summary>The app's CopySelection rule: a character other than CR, LF or space (IndexOfAnyExcept).</summary>
    private static bool HasCopyable(string sel) => sel.AsSpan().IndexOfAnyExcept('\r', '\n', ' ') >= 0;
    public string SessionPaste(string? target, string? text) => Surface(target) is not null ? "pasted" : NoPane;
    /// <summary>The app's PaneForTarget, with the surface's id: null / "" / "active" is the active
    /// session's focused pane — or the overlay term open over it, as ActiveSurface says — then a real
    /// pane by FindPane, then a cover by id or prefix (FindPaneBy's tail, after the real panes; an
    /// empty prefix never reaches it, so "" cannot match every cover).</summary>
    private (Sess s, string id, ISession pane)? Surface(string? target)
    {
        if (string.IsNullOrEmpty(target) || target == "active")
        {
            if (ActiveSess is not { } a || a.Panes.Count == 0) return null;
            int i = Math.Clamp(a.FocusedPane, 0, a.Panes.Count - 1);
            if (i < a.PaneIds.Count && a.PaneOverlays.TryGetValue(a.PaneIds[i], out var slot) && slot.Open) return (a, slot.Id!, slot.Term!);
            return (a, a.PaneIds[i], a.Panes[i]);
        }
        if (FindPane(target) is { } hit) return (hit.s, hit.s.PaneIds[hit.pane], hit.s.Panes[hit.pane]);
        return FindCover(target);
    }
    /// <summary>A NAMED target that is a cover — null / "" / "active" never is (an empty prefix would
    /// match every cover), and a real pane's id resolves before a cover's, as in FindPaneBy.</summary>
    private (Sess s, string id, ISession pane)? CoverTarget(string? target)
        => string.IsNullOrEmpty(target) || target == "active" || FindPane(target) is not null ? null : FindCover(target);
    public string SessionSearch(string? target, string? query, string? action) => "no matches";
    public bool SessionScratch(string? target, string op) => FindSes(target) is not null;
    public void Quick(string op) { QuickVisible = op switch { "on" => true, "off" => false, "toggle" => !QuickVisible, _ => QuickVisible }; }
    // Mirrors the app: no clamp, because ControlServer.TryOverlaySize refuses out-of-range before the
    // host is reached. The range check here is the fake's own tripwire — a test that drives the host
    // directly with a bad size must see a refusal, not a silently-coerced panel.
    private const string NoOverlaySession = ISessionHost.RefusePrefix + "no session matches that target; nothing opened, resized or closed";

    public string SessionOverlay(string? target, string action, string? command, int sizePercent, bool wait, bool block, string? pane, OverlayTextArgs text)
    {
        // The pane word is parsed FIRST, before any resolve (the app's order, task 4): a bad word is
        // refused with nothing looked up. The server refused it already; this is the fake's own
        // guard, so a test that drives the host directly gets the same answer.
        if (!OverlayPanes.TryParse(pane, out int index, out string? paneRefusal)) return ISessionHost.RefusePrefix + paneRefusal;
        // Then, as the app: resize / a size with a pane, refused before any resolve (revmux r1 of P5:
        // the fake checked them AFTER the pane count, so a size on a single-pane session answered
        // "pane not visible" here and the size refusal in the app). A pane overlay's OWN id on the
        // target with the word omitted names its slot, with the two refusals that name the id.
        if (index != OverlayPanes.SessionWide)
        {
            if (action == "resize") return ISessionHost.RefusePrefix + OverlayPanes.ResizeWithPaneRefusal;
            if (sizePercent != 0) return ISessionHost.RefusePrefix + OverlayPanes.SizeWithPaneRefusal;
            return PaneOverlay(target, action, command, wait, index, text);
        }
        if (PaneOverlayIndexOf(target) is >= 0 and var owned)
        {
            if (action == "resize") return ISessionHost.RefusePrefix + OverlayPanes.OverlayIdResizeRefusal(target!, owned);
            if (sizePercent != 0) return ISessionHost.RefusePrefix + OverlayPanes.OverlayIdSizeRefusal(target!, owned);
            return PaneOverlay(target, action, command, wait, owned, text);
        }

        // The SESSION-WIDE slot, as the app, in the app's ORDER and WORDING (the suite asserts
        // wording, so the two hosts must not drift), for open, close and resize — the cases
        // ISessionHost.SessionOverlay's refusal list names: open checks the command before the
        // session; open and resize are refused whenever NO session resolves; close is refused only
        // when a target other than absent, empty or "active" resolves to nothing (`named`), and is
        // ok ("no overlay") for those three with nothing active, or on a session that exists and has
        // no overlay — idempotent, the conformance contract closes with nothing open; resize with no
        // overlay open is a refusal. NOT mirrored: open's reply (the fake answers the SESSION id; the
        // app answers the overlay pane id, `<session>:overlay:<hex>`), `result` (no arm — it falls
        // into open's "needs a command" refusal, where the app answers its last exit), the app's
        // refusal of a target that names one pane of a split session (OverlayTargetRefusal), and the
        // session-wide slot's TERM: the fake's is a flag with no buffer, so copy / text on it answer
        // "no overlay" with none and "overlay not realized" with one — the phrase for a slot whose
        // surface has no emulator, which here is literally so. The pane slots have real terms.
        var s = FindSes(target);
        bool named = !string.IsNullOrEmpty(target) && target != "active";
        if (sizePercent is < 0 or > 100) return ISessionHost.RefusePrefix + $"size-percent {sizePercent} is outside 0..100";
        switch (action)
        {
            case "close":
                if (s is null) return named ? NoOverlaySession : "no overlay";
                if (!s.Overlay) return "no overlay";
                s.Overlay = false; s.OverlaySize = 0; return "closed";
            case "resize":
                if (s is null) return NoOverlaySession;
                if (!s.Overlay) return ISessionHost.RefusePrefix + "no overlay to resize on that target; open one first";
                s.OverlaySize = sizePercent; return $"resized {s.OverlaySize}%";
            case "copy":
            case "text":
                if (s is null) return NoOverlaySession;
                if (!s.Overlay) return ISessionHost.RefusePrefix + OverlayPanes.NoOverlayRefusal(OverlayPanes.SessionWide);
                return ISessionHost.RefusePrefix + OverlayPanes.NotRealizedRefusal(s.Id + ":overlay");
            default:
                if (string.IsNullOrWhiteSpace(command)) return ISessionHost.RefusePrefix + "overlay open needs a command; nothing opened";
                if (s is null) return NoOverlaySession;
                s.Overlay = true; s.OverlaySize = sizePercent; return s.Id;
        }
    }

    /// <summary>The PANE slot (P5), in the order the app's host takes (task 4), every refusal's
    /// wording from <see cref="OverlayPanes"/>: the command (open only) before the session, as the
    /// session-wide arm; no session → the same "no session" refusal (a bare close with nothing active
    /// stays ok "no overlay"); a <c>--target</c> that is a PANE id on the other side than <c>--pane</c>
    /// is refused (the agreement check; the session id, a name, or the same side pass); an index past
    /// the pane count is "pane not visible" (a size with a pane, or resize with a pane, was refused by
    /// the caller before any resolve, as the app's SessionOverlay does). Then per action: open mints
    /// <c>&lt;pane id&gt;:overlay:&lt;hex&gt;</c> with a real term (a second open on a held slot is
    /// REFUSED — no silent replace), close answers "closed" / ok "no overlay", result answers the
    /// slot's last result or its two refusals, copy the cover's selection or its refusals, text the
    /// term's buffer. NOT mirrored: <c>block</c> (nothing runs here; the reply is the id as for a
    /// non-blocking open) — a test of it needs the app.</summary>
    private string PaneOverlay(string? target, string action, string? command, bool wait, int index, OverlayTextArgs text)
    {
        bool named = !string.IsNullOrEmpty(target) && target != "active";
        if (action is "open" && string.IsNullOrWhiteSpace(command)) return ISessionHost.RefusePrefix + "overlay open needs a command; nothing opened";
        var s = FindSes(target);
        if (s is null) return action == "close" && !named ? "no overlay" : NoOverlaySession;
        if (named && !(s.Id == target || s.Id.StartsWith(target!, StringComparison.Ordinal)))
            for (int i = 0; i < s.PaneIds.Count; i++)
            {
                if ((s.PaneIds[i] == target || s.PaneIds[i].StartsWith(target!, StringComparison.Ordinal)) && i != index)
                    return ISessionHost.RefusePrefix + OverlayPanes.Disagree(target!, i, index);
                // The pane's overlay id too (the app's LocatePaneSlot): naming the other side's overlay is naming two panes.
                if (s.PaneOverlays.TryGetValue(s.PaneIds[i], out var other) && other.Id is { } oid
                    && (oid == target || oid.StartsWith(target!, StringComparison.Ordinal)) && i != index)
                    return ISessionHost.RefusePrefix + OverlayPanes.Disagree(target!, i, index, overlay: true);
            }
        if (index >= s.Panes.Count) return ISessionHost.RefusePrefix + OverlayPanes.NotVisibleRefusal(s.Id);
        var slot = s.SlotOf(index);
        switch (action)
        {
            case "close":
                if (!slot.Open) return "no overlay";
                s.ClosePaneOverlay(slot);
                return "closed";
            case "result":
                if (slot.Open && !slot.Exited) return ISessionHost.RefusePrefix + OverlayPanes.StillRunning;
                if (slot.LastResult == OverlayPanes.NoResult) return ISessionHost.RefusePrefix + OverlayPanes.NoResult;
                return slot.LastResult;
            case "copy":
                if (!slot.Open) return ISessionHost.RefusePrefix + OverlayPanes.NoOverlayRefusal(index);
                if (!s.Selections.TryGetValue(slot.Id!, out var sel) || sel.Length == 0) return ISessionHost.RefusePrefix + OverlayPanes.NoSelection;
                return sel;
            case "text":
                if (!slot.Open) return ISessionHost.RefusePrefix + OverlayPanes.NoOverlayRefusal(index);
                try { return SurfaceText.Dump(slot.Term!, text); }
                catch (Exception e) { return ISessionHost.RefusePrefix + OverlayPanes.ReadFailedRefusal(e.Message); }
            default: // "open"
                if (slot.Open) return ISessionHost.RefusePrefix + OverlayPanes.AlreadyOpenRefusal(index);
                string id = s.PaneIds[index] + ":overlay:" + Guid.NewGuid().ToString("N")[..6];
                var term = new TerminalSession(80, 24);
                slot.Id = id; slot.Term = term; slot.Wait = wait; slot.Exited = false; slot.ExitCode = 0;
                slot.LastResult = OverlayPanes.NoResult;
                s.CoverPanes.Add((id, term));
                return id;
        }
    }

    /// <summary>The app's PaneOverlayIndexOf: the pane whose OPEN overlay <paramref name="target"/>
    /// names (its id or a prefix of it, through <see cref="CoverTarget"/> — a real pane's id resolves
    /// first, so a pane id, itself a prefix of its overlay's id, still means the session-wide slot), or
    /// -1 for anything else.</summary>
    private int PaneOverlayIndexOf(string? target)
    {
        if (CoverTarget(target) is not { } hit) return -1;
        for (int i = 0; i < hit.s.PaneIds.Count; i++)
            if (hit.s.PaneOverlays.TryGetValue(hit.s.PaneIds[i], out var slot) && slot.Id == hit.id) return i;
        return -1;
    }

    /// <summary>The fake's stand-in for the app's WatchOverlayExit on a pane slot (task 2): the
    /// program in pane <paramref name="index"/>'s overlay exits with <paramref name="code"/> — the slot
    /// records <c>exit N</c>, and without <c>--wait</c> the overlay closes; with it the term stays
    /// up (the banner) until a close.</summary>
    internal void ExitPaneOverlay(Sess s, int index, int code)
    {
        var slot = s.SlotOf(index);
        if (!slot.Open) throw new InvalidOperationException("no overlay is open in that slot");
        slot.Exited = true; slot.ExitCode = code; slot.LastResult = $"exit {code}";
        if (!slot.Wait) s.ClosePaneOverlay(slot);
    }
    public bool Notify(string? target, string? title, string body) { var s = FindSes(target); if (s is null) return false; s.Notifications++; return true; }
    public bool SessionFlag(string? target, string op) { if (op == "clear") { foreach (var s in Workspaces.SelectMany(w => w.Sessions)) s.Flagged = false; return true; } var x = FindSes(target); if (x is null) return false; x.Flagged = op switch { "on" => true, "off" => false, "toggle" => !x.Flagged, _ => x.Flagged }; return true; }
    public bool SessionBind(string? target, string agent) { var s = FindSes(target); /* the app: FindPaneById, a pane, so pane-capable */ if (s is null) return false; s.AgentResume = string.IsNullOrWhiteSpace(agent) || agent == "none" ? null : agent; return true; }
    public string AdoptClaude() { int n = 0; foreach (var s in Workspaces.SelectMany(w => w.Sessions)) { s.AgentResume = "claude --resume x"; n++; } return $"adopted {n}"; }
    public string RestartClaudeYolo(string? target) { var s = FindSes(target); if (s is null) return "no pane"; s.AgentResume = "claude --resume x --dangerously-skip-permissions"; return "restarting Claude in YOLO mode (resumed)"; }
    public string UpdateClaude() => "updating Claude Code…";
    public string UpdateApp() => "updating agwinterm…";
    public void WorkspaceFocus(string op) { }
    public string SessionBackground(string? target, string action, string? path, int opacity, string? mode) => FindSes(target) is not null ? "ok" : "no session";
    public string SessionSwitch(string op) => ActiveSess?.Name ?? "";
    public string CommandRun(string nameOrCommand, string? mode) => $"{mode ?? "new"}: {nameOrCommand}";
    public string CommandList() => "";
    public string CommandLeader(string op) => "idle";
    /// <summary>The fake knows one theme, "fake-theme"; every other name is the app's not-found refusal.</summary>
    public string OmpSet(string nameOrPath, bool persist)
        => string.Equals(nameOrPath, "fake-theme", StringComparison.OrdinalIgnoreCase)
            ? "oh-my-posh theme set: " + nameOrPath
            : ISessionHost.RefusePrefix + OmpThemes.NotFound(nameOrPath);
}
