<div align="center">

<img src="docs/agwinterm-icon.png" width="96" alt="agwinterm icon" />

# agwinterm

**A native Windows terminal built for AI coding agents.**

Workspaces, sessions, splits, live agent-status, and a scriptable control API — in a fast,
custom-drawn Win32 + Direct2D shell. A Windows homage to [umputun's **agterm**](https://github.com/umputun/agterm).

[![CI](https://github.com/yeroo/agwinterm/actions/workflows/ci.yml/badge.svg)](https://github.com/yeroo/agwinterm/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/yeroo/agwinterm?sort=semver)](https://github.com/yeroo/agwinterm/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

<img src="docs/img/screenshot.png" width="820" alt="agwinterm screenshot" />

</div>

---

## 💜 Kudos to umputun and agterm

agwinterm exists because of **[umputun](https://github.com/umputun)** and his terminal
**[agterm](https://github.com/umputun/agterm)**. agterm's design — a terminal that treats AI coding
agents as first-class citizens, with per-session status, a sidebar of workspaces, a quick terminal,
and a language-agnostic control socket — is the blueprint this project follows on Windows.

This is an **independent, from-scratch implementation** written in C# on a native Win32/Direct2D
stack (agterm is Swift on libghostty); no agterm code is used. It is a **tribute and a port of the
ideas/UX**, built so Windows users can have the same agent-first workflow. If you're on macOS, go use
the real thing: **[github.com/umputun/agterm](https://github.com/umputun/agterm)**. Thank you, umputun. 🙏

---

## Features

- **Workspaces → sessions → panes** in a custom-drawn sidebar (drag-reorder, rename, flag, focus).
- **Agent status** per session (idle / active / blocked / completed) as a colored dot + title-bar
  bell — driven by your agent via hooks or the control API.
- **Splits** (toggle two panes), **scratch** & **quick** terminals, and ephemeral **overlays**.
- **Multi-window** — independent windows, each with its own tree; address any of them from the CLI.
- **MRU `Ctrl+Tab` switcher**, fuzzy **command / session / action palettes**, **search**, scrollback,
  full **selection & clipboard** (word/line/select-all, copy-on-select, drag-autoscroll).
- **Whole-window theming** with **~580 bundled themes** (the ghostty / iTerm2 color-scheme set) —
  the sidebar, title bar, and terminal all retint together, light or dark.
- **cwd in the title, out of the box** (composes with oh-my-posh), plus an **oh-my-posh theme picker**.
- **A themed, tabbed Settings dialog** (General · Appearance · Notifications · Agent Status · Key Mapping)
  drawn in-app — no stock Windows chrome.
- **Custom commands** in `keymap.conf` — `{AGW_*}` tokens, `$AGW_*` env, run modes
  (send / new session / overlay / detached), and tmux-style **leader chords**.
- A **scriptable control API**: `agwintermctl` (or newline-JSON over a named pipe) — any language can
  drive it. Plus opt-in installers for the **agent skill** and **Claude Code / Codex status hooks**.

## Install

Grab the latest **`agwinterm-setup-<version>.exe`** from the
[**Releases**](https://github.com/yeroo/agwinterm/releases) page and run it.

- **Per-user, no admin required**, and **self-contained** — no .NET runtime needed on the target.
- It's currently **unsigned**, so SmartScreen will warn on first run → *More info → Run anyway*.
- The installer is deliberately minimal (copies files + shortcuts). The integrations
  (put `agwintermctl` on PATH, agent status hooks, agent skill, shell integration) are **opt-in from
  inside the app** — open the command palette (`Ctrl+Shift+P`) and pick the matching *Install…* entry.

## Build from source

Requires the **.NET 10 SDK** (see [`global.json`](global.json)) on Windows x64.

```powershell
# build + test
dotnet build Agwinterm.slnx -c Release
dotnet test  Agwinterm.slnx -c Release

# run the app
dotnet run --project src/Agwinterm.Win32 -c Release

# build the installer (needs Inno Setup 6)
./installer/build.ps1
```

## Control it from anything (`agwintermctl`)

agwinterm is scriptable through a local named pipe speaking newline-delimited JSON, with
`agwintermctl` as the CLI wrapper. A few examples:

```powershell
agwintermctl tree --json                     # the workspace/session tree
agwintermctl session status blocked --sound  # report agent status (a dot + bell in the UI)
agwintermctl session new --name build --workspace-name CI --create-workspace
agwintermctl session type "npm test`n"       # type into the active session
agwintermctl theme set "Tokyo Night"         # retint the whole window
agwintermctl window new --name scratchpad    # open a second window
agwintermctl omp set pure                     # switch the oh-my-posh theme live
```

Inside a session you get `AGWINTERM_SESSION_ID`, `AGWINTERM_WINDOW_ID`, and `AGWINTERM_PIPE`.
Run `agwintermctl install skill` (or the palette entry) to teach Claude Code / Codex the full verb set.

## Configuration

- **`%LOCALAPPDATA%\agwinterm\agwinterm.conf`** — appearance & behavior (also editable in Settings).
- **`%LOCALAPPDATA%\agwinterm\keymap.conf`** — keybindings + custom commands + leader chords.
- Themes: the bundled set ships with the app; drop extra ghostty-format `*.conf` files in
  `%LOCALAPPDATA%\agwinterm\themes\`.

## Acknowledgements

- **[umputun / agterm](https://github.com/umputun/agterm)** — the original and the inspiration for every
  bit of this project's UX. 💜
- **[Ghostty](https://ghostty.org)** & **[iTerm2-Color-Schemes](https://github.com/mbadolato/iTerm2-Color-Schemes)**
  — the bundled color themes are the community ghostty/iTerm2 set.
- **[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)** (Direct2D/DirectWrite) and
  **[Porta.Pty](https://www.nuget.org/packages/Porta.Pty)** (ConPTY).

## License

[MIT](LICENSE) © 2026 Boris Kudriashov. Bundled theme files retain their upstream (iTerm2-Color-Schemes,
MIT) licensing.
