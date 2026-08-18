# lite diagnostics — Task 4 checks: --diagnose must be a safe, useful one-shot report.
param([string]$Exe = "$PSScriptRoot\..\bin\agliteterm.exe")

$ErrorActionPreference = 'Stop'
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name$(if ($detail) { " — $detail" })" }
}

"== diagnose =="

# --- normal profile ---------------------------------------------------------------------------
$before = @(Get-Process -Name 'agliteterm' -ErrorAction SilentlyContinue).Count
$out = & $Exe --pipe diagchk --diagnose 2>&1 | Out-String
$code = $LASTEXITCODE
Start-Sleep -Seconds 2
$after = @(Get-Process -Name 'agliteterm' -ErrorAction SilentlyContinue).Count

Check 'exits 0' ($code -eq 0) "exit=$code"
Check 'leaves no process behind (no window)' ($after -le $before) "before=$before after=$after"
Check 'reports the version' ($out -match 'version: \d+\.\d+\.\d+|version: dev')
Check 'reports the instance' ($out -match 'instance: diagchk')
Check 'reports the state dir' ($out -match 'dir: .*agliteterm')
Check 'reports writability' ($out -match 'dir writable: yes')
Check 'reports the session file' ($out -match 'session file:')
Check 'reports the log path' ($out -match 'log\s+path: .*\.log')
Check 'reports the pack inventory' ($out -match 'agwin-bitmap-complete-16\.agbf: (present|missing)')

# --- error case: an unwritable profile must be REPORTED, not crash ----------------------------
# %LOCALAPPDATA% points at a file, so every path beneath it is unopenable.
$bogus = Join-Path ([IO.Path]::GetTempPath()) ("lite-diag-" + [Guid]::NewGuid().ToString('N'))
Set-Content -Path $bogus -Value 'not a directory'
$old = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = $bogus
    $out2 = & $Exe --pipe diagchk2 --diagnose 2>&1 | Out-String
    $code2 = $LASTEXITCODE
} finally {
    $env:LOCALAPPDATA = $old
    Remove-Item $bogus -ErrorAction SilentlyContinue
}
Check 'still exits 0 with an unwritable profile' ($code2 -eq 0) "exit=$code2"
Check 'names the profile as not writable' ($out2 -match 'dir writable: NO') `
    ($out2 -split "`n" | Where-Object { $_ -match 'writable' } | Select-Object -First 1)
Check 'flags that saves cannot work' ($out2 -match 'saves cannot work here')

if ($fail) { "diagnose: $fail FAILED"; exit 1 } else { "diagnose: all passed"; exit 0 }
