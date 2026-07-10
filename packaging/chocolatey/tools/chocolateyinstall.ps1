$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
    packageName    = 'agwinterm'
    fileFullPath   = Join-Path $toolsDir 'agwinterm.exe'
    url64bit       = 'https://github.com/yeroo/agwinterm/releases/download/v0.10.0/agwinterm-portable-0.10.0-win-x64.exe'
    checksum64     = '2c6f5791b54461eb48f9fa171b6c5b5bfd01fecee529d630ca12f0e6613fd1d2'
    checksumType64 = 'sha256'
}

# Portable single-file build: download the exe into the package's tools dir. Chocolatey then
# auto-generates a shim named `agwinterm` on PATH; the .gui marker keeps the shim from blocking
# the console. Settings live under %LOCALAPPDATA%\agwinterm regardless of where the exe sits.
Get-ChocolateyWebFile @packageArgs
