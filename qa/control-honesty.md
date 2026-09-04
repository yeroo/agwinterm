# Control API — stop lying to the caller

Batch P2 (0.17.11) closed one defect class in the control API: **a call that appears to succeed while
doing something other than what was asked.** Every case here is a call that used to answer `ok:true`
and then did something else — typed a shortened line, opened a full-screen overlay for `--size-percent
0`, pinned a command to a pane it never named, acknowledged a sidebar op it did not know, or created a
session in whatever workspace the user had last clicked.

**The rule they all serve:** a reply describes what actually happened, or the call is refused and
nothing happens. So every case here checks **two** things: the reply, and the world — the pane's text,
`tree`, the pane's measured width — because a refusal that still did the thing, or a success that did
not, passes a reply-only check. The unit tests in `tests/Agwinterm.Pty.Tests/` pin the shapes; this
file checks the shapes are about the real thing.

Setup for every case: sandbox instance per `qa/product.md`. No config overrides. Each case reads the
sandbox's own session id first — a verb with no `--target` resolves the *caller's* session, and
`Send-Ctl` unsets the caller's variables precisely so that cannot happen silently. Two cases below
have to call the CLI **directly** (one to pipe stdin, one to *be* a caller): call `Send-Ctl` once
before them, so the variables are already gone, and never leave `AGWINTERM_SESSION_ID` set afterwards.

```powershell
. tests\ui\lib.ps1
$exe = Resolve-Binary $null 'Agwinterm.Win32.exe' 'Agwinterm.Win32'
$ctl = Resolve-Binary $env:AGWINTERMCTL 'agwintermctl.exe' 'Agwinterm.Ctl'
$s   = Start-Sandbox -Exe $exe -Ctl $ctl -Pipe 'qah'

function Tree($s)       { (ConvertFrom-Json (Send-Ctl $s @('tree'))).result }
function Node($s,$id)   { Tree $s | ForEach-Object workspaces | ForEach-Object sessions |
                          Where-Object { $_.id -eq $id } }
function WsOf($s,$id)   { Tree $s | ForEach-Object workspaces | Where-Object { $_.sessions.id -contains $id } }
function Sid($s)        { (Tree $s).workspaces[0].sessions[0].id }
function Reply($s,$a)   { ConvertFrom-Json (Send-Ctl $s $a) }
function Metrics($s,$t) { (Reply $s @('session','metrics','--target',$t)).result }
# Run the CLI AS a pane: the one case that needs the caller identity set, scoped to the call.
function As($s,$pane,$a) {
    $env:AGWINTERM_SESSION_ID = $pane
    try { (& $s.Ctl @a --pipe $s.Pipe --json 2>&1) -join '' }
    finally { Remove-Item env:AGWINTERM_SESSION_ID -ErrorAction SilentlyContinue }
}
```

---

## `--stdin` carries what argv cannot, byte for byte

**Guards:** there was no way to type a quote, a newline, a run of spaces or a leading `--`. The CLI
re-joins positionals with one space and the option splitter eats any positional starting with `--`,
and both losses were silent: `ok:true`, and the shell received a different line. P2 (0.17.11).

**Setup:** a fresh sandbox, its first session at a shell prompt. Wait for the prompt to draw.

**Steps:**
1. Build the text with every hazard in it — a quote, an embedded newline, two consecutive spaces
   and a second line starting with `--`:

   ```powershell
   $text = 'Write-Output "a  b' + "`n" + '--x"'
   $r = $text | & $ctl session type --stdin --target (Sid $s) --pipe $s.Pipe --json
   ```

   The pipe adds one trailing newline; `--stdin` drops exactly one, so this is a **draft**, not a
   submitted command. Wait ~1s.
2. Read the pane with `Get-PaneText $s (Sid $s)` and split it into lines.
3. Press Escape through the real key path — `[AgwUi]::Key($s.Hwnd, 27, 1)` — and confirm the draft
   is gone before step 4.
4. Send the *same* text as argv, the only way it could be sent before:

   ```powershell
   Send-Ctl $s @('session','type','Write-Output "a  b','--x"')
   ```

   Wait ~1s, read the pane again. Escape afterwards.

**Expect:**
- step 1 answers `{"ok":true,"result":"typed"}`;
- step 2: a line ends with `Write-Output "a  b` — **two** spaces between `a` and `b`, the quote
  intact — and the line immediately after it ends with `--x"`. The unclosed quote makes the shell
  take Enter as a line break in the draft, so the newline is visible as the second line rather than
  as a submitted command;
