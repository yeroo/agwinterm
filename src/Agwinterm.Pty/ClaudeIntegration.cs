using System.IO;

namespace Agwinterm.Pty;

/// <summary>
/// Installs the transparent Claude Code launcher: a guarded, sentinel-delimited block appended to the
/// CurrentUser PowerShell profile that — only inside an agwinterm session ($env:AGWINTERM) — defines a
/// <c>claude</c> function wrapping the real CLI. It binds Claude's session id to the agwinterm PANE id
/// (which is a stable UUID that survives relaunch) so the two are the same identity: a fresh pane starts
/// <c>claude --session-id &lt;paneId&gt;</c>, and a pane whose transcript already exists resumes with
/// <c>claude --resume &lt;paneId&gt;</c>. It also tells agwinterm (via the control pipe, <c>session.bind</c>)
/// to mark the pane as a resumable Claude session so restart re-launches it and the conversation comes back.
/// The wrapper no-ops (passes straight through to the real CLI) outside agwinterm, when the id isn't a
/// UUID, or when the user already passed a session-controlling flag. Idempotent.
/// </summary>
public static class ClaudeIntegration
{
    private const string Begin = "# >>> agwinterm claude integration >>>";
    private const string End = "# <<< agwinterm claude integration <<<";

    /// <summary>The guarded block appended to $PROFILE.</summary>
    public static readonly string Block =
        Begin + "\n" +
        """
        if ($env:AGWINTERM -eq '1' -and -not $global:__agwClaude) {
          $global:__agwClaude = $true
          function global:__agwBindClaude([string]$sid, [string]$agent = 'claude') {
            if (-not $sid) { return }
            $pipe = if ($env:AGWINTERM_PIPE) { $env:AGWINTERM_PIPE } else { 'agwinterm' }
            try {
              $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
              $c.Connect(300)
              $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true
              $w.WriteLine('{"cmd":"session.bind","target":"' + $sid + '","args":{"agent":"' + $agent + '"}}')
              $c.Dispose()
            } catch { }
          }
          function global:claude {
            $sid = $env:AGWINTERM_SESSION_ID
            # Resolve the real CLI (Application/.cmd/.exe or ExternalScript/.ps1) — never this function.
            $real = Get-Command claude -CommandType Application,ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $real) { Write-Error 'claude: executable not found on PATH'; return }
            $uuid = $sid -match '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
            # If the user is already steering the session (resume/continue/explicit id) or running headless,
            # don't inject or bind — just pass through untouched. Permission-mode flags are remembered in
            # the binding so a restored session comes back with the same mode (e.g. YOLO stays YOLO).
            $ctl = $false; $yolo = $false
            # CLI subcommands (update/doctor/mcp/...) are maintenance verbs, not conversations —
            # injecting --resume/--session-id would mangle them. Pass through untouched.
            if ($args.Count -gt 0 -and "$($args[0])" -match '^(update|doctor|mcp|config|install|migrate-installer|setup-token|plugin|agents)$') { $ctl = $true }
            foreach ($a in $args) {
              if ($a -in '-r','--resume','-c','--continue','--session-id','-p','--print') { $ctl = $true }
              if ($a -eq '--dangerously-skip-permissions') { $yolo = $true }
            }
            if (-not $sid -or -not $uuid -or $ctl) { & $real.Source @args; return }
            $cfg = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
            $enc = ($PWD.ProviderPath -replace '[^A-Za-z0-9]','-')   # Claude's per-project dir encoding
            $tx  = Join-Path $cfg (Join-Path 'projects' (Join-Path $enc ($sid + '.jsonl')))
            __agwBindClaude $sid $(if ($yolo) { 'claude --dangerously-skip-permissions' } else { 'claude' })
            if (Test-Path -LiteralPath $tx) { & $real.Source --resume $sid @args }
            else { & $real.Source --session-id $sid @args }
          }
        }
        """ + "\n" + End;

    internal enum BlockState { Missing, Current, Stale, Corrupted }

    /// <summary>Classify the launcher block inside profile text; for <see cref="BlockState.Stale"/>,
    /// <paramref name="updated"/> is the profile text with the block replaced by the current version.
    /// (Thin wrapper over the shared <see cref="ProfileBlocks"/> logic.)</summary>
    internal static BlockState InspectBlock(string existing, out string updated)
        => (BlockState)ProfileBlocks.Inspect(existing, Begin, End, Block, out updated);

    /// <summary>Append the block to the profile if not already present; replace an installed block whose
    /// content is stale (older wrapper version). Idempotent. Returns a summary.</summary>
    public static string Install()
    {
        string path = ShellIntegrationInstaller.ProfilePath();
        try
        {
            string existing = File.Exists(path) ? File.ReadAllText(path) : "";
            switch (InspectBlock(existing, out string updated))
            {
                case BlockState.Current: return "claude launcher already installed -> " + path;
                case BlockState.Corrupted: return "claude launcher block is corrupted (no end sentinel) — fix " + path + " by hand";
                case BlockState.Stale:
                    File.WriteAllText(path, updated);
                    return "updated claude launcher to the current version -> " + path;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string sep = existing.Length == 0 ? "" : (existing.EndsWith("\n") ? "\n" : "\n\n");
            File.AppendAllText(path, sep + Block + "\n");
            return "installed claude launcher (session-id binding + auto-resume) -> " + path;
        }
        catch (Exception ex)
        {
            return "failed to install claude launcher: " + ex.Message;
        }
    }

    /// <summary>Refresh the installed block to the current version if (and only if) an older one is present —
    /// run at startup so wrapper fixes reach users who installed the launcher once and never re-ran install.
    /// Never installs fresh (that stays opt-in via install.hooks). Returns null when nothing changed.</summary>
    public static string? RefreshIfInstalled() => ProfileBlocks.RefreshIfInstalled(Begin, End, Block, "claude launcher");
}
