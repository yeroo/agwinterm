# Agwinterm-only Win32 control-host integration checks.
#
# Keep these out of tests/conformance: that runner is the portable control-API floor shared with
# agliteterm, while auxiliary covers and live pane geometry belong only to the full Win32 host.
param(
    [string]$Exe,
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
$fail = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name$(if ($detail) { " — $detail" })" }
}

function Resolve-Binary([string]$explicit, [string]$name, [string]$projectDir) {
    $roots = @()
    if ($explicit) { $roots += $explicit }
    $built = Join-Path (Join-Path $PSScriptRoot '..\..') "src\$projectDir\bin"
    if (Test-Path -LiteralPath $built) {
        $roots += (Get-ChildItem -LiteralPath $built -Recurse -Filter $name -ErrorAction SilentlyContinue |
                   Sort-Object LastWriteTime -Descending | Select-Object -ExpandProperty FullName)
    }
    $roots += "$env:LOCALAPPDATA\Programs\agwinterm\$name"
    foreach ($candidate in $roots) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}

"== Win32 control-host integration =="
$ctl = Resolve-Binary $env:AGWINTERMCTL 'agwintermctl.exe' 'Agwinterm.Ctl'
$Exe = Resolve-Binary $Exe 'Agwinterm.Win32.exe' 'Agwinterm.Win32'
if (-not $ctl) { "  SKIP  agwintermctl not found (set AGWINTERMCTL)"; exit ($Strict ? 1 : 0) }
if (-not $Exe) { "  SKIP  agwinterm not found (pass -Exe)"; exit ($Strict ? 1 : 0) }
"  using: $(Split-Path $Exe -Leaf) from $(Split-Path $Exe -Parent)"

