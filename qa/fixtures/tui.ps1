# A codex-shaped TUI: alt screen, a static body, a status line repainting in place about once a
# second - the pattern that made every mouse-move drop the selection. After ~14s it BLANKS the
# body and idles, which is the state where a live selection holds no text.
#
# The counter is the interrupt detector: an interrupted script never reaches its own ?1049l, so
# the alt screen stays up either way - what tells a kill from a survivor is the number freezing.
$e = [char]27
[Console]::Write("$e[?1049h$e[H$e[2J")
1..25 | ForEach-Object { [Console]::Write("BODY-$_-tttttttttttttttt$e[K`r`n") }
foreach ($i in 1..14) { [Console]::Write("$e[26;1H$e[Kworking $i"); Start-Sleep 1 }
[Console]::Write("$e[H$e[2J")
foreach ($i in 1..120) { [Console]::Write("$e[26;1H$e[Kblanked $i"); Start-Sleep 1 }
[Console]::Write("$e[?1049l")
