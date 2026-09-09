# Dot-sourced by the canonical-token, private-data, owned-job fixture. No clipboard or registry writes.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PickerProbe {
    delegate bool EnumProc(IntPtr h,IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p,IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h,uint c);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h,System.Text.StringBuilder b,int n);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h,int id);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern bool SetWindowTextW(IntPtr h,string t);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr h,uint m,IntPtr w,string t);
    public static IntPtr Find(IntPtr owner) {
        IntPtr found=IntPtr.Zero;EnumWindows((h,p)=>{var b=new System.Text.StringBuilder(100);GetClassNameW(h,b,100);
            if(GetWindow(h,4)==owner && b.ToString()=="Agwinterm.NativePicker"){found=h;return false;}return true;},IntPtr.Zero);return found;
    }
}
'@
function Open-Pick($params=@{items=@(@{id='z';label='Zulu';subtitle='dangerous consequence'},@{id='a';label='Alpha'},@{id='b';label='Alpine'})}){
    $script:pickId=[string](Rpc 'pick.open' $params -NoTarget).id
    $script:picker=[PickerProbe]::Find($hwnd)
    [uint32]$pidCheck=0;[void][HudOwnedJob]::GetWindowThreadProcessId($picker,[ref]$pidCheck)
    if($picker-eq [IntPtr]::Zero -or $pidCheck-ne $job.Pid){throw 'No owned picker HWND'}
    $script:edit=[PickerProbe]::GetDlgItem($picker,101);$script:list=[PickerProbe]::GetDlgItem($picker,102)
    return $pickId
}
function Pick-Result([string]$id=$pickId){(Rpc 'pick.result' @{} $id -Window '').pick}
function Pick-Key([int]$key,[IntPtr]$control=$edit){[void][HudOwnedJob]::SendMessageW($control,0x100,[IntPtr]$key,[IntPtr]::Zero)}
function Pick-Query([string]$query){
    $set=[PickerProbe]::SendMessageW($edit,0x0c,[IntPtr]::Zero,$query)
    if($set-eq [IntPtr]::Zero){throw "Cannot set picker query (HWND $edit, error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))"}
}
function Pick-Count(){[int][HudOwnedJob]::SendMessageW($list,0x18b,[IntPtr]::Zero,[IntPtr]::Zero)}
for($i=0;$i-lt 50 -and ([string](Rpc 'session.text' @{} $session))-notmatch '>'; $i++){Start-Sleep -Milliseconds 100}
$text=Rpc 'session.text' @{all=$true} $session
$metrics=Rpc 'session.metrics' @{} $session|ConvertTo-Json -Compress
$front=[HudOwnedJob]::GetForegroundWindow()
$null=Open-Pick
Check 'open is pending and has native controls' ((Pick-Result).result-eq 'pending' -and $edit-ne [IntPtr]::Zero -and (Pick-Count)-eq 3)
Check 'default open never steals foreground' ([HudOwnedJob]::GetForegroundWindow()-eq $front)
$busy=Rpc 'pick.open' @{items=@();allowCustom=$true} -NoTarget -AllowError
Check 'second pending picker refuses without replacing first' (-not $busy.ok -and (Pick-Result).result-eq 'pending')
$settings=Rpc 'settings.open' @{} -AllowError
Check 'settings refuses while picker owns input' (-not $settings.ok)
Pick-Query 'dangerous';Check 'subtitle consequences do not match' ((Pick-Count)-eq 0)
Pick-Query 'al';Check 'query filters labels' ((Pick-Count)-eq 2)
Pick-Key 0x28;Pick-Key 0x28
Check 'down clamps to last filtered item' ([int][HudOwnedJob]::SendMessageW($list,0x188,[IntPtr]::Zero,[IntPtr]::Zero)-eq 1)
# Capture the actual native controls while the picker is open.
$originalHwnd=$hwnd;try{$hwnd=$picker;$null=Shot 'native-picker'}finally{$hwnd=$originalHwnd}
Pick-Key 0x0d
$picked=Pick-Result
Check 'Enter returns original item id label and index' ($picked.result-eq 'picked' -and $picked.id-eq 'b' -and $picked.label-eq 'Alpine' -and $picked.index-eq 2)
$null=Rpc 'pick.cancel' @{} $pickId -Window ''
Check 'cancel after choose preserves immutable answer' ((Pick-Result).result-eq 'picked')
Check 'native window destroyed after answer' ([PickerProbe]::Find($hwnd)-eq [IntPtr]::Zero)
$null=Open-Pick @{items=@(@{id='';label='中文 🦊'});allowCustom=$true;query='  custom value  ';prompt='Select — Ctrl+C stays in the edit'}
Check 'unmatched custom query creates one row' ((Pick-Count)-eq 1)
[void][HudOwnedJob]::SendMessageW([PickerProbe]::GetDlgItem($picker,1),0xf5,[IntPtr]::Zero,[IntPtr]::Zero)
Check 'button selects trimmed custom query' ((Pick-Result).result-eq 'custom' -and (Pick-Result).query-ceq 'custom value')
$null=Open-Pick @{items=@(@{id='';label='中文 🦊'})}
Pick-Key 0x0d
Check 'Unicode and empty item id round trip' ((Pick-Result).id-ceq '' -and (Pick-Result).label-ceq '中文 🦊')
$null=Open-Pick;Pick-Key 0x1b
Check 'Escape cancels' ((Pick-Result).result-eq 'cancelled')
$null=Open-Pick
[void][HudOwnedJob]::SendMessageW([PickerProbe]::GetDlgItem($picker,2),0xf5,[IntPtr]::Zero,[IntPtr]::Zero)
Check 'Cancel button cancels' ((Pick-Result).result-eq 'cancelled')
$null=Open-Pick
$cancelFront=[HudOwnedJob]::GetForegroundWindow()
$null=Rpc 'pick.cancel' @{} $pickId -Window ''
Check 'API cancel resolves exact id' ((Pick-Result).result-eq 'cancelled')
Check 'background API cancellation leaves foreground unchanged' ([HudOwnedJob]::GetForegroundWindow()-eq $cancelFront)
$bad=Rpc 'pick.result' @{} 'active' -AllowError -Window ''
Check 'active is not a picker-id alias' (-not $bad.ok)
Check 'picker input never enters terminal text' ((Rpc 'session.text' @{all=$true} $session)-ceq $text)
Check 'picker never resizes terminal' ((Rpc 'session.metrics' @{} $session|ConvertTo-Json -Compress)-ceq $metrics)
$null=Rpc 'session.readonly' @{op='on'} $session
try{$null=Open-Pick;Pick-Key 0x0d;Check 'readonly terminal does not block independent picker' ((Pick-Result).result-eq 'picked')}
finally{$null=Rpc 'session.readonly' @{op='off'} $session}
$quick=Rpc 'pick.open' @{items=@();allowCustom=$true} -NoTarget -Window 'quick' -AllowError
Check 'quick is not a picker owner' (-not $quick.ok)
$ctl=Join-Path $root 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$opened='Alpha'|& $ctl pick --no-block --pipe $pipe|ConvertFrom-Json
Check 'real CLI stdin no-block returns id payload' ($LASTEXITCODE-eq 0 -and $opened.id)
$reply=& $ctl pick result $opened.id --pipe $pipe|ConvertFrom-Json
Check 'real CLI pending exits one with payload' ($LASTEXITCODE-eq 1 -and $reply.result-eq 'pending')
$null=& $ctl pick cancel $opened.id --pipe $pipe
$reply=& $ctl pick result $opened.id --pipe $pipe|ConvertFrom-Json
Check 'real CLI cancelled exits two with payload' ($LASTEXITCODE-eq 2 -and $reply.result-eq 'cancelled')
if($env:CI-eq 'true' -and -not (Test-Path $hub)){
    $null=Open-Pick;Pick-Key 0x09
    Check 'Tab stays in native control family' ([HudOwnedJob]::InputWindow($hwnd,$false)-ne $hwnd)
    $null=Rpc 'pick.cancel' @{} $pickId -Window ''
    $ownerId=[string]((Rpc 'window.list').windows|Where-Object active|Select-Object -First 1).id
    $null=Open-Pick;$closingPick=$pickId
    $other=Rpc 'window.new' @{name='Picker second owner'} -NoTarget
    for($i=0;$i-lt 50 -and @((Rpc 'window.list').windows|Where-Object {$_.id-eq $other -and $_.active}).Count-eq 0;$i++){Start-Sleep -Milliseconds 100}
    Check 'global result survives frontmost change' ((Pick-Result $closingPick).result-eq 'pending')
    $wrong=Rpc 'pick.result' @{} $closingPick -AllowError -Window 'active'
    Check 'explicit wrong owner refuses' (-not $wrong.ok)
    $null=Rpc 'window.close' @{} $ownerId -Window ''
    for($i=0;$i-lt 50 -and (Pick-Result $closingPick).result-eq 'pending';$i++){Start-Sleep -Milliseconds 100}
    Check 'closing owner cancels and retains global result' ((Pick-Result $closingPick).result-eq 'cancelled')
}
