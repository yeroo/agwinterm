using System.IO;
using System.Text.Json.Nodes;

namespace Agwinterm.Pty;

/// <summary>
/// Installs Claude Code and Codex status hooks (agterm-style self-setup). Writes PowerShell
/// wrappers that push session status to agwinterm's control pipe, and idempotently merges the
/// hooks into each agent's hook file. ~/.claude/settings.json:
///   UserPromptSubmit -> active, PostToolUse -> active, Stop -> completed,
///   Notification(permission_prompt) -> blocked.
/// ~/.codex/hooks.json (same schema):
///   UserPromptSubmit -> active, PostToolUse -> active, PermissionRequest -> blocked,
///   Stop -> completed, or blocked when the last message is a question.
/// Both files also get a SessionStart hook that binds the pane to the live session id, so a restart
/// resumes that session in any shell (<see cref="AgentBindScript"/>, #316).
/// The wrappers no-op (exit 0) outside agwinterm and never fail a turn.
/// </summary>
public static class AgentHooks
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string WrapperPath => Path.Combine(LocalAppData, "agwinterm", "agwinterm-agent-status.ps1");
    public static string ClaudeSettingsPath => Path.Combine(Home, ".claude", "settings.json");
    public static string CodexNotifyPath => Path.Combine(LocalAppData, "agwinterm", "agwinterm-codex-notify.ps1");
    public static string CodexHookPath => Path.Combine(LocalAppData, "agwinterm", "agwinterm-codex-hook.ps1");
    public static string CodexHooksPath => Path.Combine(Home, ".codex", "hooks.json");
    public static string AgentBindPath => Path.Combine(LocalAppData, "agwinterm", "agwinterm-agent-bind.ps1");

    /// <summary>SessionStart handler for both agents: argv[0] is the agent, the event JSON (session_id,
    /// cwd) arrives on stdin. It reports the id, the cwd and its own PID with <c>session.bind</c>; agwinterm
    /// walks the process tree from that PID to confirm the hook belongs to the pane's own agent, reads
    /// the agent's mode flags off its command line and composes the relaunch for the pane's shell.</summary>
    public const string AgentBindScript =
        """
        param([string]$Agent)
        # agwinterm SessionStart hook: bind the pane to the live session so a restart resumes it. No-op outside agwinterm.
        if (-not $env:AGWINTERM_SESSION_ID) { exit 0 }
        $o = $null
        try { $o = [System.IO.StreamReader]::new([Console]::OpenStandardInput(), [Text.Encoding]::UTF8).ReadToEnd() | ConvertFrom-Json } catch { }
        if (-not $o -or -not $o.session_id) { exit 0 }
        # A nested headless run inherits AGWINTERM_SESSION_ID. agwinterm refuses one by the process tree anyway;
        # these cheap tells spare it the lookup: `claude -p` reports ENTRYPOINT sdk-cli (the TUI: cli) and
        # ATTENDED 0, and `codex exec` writes source "exec" into its rollout (the TUI: "cli").
        if ($Agent -eq 'claude' -and (($env:CLAUDE_CODE_ENTRYPOINT -and $env:CLAUDE_CODE_ENTRYPOINT -ne 'cli') -or $env:CLAUDE_CODE_SESSION_ATTENDED -eq '0')) { exit 0 }
        if ($Agent -eq 'codex') {
          try {
            if ($o.transcript_path -and (Test-Path -LiteralPath $o.transcript_path)) {
              $meta = Get-Content -LiteralPath $o.transcript_path -TotalCount 1 -Encoding UTF8 | ConvertFrom-Json
              if ($meta.payload.source -and $meta.payload.source -ne 'cli') { exit 0 }
            }
          } catch { }
        }
        $req = @{ cmd = 'session.bind'; target = $env:AGWINTERM_SESSION_ID; args = @{ agent = $Agent; resume = "$($o.session_id)"; cwd = "$($o.cwd)"; pid = $PID } } | ConvertTo-Json -Compress
        $pipe = if ($env:AGWINTERM_PIPE) { $env:AGWINTERM_PIPE } else { 'agwinterm' }
        try {
          $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
          $c.Connect(1000)
          $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true
          $w.WriteLine($req)
          # Wait (bounded) for the reply: agwinterm snapshots the process tree from this PID before it
          # answers, so the hook must still be alive when it does.
          $null = (New-Object System.IO.StreamReader($c)).ReadLineAsync().Wait(3000)
          $c.Dispose()
        } catch { }
        exit 0
        """;

    /// <summary>Codex `notify` program: receives the event JSON as argv[0], maps it to a session status.
    /// Superseded by <see cref="CodexHookScript"/>; still written so a config.toml `notify` line from an
    /// earlier install keeps pointing at a script that exists.</summary>
    public const string CodexNotifyScript =
        """
        param([string]$Json)
        # agwinterm Codex notify hook: map Codex events to session status. No-op outside agwinterm.
        if (-not $env:AGWINTERM_SESSION_ID) { exit 0 }
        $state = 'completed'
        try {
          $o = $Json | ConvertFrom-Json
          switch ($o.type) {
            'agent-turn-complete' {
              # A turn that ends on a question is waiting for the user, not done — surface it as
              # blocked so the prompt stays visible (agterm #276).
              $msg = "$($o.'last-agent-message')".TrimEnd()
              if ($msg.EndsWith('?')) { $state = 'blocked' } else { $state = 'completed' }
            }
            default { $state = 'completed' }
          }
        } catch { }
        $pipe = if ($env:AGWINTERM_PIPE) { $env:AGWINTERM_PIPE } else { 'agwinterm' }
        try {
          $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
          $c.Connect(1000)
          $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true
          $w.WriteLine('{"cmd":"session.status","target":"' + $env:AGWINTERM_SESSION_ID + '","args":{"status":"' + $state + '"}}')
          $c.Dispose()
        } catch { }
        exit 0
        """;

    /// <summary>Codex hooks.json handler: argv[0] is the status (or <c>stop</c>), the event JSON arrives on
    /// stdin. Stop resolves to blocked when the turn ends on a question, like the notify script did.</summary>
    public const string CodexHookScript =
        """
        param([string]$State)
        # agwinterm Codex hook: push a hooks.json event as session status. No-op outside agwinterm.
        if (-not $env:AGWINTERM_SESSION_ID) { exit 0 }
        $o = $null
        try { $o = [System.IO.StreamReader]::new([Console]::OpenStandardInput(), [Text.Encoding]::UTF8).ReadToEnd() | ConvertFrom-Json } catch { }
        # A nested `codex exec` (an agent running it from a tool shell) inherits AGWINTERM_SESSION_ID and
        # fires the same hooks. Its rollout, already written when a hook runs, says source "exec"; the
        # TUI's says "cli". Only the pane's own TUI drives the pane's status.
        try {
          if ($o -and $o.transcript_path -and (Test-Path -LiteralPath $o.transcript_path)) {
            $meta = Get-Content -LiteralPath $o.transcript_path -TotalCount 1 -Encoding UTF8 | ConvertFrom-Json
            if ($meta.payload.source -and $meta.payload.source -ne 'cli') { exit 0 }
          }
        } catch { }
        if ($State -eq 'stop') {
          # A turn that ends on a question is waiting for the user, not done (agterm #276).
          $msg = "$($o.last_assistant_message)".TrimEnd()
          $State = if ($msg.EndsWith('?')) { 'blocked' } else { 'completed' }
        }
        $pipe = if ($env:AGWINTERM_PIPE) { $env:AGWINTERM_PIPE } else { 'agwinterm' }
        try {
          $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
          $c.Connect(1000)
          $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true
          $w.WriteLine('{"cmd":"session.status","target":"' + $env:AGWINTERM_SESSION_ID + '","args":{"status":"' + $State + '"}}')
          $c.Dispose()
        } catch { }
        exit 0
        """;

    public const string WrapperScript =
        """
        param([string]$State)
        # agwinterm agent-status hook: push status to the control pipe. No-op outside agwinterm.
        if (-not $env:AGWINTERM_SESSION_ID) { exit 0 }
        $pipe = if ($env:AGWINTERM_PIPE) { $env:AGWINTERM_PIPE } else { 'agwinterm' }
        try {
          $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
          $c.Connect(1000)
          $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true
          $w.WriteLine('{"cmd":"session.status","target":"' + $env:AGWINTERM_SESSION_ID + '","args":{"status":"' + $State + '"}}')
          $c.Dispose()
        } catch { }
        exit 0
        """;

    private static readonly (string Event, string? Matcher, string State)[] Hooks =
    {
        ("UserPromptSubmit", null, "active"),
        ("PostToolUse", null, "active"),
        ("Stop", null, "completed"),
        ("Notification", "permission_prompt", "blocked"),
    };

    // Codex has a real PermissionRequest event where Claude has Notification(permission_prompt);
    // "stop" is resolved by the script, which reads the last message from stdin.
    private static readonly (string Event, string? Matcher, string State)[] CodexHooks =
    {
        ("UserPromptSubmit", null, "active"),
        ("PostToolUse", null, "active"),
        ("PermissionRequest", null, "blocked"),
        ("Stop", null, "stop"),
    };

    private static string Command(string wrapper, string state)
        => $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{wrapper}\" {state}";

    /// <summary>
    /// Merge the status hooks into an existing settings.json string (or null/empty for a new file), and
    /// the SessionStart binding hook when <paramref name="bindScript"/> is given.
    /// Returns the merged JSON, or null if the existing content is non-empty but malformed
    /// (we refuse to clobber a hand-maintained file). Idempotent: entries already referencing
    /// the wrapper are not duplicated.
    /// </summary>
    public static string? MergeClaudeSettings(string? existing, string wrapper, string? bindScript = null)
        => MergeBind(MergeHooks(existing, wrapper, Hooks), bindScript, "claude");

    /// <summary>
    /// Merge the Codex status hooks (and the SessionStart binding hook when <paramref name="bindScript"/>
    /// is given) into an existing ~/.codex/hooks.json string, under the same rules as
    /// <see cref="MergeClaudeSettings"/>: the two files share the event → matcher group → handlers schema.
    /// </summary>
    public static string? MergeCodexHooks(string? existing, string script, string? bindScript = null)
        => MergeBind(MergeHooks(existing, script, CodexHooks), bindScript, "codex");

    private static string? MergeBind(string? merged, string? bindScript, string agent)
        => merged is null || bindScript is null ? merged : MergeHooks(merged, bindScript, new[] { ("SessionStart", (string?)null, agent) });

    private static string? MergeHooks(string? existing, string wrapper, (string Event, string? Matcher, string State)[] table)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(existing))
        {
            root = new JsonObject();
        }
        else
        {
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(existing); }
            catch { return null; }
            if (parsed is not JsonObject obj) return null;
            root = obj;
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var (evt, matcher, state) in table)
        {
            if (hooks[evt] is not JsonArray arr)
            {
                arr = new JsonArray();
                hooks[evt] = arr;
            }

            if (AlreadyInstalled(arr, wrapper)) continue;

            var entry = new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = Command(wrapper, state),
                }),
            };
            if (matcher is not null) entry["matcher"] = matcher;
            arr.Add(entry);
        }

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static bool AlreadyInstalled(JsonArray eventArr, string wrapper)
    {
        foreach (var entry in eventArr)
        {
            if (entry is JsonObject o && o["hooks"] is JsonArray hs)
                foreach (var h in hs)
                    if (h is JsonObject ho && ho["command"]?.GetValue<string>() is string cmd && cmd.Contains(wrapper))
                        return true;
        }
        return false;
    }

    /// <summary>Write the wrappers, merge the Claude and Codex hooks, and install the launcher and the
    /// generic bridge. Returns a multi-line human-readable summary covering every agent.</summary>
    public static string Install()
    {
        var lines = new List<string>();

        // --- Claude Code: wrapper + settings.json hooks (fully automatic) ---
        Directory.CreateDirectory(Path.GetDirectoryName(WrapperPath)!);
        File.WriteAllText(WrapperPath, WrapperScript);
        // The SessionStart binding script is shared by both agents (argv[0] names the agent).
        File.WriteAllText(AgentBindPath, AgentBindScript);

        string? existing = File.Exists(ClaudeSettingsPath) ? File.ReadAllText(ClaudeSettingsPath) : null;
        string? merged = MergeClaudeSettings(existing, WrapperPath, AgentBindPath);
        if (merged is null)
            lines.Add("Claude: refused — ~/.claude/settings.json exists but isn't valid JSON; left untouched");
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ClaudeSettingsPath)!);
            File.WriteAllText(ClaudeSettingsPath, merged);
            lines.Add("Claude Code: status + resume hooks -> " + ClaudeSettingsPath);
        }

        // --- Codex: hook script + hooks.json (JSON, so merged like settings.json; no TOML rewriting) ---
        try
        {
            File.WriteAllText(CodexHookPath, CodexHookScript);
            // An earlier install told the user to point config.toml's `notify` at this script: keep it
            // there. It pushes the same state the Stop hook does, so leaving the line in is harmless.
            File.WriteAllText(CodexNotifyPath, CodexNotifyScript);
            string? codexExisting = File.Exists(CodexHooksPath) ? File.ReadAllText(CodexHooksPath) : null;
            string? codexMerged = MergeCodexHooks(codexExisting, CodexHookPath, AgentBindPath);
            if (codexMerged is null)
                lines.Add("Codex: refused — ~/.codex/hooks.json exists but isn't valid JSON; left untouched");
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CodexHooksPath)!);
                File.WriteAllText(CodexHooksPath, codexMerged);
                lines.Add("Codex: status + resume hooks -> " + CodexHooksPath);
                lines.Add("  Codex runs new hooks only once you trust them: open /hooks in Codex and approve them");
            }
        }
        catch (Exception ex) { lines.Add("Codex: failed to install hooks: " + ex.Message); }

        // --- Claude launcher: transparent `claude` wrapper (session-id binding + auto-resume) ---
        lines.Add("Claude launcher: " + ClaudeIntegration.Install());

        // --- Generic agents: PowerShell-profile bridge keyed off $env:AGWINTERM_AGENT_RE ---
        lines.Add("Generic: " + GenericAgentInstaller.Install());

        return string.Join("\n", lines);
    }
}
