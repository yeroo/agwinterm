# lite diagnostics log — Task 1 checks.
#
# lite has no C++ test harness, so its checks drive the BUILT exe and assert on observable output.
# Rules that this suite obeys (learned the hard way):
#   - always a sandbox instance (--pipe <name>); never the default instance, which owns real state
#   - never inject global input (keybd_event/SendInput) — it lands wherever focus happens to be
#   - capture windows with PrintWindow, never CopyFromScreen, which grabs whatever overlaps
param([string]$Exe = "$PSScriptRoot\..\bin\agwinterm-lite.exe")

$ErrorActionPreference = 'Stop'
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name$(if ($detail) { " — $detail" })" }
}

$pipe = 'logtest'
$logDir = "$env:LOCALAPPDATA\agwinterm-lite"
$log = "$logDir\lite-$pipe.log"
Remove-Item $log, "$log.old" -ErrorAction SilentlyContinue

"== log-basics =="

# --- 1. the log is created and carries the startup lines -------------------------------------
$p = Start-Process $Exe -ArgumentList @('--pipe', $pipe) -PassThru
Start-Sleep -Seconds 7
Check 'log file created' (Test-Path $log) $log
$text = if (Test-Path $log) { Get-Content $log -Raw } else { '' }
Check 'records the startup banner' ($text -match 'agwinterm-lite .* starting')
Check 'records instance + exe + args' ($text -match "instance=$pipe" -and $text -match 'args=\[')
Check 'timestamped, levelled lines' ($text -match '\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\s+INFO\s+')

# --- 2. readable WHILE lite is running (share mode) -------------------------------------------
$readable = $true
try { [IO.File]::ReadAllText($log) | Out-Null } catch { $readable = $false }
Check 'readable while lite is running' $readable

$p.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 3
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

# --- 3. error case: an unwritable profile must not stop lite ----------------------------------
# Point %LOCALAPPDATA% at a file (not a directory) so every path under it is unopenable. lite must
# still start and serve the control pipe — logging degrades to a no-op, it is never fatal.
$bogusRoot = Join-Path ([IO.Path]::GetTempPath()) ("lite-nolog-" + [Guid]::NewGuid().ToString('N'))
Set-Content -Path $bogusRoot -Value 'not a directory'
$p2 = Start-Process $Exe -ArgumentList @('--pipe', "$pipe-nolog") -PassThru -Environment @{ LOCALAPPDATA = $bogusRoot }
Start-Sleep -Seconds 7
$p2.Refresh()
Check 'starts with an unwritable profile' (-not $p2.HasExited) "exit 0x$('{0:X8}' -f $(if ($p2.HasExited) { $p2.ExitCode } else { 0 }))"
if (-not $p2.HasExited) {
    $ctl = "$env:LOCALAPPDATA\Programs\agwinterm\agwintermctl.exe"
    $tree = (& $ctl tree --json --pipe "$pipe-nolog" 2>&1) -join ''
    Check 'still serves the control pipe' ($tree -match '"ok":true')
    $p2.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 3
    if (-not $p2.HasExited) { Stop-Process -Id $p2.Id -Force }
}
Remove-Item $bogusRoot -ErrorAction SilentlyContinue

if ($fail) { "log-basics: $fail FAILED"; exit 1 } else { "log-basics: all passed"; exit 0 }
