# kitty-capture.ps1 <logfile> — headless capture harness for automated testing.
# Enables the Kitty keyboard protocol, then appends each received escape sequence (escaped) to
# <logfile>, one per line, prefixed with a tag. Exits when it receives a plain 'Q'.
param([Parameter(Mandatory)][string]$LogFile)

Add-Type -Namespace Native -Name Con2 -MemberDefinition @'
    [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr h, out uint m);
    [DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr h, uint m);
'@
$hIn = [Native.Con2]::GetStdHandle(-10)
[Native.Con2]::GetConsoleMode($hIn, [ref]([uint32]$origIn = 0)) | Out-Null
[Native.Con2]::SetConsoleMode($hIn, ($origIn -band (-bnot 0x7)) -bor 0x200) | Out-Null   # raw + VT input

function Esc([byte[]]$b) {
    ($b | ForEach-Object { if ($_ -eq 27) { '\x1b' } elseif ($_ -ge 32 -and $_ -lt 127) { [char]$_ } else { '\x{0:x2}' -f $_ } }) -join ''
}
Set-Content -Path $LogFile -Value "" -NoNewline
try {
    [Console]::Write("`e[>1u")
    $stdin = [Console]::OpenStandardInput()
    $buf = New-Object byte[] 32
    while ($true) {
        $n = $stdin.Read($buf, 0, $buf.Length)
        if ($n -le 0) { continue }
        $seq = $buf[0..($n-1)]
        Add-Content -Path $LogFile -Value (Esc $seq)
        if ($n -eq 1 -and $seq[0] -eq [byte][char]'Q') { break }
    }
}
finally {
    [Console]::Write("`e[<u")
    [Native.Con2]::SetConsoleMode($hIn, $origIn) | Out-Null
}
