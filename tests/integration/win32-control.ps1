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

if (-not ('Agwinterm.Win32ControlTest.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace Agwinterm.Win32ControlTest
{
    public static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
'@
}

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name$(if ($detail) { " — $detail" })" }
}

function Resolve-Binary([string]$explicit, [string]$name, [string]$projectDir, [switch]$AllowInstalled) {
    $roots = @()
    if ($explicit) { $roots += $explicit }
    $built = Join-Path (Join-Path $PSScriptRoot '..\..') "src\$projectDir\bin"
    if (Test-Path -LiteralPath $built) {
        $roots += (Get-ChildItem -LiteralPath $built -Recurse -Filter $name -ErrorAction SilentlyContinue |
                   Sort-Object LastWriteTime -Descending | Select-Object -ExpandProperty FullName)
    }
    # The installed binary is acceptable for the ctl CLIENT only: it talks to the pipe named below
    # and nothing else. The APP must be a build from this tree - an older installed agwinterm that
    # does not honour --app-id would start against the user's real data directory with --no-restore.
    if ($AllowInstalled) { $roots += "$env:LOCALAPPDATA\Programs\agwinterm\$name" }
    foreach ($candidate in $roots) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}

"== Win32 control-host integration =="
$ctl = Resolve-Binary $env:AGWINTERMCTL 'agwintermctl.exe' 'Agwinterm.Ctl' -AllowInstalled
$Exe = Resolve-Binary $Exe 'Agwinterm.Win32.exe' 'Agwinterm.Win32'
if (-not $ctl) { "  SKIP  agwintermctl not found (set AGWINTERMCTL)"; exit ($Strict ? 1 : 0) }
if (-not $Exe) { "  SKIP  agwinterm build not found (build src\Agwinterm.Win32 or pass -Exe)"; exit ($Strict ? 1 : 0) }
"  using: $(Split-Path $Exe -Leaf) from $(Split-Path $Exe -Parent)"

# Do not inherit routing from the developer's current terminal. This process gets its own pipe and
# every mutation below is sent only to the process this script starts. Dot-sourced or run from a
# pane inside agwinterm, these are the caller's own routing variables, so they are put back at the
# end rather than left pointing at a pipe that no longer exists.
$savedEnv = @{}
foreach ($name in 'AGWINTERM_SESSION_ID', 'AGWINTERM_PANE_ID', 'AGWINTERM_PIPE', 'AGWINTERM_APP_ID') {
    $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name)
}
$env:AGWINTERM_SESSION_ID = $null
$env:AGWINTERM_PANE_ID = $null
$env:AGWINTERM_PIPE = $null
$testToken = [guid]::NewGuid().ToString('N')
$pipe = 'win32-control-' + $testToken.Substring(0, 12)
$appId = 'agwinterm-win32-control-' + $testToken
# Two throwaway identities, not one. Startup saves state synchronously (a workspace and a session
# are created and SaveState runs before the pipe answers), so an app that ignored --app-id would
# have rewritten its fallback identity's saved state before anything here could notice. A Release
# build's fallback is AGWINTERM_APP_ID; point that at a SECOND unique directory so the fallback is
# still nothing of the user's, then treat its appearance as the failure it is. (A Debug build's
# fallback is the fixed "agwinterm-dev"; a build from this tree cannot fall that far, which is why
# Resolve-Binary refuses an installed app above.)
$envAppId = $appId + '-env'
$env:AGWINTERM_APP_ID = $envAppId
$localAppDataRoot = [IO.Path]::GetFullPath(
    [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)).TrimEnd('\')
function Resolve-TestAppDir([string]$id) {
    $dir = [IO.Path]::GetFullPath((Join-Path $localAppDataRoot $id))
    $parent = [IO.Directory]::GetParent($dir).FullName.TrimEnd('\')
    if (-not $parent.Equals($localAppDataRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($dir)).Equals($id, [StringComparison]::Ordinal)) {
        throw "refusing unsafe test app-data path: $dir"
    }
    if (Test-Path -LiteralPath $dir) { throw "unique test app-data path already exists: $dir" }
    return $dir
}
$testAppDir = Resolve-TestAppDir $appId
$envAppDir = Resolve-TestAppDir $envAppId
$captureFile = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-cover-id-" + [guid]::NewGuid().ToString('N') + '.txt')
$releaseFile = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-release-pane-" + [guid]::NewGuid().ToString('N') + '.signal')
$mouseCellReadyFile = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-mouse-cell-ready-" + [guid]::NewGuid().ToString('N') + '.signal')
$mousePixelReadyFile = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-mouse-pixel-ready-" + [guid]::NewGuid().ToString('N') + '.signal')
$mouseCellFile = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-mouse-cell-" + [guid]::NewGuid().ToString('N') + '.txt')
$mousePixelFile = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-mouse-pixel-" + [guid]::NewGuid().ToString('N') + '.txt')

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

function Send-TestClick([IntPtr]$hwnd, [int]$x, [int]$y) {
    $packed = (($y -band 0xffff) -shl 16) -bor ($x -band 0xffff)
    [void][Agwinterm.Win32ControlTest.NativeMethods]::SendMessageW(
        $hwnd, 0x0201, [IntPtr]1, [IntPtr]$packed)
    [void][Agwinterm.Win32ControlTest.NativeMethods]::SendMessageW(
        $hwnd, 0x0202, [IntPtr]0, [IntPtr]$packed)
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

    # The unique pipe proves control isolation only. Require the files seeded under the explicit
    # app-id before the first mutation this script sends. This runs after startup's own saves, which
    # is why the env fallback above exists: a process that ignored --app-id wrote to $envAppDir, and
    # that directory existing is the proof.
    $appDataAdopted = (Test-Path -LiteralPath $testAppDir -PathType Container) -and
        (Test-Path -LiteralPath (Join-Path $testAppDir 'agwinterm.conf') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $testAppDir 'profiles.json') -PathType Leaf)
    Check 'the Win32 instance adopts its dedicated app-data namespace' $appDataAdopted $testAppDir
    Check 'the Win32 instance did not fall back to the environment app-id' `
        (-not (Test-Path -LiteralPath $envAppDir)) "--app-id was ignored; state went to $envAppDir"
    if (-not $appDataAdopted) { throw 'isolated instance did not seed its dedicated app-data directory' }

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

        Invoke-Ctl @('quick', 'off') | Out-Null
        Invoke-Ctl @('sidebar', 'hide') | Out-Null
        Start-Sleep -Milliseconds 500

        # Run a mouse-aware process in a 50%-sized floating cover. It captures one SGR cell report,
        # consumes its release, enables SGR-Pixels, then captures a pixel report for the same client
        # point. With background geometry both coordinates clamp to the cover's right edge; with the
        # cover origin and font metrics they land at its horizontal midpoint.
        $cellReadyLiteral = $mouseCellReadyFile.Replace("'", "''")
        $pixelReadyLiteral = $mousePixelReadyFile.Replace("'", "''")
        $cellMouseLiteral = $mouseCellFile.Replace("'", "''")
        $pixelMouseLiteral = $mousePixelFile.Replace("'", "''")
        $mouseScript = @'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace Agwinterm.ControlMouseFixture
{
    public static class ConsoleMode
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    }
}
"@

$stdinHandle = [Agwinterm.ControlMouseFixture.ConsoleMode]::GetStdHandle(-10)
$stdinMode = [uint32]0
if (-not [Agwinterm.ControlMouseFixture.ConsoleMode]::GetConsoleMode($stdinHandle, [ref]$stdinMode)) {
    throw "GetConsoleMode failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
}

# Ask ConPTY for raw VT input so terminal mouse reports reach the fixture as
# bytes. ReadKey() consumes console input records and silently ignores them.
$stdinMode = ($stdinMode -bor 0x0200) -band (-bnot 0x0006)
if (-not [Agwinterm.ControlMouseFixture.ConsoleMode]::SetConsoleMode($stdinHandle, $stdinMode)) {
    throw "SetConsoleMode failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
}

$script:mouseInput = [Console]::OpenStandardInput()
function Read-MouseReport([int]$terminator) {
    $bytes = [Collections.Generic.List[byte]]::new()
    do {
        $value = $script:mouseInput.ReadByte()
        if ($value -lt 0) {
            throw 'stdin closed before a mouse report arrived'
        }
        $bytes.Add([byte]$value)
    } until ($value -eq $terminator)
    return [Text.Encoding]::ASCII.GetString($bytes.ToArray())
}
[IO.File]::WriteAllText('__CELL_READY__', 'ready')
[IO.File]::WriteAllText('__CELL_REPORT__', (Read-MouseReport 77))
[void](Read-MouseReport 109)
[IO.File]::WriteAllText('__PIXEL_READY__', 'ready')
[IO.File]::WriteAllText('__PIXEL_REPORT__', (Read-MouseReport 77))
'@
        $mouseScript = $mouseScript.Replace('__CELL_READY__', $cellReadyLiteral).
            Replace('__CELL_REPORT__', $cellMouseLiteral).
            Replace('__PIXEL_READY__', $pixelReadyLiteral).
            Replace('__PIXEL_REPORT__', $pixelMouseLiteral)
        $mouseEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($mouseScript))
        $mouseCommand = "powershell.exe -NoLogo -NoProfile -EncodedCommand $mouseEncoded"
        $mouseOverlay = Invoke-Ctl @(
            'session', 'overlay', 'open', $mouseCommand,
            '--size-percent', '50', '--target', $survivorId)
        $mouseOverlayId = [string]$mouseOverlay.result
        for ($i = 0; $i -lt 40 -and -not (Test-Path -LiteralPath $mouseCellReadyFile); $i++) {
            Start-Sleep -Milliseconds 250
        }

        $process.Refresh()
        $hwnd = $process.MainWindowHandle
        # A cover inherits its session's font size, so with nothing else done the cover's metrics
        # equal the background pane's and a report encoded with the WRONG pane's metrics would still
        # match. Zoom the cover alone until its cell width differs from the background's; the
        # expectations below are then cover-specific, not merely cover-positioned. cellWidth is a
        # whole pixel, so one point of zoom can round back to the same width - step until it moves.
        $backgroundMetrics = Invoke-Ctl @('session', 'metrics', '--target', $survivorId)
        $overlayMetrics = $null
        if ($mouseOverlayId -and $backgroundMetrics.ok) {
            $previousHeight = [double]$backgroundMetrics.result.cellHeight
            for ($step = 0; $step -lt 6 -and $null -eq $overlayMetrics; $step++) {
                $coverZoom = Invoke-Ctl @('font', 'inc', '--target', $mouseOverlayId)
                if (-not $coverZoom.ok) { break }
                for ($i = 0; $i -lt 40; $i++) {
                    $candidate = Invoke-Ctl @('session', 'metrics', '--target', $mouseOverlayId)
                    if (-not $candidate.ok) { Start-Sleep -Milliseconds 100; continue }
                    if ([double]$candidate.result.cellWidth -ne [double]$backgroundMetrics.result.cellWidth) {
                        $overlayMetrics = $candidate
                        break
                    }
                    # The height moving is how a step is known to have landed before the next one.
                    if ([double]$candidate.result.cellHeight -ne $previousHeight) {
                        $previousHeight = [double]$candidate.result.cellHeight
                        break
                    }
                    Start-Sleep -Milliseconds 100
                }
            }
        }
        $backgroundUntouched = Invoke-Ctl @('session', 'metrics', '--target', $survivorId)
        $coverMetricsDistinct = $null -ne $overlayMetrics -and $backgroundUntouched.ok -and
            [double]$backgroundUntouched.result.cellWidth -eq [double]$backgroundMetrics.result.cellWidth
        Check 'the floating cover zooms independently of its background pane' $coverMetricsDistinct `
            "background cellWidth=$($backgroundMetrics.result.cellWidth), cover cellWidth=$($overlayMetrics.result.cellWidth)"
        $mouseFixtureReady = $mouseOverlay.ok -and $hwnd -ne [IntPtr]::Zero -and
            (Test-Path -LiteralPath $mouseCellReadyFile) -and $coverMetricsDistinct -and
            $overlayMetrics.result.cols -gt 8 -and $overlayMetrics.result.widthPx -gt 0
        Check 'the floating cover mouse-reporting fixture becomes ready' $mouseFixtureReady

        if ($mouseFixtureReady) {
            $esc = [char]27
            $enableCellMouse = Invoke-Ctl @(
                'session', 'write', "$esc[?1000h$esc[?1006h", '--target', $mouseOverlayId)
            $clientRect = [Agwinterm.Win32ControlTest.NativeMethods+RECT]::new()
            $hasClientRect = [Agwinterm.Win32ControlTest.NativeMethods]::GetClientRect(
                $hwnd, [ref]$clientRect)
            $clickX = [int](($clientRect.Right - $clientRect.Left) / 2)
            $clickY = [int](($clientRect.Bottom - $clientRect.Top) / 2)
            if ($enableCellMouse.ok -and $hasClientRect) { Send-TestClick $hwnd $clickX $clickY }
            for ($i = 0; $i -lt 40 -and -not (Test-Path -LiteralPath $mouseCellFile); $i++) {
                Start-Sleep -Milliseconds 100
            }
            for ($i = 0; $i -lt 40 -and -not (Test-Path -LiteralPath $mousePixelReadyFile); $i++) {
                Start-Sleep -Milliseconds 100
            }
            $enablePixelMouse = Invoke-Ctl @(
                'session', 'write', "$esc[?1016h", '--target', $mouseOverlayId)
            if ($enablePixelMouse.ok -and $hasClientRect -and
                (Test-Path -LiteralPath $mousePixelReadyFile)) {
                Send-TestClick $hwnd $clickX $clickY
            }
            for ($i = 0; $i -lt 40 -and -not (Test-Path -LiteralPath $mousePixelFile); $i++) {
                Start-Sleep -Milliseconds 100
            }

            $cellReport = if (Test-Path -LiteralPath $mouseCellFile) {
                [IO.File]::ReadAllText($mouseCellFile)
            } else { '' }
            $pixelReport = if (Test-Path -LiteralPath $mousePixelFile) {
                [IO.File]::ReadAllText($mousePixelFile)
            } else { '' }
            $cellMatch = [regex]::Match($cellReport, '^\x1b\[<0;(?<x>[0-9]+);[0-9]+M$')
            $pixelMatch = [regex]::Match($pixelReport, '^\x1b\[<0;(?<x>[0-9]+);[0-9]+M$')
            $cellColumn = if ($cellMatch.Success) { [int]$cellMatch.Groups['x'].Value } else { -1 }
            $pixelX = if ($pixelMatch.Success) { [int]$pixelMatch.Groups['x'].Value } else { -1 }
            $coverCols = [int]$overlayMetrics.result.cols
            $coverWidthPx = [int]$overlayMetrics.result.widthPx
            $coverCellWidth = [int]$overlayMetrics.result.cellWidth
            $expectedCellMidpoint = [int][Math]::Floor($coverCols / 2.0) + 1
            # The column the same click would encode with the BACKGROUND pane's cell width. The
            # assertion below is only evidence of cover-specific metrics if that lands outside the
            # +/-1 tolerance, so say so explicitly rather than let a narrow cover pass by accident.
            $backgroundCellWidth = [double]$backgroundMetrics.result.cellWidth
            $midpointViaBackground = [int][Math]::Floor(($coverWidthPx / 2.0) / $backgroundCellWidth) + 1
            $metricsDistinguishable = [Math]::Abs($midpointViaBackground - $expectedCellMidpoint) -gt 1
            Check 'cover and background metrics encode the midpoint to different columns' $metricsDistinguishable `
                "cover=$expectedCellMidpoint, via background=$midpointViaBackground"
            $cellIsCoverRelative = $cellMatch.Success -and
                [Math]::Abs($cellColumn - $expectedCellMidpoint) -le 1 -and
                $cellColumn -lt $coverCols - 1
            $pixelIsCoverRelative = $pixelMatch.Success -and
                [Math]::Abs($pixelX - ($coverWidthPx / 2.0)) -le $coverCellWidth + 2 -and
                $pixelX -lt $coverWidthPx - $coverCellWidth
            $cellCodes = (($cellReport.ToCharArray() | ForEach-Object { [int]$_ }) -join ',')
            $pixelCodes = (($pixelReport.ToCharArray() | ForEach-Object { [int]$_ }) -join ',')
            Check 'mouse reports use floating-cover cell and pixel geometry' `
                ($enableCellMouse.ok -and $enablePixelMouse.ok -and $hasClientRect -and
                 $cellIsCoverRelative -and $pixelIsCoverRelative) `
                "cell=$cellColumn/$coverCols [$cellCodes]; pixel=$pixelX/$coverWidthPx [$pixelCodes]"
        }
        Invoke-Ctl @('session', 'overlay', 'close', '--target', $survivorId) | Out-Null
    }
}
finally {
    # An earlier assertion failure must not strand the fixture process waiting on its release file.
    if (-not (Test-Path -LiteralPath $releaseFile)) {
        try { [IO.File]::WriteAllText($releaseFile, 'release') } catch { }
    }
    if ($sessionId) {
        try { Invoke-Ctl @('session', 'overlay', 'close', '--target', $sessionId) | Out-Null } catch { }
    }
    try { Invoke-Ctl @('quick', 'off') | Out-Null } catch { }
    if ($survivorId) {
        try { Invoke-Ctl @('session', 'readonly', 'off', '--target', $survivorId) | Out-Null } catch { }
    }
    if ($sessionId) {
        try { Invoke-Ctl @('session', 'close', $sessionId) | Out-Null } catch { }
    }
    # Every step here is guarded: $ErrorActionPreference is Stop, and a terminating error inside a
    # finally abandons the rest of the block - the environment restore at the end included.
    foreach ($file in @($captureFile, $releaseFile, $mouseCellReadyFile, $mousePixelReadyFile, $mouseCellFile, $mousePixelFile)) {
        try { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } } catch { }
    }
    if ($process) {
        try { $process.CloseMainWindow() | Out-Null } catch { }
        try {
            if (-not $process.WaitForExit(2000)) {
                Stop-Process -Id $process.Id -Force
                $process.WaitForExit()
            }
        } catch { }
    }
    # Both directories were proved to be direct children of LocalApplicationData before launch.
    foreach ($dir in @($testAppDir, $envAppDir)) {
        try { if ($dir -and (Test-Path -LiteralPath $dir)) { Remove-Item -LiteralPath $dir -Recurse -Force } } catch { }
    }
    foreach ($name in $savedEnv.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnv[$name])
    }
}

if ($fail) { "Win32 control-host integration: $fail FAILED"; exit 1 }
"Win32 control-host integration: all passed"
exit 0
