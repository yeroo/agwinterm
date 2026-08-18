# lite session restore — scenario matrix.
#
# "restore sessions doesn't work at all" (work laptop, 2026-07-31) has never reproduced on the dev
# machine, so this walks the real-world combinations one cell at a time until one fails. Each cell:
#
#     clean state+log -> launch -> set up sessions -> close (or kill) -> relaunch -> compare
#
# and reads lite.log for WHICH of restoreSessions()'s four exits ran, so a failing cell explains
# itself instead of just saying "sessions missing".
#
# House rules: sandbox instances only (never the default, which owns real user state), no global
# input injection, and every cell gets its own instance name so cells cannot contaminate each other.
#
# One shared resource remains: the pty-host control pipe is keyed on the APP id, not the instance, so
# every cell (and any lite window already open) talks to the same agwinterm-ptyhost.exe. Don't run
# the suite with a real lite window open — the last instance out shuts the host down.
param(
    [string]$Exe = "$PSScriptRoot\..\bin\agwinterm-lite.exe",
    [string]$Only = ''          # run a single cell by name
)

$ErrorActionPreference = 'Stop'
# agwintermctl exits nonzero while a pipe is still coming up, and Start-Lite polls it on purpose.
# Pinned rather than assumed: with the native-command mapping on, that poll throws on its first
# iteration and every cell fails for a reason that has nothing to do with restore.
$PSNativeCommandUseErrorActionPreference = $false
$ctl = "$env:LOCALAPPDATA\Programs\agwinterm\agwintermctl.exe"
$dir = "$env:LOCALAPPDATA\agwinterm-lite"
$script:failed = @()

function State($inst) { "$dir\sessions-$inst.tsv" }
function Log($inst)   { "$dir\lite-$inst.log" }

# Seed a state file with EXACTLY these bytes. Not Set-Content -Encoding utf8: that means "UTF-8 with
# BOM" on Windows PowerShell 5.1 and "UTF-8 without BOM" on pwsh 7, so under 5.1 every seeded file
# would start with EF BB BF and the "V1"/"V2" header would never be recognised — cells like
# future-v2 would fail for a reason that has nothing to do with lite. run-all.ps1 does not pin the
# host, so the suite has to write the same bytes on either one.
function Write-State([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding $false))
}

function Reset-Cell($inst) {
    Remove-Item (State $inst), (Log $inst), "$(Log $inst).old", "$(State $inst).bak", "$(State $inst).tmp" `
        -ErrorAction SilentlyContinue
}

# The tree as objects. Regexing this JSON is what let a workspace object and the first session
# inside it be read as one blob, so every reader below goes through here.
function Tree($inst) {
    $raw = (& $ctl tree --json --pipe $inst 2>&1) -join ''
    try { $j = $raw | ConvertFrom-Json } catch { return $null }
    if ($j.PSObject.Properties.Name -contains 'result') { $j = $j.result }
    if ($j -and ($j.PSObject.Properties.Name -contains 'workspaces')) { return @($j.workspaces) }
    $null
}

# Every session in the window, in tree order (workspace rows are not sessions).
function SessionsOf($inst) {
    $ws = Tree $inst
    if ($null -eq $ws) { return @() }
    @($ws | ForEach-Object { $_.sessions })
}

# The newest real session id in the tree.
function LastSessionId($inst) { @(SessionsOf $inst | ForEach-Object { $_.id })[-1] }

function Start-Lite($inst) {
    $p = Start-Process $Exe -ArgumentList @('--pipe', $inst) -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 400
        $r = (& $ctl tree --json --pipe $inst 2>&1) -join ''
        if ($r -match '"ok":true') { return $p }
    }
    throw "lite ($inst) did not answer its control pipe"
}

function Stop-Lite($p, [switch]$Kill) {
    if ($Kill) { Stop-Process -Id $p.Id -Force; Start-Sleep -Seconds 2; return }
    $p.CloseMainWindow() | Out-Null
    for ($i = 0; $i -lt 25; $i++) { Start-Sleep -Milliseconds 400; $p.Refresh(); if ($p.HasExited) { break } }
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    Start-Sleep -Seconds 1
}

# Best-effort teardown for a cell that threw part-way. A leaked instance keeps the pipe name, so the
# NEXT run of that cell would connect to the stale process and test nothing.
function Stop-Leftover($p) {
    if (-not $p) { return }
    try { $p.Refresh(); if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Start-Sleep -Seconds 1 } } catch { }
}

# A cell's fingerprint: workspace names (prefixed '#') and the session names inside them, in tree
# order. Names are what the user actually notices; the prefix keeps a workspace row from being
# counted as a session (which let a "restored 2 sessions" assertion pass on one session + one
# workspace, i.e. on a setup that had silently failed).
function Signature($inst) {
    $ws = Tree $inst
    if ($null -eq $ws) { return '' }
    $parts = @()
    foreach ($w in $ws) {
        $parts += "#$($w.name)"
        foreach ($s in @($w.sessions)) { $parts += $s.name }
    }
    ($parts -join '|')
}

# How many real sessions a signature holds.
function SessionCount($sig) { @($sig -split '\|' | Where-Object { $_ -and $_ -notlike '#*' }).Count }

# Does the whole log say this? (Restore-Verdict only shows the last few lines, so a decision made
# early in a run — like the .bak fallback — is not visible there.)
function Log-Has($inst, [string]$pattern) {
    $l = Log $inst
    (Test-Path $l) -and ((Get-Content $l -Raw) -match $pattern)
}

function Restore-Verdict($inst) {
    $l = Log $inst
    if (-not (Test-Path $l)) { return '(no log)' }
    $lines = Get-Content $l | Where-Object { $_ -match 'restore:|save ok|save FAILED|save PARTIAL' }
    if (-not $lines) { return '(no save/restore lines)' }
    ($lines | Select-Object -Last 4) -join "`n        "
}

