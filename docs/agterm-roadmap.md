# agwinterm roadmap — closing the agterm gap

Execution plan to close every gap in [agterm-gap-analysis.md](agterm-gap-analysis.md).
Ordered by value ÷ effort and by dependency. Effort: S (hours) / M (a day-ish) / L (multi-day).

## How each phase runs
- One focused change per phase, implemented in an isolated fork, **verified** (deterministic `agwintermctl` + `PostMessage` input + `PrintWindow` screenshots — SendKeys is unreliable here), then committed. Core + Pty tests stay green.
- The bundled **agent skill is regenerated** whenever the `agwintermctl` surface grows, so agents always see the current verb set.
- Every user-facing state added is wired into **persistence** at the same time (the tree/state.json already covers tree/splits/cwd/font/geometry/restore-commands).

## Prerequisite gap discovered
**Scrollback** is missing — the emulator keeps only the visible grid; scrolled-off lines are discarded and the mouse wheel only does app mouse-reporting. It's core UX *and* a hard prerequisite for search. It leads Wave A.

---

## Wave A — Foundations (scripting + core terminal UX)
Highest leverage: unblocks agent scripting and fixes core holes.

- **A1 · `agtermctl` verb parity + `session text` + skill regen** — S–M.
  Add the control verbs for things we already do internally: `session go next|prev|first|last|next-attention|prev-attention`, `session move --to up|down|top|bottom` + `session move <ws>` (relocate), `workspace rename|delete|select|move|focus`, `session split on|off|toggle`, `session focus left|right|other`, `session resize --split-ratio|--grow-left|--grow-right`, `theme set|list`, `keymap reload`, `restore clear`, `session new --command|--name|--workspace-name|--create-workspace`, `sidebar visibility|expand|collapse`. Plus **`session text`** (dump the buffer as plain text — trivial on our engine, big agent win). Regenerate the skill. *No UI; pure protocol + CLI.*

- **A2 · Scrollback** — M. Core: keep a bounded ring buffer of scrolled-off rows (config `scrollback-lines`, default e.g. 5000); a render scroll-offset; mouse-wheel + Shift+PgUp/PgDn to scroll history; auto-snap-to-bottom on output/input; alt-screen apps bypass it. Prereq for A3 selection-over-history and B1 search.

- **A3 · Text selection + clipboard** — M. Mouse drag / Shift-click / double-click (word) / triple-click (line) selection over the grid **and** scrollback; highlight rendering; copy (Ctrl+C when a selection exists / right-click) and paste (Ctrl+V / right-click, with `right-click-paste` config); `session copy` (return selection over ctl, no system clipboard) + `session type --select`.

## Wave B — Terminal power features
- **B1 · In-terminal search** — M–L (needs A2). Search bar (Ctrl+F), search the visible grid + scrollback, highlight matches, "N of M" counter, next/prev, Esc to close; `session search "…" [--next|--prev|--close]`.
- **B2 · Scratch + Quick terminals** — M. Shared "extra ConPTY drawn over the content region, toggled" infra. Scratch = per-session aside (opens in the session cwd, toggled, not restored); Quick = one throwaway per window dropped over the active session. `session scratch on|off|toggle`, `quick toggle`. Turns two title-bar/footer stubs real.
- **B3 · Overlays** — L (builds on B2 infra). `session overlay open COMMAND [--size-percent N] [--wait] [--block]`, `overlay close`, `overlay result`; full-region translucent or floating framed panel; `* (overlay)` tag in `tree`. Unlocks the lazygit-style custom commands.

## Wave C — Agent & notifications
- **C1 · Notifications** — M. Parse OSC 9 / OSC 777 → Windows toast (WinRT) + a per-row count badge; click a banner jumps to the raising pane; `notify` verb; notification on/off config.
- **C2 · Agent-status polish** — S. `session status --sound|--blink|--auto-reset`, a blocked-sound setting, a Codex `notify` config line, and a generic bash/zsh/fish/**pwsh** agent-integration snippet driven by an `AGWINTERM_AGENT_RE` (codex/gemini/cursor-agent/aider/opencode/crush/goose). Regenerate the skill.

## Wave D — Sidebar & organization
- **D1 · Flagging + focus-workspace + flagged sidebar mode** — M. Durable per-session flag (survives moves, persists); flat flagged working-set view; collapse-to-one-workspace focus; `session flag on|off|toggle|clear`, `sidebar mode tree|flagged`, `workspace focus on|off|toggle`. Makes the flag stub real; persist `flagged`/`sidebarMode`/`focusedWorkspaceID`.
- **D2 · MRU Ctrl-Tab switcher** — S–M. A recency stack behind Ctrl-Tab (hold-to-walk, single-tap flips back), app-switcher style, replacing the current plain adjacent cycle.

## Wave E — Customization & settings
- **E1 · Custom-command launcher upgrade** — M. Make keymap `command` a real launcher: run **detached** (`cmd /c` / `pwsh -NoProfile -c`), expand `{AGW_X}` tokens + export `$AGW_X` env (SESSION/WORKSPACE/WINDOW id+name, PWD, SELECTION, PIPE), and support **leader chords** (`ctrl+a>g`). Wire the remaining built-in action ids as their features land.
- **E2 · Settings window + config coverage** — M. A native Windows settings dialog (or richer config) plus implementing the settings with visible effect: inactive-pane dim, window background opacity/blur, sidebar tint, new-session directory (home | current | fixed), scroll speed, `scrollback-lines`, right-click paste, custom status colors, blocked sound, notification toggles.
- **E3 · Theme library** — S–M. Bundle the ghostty theme set (≈512) as theme files so the picker has the full catalog; `theme list`/`theme set` (from A1).

## Wave F — Big architecture + niche
- **F1 · Multi-window** — L (largest single item). A window library of named, reopenable windows, each its own top-level `HWND` + workspace/session tree; frames restored on launch; `window new|list|select|rename|close|delete|zoom|resize|move` + a global `--window <id>` targeting option; control routing per window. Touches nearly everything, so it goes last. **In scope (full parity)** — decided over relying on multiple app instances.
- **F2 · Session background watermark** — M, niche. Per-session background image / rasterized text / solid color behind the terminal, auto-fit, persisted (text watermark re-rendered on restore); `session background image|text|color|clear`.

---

## Rough sequencing rationale
1. **A first** — the ctl verbs + `session text` are cheap and make agwinterm scriptable/agent-drivable now; scrollback + selection fix the two most-felt everyday gaps.
2. **B/C** — the visible power features (search, scratch/quick, overlays) and the notification/agent story we already lead with.
3. **D/E** — organization + customization polish.
4. **F last** — multi-window is the one true architectural rewrite; overlay/scratch infra and window infra shouldn't block the cheaper wins.

## Not in scope (macOS-only — see gap analysis)
AppKit menu bar & ⌘ shortcuts, SF Symbols / Liquid Glass, named macOS system sounds, libghostty + global ghostty-config inheritance, unix-socket specifics (we use a named pipe, same JSON).
