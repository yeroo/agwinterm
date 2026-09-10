# Runs only within hud-ui's private process job and suite-token lifetime.
if(-not (NavWait {(Node $session).foregroundShell-eq 'cmd'})){throw 'Teardown fixture needs its survivor shell ready'}
$survivorIds=@($job.MemberIds());$survivorCount=$survivorIds.Count
"Teardown baseline: $survivorCount processes; $($job.Members())"
foreach($finishWalk in 'cancel','commit'){
foreach($deleteActive in $false,$true){
    $doomedWs=[string](Rpc 'workspace.new' @{name='teardown-owned'})
    try {
        $null=Rpc 'workspace.select' @{} $doomedWs
        $doomed=[string](Rpc 'session.new' @{name='teardown-split'})
        if(-not (NavWait {$null-ne (Node $doomed)})){throw 'Doomed session did not materialize'}
        $right=[string](Rpc 'session.split' @{op='on'} $doomed)
        if(-not (NavWait {(Node $doomed).foregroundShells.Count-eq 2 -and @((Node $doomed).foregroundShells|Where-Object {$_-ne 'cmd'}).Count-eq 0})){throw 'Split shells not ready'}
        $null=Rpc 'session.scratch' @{op='on'} $doomed
        $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -NoTarget # waits behind scratch creation
        if(-not (NavWait {([string](Rpc 'session.text' @{})).Contains('>')})){throw 'Scratch shell not ready'}
        $null=Rpc 'session.scratch' @{op='off'} $doomed
        $paneOverlay=[string](Rpc 'session.overlay' @{action='open';pane='left';command='cmd /d /k echo PANE-TEARDOWN-READY'} $doomed)
        if(-not (NavWait {([string](Rpc 'session.text' @{} $paneOverlay)).Contains('PANE-TEARDOWN-READY')})){throw 'Pane overlay not ready'}
        $wholeOverlay=[string](Rpc 'session.overlay' @{action='open';command='cmd /d /k echo SESSION-TEARDOWN-READY'} $doomed)
        if(-not (NavWait {([string](Rpc 'session.text' @{} $wholeOverlay)).Contains('SESSION-TEARDOWN-READY')})){throw 'Session overlay not ready'}
        Check 'fixture owns extra live processes before deletion' (@($job.MemberIds()|Where-Object {$_-notin $survivorIds}).Count-gt 0)
        if(-not $deleteActive){$null=Rpc 'session.select' @{} $session}
        # Frozen walk membership must not keep a deleted session reachable by commit or cancel.
        $null=Rpc 'session.switch' @{op='begin'} -NoTarget
        # Preview the doomed member in a fresh walk for EACH finishing operation.
        if($finishWalk-eq 'commit'){
            if(-not (Node $doomed).active){$null=Rpc 'session.switch' @{op='next'} -NoTarget}
            Check 'commit walk previews doomed session before deletion' ((Node $doomed).active)
        }
        $null=Rpc 'dashboard' @{ids=$doomed} -NoTarget
        $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -NoTarget # queued UI barrier
        $null=Rpc 'workspace.delete' @{} $doomedWs
        $null=Rpc 'session.switch' @{op=$finishWalk} -NoTarget
        NavKey 13 # a stale dashboard must not activate its disposed member
        # Startup helpers in the baseline may exit normally; require every NEW job member gone,
        # not an unchanged count (or count <= baseline, which could hide a different leaked child).
        $released=NavWait {@($job.MemberIds()|Where-Object {$_-notin $survivorIds}).Count-eq 0}
        Check "workspace deletion releases every owned process (active=$deleteActive, finish=$finishWalk)" $released "baseline=$survivorCount remaining=$($job.Count()) members=$($job.Members())"
        Check 'deleted session cannot be reactivated by stale switch state' ($null-eq (Node $doomed) -and (Node $session).active)
        $beforeDuplicate=@((NavTree).sessions).Count
        $duplicate=Rpc 'session.duplicate' @{} $doomed -AllowError
        Check 'duplicate refuses a removed explicit owner instead of cloning the survivor' (-not $duplicate.ok -and @((NavTree).sessions).Count-eq $beforeDuplicate)
        Check 'unrelated survivor still accepts control reads' (-not [string]::IsNullOrWhiteSpace([string](Rpc 'session.text' @{} $session)))
        Check 'repeated deletion refuses' (-not (Rpc 'workspace.delete' @{} $doomedWs -AllowError).ok)
    } finally {
        if(@(NavTree|Where-Object id -eq $doomedWs).Count){$null=Rpc 'workspace.delete' @{} $doomedWs}
        $null=Rpc 'session.select' @{} $session
    }
}
}