function Cell {
    param(
        [string]$Name,
        [scriptblock]$Setup,          # receives the instance name; creates the state to be restored
        [switch]$Kill,                # forced kill instead of a graceful close
        [scriptblock]$Assert          # receives (before, after, instance); returns $true when the cell passes
    )
    if ($Only -and $Only -ne $Name) { return }
    $inst = "rm-$Name"
    Reset-Cell $inst
    $before = ''; $after = ''; $err = ''; $ok = $false
    $p = $null; $p2 = $null
    try {
        $p = Start-Lite $inst
        & $Setup $inst
        Start-Sleep -Seconds 2
        $before = Signature $inst
        Stop-Lite $p -Kill:$Kill; $p = $null

        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 2
        $after = Signature $inst
        Stop-Lite $p2; $p2 = $null
        # The assert runs INSIDE the try. $ErrorActionPreference is Stop, and several asserts read
        # files that a genuine regression would have removed — outside, one such cell terminated the
        # whole suite (skipping every cell after it, and the exit code with them).
        $ok = [bool](& $Assert $before $after $inst)
    } catch { $err = $_.Exception.Message; $ok = $false }
    finally { Stop-Leftover $p; Stop-Leftover $p2 }

    if ($ok) {
        "  PASS  {0,-22} [{1}]" -f $Name, $after
    } else {
        $script:failed += $Name
        "  FAIL  {0,-22}" -f $Name
        if ($err) { "        error:  $err" }
        "        before: [$before]"
        "        after:  [$after]"
        "        log:    $(Restore-Verdict $inst)"
    }
}

"== restore matrix =="

# --- baseline cells ---------------------------------------------------------------------------

Cell -Name 'single' -Setup {
    param($i)   # the instance starts with one session already
} -Assert { param($b, $a) $a -eq $b -and $b -ne '' }

Cell -Name 'several' -Setup {
    param($i)
    & $ctl session new --pipe $i 2>&1 | Out-Null; Start-Sleep -Seconds 2
    & $ctl session new --pipe $i 2>&1 | Out-Null; Start-Sleep -Seconds 2
} -Assert { param($b, $a) $a -eq $b -and (SessionCount $b) -ge 3 }

Cell -Name 'renamed' -Setup {
    param($i)
    $id = LastSessionId $i
    & $ctl session rename my-renamed-session --target $id --pipe $i 2>&1 | Out-Null
    Start-Sleep -Seconds 2
} -Assert { param($b, $a) $a -eq $b -and $a -match 'my-renamed-session' }

Cell -Name 'workspaces' -Setup {
    param($i)
    & $ctl workspace new second-ws --pipe $i 2>&1 | Out-Null; Start-Sleep -Seconds 2
    & $ctl session new --pipe $i 2>&1 | Out-Null; Start-Sleep -Seconds 2
} -Assert { param($b, $a) $a -eq $b -and $a -match 'second-ws' }

Cell -Name 'flagged' -Setup {
    param($i)
    $id = LastSessionId $i
    & $ctl session flag on --target $id --pipe $i 2>&1 | Out-Null
    Start-Sleep -Seconds 2
} -Assert {
    param($b, $a, $i)
    if ($a -ne $b) { return $false }
    (Test-Path (State $i)) -and ((Get-Content (State $i) -Raw) -match '(?m)^F\t')   # the F line survived
}

# --- cells that differ from a clean dev run ----------------------------------------------------
# These are the ones that could plausibly explain the work-laptop report.

# A spec carrying an explicit app + args — what a shell profile produces. ctl's session.new always
# makes a DEFAULT session, so the state file is seeded directly: that is also the exact shape
# restoreSessions() has to cope with, and it keeps the cell deterministic.
function Seeded {
    param([string]$Name, [string]$Tsv, [string]$Bak, [string]$Tmp, [scriptblock]$Assert)
    if ($Only -and $Only -ne $Name) { return }
    $inst = "rm-$Name"
    Reset-Cell $inst
    if ($null -ne $Tsv) { Write-State (State $inst) $Tsv }
    if ($Bak) { Write-State "$(State $inst).bak" $Bak }
    if ($Tmp) { Write-State "$(State $inst).tmp" $Tmp }
    $after = ''; $err = ''; $ok = $false
    $p = $null
    try {
        $p = Start-Lite $inst
        Start-Sleep -Seconds 3
        $after = Signature $inst
        Stop-Lite $p; $p = $null
        $ok = [bool](& $Assert $after $inst)   # inside the try — see Cell
    } catch { $err = $_.Exception.Message; $ok = $false }
    finally { Stop-Leftover $p }
    if ($ok) { "  PASS  {0,-22} [{1}]" -f $Name, $after }
    else {
        $script:failed += $Name
        "  FAIL  {0,-22}" -f $Name
        if ($err) { "        error:  $err" }
        "        after:  [$after]"
        "        log:    $(Restore-Verdict $inst)"
    }
}

$cwd = (Resolve-Path "$PSScriptRoot\..").Path
# The name coming back is only half of it: a build that dropped app/args from the S line on re-save,
# or restored the spec as a plain default shell, would pass a name-only assertion. Check the re-saved
# S line still carries the app AND its args — that is the shape a shell profile actually produces.
Seeded -Name 'app-spec' `
    -Tsv "V1`nW`tws-a`nS`t0`tprofile-session`tpowershell.exe`t$cwd`t-NoLogo`t-NoExit`nA`t0`n" -Assert {
    param($a, $i)
    if ($a -notmatch 'profile-session') { return $false }
    $line = @(Get-Content (State $i) | Where-Object { $_ -match 'profile-session' })[0]
    $f = $line -split "`t"
    # S <ws> <name> <app> <cwd> <arg0> <arg1>
    ($f[3] -eq 'powershell.exe') -and ($f[5] -eq '-NoLogo') -and ($f[6] -eq '-NoExit')
}

# A spec whose app does not exist on this machine — the shape of "a profile that doesn't resolve
# on the laptop". It must stay VISIBLE (a dead session, not a hole) and the log must name it.
Seeded -Name 'bogus-app' -Tsv "V1`nW`tws-b`nS`t0`tdead-session`tno-such-program-xyz.exe`t$cwd`nA`t0`n" -Assert {
    param($a, $i)
    # Silence is the one unacceptable outcome — that is what made this unreportable in the field.
    ($a -match 'dead-session') -and (Log-Has $i "session 'dead-session' FAILED to start")
}

# The session the control API reports for a given name ($null when there is none).
function Session-Named($inst, $name) { @(SessionsOf $inst | Where-Object { $_.name -eq $name })[0] }

