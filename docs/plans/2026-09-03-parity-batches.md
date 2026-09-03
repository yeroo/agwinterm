# Parity backlog, cut into runnable batches

The gap lists in [agterm-parity.md](../agterm-parity.md) and [lite-parity.md](../lite-parity.md) say
*what* is missing. This file says **in what order it gets built**, split so each part is one ralphex
plan, one branch, one PR — run with review steps disabled, then reviewed by revmux.

Each `P<n>` becomes its own plan file in this directory when it is its turn, and moves to
`completed/` when it ships; expanding all of them up front would just produce stale plans. A batch
without a **Shipped** line below has not started.

## The rules the split obeys

- **One plan = one repository.** ralphex runs in one worktree, so an agwinterm batch and its
  agliteterm mirror are always separate plans, never one.
- **Contract before mirror.** `tests/conformance/control-api.json` is canonical here and agliteterm's
  CI checks its copy against it. So a verb is defined in agwinterm first; the lite batch that mirrors
  it runs after, never beside it.
- **Mirror per batch, not per wave** (Boris, 2026-09-03: "I want both terminals to be on par"). Each
  agwinterm batch is followed immediately by its lite mirror (`P1-lite`, `P2-lite`, ...), a separate
  plan in the agliteterm repo. The contract step for a new verb lands in agwinterm in a small
  sibling PR that merges first; lite's CI is red on `check-contract` for exactly the gap between the
  two merges. P8 below is therefore dissolved into those per-batch mirrors.
- **A batch is a theme, not a size quota.** Items travel together when they share a test area or a
  format change, so one revmux round covers them coherently.
- **Every batch exits the same way:** its own tests, the QA cases in `qa/` (both products have them),
  the full suite green, then revmux — **two rounds minimum**; the second round is where criticals have
  historically surfaced.
- **A release follows every agwinterm batch that adds a verb.** agliteterm's CI proves the contract
  against the *released* `agwintermctl` (its `fetch-native.ps1` takes the `latest` release), so a lite
  mirror cannot go green until the verb it mirrors has shipped in a CLI. Only `*.*.9` / `*.*.18` /
  `*.*.27` reach Chocolatey and winget; a per-batch release skips those numbers unless a package
  checkpoint is intended (P1 shipped as 0.17.10, 0.17.9 skipped).

## Blocked on a decision from you

Three contract questions gate their batches. They are cheap to answer and expensive to guess.

| # | Question | Gates |
| --- | --- | --- |
| 1 | `session.new` with an unknown workspace: **refuse** (lite does) or **fall back to active** (agwinterm does)? Falling back silently is how a caller believes it placed a session somewhere it did not. | P2 |
| 2 | On the alt screen, do splits scroll into main-screen history (lite) or pin to row 0 (agwinterm)? Both are self-consistent; they cannot both stay. | P6, P7 |
| 3 | Is `control.pick` (P16) worth its size, or does the picker stay out of scope? It is the biggest single capability gap and also the biggest plan. | P16 |

---

## Wave 1 — agwinterm control API: the cheap half

### P1 · agwinterm · the read-only trio
`surface.cursor` · `statusChangedAt` on the tree's session node · `agwintermctl version`

**Shipped:** #221 — plan at [completed/2026-09-03-p1-readonly-trio.md](completed/2026-09-03-p1-readonly-trio.md);
contract step #223; released as **v0.17.10** (#224). Mirror: agliteterm P1-lite.

Three small, read-only additions with no persistence and no renderer risk. First because this is the
only batch that makes **our own tooling less fragile** rather than merely less laborious:
`AI/bin/peer-chat.py` can drop its per-agent placeholder whitelist, and `AI/state/agents.json` can
drop the `last_seen` shadow state. It is also a gentle first run of the new pipeline.

**Exit:** `surface cursor --target <pane>` prints a bare integer; `tree --json` carries epoch seconds
alongside `status`; `version` names the serving app **and the resolved path of the CLI that ran**.

### P2 · agwinterm · stop lying to the caller
`--stdin` on `session type` / `quick type` (rejecting invalid UTF-8) · `session.overlay.open`
validates `--size-percent` as 1–100 instead of clamping silently · `session.restore` reports which
pane received the content · `sidebar.width` distinguishing a clamped request from an honoured one ·
`session.new` refuses an unknown workspace *(decision 1)*

Every item is the same defect class as the control-byte refusal already shipped in #213: a call that
appears to succeed while doing something other than what was asked. Same test shape throughout.

### P3 · agwinterm · persistence
`session.context` (text set over the API, shown in title bar and tree, surviving restart) ·
`restore.capture` (fill captured-command slots on demand, not only at exit)

Alone because it is the first restore-format change of this backlog, and lite has to mirror the
format later — as an additive line type, the way `P` was.

### P4 · agwinterm · splits get their full shape
axis on `session.split` (`h|v`, surviving restore) · `session.split.close` · `session.split` **returns
the pane id** · `session.swap`

One subsystem, one restore-format change, one test area. The returned id is a gap inside our own
family: lite already returns it, and a hidden split shell has no other handle.

