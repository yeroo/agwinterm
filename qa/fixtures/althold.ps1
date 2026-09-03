# Enters the alt screen and HOLDS it. Typed as two separate commands, the shell prompt redraw
# lands in between and the buffer is back before the check runs.
$e = [char]27
[Console]::Write("$e[?1049h")
Start-Sleep 6
[Console]::Write("$e[?1049l")
