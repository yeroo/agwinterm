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
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
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
$beforeFont=Rpc 'session.metrics' -Window quick
$null=Rpc 'font' @{op='inc'} -Window quick
$afterFont=Rpc 'session.metrics' -Window quick
Check 'font inc addresses active quick surface' ($afterFont.cellHeight-gt $beforeFont.cellHeight)
$null=Rpc 'font' @{op='reset'} -Window quick
Check 'font reset restores quick cell metrics' ((Rpc 'session.metrics' -Window quick).cellHeight-eq $beforeFont.cellHeight)
$null=Rpc 'session.type' @{text="echo P14-FIND-MARKER`r"} -Window quick
for($i=0;$i-lt 50 -and ([string](Rpc 'session.text' -Window quick))-notmatch '(?m)^P14-FIND-MARKER\s*$';$i++){Start-Sleep -Milliseconds 100}
Check 'quick search reaches its active surface' ((Rpc 'session.search' @{query='P14-FIND-MARKER'} -Window quick)-match 'of [1-9]')
$null=Rpc 'session.search' @{query='NO-MATCH-OLD-QUERY'} -Window quick
$null=Rpc 'session.search' @{action='close'} -Window quick
[void][HudOwnedJob]::SendMessageW($q,0x100,[IntPtr]118,[IntPtr]1) # F7 mapped to toggle_search
foreach($c in 'P14-FIND-MARKER'.ToCharArray()){[void][HudOwnedJob]::SendMessageW($q,0x102,[IntPtr][int]$c,[IntPtr]1)}
$keySearch=Rpc 'session.search' @{action='state'} -Window quick
Check 'surface-local search keymap action accepts its query' ($keySearch-match 'of [1-9]') ([string]$keySearch)
$null=Rpc 'session.search' @{action='close'} -Window quick
[void][HudOwnedJob]::SendMessageW($q,0x100,[IntPtr]119,[IntPtr]1) # F8 mapped to custom send
$keymapSink=Join-Path $artifact 'keymap-send.txt'
for($i=0;$i-lt 50 -and -not (Test-Path $keymapSink);$i++){Start-Sleep -Milliseconds 100}
Check 'surface-local custom keymap command runs in quick' ((Test-Path $keymapSink) -and (Get-Content $keymapSink -Raw).Trim()-eq 'quick')
$commandSink=Join-Path $artifact 'quick-command.txt'
$null=Rpc 'command.run' @{name="echo {AGW_PANE}>`"$commandSink`"";mode='send'} -Window quick
for($i=0;$i-lt 50 -and -not (Test-Path $commandSink);$i++){Start-Sleep -Milliseconds 100}
Check 'surface-local send command retains quick context' ((Test-Path $commandSink) -and (Get-Content $commandSink -Raw).Trim()-eq 'quick')
Check 'quick refuses new-session custom command mode' (-not (Rpc 'command.run' @{name='echo forbidden';mode='new'} -Window quick -AllowError).ok)
# Posted logical DPI transition: no monitor setting is changed and no foreground is taken.
$realDpi=[QuickProbe]::GetDpiForWindow($q);$beforeDpi=[QuickProbe]::Rect($q)
$suggested=[QuickProbe+RECT]::new();$suggested.left=5;$suggested.top=7;$suggested.right=405;$suggested.bottom=307
$rectMemory=[Runtime.InteropServices.Marshal]::AllocHGlobal([Runtime.InteropServices.Marshal]::SizeOf($suggested))
try {
    [Runtime.InteropServices.Marshal]::StructureToPtr($suggested,$rectMemory,$false)
    [void][HudOwnedJob]::SendMessageW($q,0x2e0,[IntPtr]((144-shl 16)-bor 144),$rectMemory)
    Check 'DPI transition cannot override physical percentage frame' ([QuickProbe]::Rect($q)-eq $beforeDpi)
    [void][HudOwnedJob]::SendMessageW($q,0x2e0,[IntPtr](($realDpi-shl 16)-bor $realDpi),$rectMemory)
}finally{[Runtime.InteropServices.Marshal]::FreeHGlobal($rectMemory)}
$null=Rpc 'session.write' @{text=([string][char]27+'[6;9H')} -Window quick
[void][HudOwnedJob]::SendMessageW($q,7,[IntPtr]::Zero,[IntPtr]::Zero) # logical focus: owns only this process's caret
[void][HudOwnedJob]::SendMessageW($q,0xf,[IntPtr]::Zero,[IntPtr]::Zero)
$caret=[HudOwnedJob]::CaretRect($q)
Check 'quick system caret follows nonzero terminal cursor' ($caret.left-gt 20 -and $caret.top-gt 40)
[void][HudOwnedJob]::SendMessageW($q,8,[IntPtr]::Zero,[IntPtr]::Zero)
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
    if($probe -and -not [QuickProbe]::UnregisterHotKey([IntPtr]::Zero,0x615)){$cleanup=$false;throw 'Cannot release unexpected old-chord probe'}
    Check 'old hotkey remains registered after replacement conflict' (-not $probe)
    $configFile=Join-Path $appDir 'agwinterm.conf'
    $beforeConfig=[IO.File]::ReadAllText($configFile)
    $locked=[IO.File]::Open($configFile,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $r=Rpc 'config.set' @{key='quick-terminal-hotkey';value='ctrl+alt+shift+f8'} -AllowError
        Check 'failed save is an error and preserves complete config bytes' (-not $r.ok -and [IO.File]::ReadAllText($configFile)-ceq $beforeConfig)
        $probe=[QuickProbe]::RegisterHotKey([IntPtr]::Zero,0x615,0x4007,0x77)
        try { Check 'failed save releases provisional new chord' $probe }
        finally {if($probe -and -not [QuickProbe]::UnregisterHotKey([IntPtr]::Zero,0x615)){$cleanup=$false;throw 'Cannot release provisional-chord probe'}}
        $probe=[QuickProbe]::RegisterHotKey([IntPtr]::Zero,0x615,0x4007,0x78)
        if($probe -and -not [QuickProbe]::UnregisterHotKey([IntPtr]::Zero,0x615)){$cleanup=$false;throw 'Cannot release retained-chord probe'}
        Check 'failed save never releases old chord' (-not $probe)
        Check 'size write failure is not a successful empty reply' (-not (Rpc 'config.set' @{key='quick-terminal-size';value='50'} -AllowError).ok)
    }finally{$locked.Dispose()}
    if($env:CI-eq 'true' -and -not (Test-Path $hub)){
        $null=Rpc 'quick' @{op='off'}
        [void][QuickProbe]::SetForegroundWindow($hwnd)
        [QuickProbe]::Hotkey()
        for($i=0;$i-lt 50 -and -not (Rpc 'window.state').quickTerminalVisible;$i++){Start-Sleep -Milliseconds 100}
        Check 'disposable CI real global chord summons and focuses panel' ((Rpc 'window.state').quickTerminalVisible -and [HudOwnedJob]::GetForegroundWindow()-eq $q)
        [void][QuickProbe]::SetForegroundWindow($hwnd)
        for($i=0;$i-lt 50 -and (Rpc 'window.state').quickTerminalVisible;$i++){Start-Sleep -Milliseconds 100}
        Check 'human panel dismisses on real focus loss' (-not (Rpc 'window.state').quickTerminalVisible)
        $second=[string](Rpc 'window.new' @{name='P14-second'})
        for($i=0;$i-lt 50 -and @((Rpc 'window.list').windows|Where-Object {$_.id-eq $second -and $_.open}).Count-eq 0;$i++){Start-Sleep -Milliseconds 100}
        $null=Rpc 'quick' @{op='on'} -Window $second
        Check 'second library window shares singleton quick host and visibility' ([QuickProbe]::Find($job.Pid)-eq $q -and (Rpc 'window.state' -Window $second).quickTerminalVisible)
        $null=Rpc 'window.close' @{} $second
        for($i=0;$i-lt 50 -and @((Rpc 'window.list').windows|Where-Object {$_.id-eq $second -and $_.open}).Count-gt 0;$i++){Start-Sleep -Milliseconds 100}
        Check 'closing one of two library windows preserves quick shell' ((Rpc 'window.state').quickTerminalVisible -and (Rpc 'session.text' -Window quick -AllowError).ok)
    }else{'CI-only: real global-key injection / foreground check not run on shared desktop.'}
    $null=Rpc 'config.set' @{key='quick-terminal-hotkey';value=''}
    $probe=[QuickProbe]::RegisterHotKey([IntPtr]::Zero,0x615,0x4007,0x78)
    try { Check 'disabling hotkey releases OS reservation' $probe }
    finally { if($probe -and -not [QuickProbe]::UnregisterHotKey([IntPtr]::Zero,0x615)){$cleanup=$false;throw 'Cannot release probe hotkey'} }
} finally { if(-not [QuickProbe]::UnregisterHotKey([IntPtr]::Zero,0x614)){$cleanup=$false;throw 'Cannot release conflict-test hotkey'} }
# Simulate an invalid desired startup chord with no actual reservation, then fail its replacement.
$beforeConfig=[IO.File]::ReadAllText($configFile)
try {
    [IO.File]::AppendAllText($configFile,"`nquick-terminal-hotkey = invalid`n")
    $null=Rpc 'config.set' @{key='quick-terminal-size';value='90'} # reload desired config, not OS binding
    $locked=[IO.File]::Open($configFile,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $r=Rpc 'config.set' @{key='quick-terminal-hotkey';value='ctrl+alt+shift+f8'} -AllowError
        $probe=[QuickProbe]::RegisterHotKey([IntPtr]::Zero,0x615,0x4007,0x77)
        try { Check 'invalid old configured chord cannot strand replacement on save failure' (-not $r.ok -and $probe) }
        finally {if($probe -and -not [QuickProbe]::UnregisterHotKey([IntPtr]::Zero,0x615)){$cleanup=$false;throw 'Cannot release invalid-config probe'}}
    }finally{$locked.Dispose()}
}finally{[IO.File]::WriteAllText($configFile,$beforeConfig);$null=Rpc 'config.set' @{key='quick-terminal-size';value='90'}}
[void][HudOwnedJob]::SendMessageW($q,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
Check 'quick window close hides instead of destroying shell' (-not [QuickProbe]::IsWindowVisible($q) -and (Rpc 'session.text' -Window quick -AllowError).ok)
$null=Rpc 'quick' @{op='on'}
$null=Rpc 'session.type' @{text="exit`r"} -Window quick
for($i=0;$i-lt 50 -and (Rpc 'window.state').quickTerminalVisible;$i++){Start-Sleep -Milliseconds 100}
Check 'shell exit dismisses and discards quick surface' (-not (Rpc 'window.state').quickTerminalVisible -and -not (Rpc 'session.text' -Window quick -AllowError).ok)
$null=Rpc 'quick' @{op='on'}
for($i=0;$i-lt 50 -and -not (Rpc 'session.text' -Window quick -AllowError).ok;$i++){Start-Sleep -Milliseconds 100}
Check 'summon after exit creates fresh shell' ((Rpc 'session.text' -Window quick -AllowError).ok)
$null=Rpc 'config.set' @{key='quick-terminal-hotkey';value='ctrl+alt+shift+f9'}
$verifyQuickShutdown=$true # parent verifies release only AFTER owned-job teardown, before token release
