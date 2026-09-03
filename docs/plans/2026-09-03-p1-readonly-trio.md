# P1 — the read-only trio: `surface.cursor`, `statusChangedAt`, `agwintermctl version`

Batch **P1** of the parity programme in
[2026-09-03-parity-batches.md](2026-09-03-parity-batches.md). Closes items 1, 2 and 8 of
[agterm-parity.md](../agterm-parity.md).

## Overview

Three small, **read-only** additions to the control API. Nothing here mutates a session, changes the
restore format, or touches the renderer — which is why they travel together and why this batch goes
first.

They are grouped by what they have in common: each one answers a question a caller currently has to
*guess at*, and each guess has already cost us work.

- **`surface.cursor`** — the caret column of a pane, as a bare integer. This is the last check before
  typing into another agent's composer: the caret rests at a known column in an empty box, so
  anything else means a draft is sitting there and the send should refuse. Today
  `C:\Users\boris\AI\bin\peer-chat.py` proves emptiness by matching rendered text against a whitelist
  of each agent's placeholder string, which fails **both** ways — an unrecognised placeholder refuses
  a safe send, and a draft that happens to look like a placeholder reads as empty — and rots every
  time an agent changes its prompt chrome.
- **`statusChangedAt`** — epoch seconds recording when a session's status was last written.
  `tree --json` reports `"status":"active"` with no age, so nothing can tell a working agent from one
  whose hook died forty minutes ago. `C:\Users\boris\AI\state\agents.json` keeps a `last_seen` per
  agent purely to answer this — shadow state that exists because the tree cannot.
- **`agwintermctl version`** — which app is serving the pipe, **and the resolved path of the CLI that
  ran**. This machine has `agwintermctl.exe` in the install directory and in two source build trees,
  none of them on `PATH`. "Which binary did I just run, and which app did it reach" is currently
  unanswerable without `Get-Command` and a guess.

`ping` already returns `"agwinterm " + AppVersion()` (`ControlServer.cs:107`), so the server half of
`version` is a presentation change, not new plumbing.

## Context (from discovery)

- `src/Agwinterm.Pty/ControlServer.cs:107` — `ping`, the model for a bare-string reply.
- `src/Agwinterm.Pty/ControlServer.cs:132–260` — the verb switch. Targetless verbs are dispatched in
  the first block; verbs that need a resolved `ISession s` are in the second (`case "session.status"`
  at :253 is the shape to copy).
- `src/Agwinterm.Pty/ControlServer.cs:282` — `HandleTree`, which builds the tree JSON by hand with a
  `StringBuilder`; per-session fields are appended around :298–325, most of them **omitted when
  default** (`if (n.Flagged) …`).
- `src/Agwinterm.Pty/ISessionHost.cs:8` — `record SessionSnapshot(...)`, the tree's session node.
- `src/Agwinterm.Win32/Program.ControlHost.cs:162` — `Resolve(target)`: `null`/`"active"` → the
  active surface, then **any pane by id or id-prefix**, then a session by name. A pane id already
  resolves to that pane's `ISession`.
- `src/Agwinterm.Win32/Program.ControlHost.cs:174` — `Tree()`, which builds each `SessionSnapshot`
  and aggregates per-pane status through `AggStatus(s)` (`Program.Services.cs:122`, severity order
  Blocked > Completed > Active > Idle).
- `src/Agwinterm.Pty/TerminalSession.cs:47` — `SetStatus`, which already computes a `changed` flag.
- `src/Agwinterm.Pty/TerminalSession.cs:70` — `SyncRoot`; `:306` `MutateLocked`; `:330` `SnapshotRow`
  (`lock (_sync) return Emulator.DumpRow(row)`) — **the shape a cursor read should copy**.
- `src/Agwinterm.Pty/ServerSession.cs:79,90` — the pty-host client session. It holds its **own
  replica** `ITerminalCore`, fed from the host's raw ConPTY stream, and locks the same way
  (`:304 SnapshotRow`). So a cursor read needs **no pty-host protocol change**.
- `src/Agwinterm.Core/ITerminalCore.cs:18,19` — `CursorRow` / `CursorCol`, present on both the
  managed `TerminalEmulator` and `RustTerminalCore`.
- `src/Agwinterm.Ctl/Program.cs:62` — the CLI's verb table (`case "ping"`), and `:112` the `session`
  sub-verb block.
- `src/Agwinterm.Pty/AgentSkill.cs:72,80,96` — the SKILL.md the app writes for agents. It documents
  **only** the verbs it implements, deliberately.
