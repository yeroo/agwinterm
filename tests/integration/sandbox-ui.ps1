#Requires -Version 7.0
param(
    [string]$Exe = "$PSScriptRoot/../../src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe",
    [string]$CtlExe = "$PSScriptRoot/../../src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe",
    [ValidateSet('Hud','Quick','Navigation','Picker','Accessibility','Command','Menu')]
    [string]$Suite = 'Navigation',
    [string]$PowerShellHome = $PSHOME,
    [string]$DotnetHome = "$env:ProgramFiles/dotnet",
    [string]$OutputDirectory = "$PSScriptRoot/../../artifacts/sandbox/$((Get-Date).ToString('yyyyMMddTHHmmss'))-$([guid]::NewGuid().ToString('N').Substring(0,8))",
    [ValidateRange(1,86400)][int]$TimeoutSeconds = 900,
    [ValidateRange(2048,65536)][int]$MemoryInMB = 4096,
    [switch]$DisableVGpu,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows Sandbox UI tests require a Windows host.' }

function Copy-Directory([string]$Source, [string]$Destination) {
    $null = New-Item -ItemType Directory -Path $Destination -Force
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse
    }
}

$Exe = (Resolve-Path -LiteralPath $Exe).Path
$CtlExe = (Resolve-Path -LiteralPath $CtlExe).Path
$PowerShellHome = (Resolve-Path -LiteralPath $PowerShellHome).Path
if (-not (Test-Path -LiteralPath (Join-Path $PowerShellHome 'pwsh.exe'))) {
    throw 'PowerShellHome must contain a portable Windows x64 PowerShell 7 installation.'
}
$needsRuntime = $false
foreach ($binary in @($Exe, $CtlExe)) {
    $runtimeConfig = [IO.Path]::ChangeExtension($binary, '.runtimeconfig.json')
    if (Test-Path -LiteralPath $runtimeConfig) {
        $options = (Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions
        if ($options.framework -or $options.frameworks) { $needsRuntime = $true }
    }
}
if ($needsRuntime) {
    $DotnetHome = (Resolve-Path -LiteralPath $DotnetHome).Path
    foreach ($part in @('dotnet.exe','host','shared')) {
        if (-not (Test-Path -LiteralPath (Join-Path $DotnetHome $part))) {
            throw "Framework-dependent binaries require a Windows .NET installation: missing $DotnetHome/$part"
        }
    }
}
$sandbox = if (-not $PrepareOnly) {
    $command = Get-Command WindowsSandbox.exe -ErrorAction SilentlyContinue
    if (-not $command) {
        throw 'Enable Windows Sandbox (Containers-DisposableClientVM) in Windows Features, restart if required, and finish installing the Sandbox client from Start/Store. Use -PrepareOnly to stage without the client.'
    }
    $command.Source
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw "Output directory already exists: $OutputDirectory" }
$sources = @((Split-Path $Exe), (Split-Path $CtlExe), $PowerShellHome, $PSScriptRoot)
if ($needsRuntime) { $sources += $DotnetHome }
foreach ($source in $sources) {
    $prefix = [IO.Path]::GetFullPath($source).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if ($OutputDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output directory must be outside staged source folders: $source"
    }
}

$mutex = [Threading.Mutex]::new($false, 'Local\agwinterm-sandbox-ui')
$locked = $false
try {
    if (-not $PrepareOnly) {
        try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { throw 'Another Sandbox UI run is active.' }
        if (Get-Process -Name WindowsSandbox,WindowsSandboxClient,WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) {
            throw 'Close the existing Windows Sandbox before starting a UI run.'
        }
    }
    $payload = Join-Path $OutputDirectory 'input'
    $results = Join-Path $OutputDirectory 'results'
    Write-Host "Preparing $Suite payload in $OutputDirectory"
    $null = New-Item -ItemType Directory -Path $payload, $results
    Copy-Directory (Split-Path $Exe) (Join-Path $payload 'app')
    $ctlDirectory = Join-Path $payload 'src/Agwinterm.Ctl/bin/Release/net10.0-windows'
    Copy-Directory (Split-Path $CtlExe) $ctlDirectory
    if ((Split-Path $CtlExe -Leaf) -ne 'agwintermctl.exe') {
        Copy-Item -LiteralPath $CtlExe -Destination (Join-Path $ctlDirectory 'agwintermctl.exe')
    }
    Copy-Directory $PSScriptRoot (Join-Path $payload 'tests/integration')
    Copy-Directory $PowerShellHome (Join-Path $payload 'powershell')
    if ($needsRuntime) {
        $runtimeDirectory = Join-Path $payload 'dotnet'
        $null = New-Item -ItemType Directory -Path $runtimeDirectory
        Copy-Item -LiteralPath (Join-Path $DotnetHome 'dotnet.exe') -Destination $runtimeDirectory
        foreach ($part in @('host','shared')) {
            Copy-Directory (Join-Path $DotnetHome $part) (Join-Path $runtimeDirectory $part)
        }
    }
    @{suite=$Suite; exeName=(Split-Path $Exe -Leaf)} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $payload 'run.json') -Encoding utf8
    $inputXml = [Security.SecurityElement]::Escape($payload)
    $resultsXml = [Security.SecurityElement]::Escape($results)
    $vgpu = if ($DisableVGpu) { 'Disable' } else { 'Enable' }
    $configuration = Join-Path $OutputDirectory 'ui-tests.wsb'
    @"
<Configuration>
  <MemoryInMB>$MemoryInMB</MemoryInMB>
  <vGPU>$vgpu</vGPU>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$inputXml</HostFolder><SandboxFolder>C:\AgwintermInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$resultsXml</HostFolder><SandboxFolder>C:\AgwintermResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\AgwintermInput\tests\integration\sandbox-ui-guest.ps1</Command></LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath $configuration -Encoding utf8
    Write-Host "Sandbox configuration: $configuration"
    Write-Host "Results: $results"
    if ($PrepareOnly) { return }

    Start-Process -FilePath $sandbox -ArgumentList "`"$configuration`""
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $resultFile = Join-Path $results 'result.json'
    while (-not (Test-Path -LiteralPath $resultFile)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Sandbox did not return a result within $TimeoutSeconds seconds. It may still be running; close it manually. Evidence: $results"
        }
        Start-Sleep -Milliseconds 500
    }
    $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
    if ($result.exitCode -notin @(0,1,2)) { throw "Invalid Sandbox result: $resultFile" }
    Write-Host "Sandbox $Suite completed with exit code $($result.exitCode). $($result.error)"
    Write-Host 'Close Windows Sandbox when finished inspecting the results.'
    exit $result.exitCode
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