- step 4 **also** answers `ok:true` — that is the lie — and the pane shows `Write-Output "a  b` with
  **no** `--x"` anywhere: the option splitter took it as a flag, and nothing said so. The case is
  only worth running because steps 1 and 4 come out different; a run where both show `--x"` means
  the argv path grew a guard and the case should be rewritten, not deleted.

**Fails when:** `StdinText.Decode` starts stripping more than one trailing newline, the CLI folds
`--stdin` back through the positional join, or `HandleType` starts stripping quotes or collapsing
whitespace. The argv half is a control, not a check: it demonstrates the pre-P2 loss on the path P2
left as it was.

**Cleanup:** Escape, and confirm the last non-blank line is a bare prompt.

---

## Invalid UTF-8 on `--stdin` is refused before anything is sent

**Guards:** the server reads its pipe through a `StreamReader` with the replacement fallback, so any
bad byte that reaches it is already U+FFFD and the call answers `ok:true` with a replacement character
typed into the shell — "succeeded, wrong content" exactly. Detection has to be client-side, and this
case is what proves it is. P2 (0.17.11).

**Setup:** a session at a prompt. Record its text as `$before` and confirm it is non-empty (a prompt
is on screen — otherwise "unchanged" is vacuous).

**Steps:**
1. Write a file whose bytes are `echo ` followed by a lone `0x80` — five ASCII bytes and one
   continuation byte with no lead:

   ```powershell
   $bad = Join-Path $s.AppDir 'bad.bin'
   [IO.File]::WriteAllBytes($bad, [byte[]](0x65,0x63,0x68,0x6F,0x20,0x80))
   ```

