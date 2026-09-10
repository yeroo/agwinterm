# Actual UI Automation clients, only for HWNDs belonging to hud-ui's owned app/job.
# This is provider integration, not a claim that Narrator/NVDA presentation was manually tested.
# PowerShell Core ships its matching desktop assemblies. Mixing Framework 4.x with that
# runtime can bind Client 10.0 to Types 4.0 and fail only when FromHandle resolves a type.
$uiaAssemblies=if($PSVersionTable.PSEdition-eq 'Core'){$PSHOME}else{"$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/WPF"}
Add-Type -Path "$uiaAssemblies/UIAutomationTypes.dll"
Add-Type -Path "$uiaAssemblies/UIAutomationClient.dll"
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
        if(-not (NavWait {@([NavUiaWindows]::Owned($job.Pid)|Where-Object {$_-notin $beforeHandles}).Count-eq 1})){throw 'New owned UIA HWND did not materialize'}
        $handles=@([NavUiaWindows]::Owned($job.Pid)|Where-Object {$_-notin $beforeHandles})
        if($handles.Count-ne 1){throw 'Cannot identify exactly one new owned UIA HWND'}
        $uiaHandles+=,$handles[0]
        # Opening the HWND precedes asynchronous creation/publication of its first session.
        if(-not (NavWait {@((Rpc 'tree' -Window $window).workspaces[0].sessions|Where-Object { -not [string]::IsNullOrWhiteSpace($_.id) }).Count-eq 1})){throw 'UIA initial session did not materialize'}
        $sid=[string](Rpc 'tree' -Window $window).workspaces[0].sessions[0].id
        if([string]::IsNullOrWhiteSpace($sid)){throw 'UIA initial session disappeared before identity capture'}
        $uiaSessions+=,$sid
        if(-not (NavWait {@((Rpc 'tree' -Window $window).workspaces[0].sessions)[0].foregroundShell-eq 'cmd'})){throw 'UIA shell not ready'}
        if(-not (NavWait {([string](Rpc 'session.text' @{} $sid -Window $window)).Contains('>')})){throw 'UIA command prompt not ready'}
        # Produce real child output: injected emulator-only text is replaced by ConPTY repaint
        # after a later session switch/resize and cannot serve as a retained-range oracle.
        $null=Rpc 'session.type' @{text="echo UIA-ONLY-$label`r"} $sid -Window $window
        if(-not (NavWait {([string](Rpc 'session.text' @{} $sid -Window $window)).Contains("UIA-ONLY-$label")})){throw 'UIA marker did not reach owned shell'}
    }
    $null=Rpc 'quick' @{op='on'} -Window $libraryWindow
    $q=@([NavUiaWindows]::Owned($job.Pid,$true))
    if($q.Count-ne 1){throw 'No unique owned quick HWND'}
    if(-not (NavWait {([string](Rpc 'session.text' -Window quick)).Contains('>')})){throw 'Quick shell did not finish startup'}
    $null=Rpc 'session.type' @{text="echo UIA-ONLY-QUICK`r"} -Window quick
    if(-not (NavWait {([string](Rpc 'session.text' -Window quick)).Contains('UIA-ONLY-QUICK')})){throw 'Quick marker did not reach its owned pane'}
    $a=UiaDoc $uiaHandles[0];$b=UiaDoc $uiaHandles[1];$quickDoc=UiaDoc $q[0]
    Check 'two library UIA roots and quick expose separate document text' ($a.range.GetText(-1).Contains('UIA-ONLY-A') -and $b.range.GetText(-1).Contains('UIA-ONLY-B') -and $quickDoc.range.GetText(-1).Contains('UIA-ONLY-QUICK'))
    Check 'UIA text runtime ids differ across windows' (($a.document.GetRuntimeId()-join ',')-ne ($b.document.GetRuntimeId()-join ',') -and ($a.document.GetRuntimeId()-join ',')-ne ($quickDoc.document.GetRuntimeId()-join ','))
    $alternate=[string](Rpc 'session.new' @{name='UIA-focus-alternate';'no-select'=$true} -Window $uiaWindows[0])
    if(-not (NavWait {@((Rpc 'tree' -Window $uiaWindows[0]).workspaces[0].sessions|Where-Object id -eq $alternate).Count-eq 1})){throw 'UIA focus peer not ready'}
    $null=Rpc 'session.select' @{} $uiaSessions[0] -Window $uiaWindows[0]
    # A FIFO UI action, unlike a pipe-thread tree read, observes the posted selection landing.
    $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -Window $uiaWindows[0]
    if(-not (NavWait {@((Rpc 'tree' -Window $uiaWindows[0]).workspaces[0].sessions|Where-Object {$_.id-eq $uiaSessions[0] -and $_.active}).Count-eq 1})){
        throw "Original UIA session did not become active: expected=$($uiaSessions[0]), window=$($uiaWindows[0]), tree=$(Rpc 'tree' -Window $uiaWindows[0]|ConvertTo-Json -Depth 8 -Compress)"
    }
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'UIA-focus-alternate')
    $focusNode=$a.root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
    if($null-eq $focusNode){throw 'No owned sidebar session for UIA focus'}
    $focusNode.SetFocus()
    Check 'UIA session focus routes only to its owning window' (NavWait {$focusNode.Current.HasKeyboardFocus -and $b.range.GetText(-1).Contains('UIA-ONLY-B')})
    $settingsCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Settings')
    $settingsButton=$b.root.FindFirst([System.Windows.Automation.TreeScope]::Children,$settingsCondition)
    if($null-eq $settingsButton){throw 'Owned peer has no UIA Settings button'}
    $settingsIdentity=$settingsButton.GetRuntimeId()-join ','
    $null=Rpc 'sidebar' @{op='hide'} -Window $uiaWindows[1]
    if(-not (NavWait {$null-ne $b.root.FindFirst([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Recent sessions'))})){throw 'Sidebar-hidden chrome did not materialize'}
    Check 'retained Settings button keeps its action across chrome insertion' ($settingsButton.Current.Name-eq 'Settings' -and ($settingsButton.GetRuntimeId()-join ',')-eq $settingsIdentity)
    Check 'chrome Invoke button does not advertise unsupported keyboard focus' (-not $settingsButton.Current.IsKeyboardFocusable)
    ($settingsButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    $settingsOpened=NavWait {
        $group=$b.root.FindFirst([System.Windows.Automation.TreeScope]::Children,$settingsCondition)
        $group.Current.ControlType-eq [System.Windows.Automation.ControlType]::Group -and $a.range.GetText(-1).Contains('UIA-ONLY-A')
    }
    Check 'UIA Invoke opens settings in its owner only' $settingsOpened
    if($settingsOpened){
        $tabCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::TabItem)
        $tab=$b.root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$tabCondition)
        $tab.SetFocus();Check 'settings tab UIA SetFocus updates actual keyboard focus' (NavWait {$tab.Current.HasKeyboardFocus})
        $controlCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)
        $control=$b.root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$controlCondition)
        $control.SetFocus();Check 'settings control UIA SetFocus moves focus off tab header' (NavWait {$control.Current.HasKeyboardFocus -and -not $tab.Current.HasKeyboardFocus})
    }
    if(-not $settingsOpened){"UIA invoke diagnostics: A=$($a.range.GetText(-1)) B=$($b.range.GetText(-1)) windows=$($uiaWindows-join ',')"}
    [void][HudOwnedJob]::SendMessageW($uiaHandles[1],0x100,[IntPtr]27,[IntPtr]1)
    # Remove the earlier row; a retained provider must still identify the same session, not its ordinal.
    $retainedRuntime=$focusNode.GetRuntimeId()-join ','
    $null=Rpc 'session.close' @{} $uiaSessions[0] -Window $uiaWindows[0]
    $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -Window $uiaWindows[0]
    $focusNode.SetFocus()
    Check 'retained sidebar item keeps session identity after earlier row closes' (NavWait {$focusNode.Current.Name-eq 'UIA-focus-alternate' -and $focusNode.Current.HasKeyboardFocus -and ($focusNode.GetRuntimeId()-join ',')-eq $retainedRuntime})
    $replacement=[string](Rpc 'session.new' @{name='UIA-unrelated-replacement'} -Window $uiaWindows[0])
    $null=Rpc 'session.close' @{} $alternate -Window $uiaWindows[0]
    $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -Window $uiaWindows[0]
    # UIA's client broker may retain an old Name/default rather than forward the provider HRESULT.
    # Test the user-visible invariant: no rebinding, and a stale focus request cannot steal focus.
    $staleName=$null;try{$staleName=$focusNode.Current.Name}catch{}
    $sentinel=[string](Rpc 'session.new' @{name='UIA-focus-sentinel'} -Window $uiaWindows[0])
    $sentinelCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'UIA-focus-sentinel')
    if(-not (NavWait {$null-ne $a.root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$sentinelCondition)})){throw 'UIA sentinel not ready'}
    $sentinelNode=$a.root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$sentinelCondition)
    $sentinelNode.SetFocus()
    if(-not (NavWait {$sentinelNode.Current.HasKeyboardFocus})){throw 'UIA sentinel did not receive focus'}
    try{$focusNode.SetFocus()}catch{}
    $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -Window $uiaWindows[0]
    "Retained UIA check: original=$($uiaSessions[0]) retained=$alternate tree=$(Rpc 'tree' -Window $uiaWindows[0]|ConvertTo-Json -Depth 6 -Compress)"
    Check 'removed sidebar item cannot rebind name or focus to replacement' ($staleName-ne 'UIA-unrelated-replacement' -and $sentinelNode.Current.HasKeyboardFocus) "broker name='$staleName'"
    # Retain ranges across close; neither may attach itself to another library/quick context.
    foreach($index in 1,0){
        $null=Rpc 'window.close' @{} $uiaWindows[$index] -Window $libraryWindow
        if(-not (NavWait {@((Rpc 'window.list').windows|Where-Object {$_.id-eq $uiaWindows[$index] -and $_.open}).Count-eq 0})){throw 'Owned UIA window did not close'}
        $retired=if($index-eq 1){$b.range}else{$a.range}
        $refused=$false;try{$null=$retired.GetText(-1)}catch{$refused=$true}
        Check "retained range refuses after owning window $index closes" $refused
        $refused=$false;try{$null=$retired.Clone()}catch{$refused=$true}
        Check "retained range cannot clone after owning window $index closes" $refused
        Check 'quick UIA remains readable after another window closes' ($quickDoc.range.GetText(-1).Contains('UIA-ONLY-QUICK'))
    }
} finally {
    foreach($window in $uiaWindows){if(@((Rpc 'window.list').windows|Where-Object {$_.id-eq $window -and $_.open}).Count){$null=Rpc 'window.close' @{} $window -Window $libraryWindow}}
    $null=Rpc 'quick' @{op='off'} -Window $libraryWindow
    $null=Rpc 'window.select' @{} $libraryWindow -Window $libraryWindow
}
