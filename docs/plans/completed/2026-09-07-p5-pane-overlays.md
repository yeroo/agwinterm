# P5 — overlays stop being session-wide

Batch **P5** of the parity programme in
[2026-09-03-parity-batches.md](../2026-09-03-parity-batches.md). Closes item 1 of
[agterm-parity.md](../../agterm-parity.md) ("Pane-scoped overlays, and reading an overlay", as
numbered today) and takes the obligation P4 left it (`Program.Sessions.cs:1700-1702`: a pane-scoped
overlay moves with its pane on a swap). The only control-API batch with renderer work; **no
restore-format change** (an overlay is never persisted — `Program.WndProc.cs:616-617` — and stays
that way).

Gates answered by Boris on 2026-09-07 before this plan was written: `--pane` takes **`left|right`
only** (agterm's words; a pane id stays refused, as #213 refuses it on `--target`); the batch is
**scoping + `copy` + `text` only** (`--cwd`, `--follow`, `--background-color` stay out — #139/#88);
the release after it is **0.17.14**.

## The rule, stated once and quoted everywhere

agterm's reference (`session overlay`, read 2026-09-07), in our words:

> A session has three overlay slots: **one session-wide** and **one per pane**. A session-wide
> overlay covers the whole session (every pane, and any pane overlay under it), as today. A pane
> overlay covers **exactly one pane's box** — always the full box, never floating — and the sibling
> pane stays visible and interactive. `--pane left|right` names the slot: `left` is pane 0 and
> `right` is pane 1 **whatever the axis** (on a horizontal split `left` is the top pane), a
> non-split session accepts `--pane left`, and the flag omitted means the session-wide slot —
> today's behaviour, byte for byte. A pane overlay is that pane's **surface** while it is open:
> keys typed into the focused pane, the mouse inside the pane's box and `--target active` reach
> the overlay; `--target <pane id>` reaches the shell **underneath** (agterm: "`session text`
> reads the surface underneath"); `--target <overlay id>` reaches the overlay from anywhere, and on
> `session overlay` itself names that overlay's slot — the same as passing its `--pane` word — for
> as long as the id resolves (an overlay that closed is reached by `--pane` only); with `--pane`
> naming the other side it is refused. The slot moves with its pane (a swap, a `split close` of the
> other pane) and dies with it (`split close`, `split off`, the shell exiting when that removes the
> pane — a single-pane session keeps an exited shell on screen, and its overlay with it — `session
> close`, the window closing).

This sentence lives in `ISessionHost.SessionOverlay`'s comment; `AgentSkill.cs` and the CLI
header quote it (P4's lesson: state an invariant by CONDITION, once; every copy is a quote, and a
sweep for the OLD wording — "covers the whole session", "session-wide" — is part of the docs task).

Refusals are agterm's phrases **verbatim as the head of the message**, a colon and what our guard
saw after it (the #248 lesson: a refusal names only what its guard SAW):

| agterm phrase | when | ours, after the colon |
| --- | --- | --- |
| `pane not visible` | `--pane right` on a single-pane session (we never hide a pane, so this is the only case) | `session <id> has one pane; pass --pane left or omit --pane` |
| `pane overlay already open` | `open --pane X` while X holds one | `close it first (session overlay close --pane X), or read it (result / copy / text)` — **no silent replace**: the session-wide slot replaces (today, unchanged); the pane slot refuses, agterm's rule |
| `no overlay` | `copy` / `text` / `close --pane` … with nothing in the named slot | `--pane X` names which slot; nothing for the session-wide one |
| `overlay not realized` | `copy` / `text` between `open` and the terminal being up | see Technical Details — probably unreachable here; if it is, the phrase is still the contract for a pane whose `S` has no emulator yet |
| `no selection` | `copy` with nothing selected inside the overlay | — |
| `failed to read surface buffer` | `text` when the emulator read throws | the exception message |
| `overlay still running` | `result --pane X` (or `result --target <X's overlay id>`) while X's overlay is up | — |
| `no overlay result` | `result --pane X` when nothing ran in X since the window opened | — |

Two divergences from agterm, recorded here and in `docs/agterm-parity.md`, not silently:

- **The session-wide `result` stays window-wide and `ok`.** `session overlay result` (no `--pane`)
  keeps answering the LAST overlay exit in the window (`_lastOverlayExit`, `ok:true "no overlay"`
  when none) — shipped, tested (`win32-control.ps1:632-670`), and the P2 leftovers (#227/#228)
  have already been closed on that shape. The **pane** arm is agterm's: per slot, `exit N`, the
  two refusals above — reached by `--pane X`, or by `--target <X's overlay id>` while that overlay
  is up (the rule's "names that overlay's slot"; once it closed the id resolves nowhere and the
  bare form is the window-wide value again). Changing the session-wide arm is a contract break
  for no caller.
- **`--pane` with `--size-percent` is refused at both ends** (CLI exit 2 with "Nothing sent", and
  the server refuses a raw client), and `resize --pane` likewise; agterm calls both a usage error.
  Same words both ends.

## Overview

- **`session overlay open <cmd> --pane left|right [--wait] [--block] [--target]`** — the program
  runs in an ephemeral terminal drawn over that pane's box; the other pane keeps rendering and
  taking input. `--wait` (banner + any key, **in the focused pane only**) and `--block` (the
  caller gets `exit N`; a close before exit gets `closed`) mean what they mean today. Reply: the
  overlay pane id (`<pane id>:overlay:<hex>` — the owner is readable off the id, as
  `<session id>:overlay:<hex>` is today). Without `--pane`: unchanged, including the #213 refusal
  when `--target` names one pane of a split.
- **`session overlay close --pane left|right`** — closes that slot; `ok "no overlay"` when empty
  (the session-wide arm's shape; closing nothing is the P2 class only when something was asked
  for). `session overlay result --pane left|right` — that slot's `exit N`, or the two refusals.
- **`session overlay copy [--pane left|right]`** — `result.text` = the selection made INSIDE the
  overlay (the pane's own `SelectionText`); the clipboard is not touched; `session copy --target
  <pane id>` keeps reading the pane underneath (bare / `active` reads the focused surface, the
  overlay, like `session text`). **`session overlay text [--all] [--lines N] [--pane left|right]`**
  — the overlay's drawn buffer, through `HandleText`'s walk; `--all` and `--lines` exclusive.
  `--all` is added to **`session text` too** (it is the same reader; agterm's `session text` has
  it and ours did not).
- **`tree --json`**: `"paneOverlays":["left"]` / `["right"]` / `["left","right"]` inside the
  session node, **omitted when empty** (like `overlay`), independent of `overlay`.
- **Keys and mouse**: `Ctrl+Shift+W` (`close_session` / `close_pane`, `Program.Input.cs:131`)
  closes the FOCUSED pane's overlay before it would close the pane (agterm's ⌘W); `Esc`
  (`close_cover`) closes the session-wide cover if one is up, else the focused pane's overlay,
  else falls through as today. A click in a pane's box focuses that pane and lands in its overlay;
  the wheel scrolls the surface under the pointer; the divider drag stays available while only
  pane overlays are up (it is disabled only under a session-wide cover, as today).
- **Swap / close**: a pane overlay travels with its pane (`SwapPanes` regrids it — the box
  changed); `split close` / `split off` / a pane's shell exiting dispose that pane's overlay with
  the pane; the survivor keeps its own.

## Context (from discovery)

State today (`src/Agwinterm.Win32/Program.cs`): `Ses.Overlay` `:373`, `OverlaySizePercent` `:374`,
`OverlayWait` `:375`, `OverlayExited` `:376`, `OverlayExitCode` `:377` — one slot per session;
`Pane.OverlayDone` `:324` is already per overlay pane. `_cover` / `_coverKind` (3 = overlay) /
`_ovlOwner` `:103-107`; `_lastOverlayExit` + `_overlayExitLock` `:108-109`.

Lifecycle (`Program.Sessions.cs`): `OverlayOpen` `:766-784` (mints the id, `CreatePane(..., shellWrap:
true, extraEnv)`, the lock-ordered reset of `_lastOverlayExit`, cover only if active,
`WatchOverlayExit`); `CloseOverlayOf` `:787-800` (`OverlayDone.TrySetResult("closed")`, dispose);
`CloseActiveOverlay` `:803`; `WatchOverlayExit` `:806-830` (one-shot; `--wait` → banner state,
else close); `SetActive` `:556-561` drops/re-shows the cover; `CloseSessionInternal` `:1506-1507`;
`SwapPanes` `:1709-1725`; `WM_DESTROY` `Program.WndProc.cs:624`. Geometry: `ContentArea` `:456`,
**`PaneLayout` `:468-488` (the one source of layout truth)**, `PaneAlongAxisAt` `:494-505`,
`CoverRect` `:701-715` (reads `ContentArea`, never a pane box), `RegridCover` `:718-728`,
`RegridSession` `:521-543`. `ActiveSurface()` `:658` = `_cover ?? _active?.ActivePane` — 40
callers (input, selection, targeting, UIA, chrome); the seam.

Render (`Program.Render.cs`): `DrawWindowContent` `:641-676` (full cover vs floating panel over
`RenderPanes`), `RenderPanes` `:726-766` (per pane: watermark, dynamic bg, `RenderTerminal`,
inactive dim, divider), `DrawCoverBadge` `:788`, `DrawOverlayFooter` `:809-811` (reads
`_ovlOwner.OverlayExited`).

Input (`Program.Input.cs`): `PaneAt` `:561-572` and `PaneBox` `:575-591` **short-circuit to
`_cover` whenever one exists**; `ActivePaneView` `:404-417` (mouse-report origin); `FocusPaneAt`
`:420`; the `--wait` any-key close `:1122` and `WM_CHAR` `Program.WndProc.cs:312`; keybinding
gates `:1228-1238`; `AgwValues` `:239-264` (`AGW_PANE` = `overlay` for a cover). WndProc: divider
`:395`, focus-on-click `:397`, wheel `:562-572`, unread `:606`.

Services: `OwningSes` `Program.Services.cs:55-63` (matches `s.Overlay`), unread `:73/:137`,
`ReapOrphanedHostedSessions` `Program.Sessions.cs:910` (overlay ids count as claimed), `FindPaneBy`
`:1233` (`cover: true`), `ZoomPane` `:1243`, `ChangeFontSize` `:1263`, `IsSurfaceVisible` `:668`.
In-app openers of the session-wide slot, unchanged: `Program.Input.cs:210` (`command … --mode
overlay`), `Program.Sessions.cs:1060` (`claude update`), `Program.Services.cs:783/801`.

Control API: `ControlServer.cs:313-328` `case "session.overlay"` (host-level, before `Resolve`;
`TryOverlaySize` `:1185-1202`); `:304` `session.copy`; `:373` + `:708-731` `session.text` /
`HandleText` (no `--all`); tree emission `:444` (`overlay`), `:449` (`overlaySize`), `:461-470`
(`focusedPane`, `splitRatios`, `paneIds` — `paneOverlays` slots in beside them);
`SessionSnapshot` `ISessionHost.cs:14-22`; `ISessionHost.SessionOverlay` `:357-391` (doc lists
the refusals; `SingleSessionHost` `:570`); `Program.ControlHost.cs:820-836` `OverlayTargetRefusal`,
`:840` `NoSessionRefusal`, `:842-937` `SessionOverlay` (resolve on the caller's thread, then
`InvokeOnUiQueued` hops; `--block` awaits `OverlayDone` from inside the open hop `:906-914`;
`:181` `Resolve` — `active` → `ActiveSurface()`; `:744` `SessionCopy` → `PaneForTarget`;
`:1168-1178` `PaneForTarget` / `FindSesForTarget`). CLI: `Ctl/Program.cs:351-368` (no `--pane`, no
`copy`/`text`); usage header `:49` (`session text [--lines N]`), `:62`. Skill:
`src/Agwinterm.Pty/AgentSkill.cs:172-208` (overlays), `:126/:129` (`text` / `copy`), `:286` (swap:
"overlays … don't move" — stale after this batch), `:298`, `:332`. `SwapReply.cs:35` ("overlays
and scratch are session-wide" — stale), `:74-77` (cover-id refusal, stays).

Tests: `tests/Agwinterm.Pty.Tests/OverlaySizeTests.cs` (whole file), `ControlApiTests.cs:30-35,
82-89, 240-263`, **`SessionSplitTests.cs:810-838`** (`Swap_LeavesEverythingElseWhereItWas…` pins
`overlay`/`overlaySize` byte-identical across a swap — still true for the session-wide slot; the
test grows a pane-overlay half that MOVES), `FakeSessionHost.cs:536-570` (+ `:20/:25/:239`);
`tests/integration/win32-control.ps1:619-817` (overlay block) and `:1136-1245` (the mouse-report
fixture uses a session-wide overlay — its geometry must not move); `tests/conformance/
control-api.json:350-371, 661-671` (untouched here). QA: `qa/control-honesty.md:136-166`;
`qa/panes.md` has no overlay case yet.

Docs: `docs/agterm-parity.md:47-48` (the honesty half only) and `:91-99` (the item);
`README.md:91, 217, 231, 269-288`; the batch index `:121-128`.

## Constraints

- **Do not touch `tests/conformance/control-api.json`.** The steps (`open --pane left` → string;
  `close --pane left`; `overlay text --pane left` → object with `text`; `session text --all`;
  refusals: `open --pane right` on a single pane, `open --pane left --size-percent 40`, `copy` with
  no overlay) land in the sibling contract PR after this merges — as #235 followed #233.
- **The session-wide slot is unchanged in every observable way**: the same replies, the same
  `_lastOverlayExit` window-wide `result`, the same #213 refusal for a pane-id target without
  `--pane`, the same cover rendering and metrics (`win32-control.ps1:1136-1245` must pass with its
  numbers untouched). No existing test is weakened; `OverlaySizeTests` stays green as it is.
- **A pane overlay is always full-pane.** `--pane` + `--size-percent` and `resize --pane` are
  refused at the CLI (exit 2, "Nothing sent") AND the server (a raw client); nothing opens.
- **`left` = pane 0, `right` = pane 1, whatever the axis** — one sentence, stated in the rule.
- **No restore-format change, no pty-host protocol change, no ABI change** (`tools/check-abi.ps1`
  v18 both sides). Overlays are not persisted.
- **Refusals leave the world untouched**; every host method that answers with state uses
  `InvokeOnUiQueued` and re-resolves inside the hop (the #234 wording for a hop that cannot run).
- **Refusal words and reply shapes live in `Agwinterm.Pty`** where the fake exercises them: a
  `OverlayPanes` (or similar) static holding the two words, the parse, and every refusal string
  from the table — the `SplitAxes` / `SwapReply` shape. The Win32 host and the fake both call it;
  no refusal string is typed twice.
- **One overlay-slot type.** Whatever holds `Overlay` / `Wait` / `Exited` / `ExitCode` (+
  `SizePercent` for the session-wide one) is ONE class used for both the session-wide slot and the
  per-pane slots — not five fields copied onto `Pane`. `Pane.OverlayDone` stays on the overlay
  pane itself (it is per program run).
- **Every path that finds, regrids, disposes, reaps, or counts an overlay pane handles both slot
  kinds** — the Context list is the checklist (`OwningSes`, `FindPaneBy`, `IsSurfaceVisible`,
  unread, `ReapOrphanedHostedSessions`, `RegridSession`, `CloseSessionInternal`, `WM_DESTROY`,
  `ZoomPane` / `ChangeFontSize`, UIA). A missed one is a leak or a crash, not a Minor.
- Cross-cutting safety rules from `qa/product.md` in full: sandbox instance (`--pipe` and
  `--app-id`), never `keybd_event` / `SendInput`, `PrintWindow` never `CopyFromScreen`, never the
  real profile.

## Testing Strategy

- **Unit** — `tests/Agwinterm.Pty.Tests/PaneOverlayTests.cs` (new; sectioned per Overview item):
  parse (`left`/`right` accepted, `Left`/`top`/a pane id/`""` refused with both words named,
  nothing opened); `open --pane` on a split (tree `paneOverlays`, both panes independently, second
  open on the same pane refused and the first still there); `--pane left` on a single pane ok,
  `--pane right` refused `pane not visible`; `--pane` + `size-percent` refused, `resize --pane`
  refused; `close --pane` empty → `ok "no overlay"`; `result --pane` three states; `copy` / `text`
  each error verbatim, `text --all` vs `--lines` exclusive (on both verbs); the session-wide slot
  and a pane slot coexisting (`overlay` and `paneOverlays` both in the tree); **swap moves
  `paneOverlays` and keeps `overlay`/`overlaySize`** (`SessionSplitTests.cs:810-838` grows the
  half); `split close` of a pane with an overlay drops its entry; the fake models a pane slot and a
  `SelectionText` per cover pane. `ControlServerTests`: `HandleText` `--all`.
- **Integration** — `tests/integration/win32-control.ps1`, a new pane-overlay block after `:817`:
  split; `open --pane right "cmd /c <marker loop>"` → id; `session text --target <left pane>` still
  the shell and `session type --target <left pane>` still lands (the left pane is interactive);
  `session text --target <overlay id>` shows the marker; `overlay text --pane right` = the same
  buffer; `overlay copy --pane right` → `no selection`, then `selection all --target <overlay id>`
  → `copy` returns the marker and the clipboard is unchanged; `tree` `paneOverlays == ["right"]`;
  `session swap` → `["left"]` and `session metrics --target <overlay id>` equals the new box;
  `open --pane left --size-percent 40` exit 2 + nothing opened; `close --pane left` → `no
  overlay`; `split close --target <the pane with the overlay>` → `paneOverlays` gone, the overlay
  id resolves nowhere, no orphaned host process. The existing overlay block and the mouse fixture
  pass unchanged.
- **QA** — `qa/panes.md` gains a case: a pane overlay in the right pane with the left pane
  typed into (PrintWindow capture: left pane shows the typed text, right pane shows the program,
  divider intact; the `--wait` banner sits at the bottom of the RIGHT box only); the `.png` beside
  the plan as P4 did.
- Each task's checks pass before the next task starts.

## Progress Tracking

- Mark completed items with `[x]` immediately when done
- Add newly discovered tasks with ➕ prefix
- Document issues/blockers with ⚠️ prefix
- Update plan if implementation deviates from original scope

## Implementation Steps

### Task 1: the vocabulary and the slot type, with the fake
- [x] `src/Agwinterm.Pty/OverlayPanes.cs` (name free): `Left = "left"`, `Right = "right"`,
      `TryParse(string?, out int index, out string? refusal)` (absent → -1 = session-wide; a
      non-string or any other word refused naming both words and the absent form); the refusal
      strings from the table as constants/builders, each starting with agterm's phrase; a
      `SizeWithPane` refusal for `--pane` + `--size-percent` and a `ResizeWithPane` one. One
      comment states the rule sentence (the canonical copy is `ISessionHost.SessionOverlay` —
      point at it, do not paraphrase it)
- [x] `ISessionHost.SessionOverlay(string? target, string action, string? command, int sizePercent,
      bool wait, bool block, string? pane)` — `action` gains `copy` and `text`; the doc comment
      carries the rule sentence and the per-action reply: `open` → id, `close` → `closed` /
      `no overlay`, `result` → `exit N` (pane arm: the two refusals), `copy` → the text, `text` →
      the text. Since `text` needs `all`/`lines`, add them as a small `OverlayTextArgs`/two
      parameters — decide once, in the interface. `SingleSessionHost` refuses everything with a
      pane
- [x] `SessionSnapshot` gains `IReadOnlyList<string> PaneOverlays` (default empty) at the END;
      `ControlServer` emits `"paneOverlays":[…]` beside `paneIds` **only when non-empty**
      (comment: absence = none, like `overlay`; a caller reads the words back verbatim)
- [x] server: `session.overlay` reads `pane` with the strict reader; `--pane` with a present
      `size-percent` → refused before the host is called (both ends, same words); `resize` with a
      pane → refused; `copy`/`text` results are objects `{ "text": … }` (agterm's `result.text`);
      `text` takes `all` (bool) and `lines`, exclusive → refusal naming both. **`session.text`
      gains `all`** (`HandleText`: `all` → `take = rows + hist`; `all` + `lines` refused)
- [x] fake: per-pane overlay slots (`FakeSession.PaneOverlays`), a selection text per cover pane
      and a buffer per cover pane, the refusals via `OverlayPanes` — mirroring the app's order;
      `PaneOverlayTests.cs` sections for everything the fake can prove (parse, tree, refusals,
      coexistence, `close` empty, `result` states, `copy`/`text` errors, `text --all`, swap moves
      the slot, `split close` drops it)
- [x] run the .NET suite — must pass before task 2

### Task 2: the slot on the pane, and its lifecycle
- [x] one slot class (e.g. `OverlaySlot { Pane Term; int SizePercent; bool Wait; bool Exited; int
      ExitCode; string LastResult = "no overlay result" }`) replacing `Ses.Overlay` +
      `OverlaySizePercent/Wait/Exited/ExitCode` (`Program.cs:373-377`) and added as `Pane.Overlay`
      (`SizePercent` always 0 there). Every reader of the five fields moves to the slot — grep
      each name; the Context list is the checklist
- [x] `OverlayOpen(ses, pane: Pane?, command, sizePercent, wait, extraEnv)`: `pane is null` =
      today's path verbatim (replace-and-cover); a pane → refuse-if-held is the HOST's check (the
      hop re-resolves), the id is `<pane id>:overlay:<hex>`, `CreatePane` in the PANE's cwd
      (a `CwdOf(Pane)` twin of `CwdOf(Ses)` `Program.Sessions.cs:677`, on `SafeCwd(Pane)`
      `Program.Services.cs:191` — the overlay runs where the shell under it is) with the pane's `FontSize`,
      the slot set under `_overlayExitLock` the way `ses.Overlay` is, `_lastOverlayExit` NOT
      reset (the pane arm has its own `LastResult`), `WatchOverlayExit(ses, pane, term)` writes
      the slot's `Exited/ExitCode/LastResult` and, without `--wait`, closes the slot
- [x] `CloseOverlayOf(ses)` keeps the session-wide slot; `ClosePaneOverlay(ses, pane)` for the
      pane slot (release `OverlayDone` with `closed`, dispose, regrid the pane, redraw). Callers:
      `split close` / `split off` / shell-exit collapse (`ClosePane`, survivor promotion) /
      `CloseSessionInternal` / `WM_DESTROY` — each disposes pane overlays too
- [x] `SwapPanes`: the slot travels (it is on the pane — assert that in a comment) and the boxes
      changed, so regrid both pane overlays with the session (`RegridSession` regrids
      `p.Overlay.Term` to `p`'s box for every pane); drop the "session-wide until P5" clause and
      the `SwapReply.cs:35` sentence
- [x] `OwningSes`, `FindPaneBy` (`cover: true` for pane overlays too — the `--target <overlay
      id>` recipe must keep working), `IsSurfaceVisible`, unread (`:73/:137`),
      `ReapOrphanedHostedSessions`, `ZoomPane` / `ChangeFontSize` (a focused pane overlay zooms
      alone, like a cover), UIA snapshot — each handles both kinds
- [x] run the .NET suite (nothing observable changes yet; the build must be clean)

### Task 3: render, focus, mouse
- [x] `ActiveSurface()` = `_cover ?? FocusedSurface(_active)` where a pane's surface is its
      overlay term while the slot is open, else the pane. One helper, used by `PaneAt` /
      `PaneBox` / `ActivePaneView` / `Resolve(active)` — the 40 callers do not change
- [x] `RenderPanes`: after a pane's own render, if `pane.Overlay` is open, `RenderTerminal` the
      overlay term over the same box with the overlay's metrics (opaque fill first — the
      program's screen, not a blend), the `overlay` badge at the box's top-right, and the
      `--wait` footer inside the box (`DrawOverlayFooter` takes a rect + slot; the session-wide
      call passes `CoverRect()` + the session slot). Inactive-pane dim applies over the overlay
      as over the pane (it is the pane's surface). A session-wide cover / scratch / quick draws
      on top of everything, as today
- [x] `PaneAt` / `PaneBox`: `_cover` short-circuits as today; else the pane under the point,
      **then its overlay term if the slot is open** (origin = the pane's box, metrics = the
      overlay's). `ActivePaneView` the same for the focused pane. Selection, links, right-click
      paste, drag-and-drop and the double-click path then work inside a pane overlay for free —
      say so in a comment rather than adding branches
- [x] focus: `FocusPaneAt` unchanged (it focuses the PANE; the surface follows); `WM_SETCURSOR`
      / `DividerAt` / `WM_LBUTTONDOWN` divider gate: `_cover is null` stays the condition (pane
      overlays do not block the divider); the wheel (`WndProc.cs:562-572`): under a cover as
      today, else the surface under the pointer (`PaneAt`)
- [x] keys: `close_session`/`close_pane` (`Input.cs:131`): kind-3 cover → `CloseActiveOverlay()`
      (today), else the focused pane's open slot → `ClosePaneOverlay`, else the pane; `close_cover`
      (`:163`, `:1228`): a cover, else the focused pane's slot, else fall through (the keybinding
      gate at `:1228-1230` must let the chord reach the pane when neither is up — today's
      behaviour); the any-key close (`:1122`, `WndProc.cs:312`): the focused pane's exited slot
      too; `AgwValues`: `AGW_PANE = overlay` and `AGW_PANE_ID` = the term id when the surface is
      a pane overlay (the program under it keeps `left`/`right`)
- [x] `SetActive`: pane overlays need nothing (they live on panes); the cover logic unchanged.
      `RegridSession` regrids open pane overlays (Task 2) — check `RebuildFont`, `WM_SIZE`,
      `SidebarWidthChanged`, toolbar height each reach it
- [x] `.NET` suite + a live smoke in a sandbox (`--pipe`/`--app-id`): split, open on the right,
      type into the left, PrintWindow — keep the capture for the QA case
- ➕ [x] the host's pane arm for `open` / `close` only (`PaneOverlayAction`, `Program.ControlHost.cs`)
      so the smoke could drive a pane overlay live (the CLI has no `--pane` yet, so the smoke
      speaks raw JSON to the pipe); task 4 adds the agreement check, `--block`, `result`, `copy`,
      `text` and the CLI. Smoke: `.ralphex/p5-t3-smoke.ps1` (untracked, 30 checks: open/refusals/
      type-into-left/text by overlay id vs pane id/`session text` active/metrics/Esc/Ctrl+Shift+W/
      `--wait` banner keyed from the other pane/split close survivor/`pane not visible`); capture
      kept as `docs/plans/2026-09-07-p5-pane-overlay.png` for the QA case (task 5)

### Task 4: the host and the CLI
- [x] `Program.ControlHost.cs` `SessionOverlay`: `pane` parsed via `OverlayPanes` first (a bad
      word is refused before any resolve); with a pane: `OverlayTargetRefusal` is replaced by the
      **agreement check** — `--target` may be the session id or either pane id, but a pane id
      that names the OTHER side than `--pane` is refused (`'{target}' is the right pane; --pane
      left names the other one. Nothing opened.`), and `pane not visible` when the index is past
      `Panes.Count`; each action's hop re-resolves the pane by index inside the UI hop;
      `open --block` awaits the slot's `OverlayDone` from inside the open hop (as `:906-914`);
      `copy` → `SelectionText(term)` under the UI hop (`no overlay` / `overlay not realized` /
      `no selection`); `text` → the emulator walk `HandleText` does, on the term's `S`
      (`ControlServer` owns the walk today — either expose it or give the host an `ISession` back
      and let the server walk it; decide once); `result --pane` → the slot's `LastResult`
      (`overlay still running` while `Term` is up, `no overlay result` when it never ran, else
      `exit N`); `close --pane` → `closed` / `no overlay`
- [x] `session.text` (`Resolve(active)`) reaches a focused pane overlay through `ActiveSurface()`
      — confirm and pin (`session text` with no target while the focused pane holds an overlay
      returns the overlay: the same rule as a cover today)
- [x] CLI (`Ctl/Program.cs:351-368`): `--pane` passed through as a string (the server validates
      the word); `--pane` + `--size-percent` → exit 2 `"--pane and --size-percent cannot be
      combined: a pane overlay is always full-pane. Nothing sent."`; `resize --pane` → exit 2;
      `overlay copy` / `overlay text [--all] [--lines N]` sub-actions; `--all` + `--lines` → exit
      2; `session text --all`; the `--json` and plain forms print `result.text`; the usage header
      gains the overlay line (it has none today — `:49/:62` are the `text`/`copy` lines) quoting
      the rule sentence
- [x] `tests/integration/win32-control.ps1`: the pane-overlay block from Testing Strategy;
      `conformance.ps1 -Strict` still passes on the untouched file
- [x] run the .NET suite and `win32-control.ps1 -Strict` against a sandbox

### Task 5: docs, trackers, QA
- [x] `AgentSkill.cs:172-208`: the rule sentence quoted; `--pane` on `open`/`close`/`result`;
      `copy` and `text` entries with their errors; `session text --all`; `:126/:129` updated;
      `:286` (swap) rewritten — a pane overlay MOVES, the session-wide one stays; `:298/:332`
      checked; grep the OLD wording (`covers the whole session`, `session-wide`, `overlays /
      scratch / quick` in the swap line) across `src/`, `README.md`, `docs/`, `qa/`
- [x] `README.md:91, 217, 231, 269-288`: the pane form in the examples, the refusal paragraph
      grows the pane refusals, `tree` line names `paneOverlays`
- [x] `docs/agterm-parity.md`: item 1 → a Closed row (`--pane left|right`, `session.overlay.copy`,
      `session.overlay.text`, `session.text --all` | agterm 0.24.0 / 2026-08-01 | agwinterm
      *(P5, #PR)*, lite: P5-lite); the two divergences above under the Closed table's notes;
      `:47-48` updated (the capability half is closed now)
- [x] `qa/panes.md`: the pane-overlay case with the PrintWindow capture; `qa/control-read.md` (or
      `control-honesty.md`): `overlay text` reads the overlay, `session text --target <pane>` the
      shell under it — the honesty pair
- [x] batch index `docs/plans/2026-09-03-parity-batches.md`: P5's **Shipped** line (PR number,
      plan path under `completed/`, the two divergences in one clause, contract sibling PR to
      follow, release 0.17.14, mirror P5-lite); P4's line gains "released as **v0.17.13**" and
      the leftovers record (#239 → #244; P2/P3 leftovers #227/#228/#234/#246 closed by
      #248/#247/#245/#249; lite #29/#22/#25 → lite #35/#37/#38)
- [x] `docs/lite-parity.md` (if it lists overlays): P5-lite pending — lite has no pane overlays

### Task 6: [Final] Verify acceptance criteria
- [x] every Overview item implemented; the rule sentence byte-identical (comment markers
      stripped) in `ISessionHost.SessionOverlay`, `AgentSkill.cs` and the CLI header; every
      refusal string in the table reachable from exactly one definition (`OverlayPanes`)
- [x] edge cases, probed live against sandboxes (an untracked `.ralphex/p5-edge-cases.ps1`, as
      P4's): a pane overlay on BOTH panes then `swap` (both move, both ids still resolve); a
      session-wide overlay opened OVER two pane overlays (covers both; closing it reveals both
      still running); `split off` while the split pane holds an overlay (disposed, survivor's
      kept, no orphaned host process — `Get-Process`); the overlay's program exiting under
      `--wait` while the OTHER pane is focused (banner drawn in its own box; a key in the focused
      pane goes to that pane's shell, not to the banner); `Ctrl+Shift+W` with the focused pane's
      overlay up (overlay closes, pane stays); `--pane left` on a single-pane session then
      `split on` (the overlay stays on pane 0, box shrinks, `paneOverlays == ["left"]`); a
      horizontal split with `--pane left` (the TOP box); `session text --all` on a pane with
      history (rows + history); the pane's shell exiting while its overlay is up (both go; the
      survivor promoted; the tree clean); `open --pane left --target <right pane id>` refused
      with nothing opened; window resize with a pane overlay up (the overlay's pty told the new
      box — `session metrics --target <overlay id>`)
- [x] confirm `tests/conformance/control-api.json` is **unchanged** (`git diff main` on it empty)
      and `conformance.ps1 -Strict` passes
- [x] run the full .NET suite, the Rust suite, `tools/check-abi.ps1` (v18), `win32-control.ps1`
      and `restore-roundtrip.ps1` end to end against a sandbox
- ➕ verification record (2026-09-07, Release build, byte-probed fresh): rule sentence byte-identical
      in the three copies (`.ralphex/p5-rule-check.py`, 1071 chars); every table phrase defined once
      in `OverlayPanes` (the `"no selection"` literal at `Program.ControlHost.cs` `SelectionCopy` is
      the pre-existing `session copy` refusal, not the overlay's); `overlay not realized` documented,
      not emitted (no path). Edge cases 82/82 across two sandboxes (`.ralphex/p5-edge-cases.ps1`):
      the plan's list plus `--split-ratio 0.3` before the swap so the regrid is visible in `metrics`,
      `result --pane` in all three states, `copy` on the empty and the unselected slot, `overlay text
      --lines`. One finding worth recording, by design: after `split off` removes the pane carrying
      the session id, `session text --target <session id>` reaches the survivor's SHELL under its
      overlay (the exact-session arm answers the focused PANE, the same arm a session-wide cover has
      always had; only `active` and the overlay id reach the overlay). Suites: .NET 911 (246 Core +
      665 Pty), Rust 36, ABI v18, `conformance.ps1 -Strict` all passed, `win32-control.ps1 -Strict`
      144 PASS / 0 FAIL / 0 SKIP, `restore-roundtrip.ps1 -Strict` all passed, `control-api.json`
      unchanged against main, no orphaned host process afterwards.
- [x] mark P5 **Shipped** in the batch index with the PR number; open the PR from this task so the
      trackers carry the number (P1–P4's practice), with the QA capture in the body

## Technical Details

- **Why the slot is on `Pane`.** The obligation P4 recorded — an overlay moves with a swap — is
  met by construction when the slot lives on the pane `SwapPanes` reverses; a map on `Ses` keyed
  by index would need its own swap step and a second source of truth. The session-wide slot stays
  on `Ses` because it covers the session, not a pane.
- **Why `left`/`right` and not ids on `--pane`.** Boris's call, and #213's: ids on `--target`
  are refused for overlays because the reply would silently widen; the same word on `--pane` would
  be a second vocabulary for one thing. `left|right` is agterm's, and the agreement check keeps
  `--target <pane id> --pane <other side>` from covering a pane the caller did not name.
- **Why `left` is pane 0 on every axis.** agterm addresses panes by role (`primary|split`, shown
  as left/right); ours are ordered. A caller that split horizontally and passes `--pane left` gets
  the top pane — the same pane `session focus left`'s family calls first. Stated once; the CLI
  header and the skill quote it.
- **Why the second open on a pane refuses while the session-wide open replaces.** agterm's rule
  for the pane slot (`pane overlay already open`); the session-wide replace is shipped and tested
  (`win32-control.ps1:673-694`, a blocking open replaced returns `closed`). Changing the shipped
  one is a contract break; making the new one silent is the P2 class.
- **Why the session-wide `result` stays window-wide.** Recorded as a divergence above. The pane
  arm has a `LastResult` per slot because there is no window-wide meaning for "the last pane
  overlay" a caller could want.
- **`overlay not realized`.** `CreatePane` builds the emulator synchronously, so the moment agterm
  names (terminal not yet up) may not exist here; the pty may still be starting. If no code path
  can produce it, the phrase is documented as agterm's, not emitted, and the skill says the term
  is readable the moment `open` returns — do not fabricate a state to match a string.
- **`copy` does not touch the clipboard** — `SelectionText(pane)` is read-only; `CopySelection`
  (which writes the clipboard) is not called. The test proves the clipboard unchanged.
- **`text --all` on `session text` too.** One reader, two verbs; adding the flag to one and not
  the other would make `overlay text --all` the only place it exists.
- **No pane-overlay resize, no floating pane overlay** — agterm has neither; a box is the pane's.

## Post-Completion

*Informational — no checkboxes*

- **Sibling contract PR** after this merges (`tests/conformance/control-api.json`): `session
  overlay open "cmd /c exit 0" --pane left` (string), `session overlay text --pane left` (object
  with `text`), `session overlay close --pane left` (string), `session text --all` (string);
  errors: `open --pane right` on a single-pane session, `open --pane left --size-percent 40`,
  `overlay copy` with nothing open. agliteterm's `check-contract` goes red until P5-lite —
  expected.
- **Release 0.17.14** after the contract PR (Boris tags; lite's PR CI is red by design until it
  exists).
- **P5-lite**: lite's overlay is ONE popup window over the active session (`agliteterm/src/main.cpp`
  ~5861, "one at a time; opening a new overlay replaces the previous"); a pane overlay there is a
  popup sized to the pane rect, or a divergence — the P5-lite plan decides, as P4-lite did for
  swap.
- Review: **revmux**, two rounds minimum, and a narrow round for each fix commit.
