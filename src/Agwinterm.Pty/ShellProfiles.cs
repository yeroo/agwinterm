using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agwinterm.Pty;

/// <summary>One launchable shell/terminal profile (Windows Terminal-style): a name, the command
/// (exe name or full path), optional args, optional starting dir, and an optional Segoe glyph.</summary>
public sealed class ShellProfile
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string[]? Args { get; set; }
    public string? Cwd { get; set; }
    public string? Icon { get; set; }
    /// <summary>Environment variables injected into this profile's sessions (WT's per-profile environment).</summary>
    public Dictionary<string, string>? Env { get; set; }
}

/// <summary>
/// Windows shell profiles — the set of shells a new agwinterm session can launch (cmd, Windows
/// PowerShell, PowerShell 7, Git Bash, WSL distros, or any user-defined command). Persisted to
/// <c>%LOCALAPPDATA%\agwinterm\profiles.json</c>: auto-seeded from detected shells on first run,
/// then owned by the user (never overwritten). A malformed file falls back to the built-in default.
/// </summary>
public static class ShellProfiles
{
    /// <summary>The whole profiles.json: an ordered list plus the name of the default profile.</summary>
    public sealed class Config
    {
        public string Default { get; set; } = "Windows PowerShell";
        public List<ShellProfile> Profiles { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string Term = ""; // Segoe Fluent CommandPrompt glyph (used as a generic shell icon)

    /// <summary>Load profiles.json (seeding it from detected shells if absent); malformed → default only.</summary>
    public static Config Load(string appDir)
    {
        string path = Path.Combine(appDir, "profiles.json");
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOpts);
                if (cfg is not null && cfg.Profiles.Count > 0) return Normalize(cfg);
            }
        }
        catch { /* malformed → seed / fall back below */ }

        var seeded = new Config { Default = "Windows PowerShell", Profiles = Detect() };
        try { Directory.CreateDirectory(appDir); File.WriteAllText(path, JsonSerializer.Serialize(seeded, JsonOpts)); }
        catch { /* best-effort; run with the in-memory list */ }
        return Normalize(seeded);
    }

    /// <summary>The profiles.json path (for opening in an editor).</summary>
    public static string PathFor(string appDir) => Path.Combine(appDir, "profiles.json");

    /// <summary>Persist the config (default + profiles) to profiles.json (best-effort).</summary>
    public static void Save(string appDir, Config c)
    {
        try { Directory.CreateDirectory(appDir); File.WriteAllText(PathFor(appDir), JsonSerializer.Serialize(c, JsonOpts)); }
        catch { /* best-effort */ }
    }

    /// <summary>Set the default profile by name and persist; returns the reloaded/normalized config.</summary>
    public static Config SetDefault(string appDir, string name)
    {
        var c = Load(appDir);
        if (c.Profiles.Exists(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            c.Default = name;
            Save(appDir, c);
        }
        return Normalize(c);
    }

    private static Config Normalize(Config c)
    {
        c.Profiles.RemoveAll(p => string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Command));
        // Repair earlier seeds that captured the wsl.exe stub's "install WSL?" message lines as
        // distros (e.g. "WSL: Press any key to install …") — distro names never contain spaces.
        c.Profiles.RemoveAll(p => p.Name.StartsWith("WSL: ", StringComparison.OrdinalIgnoreCase)
                                  && p.Name["WSL: ".Length..].Contains(' '));
        if (c.Profiles.Count == 0) c.Profiles.Add(DefaultPowerShell());
        if (string.IsNullOrWhiteSpace(c.Default) ||
            !c.Profiles.Exists(p => p.Name.Equals(c.Default, StringComparison.OrdinalIgnoreCase)))
            c.Default = c.Profiles[0].Name;
        return c;
    }

    private static ShellProfile DefaultPowerShell() =>
        new() { Name = "Windows PowerShell", Command = "powershell.exe", Icon = Term };

    /// <summary>Detect installed shells for the first-run seed (include only those found).</summary>
    private static List<ShellProfile> Detect()
    {
        var list = new List<ShellProfile> { DefaultPowerShell() }; // always present; the default

        string? pwsh = Which("pwsh.exe")
            ?? Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"));
        if (pwsh is not null) list.Add(new ShellProfile { Name = "PowerShell 7", Command = pwsh, Icon = Term });

        list.Add(new ShellProfile { Name = "Command Prompt", Command = "cmd.exe", Icon = Term });

        foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramW6432"),
                                   Environment.GetEnvironmentVariable("ProgramFiles"),
                                   Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            string bash = Path.Combine(pf, "Git", "bin", "bash.exe");
            if (File.Exists(bash)) { list.Add(new ShellProfile { Name = "Git Bash", Command = bash, Args = new[] { "-i", "-l" }, Icon = Term }); break; }
        }

        try
        {
            string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            if (File.Exists(wsl))
                foreach (var distro in WslDistros(wsl))
                    list.Add(new ShellProfile { Name = "WSL: " + distro, Command = wsl, Args = new[] { "-d", distro }, Icon = Term });
        }
        catch { /* WSL optional */ }

        string? nu = Which("nu.exe");
        if (nu is not null) list.Add(new ShellProfile { Name = "Nushell", Command = nu, Icon = Term });

        return list;
    }

    /// <summary>Enumerate installed WSL distros via <c>wsl.exe -l -q</c> (its output is UTF-16LE).</summary>
    private static List<string> WslDistros(string wsl)
    {
        var result = new List<string>();
        string outp;
        try
        {
            var psi = new ProcessStartInfo(wsl, "-l -q")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode,
            };
            using var p = Process.Start(psi);
            if (p is null) return result;
            var read = p.StandardOutput.ReadToEndAsync();   // read async so a prompting stub can't deadlock us
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return result; }
            // Without WSL installed, wsl.exe is a stub that prints "Press any key to install…"
            // prompt lines and exits non-zero — those lines are NOT distros.
            if (p.ExitCode != 0) return result;
            outp = read.GetAwaiter().GetResult();
        }
        catch { return result; }
        foreach (var raw in outp.Split('\n'))
        {
            var d = raw.Trim().Trim('\0', '\r');
            if (d.Length > 0 && !d.Contains(' ')) result.Add(d);   // real distro names never contain spaces
        }
        return result;
    }

    private static string? Which(string exe)
    {
        try
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
        }
        catch { }
        return null;
    }

    private static string? Exists(string p) => File.Exists(p) ? p : null;
}
