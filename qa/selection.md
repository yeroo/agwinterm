# Selection

What happens to a selection while the buffer moves under it. Every case here is a bug that shipped.

**The rule they all serve:** a selection may only name lines its screen can SHOW, and the highlight
and the clipboard read the same cells — so what lands on the clipboard is always what is visibly
highlighted.

Where a shift can be *measured* (main screen, scrollback on, full scroll region) a selection follows
its text and dies with its lines. Where it cannot — a full-screen TUI repainting in place, a
reserved DECSTBM line, scrollback off — it cannot follow, so it stays on the cells it covers,
bounded to the visible range. On the alt screen that range starts at `HistoryCount`: below it is the
*other* screen's scrollback, which the renderer never draws there.

Setup for every case: sandbox instance per `qa/product.md`, `copy-on-select = false`,
`copy-on-ctrl-c = true`, unless the case says otherwise. Fixtures print **distinct** text per line
throughout — with identical lines, a selection that slid onto different rows compares equal to the
original and passes, which is the exact defect being tested for.

---

## A drag makes a selection, and it follows its text when output scrolls

**Guards:** the original behaviour. Scrolling used to drop the selection outright, which made
Ctrl+C-copies close to unusable next to a running agent: the agent prints, the selection is gone,
Ctrl+C interrupts and cancels its turn.

**Setup:** run `qa/fixtures/marker.ps1` (40 rows of `MARKER-<n>-xxxxxxxxxxxxxxxx`).

**Steps:**
1. Drag (300,150) → (900,400).
2. Record what `session copy` returns.
3. Run `qa/fixtures/noise.ps1` (40 more rows), wait for it to finish.

**Expect:** `session copy` returns a non-empty selection at step 2, and the *same* string at step 3.
Non-empty **and** equal — two empty strings compare equal, so a drag that selected nothing would
otherwise pass this while proving the opposite.

**Fails when:** `ReconcileSel`'s eviction shift stops being applied on the trackable path, or the
output handler goes back to clearing the selection.

---

## A selection dies with its lines when they are evicted

**Guards:** the other half of the rule. Buffer-absolute rows renumber when scrollback eviction drops
the oldest lines; a selection that ignores that keeps naming the same numbers while the text under
them moves, so the highlight and `session copy` quietly return something the user never selected.

**Setup:** `marker.ps1`, then a drag as above.

**Steps:** run `qa/fixtures/flood.ps1` (6000 rows — past the 5000-line cap plus the batched-trim
slack, so eviction really happens). Wait ~25s.

**Expect:** `session copy` returns empty. Dropped, not shifted onto a stranger's text.

**Fails when:** the `evicted = Δgeneration − Δhistory` arithmetic in `ReconcileSel` is wrong, or the
`min(anchor, focus) < evicted` drop is removed.

---

## Crossing into the alt screen drops the selection

**Guards:** the alt screen is a different buffer — an index into one names unrelated text in the
other. A full-screen app starting up is the common case, and the highlight would otherwise sit on
top of its UI and copy that.

**Setup:** `marker.ps1`, then a drag as above.

**Steps:** run `qa/fixtures/althold.ps1`, which enters the alt screen and **holds** it for 6s.
(Typed as two separate commands, the shell's prompt redraw lands in between and the buffer is back
before the check runs — the case would then pass for the wrong reason.)

**Expect:** `session copy` returns empty while the alt screen is up.

**Fails when:** the `alt != p.SelAlt` drop at the top of `ReconcileSel` is removed or moved below
the clamp.

---

## A drag inside a repainting TUI builds a selection and keeps it

**Guards:** the codex/Claude Code report — *"in codex I'm not able to copy text; when I press Ctrl+C
with selected text, codex interrupts something"*. The rule was to drop a non-measurable selection on
the first byte of output. codex repaints its status line about once a second, so that fired
mid-drag: every mouse-move found new output, dropped the anchor and re-anchored under the cursor.
The drag never built a selection at all. Fixed in v0.17.7.

**Setup:** run `marker.ps1` first (so the pane has real main-screen history), then
`qa/fixtures/tui.ps1` — alt screen, 25 distinct `BODY-<n>` rows, a status line repainting every
second, which after 14s blanks the body and idles.

**Steps:**
1. Wheel up 10 notches over the pane. On the alt screen this must change nothing.
2. Drag (300,150) → (900,300).
3. Read `session copy` immediately, then again after ~5s of repaints.

**Expect:** a non-empty selection at both reads, the same both times; it contains no `MARKER-` text;
and its first non-blank line appears in `session text`.

The `MARKER-` clause is the point of step 1: the wheel used to accumulate a scroll offset the alt
screen never renders, so the drag mapped onto main-screen history — a selection nothing highlighted,
which Ctrl+C then copied.

**Fails when:** `ReconcileSel`'s non-trackable branch drops on output again, or `CellAtPx` stops
pinning the alt screen's offset to 0.

---

## Ctrl+C over that selection copies instead of interrupting

**Guards:** the user-visible half of the same bug.

