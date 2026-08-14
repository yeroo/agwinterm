# Pack and push the Chocolatey package for a released version.
#
# Shared by two callers so the packaging steps can never drift apart:
#   - release.yml's `chocolatey` job, on every tag
#   - choco-push.yml (workflow_dispatch), to REPUSH a version whose package changed but whose
#     version must not — which is exactly what Chocolatey moderation asks for ("repush your updated
#     package with the exact same version").
#
# The committed nuspec/install script stay pinned at whatever was last released; this rewrites the
# version, download URL and checksum in place from the actual release asset.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,      # e.g. 0.17.3 (no leading v)
    [Parameter(Mandatory)][string]$ApiKey,
    [string]$Repository = 'yeroo/agwinterm',
    [string]$OutputDir = $env:RUNNER_TEMP
)

$ErrorActionPreference = 'Stop'
if (-not $OutputDir) { $OutputDir = [IO.Path]::GetTempPath() }

$here  = Split-Path -Parent $MyInvocation.MyCommand.Definition
$asset = "agwinterm-portable-$Version-win-x64.exe"
$url   = "https://github.com/$Repository/releases/download/v$Version/$asset"

# tools\ must contain ONLY the install/uninstall scripts and the shim marker. VERIFICATION.txt and
# LICENSE.txt belong to packages that EMBED a binary; this one downloads it in the install script,
# and shipping them anyway is what got 0.17.3 held for corrective action. Checked before the
# download so a packaging mistake fails in a second rather than after fetching the release asset.
foreach ($stray in 'tools/VERIFICATION.txt', 'tools/LICENSE.txt') {
    if (Test-Path (Join-Path $here $stray)) {
        throw "$stray must not be in a download-based package (Chocolatey moderation rejects it)"
    }
}

Write-Host "== downloading $asset for its checksum =="
$tmp = Join-Path $OutputDir $asset
Invoke-WebRequest $url -OutFile $tmp
$sha = (Get-FileHash $tmp -Algorithm SHA256).Hash.ToLower()
Write-Host "sha256: $sha"

Push-Location $here
try {
    (Get-Content agwinterm.nuspec -Raw) -replace '(?<=<version>)[^<]+(?=</version>)', $Version |
        Set-Content agwinterm.nuspec
    $ps = 'tools/chocolateyinstall.ps1'
    $c = Get-Content $ps -Raw
    $c = $c -replace 'download/v[^/]+/agwinterm-portable-[^'']+\.exe', "download/v$Version/$asset"
    $c = $c -replace "checksum64\s+= '[0-9a-fA-F]+'", "checksum64     = '$sha'"
    Set-Content $ps $c

    Write-Host "== pack =="
    choco pack --outputdirectory $OutputDir
    if ($LASTEXITCODE -ne 0) { throw 'choco pack failed' }

    Write-Host "== push $Version =="
    choco push (Join-Path $OutputDir "agwinterm.$Version.nupkg") --source https://push.chocolatey.org/ --api-key $ApiKey
    if ($LASTEXITCODE -ne 0) { throw 'choco push failed' }
} finally { Pop-Location }

Write-Host "== pushed agwinterm $Version =="