# Do not inherit routing from the developer's current terminal. This process gets its own pipe and
# every mutation below is sent only to the process this script starts.
$env:AGWINTERM_SESSION_ID = $null
$env:AGWINTERM_PANE_ID = $null
$env:AGWINTERM_PIPE = $null
$env:AGWINTERM_APP_ID = $null
$testToken = [guid]::NewGuid().ToString('N')
$pipe = 'win32-control-' + $testToken.Substring(0, 12)
$appId = 'agwinterm-win32-control-' + $testToken
$localAppDataRoot = [IO.Path]::GetFullPath(
    [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)).TrimEnd('\')
$testAppDir = [IO.Path]::GetFullPath((Join-Path $localAppDataRoot $appId))
$testAppParent = [IO.Directory]::GetParent($testAppDir).FullName.TrimEnd('\')
if (-not $testAppParent.Equals($localAppDataRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFileName($testAppDir)).Equals($appId, [StringComparison]::Ordinal)) {
    throw "refusing unsafe test app-data path: $testAppDir"
}
if (Test-Path -LiteralPath $testAppDir) { throw "unique test app-data path already exists: $testAppDir" }
$captureFile = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-cover-id-" + [guid]::NewGuid().ToString('N') + '.txt')
$releaseFile = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-release-pane-" + [guid]::NewGuid().ToString('N') + '.signal')

function Invoke-Ctl($argv) {
    $argv = [string[]]@($argv)
    $out = (& $ctl @argv --pipe $pipe --json 2>&1) -join "`n"
    try { return $out | ConvertFrom-Json }
    catch { return [pscustomobject]@{ __raw = $out } }
}

function Get-SessionSnapshot([string]$id) {
    $tree = Invoke-Ctl @('tree')
    if (-not $tree.ok) { return $null }
    foreach ($workspace in @($tree.result.workspaces)) {
        foreach ($session in @($workspace.sessions)) {
            if ($session.id -eq $id) { return $session }
        }
    }
    return $null
}

# A real window is required: the assertions compare the renderer's live client-area geometry.
$process = $null
$sessionId = $null
try {
    $process = Start-Process $Exe -ArgumentList @('--app-id', $appId, '--pipe', $pipe, '--no-restore') -PassThru
    $up = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        if ((Invoke-Ctl @('ping')).ok) { $up = $true; break }
    }
    Check 'the isolated Win32 instance answers' $up
    if (-not $up) { throw 'instance never came up' }

    # Reproduce the collision from the review: the initial pane shares the session id; a hidden
    # scratch id begins with that id. Once the initial side of a split exits, only the exact session
    # and the scratch prefix remain. Exact session resolution must win.
    $releaseLiteral = $releaseFile.Replace("'", "''")
    $exitCommand = "powershell.exe -NoLogo -NoProfile -Command `"while (-not (Test-Path -LiteralPath '$releaseLiteral')) { Start-Sleep -Milliseconds 100 }`""
    $created = Invoke-Ctl @('session', 'new', '--name', 'win32-resolver', '--command', $exitCommand)
    $sessionId = [string]$created.result
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        $snapshot = Get-SessionSnapshot $sessionId
        if ($snapshot -and $snapshot.active) { $ready = $true; break }
        Start-Sleep -Milliseconds 200
    }
    Check 'the resolver fixture session becomes active' ($created.ok -and $ready)

    Invoke-Ctl @('session', 'scratch', 'on', '--target', $sessionId) | Out-Null
    Start-Sleep -Milliseconds 500
    Invoke-Ctl @('session', 'scratch', 'off', '--target', $sessionId) | Out-Null
    Invoke-Ctl @('session', 'split', 'on') | Out-Null

    $survivorId = $null
    for ($i = 0; $i -lt 30; $i++) {
        $snapshot = Get-SessionSnapshot $sessionId
        $paneIds = @($snapshot.paneIds)
        if ($paneIds.Count -eq 2) {
            $survivorId = [string]($paneIds | Where-Object { $_ -ne $sessionId } | Select-Object -First 1)
            break
        }
        Start-Sleep -Milliseconds 200
    }
    Check 'the fixture has a second pane' (-not [string]::IsNullOrEmpty($survivorId))

    if ($survivorId) {
        # While both meanings exist, an exact pane id has priority over the same exact session id.
        # The newly split pane is active, so resolving as a session here would modify the wrong side.
        Invoke-Ctl @('session', 'readonly', 'off', '--target', $survivorId) | Out-Null
        $setExactPane = Invoke-Ctl @('session', 'readonly', 'on', '--target', $sessionId)
        $activeSplitReadonly = Invoke-Ctl @('session', 'readonly', 'state', '--target', $survivorId)
        Check 'an exact pane id wins before the same exact session id' `
            ($setExactPane.ok -and $activeSplitReadonly.result -eq 'off') `
            "active split=$($activeSplitReadonly.result)"
        Invoke-Ctl @('session', 'readonly', 'off', '--target', $sessionId) | Out-Null
    }

    # Keep the primary pane alive until the collision assertion above is complete, then release it
    # deterministically so the survivor-promotion assertions cannot race a fixed process lifetime.
    [IO.File]::WriteAllText($releaseFile, 'release')

    $promoted = $false
    for ($i = 0; $i -lt 80; $i++) {
        $snapshot = Get-SessionSnapshot $sessionId
        if ($snapshot -and -not $snapshot.PSObject.Properties['paneCount']) { $promoted = $true; break }
        Start-Sleep -Milliseconds 250
    }
    Check 'the initial pane exits and the split survivor is promoted' $promoted

    if ($survivorId) {
        Invoke-Ctl @('session', 'readonly', 'off', '--target', $survivorId) | Out-Null
        Invoke-Ctl @('session', 'readonly', 'off', '--target', ($sessionId + ':scratch')) | Out-Null
        $setExact = Invoke-Ctl @('session', 'readonly', 'on', '--target', $sessionId)
        $survivorReadonly = Invoke-Ctl @('session', 'readonly', 'state', '--target', $survivorId)
        $scratchReadonly = Invoke-Ctl @('session', 'readonly', 'state', '--target', ($sessionId + ':scratch'))
        Check 'exact session id beats its hidden scratch prefix for pane operations' `
            ($setExact.ok -and $survivorReadonly.result -eq 'on' -and $scratchReadonly.result -eq 'off') `
            "survivor=$($survivorReadonly.result); scratch=$($scratchReadonly.result)"

        Invoke-Ctl @('session', 'readonly', 'off', '--target', $survivorId) | Out-Null
        $sessionMarker = 'exact-session-' + [guid]::NewGuid().ToString('N')
        Invoke-Ctl @('session', 'type', "Write-Output '$sessionMarker'`r", '--target', $sessionId) | Out-Null
        Start-Sleep -Seconds 2
        $survivorText = Invoke-Ctl @('session', 'text', '--target', $survivorId)
        Check 'exact session id resolves content verbs to the surviving regular pane' `
            ($survivorText.ok -and ([string]$survivorText.result).Contains($sessionMarker))

        $sessionMetrics = Invoke-Ctl @('session', 'metrics', '--target', $sessionId)
        $survivorMetrics = Invoke-Ctl @('session', 'metrics', '--target', $survivorId)
        $sameMetrics = $sessionMetrics.ok -and $survivorMetrics.ok -and
            $sessionMetrics.result.widthPx -gt 0 -and $sessionMetrics.result.heightPx -gt 0 -and
            $sessionMetrics.result.widthPx -eq $survivorMetrics.result.widthPx -and
            $sessionMetrics.result.heightPx -eq $survivorMetrics.result.heightPx
        Check 'exact session metrics describe its surviving regular pane' $sameMetrics

        $baselineCellWidth = [double]$survivorMetrics.result.cellWidth
        $baselineCellHeight = [double]$survivorMetrics.result.cellHeight
        $fontInc = Invoke-Ctl @('font', 'inc', '--target', $sessionId)
        $changedMetrics = $null
        for ($i = 0; $i -lt 40; $i++) {
            $candidate = Invoke-Ctl @('session', 'metrics', '--target', $survivorId)
            if ($candidate.ok -and
                ([double]$candidate.result.cellWidth -ne $baselineCellWidth -or
                 [double]$candidate.result.cellHeight -ne $baselineCellHeight)) {
                $changedMetrics = $candidate
                break
            }
            Start-Sleep -Milliseconds 100
        }
        Check 'font inc through the exact session id changes the surviving pane' `
            ($fontInc.ok -and $null -ne $changedMetrics)

        $changedViaSession = Invoke-Ctl @('session', 'metrics', '--target', $sessionId)
        $fontMetricsMatch = $null -ne $changedMetrics -and $changedViaSession.ok -and
            $changedViaSession.result.cellWidth -eq $changedMetrics.result.cellWidth -and
            $changedViaSession.result.cellHeight -eq $changedMetrics.result.cellHeight
        Check 'font-target metrics still agree through session and survivor ids' $fontMetricsMatch

        $fontReset = Invoke-Ctl @('font', 'reset', '--target', $sessionId)
        $fontResetApplied = $false
        for ($i = 0; $i -lt 40; $i++) {
            $candidate = Invoke-Ctl @('session', 'metrics', '--target', $survivorId)
            if ($candidate.ok -and
                [double]$candidate.result.cellWidth -eq $baselineCellWidth -and
                [double]$candidate.result.cellHeight -eq $baselineCellHeight) {
                $fontResetApplied = $true
                break
            }
            Start-Sleep -Milliseconds 100
        }
        Check 'the promoted pane font resets after the routing check' ($fontReset.ok -and $fontResetApplied)
        $sessionMetrics = Invoke-Ctl @('session', 'metrics', '--target', $sessionId)

        # Exercise an auxiliary cover by its actual inherited id. The background pane is read-only;
        # the quick cover is not, so readonly state proves PaneForTarget did not fall through to the
        # session below it. Metrics and selection/copy cover the other pane-resolution consumers.
        Invoke-Ctl @('session', 'readonly', 'on', '--target', $survivorId) | Out-Null
        Invoke-Ctl @('quick', 'on') | Out-Null
        Start-Sleep -Seconds 2
        $captureLiteral = $captureFile.Replace("'", "''")
        $captureCommand = "Set-Content -LiteralPath '$captureLiteral' -Value `$env:AGWINTERM_SESSION_ID -Encoding utf8`r"
        Invoke-Ctl @('session', 'type', $captureCommand, '--target', 'active') | Out-Null
        for ($i = 0; $i -lt 40 -and -not (Test-Path -LiteralPath $captureFile); $i++) {
            Start-Sleep -Milliseconds 250
        }
        $quickId = if (Test-Path -LiteralPath $captureFile) {
            (Get-Content -LiteralPath $captureFile -Raw).Trim()
        } else { '' }
        Check 'the quick terminal exposes its auxiliary pane id' ($quickId.StartsWith('quick:')) $quickId

        if ($quickId) {
            $quickReadonly = Invoke-Ctl @('session', 'readonly', 'state', '--target', $quickId)
            $backgroundReadonly = Invoke-Ctl @('session', 'readonly', 'state', '--target', $survivorId)
            Check 'pane operations target the quick cover rather than the background pane' `
                ($quickReadonly.ok -and $quickReadonly.result -eq 'off' -and $backgroundReadonly.result -eq 'on') `
                "quick=$($quickReadonly.result); background=$($backgroundReadonly.result)"

            $coverMetrics = Invoke-Ctl @('session', 'metrics', '--target', $quickId)
            $coverIsDistinct = $coverMetrics.ok -and $sessionMetrics.ok -and
                $coverMetrics.result.widthPx -gt 0 -and $coverMetrics.result.heightPx -gt 0 -and
                $coverMetrics.result.widthPx -lt $sessionMetrics.result.widthPx -and
                $coverMetrics.result.heightPx -lt $sessionMetrics.result.heightPx
            Check 'session.metrics measures the quick cover rectangle' $coverIsDistinct

            $quickMarker = 'quick-cover-' + [guid]::NewGuid().ToString('N')
            Invoke-Ctl @('session', 'type', "Write-Output '$quickMarker'`r", '--target', $quickId) | Out-Null
            Start-Sleep -Seconds 2
            Invoke-Ctl @('selection', 'all', '--target', $quickId) | Out-Null
            $quickCopy = Invoke-Ctl @('session', 'copy', '--target', $quickId)
            Check 'session.copy reads the targeted quick cover selection' `
                ($quickCopy.ok -and ([string]$quickCopy.result).Contains($quickMarker))
        }
    }
}
finally {
    # An earlier assertion failure must not strand the fixture process waiting on its release file.
    if (-not (Test-Path -LiteralPath $releaseFile)) {
        try { [IO.File]::WriteAllText($releaseFile, 'release') } catch { }
    }
    try { Invoke-Ctl @('quick', 'off') | Out-Null } catch { }
    if ($survivorId) {
        try { Invoke-Ctl @('session', 'readonly', 'off', '--target', $survivorId) | Out-Null } catch { }
    }
    if ($sessionId) {
        try { Invoke-Ctl @('session', 'close', $sessionId) | Out-Null } catch { }
    }
    if (Test-Path -LiteralPath $captureFile) { Remove-Item -LiteralPath $captureFile -Force }
    if (Test-Path -LiteralPath $releaseFile) { Remove-Item -LiteralPath $releaseFile -Force }
    if ($process) {
        try { $process.CloseMainWindow() | Out-Null } catch { }
        if (-not $process.WaitForExit(2000)) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }
    # $testAppDir was proved to be one direct child of LocalApplicationData before launch.
    if (Test-Path -LiteralPath $testAppDir) { Remove-Item -LiteralPath $testAppDir -Recurse -Force }
}

if ($fail) { "Win32 control-host integration: $fail FAILED"; exit 1 }
"Win32 control-host integration: all passed"
exit 0
