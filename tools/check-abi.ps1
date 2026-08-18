# The agwinterm-core C ABI is declared in THREE places, one per consumer, and nothing has ever
# checked that they agree:
#
#   native/agwinterm-core/src/lib.rs      pub const ABI_VERSION      (the source of truth)
#   src/Agwinterm.Core/RustEmulatorCore.cs      public const uint RequiredAbi
#   lite/src/main.cpp                    static constexpr uint32_t kRequiredAbi
#
# Today a drift is caught at RUNTIME by each consumer's handshake, which is a loud refusal rather
# than corruption — but only once someone runs the mismatched pair. That is tolerable while all
# three live in one tree and change in one commit. It stops being tolerable when agliteterm ships
# from its own repository against a PINNED core build (see
# docs/plans/2026-08-17-agliteterm-product-split.md), because then the pair can be wrong for a whole
# release cycle before anyone loads it.
#
# So: fail the build instead. Also emits the manifest that a separate repo consumes to verify it
# has the right core before shipping against it.
[CmdletBinding()]
param([string]$ManifestOut)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path -Parent $MyInvocation.MyCommand.Definition) -Parent

function Read-Abi([string]$Path, [string]$Pattern, [string]$Label) {
    $full = Join-Path $root $Path
    if (-not (Test-Path $full)) { throw "$Label`: $Path not found" }
    $m = [regex]::Match((Get-Content $full -Raw), $Pattern)
    if (-not $m.Success) { throw "$Label`: no ABI declaration matching /$Pattern/ in $Path" }
    [pscustomobject]@{ Label = $Label; Path = $Path; Abi = [int]$m.Groups[1].Value }
}

$decls = @(
    Read-Abi 'native/agwinterm-core/src/lib.rs'      'pub const ABI_VERSION:\s*u32\s*=\s*(\d+)'        'rust core (source of truth)'
    Read-Abi 'src/Agwinterm.Core/RustEmulatorCore.cs' 'public const uint RequiredAbi\s*=\s*(\d+)'       'C# RustEmulatorCore'
    Read-Abi 'lite/src/main.cpp'                      'constexpr uint32_t kRequiredAbi\s*=\s*(\d+)'     'lite (C++)'
)

$decls | ForEach-Object { "  {0,-28} v{1,-3} {2}" -f $_.Label, $_.Abi, $_.Path }

$distinct = @($decls.Abi | Sort-Object -Unique)
if ($distinct.Count -ne 1) {
    throw ("agwinterm-core ABI DRIFT: consumers disagree ({0}). " -f ($distinct -join ' vs ')) +
          "Bump every declaration in the same commit — a mismatched pair is a hard refusal at load, " +
          "and across repositories it can ship."
}

$abi = $distinct[0]
"ABI v$abi — all $($decls.Count) declarations agree"

if ($ManifestOut) {
    # Ships beside agwinterm_core.dll / agwinterm-ptyhost.exe so a consumer in another repository
    # can check the pairing BEFORE loading the dll, instead of finding out via fatal().
    $manifest = [ordered]@{
        abiVersion = $abi
        artifacts  = @('agwinterm_core.dll', 'agwinterm-ptyhost.exe')
        note       = 'Consumers must require exactly this abiVersion; the C ABI carries no compatibility guarantee across versions.'
    }
    $dir = Split-Path -Parent $ManifestOut
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $manifest | ConvertTo-Json | Set-Content $ManifestOut -Encoding utf8
    "manifest -> $ManifestOut"
}
