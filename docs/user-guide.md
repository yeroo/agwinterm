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
- **Claude Code and Codex session binding & auto-resume**: the same installer adds a `SessionStart`
  hook to both agents. Each time a session starts, resumes, is cleared or compacts, the hook tells
  agwinterm the live session id and directory, and agwinterm stores the line that resumes it in the
  pane's own shell: `cd '<dir>' && claude --resume <id>` in Git Bash, `Set-Location -LiteralPath
  '<dir>'; codex resume <id>` in PowerShell, `cd /d` in cmd. The permission or sandbox mode it was
  started with (`--dangerously-skip-permissions`, `--permission-mode`, Codex's `--sandbox`,
  `--ask-for-approval`, `--profile`) is kept. On restart, even after a reboot, agwinterm types that line
  into each bound pane and the conversation comes back. A `claude -p` or `codex exec` an agent runs
  from its own tool shell does not rebind the pane. Codex runs a new hook only after you trust it
  once in Codex's `/hooks`, and Codex fires it on a session's first turn. For PowerShell the installer
  also adds a transparent `claude` wrapper (active only inside agwinterm) that starts a fresh pane's
  session under the pane id. Already had Claude running before installing this? Run
  `agwintermctl claude adopt` (or palette → *Make Claude Sessions Resumable*) once to bind your
  existing conversations to their panes.
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
  **keeps running**; the next start reattaches each pane to its live session, same process, same
  state. Closing a pane still closes its shell.
- **Splits** — side by side or stacked (`--axis vertical|horizontal`, agterm's words), either pane
  closable, the two swappable with every id kept; a split collapses to the survivor when a pane exits.
- **Scratch** and **quick** terminals ([quick terminal](quick-terminal.md)), ephemeral **overlays**
  over a session or over **one pane** of it (`--pane left|right`; open/close/result/copy/text via the
  [control API](control-api.md)), a passive [session HUD](session-hud.md),
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
- **Git Bash working directory**: PowerShell panes report their directory out of the box. A Git Bash
  (or MSYS2) pane does it with a few lines in `~/.bashrc`, so the title shows the directory and the pane
  restores in it after a restart (agwinterm 0.20.11 or later, earlier builds also raised a notification):

  ```bash
  # agwinterm: report the working directory at every prompt (OSC 9;9, the form ConPTY passes through)
  if [ "$AGWINTERM" = 1 ]; then
    __agw_cwd() { printf '\e]9;9;%s\a' "$(pwd -W)"; }
    PROMPT_COMMAND="__agw_cwd${PROMPT_COMMAND:+;$PROMPT_COMMAND}"
  fi
  ```

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

## Menu bar

The title bar carries agterm's menus — **File**, **View**, **Navigate**, **Help** — as a row of
labels after the sidebar toggle. Every row shows its *effective* shortcut (a rebind in `keymap.conf`
shows the rebind), a row that cannot apply right now is dim, and a state row's label follows the
state (Hide Sidebar / Show Sidebar, Flag / Unflag Session). Two rows open a flyout: **File ▸ Open
Window** (the window library, a check mark on the open ones) and **File ▸ Open Recent** (closed
sessions and workspaces).

It takes the Windows keyboard model: a lone **Alt** tap or **F10** focuses the bar (←/→ move, ↓ or
Enter opens, Esc leaves), **Alt+F / Alt+V / Alt+N / Alt+H** open a menu directly, and inside an open
menu ←/→ switch menus. A `keymap.conf` binding on an Alt+letter chord wins over the mnemonic, so a
shell that wants Alt+F keeps it by binding it. The bar is a UIA menu bar, so a screen reader reads
and runs it. `show-menu-bar = false` in `agwinterm.conf` (or `agwintermctl config set show-menu-bar
false`) removes it; the hidden toolbar mode shows no chrome and so no bar.

agterm's items that have no agwinterm counterpart are left out rather than invented: Edit / Reload
Hooks, Toggle Terminal Zoom, Reset Live Sessions, and the focus-set items (Add Workspace to Focus,
Toggle Workspace Filter, Clear Focus — agwinterm's workspace focus is one workspace at a time).

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
| `Alt` (tap) / `F10` | Menu bar · `Alt+F` `Alt+V` `Alt+N` `Alt+H` open a menu |
| `F11` | Fullscreen |

Most built-in actions can be rebound in `keymap.conf` (see `F1` for the live, effective list). Some
gestures are fixed in the app and ignore `unmap`: **F1** (help), **F6** (sidebar focus), **F10** (menu
bar when nothing else bound it), **Ctrl+Shift+D** (dashboard), **Ctrl+`** (quick terminal), and
**Ctrl+= / Ctrl+- / Ctrl+0** (font zoom).

## Configuration

- **`%LOCALAPPDATA%\agwinterm\agwinterm.conf`** — appearance and behavior (also editable in Settings).
- **`%LOCALAPPDATA%\agwinterm\keymap.conf`** — keybindings, custom commands and leader chords.
  On first launch a commented starter is written from the built-in template in
  `Keymap.StarterText` (`src/Agwinterm.Core/Keymap.cs`). **`map`** takes one chord, or several
  chords on one line separated by `|` (any count). **`unmap`** drops a default or custom **keymap**
  binding so the chord can fall through to the shell; it does not override the fixed gestures above.
  **Ctrl+Tab** / **Ctrl+Shift+Tab** run the MRU session walk only while the chord is explicitly bound
  to `next_session` or `previous_session`; after `unmap ctrl+tab` MRU does not run and the chord
  follows the normal keymap / terminal path. Use `unmap ctrl+d` for one chord, or `unmap a | b` for
  several. Reload with **File → Reload Keymap** or `agwintermctl keymap reload`.
- `show-menu-bar = false` hides the title-bar menu bar (File ▸ Edit agwinterm.conf… opens the file).
- Themes: the bundled set ships with the app; drop extra ghostty-format `*.conf` files in
  `%LOCALAPPDATA%\agwinterm\themes\`.