2. Redirect it into the CLI through `cmd`, which passes bytes through untouched (a PowerShell
   pipeline would re-encode them and hide the bug):

   ```powershell
   $out  = cmd /c "`"$ctl`" session type --stdin --target $(Sid $s) --pipe $($s.Pipe) --json < `"$bad`"" 2>&1 | Out-String
   $code = $LASTEXITCODE
   ```

3. Wait ~1s. Read the pane text as `$after`.

**Expect:**
- `$code -eq 2` — not 0, and not 1 (the app's `ok:false` exit): the CLI refused before connecting;
- `$out` names the offset: contains `byte offset 5` and `0x80`, and says `nothing was sent`;
- `$after -eq $before` — no `echo`, no `?`, no U+FFFD (`[char]0xFFFD`) appeared. The five good bytes
  were **not** typed either: a partial send would be the shortened-line failure #213 was about.

**Fails when:** `StdinText` goes back to a lenient decoder, the CLI builds the request before checking
the outcome, or `Read` starts substituting instead of refusing.

---

## `--size-percent` outside 1..100 opens nothing

**Guards:** `--size-percent 0`, `-5`, `150` and `sixty` each opened a **full-screen** overlay and
answered `ok:true` — three separate silent coercions (the CLI dropped an unparseable value, `GetInt`
defaulted a non-number to 0, the host clamped). P2 (0.17.11).

**Setup:** a session at a prompt; confirm its `tree` node has no `overlay` and no `overlaySize`.

**Steps:**
1. `Send-Ctl $s @('session','overlay','open','powershell -NoProfile -Command Start-Sleep 60','--size-percent','0','--target',(Sid $s))`
2. Same with `--size-percent 150`.
3. Same with `--size-percent sixty`, called **directly** so the exit code is kept:

   ```powershell
   $out  = & $ctl session overlay open 'powershell -NoProfile -Command Start-Sleep 60' --size-percent sixty --target (Sid $s) --pipe $s.Pipe --json 2>&1 | Out-String
   $code = $LASTEXITCODE
   ```

4. Read the node after each of 1–3.
5. Then the positive control: `--size-percent 60`. Wait ~2s, read the node, then
   `session overlay close --target (Sid $s)`.

**Expect:**
- 1 and 2: `ok:false`, and the error names the value (`0`, `150`), the range `1..100`, and says to
  **omit** the flag for the full region — the caller who wrote `0` meaning "full" needs telling how;
- 3: `$code -eq 2` and `$out` names `sixty` and the range; nothing reached the app;
- after each of 1–3 the node still has **no** `overlay` and **no** `overlaySize`. The reply alone is
  not the check: a refusal that still opened the overlay is worse than the clamp it replaced;
- 5: `ok:true` with an overlay id, the node shows `"overlay":true` and `"overlaySize":60`, and after
  the close both are gone. Without this step a validator that refuses everything passes 1–4.

**Fails when:** `TryOverlaySize` accepts 0 as "full" again, the CLI goes back to dropping an
unparseable value, or `OverlayOpen` / `resize` regain a `Math.Clamp`.

---

## `session restore` names the pane it pinned, and `tree` reads it back

**Guards:** the reply was the constant `"pinned"`, the target went through a resolver no other verb
used, and `AgentSkill` promised a `restoreCommands` field that `tree --json` never emitted — so which
pane got the pin was unknowable and unreadable. P2 (0.17.11).

**Setup:** the first session at a prompt; `session split on --target <sid>`, wait ~2s, read
`paneIds` from its node and confirm there are **two**. Call the second one `$p2` — the one that is
*not* the session id, so the case cannot pass by pinning "the first pane" whatever the target said.

**Steps:**
1. `session restore "echo pinned-here" --target $p2`; parse the reply.
2. Read the node.
3. `session restore none --target $p2`; parse the reply.
4. Read the node.
5. `session restore "x" --target no-such-pane`; read the node.

**Expect:**
- 1: `result.action -eq 'pinned'`, `result.pane -eq $p2` (**not** the session id), `result.session`
  is the session id, `result.command -eq 'echo pinned-here'`;
- 2: `restoreCommands` is present on the node, has exactly one property, named `$p2`, with the
  command as its value. The first pane's id is **not** a key;
- 3: `action -eq 'cleared'`, `pane -eq $p2`, and no `command` in the reply;
- 4: `restoreCommands` is **absent** from the node — not `{}`, not `null`; the field is omitted when
  no pane carries a pin, like every other optional per-session field;
- 5: `ok:false`, and the node is unchanged — still no `restoreCommands`.

**Fails when:** `AppendRestoreCommands` stops being called from the tree writer, the host's
`SessionRestore` goes back to resolving through `FindPaneById` without reporting which pane it
found, or the empty-command path stops being reported as `cleared`.

**Cleanup:** `session split off --target <sid>`.

---

## `sidebar width` moves the divider, not just a number

**Guards:** there was no way to set the width at all, and the `sidebar` verb answered `Ok("sidebar")`
for any op — `sidebar on` sat in the conformance file passing while doing nothing. A width that
changes the reported number without moving the content region is the exact lie this batch is about,
so the oracle is the pane's **measured** width, not the reply. P2 (0.17.11).

**Setup:** sandbox at its fixed 1100x700; `Send-Ctl $s @('sidebar','show')`; confirm
`sidebar width` reads `220` and `sidebar state` reads `visible tree 220`.

**Steps:**
1. `$m0 = Metrics $s (Sid $s)` — the active pane's `widthPx`, in device pixels.
2. `$r = Reply $s @('sidebar','width','320')`; wait ~1s; `$m1 = Metrics $s (Sid $s)`.
3. Read `sidebar state` and `sidebar width`.
4. `$bad = Reply $s @('sidebar','width','5')`; `$m2 = Metrics $s (Sid $s)`; read `sidebar width`.
5. `$op = Reply $s @('sidebar','sideways')`.
6. `Reply $s @('sidebar','width','220')` to put it back.

**Expect:**
- 2: `$r.result.width -eq 320`, `visible -eq $true`, `applied -eq $true`;
- `$m1.widthPx -lt $m0.widthPx`, by **at least 50** device pixels: the sidebar grew by 100 DIP and
  the grid gives back at most one cell of that, at any DPI. Both `widthPx` values must be `> 0` — a
  headless zero on both sides compares "unchanged" and would pass a weaker check;
- 3: `sidebar state` is `visible tree 320` and `sidebar width` reads `320`;
- 4: `$bad.ok -eq $false`, its error names `5` and `120..600`; `$m2.widthPx -eq $m1.widthPx` and
  `sidebar width` still reads `320`. Refused **and** nothing moved;
- 5: `$op.ok -eq $false` and the error lists the ops it does know. `sidebar on` and `sidebar off`
  are *not* in that situation: `Reply $s @('sidebar','off')` answers `ok:true` and
  `sidebar state` then starts `hidden`, and `on` brings it back — they are real aliases, which is
  what keeps the shared conformance file green;
- 6: `widthPx` returns to `$m0.widthPx`.

**Fails when:** the host's `SidebarWidth(set)` writes `_sidebarW` without calling
`SidebarWidthChanged` (the number moves, the divider does not), the range check in
`TrySidebarWidth` is replaced with a clamp, or `HandleSidebar` goes back to `Ok("sidebar")` on the
fall-through. *Seen to fail (2026-09-04): with the `_sidebarW = w; SidebarWidthChanged();` line
removed, the reply still said `{width:320, applied:true}` and `widthPx` stayed at 857 on both
sides — the reply-only check would have passed.*

---

## `session new` refuses a workspace it does not have, and creates nothing

**Guards:** an unknown `--workspace`, or an unknown `--workspace-name` without `--create-workspace`,
fell back to the **active** workspace and answered `ok:true` with a session id — the caller believed
it had placed a session somewhere it had not. The id was minted and returned before the posted
lambda even asked the question. Decision 1 of the parity programme: **refuse**. P2 (0.17.11).

**Setup:** count the sessions in `tree` as `$n0` and confirm no session is named `ghost`.

**Steps:**
1. `Reply $s @('session','new','--workspace','no-such-thing','--name','ghost')`.
2. `Reply $s @('session','new','--workspace-name','no-such-thing','--name','ghost')`.
3. `Reply $s @('session','new','--workspace','x','--workspace-name','y','--name','ghost')`.
4. Read `tree`.
5. `Reply $s @('session','new','--workspace-name','no-such-thing','--create-workspace','--name','ghost','--no-select')`.
6. Read `tree`.

**Expect:**
- 1: `ok:false`, error names `no-such-thing` and says `No session was created`;
- 2: `ok:false`, error names `no-such-thing` **and `--create-workspace`** — the escape hatch the
  caller almost always meant;
- 3: `ok:false` — two answers to "where" are refused, not ranked;
- 4: the session count is still `$n0` and no session is named `ghost`. This is the assertion the
  case is for: an `ok:false` with an orphan session behind it would be worse than the fallback;
- 5: `ok:true` with an id; 6: a workspace named `no-such-thing` now exists and `ghost` is in it, and
  only one workspace has that name.

**Fails when:** `NewSession` mints the id before resolving the workspace again, or `FindWs` falls back
to `ActiveWorkspace()` on a miss.

---

## A bare `session new` lands in the caller's workspace, however the user clicks

**Guards:** the reported bug (Boris, work laptop, agliteterm, 2026-09-04): an agent creating several
sessions found them scattered across workspaces, because with no `--workspace` the session went to
the *active* workspace, a global the UI rewrites on every click and selection. Now the CLI sends the
pane it runs in as `caller`, and the session goes next to it. A version of this case that checked
only the **first** create passed before the fix; the second create, after a click elsewhere, is the
one that matters. P2 (0.17.11).

**Setup:** workspace A is `workspaces[0]` with its first session `$a1`. Then:
1. `Send-Ctl $s @('workspace','new','B')`; read `tree` and take the id of the workspace named `B`.
2. `$inB = (Reply $s @('session','new','--workspace',$B,'--name','inB','--no-select')).result` — the
   pane that will play the agent. Confirm `WsOf $s $inB` is `B`.
3. `Send-Ctl $s @('session','select',$a1)`; confirm workspace A is `active:$true` in `tree`.

**Steps:**
1. `$b1 = (ConvertFrom-Json (As $s $inB @('session','new','--name','bare1'))).result` — no
   `--workspace`, no `--target`, the caller identity set for this one call.
2. Read `tree`. Note which workspace is active now.
3. `Send-Ctl $s @('session','select',$a1)` — the user clicks back to A. Confirm A is active.
4. `$b2 = (ConvertFrom-Json (As $s $inB @('session','new','--name','bare2'))).result`.
5. Read `tree`.
6. The control: `$p = (Reply $s @('session','new','--name','plain')).result` through `Send-Ctl`, which
   has **no** caller identity, with A active. Read `tree`.

**Expect:**
- 1: `ok:true`; 2: `(WsOf $s $b1).name -eq 'B'` — while **A** was active;
- 4–5: `(WsOf $s $b2).name -eq 'B'` again — the select in step 3 did not steer it. This is the
  assertion the case exists for;
- 6: `(WsOf $s $p).id -eq $A.id` — with no caller it is still the active one, so the difference
  between 6 and 4 is the caller and nothing else. Confirm `$env:AGWINTERM_SESSION_ID` is unset after
  the case.

**Fails when:** the CLI stops sending `caller` (or sends it as `target`, which would change what an
unknown value means), or `NewSession` reads `ActiveWorkspace()` before trying the caller's pane.
*Seen to fail (2026-09-04): with the `FindPaneById(caller)` branch disabled in the host, `bare1`
landed in workspace A on the very first create, with `ok:true` and an id — the pre-P2 behaviour.*

**Cleanup:** `Stop-Sandbox $s`, always in a `finally`.
