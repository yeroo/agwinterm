# agterm → agwinterm gap analysis

> **⚠️ Superseded (2026-07-07):** the status columns below predate the big porting waves (scratch/quick/overlay, multi-window, search, selection/clipboard, MRU, flags are all **done** now). For the current analysis — agterm's July PRs + community show-and-tell recipes — see **[agterm-gap-analysis-2026-07.md](agterm-gap-analysis-2026-07.md)**.

Comparison of umputun's **agterm** (macOS/Swift/libghostty, latest `master`) against our **agwinterm** native Win32/Direct2D port. Grounded in agterm's README *and* source (`agterm/`, `agtermCore/`). Status legend: **Done** / **Partial** / **Missing**. Effort: S (hours) / M (a day-ish) / L (multi-day). "macOS-only" marks things not worth porting as-is.

_Generated from agterm source at clone time; re-run to refresh._

---

## Summary

| Area | Done | Partial | Missing |
|---|---|---|---|
| Core / VT / rendering | 1 | 1 | 0 |
| Structure & concepts | 3 | 1 | 4 |
| Sidebar & navigation | 4 | 2 | 2 |
| Palettes | 3 | 0 | 0 |
| Splits / panes | 4 | 1 | 0 |
| Scratch / Quick / Overlay | 0 | 0 | 3 |
| Multi-window | 0 | 0 | 1 |
| Flagging / focus | 0 | 0 | 3 |
| Notifications | 0 | 0 | 1 |
| Search | 0 | 0 | 1 |
| Selection / clipboard | 0 | 1 | 2 |
| Theming | 2 | 1 | 1 |
| Settings | 0 | 1 | 1 |
| Keymap & custom commands | 1 | 2 | 1 |
| Control API (agtermctl) | ~8 verbs | 3 | ~25 verbs |
| Agent status | 2 | 2 | 1 |
| Persistence | 6 | 0 | 2 |
| Shell / agent integration | 3 | 1 | 0 |

Roughly: the **single-window core** (sessions, workspaces, splits, palettes, rename, drag-reorder, theming, keymap basics, per-pane font, persistence, agent-status hooks) is ported. The **big missing surface** is the *auxiliary terminals* (scratch/quick/overlay), *multi-window*, *notifications*, *search*, *text selection/clipboard*, and the *bulk of the `agtermctl` verb set*.

---

## Core / VT / rendering

- **Terminal engine.** agterm embeds **libghostty** (full VT, GPU render, shell I/O) — `agterm/Ghostty/*`. — agwinterm: **Done (own impl)**: C# VT engine (`Agwinterm.Core`) + Direct2D + ConPTY. Note: ours is a *subset* VT (no full ghostty completeness); good enough for shells + Kitty graphics. macOS-only: libghostty itself.
- **Image protocols / graphics.** ghostty handles Kitty/sixel/iTerm2. — agwinterm: **Partial**: Kitty graphics via the control pipe (docxy), delivered out-of-band because ConPTY strips APC. No sixel/iTerm2. (M to add sixel.)

## Structure & concepts (`Session.swift`, `Workspace.swift`, README "Concepts")

- **Session** (named shell, cwd, scrollback, restored). — **Done**.
- **Workspace** (named group; sessions move between, keep shell). — **Done** (move via menu + drag).
- **Panes / split** (session splits into two side-by-side, shared row, divider remembered). — **Done** (we allow N horizontal panes — superset; ratio persisted).
- **Agent status** (per-row state). — **Done** (see Agent status).
- **Scratch terminal** (per-session extra shell toggled over the session, not restored). — **Missing** (title-bar icon is a toast stub). M.
- **Quick terminal** (one throwaway shell per window, drops over active session). — **Missing** (stub). M.
- **Overlay** (ephemeral terminal running one program over a session; control-API driven). — **Missing**. L.
- **Window** (multiple on-screen windows, each its own tree; window library; frames restored). — **Missing** (single window only). L.

## Sidebar & navigation (`WorkspaceSidebar.swift`, `RecencyStack.swift`)

- Two-level tree (workspaces → sessions), status dots, counts, chevrons. — **Done**.
- Drag-reorder sessions within/between workspaces; reorder workspaces. — **Done** (agterm also has `session move --to up/down/top/bottom` over the CLI; ours is mouse-only — see Control API).
- Inline rename; context menus. — **Done**.
- Session switcher (⌃P), action palette (⌃⇧P), custom palette (⌃⇧O). — **Done** (see Palettes).
- **Ctrl-Tab MRU switcher** (app-switcher-style most-recently-used walk, `RecencyStack.swift`; hold Ctrl, tap Tab, single tap flips back). — **Partial**: ours is a plain adjacent next/prev cycle, no MRU order and no hold-to-preview. M.
- **Flagged working-set view** + **focus-workspace** (collapse to one workspace) + **sidebar mode** (tree/flagged). — **Missing** (flag footer button is a toast stub; `SidebarMode`/`focusedWorkspaceID` persist in agterm). M.