# The full round trip for a spec that cannot start here: it must come back as a DEAD session rather
# than a hole, keep its name/workspace, be saved again (so it is recoverable on a machine where the
# app exists), and it must not disturb the good spec beside it.
if (-not $Only -or $Only -eq 'failed-spec') {
    $inst = 'rm-failed-spec'
    Reset-Cell $inst
    Write-State (State $inst) `
        "V1`nW`tws-f`nS`t0`tghost-session`tno-such-program-xyz.exe`t$cwd`nS`t0`tlive-session`tpowershell.exe`t$cwd`nA`t0`n"
    $err = ''; $ghost = $null; $live = $null; $named = $false; $resaved = $false; $after = ''
    $ghostOk = $false; $liveOk = $false
    $p = $null; $p2 = $null
    try {
        $p = Start-Lite $inst
        Start-Sleep -Seconds 3
        $ghost = Session-Named $inst 'ghost-session'
        $live  = Session-Named $inst 'live-session'
        $ghostOk = [bool]($ghost -and $ghost.failed -and $ghost.exited)
        $liveOk  = [bool]($live -and -not $live.failed -and -not $live.exited)
        $named = Log-Has $inst "session 'ghost-session' FAILED to start"
        Stop-Lite $p; $p = $null
        # Kept in the state file: the entry survives, so it can start on a machine that has the app.
        $resaved = (Test-Path (State $inst)) -and ((Get-Content (State $inst) -Raw) -match 'ghost-session')
        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 3
        $after = Signature $inst
        Stop-Lite $p2; $p2 = $null
    } catch { $err = $_.Exception.Message }
    finally { Stop-Leftover $p; Stop-Leftover $p2 }

    if (-not $err -and $ghostOk -and $liveOk -and $named -and $resaved -and
        $after -match 'ghost-session' -and $after -match 'live-session') {
        "  PASS  {0,-22} [{1}]" -f 'failed-spec', $after
    } else {
        $script:failed += 'failed-spec'
        "  FAIL  failed-spec"
        if ($err) { "        error:  $err" }
        "        ghost:  [$($ghost | ConvertTo-Json -Compress)] (dead entry kept: $ghostOk)"
        "        live:   [$($live | ConvertTo-Json -Compress)] (good spec unaffected: $liveOk)"
        "        log named it: $named ; re-saved: $resaved"
        "        after:  [$after]"
        "        log:    $(Restore-Verdict $inst)"
    }
}

# Forced kill: no OnDestroy, so restore depends entirely on the refreshTree() save. If a laptop
# shutdown or crash explains the report, this is the cell that shows it.
# Also the ONLY cell where adoption can fire: the pty-host outlives a killed UI, so its shells are
# still running and must be picked back up rather than re-created. The log assertion is what tells
# those two apart — the names come back identically either way, so without it this cell passes just
# as happily when adoption is broken (which is how it shipped unverified).
Cell -Name 'killed' -Setup {
    param($i)
    & $ctl session new --pipe $i 2>&1 | Out-Null; Start-Sleep -Seconds 2
} -Kill -Assert {
    param($b, $a, $i)
    $a -eq $b -and (SessionCount $b) -ge 2 -and (Log-Has $i 'adopted live session')
}

# Adoption's user-visible half, which 'killed' cannot see: it compares NAMES, and a name comes back
# from the state file whether the pane behind it has any content or not. An adopted session gets a
# brand-new empty emulator and the host forwards only NEW output, so "the shells are alive" and "the
# user can see anything" are separate claims. Put a marker on the screen, kill, adopt, demand it back.
#
# Honest about what this does and does not pin: it passes both with and without Attach.repaint,
# because the post-restore resize already makes ConPTY re-emit. It is a guard on the PROPERTY (an
# adopted pane has its screen), not a discriminator for the mechanism that provides it.
if (-not $Only -or $Only -eq 'killed-repaint') {
    $inst = 'rm-killed-repaint'
    Reset-Cell $inst
    $err = ''; $seenBefore = $false; $adopted = $false; $seenAfter = $false; $textAfter = ''
    $p = $null; $p2 = $null
    try {
        $p = Start-Lite $inst
        $id = LastSessionId $inst
        & $ctl session type "echo ADOPTME-MARKER`n" --target $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 3
        # Prove the premise: without this, a `type` that never landed would make the cell assert
        # nothing and "fail" for a reason that has nothing to do with repaint.
        $seenBefore = ((& $ctl session text --target $id --pipe $inst 2>&1) -join "`n") -match 'ADOPTME-MARKER'
        Stop-Lite $p -Kill; $p = $null

        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 4                       # the repaint jiggle is async in the host
        $adopted = Log-Has $inst 'adopted live session'
        $id2 = LastSessionId $inst
        $textAfter = (& $ctl session text --target $id2 --pipe $inst 2>&1) -join "`n"
        $seenAfter = $textAfter -match 'ADOPTME-MARKER'
        Stop-Lite $p2; $p2 = $null
    } catch { $err = $_.Exception.Message }
    finally { Stop-Leftover $p; Stop-Leftover $p2 }
    if (-not $err -and $seenBefore -and $adopted -and $seenAfter) {
        "  PASS  {0,-22} (an adopted pane comes back with its screen)" -f 'killed-repaint'
    } else {
        $script:failed += 'killed-repaint'
        "  FAIL  killed-repaint"
        if ($err) { "        error:  $err" }
        "        marker on screen before the kill: $seenBefore"
        "        session was adopted:              $adopted"
        "        marker back after adoption:       $seenAfter"
        "        after: [$(($textAfter -replace '\s+', ' ').Trim())]"
        "        log:   $(Restore-Verdict $inst)"
    }
}

# A session whose shell has already exited: its spec must still be saved and relaunched, otherwise
# a day's worth of sessions quietly evaporates the moment their shells end.
Cell -Name 'shell-exited' -Setup {
    param($i)
    $id = LastSessionId $i
    & $ctl session new --pipe $i 2>&1 | Out-Null; Start-Sleep -Seconds 2
    & $ctl session type "exit`n" --target $id --pipe $i 2>&1 | Out-Null
    Start-Sleep -Seconds 3
    # Prove the premise. Without this the cell is a duplicate of 'several': a `type` that silently
    # failed, or a shell that ignored `exit`, would leave it asserting nothing at all.
    $s = @(SessionsOf $i | Where-Object { $_.id -eq $id })[0]
    if (-not $s) { throw "session $id vanished instead of exiting" }
    if (-not $s.exited) { throw "the shell in $id never exited, so this cell would prove nothing" }
} -Assert { param($b, $a) $a -eq $b }

