# agliteterm ↔ agwinterm parity tracker

**Goal: the two products expose the same features.** agliteterm is a separate product with its own
repository, not a cut-down build — so a gap here is a gap, not a design decision, unless this file
says otherwise and gives the reason.

- agwinterm **0.17.7** released (main ahead), agliteterm **0.17.13** released.
- Verb lists below were **extracted from both dispatchers** on 2026-09-02, not written from memory:
  85 verbs in agwinterm, 41 in agliteterm. #220 added two more to agwinterm — `image.frameshm` and
  `session.metrics` — both agwinterm-only by design (see *Images*), so the gap count below is
  unchanged at 44; #221 added `surface.cursor` (88 and 41 now). The rest of that batch is a tree
  FIELD and a CLI verb, so neither moves this count.
- Update this file in the PR that closes an item.
- The verbs below are cut into runnable batches (P6–P12) in
  [plans/2026-09-03-parity-batches.md](plans/2026-09-03-parity-batches.md).

The control API is the half that has to match exactly: `tests/conformance/control-api.json` in this
repo is the canonical contract, agliteterm's CI checks its copy against it (`tools/check-contract.ps1`
fetches this repo's `main` copy and exits 1 on ANY drift; `-Update` overwrites lite's copy), and an
agent written against one product should work against the other. So a contract change lands here
first and lite is red until it runs `-Update` (P3, P5) — unless the lite batch is the one that
teaches lite the verbs, in which case the contract PR waits for that lite PR to merge, so its steps
never turn a mergeable lite PR red (P6). The UI half can differ where the platform or the product's
purpose justifies it.

---

## Control API: remaining gaps and batch status

Grouped by what they cost an agent, not alphabetically. The four `selection.*` verbs — the
sharpest gap, the one where a QA setup step silently did nothing — closed in P6-lite (see
"Mirrored: what P6 owed lite" below).

### Reading and driving a pane
`session.focus` shipped in P4-lite; `session.write` is also implemented (display injection, not
shell input). P9-lite [#52](https://github.com/yeroo/agliteterm/pull/52) implements `session.search`,
`session.switch`, `session.resize`, `session.readonly`, `session.bind` and `session.restore`;
its status and deliberate differences are recorded below. `session.background` remains refused:
lite draws no images, and adding a watermark pipeline is outside this batch.

### Images
`image.show` · `image.sixel` · `image.clear` · `image.frame`

lite renders no images at all. `image.frameshm` (shared-memory frame delivery) is the one ConPTY
makes necessary, and it is agwinterm-only — as is `session.metrics`, the pane's cell and pixel
geometry, which exists so a frameshm producer can size its frames and a mouse-driving script can
turn cells into SGR-Pixels coordinates. Neither is a gap: lite has no consumer for either, and the
README tells callers to capability-probe both. **Size:** large.

### Configuration and appearance
P10a-lite [#54](https://github.com/yeroo/agliteterm/pull/54) implements `config.get/list/set`,
`theme.list/set`, `settings.open` and `keymap.reload`, plus configurable new-replica scrollback
and copy-on-select. Lite uses validated, per-value HKCU persistence, not agwinterm.conf.

Remaining P10b work: `omp.list/set`, `font`, `profiles.list/reload`, and opt-in replay of captured
commands. Theme names are lite's four UI modes, not the full app's terminal-theme catalog.
P10 as a whole is not complete.

### Commands and installers
`command.list` · `command.run` · `command.leader` · `install.cli` · `install.hooks` ·
`install.shell` · `app.update`

lite has `install.skill` only.

### Agent integration
`claude.adopt` · `claude.yolo` · `claude.update`

Adoption is the interesting one: pointing the terminal at an already-running Claude session.

### Everything else
`broadcast` · `notify` · `dashboard` · `restore.clear` · `workspace.move`

### Being mirrored now: the read-only trio agwinterm shipped in P1
`surface.cursor` — the only *verb* of the three, and so the only one counted above. The other two
are a tree node FIELD (`statusChangedAt`) and a CLI verb (`agwintermctl version`), owed just as much
and invisible to a verb count.

Landed in agwinterm as **#221** (batch P1). The lite mirror is agliteterm's
`docs/plans/2026-09-03-p1-lite-mirror.md` (batch **P1-lite**), in flight the same day: the two
products now advance batch by batch rather than lite catching up in one P8 after wave 1.
`surface.cursor` is the one that changes behaviour rather than convenience — it is the check before
typing into another agent's composer, and without it a caller has to guess emptiness from rendered
text. lite has no `version` verb to add: `agwintermctl version` reports the app from `ping`, and
lite's `ping` answered a hard-coded `"agliteterm 0.1"`, so the mirror of that item is a truthful
`ping`.

The conformance step for `surface.cursor` (a new `integer` result kind in the runner) is in the
canonical file as of this PR — agwinterm-first, as the contract rule says. Until P1-lite merges,
agliteterm's `check-contract` is red by design; that is the gate, not a bug. `statusChangedAt` is a
nested tree field the shape runner cannot express, so each product pins it in its own tests.

### Mirrored: what P2 (agwinterm #226) owed lite — P2-lite shipped
Batch **P2-lite** — agliteterm **#26** (2026-09-04; plan `docs/plans/2026-09-04-p2-lite-mirror.md`
there; two lite reports, #23 a pane collapsing to 2 columns and #24 the window coming to the front
on its own, rode along and close with it) — delivered the four things P2 owed, one of them a new
verb:

- **`--stdin` on `session type` / `session write`** — the shared CLI's half; lite's decoder is
  unchanged and is proved against it (a quote, a newline, two spaces and a leading `--` round-trip
  byte for byte; a lone `0x80` is refused client-side and the pane receives nothing). The detection
  has to be in the CLI: a JSON request is text by the time a server reads it.
- **`--size-percent` validated as 1–100, not clamped** — refused with the value, the range and the
  way to ask for the default named, and NO popup opens or moves on a refusal. lite keeps its 70 %
  default where agwinterm's absent means the full region (its overlay is a separate popup window;
  "full" would hide the main window); the contract pins the reply and the refusal, not the geometry.
  lite also gained `resize`, a refusal for an unknown action, for `open` with no command and for a
  target that names no session, and `close` with nothing open answers `no overlay`.
