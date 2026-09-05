using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>A session's metadata for the control-API tree. <see cref="FocusedPane"/>/<see cref="PaneCount"/>/
/// <see cref="SplitRatios"/> describe its split layout; <see cref="StatusBlink"/> is the attention pulse;
/// <see cref="OverlaySize"/> is an open overlay's size-percent (0 = none/full);
/// <see cref="StatusChangedAt"/> is epoch seconds of the last status write on the pane whose status
/// won the aggregate — the age of the status actually shown; <see cref="Context"/> is the session's
/// <c>session context</c> text (null = none; the tree omits the key); <see cref="CapturedCommands"/>
/// is each pane's captured restore slot ("" = none), parallel to <see cref="PaneIds"/> like
/// <see cref="RestoreCommands"/> — the <c>restore.capture</c> read-back (P3); <see cref="Axis"/> is
/// the session's split orientation, one of <see cref="SplitAxes"/>' words (null = vertical), emitted
/// by the tree only while the session is split (P4). New optional fields go
/// at the END: both hosts and <see cref="SingleSessionHost"/> construct this positionally.</summary>
public sealed record SessionSnapshot(string Id, string Name, bool Active, AgentStatus Status,
    bool Overlay = false, int Notifications = 0, bool Flagged = false, bool Background = false,
    int FocusedPane = 0, int PaneCount = 1, bool StatusBlink = false, int OverlaySize = 0,
    IReadOnlyList<double>? SplitRatios = null, IReadOnlyList<string>? PaneIds = null,
    IReadOnlyList<string>? RestoreCommands = null, long StatusChangedAt = 0,
    string? Context = null, IReadOnlyList<string>? CapturedCommands = null,
    string? Axis = null);

/// <summary>A workspace (with its sessions) for the control-API tree.</summary>
public sealed record WorkspaceSnapshot(string Id, string Name, bool Active, IReadOnlyList<SessionSnapshot> Sessions);

/// <summary>Where a <c>session.restore</c> call landed: the pane the target resolved to and the session
/// that owns it, so the reply can name both — a split has several panes and the caller cannot otherwise
/// tell which one now carries the pin. <see cref="Refusal"/> non-null means the pane exists but cannot
/// carry a pin (a scratch / overlay / quick cover is never restored) and nothing was changed.</summary>
public sealed record RestorePinTarget(string PaneId, string SessionId, string? Refusal = null);

/// <summary>What <c>sidebar width</c> reports: <see cref="Width"/> is the width the sidebar has in
/// DIP — on screen when <see cref="Visible"/>, remembered and applied on the next show when not. The
/// two together are what lets the server say "remembered, not applied" for a set while hidden instead
/// of reporting a width nobody can see.</summary>
public sealed record SidebarWidthSnapshot(int Width, bool Visible);

/// <summary>Window-level UI state for the control-API read side (sidebar/fullscreen/zoom/quick-terminal
/// visibility + which workspace/session is active).</summary>
public sealed record WindowStateSnapshot(bool SidebarVisible, bool Fullscreen, bool Maximized,
    bool QuickTerminalVisible, string? ActiveWorkspace, string? ActiveSession);

/// <summary>A pane's live geometry for `session.metrics`, in DEVICE pixels — the space a producer's
/// frame buffer lives in, not the DIPs the chrome lays out in. <see cref="WidthPx"/>/<see cref="HeightPx"/>
/// are the exact rendered grid extent; the integer cell fields are compatibility hints because rounding
/// one fractional cell and multiplying accumulates error. A host with no UI reports zeros.</summary>
public sealed record PaneMetricsSnapshot(int Cols, int Rows, int CellWidth, int CellHeight, int WidthPx, int HeightPx)
{
    /// <summary>
    /// Build device-pixel metrics from the exact floating-point grid step used by rendering.
    /// CellWidth/CellHeight remain integer compatibility hints; WidthPx/HeightPx accumulate the
    /// unrounded step across the grid and are the authoritative sharp-frame extent.
    /// </summary>
    public static PaneMetricsSnapshot FromDipGrid(
        int cols, int rows, float cellWidthDip, float cellHeightDip, float dpiScale)
        => new(
            cols, rows,
            Math.Max(1, (int)MathF.Round(cellWidthDip * dpiScale)),
            Math.Max(1, (int)MathF.Round(cellHeightDip * dpiScale)),
            Math.Max(0, (int)MathF.Round(cols * cellWidthDip * dpiScale)),
            Math.Max(0, (int)MathF.Round(rows * cellHeightDip * dpiScale)));
}

