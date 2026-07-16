using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agwinterm.Pty;

/// <summary>
/// Claude Code version awareness for the "Update Claude Code" workflow: what's installed
/// (<c>claude --version</c>), what's shipped (the npm registry — Claude Code releases are published
/// as <c>@anthropic-ai/claude-code</c> for every install flavor, so its <c>latest</c> tag is the
/// authoritative "a new version is out" signal), and a tolerant version compare. Pure helpers only —
/// the update itself always runs as a visible <c>claude update</c> in a terminal, never silently.
/// </summary>
public static class ClaudeUpdate
{
    public const string RegistryUrl = "https://registry.npmjs.org/@anthropic-ai/claude-code/latest";

    /// <summary>Extract a dotted version from CLI output (e.g. "2.1.14 (Claude Code)"), or null.</summary>
    public static string? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = Regex.Match(output, @"\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?");
        return m.Success ? m.Value : null;
    }

    /// <summary>The "version" field of an npm registry <c>/latest</c> document, or null.</summary>
    public static string? LatestFromRegistryJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? ParseVersion(v.GetString()) : null;
        }
        catch { return null; }
    }

    /// <summary>Whether <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.
    /// Numeric per-segment compare ("2.0.10" &gt; "2.0.9"); any unparseable side compares as not-newer,
    /// so a weird string can never nag the user or trigger an update loop.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        int[]? a = Segments(candidate), b = Segments(current);
        if (a is null || b is null) return false;
        for (int i = 0; i < 3; i++)
            if (a[i] != b[i]) return a[i] > b[i];
        return false;
    }

    private static int[]? Segments(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        int dash = v.IndexOf('-');                      // ignore any prerelease tail
        if (dash > 0) v = v[..dash];
        var parts = v.Split('.');
        if (parts.Length != 3) return null;
        var seg = new int[3];
        for (int i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], out seg[i]) || seg[i] < 0) return null;
        return seg;
    }

    /// <summary>Run <c>claude --version</c> and parse the result, or null when claude isn't installed
    /// (resolved via cmd.exe so PATHEXT covers claude.exe / claude.cmd / npm shims alike).</summary>
    public static string? InstalledVersion(int timeoutMs = 10000)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c claude --version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
            return ParseVersion(outp);
        }
        catch { return null; }
    }

    /// <summary>Fetch the latest shipped Claude Code version from the npm registry, or null on any
    /// network/parse failure (offline must stay silent — awareness is best-effort).</summary>
    public static async Task<string?> FetchLatestAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("agwinterm");
            return LatestFromRegistryJson(await http.GetStringAsync(RegistryUrl).ConfigureAwait(false));
        }
        catch { return null; }
    }
}
