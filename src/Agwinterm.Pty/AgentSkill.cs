using System.IO;

namespace Agwinterm.Pty;

/// <summary>
/// The bundled agent skill (agterm-style self-onboarding): a SKILL.md that teaches an
/// agent to drive agwinterm via agwintermctl / the control pipe. Installed to
/// ~/.claude/skills/agwinterm/ and ~/.codex/skills/agwinterm/ so an LLM self-discovers it.
/// </summary>
public static class AgentSkill
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public const string SkillMarkdown =
        """
        ---
        name: agwinterm
        description: Use when running inside the agwinterm terminal (env AGWINTERM_ENABLED=1) to control it — report agent status, create/switch/close sessions, type into sessions, query the session tree, and display images — via the agwintermctl CLI or its control pipe.
        ---

        # agwinterm

        agwinterm is a Windows terminal built for AI coding agents. When you run inside it you can
        control it: report your status (a colored dot per session in the sidebar), manage sessions,
        type into sessions, and show images — via the `agwintermctl` CLI (preferred) or by writing
        one newline-delimited JSON request to its control pipe.

        ## Detect
        You are inside agwinterm when `AGWINTERM_ENABLED=1`. Relevant env vars:
        - `AGWINTERM_SESSION_ID` — your session id (the default target for commands).
        - `AGWINTERM_WINDOW_ID` — your window id.
        - `AGWINTERM_PIPE` — the control pipe name (full path `\\.\pipe\<name>`).

        ## Report your status (do this — it is the point)
        Let the user see your state at a glance:
        - `agwintermctl session status active`     — working
        - `agwintermctl session status blocked`    — you need the user
        - `agwintermctl session status completed`  — finished
        - `agwintermctl session status idle`       — clear it

        Better: run `agwintermctl install hooks` once. It wires Claude Code hooks so your status
        updates automatically (active while working, blocked on permission prompts, completed on stop).

        ## Manage sessions
        - `agwintermctl tree --json`                              — list sessions (id, name, active, status)
        - `agwintermctl session new --name "tests" --cwd C:\repo` — create one (prints its id)
        - `agwintermctl session select <id>`                     — switch to it
        - `agwintermctl session close <id>`                      — close it

        ## Type into a session
        - `agwintermctl session type "npm test" --target <id>`   — send keystrokes (newline = Enter)

        ## Show an image inline
        - `agwintermctl image show C:\path\pic.png --row 2 --col 4`
        Note: Windows ConPTY strips inline terminal-graphics sequences, so images MUST be delivered
        through this control channel — not by printing escape codes to stdout.

        ## If agwintermctl is not on PATH
        Send one JSON line to the pipe named by `%AGWINTERM_PIPE%` (default `agwinterm`), e.g. in PowerShell:
        ```
        $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $env:AGWINTERM_PIPE, 'InOut')
        $c.Connect(2000); $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush=$true
        $w.WriteLine('{"cmd":"session.status","target":"' + $env:AGWINTERM_SESSION_ID + '","args":{"status":"active"}}')
        ```
        Request shape: `{"cmd":"<area>.<command>","target":"<id|prefix|active>","args":{...}}`.
        Response: `{"ok":true,"result":...}` or `{"ok":false,"error":"..."}`.
        """;

    public static string Install()
    {
        int written = 0;
        foreach (var baseDir in new[] { Path.Combine(Home, ".claude"), Path.Combine(Home, ".codex") })
        {
            try
            {
                string dir = Path.Combine(baseDir, "skills", "agwinterm");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "SKILL.md"), SkillMarkdown);
                written++;
            }
            catch { /* skip a tool that isn't present */ }
        }
        return $"installed agent skill to {written} location(s) (~/.claude/skills/agwinterm, ~/.codex/skills/agwinterm)";
    }
}
