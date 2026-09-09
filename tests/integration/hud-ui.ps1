# Dedicated P13 fixture: private app-data, no clipboard/registry writes, exact owned job teardown.
param([string]$Exe="$PSScriptRoot/../../src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe",
      [string]$TokenOwner=$env:AGWINTERM_TEST_OWNER,[switch]$Strict,
      [ValidateSet('Hud','Quick')][string]$Suite='Hud')
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$run=$Suite.ToLowerInvariant()+'-ui-'+(Get-Date -Format yyyyMMddTHHmmss)+'-'+[guid]::NewGuid().ToString('N').Substring(0,6)
$artifact=Join-Path $root ".revmux/$run"
New-Item -ItemType Directory $artifact|Out-Null
Start-Transcript (Join-Path $artifact 'transcript.log')|Out-Null
$hub='C:/Users/boris/AI/bin/suite-token.py';$lease=$null;$job=$null;$cleanup=$true;$checks=0;$failed=0
function Check([string]$name,[bool]$ok,[string]$detail='') {
    $script:checks++;if($ok){"PASS $name"}else{$script:failed++;"FAIL $name : $detail"}
}
try {
    if(Test-Path $hub){
        if([string]::IsNullOrWhiteSpace($TokenOwner)){throw 'Local HUD tests require -TokenOwner'}
        $raw=& python $hub acquire --owner $TokenOwner --run $run --worktree $root --holder-pid $PID --purpose "$Suite private UI acceptance"
        $state=$raw|ConvertFrom-Json
        if($LASTEXITCODE-ne 0 -or -not $state.ok){throw "Suite token unavailable: $raw"}
        $lease=$state
        $lease|ConvertTo-Json -Depth 5|Set-Content (Join-Path $artifact 'lease.json')
    }elseif($env:CI-ne 'true'){throw 'No canonical suite token helper on local desktop'}
    if(-not (Test-Path -LiteralPath $Exe)){throw "Build this worktree first: $Exe"}
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public sealed class HudOwnedJob {
    [StructLayout(LayoutKind.Sequential)] struct IO { public ulong a,b,c,d,e,f; }
    [StructLayout(LayoutKind.Sequential)] struct BASIC { public long a,b; public uint flags; public UIntPtr min,max; public uint active; public UIntPtr affinity; public uint priority,schedule; }
    [StructLayout(LayoutKind.Sequential)] struct LIMIT { public BASIC basic; public IO io; public UIntPtr a,b,c,d; }
    [StructLayout(LayoutKind.Sequential)] struct ACCOUNT { public long a,b,c,d; public uint faults,total,active,terminated; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct STARTUP {
        public int cb; public string reserved,desktop,title; public int x,y,w,h,xc,yc,fill,flags;
        public short show,reserved2; public IntPtr reservedPtr,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS { public IntPtr process,thread; public uint pid,tid; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left,top,right,bottom; }
    [StructLayout(LayoutKind.Sequential)] struct GUI { public int size,flags; public IntPtr active,focus,capture,menu,move,caret; public RECT rect; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateJobObjectW(IntPtr a,string n);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(IntPtr j,int c,ref LIMIT l,int n);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr j,int c,out ACCOUNT a,int n,IntPtr r);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcessW(string app,StringBuilder cmd,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string cwd,ref STARTUP si,out PROCESS pi);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr j,IntPtr p);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr t);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr p,uint e);
    [DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr j,uint e);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h,uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h,uint m,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h,uint m,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h,out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint f);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint t,ref GUI info);
    public static IntPtr InputWindow(IntPtr h,bool capture) {
        uint pid;uint t=GetWindowThreadProcessId(h,out pid);var info=new GUI {size=Marshal.SizeOf<GUI>()};
        if(!GetGUIThreadInfo(t,ref info))throw new Exception("GetGUIThreadInfo");return capture?info.capture:info.focus;
    }
    IntPtr job,process; bool assigned; public uint Pid { get; private set; }
    public void Start(string exe,string args,string cwd) {
        job=CreateJobObjectW(IntPtr.Zero,null); if(job==IntPtr.Zero)throw new Exception("CreateJobObject");
        var limit=new LIMIT(); limit.basic.flags=0x2000; // kill-on-close, no breakaway permission
        if(!SetInformationJobObject(job,9,ref limit,Marshal.SizeOf<LIMIT>()))throw new Exception("Job limits");
        var si=new STARTUP { cb=Marshal.SizeOf<STARTUP>(),flags=1,show=4 }; // shown without activation
        PROCESS pi;
        if(!CreateProcessW(exe,new StringBuilder("\""+exe+"\" "+args),IntPtr.Zero,IntPtr.Zero,false,4,IntPtr.Zero,cwd,ref si,out pi))
            throw new Exception("CreateProcess: "+Marshal.GetLastWin32Error());
        process=pi.process; Pid=pi.pid;
        try {
            if(!AssignProcessToJobObject(job,process))throw new Exception("AssignProcessToJobObject");
            assigned=true;
            if(ResumeThread(pi.thread)==uint.MaxValue)throw new Exception("ResumeThread");
        } finally { CloseHandle(pi.thread); }
    }
    public uint Count() {
        ACCOUNT a; if(!QueryInformationJobObject(job,1,out a,Marshal.SizeOf<ACCOUNT>(),IntPtr.Zero))throw new Exception("Job accounting");
        return a.active;
    }
    public void Finish() {
        if(job==IntPtr.Zero)return;
        if(process!=IntPtr.Zero && WaitForSingleObject(process,5000)!=0) {
            if(assigned)TerminateJobObject(job,1);else TerminateProcess(process,1);
            if(WaitForSingleObject(process,10000)!=0)throw new Exception("Owned primary process still live; retain suite token");
        }
        for(int i=0;i<100 && Count()!=0;i++)Thread.Sleep(100);
        if(Count()!=0) { TerminateJobObject(job,1); for(int i=0;i<100 && Count()!=0;i++)Thread.Sleep(100); }
        if(Count()!=0)throw new Exception("Owned job still contains live processes; retain suite token");
        if(process!=IntPtr.Zero){CloseHandle(process);process=IntPtr.Zero;}
        CloseHandle(job);job=IntPtr.Zero;
    }
    public static int[] Bounds(int[] pixels,int width,int color) {
        int left=width,top=pixels.Length/width,right=-1,bottom=-1,count=0;
        for(int i=0;i<pixels.Length;i++)if((pixels[i]&0xffffff)==color){int x=i%width,y=i/width;left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);count++;}
        return new[]{left,top,right,bottom,count};
    }
}
'@
    $pipe='p13-'+[guid]::NewGuid().ToString('N')
    $appId='agwinterm-'+$run
    $appDir=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $appId
    if(Test-Path -LiteralPath $appDir){throw 'Private app directory already exists'}
    New-Item -ItemType Directory $appDir|Out-Null
    # Every pane uses a harmless cmd /d shell: never the user's PowerShell profile or agent hooks.
    @{default='HUD-test';profiles=@(@{name='HUD-test';command='cmd.exe';args=@('/d');cwd=$artifact})}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $appDir 'profiles.json')
    @('session-host = in-process','claude-update-check = false','update-check = false','fresh-env = false','copy-on-select = false')|Set-Content (Join-Path $appDir 'agwinterm.conf')
    'map f12 = close_pane'|Set-Content (Join-Path $appDir 'keymap.conf')
    $savedEnv=@{}
    foreach($name in 'AGWINTERM_APP_ID','AGWINTERM_PIPE','AGWINTERM_SESSION_ID','AGWINTERM_PANE_ID','AGWINTERM_DUMP','AGWINTERM_PERF','AGWINTERM_IMGLOG'){
        $savedEnv[$name]=[Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name,$null)
    }
    $env:AGWINTERM_APP_ID=$appId+'-fallback'
    $job=[HudOwnedJob]::new()
    $job.Start([IO.Path]::GetFullPath($Exe),"--app-id $appId --pipe $pipe --no-restore",$root)
    function Rpc([string]$verb,$params=@{},[string]$target='active',[switch]$AllowError,[string]$Window='active'){
        $client=[IO.Pipes.NamedPipeClientStream]::new('.',$pipe,[IO.Pipes.PipeDirection]::InOut)
        try {
            $client.Connect(1500)
            $writer=[IO.StreamWriter]::new($client);$writer.AutoFlush=$true
            $reader=[IO.StreamReader]::new($client)
            $writer.WriteLine((@{cmd=$verb;args=$params;target=$target;window=$Window}|ConvertTo-Json -Compress -Depth 8))
            $read=$reader.ReadLineAsync();if(-not $read.Wait(15000)){throw 'HUD RPC deadline exceeded'}
            $answer=$read.Result|ConvertFrom-Json
            if($AllowError){return $answer}
            if(-not $answer.ok){throw "$verb : $($answer.error)"};return $answer.result
        }finally{$client.Dispose()}
    }
    $ready=$false
    for($i=0;$i-lt 60;$i++){try{if(@((Rpc 'tree').workspaces[0].sessions).Count-gt 0){$ready=$true;break}}catch{};Start-Sleep -Milliseconds 200}
    if(-not $ready){throw 'Private app did not answer'}
    if(Test-Path ($appDir+'-fallback')){throw 'App ignored --app-id'}
    $proc=[Diagnostics.Process]::GetProcessById($job.Pid);$hwnd=[IntPtr]::Zero
    for($i=0;$i-lt 50 -and $hwnd-eq [IntPtr]::Zero;$i++){$proc.Refresh();$hwnd=$proc.MainWindowHandle;if($hwnd-eq [IntPtr]::Zero){Start-Sleep -Milliseconds 100}}
    [uint32]$windowPid=0;[void][HudOwnedJob]::GetWindowThreadProcessId($hwnd,[ref]$windowPid)
    if($hwnd-eq [IntPtr]::Zero -or $windowPid-ne $job.Pid){throw 'No owned GUI handle'}
    $session=[string](Rpc 'tree').workspaces[0].sessions[0].id
    function Node([string]$id=$session){@((Rpc 'tree').workspaces|ForEach-Object sessions)|Where-Object id -eq $id|Select-Object -First 1}
    function Shot([string]$name){
        Start-Sleep -Milliseconds 160
        $r=[HudOwnedJob+RECT]::new();[void][HudOwnedJob]::GetClientRect($hwnd,[ref]$r)
        $bitmap=[Drawing.Bitmap]::new($r.right,$r.bottom)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        try {
            $dc=$graphics.GetHdc();try{if(-not [HudOwnedJob]::PrintWindow($hwnd,$dc,3)){throw 'PrintWindow failed'}}finally{$graphics.ReleaseHdc($dc)}
            $bitmap.Save((Join-Path $artifact "$name.png"))
            $rect=[Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height)
            $bits=$bitmap.LockBits($rect,[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $pixels=[int[]]::new($bitmap.Width*$bitmap.Height)
                [Runtime.InteropServices.Marshal]::Copy($bits.Scan0,$pixels,0,$pixels.Length)
                return ,([HudOwnedJob]::Bounds($pixels,$bitmap.Width,0x125a9f))
            }finally{$bitmap.UnlockBits($bits)}
        }finally{$graphics.Dispose();$bitmap.Dispose()}
    }
    if($Suite-eq 'Quick') { . "$PSScriptRoot/quick-ui-cases.ps1" } else {
    $ctl=Join-Path $root 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
    $null=& $ctl session hud --spinner 'CLI HUD' --detail 'private acceptance' --size-percent 35 --target $session --pipe $pipe --json
    Check 'CLI default open accepts spinner before message and numeric width' ($LASTEXITCODE-eq 0 -and (Node).hud.message-eq 'CLI HUD' -and (Node).hud.sizePercent-eq 35)
    foreach($selector in 'target','window'){
        $null=& $ctl session hud close "--$selector" '' --pipe $pipe --json
        Check "CLI empty $selector refuses without clearing HUD" ($LASTEXITCODE-eq 2 -and (Node).hud.message-eq 'CLI HUD')
    }
    $null=Rpc 'session.hud.close' @{} $session
    for($i=0;$i-lt 50 -and ([string](Rpc 'session.text' @{} $session))-notmatch '>'; $i++){Start-Sleep -Milliseconds 100}
    $text=Rpc 'session.text' @{all=$true} $session;$metrics=Rpc 'session.metrics' @{} $session|ConvertTo-Json -Compress
    $positions=@('top-left','top-center','top-right','center-left','center','center-right','bottom-left','bottom-center','bottom-right')
    $focusBefore=[HudOwnedJob]::InputWindow($hwnd,$false)
    $boxes=@()
    foreach($position in $positions){
        $null=Rpc 'session.hud.open' @{message='Building P13 — 中文';detail='HUD does not take keyboard focus';position=$position;color='#125a9f';'size-percent'=35} $session
        Check "readback anchor $position" ((Node).hud.position-eq $position)
        $b=Shot $position;$boxes+=,$b
        Check "painted anchor $position" ($b[4]-gt 500)
    }
    Check 'nine placements have three increasing horizontal anchors' ($boxes[0][0]-lt $boxes[1][0] -and $boxes[1][0]-lt $boxes[2][0])
    Check 'nine placements have three increasing vertical anchors' ($boxes[0][1]-lt $boxes[3][1] -and $boxes[3][1]-lt $boxes[6][1])
    Check 'HUD never enters terminal text' ((Rpc 'session.text' @{all=$true} $session)-ceq $text)
    Check 'HUD never resizes the terminal' ((Rpc 'session.metrics' @{} $session|ConvertTo-Json -Compress)-ceq $metrics)
    Check 'HUD leaves window input focus unchanged' ([HudOwnedJob]::InputWindow($hwnd,$false)-eq $focusBefore)
    $null=Rpc 'session.hud.update' @{message='Ready';'text-color'='#ffdd00'} $session
    Check 'update replaces defaults but retains background' ((Node).hud.position-eq 'center' -and (Node).hud.backgroundColor-eq '#125a9f' -and (Node).hud.detail-eq $null)
    $updatedBox=Shot 'updated'
    # Copy-on-select is disabled in this private instance; the drag never writes the clipboard.
    $mx=[int](($updatedBox[0]+$updatedBox[2])/2);$my=[int](($updatedBox[1]+$updatedBox[3])/2)
    $point=($my-shl 16)-bor $mx
    [void][HudOwnedJob]::SendMessageW($hwnd,0x201,[IntPtr]1,[IntPtr]$point)
    Check 'mouse press through HUD reaches terminal selection capture' ([HudOwnedJob]::InputWindow($hwnd,$true)-eq $hwnd)
    [void][HudOwnedJob]::SendMessageW($hwnd,0x202,[IntPtr]::Zero,[IntPtr]$point)
    $null=Rpc 'selection.clear' @{} $session
    $before=Node|ConvertTo-Json -Depth 6 -Compress
    foreach($bad in @(@{message='x';position='north'},@{message="bad`nline"},@{message='x';'size-percent'=101},@{message='x';spinner=$true})){
        $a=Rpc 'session.hud.update' $bad $session -AllowError
        Check 'invalid update refuses and preserves live spec' (-not $a.ok -and (Node|ConvertTo-Json -Depth 6 -Compress)-ceq $before)
    }
    $sink=Join-Path $artifact 'typed-through.txt'
    # ConPTY advertises win32-input mode. Pin this fixture to legacy char input before posting
    # printable WM_CHAR and an actual Enter WM_KEYDOWN (WM_CHAR deliberately ignores controls).
    $null=Rpc 'session.write' @{text=([string][char]27+'[?9001l')} $session
    foreach($c in ("echo WORKED>`"$sink`"").ToCharArray()){[void][HudOwnedJob]::SendMessageW($hwnd,0x102,[IntPtr][int]$c,[IntPtr]1)}
    [void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]13,[IntPtr]1)
    for($i=0;$i-lt 50 -and -not (Test-Path $sink);$i++){Start-Sleep -Milliseconds 100}
    Check 'posted human characters execute underneath HUD' ((Test-Path $sink) -and (Get-Content $sink -Raw).Trim()-eq 'WORKED')
    $split=[string](Rpc 'session.split' @{op='on'} $session)
    $a=Rpc 'session.hud.open' @{message='must refuse'} $split -AllowError
    Check 'split-pane target refuses rather than widening' (-not $a.ok -and (Node).hud.message-eq 'Ready')
    $null=Rpc 'session.hud.open' @{message='Whole split'} $session
    Check 'session id still addresses whole split HUD' ((Node).hud.message-eq 'Whole split')
    $paneOverlay=Rpc 'session.overlay' @{action='open';pane='left';command='cmd /d /k'} $session
    Check 'pane overlay coexists under session HUD' ((Node).hud.message-eq 'Whole split' -and (Node).paneOverlays.Count-eq 1)
    [void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]123,[IntPtr]1) # private F12 binding: ordinary close action
    Check 'ordinary close shortcut removes HUD but preserves panes and pane overlay' ($null-eq (Node).hud -and (Node).paneCount-eq 2 -and (Node).paneOverlays.Count-eq 1)
    $null=Rpc 'session.overlay' @{action='close';pane='left'} $session
    $other=[string](Rpc 'session.new' @{name='hud-background';'no-select'=$true})
    for($i=0;$i-lt 50 -and $null-eq (Node $other);$i++){Start-Sleep -Milliseconds 50}
    $null=Rpc 'session.hud.open' @{message='Background status';spinner='dot'} $other
    Check 'background HUD targets only named session without selecting it' ((Node).active -and -not (Node $other).active -and (Node $other).hud.message-eq 'Background status')
    $null=Rpc 'session.close' @{} $other
    $null=Rpc 'session.scratch' @{op='on'} $session
    $a=Rpc 'session.hud.open' @{message='wrong surface'} -AllowError
    Check 'active auxiliary cover refuses HUD' (-not $a.ok)
    $null=Rpc 'session.scratch' @{op='off'} $session
    $overlay=Rpc 'session.overlay' @{action='open';command='cmd /d /k echo HUD-OVERLAY'} $session
    Check 'program overlay replaces HUD' ($null-eq (Node).hud -and (Node).overlay)
    $a=Rpc 'session.hud.open' @{message='must not replace program'} $session -AllowError
    Check 'HUD cannot replace program overlay' (-not $a.ok -and (Node).overlay)
    $null=Rpc 'session.hud.close' @{} $session
    Check 'HUD close leaves program overlay alone' ([bool](Node).overlay)
    $null=Rpc 'session.overlay' @{action='close'} $session
    $null=Rpc 'session.hud.open' @{message='close through overlay'} $session
    $null=Rpc 'session.overlay' @{action='close'} $session
    for($i=0;$i-lt 50 -and $null-ne (Node).hud;$i++){Start-Sleep -Milliseconds 50}
    Check 'overlay close removes passive HUD' ($null-eq (Node).hud)
    $null=Rpc 'session.hud.close' @{} $session
    Check 'close absent HUD is idempotent' ($null-eq (Node).hud)
    $a=Rpc 'session.hud.close' @{} 'not-a-session' -AllowError
    Check 'unknown close target refuses' (-not $a.ok)
    $null=Rpc 'session.hud.open' @{message='transient';spinner='bar'} $session
    Check 'HUD creates no extra terminal panes' ((Node).paneCount-eq 2)
    $survivor=Rpc 'session.split.close' @{} $session
    foreach($action in 'open','update','close'){
        $hudArgs=if($action-eq 'close'){@{}}else{@{message='wrong'}}
        $answer=Rpc "session.hud.$action" $hudArgs $survivor -AllowError
        Check "$action refuses surviving secondary pane id" (-not $answer.ok -and (Node).hud.message-eq 'transient')
    }
    $null=Rpc 'session.hud.update' @{message='session identity survives'} $session
    Check 'owning session id remains usable without its original pane' ((Node).hud.message-eq 'session identity survives')
    $null=Rpc 'session.close' @{} $session
    for($i=0;$i-lt 50 -and $null-ne (Node);$i++){Start-Sleep -Milliseconds 50}
    Check 'session close removes HUD state' ($null-eq (Node))
    }
}catch{$failed++;"FAIL fixture: $($_.Exception.Message)"}
finally {
    if($job){
        try{
            if($hwnd){[void][HudOwnedJob]::PostMessageW($hwnd,0x10,[IntPtr]::Zero,[IntPtr]::Zero)}
            $job.Finish();'Cleanup: owned job has zero live processes; no name-based cleanup.'
        }catch{$cleanup=$false;"CLEANUP INCOMPLETE: $_"}
    }
    if($savedEnv){foreach($name in $savedEnv.Keys){[Environment]::SetEnvironmentVariable($name,$savedEnv[$name])}}
    if($cleanup){'Clipboard/registry untouched; only private app-data retained for evidence; no queued launches.'}
    if($lease -and $cleanup){
        $raw=& python $hub release --owner $lease.owner --token $lease.token --cleanup-confirmed
        $raw|Set-Content (Join-Path $artifact 'release.json');$raw
        if($LASTEXITCODE-ne 0){$cleanup=$false}
    }
    "$Suite-ui: $checks checks, $failed failed; cleanup complete: $cleanup; artifacts $artifact"
    Stop-Transcript|Out-Null
}
if(-not $cleanup){exit 2};if($failed -or ($Strict -and $checks-eq 0)){exit 1};exit 0