- `tests/Agwinterm.Pty.Tests/FakeSessionHost.cs:9` — the host double used by `ControlApiTests` and
  `ControlServerTests`.
- `qa/product.md`, `qa/selection.md` — the markdown QA cases and how they are driven.

## Constraints

- **Do not touch `tests/conformance/control-api.json`.** That file is the *shared floor* both
  products stand on, and agliteterm's CI checks its copy against it every build. Adding a step for a
  verb agliteterm does not implement turns lite's CI red until batch P8 lands — weeks, not hours.
  The conformance steps for these three verbs are **P8's** work, agwinterm-first as always. This is a
  named non-goal, not an oversight.
- **No pty-host protocol change.** If the design starts to need one, stop and say so: `ServerSession`
  keeps a replica emulator precisely so read-only questions can be answered locally.
- **Read-only.** No verb in this batch may mutate a session, the restore file, or the UI.
- Follow the existing reply envelope: `Ok(...)` / `Err(...)`, and `OkRaw` for a value that is
  *already* JSON (double-encoding is a bug this repo has shipped once before).
- Cross-cutting safety rules for anything that drives a live app are in `qa/product.md` and apply in
  full: sandbox instance (`--pipe <name>` **and** `--app-id <sandbox>`), never `keybd_event` /
  `SendInput`, `PrintWindow` never `CopyFromScreen`, never the real user profile.

## Testing Strategy

- **Unit tests**: `tests/Agwinterm.Pty.Tests/` against `ControlServer` + `FakeSessionHost`. This is
  where verb shape, targeting and refusals are pinned.
- **QA cases**: markdown in `qa/`, driven by the `ui-qa` skill against a sandbox instance. This is
  where "the number is *right*", not merely well-shaped, gets checked — a cursor column that is
  always `0` passes every unit test.
- Each task's checks must pass before the next task starts.

## Progress Tracking

- Mark completed items with `[x]` immediately when done
- Add newly discovered tasks with ➕ prefix
- Document issues/blockers with ⚠️ prefix
- Update plan if implementation deviates from original scope

## Implementation Steps

### Task 1: `surface.cursor`
- [x] add a thread-safe cursor read to `ISession` — `(int Row, int Col) SnapshotCursor()`, documented
      as a snapshot under the same lock `SnapshotRow` uses
- [x] implement it in `TerminalSession` (`lock (_sync) return (Emulator.CursorRow, Emulator.CursorCol)`)
      and in `ServerSession` identically, reading its replica emulator
- [x] add `case "surface.cursor"` to the **resolved-session** block of `ControlServer.Dispatch`,
      returning the **column only, as a bare integer** — agterm's shape, so an agent written against
      either product gets the same reply. Row is deliberately not reported; a JSON object here would
      diverge from agterm for no caller we have
- [x] targeting: a **pane id** reports that pane (`Resolve` already does this); a **session** id or
      name reports its **focused** pane. Document that in the verb's comment — a cursor is a per-pane
      thing, and the focused pane is the only non-arbitrary answer for a session-wide target
- [x] add `surface cursor [--target <id>]` to `src/Agwinterm.Ctl/Program.cs`, printing the bare integer
- [x] tests in `tests/Agwinterm.Pty.Tests/`: a known column is reported; the column moves after the
      emulator is fed text; an unknown target returns `ok:false`; column `0` on a fresh session is
      reported as `0` and **not** confused with "no answer"
- [x] a test that pins the reply is a bare integer, not `{"col":N}` — this is the contract P8 mirrors
- [x] run the .NET suite — must pass before task 2

### Task 2: `statusChangedAt`
- [x] record the timestamp on **every** `SetStatus` call, not only when `changed` is true. The caller
      asking is "is this agent's hook still alive", and a hook re-asserting `active` every 30 s is
      exactly the liveness signal that matters — collapsing repeats would report the wrong age. Put
      that reasoning in a comment; it is the one decision here someone will later think is a bug
- [x] expose it on `ISession` as epoch seconds; implement in `TerminalSession` and `ServerSession`.
      Initialise it at construction, so a session whose status was never written reports its own age
      rather than `0` or null
- [x] add `StatusChangedAt` to `SessionSnapshot` (`ISessionHost.cs:8`) as an optional parameter, in
      keeping with the record's existing shape
- [x] fill it in `Program.ControlHost.Tree()` from **the pane whose status won `AggStatus`**, so the
      age describes the status actually shown. Where several panes tie at the winning severity, use
      the most recent — a session is "as fresh as" its freshest contributor
