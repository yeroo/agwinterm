# Build the agwinterm installer: installer\Output\agwinterm-setup-<ver>.exe
# (Agwinterm.Win32 + agwintermctl + the Rust pty-host and core dll).
#
# The lite terminal is no longer built here — it became its own product, agliteterm, and ships from
# github.com/yeroo/agliteterm. Releases still CARRY a frozen agwinterm-lite-setup asset so installs
# that predate the handover can still find their way across; see .github/workflows/release.yml.
# Prereqs: .NET SDK (net10.0-windows) + Inno Setup 6 (ISCC on PATH or the usual locations).
$ErrorActionPreference = "Stop"
$here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root   = Split-Path -Parent $here
$dotnet = if (Test-Path "C:\Program Files\dotnet\dotnet.exe") { "C:\Program Files\dotnet\dotnet.exe" } else { "dotnet" }
$stage = Join-Path $here "stage"

# resolve ISCC (Inno Setup compiler): PATH, machine-wide (choco/CI), and the per-user winget location.
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
  $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw "ISCC (Inno Setup compiler) not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup)." }

Write-Host "== clean stage ==" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

# version = the installer's AppVersion define (stamped into the assemblies so `ping` reports it).
$issText = Get-Content (Join-Path $here "agwinterm.iss") -Raw
if ($issText -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in agwinterm.iss" }
$ver = $Matches[1]

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
# The app loads the Meslo Nerd Font by name from its exe dir. It used to live in lite/assets and
# moved to assets/fonts/ when lite left this repository — it is the MAIN app's font too, and a
# shared file has no business living inside a component that was about to be deleted.
Copy-Item (Join-Path $root "assets\fonts\MesloLGLDZNerdFont-Regular.ttf") $stage -Force
Copy-Item (Join-Path $root "assets\fonts\FONT-LICENSE.txt") $stage -Force

# sanity: required payload present
foreach ($f in @("Agwinterm.Win32.exe","agwintermctl.exe","agwinterm_core.dll","agwinterm-ptyhost.exe","MesloLGLDZNerdFont-Regular.ttf","assets\agwinterm.ico")) {
  if (-not (Test-Path (Join-Path $stage $f))) { throw "stage missing $f" }
}
if (-not (Get-ChildItem (Join-Path $stage "themes") -Filter *.conf -ErrorAction SilentlyContinue)) { throw "stage missing themes\*.conf" }
Write-Host ("stage OK: {0} files" -f (Get-ChildItem $stage -Recurse -File).Count) -ForegroundColor Green

Write-Host "== compile installer (ISCC) ==" -ForegroundColor Cyan
& $iscc (Join-Path $here "agwinterm.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem (Join-Path $here "Output") -Filter *.exe |
  Where-Object { $_.Name -like "*$ver*" } | Sort-Object LastWriteTime -Descending |
  ForEach-Object { Write-Host ("== done: {0} ({1:N1} MB) ==" -f $_.FullName, ($_.Length/1MB)) -ForegroundColor Green }
