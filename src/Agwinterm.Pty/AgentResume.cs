using System.Text;
using System.Text.RegularExpressions;

namespace Agwinterm.Pty;

/// <summary>
/// The pure half of the SessionStart resume binding (#316). An agent's SessionStart hook reports the live
/// session id, its cwd (Devin's project directory, as Devin reports none) and its own PID; the host takes one process snapshot and asks this class three things:
/// which process is the agent that fired the hook (and is it the pane's own, not one nested under another
/// agent), which of its flags must survive a relaunch, and what to type into the pane's shell to resume it.
///
/// <para>The binding used to come only from the PowerShell <c>claude</c> wrapper, which assumed Claude's
/// session id is the pane id. That fails once the pane is recreated or the user runs <c>/resume</c> or
/// <c>/clear</c>, and a Git Bash pane never had a wrapper at all. SessionStart fires on startup, resume,
/// clear and compact, so the id it reports is always the one to resume.</para>
/// </summary>
public static class AgentResume
{
    /// <summary>One row of a process snapshot (Win32_Process). <paramref name="Created"/> orders siblings.</summary>
    public sealed record ProcRow(int Pid, int ParentPid, string Name, string CommandLine, long Created = 0);

    public enum ShellKind { PowerShell, Bash, Cmd, Other }

    public static readonly string[] Agents = { "claude", "codex", "devin" };

