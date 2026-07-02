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

        Flags (any status): `--sound [name]` plays a cue (default alert, a system-sound name
        `beep|asterisk|exclamation|hand|question`, a Windows sound-event alias, or a `.wav` path);
        `--blink` pulses the sidebar dot + title-bar bell until cleared; `--auto-reset` clears the
        status back to idle the moment the user selects the session. Example, when you need attention:
        `agwintermctl session status blocked --sound --blink`.
        (A default blocked cue can also be set once via `blocked-sound =` in the config.)

        Better: run `agwintermctl install hooks` once. It wires Claude Code hooks so your status updates
        automatically (active while working, blocked on permission prompts, completed on stop); writes a
        Codex `notify` script (prints the one config.toml line to add); and installs a generic
        PowerShell-profile bridge that marks any command matching `$env:AGWINTERM_AGENT_RE` active/completed.

        ## Manage sessions & workspaces
        - `agwintermctl tree --json`                              — list workspaces+sessions (id, name, active, status)
        - `agwintermctl session new [--name N] [--cwd DIR] [--workspace ID|--workspace-name NAME [--create-workspace]] [--command "argv"]`
          — create a session (prints its id). `--command` runs that program as the session process (argv-style, no shell) instead of the shell.
        - `agwintermctl session select <id>` / `session close <id>`
        - `agwintermctl session go next|prev|first|last|next-attention|prev-attention` — move the active session
        - `agwintermctl session move --to up|down|top|bottom`     — reorder within its workspace
        - `agwintermctl session move <workspace-id>`             — relocate to another workspace
        - `agwintermctl workspace new [name]` / `workspace rename <name> [--target WS]` / `workspace select [--target WS]` / `workspace move --to <dir> [--target WS]` / `workspace delete [--target WS]`

        ## Read a session's output
        - `agwintermctl session text [--target <id>]`            — dump the session's visible buffer as plain text (great for reading command output)
        - `agwintermctl session copy [--target <id>]`            — return the session's current mouse text selection ("" if none)
        - `agwintermctl session search "<term>"`                 — open the find bar over the active session; returns "N of M" (or "no matches")
        - `agwintermctl session search --next|--prev|--close`    — step matches / close the find bar

        ## Type into a session
        - `agwintermctl session type "npm test" --target <id>`   — send keystrokes (newline = Enter)

        ## Scratch & quick terminals
        - `agwintermctl session scratch on|off|toggle [--target <id>]` — a per-session extra shell drawn over that session's content (opens in the session's cwd; stays alive when hidden; not restored)
        - `agwintermctl quick on|off|toggle`                     — the window's single throwaway shell, dropped over the active session (opens in the home dir; stays alive when hidden; not restored)

        ## Overlays (run a program over a session, ephemerally)
        - `agwintermctl session overlay open "<command>" [--size-percent N] [--wait] [--block] [--target <id>]`
          — run `<command>` in a throwaway terminal over the session; it vanishes when the program exits, leaving the session untouched. Returns the overlay id.
          `--size-percent N` (1..100) makes it a centered floating panel over a dimmed session (default = full content region). The session gets a `* (overlay)` tag in `tree`.
        - `--wait` keeps the panel after the program exits (shows "press any key to close") instead of auto-dismissing.
        - `--block` waits for the program to exit and returns `exit N` (its exit code). Good for `lazygit`, `htop`, an editor, or any pick-a-thing helper you want to run and read the result of.
        - `agwintermctl session overlay close [--target <id>]`   — dismiss the overlay now.
        - `agwintermctl session overlay result`                 — the last overlay's `exit N` (or `no overlay`).

        ## Notify the user (desktop notification)
        - `agwintermctl notify "build finished" [--title "npm"] [--target <id>]`
          — raise a notification against a session: an in-app banner (click it to jump to that session),
          a red count badge on the session's sidebar row (cleared when you next select it), and an OS
          tray balloon (unless `desktop-notifications = false` in the config). Great for signaling that a
          long task finished or that you need attention while the user is looking at another session.
          You can also emit it straight from the shell with an OSC sequence: `printf '\e]9;%s\a' "message"`
          (or OSC 777: `\e]777;notify;Title;Body\a`).

        ## Flag sessions & focus a workspace
        - `agwintermctl session flag on|off|toggle|clear [--target <id>]` — flag a session (a durable working-set
          mark shown as a flag on its sidebar row; survives moves; persists). `clear` unflags every session.
        - `agwintermctl sidebar mode tree|flagged|toggle`        — switch the sidebar between the workspace tree
          and a flat list of just the flagged sessions (great for a curated working set across workspaces).
        - `agwintermctl workspace focus on|off|toggle`           — show only the active workspace in the tree
          (hide the rest); a "show all" banner / this verb brings the others back.
        - `agwintermctl tree --json` reports `"flagged":true` per flagged session.
        - `agwintermctl session switch begin|advance|advance-back|commit|cancel` — drive the MRU (Ctrl+Tab)
          recency switcher programmatically (advance previews the next recent session; commit lands it). The
          interactive equivalent is holding Ctrl and tapping Tab (Shift+Tab walks back, Esc cancels).

        ## Splits, font, sidebar, theme
        - `agwintermctl session split on|off|toggle` · `session focus left|right|other` · `session resize --split-ratio 0.7` (or `--grow-left/--grow-right N`)
        - `agwintermctl font inc|dec|reset [--target <id>]`      — per-session font zoom
        - `agwintermctl sidebar show|hide|toggle|expand|collapse`
        - `agwintermctl theme list` · `agwintermctl theme set "Solarized Light"`

        ## Config / restore
        - `agwintermctl keymap reload`                           — re-parse keymap.conf (reports diagnostics)
        - `agwintermctl restore clear`                           — clear the saved session-tree state
        - `agwintermctl install hooks|skill|shell`               — install agent-status hooks / this skill / shell-integration (live cwd)

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
