# The paste sink's launch and readiness test, on synthetic pane text: no app, no pane. The readiness
# poll gates the P6 paste cases in win32-control.ps1 (nothing is pasted until the sink is seen
# running), so what satisfies it is pinned here — and so is the shape of the launch: a program the
# control API starts DIRECTLY, no shell underneath (round 9 of #256). Runs in CI before
# win32-control.ps1.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'paste-sink.ps1')
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name$(if ($detail) { " — $detail" })" }
}
# The app's ParseArgv (Program.Sessions.cs): whitespace splits, double quotes group and are dropped.
function Split-Argv([string]$s) {
    $args = @(); $cur = ''; $inQuote = $false; $has = $false
    foreach ($ch in $s.ToCharArray()) {
        if ($ch -eq '"') { $inQuote = -not $inQuote; $has = $true }
        elseif ([char]::IsWhiteSpace($ch) -and -not $inQuote) { if ($has) { $args += $cur; $cur = ''; $has = $false } }
        else { $cur += $ch; $has = $true }
    }
    if ($has) { $args += $cur }
    , $args
}

"== paste sink (synthetic) =="
$marker = 'agw-sink-0123abcd'
$launch = New-PasteSinkLaunch $marker
$argv = Split-Argv $launch.Command
Check 'the launch is powershell.exe started directly: argv[0] powershell, -NoProfile, -Command, one script — no cmd /c, no -NoExit, no shell to fall back to' ($argv.Count -eq 4 -and $argv[0] -eq 'powershell' -and $argv[1] -eq '-NoProfile' -and $argv[2] -eq '-Command' -and $launch.Command -notmatch '(?i)-NoExit|cmd(\.exe)? /c|\bexit\b') "argv=$($argv -join ' | ')"
Check 'the sink script prints the ready marker, then reads lines forever and runs none' ($argv[3] -eq "'$marker-ready'; for(;;){ [void](Read-Host) }") "script=$($argv[3])"
Check 'the command carries no `$` (nothing for a shell to expand, should one ever see it) and no -NonInteractive (Read-Host throws under it)' (-not $launch.Command.Contains('$') -and $launch.Command -notmatch '(?i)-NonInteractive') "command=$($launch.Command)"
Check 'the ready marker is <marker>-ready' ($launch.Ready -eq 'agw-sink-0123abcd-ready')

# The pane before the sink prints: empty, or only what the app painted.
Check 'empty pane: NOT ready' (-not (Test-PasteSinkReady '' $launch.Ready) -and -not (Test-PasteSinkReady "`n`n" $launch.Ready))
# Had the launch been TYPED into a shell, its echo would carry the marker: the sink is not typed, but
# the predicate is pinned against that text too — a marker in quotes on a command line is the marker.
$echo = 'PS C:\Users\runneradmin> ' + $launch.Command + "`n"
Check '(a typed echo of the launch WOULD satisfy the predicate — which is why the sink is started directly, never typed)' (Test-PasteSinkReady $echo $launch.Ready)
# The sink started: it printed the marker.
Check 'the printed marker: ready' (Test-PasteSinkReady ($launch.Ready + "`n") $launch.Ready)
Check 'the printed marker split by a row break (CRLF, a narrow pane): ready — row breaks are ignored' (Test-PasteSinkReady ('agw-sink-0123a' + "`r`n" + 'bcd-ready' + "`n") $launch.Ready)
Check 'another run''s marker is NOT ready' (-not (Test-PasteSinkReady ('agw-sink-ffffffff-ready' + "`n") $launch.Ready))
Check 'an empty marker is never ready' (-not (Test-PasteSinkReady ($launch.Ready + "`n") ''))

if ($fail) { "paste sink: $fail FAILED"; exit 1 }
"paste sink: all passed"
exit 0
