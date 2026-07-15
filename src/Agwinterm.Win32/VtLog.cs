namespace Agwinterm.Win32;

/// <summary>
/// Env-gated unhandled-VT-sequence log: set AGWINTERM_VT_LOG=&lt;path&gt; and every escape sequence the
/// emulator has no handler for is appended there (timestamp, pane, kind, detail). Off (null path) it
/// costs one null check per unhandled sequence. This is the debug tap that turns a protocol gap into
/// one grep — e.g. the OSC 52 clipboard drop would have shown up as `OSC 52;c;…` on the first run.
/// </summary>
internal static class VtLog
{
    private static readonly string? _path = Environment.GetEnvironmentVariable("AGWINTERM_VT_LOG") is { Length: > 0 } p ? p : null;
    private static readonly object _lock = new();

    public static void Write(string paneId, string kind, string detail)
    {
        if (_path is null) return;
        try
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{(paneId.Length >= 8 ? paneId[..8] : paneId)}] {kind,-4} {detail}\n";
            lock (_lock) System.IO.File.AppendAllText(_path, line);
        }
        catch { /* best-effort diagnostics — never disturb the terminal */ }
    }
}
