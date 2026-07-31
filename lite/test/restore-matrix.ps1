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
param(
    [string]$Exe = "$PSScriptRoot\..\bin\agwinterm-lite.exe",
    [string]$Only = ''          # run a single cell by name
)

$ErrorActionPreference = 'Stop'
$ctl = "$env:LOCALAPPDATA\Programs\agwinterm\agwintermctl.exe"
$dir = "$env:LOCALAPPDATA\agwinterm-lite"
$script:failed = @()

function State($inst) { "$dir\sessions-$inst.tsv" }
function Log($inst)   { "$dir\lite-$inst.log" }

function Reset-Cell($inst) {
    Remove-Item (State $inst), (Log $inst), "$(Log $inst).old", "$(State $inst).bak", "$(State $inst).tmp" `
        -ErrorAction SilentlyContinue
}

# The newest real session id in the tree (workspace rows carry numeric ids).
function LastSessionId($inst) {
    @(([regex]::Matches(((& $ctl tree --json --pipe $inst 2>&1) -join ''), '"id"\s*:\s*"([^"]+)"') |
       ForEach-Object { $_.Groups[1].Value }) | Where-Object { $_ -notmatch '^\d+$' })[-1]
}

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

# A cell's fingerprint: the session names in tree order. Names are what the user actually notices.
function Signature($inst) {
    $j = (& $ctl tree --json --pipe $inst 2>&1) -join ''
    $names = [regex]::Matches($j, '"name"\s*:\s*"([^"]*)"') | ForEach-Object { $_.Groups[1].Value }
    ($names -join '|')
}

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
    $before = ''; $after = ''; $err = ''
    try {
        $p = Start-Lite $inst
        & $Setup $inst
        Start-Sleep -Seconds 2
        $before = Signature $inst
        Stop-Lite $p -Kill:$Kill

        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 2
        $after = Signature $inst
        Stop-Lite $p2
    } catch { $err = $_.Exception.Message }

    $ok = $false
    if (-not $err) { $ok = & $Assert $before $after $inst }
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
} -Assert { param($b, $a) $a -eq $b -and ($b -split '\|').Count -ge 3 }

Cell -Name 'renamed' -Setup {
    param($i)
    $id = @(([regex]::Matches(((& $ctl tree --json --pipe $i 2>&1) -join ''), '"id"\s*:\s*"([^"]+)"') |
             ForEach-Object { $_.Groups[1].Value }) | Where-Object { $_ -notmatch '^\d+$' })[-1]
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
    $id = @(([regex]::Matches(((& $ctl tree --json --pipe $i 2>&1) -join ''), '"id"\s*:\s*"([^"]+)"') |
             ForEach-Object { $_.Groups[1].Value }) | Where-Object { $_ -notmatch '^\d+$' })[-1]
    & $ctl session flag on --target $id --pipe $i 2>&1 | Out-Null
    Start-Sleep -Seconds 2
} -Assert {
    param($b, $a, $i)
    if ($a -ne $b) { return $false }
    (Get-Content (State $i) -Raw) -match '(?m)^F\t'      # the flagged line survived the round trip
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
    if ($null -ne $Tsv) { Set-Content -Path (State $inst) -Value $Tsv -NoNewline -Encoding utf8 }
    if ($Bak) { Set-Content -Path "$(State $inst).bak" -Value $Bak -NoNewline -Encoding utf8 }
    if ($Tmp) { Set-Content -Path "$(State $inst).tmp" -Value $Tmp -NoNewline -Encoding utf8 }
    $after = ''; $err = ''
    try {
        $p = Start-Lite $inst
        Start-Sleep -Seconds 3
        $after = Signature $inst
        Stop-Lite $p
    } catch { $err = $_.Exception.Message }
    $ok = $false
    if (-not $err) { $ok = & $Assert $after $inst }
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
Seeded -Name 'app-spec' -Tsv "V1`nW`tws-a`nS`t0`tprofile-session`tpowershell.exe`t$cwd`n A`t0`n".Replace(' A', 'A') -Assert {
    param($a) $a -match 'profile-session'
}

# A spec whose app does not exist on this machine — the shape of "a profile that doesn't resolve
# on the laptop". Pins current behaviour so Task 5 can change it deliberately.
Seeded -Name 'bogus-app' -Tsv "V1`nW`tws-b`nS`t0`tdead-session`tno-such-program-xyz.exe`t$cwd`nA`t0`n" -Assert {
    param($a, $i)
    $log = Restore-Verdict $i
    # Either it comes back (host tolerates the bad app) or the log names the failure. Silence is the
    # only unacceptable outcome, because that is what made this unreportable in the field.
    ($a -match 'dead-session') -or ($log -match 'FAILED to start' -or $log -match 'no session could be started')
}

# Forced kill: no OnDestroy, so restore depends entirely on the refreshTree() save. If a laptop
# shutdown or crash explains the report, this is the cell that shows it.
Cell -Name 'killed' -Setup {
    param($i)
    & $ctl session new --pipe $i 2>&1 | Out-Null; Start-Sleep -Seconds 2
} -Kill -Assert { param($b, $a) $a -eq $b -and ($b -split '\|').Count -ge 2 }

# A session whose shell has already exited: its spec must still be saved and relaunched, otherwise
# a day's worth of sessions quietly evaporates the moment their shells end.
Cell -Name 'shell-exited' -Setup {
    param($i)
    $id = @(([regex]::Matches(((& $ctl tree --json --pipe $i 2>&1) -join ''), '"id"\s*:\s*"([^"]+)"') |
             ForEach-Object { $_.Groups[1].Value }) | Where-Object { $_ -notmatch '^\d+$' })[-1]
    & $ctl session new --pipe $i 2>&1 | Out-Null; Start-Sleep -Seconds 2
    & $ctl session type "exit`n" --target $id --pipe $i 2>&1 | Out-Null
    Start-Sleep -Seconds 3
} -Assert { param($b, $a) $a -eq $b }

