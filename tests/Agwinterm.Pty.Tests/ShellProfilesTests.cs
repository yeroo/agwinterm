using Agwinterm.Pty;
using System.Text.Json;

namespace Agwinterm.Pty.Tests;

/// <summary>Profile loading self-repair — born from a field failure: a Store PowerShell update
/// rotated its versioned WindowsApps dir and every pane died with "spawn failed" because the
/// profile had captured the resolved (version-baked) path.</summary>
public class ShellProfilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agw-profiles-" + Guid.NewGuid().ToString("N")[..8]);

    public ShellProfilesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void WriteProfiles(object cfg) =>
        // camelCase to match Save()'s output — the loader's deserialization is case-SENSITIVE.
        File.WriteAllText(Path.Combine(_dir, "profiles.json"),
            JsonSerializer.Serialize(cfg, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    [Fact]
    public void StaleAbsolutePath_HealsToBareName_WhenPathResolvesIt()
    {
        // The exact field shape: a version-baked WindowsApps path that no longer exists, whose
        // bare name (cmd.exe here — guaranteed on PATH) still resolves.
        WriteProfiles(new
        {
            Default = "P7",
            Profiles = new[] { new { Name = "P7", Command = @"C:\Program Files\WindowsApps\Microsoft.PowerShell_7.6.3.0_x64__8wekyb3d8bbwe\cmd.exe" } }
        });
        var cfg = ShellProfiles.Load(_dir);
        Assert.Equal("cmd.exe", Assert.Single(cfg.Profiles, p => p.Name == "P7").Command);
    }

    [Fact]
    public void MissingExe_NotOnPath_IsLeftAlone()
    {
        // Unresolvable bare name → keep the absolute path (an honest error beats a silent swap
        // to something unrelated; the LaunchShell fallback handles the pane-level degrade).
        string stale = @"C:\definitely\not\here\no-such-shell-agw.exe";
        WriteProfiles(new { Default = "X", Profiles = new[] { new { Name = "X", Command = stale } } });
        var cfg = ShellProfiles.Load(_dir);
        Assert.Equal(stale, Assert.Single(cfg.Profiles, p => p.Name == "X").Command);
    }

    [Fact]
    public void ExistingAbsolutePath_IsUntouched()
    {
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        WriteProfiles(new { Default = "Real", Profiles = new[] { new { Name = "Real", Command = real } } });
        var cfg = ShellProfiles.Load(_dir);
        Assert.Equal(real, Assert.Single(cfg.Profiles, p => p.Name == "Real").Command);
    }
}
