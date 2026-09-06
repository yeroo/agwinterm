# Persistence — what an agent sets survives, and is visible where it was promised

Batch P3 (`docs/plans/completed/2026-09-05-p3-persistence.md`) adds two things: **`session context`**, one
line of "what is this pane for" per session, and **`restore capture`**, a checkpoint of each pane's
foreground command into its restore slot. Both are readable back in `tree --json`, and both survive
a restart — the restart half is `tests/integration/restore-roundtrip.ps1` (graceful, killed, refusal,
replay), and the reply-and-world half of every verb is in `tests/integration/win32-control.ps1`.
Neither of those can see a pixel. The cases here are the **visible surfaces** the context was
promised on: the title bar, the sidebar row, the session palette's line and its search, and the
reopened session. Every case captures with `PrintWindow` (`Save-SandboxCapture`), because "the
context is shown dimmed beside the name" is a claim about pixels, and `tree` saying `context` proves
only that the field is set.

**The rule they all serve:** what you set is what is shown — and nothing *else* moved. A suffix that
pushes the bell, the right button group or the next sidebar row is a layout regression that a
text check cannot see; so several cases compare a region **before and after** byte for byte
(`Compare-Capture`) rather than eyeballing one picture.

Setup for every case: sandbox instance per `qa/product.md`. No config overrides. Every capture is
window-relative in **device** pixels: multiply DIP by `Get-SandboxScale` first. The title bar is
40 DIP tall in the default toolbar mode; the sidebar starts under it at x = 0, rows of one fixed
height each.

```powershell
. tests\ui\lib.ps1
$exe = Resolve-Binary $null 'Agwinterm.Win32.exe' 'Agwinterm.Win32'
$ctl = Resolve-Binary $env:AGWINTERMCTL 'agwintermctl.exe' 'Agwinterm.Ctl'
$s   = Start-Sandbox -Exe $exe -Ctl $ctl -Pipe 'qap'
$out = Join-Path $env:TEMP 'agwinterm-qa-persistence'; New-Item -ItemType Directory -Force $out | Out-Null
$k   = Get-SandboxScale $s
function Px([double]$dip) { [int]([Math]::Round($dip * $k)) }

function Tree($s)       { (ConvertFrom-Json (Send-Ctl $s @('tree'))).result }
function Node($s,$id)   { Tree $s | ForEach-Object workspaces | ForEach-Object sessions |
                          Where-Object { $_.id -eq $id } }
function Sid($s)        { (Tree $s).workspaces[0].sessions[0].id }
function Reply($s,$a)   { ConvertFrom-Json (Send-Ctl $s $a) }
function Shot($name, [int[]]$crop) { $p = Join-Path $out "$name.png"; [void](Save-SandboxCapture $s $p -Crop $crop); $p }
# Regions, device pixels: the whole title bar; its right 220 DIP (the button group); the sidebar's
# first two rows (the sidebar is 220 DIP wide; the row band under the title bar). The window's WIDTH
# is read off a capture, not computed: Start-Sandbox sizes the window with SetWindowPos, which takes
# DEVICE pixels, so the window is 1100 px wide at every scale and `Px 1100` / `Px 880` were past its
# right edge on any HiDPI display (the crop threw before step 1 at 150 %; revmux r1).
$probe = Save-SandboxCapture $s (Join-Path $out 'probe.png'); $W = [int]$probe[0]
$TitleBar   = @(0, 0, $W, (Px 40))
$RightGroup = @(($W - (Px 220)), 0, (Px 220), (Px 40))
$Rows       = @(0, (Px 40), (Px 220), (Px 120))
```

---

## The title bar shows the context dimmed after the name, and the right group does not move

**Guards:** the reason the field exists. Before P3 "what is this pane for" was guessable only from a
name that also had to be short. The suffix shares the title's width budget so the bell and the
right button group stay put; a version that appended the context without a budget pushed the bell
off the right edge on a long value. P3.

**Setup:** the first session at a prompt. `Send-Ctl $s @('sidebar','show')`. Record the session id
as `$sid = Sid $s` and confirm `Node $s $sid` has no `context`.

