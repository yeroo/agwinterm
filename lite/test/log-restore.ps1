# lite diagnostics log — Task 2 checks: session save/restore must narrate itself.
#
# The point of these lines is the work-laptop report "restore doesn't work at all", which was
# unanswerable because a failed save and a failed rebuild look identical from outside. The log now
# distinguishes them, and this check pins the wording so it can't silently regress.
param([string]$Exe = "$PSScriptRoot\..\bin\agliteterm.exe")

$ErrorActionPreference = 'Stop'
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name$(if ($detail) { " — $detail" })" }
}

$pipe  = 'logrst'
$dir   = "$env:LOCALAPPDATA\agliteterm"
$log   = "$dir\agliteterm-$pipe.log"
$state = "$dir\sessions-$pipe.tsv"
$ctl   = "$env:LOCALAPPDATA\Programs\agwinterm\agwintermctl.exe"
# The .bak and .tmp go too: since the save keeps a previous generation, leaving one behind means
# run 1 is NOT a first run — it falls back to the last suite run's sessions and every count is off.
Remove-Item $log, "$log.old", $state, "$state.bak", "$state.tmp" -ErrorAction SilentlyContinue

"== log-restore =="

# --- run 1: create sessions, close, and expect a narrated save --------------------------------
$p = Start-Process $Exe -ArgumentList @('--pipe', $pipe) -PassThru
Start-Sleep -Seconds 7
& $ctl session new --pipe $pipe 2>&1 | Out-Null
Start-Sleep -Seconds 3
$p.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 4
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
Start-Sleep -Seconds 1

$text = Get-Content $log -Raw
Check 'first run reports no state file' ($text -match 'restore: no state file')
# Saves fire on every structural change, so the state that survives the run is the LAST one.
$saves = [regex]::Matches($text, 'save ok: (\d+) session\(s\), (\d+) bytes')
$save = if ($saves.Count) { $saves[$saves.Count - 1] } else { [regex]::Match('', 'x') }
Check 'save is recorded with a count and byte total' ($saves.Count -gt 0)
if ($save.Success) {
    Check 'saved at least 2 sessions' ([int]$save.Groups[1].Value -ge 2) "count=$($save.Groups[1].Value)"
    Check 'byte total is nonzero' ([int]$save.Groups[2].Value -gt 0) "bytes=$($save.Groups[2].Value)"
}

# --- run 2: relaunch and expect a narrated restore ---------------------------------------------
$p2 = Start-Process $Exe -ArgumentList @('--pipe', $pipe) -PassThru
Start-Sleep -Seconds 8
$text2 = Get-Content $log -Raw
# The log accumulates across runs, so read the LAST restore — the first one belongs to run 1.
function LastMatch([string]$text, [string]$pattern) {
    $m = [regex]::Matches($text, $pattern)
    if ($m.Count) { $m[$m.Count - 1] } else { [regex]::Match('', 'x') }
}
$spec = LastMatch $text2 'restore: (\d+) spec\(s\) from'
Check 'restore reports the spec count' $spec.Success
$built = LastMatch $text2 'restore: (\d+) of (\d+) session\(s\) built'
Check 'restore reports how many were built' $built.Success
if ($built.Success -and $save.Success) {
    Check 'built count matches what was saved' ($built.Groups[1].Value -eq $save.Groups[1].Value) `
        "built=$($built.Groups[1].Value) saved=$($save.Groups[1].Value)"
}
$p2.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 4
if (-not $p2.HasExited) { Stop-Process -Id $p2.Id -Force }
Start-Sleep -Seconds 1

# --- error case: a zero-byte state file must be NAMED, not silently ignored --------------------
# The .bak goes too. Run 2's save left one behind, and with it there restore takes the FALLBACK path
# instead of the empty-file one — the check would still pass while testing something else entirely.
Remove-Item $log, "$state.bak" -ErrorAction SilentlyContinue
Set-Content -Path $state -Value '' -NoNewline
$p3 = Start-Process $Exe -ArgumentList @('--pipe', $pipe) -PassThru
Start-Sleep -Seconds 8
$text3 = Get-Content $log -Raw
Check 'empty state file is reported as EMPTY' ($text3 -match 'restore: state file is EMPTY')
Check 'with no .bak either, it starts fresh' ($text3 -match 'starting fresh')
# ...and the window is actually usable: a log line proves nothing if lite died right after writing it.
$alive = ((& $ctl tree --json --pipe $pipe 2>&1) -join '') -match '"ok":true'
Check 'lite still answers after an empty state file' $alive
$p3.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 4
if (-not $p3.HasExited) { Stop-Process -Id $p3.Id -Force }

if ($fail) { "log-restore: $fail FAILED"; exit 1 } else { "log-restore: all passed"; exit 0 }
