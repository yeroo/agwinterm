# Run every lite check. These drive the BUILT exe (lite has no C++ unit-test harness), so build
# first: lite\build.ps1
param([string]$Exe = "$PSScriptRoot\..\bin\agwinterm-lite.exe")

$ErrorActionPreference = 'Continue'
$failed = @()
foreach ($t in 'log-basics', 'log-restore', 'log-focus-font', 'log-rotation', 'diagnose', 'restore-matrix') {
    $script = Join-Path $PSScriptRoot "$t.ps1"
    if (-not (Test-Path $script)) { continue }
    & $script -Exe $Exe
    if ($LASTEXITCODE -ne 0) { $failed += $t }
    ""
}
if ($failed.Count) { "FAILED: $($failed -join ', ')"; exit 1 }
"all lite checks passed"
exit 0
