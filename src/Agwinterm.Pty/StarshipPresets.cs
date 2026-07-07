using System.Diagnostics;
using System.IO;

namespace Agwinterm.Pty;

/// <summary>
/// Discovers starship presets (<c>starship preset --list</c>) and materializes them as config
/// files so agwinterm can offer a live scheme changer (the starship analog of <see cref="OmpThemes"/>).
/// Preset TOMLs are generated once into <c>&lt;appDir&gt;\starship\&lt;preset&gt;.toml</c> and pointed
/// at via <c>STARSHIP_CONFIG</c>.
/// </summary>
public static class StarshipPresets
{
    private static IReadOnlyList<string>? _cache;

    /// <summary>True if a starship executable is reachable on PATH.</summary>
    public static bool Available()
    {
        try { return Run("--version", out _); }
        catch { return false; }
    }

    /// <summary>Preset names from `starship preset --list` (cached; empty when starship is missing).</summary>
    public static IReadOnlyList<string> List()
    {
        if (_cache is not null) return _cache;
        var names = new List<string>();
        try
        {
            if (Run("preset --list", out string stdout))
                foreach (var line in stdout.Split('\n'))
                {
                    string n = line.Trim();
                    if (n.Length > 0) names.Add(n);
                }
        }
        catch { }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return _cache = names;
    }

    /// <summary>Path to the generated config for <paramref name="preset"/> (creating it on first
    /// use), or null when the preset is unknown / starship is missing. Empty preset = null
    /// (starship's own default config, i.e. don't set STARSHIP_CONFIG).</summary>
    public static string? ConfigFor(string preset, string appDir)
    {
        if (string.IsNullOrWhiteSpace(preset)) return null;
        if (!List().Contains(preset, StringComparer.OrdinalIgnoreCase)) return null;
        try
        {
            string dir = Path.Combine(appDir, "starship");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, preset + ".toml");
            if (!File.Exists(path))
            {
                if (!Run($"preset {preset}", out string toml) || toml.Length == 0) return null;
                File.WriteAllText(path, toml);
            }
            return path;
        }
        catch { return null; }
    }

    private static bool Run(string args, out string stdout)
    {
        stdout = "";
        var psi = new ProcessStartInfo("starship", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Presets embed nerd-font glyphs in their format strings; the default (OEM codepage)
            // decode would bake mojibake into the generated TOML.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi);
        if (p is null) return false;
        stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
        return p.ExitCode == 0;
    }
}
