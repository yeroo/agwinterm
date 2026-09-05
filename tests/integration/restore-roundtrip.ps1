# Restart round-trip: state an agent sets must survive the app it set it in (P3, batch P3 of the
# parity programme; docs/plans/2026-09-05-p3-persistence.md).
#
# Every cell is: sandbox up -> set a context and capture a running command -> the app goes down
# (gracefully, or killed) -> the SAME instance relaunches WITHOUT --no-restore -> `tree --json` must
# carry the context and the captured command. Modelled on agliteterm's test/restore-matrix.ps1
# (Cell / Stop-Lite -Kill / Signature): the killed cell is the one the batch exists for — before P3
# every ordinary save wrote "" into the captured slot, so a crash, a Stop-Process or a missed
# update-quit left exactly the file a restart then read.
#
#   graceful   WM_CLOSE -> WM_DESTROY -> the final save; relaunch; context + capture are back
#   killed     Stop-Process -Force: no final save, only the checkpoints `session context` and
#              `restore capture` wrote; relaunch; same assertions
#   refusal    a refused context (control character, over-length) and a refused capture target
#              before a graceful restart: the OLD value comes back and no slot was touched
#   replay     restore-commands = true in the sandbox's own agwinterm.conf, killed: after the
#              relaunch the captured command is typed back into the pane (`session text` shows it
#              with the `& ` prefix the replay adds, which the original typed line never had)
#
# The long-lived child is `ping -n 300 127.0.0.1`, not a powershell one-liner: powershell, pwsh and
# cmd are on the restore denylist (restore-denylist.conf), so a shell child is the honest null and
# would prove nothing. ping is not denylisted, is quiet enough, and ends on its own.
#
# House rules (qa/product.md): sandbox instances only (--pipe AND --app-id, minted by Start-Sandbox;
# Restart-Sandbox refuses anything else — this is the first script that relaunches without
# --no-restore, which against a real app-id would restore and then overwrite the user's sessions),
# never keybd_event / SendInput, never the real profile. Do not run the .NET or Rust suites beside
# it: the cells are timing-sensitive (a shell must come up and echo before the capture runs).
param(
    [string]$Exe,
    [switch]$Strict,
    [string]$Only = ''          # run a single cell by name: graceful | killed | refusal | replay
)

$ErrorActionPreference = 'Stop'
# agwintermctl exits nonzero while a pipe is coming up, and the ping loop in the harness polls it on
# purpose; with the native-command mapping on, that poll would throw on its first iteration.
$PSNativeCommandUseErrorActionPreference = $false
. "$PSScriptRoot\..\ui\lib.ps1"

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name$(if ($detail) { " — $detail" })" }
}

"== restore round-trip =="
$ctl = Resolve-Binary $env:AGWINTERMCTL 'agwintermctl.exe' 'Agwinterm.Ctl'
$exe = Resolve-Binary $Exe 'Agwinterm.Win32.exe' 'Agwinterm.Win32'
if (-not $ctl) { "  SKIP  agwintermctl not found (set AGWINTERMCTL)"; exit ($Strict ? 1 : 0) }
if (-not $exe) { "  SKIP  agwinterm build not found (build src\Agwinterm.Win32 or pass -Exe)"; exit ($Strict ? 1 : 0) }
if ($exe -like "$env:LOCALAPPDATA\Programs\*") {
    # Resolve-Binary in lib.ps1 falls back to the installed app; a restore cell must run a build from
    # this tree, or a pre-P3 app "fails" every cell for a reason that has nothing to do with the code.
    "  SKIP  only an installed agwinterm was found; build src\Agwinterm.Win32 or pass -Exe"; exit ($Strict ? 1 : 0)
}
"  using: $exe"
"  ctl:   $ctl"

$ChildLine    = 'ping -n 300 127.0.0.1'
$ChildPattern = '(?i)ping(\.exe)?"?\s+-n 300 127\.0\.0\.1'   # the CIM command line: "C:\WINDOWS\system32\PING.EXE" -n 300 127.0.0.1

