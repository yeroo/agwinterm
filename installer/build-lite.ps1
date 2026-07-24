# Package agwinterm-lite: build the C++/GDI client (+ its Rust core & pty-host) via
# lite/build.ps1, then zip lite\bin -> installer\Output\agwinterm-lite-<ver>-win-x64.zip.
# The zip is self-contained: lite exe + agwinterm-ptyhost.exe + agwinterm_core.dll + font.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root = Split-Path -Parent $here
$outDir = Join-Path $here "Output"

# version = the installer's AppVersion define (single source of truth)
$iss = Get-Content (Join-Path $here "agwinterm.iss") -Raw
if ($iss -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in agwinterm.iss" }
$ver = $Matches[1]

Write-Host "== build agwinterm-lite (v$ver) ==" -ForegroundColor Cyan
& (Join-Path $root "lite\build.ps1")
if ($LASTEXITCODE -ne 0) { throw "lite build failed" }

$bin = Join-Path $root "lite\bin"
# Ship only the RUNTIME files (cl.exe leaves .obj intermediates in bin\ — exclude them).
$ship = @("agwinterm-lite.exe", "agwinterm-ptyhost.exe", "agwinterm_core.dll",
          "MesloLGLDZNerdFont-Regular.ttf", "FONT-LICENSE.txt")
foreach ($f in $ship) {
  if (-not (Test-Path (Join-Path $bin $f))) { throw "lite bin missing $f" }
}

New-Item -ItemType Directory -Force $outDir | Out-Null
$zip = Join-Path $outDir "agwinterm-lite-$ver-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path ($ship | ForEach-Object { Join-Path $bin $_ }) -DestinationPath $zip -CompressionLevel Optimal
Write-Host ("== done: {0} ({1:N1} MB) ==" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
