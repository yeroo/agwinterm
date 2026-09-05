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

if (('Agwinterm.Win32ControlTest.NativeMethods' -as [type]) -and
    -not ('Agwinterm.Win32ControlTest.NativeMethods' -as [type]).GetMethod('Chord')) {
    throw 'a NativeMethods type from an older run of this script is loaded in this shell (no Chord); run it from a fresh pwsh'
}
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

        [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] s);
        [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] s);

        /// <summary>Ctrl (+Shift) + key through the window's own key path — the same shape as
        /// AgwUi.Chord in tests/ui/lib.ps1. A posted WM_KEYDOWN cannot make GetKeyState see the
        /// modifier, so this attaches to the target's input queue and sets the shared key state for
        /// the duration. Scoped to this instance; nothing is injected globally.</summary>
        public static void Chord(IntPtr h, int vk, bool shift) {
            uint me = GetCurrentThreadId(), it = GetWindowThreadProcessId(h, IntPtr.Zero);
            AttachThreadInput(me, it, true);
            var st = new byte[256]; GetKeyboardState(st);
            st[0x11] = 0x80; st[0xA2] = 0x80;                      // VK_CONTROL, VK_LCONTROL
            if (shift) { st[0x10] = 0x80; st[0xA0] = 0x80; }       // VK_SHIFT, VK_LSHIFT
            SetKeyboardState(st);
            PostMessageW(h, 0x0100, (IntPtr)vk, (IntPtr)1);
            System.Threading.Thread.Sleep(400);
            st[0x11] = 0; st[0xA2] = 0; st[0x10] = 0; st[0xA0] = 0;
            SetKeyboardState(st);
            PostMessageW(h, 0x0101, (IntPtr)vk, (IntPtr)1);
            AttachThreadInput(me, it, false);
            System.Threading.Thread.Sleep(300);
        }
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
$scId = $null
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
    $splitReply = Invoke-Ctl @('session', 'split', 'on')

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
    # P4: `session split` answers the pane id it produced, read back off the session inside the UI
    # hop — so it must be exactly the new entry the tree lists, not a constant and not a stale guess.
    Check 'session split on replies with the new pane id the tree lists' `
        ($splitReply.ok -and $survivorId -and ([string]$splitReply.result -eq $survivorId)) `
        "reply=$($splitReply.result) tree=$survivorId"
    # `on` when already split: the same id again, and still two panes (the P2 no-op class, now honest).
    $splitAgain = Invoke-Ctl @('session', 'split', 'on')
    $paneCountAgain = @((Get-SessionSnapshot $sessionId).paneIds).Count
    Check 'session split on when already split replies with the existing split pane id' `
        ($splitAgain.ok -and ([string]$splitAgain.result -eq $survivorId) -and $paneCountAgain -eq 2) `
        "reply=$($splitAgain.result) panes=$paneCountAgain"

    # P4: the axis. `--axis horizontal` on the already-split session re-orients it live and still
    # answers the existing split pane's id. The proof is the GRID, read through each pane's metrics:
    # stacked panes have their rows roughly halved and their columns back at the full width — the
    # axis is provable from cols x rows without a pixel. The tree carries it as `axis`.
    if ($survivorId) {
        $vertPrimary = Invoke-Ctl @('session', 'metrics', '--target', $sessionId)
        $horizReply = Invoke-Ctl @('session', 'split', 'on', '--axis', 'horizontal')
        $horizPrimary = $null
        for ($i = 0; $i -lt 30; $i++) {
            $candidate = Invoke-Ctl @('session', 'metrics', '--target', $sessionId)
            if ($candidate.ok -and [int]$candidate.result.rows -lt [int]$vertPrimary.result.rows) { $horizPrimary = $candidate; break }
            Start-Sleep -Milliseconds 200
        }
        $horizSplit = Invoke-Ctl @('session', 'metrics', '--target', $survivorId)
        $axisNode = Get-SessionSnapshot $sessionId
        Check 'session split on --axis horizontal re-orients the live split and answers the same pane id' `
            ($horizReply.ok -and ([string]$horizReply.result -eq $survivorId) -and
             $axisNode.axis -eq 'horizontal' -and @($axisNode.paneIds).Count -eq 2) `
            "reply=$($horizReply.result) axis=$($axisNode.axis)"
        Check 'a horizontal split stacks the panes: rows halved and columns the full width, on both panes' `
            ($vertPrimary.ok -and $null -ne $horizPrimary -and $horizSplit.ok -and
             [int]$horizPrimary.result.rows -lt [int]$vertPrimary.result.rows -and
             [int]$horizPrimary.result.cols -gt [int]$vertPrimary.result.cols -and
             [int]$horizSplit.result.cols -eq [int]$horizPrimary.result.cols -and
             [int]$horizSplit.result.rows -lt [int]$vertPrimary.result.rows) `
            "vertical primary=$($vertPrimary.result.cols)x$($vertPrimary.result.rows) horizontal primary=$($horizPrimary.result.cols)x$($horizPrimary.result.rows) split=$($horizSplit.result.cols)x$($horizSplit.result.rows)"
        # The words of the other axis are refused naming this one; this axis's words work.
        $focusLeft = Invoke-Ctl @('session', 'focus', 'left')
        $focusBottom = Invoke-Ctl @('session', 'focus', 'bottom')
        Check 'session focus left is refused on a horizontal split (naming the axis) and bottom is accepted' `
            ((-not $focusLeft.ok) -and ([string]$focusLeft.error).Contains('horizontal') -and $focusBottom.ok) `
            "left=$($focusLeft.error) bottom=$($focusBottom.result)"
        $growLeft = Invoke-Ctl @('session', 'resize', '--grow-left', '3')
        Check 'session resize --grow-left is refused on a horizontal split and points at --grow-top' `
            ((-not $growLeft.ok) -and ([string]$growLeft.error).Contains('grow-top')) "reply=$($growLeft.error)"
        # Back to vertical for the rest of the script, which was written against columns.
        $backReply = Invoke-Ctl @('session', 'split', 'on', '--axis', 'vertical')
        $restored = $false
        for ($i = 0; $i -lt 30; $i++) {
            $candidate = Invoke-Ctl @('session', 'metrics', '--target', $sessionId)
            if ($candidate.ok -and [int]$candidate.result.rows -eq [int]$vertPrimary.result.rows -and
                [int]$candidate.result.cols -eq [int]$vertPrimary.result.cols) { $restored = $true; break }
            Start-Sleep -Milliseconds 200
        }
        Check 'session split on --axis vertical restores the side-by-side grid exactly' `
            ($backReply.ok -and $restored -and (Get-SessionSnapshot $sessionId).axis -eq 'vertical')

        # P4: `session swap` exchanges the two panes. The proof is per-pane geometry: with the divider at
        # 30/70 each pane's box is distinct, and after the swap each pane's metrics are the OTHER pane's
        # old metrics — the boxes stayed where they were, the shells changed places, and every id still
        # names the shell it named (the session id's pane is slot 1 now, so the tree lists it second).
        # The ratio sequence is what keeps the boxes: reversing the panes alone would have jumped the
        # divider to 70/30. Swapped back, then the divider returned to 50/50, so the checks below — written
        # against pane 0 = the session id — see the fixture exactly as they did.
        $swapResize = Invoke-Ctl @('session', 'resize', '--split-ratio', '0.3')
        $narrow = $null
        for ($i = 0; $i -lt 30; $i++) {
            $candidate = Invoke-Ctl @('session', 'metrics', '--target', $sessionId)
            if ($candidate.ok -and [int]$candidate.result.cols -lt [int]$vertPrimary.result.cols) { $narrow = $candidate; break }
            Start-Sleep -Milliseconds 200
        }
        $wide = Invoke-Ctl @('session', 'metrics', '--target', $survivorId)
        Check 'a 30/70 divider gives the two panes distinct boxes (the swap fixture)' `
            ($swapResize.ok -and $null -ne $narrow -and $wide.ok -and [int]$narrow.result.cols -lt [int]$wide.result.cols) `
            "primary=$($narrow.result.cols)x$($narrow.result.rows) split=$($wide.result.cols)x$($wide.result.rows)"
        $swapNode0 = Get-SessionSnapshot $sessionId
        $swapReply = Invoke-Ctl @('session', 'swap')
        $swappedPrimary = $null
        for ($i = 0; $i -lt 30; $i++) {
            $candidate = Invoke-Ctl @('session', 'metrics', '--target', $sessionId)
            if ($candidate.ok -and [int]$candidate.result.cols -eq [int]$wide.result.cols) { $swappedPrimary = $candidate; break }
            Start-Sleep -Milliseconds 200
        }
        $swappedSplit = Invoke-Ctl @('session', 'metrics', '--target', $survivorId)
        $swapNode = Get-SessionSnapshot $sessionId
        Check 'session swap replies with the split block after the swap: the same session, the pane ids reversed, the axis kept' `
            ($swapReply.ok -and $swapReply.result.session -eq $sessionId -and
             (@($swapReply.result.paneIds) -join ',') -eq "$survivorId,$sessionId" -and $swapReply.result.axis -eq 'vertical' -and
             $swapNode -and (@($swapNode.paneIds) -join ',') -eq "$survivorId,$sessionId" -and $swapNode.axis -eq 'vertical') `
            "reply=$($swapReply | ConvertTo-Json -Compress) node=$($swapNode | ConvertTo-Json -Compress)"
        Check 'after the swap each pane measures the other pane''s old box (the boxes stayed, the shells moved, the ids did not)' `
            ($null -ne $swappedPrimary -and $swappedSplit.ok -and
             [int]$swappedPrimary.result.cols -eq [int]$wide.result.cols -and [int]$swappedPrimary.result.rows -eq [int]$wide.result.rows -and
             [int]$swappedSplit.result.cols -eq [int]$narrow.result.cols -and [int]$swappedSplit.result.rows -eq [int]$narrow.result.rows) `
            "session-id pane=$($swappedPrimary.result.cols)x$($swappedPrimary.result.rows) (was $($narrow.result.cols)x$($narrow.result.rows)) split pane=$($swappedSplit.result.cols)x$($swappedSplit.result.rows) (was $($wide.result.cols)x$($wide.result.rows))"
        Check 'the ratio sequence is kept and the focus followed the pane' `
            ($swapNode -and ((@($swapNode.splitRatios) -join ',') -eq (@($swapNode0.splitRatios) -join ',')) -and
             [int]$swapNode.focusedPane -eq (1 - [int]$swapNode0.focusedPane)) `
            "ratios before=$(@($swapNode0.splitRatios) -join ',') after=$(@($swapNode.splitRatios) -join ',') focus before=$($swapNode0.focusedPane) after=$($swapNode.focusedPane)"
        $swapBack = Invoke-Ctl @('session', 'swap')
        $identity = $false
        for ($i = 0; $i -lt 30; $i++) {
            $candidate = Invoke-Ctl @('session', 'metrics', '--target', $sessionId)
            if ($candidate.ok -and [int]$candidate.result.cols -eq [int]$narrow.result.cols) { $identity = $true; break }
            Start-Sleep -Milliseconds 200
        }
        $swapNode2 = Get-SessionSnapshot $sessionId
        Check 'swap twice is the identity: the ids, the order, the ratios and the focus are back' `
            ($swapBack.ok -and $identity -and $swapNode2 -and ($swapNode2 | ConvertTo-Json -Compress) -eq ($swapNode0 | ConvertTo-Json -Compress)) `
            "before=$($swapNode0 | ConvertTo-Json -Compress) after=$($swapNode2 | ConvertTo-Json -Compress)"
        Invoke-Ctl @('session', 'resize', '--split-ratio', '0.5') | Out-Null
        for ($i = 0; $i -lt 30; $i++) {
            $candidate = Invoke-Ctl @('session', 'metrics', '--target', $sessionId)
            if ($candidate.ok -and [int]$candidate.result.cols -eq [int]$vertPrimary.result.cols) { break }
            Start-Sleep -Milliseconds 200
        }
    }

    # P4: `session split close` closes EITHER side and answers the survivor's id. Pane 0 carries the
    # session id, so closing it by that id is the case no verb could do before (`off` hard-codes pane 0
    # as the survivor); the old pane 1 must survive as the session's only pane, under its old id and
    # with its shell — proved by typing a marker into it before the close and reading it back after,
    # both by its own id and by the session id (the exact-session arm now reaches it as the focused
    # pane, the promotion case pinned further down). A second close on the now single-pane session is
    # refused naming `session close`. On its own --no-select fixture, so the active session — and every
    # check below that reads it — is untouched.
    $scMade = Invoke-Ctl @('session', 'new', '--name', 'p4-split-close', '--no-select')
    $scId = [string]$scMade.result
    for ($i = 0; $i -lt 30 -and -not (Get-SessionSnapshot $scId); $i++) { Start-Sleep -Milliseconds 200 }
    $scSplit = Invoke-Ctl @('session', 'split', 'on', '--target', $scId)
    $scPane1 = [string]$scSplit.result
    $scNode = $null
    for ($i = 0; $i -lt 30; $i++) {
        $scNode = Get-SessionSnapshot $scId
        if ($scNode -and @($scNode.paneIds).Count -eq 2) { break }
        Start-Sleep -Milliseconds 200
    }
    Check 'the split-close fixture is split, with the reply naming its pane 1' `
        ($scMade.ok -and $scSplit.ok -and $scNode -and @($scNode.paneIds).Count -eq 2 -and @($scNode.paneIds)[1] -eq $scPane1) `
        "made=$($scMade | ConvertTo-Json -Compress) split=$($scSplit | ConvertTo-Json -Compress)"
    $scMarker = 'split-close-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $scTyped = $false
    foreach ($attempt in 1, 2, 3) {   # typed input can be dropped while the shell starts (the ConPTY early-discard class): type again
        Invoke-Ctl @('session', 'type', "Write-Output '$scMarker'`r", '--target', $scPane1) | Out-Null
        for ($i = 0; $i -lt 16; $i++) {
            Start-Sleep -Milliseconds 250
            if (([string](Invoke-Ctl @('session', 'text', '--target', $scPane1)).result).Contains($scMarker)) { $scTyped = $true; break }
        }
        if ($scTyped) { break }
    }
    $scClose = Invoke-Ctl @('session', 'split', 'close', '--target', $scId)
    Start-Sleep -Milliseconds 400
    $scAfter = Get-SessionSnapshot $scId
    $scText = Invoke-Ctl @('session', 'text', '--target', $scPane1)
    $scViaSession = Invoke-Ctl @('session', 'text', '--target', $scId)
    Check 'session split close on pane 0 (by the session id it carries) answers the old pane 1 id as the survivor, and the node is single' `
        ($scClose.ok -and $scPane1 -and ([string]$scClose.result -eq $scPane1) -and $scAfter -and -not $scAfter.PSObject.Properties['paneCount']) `
        "close=$($scClose | ConvertTo-Json -Compress) node=$($scAfter | ConvertTo-Json -Compress)"
    Check 'the survivor keeps its id and its shell: session text by the old pane 1 id, and by the session id, still shows what was typed there' `
        ($scTyped -and $scText.ok -and ([string]$scText.result).Contains($scMarker) -and $scViaSession.ok -and ([string]$scViaSession.result).Contains($scMarker)) `
        "typed=$scTyped byPane=$($scText.ok) bySession=$($scViaSession.ok)"
    # The CLI-side refusals of the split family (revmux r1-r3 of P4), each proved twice: exit 2 with the
    # message, and the ORACLE — the split-close fixture still has two panes. Every one of these acted on
    # the caller's own pane with exit 0 before it was refused. Run BEFORE the close below, on the split.
    $scGuardMade = Invoke-Ctl @('session', 'new', '--name', 'p4-split-guards', '--no-select')
    $scGuardId = [string]$scGuardMade.result
    for ($i = 0; $i -lt 30 -and -not (Get-SessionSnapshot $scGuardId); $i++) { Start-Sleep -Milliseconds 200 }
    $scGuardSplit = Invoke-Ctl @('session', 'split', 'on', '--target', $scGuardId)
    $scGuardPane1 = [string]$scGuardSplit.result
    for ($i = 0; $i -lt 30; $i++) { $n = Get-SessionSnapshot $scGuardId; if ($n -and @($n.paneIds).Count -eq 2) { break }; Start-Sleep -Milliseconds 200 }
    foreach ($shape in @(
            @(@('session', 'split', 'off', $scGuardPane1), 'unexpected argument'),
            @(@('session', 'split', 'close', $scGuardPane1), 'unexpected argument'),
            @(@('session', 'split', 'clos', '--target', $scGuardId), 'unknown op'),
            @(@('session', 'split', 'close', '--targt', $scGuardId), 'unknown option'),
            @(@('session', 'split', 'off', '--target', ''), 'is empty'),
            @(@('session', 'swap', $scGuardId), 'unexpected argument'),
            @(@('session', 'swap', '--targt', $scGuardId), 'unknown option'),
            @(@('session', 'swap', '--target', ''), 'is empty'))) {
        $argv = $shape[0]; $want = $shape[1]
        $out = & $ctl @argv --pipe $pipe 2>&1
        $code = $LASTEXITCODE
        Check "the CLI refuses '$($argv -join ' ')' before sending anything ($want)" ($code -eq 2 -and ("$out" -match $want) -and ("$out" -match 'Nothing sent')) "exit $code, output: $out"
    }
    $scGuardStill = Get-SessionSnapshot $scGuardId
    Check 'and none of the refused shapes touched the fixture: still two panes, the same two' `
        ($scGuardStill -and @($scGuardStill.paneIds).Count -eq 2 -and @($scGuardStill.paneIds)[1] -eq $scGuardPane1) "node=$($scGuardStill | ConvertTo-Json -Compress)"
    # And the case fix: `Close` is `close` (it used to fall through to toggle and collapse the split —
    # pane 1 gone, ok:true). Closing pane 1 by its id leaves pane 0 (the session id) as the survivor.
    $scCase = Invoke-Ctl @('session', 'split', 'Close', '--target', $scGuardPane1)
    Start-Sleep -Milliseconds 400
    $scGuardAfter = Get-SessionSnapshot $scGuardId
    Check 'session split Close (capitalised) closes the NAMED pane and answers the survivor, rather than toggling' `
        ($scCase.ok -and ([string]$scCase.result -eq $scGuardId) -and $scGuardAfter -and -not $scGuardAfter.PSObject.Properties['paneCount']) `
        "reply=$($scCase | ConvertTo-Json -Compress) node=$($scGuardAfter | ConvertTo-Json -Compress)"
    Invoke-Ctl @('session', 'close', $scGuardId) | Out-Null

    $scRefused = Invoke-Ctl @('session', 'split', 'close', '--target', $scId)
    $scStill = Get-SessionSnapshot $scId
    Check 'session split close on a single-pane session is refused naming session close, and the session stands' `
        ((-not $scRefused.ok) -and ([string]$scRefused.error).Contains('session close') -and $null -ne $scStill) `
        "reply=$($scRefused | ConvertTo-Json -Compress)"
    Invoke-Ctl @('session', 'close', $scId) | Out-Null
    $scId = $null

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
        Start-Sleep -Milliseconds 500

        # --stdin two-sources guard, every spelling revmux found (P2 r1: a boolean flag swallowing the
        # positional; r2: any other flag, a misspelt flag, a bare "true"). The CLI must exit 2 and send
        # NOTHING — the pane's text is the oracle, not the exit code alone.
        $stdinBefore = (Invoke-Ctl @('session', 'text', '--target', $survivorId)).result
        foreach ($shape in @(
                @('--stdin', '--allow-control', 'from argv'),
                @('--stdin', '--wait', 'from argv'),
                @('--stdin', '--allow-controll', 'from argv'),
                @('--stdin', 'true'))) {
            $out = 'from pipe' | & $ctl session type @shape --target $survivorId --pipe $pipe 2>&1
            $code = $LASTEXITCODE
            Check "session type --stdin refuses a swallowed positional ($($shape -join ' '))" ($code -eq 2 -and ("$out" -match 'one source')) "exit $code, output: $out"
        }
        Start-Sleep -Milliseconds 300
        $stdinAfter = (Invoke-Ctl @('session', 'text', '--target', $survivorId)).result
        Check 'and typed nothing for any of them' ($stdinAfter -eq $stdinBefore) "text changed"
        # The positive control (r3): the pipe selector under its OTHER name takes a value and must not
        # trip the shape guard. --socket is --pipe; a refusal here names things the caller did not do.
        $sockMarker = 'stdin-socket-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $out = "Write-Output '$sockMarker'`r" | & $ctl session type --stdin --socket $pipe --target $survivorId 2>&1
        Check 'session type --stdin --socket <pipe> is accepted (the valued global option under its alias)' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE, output: $out"
        Start-Sleep -Seconds 2
        $sockText = (Invoke-Ctl @('session', 'text', '--target', $survivorId)).result
        Check 'and the piped text reached the pane' (([string]$sockText).Contains($sockMarker))

        # session.overlay on a target that matches NOTHING is a refusal for close and resize alike
        # (r2: `close --target buidl` answered ok "no overlay" while the overlay on `build` was up),
        # and the positive control: an untargeted close with nothing open stays ok. Asserted against
        # the app, not the fake — the unit test drives FakeSessionHost's copy of the rule.
        $ghostClose = Invoke-Ctl @('session', 'overlay', 'close', '--target', 'no-such-session-zz')
        Check 'overlay close on a target that matches no session is refused' `
            ((-not $ghostClose.ok) -and ("$($ghostClose.error)" -match 'no session')) "$($ghostClose | ConvertTo-Json -Compress)"
        $ghostResize = Invoke-Ctl @('session', 'overlay', 'resize', '--size-percent', '50', '--target', 'no-such-session-zz')
        Check 'overlay resize on a target that matches no session is refused, not told to open one' `
            ((-not $ghostResize.ok) -and ("$($ghostResize.error)" -match 'no session') -and ("$($ghostResize.error)" -notmatch 'open one first')) "$($ghostResize | ConvertTo-Json -Compress)"
        $bareClose = Invoke-Ctl @('session', 'overlay', 'close')
        Check 'an untargeted overlay close with nothing open stays ok' ($bareClose.ok -and $bareClose.result -eq 'no overlay') "$($bareClose | ConvertTo-Json -Compress)"

        # sidebar.width must move the divider, not just a number. The unit tests see the fake host
        # only; here the proof is live geometry: the active session's measured width (session.metrics,
        # columns x cell width) shrinks when the sidebar widens, because the grid derives from the
        # divider. Then the persisted half: the width lands in this instance's state file, which is
        # what a restart reads. Then the refusal: out of range answers ok:false and the width stays.
        Invoke-Ctl @('sidebar', 'show') | Out-Null
        Start-Sleep -Milliseconds 400
        $widthBefore = Invoke-Ctl @('sidebar', 'width')
        $metricsBefore = Invoke-Ctl @('session', 'metrics', '--target', $sessionId)
        $widthSet = Invoke-Ctl @('sidebar', 'width', '320')
        Start-Sleep -Milliseconds 500
        $metricsAfter = Invoke-Ctl @('session', 'metrics', '--target', $sessionId)
        $stateAfter = Invoke-Ctl @('sidebar', 'state')
        Check 'sidebar.width reads the default before any set' `
            ($widthBefore.ok -and $widthBefore.result.width -eq 220 -and $widthBefore.result.visible) `
            "result=$($widthBefore | ConvertTo-Json -Compress)"
        Check 'sidebar.width replies with the width in effect and that it was applied' `
            ($widthSet.ok -and $widthSet.result.width -eq 320 -and $widthSet.result.visible -and $widthSet.result.applied) `
            "result=$($widthSet | ConvertTo-Json -Compress)"
        Check 'sidebar.width moved the divider (the active pane got narrower)' `
            ($metricsBefore.ok -and $metricsAfter.ok -and $metricsAfter.result.widthPx -lt $metricsBefore.result.widthPx) `
            "widthPx before=$($metricsBefore.result.widthPx) after=$($metricsAfter.result.widthPx)"
        Check 'sidebar state carries the width' ($stateAfter.ok -and [string]$stateAfter.result -eq 'visible tree 320') `
            "state=$($stateAfter.result)"
        $stateFile = Get-ChildItem -LiteralPath (Join-Path $testAppDir 'windows') -Filter '*.json' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $persisted = $stateFile -and ((Get-Content -LiteralPath $stateFile.FullName -Raw) -match '"SidebarWidth":\s*320')
        Check 'sidebar.width is persisted to the state file (what a restart reads)' $persisted `
            "file=$($stateFile.FullName)"
        $widthRefused = Invoke-Ctl @('sidebar', 'width', '5')
        $widthAfterRefusal = Invoke-Ctl @('sidebar', 'width')
        Check 'sidebar.width refuses out of range and the divider stays where it was' `
            ((-not $widthRefused.ok) -and ([string]$widthRefused.error).Contains('120..600') -and $widthAfterRefusal.result.width -eq 320) `
            "error=$($widthRefused.error) width=$($widthAfterRefusal.result.width)"
        $badOp = Invoke-Ctl @('sidebar', 'sideways')
        Check 'a sidebar op the app cannot do is refused, not acknowledged' `
            ((-not $badOp.ok) -and ([string]$badOp.error).Contains('sideways')) "reply=$($badOp | ConvertTo-Json -Compress)"
        Invoke-Ctl @('sidebar', 'width', '220') | Out-Null

        Invoke-Ctl @('sidebar', 'hide') | Out-Null
        Start-Sleep -Milliseconds 500

        # session.context (P3): the reply carries the value IN EFFECT (read back off the session
        # inside the UI hop, not echoed from the request) and `tree` reads it back under `context`.
        # Every refusal is asserted twice — the reply, and that the old value still stands — because
        # a refusal that still wrote, or a success that did not, passes a reply-only check. The unit
        # tests drive the fake host's copy of the rules; this is the verb against the app.
        $ctxText = 'p3 context ' + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $ctxSet = Invoke-Ctl @('session', 'context', $ctxText, '--target', $sessionId)
        Start-Sleep -Milliseconds 400
        $ctxNode = Get-SessionSnapshot $sessionId
        Check 'session.context replies with the session id and the value in effect' `
            ($ctxSet.ok -and $ctxSet.result.session -eq $sessionId -and $ctxSet.result.context -eq $ctxText) `
            "reply=$($ctxSet | ConvertTo-Json -Compress)"
        Check 'tree reads the context back under "context"' ($ctxNode.context -eq $ctxText) "context=$($ctxNode.context)"
        $ctxPadded = Invoke-Ctl @('session', 'context', "  $ctxText  ", '--target', $sessionId)
        Check 'leading and trailing whitespace is trimmed, and the reply says so' `
            ($ctxPadded.ok -and $ctxPadded.result.context -eq $ctxText) "reply=$($ctxPadded | ConvertTo-Json -Compress)"
        $ctxCtl = Invoke-Ctl @('session', 'context', "bad`tvalue", '--target', $sessionId)
        $ctxLong = Invoke-Ctl @('session', 'context', ('x' * 201), '--target', $sessionId)
        $ctxBlank = Invoke-Ctl @('session', 'context', '   ', '--target', $sessionId)
        $ctxGhost = Invoke-Ctl @('session', 'context', 'ghost', '--target', 'no-such-session-zz')
        # text beside --clear is refused CLIENT-side (exit 2, nothing sent), like `session type --stdin "text"`
        $ctxBothOut = (& $ctl session context 'text' --clear --target $sessionId --pipe $pipe 2>&1) -join ' '
        $ctxBothCode = $LASTEXITCODE
        Start-Sleep -Milliseconds 400
        $ctxNode2 = Get-SessionSnapshot $sessionId
        Check 'a control character is refused naming it and its offset' `
            ((-not $ctxCtl.ok) -and ("$($ctxCtl.error)" -match 'U\+0009') -and ("$($ctxCtl.error)" -match 'offset 3')) "error=$($ctxCtl.error)"
        Check 'over the ceiling is refused naming the ceiling' `
            ((-not $ctxLong.ok) -and ("$($ctxLong.error)" -match '\b201\b') -and ("$($ctxLong.error)" -match '\b200\b')) "error=$($ctxLong.error)"
        Check 'blank is refused and names --clear as the way to remove one' `
            ((-not $ctxBlank.ok) -and ("$($ctxBlank.error)" -match '--clear')) "error=$($ctxBlank.error)"
        Check 'an unknown target is refused with the rename wording' `
            ((-not $ctxGhost.ok) -and ("$($ctxGhost.error)" -match 'session not found')) "error=$($ctxGhost.error)"
        Check 'text beside --clear is refused by the CLI and nothing is sent' `
            ($ctxBothCode -eq 2 -and $ctxBothOut -match 'one source') "exit $ctxBothCode, output: $ctxBothOut"
        Check 'and after every refusal the old context stands' ($ctxNode2.context -eq $ctxText) "context=$($ctxNode2.context)"
        $ctxRename = Invoke-Ctl @('session', 'rename', 'p3-renamed', '--target', $sessionId)
        Start-Sleep -Milliseconds 400
        $ctxNode3 = Get-SessionSnapshot $sessionId
        Check 'a rename edits the name and leaves the context alone (two fields)' `
            ($ctxRename.ok -and $ctxNode3.name -eq 'p3-renamed' -and $ctxNode3.context -eq $ctxText) "name=$($ctxNode3.name) context=$($ctxNode3.context)"
        $ctxState = Get-ChildItem -LiteralPath (Join-Path $testAppDir 'windows') -Filter '*.json' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        Check 'the context is in the state file (what a restart reads)' `
            ($ctxState -and ((Get-Content -LiteralPath $ctxState.FullName -Raw) -match ('"Context":\s*"' + [regex]::Escape($ctxText) + '"'))) "file=$($ctxState.FullName)"
        $ctxClear = Invoke-Ctl @('session', 'context', '--clear', '--target', $sessionId)
        Start-Sleep -Milliseconds 400
        $ctxNode4 = Get-SessionSnapshot $sessionId
        Check 'clear replies context:null and the tree omits the key' `
            ($ctxClear.ok -and $ctxClear.result.PSObject.Properties['context'] -and $null -eq $ctxClear.result.context -and
             -not $ctxNode4.PSObject.Properties['context']) "reply=$($ctxClear | ConvertTo-Json -Compress)"

        # session reopen carries the context (P3): the undo-close record holds it, so the reopened
        # session comes back on the same id WITH it. Reopen is not a control verb — it is the
        # Ctrl+Shift+R binding — so it is driven through the window's own key path (PostMessage to
        # this instance's hwnd with the modifier set on its input queue; nothing global).
        $reopenCtx = 'reopen me ' + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $reopenMade = Invoke-Ctl @('session', 'new', '--name', 'p3-reopen', '--no-select')
        $reopenId = [string]$reopenMade.result
        for ($i = 0; $i -lt 30 -and -not (Get-SessionSnapshot $reopenId); $i++) { Start-Sleep -Milliseconds 200 }
        $reopenSet = Invoke-Ctl @('session', 'context', $reopenCtx, '--target', $reopenId)
        Start-Sleep -Milliseconds 400
        Invoke-Ctl @('session', 'close', $reopenId) | Out-Null
        for ($i = 0; $i -lt 30 -and (Get-SessionSnapshot $reopenId); $i++) { Start-Sleep -Milliseconds 200 }
        $reopenGone = -not (Get-SessionSnapshot $reopenId)
        $process.Refresh()
        [Agwinterm.Win32ControlTest.NativeMethods]::Chord($process.MainWindowHandle, 0x52, $true)   # Ctrl+Shift+R = reopen_session
        $reopenNode = $null
        for ($i = 0; $i -lt 30; $i++) { $reopenNode = Get-SessionSnapshot $reopenId; if ($reopenNode) { break }; Start-Sleep -Milliseconds 200 }
        Check 'session reopen (Ctrl+Shift+R) brings the session back on its id with its context' `
            ($reopenMade.ok -and $reopenSet.ok -and $reopenGone -and $reopenNode -and $reopenNode.context -eq $reopenCtx) `
            "gone=$reopenGone node=$($reopenNode | ConvertTo-Json -Compress)"
        if ($reopenNode) { Invoke-Ctl @('session', 'close', $reopenId) | Out-Null; Start-Sleep -Milliseconds 400 }

        # restore.capture (P3): the slot is written NOW and the reply says, per pane, what landed;
        # `tree` reads it back under capturedCommands keyed by pane id, and the state file carries it
        # so a kill after this point still restores it. The long-lived child is ping: powershell,
        # pwsh and cmd are on the restore denylist, so a shell child is the honest null, not a capture.
        $capBefore = Get-SessionSnapshot $sessionId
        Check 'no pane carries a capture before the verb has run' (-not $capBefore.PSObject.Properties['capturedCommands'])
        $pingLine = 'ping -n 300 127.0.0.1'
        $pingPattern = '(?i)ping(\.exe)?"?\s+-n 300 127\.0\.0\.1'
        $pingUp = $false
        foreach ($attempt in 1, 2) {   # typed input can be dropped while the shell is busy (the ConPTY early-discard class): type again
            Invoke-Ctl @('session', 'type', "$pingLine`r", '--target', $survivorId) | Out-Null
            for ($i = 0; $i -lt 16; $i++) {
                Start-Sleep -Milliseconds 250
                if (([string](Invoke-Ctl @('session', 'text', '--target', $survivorId)).result) -match 'Pinging 127\.0\.0\.1') { $pingUp = $true; break }
            }
            if ($pingUp) { break }
        }
        Check 'the fixture child (ping) is running in the survivor pane' $pingUp
        $capOne = Invoke-Ctl @('restore', 'capture', '--target', $survivorId)
        $capOnePanes = @($capOne.result.panes)
        Check 'restore capture --target names the pane, its session, what it captured, and the replay toggle' `
            ($capOne.ok -and $capOne.result.captured -eq 1 -and $capOnePanes.Count -eq 1 -and
             $capOnePanes[0].pane -eq $survivorId -and $capOnePanes[0].session -eq $sessionId -and
             ("$($capOnePanes[0].captured)" -match $pingPattern) -and
             $capOne.result.PSObject.Properties['replayOnRestore'] -and $capOne.result.replayOnRestore -eq $false) `
            "reply=$($capOne | ConvertTo-Json -Compress -Depth 5)"
        Start-Sleep -Milliseconds 400
        $capNode = Get-SessionSnapshot $sessionId
        Check 'tree reads the capture back under capturedCommands keyed by pane id' `
            ($capNode.PSObject.Properties['capturedCommands'] -and ("$($capNode.capturedCommands.$survivorId)" -match $pingPattern)) `
            "capturedCommands=$($capNode.capturedCommands | ConvertTo-Json -Compress)"
        $capAll = Invoke-Ctl @('restore', 'capture')
        $capAllPanes = @($capAll.result.panes)
        $capAllShape = $capAll.ok -and $capAllPanes.Count -ge 2 -and
            @($capAllPanes | Where-Object { -not ($_.PSObject.Properties['pane'] -and $_.PSObject.Properties['session'] -and $_.PSObject.Properties['captured']) }).Count -eq 0 -and
            ("$(($capAllPanes | Where-Object { $_.pane -eq $survivorId }).captured)" -match $pingPattern) -and
            $capAll.result.captured -ge 1
        Check 'a bare restore capture reaches every real pane and every entry has pane/session/captured' $capAllShape `
            "reply=$($capAll | ConvertTo-Json -Compress -Depth 5)"
        $capGhost = Invoke-Ctl @('restore', 'capture', '--target', 'no-such-pane-zz')
        Check 'an unknown target is refused in the verb''s own words, naming the target' `
            ((-not $capGhost.ok) -and ("$($capGhost.error)" -match 'restore capture') -and ("$($capGhost.error)" -match 'no-such-pane-zz')) "error=$($capGhost.error)"
        Invoke-Ctl @('session', 'scratch', 'on', '--target', $sessionId) | Out-Null
        Start-Sleep -Milliseconds 800
        $capCover = Invoke-Ctl @('restore', 'capture', '--target', ($sessionId + ':scratch'))
        Invoke-Ctl @('session', 'scratch', 'off', '--target', $sessionId) | Out-Null
        Start-Sleep -Milliseconds 400
        # Matched on the COVER refusal's own wording, not on 'scratch': the unknown-target refusal
        # interpolates the target, and this target contains 'scratch', so that word passed both arms —
        # the check would have stayed green with the cover arm deleted (revmux r1).
        Check 'a scratch cover is refused: it has no restore slot' `
            ((-not $capCover.ok) -and ("$($capCover.error)" -match 'never restored') -and ("$($capCover.error)" -match 'no restore slot')) "error=$($capCover.error)"
        $capNode2 = Get-SessionSnapshot $sessionId
        Check 'and neither refusal touched the slot' ("$($capNode2.capturedCommands.$survivorId)" -match $pingPattern) `
            "capturedCommands=$($capNode2.capturedCommands | ConvertTo-Json -Compress)"
        $capState = Get-ChildItem -LiteralPath (Join-Path $testAppDir 'windows') -Filter '*.json' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        Check 'the capture is in the state file under Command (what a restart, or a kill, leaves)' `
            ($capState -and ((Get-Content -LiteralPath $capState.FullName -Raw) -match '(?i)"Command":\s*"[^"]*ping')) "file=$($capState.FullName)"
        # End the child, then a re-capture of NOTHING must report null and clear the earlier
        # checkpoint — a stale capture replayed at the next start would be the wrong command.
        # The child is ENDED BY PID, not by typing ^C: a lone 0x03 written to ConPTY over the pipe
        # does not reliably raise a console Ctrl+C (it depends on the input buffer's processed-input
        # mode at that instant), so ping kept running and the check failed intermittently (2026-09-05:
        # failed in the full run, passed in isolation). The product knows this — QuitClaudeAndRelaunch
        # sends 0x03 twice and then POLLS for the child to be gone. What this check proves is the
        # VERB's honesty (nothing running -> null), not ConPTY's ^C timing, so the child is stopped
        # deterministically and the re-capture is polled until the shell has no non-denylisted child.
        # Only a ping that is a DESCENDANT of this sandbox's app process: the command line alone is
        # not an identity — restore-roundtrip.ps1 starts the identical line in its own sandbox, and a
        # command-line match killed the other run's live fixture when the two overlapped (revmux r1).
        # `session type` returns no pid, so the ancestry walk (pane shell -> conhost/ptyhost -> app)
        # is the only handle on which ping is ours.
        $procs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
        $parentOf = @{}; foreach ($pr in $procs) { $parentOf[[int]$pr.ProcessId] = [int]$pr.ParentProcessId }
        $procs | Where-Object { $_.Name -eq 'PING.EXE' -and $_.CommandLine -match '-n 300 127\.0\.0\.1' } | ForEach-Object {
            $cur = [int]$_.ParentProcessId; $ours = $false
            for ($hop = 0; $hop -lt 12 -and $cur -gt 4; $hop++) {
                if ($cur -eq $process.Id) { $ours = $true; break }
                if (-not $parentOf.ContainsKey($cur)) { break }
                $cur = $parentOf[$cur]
            }
            if ($ours) { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        }
        $capNone = $null
        $capTries = 0
        while ($capTries -lt 10) {
            Start-Sleep -Milliseconds 700
            $capTries++
            $capNone = Invoke-Ctl @('restore', 'capture', '--target', $survivorId)
            if ($capNone.ok -and $capNone.result.captured -eq 0) { break }
        }
        Start-Sleep -Milliseconds 400
        $capNode3 = Get-SessionSnapshot $sessionId
        Check 'a re-capture with nothing running reports null, counts zero, and clears the checkpoint' `
            ($capNone.ok -and $capNone.result.captured -eq 0 -and $null -eq @($capNone.result.panes)[0].captured -and
             -not ($capNode3.PSObject.Properties['capturedCommands'] -and $capNode3.capturedCommands.PSObject.Properties[$survivorId])) `
            "after $capTries capture(s): reply=$($capNone | ConvertTo-Json -Compress -Depth 5) capturedCommands=$($capNode3.capturedCommands | ConvertTo-Json -Compress)"

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
    if ($scId) {
        try { Invoke-Ctl @('session', 'close', $scId) | Out-Null } catch { }
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
    foreach ($name in $savedEnv.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnv[$name])
    }
}

if ($fail) { "Win32 control-host integration: $fail FAILED"; exit 1 }
"Win32 control-host integration: all passed"
exit 0
