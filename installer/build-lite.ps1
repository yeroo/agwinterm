# Build ONLY the agwinterm-lite Inno setup (installer\Output\agwinterm-lite-setup-<ver>.exe) —
# handy when iterating on lite, since it skips the .NET publish of the main app entirely.
# installer\build.ps1 builds BOTH setups and is what CI/releases run.
# The setup is self-contained: lite exe + agwinterm-ptyhost.exe + agwinterm_core.dll + fonts.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root = Split-Path -Parent $here
$stageLite = Join-Path $here "stage-lite"

# resolve ISCC (Inno Setup compiler): PATH, machine-wide (choco/CI), and the per-user winget location.
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
  $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw "ISCC (Inno Setup compiler) not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup)." }

# version lock-step: agwinterm-lite.iss must match agwinterm.iss (single source of truth).
$issText = Get-Content (Join-Path $here "agwinterm.iss") -Raw
if ($issText -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in agwinterm.iss" }
$ver = $Matches[1]
$liteText = Get-Content (Join-Path $here "agwinterm-lite.iss") -Raw
if ($liteText -match '#define\s+AppVersion\s+"([^"]+)"' -and $Matches[1] -ne $ver) {
  throw "version mismatch: agwinterm.iss=$ver but agwinterm-lite.iss=$($Matches[1]) - keep them in step"
}

Write-Host "== build agwinterm-lite (v$ver) ==" -ForegroundColor Cyan
& (Join-Path $root "lite\build.ps1")
if ($LASTEXITCODE -ne 0) { throw "lite build failed" }

# Stage: the lite exe + its copy of the shared core/pty-host (lite\build.ps1 drops all three in
# lite\bin) + ALL bundled assets (fonts + licenses) + the app icon for its shortcut.
Write-Host "== stage lite ==" -ForegroundColor Cyan
if (Test-Path $stageLite) { Remove-Item -Recurse -Force $stageLite }
New-Item -ItemType Directory -Force $stageLite | Out-Null
$bin = Join-Path $root "lite\bin"
foreach ($f in @("agwinterm-lite.exe", "agwinterm-ptyhost.exe", "agwinterm_core.dll")) {
  if (-not (Test-Path (Join-Path $bin $f))) { throw "lite bin missing $f" }
  Copy-Item (Join-Path $bin $f) $stageLite -Force
}
Copy-Item (Join-Path $root "lite\assets\*") $stageLite -Force
Copy-Item (Join-Path $root "src\Agwinterm.Win32\assets\agwinterm.ico") $stageLite -Force
foreach ($f in @("agwinterm.ico", "CozetteVector.ttf", "MesloLGLDZNerdFont-Regular.ttf")) {
  if (-not (Test-Path (Join-Path $stageLite $f))) { throw "lite stage missing $f" }
}

Write-Host "== compile lite installer (ISCC) ==" -ForegroundColor Cyan
& $iscc (Join-Path $here "agwinterm-lite.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC (lite) failed" }
$out = Join-Path $here "Output\agwinterm-lite-setup-$ver.exe"
Write-Host ("== done: {0} ({1:N1} MB) ==" -f $out, ((Get-Item $out).Length / 1MB)) -ForegroundColor Green
