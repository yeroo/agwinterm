using System.IO;

namespace Agwinterm.Pty;

/// <summary>
/// Installs a generic, agent-agnostic status bridge into the user's PowerShell profile, for agents
/// that lack native hooks (unlike Claude Code, which uses <see cref="AgentHooks"/>). Appends a guarded,
/// sentinel-delimited block to the END of the CurrentUser powershell.exe profile that — only inside an
/// agwinterm session ($env:AGWINTERM) — watches for commands whose name matches $env:AGWINTERM_AGENT_RE
/// (a regex; a sensible default is seeded). When such a command is submitted it pushes status "active";
/// when it finishes (detected at the next prompt) it pushes "completed". Idempotent.
/// </summary>
public static class GenericAgentInstaller
{
    private const string Begin = "# >>> agwinterm generic agent status >>>";
    private const string End = "# <<< agwinterm generic agent status <<<";

    /// <summary>The guarded block appended to $PROFILE. Wraps the prompt + a PSReadLine Enter handler.</summary>
    public static readonly string Block =
        Begin + "\n" +
        """
        if ($env:AGWINTERM -eq '1' -and -not $global:__agwAgent) {
          $global:__agwAgent = $true
          if (-not $env:AGWINTERM_AGENT_RE) { $env:AGWINTERM_AGENT_RE = 'claude|codex|aider|gemini|cursor|copilot|goose|opencode|amp' }
          function global:__agwStatus([string]$s) {
            if (-not $env:AGWINTERM_SESSION_ID) { return }
            $pipe = if ($env:AGWINTERM_PIPE) { $env:AGWINTERM_PIPE } else { 'agwinterm' }
            try {
              $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
              $c.Connect(300)
              $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true
              $w.WriteLine('{"cmd":"session.status","args":{"status":"' + $s + '"}}')
              $c.Dispose()
            } catch { }
          }
          if (Get-Module -ListAvailable PSReadLine) {
            Set-PSReadLineKeyHandler -Key Enter -ScriptBlock {
              $line = $null; $cur = $null
              [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cur)
              if ($line -and $line -match ('^\s*(' + $env:AGWINTERM_AGENT_RE + ')\b')) { __agwStatus 'active' }
              [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
            }
            $global:__agwAgentPrompt = $function:prompt
            function global:prompt {
              $h = Get-History -Count 1
              if ($h -and $h.CommandLine -match ('^\s*(' + $env:AGWINTERM_AGENT_RE + ')\b') -and $h.Id -ne $global:__agwLastH) {
                $global:__agwLastH = $h.Id; __agwStatus 'completed'
              }
              if ($global:__agwAgentPrompt) { & $global:__agwAgentPrompt } else { "PS $((Get-Location).Path)> " }
            }
          }
        }
        """ + "\n" + End;

    /// <summary>Append the block to the profile if not already present. Idempotent. Returns a summary.</summary>
    public static string Install()
    {
        string path = ShellIntegrationInstaller.ProfilePath();
        try
        {
            string existing = File.Exists(path) ? File.ReadAllText(path) : "";
            if (existing.Contains(Begin))
                return "generic agent status already installed -> " + path;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string sep = existing.Length == 0 ? "" : (existing.EndsWith("\n") ? "\n" : "\n\n");
            File.AppendAllText(path, sep + Block + "\n");
            return "installed generic agent status (regex $env:AGWINTERM_AGENT_RE) -> " + path;
        }
        catch (Exception ex)
        {
            return "failed to install generic agent status: " + ex.Message;
        }
    }
}
