# Control API — the read-only trio

`surface.cursor`, `statusChangedAt` and `agwintermctl version`. Three verbs that answer a question a
caller previously had to guess at, so every case here checks the **number is right**, not merely
well-shaped. A cursor column that is always `0`, a timestamp that is always "now", and a `version`
that names the pipe it was told to use all pass a shape check while proving nothing — the unit tests
in `tests/Agwinterm.Pty.Tests/` already pin the shapes.

**The rule they all serve:** each reply describes the *live* thing it names — the caret where it
actually rests, the moment the status was actually written, the binary that actually ran — so a
script can act on it without a second, independent check.

Setup for every case: sandbox instance per `qa/product.md`. No config overrides unless the case says
so. Each case reads the sandbox's own session id first — a verb with no `--target` resolves the
*caller's* session, and `Send-Ctl` unsets the caller's variables precisely so that cannot happen
silently.

```powershell
. tests\ui\lib.ps1
$exe = Resolve-Binary $null 'Agwinterm.Win32.exe' 'Agwinterm.Win32'
$ctl = Resolve-Binary $env:AGWINTERMCTL 'agwintermctl.exe' 'Agwinterm.Ctl'
$s   = Start-Sandbox -Exe $exe -Ctl $ctl -Pipe 'qa1'

function Tree($s)     { (ConvertFrom-Json (Send-Ctl $s @('tree'))).result }
function Node($s,$id) { Tree $s | ForEach-Object workspaces | ForEach-Object sessions |
                        Where-Object { $_.id -eq $id } }
function Sid($s)      { (Tree $s).workspaces[0].sessions[0].id }
function Col($s,$t)   { [int](Get-CtlResult $s @('surface','cursor','--target',$t)) }
```

---

## The caret column tracks what the pane is showing

**Guards:** the whole point of the verb. `AI/bin/peer-chat.py` decides "is this agent's composer
empty" by matching rendered text against a whitelist of each agent's placeholder string, which
refuses a safe send when it meets an unrecognised placeholder and lets a draft through when the
draft happens to look like one. The caret column replaces that guess — but only if the number is the
real caret. A stub returning `0`, or a value read once and cached, satisfies every unit test and
breaks the caller in exactly the way the whitelist already did.

**Setup:** a fresh sandbox, its first session at a shell prompt. Give it a moment to finish drawing
the prompt before the first read; a column caught mid-repaint is not a bug, it is a race in the
case.

**Steps:**
1. Record `$c0 = Col $s (Sid $s)`.
2. `Send-Ctl $s @('session','type','abcdefgh')` — **no newline**, so this stays an unsubmitted draft
   at the prompt, which is the state the caller actually asks about. Wait ~1s.
3. Record `$c1 = Col $s (Sid $s)` and the pane text.
4. Type `xyz` (again no newline), wait ~1s, record `$c2`.
5. `[AgwUi]::Key($s.Hwnd, 8, 3)` — three Backspaces through the real key path.
6. Record `$c3`.

**Expect:**
- `$c1 - $c0 -eq 8` and `$c2 - $c1 -eq 3` — the column moves by exactly what was typed;
- `$c3 -eq $c1` — and it moves **back**. A counter that only ever grows passes steps 1–4;
- the absolute value is anchored to the screen: in the last non-blank line of `session text`, find
  `abcdefghxyz`, and require `$c2` to equal that index plus 11. The caret must sit immediately after
  the text it typed. (Match on the *position* of the typed text rather than the whole line's length:
  a right-aligned prompt segment or a PSReadLine prediction legitimately draws to the right of the
  caret, and comparing lengths would fail on chrome instead of on the caret.)

**Fails when:** `SnapshotCursor` stops reading the live emulator (a cached or defaulted value), or
`surface.cursor` starts reporting the row.

**Cleanup:** Escape, or enough Backspaces to empty the line, before the next case types into it.

---

## Column 0 is an answer, not a refusal

**Guards:** the empty composer is the *common* case for the caller, and it is the value most easily
lost — a `?? 0`, a `TryGet` that falls through, a shell script testing `if [ -n "$col" ]`. "The caret
is at the left margin" and "nothing answered" must not look alike.

**Steps:** create a session running a program that homes the caret and then prints nothing —

