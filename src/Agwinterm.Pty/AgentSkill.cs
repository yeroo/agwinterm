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
        - `AGWINTERM_SESSION_ID` — your session id (the default target for commands). Unique PER PANE:
          two agents in a split can't collide (each pane resolves as its own target).
        - `AGWINTERM_PANE_ID` — explicit pane identity (same value; use when you specifically mean the pane).
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
        - `agwintermctl tree --json`                              — list workspaces+sessions (id, name, active, status,
          `statusChangedAt` = epoch SECONDS of the last status write on the pane whose status the node is
          showing — its liveness clock. Every status write to that pane restamps it, including a re-assert of
          the same status, so `now - statusChangedAt` is how long ago that agent last said anything: a large
          age next to `"status":"active"` means the hook died, not that work is still running. In a split
          session the age belongs to the pane whose status won: a write to a pane that LOSES the
          aggregate does not move it, but panes tied at the winning status all do, so two panes both
          `active` report the freshest of the two: the session-level age cannot tell a dead hook
          from a live one beside it, and no verb reports the stamp per pane. Always present, even
          for an idle session that never set one)
        - `agwintermctl events [--since CURSOR] [--limit N]`      — poll the event log (status/notification/session/tree changes); returns {cursor, events:[{seq,type,session,info}]}. Pass the returned cursor as --since next poll.
        - `agwintermctl session new [--name N] [--cwd DIR] [--workspace ID|--workspace-name NAME [--create-workspace]] [--command "argv"] [--profile NAME] [--no-select] [--wait]`
          `--no-select` creates the session in the background without stealing focus or changing the current selection.
          `--wait` (with `--command`) holds the session on "press any key" after the command exits, so its final output stays readable.
          — create a session (prints its id). `--command` runs that program as the session process (argv-style, no shell) instead of the shell.
          `--profile NAME` picks a shell profile (default = Windows PowerShell).
          `--workspace` is a workspace id (or unique id prefix; `tree --json` lists them), `--workspace-name` its sidebar label
          (case-insensitive). **An unknown workspace is refused** (`ok:false`, no session created) — it is never swapped for the
          active workspace: an unknown id, or an unknown name without `--create-workspace`, is an error that names the value.
          `--workspace-name NAME --create-workspace` creates the workspace when it does not exist and reuses it when it does.
          `--workspace` and `--workspace-name` together are refused; pass one. **Omit both and the session lands in YOUR
          workspace** — the workspace of the pane you run in (the CLI sends its `AGWINTERM_SESSION_ID` as the caller), however
          the user has clicked around meanwhile. Sessions go next to you unless you say otherwise, and `--workspace` is how you
          say otherwise. Only a CLI whose pane the answering window cannot see falls back to the active workspace — the one
          the user last clicked, which moves under you: a script from an unrelated shell, a pane that has since been closed,
          or a pane in ANOTHER WINDOW (with no `--window`, the frontmost window answers, and its host knows only its own
          panes — pass `--window` to reach yours, see "Windows").
        - `agwintermctl session duplicate [ID]` — clone a session (same cwd + shell profile) as a new session (default: active).
        - `agwintermctl session restore "<command>" --target PANE` — pin a command to re-run on every restart for that pane; `none` clears.
          The target is mandatory (no active-pane default: a pin outlives whatever is active now; inside a session `AGWINTERM_SESSION_ID`
          is the pane). Replies `{action:"pinned"|"cleared", pane, session[, command]}` naming the pane it landed on — a session
          NAME lands on its focused pane, a session ID on the pane that carries that id while one does, and on the FOCUSED
          pane while none does (see the splits section for when that is) — exactly as `session type` does. Read it back in
          `tree --json` as `restoreCommands`: an object keyed by pane id listing only pinned panes (absent when none).
          An unknown target, or a scratch/overlay/quick pane (never restored), is refused and nothing is pinned.
        - `agwintermctl profiles list` — shell profiles (cmd, Windows PowerShell, PowerShell 7, Git Bash, WSL:*, custom); `* ` marks the default.
          Profiles live in `%LOCALAPPDATA%\agwinterm\profiles.json` (auto-seeded from detected shells; edit to add your own — name/command/args/cwd/icon/env); `agwintermctl profiles reload` re-reads it.
        - `agwintermctl session select <id>` / `session close <id>`
        - `agwintermctl session rename <new-name> [--target ID]` — set a session's custom name (sidebar + title bar)
        - `agwintermctl session context <text> [--target ID]` — ONE LINE of "what is this pane for", shown dimmed after the
          name in the title bar and the sidebar row and on the palette's second line, where the name has to stay short.
          It survives a restart (and `session reopen`), and `tree --json` reads it back as `context` on the session node
          (absent when none) — so set it when you start a task and any agent can see what each pane is doing. The value
          is one line: a newline, tab or other control character is REFUSED (a control byte in the title bar is a
          rendering accident, not a context), blank is refused, and more than 200 characters is refused — the ceiling is
          the display budget of the row, not a storage limit. `--clear` removes it (text beside `--clear` is refused);
          `--stdin` takes the text from stdin the way `session type --stdin` does (one trailing newline dropped, an
          embedded one then refused). Replies `{session, context}` with the value IN EFFECT after the write, read off the
          session; the target resolves as `session rename` does (a scratch/overlay cover id lands on the session under it).
        - `agwintermctl session seen [--target ID]` — clear a session's unseen-notification badge headlessly
        - `agwintermctl session output [--target ID]` — the LAST COMPLETED command's output (FTCS marks;
          pwsh sessions emit them automatically) — cleaner than parsing `session text` yourself
        - `agwintermctl sidebar state` — read-back `<visible|hidden> <tree|flagged> <width>`, e.g. `visible tree 220`:
          visibility, view mode and width (DIP) in one call (`ping` reports the app version)
        - `agwintermctl sidebar width [N]` — read (no N) or set the sidebar width in device-independent pixels. Replies
          `{width, visible[, applied[, note]]}` with the width ACTUALLY in effect, so compare `width` with what you asked
          for. N outside 120..600 is REFUSED with the range named (nothing moves), never clamped — `sidebar hide` is how
          you ask for no sidebar. A set while the sidebar is hidden is remembered and persisted but not applied
          (`applied:false` + a note); it takes effect on the next `sidebar show`. Persists across restarts.
        - `agwintermctl version [--json]` — which CLI binary you just ran (version + its resolved path) and which app
          answered (version + pipe), on two greppable lines, `cli` and `app`. Several agwintermctl.exe can coexist and
          none need be on PATH; this says which one this was. It exits 0 and still prints the `cli` half when no app
          answers, marking the app `unavailable` — that is the case it exists for
        - `agwintermctl session go next|prev|first|last|next-attention|prev-attention` — move the active session
        - `agwintermctl session move --to up|down|top|bottom`     — reorder within its workspace
        - `agwintermctl session move <workspace-id>`             — relocate to another workspace
        - `agwintermctl workspace new [name]` / `workspace rename <name> [--target WS]` / `workspace select [--target WS]` / `workspace move --to <dir> [--target WS]` / `workspace delete [--target WS]`
        - `agwintermctl workspace collapse [WS] [--target WS]` / `workspace expand [WS]` — collapse/expand a single workspace's session list (default: active)

        ## Read a session's output
        - `agwintermctl session text [--lines N] [--target <id>]` — dump the pane as plain text. Without `--lines` that is the
          visible screen; `--lines N` returns the last N lines ending at the bottom of the screen, reaching back into
          scrollback — which is where a launch banner or an error printed before a full-screen app started still lives
        - `agwintermctl session copy [--target <id>]`            — return the session's current mouse text selection ("" if none)
        - `agwintermctl surface cursor [--target <id>]`          — the caret COLUMN of a pane, as a bare integer.
          Use it before typing into ANOTHER agent's composer: an empty composer parks the caret at a known
          column, so a different column means a draft is sitting there and you should not send. The same
          column is necessary, not sufficient: a draft whose length is an exact multiple of the pane width
          wraps the caret back to the column it started at, so back a match with `session text` of the
          composer row before typing. After a print into the last column the answer equals the pane width
          (the wrap is deferred), so do not use it as an index into a `session text` row without clamping.
          A pane id
          reports that pane; a session NAME reports its focused pane. (A session id targets the pane that carries it
          while one does, same as `session text`/`session type`, so the pane you check is the pane you type into. While
          NO pane carries it — the carrier was closed, however it was closed — the id behaves like a NAME and reaches the
          FOCUSED pane, which can change under you: in a split session, address panes by the ids `tree --json` lists.)
          Reading rendered text and guessing at placeholder strings is what this replaces
        - `agwintermctl session search "<term>"`                 — open the find bar over the active session; returns "N of M" (or "no matches")
        - `agwintermctl session search --next|--prev|--close`    — step matches / close the find bar

        ## Selection & clipboard
        - `agwintermctl selection all [--target <id>]`           — select the whole buffer (scrollback + live grid)
        - `agwintermctl selection copy [--target <id>]`          — copy the current selection to the Windows clipboard
        - `agwintermctl selection clear [--target <id>]`         — clear the selection
        - `agwintermctl session paste "<text>" [--target <id>]`  — paste text into the pane (clipboard if text omitted; honors bracketed paste)
        - keys: Ctrl+C (copy selection) · Ctrl+V (paste) · Ctrl+Shift+A (select all) · double/triple-click = word/line · drag past the edge auto-scrolls
        - config `copy-on-select = true` auto-copies each finished selection (no Ctrl+C needed)

        ## Type into a session
        - `agwintermctl session type "npm test" --target <id>`   — send keystrokes (newline = Enter). Control bytes are
          REFUSED, not stripped: a NUL would truncate your command while its Return still fired. Add `--allow-control`
          when you really mean one (an escape sequence for a TUI, a lone ^C). `session write` is NOT the way — it
          injects into the display and never reaches the shell
        - `agwintermctl session type --stdin --target <id>`      — the text is STDIN, as bytes. This is how text with
          quotes, newlines, runs of spaces or a leading `--` is sent: positionals are re-joined with one space and
          the option parser eats a leading `--`, both silently. Pipe a here-string (`@"..."@ | agwintermctl session
          type --stdin --target <id>`) or redirect a file. Exactly one trailing newline is dropped (the one the
          shell adds), so end the text with TWO newlines to press Enter. Invalid UTF-8 is refused with the byte
          offset and NOTHING is sent (exit 2). `--stdin` with positional text or `--select` is refused as ambiguous.
          `--allow-control` still applies. There is no `quick type` verb: the quick terminal is a pane whose id starts
          with `quick:`, so `session type --stdin --target quick:` types into it (once `quick on` has created it)

        ## Scratch & quick terminals
        - `agwintermctl session scratch on|off|toggle [--target <id>]` — a per-session extra shell drawn over that session's content (opens in the session's cwd; stays alive when hidden; not restored)
        - `agwintermctl quick on|off|toggle`                     — the window's single throwaway shell, dropped over the active session (opens in the home dir; stays alive when hidden; not restored)

        ## Overlays (run a program over a session, ephemerally)
        - `agwintermctl session overlay open "<command>" [--size-percent N] [--wait] [--block] [--target <id>]`
          — run `<command>` in a throwaway terminal over the session; it vanishes when the program exits, leaving the session untouched. Returns the overlay id.
          `--size-percent N` (1..100) makes it a centered floating panel over a dimmed session (default = full content region). The session gets a `* (overlay)` tag in `tree`.
          The range is VALIDATED, not clamped: `0`, `-5`, `150`, `sixty` and a quoted `"60"` are each refused (`ok:false`,
          the value and the range named) and NO overlay opens — read `tree` to confirm. To ask for the full region, omit
          the flag; there is no number that means it. The same rule applies to `overlay resize --size-percent N`, whose
          reply `resized N%` is always the N you asked for.
        - `--wait` keeps the panel after the program exits (shows "press any key to close") instead of auto-dismissing.
        - `--block` waits for the program to exit and returns `exit N` (its exit code). Good for `lazygit`, `htop`, an editor, or any pick-a-thing helper you want to run and read the result of.
          The reply is the outcome of the overlay THIS call opened: `closed` when it was closed or replaced before its
          program exited. `exit 1` also covers a program that could not be started at all (the session's cwd gone,
          the pty-host down), and a blocking open closes its pane as it replies, so AFTER the call `exit 1` cannot be
          told from a program that ran and failed. When that distinction matters, do not block: open with `--wait`
          (the pane stays after the exit and the reply is its id), poll `overlay result` until it says `exit N`,
          `session text --target <that id>` for the output or the start failure (`exit N` is written when the process
          ends, not when its output is drained — the last lines can still be landing; read again if the text looks
          cut off), then `overlay close --target <that id>` (a bare `close` closes the ACTIVE session's overlay, which
          need not be yours; that close answers `ok:false` when the overlay is already gone — a key pressed on the
          `--wait` prompt, a later open replacing it — which means closed, not failed; `tree` settles it). `overlay result` is ONE value
          per window — reset to `no overlay` by any open in the window, written by any session's overlay exit — so
          the poll is trustworthy only while yours is the only overlay in the window; `tree` shows which sessions
          have one.
          The window closing under a blocking open answers `ok:false` with the status unknown.
        - `agwintermctl session overlay close [--target <id>]`   — dismiss the overlay now.
        - `agwintermctl session overlay result`                 — the last overlay's `exit N` (or `no overlay`): one value per
          window, not per session — reset by any open in the window, written by whichever session's overlay exits
          next; it ignores `--target`. Two overlays in one window make it name either one's exit.
        - What is REFUSED (`ok:false`, nothing happened): `open` with no command; `open` and `resize` whenever no
          session resolves (a `--target` that matches nothing, or no target while no session is active); a `close`
          whose `--target` names something that does not exist; `open`, `close` and `resize` whose `--target` names
          one pane of a split session (an overlay covers the whole session — the refusal names the session id to
          pass instead); and `resize` on a session with no overlay open.
          One `resize` `ok:false` is NOT "nothing happened": the reply that says the window did not run the request
          within 15 s — that resize is still queued and may land when the window's loop resumes; read `tree`.
          `close` answers `ok` ("no overlay") when the session resolves and has no overlay, or when the target is
          absent, empty or `active` while nothing is active — closing nothing leaves "no overlay open" true. These used to
          answer `ok:true` with the failure as the result text; branch on `ok`.

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

        ## Windows (multi-window)
        Each window is independent: its own workspace/session tree + sidebar. Sessions never move
        between windows — instead you TARGET a window.
        - `agwintermctl window list`                             — id (short), name, whether open + which is active.
        - `agwintermctl window new [--name <name>]`              — open a new window (seeded with 1 workspace + 1 session).
        - `agwintermctl window select|close|delete|rename <w> [name]` · `window resize <w> W H` · `window move <w> X Y` · `window zoom <w>`
          (`<w>` = a window id, a unique id prefix, or `active`; the last window can't be deleted).
        - `--window <id|prefix|active>` targets a specific window on ANY content verb, e.g.
          `agwintermctl session new --window <prefix> --name build` or `agwintermctl tree --window active`.
          Omit it to act on the frontmost window. Your own window is `AGWINTERM_WINDOW_ID`.
        - App-global verbs ignore `--window`: `config`, `theme`, `settings`, `keymap`, `install`.

        ## Custom commands (keymap.conf) & the launcher
        Define commands in keymap.conf, then run them by chord (Ctrl+Shift+O palette) or the control API:
        - `command <Label> = <text>`                    — default "send" mode: types the text into the active session
        - `command [new] <Label> = <text>`              — run in a fresh session's shell (stays interactive after)
        - `command [overlay] <Label> = <text>`          — run in an ephemeral overlay pane over the session
        - `command [detached] <Label> = <text>`         — launch an independent OS process (open a URL, external tool)
        - Run one: `agwintermctl command run "<Label or raw command>" [--mode new|overlay|detached|send]`
          (a raw command with no matching label defaults to `--mode new`).
        - List them: `agwintermctl command list`         — tab-separated `label  mode  chord  text`.
        - `{AGW_*}` tokens are expanded in the text AND passed as `$AGW_*` env vars to the launched process:
          `{AGW_SESSION}` (name) · `{AGW_SESSION_ID}` · `{AGW_WORKSPACE}` · `{AGW_CWD}` (active pane cwd) ·
          `{AGW_PANE_ID}` · `{AGW_APP}` (path to agwintermctl, for callbacks). Unknown `{AGW_*}` → empty.
        - Leader chords (tmux-style): `leader = ctrl+k`, then `map leader b = command:Build`. Press the leader,
          then the follow-up key. Drive/observe it with `agwintermctl command leader state|begin|cancel|key:<chord>`.

        ## Splits, font, sidebar, theme
        A session has at most TWO panes. Each pane has its own id (`tree --json` lists them as `paneIds`). THE SESSION-ID
        RULE, by condition and not by verb: a session id names the pane that carries it while one does — pane 0 of a fresh or
        reopened session; after a `swap`, either side (a restore keeps the order it saved) — and names the session's FOCUSED
        pane while none does. None does once
        the carrier was closed, however it was closed: `split close` on it, Ctrl+Shift+W on it, `split off` after a `swap`
        (that collapses onto the other pane), or its shell exiting. A later `split` mints a fresh id, so the state persists
        until the session is closed and reopened (a reopened session's pane 0 carries the id again). In a split session,
        address panes by their own ids. The split verbs take `--target <id>` as a
        session or either of its panes and act on THAT session, not the active one; `focus` and `resize` act on the active session.
        - `agwintermctl session split [on|off|toggle] [--axis vertical|horizontal] [--target <id>]` — REPLIES WITH A PANE ID, a bare
          string: `on` = the split pane's id — ALSO when the session was already split (nothing changes; a caller that does not
          know whether it split gets something addressable either way); `off` = the survivor's id (pane 0), also when already
          single; `toggle` = whichever it produced. Default op = toggle. The axis names the ARRANGEMENT, agterm's words: vertical = left/right panes (the default of a session never split), horizontal = top/bottom panes.
          Omitted = keep the session's orientation — remembered for the life of the session, through `off`, and across a
          restart only while the session is still split (a collapsed session writes no axis, so after a restart it is
          vertical again; pass `--axis` if it matters); given on an
          already-split session = re-orient it live. Anything but the two words is refused and nothing is split. `tree --json`
          reports `axis` beside `paneIds` / `focusedPane` / `splitRatios` whenever `paneCount` > 1.
        - `agwintermctl session split close [--target <id>]` — close ONE pane, EITHER side (`off` can only keep pane 0): a pane id
          closes that pane; a session name or no target closes the session's focused pane (what Ctrl+Shift+W does); from your
          own pane, your pane. Replies with the SURVIVOR's id, which becomes pane 0 with the whole area and the focus. A one-pane
          session is REFUSED — `session close` is the verb that closes a session. Closing the pane that carries the session
          id is one of the ways the session-id rule above flips to "the focused pane": keep addressing panes by the ids
          `tree --json` lists.
        - `agwintermctl session swap [--target <id>]` — exchange the two panes. What moves: the pane order (left↔right or
          top↔bottom), the focus (it follows its pane) and the two shells' contents. What does not: the axis, the divider (the
          left/top box keeps its size — the contents change places), overlays / scratch / quick, the status, the context, the
          flag — and EVERY ID. A swap moves panes, never ids: the id you hold keeps reaching the same shell, now on the other
          side, and the session id keeps naming the pane it always named. Replies `{session, paneIds, focusedPane, axis}` = the
          tree's split block after the swap. A one-pane session is refused.
        - `agwintermctl session focus [primary|split|left|right|top|bottom|other]` — move focus between the panes (default
          `other`, the one word valid on either axis; `primary` = pane 0, `split` = pane 1). `left`/`right` exist on a vertical
          split only and `top`/`bottom` on a horizontal one — the wrong pair is refused naming the axis; so is a one-pane session.
        - `agwintermctl session resize [--split-ratio R] [--grow-left N|--grow-right N|--grow-top N|--grow-bottom N]` — move the
          divider: `--split-ratio` is pane 0's share (0.7 = the left/top pane gets 70%); `--grow-left/--grow-right` move a
          vertical split's divider by N columns, `--grow-top/--grow-bottom` a horizontal one's by N rows. The other axis's flags
          are refused and the divider does not move; so is a one-pane session.
        - `agwintermctl font inc|dec|reset [--target <id>]`      — font zoom; target a session (active pane) or a specific split/scratch/quick pane by its id (see `tree` paneIds)
        - `agwintermctl dashboard [<id> ...] [--close] [--font-size N]` — grid overlay of live sessions (no ids = most-recent; `--close` dismisses; Ctrl+Shift+D toggles it in the UI)
        - `agwintermctl sidebar show|hide|toggle|expand|collapse` (`on`/`off` are aliases of show/hide). An op the sidebar
          cannot do is REFUSED (`ok:false`, nothing changed) — it used to answer `ok` and do nothing. `sidebar width [N]`
          reads/sets the width (see "Manage sessions & workspaces").
        - `agwintermctl session background set <image> [--opacity 0..100] [--mode fit|fill|center|tile]` — a faint per-session
          watermark drawn behind the terminal (the image is copied into app data); `session background clear` removes it.
          Per-session (honors `--target`/`--window`); persists; `tree` reports `"background":true`.
        - `agwintermctl theme list` · `agwintermctl theme set "Solarized Light"`
          Bundled curated themes — dark: Dracula, Tokyo Night, Catppuccin Mocha, Gruvbox Dark, Nord, One Dark, Solarized Dark;
          light: Solarized Light, Catppuccin Latte, GitHub Light. Drop more ghostty-format files in %LOCALAPPDATA%\agwinterm\themes\.

        ## Settings & config
        - `agwintermctl settings`                                — open the Settings window (grouped appearance/behavior controls)
        - `agwintermctl config list`                             — every config key with its current value
        - `agwintermctl config get <key>` · `config set <key> <value>` — read/change a setting (persists to agwinterm.conf, applies live)
          Keys: font-family, font-size, cursor-style, cursor-blink, cursor-blink-ms, theme, scrollback-lines,
          inactive-pane-dim (0-100, dims non-active split panes), window-opacity (30-100), sidebar-tint (-100..100),
          scroll-speed (1-10), new-session-dir, right-click-paste, copy-on-select, desktop-notifications, shell-integration,
          restore-commands, blocked-sound, omp-theme. Changing the theme retints the WHOLE window (sidebar/title/status), not just the terminal.

        ## Config / restore
        - `agwintermctl keymap reload`                           — re-parse keymap.conf (reports diagnostics)
        - `agwintermctl restore clear`                           — clear the saved session-tree state
        - `agwintermctl restore capture [--target ID]`            — capture the foreground command of EVERY real pane (or of the one
          pane/session named) into its restore slot NOW and save, so a crash, a `Stop-Process`, a power loss or a missed
          update-quit still finds it there; without this the capture happened only on a clean quit. Call it before a long
          run, or from a hook. It can take SECONDS (one CIM process query for all panes, up to 15 s) — do not put it on a
          hot path. Replies `{captured, replayOnRestore, panes:[{pane, session, captured}]}`: `captured` per pane is the
          command line the shell is running, or `null` = that shell had no non-denylisted child (the honest answer, and
          it is written into the slot — a fresh capture overwrites an older checkpoint, including to empty; `powershell`,
          `cmd` and the other shells on `restore-denylist.conf` never count as a command). `replayOnRestore` is the
          `restore-commands` toggle: the capture ALWAYS happens, but the slot is typed back at restart only when this is
          true — check it, or `config set restore-commands true`, or the checkpoint is kept and never replayed. Read the
          slots back in `tree --json` as `capturedCommands`, keyed by pane id like `restoreCommands`. An unknown target,
          or a scratch/overlay/quick pane (never restored, so no slot), is refused and nothing is captured or saved; a
          process query that fails or times out is a refusal too, never an empty answer for every pane. ONE refusal
          leaves something behind and says so: "captured into memory but the state file could not be written" — the
          slots are filled (`tree` shows them) but the checkpoint is not on disk and will not survive a restart; fix
          the state directory and capture again. `session context` / `session rename` replies describe the value in
          memory the same way — their save is best-effort and silent.
        - `agwintermctl install hooks|skill|shell`               — install agent-status hooks / this skill / shell-integration (live cwd)
        - `agwintermctl install cli [--remove]`                  — add (or remove) agwintermctl on the user PATH
        - `agwintermctl omp list` / `omp set <name> [--persist]` — list / apply oh-my-posh themes (live; --persist keeps it for new sessions).
          A name that resolves to no theme is REFUSED (`ok:false`, the theme in effect unchanged) — it used to answer `ok:true` with the text.

        ## Show an image inline
        - `agwintermctl image show C:\path\pic.png --row 2 --col 4`
        - `agwintermctl image sixel C:\path\pic.six [--row R --col C]` — render a sixel file (delivered
          out-of-band; ConPTY strips sixel through the shell, so use this to display one)
        - `agwintermctl image frameshm <Local\agwinterm-frame-NAME> [--slot N] [--seq N] ...` — display
          a high-rate raw BGRA/RGBA frame from a versioned shared-memory mapping. This is a specialized
          producer path; query `agwintermctl session metrics [<pane-id>] --json` before sizing it and
          follow https://github.com/yeroo/agwinterm/blob/main/docs/specs/image-frameshm.md for the
          versioned contract. Ordinary image files should use `image show`.
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
