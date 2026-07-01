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

    /// <summary>Create a session (optionally in a given workspace); returns its id.</summary>
    string NewSession(string? name, string? cwd, string? workspace);

    bool SelectSession(string target);
    bool CloseSession(string target);

    /// <summary>Create a workspace; returns its id.</summary>
    string NewWorkspace(string? name);
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
    public string NewSession(string? name, string? cwd, string? workspace) => "single";
    public bool SelectSession(string target) => true;
    public bool CloseSession(string target) => false;
    public string NewWorkspace(string? name) => "ws";
}
