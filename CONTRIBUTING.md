# Contributing to agwinterm

## Before you start

If this is your first pull request to agwinterm, open an [issue](https://github.com/yeroo/agwinterm/issues) describing what you plan to build and why, then wait for a reply before writing code. Some ideas do not fit the project's direction, and finding that out early costs less than finding it out after a finished PR.

Check that what you want does not already exist: agwinterm carries a broad control API, custom commands you bind yourself in `keymap.conf`, and a Settings dialog, so a lot is reachable without new code. Look at `agwintermctl --help`, the [README](README.md), the [user guide](docs/user-guide.md), the [control API](docs/control-api.md), and the bundled agent skill (`agwintermctl install skill`, source in `src/Agwinterm.Pty/AgentSkill.cs`) before proposing anything.

Issues are for concrete bugs and for features; a feature issue says what problem it solves before it says how.

## Is it worth it?

Weigh what a feature adds against the code it brings with it.

- **Does it help most users, or one setup?** A feature covering a handful of edge cases rarely pays for its maintenance cost.
- **Does it belong in agwinterm?** agwinterm is a from-scratch terminal: the emulator, key handling, rendering and the control API are all here, so those bugs are ours. What is not ours: the shell (PowerShell, PSReadLine, cmd, bash), ConPTY, and Windows itself. A PR working around a shell or ConPTY bug inside the terminal will be asked to show that the bug is really the terminal's.
- **Does it change what agwinterm is?** It is a terminal with a two-level workspace to session sidebar and a control API, following [umputun's agterm](https://github.com/umputun/agterm) on Windows. It is not a multiplexer, not a session manager for other tools, not an IDE. PRs pulling it in those directions will be closed. Where agterm already has a model for something (a menu, a verb, a keymap rule), agwinterm follows that model unless there is a Windows reason not to; say so in the PR when you diverge.
- **Is the code proportional to the value?** Several hundred lines for something a `keymap.conf` custom command already covers is a hard sell. Keep it small.

## Development setup

Building needs Windows x64 and the **.NET 10 SDK** (the exact version is pinned in [`global.json`](global.json)). Nothing else is required for a working terminal: the managed emulator is the default (`emulator-core = managed`).

```powershell
dotnet build Agwinterm.slnx -c Release
dotnet test  Agwinterm.slnx -c Release          # Core + Pty unit tests
dotnet run --project src/Agwinterm.Win32 -c Release
```

The Rust side (`native/`: the `agwinterm-core` emulator and the `agwinterm-ptyhost` server) is optional for day-to-day work and needed for `emulator-core = rust`, `session-host = server-rust`, the installer, and CI's parity oracle. It needs a stable Rust toolchain:

```powershell
cargo build --release --manifest-path native/Cargo.toml
```

| command | what it does |
|---|---|
| `dotnet build Agwinterm.slnx -c Release` | build everything managed |
| `dotnet test Agwinterm.slnx -c Release` | host-free `Agwinterm.Core` tests and the `Agwinterm.Pty` tests |
| `cargo build --release --manifest-path native/Cargo.toml` | the Rust core and pty-host |
| `./tools/check-abi.ps1` | the Rust core's C ABI is declared in more than one place; this checks they agree |
| `./installer/build.ps1` | the Inno Setup installer (needs Inno Setup 6 and the Rust build) |
| `./installer/build-portable.ps1` | the portable single-file exe |

Build and the unit tests must be green before you send a PR. `Agwinterm.Core` treats warnings as errors and every project has nullable reference types on, so a new warning fails the build rather than accumulating.

**One Release output root is fresh.** The solution builds with Platform=x64, so the app lands in `src\Agwinterm.Win32\bin\x64\Release\net10.0-windows\win-x64\`. A sibling `bin\Release\...` tree may exist from older configurations and is never refreshed; do not run or test from it.

### The interactive suites

`tests/integration/*.ps1` drive a real window: the control host (`win32-control.ps1`), and the `hud-ui.ps1` suites (`-Suite Hud|Quick|Navigation|Picker|Accessibility|Command|Menu`) for the HUD, the quick terminal, sidebar navigation, the native picker, UI Automation, session commands and the menu bar. CI runs all of them except `Accessibility` on every PR, and they are the evidence a UI change is judged on, so a change that touches UI behaviour adds or extends a case there.

They run under **PowerShell 7** (`pwsh -NoProfile -File ...`), never Windows PowerShell 5.1. Each one starts a private instance (`--app-id` and `--pipe` with throwaway names, `--no-restore`) so your own sessions, config and clipboard are never touched, and drives it only by posting messages to that instance's own windows: no `keybd_event`, no `SendInput`, no global mouse. Keep to that when you add a case. On a machine without the maintainer's suite-token helper the runners refuse to start unless `$env:CI = 'true'` is set, which is the intended way to run them on your own desktop.

`tests/conformance/control-api.json` is the control-API contract shared with [agliteterm](https://github.com/yeroo/agliteterm); `tests/conformance/conformance.ps1` runs it. `qa/*.md` are behaviour cases written for a person (or an agent) to execute against a live build; `qa/product.md` says how.

### Dev builds run side-by-side with the installed release

A **Debug** build uses a separate instance identity, `agwinterm-dev`, so it keeps its **own** data dir (`%LOCALAPPDATA%\agwinterm-dev`: config, sessions, keymap, themes) and its **own** control pipe. It never touches, or fights over the pipe with, your installed **Release** (`agwinterm`), so you can daily-drive the release and run dev builds at the same time.

```powershell
dotnet run --project src/Agwinterm.Win32           # Debug -> the "agwinterm-dev" instance
agwintermctl --pipe agwinterm-dev tree             # drive the dev instance from outside
agwintermctl tree                                  # (default pipe) drives the release
```

Inside any session, `AGWINTERM_PIPE` is already set, so a bare `agwintermctl` auto-targets the instance it is running in. Force a specific identity with `--app-id <name>` or `AGWINTERM_APP_ID` (handy for a second throwaway instance). Dev builds also skip registering as the default-terminal COM server, so they never intercept the release's console handoffs.

## AI-assisted contributions

AI-assisted development is welcome. The expectations for those PRs are specific.

**Quality expectations**

- Review your own code before submitting it, whether you or a tool wrote it.
- Follow the project's conventions. Read the codebase first, with or without AI help. The ones that catch people out:
  - **Module boundary.** `Agwinterm.Core` is host-free and targets plain `net10.0`: no Win32, no Direct2D, no `System.Windows`, no console. That is what lets its tests run with no window. The emulator, config and keymap parsing, the menu model and the host-actions seam (`IHostActions`) live there. `Agwinterm.Pty` owns sessions, the control server and the agent skill; `Agwinterm.Win32` is the shell, and its `Program` class is split by concern across `Program.*.cs`. When a feature splits, push the host-free half down into `Agwinterm.Core` and keep the Win32 side a thin adapter for side effects.
  - **Threads.** The window has one UI thread. A control verb arrives on a pipe thread and reaches window state only through `PostVerb`, which marshals the work onto the UI thread; session output arrives on reader threads and requests a repaint rather than drawing. Touching `_config`, panes, the tree or Direct2D from another thread is a bug even when it appears to work.
  - **Tests.** xUnit in `tests/Agwinterm.Core.Tests` (by concern: `NavigationTests` covers the keymap, `MenuModelTests` the menus, and so on) and `tests/Agwinterm.Pty.Tests`. Add focused coverage to the file that already owns the behaviour; create a new one only when that gives clearer ownership. Behaviour that needs a window goes into an interactive suite, see above.
  - **Documentation.** A change to the control API updates `docs/control-api.md` and the agent skill in `src/Agwinterm.Pty/AgentSkill.cs`, and `tests/conformance/control-api.json` when the verb is one agliteterm shares. A change to the keymap format updates `Keymap.StarterText` (the header written to a new `keymap.conf`) and `docs/user-guide.md`. There is no CHANGELOG to edit; release notes are written at release time.
  - **Config.** A new `agwinterm.conf` key is parsed in `TerminalConfig`, applied live in `ApplyConfigKeys` (a reload must not fall short of a `config set`), and documented in the user guide.
- Code has to be readable by a person maintaining it a year from now.
- Commit messages and PR descriptions should say something specific. Generic AI output is not enough.
- Keep PRs focused. Unrelated cleanup does not belong in a feature PR, so send it separately.
- Checking AI output for security problems is your job. These tools introduce vulnerabilities that are not obvious on a skim.
- You must understand and be able to explain every line you submit. If asked about your changes during review, "the AI wrote it" is not an acceptable answer.

**Reviewable scope**

- A PR has to be sized for a human to review.
- Split a large change into focused, logical PRs.
- A PR touching dozens of files with thousands of lines is not reviewable. Break it down.

**What will not be accepted**

- Unreviewed AI output dumped for the maintainer to clean up.
- Code with no tests, or with a failing build or test run.
- Changes that ignore project conventions after being pointed at them.
- PRs that do not respond to review feedback.

A PR violating these may be closed without further discussion. Contributions are valued, but the project cannot act as free QA for bulk AI-generated code.

## Adding a user-facing action

This is the convention you cannot guess from the code: a new user action is not done when the key works.

The keymap, the menu bar, the command palette and the control pipe are callers of the same seam and must not drift apart. A new **keymap action** needs all of:

1. its id in `Keymap.ValidActions` and in the `actions:` list of `Keymap.StarterText`
2. a `case` in `RunAction` (`Program.Input.cs`) that calls the same method the menu or verb calls
3. a row in `MenuModel` when it belongs in a menu, so the accelerator and the enabled state show there
4. a parser test in `NavigationTests`, and an interactive case when the action does something visible

A new **control verb** needs all of:

1. an `agwintermctl` subcommand in `src/Agwinterm.Ctl/Program.cs`
2. dispatch in `ControlServer` (`src/Agwinterm.Pty`), which owns the host-free part (argument parsing, validation, error text, response shape) and calls the host through `ISessionHost` for the side effect alone
3. the `ISessionHost` implementation in `Program.ControlHost.cs`, reaching window state through `PostVerb`
4. `docs/control-api.md`, the agent skill, and the conformance spec when agliteterm shares the verb
5. an end-to-end check in `tests/integration/win32-control.ps1` or a `hud-ui.ps1` cases file

A verb that sets or mutates per-session state also owes a matching read-back field on the `tree` node, so a script can query the value it just wrote. A refusal names only what its guard saw, and a verb that cannot do the whole job says so rather than doing half.

The one exemption is chrome with nothing to drive, meaning pure rendering or visual polish. Say so in the PR description when you claim it.

## Issues and PRs

Every issue and pull request has to answer two questions:

1. **What is the problem?** What exactly is broken, missing, or awkward. Be specific. "It would be nice to have X" is not a problem statement.
2. **How does this solve it?** Why this particular approach is the right fix, and how it addresses the cause rather than the symptom.

A PR with no problem statement will be closed. If the problem is hard to articulate, the solution is probably not needed.

PRs here merge on evidence: the description carries the test output, and for a UI change a screenshot or the transcript of the interactive case that shows it. CI runs on every PR; the first PR from a new fork waits for a maintainer to approve the workflow run, which happens once the review is under way.

Releases are the maintainer's: a `v*` tag on `main` builds the installer, the portable exe and the CLI, publishes them with provenance attestations, and submits to winget and Chocolatey. Do not bump `installer/agwinterm.iss` in a feature PR.

## The contract with agliteterm

`tests/conformance/control-api.json` is the canonical spec for the control-API subset [agliteterm](https://github.com/yeroo/agliteterm) shares, and **both repositories run it in CI**. agliteterm builds against an ABI-pinned `agwinterm_core.dll` published from this repo and refuses to build if the published `abiVersion` is not the one it requires, so an ABI change in `native/agwinterm-core` is a release-coordinated change in both repositories: run `./tools/check-abi.ps1`, and say in the PR that the ABI moved.
