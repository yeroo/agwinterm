# Contributing to agwinterm

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

# build the portable single-file exe (no Inno Setup needed)
./installer/build-portable.ps1
```

## Dev builds run side-by-side with the installed release

A **Debug** build uses a separate instance identity, `agwinterm-dev`, so it keeps its **own** data dir
(`%LOCALAPPDATA%\agwinterm-dev`: config, sessions, keymap, themes) and its **own** control pipe. It
never touches, or fights over the pipe with, your installed **Release** (`agwinterm`), so you can
daily-drive the release and run dev builds at the same time.

```powershell
dotnet run --project src/Agwinterm.Win32           # Debug -> the "agwinterm-dev" instance
agwintermctl --pipe agwinterm-dev tree             # drive the dev instance from outside
agwintermctl tree                                  # (default pipe) drives the release
```

Inside any session, `AGWINTERM_PIPE` is already set, so a bare `agwintermctl` auto-targets the
instance it is running in. Force a specific identity with `--app-id <name>` or `AGWINTERM_APP_ID`
(handy for a second throwaway instance). Dev builds also skip registering as the default-terminal COM
server, so they never intercept the release's console handoffs.

## The contract with agliteterm

`tests/conformance/control-api.json` is the canonical spec for the control-API subset agliteterm
shares, and **both repositories run it in CI**. agliteterm builds against an ABI-pinned
`agwinterm_core.dll` published from this repo and refuses to build if the published `abiVersion` is
not the one it requires, so an ABI change is a release-coordinated change in both repositories.
