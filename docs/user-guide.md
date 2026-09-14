# User guide

## Agent-first

- **Workspaces → sessions → panes** in a custom-drawn sidebar: drag-reorder, rename, flag, focus,
  unread badges, **multi-select with Ctrl/Shift+click** for batch flag / move / close, and reopen
  closed sessions *and workspaces* with `Ctrl+Shift+R`. See [workspace navigation](navigation.md).
- **Dashboard** (`Ctrl+Shift+D`): a grid of **live** session previews — arrow-navigate, Enter or
  double-click to jump in; or drive it with `agwintermctl dashboard <ids>`.
- **Agent status** per session (idle / active / blocked / completed) as a colored dot and a title-bar
  bell, driven by your agent via hooks or the control API, with blink, auto-reset, and sounds. Run
  `agwintermctl install hooks` (or the palette entry) once to wire Claude Code / Codex up.
- **Claude Code session binding & auto-resume**: the same installer adds a transparent `claude`
  wrapper (active only inside agwinterm) that ties Claude's session id to the agwinterm pane. You just
  type `claude` — a fresh pane starts a bound session, and a pane that already has a transcript
  **resumes** it. On restart, agwinterm re-launches each bound pane and the conversation comes back.
  Already had Claude running before installing this? Run `agwintermctl claude adopt` (or palette →
  *Make Claude Sessions Resumable*) once to bind your existing conversations to their panes.
- **Update Claude Code** (palette → *Update Claude Code*, or `agwintermctl claude update`): agwinterm
  notices when a new Claude Code ships (npm registry; `claude-update-check = false` to opt out), then,
  on your command, runs `claude update` in an overlay terminal and **restarts every running Claude
  session**, each resuming its own conversation (YOLO panes stay YOLO).
- **agwinterm self-update** (palette → *Update agwinterm*, or `agwintermctl app update`): notices new
  releases on GitHub (`update-check = false` to opt out), then, on your command, downloads the right
  artifact for your install (installer or portable exe), **verifies its SHA-256** against the release
  digest, restarts, and your sessions restore. Scoop and Chocolatey installs are never touched; the
  hint points at your package manager instead.
- **Shells that survive the UI** (EXPERIMENTAL): flip *Settings → General → Session host* to
  **Pty-host server** (`session-host = server`) and your sessions live in a tiny headless process.
  Quit, self-update, or even crash the UI and every shell (including a running Claude conversation)
  **keeps running**; the next start reattaches each pane to its live session. Closing a pane still
  closes its shell.
- **Splits** — side by side or stacked (`--axis vertical|horizontal`, agterm's words), either pane
  closable, the two swappable with every id kept; a split collapses to the survivor when a pane exits.
- **Scratch** and **quick** terminals ([quick terminal](quick-terminal.md)), ephemeral **overlays**
  over a session or over **one pane** of it (`--pane left|right`), a passive [session HUD](session-hud.md),
  a [native picker](native-picker.md), and **multi-window** with per-window addressing.

## A real Windows terminal

- **Default Terminal Application**: register agwinterm from *Settings → General* (per-user, no admin,
  one-click revert) and every console app you launch — `cmd` from Win+R, double-clicked `.exe`s —
  opens as an agwinterm session via the ConPTY handoff, titled by the app.
  See [default-terminal testing](defterm-testing.md).
- **Fast**: sustained output at **~33k lines/s** (~87 % of a bare conhost window), 0.4 s cold start,
  ~0 % idle CPU, and a leak-hunted session lifecycle (`tools/profile-memory.ps1` keeps it honest).
- Sixel and Kitty graphics, win32-input-mode and the Kitty keyboard protocol, SGR and SGR-Pixels mouse
  reporting (`?1006`/`?1016`, including DECRQM discovery and device-pixel coordinates), ligatures
  (toggleable), builtin box-drawing glyphs, buffer restore, block selection and keyboard mark mode,
  read-only panes, elevated and de-elevated sessions side by side (⚡ marker), FTCS/OSC-133 prompt
  marks with jump-to-prompt, taskbar progress (OSC 9;4).
- Shells are launched with `TERM_PROGRAM=agwinterm` (plus the usual `AGWINTERM_*` variables), so prompt
  engines, tmux and scripts can detect the host terminal.

## Accessible — screen readers are first-class

- The terminal is a **UIA text document**: Narrator/NVDA read it line by line, track the caret (tight
  one-cell focus box), and **new output is announced automatically** after it settles.
- **Everything is in the UIA tree**: sessions, every chrome button, settings tabs and controls —
  scannable (Caps Lock + arrows), focusable, and invokable. Dialogs are **modally scoped** so the
  reader cannot wander behind them.
- **F6** moves keyboard focus between the terminal and the session list (arrows + Enter there). The
  **Settings dialog is fully keyboard-navigable** — Tab reaches the tab headers too (Enter switches),
  with a classic keyboard-only dotted focus rectangle; buttons **speak on hover** and show
  **tooltips**.
- **F1 help** lists the *effective* keybindings and, when a reader is attached, speaks an orientation
  guide for low-vision users.

## Looks & feel

- **Whole-window theming** with **~580 bundled themes** (the ghostty / iTerm2 set): sidebar, title
  bar, and terminal retint together. **Fonts apply live** from Settings, and an optional **follow
  Windows light/dark** mode swaps between a light and a dark theme you choose.
- **Configurable sidebar font size**, sidebar tint, window opacity, and inactive-pane muting.
- **cwd in the title** out of the box (composes with oh-my-posh) and an **oh-my-posh theme picker**.
- Toolbar modes (normal / compact / **hidden** full-bleed), window opacity, unfocused dim, per-session
  background watermarks.
- **MRU `Ctrl+Tab` switcher**, fuzzy **command / session / action palettes**, search, tmux-style
  **leader chords**, custom commands with `{AGW_*}` tokens and run modes.

## Keyboard essentials

| Key | Action |
|---|---|
| `F1` | Help (effective keybindings + accessibility guide) |
| `F6` | Move focus terminal ⇄ session list |
| `Ctrl+Shift+D` | Dashboard — grid of live sessions |
| `Ctrl+Shift+T` / `Ctrl+Shift+R` | New session / reopen closed |
| `Ctrl+Tab` | MRU session switcher |
| `Ctrl+D` | Split pane · `` Ctrl+` `` quick terminal · `Ctrl+J` scratch |
| `Ctrl+Shift+P` | Action palette |
| `F11` | Fullscreen |

Everything is rebindable in `keymap.conf` (see `F1` for the live list).

## Configuration

- **`%LOCALAPPDATA%\agwinterm\agwinterm.conf`** — appearance and behavior (also editable in Settings).
- **`%LOCALAPPDATA%\agwinterm\keymap.conf`** — keybindings, custom commands and leader chords.
- Themes: the bundled set ships with the app; drop extra ghostty-format `*.conf` files in
  `%LOCALAPPDATA%\agwinterm\themes\`.