/// <summary>
/// The control server's view of the app. Lets it target a session by id / unique-prefix /
/// "active" (or null = active), enumerate the workspace→session tree, and create/select/close
/// sessions and workspaces. The app (MainWindow) implements this; mutating methods marshal to
/// the UI thread.
/// </summary>
public interface ISessionHost
{
    /// <summary>Marker a host puts in front of a verb's result to mean "refused, and here is why" —
    /// the control server turns it into an ok:false error. Needed where the host returns a string
    /// rather than a bool, so a refusal cannot be mistaken for a result.</summary>
    public const string RefusePrefix = "refuse:";

    ISession? Resolve(string? target);

    /// <summary>The full workspace→session tree.</summary>
    IReadOnlyList<WorkspaceSnapshot> Tree();

    /// <summary>Window-level UI state (sidebar/fullscreen/zoom/quick-terminal visibility + active ws/session).</summary>
    WindowStateSnapshot WindowState();

    /// <summary>Live cell + pane pixel metrics for <paramref name="target"/> (pane id / session id /
    /// "active" / null), in device pixels. Null = this host cannot measure (no UI, or the target has no
    /// pane in the layout); the control server then answers zeros rather than an error, because a
    /// consumer treats a zero cell size as "no metrics" and a hard error as a broken terminal.
    /// Default: null, so a host that does not draw need not pretend to know its geometry.</summary>
    PaneMetricsSnapshot? PaneMetrics(string? target) => null;

    /// <summary>
    /// Create a session; returns its id. Optionally in a workspace (by id/prefix via
    /// <paramref name="workspace"/>, or by sidebar label via <paramref name="workspaceName"/> +
    /// <paramref name="createWorkspace"/>), running <paramref name="command"/> as its process
    /// instead of the shell. A workspace that does not resolve — an unknown id/prefix, or an
    /// unknown name without <paramref name="createWorkspace"/> — is <b>refused</b> with
    /// <see cref="RefusePrefix"/> + the <see cref="SessionNewWorkspaces"/> wording, and no session
    /// is created; it is never swapped for the active workspace (P2, decision 1). The host resolves
    /// the workspace before it mints the id, so a refusal cannot leave an orphan behind ok:false.
    /// <para>With neither workspace argument the session lands in the workspace of the
    /// <paramref name="caller"/>'s pane — the pane that ran <c>session new</c>, which the CLI sends
    /// from its <c>AGWINTERM_SESSION_ID</c> — so an agent gets sessions next to itself. The ACTIVE
    /// workspace is the <b>last</b> answer, not the first: it is a global the UI rewrites on every
    /// click and selection, and reading it made a bare <c>session new</c> land wherever the user
    /// had last clicked (P2, task 5a). It is used only when there is no caller, or the caller's pane
    /// no longer exists (the agent that typed the command has since been closed, or a script from an
    /// unrelated shell); a stale caller is <b>not</b> refused, because refusing would break a working
    /// script to fix a preference. Resolved synchronously, before the creation is posted.</para>
    /// </summary>
    string NewSession(string? name, string? cwd, string? workspace, string? command = null,
        string? workspaceName = null, bool createWorkspace = false, string? profile = null, bool noSelect = false, bool wait = false,
        string? caller = null);

    /// <summary>Clone a session (same cwd + shell profile); returns the new session id. (agterm #234)</summary>
    string DuplicateSession(string? target);

    /// <summary>List the configured shell profiles (name, command, default marker).</summary>
    string ProfilesList();
    /// <summary>Reload profiles.json from disk.</summary>
    string ProfilesReload();

    bool SelectSession(string target);
    bool CloseSession(string target);

    /// <summary>Create a workspace; returns its id.</summary>
    string NewWorkspace(string? name);

    /// <summary>Adjust a session's font zoom: op = "inc" | "dec" | "reset". Returns false if the target isn't found.</summary>
    bool SetFontSize(string? target, string op);
    /// <summary>Open/close the dashboard grid: ids = comma/space-separated session ids (empty = MRU),
    /// close dismisses it, fontSize pins a cell font size (0 = auto).</summary>
    bool Dashboard(bool close, string? ids, int fontSize);

    // ---- Wave A1: verb parity for existing features (all marshal to the UI thread) ----

    /// <summary>Move the active session: dir = next|prev|first|last|next-attention|prev-attention.</summary>
    void SessionGo(string dir);
    /// <summary>Reorder a session within its workspace: dir = up|down|top|bottom.</summary>
    bool SessionReorder(string? target, string dir);
    /// <summary>Relocate a session to another workspace (by id/prefix/active), appending.</summary>
    bool SessionToWorkspace(string? target, string workspace);

