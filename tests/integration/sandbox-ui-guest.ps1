param(
    [string]$InputDirectory = 'C:\AgwintermInput',
    [string]$ResultsDirectory = 'C:\AgwintermResults',
    [string]$WorkDirectory = 'C:\AgwintermTests'
)
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') {
    throw 'This entry point must run inside Windows Sandbox; use sandbox-ui.ps1 on the host.'
}
$results = $ResultsDirectory
$work = $WorkDirectory
$exitCode = 2
$failure = $null
try {
    $null = New-Item -ItemType Directory -Path $work
    foreach ($item in Get-ChildItem -LiteralPath $inputDirectory -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $work -Recurse
    }
    $run = Get-Content -LiteralPath (Join-Path $work 'run.json') -Raw | ConvertFrom-Json
    $env:CI = 'true'
    $env:PATH = (Join-Path $work 'powershell') + ';' + $env:PATH
    $runtime = Join-Path $work 'dotnet'
    if (Test-Path -LiteralPath $runtime) {
        $env:DOTNET_ROOT = $runtime
        $env:DOTNET_ROOT_X64 = $runtime
        $env:DOTNET_MULTILEVEL_LOOKUP = '0'
        $env:PATH = $runtime + ';' + $env:PATH
    }
    $runner = Join-Path $work 'tests/integration/hud-ui.ps1'
    $exe = Join-Path (Join-Path $work 'app') $run.exeName
    $process = Start-Process -FilePath (Join-Path $work 'powershell/pwsh.exe') -ArgumentList @(
        '-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass',
        '-File',"`"$runner`"",'-Exe',"`"$exe`"",'-Suite',$run.suite,'-Strict'
    ) -RedirectStandardOutput (Join-Path $results 'stdout.log') -RedirectStandardError (Join-Path $results 'stderr.log') -Wait -PassThru
    $exitCode = $process.ExitCode
} catch {
    $failure = $_.ToString()
} finally {
    try {
        $evidence = Join-Path $work '.revmux'
        if (Test-Path -LiteralPath $evidence) {
            Copy-Item -LiteralPath $evidence -Destination (Join-Path $results 'evidence') -Recurse
        }
    } catch {
        $exitCode = 2
        $failure = "Cannot copy test evidence: $_"
    }
    $temporaryResult = Join-Path $results 'result.json.tmp'
    @{exitCode=$exitCode; error=$failure} | ConvertTo-Json |
        Set-Content -LiteralPath $temporaryResult -Encoding utf8
    Move-Item -LiteralPath $temporaryResult -Destination (Join-Path $results 'result.json')
}
