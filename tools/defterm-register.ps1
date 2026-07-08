# defterm-register.ps1 — register agwinterm as the Windows "default terminal application" (T2-13).
# Per-user (HKCU), no admin. Reversible with tools/defterm-restore-conhost.ps1.
#
# Wires up the full defterm handoff chain:
#   in-box conhost  --DelegationConsole-->  OpenConsole.exe  --DelegationTerminal(ITerminalHandoff3)-->  agwinterm
# and registers OpenConsoleProxy.dll as the proxy/stub that marshals the pipe/process HANDLEs.
#
# Usage (agwinterm.exe must be running so its class factory is live before you launch a console app):
#   pwsh -File tools\defterm-register.ps1 [-BuildDir <folder with Agwinterm.Win32.exe + OpenConsole.exe + OpenConsoleProxy.dll>]

param(
    [string]$BuildDir = "$PSScriptRoot\..\src\Agwinterm.Win32\bin\Debug\net10.0-windows\win-x64"
)
$ErrorActionPreference = 'Stop'

$agw   = (Resolve-Path (Join-Path $BuildDir 'Agwinterm.Win32.exe')).Path
$oc    = (Resolve-Path (Join-Path $BuildDir 'OpenConsole.exe')).Path
$proxy = (Resolve-Path (Join-Path $BuildDir 'OpenConsoleProxy.dll')).Path

# --- CLSIDs (from microsoft/terminal Package.appxmanifest + our own) ---
$CLSID_agwTerminal = '{AE4D2C1F-6B3A-4E8D-9C7F-1A2B3C4D5E6F}'   # DefTerm.Clsid (our DelegationTerminal)
$CLSID_openConsole = '{2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}'   # OpenConsole ExeServer (DelegationConsole)
$CLSID_proxy       = '{3171DE52-6EFA-4AEF-8A9F-D02BD67E7A4F}'   # OpenConsoleHandoffProxy
$IIDs = @(                                                       # interfaces the proxy marshals
    '{E686C757-9A35-4A1C-B3CE-0BCC8B5C69F4}',
    '{6F23DA90-15C5-4203-9DB0-64E73F1B1B00}',                    # ITerminalHandoff3
    '{746E6BC0-AB05-4E38-AB14-71E86763141F}'
)

function SetKey($path, $default) { New-Item -Path $path -Force | Out-Null; if ($null -ne $default) { Set-ItemProperty -Path $path -Name '(default)' -Value $default } }

$Classes = 'HKCU:\Software\Classes'

# 1) agwinterm as the terminal COM server (LocalServer32).
SetKey "$Classes\CLSID\$CLSID_agwTerminal" 'agwinterm default-terminal handoff'
SetKey "$Classes\CLSID\$CLSID_agwTerminal\LocalServer32" "`"$agw`""

# 2) OpenConsole as the console COM server (LocalServer32).
SetKey "$Classes\CLSID\$CLSID_openConsole" 'OpenConsole'
SetKey "$Classes\CLSID\$CLSID_openConsole\LocalServer32" "`"$oc`""

# 3) OpenConsoleProxy.dll as the proxy/stub, and map each handoff interface to it.
SetKey "$Classes\CLSID\$CLSID_proxy" 'OpenConsoleHandoffProxy'
SetKey "$Classes\CLSID\$CLSID_proxy\InprocServer32" $proxy
Set-ItemProperty -Path "$Classes\CLSID\$CLSID_proxy\InprocServer32" -Name 'ThreadingModel' -Value 'Both'
foreach ($iid in $IIDs) {
    SetKey "$Classes\Interface\$iid" $null
    SetKey "$Classes\Interface\$iid\ProxyStubClsid32" $CLSID_proxy
}

# 4) Make agwinterm the default terminal.
$startup = 'HKCU:\Console\%%Startup'
New-Item -Path $startup -Force | Out-Null
Set-ItemProperty -Path $startup -Name 'DelegationConsole'  -Value $CLSID_openConsole -Type String
Set-ItemProperty -Path $startup -Name 'DelegationTerminal' -Value $CLSID_agwTerminal -Type String

Write-Host "agwinterm registered as the default terminal application." -ForegroundColor Green
Write-Host "  terminal (agwinterm): $agw"
Write-Host "  console  (OpenConsole): $oc"
Write-Host "  proxy    (OpenConsoleProxy): $proxy"
Write-Host "`nMake sure agwinterm is RUNNING, then launch a console app (e.g. 'cmd' from Win+R)." -ForegroundColor Cyan
Write-Host "To revert:  pwsh -File tools\defterm-restore-conhost.ps1" -ForegroundColor Yellow
