using System.IO;

namespace Agwinterm.Pty;

/// <summary>State of a sentinel-delimited block inside a PowerShell profile.</summary>
public enum ProfileBlockState { Missing, Current, Stale, Corrupted }

/// <summary>
/// Shared logic for the guarded, sentinel-delimited blocks agwinterm appends to the user's
/// PowerShell profile (claude launcher, generic agent bridge, …): classify the installed block
/// against the current version and produce the spliced replacement for a stale one, so installer
/// re-runs and startup refreshes update old blocks instead of silently keeping them.
/// </summary>
public static class ProfileBlocks
{
    /// <summary>Classify the block delimited by <paramref name="begin"/>/<paramref name="end"/> inside
    /// profile text; for <see cref="ProfileBlockState.Stale"/>, <paramref name="updated"/> is the
    /// profile text with the block replaced by <paramref name="block"/>.</summary>
    public static ProfileBlockState Inspect(string existing, string begin, string end, string block, out string updated)
    {
        updated = existing;
        int b = existing.IndexOf(begin, StringComparison.Ordinal);
        if (b < 0) return ProfileBlockState.Missing;
        int e = existing.IndexOf(end, b, StringComparison.Ordinal);
        if (e < 0) return ProfileBlockState.Corrupted;
        if (existing[b..(e + end.Length)] == block) return ProfileBlockState.Current;
        updated = existing[..b] + block + existing[(e + end.Length)..];
        return ProfileBlockState.Stale;
    }

    /// <summary>Refresh an already-installed block to the current version (refresh-only: never
    /// installs fresh; best-effort). Returns a summary line, or null when nothing changed.</summary>
    public static string? RefreshIfInstalled(string begin, string end, string block, string what)
    {
        try
        {
            string path = ShellIntegrationInstaller.ProfilePath();
            if (!File.Exists(path)) return null;
            if (Inspect(File.ReadAllText(path), begin, end, block, out string updated) != ProfileBlockState.Stale) return null;
            File.WriteAllText(path, updated);
            return what + " updated -> " + path;
        }
        catch { return null; }    // a locked/readonly profile must not break startup
    }
}