**Steps:**
1. `$t0 = Shot 'title-before' $TitleBar`; `$g0 = Shot 'group-before' $RightGroup`.
2. `Reply $s @('session','context','build the persistence batch','--target',$sid)`; wait ~1s.
3. `$t1 = Shot 'title-after' $TitleBar`; `$g1 = Shot 'group-after' $RightGroup`.
4. A value that fills the budget: `Reply $s @('session','context',('long ' * 40).Trim(),'--target',$sid)`;
   wait ~1s; `$t2 = Shot 'title-long' $TitleBar`; `$g2 = Shot 'group-long' $RightGroup`.
5. `Reply $s @('session','context','--clear','--target',$sid)`; wait ~1s; `$t3 = Shot 'title-cleared' $TitleBar`.

**Expect:**
- step 2 answers `ok:true` with `result.context` equal to the text, and `Node $s $sid` reads it back;
- `Compare-Capture $t0 $t1` is **false** — something changed in the title bar — and, looking at
  `title-after.png`, the change is the text `build the persistence batch` in a dimmer colour than
  the session name, on the same line, to its right, before the bell;
- `Compare-Capture $g0 $g1` is **true**: the right button group is pixel-identical. This is the
  assertion the case is for. A suffix that moved the group passes every text check;
- step 4: `title-long.png` shows the value ellipsized (`…`) and still before the bell, and
  `Compare-Capture $g0 $g2` is **true** again — a value the budget cannot hold is cut, not pushed;
- step 5: `Compare-Capture $t0 $t3` is **true** — clearing puts the title bar back exactly.

