# Actual UI Automation clients, only for HWNDs belonging to hud-ui's owned app/job.
# This is provider integration, not a claim that Narrator/NVDA presentation was manually tested.
Add-Type -Path "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/WPF/UIAutomationClient.dll"
Add-Type -Path "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/WPF/UIAutomationTypes.dll"
Add-Type -TypeDefinition @'
using System;using System.Collections.Generic;using System.Runtime.InteropServices;using System.Text;
public static class NavUiaWindows {
 delegate bool Visitor(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumWindows(Visitor f,IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h,StringBuilder b,int n);
 public static IntPtr[] Owned(uint pid,bool quick=false){var found=new List<IntPtr>();EnumWindows((h,p)=>{uint n;GetWindowThreadProcessId(h,out n);if(n==pid){var cls=new StringBuilder(256);GetClassNameW(h,cls,256);if(cls.ToString()!="AgwintermWin32")return true;var text=new StringBuilder(256);GetWindowTextW(h,text,256);if((text.ToString()=="agwinterm quick terminal")==quick)found.Add(h);}return true;},IntPtr.Zero);return found.ToArray();}
}
'@
function UiaDoc([IntPtr]$handle){
    $root=[System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Document)
    $document=$root.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)
    if($null-eq $document){throw 'Owned window has no UIA document'}
    return @{root=$root;document=$document;range=($document.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)).DocumentRange}
}
$uiaWindows=@();$uiaHandles=@();$uiaSessions=@()
try {
    foreach($label in 'A','B'){
        $beforeHandles=@([NavUiaWindows]::Owned($job.Pid))
        $window=[string](Rpc 'window.new' @{name="UIA-owned-$label"} -Window $libraryWindow)
        $uiaWindows+=,$window
        if(-not (NavWait {@((Rpc 'window.list').windows|Where-Object {$_.id-eq $window -and $_.open}).Count-eq 1})){throw 'UIA peer did not open'}
        $handles=@([NavUiaWindows]::Owned($job.Pid)|Where-Object {$_-notin $beforeHandles})
        if($handles.Count-ne 1){throw 'Cannot identify exactly one new owned UIA HWND'}
        $uiaHandles+=,$handles[0]
        $sid=[string](Rpc 'tree' -Window $window).workspaces[0].sessions[0].id;$uiaSessions+=,$sid
        if(-not (NavWait {@((Rpc 'tree' -Window $window).workspaces[0].sessions)[0].foregroundShell-eq 'cmd'})){throw 'UIA shell not ready'}
        $null=Rpc 'session.write' @{text=($esc+'[2J'+$esc+'[H'+"UIA-ONLY-$label")} $sid -Window $window
    }
    $null=Rpc 'quick' @{op='on'} -Window $libraryWindow
    $q=@([NavUiaWindows]::Owned($job.Pid,$true))
    if($q.Count-ne 1){throw 'No unique owned quick HWND'}
    if(-not (NavWait {([string](Rpc 'session.text' -Window quick)).Contains('>')})){throw 'Quick shell did not finish startup'}
    $null=Rpc 'session.write' @{text=($esc+'[2J'+$esc+'[H'+'UIA-ONLY-QUICK')} -Window quick
    if(-not (NavWait {([string](Rpc 'session.text' -Window quick)).Contains('UIA-ONLY-QUICK')})){throw 'Quick marker did not reach its owned pane'}
    $a=UiaDoc $uiaHandles[0];$b=UiaDoc $uiaHandles[1];$quickDoc=UiaDoc $q[0]
    Check 'two library UIA roots and quick expose separate document text' ($a.range.GetText(-1).Contains('UIA-ONLY-A') -and $b.range.GetText(-1).Contains('UIA-ONLY-B') -and $quickDoc.range.GetText(-1).Contains('UIA-ONLY-QUICK'))
    Check 'UIA text runtime ids differ across windows' (($a.document.GetRuntimeId()-join ',')-ne ($b.document.GetRuntimeId()-join ',') -and ($a.document.GetRuntimeId()-join ',')-ne ($quickDoc.document.GetRuntimeId()-join ','))
    $alternate=[string](Rpc 'session.new' @{name='UIA-focus-alternate'} -Window $uiaWindows[0])
    if(-not (NavWait {@((Rpc 'tree' -Window $uiaWindows[0]).workspaces[0].sessions|Where-Object id -eq $alternate).Count-eq 1})){throw 'UIA focus peer not ready'}
    $null=Rpc 'session.select' @{} $uiaSessions[0] -Window $uiaWindows[0]
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'UIA-focus-alternate')
    $focusNode=$a.root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
    if($null-eq $focusNode){throw 'No owned sidebar session for UIA focus'}
    $focusNode.SetFocus()
    Check 'UIA session focus routes only to its owning window' (NavWait {$focusNode.Current.HasKeyboardFocus -and $b.range.GetText(-1).Contains('UIA-ONLY-B')})
    $settingsCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Settings')
    $settingsButton=$b.root.FindFirst([System.Windows.Automation.TreeScope]::Children,$settingsCondition)
    if($null-eq $settingsButton){throw 'Owned peer has no UIA Settings button'}
    ($settingsButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Check 'UIA Invoke opens settings in its owner only' (NavWait {
        $group=$b.root.FindFirst([System.Windows.Automation.TreeScope]::Children,$settingsCondition)
        $group.Current.ControlType-eq [System.Windows.Automation.ControlType]::Group -and $a.range.GetText(-1).Contains('UIA-ONLY-A')
    })
    [void][HudOwnedJob]::SendMessageW($uiaHandles[1],0x100,[IntPtr]27,[IntPtr]1)
    # Retain ranges across close; neither may attach itself to another library/quick context.
    foreach($index in 1,0){
        $null=Rpc 'window.close' @{} $uiaWindows[$index] -Window $libraryWindow
        if(-not (NavWait {@((Rpc 'window.list').windows|Where-Object {$_.id-eq $uiaWindows[$index] -and $_.open}).Count-eq 0})){throw 'Owned UIA window did not close'}
        $retired=if($index-eq 1){$b.range}else{$a.range}
        $refused=$false;try{$null=$retired.GetText(-1)}catch{$refused=$true}
        Check "retained range refuses after owning window $index closes" $refused
        Check 'quick UIA remains readable after another window closes' ($quickDoc.range.GetText(-1).Contains('UIA-ONLY-QUICK'))
    }
} finally {
    foreach($window in $uiaWindows){if(@((Rpc 'window.list').windows|Where-Object {$_.id-eq $window -and $_.open}).Count){$null=Rpc 'window.close' @{} $window -Window $libraryWindow}}
    $null=Rpc 'quick' @{op='off'} -Window $libraryWindow
    $null=Rpc 'window.select' @{} $libraryWindow -Window $libraryWindow
}
