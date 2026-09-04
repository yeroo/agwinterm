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
repo is the canonical contract, agliteterm's CI checks its copy against it, and an agent written
against one product should work against the other. The UI half can differ where the platform or the
product's purpose justifies it.

---

## Control API: 45 verbs agliteterm does not answer

Grouped by what they cost an agent, not alphabetically.

### Selection — the sharpest gap
`selection.all` · `selection.clear` · `selection.copy` · `selection.finalize`

lite has `session.copy` (the selection's text) but no way to **make**, clear or finalise one. So
copy tooling, anything reading a user's selection, and the QA cases that drive selection through the
API all stop at the door. A step that calls `selection clear` there does nothing and returns an
error most callers ignore — a setup that silently did not happen, which is the failure mode the QA
cases exist to prevent (`qa/product.md` in that repo says so).

**Size:** small. lite already has the selection model behind `session.copy`.

### Reading and driving a pane
`session.search` · `session.focus` · `session.switch` · `session.resize` · `session.background` ·
`session.readonly` · `session.bind` · `session.restore` · `session.write`

`session.write` is in flight (agliteterm #15). `session.readonly` matters most of the rest: it is how
you stop stray keys reaching a running agent.

### Images
`image.show` · `image.sixel` · `image.clear` · `image.frame`

lite renders no images at all. `image.frameshm` (shared-memory frame delivery) is the one ConPTY
makes necessary, and it is agwinterm-only — as is `session.metrics`, the pane's cell and pixel
geometry, which exists so a frameshm producer can size its frames and a mouse-driving script can
turn cells into SGR-Pixels coordinates. Neither is a gap: lite has no consumer for either, and the
README tells callers to capability-probe both. **Size:** large.

### Configuration and appearance
`config.get` · `config.list` · `config.set` · `theme.list` · `theme.set` · `omp.list` · `omp.set` ·
`font` · `settings.open` · `keymap.reload` · `profiles.list` · `profiles.reload`

lite keeps settings in the registry (`HKCU\Software\agliteterm`) with a Properties dialog, so this is
not a straight port — but `config.get`/`set` over the API is what lets an agent set up a workspace
without a human clicking. **Size:** medium as a group, small each.

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

### Being mirrored next: what P2 (agwinterm #226) owes lite
Batch **P2-lite** (agliteterm `docs/plans/2026-09-04-p2-lite-mirror.md`) owes four things, one of
them a new verb:

- **`--stdin` on `session type` / `session write`**, refusing invalid UTF-8 client-side with the
  byte offset named, sending nothing, stripping exactly one trailing newline, and refusing `--stdin`
  beside positional text. The detection has to be in the CLI: a JSON request is text by the time a
  server reads it, so the server cannot see the bad bytes at all.
- **`--size-percent` validated as 1–100, not clamped** — absent means the full region, anything else
  outside the range is refused with the value, the range and the way to ask for full named. No
  overlay opens or resizes on a refusal.
- **A bare `session new` lands in the caller's own workspace** (P2 task 5a, and the item Boris is
  actually feeling — the report came from agliteterm on the work laptop). lite's `newSession()` reads
  `g_activeWs`, which every click, selection and API `workspace.new` rewrites; the mirror adds a
  `caller` argument to the dispatcher (the CLI already sends `AGWINTERM_SESSION_ID`) and resolves
  that pane's workspace before creating, with *active* as the last fallback rather than the first.

- **`sidebar width`** — the new verb, and the one the contract now pins: lite has a real `g_sidebarW`
  and a `sidebar` verb that today **toggles on any op it does not know** (`sidebar width` flips the
  sidebar and answers ok), so the mirror is cheap and the honesty fix is overdue. Lite's own range
  (90..900, what its splitter and registry already allow) replaces agwinterm's 120..600; the contract
  pins the reply shape and the refusal, not the numbers.

`session.new`'s **refusal** of an unknown workspace needs no mirror — lite had it first, which is why
decision 1 went that way. `session.restore`'s pane reply stays agwinterm-only until P9 brings the
verb to lite at all. The conformance steps for `session new --workspace no-such-workspace`,
`--size-percent`, `sidebar width` and the two sidebar refusals landed in the sibling contract PR
right after #226, so `check-contract` is red by design for the gap between that merge and P2-lite.

---

## Where agliteterm is AHEAD

Not a one-way list, and these should move the other way.

- **`session.split` returns the split's id.** agwinterm's returns nothing, and a hidden split shell
  has no other handle. **agwinterm should copy this** — tracked in `agterm-parity.md` too.
- **Splits scroll into main-screen history on the alt screen.** lite's renderer and hit-test derive
  their row from the same offset on either screen; agwinterm pins the alt screen to 0. Both are
  self-consistent (highlight and clipboard agree either way), so this is a difference to decide about
  rather than a defect — see `qa/product.md` in the lite repo.
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
| Mark mode (keyboard selection) | agwinterm: Ctrl+Shift+M, arrows, Enter copies |
| Select All | no equivalent |
| Drag-autoscroll | dragging past the pane edge does not extend the selection |
| Configurable scrollback | lite does not call `agwcore_emu_set_scrollback`; the cap is the core default |
| Images / graphics | see the `image.*` verbs above |
| Dashboard, quick-terminal parity, multi-window | agwinterm has a window library; lite has one window plus popups |
| Wheel over the pane | a posted `WM_MOUSEWHEEL` does not reach lite's handler (harness finding, 0.17.11) |

---

## What is deliberately different

- **Settings storage.** agwinterm reads `agwinterm.conf` and honours `--app-id`; lite uses the
  registry and a `%LOCALAPPDATA%` override. That is why the two QA adapters isolate differently, and
  it is not worth unifying.
- **Splits as sessions.** lite models a split as a hidden session; agwinterm models panes inside a
  session. Behaviour matches now (a split belongs to its session, closes with it, restores with it),
  and the internal shape can stay different.
- **The native core is shared.** Both load `agwinterm_core.dll` across the same C ABI, so emulator
  behaviour — widths, scrollback, alt screen — is common by construction. A difference there is a
  bug in one of the clients, not a parity gap.

---

*Companion to [agterm-parity.md](agterm-parity.md), which tracks both products against umputun's
agterm. This file tracks them against each other.*
