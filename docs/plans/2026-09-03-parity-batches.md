# Parity backlog, cut into runnable batches

The gap lists in [agterm-parity.md](../agterm-parity.md) and [lite-parity.md](../lite-parity.md) say
*what* is missing. This file says **in what order it gets built**, split so each part is one ralphex
plan, one branch, one PR — run with review steps disabled, then reviewed by revmux.

Each `P<n>` becomes its own plan file in this directory when it is its turn, and moves to
`completed/` when it ships; expanding all of them up front would just produce stale plans. A batch
without a status line below is backlog; **Ready** is not **Shipped**.

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
- **Every batch exits the same way:** its own tests, applicable QA acceptance and the full suite
  green, with independent review. Boris's 2026-09-08 stopping rule is one full review, batched
  fixes and one narrow confirmation when needed. More rounds require a correctness, safety or
  acceptance blocker, or a substantive new change; cosmetic findings do not hold a merge.
  During Claude's rate-limit outage Codex leads, with Codex-only independent revmux reviewers.
  Shared-desktop tests still require the suite token through proven cleanup; unsafe legacy
  fixtures run only on disposable CI runners until hardened.
- **A release follows every agwinterm batch that adds a verb.** agliteterm's CI proves the contract
  against the *released* `agwintermctl` (its `fetch-native.ps1` takes the `latest` release), so a lite
  mirror cannot go green until the verb it mirrors has shipped in a CLI. Only `*.*.9` / `*.*.18` /
  `*.*.27` reach Chocolatey and winget; a per-batch release skips those numbers unless a package
  checkpoint is intended (P1 shipped as 0.17.10, 0.17.9 skipped).

## Blocked on a decision from you

Three contract questions gate their batches. They are cheap to answer and expensive to guess.

| # | Question | Gates |
| --- | --- | --- |
| 1 | ~~`session.new` with an unknown workspace: **refuse** (lite does) or **fall back to active** (agwinterm does)?~~ **Answered: refuse** (2026-09-04, in P2 #226). lite already refused, every other workspace-taking verb here already refused, and the contract file states the principle in prose for `workspace.select`. A bare `session new` with *no* workspace named now lands in the **caller's** workspace, not the active one (P2 task 5a, from a live agliteterm report). | P2 |
| 2 | ~~On the alt screen, do selections scroll into main-screen history (lite) or pin to row 0 (agwinterm)? Both are self-consistent; they cannot both stay.~~ **Answered: pin to row 0** (Boris, 2026-09-07) — agwinterm's way and most terminals'. lite stops scrolling into main-screen history while the alt screen is up, and `selection all` on the alt screen is the app's screen only, in both products. Lands in P6-lite (the verb's range) and P7-lite (the wheel and the drag). | P6, P7 |
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

**Shipped:** #226 (2026-09-04, four revmux rounds) — plan at
[completed/2026-09-04-p2-honesty.md](completed/2026-09-04-p2-honesty.md); contract steps in this
PR; release to follow, as #224 followed P1. Grew one item on the way: a bare `session new` lands in
the **caller's** workspace (task 5a). Round-4 leftovers: #228. Mirror: agliteterm P2-lite —
**shipped** as agliteterm #26 (2026-09-04; plan `docs/plans/2026-09-04-p2-lite-mirror.md` there,
closing lite #23 and #24 with it; its `--size-percent`, `sidebar width`, `caller` and `--stdin`
checks SKIP against the released `agwintermctl` until the release that carries #226 is tagged).

Every item is the same defect class as the control-byte refusal already shipped in #213: a call that
appears to succeed while doing something other than what was asked. Same test shape throughout.

### P3 · agwinterm · persistence
`session.context` (text set over the API, shown in title bar and tree, surviving restart) ·
`restore.capture` (fill captured-command slots on demand, not only at exit)

Alone because it is the first restore-format change of this backlog, and lite has to mirror the
format later — as an additive line type, the way `P` was.

