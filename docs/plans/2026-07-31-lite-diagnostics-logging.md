# lite diagnostics logging

## Overview

agwinterm-lite has no way to explain itself after the fact. Every field report from the 2026-07-29→31
dogfood cost hours because the failure happened on the work laptop and left no trace:

- **"restore sessions doesn't work at all"** — still unreproduced. Both a named instance and the
  default instance save and restore correctly on the dev machine, and the obvious suspects
  (`stateFilePath()` creating its directory, `saveSessionState()`'s only bail being a failed
  `CreateFileW`, `g_restoring` being cleared) are all clean. Without knowing whether the laptop is
  failing to **write** the file or failing to **rebuild** from it, any fix is a guess — and the two
  fixes are opposite.
- **render artefacts when switching sessions** — a screenshot, no mechanism. One real bug was found
  and fixed along the way (the selection was keyed by pane index, so it suppressed the caret and
  painted over the next session), but it does not explain the clipped left edge or the second column.
- **"can't type after switching sessions"** — this one WAS pinned, but only because `GetGUIThreadInfo`
  could be run live on the dev machine. The same trick is not available remotely.

This plan adds a small always-on logging subsystem to lite and makes session save/restore its first
customer, so the next report arrives with evidence attached instead of needing a re-enactment.

Explicit non-goal: fixing the restore break itself. The log is what tells us which fix to write.

## Context (from discovery)

- **Files/components involved**: `lite/src/main.cpp` (single translation unit, ~5300 lines),
  `lite/build.ps1`. No other project touches this.
- **Related patterns found**:
  - `stateFilePath()` (line ~1513) resolves `%LOCALAPPDATA%\agwinterm-lite\sessions.tsv` for the
    default instance, `sessions-<instance>.tsv` otherwise, and `CreateDirectoryW`s the parent.
  - `saveSessionState()` (~1579) writes the whole file with `CREATE_ALWAYS`; it returns silently on a
    failed open — currently the single most likely place for a silent field failure.
  - `restoreSessions()` (~5042) returns false on: missing file, empty file, no `S` lines, or no
    session successfully created. Four distinct causes, all indistinguishable from outside.
  - `updDir()`/`updCleanup()` (~2469) already use the same `%LOCALAPPDATA%\agwinterm-lite` root, and
    the self-update helper writes a `.log` next to its payload — precedent for logging to that dir.
  - `fatal()` exists for hard failures; there is no non-fatal diagnostic channel.
- **Dependencies identified**: none new. Win32 file APIs only, consistent with lite's no-dependency
  posture (it links agwinterm_core + nanopb and nothing else).
- **Threads that will call the logger**: the UI thread, one `readerThread` per session (line 1194),
  `updWorker` (2675), `ctlServerThread` (5299) and a `ctlClientThread` per control connection (5042).
  The logger must therefore be thread-safe on its own — `g_lock` guards emulator state, not this.

## Development Approach

- **Testing approach**: Regular (code first, then tests). lite has no C++ test harness and this plan
  does not add one; "tests" here are behavioural checks driven from PowerShell against the built exe,
  in the style already used this session (posted messages + `PrintWindow`, never global input).
- Complete each task fully before moving to the next
- Make small, focused changes
- **CRITICAL: every task MUST include new/updated tests** for code changes in that task
  - tests are not optional - they are a required part of the checklist
  - for this plan a "test" is a scripted check under `lite/test/` that runs the built exe and asserts
    on the log file / diagnose output
  - tests cover both success and error scenarios (e.g. an unwritable state directory)
- **CRITICAL: all tests must pass before starting next task** - no exceptions
- **CRITICAL: update this plan file when scope changes during implementation**
- Run tests after each change
- Maintain backward compatibility

## Testing Strategy

- **Unit tests**: not available for lite (no C++ harness). Each task instead ships a PowerShell check
  under `lite/test/` that drives the built exe and asserts on observable output.