    /// <summary>Rename a session: sets its custom name (shown in the sidebar and title bar). Resolves
    /// the target the way every content verb does (exact pane, exact session, pane prefix, session
    /// prefix / name — a scratch or overlay cover id lands on the session it covers). False when no
    /// session resolves, the name is blank, or the window is closing and the rename could not be
    /// queued (#228 item 5: the post's result is the reply, not a constant true).</summary>
    bool SessionRename(string? target, string name);

    /// <summary>
    /// <c>session.context</c>: set (or, with <paramref name="context"/> null, clear) a session's
    /// context — the one-line "what is this pane for" shown dimmed beside the name in the title bar
    /// and the sidebar row, carried in <c>tree</c> as <c>context</c>, and persisted so it survives a
    /// restart (P3). The server has already normalized and validated the text through
    /// <see cref="SessionContexts"/> (control characters, blank, over-length and text-beside-clear are
    /// refused before this is reached), so the host stores what it is given.
    /// <para><b>Resolution</b> is <see cref="SessionRename"/>'s: null / "" / "active" is the active
    /// session, else exact pane, exact session, pane prefix, session prefix / unique name; a scratch
    /// or overlay cover id resolves to the session it covers, because a CLI launched inside a cover
    /// inherits the cover's id and "this session" is the one under it. The window-level quick
    /// terminal covers no session and is refused.</para>
    /// <para><b>Returns</b> the JSON reply <see cref="SessionContexts.Reply"/> builds —
    /// <c>{"session":id,"context":text|null}</c> — naming the session the value landed on and the
    /// value IN EFFECT after the write, read back off the session rather than echoed from the
    /// request; the server emits it raw. A target that resolves to no session returns
    /// <see cref="ISessionHost.RefusePrefix"/> + <see cref="SessionContexts.NoSession"/> and changes
    /// nothing.</para>
    /// <para><b>Threading</b>: the write is applied on the UI thread through the FIFO queued hop (the
    /// same queue every posted action travels), and the calling pipe thread waits for it, so the
    /// reply describes a state that exists. A hop that cannot be queued or run (the window is
    /// closing, or a message loop that never pumps within the bound) throws, which the server turns
    /// into ok:false — never ok:true for a write that did not land. The session is resolved INSIDE
    /// the hop so a session closed between the request and the write is refused, not written to.</para>
    /// </summary>
    string SessionContext(string? target, string? context);

    /// <summary>Clear a session's unseen-notification badge without visiting it (headless "seen").</summary>
    bool SessionSeen(string? target);

    /// <summary>Sidebar state read-back: <c>"&lt;visible|hidden&gt; &lt;tree|flagged&gt; &lt;width&gt;"</c>,
    /// e.g. "visible tree 220" — visibility, mode and width in one call. The width is the one in
    /// effect when shown (see <see cref="SidebarWidth"/>); "hidden" beside it says it is not on screen.</summary>
    string SidebarState();

    /// <summary>sidebar.width: read (<paramref name="set"/> null) or set the sidebar width in DIP and
    /// report the width actually in effect afterwards. The server has already refused anything outside
    /// <see cref="SidebarWidths.Min"/>..<see cref="SidebarWidths.Max"/>, so the host stores and applies
    /// without clamping — a clamp here could only hide a bug. A set while the sidebar is hidden is
    /// remembered (it is what the next show uses, and it is persisted) but the divider does not move;
    /// the snapshot's <see cref="SidebarWidthSnapshot.Visible"/> false is how the server knows to say so.
    /// A set while visible goes through the same re-layout the toggle does: the content origin, the
    /// grid, hit-testing and the column count all derive from the width.</summary>
    SidebarWidthSnapshot SidebarWidth(int? set);

    /// <summary>Broadcast-input toggle for the frontmost window: op = on|off|toggle|state. Returns "on"/"off".</summary>
    string BroadcastOp(string op);

    /// <summary>Read-only toggle for a target pane: op = on|off|toggle|state. Returns "on"/"off".</summary>
    string ReadOnlyOp(string? target, string op);

    /// <summary>Plain text of the last completed command's output (FTCS/OSC 133 marks).</summary>
    string SessionOutput(string? target);

    bool WorkspaceRename(string? target, string name);
    bool WorkspaceDelete(string? target);
    /// <summary>Collapse (expand=false) or expand (true) a single workspace by id; null target = active.</summary>
    bool WorkspaceCollapse(string? target, bool expand);
    bool WorkspaceSelect(string? target);
    /// <summary>Reorder a workspace among its siblings: dir = up|down|top|bottom.</summary>
    bool WorkspaceReorder(string? target, string dir);

