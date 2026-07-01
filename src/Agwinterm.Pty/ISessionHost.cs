using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>A session's metadata for the control-API tree.</summary>
public sealed record SessionSnapshot(string Id, string Name, bool Active, AgentStatus Status);

/// <summary>
/// The control server's view of the app's sessions. Lets it target a session by id /
/// unique-prefix / "active" (or null = active), enumerate the tree, and create/select/close
/// sessions. The app (MainWindow) implements this; mutating methods marshal to the UI thread.
/// </summary>
public interface ISessionHost
{
    /// <summary>Resolve a target (session id, unique id prefix, "active", or null) to a session.</summary>
    TerminalSession? Resolve(string? target);

    /// <summary>Snapshot of all sessions for `tree`.</summary>
    IReadOnlyList<SessionSnapshot> Snapshot();

    /// <summary>Create a session; returns its id. name/cwd optional.</summary>
    string NewSession(string? name, string? cwd);

    /// <summary>Make the target session active. Returns false if not found.</summary>
    bool SelectSession(string target);

    /// <summary>Close the target session. Returns false if not found.</summary>
    bool CloseSession(string target);
}

/// <summary>Adapter exposing a single fixed session as an <see cref="ISessionHost"/> (tests / simple hosts).</summary>
public sealed class SingleSessionHost : ISessionHost
{
    private readonly TerminalSession _session;
    public SingleSessionHost(TerminalSession session) => _session = session;

    public TerminalSession? Resolve(string? target) => _session;
    public IReadOnlyList<SessionSnapshot> Snapshot() =>
        new[] { new SessionSnapshot("single", "session", true, _session.Status) };
    public string NewSession(string? name, string? cwd) => "single";
    public bool SelectSession(string target) => true;
    public bool CloseSession(string target) => false;
}
