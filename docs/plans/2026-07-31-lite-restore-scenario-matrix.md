# lite restore scenario matrix

## Overview

agwinterm-lite loses sessions across a restart on the work laptop — reported after a two-day
dogfood as "restore sessions doesn't work at all". It has never reproduced on the dev machine: both
a named instance and the default instance save and restore correctly there, and the obvious
suspects are clean (`stateFilePath()` creates its directory, `saveSessionState()`'s only bail is a
failed `CreateFileW`, `g_restoring` is cleared on every path).

Guessing has already been tried and cost a day. This plan instead **walks every real-world
combination on the dev machine** until one fails, fixes what it catches, and hardens the rest. The
matrix stays in the tree afterwards as permanent regression cover — restore is the feature most
likely to break silently, because nothing tells you it broke until the day you restart.

Two defects are already visible from reading the code, independent of the report, and this plan
fixes both:

1. **`restartApp()` drops the instance.** It relaunches the bare exe with no `--pipe`, so a named
   instance restarts as the *default* one and then reads a different state file. "Restart
   everything" on a named window therefore always looks like restore failed.
2. **The only structural save is the last line of `refreshTree()`.** Anything that rebuilds the tree
   while the session list is momentarily empty rewrites the file with zero `S` lines — and a good
   file is replaced by an empty one with `CREATE_ALWAYS`, so there is no previous copy to fall back
   to.

## Context (from discovery)

- **Files/components involved**: `lite/src/main.cpp` — `stateFilePath()` (~1513), `saveSessionState()`
  (~1579), `restoreSessions()` (~5042), `refreshTree()` (~3091, the only structural save),
  `restartApp()` (~3900), `newSession()` (1208), `OnDestroy()` (~4756). Checks go in `lite/test/`.
