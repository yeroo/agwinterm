# Panes — the two panes of a session, and what a split verb may and may not move

Batch P4 (`docs/plans/completed/2026-09-06-p4-splits.md`) gives a session's split its full shape: `session split`
answers the **pane id** it produced, `--axis vertical|horizontal` chooses the arrangement (agterm's
words: **vertical = left/right panes, horizontal = top/bottom panes**), `session split close` closes
**either** pane, and `session swap` exchanges the two panes while every id stays where it was. The
reply-and-world half of every verb is `tests/integration/win32-control.ps1`, the restart half is
`tests/integration/restore-roundtrip.ps1`, and neither can see a pixel. The cases here are the
**visible** claims: that a horizontal split stacks, that the accent bar marks the focused pane on
either axis, that the divider drags along the axis and the pointer says so, that a swap moves the
contents and not the divider, and that closing pane 0 leaves one pane filling the area.

**The rule they all serve:** the arrangement is a claim about pixels, and so is "did not move". Read
the pane count and the divider from a `PrintWindow` capture (`Save-SandboxCapture`) — `session text`
answers about one pane and cannot tell you how many there are, and a case that asks it passes
whatever happens. Where something must NOT move (the divider under a swap, the divider under a
refused resize) the case compares a **gutter strip** before and after byte for byte
(`Compare-Capture`) rather than eyeballing two pictures.

Setup for every case: sandbox instance per `qa/product.md`, sidebar shown, no config overrides.
Every capture is window-relative in **device** pixels: multiply DIP by `Get-SandboxScale`. The
layout constants the strips are built from are the app's own (`ContentArea` / `PaneLayout` in
`Program.Sessions.cs`): title bar 40 DIP, pane padding 8 DIP left/right and 6 DIP top/bottom, the
sidebar 220 DIP when shown, the gutter between panes 6 DIP thick with a 1 DIP hairline down its
middle, the focused-pane accent 2 DIP tall. For a two-pane session with pane 0's share `r`, the
content region is `x0 = 228, y0 = 46, w = W/k - 236, h = H/k - 52` DIP and the gutter starts at
`x0 + (w - 6) * r` (vertical) or `y0 + (h - 6) * r` (horizontal). The strips below are the gutter
minus one pixel each side, so a rounding at another scale lands on gutter, never on a glyph.

