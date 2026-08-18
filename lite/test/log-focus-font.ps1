# lite diagnostics log — Task 3 checks: focus handoffs and font resolution must be in the log.
#
# "I can't type after switching sessions" was only pinned because GetGUIThreadInfo could be run live
# on the dev machine. These lines make the same story readable from a laptop's log file.
param([string]$Exe = "$PSScriptRoot\..\bin\agliteterm.exe")

$ErrorActionPreference = 'Stop'
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name$(if ($detail) { " — $detail" })" }
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class FF {
  [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr p, IntPtr c, string cls, string win);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO {
    public int cbSize; public int flags; public IntPtr hwndActive, hwndFocus, hwndCapture,
    hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
  public static IntPtr LP(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }
}
"@

$pipe = 'logfoc'
$dir  = "$env:LOCALAPPDATA\agliteterm"
$log  = "$dir\agliteterm-$pipe.log"
$ctl  = "$env:LOCALAPPDATA\Programs\agwinterm\agwintermctl.exe"
Remove-Item $log, "$dir\sessions-$pipe.tsv" -ErrorAction SilentlyContinue

"== log-focus-font =="

$p = Start-Process $Exe -ArgumentList @('--pipe', $pipe) -PassThru
Start-Sleep -Seconds 7
$p.Refresh(); $h = $p.MainWindowHandle
& $ctl session new --pipe $pipe 2>&1 | Out-Null
Start-Sleep -Seconds 3

# --- font resolution is recorded, with its source and the pack inventory ----------------------
$text = Get-Content $log -Raw
$font = [regex]::Match($text, 'font: (.+?) \((remembered|first-run default)\) cell=(\d+)x(\d+) \| packs: agbf=(\d) complete=(\d)')
Check 'font resolution is logged' $font.Success
if ($font.Success) {
    Check 'names the source of the selection' ($font.Groups[2].Value -in @('remembered','first-run default'))
    Check 'reports a real cell size' ([int]$font.Groups[3].Value -gt 0 -and [int]$font.Groups[4].Value -gt 0)
    Check 'reports pack inventory' ($font.Groups[5].Value -eq '1' -and $font.Groups[6].Value -eq '1') `
        "agbf=$($font.Groups[5].Value) complete=$($font.Groups[6].Value)"
}

# --- clicking the sidebar is narrated, and focus really does end up on the frame ---------------
$tree = [FF]::FindWindowExW($h, [IntPtr]::Zero, "SysTreeView32", $null)
[FF]::PostMessageW($tree, 0x0201, [IntPtr]1, [FF]::LP(60, 49)) | Out-Null   # WM_LBUTTONDOWN
Start-Sleep -Milliseconds 400
[FF]::PostMessageW($tree, 0x0202, [IntPtr]0, [FF]::LP(60, 49)) | Out-Null   # WM_LBUTTONUP
Start-Sleep -Seconds 2

$text2 = Get-Content $log -Raw
Check 'sidebar focus grab is logged' ($text2 -match 'focus: sidebar took focus')
Check 'handing the keyboard back is logged' ($text2 -match 'focus: terminal has the keyboard')

$tid = [FF]::GetWindowThreadProcessId($h, [IntPtr]::Zero)
$gi = New-Object FF+GUITHREADINFO
$gi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($gi)
[FF]::GetGUIThreadInfo($tid, [ref]$gi) | Out-Null
Check 'focus owner is the frame, not the tree' ($gi.hwndFocus -eq $h) "focus=$($gi.hwndFocus) frame=$h tree=$tree"

$p.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 3
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

if ($fail) { "log-focus-font: $fail FAILED"; exit 1 } else { "log-focus-font: all passed"; exit 0 }
