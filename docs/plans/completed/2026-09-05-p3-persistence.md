# P3 — persistence: `session.context` and `restore.capture`

Batch **P3** of the parity programme in
[2026-09-03-parity-batches.md](2026-09-03-parity-batches.md). Closes items 1 and 5 (the
`restore.capture` half) of [agterm-parity.md](../agterm-parity.md).

## The restore-format rule this batch sets

P3 is alone in its wave because it is the first change to the per-window restore file
(`%LOCALAPPDATA%\<app-id>\windows\<window-id>.json`) in this backlog, and P4 (split axis) and P7 will
follow it. The rule, stated once here so the later batches inherit it:

- **Additive keys only, no version field.** `AppState` / `SessionState` / `PaneState`
  (`src/Agwinterm.Win32/Program.Services.cs:1113-1145`) are plain POCOs round-tripped by
  `System.Text.Json` with nothing but `WriteIndented` set (`:1146`). An unknown key is ignored on read.
  `RestoreCommand`, `AgentResume` and `SidebarWidth` were all added this way and none of them bumped
  anything — there is no version on this file to bump (the *index* `windows.json` has one,
  `Program.cs:78`; the tree file does not). P3 adds **one** key, `SessionState.Context`, and reuses
  `PaneState.Command` for the captured slot rather than inventing a second one.
- **The failure mode is write-back loss, not a crash.** A 0.17.11 build (or a second window of a mixed
  install) reading a P3 file drops `Context` on read and writes the file back without it on its next
  save. JSON tolerance protects against the parse error; nothing protects against the downgrade, and
  this batch does not try to — it is the same exposure `RestoreCommand` already has. Say so in the
  format comment, once.
- **Every loaded value is validated, not trusted.** `SidebarWidth` is the precedent
  (`Program.Services.cs:1492-1494`: out of range → default). A `Context` that fails the rules Task 1
  sets (length, control characters) is dropped on load, not displayed.
- **The format gets a test that can see it.** Nothing under `tests/` references `AppState` or
  `TryRestoreState` today; the only persistence assertion in the repo greps the file for
  `"SidebarWidth": 320` (`tests/integration/win32-control.ps1:387-390`). Task 3 moves the POCOs and the
  serializer options into `Agwinterm.Pty` so a round-trip test exists, the way `BufferPersist` has one.
- **lite's mirror is a new line type**, appended after the `S` lines the way `P` was
  (`agliteterm/src/main.cpp:2711-2714`): its reader ignores unknown line types by policy
  (`:7831-7834`). That is P3-lite's obligation, not this batch's; noted so the JSON key and the line
  type carry the same name and the same validation rules.

## Overview

Two items, one theme: **state an agent sets should survive the app it set it in.**

- **`session.context`** — free text per session, set over the API, shown dimmed beside the name in
  the title bar and the sidebar row, carried in `tree --json`, and restored after a restart. Today
  "what is this pane for" is guessable only from a name that also has to be short. The value is one
  line: newlines and other control characters are **refused** (the #213 class — a control byte in
  the title bar is not a context, it is a rendering accident), blank is refused unless the caller
  says `--clear`, and there is a length ceiling so the title bar and the sidebar row have something
  they can fit. The reply names the session and the value in effect.
- **`restore.capture`** — capture the foreground command of every real pane (or one, with
  `--target`) into its restore slot **now**, and report per pane what was captured. Today the capture
  happens exactly once, in `WM_DESTROY` (`Program.WndProc.cs:595-598`), which a crash, a
  `Stop-Process`, a power loss or a missed update-quit never reaches — so the one restart where a
  captured command matters most is the one where the slot is guaranteed empty. Worse than empty: every
  ordinary save writes `""` into it (`Program.Services.cs:1374`), because the captured command has
  **no in-memory field at all** — it exists only as a local dictionary inside `SaveState`. The verb
  therefore needs a durable `Pane.CapturedCommand`, and the quit-time capture moves onto the same
  field so there is one path, not two.

Both replies are structured (`OkRaw`) and both values are readable back from `tree --json`, because
P2 spent a round learning that a write without a read-back is only honest in the instant of the call.

## Context (from discovery)

Session identity and the name it already has:

- `src/Agwinterm.Win32/Program.cs:320-346` — `Ses`, with `Name` and `CustomName` (null = show the
  cwd/OSC title). **`Context` goes here**, beside `CustomName`; it is per session, not per pane.
