# The paste sink: the process a pane runs while the integration suite pastes into it. Dot-sourced by
# win32-control.ps1; paste-sink.tests.ps1 drives the two functions on synthetic pane text.
#
# Why a sink. The P6 paste case proves the clipboard holds the sentinel, closes it, and then asks the
# app to paste — and the paste reads the clipboard AGAIN. A copy made anywhere on this desktop
# between the two reads is pasted instead of the sentinel (round 8 of #256), and a shell would RUN a
# multi-line one: the profile is throwaway, the shell is the user's. So the pastes go into a session
# whose process IS a sink — a program that reads lines and never runs them; the console echoes what
# it reads, so a pasted line still shows in `session text`. The host-side fix (compare the clipboard
# to what was proven, capture instead of paste) is #257's; this is the harness's guard.
#
# Why a session of its own, started with `session new --command` (New-PasteSinkLaunch):
# - The control API's --command starts the program DIRECTLY (CreatePane's last arm: ParseArgv, then
#   StartAsync(argv[0], argv[1..]) — not the UI's `powershell -NoExit -Command` wrap, not `cmd /c`).
#   The pane's process tree holds no shell: nothing is typed, nothing is echoed, and when the sink
#   leaves for any reason (a Ctrl+C in a pasted line, a crash) there is no interpreter for the pane
#   to fall back to — the app shows the exited surface, and a paste into it reaches no program.
#   (Round 9, Codex: a sink typed INTO a shell hands the pane back to that shell when it dies, and a
#   trailing `; exit` does not run when the pipeline was interrupted.)
# - Readiness (Test-PasteSinkReady) is the marker the sink prints, seen in the pane's text: proof that
#   the sink STARTED. Nothing is pasted until it is seen. It is not an isolation claim beyond the
#   process arrangement above.
# - No `$` in the command: `session new --command` splits it on whitespace with double quotes
#   grouping (ParseArgv) and passes the pieces verbatim, but a `$` would need escaping should the
#   line ever be typed into a shell; [void](Read-Host) needs none. Read-Host throws under
#   -NonInteractive, so the sink runs without it.
function New-PasteSinkLaunch([string]$marker) {
    [pscustomobject]@{
        # The `session new --command` value: powershell.exe (always present), the sink as its -Command.
        Command = "powershell -NoProfile -Command `"'$marker-ready'; for(;;){ [void](Read-Host) }`""
        # What the running sink prints.
        Ready   = "$marker-ready"
    }
}

# Whether the pane's text shows the sink running: the ready marker, whole, with row breaks ignored
# (a narrow pane wraps it).
function Test-PasteSinkReady([string]$text, [string]$ready) {
    if ([string]::IsNullOrEmpty($text) -or [string]::IsNullOrEmpty($ready)) { return $false }
    return ($text -replace "`r?`n", '').Contains($ready)
}
