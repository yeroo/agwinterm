# agterm parity tracker

What [umputun/agterm](https://github.com/umputun/agterm) has that **agwinterm** and **agliteterm** do
not, and what we have closed.

- Compared against **agterm v0.26.0** (2026-09-02), releases 0.22.0 → 0.26.0.
- **The running order lives in [plans/2026-09-03-parity-batches.md](plans/2026-09-03-parity-batches.md)** —
  this file is what is missing, that file is which batch builds it and when.
- Ours: agwinterm **0.17.12** released (main is ahead), agliteterm **0.17.13** released.
- Update this file in the PR that closes an item. A line that says "missing" long after it shipped
  is worse than no tracker.
- **This supersedes the earlier gap docs in this directory** (`agterm-gap-analysis*.md`,
  `agterm-roadmap.md`), which compare against agterm v0.10.2 and the July waves. Those describe a
  version of both products that no longer exists; read them as history.

agterm is macOS + libghostty; we are Win32 + ConPTY. Some items do not port at all, and those are
named as such rather than left to look like a backlog nobody is working on.

---

## Closed

| Item | agterm | Closed by |
| --- | --- | --- |
| `session.text --lines N` reaching scrollback | 0.25.0 | agwinterm #213 |
| `session.type` refuses control bytes (NUL truncation) | 0.25.0 | agwinterm #213, lite #15 *(open)* |
| An escape hatch that works: `--allow-control` | — | agwinterm #214 *(open)*, lite #15 *(open)* |
| `session.overlay` stops widening a pane id to its session | — | agwinterm #213 |
| `session.write` (display injection) in lite | — | lite #15 *(open)* |
| Per-session split panes (lite matched agwinterm) | — | lite #13 |
| `session.new --workspace` in lite | — | lite #13 |
| `surface.cursor` — the caret column | 0.24.0 | agwinterm #221 *(P1)*, lite: P8 |
| `statusChangedAt` on the tree's session node | 0.25.0 | agwinterm #221 *(P1)*, lite: P8 |
| `agwintermctl version` | 0.25.0 | agwinterm #221 *(P1)*, lite: P8 |
| `--stdin` on `session type` / `session write`, rejecting invalid UTF-8 | 0.26.0 | agwinterm #226 *(P2)*, lite: P2-lite |
| `session.overlay.open` / `resize` validate `--size-percent` as 1–100 | 0.26.0 | agwinterm #226 *(P2)*, lite: P2-lite |
| `session.restore` reports which pane received the content (+ `restoreCommands` in `tree`) | 0.26.0 | agwinterm #226 *(P2)* |
| `sidebar.width` — the width in effect, out of range refused; unknown `sidebar` ops refused | 0.26.0 | agwinterm #226 *(P2)* |
| `session.new` refuses an unknown workspace; a bare `session new` lands in the caller's workspace | — | agwinterm #226 *(P2)*, lite: P2-lite *(caller half)* |
| `session.context` — one line per session, shown in the title bar, the row and the palette, in `tree` as `context`, restored after a restart | 0.26.0 | agwinterm #233 *(P3)*, lite: P3-lite |
| `restore.capture` — fill the captured-command slots on demand, per-pane reply, `capturedCommands` in `tree` | 0.26.0 | agwinterm #233 *(P3)*, lite: P3-lite |
| `session.split` replies with the **pane id** (the split pane's on `on`, also when already split; the survivor's on `off`) | — | agwinterm *(P4, PR pending)*; lite had it (#13) |
| Horizontal splits — `--axis vertical\|horizontal` on `session.split` (agterm's words: vertical = left/right, horizontal = top/bottom), per session, re-orients live, survives restore, `axis` in `tree`; `session.focus` takes `primary\|split\|left\|right\|top\|bottom\|other`, `session.resize` gains `--grow-top` / `--grow-bottom` | 0.23.0 | agwinterm *(P4, PR pending)*, lite: P4-lite |
| `session.split.close` — closes **either** pane, the survivor's id as the reply; a one-pane session refused | 0.23.0 | agwinterm *(P4, PR pending)*, lite: P4-lite |
| `session.swap` — the panes exchanged; axis, ratio sequence, focus's pane, overlays, status and **every id** kept | 0.26.0 | agwinterm *(P4, PR pending)*, lite: P4-lite *(expensive there — see [lite-parity.md](lite-parity.md))* |

The overlay entry is the *honesty* half only: a pane id is now refused rather than silently widened.
Pane-scoped overlays themselves are still open, below.

The P4 rows carry **two deliberate divergences** from agterm, recorded here rather than left to be
discovered:

- **`session split off` destroys the split shell.** agterm's `off` hides it and keeps the process.
  Pre-existing and unchanged by P4; `session split close` is the same destruction with a pane target,
  and what it adds is that the target may be **either** side (`off` can only keep pane 0).
- **A swap moves panes, never ids.** agterm addresses a session's panes by role (`primary|split`), so
  its swap moves the session's public identity to the other shell. Ours are addressed by **id**, and an
  agent holding a pane id must keep reaching the same shell after a swap. So a swap reverses the pane
  order, keeps the axis and the ratio sequence (the left/top box keeps its size; the contents change
  places), moves the focus with its pane and leaves every id where it was — the session id keeps naming
  the pane it always named, now on the other side. The invariant "the first pane carries the session
  id" is therefore "exactly one pane carries the session id", and the resolver's exact-pane-first order
  is what keeps the session id naming that pane.

The P1 rows were items **1, 2 and 8** of the open list before P1 closed them, the P2 rows were
item **6**, the `sidebar.width` half of item **5** and two sub-items of item **11**, the P3 rows
were item **1** and the `restore.capture` half of item **5** (which closes that item), and the P4
rows were items **2 and 3** (`session.swap`, and the three split gaps); the list below is
renumbered after each, so read those plans' "closes items ..." against this note rather than
against the current numbering. P3 is the first batch to touch the per-window restore file, and it
set the format rule the later batches inherit: additive keys only, no version field, every loaded
value validated (`plans/completed/2026-09-05-p3-persistence.md`); P4 is the second, and follows it
with one additive key (`Axis`, written only for a split horizontal session, validated on load). `--stdin` is `session type` / `session write` here — `quick type` has
no separate verb because the quick terminal is a pane with an id that `session type --target`
reaches. The agliteterm mirrors are the per-batch `P<n>-lite` plans, tracked in
[lite-parity.md](lite-parity.md) — agwinterm-first, so the contract is fixed before it is copied.

---

## Open — agent-facing control API

Everything here is portable. Ordered by what costs us work today.

### 1. Pane-scoped overlays, and reading an overlay
**agterm 0.24.0 (`session.overlay.copy`, `session.overlay.text`), pane scoping 2026-08-01.**

An overlay covers the whole session, so a review TUI aimed at the right pane blanks the left pane the
user is reading. agterm delivers it as `--pane left|right` on the existing verbs, with the flag
omitted keeping session-wide behaviour. Separately, `session text` addresses the pane *underneath* an
overlay, so an overlay's own output cannot be read at all.

**Size:** medium (the scoping is real work in the renderer); the two read verbs are small.

---

## Open — UI

### 2. `control.pick` — the native picker, driven over the API
**agterm 2026-07-28.** Half the agterm cookbook is built on it: project launcher, workspace picker,
conversation picker, backlog picker, SQLite browser. Nothing here can do that without shipping a
picker binary of its own.
**The biggest single capability gap.** Size: large.

### 3. `session.hud` and `--position`
**agterm 0.22.0 / 0.24.0.** A transient overlay for status an agent wants seen without printing into
the terminal, anchored to one of nine positions. Size: medium.

### 4. Quick terminal: screen percentage, and a global hotkey
**agterm 0.24.0 / 0.25.0.** Sizes as 40–90% of the screen, and a system-wide hotkey summons it over
any app. Ours is a fixed size with no global hotkey. Size: medium (the hotkey is a `RegisterHotKey`
and a policy decision about stealing a chord system-wide).

### 5. Workspace navigation and keymap alternatives
**agterm 0.23.0 / 0.24.0.** `workspace.go next|prev`, `toggle_workspace_collapse`, and keymap entries
that accept several chords for one action separated by `|` (a native chord *and* a tmux-style
leader). We have `session go`; workspaces are keyboard-unreachable without a chord. Size: small.

### 6. Smaller things from 0.26
Cursor shape and blink settings; sidebar tooltips revealing truncated names; the tree naming the
shell holding each pane's foreground process. (The other two that were here — `session.restore`
reporting the pane, and `--size-percent` validated rather than clamped — closed in P2.)

---

## Not chasing, with reasons

### Live and remote sessions (zmx)
**agterm 0.26.0.** Processes survive a restart through a bundled zmx multiplexer; sessions on another
Mac attach over SSH. Requires zsh as the login shell and macOS.

**We are partly there by a different route, and it is worth knowing where the line is.** Our pty-host
outlives the UI by design, and lite *adopts* live shells on restart rather than relaunching them — so
"quit the app, come back to running processes" already works here. What we do not have is surviving a
**reboot**, or attaching to another machine's sessions. agwinterm's `restore-commands` re-runs a
captured command line, which is a much weaker thing: the agent's conversation is gone, only the
invocation comes back.

### GPU buffer release for hidden panes
**agterm 0.26.0.** Hidden panes release their Metal swap chain buffers; their measurement was 1.1 GB
→ 199 MB with 11 hidden panes. Our renderer is Direct2D with a different allocation shape — measure
before assuming it applies, then decide.

### macOS-only
Voice dictation and the accessibility tree (0.22.0), hardened-runtime and TCC entitlements, Apple
notarisation. agwinterm has its own UIA path.

---

## Where we are ahead

Not a one-way list. Ours have, and agterm's control surface does not appear to: `session.readonly`,
shell `profiles`, `omp` theme control, `claude.adopt` / `claude.yolo` / `claude.update`,
`app.update`, `session.metrics` (live cell and pixel geometry) and `image.frameshm` (shared-memory
frame delivery, which ConPTY makes necessary) — the last two on `main` since **#220**.

## agliteterm, against agwinterm

Tracked separately, in **[lite-parity.md](lite-parity.md)** — the goal there is that the two products
expose the same features, so it is a list with an end, not a survey. Short version as of 2026-09-02:
agliteterm answers **41 of agwinterm's 85** control verbs, the sharpest gap being `selection.*`
(it can read a selection but not make one). It is also *ahead* in three places, which should move the
other way.

---

*The 0.22–0.25 survey this builds on was done by another session on 2026-09-01 and lives at
`C:\Users\boris\AI\docs\agterm-parity.md`; this file is the maintained copy, extended to 0.26.0 and
corrected where we have since closed items.*
