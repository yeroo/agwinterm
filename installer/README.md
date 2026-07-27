# agwinterm installers

Builds **per-user** Windows installers — end users need **no** .NET runtime installed:

- **`agwinterm-setup-<ver>.exe`** — the full `agwinterm` (.NET) terminal + `agwintermctl`, from a
  self-contained publish. Its shortcuts pass `--default-session-host server-rust` so a fresh
  install defaults new sessions to the Rust pty-host (a first-run seed only; it never overrides
  an existing config, and the Settings UI stays authoritative after).
- **`agwinterm-lite-setup-<ver>.exe`** — the lightweight `agwinterm-lite` (C++/GDI) client for
  old / low-RAM machines, standalone. Always rides the Rust pty-host.

Each setup ships its own copy of the shared `agwinterm-ptyhost.exe` + `agwinterm_core.dll`, so
either installs (and uninstalls) cleanly without the other.

## Build

```powershell
installer\build.ps1        # both setups (what CI/releases run)
installer\build-lite.ps1   # lite setup only — skips the .NET publish, handy when iterating on lite
```

Prereqs:
- .NET SDK with the `net10.0-windows` target (the repo pins .NET 10).
- Rust + MSVC C++ build tools (for the Rust core/host and the lite client).
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) — `ISCC.exe` on PATH, the default install location,
  or the per-user winget location (`winget install JRSoftware.InnoSetup`).

`build.ps1`:
1. publishes `Agwinterm.Win32` (the app) and `Agwinterm.Ctl` (`agwintermctl`) as **self-contained win-x64**
   (a folder, not single-file — robust for the Vortice native libs and the on-disk `themes\`/`assets\`)
   into `installer\stage`,
2. builds the Rust core + pty-host and the `agwinterm-lite` client, staging lite (exe + its copies of
   the core dll / pty-host + bundled fonts) into `installer\stage-lite`,
3. compiles `installer\agwinterm.iss` and `installer\agwinterm-lite.iss`,
4. writes `installer\Output\agwinterm-setup-<ver>.exe` and `installer\Output\agwinterm-lite-setup-<ver>.exe`.

## What the installers do

Deliberately **minimal / non-invasive** (agterm-style): they only copy files and create shortcuts.
They do **not** touch your `PATH`, profile, or config.

- **Per-user, no admin** (`PrivilegesRequired=lowest`); main installs to
  `%LOCALAPPDATA%\Programs\agwinterm`, lite to `%LOCALAPPDATA%\Programs\agwinterm-lite`.
- **Start-menu** shortcut (`agwinterm` / `agwinterm lite`); **desktop** shortcut via a checkbox (task).
- **Launch on finish**: a "Launch …" checkbox (interactive installs).
- **Uninstall** removes the app files (your `%LOCALAPPDATA%\agwinterm` / `%LOCALAPPDATA%\agwinterm-lite`
  config/sessions are left intact).
- Upgrading the main setup from 0.15.0–0.16.0 (which bundled lite) removes the bundled
  `agwinterm-lite.exe` and its old shortcuts — install the lite setup to keep lite.

### Integrations are opt-in (from inside the app)

Open the action palette (**Ctrl+Shift+P**) and run the **Install …** entries when you want them:

- **Install Command-Line Tool (PATH)** — adds `agwintermctl` to your user `PATH` (so shells & AI agents can call it).
- **Install Agent Status Hooks** — Claude Code / Codex / generic-agent status reporting, plus a
  transparent `claude` launcher that binds Claude's session id to the agwinterm pane so restart
  auto-resumes the conversation.
- **Install Agent Skill** — teaches agents to drive agwinterm via `agwintermctl`.
- **Install Shell Integration** — a `$PROFILE` OSC-7 hook for live cwd (also works out of the box without this).

Each is reversible / re-runnable and can also be driven headless: `agwintermctl install cli|hooks|skill|shell`.

## Silent install / uninstall

```powershell
agwinterm-setup-<ver>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
agwinterm-lite-setup-<ver>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
# uninstall:
"%LOCALAPPDATA%\Programs\agwinterm\unins000.exe" /VERYSILENT
"%LOCALAPPDATA%\Programs\agwinterm-lite\unins000.exe" /VERYSILENT
```

## Signing (not done)

The installer is **unsigned**, so Windows SmartScreen will warn on first run. To sign, obtain a
code-signing certificate and sign both the app exe and the setup exe, e.g.:

```powershell
signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 <file>.exe
```

Sign the published `Agwinterm.Win32.exe`/`agwintermctl.exe` (in `stage\`) **before** compiling, then
sign the produced `agwinterm-setup-<ver>.exe`.