# Two windows open at once. Multi-window lite is one PROCESS per window, each with its own
# sessions-<instance>.tsv, so each window restores ITS OWN sessions and never the other's. Pinned
# because "my sessions are gone" has this mundane reading: they came back in the other window.
if (-not $Only -or $Only -eq 'two-windows') {
    $ia = 'rm-two-windows-a'; $ib = 'rm-two-windows-b'
    Reset-Cell $ia; Reset-Cell $ib
    $afterA = ''; $afterB = ''; $err = ''
    $pa = $null; $pb = $null; $pa2 = $null; $pb2 = $null
    try {
        $pa = Start-Lite $ia
        $pb = Start-Lite $ib
        & $ctl session rename win-a-session --target (LastSessionId $ia) --pipe $ia 2>&1 | Out-Null
        & $ctl session rename win-b-session --target (LastSessionId $ib) --pipe $ib 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        Stop-Lite $pa; $pa = $null
        Stop-Lite $pb; $pb = $null

        $pa2 = Start-Lite $ia; $pb2 = Start-Lite $ib
        Start-Sleep -Seconds 2
        $afterA = Signature $ia; $afterB = Signature $ib
        Stop-Lite $pa2; $pa2 = $null
        Stop-Lite $pb2; $pb2 = $null
    } catch { $err = $_.Exception.Message }
    finally { Stop-Leftover $pa; Stop-Leftover $pb; Stop-Leftover $pa2; Stop-Leftover $pb2 }
    $ok = (-not $err) -and ($afterA -match 'win-a-session') -and ($afterA -notmatch 'win-b-session') `
                       -and ($afterB -match 'win-b-session') -and ($afterB -notmatch 'win-a-session')
    if ($ok) { "  PASS  {0,-22} (each window restores only its own)" -f 'two-windows' }
    else {
        $script:failed += 'two-windows'
        "  FAIL  two-windows"
        if ($err) { "        error:  $err" }
        "        window A: [$afterA]"
        "        window B: [$afterB]"
    }
}

# --- "Restart everything" must come back as the SAME instance ----------------------------------
# restartApp() used to relaunch the bare exe, so a named window restarted as the DEFAULT instance
# and read a different sessions file. Nothing crashed; the sessions were simply someone else's.

function Find-Lite($inst) {
    Get-CimInstance Win32_Process -Filter "Name='agwinterm-lite.exe'" |
        Where-Object { $_.CommandLine -match [regex]::Escape($inst) } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}

# Every agwinterm-lite.exe alive right now, by pid. Snapshotted before the restart so anything the
# restart itself produces can be told apart from the user's own running lite.
function Lite-Pids { @(Get-CimInstance Win32_Process -Filter "Name='agwinterm-lite.exe'" | ForEach-Object { $_.ProcessId }) }

# The regression this cell exists to catch is also the regression that makes it dangerous: if
# restartCommandLine() ever drops the --pipe, "Restart everything" relaunches as the DEFAULT
# instance, which owns the user's REAL sessions.tsv and would overwrite it on its next save. The
# cell's own $p2 lookup finds nothing in that case and throws, so nothing else would ever stop it.
function Stop-Stray-Lite($known) {
    Get-CimInstance Win32_Process -Filter "Name='agwinterm-lite.exe'" |
        Where-Object { $known -notcontains $_.ProcessId } | ForEach-Object {
            "        (stopping stray lite pid $($_.ProcessId): $($_.CommandLine))"
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
}

function Restart-Cell {
    param([string]$Name)
    if ($Only -and $Only -ne $Name) { return }
    $inst = "rm-$Name"
    Reset-Cell $inst
    $before = ''; $after = ''; $err = ''
    $p = $null; $p2 = $null
    $known = Lite-Pids                        # includes the user's own lite, which must NOT be touched
    try {
        $p = Start-Lite $inst
        $known += $p.Id
        & $ctl session new --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        $before = Signature $inst
        $oldId = $p.Id

        # File > Restart everything (IDM_RESTART = 103), posted — never injected globally.
        $p.Refresh()
        if (-not $p.MainWindowHandle -or $p.MainWindowHandle -eq 0) { throw "no main window for $inst" }
        [void][Win32Post]::PostMessageW($p.MainWindowHandle, 0x0111, [IntPtr]103, [IntPtr]::Zero)

        for ($i = 0; $i -lt 25; $i++) { Start-Sleep -Milliseconds 400; $p.Refresh(); if ($p.HasExited) { break } }
        if (-not $p.HasExited) { throw 'the old instance never exited after Restart everything' }

        # The relaunch is a detached grandchild (cmd /c ping & start), so wait for its pipe.
        $p2 = $null
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 500
            $r = (& $ctl tree --json --pipe $inst 2>&1) -join ''
            if ($r -match '"ok":true') { $p2 = @(Find-Lite $inst | Where-Object { $_.Id -ne $oldId })[0]; break }
        }
        if (-not $p2) { throw 'the restarted instance never answered its control pipe' }
        $known += $p2.Id
        Start-Sleep -Seconds 2
        $after = Signature $inst
        Stop-Lite $p2; $p2 = $null
        $p = $null   # already exited: that is what this cell just asserted
    } catch { $err = $_.Exception.Message }
    finally { Stop-Leftover $p; Stop-Leftover $p2; Stop-Stray-Lite $known }

    if (-not $err -and $after -eq $before -and (SessionCount $before) -ge 2) {
        "  PASS  {0,-22} [{1}]" -f $Name, $after
    } else {
        $script:failed += $Name
        "  FAIL  {0,-22}" -f $Name
        if ($err) { "        error:  $err" }
        "        before: [$before]"
        "        after:  [$after]"
        "        log:    $(Restore-Verdict $inst)"
    }
}

if (-not ('Win32Post' -as [type])) {
    Add-Type -Namespace '' -Name Win32Post -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
'@
}

Restart-Cell -Name 'restart-named'

# The no-regression half: the DEFAULT instance must still relaunch as the default (no stray --pipe).
# Checked through --diagnose's "restart cmdline", which is the exact string restartApp() launches —
# the default instance owns real user state and this suite never starts it.
if (-not $Only -or $Only -eq 'restart-cmdline') {
    $named = (& $Exe --pipe rm-restart-cmdline --diagnose 2>&1 | Out-String)
    $plain = (& $Exe --diagnose 2>&1 | Out-String)
    $nOk = $named -match '(?m)^\s*restart cmdline: .*agwinterm-lite\.exe" --pipe "rm-restart-cmdline"'
    $dOk = ($plain -match '(?m)^\s*restart cmdline: .*agwinterm-lite\.exe"\s*$') -and ($plain -notmatch 'restart cmdline: .*--pipe')
    if ($nOk -and $dOk) {
        "  PASS  {0,-22} (named keeps --pipe; default stays bare)" -f 'restart-cmdline'
    } else {
        $script:failed += 'restart-cmdline'
        "  FAIL  restart-cmdline"
        "        named:   $(($named -split "`n" | Where-Object { $_ -match 'restart cmdline' }) -join '')"
        "        default: $(($plain -split "`n" | Where-Object { $_ -match 'restart cmdline' }) -join '')"
    }
}

# --- a good state file must never be replaced by an empty or truncated one ----------------------
# The save is atomic (tmp + rename) and keeps one .bak generation; restore falls back to the .bak.

# The transient-empty save, driven for real. Split shells are hidden and deliberately not persisted,
# so with a split open, closing the only VISIBLE session leaves lite running with zero persistable
# sessions. That save used to write a 0-session file over a good one — and with CREATE_ALWAYS there
# was nothing left to fall back to.
if (-not $Only -or $Only -eq 'zero-guard') {
    $inst = 'rm-zero-guard'
    Reset-Cell $inst
    $err = ''; $kept = $false; $skipped = $false; $after = ''
    $p = $null; $p2 = $null
    try {
        $p = Start-Lite $inst
        $id = LastSessionId $inst
        & $ctl session rename keep-me --target $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        & $ctl session split on --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        & $ctl session close $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 3
        $kept = (Test-Path (State $inst)) -and ((Get-Content (State $inst) -Raw) -match 'keep-me')
        $skipped = Log-Has $inst 'save SKIPPED'
        Stop-Lite $p; $p = $null
        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 2
        $after = Signature $inst
        Stop-Lite $p2; $p2 = $null
    } catch { $err = $_.Exception.Message }
    finally { Stop-Leftover $p; Stop-Leftover $p2 }
    if (-not $err -and $kept -and $skipped -and $after -match 'keep-me') {
        "  PASS  {0,-22} [{1}]" -f 'zero-guard', $after
    } else {
        $script:failed += 'zero-guard'
        "  FAIL  zero-guard"
        if ($err) { "        error:  $err" }
        "        state kept the session: $kept ; log said SKIPPED: $skipped"
        "        after:  [$after]"
        "        log:    $(Restore-Verdict $inst)"
    }
}

# The other side of that guard: closing the LAST session is a deliberate empty, so it must be
# written (and must not be undone by the .bak on the next launch). A guard that over-refuses would
# resurrect sessions the user closed on purpose.
if (-not $Only -or $Only -eq 'closed-last') {
    $inst = 'rm-closed-last'
    Reset-Cell $inst
    $err = ''; $tsv = 'x'; $after = ''; $bak = $true
    $p = $null; $p2 = $null
    try {
        $p = Start-Lite $inst
        $id = LastSessionId $inst
        & $ctl session rename gone-for-good --target $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        # Closing the last session empties the window. (Driven over the control pipe it does not tear
        # the window down — DestroyWindow only works from the UI thread — but the save is the same
        # one, and the save is what this cell is about.)
        & $ctl session close $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 3
        $tsv = if (Test-Path (State $inst)) { (Get-Content (State $inst) -Raw) } else { '' }
        $bak = Test-Path "$(State $inst).bak"
        if (-not (Log-Has $inst 'save ok: 0 session')) { throw 'the deliberate empty save never happened' }
        Stop-Lite $p; $p = $null
        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 2
        $after = Signature $inst
        Stop-Lite $p2; $p2 = $null
    } catch { $err = $_.Exception.Message }
    finally { Stop-Leftover $p; Stop-Leftover $p2 }
    if (-not $err -and $tsv -notmatch 'gone-for-good' -and -not $bak -and $after -notmatch 'gone-for-good') {
        "  PASS  {0,-22} (deliberate empty is honoured)" -f 'closed-last'
    } else {
        $script:failed += 'closed-last'
        "  FAIL  closed-last"
        if ($err) { "        error:  $err" }
        "        state: [$($tsv -replace "`r?`n", '\n')] ; .bak left behind: $bak"
        "        after: [$after]"
    }
}

# The guard must survive being used. Closing the last session marks the empty as DELIBERATE, and
# driven over the control pipe the window does not actually go away (DestroyWindow is a no-op off the
# UI thread) — so a window keeps running with that mark set. If it is a one-way latch rather than a
# statement about one save, every later transient empty is waved straight through and the good file
# is overwritten with a zero-session one, which is exactly what 'zero-guard' exists to prevent.
if (-not $Only -or $Only -eq 'guard-after-empty') {
    $inst = 'rm-guard-after-empty'
    Reset-Cell $inst
    $err = ''; $kept = $false; $skipped = $false; $after = ''
    $p = $null; $p2 = $null
    try {
        $p = Start-Lite $inst
        & $ctl session close (LastSessionId $inst) --pipe $inst 2>&1 | Out-Null   # window is now empty
        Start-Sleep -Seconds 3
        if (-not (Log-Has $inst 'save ok: 0 session')) { throw 'the window never reached the deliberate-empty save' }
        # Re-fill it, then drive a TRANSIENT empty exactly as zero-guard does.
        & $ctl session new --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        $id = LastSessionId $inst
        & $ctl session rename kept-after-empty --target $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        & $ctl session split on --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        & $ctl session close $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 3
        $kept = (Test-Path (State $inst)) -and ((Get-Content (State $inst) -Raw) -match 'kept-after-empty')
        $skipped = Log-Has $inst 'save SKIPPED'
        Stop-Lite $p; $p = $null
        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 2
        $after = Signature $inst
        Stop-Lite $p2; $p2 = $null
    } catch { $err = $_.Exception.Message }
    finally { Stop-Leftover $p; Stop-Leftover $p2 }
    if (-not $err -and $kept -and $skipped -and $after -match 'kept-after-empty') {
        "  PASS  {0,-22} [{1}]" -f 'guard-after-empty', $after
    } else {
        $script:failed += 'guard-after-empty'
        "  FAIL  guard-after-empty"
        if ($err) { "        error:  $err" }
        "        state kept the session: $kept ; log said SKIPPED: $skipped"
        "        after: [$after]"
        "        log:   $(Restore-Verdict $inst)"
    }
}

# The guard's counting has to agree with the save's. The save writes only VISIBLE sessions, but a
# hidden one (the quick terminal here, or a split shell) keeps the session list non-empty — so
# closing the last visible session while one is open is still a deliberate empty, and the file must
# be rewritten. Judged by the raw list instead, the save is refused and the sessions the user just
# closed are read straight back out of the untouched file on the next launch.
if (-not $Only -or $Only -eq 'closed-last-with-hidden') {
    $inst = 'rm-hidden-empty'
    Reset-Cell $inst
    $err = ''; $tsv = 'x'; $after = ''; $wrote = $false
    $p = $null; $p2 = $null
    try {
        $p = Start-Lite $inst
        $id = LastSessionId $inst
        & $ctl session rename closed-with-quick --target $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        & $ctl quick on --pipe $inst 2>&1 | Out-Null       # a hidden session the save never writes
        Start-Sleep -Seconds 2
        & $ctl session close $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 3
        $wrote = Log-Has $inst 'save ok: 0 session'
        $tsv = if (Test-Path (State $inst)) { (Get-Content (State $inst) -Raw) } else { '' }
        Stop-Lite $p; $p = $null
        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 2
        $after = Signature $inst
        Stop-Lite $p2; $p2 = $null
    } catch { $err = $_.Exception.Message }
    finally { Stop-Leftover $p; Stop-Leftover $p2 }
    if (-not $err -and $wrote -and $tsv -notmatch 'closed-with-quick' -and $after -notmatch 'closed-with-quick') {
        "  PASS  {0,-22} (hidden shells don't block the deliberate empty)" -f 'closed-last-with-hidden'
    } else {
        $script:failed += 'closed-last-with-hidden'
        "  FAIL  closed-last-with-hidden"
        if ($err) { "        error:  $err" }
        "        empty save happened: $wrote"
        "        state: [$($tsv -replace "`r?`n", '\n')]"
        "        after: [$after]"
        "        log:   $(Restore-Verdict $inst)"
    }
}

# An interrupted write can only ever leave a stray .tmp: the previous file is replaced by a rename,
# which either happened or didn't. Seed the wreckage of a half-finished save and assert both
# sessions still come back.
Seeded -Name 'interrupted-write' `
    -Tsv "V1`nW`tws-i`nS`t0`tsurvivor-one`t`t$cwd`nS`t0`tsurvivor-two`t`t$cwd`nA`t0`n" `
    -Tmp "V1`nW`tws-i`nS`t0`tsurviv" -Assert {
    param($a, $i)
    ($a -match 'survivor-one') -and ($a -match 'survivor-two')
}