**Shipped:** #233 (2026-09-05) — plan at
[completed/2026-09-05-p3-persistence.md](completed/2026-09-05-p3-persistence.md); contract steps in
the sibling PR #235, as #229 followed #226. Set the restore-format rule the later batches inherit:
additive keys only, no version field, every loaded value validated. Round-4 leftovers from P2
(#227 / #228) remain, in `SessionOverlay`. Mirror: agliteterm P3-lite — **shipped** as agliteterm
#28 (2026-09-05; plan `docs/plans/2026-09-05-p3-lite-mirror.md` there): `session.context` as a `C`
line type after the `S` lines, `restore.capture` as a `K` line with an in-process Toolhelp32 + PEB
query instead of the CIM one, `replayOnRestore` initially a constant `false` (P10b adds default-off
captured replay; P9's explicit pin/binding replay is separate), and a
hidden pane refusing `session context` (the three divergences are in `docs/lite-parity.md`); its
checks SKIP against the released `agwintermctl` until the release that carries #233 + #235 is tagged.

### P4 · agwinterm · splits get their full shape
axis on `session.split` (`--axis vertical|horizontal`, agterm's words, surviving restore) · `session.split.close` · `session.split` **returns
the pane id** · `session.swap`

One subsystem, one restore-format change, one test area. The returned id is a gap inside our own
family: lite already returns it, and a hidden split shell has no other handle.

**Shipped:** #238 (2026-09-06) — plan at
[completed/2026-09-06-p4-splits.md](completed/2026-09-06-p4-splits.md); contract steps in the
sibling PR that follows, as #235 followed #233. Every `session split` reply is a pane id (a string,
so the shipped `split off` conformance step is untouched); the axis is agterm's words (vertical =
left/right, horizontal = top/bottom), per session, live re-orient, `axis` in the tree, and the
second additive restore key under P3's rule (`SessionState.Axis`, written only for a split
horizontal session, validated on load); `session.split.close` closes either pane; `session.swap`
moves panes, never ids — the one divergence P4 adds to agterm's swap, recorded in
`docs/agterm-parity.md` together with what it costs a downgrade to 0.17.12 (a swapped session's
two panes both carry the session id there; checked once, no `.bad`). Pane ids are durable across
split, close, swap and restore (`CreateSession` takes pane 0's id separately). Mirror: P4-lite —
**shipped** as agliteterm #30 (2026-09-06; plan `docs/plans/completed/2026-09-06-p4-lite-mirror.md`
there), swap included (Boris's call; the hidden session is drawn in the owner's other slot, so a
swap is one flag read — no tree identity moves). The contract pins the swap in #241. Four revmux
rounds, no Major after r1; the divergences P4-lite records are in `docs/lite-parity.md` ("Splits as
sessions"). Released as **v0.17.13** (#243, 2026-09-06). Leftovers, all merged: P4's round-4
record #239 → #244; the P2/P3 leftovers #227 / #228 / #234 / #246 closed by #248 / #247 / #245 /
#249 (the `session.overlay` per-open block completion and queued resize, the P2 round-4 sweep, the
P3 round-2 sweep, the recents clock); lite's #29 / #22 / #25 → agliteterm #35 / #37 / #38.

### P5 · agwinterm · overlays stop being session-wide
`--pane left|right` on the overlay verbs, flag omitted keeping today's session-wide behaviour ·
`session.overlay.copy` · `session.overlay.text`

Kept separate from P2–P4 because it is the only control-API batch with real renderer work. #213
closed the honesty half — a pane id is refused rather than silently widened — so this closes the
capability half: a review TUI in the right pane stops blanking the left pane the user is reading, and
an overlay's own output becomes readable at all.

