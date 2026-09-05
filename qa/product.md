# agwinterm — QA adapter

How the `ui-qa` skill brings this product up, drives it and observes it. The cases in `qa/*.md`
assume everything here.

## Build

```powershell
dotnet build Agwinterm.slnx -c Release
```

**Two Release output roots exist and only one is fresh.** The solution sets Platform=x64, so the
build writes `src\Agwinterm.Win32\bin\x64\Release\net10.0-windows\win-x64\`. The sibling
`src\Agwinterm.Win32\bin\Release\net10.0-windows\win-x64\` is a leftover from an older
configuration and is never refreshed — launching it runs months-old code and reports green for a
fix that is not there. That happened, on the bug half these cases exist for.

`Resolve-Binary` in `tests/ui/lib.ps1` handles it: it takes the **newest** matching exe under
`bin`, and prints which one it chose. Read that line before trusting a run.

The native core is separate and can be stale in the same way — a mismatched ABI fails five unit
tests before QA starts:

```powershell
cd native\agwinterm-core; cargo build --release
```

## Launching an isolated instance

`Start-Sandbox` in `tests/ui/lib.ps1`:

```powershell
. tests\ui\lib.ps1
$exe = Resolve-Binary $null 'Agwinterm.Win32.exe' 'Agwinterm.Win32'
$ctl = Resolve-Binary $env:AGWINTERMCTL 'agwintermctl.exe' 'Agwinterm.Ctl'
$s = Start-Sandbox -Exe $exe -Ctl $ctl -Pipe 'qa1' -Conf @('copy-on-ctrl-c = true')
```

It passes `--pipe <name> --app-id <name> --no-restore`, waits for the control pipe to answer,
un-maximises the window and places it at a **fixed size (1100x700 at 150,100)** — every coordinate
in every case is client-relative to that window.

`--app-id` is what isolates config and state. A `%LOCALAPPDATA%` override does **not** work here:
.NET resolves LocalApplicationData through the known-folder API and reads the user's real profile
regardless, so a case that relied on it would be writing to the user's own session list.

`Stop-Sandbox $s` kills the instance and removes its directory. Always in a `finally`.

**The one relaunch without `--no-restore`** is `Restart-Sandbox $s` (`-Kill` for `Stop-Process`
instead of `WM_CLOSE`): it closes the instance, relaunches the **same** `--pipe` / `--app-id` so the
app reads its own saved tree, and updates `$s` in place. `--no-restore` gates only the restore —
saves still happen — so the first launch needs no special form. It refuses any app-id that is not
one `Start-Sandbox` minted (`Test-SandboxAppId`): against the product's own ids the relaunch would
restore, and on its next save overwrite, the user's real session list. The persisted cases live in
`tests/integration/restore-roundtrip.ps1`; `qa/persistence.md` has the visible surfaces.

## Driving input

`tests/ui/lib.ps1` defines `[AgwUi]`, all `PostMessage` to the sandbox window:

| call | what it does |
| --- | --- |
| `Drag($h, x1,y1, x2,y2)` | press, 8 moves, release — client coordinates |
| `DragHold($h, x1,y1, x2,y2, holdMs)` | as above but **holds** outside the pane, so drag-autoscroll ticks |
| `Click($h, button, x, y)` | 1 = left, 2 = right |
| `Wheel($h, cx, cy, notches)` | wheel up; takes client coords and converts (WM_MOUSEWHEEL carries screen coords) |
| `Key($h, vk, times)` | a key with no modifiers |
| `Chord($h, vk, $shift)` | Ctrl(+Shift)+key |

`Chord` is the only one that touches key state: a posted `WM_KEYDOWN` cannot make `GetKeyState`
see Ctrl, so it attaches to **this instance's** input queue for the duration and restores what it
found. Nothing is injected globally — the user keeps typing in their own window throughout.

## Observing

Through the real control client, `agwintermctl`, against the sandbox pipe:

| helper | verb | what it answers |
| --- | --- | --- |
| `Get-PaneText $s` | `session text` | what the pane is showing right now |
| `Get-PaneSelection $s` | `session copy` | what a copy would return — the same path Ctrl+C takes |
| `Send-Ctl $s @('session','type', "text`r")` | `session type` | type into the sandbox |
| `Send-Ctl $s @('selection','all' / 'clear')` | `selection.*` | drive selection without the mouse |

**The invariant most cases rest on:** `session copy` and `session text` must agree. A selection may
only name lines the pane can SHOW, so anything the copy returns has to be findable in the visible
text. That is why the checks compare the two rather than comparing against a literal.

**Clear `AGWINTERM_SESSION_ID` / `AGWINTERM_PANE_ID` / `AGWINTERM_PIPE` before every ctl call.**
`Send-Ctl` does it, and it matters more than it looks: a QA run is started from inside an agwinterm
pane, so those variables are set, and a verb with no `--target` resolves *the caller's* session.
Against a sandbox that session does not exist — `session text` comes back empty and `session type`
is accepted and thrown away, with no error anywhere. A pass built on that is a pass over nothing;
it happened on the first run of this suite.

**Pixels** come from `Save-SandboxCapture $s <png> [-Crop x,y,w,h]` — `PrintWindow` with
`PW_RENDERFULLCONTENT` (a Direct2D window renders nothing into a plain DC), never `CopyFromScreen`,
which grabs whatever is on top. Crops are window-relative **device** pixels: multiply DIP by
`Get-SandboxScale $s`. `Compare-Capture a b` says whether two captures are identical, which is how a
case asserts "this region did not move" instead of eyeballing it.

The clipboard is read with `Get-Clipboard`. Some machines have none — the cases that need it skip
rather than fail, and say so. Always restore what was on it.

## Fixtures

`qa/fixtures/*.ps1`, written into the sandbox's temp dir and run with `session type`. Paths use
forward slashes so no case has to quote a backslash.

## What a run looks like

```powershell
. tests\ui\lib.ps1     # driver
# then follow qa/selection.md, qa/control-read.md, qa/control-honesty.md, qa/clipboard.md, qa/persistence.md
```

There is no runner script by design: the cases are the specification, and the agent executing them
is the runner. `tests/ui/lib.ps1` is plumbing — it holds no assertions and no cases.

## Also worth running before a PR

```powershell
dotnet test Agwinterm.slnx -c Release           # 312 incl. SelectionBoundsTests
pwsh tests\conformance\conformance.ps1          # control-API shape, shared with agliteterm
```
