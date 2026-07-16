namespace Agwinterm.Pty;

/// <summary>
/// Where terminal sessions live. The UI creates every session through exactly one of these, chosen
/// at startup from the <c>session-host</c> config key — never via <c>new TerminalSession</c>
/// directly — so the pty-host server backend (#105, Phase 2) can slot in behind the same seam.
/// </summary>
public interface ISessionBackend
{
    /// <summary>Stable name for diagnostics/toasts ("in-process", "server").</summary>
    string Name { get; }

    /// <summary>Create a session sized <paramref name="cols"/>×<paramref name="rows"/> (not yet
    /// started). <paramref name="id"/> is the PANE id — for the server backend it names the hosted
    /// session, which is the adoption key after a UI restart (in-process ignores it).</summary>
    ISession Create(string id, int cols, int rows);
}

/// <summary>Today's model: the UI process owns the ConPTY (a plain <see cref="TerminalSession"/>).</summary>
public sealed class InProcessSessionBackend : ISessionBackend
{
    public static readonly InProcessSessionBackend Instance = new();
    public string Name => "in-process";
    public ISession Create(string id, int cols, int rows) => new TerminalSession(cols, rows);
}

public static class SessionBackends
{
    /// <summary>Resolve the configured <c>session-host</c> value to a backend. "server" (#105,
    /// experimental) hosts sessions in the pty-host process — spawned on demand from
    /// <paramref name="exePath"/> (the same exe, <c>--pty-host</c> role) under
    /// <paramref name="appId"/>'s pipe namespace. Anything else = in-process.</summary>
    public static ISessionBackend Resolve(string? configured, string appId, string? exePath)
        => string.Equals(configured, "server", StringComparison.OrdinalIgnoreCase)
            ? new ServerSessionBackend(appId, exePath)
            : InProcessSessionBackend.Instance;
}