    /// <summary>
    /// <c>session.split</c>: split or collapse the targeted session (null/"active" = the active one):
    /// op = on|off|toggle. <paramref name="axis"/> is <see cref="SplitAxes.Vertical"/> (left/right
    /// panes — the default of a session never split) or <see cref="SplitAxes.Horizontal"/> (top/bottom
    /// panes), already parsed by the server through <see cref="SplitAxes.TryParse"/>; null keeps the
    /// session's current orientation. The axis is PER SESSION and survives <c>off</c> (agterm:
    /// omitting the flag preserves the orientation, so a later <c>on</c> without one splits the way
    /// the session was last split); <c>on</c> WITH an axis on an already-split session re-orients it
    /// live and still answers the existing split pane's id; <c>off</c> ignores the axis.
    /// <para><b>Returns a pane id</b> — a bare string, so the caller can address the shell it just
    /// asked for (P4; before, the constant "split" from a Post-and-return-true, so the id was only
    /// knowable by diffing the tree). Per op: <c>on</c> → the split pane's id, ALSO when the session
    /// was already split (the caller that does not know whether it split gets something addressable
    /// either way, and nothing changes); <c>off</c> → the survivor's id (pane 0), also when already
    /// single; <c>toggle</c> → whichever it produced. Or <see cref="RefusePrefix"/> + a refusal —
    /// <c>session not found</c>, the axis refusal — and nothing split.</para>
    /// <para><b>Invariants (#230)</b>: the target is resolved on the caller's thread so an unknown
    /// target answers a refusal with nothing queued; the split lands on THAT session, not on whichever
    /// is active; splitting a session that is not the active one does not move focus to it (the
    /// split appears when the user next selects it). The reply is read back off the session INSIDE
    /// the same FIFO UI hop the write travelled (<see cref="SessionContext"/>'s threading), so the id
    /// names a pane that exists; a hop that cannot be queued or run is a refusal.</para>
    /// </summary>
    string Split(string? target, string op, string? axis);
    /// <summary>
    /// <c>session.split.close</c> (P4): close ONE pane of a split session — EITHER side — and reply
    /// with the survivor's id (a bare string, the shape every <c>session split</c> reply has). The
    /// survivor becomes pane 0 with the full width or height and the focus; the session keeps its id,
    /// name, context, flag, axis (for the next split), overlay and scratch. <c>session split off</c>
    /// keeps its own rule (pane 0 survives — agterm's <c>off</c> minus the hide); this verb is the
    /// one that can close pane 0 of two, which before P4 only Ctrl+Shift+W on a focused pane 0 could.
    /// <para><b>Target</b>: null / "" / "active" = the active session's focused pane (what Ctrl+Shift+W
    /// closes); else the content verbs' resolver (exact pane, exact session → its focused pane, pane
    /// prefix, session prefix / name → its focused pane). Exactly one pane carries the session id, so
    /// the session id names THAT pane through the exact-pane arm; a session NAME names the focused
    /// pane. <see cref="SplitCloseReply"/> has the wording of every refusal, each with nothing closed:
    /// an unknown target; a scratch / overlay / quick cover (not a side of a split); a ONE-PANE session
    /// (<c>session close</c> is the verb for that — a <c>split close</c> that closed the session would
    /// be the silent-success class one verb over).</para>
    /// <para><b>Invariants (#230)</b>: resolved on the caller's thread so a bad target answers a refusal
    /// with nothing queued; the close lands on THAT session, not on whichever is active, and closing a
    /// pane of a non-active session does not move focus to it; the pane is removed and the reply read
    /// back INSIDE the same FIFO UI hop, re-resolving there, so a pane that exited or was closed in
    /// between is refused rather than double-closed; a hop that cannot be queued or run is a refusal.</para>
    /// </summary>
    string SplitClose(string? target);
    /// <summary>
    /// <c>session.swap</c> (P4): exchange the two panes of a split session and reply with the session's
    /// split block after the swap — <see cref="SwapResult"/>, which <see cref="SwapReply.Build"/> spells
    /// as <c>{"session","paneIds","focusedPane","axis"}</c> (an object, the one <c>session split</c>
    /// family reply that is not a bare string). The pane ORDER is reversed, the FOCUS follows the pane,
    /// the axis is kept, the ratio SEQUENCE is kept (the left/top box stays the size it was; the two
    /// panes exchange their shares), and EVERY ID IS KEPT — a swap moves panes, never ids, so an agent
    /// holding a pane id keeps reaching the same shell. That relaxes one invariant: "the first pane
    /// shares the session id" becomes "exactly one pane carries the session id, and a swap may put it
    /// on either side"; the resolver's exact-pane-first order is what keeps the session id naming that
    /// pane wherever it sits, and restore keeps the saved ids verbatim rather than re-minting pane 0.
    /// <see cref="SwapReply"/> has what else was checked and found session-wide or order-independent.
    /// <para><b>Target</b>: null / "" / "active" = the active session; else the content verbs' resolver
    /// (a session id, either pane's id, a prefix, or a session name) — the session either pane belongs
    /// to. Refusals, each with nothing moved (<see cref="SwapReply"/>'s wording): an unknown target; a
    /// scratch / overlay / quick cover; a ONE-PANE session (nothing to exchange).</para>
    /// <para><b>Invariants (#230)</b>: resolved on the caller's thread so a bad target answers a refusal
    /// with nothing queued; the swap lands on THAT session, not on whichever is active, and swapping a
    /// non-active session does not move focus to it; the panes are exchanged and the reply read back
    /// INSIDE the same FIFO UI hop, re-resolving there; a hop that cannot be queued or run is a refusal.</para>
    /// </summary>
    SwapResult Swap(string? target);
    /// <summary><c>session.focus</c>: move pane focus in the active session. dir is one of agterm's
    /// words — <c>primary|split|left|right|top|bottom|other</c> — judged against the session's axis by
    /// <see cref="SplitAxes.TryFocusIndex"/> (<c>top</c> on a vertical split is refused naming the
    /// axis). Returns <c>"focus"</c>, or <see cref="RefusePrefix"/> + a refusal with focus unmoved:
    /// the axis refusal, an unknown word, or <see cref="SplitAxes.NotSplit"/> for a one-pane session
    /// (P4; before, every case answered ok having possibly done nothing).</summary>
    string FocusPaneDir(string dir);
    /// <summary><c>session.resize</c>: set the active session's split — an absolute ratio (0..1) for
    /// the first (left/top) pane, or move the divider by N cells: <c>growLeft</c>/<c>growRight</c>
    /// in columns on a vertical split, <c>growTop</c>/<c>growBottom</c> in rows on a horizontal one
    /// (<see cref="SplitAxes.TryGrow"/>; flags from the other axis are refused naming the axis, and
    /// the divider does not move). Returns <c>"resized"</c>, or <see cref="RefusePrefix"/> + the
    /// refusal — also <see cref="SplitAxes.NoDivider"/> for a one-pane session.</summary>
    string ResizeSplit(double? ratio, int growLeft, int growRight, int growTop, int growBottom);

