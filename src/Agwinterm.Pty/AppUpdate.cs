using System.Security.Cryptography;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>Which kind of agwinterm install is running — decides how (or whether) self-update applies.</summary>
public enum UpdateChannel
{
    /// <summary>Inno per-user install under %LOCALAPPDATA%\Programs\agwinterm → silent setup.exe re-run.</summary>
    Installed,
    /// <summary>A bare portable exe anywhere else → rename-swap the exe in place.</summary>
    Portable,
    /// <summary>scoop / chocolatey layout → hands off; the package manager owns the files.</summary>
    PackageManager,
}

/// <summary>One downloadable file of a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string Url, string? Sha256);

/// <summary>A GitHub release: the version its tag names plus its assets.</summary>
public sealed record ReleaseInfo(string Version, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// agwinterm self-update: channel detection, release lookup (GitHub releases API — the same source
/// every install flavor ships from), SHA-256-verified downloads (release assets carry a digest; we
/// have no Authenticode cert, so the digest check is the integrity gate), and the tiny apply-helper
/// script that swaps binaries AFTER the app has exited. Applying is never silent-background: the
/// user triggers it, the app saves state, exits, and relaunches restored.
/// </summary>
public static class AppUpdate
{
    public const string DefaultApiUrl = "https://api.github.com/repos/yeroo/agwinterm/releases/latest";

    /// <summary>Test seam: a local FILE PATH here (instead of an URL) is read as the API response,
    /// and asset "URLs" that are local paths are copied instead of downloaded — lets E2E runs
    /// exercise the whole pipeline hermetically.</summary>
    public const string ApiEnvVar = "AGWINTERM_UPDATE_API";

    /// <summary>Classify the running install from its own exe path. <paramref name="localAppData"/>
    /// is a parameter for testability (%LOCALAPPDATA% in production).</summary>
    public static UpdateChannel DetectChannel(string exePath, string localAppData)
    {
        if (exePath.Contains(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase) ||
            exePath.Contains(@"\chocolatey\", StringComparison.OrdinalIgnoreCase) ||
            exePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            return UpdateChannel.PackageManager;
        string installDir = Path.Combine(localAppData, "Programs", "agwinterm") + Path.DirectorySeparatorChar;
        return exePath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase)
            ? UpdateChannel.Installed : UpdateChannel.Portable;
    }

    /// <summary>Parse a GitHub /releases/latest document into version + assets (digest "sha256:hex"
    /// → hex). Null on any shape mismatch — an update must never proceed on a guess.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? ver = root.TryGetProperty("tag_name", out var t) ? ClaudeUpdate.ParseVersion(t.GetString()) : null;
            if (ver is null || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<ReleaseAsset>();
            foreach (var a in assets.EnumerateArray())
            {
                string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                string? dig = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (dig is not null && dig.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) dig = dig[7..];
                else dig = null;   // unknown digest algorithm → treat as absent (fail closed later)
                if (name is not null && url is not null) list.Add(new ReleaseAsset(name, url, dig));
            }
            return new ReleaseInfo(ver, list);
        }
        catch { return null; }
    }

    /// <summary>The release asset a channel updates from: the setup exe for installed copies, the
    /// portable exe otherwise. Null when the release carries no such asset (or for package managers,
    /// which never self-update).</summary>
    public static ReleaseAsset? PickAsset(ReleaseInfo release, UpdateChannel channel) => channel switch
    {
        UpdateChannel.Installed => release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("agwinterm-setup-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
        UpdateChannel.Portable => release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("agwinterm-portable-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
        _ => null,
    };

    /// <summary>Fetch and parse the latest release, or null on any failure (offline stays silent).
    /// Honors the <see cref="ApiEnvVar"/> test seam (URL override, or a local JSON file path).</summary>
    public static async Task<ReleaseInfo?> FetchLatestAsync()
    {
        try
        {
            string api = Environment.GetEnvironmentVariable(ApiEnvVar) is { Length: > 0 } o ? o : DefaultApiUrl;
            string json;
            if (!api.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(api))
                json = await File.ReadAllTextAsync(api).ConfigureAwait(false);
            else
            {
                using var http = NewHttp();
                json = await http.GetStringAsync(api).ConfigureAwait(false);
            }
            return ParseRelease(json);
        }
        catch { return null; }
    }

    /// <summary>Download an asset to <paramref name="destPath"/> and verify its SHA-256 against the
    /// release digest. FAIL-CLOSED: no digest, digest mismatch, or any IO error → false and the file
    /// is deleted. Local-path "URLs" are copied (test seam).</summary>
    public static async Task<bool> DownloadVerifiedAsync(ReleaseAsset asset, string destPath)
    {
        if (string.IsNullOrEmpty(asset.Sha256)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            if (!asset.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(asset.Url))
                File.Copy(asset.Url, destPath, overwrite: true);
            else
            {
                using var http = NewHttp();
                await using var src = await http.GetStreamAsync(asset.Url).ConfigureAwait(false);
                await using var dst = File.Create(destPath);
                await src.CopyToAsync(dst).ConfigureAwait(false);
            }
            string actual;
            await using (var f = File.OpenRead(destPath))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(f).ConfigureAwait(false));
            if (actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
            File.Delete(destPath);
            return false;
        }
        catch
        {
            try { File.Delete(destPath); } catch { }
            return false;
        }
    }

    /// <summary>The apply helper, written next to the download and run detached: waits for the app
    /// process to exit, applies the channel's swap, relaunches the SAME exe path. The portable swap
    /// renames the running exe aside (allowed while it runs) rather than overwriting; the app deletes
    /// the ".old" leftover on its next start.</summary>
    public static string HelperScript => """
        param([int]$ProcId, [string]$Channel, [string]$Payload, [string]$Exe)
        function Log([string]$m) { try { Add-Content -Path ($Payload + '.log') -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $m) } catch { } }
        Log "wait pid=$ProcId channel=$Channel"
        try { Wait-Process -Id $ProcId -Timeout 120 -ErrorAction SilentlyContinue } catch { }
        if (Get-Process -Id $ProcId -ErrorAction SilentlyContinue) { Log 'ABORT: app never exited'; exit 1 }
        Start-Sleep -Milliseconds 500
        Log 'app exited; applying'
        if ($Channel -eq 'installed') {
          Start-Process $Payload -ArgumentList '/VERYSILENT','/NORESTART','/SUPPRESSMSGBOXES' -Wait
          Log 'setup finished'
        } else {
          $old = $Exe + '.old'
          Remove-Item $old -Force -ErrorAction SilentlyContinue
          $moved = $false
          for ($i = 0; $i -lt 20 -and -not $moved; $i++) {   # straggler/AV locks: retry ~10s
            try { Move-Item $Exe $old -Force -ErrorAction Stop; $moved = $true } catch { Log ("rename-aside failed: " + $_.Exception.Message); Start-Sleep -Milliseconds 500 }
          }
          if (-not $moved) { Log 'GIVING UP: exe still locked'; exit 1 }   # never delete the payload on failure
          try { Move-Item $Payload $Exe -Force -ErrorAction Stop; Log 'swap done' }
          catch { Log ("swap-in failed, rolling back: " + $_.Exception.Message); Move-Item $old $Exe -Force; exit 1 }
        }
        Start-Process $Exe
        Log 'relaunched'
        """;

    private static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("agwinterm");   // GitHub API requires a UA
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
