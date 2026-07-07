using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>A session's metadata for the control-API tree.</summary>
public sealed record SessionSnapshot(string Id, string Name, bool Active, AgentStatus Status, bool Overlay = false, int Notifications = 0, bool Flagged = false, bool Background = false);

/// <summary>A workspace (with its sessions) for the control-API tree.</summary>
public sealed record WorkspaceSnapshot(string Id, string Name, bool Active, IReadOnlyList<SessionSnapshot> Sessions);

/// <summary>
/// The control server's view of the app. Lets it target a session by id / unique-prefix /
/// "active" (or null = active), enumerate the workspace→session tree, and create/select/close
/// sessions and workspaces. The app (MainWindow) implements this; mutating methods marshal to
/// the UI thread.
/// </summary>
public interface ISessionHost
{
    TerminalSession? Resolve(string? target);

    /// <summary>The full workspace→session tree.</summary>
    IReadOnlyList<WorkspaceSnapshot> Tree();

    /// <summary>
    /// Create a session; returns its id. Optionally in a workspace (by id/prefix via
    /// <paramref name="workspace"/>, or by sidebar label via <paramref name="workspaceName"/> +
    /// <paramref name="createWorkspace"/>), running <paramref name="command"/> as its process
    /// instead of the shell.
    /// </summary>
    string NewSession(string? name, string? cwd, string? workspace, string? command = null,
        string? workspaceName = null, bool createWorkspace = false, string? profile = null);

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

    // ---- Wave A1: verb parity for existing features (all marshal to the UI thread) ----

    /// <summary>Move the active session: dir = next|prev|first|last|next-attention|prev-attention.</summary>
    void SessionGo(string dir);
    /// <summary>Reorder a session within its workspace: dir = up|down|top|bottom.</summary>
    bool SessionReorder(string? target, string dir);
    /// <summary>Relocate a session to another workspace (by id/prefix/active), appending.</summary>
    bool SessionToWorkspace(string? target, string workspace);

    /// <summary>Rename a session: sets its custom name (shown in the sidebar and title bar).</summary>
    bool SessionRename(string? target, string name);

    /// <summary>Clear a session's unseen-notification badge without visiting it (headless "seen").</summary>
    bool SessionSeen(string? target);

    /// <summary>Sidebar state read-back: "visible tree" | "hidden flagged" | ….</summary>
    string SidebarState();

    bool WorkspaceRename(string? target, string name);
    bool WorkspaceDelete(string? target);
    bool WorkspaceSelect(string? target);
    /// <summary>Reorder a workspace among its siblings: dir = up|down|top|bottom.</summary>
    bool WorkspaceReorder(string? target, string dir);

    /// <summary>Split the active session: op = on|off|toggle.</summary>
    void Split(string op);
    /// <summary>Move pane focus in the active session: dir = left|right|other.</summary>
    void FocusPaneDir(string dir);
    /// <summary>Set the active session's split: an absolute left ratio (0..1) or grow left/right by N columns.</summary>
    void ResizeSplit(double? ratio, int growLeft, int growRight);

    IReadOnlyList<string> ThemeList();
    bool ThemeSet(string name);
    string KeymapReload();
    string RestoreClear();
    /// <summary>Sidebar: op = show|hide|toggle|expand|collapse.</summary>
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
    /// <summary>Copy the target pane's current selection to the Windows clipboard.</summary>
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
    /// Overlay control. action = open|close|result. For open: run <paramref name="command"/> in an
    /// ephemeral terminal over the target session; sizePercent 0 = full-region, 1..100 = a centered
    /// floating panel; wait = keep it after the program exits (press a key to close); block = wait for
    /// the program to exit and return its status. Returns the session id (open), "exit N" (block/result),
    /// "closed", or "no overlay".
    /// </summary>
    string SessionOverlay(string? target, string action, string? command, int sizePercent, bool wait, bool block);

    /// <summary>Raise a desktop notification against a session (in-app banner + sidebar badge + OS tray balloon).
    /// Returns false if the target isn't found.</summary>
    bool Notify(string? target, string? title, string body);

    /// <summary>Flag/unflag a session (durable working-set flag): op = on|off|toggle|clear (clear unflags every session).
    /// Returns false if a per-session op targets a session that isn't found; "clear" always succeeds.</summary>
    bool SessionFlag(string? target, string op);

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
    private readonly TerminalSession _session;
    public SingleSessionHost(TerminalSession session) => _session = session;

    public TerminalSession? Resolve(string? target) => _session;
    public IReadOnlyList<WorkspaceSnapshot> Tree() =>
        new[] { new WorkspaceSnapshot("ws", "workspace", true,
            new[] { new SessionSnapshot("single", "session", true, _session.Status) }) };
    public string NewSession(string? name, string? cwd, string? workspace, string? command = null,
        string? workspaceName = null, bool createWorkspace = false, string? profile = null) => "single";
    public string ProfilesList() => "Windows PowerShell";
    public string ProfilesReload() => "0 profiles loaded";
    public bool SelectSession(string target) => true;
    public bool CloseSession(string target) => false;
    public string NewWorkspace(string? name) => "ws";
    public bool SetFontSize(string? target, string op) => false; // no per-session font zoom in the single-session host

    // Wave A1 verbs — no-ops for the single-session test adapter.
    public void SessionGo(string dir) { }
    public bool SessionReorder(string? target, string dir) => false;
    public bool SessionToWorkspace(string? target, string workspace) => false;
    public bool SessionRename(string? target, string name) => false;
    public bool SessionSeen(string? target) => false;
    public string SidebarState() => "visible tree";
    public bool WorkspaceRename(string? target, string name) => false;
    public bool WorkspaceDelete(string? target) => false;
    public bool WorkspaceSelect(string? target) => false;
    public bool WorkspaceReorder(string? target, string dir) => false;
    public void Split(string op) { }
    public void FocusPaneDir(string dir) { }
    public void ResizeSplit(double? ratio, int growLeft, int growRight) { }
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
    public void WorkspaceFocus(string op) { }
    public string SessionBackground(string? target, string action, string? path, int opacity, string? mode) => "unsupported";
    public string SessionSwitch(string op) => "unsupported";
    public string CommandRun(string nameOrCommand, string? mode) => "unsupported";
    public string CommandList() => "";
    public string CommandLeader(string op) => "idle";
    public string OmpSet(string nameOrPath, bool persist) => "unsupported";
}
