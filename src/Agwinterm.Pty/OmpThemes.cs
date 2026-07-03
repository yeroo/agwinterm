using System.IO;

namespace Agwinterm.Pty;

/// <summary>
/// Discovers installed oh-my-posh themes (<c>*.omp.json</c>) so agwinterm can offer a live
/// scheme changer. Looks in <c>$env:POSH_THEMES_PATH</c> first, then the common winget / scoop /
/// chocolatey install locations. De-dupes by theme name (filename without <c>.omp.json</c>).
/// </summary>
public static class OmpThemes
{
    /// <summary>Candidate directories that may hold oh-my-posh theme files, in priority order.</summary>
    public static IEnumerable<string> Dirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? Env(string v) => Environment.GetEnvironmentVariable(v);

        foreach (var d in new[]
        {
            Env("POSH_THEMES_PATH"),
            Path.Combine(Env("LOCALAPPDATA") ?? "", "Programs", "oh-my-posh", "themes"),          // winget
            Path.Combine(Env("USERPROFILE") ?? "", "scoop", "apps", "oh-my-posh", "current", "themes"), // scoop
            Path.Combine(Env("ProgramData") ?? "", "chocolatey", "lib", "oh-my-posh", "tools", "themes"), // choco
        })
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            if (seen.Add(d) && Directory.Exists(d)) yield return d;
        }
    }

    /// <summary>All discovered themes as (name, full path), sorted by name, de-duped (first dir wins).</summary>
    public static IReadOnlyList<(string Name, string Path)> List()
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Dirs())
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.omp.json"); }
            catch { continue; }
            foreach (var f in files)
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".omp.json", StringComparison.OrdinalIgnoreCase))
                    name = name[..^".omp.json".Length];
                if (!byName.ContainsKey(name)) byName[name] = f;
            }
        }
        return byName.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Resolve a theme name (or a direct .omp.json path) to a full file path, or null if not found.</summary>
    public static string? Resolve(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return null;
        if (nameOrPath.EndsWith(".omp.json", StringComparison.OrdinalIgnoreCase) && File.Exists(nameOrPath))
            return nameOrPath;
        return List().FirstOrDefault(t => string.Equals(t.Name, nameOrPath, StringComparison.OrdinalIgnoreCase)).Path;
    }
}
