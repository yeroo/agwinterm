# defterm-fetch-openconsole.ps1 — download OpenConsole.exe + OpenConsoleProxy.dll (MIT, from the
# Windows Terminal release) into the agwinterm build dir. These are required for the default-terminal
# handoff: OpenConsole is the console delegate, OpenConsoleProxy marshals the handoff HANDLEs.

param(
    [string]$BuildDir = "$PSScriptRoot\..\src\Agwinterm.Win32\bin\Debug\net10.0-windows\win-x64",
    [string]$Version  = '1.24.11321.0'
)
$ErrorActionPreference = 'Stop'

$url = "https://github.com/microsoft/terminal/releases/download/v$Version/Microsoft.WindowsTerminal_${Version}_x64.zip"
$zip = Join-Path $env:TEMP "wt-$Version.zip"
$ex  = Join-Path $env:TEMP "wt-$Version"

Write-Host "Downloading Windows Terminal $Version (for OpenConsole.exe + OpenConsoleProxy.dll)..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
Remove-Item $ex -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive $zip $ex -Force

$src = Get-ChildItem $ex -Recurse -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'OpenConsole.exe') } | Select-Object -First 1
if (-not $src) { throw "OpenConsole.exe not found in the downloaded package" }

Copy-Item (Join-Path $src.FullName 'OpenConsole.exe')      $BuildDir -Force
Copy-Item (Join-Path $src.FullName 'OpenConsoleProxy.dll') $BuildDir -Force
Write-Host "Placed OpenConsole.exe + OpenConsoleProxy.dll in:`n  $BuildDir" -ForegroundColor Green