- **A bare `session new` lands in the caller's own workspace** (P2 task 5a, and the item Boris was
  actually feeling — the report came from agliteterm on the work laptop). lite resolves the
  `caller` the CLI sends (inside `args`, as `Program.cs` puts it) by id or ≥4-char prefix only —
  never by name — under its lock, with *active* as the last fallback; `--workspace` beside
  `--workspace-name` is refused before anything is created.
- **`sidebar width`** — the new verb, and the one the contract pins: reads or sets the width, answers
  the width **in effect** with `visible` and `applied` (a set while hidden is remembered), and lite's
  `sidebar` no longer toggles on an op it does not know — `sideways` and a typo are refused naming
  the five ops and nothing changes. Lite's own range (90..900, what its splitter and registry already
  allow) replaces agwinterm's 120..600, plus a second refusal against the live window for a width
  that would leave the terminal under 20 columns; the contract pins the reply shape and the
  refusal, not the numbers. So **`sidebar.width` is mirrored here, in P2-lite**, not deferred to a
  later batch; lite's verb count is 43 with it.

`session.new`'s **refusal** of an unknown workspace needed no mirror — lite had it first, which is
why decision 1 went that way. P9-lite implements `session.restore` and its pane reply.
The conformance steps for `session new --workspace
no-such-workspace`, `--size-percent`, `sidebar width` and the two sidebar refusals landed in the
sibling contract PR (#229) right after #226; agliteterm's `test/control-api.json` is that file
again and `check-contract` is in step. Until the agwinterm release that carries #226 is tagged, lite's
checks that need the newer client (`--stdin`, a strict `--size-percent`, `sidebar width N`, `caller`)
probe it and SKIP — red under `-Strict`, which is the release gate, the P1-lite rule.

### Mirrored: what P3 (agwinterm #233) owed lite — P3-lite shipped
Batch **P3-lite** — agliteterm **#28** (2026-09-05; plan `docs/plans/2026-09-05-p3-lite-mirror.md`
there) — delivered both verbs batch **P3** (agwinterm **#233**, plan
`docs/plans/completed/2026-09-05-p3-persistence.md`) added; lite's verb count is **45** with them,
and the two items below read as what was owed. Three deliberate divergences, each recorded in
lite's README and its shipped skill text rather than left for a reader to trip over:

- **No title-bar surface.** lite's caption is the OS caption, set once to the instance name; no
  session name has ever been in it, so a context there would be a new behaviour, not a mirror, and
  could not be "dimmed". The sidebar row — lite's one per-session text surface — draws the context
  dimmed after the name in its post-paint pass, beside the pennant and the unread pill it already
  draws, and the badges do not move (`qa/persistence.md` there has the capture).
- **`replayOnRestore` is a constant `false`.** It describes replay of captured `K` commands, which
  lite never types back. P9's explicit `R` pins and `B` bindings are separate and do not change
  this answer. A future captured-command replay setting belongs to the configuration batch.
  The capture itself is in-process (one Toolhelp32 snapshot plus the PEB read lite
  already does for a shell's cwd — milliseconds, no child process, so none of the CIM query's
  timeout-and-kill semantics), with agwinterm's default denylist frozen as a constant: lite has no
  `restore-denylist.conf`.
- **A hidden pane refuses `session context`.** lite's `resolveTarget` reaches a split shell by id;
  a context set on it would be drawn nowhere (no row) and saved nowhere (no `S` line, so no `C`
  slot) — "succeeded, then vanished". Refused naming the id, the same rule as the cover-pane
  refusal on `restore.capture`.

The persistence lands as two additive line types in lite's `sessions.tsv` — `C` (context, after the
`S` block) and `K` (the captured slots, after the `P` lines whose split its second field names) —
positional like `P`, refused wholesale on a count mismatch with the `P` guard's wording, and every
loaded context validated through the verb's own rules (`docs/state-file.md` there). The refusal
wording is `SessionContexts` / `RestoreCaptureReply` verbatim. Two revmux rounds, both without a
Major: round 1's nine Minors were fixed in one commit (a stale save overtaking a newer one, a
`restore capture` answering ok on a failed save, an exited pane's recycled pid, an unlocked paint
read), and that commit's own suite run found lite's JSON was not ASCII-safe — `café 🚀` came back
through the CLI's stdout as `caf? ??` on a non-UTF-8 console — so lite's `jsonEscape` now escapes
non-ASCII as `\uXXXX` the way agwinterm's server does. Round 2's six Minors are agliteterm #29.
Both products' P3 checks run for real since agwinterm **0.17.12** (2026-09-05). What P3 owed, for the record:

- **`session.context`** — one line of "what is this pane for", set over the API, shown dimmed after
  the name in the title bar and the sidebar row, carried in `tree --json` as `context` on the session
  node (absent when none), persisted so it survives a restart and an undo-close. The rules are the
  shared `SessionContexts` class in `Agwinterm.Pty` and lite mirrors them exactly, not
  approximately: a control character (newline, tab, escape) is refused naming the offset, blank is
  refused (`--clear` is how you remove one, and text beside `--clear` is refused), more than 200
  characters is refused — the ceiling is a display budget, not a storage limit. The reply is
  `{session, context}` with the value **in effect** after the write. A rename leaves the context
  alone; the two are separate fields. **Lite's restore file** takes it as a **`C` line type appended
  after the `S` lines**, positional like `P` and refused wholesale on a count mismatch the way `P`
  is (`main.cpp:7866-7871`) — an additive line type, which is the rule agwinterm's own format set
  for this batch (additive keys only, no version, every loaded value validated: a stored context
  that fails the rules is dropped on load, not drawn). The title-bar and row suffix are lite's UI
  half of the item.