```powershell
Send-Ctl $s @('session','new','--name','col0','--no-select','--command',
              'powershell -NoProfile -Command "Clear-Host; Start-Sleep 120"')
```

Wait ~6s for it to start, then read `surface cursor --target col0` **raw**, not through
`Get-CtlResult`, which cannot tell `0` from a parse failure.

**Expect:** `{"ok":true,"result":0}`. Not an error, not `""`, not a missing `result`. Confirm the
screen really is empty — `session text --target col0` returns nothing — so the `0` is the caret at the
left margin of a blank screen and not a stub.

**Fails when:** the bare-integer reply goes back through a path that treats `0` as "no value".

---

## A session target resolves to the same pane `session type` would reach

**Guards:** a cursor is a per-pane thing and a split has two of them, so the only useful guarantee is
that the pane you *check* is the pane you then *type into*. `surface.cursor` and `session type` share
`Resolve`, and this case is what keeps them sharing it: a caller that checked pane A and typed into
pane B would be worse than no check at all.

Two behaviours of `Resolve` are worth knowing before reading the expectations, because both look like
bugs until you have seen them:

- **at most one pane carries the session id** (pane 0 until a `session swap` moves it — P4), so a
  session-**id** target matches as a pane and answers for THAT pane, whatever `focusedPane` says —
  until `session split close` removes that pane, after which the id matches no pane and answers for
  the **focused** one, like a name;
- a session-**name** target goes down the other path and answers for the **focused** pane.

Both are pre-existing and shared by every content verb. What matters here is that the cursor read and
the type land in the same place, so both halves are checked together.

**Setup:** the first session at a prompt, `session split on`, then read `paneIds` and `focusedPane`
from `tree --json`.

**Steps:**
1. Record each pane's column.
2. `session type QQQQ --target <session-id>`; wait; record both columns again.
3. `session focus other`, wait, and confirm `focusedPane` flipped in the tree.
4. `session type ZZZZZZZZZ --target <session-id>`; record columns.
5. `session type NN --target "<session name>"`; record columns.
6. Read `surface cursor` for the session id, the session name, each pane id, and one pane id
   truncated to its first 8 characters.

**Expect:**
- the id-targeted read equals pane 0's column at every step, and steps 2 and 4 both landed in pane 0
  — `+4` then `+9`, unaffected by the focus change at step 3;
- the name-targeted read equals the **focused** pane's column, and step 5's `+2` landed there;
- the two panes report **different** columns. Equal columns would let an "always the first pane"
  implementation pass everything above;
- the 8-character prefix agrees with the full pane id it abbreviates;
- `surface cursor --target no-such-pane` returns `{"ok":false,"error":"no session"}` — a miss is an
  error, not a `0`.

**Fails when:** `Resolve` stops preferring pane ids, or `surface.cursor` starts resolving its target
differently from the rest of the content verbs.

**Cleanup:** `session split off`.

---

## `statusChangedAt` is the age of the last status *write*

**Guards:** `tree --json` reported `"status":"active"` with no age, so nothing could tell a working
agent from one whose hook died forty minutes ago — the gap `AI/state/agents.json` keeps a `last_seen`
per agent to paper over. The decision under test is that the timestamp is written on **every**
`SetStatus`, not only when the value changes: a hook re-asserting `active` every 30 s is exactly the
liveness signal being asked for. Someone will eventually read that as a bug and "fix" it into
change-only, which is what this case exists to catch.

**Steps:**
1. `session status active --target <sid>`. Read the node; record `$t1 = $_.statusChangedAt` and
   `$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()`.
2. Wait 12s. Read the node again; record `$t2`.
3. `session status active --target <sid>` — the **same** status, deliberately. Wait ~1s, read `$t3`.
4. `session status idle --target <sid>`, read `$t4`.

**Expect:**
- `$now - $t1` is between 0 and 5 — seconds, not milliseconds, and not the epoch;
- `$t2 -eq $t1` — an untouched status does not drift forward. (Take `$t2` from the same read that
  proves the age *grew*: `[DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - $t2 -ge 10`.);
- `$t3 -gt $t2` and the fresh age is under 5s — **the age went back down** after re-asserting the
  same status. This is the assertion the case is for;
