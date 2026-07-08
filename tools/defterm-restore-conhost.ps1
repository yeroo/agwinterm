# defterm-restore-conhost.ps1 — SAFETY NET: revert the Windows "default terminal application"
# back to the classic Windows Console Host (conhost). Run this if a defterm experiment leaves
# console apps unable to open. Works from Win+R (`powershell -File ...`) or any running shell;
# needs no admin (it's a per-user HKCU setting).

$ErrorActionPreference = 'Stop'
$key = 'HKCU:\Console\%%Startup'

# The zero GUID = "Let Windows decide", which resolves to the classic Windows Console Host (conhost)
# when no third-party default terminal is installed. This is the stock Windows Home baseline.
$zero = '{00000000-0000-0000-0000-000000000000}'

New-Item -Path $key -Force | Out-Null
Set-ItemProperty -Path $key -Name 'DelegationConsole'  -Value $zero -Type String
Set-ItemProperty -Path $key -Name 'DelegationTerminal' -Value $zero -Type String

Write-Host "Default terminal reset to 'Let Windows decide' (= Windows Console Host / conhost)." -ForegroundColor Green
Write-Host "DelegationConsole  = $zero"
Write-Host "DelegationTerminal = $zero"
Write-Host "`nOpen a NEW console (e.g. cmd from Win+R) to confirm it opens normally." -ForegroundColor Cyan