# The second chance restore never had: a primary that parses to nothing, with a good previous
# generation beside it.
Seeded -Name 'bak-fallback' -Tsv '' `
    -Bak "V1`nW`tws-k`nS`t0`tfrom-the-bak`t`t$cwd`nA`t0`n" -Assert {
    param($a, $i)
    ($a -match 'from-the-bak') -and (Log-Has $i 'falling back')
}

# The other half of Task 4, and the half bak-fallback cannot reach: bak-fallback SEEDS a .bak by
# hand, so it would pass unchanged against a build that never writes one. This asserts lite itself
# rotates — that after a second save the .bak holds the PREVIOUS generation while the primary holds
# the current one. On the pre-fix in-place write there is no .bak at all and this fails outright.
if (-not $Only -or $Only -eq 'bak-rotation') {
    $inst = 'rm-bak-rotation'
    Reset-Cell $inst
    $err = ''; $bakExists = $false; $bakIsPrev = $false; $tsvIsCurrent = $false; $orig = ''
    $p = $null
    try {
        $p = Start-Lite $inst
        $id = LastSessionId $inst
        $orig = @(SessionsOf $inst | Where-Object { $_.id -eq $id })[0].name
        Start-Sleep -Seconds 2                                  # save #1: primary written, nothing to rotate yet
        & $ctl session rename rotated-generation --target $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 3                                  # save #2: primary -> .bak, new primary published
        # Read both WHILE lite runs: the shutdown save rotates again, which would put the current
        # name in the .bak too and make the "previous generation" half of this untestable.
        $bakExists = Test-Path "$(State $inst).bak"
        if ($bakExists) {
            $bak = Get-Content "$(State $inst).bak" -Raw
            $bakIsPrev = ($bak -match [regex]::Escape($orig)) -and ($bak -notmatch 'rotated-generation')
        }
        $tsvIsCurrent = (Test-Path (State $inst)) -and ((Get-Content (State $inst) -Raw) -match 'rotated-generation')
        Stop-Lite $p; $p = $null
    } catch { $err = $_.Exception.Message }
    finally { Stop-Leftover $p }
    if (-not $err -and $bakExists -and $bakIsPrev -and $tsvIsCurrent) {
        "  PASS  {0,-22} (.bak holds the previous generation)" -f 'bak-rotation'
    } else {
        $script:failed += 'bak-rotation'
        "  FAIL  bak-rotation"
        if ($err) { "        error:  $err" }
        "        .bak exists: $bakExists / holds '$orig' and not the new name: $bakIsPrev"
        "        primary holds the new name: $tsvIsCurrent"
        "        log:   $(Restore-Verdict $inst)"
    }
}

# The publish half of the atomic write, forced to fail. Nothing else in the suite reaches it: every
# other cell publishes successfully, so the rollback, the "previous state is untouched" message and
# — the point — the promise that a FAILED save never costs the saved sessions were all untested.
# Holding the primary open with no sharing is the one deterministic way to deny the rename without
# touching the state directory (which every other cell writes into at the same time).
if (-not $Only -or $Only -eq 'publish-blocked') {
    $inst = 'rm-publish-blocked'
    Reset-Cell $inst
    $err = ''; $logged = $false; $intact = $false; $after = ''
    $p = $null; $p2 = $null; $fs = $null
    try {
        $p = Start-Lite $inst
        $id = LastSessionId $inst
        & $ctl session rename survives-blocked-publish --target $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 3                       # the good save this cell must not lose
        if ((Get-Content (State $inst) -Raw) -notmatch 'survives-blocked-publish') { throw 'setup save never landed' }
        # FileShare.None: lite can create and write the .tmp, but neither ReplaceFileW nor MoveFileExW
        # can touch the primary.
        $fs = [System.IO.File]::Open((State $inst), 'Open', 'ReadWrite', 'None')
        & $ctl session rename must-not-land --target $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 3
        $logged = Log-Has $inst 'save FAILED to publish'
        # Read through the handle that holds the lock — nothing else can open the file right now.
        $fs.Position = 0
        $buf = New-Object byte[] $fs.Length
        [void]$fs.Read($buf, 0, $buf.Length)
        $held = [System.Text.Encoding]::UTF8.GetString($buf)
        $intact = ($held -match 'survives-blocked-publish') -and ($held -notmatch 'must-not-land')
        $fs.Close(); $fs = $null
        # Hard kill: a graceful stop saves again (with the lock gone) and would overwrite the very
        # file this cell is asserting survived.
        Stop-Lite $p -Kill; $p = $null
        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 2
        $after = Signature $inst
        Stop-Lite $p2 -Kill; $p2 = $null
    } catch { $err = $_.Exception.Message }
    finally { if ($fs) { try { $fs.Close() } catch { } }; Stop-Leftover $p; Stop-Leftover $p2 }
    if (-not $err -and $logged -and $intact -and $after -match 'survives-blocked-publish') {
        "  PASS  {0,-22} (a failed publish leaves the saved state readable)" -f 'publish-blocked'
    } else {
        $script:failed += 'publish-blocked'
        "  FAIL  publish-blocked"
        if ($err) { "        error:  $err" }
        "        log named the failed publish: $logged ; primary still held the old state: $intact"
        "        after: [$after]"
        "        log:   $(Restore-Verdict $inst)"
    }
}

# The OTHER half of the atomic write, and the one no other cell reaches: the temp file cannot be
# CREATED at all (a policy-locked profile, a DLP/AV agent that blocks new files), so the save falls
# back to writing sessions.tsv in place. publish-blocked locks the PRIMARY, which forces a failed
# publish, not a failed create — so this fallback shipped untested. It has to keep both promises the
# atomic path makes: a generation in the .bak (copied by hand here, since nothing rotates), and the
# .bak dropped when the user deliberately empties the window, or the next launch reads it and brings
# back exactly what they just closed.
if (-not $Only -or $Only -eq 'inplace-fallback') {
    $inst = 'rm-inplace-fallback'
    Reset-Cell $inst
    $err = ''; $inPlace = $false; $current = $false; $bakIsPrev = $false; $bakGone = $false; $after = ''
    $p = $null; $p2 = $null; $fs = $null
    try {
        $p = Start-Lite $inst
        $id = LastSessionId $inst
        & $ctl session rename inplace-one --target $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 3                       # the generation the fallback must preserve
        if ((Get-Content (State $inst) -Raw) -notmatch 'inplace-one') { throw 'setup save never landed' }
        # FileShare.None on the .tmp path: lite's CREATE_ALWAYS on it fails with a sharing violation,
        # which is the same door-slam a profile that forbids creating new files gives it.
        $fs = [System.IO.File]::Open("$(State $inst).tmp", 'Create', 'ReadWrite', 'None')
        & $ctl session rename inplace-two --target $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 3
        $inPlace = Log-Has $inst 'save ok \(IN PLACE\)'
        $current = (Get-Content (State $inst) -Raw) -match 'inplace-two'
        if (Test-Path "$(State $inst).bak") {
            $bak = Get-Content "$(State $inst).bak" -Raw
            $bakIsPrev = ($bak -match 'inplace-one') -and ($bak -notmatch 'inplace-two')
        }
        # Now the deliberate empty, still on the in-place path: the .bak must go with it.
        & $ctl session close $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 3
        $bakGone = -not (Test-Path "$(State $inst).bak")
        $fs.Close(); $fs = $null
        Stop-Lite $p -Kill; $p = $null               # a graceful stop would save again, atomically
        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 2
        $after = Signature $inst
        Stop-Lite $p2 -Kill; $p2 = $null
    } catch { $err = $_.Exception.Message }
    finally { if ($fs) { try { $fs.Close() } catch { } }; Stop-Leftover $p; Stop-Leftover $p2 }
    if (-not $err -and $inPlace -and $current -and $bakIsPrev -and $bakGone -and $after -notmatch 'inplace-two') {
        "  PASS  {0,-22} (in-place save keeps a generation, and drops it on a deliberate empty)" -f 'inplace-fallback'
    } else {
        $script:failed += 'inplace-fallback'
        "  FAIL  inplace-fallback"
        if ($err) { "        error:  $err" }
        "        wrote in place: $inPlace ; primary holds the new state: $current"
        "        .bak held the previous generation: $bakIsPrev ; dropped on the deliberate empty: $bakGone"
        "        after:  [$after]"
        "        log:    $(Restore-Verdict $inst)"
    }
}

# Backward compatibility: a plain 0.17.x file — no D line, no .bak anywhere — must still restore.
Seeded -Name 'compat-0.17' `
    -Tsv "V1`nW`tws-c`nS`t0`told-one`t`t$cwd`nS`t0`told-two`t`t$cwd`nF`t1`nA`t0`n" -Assert {
    param($a, $i)
    ($a -match 'old-one') -and ($a -match 'old-two')
}