- [x] emit `"statusChangedAt":<epoch seconds>` in `HandleTree` **always**, not only when non-default:
      a consumer that has to distinguish "absent" from "old" gains nothing from the omission
- [x] tests: a session that has had a status written reports a plausible epoch; writing the *same*
      status again moves the timestamp; the winning pane's timestamp is the one reported when panes
      disagree; the field is present for an idle session
- [x] run the .NET suite — must pass before task 3

### Task 3: `agwintermctl version`
- [x] add a `version` verb to `src/Agwinterm.Ctl/Program.cs` reporting two things on separate,
      greppable lines: the **CLI** (its version and `Environment.ProcessPath`, the resolved path of
      the executable that actually ran) and the **app** serving the pipe (from `ping`, plus the pipe
      name it tried)
- [x] **exit 0 and still print the CLI half when no app answers**, marking the app half unavailable.
      A diagnostic that fails when the thing being diagnosed is down is the one case it exists for
- [x] `--json` for the machine-readable form, matching how `tree` offers one
- [x] tests: the CLI half is produced without any app running; the pipe name appears in the output;
      `--json` parses
- [x] run the .NET suite — must pass before task 4

### Task 4: QA cases and the agent-facing docs
- [ ] add `qa/control-read.md` with cases for all three, following `qa/selection.md`'s style — prose
      for the steps, machinery for the oracle
- [ ] `surface.cursor` case must prove the number **tracks reality**: feed a known prompt into a
      sandbox pane, assert the column matches, type more, assert it moved. A case that only asserts
      "an integer came back" is the vacuous pass this QA system exists to prevent
- [ ] `statusChangedAt` case: set a status, read the tree, assert the age is small; wait, re-assert
      the same status, assert the age went **back down**
- [ ] `version` case: assert it names the sandbox instance's pipe, and assert the no-app path exits 0
- [ ] add the three verbs to `src/Agwinterm.Pty/AgentSkill.cs` — it documents only what the app
      implements, and an agent that cannot see a verb will not use it
- [ ] add them to the `agwintermctl` section of `README.md` (`:207`)
- [ ] run the QA cases against a sandbox build — must pass before task 5

### Task 5: [Final] Verify acceptance criteria
- [ ] verify every requirement in Overview is implemented
- [ ] verify the edge cases: a target that names a multi-pane session; a pane id prefix; a session
      that has exited; the alt screen (a column is a column — assert no special-casing crept in)
- [ ] confirm `tests/conformance/control-api.json` is **unchanged** (`git diff` must be empty for it)
- [ ] run the full .NET suite, the Rust suite, and `tools/check-abi.ps1` — nothing in this batch
      should move the core ABI, and a change there means something went wrong
- [ ] tick items 1, 2 and 8 in `docs/agterm-parity.md`, moving them to the **Closed** table with the
      PR number, and note in `docs/lite-parity.md` that P8 owes the mirror
- [ ] **correct the factual error** in `agterm-parity.md`'s "Where we are ahead": it claims
      `session.metrics` (live cell and pixel geometry) exists. No such verb is on `main` — it lives
      on the unmerged local branch `feat/image-frameshm-control`. Either say so or drop the claim

## Technical Details

- **Why a bare integer.** agterm's `surface.cursor` prints a bare column, and half the value of this
  batch is that a script written for agterm's cookbook works here unchanged. A richer reply is easy
  to add later and impossible to take back.
- **Why the replica is good enough.** `ServerSession`'s emulator is fed the same ConPTY bytes as the
  host's, so it converges to the same cursor; it can lag by the pipe latency. For "is the composer
  empty before I type into it" that is fine, and the alternative — a synchronous round trip to the
  pty-host on every read — buys accuracy nobody needs at a cost everybody pays.
- **Written vs changed.** `SetStatus` already distinguishes them for its `StatusChanged` event, and
  that event should keep its current behaviour (fire on change only) — the repaint it drives has no
  reason to run on a no-op write. Only the timestamp is written unconditionally.
- **Epoch seconds, not a formatted date.** Matches agterm, survives JSON, and leaves the display
  choice to the caller.

## Post-Completion

*Informational — no checkboxes*

- `AI/bin/peer-chat.py` can drop its placeholder whitelist in favour of the caret column, and
  `AI/state/agents.json` can drop `last_seen`. Both live outside this repository and are follow-on
  work, not part of this PR.
- P8 mirrors all three verbs into agliteterm and adds the conformance steps, agwinterm-first.
- Review: **revmux**, two rounds minimum. The second round is historically where criticals surface.