# --- "Restart everything" must come back as the SAME instance ----------------------------------
# restartApp() used to relaunch the bare exe, so a named window restarted as the DEFAULT instance
# and read a different sessions file. Nothing crashed; the sessions were simply someone else's.

function Find-Lite($inst) {
    Get-CimInstance Win32_Process -Filter "Name='agwinterm-lite.exe'" |
        Where-Object { $_.CommandLine -match [regex]::Escape($inst) } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}

function Restart-Cell {
    param([string]$Name)
    if ($Only -and $Only -ne $Name) { return }
    $inst = "rm-$Name"
    Reset-Cell $inst
    $before = ''; $after = ''; $err = ''
    try {
        $p = Start-Lite $inst
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
        Start-Sleep -Seconds 2
        $after = Signature $inst
        Stop-Lite $p2
    } catch { $err = $_.Exception.Message }

    if (-not $err -and $after -eq $before -and ($before -split '\|').Count -ge 2) {
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
    try {
        $p = Start-Lite $inst
        $id = LastSessionId $inst
        & $ctl session rename keep-me --target $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        & $ctl session split on --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        & $ctl session close $id --pipe $inst 2>&1 | Out-Null
        Start-Sleep -Seconds 3
        $kept = (Get-Content (State $inst) -Raw) -match 'keep-me'
        $skipped = (Get-Content (Log $inst) -Raw) -match 'save SKIPPED'
        Stop-Lite $p
        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 2
        $after = Signature $inst
        Stop-Lite $p2
    } catch { $err = $_.Exception.Message }
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
        Stop-Lite $p
        $p2 = Start-Lite $inst
        Start-Sleep -Seconds 2
        $after = Signature $inst
        Stop-Lite $p2
    } catch { $err = $_.Exception.Message }
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

# Backward compatibility: a plain 0.17.x file — no D line, no .bak anywhere — must still restore.
Seeded -Name 'compat-0.17' `
    -Tsv "V1`nW`tws-c`nS`t0`told-one`t`t$cwd`nS`t0`told-two`t`t$cwd`nF`t1`nA`t0`n" -Assert {
    param($a, $i)
    ($a -match 'old-one') -and ($a -match 'old-two')
}

# --- harness self-check: a deliberately corrupted state file MUST fail a cell -------------------
# Without this, a harness that silently passes everything would be indistinguishable from a
# working restore — which is exactly the trap this whole exercise exists to avoid.
$selfInst = 'rm-selfcheck'
Reset-Cell $selfInst
$sp = Start-Lite $selfInst
& $ctl session new --pipe $selfInst 2>&1 | Out-Null
Start-Sleep -Seconds 2
$sigBefore = Signature $selfInst
Stop-Lite $sp
Set-Content -Path (State $selfInst) -Value '' -NoNewline      # wipe it: restore must not match
Remove-Item "$(State $selfInst).bak" -ErrorAction SilentlyContinue   # ...and the generation it would fall back to
$sp2 = Start-Lite $selfInst
Start-Sleep -Seconds 2
$sigAfter = Signature $selfInst
Stop-Lite $sp2
if ($sigAfter -ne $sigBefore) {
    "  PASS  {0,-22} (corrupted state correctly fails to restore)" -f 'harness-selfcheck'
} else {
    $script:failed += 'harness-selfcheck'
    "  FAIL  harness-selfcheck    — a wiped state file still 'restored'; the harness proves nothing"
}

""
if ($script:failed.Count) { "restore-matrix: FAILED cells: $($script:failed -join ', ')"; exit 1 }
"restore-matrix: all cells passed"
exit 0