# The D line's ordinary case, which only 'killed' covers non-deterministically: ids the host does
# NOT have. That is every graceful restart (the host kills those sessions on the way out), so the
# specs must be created fresh — an id that isn't live must never make a spec vanish.
Seeded -Name 'stale-ids' `
    -Tsv "V1`nW`tws-s`nS`t0`tstale-one`t`t$cwd`nS`t0`tstale-two`t`t$cwd`nD`tnobody-1`tnobody-2`nA`t0`n" -Assert {
    param($a, $i)
    ($a -match 'stale-one') -and ($a -match 'stale-two') -and -not (Log-Has $i 'adopted live session')
}

# A D line SHORTER than the S list (hand-edited, or written while a session had no host id yet).
# The pairing is by index, so the specs past the end of the D line must still restore rather than
# read off the end of it.
Seeded -Name 'short-id-line' `
    -Tsv "V1`nW`tws-d`nS`t0`tpaired-one`t`t$cwd`nS`t0`tunpaired-two`t`t$cwd`nD`tnobody-1`nA`t0`n" -Assert {
    param($a, $i) ($a -match 'paired-one') -and ($a -match 'unpaired-two')
}

# --- malformed / future state files: degrade, never crash and never take the window down -------
# Every one of these is a file the user can genuinely end up with: a save that never got any bytes,
# a disk that filled mid-write, a downgrade after running a newer build, a hand-edited file.