- **E2E tests**: this project's e2e style for lite is: launch with `--pipe <sandbox>`, drive with
  `agwintermctl` and posted window messages, capture with `PrintWindow`. Never inject global input
  (`keybd_event`/`SendInput`) — it lands in whatever window has focus, which on a machine in use
  means typing into the user's windows. Never run the default instance against real user state.
- Treat these checks with the same rigor as unit tests: they must pass before the next task.

## Progress Tracking

- Mark completed items with `[x]` immediately when done
- Add newly discovered tasks with ➕ prefix
- Document issues/blockers with ⚠️ prefix
- Update plan if implementation deviates from original scope
- Keep plan in sync with actual work done

## What Goes Where

- **Implementation Steps** (`[ ]` checkboxes): changes inside this repo — logger, call sites, checks, docs
- **Post-Completion** (no checkboxes): running the new build on the work laptop and reading what it says

## Implementation Steps

### Task 1: Add the logging core
- [x] add `logInit()` / `logWrite(level, fmt, ...)` to `lite/src/main.cpp`, above the first caller
- [x] resolve the path once at startup: `%LOCALAPPDATA%\agwinterm-lite\lite.log` (default instance) or
      `lite-<instance>.log`, so multi-window instances never interleave into one file
- [x] make it thread-safe with its own `CRITICAL_SECTION` — callers include the UI thread, per-session
      reader threads, the control server and its per-client threads, and the update worker