    IReadOnlyList<string> ThemeList();
    bool ThemeSet(string name);
    string KeymapReload();
    string RestoreClear();
    /// <summary>Sidebar: op = show|hide|toggle|expand|collapse|mode:tree|mode:flagged|mode:toggle. The
    /// server refuses anything else before calling (and folds on/off into show/hide), so an op that
    /// reaches here is one the host handles — before P2 an unknown op fell through the host's switch
    /// and the verb still answered ok:true.</summary>
    void SidebarOp(string op);

    /// <summary>Set a config key (persists to agwinterm.conf + applies live). Returns an ack.</summary>
    string ConfigSet(string key, string value);
    /// <summary>Current value of a config key.</summary>
    string ConfigGet(string key);
    /// <summary>All config keys with their current values ("key = value" per line).</summary>
    string ConfigList();
    /// <summary>Open the Settings window.</summary>
    string SettingsOpen();

    /// <summary>Current text selection of the target session's active pane ("" if none).</summary>
    string SessionCopy(string? target);

    /// <summary>Select the target pane's whole buffer (scrollback + live grid).</summary>
    string SelectionAll(string? target);
    /// <summary>Copy the target pane's current selection to the Windows clipboard. A live selection
    /// whose cells hold no text — a full-screen app repainted over them — copies nothing and leaves
    /// the clipboard untouched, answering "nothing to copy".</summary>
    string SelectionCopy(string? target);
    /// <summary>Clear the target pane's selection.</summary>
    string SelectionClear(string? target);
    /// <summary>Run the selection-finalize path (honors copy-on-select) — for scripting/testing.</summary>
    string SelectionFinalize(string? target);
    /// <summary>Paste text (or the clipboard when text is null/empty) into the target pane, honoring bracketed paste.</summary>
    string SessionPaste(string? target, string? text);

    /// <summary>Open/drive the find bar over the active session; returns "N of M" / "no matches" / "closed".</summary>
    string SessionSearch(string? target, string? query, string? action);