# An empty primary with NO generation to fall back to: lite must start fresh, with a usable window,
# and the log must say which of the two empties it was (no file vs. a file with nothing in it).
Seeded -Name 'empty-file' -Tsv '' -Assert {
    param($a, $i)
    ($a -ne '') -and (Log-Has $i 'state file is EMPTY') -and (Log-Has $i 'starting fresh')
}

# Cut mid-record, the shape a full disk used to leave behind. The complete specs above the cut must
# restore; the partial one is dropped rather than restored as a spec with empty fields.
Seeded -Name 'truncated' `
    -Tsv "V1`nW`tws-t`nS`t0`twhole-one`t`t$cwd`nS`t0`twhole-two`t`t$cwd`nS`t0`tcut-o" -Assert {
    param($a, $i)
    ($a -match 'whole-one') -and ($a -match 'whole-two') -and ($a -notmatch 'cut-o')
}

# A file written by a FUTURE build (downgrade, or a roaming profile shared between versions). The
# format grows by adding line types, so the right answer is to read what this build understands and
# say so — refusing would lose the sessions and then overwrite the newer file with a V1 one.
Seeded -Name 'future-v2' `
    -Tsv "V2`nW`tws-v`nS`t0`tfrom-the-future`t`t$cwd`nZ`tsomething-new`t42`nA`t0`n" -Assert {
    param($a, $i)
    ($a -match 'from-the-future') -and (Log-Has $i 'is format V2')
}