- [x] open with `FILE_APPEND_DATA` + `FILE_SHARE_READ` so the file can be read while lite runs
- [x] format each line `YYYY-MM-DD HH:MM:SS.mmm  LEVEL  message` (local time, matching the self-update
      helper's log so both read the same way)
- [x] rotate at ~1 MB: rename to `lite.log.old` (replacing any previous) and start a new file
- [x] write one INFO line at startup recording version, instance name, exe path, and argv
- [x] add `lite/test/log-basics.ps1`: launch a sandbox instance, assert the log file is created,
      contains the startup line, and is readable while the process runs
- [x] add an error case to the same check: point `%LOCALAPPDATA%` at an unwritable path and assert
      lite still starts and does not crash (logging must never be fatal)
- [x] run the checks - must pass before task 2

### Task 2: Instrument session save/restore
- [x] log in `saveSessionState()`: resolved path, session count written, bytes written, and on failure
      the `GetLastError()` from `CreateFileW`/`WriteFile` — this is the "did the laptop even write it"
      question, currently a silent `return`
- [x] log in `stateFilePath()` when `CreateDirectoryW` fails with anything other than
      `ERROR_ALREADY_EXISTS`
- [x] log in `restoreSessions()` which of its four exits was taken: file missing (with the path),
      file empty, no `S` lines parsed, or no session created — plus, on success, the spec count and
      how many sessions were actually built
- [x] log each spec that fails to produce a session, with its app/cwd, so a shell that no longer
      launches on that machine is visible by name
- [x] add `lite/test/log-restore.ps1`: create sessions in a sandbox instance, close, assert the log
      shows a save with a nonzero count and byte total; relaunch, assert it shows a restore with a
      matching spec count
- [x] add the failure case: corrupt the state file to zero bytes and assert the log names the
      "file empty" exit rather than going quiet
- [x] run the checks - must pass before task 3

### Task 3: Instrument focus and font resolution
- [x] log the focus handoffs added in #182: sidebar `WM_SETFOCUS` bounce, `WM_ACTIVATE` re-claim, and
      any `WM_APP_FOCUSTERM` skipped because a rename owns the keyboard — the "can't type" class of
      report should be answerable from the log alone
- [x] log font resolution at startup: which catalog face+size was chosen, whether it came from the
      registry or `setDefaultFont()`, and which `.agbf` packs were found next to the exe
- [x] add `lite/test/log-focus-font.ps1`: post a click at a sidebar row, assert the log records the
      bounce and that `GetGUIThreadInfo` still reports the frame as focus owner
- [x] add the font case: clear the saved selection, launch, assert the log names
      `AGWin Bitmap Complete 16` as the first-run default
- [x] run the checks - must pass before task 4

### Task 4: Add `--diagnose`
- [x] add a `--diagnose` argument that prints a single report to stdout (via `AttachConsole`, the
      same technique `--bench-agbf` already uses) and exits without creating a window
- [x] report: lite version and exe path; instance name; state file path, existence, size, mtime, and a
      real writability probe (create + delete a temp file in that directory); the state file's
      contents; resolved font and pack inventory; log file path and size; `%LOCALAPPDATA%` value
- [x] make it safe to run while another lite is live (read-only, shares reads, never writes state)
- [x] add `lite/test/diagnose.ps1`: assert the output names the state path, reports writable=true on a
      normal profile, and exits 0 with no window created
- [x] add the error case: run it with an unwritable state dir and assert it reports writable=false
      rather than failing
- [x] run the checks - must pass before task 5

### Task 5: Verify acceptance criteria
- [x] verify all requirements from Overview are implemented
- [x] verify edge cases are handled: unwritable directory, missing directory, log rotation at the
      threshold, two instances logging concurrently to their own files
- [x] confirm the steady-state cost is acceptable for the target machines: logging is per structural
      event (session create/close/save/restore/focus change), NOT per output chunk or per frame
- [x] run the full `lite/test/` check suite (added `lite/test/run-all.ps1` as the entry point,
      and ➕ `lite/test/log-rotation.ps1` — rotation and per-instance isolation had no coverage,
      they were only listed as Task 5 edge cases)
- [x] run the existing .NET + Rust suites to confirm nothing outside lite moved
- [x] confirm lite still builds clean via `lite/build.ps1`

### Task 6: [Final] Update documentation
- [x] document the log location, rotation, and `--diagnose` in `README.md`'s lite section
- [x] add a short "reporting a lite problem" note: run `--diagnose`, attach `lite.log`
- [x] record the diagnosis-first lesson in the build-and-test gotchas memory

## Technical Details

- **Log line format**: `2026-07-31 13:22:41.508  INFO  restore: 3 specs, 3 sessions built` — fixed-width
  level so the file greps cleanly.
- **Levels**: INFO and WARN only. lite has `fatal()` for unrecoverable cases; a third level would be
  ceremony without a consumer.
- **Rotation**: checked on write, not on a timer. At ~1 MB, `MoveFileExW(..., MOVEFILE_REPLACE_EXISTING)`
  to `*.log.old`, then reopen. Two generations is enough to survive one restart.
- **Failure policy**: logging never throws, never blocks startup, and never becomes fatal. If the file
  cannot be opened, the logger degrades to a no-op for the process lifetime and lite runs unchanged.
- **Per-instance files**: multi-window lite is one process per window (`--pipe <name>`), so a shared
  file would interleave. Each instance writes its own, named the same way the state file is.
- **What is NOT logged**: terminal output, pasted text, typed keys, or command lines from sessions.
  The log records lite's own decisions — paths, counts, error codes, focus transitions — so it can be
  attached to an issue without leaking what the user was working on.

## Post-Completion

*Items requiring manual intervention or external systems - no checkboxes, informational only*

**Manual verification**:
- Install the resulting build on the work laptop and reproduce the restore failure normally
- Read `lite.log` there: it should say either "save failed, GetLastError=..." (the laptop cannot write
  the state file) or "restore: file missing / 0 specs / N specs, 0 sessions built" (it can write but
  cannot rebuild). Those point at opposite fixes, which is exactly the fork this plan exists to settle.
- Run `agwinterm-lite --diagnose` on the laptop and compare the state path and writability against
  this machine
- While there, capture the outstanding M1 perf-gate numbers (<150 ms start, <40 MB at 3 sessions)

**Follow-on work unblocked by this** (separate plans, not part of this one):
- the session-restore fix itself, once the log names the cause
- the switch-time render artefacts, which may need frame-level instrumentation this logger does not
  attempt