    /// <summary>Toggle/show/hide a session's scratch terminal: op = on|off|toggle. Returns false if the target isn't found.</summary>
    bool SessionScratch(string? target, string op);

    /// <summary>Toggle/show/hide the window's quick terminal: op = on|off|toggle.</summary>
    void Quick(string op);

    /// <summary>
    /// Overlay control. action = open|close|resize|result. For open: run <paramref name="command"/> in
    /// an ephemeral terminal over the target session; sizePercent 0 = full-region, 1..100 = a centered
    /// floating panel; wait = keep it after the program exits (press a key to close); block = wait for
    /// the program to exit and return its status. Returns the session id (open), "exit N"
    /// (block/result), "closed", "resized N%", or "no overlay" (deliberately ok, two states: close on
    /// a session that resolves and has no overlay, and an untargeted close that resolves no session).
    /// A FAILURE is signalled by prefixing <see cref="RefusePrefix"/>, which the server turns into
    /// ok:false: open with no command; open or resize whenever NO session resolves (a named target
    /// that matches nothing, or no target and no active session); close only when a non-empty,
    /// non-"active" target resolves to nothing; resize with no overlay open. A second host that
    /// returns those as plain strings reproduces the ok:true-on-failure P2 removed.
    /// </summary>
    string SessionOverlay(string? target, string action, string? command, int sizePercent, bool wait, bool block);

    /// <summary>Raise a desktop notification against a session (in-app banner + sidebar badge + OS tray balloon).
    /// Returns false if the target isn't found.</summary>
    bool Notify(string? target, string? title, string body);

    /// <summary>Flag/unflag a session (durable working-set flag): op = on|off|toggle|clear (clear unflags every session).
    /// Returns false if a per-session op targets a session that isn't found; "clear" always succeeds.</summary>
    bool SessionFlag(string? target, string op);

    /// <summary>Bind a resumable agent (e.g. "claude") to the target pane so restart re-launches it and the
    /// agent resumes its own session. <paramref name="agent"/> = "" or "none" clears the binding.
    /// Returns false if the target pane isn't found.</summary>
    bool SessionBind(string? target, string agent);
    /// <summary>Pin a restore command on a pane, re-run every restart (agterm #271); <paramref name="command"/>
    /// null clears. The server has already refused an empty / "active" target and folded ""/"none" into
    /// null. Resolves <paramref name="target"/> the way every other content verb does (exact pane, exact
    /// session, pane prefix, session prefix/name) and returns the pane it landed on; null = nothing matched,
    /// and nothing was pinned.</summary>
    RestorePinTarget? SessionRestore(string target, string? command);
    /// <summary>
    /// <c>restore.capture</c> (P3): capture the foreground command of every real pane — or of the one
    /// <paramref name="target"/> names — into its restore slot NOW, and report per pane what was
    /// captured. Before this the capture happened exactly once, in the window's WM_DESTROY, which a
    /// crash, a <c>Stop-Process</c>, a power loss or a missed update-quit never reaches; and every
    /// ordinary save wrote "" into the slot because the captured command had no in-memory field. The
    /// slot is durable now (the app's <c>Pane.CapturedCommand</c>, written by this verb and by the
    /// quit-time capture, read by every save), so a checkpoint survives until the next capture.
    /// <para><b>Target</b>: null / "" = every real pane of every session, in tree order; "active" =
    /// the active session's active pane; else the resolver <c>session.restore</c> uses (exact pane,
    /// exact session → its active pane, pane prefix, session prefix / unique name). An unknown target
    /// is refused with <see cref="RestoreCaptureReply.UnknownTarget"/>; a scratch / overlay / quick
    /// cover (never in the saved tree, so no slot) with <see cref="RestoreCaptureReply.CoverPane"/>.
    /// A refusal captures nothing for anyone and saves nothing.</para>
    /// <para><b>Null captured</b> = the shell had no non-denylisted child: the honest answer, written
    /// into the slot (a fresh capture overrides an earlier checkpoint, including to empty). A process
    /// query that fails or times out is a refusal (<see cref="RestoreCaptureReply.QueryFailed"/>),
    /// never an empty answer for every pane.</para>
    /// <para><b>The toggle</b> (<c>restore-commands</c>) gates only whether the slot is TYPED BACK at
    /// restart, not the capture — the pin ignores it too, and a verb that did nothing on a default
    /// install would be the silent-success class. <see cref="RestoreCaptureResult.ReplayOnRestore"/>
    /// carries it so the caller knows.</para>
    /// <para><b>Threading</b> (the app): the pane + pid snapshot and the CIM query run on the calling
    /// pipe thread with the 15 s timeout the non-quit callers use — never on the UI thread — and
    /// every slot write plus one save land in a single FIFO queued hop; a hop that cannot run throws,
    /// which the server turns into ok:false with nothing written. A pane closed between the snapshot
    /// and the hop is dropped from the reply rather than written to.</para>
    /// </summary>
    RestoreCaptureResult RestoreCapture(string? target);
    /// <summary>Poll the event log for events after <paramref name="since"/> (0 = all buffered), up to
    /// <paramref name="limit"/> (0 = no cap). Returns JSON {cursor, events:[{seq,type,session?,info?}]}. (agterm #273)</summary>
    string Events(long since, int limit);

