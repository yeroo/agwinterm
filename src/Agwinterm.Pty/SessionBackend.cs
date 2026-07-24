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
    /// <summary>Resolve the configured <c>session-host</c> value to a backend:
    ///  - "server": the C# pty-host (the same exe, <c>--pty-host</c> role).
    ///  - "server-rust": the standalone Rust pty-host binary (agwinterm-ptyhost.exe next to the
    ///    exe) — same protocol (protobuf v2, oracle-proven), but blocking-threaded so the .NET 10
    ///    IOCP crash class (#118) is structurally absent. Uses a distinct pipe namespace
    ///    (<c>&lt;appId&gt;-rust</c>) so it never collides with a C# host on the same instance.
    ///  - anything else: in-process.
    /// A missing Rust binary falls through to a null exe, so Create throws and the UI falls back to
    /// in-process (same graceful path as an unreachable C# host).</summary>
    public static ISessionBackend Resolve(string? configured, string appId, string? exePath)
    {
        if (string.Equals(configured, "server", StringComparison.OrdinalIgnoreCase))
            return new ServerSessionBackend(appId, exePath);
        if (string.Equals(configured, "server-rust", StringComparison.OrdinalIgnoreCase))
        {
            string rustAppId = appId + "-rust";
            string? rustHost = FindRustHost(exePath);
            return new ServerSessionBackend(rustAppId, rustHost, $"--pipe \"{rustAppId}\"", name: "server-rust");
        }
        return InProcessSessionBackend.Instance;
    }

    /// <summary>Locate agwinterm-ptyhost.exe next to the app exe (the installer ships it there).</summary>
    private static string? FindRustHost(string? exePath)
    {
        try
        {
            string dir = exePath is not null ? Path.GetDirectoryName(exePath)! : AppContext.BaseDirectory;
            string p = Path.Combine(dir, "agwinterm-ptyhost.exe");
            return File.Exists(p) ? p : null;
        }
        catch { return null; }
    }
}