```powershell
. tests\ui\lib.ps1
$exe = Resolve-Binary $null 'Agwinterm.Win32.exe' 'Agwinterm.Win32'
$ctl = Resolve-Binary $env:AGWINTERMCTL 'agwintermctl.exe' 'Agwinterm.Ctl'
$s   = Start-Sandbox -Exe $exe -Ctl $ctl -Pipe 'qapn'
$out = Join-Path $env:TEMP 'agwinterm-qa-panes'; New-Item -ItemType Directory -Force $out | Out-Null
$k   = Get-SandboxScale $s
function Px([double]$dip) { [int]([Math]::Round($dip * $k)) }
function Tree($s)        { (ConvertFrom-Json (Send-Ctl $s @('tree'))).result }
function Node($s,$id)    { Tree $s | ForEach-Object workspaces | ForEach-Object sessions | Where-Object { $_.id -eq $id } }
function Sid($s)         { (Tree $s).workspaces[0].sessions[0].id }
function Reply($s,$a)    { ConvertFrom-Json (Send-Ctl $s $a) }
function Metrics($s,$id) { (Reply $s @('session','metrics','--target',$id)).result }
function Text($s,$id)    { (Reply $s @('session','text','--target',$id)).result }
function Shot($name, [int[]]$crop) { $p = Join-Path $out "$name.png"; [void](Save-SandboxCapture $s $p -Crop $crop); $p }
# Crop a SAVED capture with the same encoder, so Compare-Capture holds between a live crop and a
# crop of an earlier full capture (Save-SandboxCapture crops the live window only).
function CropFile($src, [int[]]$c, $dst) {
    Add-Type -AssemblyName System.Drawing
    $b = [System.Drawing.Bitmap]::FromFile($src)
    $cb = $b.Clone((New-Object System.Drawing.Rectangle $c[0], $c[1], $c[2], $c[3]), $b.PixelFormat)
    $b.Dispose(); $cb.Save($dst, [System.Drawing.Imaging.ImageFormat]::Png); $cb.Dispose(); $dst
}
Send-Ctl $s @('sidebar','show') | Out-Null; Start-Sleep 2
# The window's size is read off a capture, not assumed (SetWindowPos takes device pixels).
$probe = Save-SandboxCapture $s (Join-Path $out 'probe.png'); $W = [int]$probe[0]; $H = [int]$probe[1]
$X0 = Px 228; $Y0 = Px 46; $TW = $W - (Px 236); $TH = $H - (Px 52)
$totalW = $W / $k - 236; $totalH = $H / $k - 52
function HGutter([double]$r) { $h0 = ($totalH - 6) * $r; @($X0, ((Px (46 + $h0)) + 1), $TW, ((Px 6) - 2)) }   # the row gutter of a horizontal split
function VGutter([double]$r) { $w0 = ($totalW - 6) * $r; @(((Px (228 + $w0)) + 1), $Y0, ((Px 6) - 2), $TH) } # the column gutter of a vertical split
$TopEdge = @($X0, (Px 41), $TW, (Px 2))                                                                   # where the accent sits under the title bar
# Baselines from the single-pane window: no gutter, no accent.
$sid = Sid $s; $m1 = Metrics $s $sid
$single = Shot 'single'
$g0h = Shot 'single-hgutter' (HGutter 0.5); $g0v = Shot 'single-vgutter' (VGutter 0.5)
$g0v30 = Shot 'single-vgutter30' (VGutter 0.3); $e0 = Shot 'single-topedge' $TopEdge
```

`.ralphex/p4-panes-qa.ps1` is this file as one script, for a re-run; the cases are the specification.

---

## A horizontal split stacks the panes

**Guards:** the axis. Before P4 every split was side by side and the word for it disagreed between
this repo's own documents; P4 fixes the vocabulary to agterm's and lays the panes out on either axis
from one function (`PaneLayout`). The arrangement is provable from the grid — stacked panes have their
rows halved and their columns intact — and visible as a **horizontal** hairline across the content
region. Re-orienting an already-split session with `--axis` is live and answers the same id.

**Steps:**
1. `$r = Reply $s @('session','split','on','--axis','horizontal','--target',$sid); $pid1 = [string]$r.result`; wait ~2s.
2. `$n = Node $s $sid`; `$m0 = Metrics $s $sid; $mp = Metrics $s $pid1`.
3. `Shot 'horizontal'`; `$gh = Shot 'horizontal-hgutter' (HGutter 0.5)`.
4. `$r2 = Reply $s @('session','split','on','--axis','vertical','--target',$sid)`; wait ~2s;
   `$gv = Shot 'vertical-vgutter' (VGutter 0.5)`; `$mv = Metrics $s $sid`.

**Expect:**
- step 1 answers `ok:true` with a bare string that `$n.paneIds` contains, and `$n.axis` is
  `horizontal` with `paneCount` 2;
