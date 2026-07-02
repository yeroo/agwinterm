using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>A session's metadata for the control-API tree.</summary>
public sealed record SessionSnapshot(string Id, string Name, bool Active, AgentStatus Status);

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
        string? workspaceName = null, bool createWorkspace = false);

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

    /// <summary>Current text selection of the target session's active pane ("" if none).</summary>
    string SessionCopy(string? target);
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
        string? workspaceName = null, bool createWorkspace = false) => "single";
    public bool SelectSession(string target) => true;
    public bool CloseSession(string target) => false;
    public string NewWorkspace(string? name) => "ws";
    public bool SetFontSize(string? target, string op) => false; // no per-session font zoom in the single-session host

    // Wave A1 verbs — no-ops for the single-session test adapter.
    public void SessionGo(string dir) { }
    public bool SessionReorder(string? target, string dir) => false;
    public bool SessionToWorkspace(string? target, string workspace) => false;
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
    public string SessionCopy(string? target) => "";
}
