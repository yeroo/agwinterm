# Build the agwinterm installer: self-contained publish of the app + CLI into installer\stage,
# then compile installer\agwinterm.iss with Inno Setup (ISCC) -> installer\Output\agwinterm-setup-<ver>.exe
# Prereqs: .NET SDK (net10.0-windows) + Inno Setup 6 (ISCC on PATH or in the usual locations).
$ErrorActionPreference = "Stop"
$here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root   = Split-Path -Parent $here
$dotnet = if (Test-Path "C:\Program Files\dotnet\dotnet.exe") { "C:\Program Files\dotnet\dotnet.exe" } else { "dotnet" }
$stage  = Join-Path $here "stage"

# resolve ISCC (Inno Setup compiler)
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
  $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw "ISCC (Inno Setup compiler) not found. Install Inno Setup 6." }

Write-Host "== clean stage ==" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

# version = the installer's AppVersion define (stamped into the assemblies so `ping` reports it)
$issText = Get-Content (Join-Path $here "agwinterm.iss") -Raw
if ($issText -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in agwinterm.iss" }
$ver = $Matches[1]

# Self-contained, NON-single-file (a folder): most robust for Vortice native libs + the on-disk
# themes\ / assets\ the app reads relative to the exe. The installer bundles the whole folder.
$common = @("-c","Release","-r","win-x64","--self-contained","true","-p:PublishSingleFile=false","-p:Version=$ver","-o",$stage)

Write-Host "== publish Agwinterm.Win32 (app) ==" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\Agwinterm.Win32\Agwinterm.Win32.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }

Write-Host "== publish Agwinterm.Ctl (agwintermctl) ==" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\Agwinterm.Ctl\Agwinterm.Ctl.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "ctl publish failed" }

# sanity: required payload present
foreach ($f in @("Agwinterm.Win32.exe","agwintermctl.exe","assets\agwinterm.ico")) {
  if (-not (Test-Path (Join-Path $stage $f))) { throw "stage missing $f" }
}
if (-not (Get-ChildItem (Join-Path $stage "themes") -Filter *.conf -ErrorAction SilentlyContinue)) { throw "stage missing themes\*.conf" }
Write-Host ("stage OK: {0} files" -f (Get-ChildItem $stage -Recurse -File).Count) -ForegroundColor Green

Write-Host "== compile installer (ISCC) ==" -ForegroundColor Cyan
& $iscc (Join-Path $here "agwinterm.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$out = Get-ChildItem (Join-Path $here "Output") -Filter *.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ("== done: {0} ({1:N1} MB) ==" -f $out.FullName, ($out.Length/1MB)) -ForegroundColor Green
