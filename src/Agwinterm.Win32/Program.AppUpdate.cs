using System.Diagnostics;
using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

// agwinterm self-update ("Update agwinterm" palette action / `agwintermctl app update`):
// check GitHub releases → download the channel's asset → verify SHA-256 → save state → exit all
// windows gracefully → a detached helper swaps the binary and relaunches → sessions restore.
// Package-manager installs (scoop/choco) are never self-updated — we only point at the manager.
internal partial class Program
{
    /// <summary>The running exe (Environment.ProcessPath — Assembly.Location is empty in the
    /// single-file portable build).</summary>
    private static string AppExePath => Environment.ProcessPath ?? "";

    /// <summary>Version to compare against releases: the stamped InformationalVersion ("dev" in
    /// unstamped builds, which never triggers). AGWINTERM_VERSION_OVERRIDE is a test seam.</summary>
    private static string AppCurrentVersion()
        => Environment.GetEnvironmentVariable("AGWINTERM_VERSION_OVERRIDE") is { Length: > 0 } o
            ? o : _termProgramVersion;

    private static UpdateChannel AppChannel()
        => AppUpdate.DetectChannel(AppExePath, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static string UpdatesDir => Path.Combine(AppDir, "updates");

    /// <summary>Startup housekeeping: drop the ".old" exe a portable swap left behind and any
    /// downloaded payloads from previous updates. Best-effort — a still-locked file waits for the
    /// next start.</summary>
    private static void CleanupUpdateLeftovers()
    {
        try { if (AppExePath.Length > 0 && File.Exists(AppExePath + ".old")) File.Delete(AppExePath + ".old"); } catch { }
        try { if (Directory.Exists(UpdatesDir)) Directory.Delete(UpdatesDir, recursive: true); } catch { }
    }

    /// <summary>One background check ("be aware when a new agwinterm ships"): GitHub latest release
    /// vs the running version. New version → one toast per version + the palette hint. The package-
    /// manager toast points at the manager instead of the palette action.</summary>
    private async Task CheckAppUpdateOnce()
    {
        string cur = AppCurrentVersion();
        var rel = await AppUpdate.FetchLatestAsync();
        if (rel is null) return;                                   // offline stays silent
        if (!ClaudeUpdate.IsNewer(rel.Version, cur)) { _appLatest = null; return; }
        bool fresh = !string.Equals(_appLatest, rel.Version, StringComparison.Ordinal);
        _appLatest = rel.Version;
        if (!fresh) return;
        string msg = AppChannel() == UpdateChannel.PackageManager
            ? $"agwinterm {rel.Version} is out — update via your package manager (e.g. scoop update agwinterm)"
            : $"agwinterm {rel.Version} is out (you have {cur}) — palette → Update agwinterm";
        Post(() => ShowToast(msg, 6000));
    }

    /// <summary>The self-update workflow. Fail-closed at every step (no digest / bad digest / missing
    /// asset → abort with a toast, nothing applied); on success the app exits itself and the helper
    /// swaps + relaunches, so the user comes back to restored sessions on the new version.</summary>
    private string UpdateAgwinterm()
    {
        var channel = AppChannel();
        if (channel == UpdateChannel.PackageManager)
            return "this agwinterm is managed by a package manager — update with scoop/choco";
        if (AppExePath.Length == 0) return "cannot resolve the running exe path";
        if (_appUpdating) return "agwinterm update already running";
        _appUpdating = true;
        _ = Task.Run(async () =>
        {
            void Toast(string m) => Post(() => ShowToast(m, 5000));
            bool applying = false;
            try
            {
                string cur = AppCurrentVersion();
                var rel = await AppUpdate.FetchLatestAsync();
                if (rel is null) { Toast("agwinterm update check failed (offline or rate-limited) — try again later"); return; }
                if (!ClaudeUpdate.IsNewer(rel.Version, cur))
                { _appLatest = null; Toast($"agwinterm {cur} is already the latest"); return; }
                var asset = AppUpdate.PickAsset(rel, channel);
                if (asset is null) { Toast($"release {rel.Version} has no {(channel == UpdateChannel.Installed ? "setup" : "portable")} asset"); return; }
                if (string.IsNullOrEmpty(asset.Sha256)) { Toast("release asset carries no SHA-256 digest — refusing an unverifiable update"); return; }
                Toast($"downloading agwinterm {rel.Version}…");
                string payload = Path.Combine(UpdatesDir, asset.Name);
                if (!await AppUpdate.DownloadVerifiedAsync(asset, payload))
                { Toast("download failed SHA-256 verification — update aborted"); return; }

                string helper = Path.Combine(UpdatesDir, "apply-update.ps1");
                await File.WriteAllTextAsync(helper, AppUpdate.HelperScript);
                applying = true;                                    // hand-off: flag stays up until exit
                Post(() =>
                {
                    ShowToast($"agwinterm {cur} → {rel.Version} — restarting…", 4000);
                    var psi = new ProcessStartInfo("powershell.exe",
                        $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{helper}\" " +
                        $"-ProcId {Environment.ProcessId} -Channel {(channel == UpdateChannel.Installed ? "installed" : "portable")} " +
                        $"-Payload \"{payload}\" -Exe \"{AppExePath}\"")
                    { UseShellExecute = false, CreateNoWindow = true };
                    try { Process.Start(psi); } catch (Exception ex) { ShowToast("failed to start the update helper: " + ex.Message, 6000); _appUpdating = false; return; }
                    QuitAllWindowsForUpdate();
                });
            }
            finally { if (!applying) _appUpdating = false; }
        });
        return "updating agwinterm…";
    }

    /// <summary>Close every window of this process gracefully (each WM_DESTROY saves its tree; the
    /// last one quits). <see cref="_updateQuitting"/> keeps multi-window state marked open so ALL
    /// windows come back after the relaunch — a normal explicit close marks a window non-reopening.</summary>
    private static void QuitAllWindowsForUpdate()
    {
        _updateQuitting = true;
        List<Program> wins;
        lock (_windowIndex) wins = _byId.Values.ToList();
        foreach (var w in wins) w.Post(() => DestroyWindow(w._hwnd));
    }
}