    /// <summary>One-time migration: bind every pane that has an existing Claude transcript for its cwd to
    /// resume that conversation on restart (for sessions started before the launcher wrapper). Returns a summary.</summary>
    string AdoptClaude();

    /// <summary>Restart the target pane's Claude in YOLO mode (--dangerously-skip-permissions), resuming its
    /// current conversation, and persist that as the pane's relaunch command. Null target = the active pane.</summary>
    string RestartClaudeYolo(string? target);

    /// <summary>The Update Claude Code workflow: run <c>claude update</c> in an overlay terminal over the
    /// active session, then restart every pane with a live Claude so the new version is picked up
    /// (each resumes its own conversation). Returns an ack / error string.</summary>
    string UpdateClaude();

    /// <summary>The agwinterm self-update workflow: download + SHA-256-verify the latest release for
    /// this install channel (installer / portable; package-manager installs are refused with a hint),
    /// then exit, swap, relaunch — sessions restore. Returns an ack / error string.</summary>
    string UpdateApp();

    /// <summary>Focus/unfocus the active workspace (hide the others in the sidebar tree): op = on|off|toggle.</summary>
    void WorkspaceFocus(string op);

    /// <summary>Set/clear a session's background watermark. action = set|clear. For set: <paramref name="path"/> is
    /// the source image (copied into app data), <paramref name="opacity"/> 0..100 (-1 = keep current),
    /// <paramref name="mode"/> = fit|fill|center|tile (null = keep). Returns an ack / "no session".</summary>
    string SessionBackground(string? target, string action, string? path, int opacity, string? mode);

    /// <summary>Drive the MRU (Ctrl+Tab) session switcher state machine directly:
    /// op = begin|advance|advance-back|commit|cancel. Returns the resulting active session name.
    /// Lets the control API / tests exercise the walk without synthetic global key input.</summary>
    string SessionSwitch(string op);

    /// <summary>Run a custom command (by keymap label) or a raw command string, expanding {AGW_*} tokens and
    /// injecting $AGW_* env from the active session. <paramref name="mode"/> (send|new|overlay|detached)
    /// overrides the command's configured mode (raw strings default to "new"). Returns "mode: &lt;expanded&gt;".</summary>
    string CommandRun(string nameOrCommand, string? mode);

    /// <summary>List configured custom commands: one tab-separated "label\tmode\tchord\ttext" line each.</summary>
    string CommandList();

    /// <summary>Drive the leader-chord state machine for observability/testing:
    /// op = state|begin|cancel|"key:&lt;chord&gt;". Returns "idle"|"pending"|the resolution result.</summary>
    string CommandLeader(string op);

    /// <summary>Apply an oh-my-posh theme (by name or .omp.json path) live to the active session's shell
    /// (re-inits oh-my-posh and re-applies the OSC-7 prompt wrap). When <paramref name="persist"/>, also
    /// save it to config so new sessions launch with it. Returns an ack / "not found".</summary>
    string OmpSet(string nameOrPath, bool persist);
}

/// <summary>Adapter exposing a single fixed session as an <see cref="ISessionHost"/> (tests / simple hosts).</summary>
public sealed class SingleSessionHost : ISessionHost
{
    private readonly ISession _session;
    public SingleSessionHost(ISession session) => _session = session;

    public ISession? Resolve(string? target) => _session;
    public IReadOnlyList<WorkspaceSnapshot> Tree() =>
        new[] { new WorkspaceSnapshot("ws", "workspace", true,
            new[] { new SessionSnapshot("single", "session", true, _session.Status,
                                        StatusChangedAt: _session.StatusChangedAt) }) };
    public WindowStateSnapshot WindowState() => new(true, false, false, false, "ws", "single");
    public string NewSession(string? name, string? cwd, string? workspace, string? command = null,
        string? workspaceName = null, bool createWorkspace = false, string? profile = null, bool noSelect = false, bool wait = false,
        string? caller = null) => "single";
    public string DuplicateSession(string? target) => "";
    public string ProfilesList() => "Windows PowerShell";
    public string ProfilesReload() => "0 profiles loaded";
    public bool SelectSession(string target) => true;
    public bool CloseSession(string target) => false;
    public string NewWorkspace(string? name) => "ws";
    public bool SetFontSize(string? target, string op) => false; // no per-session font zoom in the single-session host
    public bool Dashboard(bool close, string? ids, int fontSize) => false; // no dashboard in the single-session host

