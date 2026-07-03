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

- **Per-user, no admin** (`PrivilegesRequired=lowest`); installs to `%LOCALAPPDATA%\Programs\agwinterm`.
- **Start-menu** shortcut always; **desktop** shortcut via a checkbox (task).
- **PATH** (opt-out task): appends the install dir to the per-user `Path` so `agwintermctl` is callable
  from any newly opened shell (and by AI agents). Removed cleanly on uninstall.
- **Agent skill**: runs `agwintermctl install skill` (writes `SKILL.md` to `~/.claude/skills/agwinterm`
  and `~/.codex/skills/agwinterm`) — works during install with no running app, incl. silent installs.
- **Launch on finish**: a "Launch agwinterm" checkbox (interactive installs).
- **Uninstall** removes the app files and strips the install dir from PATH.

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
