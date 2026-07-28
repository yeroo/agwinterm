# Build BOTH agwinterm installers (separate setups for the main terminal and lite):
#   installer\Output\agwinterm-setup-<ver>.exe        (main: Agwinterm.Win32 + agwintermctl)
#   installer\Output\agwinterm-lite-setup-<ver>.exe   (lite: agwinterm-lite.exe, standalone)
# Each ships its own copy of the shared Rust pty-host + core dll.
# Prereqs: .NET SDK (net10.0-windows) + Inno Setup 6 (ISCC on PATH or the usual locations).
$ErrorActionPreference = "Stop"
$here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root   = Split-Path -Parent $here
$dotnet = if (Test-Path "C:\Program Files\dotnet\dotnet.exe") { "C:\Program Files\dotnet\dotnet.exe" } else { "dotnet" }
$stage      = Join-Path $here "stage"        # main terminal payload
$stageLite  = Join-Path $here "stage-lite"   # lite terminal payload

# resolve ISCC (Inno Setup compiler): PATH, machine-wide (choco/CI), and the per-user winget location.
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
  $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw "ISCC (Inno Setup compiler) not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup)." }

Write-Host "== clean stages ==" -ForegroundColor Cyan
foreach ($d in @($stage, $stageLite)) { if (Test-Path $d) { Remove-Item -Recurse -Force $d }; New-Item -ItemType Directory -Force $d | Out-Null }

# version = the main installer's AppVersion define (stamped into the assemblies so `ping` reports it).
# The lite installer's AppVersion must be kept in step with it.
$issText = Get-Content (Join-Path $here "agwinterm.iss") -Raw
if ($issText -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in agwinterm.iss" }
$ver = $Matches[1]
$liteText = Get-Content (Join-Path $here "agwinterm-lite.iss") -Raw
if ($liteText -match '#define\s+AppVersion\s+"([^"]+)"' -and $Matches[1] -ne $ver) {
  throw "version mismatch: agwinterm.iss=$ver but agwinterm-lite.iss=$($Matches[1]) - keep them in step"
}

# Self-contained folder publish (robust for Vortice native libs + the on-disk themes\/assets\).
$common = @("-c","Release","-r","win-x64","--self-contained","true","-p:PublishSingleFile=false","-p:Version=$ver","-o",$stage)

Write-Host "== publish Agwinterm.Win32 (app) ==" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\Agwinterm.Win32\Agwinterm.Win32.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }

Write-Host "== publish Agwinterm.Ctl (agwintermctl) ==" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\Agwinterm.Ctl\Agwinterm.Ctl.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "ctl publish failed" }

# Rust emulator core + pty-host: built once, copied into BOTH stages.
Write-Host "== build agwinterm-core + pty-host (Rust) ==" -ForegroundColor Cyan
& cargo build --release --manifest-path (Join-Path $root "native\Cargo.toml")
if ($LASTEXITCODE -ne 0) { throw "cargo build (native) failed" }
$coreDll = Join-Path $root "native\target\release\agwinterm_core.dll"
$ptyExe  = Join-Path $root "native\target\release\agwinterm-ptyhost.exe"
Copy-Item $coreDll $stage -Force
Copy-Item $ptyExe  $stage -Force
# The main app loads the Meslo Nerd Font by name from its exe dir (lite/assets is the canonical source).
Copy-Item (Join-Path $root "lite\assets\MesloLGLDZNerdFont-Regular.ttf") $stage -Force
Copy-Item (Join-Path $root "lite\assets\FONT-LICENSE.txt") $stage -Force

Write-Host "== build agwinterm-lite (C++/GDI) ==" -ForegroundColor Cyan
& (Join-Path $root "lite\build.ps1")
if ($LASTEXITCODE -ne 0) { throw "lite build failed" }
# Lite stage: the lite exe + its own copy of the shared core/pty-host + ALL bundled assets (fonts,
# toolbar icons, licenses) + the app icon for its shortcut.
Copy-Item (Join-Path $root "lite\bin\agwinterm-lite.exe") $stageLite -Force
Copy-Item $coreDll $stageLite -Force
Copy-Item $ptyExe  $stageLite -Force
Copy-Item (Join-Path $root "lite\assets\*") $stageLite -Force   # fonts + licenses + lite icon

# sanity: required payload present in each stage
foreach ($f in @("Agwinterm.Win32.exe","agwintermctl.exe","agwinterm_core.dll","agwinterm-ptyhost.exe","MesloLGLDZNerdFont-Regular.ttf","assets\agwinterm.ico")) {
  if (-not (Test-Path (Join-Path $stage $f))) { throw "main stage missing $f" }
}
if (-not (Get-ChildItem (Join-Path $stage "themes") -Filter *.conf -ErrorAction SilentlyContinue)) { throw "main stage missing themes\*.conf" }
foreach ($f in @("agwinterm-lite.exe","agwinterm_core.dll","agwinterm-ptyhost.exe","agwinterm-lite.ico","CozetteVector.ttf")) {
  if (-not (Test-Path (Join-Path $stageLite $f))) { throw "lite stage missing $f" }
}
Write-Host ("stage OK: main {0} files, lite {1} files" -f (Get-ChildItem $stage -Recurse -File).Count, (Get-ChildItem $stageLite -Recurse -File).Count) -ForegroundColor Green

Write-Host "== compile main installer (ISCC) ==" -ForegroundColor Cyan
& $iscc (Join-Path $here "agwinterm.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC (main) failed" }
Write-Host "== compile lite installer (ISCC) ==" -ForegroundColor Cyan
& $iscc (Join-Path $here "agwinterm-lite.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC (lite) failed" }

Get-ChildItem (Join-Path $here "Output") -Filter *.exe |
  Where-Object { $_.Name -like "*$ver*" } | Sort-Object LastWriteTime -Descending |
  ForEach-Object { Write-Host ("== done: {0} ({1:N1} MB) ==" -f $_.FullName, ($_.Length/1MB)) -ForegroundColor Green }