## Palettes (`Views/Palette.swift`, `SessionSwitcher.swift`, `Fuzzy.swift`)

- Session switcher / action palette / custom-commands palette; fuzzy filter, ↑↓, Enter, Esc. — **Done** (our `PaletteKind` overlay). Note: agterm's action palette exposes the *full* 34-action set incl. scratch/quick/search/flag/window actions we don't have yet.

## Splits / panes (`Views/PaneShortcuts.swift`, `TerminalSurface.swift`)

- Toggle split, focus left/right pane, close pane, divider drag, per-pane cwd. — **Done**.
- Divider ratio set/nudge (`session resize --split-ratio` / `--grow-left/right`). — **Partial**: we support drag; no control-API resize verb and no keyboard nudge. S.
- **Inactive-pane dim** (`inactivePaneMuteStrength`, Settings). — **Missing** (we accent the active pane but don't mute the inactive). S.

## Scratch / Quick / Overlay

- All **Missing** (above). Overlays carry the most CLI surface: `session overlay open COMMAND [--size-percent N] [--wait] [--block] [--target]`, `overlay close`, `overlay result`; a `* (overlay)` tag in `tree`. On Windows an overlay = a transient full-region or floating child terminal (ConPTY) drawn over the pane. L for overlays; M each for scratch/quick.

## Multi-window (`WindowLibrary.swift`, `WindowGeometry.swift`)

- **Missing.** A window library with per-window trees, frames restored on launch, and `window new/list/select/close/rename/delete/resize/move/zoom` + a global `--window <id>` targeting option. L. (Windows-sensible: one top-level `HWND` per window; our whole shell is currently single-window/single-tree.)

## Flagging / focus — **Missing** (M). `session flag on|off|toggle|clear`, `sidebar mode flagged`, `workspace focus on|off|toggle`; `flagged` + `focusedWorkspaceID` + `sidebarMode` all persist.

## Notifications (`Notifications/*`, `Notifications.swift`)

- **Missing.** A program raises a desktop notification via **OSC 9 / OSC 777** (or `agtermctl notify`); shows as a banner + a **count badge on the session row**; click jumps to the raising pane. Windows-sensible via toast notifications (WinRT) + a row badge. M. (We already have the agent-status dot; badge/notify is separate.)

## Search (`Views/TerminalSearchBar.swift`)

- **Missing.** In-terminal find bar (⌘F / `toggle_search`), highlight matches, "N of M" counter, next/prev, `session search "err" [--next|--prev|--close]`. Needs a search over the scrollback buffer + match highlighting in the renderer. M–L.

## Selection / clipboard

- **Text selection** (drag / shift-click to select in the terminal). — **Missing**: we have mouse *reporting* for apps but no user text selection. M.
- **`session copy`** (returns selected text in the response; doesn't touch clipboard) + **`session type --select`** paste. — **Missing** (depends on selection). S once selection exists.
- **Right-click paste** (`rightClickPaste` setting). — **Missing**. S.
- **`session text`** (dump the whole terminal buffer as plain text over the CLI). — **Missing**. S.

## Theming (`SettingsModel`, theme picker)

- Configurable palette (ANSI-16 + default fg/bg + cursor); live-preview picker (`select_theme`); persisted. — **Done**.
- Ghostty-format theme files loaded from a themes dir. — **Done** (we read `%LOCALAPPDATA%\agwinterm\themes`).
- **512 bundled themes.** — **Partial**: we ship ~7 built-ins + can load ghostty theme files, but don't bundle the 512. S–M (bundle the ghostty theme set as files).
- **`theme list` / `theme set` over the CLI.** — **Missing**. S.

## Settings (`SettingsView.swift`, `AppSettings.swift`)

- A **GUI Settings window** (Cmd+,) with General/Appearance/KeyMapping tabs. — **Missing**: we use a plain `agwinterm.conf` file. macOS-style window not required, but a Windows settings dialog would help. M.
- Config coverage — **Partial**. agterm settings we DON'T have: `mouseScrollMultiplier`, `inactivePaneMuteStrength`, `sidebarBackgroundShift` (sidebar tint), `backgroundOpacity`/`backgroundBlur` (translucent window), `newSessionDirectory` (home | currentSession | custom) + `newSessionCustomDirectory`, `lines` (scrollback size), `rightClickPaste`, `notification*`, `compactToolbar`, `attentionButtonEnabled`, `blockedStatusSoundName`, `*StatusColorHex` (custom status colors), `inheritGlobalGhosttyConfig`. We have: font family/size, theme, cursor, shell-integration, restore-commands. Each individual setting is S.

## Keymap & custom commands (`Keymap.swift`, `BuiltinAction.swift`, `CustomCommand.swift`)

- `keymap.conf` with `map <chord> <action>` + `command "Name" [chord] <shell line>`; reload with diagnostics. — **Done** (structure).
- **Full built-in action set (34).** — **Partial**: we wire ~15. Missing action ids: `new_window`, `rename_window`, `delete_window`, `rename_workspace`, `open_directory`, `toggle_scratch`, `toggle_search`, `toggle_flag`, `toggle_flagged_view`, `focus_workspace`, `quick_terminal`, `select_theme`, `first_session`, `last_session` (most gated on missing features). S per action once the feature exists.
- **Custom-command model.** — **Partial/divergent**: agterm runs the command **detached via `/bin/sh -c`** with **`{AGT_X}` token expansion** (9 tokens: SESSION_ID/NAME/PWD, WORKSPACE_ID/NAME, WINDOW_ID/NAME, SELECTION, SOCKET) and exports the same as `$AGT_X` env; supports **leader sequences** (`ctrl+a>g`). Ours **types the text into the active pane** — no detached process, no token expansion, no leader chords. M to match (run detached `cmd /c` / `pwsh -c`, expand `%AGW_X%`/env, add leader parsing).
- Note (macOS-only): `map` uses `cmd`; on Windows map to Ctrl/Alt. `+`, arrows, `>` aren't expressible (same v1 limitation).

## Control API — `agtermctl` (`agtermctlKit/Commands.swift`, `ControlProtocol.swift` = 48 request kinds)

We have **~8**: ping, tree, `workspace new`, `session new/select/close/status/type/write`, `font inc/dec/reset`, `image show/frame/clear`, `install hooks/skill/shell`. **Partial**: `--json`, `--socket` (named pipe). Ours uses a **named pipe**; agterm uses a **unix-domain socket** — protocol shape is the same (newline JSON), so verbs port cleanly.

**Missing verbs** (high-value scripting surface):
- `session new --command <argv>` (exec-replace the shell; closes on exit), `--stdin`, `--select` (realize surface), `--name`, `--workspace-name`, `--create-workspace`. — M.
- `session go next|prev|first|last|next-attention|prev-attention`. — S.
- `session move --to up|down|top|bottom` (reorder) and `session move <ws>` (relocate). — S.
- `workspace rename|delete|select|move|focus`. — S.
- `session split on|off|toggle`, `session focus left|right|other`, `session resize --split-ratio/--grow-*`. — S.
- `session scratch`, `quick`, `session overlay open/close/result`. — gated on those features.
- `session flag`, `sidebar visibility/mode`, `sidebar expand/collapse`. — gated on flag/focus.
- `session search`, `session copy`, `session text`. — gated on search/selection.
- `session background image|text|color|clear` (watermark — see below). — M.
- `notify`. — gated on notifications.
- `window *` + global `--window`. — gated on multi-window.
- `keymap reload`, `config reload`, `theme set/list`, `restore clear`. — S each (`keymap reload` we have as an action, not a CLI verb).
- **agent skill** teaches the *full* agtermctl set — our bundled skill should be regenerated to match whatever we implement.

## Agent status (`AgentStatus.swift`, `Control/StatusSoundPlayer.swift`, `AgentHooksInstall.swift`)

- States (idle/active/blocked/completed), sidebar glyph, title-bar bell, ⌃⇧I attention list, `tree --json` reports `status`, typing clears blocked/completed. — **Done**.
- Claude Code hooks + skill + generic shell integration installer. — **Done** (Claude hooks). **Partial**: agterm also prints a **Codex** `notify` TOML line and ships a **generic bash/zsh/fish `integration.sh`** driven by `AGTERM_AGENT_RE` (matches codex/gemini/cursor-agent/aider/opencode/crush/goose). We install Claude hooks + a cwd shell snippet; we don't emit the Codex/agent-regex generic integration. S–M.
- **`--blink` / `--sound` / `--auto-reset`** on `session status`, plus a **blocked-sound** setting. — **Partial/Missing**: we set status but not blink/sound/auto-reset. `--sound` → Windows system sounds. S.
- **Notification badge** count on the row. — **Missing** (tied to Notifications).

## Persistence (`Snapshot.swift`, `PersistenceStore.swift`, `CommandRestore.swift`)

- tree / names / selection / sidebar width+visible / split layout + ratio / per-pane cwd / per-session font / window geometry. — **Done**.
- Opt-in restore-running-commands + `restore-denylist.conf`. — **Done** (agterm seeds the denylist with tmux/screen/zellij; ours seeds shells/prompt-helpers — fine). Note agterm also persists `initialCommand` (a `--command` session) and `splitForegroundCommand`; we have no `--command` sessions yet.
- **`flagged` / `sidebarMode` / `focusedWorkspaceID`** persistence. — **Missing** (gated on flagging/focus). S once those exist.
- **`backgroundWatermark`** per session. — **Missing** (see below). M.

## Shell / agent integration

- Env vars injected into sessions: agterm `AGTERM_ENABLED/WINDOW_ID/WORKSPACE_ID/SESSION_ID/SOCKET`; ours `AGWINTERM/_ENABLED/_WINDOW_ID/_WORKSPACE_ID/_SESSION_ID/_PIPE`. — **Done** (equivalent).
- Live cwd via OSC 7 shell integration. — **Done** (opt-in `$PROFILE` installer). agterm auto-injects for zsh/bash/fish/nu via ghostty; we install a pwsh hook.
- Install CLI-tool-on-PATH. — **Partial**: agterm symlinks `agtermctl` into `/usr/local/bin` (Help ▸ Install Command Line Tool). We don't add `agwintermctl` to PATH. S (drop into a PATH dir / add a shim).

---

## Things in the code the README undersells / surprised me

- **Session background watermark** (`background image|text|color`, `BackgroundWatermark`, `WatermarkRenderer.swift`): a per-session background image OR rasterized text OR solid color behind the terminal, auto-fit, and **persisted** (a `.text` watermark re-renders its PNG on restore). A distinctive, fully-wired feature barely mentioned in the README.
- **`session text`**: dump the whole terminal buffer as plain text over the CLI — trivial-looking but very useful for agents/scripts; cheap to add on our engine.
- **`session copy` + `--select` paste**: cross-session selection transfer purely over the control channel (no system clipboard) — clever and scriptable.
- **`window zoom/resize/move`** and a persistent **window library** (windows are first-class, named, reopenable) — deeper than "multi-window", it's a saved set of windows.
- **`RecencyStack`** MRU switcher is a real ordered stack, not just prev/next — the "single tap flips back" behavior needs it.
- **Custom-command `{AGT_X}` tokens + `$AGT_X` env + leader chords + detached `/bin/sh -c`**: the custom-command system is a mini launcher (open GUI apps, run overlays), not just "type text" — our version is much simpler and diverges here.
- **`restore clear`** CLI + `initialCommand`/`splitForegroundCommand` persistence: the restore-commands feature is more complete than ours (handles `--command` sessions and the split pane).

## Recommended next (highest value, Windows-sensible), by effort

1. **`agtermctl` verb parity for what we already do** — `session go`, `session move`/`workspace move` (reorder+relocate), `workspace rename/delete/select`, `session split/focus/resize`, `theme set/list`, `keymap reload`, `restore clear`, `session new --command/--name/--workspace-name/--create-workspace`. Cheap, high leverage for agent scripting. **S–M**.
2. **`session text`** (buffer dump) — trivial on our engine, big scripting win. **S**.
3. **Text selection + `session copy` + right-click paste** — core terminal UX gap. **M**.
4. **In-terminal search** (⌃F, highlight, N-of-M, `session search`). **M–L**.
5. **Scratch + Quick terminals** (the two easy auxiliary shells; overlays can follow). **M**.
6. **Notifications** (OSC 9/777 → Windows toast + row badge, `notify`). **M**.
7. **Agent-status `--sound`/`--blink`/`--auto-reset` + Codex/generic-agent integration** (round out the agent story we already lead with). **S**.
8. **Flagging + focus-workspace + flagged sidebar mode** (make the flag stub real; persists cleanly). **M**.

Bigger, later: **multi-window** (L), **overlays** (L), **GUI Settings window** (M), **512 bundled themes** (S–M), **session background watermark** (M), richer custom-commands (`{AGW_X}` tokens + detached run + leader chords) (M), inactive-pane mute + window opacity/blur + sidebar tint (S each).

## Not worth porting as-is (macOS-specific)

- The AppKit **menu bar** and **⌘ shortcuts** (we use Ctrl/Alt; keep the palette + keymap as the surface).
- **SF Symbols**, **Liquid Glass** sidebar, macOS **system sounds** by name (use Segoe Fluent glyphs / Windows sounds).
- **libghostty** + ghostty global-config inheritance (`~/.config/ghostty/config`) — we have our own engine + `agwinterm.conf`; a ghostty-config compatibility layer isn't warranted.
- **unix-domain socket** specifics — we already use a named pipe with the same JSON protocol.