**Fails when:** `DrawTitleBar` draws the context outside the `titleAvail` budget, drops the trimming
sign, reserves room for a context it then does not draw (a title cut short for nothing), or moves the
pill strip for a title that FITS — the strip (`pillX`) is anchored on the title's right edge, the
context is drawn after the pills, and the only way a context reaches a pill is by shortening a title
longer than its share, which is the one case where the strip legitimately moves left (#234).

*Seen (2026-09-05, task 2 smoke): `~  build the persistence batch` dimmed before the bell at 150%
DPI; the row read `session 1  build the persistence…`.*

---

## The sidebar row carries the same suffix, and the row below does not move

**Guards:** the sidebar has one row height for every consumer of `_sidebarRows` — click, `RowAt`,
the rename EDIT, drag, UIA. A context drawn as a second line would need a taller row for that
session only, and every hit-test below it would be off by one line. So the context is a suffix in
the *same* row, clipped to the name rect. P3.

**Setup:** two sessions in the first workspace — the first with no context, the second created with
`Reply $s @('session','new','--name','below','--no-select')`. Sidebar shown. Wait ~2s for the row to
draw.

**Steps:**
1. `$r0 = Shot 'rows-before' $Rows`.
2. `Reply $s @('session','context','what this pane is for','--target',$sid)`; wait ~1s.
3. `$r1 = Shot 'rows-after' $Rows`.
4. Crop the row for `below` alone out of both captures. `rowH` is what `DrawSidebar` computes —
   `max(cellH + 8, ceil(13 * 1.5) + 6)` DIP, with `cellH` the `cellHeight` from `session metrics`
   divided by the scale — and rows start at `40 + 6` DIP (title bar + `PadY`): the workspace header
   is row 0, the first session row 1, `below` row 2. So in the `$Rows` band (which starts at y = 40)
   the strip is `y = Px(6 + 2*rowH)`, `Px(rowH)` tall. Save as `row2-before.png` / `row2-after.png`.

   ```powershell
   $m    = (Reply $s @('session','metrics','--target',$sid)).result
   $rowH = [Math]::Max([double]$m.cellHeight / $k + 8, [Math]::Ceiling(13 * 1.5) + 6)
   $y    = Px (6 + 2 * $rowH)   # band-relative; the same strip is window y = Px(46 + 2*rowH)
   ```

**Expect:**
- `Compare-Capture $r0 $r1` is **false**, and `rows-after.png` shows the first row as
  `session 1  what this pane is for` with the suffix dimmer than the name, ending in `…` if it
  does not fit before the status dot;
- `Compare-Capture row2-before row2-after` is **true**: the row for `below` is pixel-identical, at
  the same y. The row height did not change for the session that gained a context;
- clicking the second row still selects it: `[AgwUi]::Click($s.Hwnd, 1, (Px 60), (Px (40 + 1.5*rowH/$k)))`
  followed by `(Tree $s).workspaces[0].sessions | Where-Object active` naming `below` — the
  hit-test agrees with the pixels.

**Fails when:** `DrawSessionRow` grows `rowH` per row, or draws the suffix into the counts / dot
rect instead of the name rect.

---

## The palette shows the context on its second line, and finds a session by it

**Guards:** the palette is the one surface with room for the long form; the title bar and the row
ellipsize. And a context nobody can *search* is a label, not a handle: an agent that set
`context: reviewing PR 226` on a pane must be able to reach it by typing `226`. P3.

**Setup:** two sessions; `$sid` with the context `reviewing PR 226`, the other (`below`) with none.
No overlay open, palette closed.

**Steps:**
1. `[AgwUi]::Chord($s.Hwnd, 0x50, $false)` — Ctrl+P, the session palette. Wait ~0.8s.
2. **Harness note, not a product defect:** a posted Ctrl+P is translated by the sandbox's own
   message loop without the Ctrl the real key path carries, so a `p` lands in the query. Send one
   Backspace first: `[AgwUi]::Key($s.Hwnd, 8, 1)`.
3. `$p1 = Shot 'palette' @(0, 0, (Px 1100), (Px 700))`.
4. Type the query only the context matches, one WM_CHAR per character:
   `foreach ($ch in [char[]]'226') { [void][AgwUi]::PostMessageW($s.Hwnd, 0x0102, [IntPtr][int]$ch, [IntPtr]1); Start-Sleep -Milliseconds 40 }`;
   wait ~0.8s; `$p2 = Shot 'palette-query' @(0, 0, (Px 1100), (Px 700))`.
5. Escape: `[AgwUi]::Key($s.Hwnd, 27, 1)`.

**Expect:**
- `palette.png`: the row for `$sid` has a dimmed second line reading
  `reviewing PR 226  ·  workspace 1  ·  <cwd>` — the context **first**, then the workspace, then
  the cwd, the two dots between them; the row for `below` has the old two-part line
  (`workspace 1  ·  <cwd>`) with no leading context;
- `palette-query.png`: only the `$sid` row remains listed for the query `226`; `below` is gone. A
  query that matches neither the name nor the cwd found the session by its context;
- after Escape the palette is closed and `Node $s $sid` is unchanged (the query typed nothing into
  the pane: `Get-PaneText $s $sid` does not contain `226` as a typed line).

**Fails when:** the palette's `Secondary` builder stops prepending the context, or `Search` stops
including it.

---

## Reopening a closed session brings its context back

**Guards:** undo-close (`Ctrl+Shift+R`, `record ClosedSession`) reused the session's id but knew
nothing of the context, so a reopen would have brought back the name and lost the label. The record
now carries it. P3.

**Setup:** a session created for the case: `$id2 = (Reply $s @('session','new','--name','second')).result`;
wait ~1.5s; `Reply $s @('session','context','reopen me','--target',$id2)`; confirm `Node $s $id2`
reads it back. Sidebar shown; `$r0 = Shot 'reopen-before' $Rows` with the row visible.

**Steps:**
1. `Send-Ctl $s @('session','close',$id2)`; wait ~1.2s; confirm `Node $s $id2` is `$null`.
2. `[AgwUi]::Chord($s.Hwnd, 0x52, $true)` — Ctrl+Shift+R. Wait ~2.5s.
3. `$n = Node $s $id2`; `$r1 = Shot 'reopen-after' $Rows`.

**Expect:**
- `$n` is not null — the session is back on the **same id** — and `$n.context -eq 'reopen me'`;
- `reopen-after.png` shows the row `second  reopen me` again, suffix dimmed as before;
- `Compare-Capture $r0 $r1` is **true** if the reopened session landed in the same row position
  (it does when it was the last row); if the order differs, the row's *content* must still show the
  suffix — say which in the run notes.

**Fails when:** `CaptureClosedSessionData` stops recording `Context`, or the replay path creates the
session without setting it before the first redraw.

---

## The persisted half

Not a case here — a script, because it has to relaunch the app: `tests/integration/restore-roundtrip.ps1`
(graceful close, kill, refusal-left-nothing, and the replay cell with `restore-commands = true`).
After any of its cells the surfaces above can be captured again on the relaunched sandbox: the
suffix is drawn from `Ses.Context`, which `TryRestoreState` fills through the same rules the verb
uses, so a value the verb would refuse (a newline written into the file by hand) is dropped on load
and **not** shown — `tests/Agwinterm.Pty.Tests/RestoreStateTests.cs` pins that.

**Cleanup:** `Stop-Sandbox $s`, always in a `finally`; the captures under `$out` are evidence, keep
them with the run notes.
