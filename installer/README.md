# agwinterm installer

Builds a **per-user** Windows installer (`agwinterm-setup-<ver>.exe`) from a self-contained
publish of the app — end users need **no** .NET runtime installed.

## Build

```powershell
installer\build.ps1
```

Prereqs:
- .NET SDK with the `net10.0-windows` target (the repo pins .NET 10).
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) — `ISCC.exe` on PATH or in the default install location.

The script:
1. publishes `Agwinterm.Win32` (the app) and `Agwinterm.Ctl` (`agwintermctl`) as **self-contained win-x64**
   (a folder, not single-file — robust for the Vortice native libs and the on-disk `themes\`/`assets\`)
   into `installer\stage`,
2. compiles `installer\agwinterm.iss`,
3. writes `installer\Output\agwinterm-setup-<ver>.exe`.

## What the installer does

Deliberately **minimal / non-invasive** (agterm-style): it only copies files and creates shortcuts.
It does **not** touch your `PATH`, profile, or config.

- **Per-user, no admin** (`PrivilegesRequired=lowest`); installs to `%LOCALAPPDATA%\Programs\agwinterm`.
- **Start-menu** shortcut always; **desktop** shortcut via a checkbox (task).
- **Launch on finish**: a "Launch agwinterm" checkbox (interactive installs).
- **Uninstall** removes the app files.

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
# uninstall:
"%LOCALAPPDATA%\Programs\agwinterm\unins000.exe" /VERYSILENT
```

## Signing (not done)

The installer is **unsigned**, so Windows SmartScreen will warn on first run. To sign, obtain a
code-signing certificate and sign both the app exe and the setup exe, e.g.:

```powershell
signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 <file>.exe
```

Sign the published `Agwinterm.Win32.exe`/`agwintermctl.exe` (in `stage\`) **before** compiling, then
sign the produced `agwinterm-setup-<ver>.exe`.
