# agterm parity tracker

What [umputun/agterm](https://github.com/umputun/agterm) has that **agwinterm** and **agliteterm** do
not, and what we have closed.

- Compared against **agterm v0.26.0** (2026-09-02), releases 0.22.0 → 0.26.0.
- Ours: agwinterm **0.17.7** released (main is ahead), agliteterm **0.17.13** released.
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

The overlay entry is the *honesty* half only: a pane id is now refused rather than silently widened.
Pane-scoped overlays themselves are still open, below.

---

## Open — agent-facing control API

Everything here is portable. Ordered by what costs us work today.

### 1. `surface.cursor` — the caret column
**agterm 0.24.0.** Reports a surface's cursor column as a bare integer.

The only gap that makes tooling *weaker* rather than more laborious. It is the last check before
typing into another agent's composer: the caret rests at a known column in an empty box, so anything
else means a draft is sitting there and the send should refuse. Without it, `AI/bin/peer-chat.py`
proves emptiness from rendered text against a whitelist of each agent's placeholder string — which
fails both ways (an unrecognised placeholder refuses a safe send; a draft that looks like a
placeholder reads as empty) and rots as agents change their prompt chrome.

**Shape:** `agwintermctl surface cursor --target <pane-id>` printing a bare integer.
**Size:** small. The emulator already tracks `CursorRow`/`CursorCol`.

### 2. `statusChangedAt` on the tree's session node
**agterm 0.25.0.** Epoch seconds recording when a status was last written.

`tree --json` reports `"status":"active"` with no age, so nothing can tell a working agent from one
whose hook died forty minutes ago. `AI/state/agents.json` keeps a `last_seen` per agent purely
because of this — shadow state that exists to answer a question the tree should answer.

**Size:** small. One field on `SessionSnapshot`, set where status is written.

### 3. `session.context` — per-session descriptive text
**agterm 0.26.0.** Text set over the API, shown in the title bar and tree, persisted across restarts.

"What is this pane for" is currently guessable only from a name that also has to be short.

**Size:** small, plus one field in the restore format (lite: an additive line type, the way `P` was).

### 4. Pane-scoped overlays, and reading an overlay
**agterm 0.24.0 (`session.overlay.copy`, `session.overlay.text`), pane scoping 2026-08-01.**

An overlay covers the whole session, so a review TUI aimed at the right pane blanks the left pane the
user is reading. agterm delivers it as `--pane left|right` on the existing verbs, with the flag
omitted keeping session-wide behaviour. Separately, `session text` addresses the pane *underneath* an
overlay, so an overlay's own output cannot be read at all.

**Size:** medium (the scoping is real work in the renderer); the two read verbs are small.

### 5. `session.swap`
**agterm 0.26.0.** Exchange the split panes, preserving axis, ratio, focus, overlays and status
ownership. We cannot swap at all. Our peer tooling addresses panes by id rather than by side, so this
is convenience here rather than a blocker.
**Size:** small–medium.

### 6. Splits are thinner than theirs
- **Horizontal splits** (0.23.0): an axis on `session.split`, surviving restore. Ours is
  `on|off|toggle` only.
- **`session.split.close`** (0.23.0): destroys the pane. agwinterm's `off` and lite's unsplit already
  destroy the shell, so this is naming, not behaviour.
- **agwinterm's `session.split` returns nothing.** lite returns the split's id, which is the only
  handle on a hidden split shell. agwinterm should do the same — a small, real gap *within* our own
  family.

### 7. `sidebar.width`, `restore.capture`
**agterm 0.26.0.** Set/report the sidebar divider (distinguishing a clamped request from an honoured
one); fill captured-command slots on demand rather than only at exit. Both small.

### 8. `agwintermctl version`
**agterm 0.25.0.** Reports which app is serving the socket **and the resolved path of the CLI that
ran**. This machine has `agwintermctl.exe` in the install directory and in two source build trees,
and none of them on `PATH` — exactly the confusion this catches.
**Size:** small.

### 9. `--stdin` on `session type` / `quick type`, rejecting invalid UTF-8
**agterm 0.26.0.** We have no `--stdin` at all, so text with quotes or newlines has to survive a
command line. Worth pairing with the control-byte refusal already shipped.

---

## Open — UI

### 10. `control.pick` — the native picker, driven over the API
**agterm 2026-07-28.** Half the agterm cookbook is built on it: project launcher, workspace picker,
conversation picker, backlog picker, SQLite browser. Nothing here can do that without shipping a
picker binary of its own.
**The biggest single capability gap.** Size: large.

### 11. `session.hud` and `--position`
**agterm 0.22.0 / 0.24.0.** A transient overlay for status an agent wants seen without printing into
the terminal, anchored to one of nine positions. Size: medium.

### 12. Quick terminal: screen percentage, and a global hotkey
**agterm 0.24.0 / 0.25.0.** Sizes as 40–90% of the screen, and a system-wide hotkey summons it over
any app. Ours is a fixed size with no global hotkey. Size: medium (the hotkey is a `RegisterHotKey`
and a policy decision about stealing a chord system-wide).

### 13. Workspace navigation and keymap alternatives
**agterm 0.23.0 / 0.24.0.** `workspace.go next|prev`, `toggle_workspace_collapse`, and keymap entries
that accept several chords for one action separated by `|` (a native chord *and* a tmux-style
leader). We have `session go`; workspaces are keyboard-unreachable without a chord. Size: small.

### 14. Smaller things from 0.26
Cursor shape and blink settings; sidebar tooltips revealing truncated names; the tree naming the
shell holding each pane's foreground process; `session.restore` reporting which pane received the
content; `session.overlay.open` validating `--size-percent` as 1–100 (we clamp silently instead).

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
`session.metrics` (live cell and pixel geometry), `image.frameshm` (shared-memory frame delivery,
which ConPTY makes necessary), shell `profiles`, `omp` theme control, `claude.adopt` / `claude.yolo`
/ `claude.update`, and `app.update`.

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