- **`restore.capture [--target ID]`** — capture the foreground command of every real pane (or of
  the one named) into its restore slot **now** and save, against lite's own capture path rather than
  a port of agwinterm's CIM query. The reply is `{captured, replayOnRestore, panes:[{pane, session,
  captured|null}]}`: `null` = the shell had no non-denylisted child, and null is written too (a
  fresh capture replaces an older checkpoint); `replayOnRestore` reports the restore-commands
  toggle, which gates the replay at restart and never the capture. An unknown target, a present
  but empty one (only an OMITTED `--target` means every real pane) or a pane that is never restored
  is refused with nothing written; a process query that fails is a refusal, not an empty answer for
  every pane. `tree --json` reads the slots back as `capturedCommands`,
  keyed by pane id like `restoreCommands`. P3 landed captured slots first; P9 adds explicit pins
  separately, not as an instruction to replay captures.

The conformance steps for both (the `session context` set and `--clear` steps, the `restore capture`
step pinning the reply keys, and the two errors-block refusals) landed in the sibling contract PR
**#235** right after #233, agwinterm-first as the rule says; agliteterm's `test/control-api.json` is
that file again and `check-contract` is in step. P3-lite's own checks (the honesty P3 block, the
matrix's context/capture cells, the four conformance steps) probe the client (`agwintermctl
restore` answers a usage line on a post-#233 build) and SKIP against the released `agwintermctl`
until the release that carries #233 + #235 is tagged — red under `-Strict`, the release gate
(**0.17.12**, tagged 2026-09-05, closed it).

### Mirrored: what P5 (agwinterm #250) owed lite — P5-lite shipped

Batch **P5-lite** — agliteterm **#40** (2026-09-07, four revmux rounds; plan
`docs/plans/completed/2026-09-07-p5-lite-mirror.md` there). lite has pane overlays: a pane overlay
is IN-WINDOW, one more hidden `Session` hung on the shell it covers (`Session::overlay`), sized to
the shell's grid, painted in the pane's box instead of the shell and reached by `focusedSession()`
/ `hitTest` as that pane's surface while it is open — not a popup sized to the pane rect (a popup's
`g_focusOverride` takes every key, so the sibling pane could not stay interactive, the rule's one
hard property), and not a recorded divergence: the contract's four P5 steps and three refusals run
the same on both products. `--pane left|right` (`left` = slot 0, `right` = slot 1 whatever the
axis, the slots `session focus` names), `open` / `close` / `result` / `copy` / `text` on the slot
with agwinterm's sentences verbatim (`OverlayPanes.cs`), `paneOverlays` in `tree` as the same array
of words, `session text --all` / `--lines N`, the overlay id as `AGWINTERM_SESSION_ID` of the
program inside, the chord closing the focused pane's overlay first, the slot moving with its shell
on a swap and dying with its pane — all mirrored. What differs, each recorded in the P5-lite plan:

- **(a)** the session-wide slot is a popup over the window, not a cover inside the content region
  (P2-lite, unchanged). The former no-selection limitation closed in P7-lite: popup selection
  paints, takes a drag, and copies its own cells.
- **(b)** `session text` and `overlay text` default to the WHOLE buffer (scrollback + screen) —
  `--all` is the explicit spelling of lite's bare form, `--lines N` the last N lines of that text
  and `--lines 0` the screen — where agwinterm's and agterm's bare `session text` is the screen
  only: lite's skill promised "the whole buffer" and its suites read markers from history through
  it, so the default stays and the flags give a caller the screen when it wants one.
- **(c)** `exit N` is the exit status of the command `open` ran as PowerShell reports it
  (`$LASTEXITCODE` for a native program, else 0 / 1 from `$?`), carried in an FTCS
  `OSC 133;D;<code>` mark the overlay's own command line emits — read off the FIRST mark with an
  exit in that overlay; the pty-host protocol carries no exit code and is frozen. A command that
  never completed leaves the slot's result as it was, and `overlay still running` /
  `no overlay result` are refusals as in agwinterm. The mark is the terminal's shared FTCS state,
  so a command that emits `OSC 133;D` (or a full A–D cycle) of its own sets the exit `result`
  reports: the status is the command's own claim, not a host-side record; a caller that needs one
  it cannot forge reads `session output` or the program's own artefact (a host-side record is a
  pty-host protocol change, its own item).
- **(d)** the session-wide `open` keeps accepting any target that resolves (P2-lite) where
  agwinterm refuses a pane id of a split without `--pane` (#213) — except a pane overlay's own id,
  which names its slot on both products.
- **(e)** `open --pane` answers the overlay's id (a lite session id, `<prefix>-<seq>`; the program
  inside holds it) because the slot is created inline the way `session split on` is, while the
  popup keeps its status word (created after the reply is written) — the contract's step notes
  both spellings.
- **(f)** an overlay's (or any cover's) id on `flag on`/`off`/`toggle` / `seen` / `rename` /
  `status` / `duplicate` / `move` (`flag clear` takes no target and unflags every session, in both
  products) is refused naming the session it covers (`session <verb>: '<id>' is a
  scratch/overlay/quick pane, not a session; … Nothing <done>.`), where agwinterm lands a scratch
  or overlay cover id on the session it covers (`FindSesForTarget`) — a program inside a lite
  overlay that wants the pane's session row names that session's id (`tree`'s `paneOverlays` says
  which); lite's `session context` refuses a cover the same way.

Lite has no `--wait` / `--block` (the overlay stays up until closed, P2-lite) and no
`overlay not realized` / `OverlayIdGoneRefusal` (the emulator is built before `newSession`
returns; the slot's verbs run inline under one lock — documented, not emitted). The contract's
P5 steps (#252) are the gate: agliteterm's `check-contract` is red until #252 is on `main` and
lite's copy is updated, as #235's were before P3-lite.

### Mirrored: what P6 owed lite — P6-lite shipped, and what lite found here

Batch **P6-lite** — agliteterm **#45** (2026-09-07, three revmux rounds; plan
`docs/plans/2026-09-07-p6-lite-selection.md` there). lite answers `selection all` / `copy` /
`clear` / `finalize` with agwinterm's sentences (`selected all` / `empty`, `no selection` /
`copied N chars`, `cleared`, `finalized (copied)` / `finalized (empty)`), on the selection's OWNER
(the surface `g_sel.sess` names: a verb with a target reads or writes the selection only when it is
that pane's, and `all` replaces the owner), through a swap (the highlight follows its shell), and
`copy` clears even a blank selection where `finalize` alone keeps one — agwinterm's
`CopySelection(clear: true)` rule, which lite's second round caught the plan getting wrong.
Decision 2 landed with it: on the alt screen `selection all` is the app's screen only, in both
products. What differs, each recorded in the P6-lite plan:

- **(a)** an unresolved target is refused `ok:false` (`session not found`) on all four, as on
  every lite verb — and agwinterm answered `ok:true` with the string `no session` on the four
  selection verbs AND on `session.paste`, a refusal a script reads as success — and `session.copy`
  answered `ok:true` with `""`, a missing pane and an empty selection in one reply. **Fixed here in
  the P6 contract PR, #256** (`ControlServer` wraps the five in `HostReply`, the hosts return
  `RefusePrefix + SessionContexts.NoSession`; `session.copy` refuses with the read verbs, as
  `session.text` does); the contract's three new refusals pin `ok:false` on both products. Not a
  difference any more.
- **(b)** P10a closes the off-switch gap: `copy-on-select` defaults on; off suppresses mouse-release
  and `selection finalize` writes, with the reply `finalized (copy-on-select off)`. Explicit copy
  remains available. The historical P6 contract note is tracked separately in
  [lite #55](https://github.com/yeroo/agliteterm/issues/55); its executable string-shape floor is unchanged.
- **(c)** Before P7, `selection all` on any popup (overlay, quick or scratch — all three share `paintPopup`)
  was refused `the popup paints no selection`; agwinterm's covers take a selection. P7-lite paints
  one and lifts this.
- **(d)** `selection copy`'s clipboard write is posted to the UI thread; the reply counts the text
  posted. A caller reading the clipboard right after waits for the window's next message (the
  suites' 300 ms). When that enqueue fails, `copy` and `finalize` refuse `the clipboard write could
  not be queued; selection unchanged` — the selection is kept for a retry.
- **(e)** The P6 alt-screen pin initially covered only the verb. P7-lite extends it to wheel,
  drag and keyboard selection; none of those routes enters main-screen history from the alt screen.
- and `copied N chars` counts differently on non-ASCII text: lite counts UTF-8 bytes, agwinterm
  UTF-16 code units (`string.Length`). Same N for ASCII.
- **(f)** `session.paste` reports what happened in agwinterm only (#256, rounds 8-9): `pasted` when
  the payload was handed to the pane's input without a synchronous error (not proof a program read
  it), `nothing to paste` when neither the text nor the clipboard gave any (no claim about why: an
  empty, non-text or unreadable clipboard alike), refusals `ok:false` before the clipboard is read —
  `pane is read-only` (`session readonly on`, the menu item or the `toggle_read_only` binding),
  `the pane's process has exited` (a single-pane session keeps the exited surface; the exit is
  observed a moment after it happens, so a paste right after a child dies can still be `pasted`) — and
  `paste failed: <why>` when the write threw: that one comes after the payload was picked (the
  clipboard may have been read) and says nothing about how much landed. Outside its refusals,
  lite's `session.paste` still answers `pasted` on an exited pane or an empty payload (it writes only a non-empty
  one to a live handle). P9 adds the read-only refusal before clipboard access, but does not mirror
  the empty/exited/write-failure outcomes yet. Those remain [lite #53](https://github.com/yeroo/agliteterm/issues/53); agwinterm #257
  separately tracks missing-session refusals on `readonly` and `search`. The contract's paste
  step stays shape-only until both products answer alike.

The contract's P6 steps (#256) are shape-only and run on the no-selection arm of `copy` and
`finalize` on purpose: the Windows clipboard is shared with the user and with every other sandbox on
the machine, so the contract never writes it. The `session copy` read-back after `selection all` is
shape coverage, not proof that a selection was made — the value is the shell's screen and is not
compared, so a no-op `selected all` followed by an empty copy would pass; that proof, and the copy
itself (`copied N chars`), is each product's own honesty suite's, on a fixture whose content is known.

### Mirrored: P7-lite keyboard and mouse selection shipped

agliteterm [#50](https://github.com/yeroo/agliteterm/pull/50) merged on 2026-09-08 as `1e0903a`.
Ctrl+Shift+M enters mark mode; arrows/Home/End extend it, Enter or Ctrl+C copies, and Escape cancels.
Ctrl+Shift+A selects all. Both bindings can be cleared or rebound. Double/triple-click selects a
word/line; release copies by default (P10a adds the off switch); dragging past a main-screen edge
autoscrolls. Posted wheel messages work.
Frame, split, pane-overlay and overlay/quick/scratch popup surfaces share the selection rules;
alternate screens stay pinned to their own grid. Captured drags are cancelled on invalidation.

The P6 contract mirror is in step. Local Strict selection acceptance and GitHub Windows CI both
passed 70 selection checks, and the complete `run-all.ps1 -Strict` passed in CI. The local fixture
held the shared suite token through owned-process teardown and whole-format clipboard/touched-HKCU
restoration. Legacy fixture hardening remains [lite #51](https://github.com/yeroo/agliteterm/issues/51);
those older full-suite fixtures are confined to the disposable CI runner until hardened.

### Mirrored: P9-lite driving a pane shipped

agliteterm [#52](https://github.com/yeroo/agliteterm/pull/52) merged on 2026-09-08 as `190e514`,
from tested candidate `c77b256`. Guarded local acceptance and Windows CI each passed 188 combined
selection/driving checks; 30 pure driving checks and the complete Strict suite passed. The final
Codex-only confirmation reported no findings from two healthy independent reviewers. CI evidence:
[run 34215226185](https://github.com/yeroo/agliteterm/actions/runs/34215226185).

- **Human-input gate.** `readonly` blocks keys, paste and reporting mouse input on the addressed
  surface, while API typing/display writes and terminal replies remain allowed. Blocked keys
  preserve scrollback and working status. The flag resets on restart; status shows READ-ONLY.
- **Explicit replay.** `restore` pins a command; `bind` supplies a verbatim command that wins over
  the pin. Additive `R`/`B` state fields preserve quotes, backslashes and Unicode. Replay waits
  2500 ms (not a shell-readiness proof), re-resolves current state, and skips adopted or gone shells.
  Captured `K` commands never replay. Lite accepts a session id/name for binding to its own shell;
  agwinterm uses pane-id resolution and lower-cases binding text. Lite saves before replying;
  agwinterm posts the update.
- **Split and navigation.** `resize` persists slot-0's ratio as `G`, clamps to 0.05..0.95, validates
  growth against the axis, and refuses unavailable geometry before mutation. Lite accepts a numeric
  ratio string as well as a JSON number. `switch` previews a snapshot of recency without changing
  it; commit updates recency, cancel restores the origin. Unnamed replies use `session N` instead
  of agwinterm's empty name. Next/previous key bindings still walk tree order, not MRU.
- **Search.** The verb searches the active surface (a valid target does not redirect it), maps
  Unicode scalars to cells, paints current/other matches and shows FIND status. Alternate-screen
  search excludes main history. Counts refresh on a search call; stale row highlights are suppressed.
  A find bar/Ctrl+F and mouse divider drag remain follow-ups, not shipped features.
- **Refusals.** Lite refuses unknown `readonly`/`switch` operations and missing `readonly`/`search`
  targets with `ok:false`; agwinterm still toggles on an unknown readonly op or returns success
  strings for the other cases (missing-session follow-up: [#257](https://github.com/yeroo/agwinterm/issues/257)).
  Both now refuse read-only API paste; the older P9 plan predates agwinterm #256's fix. Lite's
  remaining paste outcome differences are listed under P6 above.

Unknown-operation refusals and binding-command case preservation are tracked in
[agwinterm #259](https://github.com/yeroo/agwinterm/issues/259), separately from #257.

No new canonical contract steps are added by P9: the existing shared floor is unchanged. A later
contract batch follows the remaining agwinterm honesty fixes, with the usual lite mirror update.

### Mirrored: P10a-lite configuration

agliteterm [#54](https://github.com/yeroo/agliteterm/pull/54), candidate `32f0d4a`, adds seven verbs
and fourteen supported keys. Unknown or invalid settings refuse without mutation. Settings apply
to the current instance and persist for later launches; other running instances retain their
runtime state. UI toggles and Properties edits persist only their changed fields, preserving
unrelated preferences saved by another instance.

- Configuration work runs on the UI thread. Pending timed-out requests are cancelled; already
  running timeouts report an unknown outcome and require read-back. Modal dialogs permit reads
  but refuse mutation/reload and duplicate settings opens, protecting unsaved edits.
- `theme` exposes auto/dark/light/classic. `settings.open` requests Properties without raising
  the terminal. `keymap.reload` drops stale bindings, restores absent defaults and respects zero.
- `scrollback-lines` defaults to 5000 and affects new local replicas, including adopted surfaces,
  not existing buffers or the host cap. Zero disables history; positive caps retain the core's
  512-row batched-eviction slack. Copy-on-select defaults on; explicit copy is independent of it.

Local acceptance: 287 combined selection/driving/configuration checks, 118 driving-only and 99
configuration-only checks, plus 142 pure configuration and 30 driving checks. Each interactive run
released its suite token after verified process/clipboard/registry cleanup. One full Codex-only
review and one narrow confirmation completed; the final two healthy reviewers found no shipping
blockers. A subsequent test-only correction replaces a fixed positive replay wait with bounded
exact-marker polling; all 118 driving checks passed again with verified cleanup. Runtime code is
unchanged from the reviewed revision. Full Windows CI and delivery evidence are linked from the PR
and [run 34225641992](https://github.com/yeroo/agliteterm/actions/runs/34225641992).
No new canonical conformance steps are introduced. Font targeting, profiles, OMP and captured
command replay remain P10b, not delivered features of P10a.

---

## Where agliteterm is AHEAD

Not a one-way list, and these should move the other way.

- ~~**`session.split` returns the split's id.** agwinterm's returns nothing, and a hidden split shell
  has no other handle. **agwinterm should copy this** — tracked in `agterm-parity.md` too.~~
  **Matched in batch P4** (agwinterm, #238): `session split` answers a pane id — the split
  pane's on `on` (also when the session was already split, lite's rule), the survivor's on `off`.
  lite had it right first.
- **`window.select` says whether the raise was granted.** agwinterm answers `selected` whenever the
  window exists; Windows refuses a background process the foreground while the user is typing
  elsewhere, so that reply is a guess. lite (P2-lite, its #24) answers `selected` only when
  `GetForegroundWindow` is the window afterwards, and a string starting `not raised:` otherwise —
  still `ok`, the contract's shape, so one script works against both. **agwinterm should copy this.**
- **Alternate-screen history difference resolved.** Boris chose pinning to the alternate grid.
  P6-lite applied that rule to selection verbs; P7-lite applied it to wheel, drag and mark mode.
- ~~**An unknown workspace is refused, not silently swapped** for the active one on `session.new`.
  agwinterm falls back.~~ **Matched in batch P2** (agwinterm **#226**): agwinterm now refuses an
  unknown `--workspace`
  id/prefix, and an unknown `--workspace-name` without `--create-workspace`, with `ok:false` and no
  session created — and refuses the two flags together. lite had it right first; this was decision 1
  of the parity programme, answered "refuse" because one script has to work against both products.

---

## UI and terminal features agliteterm lacks

Not exhaustive the way the verb list is — these are the gaps found while working on both products,
and the list should grow as more turn up.

| Feature | Notes |
| --- | --- |
| Images / graphics | see the `image.*` verbs above |
| Dashboard, quick-terminal parity, multi-window | agwinterm has a window library; lite has one window plus popups |

---

## What is deliberately different

- **Settings storage.** agwinterm reads `agwinterm.conf` and honours `--app-id`; lite uses the
  registry and a `%LOCALAPPDATA%` override. That is why the two QA adapters isolate differently, and
  it is not worth unifying.
- **`session text` reads the whole buffer in lite** (screen + scrollback, `--all` its explicit
  spelling) and the screen only in agwinterm and agterm; `--lines N` is the same reader on both,
  and a script wanting the screen passes `--lines 0` / reads `--lines N` on either (P5-lite).
- **Splits as sessions.** lite models a split as a hidden session; agwinterm models panes inside a
  session. Behaviour matches — a split belongs to its session, closes with it, restores with it, axis
  and order included (an `L` line beside the `P`) — and the internal shape stays different. P4-lite
  (agliteterm #30) implemented `session swap` after all: the hidden session is drawn in the owner's
  other slot, so a swap is one flag read where the two panes are laid out and hit-tested; no tree
  identity moves, no id moves, the `K` line stays by role. `session split close` on the session's own
  shell promotes the hidden session's object into the session's place (same id, name, workspace,
  flag, context, sidebar row; its own pane id kept — `Session::paneId`, set once and never written),
  with a `tree` event and no `session closed` — agwinterm's `[B]` picture. What differs, all recorded
  in the P4-lite plan (`docs/plans/completed/2026-09-06-p4-lite-mirror.md` there):
  - **One node shape agwinterm never emits.** That promoted session's node carries `paneIds` alone
    (`[<its shell's id>]`, no `paneCount`), so in lite the presence of `paneIds` does not imply a
    split — `paneCount` is the split discriminator in both products. agwinterm reaches the same
    state (its env vars are per pane too; closing the pane that carries the session id keeps the
    session) and emits nothing for it (`ControlServer` writes `paneIds` only under `paneCount > 1`);
    lite's key exists so the survivor's own agent, whose `AGWINTERM_SESSION_ID` is no node's `id`,
    can find its session's node — a gap agwinterm's own skill has no recipe for.
  - **The session-id rule differs by one sentence.** agwinterm's session id names the FOCUSED pane
    while no pane carries it; lite's always names the session's own shell, and the split's shell is
    reached only by its own id (no per-session pane resolution). One exception: after a kill-restart
    a promoted session is adopted by its shell's id and comes back under it.
  - **`session close <split shell's id>`** is a pre-P4 divergence in lite's favour: it closes that
    shell (an unsplit) where agwinterm answers `session not found` for a pane id.
  - **Restore.** Split shells are recreated, never adopted, so their ids are fresh after a graceful
    restart; the `L` line puts the axis and order back onto the recreated pair.
  - **`tree`.** A lite node is `active` when the displayed session is it, whichever pane has focus;
    the split's shell has no node. A split side whose shell exits collapses to the survivor in both
    products; a one-pane session's exit stays on screen as `(exited)` in lite.
  Not a divergence, checked both ways: `session select` never moves the active workspace in either
  product.
- **The native core is shared.** Both load `agwinterm_core.dll` across the same C ABI, so emulator
  behaviour — widths, scrollback, alt screen — is common by construction. A difference there is a
  bug in one of the clients, not a parity gap.

---

*Companion to [agterm-parity.md](agterm-parity.md), which tracks both products against umputun's
agterm. This file tracks them against each other.*