**Setup:** the selection from the previous case.

**Steps:** put `SENTINEL` on the clipboard, send Ctrl+C, wait 2s.

(When you need to KILL a fixture between cases, clear the selection first. Otherwise Ctrl+C copies
instead of interrupting — which is the feature under test, and it will leave the old fixture running
under the next case. That happened during this file's first run.)

**Expect:** the clipboard holds exactly what `session copy` returned. Not `SENTINEL`.

**Skips when:** the machine has no usable clipboard. Say so — a skip is not a pass.

**Fails when:** the `_config.CopyOnCtrlC && HasLiveSel(ap)` branch stops consuming the key, or
`HasLiveSel` returns false for a live TUI selection.

---

## Drag-autoscroll cannot walk into the other screen's history

**Guards:** found by review round two. Dragging past the pane's top edge starts a 50ms autoscroll
timer that raised `ScrollOffset` and mapped through it — one tick at a time down into main-screen
scrollback, with the highlight stopping at the top of the screen and the copy carrying on.

**Setup:** the TUI from above, selection cleared, and **while it is still showing its body** — after
it blanks (t+14s) a drag selects nothing, and "nothing" contains no `MARKER-` either, so the case
would pass having proved nothing. Restart the fixture if the body is gone.

**Steps:** drag from (400,400) to (400,5) and **hold the button there for ~3s** before releasing, so
the timer actually ticks. Releasing early is the same trap: the timer never runs and the case passes.

**Expect:** `session copy` is non-empty **and** contains `BODY-` rows **and** contains no `MARKER-`.
All three: the first two are what stop the third from being vacuous.

**Fails when:** `SelAutoscrollTick` stops pinning `no = 0` on the alt screen, or `ClampSel` loses its
`minLine`.

---

## Select All on the alt screen takes only the alt screen

**Guards:** review round two. `SelectAll` anchors at line 0, which on the alt screen is hundreds of
rows below anything it shows, and the copy pulled all of it through `GetHistoryCell`.

**Steps:** with the TUI up, run `selection all`, then read `session copy`.

**Expect:** non-empty, and no `MARKER-` text.

**Fails when:** `ClampSel`'s `minLine = alt ? hist : 0` becomes `0`. Verified: reverting it fails
this case and two of `SelectionBoundsTests`.

---

## Mark-mode Up stops at the top of the alt screen

**Guards:** review round two. `case VK_UP` floored at 0 rather than at the first visible line, so the
caret walked off the top into rows nothing highlights and Enter/Ctrl+C copied them.

**Steps:** with the TUI up and the selection cleared, put `SENTINEL` on the clipboard, then
Ctrl+Shift+M (mark mode), Up ×40 (further than the screen is tall), Ctrl+C.

**Expect:** the clipboard contains no `MARKER-` text.

**Skips when:** no clipboard.

**Fails when:** `minLine` stops bounding `VK_UP` in `MarkModeKey`.

---

## Ctrl+C over a blank selection reaches the app, and leaves the clipboard alone

**Guards:** review round one, and this is the sharpest one — the *fix* for the original bug
reintroduced it. `CopySelection` decided "did we copy anything?" by string length, but the copy joins
its rows with CRLF whether or not a row held text. A blanked six-row selection was ten characters of
pure separator, so it read as a successful copy: the clipboard was overwritten with newlines, a
"Text copied" toast fired, and Ctrl+C was swallowed. Every selection taller than one row.

**Setup:** the TUI, ~14s in, after it has blanked its body. Drag (300,150) → (900,300) over the now-
blank rows.

**Steps:** confirm `session copy` returns nothing but whitespace; put `SENTINEL` on the clipboard;
send Ctrl+C; wait 3s.

**Expect:**
- the clipboard still holds `SENTINEL`;
- the interrupt reached the app. Observe it by the TUI's counter **freezing**, not by the alt screen
  going away: an interrupted script never reaches its own `?1049l`, so the alt screen stays up
  either way. Read `blanked (\d+)` from `session text`, wait 4s, read again — equal means killed.

**Fails when:** emptiness goes back to being measured by string length, or the Ctrl+C branch stops
gating on `CopySelection`'s return value.

---

## With scrollback off, the selection stays put — and copies what is on screen

**Guards:** with `scrollback-lines = 0` nothing is ever pushed into history, so no shift can be
measured. The selection cannot follow its text; keeping it is only safe because the highlight and
the copy read the same cells.

**Setup:** a second sandbox instance with `scrollback-lines = 0`. Run `marker.ps1`, drag
(300,150) → (900,400), then run `noise.ps1`.

**Expect:** `session copy` is still non-empty, and its first non-blank line is present in
`session text`. A selected row may legitimately be blank after the scroll — compare the first row
that carries text, and require **at least 8 characters** of it: a one-character row like `6` is
present in almost any screen, so matching on it proves nothing.

**Fails when:** `scrollback-lines` stops reaching the core (it is an ABI call, and it silently did
nothing once), or the non-trackable branch starts dropping on output again.