- **Related patterns found**:
  - State is a tab-separated V1 file: `W` workspace lines, `S` session lines
    (`ws`, `name`, `app`, `cwd`, then args), an optional `F` flagged-index line, and `A` active-ws.
  - Restore re-launches each spec via `newSession(cols, rows, app, args, cwd)`; a spec whose app no
    longer resolves on that machine produces no session, and (before #183) said nothing.
  - Multi-window lite is one process per window, each with its own `sessions-<instance>.tsv`. A
    session that lived in another window legitimately does not come back in this one.
  - Hidden split shells are deliberately skipped by the save (`if (s->hidden) continue;`).
  - `sessionLiveCwd()` reads the shell's PEB so the saved cwd is the live one, not the creation dir.
- **Dependencies identified**: none new. Builds on the diagnostics from
  `docs/plans/2026-07-31-lite-diagnostics-logging.md` (PR #183) — the matrix reads `lite.log` to tell
  *which* of restore's four exits a failing cell took, instead of just observing "sessions missing".
- **Prerequisite**: #183 must be merged (or this branched from it), or the matrix has no log to read.

## Development Approach

- **Testing approach**: Regular (code first, then tests) — carried over from the diagnostics plan.
  lite has no C++ unit harness; a "test" here is a scripted check under `lite/test/` that drives the
  built exe and asserts on the tree, the state file, and the log.
- Complete each task fully before moving to the next
- Make small, focused changes
- **CRITICAL: every task MUST include new/updated tests** for code changes in that task
  - tests are not optional - they are a required part of the checklist
  - each fix lands with the matrix cell that proves it, and that cell must fail before the fix
  - tests cover both success and error scenarios
- **CRITICAL: all tests must pass before starting next task** - no exceptions
- **CRITICAL: update this plan file when scope changes during implementation**
- Run tests after each change
- Maintain backward compatibility — in particular, an existing `sessions.tsv` written by 0.17.x must
  still restore after any format hardening

## Testing Strategy

- **Unit tests**: not available for lite. Each task ships PowerShell checks under `lite/test/`.
- **E2E tests**: the established lite style — sandbox instances only (`--pipe <name>`), drive with
  `agwintermctl` and posted window messages, capture with `PrintWindow`. Never inject global input;
  never run the default instance, which owns real user state.
- Treat these with the same rigor as unit tests: they must pass before the next task.

## Progress Tracking

- Mark completed items with `[x]` immediately when done
- Add newly discovered tasks with ➕ prefix
- Document issues/blockers with ⚠️ prefix
- Update plan if implementation deviates from original scope
- Keep plan in sync with actual work done

## What Goes Where

- **Implementation Steps** (`[ ]` checkboxes): the matrix, the fixes it justifies, and docs
- **Post-Completion** (no checkboxes): confirming on the work laptop, which is the only place the
  original report exists

## Implementation Steps

### Task 1: Build the scenario matrix harness
- [x] add `lite/test/restore-matrix.ps1`: a table of scenarios, each running save → restart → assert,
      reporting one PASS/FAIL line per cell plus the restore verdict read from `lite.log`
- [x] give each cell an isolated instance name and a clean state file, so cells cannot contaminate
      each other
- [x] make a failing cell print the relevant `lite.log` lines, so a failure is self-explaining
- [x] cover the baseline cells first: single session; several sessions; named workspaces; a renamed
      session; a flagged session
- [x] verify the harness itself by deliberately corrupting a state file and confirming the cell fails
- [x] run the matrix - baseline cells must pass before task 2

### Task 2: Extend the matrix to the scenarios that differ from a clean dev run
- [x] add a **profile/app** cell: a session launched with an explicit app + args (what a shell profile
      produces), restarted, asserting it comes back with the same app
- [x] add a **shell-exited** cell: let a session's shell exit, then restart, and pin what SHOULD
      happen (the spec is still saved and relaunched) so the behaviour is a decision, not an accident
- [x] add a **kill vs graceful close** pair: `CloseMainWindow` (runs `OnDestroy`) versus a forced kill
      (relies solely on the `refreshTree()` save) — this is the cell that tells us whether a laptop
      crash or a shutdown-without-close explains the report
- [x] add a **multi-window** cell: sessions created in a second instance, asserting each instance
      restores its own and documenting that cross-instance is not expected
- [ ] add an **unwritable profile** cell: assert restore degrades cleanly and the log names the cause
      ⚠️ never written — same false check-off the audit caught for `two-windows`. The unwritable
      profile is covered *outside* the matrix (`diagnose.ps1`'s write probe, `log-basics.ps1`'s
      "lite still starts"), but no cell drives restore against a read-only `%LOCALAPPDATA%` or
      asserts that `save FAILED to open` names the cause. Carried forward.
- [x] run the matrix - record which cells fail (⚠️ them in this plan before fixing)


⚠️ **ROOT CAUSE FOUND by the `killed` cell (2026-07-31), and it is NOT what it first looked like.**

First reading (wrong): id collision. `newSession()` numbers ids `<prefix>-<seq>` from 1 each launch,
the pty-host outlives the UI, so a create would hit `session '<id>' already exists`. Plausible, and
the `-3` on the next fresh session fit — but the log disproved it: `restore: 2 saved id(s) in the
file, host holds 0 live session(s)`. There was nothing to collide with.

Actual cause: **`connectControl()` accepts a dying host.** It opens the existing control pipe with
timeout 0 and treats `hello` as proof of life. After lite is force-killed, its host is tearing down
but still accepts a connection and answers hello for a moment, while refusing every real command.
lite therefore restored against a corpse: all creates returned false in the same millisecond
(`restore: 0 of 2 session(s) built`), the sessions were dropped, and the state file was immediately
rewritten with the single fresh session — destroying the evidence. On a machine that gets shut down
or signed out rather than closed cleanly, that is "restore doesn't work **at all**".

Fix: `controlHandshake()` probes with `list` — the cheapest request that actually touches the
session table — and `connectControl()` retries with a freshly spawned host (up to 4 attempts,
400 ms apart) so a dying host is discarded instead of trusted.

➕ Session ADOPTION was built alongside (Boris's choice) and is genuinely useful even though it was
not the bug: when the host really does survive (another lite window keeps it alive), restore now
attaches to the live shells rather than creating new ones. Session ids are persisted in a new `D`
line, which is additive — a 0.17.x file without one still restores.

➕ `SessionInfo.title` had to get real storage (`max_size:256`): nanopb fails an entire ListReply
decode on string overflow, so a truncated title silently broke `list` for any host holding a titled
session. Cost an hour of confusion when the health probe started failing on healthy hosts.

### Task 3: Never restore against a dying pty-host [DONE — the actual fix]
- [x] add `controlHandshake()`: hello AND a `list` probe, because hello alone is answered by a host on its way out
- [x] retry `connectControl()` with a freshly spawned host, giving the dying one time to release the pipe
- [x] persist session ids (`D` line) and adopt live host sessions on restore, falling back to create
- [x] give `SessionInfo.title` real storage so `list` decodes on a host with titled sessions
- [x] matrix cell `killed` covers it: force-kill, relaunch, both sessions return
- [x] run the matrix - all cells pass

### Task 3b: Fix `restartApp()` losing the instance
- [x] preserve `--pipe <instance>` (and any other launch args that identify the instance) when
      relaunching, so "Restart everything" comes back as the SAME instance reading the SAME state
- [x] add the matrix cell: named instance, Restart everything, assert the sessions return
- [x] add the error case: assert the default instance still restarts correctly (no regression)
- [x] run the matrix - must pass before task 4

➕ `restartCommandLine()` is the seam: `restartApp()` launches it and `--diagnose` prints it. That
matters because the no-regression half — "the default instance still relaunches as the default" —
cannot be checked by running the default instance, which owns real user state. The matrix asserts
the string instead. `--pipe` is the only arg that identifies the instance; `-d`/`-p`/`--maximized`
are first-launch session args that restore supersedes, so they are deliberately not carried over.

### Task 4: Never let a good state file be replaced by an empty one
- [x] write the state file atomically: temp file + `MoveFileExW(..., MOVEFILE_REPLACE_EXISTING)`, so a
      crash or a full disk mid-write cannot leave a truncated file where a good one was
- [x] refuse to overwrite a non-empty state file with a zero-session save unless the session list is
      genuinely empty by user action (closing the last session), and log loudly when that is skipped
- [x] keep one previous generation (`sessions.tsv.bak`) and fall back to it when the primary parses to
      zero specs — restore currently has no second chance
- [x] add matrix cells: interrupted write leaves the previous state intact; a zero-session save does
      not clobber a populated file; the `.bak` fallback actually restores
- [x] verify backward compatibility: a plain 0.17.x `sessions.tsv` with no `.bak` still restores
- [x] run the matrix - must pass before task 5

➕ The transient-empty save turned out to be **drivable end to end**, so `zero-guard` is a real
reproduction rather than an assertion about an internal branch: split shells are hidden and
deliberately not persisted, so with a split open, closing the only *visible* session leaves lite
running with zero persistable sessions. That save used to write a 0-session file over a good one.

➕ A deliberate empty (`g_userEmptied`, set only when closing the last session) must also **delete the
`.bak`** — otherwise the next launch falls back to it and resurrects exactly what the user just
closed. `closed-last` is the cell that pins it, and it is the guard's real regression risk: refusing
too much is as bad as refusing too little. Driven over the control pipe, closing the last session
does not tear the window down (`DestroyWindow` only works from the UI thread) — a pre-existing quirk,
noted here because the cell has to work around it; the save it exercises is the same one.

➕ Restore now logs which file it used, and `--diagnose` reports the `.bak` generation beside the
primary.

### Task 5: Make a failed spec visible and recoverable
- [x] when a spec fails to start, keep it in the tree as a dead session (or re-add it to the state)
      rather than dropping it, so a machine-specific launch failure loses the NAME but not the entry
- [x] surface it in the UI the way an exited session already is, so the user can see what happened
- [x] add the matrix cell: a spec with a deliberately bogus app, asserting the session is visible and
      the log names it
- [x] add the success case: a spec with a valid app still restores normally
- [x] run the matrix - must pass before task 6

➕ `failedSpecSession()` builds the dead entry: a `Session` with an EMPTY id (there is no host session
behind it), `exited` + a new `failed` flag, its own emulator, and the spec's name/ws/cwd/app/args. The
empty id is the seam — `killSession()` and the host half of `hostResize()` return early on it, so a
placeholder is inert, while the emulator still resizes and paints. Its pane carries a short "this
session could not be restored on this machine" note with the app and cwd, because the terminal is
where the user looks before the log. The tree marks it `(failed to start)` rather than `(exited)`, and
`ctl tree --json` gained `exited`/`failed` booleans (additive) so the matrix can assert it.

➕ Because the entry is a normal session, the next save persists it — so a spec that only fails on THIS
machine survives to start on the machine that has the app. When every spec fails, restore still
returns false (the caller opens a working session) but the dead entries stay in the tree beside it.

⚠️ `lite/test/log-restore.ps1` was order-dependent after Task 4: it deleted the state file but not the
`.bak`, so run 1 fell back to the previous suite run's sessions and "built count matches what was
saved" compared run 1's restore with run 1's last save. Fixed in the harness — the reset removes
`.bak`/`.tmp`, and the run-2 assertions read the LAST restore line, not the first.

### Task 6: Verify acceptance criteria
- [x] verify every scenario in the matrix passes, and that each fix is tied to the cell that failed
- [x] verify edge cases: empty file, truncated file, unknown future `V2` header, missing `W` lines,
      a spec referencing a workspace index that no longer exists
- [x] run the full `lite/test/run-all.ps1` suite
- [x] run the .NET and Rust suites to confirm nothing outside lite moved
- [x] confirm lite builds clean via `lite/build.ps1`

**Fix → the cell that caught it** (every fix in this plan is anchored to a cell that fails without it):

| Fix | Cell |
| --- | --- |
| `controlHandshake()` probes with `list`; `connectControl()` retries a fresh host (Task 3) | `killed` |
| session ids in the `D` line, adopt live host sessions on restore (Task 3) | `killed` (asserts the log says `adopted live session`), `stale-ids`, `short-id-line` |
| `SessionInfo.title` is *skipped* on decode so `list` can never overflow (Task 3, revised in review) | `killed` (via the health probe) |
| `restartCommandLine()` preserves `--pipe` (Task 3b) | `restart-named`, `restart-cmdline` |
| atomic temp-file write, published with `ReplaceFileW` (Task 4) | `bak-rotation`, `publish-blocked`, `interrupted-write` |
| protocol fields are fixed-size and `strcpy_s` *terminates* rather than truncates (review pass 2) | `oversize-fields` |
| a session name cannot forge a state-file line (review pass 2) | `name-injection` |
| refuse a zero-session save over a populated file (Task 4) | `zero-guard` |
| a deliberate empty also deletes the `.bak` (Task 4) | `closed-last` |
| `.bak` fallback when the primary yields zero specs (Task 4) | `bak-fallback`, `compat-0.17` |
| `failedSpecSession()` keeps an unstartable spec as a dead entry (Task 5) | `failed-spec`, `bogus-app` |
| tolerate an unknown `V` header instead of silently swallowing it (Task 6) | `future-v2` |

⚠️ `interrupted-write` is a weaker anchor than it reads as: nothing in lite ever *reads* `sessions.tsv.tmp`,
so seeding a stray one only proves a good primary still restores beside it. `bak-rotation` is what
actually pins the publish (lite writes the `.bak` itself, and the primary holds the newer generation),
which is why it is listed first.

➕ `publish-blocked` (review pass 3) closes the gap this note used to describe: it holds the primary
open with `FileShare.None`, so `ReplaceFileW` *and* the `MoveFileExW` fallback are both denied, and
asserts the log names the failed publish while the saved state stays readable and still restores. The
same pass gave the save an in-place fallback for the other half — a directory that allows writing the
existing `sessions.tsv` but not creating `sessions.tsv.tmp` beside it, where the atomic write would
otherwise have saved *nothing* on a machine where the old build saved fine.

➕ Task 2 had checked off a multi-window cell that was never actually written — every cell used a
single instance. Caught by this task's audit and added: `two-windows` runs two instances at once and
asserts each restores only its own sessions, which is the mundane reading of "my sessions are gone".

➕ A future-build file is now READ for the line types this build recognises rather than discarded —
refusing it would lose the sessions *and* then overwrite the newer file with a V1 one. `parseStateFile()`
records the version so the log can say which it did.

**Results (2026-07-31)**: `lite/test/run-all.ps1` — all checks pass, including the matrix cells and the
harness self-check. .NET: `Agwinterm.Core.Tests` 200/200, `Agwinterm.Pty.Tests` 116/116. Rust workspace:
27/27. `lite/build.ps1` builds clean. The only non-lite files touched on this branch (`persist.rs`,
`Ptyhost.cs`) differ by line endings alone — no content moved outside lite.

### Code review (2026-07-31) — what it changed

⚠️ **The `list` probe could stop lite from launching at all.** Task 3 gave `SessionInfo.title` fixed
storage because a too-small buffer failed the decode; but a title is whatever OSC 0/2 the shell wrote,
so *any* fixed size is one a real title can exceed — and `list` had just become the startup gate, so
one long title anywhere on the shared host meant four failed attempts and `fatal()`. `title` is now a
**skipped callback** (lite reads only `id`), and the probe asks whether the host *answered* rather
than whether the reply decoded. A fix that traded "restore doesn't work" for "lite doesn't start".

⚠️ **The zero-session guard failed open, and took the `.bak` with it.** `stateFileSessionCount()`
returns `-1` for "exists but unreadable", which `had > 0` waved through — and the `.bak` delete was
keyed on `saved == 0` rather than on the deliberate-empty flag, so one transient empty against a
locked primary destroyed both generations. That is the corporate-agent/locked-profile environment
this plan named as its leading hypothesis. Now: refuse unless the primary is provably empty, and
delete the `.bak` only for a deliberate empty.

⚠️ **`g_userEmptied` was a one-way latch.** Closing the last session over the control pipe leaves the
window alive (`DestroyWindow` is a no-op off the UI thread), so the flag stayed set for the rest of
the process and disabled the guard permanently. Cleared whenever a session is added; the new
`guard-after-empty` cell drives exactly that sequence.

⚠️ **`--no-restore` after a kill was a hard startup failure.** The id-collision fix ran *inside*
`restoreSessions()`, past its early returns — so with the state file missing or `--no-restore` set
while the host still held `<prefix>-N`, the first create was rejected and lite died with "could not
create the first session". The host scan moved to `scanHostSessions()`, which runs on every launch
(and removes the duplicate `list` round trip).

⚠️ **Adoption ignored `attached` and `has_exited`**, both already on the wire. Adopting an attached
session supersedes the window currently driving it — two windows on one instance stole each other's
shells; adopting an exited one produced a dead pane where a relaunched shell belonged.

➕ Also fixed: `window.delete` left the `.bak`/`.tmp` behind (so a deleted window's sessions came
back), a failed attach after a successful create orphaned a host shell, the retry loop could stack up
multiple hosts on one pipe name, a `V0` (0.17.x) file drew a scary version warning, and the instance
name reached both the state path and the `cmd.exe` restart line unsanitised.

➕ **Test cover for the code the branch was actually about.** Adoption had no assertion at all —
`killed` passed identically whether it adopted or re-created — and no cell seeded a `D` line, so the
whole new parse/lookup branch was dead under test. `killed` now asserts the log says
`adopted live session`; `stale-ids` and `short-id-line` pin the deterministic `D`-line cases;
`shell-exited` proves its own premise instead of duplicating `several`; `guard-after-empty` covers the
latch. Harness-wise: asserts moved inside the try (one assert on a missing file used to terminate the
whole suite and skip every cell after it), every cell force-stops its instances in a `finally`, the
self-check honours `-Only`, `Signature()` no longer counts workspace rows as sessions, and the
session-object lookup went from a regex that could span a workspace and the first session inside it to
`ConvertFrom-Json`.

**Post-review results (2026-07-31)**: `lite/build.ps1` clean; `lite/test/run-all.ps1` — all lite checks
pass, 26 matrix cells plus the harness self-check. The second review pass added
`closed-last-with-hidden`: the zero-session guard counted the raw session list while the save counts
only visible sessions, so closing the last visible session with a quick terminal (or a split shell)
open refused the save and the closed session came back on the next launch. Verified failing before
the fix.

### Task 7: [Final] Update documentation
- [x] document the state file, its `.bak` generation, and the restore rules in the README lite section
- [x] note the multi-window/per-instance state rule, since "my sessions are gone" has that mundane
      reading
- [x] record whatever the matrix caught in the build-and-test gotchas memory

➕ The README's new **Session restore & the state file** section is written for the person reading it
*because* sessions went missing, so it leads with the per-instance rule ("right sessions, wrong
window" is the mundane reading) before the format, the atomic write, the `.bak`, and the restore
order — every branch of which names itself in `lite.log`. "Restart everything" is documented as
keeping the instance, since that was a real defect and is now a promise.

⚠️ Documenting the format caught a small inaccuracy in the draft: `O` (focused workspace) is **parsed
but never written** by this build. The README now says so rather than listing it as a record lite
emits — the point of the bullet is that a line type this build doesn't write is still honoured, which
is the same forward-compatibility rule as `V2`.

➕ Memory (`agwinterm-build-and-test-gotchas`) gained the traps from Tasks 3b–5 beside the Task 2–3
ones already recorded: a guard needs its legitimate-case escape hatch (and the `.bak` delete that goes
with it), the transient-empty save is drivable via a split, and relaunch paths silently drop `--pipe`.

**Docs validation (2026-07-31)**: `lite/build.ps1` clean, `lite/test/run-all.ps1` all checks pass —
every matrix cell plus the harness self-check. No code changed in this task.

## Technical Details

- **State file**: `%LOCALAPPDATA%\agwinterm-lite\sessions.tsv` (default instance) or
  `sessions-<instance>.tsv`. Format V1, tab-separated: `W<TAB>name`, `S<TAB>ws<TAB>name<TAB>app<TAB>cwd[<TAB>arg…]`,
  `F<TAB>idx…`, `A<TAB>activeWs`.
- **Atomic write**: build the full buffer in memory (already done), write to `sessions.tsv.tmp`, flush
  and close, then `MoveFileExW` over the target. Rename the previous target to `.bak` first.
- **Restore order** after this plan: primary → `.bak` if the primary yields zero specs → fresh start.
  Every branch says which one it took in the log.
- **Instance preservation in `restartApp()`**: rebuild the command line from the current instance
  rather than launching a bare exe.
- **What stays out of scope**: the switch-time render artefacts, and image paste. Separate reports.

## Post-Completion

*Items requiring manual intervention or external systems - no checkboxes, informational only*

**Manual verification**:
- Install the resulting build on the work laptop and restart lite the way it is normally used
- If sessions still vanish, read `lite.log` there: it now names which of restore's four exits ran, and
  whether the save that preceded it succeeded and with how many sessions. Run `--diagnose` for the
  state path plus the write probe.
- If the matrix caught the bug here, confirm the same scenario on the laptop no longer loses sessions

**Open question this plan may answer or may not**:
- If every matrix cell passes and the laptop still fails, the cause is environmental (a redirected or
  policy-locked `%LOCALAPPDATA%`, or an aggressive corporate agent removing files) — which the
  `--diagnose` write probe distinguishes from a code defect.
