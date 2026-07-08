# test-kitty-keyboard.ps1 — interactively verify agwinterm's Kitty keyboard protocol.
#
# Run this INSIDE an agwinterm session:  pwsh -File tools\test-kitty-keyboard.ps1
# It enables the Kitty keyboard protocol, then prints the exact escape sequence the
# terminal sends for each key you press. Press keys from the checklist below and compare.
# Press  q  (plain, no modifiers) to quit.
#
# Legacy vs Kitty — what to look for:
#   Tab            -> \x1b[9u        (Kitty)   vs  \t (0x09) legacy
#   Ctrl+I         -> \x1b[105;5u              (DIFFERENT from Tab — the whole point!)
#   Enter          -> \x1b[13u
#   Ctrl+M         -> \x1b[109;5u              (DIFFERENT from Enter)
#   Esc            -> \x1b[27u
#   Backspace      -> \x1b[127u
#   Ctrl+A         -> \x1b[97;5u
#   Ctrl+Shift+B   -> \x1b[98;6u  (use B: Ctrl+Shift+A is the app's Select-All chord)
#   Alt+A          -> \x1b[97;3u
#   F1             -> \x1bOP     ; Shift+F1 -> \x1b[1;2P
#   Up             -> \x1b[A     ; Ctrl+Up  -> \x1b[1;5A
#   plain 'a'      -> a          (plain typing is untouched)
#
# Note: agwinterm's own keyboard shortcuts (the Ctrl+Shift+* chords) still take precedence over
# the app, matching Windows Terminal — so those won't reach the protocol.

$ErrorActionPreference = 'Stop'

Add-Type -Namespace Native -Name Con -MemberDefinition @'
    [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr h, out uint m);
    [DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr h, uint m);
'@

$STDIN = -10; $STDOUT = -11
$hIn  = [Native.Con]::GetStdHandle($STDIN)
$hOut = [Native.Con]::GetStdHandle($STDOUT)
[Native.Con]::GetConsoleMode($hIn,  [ref]([uint32]$origIn  = 0))  | Out-Null
[Native.Con]::GetConsoleMode($hOut, [ref]([uint32]$origOut = 0))  | Out-Null

# Raw input: no line buffering, no echo, no ctrl-C processing, VT input on.
$ENABLE_PROCESSED_INPUT = 0x1; $ENABLE_LINE_INPUT = 0x2; $ENABLE_ECHO_INPUT = 0x4
$ENABLE_VT_INPUT = 0x200; $ENABLE_VT_PROCESSING = 0x4
$rawIn = ($origIn -band (-bnot ($ENABLE_PROCESSED_INPUT -bor $ENABLE_LINE_INPUT -bor $ENABLE_ECHO_INPUT))) -bor $ENABLE_VT_INPUT
[Native.Con]::SetConsoleMode($hIn, $rawIn) | Out-Null
[Native.Con]::SetConsoleMode($hOut, $origOut -bor $ENABLE_VT_PROCESSING) | Out-Null

function Esc([byte[]]$b) {
    ($b | ForEach-Object {
        if ($_ -eq 27) { '\x1b' }
        elseif ($_ -ge 32 -and $_ -lt 127) { [char]$_ }
        else { '\x{0:x2}' -f $_ }
    }) -join ''
}

try {
    [Console]::Write("`e[>1u")   # enable: disambiguate escape codes
    Write-Host "Kitty keyboard ON. Press keys (see the header for the checklist). Plain 'q' quits.`n"
    $stdin = [Console]::OpenStandardInput()
    $buf = New-Object byte[] 32
    while ($true) {
        $n = $stdin.Read($buf, 0, $buf.Length)
        if ($n -le 0) { continue }
        $seq = $buf[0..($n-1)]
        Write-Host ("  {0,-16}  ({1} byte$(if($n-ne1){'s'}))" -f (Esc $seq), $n)
        if ($n -eq 1 -and $seq[0] -eq [byte][char]'q') { break }   # plain q quits
    }
}
finally {
    [Console]::Write("`e[<u")    # disable Kitty keyboard
    [Native.Con]::SetConsoleMode($hIn, $origIn)   | Out-Null
    [Native.Con]::SetConsoleMode($hOut, $origOut) | Out-Null
    Write-Host "`nKitty keyboard OFF. Bye."
}