# S lines with no W lines at all: the workspace list stays whatever the build defaults to, and every
# spec lands in it rather than vanishing with its workspace.
Seeded -Name 'no-workspaces' `
    -Tsv "V1`nS`t0`tno-ws-session`t`t$cwd`nA`t0`n" -Assert {
    param($a, $i) $a -match 'no-ws-session'
}

# A spec pointing at a workspace that no longer exists (workspace deleted by an older build, or the
# file hand-edited), plus an out-of-range active workspace: both clamp to 0, so the session is
# visible instead of being filed under a workspace nobody can reach.
Seeded -Name 'stray-ws-index' `
    -Tsv "V1`nW`tonly-ws`nS`t7`tstray-session`t`t$cwd`nS`t0`thome-session`t`t$cwd`nF`t9`nA`t9`n" -Assert {
    param($a, $i) ($a -match 'stray-session') -and ($a -match 'home-session') -and ($a -match 'only-ws')
}

# Fields longer than the protocol's fixed-size wire storage. This is NOT a hand-edit-only case: the
# save persists the shell's live cwd read straight out of its PEB, which has no MAX_PATH limit, so a
# session sitting in a deep directory (long paths enabled, nested node_modules) writes a cwd lite
# then has to read back. strcpy_s does not truncate on overflow — it terminates the process — so
# before the fix this file killed lite at launch with no window and no log line. Both sessions must
# come back: the long cwd is dropped (the session starts in the default dir), the long ARG cannot be
# truncated without changing the command, so that spec is kept as a dead entry instead.
$longCwd = 'C:\' + ('deep-directory-name\' * 25)
$longArg = '-x' + ('y' * 3000)
Seeded -Name 'oversize-fields' `
    -Tsv "V1`nW`tws-o`nS`t0`tlong-cwd-session`t`t$longCwd`nS`t0`tlong-arg-session`tpowershell.exe`t$cwd`t$longArg`nA`t0`n" -Assert {
    param($a, $i)
    ($a -match 'long-cwd-session') -and ($a -match 'long-arg-session') -and
    (Log-Has $i 'does not fit the protocol field') -and (Log-Has $i 'arg 0 is')
}

# A session NAME carrying the state file's own delimiters. session.rename takes a JSON string, and
# jsonParseString decodes \t and \n, so the name arrives with real control characters in it — and the
# file is tab-separated and newline-delimited. Unescaped, the name below appends a whole second `S`
# line, which the next launch starts as a real session running whatever app it names. (It also puts
# the S count out of step with the D line, which switches adoption off for the entire file.) The
# state file must come back with exactly ONE session line and no trace of the injected one.
if (-not $Only -or $Only -eq 'name-injection') {
    $inst = 'rm-name-injection'
    Reset-Cell $inst
    $err = ''; $lines = 0; $after = ''; $renamed = ''; $count = 0; $forged = 0
    $p = $null; $p2 = $null
    try {
        $p = Start-Lite $inst
        $id = LastSessionId $inst
        $evil = 'pwn' + [char]10 + 'S' + [char]9 + '0' + [char]9 + 'injected' + [char]9 + 'notepad.exe' + [char]9
        $renamed = (& $ctl session rename $evil --target $id --pipe $inst 2>&1) -join ''
        Start-Sleep -Seconds 2
        Stop-Lite $p; $p = $null
        $lines = @(Get-Content (State $inst) | Where-Object { $_ -match "^S`t" }).Count
        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 3
        # Count sessions, don't grep the signature: the payload survives as ordinary TEXT inside the
        # one session's name (that is the fix working), so 'injected' appearing there is expected.
        $count = @(SessionsOf $inst).Count
        $forged = @(SessionsOf $inst | Where-Object { $_.name -eq 'injected' }).Count
        $after = Signature $inst
        Stop-Lite $p2; $p2 = $null
    } catch { $err = $_.Exception.Message }
    finally { Stop-Leftover $p; Stop-Leftover $p2 }
    if (-not $err -and $renamed -match 'renamed' -and $lines -eq 1 -and $count -eq 1 -and $forged -eq 0) {
        "  PASS  {0,-22} (a name cannot forge a session line)" -f 'name-injection'
    } else {
        $script:failed += 'name-injection'
        "  FAIL  name-injection"
        if ($err) { "        error:  $err" }
        "        rename said: $renamed"
        "        S lines in the saved file: $lines (expected 1)"
        "        sessions restored: $count (expected 1), forged 'injected' session(s): $forged"
        "        after:  [$after]"
        "        log:    $(Restore-Verdict $inst)"
    }
}

# --- harness self-check: a deliberately corrupted state file MUST fail a cell -------------------
# Without this, a harness that silently passes everything would be indistinguishable from a
# working restore — which is exactly the trap this whole exercise exists to avoid.
if (-not $Only -or $Only -eq 'harness-selfcheck') {
    $selfInst = 'rm-selfcheck'
    Reset-Cell $selfInst
    $sigBefore = ''; $sigAfter = ''; $err = ''
    $sp = $null; $sp2 = $null
    try {
        $sp = Start-Lite $selfInst
        & $ctl session new --pipe $selfInst 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        $sigBefore = Signature $selfInst
        Stop-Lite $sp; $sp = $null
        Write-State (State $selfInst) ''                              # wipe it: restore must not match
        Remove-Item "$(State $selfInst).bak" -ErrorAction SilentlyContinue   # ...and the generation it would fall back to
        $sp2 = Start-Lite $selfInst
        Start-Sleep -Seconds 2
        $sigAfter = Signature $selfInst
        Stop-Lite $sp2; $sp2 = $null
    } catch { $err = $_.Exception.Message }
    finally { Stop-Leftover $sp; Stop-Leftover $sp2 }
    if (-not $err -and $sigBefore -ne '' -and $sigAfter -ne $sigBefore) {
        "  PASS  {0,-22} (corrupted state correctly fails to restore)" -f 'harness-selfcheck'
    } else {
        $script:failed += 'harness-selfcheck'
        "  FAIL  harness-selfcheck    — a wiped state file still 'restored'; the harness proves nothing"
        if ($err) { "        error:  $err" }
        "        before: [$sigBefore]"
        "        after:  [$sigAfter]"
    }
}

""
if ($script:failed.Count) { "restore-matrix: FAILED cells: $($script:failed -join ', ')"; exit 1 }
"restore-matrix: all cells passed"
exit 0