### P5 · agwinterm · overlays stop being session-wide
`--pane left|right` on the overlay verbs, flag omitted keeping today's session-wide behaviour ·
`session.overlay.copy` · `session.overlay.text`

Kept separate from P2–P4 because it is the only control-API batch with real renderer work. #213
closed the honesty half — a pane id is refused rather than silently widened — so this closes the
capability half: a review TUI in the right pane stops blanking the left pane the user is reading, and
an overlay's own output becomes readable at all.

---

## Wave 2 — agliteterm catches up

44 verbs behind. Taken in one go that is not a plan, it is a rewrite. Ordered by what an agent
actually hits first.

### P6 · lite · `selection.*`
`selection.all` · `selection.clear` · `selection.copy` · `selection.finalize`

The sharpest gap in the product: lite can *read* a selection (`session.copy`) but not make, clear or
finalise one, so the lite QA cases that drive selection through the API silently do nothing — the
exact failure mode `qa/product.md` exists to prevent. The model already exists behind `session.copy`.
*Needs decision 2.*

### P7 · lite · selection by keyboard and mouse
mark mode (Ctrl+Shift+M, arrows, Enter copies) · Select All · drag-autoscroll past the pane edge ·
the posted `WM_MOUSEWHEEL` that never reaches lite's handler (harness finding, 0.17.11)

Same model as P6, different surface — split because it is UI work with a different test shape.

### P8 · lite · mirror Wave 1 — dissolved into per-batch mirrors
`surface.cursor` · `statusChangedAt` · `version` → **P1-lite** (agliteterm
`docs/plans/2026-09-03-p1-lite-mirror.md`, 2026-09-03) · `--stdin` · size-percent validation →
P2-lite · `session.context` → P3-lite. Each runs right after its agwinterm batch merges.

### P9 · lite · driving a pane
`session.readonly` **first** — it is how you stop stray keys reaching a running agent — then
`session.focus` · `session.switch` · `session.resize` · `session.background` · `session.search` ·
`session.bind` · `session.restore`

### P10 · lite · the configuration surface
`config.get/list/set` · `theme.list/set` · `omp.list/set` · `font` · `settings.open` ·
`keymap.reload` · `profiles.list/reload` · configurable scrollback (`agwcore_emu_set_scrollback`,
which lite never calls, so its cap is the core default)

Not a straight port: lite keeps settings in `HKCU\Software\agliteterm` behind a Properties dialog.
The real work of this plan is naming the keys once. `config.get/set` over the API is what lets an
agent set up a workspace without a human clicking.

### P11 · lite · commands, installers, agent integration
`command.list/run/leader` · `install.cli/hooks/shell` · `app.update` · `claude.adopt/yolo/update`

lite ships `install.skill` only. `claude.adopt` — pointing the terminal at an already-running Claude
session — is the interesting one.

### P12 · lite · the remainder
`broadcast` · `notify` · `dashboard` · `restore.clear` · `workspace.move`

Closes the verb list except images. **After this, lite answers every agwinterm verb but `image.*`.**

---

## Wave 3 — the UI gaps against agterm

### P13 · agwinterm · `session.hud` and `--position`
A transient overlay for status an agent wants seen without printing into the terminal, anchored to
one of nine positions.

### P14 · agwinterm · quick terminal
Size as 40–90% of the screen · a system-wide hotkey that summons it over any app

The hotkey is a `RegisterHotKey` plus a policy decision about stealing a chord machine-wide, which
the plan should state rather than assume.

### P15 · agwinterm · navigation and the small sweep
`workspace.go next|prev` · `toggle_workspace_collapse` · keymap entries accepting several chords for
one action separated by `|` · sidebar tooltips revealing truncated names · the tree naming the shell
holding each pane's foreground process · cursor shape and blink settings

Workspaces are currently keyboard-unreachable without a chord.

### P16 · agwinterm · `control.pick`
The native picker driven over the API. Half the agterm cookbook is built on it — project launcher,
workspace picker, conversation picker, backlog picker, SQLite browser — and nothing here can do that
without shipping a picker binary of its own. **The biggest single capability gap and the biggest
plan; expect more than two revmux rounds.** *Needs decision 3.*

### P17 · lite · mirror Wave 3
Whatever of P13–P16 survives contact, mirrored. Sized once P13–P16 are real rather than guessed at.

---

## Deferred, deliberately

- **`image.*` in lite** (`image.show`, `image.sixel`, `image.clear`, `image.frame`). lite renders no
  images at all; this is a wave of its own, not a batch. `image.frameshm` stays agwinterm-only —
  shared-memory frame delivery is what ConPTY makes necessary, and lite has no consumer for it.
- Everything under *Not chasing* in [agterm-parity.md](../agterm-parity.md): zmx live/remote
  sessions, the GPU buffer release measurement, and the macOS-only items.

---

*Companion to the two trackers. When a batch lands, tick it in the tracker it came from — a line that
still says "missing" long after it shipped is worse than no tracker.*
