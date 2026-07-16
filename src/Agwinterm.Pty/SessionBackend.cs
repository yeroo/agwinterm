namespace Agwinterm.Pty;

/// <summary>
/// Where terminal sessions live. The UI creates every session through exactly one of these, chosen
/// at startup from the <c>session-host</c> config key — never via <c>new TerminalSession</c>
/// directly — so the pty-host server backend (#105, Phase 2) can slot in behind the same seam.
/// </summary>
public interface ISessionBackend
{
    /// <summary>Stable name for diagnostics/toasts ("in-process", later "server").</summary>
    string Name { get; }

    /// <summary>Create a session sized <paramref name="cols"/>×<paramref name="rows"/> (not yet started).</summary>
    ISession Create(int cols, int rows);
}

/// <summary>Today's model: the UI process owns the ConPTY (a plain <see cref="TerminalSession"/>).</summary>
public sealed class InProcessSessionBackend : ISessionBackend
{
    public static readonly InProcessSessionBackend Instance = new();
    public string Name => "in-process";
    public ISession Create(int cols, int rows) => new TerminalSession(cols, rows);
}

public static class SessionBackends
{
    /// <summary>Resolve the configured <c>session-host</c> value to a backend. "server" is reserved
    /// for the pty-host process (#105) and falls back to in-process until it ships —
    /// <paramref name="fellBack"/> lets the caller tell the user instead of silently ignoring it.</summary>
    public static ISessionBackend Resolve(string? configured, out bool fellBack)
    {
        fellBack = string.Equals(configured, "server", StringComparison.OrdinalIgnoreCase);
        return InProcessSessionBackend.Instance;
    }
}
