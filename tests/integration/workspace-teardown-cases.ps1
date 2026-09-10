# Runs only within hud-ui's private process job and suite-token lifetime.
if(-not (NavWait {(Node $session).foregroundShell-eq 'cmd'})){throw 'Teardown fixture needs its survivor shell ready'}
$survivorCount=$job.Count()
foreach($deleteActive in $false,$true){
    $doomedWs=[string](Rpc 'workspace.new' @{name='teardown-owned'})
    try {
        $null=Rpc 'workspace.select' @{} $doomedWs
        $doomed=[string](Rpc 'session.new' @{name='teardown-split';wait=$true})
        $right=[string](Rpc 'session.split' @{op='on'} $doomed)
        if(-not (NavWait {(Node $doomed).foregroundShells.Count-eq 2 -and @((Node $doomed).foregroundShells|Where-Object {$_-ne 'cmd'}).Count-eq 0})){throw 'Split shells not ready'}
        $null=Rpc 'session.scratch' @{op='on'} $doomed
        if(-not (NavWait {([string](Rpc 'session.text' @{})).Contains('>')})){throw 'Scratch shell not ready'}
        $null=Rpc 'session.scratch' @{op='off'} $doomed
        $paneOverlay=[string](Rpc 'session.overlay' @{action='open';pane='left';command='cmd /d /k echo PANE-TEARDOWN-READY'} $doomed)
        if(-not (NavWait {([string](Rpc 'session.text' @{} $paneOverlay)).Contains('PANE-TEARDOWN-READY')})){throw 'Pane overlay not ready'}
        $wholeOverlay=[string](Rpc 'session.overlay' @{action='open';command='cmd /d /k echo SESSION-TEARDOWN-READY'} $doomed)
        if(-not (NavWait {([string](Rpc 'session.text' @{} $wholeOverlay)).Contains('SESSION-TEARDOWN-READY')})){throw 'Session overlay not ready'}
        Check 'fixture owns extra live processes before deletion' ($job.Count()-gt $survivorCount)
        if(-not $deleteActive){$null=Rpc 'session.select' @{} $session}
        # Frozen walk membership must not keep a deleted session reachable by commit or cancel.
        $null=Rpc 'session.switch' @{op='begin'} -NoTarget
        $null=Rpc 'workspace.delete' @{} $doomedWs
        $null=Rpc 'session.switch' @{op='cancel'} -NoTarget
        $null=Rpc 'session.switch' @{op='commit'} -NoTarget
        Check "workspace deletion releases every owned process (active=$deleteActive)" (NavWait {$job.Count()-eq $survivorCount})
        Check 'deleted session cannot be reactivated by stale switch state' ($null-eq (Node $doomed) -and (Node $session).active)
        Check 'unrelated survivor still accepts control reads' (-not [string]::IsNullOrWhiteSpace([string](Rpc 'session.text' @{} $session)))
        Check 'repeated deletion refuses' (-not (Rpc 'workspace.delete' @{} $doomedWs -AllowError).ok)
    } finally {
        if(@(NavTree|Where-Object id -eq $doomedWs).Count){$null=Rpc 'workspace.delete' @{} $doomedWs}
        $null=Rpc 'session.select' @{} $session
    }
}