function Reply($s, [string[]]$a) { ConvertFrom-Json (Send-Ctl $s $a) }
function Tree($s)       { (Reply $s @('tree')).result }
function Sessions($s)   { Tree $s | ForEach-Object workspaces | ForEach-Object sessions }
function Node($s, $id)  { Sessions $s | Where-Object { $_.id -eq $id } }
function Sid($s)        { (Tree $s).workspaces[0].sessions[0].id }
function PaneText($s, $id) { [string](Reply $s @('session', 'text', '--target', $id)).result }
function StateFile($s) {
    Get-ChildItem -LiteralPath (Join-Path $s.AppDir 'windows') -Filter '*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

# A shell that has not drawn its prompt yet drops what is typed at it (the ConPTY early-discard
# class), so wait for SOME text before typing, and type again if the echo never shows.
function Wait-Prompt($s, $id) {
    for ($i = 0; $i -lt 40; $i++) {
        if ((PaneText $s $id).Trim().Length -gt 0) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function Start-Child($s, $id) {
    foreach ($attempt in 1, 2) {
        Send-Ctl $s @('session', 'type', "$ChildLine`r", '--target', $id) | Out-Null
        for ($i = 0; $i -lt 16; $i++) {
            Start-Sleep -Milliseconds 250
            if ((PaneText $s $id) -match 'Pinging 127\.0\.0\.1') { return $true }
        }
    }
    return $false
}

# A killed app takes its ConPTYs with it and the children normally go too; if one does not, it must
# not outlive the run. Only OUR child's exact command line, and only when its parent is gone.
function Stop-StrayChildren {
    Get-CimInstance Win32_Process -Filter "Name = 'PING.EXE'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '-n 300 127\.0\.0\.1' } | ForEach-Object {
            $parent = Get-Process -Id $_.ParentProcessId -ErrorAction SilentlyContinue
            if (-not $parent) {
                "        (stopping stray ping pid $($_.ProcessId), parent $($_.ParentProcessId) gone)"
                Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            }
        }
}

# One cell: its own sandbox (own pipe, own minted app-id, own dir), torn down in finally whatever
# happened. Setup returns what Assert needs (ids); both run inside the try so a throw is reported
# as the cell's failure rather than ending the suite.
function Cell {
    param([string]$Name, [switch]$Kill, [string[]]$Conf = @(), [scriptblock]$Setup, [scriptblock]$Assert)
    if ($Only -and $Only -ne $Name) { return }
    "-- $Name --"
    $s = $null
    try {
        $s = Start-Sandbox -Exe $exe -Ctl $ctl -Pipe "rr-$Name" -Conf $Conf
        if (-not $s.Proc -or $s.Proc.HasExited -or -not ((Send-Ctl $s @('ping')) -match '"ok":true')) { throw "the sandbox for '$Name' never came up" }
        # Setup's Check lines and its return value share one output stream: print the lines, keep the
        # hashtable — otherwise a setup FAIL is counted but never shown.
        $produced = @(& $Setup $s)
        $produced | Where-Object { $_ -is [string] } | ForEach-Object { $_ }
        $ctx = $produced | Where-Object { $_ -is [hashtable] } | Select-Object -Last 1
        if (-not $ctx) { throw 'setup returned no context' }
        $oldPid = $s.Proc.Id
        Restart-Sandbox $s -Kill:$Kill | Out-Null
        Check "$Name`: the same instance relaunched (new pid, same pipe, same app-id)" `
            ($s.Proc.Id -ne $oldPid -and -not $s.Proc.HasExited) "old=$oldPid new=$($s.Proc.Id)"
        & $Assert $s $ctx
    } catch {
        Check "$Name ran to completion" $false $_.Exception.Message
    } finally {
        if ($s) { Stop-Sandbox $s }
        Stop-StrayChildren
    }
}

# The shared setup: a context on the first session, an idle second session, the child in the first
# session's pane, a capture, and the state file already carrying both — the world before the restart.
function Setup-ContextAndCapture($s, [string]$contextText, [string]$name) {
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'the first shell never drew a prompt' }
    $set = Reply $s @('session', 'context', $contextText, '--target', $sid)
    Check "$name`: session context replies with the value in effect" ($set.ok -and $set.result.context -eq $contextText) ($set | ConvertTo-Json -Compress)
    $idle = [string](Reply $s @('session', 'new', '--name', 'idle', '--no-select')).result
    for ($i = 0; $i -lt 30 -and -not (Node $s $idle); $i++) { Start-Sleep -Milliseconds 200 }
    if (-not (Start-Child $s $sid)) { throw 'the ping child never echoed in the first pane' }
    $cap = Reply $s @('restore', 'capture')
    $mine = @($cap.result.panes) | Where-Object { $_.pane -eq $sid }
    Check "$name`: restore capture names the running command in the first pane" `
        ($cap.ok -and $cap.result.captured -ge 1 -and $mine -and ("$($mine.captured)" -match $ChildPattern)) ($cap | ConvertTo-Json -Compress -Depth 5)
    Start-Sleep -Milliseconds 600
    $file = StateFile $s
    $raw = if ($file) { Get-Content -LiteralPath $file.FullName -Raw } else { '' }
    Check "$name`: the state file already carries the context and the capture (what a kill leaves)" `
        (($raw -match ('"Context":\s*"' + [regex]::Escape($contextText) + '"')) -and ($raw -match '(?i)"Command":\s*"[^"]*ping')) "file=$($file.FullName)"
    return @{ Sid = $sid; Idle = $idle; Context = $contextText; ReplayOnRestore = [bool]$cap.result.replayOnRestore }
}

function Assert-ContextAndCapture($s, $ctx, [string]$name) {
    Start-Sleep -Seconds 3   # the restored shells come up; the tree is readable before they do
    $n = Node $s $ctx.Sid
    Check "$name`: the context is back on the same session id" ($n -and $n.context -eq $ctx.Context) "node=$($n | ConvertTo-Json -Compress)"
    Check "$name`: the captured command is back under capturedCommands, readable before any replay" `
        ($n -and $n.PSObject.Properties['capturedCommands'] -and ("$($n.capturedCommands.($ctx.Sid))" -match $ChildPattern)) `
        "capturedCommands=$($n.capturedCommands | ConvertTo-Json -Compress)"
    $idle = Node $s $ctx.Idle
    Check "$name`: the idle session is back without a context or a capture" `
        ($idle -and -not $idle.PSObject.Properties['context'] -and -not ($idle.PSObject.Properties['capturedCommands'] -and $idle.capturedCommands.PSObject.Properties[$ctx.Idle])) `
        "node=$($idle | ConvertTo-Json -Compress)"
}

Cell -Name 'graceful' -Setup {
    param($s)
    Setup-ContextAndCapture $s 'graceful: survives WM_CLOSE' 'graceful'
} -Assert {
    param($s, $ctx)
    Assert-ContextAndCapture $s $ctx 'graceful'
    Start-Sleep -Seconds 4   # past the 2.5 s replay delay: with restore-commands off nothing is typed back
    Check 'graceful: nothing was replayed (restore-commands is off, and the capture reply said so)' `
        ((-not $ctx.ReplayOnRestore) -and ((PaneText $s $ctx.Sid) -notmatch '(?i)& "[^"]*ping')) "text=$(PaneText $s $ctx.Sid)"
}

Cell -Name 'killed' -Kill -Setup {
    param($s)
    Setup-ContextAndCapture $s 'killed: survives Stop-Process' 'killed'
} -Assert {
    param($s, $ctx)
    Assert-ContextAndCapture $s $ctx 'killed'
}

Cell -Name 'refusal' -Setup {
    param($s)
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'the first shell never drew a prompt' }
    $keep = 'keep me: the refusals below change nothing'
    $set = Reply $s @('session', 'context', $keep, '--target', $sid)
    Check 'refusal: the context to keep is set' ($set.ok -and $set.result.context -eq $keep) ($set | ConvertTo-Json -Compress)
    $ctl1 = Reply $s @('session', 'context', "two`nlines", '--target', $sid)
    $ctl2 = Reply $s @('session', 'context', ('y' * 201), '--target', $sid)
    $cap  = Reply $s @('restore', 'capture', '--target', 'no-such-pane-zz')
    Start-Sleep -Milliseconds 600
    $n = Node $s $sid
    Check 'refusal: a newline is refused' ((-not $ctl1.ok) -and ("$($ctl1.error)" -match 'U\+000A')) "error=$($ctl1.error)"
    Check 'refusal: over the ceiling is refused' ((-not $ctl2.ok) -and ("$($ctl2.error)" -match '\b200\b')) "error=$($ctl2.error)"
    Check 'refusal: an unknown capture target is refused' ((-not $cap.ok) -and ("$($cap.error)" -match 'no-such-pane-zz')) "error=$($cap.error)"
    Check 'refusal: the old context stands and no slot was written' ($n.context -eq $keep -and -not $n.PSObject.Properties['capturedCommands']) "node=$($n | ConvertTo-Json -Compress)"
    $raw = (Get-Content -LiteralPath (StateFile $s).FullName -Raw)
    Check 'refusal: the state file carries the kept value and none of the refused text' `
        (($raw -match ('"Context":\s*"' + [regex]::Escape($keep) + '"')) -and ($raw -notmatch 'two') -and ($raw -notmatch 'yyyyy')) ''
    return @{ Sid = $sid; Context = $keep }
} -Assert {
    param($s, $ctx)
    Start-Sleep -Seconds 3
    $n = Node $s $ctx.Sid
    Check 'refusal: after the restart the kept context is back and no capture appeared' `
        ($n -and $n.context -eq $ctx.Context -and -not $n.PSObject.Properties['capturedCommands']) "node=$($n | ConvertTo-Json -Compress)"
}

# restore-commands is a config key (TerminalConfig.cs: `restore-commands = false` by default), and the
# sandbox has its own agwinterm.conf, so the replay half is checkable here rather than asserted.
Cell -Name 'replay' -Kill -Conf @('restore-commands = true') -Setup {
    param($s)
    $produced = @(Setup-ContextAndCapture $s 'replay: typed back after a kill' 'replay')
    $ctx = $produced | Where-Object { $_ -is [hashtable] } | Select-Object -Last 1
    $produced | Where-Object { $_ -is [string] }
    Check 'replay: the capture reply says the slot WILL be typed back (replayOnRestore true)' ([bool]$ctx.ReplayOnRestore) ''
    $ctx
} -Assert {
    param($s, $ctx)
    Assert-ContextAndCapture $s $ctx 'replay'
    # The replay is typed 2.5 s after the shell starts, with the call operator prefixed; give the
    # shell (profile and all) time to have drawn it.
    $typed = $false; $text = ''
    for ($i = 0; $i -lt 40; $i++) {
        $text = PaneText $s $ctx.Sid
        if ($text -match '(?i)& "[^"]*ping[^"]*"\s+-n 300 127\.0\.0\.1') { $typed = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Check 'replay: the captured command was typed back into the pane (with the `& ` prefix the replay adds)' $typed "text=$text"
}

if ($fail) { "restore round-trip: $fail FAILED"; exit 1 }
"restore round-trip: all passed"
exit 0