**Shipped:** #250 (2026-09-07) — plan at
[completed/2026-09-07-p5-pane-overlays.md](completed/2026-09-07-p5-pane-overlays.md) (the QA
capture `completed/2026-09-07-p5-pane-overlay.png` beside it); contract steps in the sibling PR that
follows, as #235 followed #233 (`open --pane left`, `overlay text --pane left`, `close --pane left`,
`session text --all`, and the three refusals). `--pane left|right` on `open` / `close` / `result`
(agterm's words; `left` = pane 0 and `right` = pane 1 whatever the axis; a pane id on `--pane` is
refused as #213 refuses it on `--target`), `overlay copy` / `overlay text [--all|--lines N]` and
`session text --all` (one reader, two verbs), `paneOverlays` in the tree; the slot lives on the pane,
so it moves with a swap by construction and dies with `split close` / `split off` / the shell
exiting. Scoping + `copy` + `text` only (`--cwd`, `--follow`, `--background-color` stay out —
#139 / #88). The two divergences, recorded in `docs/agterm-parity.md`: the session-wide `result`
stays window-wide and `ok` (only `result --pane` is agterm's per-slot arm), and `--pane` with
`--size-percent` / `resize --pane` are refused at both ends. No restore-format change (overlays are
never persisted), no ABI change. The release (**0.17.14** — Boris tags) follows the contract PR.
Mirror: **P5-lite — shipped** (agliteterm #40, 2026-09-07, four revmux rounds; plan
`docs/plans/completed/2026-09-07-p5-lite-mirror.md` there): a pane overlay is IN-WINDOW — a hidden
`Session` hung on the shell it covers as that pane's surface — not a popup sized to the pane rect;
the contract's steps run the same on both products. Six recorded differences (`docs/lite-parity.md`,
"Mirrored: what P5 owed lite"), the notable ones: lite's `session text` defaults to the whole buffer
(`--all` its explicit spelling); `exit N` rides an FTCS `OSC 133;D` mark the wrapper emits (the
command's own claim); a cover id on the session verbs is refused where agwinterm lands it on the
covered session.

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

**Shipped:** agliteterm **#45** (2026-09-07, three revmux rounds — round 1's two Majors were one
mechanism: the popup guard named only the overlay while quick and scratch share `paintPopup`; round
2's outside re-review caught the plan itself, `copy` clears even a blank selection; plan
`docs/plans/2026-09-07-p6-lite-selection.md` there), with decision 2 (pin to row 0). The
agwinterm half is the **contract PR #256** (the sibling of #235 / #252, merged AFTER #45 so lite's
`check-contract` stays green until it runs `-Update`): the four `selection.*` steps plus a
`session copy` read-back, three refusals — and the defect lite's plan found here, the four selection
verbs and `session.paste` answering `ok:true` `no session` on a target that resolves to no pane
(and `session.copy` `ok:true` `""`), fixed in the same PR (`HostReply` + `RefusePrefix`; `session.copy`
refuses with the read verbs). Six recorded differences in
`docs/lite-parity.md`, "Mirrored: what P6 owed lite". Left for their own issue: `session.readonly`
and `session.search` still answer `ok:true` on no session.

### P7 · lite · selection by keyboard and mouse
mark mode (Ctrl+Shift+M, arrows, Enter copies) · Select All · drag-autoscroll past the pane edge ·
the posted `WM_MOUSEWHEEL` that never reaches lite's handler (harness finding, 0.17.11)

Same model as P6, different surface — split because it is UI work with a different test shape.

**Shipped:** agliteterm [#50](https://github.com/yeroo/agliteterm/pull/50), 2026-09-08,
main `1e0903a`. Mark mode, seeded/rebindable Select All, word/line mouse selection, drag-autoscroll,
posted wheel handling and alternate-screen pinning work on frame and popup surfaces. The merged
P6 contract is mirrored. Local Strict selection acceptance and disposable Windows CI both passed
70 checks; the full Strict suite also passed. Clipboard/registry and owned-process cleanup are
guarded in the selection fixture. Remaining legacy shared-desktop fixture safety is lite #51.

### P8 · lite · mirror Wave 1 — dissolved into per-batch mirrors
`surface.cursor` · `statusChangedAt` · `version` → **P1-lite — shipped** (agliteterm #20,
2026-09-04, six revmux rounds; plan `docs/plans/completed/2026-09-03-p1-lite-mirror.md` there) ·
`--stdin` · size-percent validation · `sidebar width` · the caller-workspace default for a bare
`session new` → **P2-lite — shipped** (agliteterm #26, 2026-09-04) · `session.context` ·
`restore.capture` → **P3-lite — shipped** (agliteterm #28, 2026-09-05) · `--axis` · `split close` ·
`swap` · the pane-id reply → **P4-lite — shipped** (agliteterm #30, 2026-09-06; released as lite
0.17.15) · `--pane left|right` · `overlay copy` / `text` · `session text --all` → **P5-lite —
shipped** (agliteterm #40, 2026-09-07; see P5's line). Each runs right after its agwinterm batch
merges.

### P9 · lite · driving a pane
`session.readonly` **first** — it is how you stop stray keys reaching a running agent — then
`session.focus` · `session.switch` · `session.resize` · `session.background` · `session.search` ·
`session.bind` · `session.restore`

**Shipped:** agliteterm [#52](https://github.com/yeroo/agliteterm/pull/52), main `190e514`, tested
candidate `c77b256` (2026-09-08). Six verbs implemented; `focus` already shipped in P4-lite and `background`
remains refused because lite draws no images. Guarded local acceptance passed 188 combined P7/P9
checks, pure driving checks passed 30, and the final two-reviewer Codex-only confirmation was clean.
The full Strict Windows CI suite passed, including the same 188 checks. Explicit pins/bindings replay on fresh
restored shells, never adopted shells; P9 itself does not replay captures (P10b adds opt-in replay). Differences and remaining
find-bar/divider-drag work are recorded in `docs/lite-parity.md`.

### P10 · lite · the configuration surface — P10a and P10b shell configuration

P10a [lite #54](https://github.com/yeroo/agliteterm/pull/54) implements `config.get/list/set`,
`theme.list/set`, `settings.open`, `keymap.reload`, configurable new-replica scrollback and
copy-on-select. Fourteen keys share validation and canonical formatting. Registry setters and UI
actions persist only changed fields, rather than stale multi-setting snapshots. Modal editing
blocks mutations; menu and toolbar checks reflect API changes.

The local combined suite passed 287 checks; the isolated driving/configuration modes passed
118/99, plus 172 pure unit checks. The narrow Codex-only confirmation found no blockers from
two healthy independent reviewers. Exact CI/delivery evidence and deferred metadata are in the PR.

P10b [lite #57](https://github.com/yeroo/agliteterm/pull/57) implements `omp.list/set`,
`profiles.list/reload`, and opt-in captured-command replay. Review and full Strict CI delivery
evidence is linked from the PR. The profile schema explicitly supports name/command/args/cwd, not custom env,
icons or elevation. Font targeting is excluded under Boris's fixed-strike/no-zoom decision.
Current behavior, rollback limits and product differences are recorded in `docs/lite-parity.md`.

### P11 · lite · commands, installers, agent integration
P11 [lite #59](https://github.com/yeroo/agliteterm/pull/59) implements `command.list/run/leader`,
`install.cli/hooks/shell`, `app.update` and `claude.adopt/yolo/update` as one delivery. Custom commands
cover all four modes, palette and leader bindings. Installers preserve unrelated data and retain
backups; Codex configuration is suggested, not rewritten. Agent adoption requires exact live process
identity, and restarts use a guarded PowerShell prompt bridge after proven descendant exit, never
a newest-folder guess or fixed-delay command injection. Failed/no-op updates restart nothing.

Compatibility differences, prompt-bridge requirements and asynchronous outcome semantics are in
`docs/lite-parity.md`. The implementation PR records Codex-only independent review and integration/CI
gates; it must merge before this companion status update. No release/tag is part of P11.

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