- `$t4 -ge $t3`.

**Fails when:** the stamp moves under `if (changed)` in `TerminalSession.SetStatus`, or the field is
serialised as milliseconds / a formatted date.

---

## Every session reports an age, including one that never set a status

**Guards:** a consumer distinguishing "absent" from "old" gains nothing from an omitted field, and
`0` would read as January 1970 — an agent idle for 56 years.

**Steps:** `session new --name virgin --no-select`, then read its node without ever setting a status.

**Expect:** `statusChangedAt` is **present** on every session node in the tree, and `virgin`'s value
is within a few seconds of now — its own creation time, since that is when the clock started.
`"status"` is `idle` and the timestamp is neither `0` nor absent.

**Fails when:** the field goes back to being emitted only when non-default, or the initialiser in the
session constructor is dropped.

---

## A split session reports the age of the pane whose status won

**Guards:** the tree's `status` is an aggregate (Blocked > Completed > Active > Idle), so its age has
to describe the pane that produced it. Reporting the newest write from *any* pane makes a blocked
agent look freshly seen because its neighbour is chatty.

**Setup:** a split session; note both pane ids.

**Steps:**
1. `session status blocked --target <paneA>`.
2. Wait 12s.
3. `session status active --target <paneB>`.
4. Read the session's node.

**Expect:** `"status":"blocked"` — and `statusChangedAt` is pane A's, i.e. ~12s old, **not** the
couple of seconds pane B's write would give. Then set pane B to `blocked` too and re-read: the tie is
broken by recency, so the age drops to a few seconds.

**Fails when:** `StatusAggregate` stops carrying the winning pane's timestamp alongside its status,
or the tie-break stops preferring the most recent.

---

## `version` names the binary that ran and the app it reached

**Guards:** three `agwintermctl.exe` live on this machine — the install directory and two source
build trees — and none is on `PATH`. A QA run that drives a stale one reports green for a fix that is
not in the binary under test. That has happened; `Resolve-Binary` exists because of it.

**Steps:** run the CLI **directly**, not through `Send-Ctl` (which forces `--json`), and keep the
exit code:

```powershell
$out = & $ctl version --pipe $s.Pipe 2>&1 | Out-String
$code = $LASTEXITCODE
```

**Expect:**
- two lines, one starting `cli ` and one starting `app ` — greppable, in that order;
- the `cli` line contains the **same path** as `$ctl`, compared case-insensitively after resolving
  both. Not merely "a path": the point of the line is which of the three ran;
- the `app` line contains `\\.\pipe\qa1` — the sandbox's pipe, not the default `agwinterm` — and the
  version the app reports, which must match `Send-Ctl $s @('ping')`'s result;
- `$code -eq 0`.

**Fails when:** the CLI half is rendered from the app's reply (it must be local), or the pipe name is
taken from the default instead of the resolved one.

---

## `version` still answers when no app is listening

**Guards:** the one case the command exists for. A diagnostic that fails when the thing being
diagnosed is down tells you nothing you did not already know, and a non-zero exit stops the script
that was about to print it.

**Steps:**

```powershell
$out  = & $ctl version --pipe "agwinterm-qa-absent-$(New-Guid)" 2>&1 | Out-String
$code = $LASTEXITCODE
$j    = & $ctl version --pipe "agwinterm-qa-absent-$(New-Guid)" --json | ConvertFrom-Json
```

Pick a pipe name nothing can be serving — a fresh GUID suffix, so a forgotten instance from an
earlier run cannot answer and turn this case green for the wrong reason. Each call takes about 3s:
that is `Probe`'s connect timeout expiring, not a hang.

**Expect:**
- `$code -eq 0`;
- the `cli` line is present and complete — version and resolved path;
- the `app` line says `unavailable` and still names the pipe it tried;
- `$j.cli.path` is that same path, `$j.app.available` is `$false`, `$j.app.pipe` is the name that was
  passed. And the same `--json` run against the live sandbox parses with `app.available` `$true`.

**Fails when:** `Probe`'s catch-all narrows and lets a connect failure escape, or the verb starts
returning 1 on an unreachable pipe.

**Cleanup:** `Stop-Sandbox $s`, always in a `finally`.
