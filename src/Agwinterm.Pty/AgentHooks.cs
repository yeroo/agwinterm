using System.IO;
using System.Text.Json.Nodes;

namespace Agwinterm.Pty;

/// <summary>
/// Installs Claude Code status hooks (agterm-style self-setup). Writes a PowerShell
/// wrapper that pushes session status to agwinterm's control pipe, and idempotently
/// merges the four hooks into ~/.claude/settings.json:
///   UserPromptSubmit -> active, PostToolUse -> active, Stop -> completed,
///   Notification(permission_prompt) -> blocked.
/// The wrapper no-ops (exit 0) outside agwinterm and never fails a turn.
/// </summary>
public static class AgentHooks
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string WrapperPath => Path.Combine(LocalAppData, "agwinterm", "agwinterm-agent-status.ps1");
    public static string ClaudeSettingsPath => Path.Combine(Home, ".claude", "settings.json");
    public static string CodexNotifyPath => Path.Combine(LocalAppData, "agwinterm", "agwinterm-codex-notify.ps1");
    public static string CodexConfigPath => Path.Combine(Home, ".codex", "config.toml");

    /// <summary>Codex `notify` program: receives the event JSON as argv[0], maps it to a session status.</summary>
    public const string CodexNotifyScript =
        """
        param([string]$Json)
        # agwinterm Codex notify hook: map Codex events to session status. No-op outside agwinterm.
        if (-not $env:AGWINTERM_SESSION_ID) { exit 0 }
        $state = 'completed'
        try {
          $o = $Json | ConvertFrom-Json
          switch ($o.type) {
            'agent-turn-complete' { $state = 'completed' }
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

    private static string Command(string wrapper, string state)
        => $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{wrapper}\" {state}";

    /// <summary>
    /// Merge the status hooks into an existing settings.json string (or null/empty for a new file).
    /// Returns the merged JSON, or null if the existing content is non-empty but malformed
    /// (we refuse to clobber a hand-maintained file). Idempotent: entries already referencing
    /// the wrapper are not duplicated.
    /// </summary>
    public static string? MergeClaudeSettings(string? existing, string wrapper)
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

        foreach (var (evt, matcher, state) in Hooks)
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

    /// <summary>Write the wrappers, merge the Claude hooks, install the generic bridge, and print the
    /// Codex config line. Returns a multi-line human-readable summary covering every agent.</summary>
    public static string Install()
    {
        var lines = new List<string>();

        // --- Claude Code: wrapper + settings.json hooks (fully automatic) ---
        Directory.CreateDirectory(Path.GetDirectoryName(WrapperPath)!);
        File.WriteAllText(WrapperPath, WrapperScript);

        string? existing = File.Exists(ClaudeSettingsPath) ? File.ReadAllText(ClaudeSettingsPath) : null;
        string? merged = MergeClaudeSettings(existing, WrapperPath);
        if (merged is null)
            lines.Add("Claude: refused — ~/.claude/settings.json exists but isn't valid JSON; left untouched");
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ClaudeSettingsPath)!);
            File.WriteAllText(ClaudeSettingsPath, merged);
            lines.Add("Claude Code: status hooks -> " + ClaudeSettingsPath);
        }

        // --- Codex: notify script (config line must be added by the user; we don't rewrite TOML) ---
        try
        {
            File.WriteAllText(CodexNotifyPath, CodexNotifyScript);
            string tomlLine = "notify = [\"powershell\",\"-NoProfile\",\"-ExecutionPolicy\",\"Bypass\",\"-File\",\""
                + CodexNotifyPath.Replace("\\", "\\\\") + "\"]";
            lines.Add("Codex: wrote " + CodexNotifyPath);
            lines.Add("  add this line to " + CodexConfigPath + " :");
            lines.Add("    " + tomlLine);
        }
        catch (Exception ex) { lines.Add("Codex: failed to write notify script: " + ex.Message); }

        // --- Claude launcher: transparent `claude` wrapper (session-id binding + auto-resume) ---
        lines.Add("Claude launcher: " + ClaudeIntegration.Install());

        // --- Generic agents: PowerShell-profile bridge keyed off $env:AGWINTERM_AGENT_RE ---
        lines.Add("Generic: " + GenericAgentInstaller.Install());

        return string.Join("\n", lines);
    }
}
