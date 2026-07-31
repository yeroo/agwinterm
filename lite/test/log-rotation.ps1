# lite diagnostics log — rotation and per-instance isolation.
#
# Rotation matters because the log is always on: it must not grow without bound on a machine that
# runs lite for weeks. Per-instance files matter because multi-window lite is one process per window
# — a shared file would interleave several writers' lines and be unreadable.
param([string]$Exe = "$PSScriptRoot\..\bin\agwinterm-lite.exe")

$ErrorActionPreference = 'Stop'
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name$(if ($detail) { " — $detail" })" }
}

$dir  = "$env:LOCALAPPDATA\agwinterm-lite"
$ctl  = "$env:LOCALAPPDATA\Programs\agwinterm\agwintermctl.exe"

"== log-rotation =="

# --- rotation: pre-fill past the 1 MB threshold, then make lite write ------------------------
$pipe = 'logrot'
$log = "$dir\lite-$pipe.log"
Remove-Item $log, "$log.old", "$dir\sessions-$pipe.tsv" -ErrorAction SilentlyContinue
Set-Content -Path $log -Value ('x' * 1048600) -NoNewline     # just over 1 MiB
$preSize = (Get-Item $log).Length
Check 'seeded an oversized log' ($preSize -gt 1048576) "size=$preSize"

$p = Start-Process $Exe -ArgumentList @('--pipe', $pipe) -PassThru
Start-Sleep -Seconds 7
Check 'rotated to .old' (Test-Path "$log.old") "$log.old"
if (Test-Path "$log.old") { Check 'the .old copy holds the previous content' ((Get-Item "$log.old").Length -gt 1048576) }
$new = Get-Item $log
Check 'a fresh log was started' ($new.Length -lt 10240) "size=$($new.Length)"
Check 'the fresh log has the startup banner' ((Get-Content $log -Raw) -match 'starting')
$p.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 3
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

# --- per-instance isolation: two instances must not share a file -----------------------------
$a = 'logiso1'; $b = 'logiso2'
Remove-Item "$dir\lite-$a.log", "$dir\lite-$b.log", "$dir\sessions-$a.tsv", "$dir\sessions-$b.tsv" -ErrorAction SilentlyContinue
$pa = Start-Process $Exe -ArgumentList @('--pipe', $a) -PassThru
$pb = Start-Process $Exe -ArgumentList @('--pipe', $b) -PassThru
Start-Sleep -Seconds 9
Check 'instance A has its own log' (Test-Path "$dir\lite-$a.log")
Check 'instance B has its own log' (Test-Path "$dir\lite-$b.log")
if ((Test-Path "$dir\lite-$a.log") -and (Test-Path "$dir\lite-$b.log")) {
    $ta = Get-Content "$dir\lite-$a.log" -Raw
    $tb = Get-Content "$dir\lite-$b.log" -Raw
    Check "A's log mentions only A" (($ta -match "instance=$a") -and ($ta -notmatch "instance=$b"))
    Check "B's log mentions only B" (($tb -match "instance=$b") -and ($tb -notmatch "instance=$a"))
}
foreach ($proc in @($pa, $pb)) {
    $proc.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 2
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}

if ($fail) { "log-rotation: $fail FAILED"; exit 1 } else { "log-rotation: all passed"; exit 0 }