- `session.rename` end to end, the shape `session.context` copies: CLI `src/Agwinterm.Ctl/Program.cs:15`
  (usage) and `:167-169`; server `src/Agwinterm.Pty/ControlServer.cs:242` (host-verb block);
  `ISessionHost.cs:138`; host `Program.ControlHost.cs:406-412` (`Post(() => {…}); return true;` — one
  of the verbs #228 item 5 names); fake `FakeSessionHost.cs:261` (sets `Name` only); test
  `ControlApiTests.cs:127-133`; conformance step `tests/conformance/control-api.json:86-95`.
- Where the name is consumed — every surface the context must be considered for: sidebar row
  `Program.Chrome.cs:187` (`DrawSessionRow` `:143-206`; row height once per paint at `:62-64`, and
  everything downstream — click, `RowAt`, rename EDIT, drag, UIA — reads `_sidebarRows`); title bar
  `Program.Services.cs:294-305` inside `DrawTitleBar` (`:256`), with a pill strip already at
  `pillX = titleX + titleW + 10f` (`:306-317`, `BROADCAST` / `READ-ONLY`); `SessionDisplayName`
  `:212-231`; session palette `Program.Chrome.cs:749-764` with a ready-made dimmed `Secondary` line
  (workspace · cwd today) and a `Search` field; Ctrl+Tab MRU `Program.Render.cs:718`; UIA
  `Program.cs:911`; `window.state` `Program.ControlHost.cs:197`; **undo-close**
  `Program.Sessions.cs:1275-1277` (`record ClosedSession`), captured `:1292-1295`, replayed `:1355`,
  `:1372` — context must ride here or `session reopen` loses it.
- The caption is self-drawn (`WM_NCCALCSIZE`, `Program.Chrome.cs:1289`); `SetWindowTextW` is declared
  (`Win32.cs:425`) and never called. There is no OS title to update, only the Direct2D row.
  `TitleBarH` is 40/30/0 by toolbar mode (`Program.cs:142`): a second caption line is not viable, a
  dimmed suffix is.
- `SessionSnapshot` (`ISessionHost.cs:10-14`) — all-optional positional record; `string? Context = null`
  at the end is source-compatible with both hosts and `SingleSessionHost` (`:326`). `HandleTree`
  (`ControlServer.cs:395-449`) emits optional fields only when set.

The restore file:

- Written by `SaveState(bool captureCommands = false)` (`Program.Services.cs:1323`): no-op while
  `_restoring`; per-session record `:1352-1366`, per-pane `:1385`; atomic publish `:1398-1401`.
  Read by `TryRestoreState` (`:1473`); a parse failure renames the file `.bad` (`:1481-1486`) — that is
  the only "bad file" behaviour and an unknown key does not reach it. `StatePath` `:1156`.
- **The capture**: `CaptureForegroundCommands` (`:1280-1319`) — one PowerShell + CIM snapshot for all
  pids, most recently started non-denylisted child per shell pid, `restore-denylist.conf` via
  `LoadDenylist` (`:1240ff`), `StripExe` (`:1267`). Default timeout 4 s; the two non-quit callers pass
  **15 s** because a cold CIM start can exceed 4 s. Called with `captureCommands: true` **only** from
  `WM_DESTROY`; the other 43 `SaveState()` calls write `Command = ""` (`:1374`). Gated by
  `_config.RestoreCommands`, default `false` (`src/Agwinterm.Core/TerminalConfig.cs:41`).
- Replay at restore (`:1565-1621`): agent resume wins, then the pin (`RestoreCommand`, `:1584-1597`,
  independent of the toggle), then the capture (`:1599-1621`, **toggle-gated**, denylist re-checked,
  `"& "` prefixed), all typed 2.5 s after the shell starts. `PanesOf` (`:1232`) is real panes only —
  covers are never captured, matching `SessionRestore`'s refusal.
- Threading precedents: `RestartAllClaudeSessions` (`Program.Sessions.cs:1047-1072`) runs the CIM query
  **off** the UI thread and posts the writes — **copy this**. `AdoptClaudeSessions` (`:1086-1128`) is
  reached through `InvokeOnUi` and blocks the UI thread for up to 15 s — **do not copy this**. The
  P2 hop is `InvokeOnUiQueued` (`Program.Sessions.cs:1694`: FIFO with posted removals, bounded by a
  failed `Post`, `_uiGone`, 15 s). `ChildProcessId` is already on `ISession` (`ISession.cs:26`) and
  already crosses the host protocol (`PtyHostServer.cs:217`, `:321`) — no protocol change.

Server and tests:

- `ControlServer.cs` — `Ok` `:1173`, `OkRaw` `:1174`, `Err` `:1175`; `RefusePrefix` stripped at
  `:305-307` and `:222-224`; readers `GetString` `:1163` / `GetInt` `:1168` (silently defaults) /
  `GetBool` `:1170`; strict readers `TryNum` `:1043`, `TryOverlaySize` `:1070`, `TrySidebarWidth`
  `:1123`. Dispatch blocks: app `:173-192`, **host verbs** `:199-344` (`session.rename` `:242`,
  `restore.clear` `:265`, `session.restore` `:316`), session-resolved `:346-372`.
- `src/Agwinterm.Pty/SidebarWidths.cs` — the shape for "rules + refusal wording in one class the server
  can apply before the host is reached and the fake can exercise"; `StdinText.cs` for a reader.
- `tests/Agwinterm.Pty.Tests/FakeSessionHost.cs` — `Sess` `:11-63`, `RestorePins` `:36` surfaced
  through `Tree()` `:150-160`, `CoverPanes` `:44-53`, resolvers `Find` `:83` / `FindWs` `:90` /
  `FindPane` `:112` / `FindCover` `:123`. **No process notion** — no pids, no children.
  `ControlApiTests.cs` helpers `:13-41`; `SessionRestoreTests.cs` is the one-file-per-item precedent;
  `tests/Agwinterm.Core.Tests/BufferPersistTests.cs` the serializer round-trip precedent.
- **No restart harness.** `tests/ui/lib.ps1` `Start-Sandbox` (`:133-164`) always passes `--no-restore`
  and `Stop-Sandbox` (`:186-190`) deletes the app dir; `Connect-Sandbox` (`:172`) attaches to a running
  instance only. The finished model is `agliteterm/test/restore-matrix.ps1` (`Cell` `:129`, `Start-Lite`
  `:71`, `Stop-Lite -Kill` `:81`, `Signature` `:100`, `Restart-Cell` `:449`).
- Docs surfaces: `AgentSkill.cs:93` (`session rename` line; `:83-89` is `session restore`, the most
  recent entry), `README.md:213-234` (example block) and `:245-283` (the prose list where P1/P2 added
  paragraphs), CLI usage `Ctl/Program.cs:10-35`.
- Nothing context-like exists already: no note/label field on `Ses`, `Pane`, `SessionSnapshot` or
  `SessionState`. `session.hud` (parity item 7) is a transient overlay, P13 — not this.

## Constraints

- **Do not touch `tests/conformance/control-api.json`.** The `session context` step (set, then the
  `tree --json` read-back) and the `restore capture` step land in a sibling PR after this merges and
  before the release — #223 after #221, #229 after #226.
- **Additive only, everywhere**: one new key in the restore file, `context` and `capturedCommands`
  on the tree, a new optional parameter at the end of `SessionSnapshot`. Nothing renamed or removed.
  `PaneState.Command` is reused, not replaced.
- **No pty-host protocol change, no ABI change.** `tools/check-abi.ps1` stays at v18 both sides.
- **The UI thread is never blocked on CIM.** The capture runs on the pipe thread and lands its writes
  through `InvokeOnUiQueued`. A verb that freezes the window for seconds is P2's defect class one
  layer down.
- **Refusals leave the world untouched**: a refused context leaves the old one in place and saves
  nothing; a refused capture target captures nothing for anyone.
- **Refusal logic lives in `Agwinterm.Pty`** where the fake can exercise it (`SessionContexts`, the
  capture reply formatter). `OverlayTargetRefusal` is the pile not to add to.
- **#228 in the blast radius.** Item 5 (`Post(...); return true;`) names `SessionRename`, the line the
  new verb is written beside — the new verbs read the hop's result, and `SessionRename` is fixed in the
  same commit. Item 3 (the fake's `Find` reaching covers where the app's does not) is the resolver
  `session.context` lands on — split it as the issue says, or the verb behaves differently against
  the fake than against the app. Items 1, 2, 4 and all of #227 are `SessionOverlay`'s
  (`Program.ControlHost.cs:679-760`): **do not touch them**; note the file overlap in the PR so the
  #227/#228 follow-up does not collide.
- Cross-cutting safety rules from `qa/product.md` apply in full — sandbox instance (`--pipe <name>`
  **and** `--app-id <sandbox>`), never `keybd_event` / `SendInput`, `PrintWindow` never
  `CopyFromScreen`, never the real user profile. **This batch is the first that must relaunch a
  sandbox *without* `--no-restore`** (`qa/product.md:39` states the contract as always passing it);
  that is safe only under a sandbox app-id, and the harness must refuse to run under any other.

## Testing Strategy

- **Unit tests** in `tests/Agwinterm.Pty.Tests/`, one new file per item plus one for the format.
  Every refusal gets two tests: it is reported, *and* nothing changed.
- **Format round-trip** in `tests/Agwinterm.Pty.Tests/RestoreStateTests.cs` once the POCOs are
  reachable: a value survives serialize → deserialize; an old-shape file (no `Context`) loads with
  null; a file with an unknown key loads; an out-of-rules `Context` is dropped on load.
- **Restart round-trip** in `tests/integration/restore-roundtrip.ps1` against a sandbox app-id, two
  cells: graceful close (`WM_CLOSE`) and **kill** (`Stop-Process`). Both relaunch without
  `--no-restore` and assert the context and the captured command in `tree --json` afterwards.
- **QA cases** in `qa/persistence.md`, driven by the `ui-qa` skill: the dimmed suffix is actually
  visible in the title bar and the row (a `PrintWindow` capture), the palette shows it, `session
  reopen` brings it back.
- **Integration**: `tests/integration/win32-control.ps1` gains the verb checks that do not need a
  restart (set / read-back / refusals / capture reply shape).
- Each task's checks must pass before the next task starts.

## Progress Tracking

- Mark completed items with `[x]` immediately when done
- Add newly discovered tasks with ➕ prefix
- Document issues/blockers with ⚠️ prefix
- Update plan if implementation deviates from original scope

## Implementation Steps

### Task 1: `session.context` — rules, verb, host, fake, CLI
- [x] add `src/Agwinterm.Pty/SessionContexts.cs` modelled on `SidebarWidths.cs`: `MaxLength` (200),
      `Validate(string?)` returning the refusal text or null — refuses any character below U+0020
      or U+007F..U+009F (naming the offset, as `StdinText` does), refuses blank, refuses over-length
      naming the ceiling; `Normalize` trims leading/trailing whitespace. One class, one wording, used
      by the server before the host is reached **and** by `TryRestoreState` on load
- [x] `ISessionHost.SessionContext(string target, string? context)` returning `string` — `null`
      clears; `RefusePrefix` for "no session" (the same wording `session.rename` uses); on success
      the JSON the server emits raw: `{"session":"<id>","context":"<text>"|null}`
- [x] server: `session.context` in the **host-verb block beside `session.rename`**
      (`ControlServer.cs:242`); `clear:true` in args clears; text and `clear` together are refused
      (two sources for one field — P2's `--stdin` rule); validation before the host call
- [x] host (`Program.ControlHost.cs`, beside `SessionRename`): resolve with the same session
      resolver rename uses, apply through **`InvokeOnUiQueued`** so the reply carries the value
      after it was applied and a failed post is a refusal, then `RequestRedraw(); SaveState();`.
      **In the same commit make `SessionRename` return `Post(...)`** (#228 item 5, for that verb)
- [x] `SessionSnapshot` gains `string? Context = null` (last position); `Tree()` in both hosts fills
      it; `HandleTree` emits `"context"` **only when set**
- [x] fake: `Sess.Context`, a real `SessionContext`, surfaced in `Tree()`; **split the fake's
      resolvers as #228 item 3 says** (cover fall-through on `Resolve` and a cover-aware session
      helper; `Find` session-only like the app's `Program.Sessions.cs:1658-1668`), rewording the
      field comment — the new verb must resolve identically against fake and app
- [x] CLI (`Ctl/Program.cs`): `session context <text> [--target ID]`, `session context --clear
      [--target ID]`, `session context --stdin` through `StdinText` (one trailing newline stripped;
      an embedded newline is then refused by the rules above, deliberately — the context is one
      line); text with `--stdin` or with `--clear` refused client-side, sending nothing
- [x] tests `tests/Agwinterm.Pty.Tests/SessionContextTests.cs`: set → reply names session and text →
      tree carries it; clear → reply `context:null` → tree omits it; unknown target refused and no
      session changed; blank refused and the old value stands; a control character refused with its
      offset and the old value stands; over-length refused naming the ceiling; text+clear refused;
      whitespace normalised; **a cover pane id is refused by `session.context` exactly as by
      `session.rename`** (the #228-3 test)
  - ⚠️ discovery correction: the app's `session.rename` does NOT refuse a scratch/overlay cover id —
    `FindSesForTarget` → `FindControlPane` → `FindPaneBy`'s cover tail lands it on the session it
    covers (a CLI inside a scratch pane inherits the cover's id, and "this session" is the one under
    it). `session.context` resolves identically (asserted for both verbs in one test), and the
    session-only `Find` behind `session.close` / `select` refuses the same id — that is the #228-3 split
    the fake now mirrors. Only the window-level quick terminal (no session) is refused, in the app;
    the fake does not model it.
- [x] run the .NET suite — must pass before task 2

### Task 2: the context on every surface
- [x] `Ses.Context` (`Program.cs:320-346`), beside `CustomName` (added in Task 1 — the host needs the field)
- [x] title bar: a dimmed run in `_uiSmall` / `ChromeDim` after the title, **before** the pill strip
      (`Program.Services.cs:306-317`), ellipsized within the same `titleAvail` budget so the bell and
      the right button group never move; drawn only when set. Say in a comment why it is a suffix
      and not a second line (`TitleBarH` 40/30/0)
- [x] sidebar row (`DrawSessionRow`, `Chrome.cs:143-206`): the same dimmed suffix after the name in
      the **same** row, clipped to the row's name rect; no per-row height change (state why: every
      consumer of `_sidebarRows` assumes one `rowH`, and the palette is where the long form lives)
- [x] session palette (`Chrome.cs:749-764`): `Secondary` becomes `"<context>  ·  <workspace>  ·
      <cwd>"` when set, and `Search` includes the context so a palette query finds a session by it
- [x] undo-close: `ClosedSession` (`Program.Sessions.cs:1275`) carries the context, captured at
      `:1292-1295` and replayed at `:1355` / `:1372`
- [x] `window.state`'s `ActiveSession` and the UIA `Name` stay the name — the context is not a name
      (comment at the UIA line)
- [x] `RequestRedraw()` after every mutation; the inline rename EDIT (`Chrome.cs:328`, `:376`) is
      untouched — rename edits the name, not the context
- [x] run the .NET suite, then a sandbox smoke: set a context, `PrintWindow` the title bar and the
      row, attach the capture to the task note
  - ➕ verified 2026-09-05 by a sandbox smoke (`.ralphex/progress/p3-task2-smoke.ps1`, gitignored with
    its captures `p3-task2-smoke-{titlebar,sidebar,palette,palette-query,cleared}.png`): set → reply and
    `tree --json` carry the text; the title bar shows `~  build the persistence batch` dimmed before the
    bell; the row shows `session 1  build the persistence…` ellipsized before the dot; the palette line
    reads `build the persistence batch  ·  workspace 1  ·  C:\Users\boris` and the query `persist` finds
    the session by it; `session rename` keeps the context; `window.state` answers the name only;
    close + Ctrl+Shift+R brings the context back on the same id; `--clear` empties the reply, the tree
    and the surfaces. 10/10 checks, twice. No unit test reaches the Win32 draw path — the smoke is the check.
  - ⚠️ harness note, not a product defect: a posted Ctrl+P chord (`[AgwUi]::Chord`) leaves a translated
    `p` in the palette query — the sandbox's own loop translates the key without the Ctrl the real key
    path carries. The smoke sends one Backspace first; the palette's WM_CHAR handling is untouched.
  - title bar layout: title and context share ONE `titleAvail` budget — a long title yields at most 40% of
    it to the context, both ellipsize inside their share, the bell follows the run (clamped as before)
    and the right group never moves. A title that fills the budget shows no suffix; the palette carries it.
  - row suffix draws in its own `_sidebarCtx` format (`_sidebarSmall`'s size + "…" trimming) so the
    counts stay untrimmed and the suffix never hard-clips mid-glyph against the dot.

### Task 3: the restore format — `Context` persists, and the format gets a test
- [x] move `AppState`, `WorkspaceState`, `SessionState`, `PaneState` and the serializer options out of
      `Program.Services.cs:1113-1146` into `src/Agwinterm.Pty/RestoreState.cs` (public POCOs +
      `RestoreState.Serialize` / `TryDeserialize` wrapping today's `_stateJson`), **byte-for-byte the
      same output** for an unchanged tree — verify by saving a state before and after the move and
      diffing the files
  - ➕ verified 2026-09-05 against files the PREVIOUS builds wrote rather than a fresh save (a fresh
    save's guids and cwds differ run to run, so two saves never diff cleanly): every restore file under
    the release and dev app dirs (37 files, 0.17.x builds up to today's 12:55 save) was read with
    `RestoreState.TryDeserialize` and written back with `RestoreState.Serialize` — 37 same, 0 different
    (`.ralphex/progress/p3-task3-state-diff.txt`, gitignored; the throwaway test that produced it is
    not committed). The index (`windows.json`) shares `RestoreState.Json` so its output is unchanged too.
- [x] `SessionState.Context` (`string?`); saved from `Ses.Context` at `:1352-1366`; loaded in
      `TryRestoreState` **through `SessionContexts.Validate`** — a value that fails the rules is
      dropped, not shown (the `SidebarWidth` precedent at `:1492-1494`)
  - the load goes through `RestoreState.LoadContext` (control check on the raw value, `Normalize`,
    `Validate` — `SessionContexts.TryNormalize`, the verb's own path) so the format test can reach it;
    `TryRestoreState` is not testable from `tests/`
  - `Context` carries `[JsonIgnore(WhenWritingNull)]`: a session without one writes NO key, so a tree
    without contexts still saves the exact bytes 0.17.11 saves (the 37-file check above ran with the
    key already added) and the write-back comparison below is an equality, not a "differs only by"
- [x] the format comment on `RestoreState` states the rule from the top of this plan: additive keys,
      no version, unknown keys ignored, older builds drop unknown keys on write-back
- [x] `tests/Agwinterm.Pty.Tests/RestoreStateTests.cs`: round-trip preserves `Context`; a file
      written without the key deserializes with `Context == null` and every other field intact; a
      file with an unknown key deserializes; a serialized state with `Context = null` is what a
      pre-P3 build would write for that field (the write-back comparison, stated as such)
  - 13 tests: the pre-P3 fixture round-trips byte-for-byte and the in-memory tree serializes to it;
    unknown keys at every level are read and (visibly) dropped on write-back; missing keys take their
    defaults; only broken JSON is a bad file; `LoadContext` drops what the verb refuses (newline, tab,
    ESC, NEL, blank, over the ceiling) and normalises what it accepts; a file with a newline in
    `Context` parses, carries the raw value, and loads as none
- [x] run the .NET suite — must pass before task 4
  - Pty 519/519 (incl. the 13 new), Core 246/246; Win32 host full rebuild (`--no-incremental`) 0 warnings

### Task 4: `restore.capture` — a durable slot, one capture path, a verb that reports
- [x] `Pane.CapturedCommand` (`string?`, `Program.cs:288-317`, beside `RestoreCommand`). `SaveState`
      writes `p.CapturedCommand ?? ""` into `PaneState.Command` **on every save**; the
      `captureCommands: true` path becomes "run the capture, write the field, then save" — one
      field, one writer, and the `WM_DESTROY` call keeps its behaviour (a fresh capture at quit
      overrides an earlier checkpoint, including to empty when nothing is running). Delete the
      local `cmdByPid` shape once nothing else reads it
  - the quit-time path is `CaptureCommandsIntoPanes` (UI thread, toggle-gated as before); `cmdByPid`
    is gone from `SaveState`. `CaptureForegroundCommands` is now a wrapper over
    `TryCaptureForegroundCommands(…, out map)`, which says whether the query RAN — the two Claude
    callers keep the old shape, the verb uses the honest one
- [x] `TryRestoreState` loads `PaneState.Command` back into `Pane.CapturedCommand` so a restored
      pane's slot is readable before it is replayed
  - and so the `SaveState()` at the end of the restore no longer writes `""` over it
- [x] `ISessionHost.RestoreCapture(string? target)` returning `IReadOnlyList<CapturedPane>` (a Pty
      record: `PaneId, SessionId, string? Captured`) or a refusal for an unknown target; `null`
      captured = the shell had no non-denylisted child, which is the honest answer and distinct
      from a failed query
  - ➕ returns `RestoreCaptureResult(Panes, ReplayOnRestore, Refusal)` rather than a bare list: a list
    cannot carry the refusal, and the toggle is the host's to report (the fake reads it from
    `config.set restore-commands`, so a test drives it the way a caller does). A failed / timed-out
    query is its own refusal (`RestoreCaptureReply.QueryFailed`), never an all-null answer
- [x] host: snapshot real panes + pids under `lock (_workspaces)` on the pipe thread (the way `Tree()`
      does); run `CaptureForegroundCommands` **on the pipe thread** with the 15 s timeout the
      non-quit callers use; land every `CapturedCommand` write plus one `SaveState()` in a single
      `InvokeOnUiQueued` hop; a failed hop is a refusal (nothing captured, nothing saved). Never
      through `InvokeOnUi`. `--target` resolves with the resolver `session.restore` uses (a pane; a
      session id is its first pane); an unknown target is a verb-specific refusal, not `"no session"`
  - `"active"` (only reachable as an explicit `--target active`) = the active session's active pane in
    both hosts; null / `""` = every real pane. A pane closed between the snapshot and the hop is
    dropped from the reply, not written to; the save runs only when something landed
- [x] the verb ignores the `restore-commands` toggle for the **capture** — the pin ignores it too,
      and a no-op verb on a default install is the silent-success class — but the reply carries
      `"replayOnRestore": <toggle>` so the caller knows whether the slot will be typed back
- [x] server: `restore.capture` in the **host-verb block beside `restore.clear`**
      (`ControlServer.cs:265`); reply built by `src/Agwinterm.Pty/RestoreCaptureReply.cs` and emitted
      with `OkRaw`: `{"captured":<n non-null>,"replayOnRestore":bool,"panes":[{"pane","session",
      "captured":string|null}]}`; a comment on the record says the shape is ours, not agterm's
      (the parity entry is one sentence)
- [x] `SessionSnapshot` / `HandleTree`: `capturedCommands` per session, emitted only when any pane
      has one — the read-back, exactly as `restoreCommands` got in P2
  - `AppendRestoreCommands` became `AppendPaneMap(key, values, paneIds)` and writes both maps
- [x] fake: a per-pane `Captured` table the test seeds, `RestoreCapture` reading it, covers refused
  - two tables, because the app has two things: `Foreground` (seeded — what the shell is running, the
    stand-in for the CIM snapshot) and `Captured` (the slot the verb writes and the tree reads), plus
    a `CaptureFails` switch for the query-failed refusal
- [x] CLI: `restore capture [--target ID]`; usage header line
  - no `AGWINTERM_SESSION_ID` default: a bare call captures every pane. The header now also lists
    `restore clear`, which it never had
- [x] tests `tests/Agwinterm.Pty.Tests/RestoreCaptureTests.cs`: all-panes reply shape and count;
      one target; a pane with nothing running reports `null` and counts zero; unknown target refused
      and no pane's slot changed; a cover pane refused; the tree carries `capturedCommands` after a
      capture and not before; `replayOnRestore` mirrors the toggle
  - 18 tests; also: session id → pane 0 regardless of focus, name → focused pane, `active`, a prefix,
    an ambiguous name; a re-capture of nothing clears the earlier checkpoint; a failed query leaves
    the earlier checkpoint standing; `restoreCommands` and `capturedCommands` stay apart on the tree
- [x] run the .NET suite — must pass before task 5
  - Pty 537/537 (519 + 18), Core 246/246; Win32 host `--no-incremental` rebuild 0 warnings, DLL
    byte-probed for the three new method names (the build-gotchas rule)

### Task 5: the restart harness and the round-trip
- [x] `tests/ui/lib.ps1`: `Restart-Sandbox -Kill` — close the main window (`WM_CLOSE`) or
      `Stop-Process`, wait for exit, relaunch the same `--pipe` / `--app-id` **without**
      `--no-restore`, wait for `ping`. It must refuse to run when the app-id is not a sandbox
      (`qa/product.md:47`). Confirm first whether `--no-restore` suppresses *saving* as well as
      restoring; if it does, the first launch of the cell also runs without it against the fresh
      sandbox app dir
  - `--no-restore` gates only `TryRestoreState` (`Program.cs:1337`); `SaveState` has no such gate, so
    the first launch keeps the flag and still writes `windows\<id>.json`. Documented on `Start-Sandbox`
  - the refusal is positive, not a denylist: `Test-SandboxAppId` accepts only the `<pipe>-<8 hex>`
    shape `Start-Sandbox` mints, with the dir directly under LocalApplicationData. A graceful close that
    does not exit in time is an error, never a fallback kill (that would turn cell A into cell B).
    `Start-Sandbox` / `Restart-Sandbox` share one launcher (`Start-SandboxProcess`); `$s` now carries
    `Exe`, `AppId`, `Width`, `Height` and is updated in place, so a caller's `finally { Stop-Sandbox }`
    tears down whichever process is current
  - ➕ `Save-SandboxCapture` (PrintWindow + PW_RENDERFULLCONTENT, optional crop), `Get-SandboxScale`,
    `Compare-Capture` (byte-identical PNGs) — the plumbing `qa/persistence.md` needs; `qa/product.md`
    gained a paragraph for each of Restart-Sandbox and the captures
- [x] `tests/integration/restore-roundtrip.ps1`, modelled on `agliteterm/test/restore-matrix.ps1`:
      **cell A (graceful)** — set a context on a session, start a long-lived child in its pane
      (`powershell -NoProfile -Command Start-Sleep 300` typed over `session type`, so the capture has
      something non-denylisted to find), `restore capture`, assert the reply names it, close
      gracefully, relaunch, assert `tree --json` has the context and the captured command;
      **cell B (killed)** — same setup, `Stop-Process`, relaunch, same assertions — this is the case
      the batch exists for, and before Task 4 it fails by construction (the last ordinary save wrote
      `""`); **cell C (refusal left nothing)** — a refused context before a restart, the old value
      comes back
  - ⚠️ the child is `ping -n 300 127.0.0.1`, not the powershell one-liner the plan named: `powershell`,
    `pwsh` and `cmd` are ON the restore denylist (`LoadDenylist`), so that child captures as the honest
    null and the cell would prove nothing. ping is not denylisted, quiet, and ends by itself; a stray
    one (dead parent) is stopped after every cell
  - each cell also asserts the world BEFORE the restart: the reply, and the state file on disk already
    carrying `Context` and `Command` (what a kill leaves); an idle second session comes back without
    either; cell A additionally asserts nothing was replayed with the toggle off
- [x] if the sandbox config can enable `restore-commands` (`TerminalConfig.cs:41`), a fourth cell
      asserts the replay actually typed the command after relaunch (`session read` shows it); if it
      cannot, say so in the script header rather than skipping silently
  - it can (`-Conf @('restore-commands = true')` lands in the sandbox's own `agwinterm.conf`): cell
    `replay` (killed) asserts `replayOnRestore:true` in the capture reply and, after the relaunch, the
    pane text carrying `& "…PING.EXE" -n 300 127.0.0.1` — the `& ` prefix is what the replay adds and
    the originally typed line never had, so the restored buffer cannot satisfy it
- [x] `tests/integration/win32-control.ps1`: the no-restart checks — set, read-back in the tree,
      each refusal, `restore capture` reply shape, `session reopen` carrying the context
  - 24 checks: set (reply = value in effect, tree, whitespace trimmed), every refusal twice (control
    character with offset, over-ceiling, blank naming `--clear`, unknown target, text+`--clear` refused
    client-side with exit 2) + the old value standing, rename leaves the context, the state file, clear
    (`context:null`, key omitted); capture with `--target` (pane, session, captured, `replayOnRestore`),
    tree read-back, bare capture over every pane, unknown target and scratch cover refused + slot
    untouched, `Command` in the state file, Ctrl+C then a re-capture of nothing clearing the checkpoint
  - ⚠️ `session reopen` is not a control verb (it is the `ctrl+shift+r` binding, `Keymap.cs:51`), so
    the check drives the window's own key path: `NativeMethods.Chord` (the `AgwUi.Chord` shape —
    AttachThreadInput + SetKeyboardState + PostMessage to this instance's hwnd, nothing global)
- [x] `qa/persistence.md`: cases in the `qa/control-honesty.md` format for the visible surfaces
      (title bar suffix, row suffix, palette line, palette search) with `PrintWindow` evidence
  - five cases: title bar (suffix dimmed before the bell; the right button group byte-identical before /
    after / with a budget-filling value; clear restores the bar exactly), sidebar row (the `below` row
    byte-identical, `rowH` derived from `session metrics`), palette line + search by context, reopen,
    and a pointer to the round-trip script for the persisted half. Driven 2026-09-05 by
    `.ralphex/progress/p3-task5-qa-persistence.ps1` (gitignored, captures in `p3-task5-qa/`): 12/12
- [x] run both integration scripts end to end against a sandbox; attach the transcripts
  - `restore-roundtrip.ps1 -Strict`: 34/34 (graceful 8, killed 7, refusal 8, replay 9+2), twice;
    `win32-control.ps1 -Strict`: 61/61 (37 existing + 24 new). Transcripts (gitignored):
    `.ralphex/progress/p3-task5-restore-roundtrip.txt`, `p3-task5-win32-control.txt`,
    `p3-task5-qa-persistence.txt`. No sandbox process or dir left behind afterwards

### Task 6: docs
- [x] `AgentSkill.cs`: `session context` after the `session rename` line (`:93`), saying it is one
      line, what is refused, that it survives restart and is readable in `tree --json` as `context`;
      `restore capture` beside `restore clear`, saying it can take seconds (CIM), what `null` means,
      and that `replayOnRestore` tells the caller whether the slot will be typed back
  - `AgentSkillTests.PersistenceVerbsAreAdvertisedWithTheirRules` pins both entries, their
    neighbours' order, and the three claims (one line / refusals, SECONDS, `replayOnRestore`)
- [x] `README.md`: both verbs in the example block (`:213-234`) and a paragraph each in the prose
      list (`:245-283`) — `session rename` is in neither today, so add its one line beside `context`
      rather than leaving the new verb without its neighbour
  - example block: `restore capture`, `session rename`, `session context`; prose: "Eight" → "Ten",
    a `restore capture` paragraph after `session restore`'s and a `session context` paragraph with
    rename's line inside it (rename leaves the context alone — `Program.ControlHost.cs:414` and the
    win32-control check both say so)
- [x] CLI usage header (`Ctl/Program.cs:10-35`); `ISessionHost` comments for both methods, in the
      `SessionRestore` style (states, refusals, threading)
  - already done by Tasks 1 and 4 (`Program.cs:16-21`, `:38-41`; `ISessionHost.cs:150-174`,
    `:298-325` — resolution, returns / null, the toggle, threading, each in its own `<para>`);
    verified rather than re-written
- [x] `docs/agterm-parity.md`: tick item 1 and the `restore.capture` half of item 5, moving what is
      closed into the **Closed** table with the PR number once it exists
  - two **Closed** rows as `agwinterm *(P3, PR pending)*, lite: P3-lite` — ⚠️ the PR does not exist
    yet; Task 7 (or the PR itself) swaps the placeholder for the number. Items 1 and 5 removed from
    the open list (5 had only the `restore.capture` half left), the list renumbered 1–8, the
    "renumbered after each" note extended with the P3 rows and the format rule, and the released
    version corrected to 0.17.11
- [x] `docs/lite-parity.md`: record that P3-lite owes `session.context` (a `C` line type after the `S`
      lines, same `SessionContexts` rules) and `restore.capture`
  - an "Owed: what P3 adds — P3-lite" section after the P2-lite one: the `C` line type positional
    like `P` and refused on a count mismatch, the shared rules, the reply shapes, null semantics,
    `replayOnRestore`, and that lite's `session.restore` (P9) comes after its capture slot
- validation: Pty tests 538/538 (1 new), Core tests pass, `tests/conformance/control-api.json`
  unchanged (`git diff` empty)

### Task 7: [Final] Verify acceptance criteria
- [x] verify every item in Overview is implemented, and that the format rule at the top of this plan
      matches what shipped (one new key, `Command` reused, validated on load)
- [x] verify the edge cases: a context on a session that is then renamed (both survive); `session
      context` on a session whose window is closing (refusal, not `ok`); a context of exactly
      `MaxLength` accepted and `MaxLength + 1` refused; `restore capture` while a pane's child is
      exiting (null or the command — never a crash, never a frozen window); `restore capture
      --target` naming a cover pane; two `restore capture` calls back to back (the second's hop
      queues behind the first); a restore file with `Context` holding a newline (dropped, not shown)
- [x] confirm `tests/conformance/control-api.json` is **unchanged** — `git diff` must be empty for it
- [x] run the conformance suite against a sandbox with `-Strict`
- [x] run the full .NET suite, the Rust suite, and `tools/check-abi.ps1` — nothing here should move
      the core ABI
- [x] run `tests/integration/win32-control.ps1` and `tests/integration/restore-roundtrip.ps1` end to
      end against a sandbox
- [x] a state file saved by this build loads in a **0.17.11** build (installed at
      `%LOCALAPPDATA%\Programs\agwinterm`, run with a copy of the sandbox app dir under its own
      sandbox app-id) without a `.bad` rename — the downgrade case is loss, not a crash, and that
      claim gets checked once rather than asserted
- [x] mark P3 **Shipped** in `docs/plans/2026-09-03-parity-batches.md` with the PR number

**What the verification found** (PR **#233**, opened from this task so the trackers could carry the
number, exactly as P1's #221 and P2's #226 were recorded before their review rounds).

- **Overview and the format rule match the code.** One new key (`SessionState.Context`,
  `WhenWritingNull`), `PaneState.Command` reused for the captured slot, both validated on load
  (`RestoreState.LoadContext`, `SidebarWidth` for out-of-range). `session.context` and
  `restore.capture` both report the value in effect through one `InvokeOnUiQueued` hop, and both are
  readable back in `tree --json`.
- **The edge cases hold.** `MaxLength` / `MaxLength + 1` and a `Context` holding a newline are unit
  tests (`SessionContextTests`, `RestoreStateTests`); rename-leaves-the-context, a cover-pane refusal
  and back-to-back captures are in `win32-control.ps1`; the closing-window refusal is the hop throwing
  into `ok:false`.
- **A test-robustness fix, committed here** (`5f0924a`). The re-capture check ended its ping child by
  typing a lone `0x03`, which ConPTY does not reliably turn into a console Ctrl+C, so the child kept
  running and the check failed in the full run (passed in isolation). It now ends the child by pid
  (only the fixture's exact command line) and polls the re-capture until the slot clears — the check
  proves the verb's honesty, not ConPTY's ^C timing. The product already polls after `0x03` for the
  same reason (`QuitClaudeAndRelaunch`).
- **The downgrade case is loss, not a crash — checked once, not asserted.** A P3 file this build wrote
  (with `Context` and a captured `Command`) was loaded by the released **0.17.11** portable under a
  minted sandbox app-id: it started and answered `ping`, did **not** rename the file `.bad`, restored
  the tree, did not surface the `Context` it does not know, and dropped that key on its next save.
- **Suites, all green on this branch:** Core 246/246, Pty 538/538, Rust 36/36, `check-abi` v18 both
  sides; `conformance -Strict` 58/58; `win32-control -Strict` 61/61 (twice); `restore-roundtrip
  -Strict` 32/32. `control-api.json` `git diff` is empty — the contract steps are the sibling PR's.

**What revmux round 1 found** (`.revmux/tasks/p3-persistence/01-initial`, full branch at
`7f92d62`): two Majors, both in the capture path, and seven Minors. Fixed in the commit after this
note; round 2 is scoped to that commit.

- **Major — a failed quit-time query wiped every durable checkpoint.** `CaptureCommandsIntoPanes`
  called the discarding wrapper, which answers an EMPTY map for "nothing running" and for "the query
  did not run" alike, and then wrote `null` into every pane's `CapturedCommand` and saved. The verb
  had the rule (`QueryFailed` is a refusal, never an empty answer); the other writer of the same
  field applied the opposite one. Before P3 the slot was not durable, so a lost query at quit cost
  nothing; making it survive ordinary saves is what turned this into data loss — at the one moment
  the checkpoint exists for. Now `TryCaptureForegroundCommands`, and a query that did not run leaves
  every slot as it was.
- **Major — the documented 15 s bound was never enforced.** `ReadToEnd()` ran BEFORE
  `WaitForExit(timeout)`, so a powershell wedged inside the CIM query (stdout still open) hung the
  caller — a control-pipe thread, for `restore capture` — forever. Pre-existing in the helper; new
  because the verb put a pipe client on it. Now an async read beside the bounded wait, the process
  TREE killed on expiry, the partial output never parsed.
- Minors: the hop comments claimed a timed-out mutation cannot land later (it stays queued — the
  comments now say what `InvokeOnUiQueued` says); `pillX` was anchored after the context run, so the
  pill strip's origin depended on the context's width — the invariant `qa/persistence.md` itself
  names (pills now sit between title and context, anchored on the title alone); the re-capture
  fixture killed every `ping -n 300 127.0.0.1` on the machine, including `restore-roundtrip.ps1`'s
  in an overlapping run (now only a descendant of the sandbox's app process); the scratch-cover
  integration check matched on `scratch`, a word the unknown-target refusal also contains for that
  target (now the cover wording); five references to the plan's pre-move path; the QA doc's crops
  multiplied a device-pixel width by the scale and threw on any HiDPI display (now read off a
  capture, and `Save-SandboxCapture` refuses an origin outside the bitmap); `restore capture s1`,
  `--targt s1` and `--target ""` all silently broadened to every pane (refused in the CLI, and an
  empty target refused in the server with its own wording — `EmptyTarget`, unit-tested).
- Not taken: "persistence errors are swallowed before capture reports success" (pre-existing —
  `SaveState` ignores IO errors everywhere); "a checkpoint is never refreshed at quit while
  restore-commands is off" (immaterial — the replay is off too).

## Technical Details

- **Why the context is per session and one line.** agterm's item is per session ("shown in the title
  bar and tree"), and both of our surfaces are single rows with a fixed height — the palette is the
  only place a long form could go, and it already has a dimmed second line. A multi-line value would
  be silently truncated everywhere it is shown, which is the P2 defect class; refusing control
  characters keeps "what you set is what is shown" true. The ceiling (200) is a display budget, not
  a storage limit — say so in the constant's comment so nobody "fixes" it upward without widening
  the surfaces.
- **Why `--clear` rather than an empty string.** `session rename ""` is already refused as blank; a
  context verb that treated blank as "clear" would be the one verb in the family where an empty
  argument is a command. A flag is explicit and refuses the ambiguous combination.
- **Why `InvokeOnUiQueued` for a one-field write.** Because the reply reports the value *in effect*,
  and P2's `SessionRestore` round found that `Post(...); return true;` reports the value *requested*.
  The cost is one UI hop per call, which rename already pays in redraw.
- **Why `Pane.CapturedCommand` and not a new file key.** The slot already exists in the file
  (`Command`) and is already dropped identically by older builds; what is missing is the in-memory
  field that makes it survive the next ordinary save. Adding a second key would give the format two
  spellings of one thing and lite two line types to mirror.
- **Why the quit-time capture moves onto the same field.** Two writers of one slot — a local
  dictionary at quit and a field the rest of the time — is how the write-back trap happened in the
  first place. One field, written by the capture and read by the save, and `WM_DESTROY` becomes
  "capture, then save" like any other caller.
- **Why the verb ignores the toggle and reports it.** `session.restore`'s pin already ignores the
  toggle; a capture that silently did nothing on a default install would be `ok:true` with an empty
  world. Reporting `replayOnRestore` is cheaper than a second refusal class and tells the caller the
  one thing it cannot otherwise learn.
- **Why the capture runs on the pipe thread.** The CIM query is 1–15 s. `AdoptClaudeSessions` blocks
  the UI thread for it through `InvokeOnUi` and its own comment apologises; `RestartAllClaudeSessions`
  runs it off-thread and posts. The pipe thread belongs to the caller who asked, so it is the right
  thread to wait on, and the writes still land in FIFO order through the P2 hop.
- **Why the POCOs move to `Agwinterm.Pty`.** Not for reuse — for a test. Every P4/P7 format change
  will otherwise be verified by grepping a JSON file from PowerShell. The move is mechanical, the
  byte-for-byte check makes it safe, and `BufferPersist` set the precedent for a serializer with a
  round-trip test beside it.
- **The reply shape for `restore.capture` is proposed, not copied.** agterm's parity entry is one
  sentence and states no shape. Per-pane objects with `null` for "nothing running" match
  `session.restore`'s reply and the tree's `restoreCommands`; the conformance step in the sibling PR
  will fix it as the family's answer.

## Post-Completion

*Informational — no checkboxes*

- **Sibling contract PR, immediately after this merges and before the release**: a `session context`
  step after the `session.rename` one (`control-api.json:86-95`), a `tree` read-back asserting
  `context`, a `session context` errors-block step (control character or blank), and a
  `restore capture` step asserting the reply keys. agliteterm's `check-contract` goes red for the
  gap until P3-lite — expected.
- **Release** after the contract PR: P3 adds verbs, so the release rule applies
  (`2026-09-03-parity-batches.md:28-32`). **`0.17.18` is the next package checkpoint** — skip it unless
  Boris intends winget/choco to carry this batch. Boris calls the tag.
- **P3-lite**: `session.context` mirrored with a `C` line type appended after the `S` lines (positional
  like `P`, refused wholesale on a count mismatch the way `P` is at `main.cpp:7866-7871`), the same
  `SessionContexts` rules, the title/row suffix, and `restore.capture` against lite's own capture path.
  P3-lite's checks SKIP against the released `agwintermctl` until the release that carries this batch.
- **#227 / #228 follow-up**: items 1, 2, 4 of #228 and all of #227 remain, in `SessionOverlay`. Items 3
  and 5 (for `SessionRename` and the two new verbs) are absorbed here; the remaining `Post(...);
  return true;` verbs of item 5 are not — say so in the PR and the issue.
- Review: **revmux**, two rounds minimum, and a narrow round for each fix commit.