    // Wave A1 verbs — no-ops for the single-session test adapter.
    public void SessionGo(string dir) { }
    public bool SessionReorder(string? target, string dir) => false;
    public bool SessionToWorkspace(string? target, string workspace) => false;
    public bool SessionRename(string? target, string name) => false;
    public string SessionContext(string? target, string? context) => ISessionHost.RefusePrefix + SessionContexts.NoSession;
    public bool SessionSeen(string? target) => false;
    public string SidebarState() => $"visible tree {SidebarWidths.Default}";
    public SidebarWidthSnapshot SidebarWidth(int? set) => new(SidebarWidths.Default, true);
    public string BroadcastOp(string op) => "off";
    public string ReadOnlyOp(string? target, string op) => "off";
    public string SessionOutput(string? target) => "";
    public bool WorkspaceRename(string? target, string name) => false;
    public bool WorkspaceDelete(string? target) => false;
    public bool WorkspaceCollapse(string? target, bool expand) => false;
    public bool WorkspaceSelect(string? target) => false;
    public bool WorkspaceReorder(string? target, string dir) => false;
    public string Split(string? target, string op, string? axis) => ISessionHost.RefusePrefix + "session not found";
    public string SplitClose(string? target) => ISessionHost.RefusePrefix + SplitCloseReply.SinglePane(target ?? "active");
    public SwapResult Swap(string? target) => SwapResult.Refuse(SwapReply.SinglePane(target ?? "active"));
    public string FocusPaneDir(string dir) => ISessionHost.RefusePrefix + SplitAxes.NotSplit;
    public string ResizeSplit(double? ratio, int growLeft, int growRight, int growTop, int growBottom) => ISessionHost.RefusePrefix + SplitAxes.NoDivider;
    public IReadOnlyList<string> ThemeList() => Array.Empty<string>();
    public bool ThemeSet(string name) => false;
    public string KeymapReload() => "";
    public string RestoreClear() => "";
    public void SidebarOp(string op) { }
    public string ConfigSet(string key, string value) => "unsupported";
    public string ConfigGet(string key) => "";
    public string ConfigList() => "";
    public string SettingsOpen() => "unsupported";
    public string SessionCopy(string? target) => "";
    public string SelectionAll(string? target) => "unsupported";
    public string SelectionCopy(string? target) => "unsupported";
    public string SelectionClear(string? target) => "unsupported";
    public string SelectionFinalize(string? target) => "unsupported";
    public string SessionPaste(string? target, string? text) => "unsupported";
    public string SessionSearch(string? target, string? query, string? action) => "";
    public bool SessionScratch(string? target, string op) => false;
    public void Quick(string op) { }
    public string SessionOverlay(string? target, string action, string? command, int sizePercent, bool wait, bool block) => "no overlay";
    public bool Notify(string? target, string? title, string body) => false;
    public bool SessionFlag(string? target, string op) => false;
    public bool SessionBind(string? target, string agent) => false;
    public RestorePinTarget? SessionRestore(string target, string? command) => null;
    public RestoreCaptureResult RestoreCapture(string? target) =>
        string.IsNullOrEmpty(target) || target == "active"
            ? new RestoreCaptureResult(Array.Empty<CapturedPane>(), false)   // no restore file, no process notion: nothing to capture
            : RestoreCaptureResult.Refuse(RestoreCaptureReply.UnknownTarget(target));
    public string Events(long since, int limit) => "{\"cursor\":0,\"events\":[]}";
    public string AdoptClaude() => "unsupported";
    public string RestartClaudeYolo(string? target) => "unsupported";
    public string UpdateClaude() => "unsupported";
    public string UpdateApp() => "unsupported";
    public void WorkspaceFocus(string op) { }
    public string SessionBackground(string? target, string action, string? path, int opacity, string? mode) => "unsupported";
    public string SessionSwitch(string op) => "unsupported";
    public string CommandRun(string nameOrCommand, string? mode) => "unsupported";
    public string CommandList() => "";
    public string CommandLeader(string op) => "idle";
    public string OmpSet(string nameOrPath, bool persist) => "unsupported";
}