- both panes report the single pane's `cols` and at most `⌈rows/2⌉` rows;
- `horizontal.png` shows two prompts one **above** the other and a hairline the full content width
  between them; `Compare-Capture $g0h $gh` is **false** — the row gutter, pane background before,
  now carries the hairline. (The toolbar's split glyph shows two stacked panes too.)
- step 4: `$r2.result` is `$pid1` again — re-orienting does not mint a pane — `$n.axis` reads
  `vertical`, `Compare-Capture $g0v $gv` is **false** (the hairline is now in the column gutter),
  and `$mv` has the single pane's rows back with at most `⌈cols/2⌉` columns.

**Fails when:** `PaneLayout` stops branching on `Ses.Axis`, or a hit-test / the renderer re-derives
a box from its own arithmetic instead of the layout tuples.

*Seen (2026-09-06, task 6 run at 100% DPI, 1100x700): single 89x32; horizontal 89x16 + 89x16;
back to vertical 44x32. Hairline at y = 370, the 2 DIP accent on it (the split pane, below, is the
focused one right after a split).*

---

## The accent bar marks the focused pane, on either axis

**Guards:** the decision recorded in `Program.Render.cs`: the accent is the line **above** the
focused pane. On a vertical split that is the title bar's bottom edge for both panes — the pixels
every capture so far was taken of, unchanged; on a horizontal split that edge belongs to the top
pane only, so for the bottom pane the bar sits on the divider. A bar pinned under the title bar on a
horizontal split would mark the wrong pane half the time.

**Setup:** the vertical split from the previous case (or `session split on --axis vertical`).

**Steps:**
1. `Reply $s @('session','focus','left')`; wait ~1s; `$eL = Shot 'vl-topedge' $TopEdge; $gL = Shot 'vl-vgutter' (VGutter 0.5)`.
2. `Reply $s @('session','focus','right')`; wait; `$eR = Shot 'vr-topedge' $TopEdge; $gR = Shot 'vr-vgutter' (VGutter 0.5)`.
3. `Reply $s @('session','split','on','--axis','horizontal','--target',$sid)`; wait; `Reply $s @('session','focus','top')`;
   wait; `$eT = Shot 'ht-topedge' $TopEdge; $gT = Shot 'ht-hgutter' (HGutter 0.5)`.
4. `Reply $s @('session','focus','bottom')`; wait; `$eB = Shot 'hb-topedge' $TopEdge; $gB = Shot 'hb-hgutter' (HGutter 0.5)`.
5. `Reply $s @('session','focus','left')` on the horizontal split.

**Expect:**
- vertical: `Compare-Capture $eL $eR` is **false** (the bar moved along the top edge) and
  `Compare-Capture $gL $gR` is **true** (the column gutter is identical either way — the bar never
  sits on a vertical divider); `Compare-Capture $e0 $eL` is **false** (a bar is drawn at all);
- horizontal, top focused: `Compare-Capture $eT $e0` is **false** — the bar is under the title bar —
  and `Compare-Capture $gT $g0h` is **false** (hairline in the gutter, no bar);
- horizontal, bottom focused: `Compare-Capture $eB $e0` is **true** — nothing under the title bar —
  and `Compare-Capture $gT $gB` is **false**: the bar now sits on the divider;
- step 5 is refused, `ok:false`, the error naming `horizontal` and offering `top, bottom, primary,
  split or other`; the focus did not move.

**Fails when:** `accentY` in `RenderPanes` loses the `horizontal && ai > 0` branch, or
`SplitAxes.TryFocusIndex` accepts a direction from the other axis.

---

## The divider drags along the axis, and the pointer says which way

**Guards:** `DragDivider` took an x only; on a horizontal split the drag is vertical and the
resize is in rows. The cursor over the gutter (`WM_SETCURSOR`) is new in P4: ⇔ (`IDC_SIZEWE`) over
a vertical split's gutter, ⇕ (`IDC_SIZENS`) over a horizontal one's. And `session resize` refuses
the other axis's flags rather than moving the divider somewhere the caller did not mean.

**Setup:** the horizontal split, `session focus top` (so the pane the old gutter position falls
into is the focused one — the inactive-pane dim would otherwise colour the strip).

**Steps:**
1. `$h0 = ($totalH - 6) * 0.5; $x = $X0 + [int]($TW/2); $y1 = Px (46 + $h0 + 3); $dy = 3 * [int]$m0.cellHeight`
   (three rows, in device pixels); `$rows0 = (Metrics $s $sid).rows`.
2. `[AgwUi]::Drag($s.Hwnd, $x, $y1, $x, $y1 + $dy)`; wait ~1s; `$m = Metrics $s $sid; $n = Node $s $sid`.
3. `$gOld = Shot 'drag-old' (HGutter 0.5)`;
   `$gNew = Shot 'drag-new' (HGutter ([double]$n.splitRatios[0]))`;
   `$ref = CropFile $single (HGutter ([double]$n.splitRatios[0])) (Join-Path $out 'single-at-new.png')`.
4. `Reply $s @('session','resize','--split-ratio','0.5')`; wait; `$gBack = Shot 'drag-back' (HGutter 0.5)`.
5. `$gl = Reply $s @('session','resize','--grow-left','4')`; `$gStay = Shot 'grow-left' (HGutter 0.5)`.
6. **By hand, the one step in this file that needs one:** hover the real pointer over the gutter of
   the horizontal split and then of a vertical one. The handler reads `GetCursorPos`, so a posted
   message cannot exercise it and `SetCursorPos` / `SendInput` are off limits in a QA run.

**Expect:**
- step 2: `$m.rows` is `$rows0 + 3` and `$n.splitRatios[0]` is above 0.5 — the top pane grew by
  exactly the rows dragged (the pty was told the truth);
- step 3: `Compare-Capture $gOld $g0h` is **true** (the old gutter position is pane background
  again) and `Compare-Capture $gNew $ref` is **false** (the hairline is at the new position);
- step 4: `Compare-Capture $gBack $gT` is **true** — `--split-ratio 0.5` puts the divider back byte
  for byte where the previous case captured it;
- step 5: `ok:false`, the error naming `horizontal` and `--grow-top/--grow-bottom`, and
  `Compare-Capture $gStay $gT` is **true**: the divider did not move;
- step 6: the pointer is ⇕ over the horizontal gutter and ⇔ over the vertical one, an arrow
  elsewhere in the pane.

**Fails when:** `DragDivider` / `DividerAt` go back to x-only, `ResizeActiveSplitInternal` converts
rows with the cell width, or `SplitAxes.TryGrow` stops refusing the wrong pair.

*Seen (2026-09-06): 16 → 19 rows after a 60 px drag, ratios 0.598/0.402; the old strip identical to
the single-pane strip, the 0.5 strip identical again after the resize.*

---

## A swap exchanges the contents, not the divider — and no id moves

**Guards:** the rule P4 records as a deliberate divergence from agterm: **a swap moves panes, never
ids.** The pane order reverses and the focus follows its pane; the axis, the ratio *sequence* (the
left/top box keeps its size) and every id stay. Reversing the panes without exchanging their ratio
values would turn a 30/70 into a 70/30 — the divider would jump. Pixels are the proof for the
divider; ids reaching their shells is the proof for the ids.

**Setup:** `session split on --axis vertical`, then `session resize --split-ratio 0.3` so the two
boxes differ, then a marker in each pane:
`Send-Ctl $s @('session','type',"Write-Output 'PANE-A'`r",'--target',$sid)` and the same with
`'PANE-B'` into `$pid1`; wait ~2s.

**Steps:**
1. `$nB = Node $s $sid; $mA0 = Metrics $s $sid; $mB0 = Metrics $s $pid1`; `Shot 'swap-before'`;
   `$gwB = Shot 'swap-before-vgutter' (VGutter 0.3)`.
2. `$sw = Reply $s @('session','swap')`; wait ~2s.
3. `$nA = Node $s $sid; $mA1 = Metrics $s $sid; $mB1 = Metrics $s $pid1`; `Shot 'swap-after'`;
   `$gwA = Shot 'swap-after-vgutter' (VGutter 0.3)`.
4. `Reply $s @('session','swap')` again; wait; `$nT = Node $s $sid`; `$gwT = Shot 'swap-twice-vgutter' (VGutter 0.3)`.

**Expect:**
- `$sw` is `ok:true` with `{session, paneIds, focusedPane, axis}` equal to what `$nA` then shows:
  `paneIds` reversed (`$pid1` first, `$sid` second), `focusedPane` mirrored (`1 - $nB.focusedPane`),
  `axis` `vertical`;
- `$nA.splitRatios` equals `$nB.splitRatios` — the sequence is kept — and
  `Compare-Capture $gwB $gwA` is **true**: the column-gutter strip is byte-identical, the divider did
  not move. This is the assertion the case is for;
- the boxes stayed and the shells moved: `$mA1.cols -eq $mB0.cols` and `$mB1.cols -eq $mA0.cols`
  (the session id's pane now measures the wide box and the split pane's id the narrow one);
- `Text $s $sid` still contains `PANE-A` and `Text $s $pid1` still contains `PANE-B` — each id
  reaches the shell it always reached, now on the other side; `swap-after.png` shows `PANE-B` in the
  narrow left box and `PANE-A` in the wide right box, the accent on whichever pane held the focus;
- step 4: `$nT` equals `$nB` in `paneIds`, `focusedPane` and `splitRatios`, and
  `Compare-Capture $gwB $gwT` is **true** — swapping twice is the identity.

**Fails when:** `SwapPanes` reverses `Panes` without exchanging the two `Ratio` values (the divider
jumps to 70/30), or forgets to mirror `Active`, or anything re-mints an id.

*Seen (2026-09-06): 26 ↔ 62 columns by id after the swap, ratios `0.3,0.7` before and after, the
30% gutter strip identical across before / after / twice.*

---

## `split close` on pane 0 leaves the other pane filling the area, under its old id

**Guards:** before P4 no control verb could close pane 0 of two (`off` hard-codes pane 0 as the
survivor). `split close` takes either side; the survivor becomes pane 0 with the whole area and the
focus, keeps its id, and is reachable by the session id too (the resolver's session arm lands on
the focused pane). A one-pane session is refused, because `session close` is that verb and a
`split close` that closed a session would be the silent-success class.

**Setup:** the vertical split after the swap-twice above (pane 0 = `$sid`, pane 1 = `$pid1`);
`Send-Ctl $s @('session','type',"Write-Output 'SURVIVOR'`r",'--target',$pid1)`; wait ~2s.

**Steps:**
1. `$cl = Reply $s @('session','split','close','--target',$sid)`; wait ~2s.
2. `$nC = Node $s $sid; $mS = Metrics $s $pid1`; `Shot 'closed'`; `$gC = Shot 'closed-vgutter30' (VGutter 0.3)`.
3. `$cl2 = Reply $s @('session','split','close','--target',$sid)`.

**Expect:**
- `$cl` is `ok:true` and its result is `$pid1` — the survivor's id, unchanged;
- `$nC` has no split block (`paneCount` absent), `$mS.cols` equals the single pane's `cols` from the
  baseline (`$m1.cols`) — the survivor has the full width;
- `Compare-Capture $g0v30 $gC` is **true**: the strip where the divider was is pane background
  again, byte-identical to the single-pane window; `closed.png` shows one pane, the sidebar row
  still `session 1`;
- `Text $s $pid1` **and** `Text $s $sid` both contain `SURVIVOR` — the old pane id and the session id
  reach the same, surviving shell;
- step 3: `ok:false`, the error naming `session close`, nothing closed.

**Fails when:** `ClosePane` keeps `Panes[0]` regardless of the target, or the resolver's
session arm stops falling back to the focused pane once the session id's own pane is gone.

*Seen (2026-09-06): reply = the old pane 1 id, survivor 89 columns, both ids answering `SURVIVOR`,
the second close refused with "`session close` closes the session. Nothing closed."*

---

## Cases this file does not carry, and where they are

- **The restart half** — the axis, the swapped order and the ids surviving a graceful close and a
  kill — is `tests/integration/restore-roundtrip.ps1` (`axis-graceful`, `axis-killed`, `swap-killed`).
- **The reply shapes and refusals** of all four verbs are `tests/integration/win32-control.ps1` and
  `tests/Agwinterm.Pty.Tests/SessionSplitTests.cs`.
- **`session reopen` of a closed split session** brings back one pane — unchanged by P4 and
  documented in the plan; not a case here because there is nothing new to see.
