# Dot-sourced only by hud-ui.ps1 -Suite Quick, after its token and owned-job setup.
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class QuickProbe {
    delegate bool Visitor(IntPtr h,IntPtr p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x,y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left,top,right,bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MON { public int size; public RECT monitor,work; public uint flags; }
    [DllImport("user32.dll")] static extern bool EnumWindows(Visitor f,IntPtr p);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h,StringBuilder b,int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p,uint f);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr h,ref MON m);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool RegisterHotKey(IntPtr h,int id,uint m,uint k);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool UnregisterHotKey(IntPtr h,int id);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern void keybd_event(byte k,byte scan,uint flags,UIntPtr extra);
    public static void Hotkey() {
        keybd_event(0x11,0,0,UIntPtr.Zero);keybd_event(0x12,0,0,UIntPtr.Zero);keybd_event(0x10,0,0,UIntPtr.Zero);
        keybd_event(0x78,0,0,UIntPtr.Zero);keybd_event(0x78,0,2,UIntPtr.Zero);
        keybd_event(0x10,0,2,UIntPtr.Zero);keybd_event(0x12,0,2,UIntPtr.Zero);keybd_event(0x11,0,2,UIntPtr.Zero);
    }
    public static IntPtr Find(uint pid) {
        IntPtr result=IntPtr.Zero;
        EnumWindows((h,p)=>{uint n;GetWindowThreadProcessId(h,out n);if(n==pid){var s=new StringBuilder(256);GetWindowTextW(h,s,256);if(s.ToString()=="agwinterm quick terminal"){result=h;return false;}}return true;},IntPtr.Zero);
        return result;
    }
    public static RECT WorkArea() { POINT p;GetCursorPos(out p);var m=new MON {size=Marshal.SizeOf<MON>()};if(!GetMonitorInfoW(MonitorFromPoint(p,2),ref m))throw new Exception("Monitor query");return m.work; }
    public static string Rect(IntPtr h) { RECT r;if(!GetWindowRect(h,out r))throw new Exception("Window rect");return $"{r.left},{r.top},{r.right},{r.bottom}"; }
}
'@
$q=[QuickProbe]::Find($job.Pid)
if($q-eq [IntPtr]::Zero){throw 'No owned quick host'}
$normalRect=[QuickProbe]::Rect($hwnd)
$normalMetrics=Rpc 'session.metrics' @{} $session|ConvertTo-Json -Compress
$library=Rpc 'window.list'|ConvertTo-Json -Compress
Check 'quick shell is lazy and panel initially hidden' (-not [QuickProbe]::IsWindowVisible($q) -and -not (Rpc 'session.text' -Window quick -AllowError).ok)
$fg=[HudOwnedJob]::GetForegroundWindow()
$null=Rpc 'quick' @{op='on'}
for($i=0;$i-lt 50 -and -not (Rpc 'window.state').quickTerminalVisible;$i++){Start-Sleep -Milliseconds 100}
Check 'control show makes detached quick visible without activating' ([QuickProbe]::IsWindowVisible($q) -and [HudOwnedJob]::GetForegroundWindow()-eq $fg)
for($i=0;$i-lt 50 -and ([string](Rpc 'session.text' -Window quick))-notmatch '>';$i++){Start-Sleep -Milliseconds 100}
$null=Rpc 'session.type' @{text="set P14_KEEP=alive`r"} -Window quick
foreach($percent in 40,70,90){
    $null=Rpc 'config.set' @{key='quick-terminal-size';value=[string]$percent}
    $wa=[QuickProbe]::WorkArea();$qr=[QuickProbe+RECT]::new();[void][QuickProbe]::GetWindowRect($q,[ref]$qr)
    $wantW=[int][Math]::Floor(($wa.right-$wa.left)*$percent/100)
    $wantH=[int][Math]::Floor(($wa.bottom-$wa.top)*$percent/100)
    Check "quick uses $percent percent of monitor work area" (($qr.right-$qr.left)-eq $wantW -and ($qr.bottom-$qr.top)-eq $wantH)
    Check "normal geometry/grid untouched at $percent" ([QuickProbe]::Rect($hwnd)-eq $normalRect -and (Rpc 'session.metrics' @{} $session|ConvertTo-Json -Compress)-eq $normalMetrics)
}
foreach($bad in '39','91','NaN'){
    $r=Rpc 'config.set' @{key='quick-terminal-size';value=$bad} -AllowError
    Check "invalid size $bad refused with prior value retained" (-not $r.ok -and (Rpc 'config.get' @{key='quick-terminal-size'})-eq '90')
}
[void][HudOwnedJob]::SendMessageW($q,6,[IntPtr]::Zero,[IntPtr]::Zero) # logical blur, no foreground change
Check 'control show remains pinned across logical blur' ([QuickProbe]::IsWindowVisible($q))
$null=Rpc 'quick' @{op='off'}
for($i=0;$i-lt 50 -and (Rpc 'window.state').quickTerminalVisible;$i++){Start-Sleep -Milliseconds 50}
Check 'hide keeps quick host but removes it from screen' (-not [QuickProbe]::IsWindowVisible($q) -and [QuickProbe]::Find($job.Pid)-eq $q)
$null=Rpc 'quick' @{op='on'}
$sink=Join-Path $artifact 'quick-survived.txt'
$null=Rpc 'session.type' @{text="echo %P14_KEEP%>`"$sink`"`r"} -Window quick
for($i=0;$i-lt 50 -and -not (Test-Path $sink);$i++){Start-Sleep -Milliseconds 100}
Check 'same shell and environment survive hide/show' ((Test-Path $sink) -and (Get-Content $sink -Raw).Trim()-eq 'alive')
Check 'explicit quick prefix routes readback from library window' ((Rpc 'session.text' @{} 'quick:')-ceq (Rpc 'session.text' -Window quick))
Check 'quick does not enter library list' ((Rpc 'window.list'|ConvertTo-Json -Compress)-eq $library)
foreach($verb in 'session.new','workspace.new','session.split','restore.capture','dashboard'){
    Check "$verb cannot create hidden tree in quick host" (-not (Rpc $verb -Window quick -AllowError).ok)
}
Check 'unknown quick operation refuses' (-not (Rpc 'quick' @{op='bogus'} -AllowError).ok)
foreach($bad in 'win+a','ctrl+f12','shift+x','q'){
    Check "unsafe hotkey $bad refuses" (-not (Rpc 'config.set' @{key='quick-terminal-hotkey';value=$bad} -AllowError).ok)
}
# Reserve a private, uncommon chord on this runner's thread; a conflicting app registration
# must fail. The runner never sends its keystroke. Always release the exact registration.
$reserved=[QuickProbe]::RegisterHotKey([IntPtr]::Zero,0x614,0x4007,0x79) # Ctrl+Alt+Shift+F10
if(-not $reserved){throw 'Cannot reserve private conflict-test hotkey'}
try {
    $null=Rpc 'config.set' @{key='quick-terminal-hotkey';value='ctrl+alt+shift+f9'}
    $r=Rpc 'config.set' @{key='quick-terminal-hotkey';value='ctrl+alt+shift+f10'} -AllowError
    Check 'hotkey conflict refuses and keeps old config' (-not $r.ok -and (Rpc 'config.get' @{key='quick-terminal-hotkey'})-eq 'ctrl+alt+shift+f9')
    $probe=[QuickProbe]::RegisterHotKey([IntPtr]::Zero,0x615,0x4007,0x78)
    if($probe){[void][QuickProbe]::UnregisterHotKey([IntPtr]::Zero,0x615)}
    Check 'old hotkey remains registered after replacement conflict' (-not $probe)
    if($env:CI-eq 'true' -and -not (Test-Path $hub)){
        $null=Rpc 'quick' @{op='off'}
        [void][QuickProbe]::SetForegroundWindow($hwnd)
        [QuickProbe]::Hotkey()
        for($i=0;$i-lt 50 -and -not (Rpc 'window.state').quickTerminalVisible;$i++){Start-Sleep -Milliseconds 100}
        Check 'disposable CI real global chord summons and focuses panel' ((Rpc 'window.state').quickTerminalVisible -and [HudOwnedJob]::GetForegroundWindow()-eq $q)
        [void][QuickProbe]::SetForegroundWindow($hwnd)
        for($i=0;$i-lt 50 -and (Rpc 'window.state').quickTerminalVisible;$i++){Start-Sleep -Milliseconds 100}
        Check 'human panel dismisses on real focus loss' (-not (Rpc 'window.state').quickTerminalVisible)
    }else{'CI-only: real global-key injection / foreground check not run on shared desktop.'}
    $null=Rpc 'config.set' @{key='quick-terminal-hotkey';value=''}
    $probe=[QuickProbe]::RegisterHotKey([IntPtr]::Zero,0x615,0x4007,0x78)
    try { Check 'disabling hotkey releases OS reservation' $probe }
    finally { if($probe -and -not [QuickProbe]::UnregisterHotKey([IntPtr]::Zero,0x615)){$cleanup=$false;throw 'Cannot release probe hotkey'} }
} finally { if(-not [QuickProbe]::UnregisterHotKey([IntPtr]::Zero,0x614)){$cleanup=$false;throw 'Cannot release conflict-test hotkey'} }
[void][HudOwnedJob]::SendMessageW($q,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
Check 'quick window close hides instead of destroying shell' (-not [QuickProbe]::IsWindowVisible($q) -and (Rpc 'session.text' -Window quick -AllowError).ok)
$null=Rpc 'quick' @{op='on'}
$null=Rpc 'session.type' @{text="exit`r"} -Window quick
for($i=0;$i-lt 50 -and (Rpc 'window.state').quickTerminalVisible;$i++){Start-Sleep -Milliseconds 100}
Check 'shell exit dismisses and discards quick surface' (-not (Rpc 'window.state').quickTerminalVisible -and -not (Rpc 'session.text' -Window quick -AllowError).ok)
$null=Rpc 'quick' @{op='on'}
for($i=0;$i-lt 50 -and -not (Rpc 'session.text' -Window quick -AllowError).ok;$i++){Start-Sleep -Milliseconds 100}
Check 'summon after exit creates fresh shell' ((Rpc 'session.text' -Window quick -AllowError).ok)
