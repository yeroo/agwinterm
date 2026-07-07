# Build the PORTABLE agwinterm: one self-contained single-file exe, no installer needed.
# Output: installer\Output\agwinterm-portable-<ver>-win-x64.exe
# Themes + the window icon are embedded in the assembly (see Agwinterm.Win32.csproj), so the
# bare exe runs from anywhere. Settings still live under %LOCALAPPDATA%\agwinterm.
$ErrorActionPreference = "Stop"
$here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root   = Split-Path -Parent $here
$dotnet = if (Test-Path "C:\Program Files\dotnet\dotnet.exe") { "C:\Program Files\dotnet\dotnet.exe" } else { "dotnet" }
$stage  = Join-Path $here "stage-portable"
$outDir = Join-Path $here "Output"

# version = the installer's AppVersion define (single source of truth)
$iss = Get-Content (Join-Path $here "agwinterm.iss") -Raw
if ($iss -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in agwinterm.iss" }
$ver = $Matches[1]

Write-Host "== clean stage ==" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage, $outDir | Out-Null

Write-Host "== publish single-file portable exe (v$ver) ==" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\Agwinterm.Win32\Agwinterm.Win32.csproj") `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:SatelliteResourceLanguages=en `
  -p:DebugType=none `
  -p:Version=$ver `
  -o $stage
if ($LASTEXITCODE -ne 0) { throw "portable publish failed" }

$exe = Join-Path $stage "Agwinterm.Win32.exe"
if (-not (Test-Path $exe)) { throw "publish produced no Agwinterm.Win32.exe" }

# Ship ONLY the exe — themes/assets land next to it in the stage as loose copies, but the
# embedded fallbacks make the bare exe self-sufficient.
$dst = Join-Path $outDir "agwinterm-portable-$ver-win-x64.exe"
Copy-Item $exe $dst -Force
Write-Host ("== done: {0} ({1:N1} MB) ==" -f $dst, ((Get-Item $dst).Length/1MB)) -ForegroundColor Green
