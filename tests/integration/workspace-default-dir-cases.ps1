# Dot-sourced from win32-control.ps1 — workspace default directory (Win32 host + real spawn).
# Expects: Invoke-Ctl, Check, Wait-StateFile, $testAppDir

$wsDefaultRoot = Join-Path $testAppDir 'workspace-default-dir-root'
New-Item -ItemType Directory -Path $wsDefaultRoot -Force | Out-Null
$wsDefaultRootFull = [IO.Path]::GetFullPath($wsDefaultRoot)

function WsDefault-TreeWorkspace([string]$id) {
    $tree = Invoke-Ctl @('tree')
    if (-not $tree.ok) { return $null }
    foreach ($w in @($tree.result.workspaces)) {
        if ([string]$w.id -eq $id) { return $w }
    }
    return $null
}

function WsDefault-WaitText([string]$sessionId, [string]$needle, [int]$tries = 60) {
    for ($i = 0; $i -lt $tries; $i++) {
        $t = Invoke-Ctl @('session', 'text', '--target', $sessionId)
        if ($t.ok -and (([string]$t.result -replace '[\r\n]', '').Contains($needle))) { return [string]$t.result }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function WsDefault-TextReportsCwd([string]$text, [string]$expectedDir) {
    if ([string]::IsNullOrEmpty($text)) { return $false }
    try {
        # Ignore terminal wrapping and exclude exit/status messages after the path.
        $flat = ($text -replace '[\r\n]', '')
        if ($flat -notmatch 'CWD_BEGIN=(.*?)=CWD_END') { return $false }
        $reported = [IO.Path]::GetFullPath($Matches[1])
        $want = [IO.Path]::GetFullPath($expectedDir)
        return $reported.Equals($want, [StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}

function WsDefault-DeleteWorkspace([string]$wsId) {
    if ([string]::IsNullOrEmpty($wsId)) { return }
    try { Invoke-Ctl @('workspace', 'delete', '--target', $wsId) | Out-Null } catch { }
}

$wsNew = $null
$wsSet = $null
$sesDefault = $null
$sesExplicit = $null
try {
    $created = Invoke-Ctl @('workspace', 'new', 'ws-default-conform', '--default-dir', $wsDefaultRootFull)
    Check 'workspace new --default-dir succeeds' $created.ok ([string]$created.error)
    $wsNew = [string]$created.result
    $node = WsDefault-TreeWorkspace $wsNew
    Check 'tree carries defaultDir after workspace new' (
        $null -ne $node -and [string]$node.defaultDir -eq $wsDefaultRootFull)

    $setDir = Join-Path $testAppDir 'workspace-default-dir-set'
    New-Item -ItemType Directory -Path $setDir -Force | Out-Null
    $setDirFull = [IO.Path]::GetFullPath($setDir)
    $wsSetReply = Invoke-Ctl @('workspace', 'new', 'ws-default-set')
    Check 'workspace new for set target succeeds' $wsSetReply.ok
    $wsSet = [string]$wsSetReply.result
    $set = Invoke-Ctl @('workspace', 'default-dir', 'set', $setDirFull, '--target', $wsSet)
    Check 'workspace default-dir set succeeds' ($set.ok -and [string]$set.result.defaultDir -eq $setDirFull)
    $nodeSet = WsDefault-TreeWorkspace $wsSet
    Check 'tree carries defaultDir after set' ($null -ne $nodeSet -and [string]$nodeSet.defaultDir -eq $setDirFull)

    $bad = Invoke-Ctl @('workspace', 'default-dir', 'set', 'Z:\agwinterm-no-such-default-dir', '--target', $wsSet)
    Check 'workspace default-dir set refuses missing directory' (-not $bad.ok)
    $nodeAfterBad = WsDefault-TreeWorkspace $wsSet
    Check 'tree defaultDir unchanged after refused set' ($null -ne $nodeAfterBad -and [string]$nodeAfterBad.defaultDir -eq $setDirFull)

    $stateFile = Wait-StateFile {
        param($json)
        $json -match '"DefaultDir"\s*:\s*"'
    }
    Check 'persisted state contains workspace DefaultDir' ($null -ne $stateFile)

    $clear = Invoke-Ctl @('workspace', 'default-dir', 'clear', '--target', $wsSet)
    Check 'workspace default-dir clear succeeds' ($clear.ok -and $null -eq $clear.result.defaultDir)
    $nodeCleared = WsDefault-TreeWorkspace $wsSet
    $hasDefaultKey = $null -ne $nodeCleared -and $nodeCleared.PSObject.Properties.Name -contains 'defaultDir'
    Check 'tree omits defaultDir after clear' ($null -ne $nodeCleared -and -not $hasDefaultKey)

    $cmd = 'cmd.exe /d /c cd && echo CWD_BEGIN=%CD%=CWD_END'
    $newSes = Invoke-Ctl @('session', 'new', '--workspace', $wsNew, '--no-select', '--command-mode', 'direct', '--command', $cmd)
    Check 'session new in workspace with default-dir succeeds' $newSes.ok
    $sesDefault = [string]$newSes.result
    $text = WsDefault-WaitText $sesDefault '=CWD_END'
    Check 'new session spawns in workspace default directory' (WsDefault-TextReportsCwd $text $wsDefaultRootFull) $text

    $otherDir = Join-Path $testAppDir 'workspace-default-dir-explicit'
    New-Item -ItemType Directory -Path $otherDir -Force | Out-Null
    $otherFull = [IO.Path]::GetFullPath($otherDir)
    $newSes2 = Invoke-Ctl @(
        'session', 'new', '--workspace', $wsNew, '--no-select', '--cwd', $otherFull,
        '--command-mode', 'direct', '--command', $cmd)
    Check 'session new with explicit cwd succeeds' $newSes2.ok
    $sesExplicit = [string]$newSes2.result
    $text2 = WsDefault-WaitText $sesExplicit '=CWD_END'
    Check 'explicit cwd overrides workspace default' (WsDefault-TextReportsCwd $text2 $otherFull) $text2
}
finally {
    foreach ($sid in @($sesDefault, $sesExplicit)) {
        if ($sid) { try { Invoke-Ctl @('session', 'close', '--target', $sid) | Out-Null } catch { } }
    }
    WsDefault-DeleteWorkspace $wsNew
    WsDefault-DeleteWorkspace $wsSet
}