    private static readonly Regex SessionIdRe = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);
    private static readonly Regex FlagValueRe = new("^[A-Za-z0-9_.:-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>A session id every CLI accepts and that is safe to type unquoted into any shell.</summary>
    public static bool IsValidSessionId(string? id) => id is not null && SessionIdRe.IsMatch(id);

    public static bool IsKnownAgent(string? agent) => agent is not null && Array.IndexOf(Agents, agent) >= 0;

    /// <summary>The shell a pane runs, from its profile's executable (a path or a bare name).</summary>
    public static ShellKind ClassifyShell(string? exe)
    {
        string name = Path.GetFileName(exe ?? "").ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4];
        return name switch
        {
            "powershell" or "pwsh" => ShellKind.PowerShell,
            "bash" or "sh" or "zsh" => ShellKind.Bash,   // Git Bash, MSYS2, Cygwin: POSIX syntax, Windows paths accepted
            "cmd" => ShellKind.Cmd,
            _ => ShellKind.Other,                        // wsl and anything custom: no directory prefix
        };
    }

    /// <summary>Which agent a process is: the native binary, or node running the npm package (the launcher a
    /// shim starts, whose child is the native binary for Codex). Null for anything else.</summary>
    public static string? AgentOf(ProcRow p)
    {
        string name = p.Name.ToLowerInvariant();
        if (name == "claude.exe") return "claude";
        if (name == "codex.exe") return "codex";
        if (name == "devin.exe") return "devin";
        if (name == "node.exe")
        {
            string cmd = p.CommandLine.Replace('\\', '/');
            if (cmd.Contains("@anthropic-ai/claude-code", StringComparison.OrdinalIgnoreCase)) return "claude";
            if (cmd.Contains("@openai/codex", StringComparison.OrdinalIgnoreCase)) return "codex";
        }
        return null;
    }

    /// <summary>
    /// Walk up from the hook's process towards the pane's shell and return the command line the user started
    /// the agent with: the outermost process of the first run of <paramref name="agent"/> processes (a node
    /// launcher over the native binary counts as one agent). Null, with the reason in <paramref name="why"/>,
    /// when no such agent is found before the walk ends, or when another agent sits above it (a nested
    /// <c>claude -p</c> or <c>codex exec</c> run from an agent's tool shell, which inherits the pane's
    /// AGWINTERM_SESSION_ID). Also null for a <c>devin -p</c>: Devin's SessionStart does not tell a print run
    /// from the TUI, so its command line has to, and a Devin whose command line could not be read is refused
    /// for the same reason.
    ///
    /// <para>The walk ends at the shell, or where a parent is no longer in the snapshot. The second is
    /// normal in Git Bash: MSYS implements exec by starting a new Windows process and letting the old one go,
    /// so an npm shim run by bash (<c>sh.exe</c> over node) has a parent that has already exited. The agent
    /// found below such a break is accepted; the nested-run check then covers only what the walk saw, and
    /// the hook's own tells (a headless entrypoint, an exec rollout) cover the rest.</para>
    /// </summary>
    public static string? FindAgentCommandLine(IReadOnlyDictionary<int, ProcRow> procs, int hookPid, int shellPid, string agent, out string? why)
    {
        ProcRow? outer = null;
        bool inRun = false;
        int pid = hookPid;
        for (int depth = 0; depth < 64 && procs.TryGetValue(pid, out var row); depth++)
        {
            if (row.Pid == shellPid) break;
            string? a = AgentOf(row);
            if (outer is null)
            {
                if (a == agent) { outer = row; inRun = true; }
            }
            else if (inRun && a == agent) outer = row;
            else
            {
                inRun = false;
                if (a is not null) { why = $"nested: this {agent} runs under another {a} in the pane"; return null; }
            }
            if (row.ParentPid == row.Pid) break;
            pid = row.ParentPid;
        }
        if (outer is not null && agent == "devin" && string.IsNullOrWhiteSpace(outer.CommandLine))
        { why = "devin's command line could not be read, so a devin -p run cannot be ruled out"; return null; }
        if (outer is not null && agent == "devin" && IsDevinPrint(outer.CommandLine))
        { why = "headless: a devin -p run, not the pane's session"; return null; }
        why = outer is null ? $"no {agent} process above the hook" : null;
        return outer?.CommandLine;
    }

    private static bool IsDevinPrint(string commandLine)
    {
        var args = SplitCommandLine(commandLine);
        for (int i = 1; i < args.Count && args[i] != "--"; i++)
            if (args[i] is "-p" or "--print" || args[i].StartsWith("--print=", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The flags of the running agent that a resume must repeat: the permission and sandbox mode, so
    /// a YOLO session comes back YOLO. Values outside a conservative character set are dropped rather than
    /// quoted, which keeps the relaunch line free of anything a shell could interpret.</summary>
    public static IReadOnlyList<string> ResumeFlags(string agent, string commandLine)
    {
        var bare = agent switch
        {
            "codex" => new[] { "--dangerously-bypass-approvals-and-sandbox" },
            "devin" => new[] { "--sandbox" },
            _ => new[] { "--dangerously-skip-permissions" },
        };
        var valued = agent == "codex"
            ? new[] { "-s", "--sandbox", "-a", "--ask-for-approval", "-p", "--profile" }
            : new[] { "--permission-mode" };

        var args = SplitCommandLine(commandLine);
        var flags = new List<string>();
        for (int i = 1; i < args.Count; i++)
        {
            string a = args[i];
            if (Array.IndexOf(bare, a) >= 0) { if (!flags.Contains(a)) flags.Add(a); continue; }
            // `--sandbox value` and `--sandbox=value`; short flags only in the spaced form.
            int eq = a.StartsWith("--", StringComparison.Ordinal) ? a.IndexOf('=') : -1;
            string key = eq > 0 ? a[..eq] : a;
            if (Array.IndexOf(valued, key) < 0) continue;
            string? value = eq > 0 ? a[(eq + 1)..] : (i + 1 < args.Count ? args[++i] : null);
            if (value is not null && FlagValueRe.IsMatch(value)) { flags.Add(key); flags.Add(value); }
        }
        return flags;
    }

    /// <summary>
    /// The line typed into a restored pane to resume the session: change to its directory in the pane's own
    /// shell syntax, then resume by id with <paramref name="flags"/>. PowerShell 5.1 has no <c>&amp;&amp;</c>, so it
    /// gets <c>;</c>. An empty cwd, or a shell whose syntax is unknown, gets the resume alone.
    ///
    /// <para>The directory is the agent's, so its name is not ours to trust: the line is typed into a shell.
    /// One with a control character is left out (a newline would end the line and start another). In
    /// PowerShell every single-quote character is doubled, see <see cref="PowerShellQuoted"/>. cmd has no
    /// escape inside its double quotes for <c>"</c> or for <c>%</c>, which an interactive cmd still expands
    /// there (<c>C:\work\%OS%</c> would be typed as another directory), so such a directory is left out.</para>
    /// </summary>
    public static string Compose(ShellKind shell, string agent, string sessionId, string? cwd, IReadOnlyList<string> flags)
    {
        var run = new StringBuilder(agent switch { "codex" => "codex resume ", "devin" => "devin --resume ", _ => "claude --resume " }).Append(sessionId);
        foreach (string f in flags) run.Append(' ').Append(f);
        if (string.IsNullOrWhiteSpace(cwd) || cwd.Any(char.IsControl)) return run.ToString();
        return shell switch
        {
            ShellKind.PowerShell => $"Set-Location -LiteralPath '{PowerShellQuoted(cwd)}'; {run}",
            ShellKind.Bash => $"cd '{cwd.Replace("'", "'\\''")}' && {run}",
            ShellKind.Cmd when !cwd.Contains('"') && !cwd.Contains('%') => $"cd /d \"{cwd}\" && {run}",
            _ => run.ToString(),
        };
    }

    /// <summary>
    /// <paramref name="value"/> as the inside of a PowerShell single-quoted string. PowerShell ends such a
    /// string at any of five characters, not only the ASCII apostrophe: U+0027, U+2018, U+2019, U+201A and
    /// U+201B (its own <c>CodeGeneration.EscapeSingleQuotedStringContent</c> doubles all five). Doubling only
    /// the apostrophe let a directory named <c>x\u2019; Start-Process calc; \u2019</c> close the string and
    /// run the rest when the line was typed on restore.
    /// </summary>
    public static string PowerShellQuoted(string value)
    {
        var quoted = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            quoted.Append(c);
            if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B') quoted.Append(c);
        }
        return quoted.ToString();
    }

    /// <summary>Split a Windows command line the way CommandLineToArgvW does: whitespace separates arguments
    /// outside quotes, 2n backslashes before a quote are n backslashes and the quote toggles quoting, 2n+1
    /// are n backslashes and a literal quote, and backslashes elsewhere are literal.</summary>
    public static List<string> SplitCommandLine(string commandLine)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false, have = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\')
            {
                int n = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { n++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    cur.Append('\\', n / 2);
                    if (n % 2 == 1) cur.Append('"'); else inQuotes = !inQuotes;
                }
                else { cur.Append('\\', n); i--; }
                have = true;
            }
            else if (c == '"') { inQuotes = !inQuotes; have = true; }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (have) { args.Add(cur.ToString()); cur.Clear(); have = false; }
            }
            else { cur.Append(c); have = true; }
        }
        if (have) args.Add(cur.ToString());
        return args;
    }
}
