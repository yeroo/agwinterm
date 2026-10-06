# Menu bar (MenuBar.cs): the title bar's File / View / Navigate / Help, driven through UI Automation
# and posted keys against hud-ui's private instance — never global input. Dot-sourced by hud-ui.ps1
# (-Suite Menu): $hwnd, $job, Rpc, Check, Node, Shot and $artifact come from there, and the sandbox's
# keymap.conf carries `map alt+h = toggle_sidebar` (a bound Alt+letter runs its binding) and `leader = f9` with
# `map leader alt+h = toggle_flag` (an Alt+letter leader follow-up). Alt+F / V / N stay unbound for #358.
#
# The UIA client sees what a screen reader sees: a MenuBar of MenuItems, the open menu's rows under
# its label (disabled rows listed but not enabled, the chord as AcceleratorKey), and the keyboard
# focus a lone Alt tap gives the bar. The popup's mouse routing (Menu.cs, rewritten for the bar:
# every message routed by its screen point) is driven by posting a button message to the popup
# window itself; a press that lands INSIDE a row is not posted, so a row running from a click is
# covered by Invoke instead.
$uiaAssemblies=if($PSVersionTable.PSEdition-eq 'Core'){$PSHOME}else{"$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/WPF"}
Add-Type -Path "$uiaAssemblies/UIAutomationTypes.dll"
Add-Type -Path "$uiaAssemblies/UIAutomationClient.dll"
if(-not ('MenuBarNative' -as [type])){ Add-Type -TypeDefinition @'
using System;using System.Collections.Generic;using System.Runtime.InteropServices;using System.Text;
public static class MenuBarNative {
 delegate bool Visitor(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumWindows(Visitor f,IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool PostMessageW(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [StructLayout(LayoutKind.Sequential)] public struct RECT{public int left,top,right,bottom;}
 [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint t);
 [DllImport("user32.dll")] static extern uint MapVirtualKeyExW(uint c,uint t,IntPtr l);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int ToUnicodeEx(uint vk,uint sc,byte[] ks,StringBuilder b,int n,uint f,IntPtr l);
 // The character a key makes in the window thread's layout with no modifier down, as the app's win32-input-mode encoding computes it.
 public static string KeyChar(IntPtr h,int vk){uint p;var l=GetKeyboardLayout(GetWindowThreadProcessId(h,out p));var b=new StringBuilder(8);int n=ToUnicodeEx((uint)vk,MapVirtualKeyExW((uint)vk,0,l),new byte[256],b,8,4,l);return n>=1?b.ToString(0,1):"";}
 public static IntPtr[] Dialogs(uint pid){var found=new List<IntPtr>();EnumWindows((h,p)=>{uint n;GetWindowThreadProcessId(h,out n);if(n==pid&&IsWindowVisible(h)){var cls=new StringBuilder(256);GetClassNameW(h,cls,256);if(cls.ToString()=="#32770")found.Add(h);}return true;},IntPtr.Zero);return found.ToArray();}
 [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
 // The modifiers held on the REAL keyboard (read only): the app's chord checks read Ctrl / Shift from GetKeyState, which a desktop user typing during the run can set.
 public static string HeldMods(){var m=new List<string>();if(GetAsyncKeyState(0x10)<0)m.Add("shift");if(GetAsyncKeyState(0x11)<0)m.Add("ctrl");if(GetAsyncKeyState(0x12)<0)m.Add("alt");return m.Count>0?string.Join("+",m):"none";}
 public static IntPtr[] Popups(uint pid){var found=new List<IntPtr>();EnumWindows((h,p)=>{uint n;GetWindowThreadProcessId(h,out n);if(n==pid&&IsWindowVisible(h)){var cls=new StringBuilder(256);GetClassNameW(h,cls,256);if(cls.ToString()=="agwinterm-menu")found.Add(h);}return true;},IntPtr.Zero);return found.ToArray();}
}
'@ }
function MenuWait([scriptblock]$condition,[int]$tries=60){for($i=0;$i-lt $tries;$i++){if(& $condition){return $true};Start-Sleep -Milliseconds 100};return $false}
$A=[System.Windows.Automation.AutomationElement]
$menuRoot=$A::FromHandle($hwnd)
function Prop([string]$name,$value){[System.Windows.Automation.PropertyCondition]::new($A::"$($name)Property",$value)}
function MenuBar { $menuRoot.FindFirst([System.Windows.Automation.TreeScope]::Children,(Prop 'ControlType' ([System.Windows.Automation.ControlType]::MenuBar))) }
function BarLabel([string]$title){ $bar=MenuBar; if($null-eq $bar){return $null}; $bar.FindFirst([System.Windows.Automation.TreeScope]::Children,(Prop 'Name' $title)) }
function Rows([string]$title){ $l=BarLabel $title; if($null-eq $l){return @()}; @($l.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)) }
function Row([string]$title,[string]$name){ $l=BarLabel $title; if($null-eq $l){return $null}; $l.FindFirst([System.Windows.Automation.TreeScope]::Children,(Prop 'Name' $name)) }
function Invoke-Element($e){ ($e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function PostKey([uint32]$msg,[int]$vk,[long]$lParam){ [void][MenuBarNative]::PostMessageW($hwnd,$msg,[IntPtr]$vk,[IntPtr]$lParam) }
$WM_KEYDOWN=0x100;$WM_KEYUP=0x101;$WM_SYSKEYDOWN=0x104;$WM_SYSKEYUP=0x105
$VK_MENU=0x12;$VK_ESCAPE=0x1B;$VK_RETURN=0x0D;$VK_RIGHT=0x27;$VK_DOWN=0x28
function AltTap { PostKey $WM_SYSKEYDOWN $VK_MENU 0x00380001; PostKey $WM_SYSKEYUP $VK_MENU 0xC0380001 }
function Key([int]$vk){ PostKey $WM_KEYDOWN $vk 0x00000001; PostKey $WM_KEYUP $vk 0xC0000001 }
function AltKey([int]$vk){ PostKey $WM_SYSKEYDOWN $vk 0x20000001; PostKey $WM_SYSKEYUP $vk 0xE0000001 }
function Focused([string]$title){ $l=BarLabel $title; $null-ne $l -and $l.Current.HasKeyboardFocus }
function ShotWindow([IntPtr]$h,[string]$name){
    Start-Sleep -Milliseconds 160
    $r=[MenuBarNative+RECT]::new();[void][MenuBarNative]::GetWindowRect($h,[ref]$r)
    $bitmap=[Drawing.Bitmap]::new([Math]::Max(1,$r.right-$r.left),[Math]::Max(1,$r.bottom-$r.top))
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try { $dc=$graphics.GetHdc();try{[void][MenuBarNative]::PrintWindow($h,$dc,3)}finally{$graphics.ReleaseHdc($dc)}; $bitmap.Save((Join-Path $artifact "$name.png")) }
    finally { $graphics.Dispose();$bitmap.Dispose() }
}

$expectedFile=@('New Window','Open Window','Rename Window…','Delete Window','New Workspace','Rename Workspace','Delete Workspace',
    'New Session','Open Directory…','Open Recent','Reopen Last Closed Item','Rename Session','Duplicate Session','Reveal in Explorer',
    'Close Session','Reopen Closed Item','Clear Status','Edit Keymap…','Reload Keymap','Edit agwinterm.conf…','Reload Config')

# ---- The bar itself, through UIA.
$bar=MenuBar
Check 'the title bar carries a UIA MenuBar' ($null-ne $bar)
$labels=@(); if($bar){ $labels=@($bar.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object {$_.Current.Name}) }
Check 'its items are File, View, Navigate, Help' (($labels -join ',')-eq 'File,View,Navigate,Help') "labels=$($labels -join ',')"
Check 'a bar label is a MenuItem, not a Button' ((BarLabel 'File').Current.ControlType-eq [System.Windows.Automation.ControlType]::MenuItem)
[void](Shot 'menu-bar')

# ---- Open File through Invoke: the rows are agterm's, disabled ones say so, and a screenshot of the popup.
Invoke-Element (BarLabel 'File')
Check 'Invoke on File drops its menu (rows appear under the label)' (MenuWait {@(Rows 'File').Count-gt 0})
$fileRows=@(Rows 'File'|ForEach-Object {$_.Current.Name})
Check 'the File menu lists agterm''s rows in agterm''s order' (($fileRows -join '|')-eq ($expectedFile -join '|')) "rows=$($fileRows -join '|')"
Check 'Delete Window is listed but disabled with one window' ($null-ne (Row 'File' 'Delete Window') -and -not (Row 'File' 'Delete Window').Current.IsEnabled)
Check 'Open Recent is disabled with nothing closed' ($null-ne (Row 'File' 'Open Recent') -and -not (Row 'File' 'Open Recent').Current.IsEnabled)
Check 'New Session is enabled' ((Row 'File' 'New Session').Current.IsEnabled)
Check 'New Session shows its default chord as the accelerator' ((Row 'File' 'New Session').Current.AcceleratorKey-eq 'Ctrl+Shift+T') "acc=$((Row 'File' 'New Session').Current.AcceleratorKey)"
Check 'a row without a chord has no accelerator' ([string]::IsNullOrEmpty((Row 'File' 'Open Directory…').Current.AcceleratorKey))
$popups=@([MenuBarNative]::Popups($job.Pid))
Check 'the dropdown is a real popup window of the owned process' ($popups.Count-eq 1) "popups=$($popups.Count)"
if($popups.Count-eq 1){ ShotWindow $popups[0] 'file-menu' }
[void](Shot 'menu-bar-file-open')
# A tall dropdown on a small screen (CI's 768 px): when it fits below the bar or above it, it must not cover the
# labels; when it fits nowhere (CI: 667 px of rows in a 720 px work area) it stays inside the work area, as far
# down as that allows, and covers the least of the bar it can (the keyboard still switches menus).
Add-Type -AssemblyName System.Windows.Forms
$wa=[System.Windows.Forms.Screen]::FromHandle($hwnd).WorkingArea
$pr=[MenuBarNative+RECT]::new();[void][MenuBarNative]::GetWindowRect($popups[0],[ref]$pr)
$fileBox=(BarLabel 'File').Current.BoundingRectangle
$barBottom=$fileBox.Y+$fileBox.Height; $popupH=$pr.bottom-$pr.top
$fitsSomewhere=($popupH-le ($wa.Bottom-$barBottom)) -or ($popupH-le ($fileBox.Y-$wa.Top))
$detail="popup=$($pr.top)..$($pr.bottom) label=$($fileBox.Y)..$barBottom work=$($wa.Top)..$($wa.Bottom)"
if($fitsSomewhere){ Check 'the File dropdown does not cover the bar when it fits below or above it' ($pr.top-ge $barBottom -or $pr.bottom-le $fileBox.Y) $detail }
else { Check 'the File dropdown fits nowhere here and stays inside the work area' ($pr.top-ge $wa.Top -and $pr.bottom-le $wa.Bottom) $detail }
# A posted press outside every level and off the bar (far negative client coordinates: what a captured press
# outside looks like; -40,-40 would land on a label above the popup) closes it and touches nothing.
$treeBefore=(Rpc 'tree')|ConvertTo-Json -Depth 8 -Compress
[void][MenuBarNative]::PostMessageW($popups[0],0x201,[IntPtr]1,[IntPtr](([int64](-3000 -band 0xFFFF) -shl 16) -bor (-3000 -band 0xFFFF)))
Check 'a press outside the popup closes it' (MenuWait {@(Rows 'File').Count-eq 0 -and @([MenuBarNative]::Popups($job.Pid)).Count-eq 0}) "popups=$($popups.Count)"
Check 'and changes nothing in the tree' (((Rpc 'tree')|ConvertTo-Json -Depth 8 -Compress)-eq $treeBefore)
# The popup routes a mouse message by the message's OWN point (client coordinates of the popup, which holds
# the capture), not the pointer's: with Help open (short: it fits under the bar on any screen), a posted press
# whose point lies on the Navigate label — to the LEFT of the popup, a negative x — switches menus. With
# GetCursorPos routing the real pointer would be anywhere but there, and this could not pass.
function PopupPoint([IntPtr]$popup,[double]$sx,[double]$sy){ $r=[MenuBarNative+RECT]::new();[void][MenuBarNative]::GetWindowRect($popup,[ref]$r); $cx=[int]($sx-$r.left);$cy=[int]($sy-$r.top); [IntPtr](([int64]($cy -band 0xFFFF) -shl 16) -bor ($cx -band 0xFFFF)) }
Invoke-Element (BarLabel 'Help')
Check 'Invoke on Help drops its menu' (MenuWait {$null-ne (Row 'Help' 'About agwinterm')})
$popups=@([MenuBarNative]::Popups($job.Pid))
$navBox=(BarLabel 'Navigate').Current.BoundingRectangle
if($popups.Count-ge 1){ [void][MenuBarNative]::PostMessageW($popups[0],0x201,[IntPtr]1,(PopupPoint $popups[0] ($navBox.X+$navBox.Width/2) ($navBox.Y+$navBox.Height/2))) }
Check 'a posted press on the Navigate label switches the dropdown to Navigate' (MenuWait {@(Rows 'Navigate').Count-gt 0 -and @(Rows 'Help').Count-eq 0}) "nav=$($navBox.X),$($navBox.Y) popups=$($popups.Count)"
$popups=@([MenuBarNative]::Popups($job.Pid))
if($popups.Count-ge 1){ [void][MenuBarNative]::PostMessageW($popups[0],0x201,[IntPtr]1,[IntPtr](([int64](-3000 -band 0xFFFF) -shl 16) -bor (-3000 -band 0xFFFF))) }
Check 'a press outside closes the switched dropdown too' (MenuWait {@(Rows 'Navigate').Count-eq 0 -and @([MenuBarNative]::Popups($job.Pid)).Count-eq 0})
Invoke-Element (BarLabel 'File')
Check 'File opens again for the Invoke case' (MenuWait {@(Rows 'File').Count-gt 0})
$before=@((Rpc 'tree').workspaces|ForEach-Object sessions).Count
Invoke-Element (Row 'File' 'New Session')
Check 'Invoke on New Session creates a session and closes the menu' ((MenuWait {@((Rpc 'tree').workspaces|ForEach-Object sessions).Count-eq $before+1}) -and (MenuWait {@(Rows 'File').Count-eq 0}))
Check 'the popup window is gone with the menu' (MenuWait {@([MenuBarNative]::Popups($job.Pid)).Count-eq 0})

# ---- The keyboard: a lone Alt tap focuses the bar; arrows move; Enter opens; Esc leaves one level at a time.
AltTap
Check 'a lone Alt tap gives File the keyboard focus' (MenuWait {Focused 'File'})
Key $VK_RIGHT
Check 'Right moves the focus to View' (MenuWait {Focused 'View'})
Key $VK_RETURN
Check 'Enter opens the focused menu (View rows appear)' (MenuWait {$null-ne (Row 'View' 'Increase Font Size')})
Check 'a state row reads its current state (Hide Sidebar while the sidebar shows)' ($null-ne (Row 'View' 'Hide Sidebar'))
Check 'Swap Panes is disabled on a single pane' ($null-ne (Row 'View' 'Swap Panes') -and -not (Row 'View' 'Swap Panes').Current.IsEnabled)
Key $VK_ESCAPE
Check 'Esc closes the dropdown and keeps View focused' ((MenuWait {@(Rows 'View').Count-eq 0}) -and (Focused 'View'))
Key $VK_ESCAPE
Check 'a second Esc leaves the bar' (MenuWait {-not (Focused 'View') -and -not (Focused 'File')})
AltTap
Check 'Alt tap: the bar is focused' (MenuWait {Focused 'File'})
AltTap
Check 'Alt tap again: the bar is left (the same tap cannot re-focus it)' (MenuWait {-not (Focused 'File')})
Start-Sleep -Milliseconds 300
Check 'and it stays left' (-not (Focused 'File'))
AltTap
Check 'bar focused for the key-leak case' (MenuWait {Focused 'File'})
PostKey $WM_KEYDOWN 0x51 0x00100001; PostKey 0x102 0x71 0x00100001; PostKey $WM_KEYUP 0x51 0xC0100001   # q, as TranslateMessage would queue it
Check 'a non-mnemonic key leaves the bar' (MenuWait {-not (Focused 'File')})
Start-Sleep -Milliseconds 400
$paneText=[string](Rpc 'session.text' @{})   # the ACTIVE pane: New Session above made a second one
Check 'and its character does not reach the pane' (-not ($paneText-match '>q')) "tail=$($paneText.Trim() -replace '\s+',' ' | ForEach-Object { $_.Substring([Math]::Max(0,$_.Length-80)) })"

# ---- Alt+letter is the pane program's (#358: Claude Code pastes an image on Alt+V), not a menu mnemonic: with the
# bar shown, the pane focused (a plain cmd prompt: not the alternate screen, no mouse reporting) and no menu up,
# Alt+V / Alt+F / Alt+N and the WM_SYSCHAR TranslateMessage queues for each reach the pane, and no menu opens.
# cmd under ConPTY (win32-input-mode) echoes the key's character exactly once — in the instance's keyboard layout,
# which can change mid-run (the input language is global), so it is asked of the layout when the key is posted
# and again when the line is read, never assumed to be the Latin letter.
function PaneLine { ((([string](Rpc 'session.text' @{})).TrimEnd() -split '\r?\n')[-1]).TrimEnd() }
function Appended([string]$before,[int]$vk,[string]$posted){ $after=PaneLine; $chars=@($posted,[MenuBarNative]::KeyChar($hwnd,$vk))|Where-Object {$_}; if($chars){ @($chars|Where-Object { $after-ceq $before+$_ }).Count-gt 0 } else { $after.Length-eq $before.Length+1 -and $after.StartsWith($before) -and $after[-1]-match '\S' } }
function AnyMenuOpen { @('File','View','Navigate','Help'|Where-Object { @(Rows $_).Count-gt 0 -or (Focused $_) }).Count-gt 0 -or @([MenuBarNative]::Popups($job.Pid)).Count-gt 0 }
function AltLetter([int]$vk,[int]$sc){   # Alt+letter with the WM_SYSCHAR TranslateMessage would queue; returns the layout's character
    $ch=[MenuBarNative]::KeyChar($hwnd,$vk); $code=if($ch){[int][char]$ch}else{$vk+0x20}; $l=0x20000001 -bor ($sc -shl 16)
    PostKey $WM_SYSKEYDOWN $vk $l; PostKey 0x106 $code $l; PostKey $WM_SYSKEYUP $vk ($l -bor 0xC0000000); $ch
}
function AltV { AltLetter 0x56 0x2F }
foreach($k in @(@{n='V';vk=0x56;sc=0x2F},@{n='F';vk=0x46;sc=0x21},@{n='N';vk=0x4E;sc=0x31})){
    $lineBefore=PaneLine
    $ch=AltLetter $k.vk $k.sc; $held=[MenuBarNative]::HeldMods()   # the real modifiers as the key is posted
    Start-Sleep -Milliseconds 400
    Check "Alt+$($k.n) with the pane focused opens no menu (#358)" (-not (AnyMenuOpen)) "rows=$(@('File','View','Navigate','Help'|ForEach-Object { "$_=$(@(Rows $_).Count)" }) -join ',') popups=$(@([MenuBarNative]::Popups($job.Pid)).Count)"
    Check "and Alt+$($k.n) reaches the pane: its character lands on the prompt line once (#358)" (MenuWait {Appended $lineBefore $k.vk $ch}) "char='$ch' now='$([MenuBarNative]::KeyChar($hwnd,$k.vk))' held=$held before='$lineBefore' after='$(PaneLine)'"
    if(AnyMenuOpen){ Key $VK_ESCAPE; Key $VK_ESCAPE; [void](MenuWait {-not (AnyMenuOpen)}) }   # leave the cases below a closed bar either way
}

# ---- Mnemonics: with the bar focused (Alt tap) V opens View; Alt+letter switches an open menu; Alt+H is bound in
# keymap.conf, so the keymap wins.
AltTap
Check 'bar focused for the mnemonic case' (MenuWait {Focused 'File'})
Key 0x56
Check 'Alt tap then V opens the View menu' (MenuWait {$null-ne (Row 'View' 'Increase Font Size')})
Check 'a rebound action shows its keymap chord as the accelerator' ((Row 'View' 'Hide Sidebar').Current.AcceleratorKey-eq 'Alt+H') "acc=$((Row 'View' 'Hide Sidebar').Current.AcceleratorKey)"
AltTap
Check 'Alt while a menu is open closes it and leaves the bar' (MenuWait {@(Rows 'View').Count-eq 0 -and -not (Focused 'View') -and -not (Focused 'File')})
AltKey 0x66
Start-Sleep -Milliseconds 400
Check 'Alt+Numpad6 is not Alt+F: no menu opens' (@(Rows 'File').Count-eq 0 -and @([MenuBarNative]::Popups($job.Pid)).Count-eq 0)
AltTap; Key $VK_RIGHT; Key $VK_DOWN
Check 'View opens again (Alt tap, Right, Down)' (MenuWait {$null-ne (Row 'View' 'Increase Font Size')})
AltKey 0x4E
Check 'Alt+N while View is open switches the dropdown to Navigate' (MenuWait {@(Rows 'Navigate').Count-gt 0 -and @(Rows 'View').Count-eq 0})
Key $VK_ESCAPE; Key $VK_ESCAPE
Check 'the menu is closed again' (MenuWait {@(Rows 'Navigate').Count-eq 0 -and -not (Focused 'Navigate')})
$sidebarBefore=[string](Rpc 'sidebar' @{op='state'} -NoTarget)
AltKey 0x48
Check 'Alt+H runs the keymap''s binding (toggle_sidebar), not the Help menu' ((MenuWait {([string](Rpc 'sidebar' @{op='state'} -NoTarget))-ne $sidebarBefore}) -and @(Rows 'Help').Count-eq 0) "before=$sidebarBefore"
AltKey 0x48
Check 'and toggles it back' (MenuWait {([string](Rpc 'sidebar' @{op='state'} -NoTarget))-eq $sidebarBefore})
# F10 (a system key: WM_SYSKEYDOWN without the Alt context bit) focuses the bar at a plain prompt, and V then opens View.
PostKey $WM_SYSKEYDOWN 0x79 0x00440001; PostKey $WM_SYSKEYUP 0x79 0xC0440001
Check 'F10 focuses the bar' (MenuWait {Focused 'File'})
Key 0x56
Check 'F10 then V opens the View menu' (MenuWait {$null-ne (Row 'View' 'Increase Font Size')})
Key $VK_ESCAPE; Key $VK_ESCAPE
Check 'and Esc, Esc closes it and leaves the bar' (MenuWait {-not (AnyMenuOpen)})
# A leader follow-up on an Alt+letter (keymap.conf: `leader = f9`, `map leader alt+h = toggle_flag`) runs the
# leader's binding: not the Help menu, not the pane, and not the plain alt+h binding (toggle_sidebar).
function FlaggedCount { @((Rpc 'tree').workspaces|ForEach-Object sessions|Where-Object flagged).Count }
$lineBefore=PaneLine; $flaggedBefore=FlaggedCount
Key 0x78; [void](AltLetter 0x48 0x23)
Check 'leader then Alt+H runs the leader binding (toggle_flag)' (MenuWait {(FlaggedCount)-ne $flaggedBefore}) "flagged before=$flaggedBefore now=$(FlaggedCount)"
Check 'and not the plain Alt+H binding, no menu, nothing in the pane' (([string](Rpc 'sidebar' @{op='state'} -NoTarget))-eq $sidebarBefore -and -not (AnyMenuOpen) -and (PaneLine)-ceq $lineBefore) "sidebar=$(Rpc 'sidebar' @{op='state'} -NoTarget) before='$lineBefore' after='$(PaneLine)'"
Key 0x78; [void](AltLetter 0x48 0x23)
Check 'leader then Alt+H toggles the flag back' (MenuWait {(FlaggedCount)-eq $flaggedBefore})

# ---- Outside the terminal Alt+letter is still a mnemonic: the F6 sidebar zone and the dashboard swallow every other
# key, so there Alt+V opens View, and nothing reaches the pane. UIA tells the zone apart: a sidebar session row has the
# keyboard focus, the terminal does not. The dashboard opens through the control API (Ctrl+Shift+D reads Ctrl and
# Shift from GetKeyState, which posted input does not set).
function ZoneFocused { $list=$menuRoot.FindFirst([System.Windows.Automation.TreeScope]::Children,(Prop 'Name' 'Sessions')); $null-ne $list -and @($list.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)|Where-Object { $_.Current.HasKeyboardFocus }).Count-gt 0 }
function TerminalFocused { $t=$menuRoot.FindFirst([System.Windows.Automation.TreeScope]::Children,(Prop 'ControlType' ([System.Windows.Automation.ControlType]::Document))); $null-ne $t -and $t.Current.HasKeyboardFocus }   # its Name is the screen text
Check 'the terminal has the keyboard before F6' (TerminalFocused)
$lineBefore=PaneLine
Key 0x75   # F6
Check 'F6 moves the keyboard into the sidebar zone' (MenuWait {(ZoneFocused) -and -not (TerminalFocused)})
[void](AltV); $held=[MenuBarNative]::HeldMods()
Check 'Alt+V in the sidebar zone opens the View menu' (MenuWait {$null-ne (Row 'View' 'Increase Font Size')}) "held=$held after='$(PaneLine)'"
Key $VK_ESCAPE
Check 'Esc closes it and keeps View focused' ((MenuWait {@(Rows 'View').Count-eq 0}) -and (Focused 'View'))
Key $VK_ESCAPE
Check 'a second Esc leaves the bar and the sidebar zone keeps the keyboard' ((MenuWait {-not (AnyMenuOpen)}) -and (ZoneFocused) -and -not (TerminalFocused))
Key 0x75   # F6
Check 'F6 gives the keyboard back to the terminal' (MenuWait {(TerminalFocused) -and -not (ZoneFocused)})
Check 'nothing of the sidebar-zone case reached the pane' ((PaneLine)-ceq $lineBefore) "before='$lineBefore' after='$(PaneLine)'"
$null=Rpc 'dashboard' @{} -NoTarget
$null=Rpc 'tree'   # FIFO with the posted verb: the dashboard is up before the key below
[void](AltV); $held=[MenuBarNative]::HeldMods()
$opened=MenuWait {$null-ne (Row 'View' 'Increase Font Size')}
Check 'Alt+V over the dashboard opens the View menu' $opened "held=$held after='$(PaneLine)'"
if($opened){ Key $VK_ESCAPE; Key $VK_ESCAPE; Check 'Esc, Esc closes it over the dashboard' (MenuWait {-not (AnyMenuOpen)}) }
Check 'nothing of the dashboard case reached the pane' ((PaneLine)-ceq $lineBefore) "before='$lineBefore' after='$(PaneLine)'"
$null=Rpc 'dashboard' @{close=$true} -NoTarget   # not Esc: with the dashboard down it would reach the pane and clear the line
$null=Rpc 'tree'
$lineBefore=PaneLine
$ch=AltV; $held=[MenuBarNative]::HeldMods()
Check 'with the dashboard closed Alt+V reaches the pane again and opens no menu' ((MenuWait {Appended $lineBefore 0x56 $ch}) -and -not (AnyMenuOpen)) "char='$ch' held=$held before='$lineBefore' after='$(PaneLine)'"
# The find bar swallows every key too; it opens through session.search (Ctrl+F reads Ctrl from GetKeyState).
$lineBefore=PaneLine
$findStatus=[string](Rpc 'session.search' @{query='MENU-FIND-PROBE'})
[void](AltV); $held=[MenuBarNative]::HeldMods()
$opened=MenuWait {$null-ne (Row 'View' 'Increase Font Size')}
Check 'Alt+V in the find bar opens the View menu' $opened "held=$held find='$findStatus' after='$(PaneLine)'"
if($opened){ Key $VK_ESCAPE; Key $VK_ESCAPE; Check 'Esc, Esc closes it over the find bar' (MenuWait {-not (AnyMenuOpen)}) }
Check 'nothing of the find-bar case reached the pane' ((PaneLine)-ceq $lineBefore) "before='$lineBefore' after='$(PaneLine)'"
$null=Rpc 'session.search' @{action='close'}   # not Esc, for the reason the dashboard closes through the API

# ---- A row that runs a modal loop: Help ▸ About (a MessageBox) pumps the queued WM_CHAR of the Enter that ran it
# while the menu is already closed; the char is the menu's and must not reach the pane. Keyboard all the way:
# Alt, Right×3 to Help, Enter opens it, End selects About (its last row), Enter runs it.
$paneBefore=([string](Rpc 'session.text' @{})).TrimEnd()
AltTap; Key $VK_RIGHT; Key $VK_RIGHT; Key $VK_RIGHT
Check 'Help is focused after three Rights' (MenuWait {Focused 'Help'})
Key $VK_RETURN
Check 'Enter opens the Help menu' (MenuWait {$null-ne (Row 'Help' 'About agwinterm')})
Key 0x23   # End
PostKey $WM_KEYDOWN $VK_RETURN 0x001C0001; PostKey 0x102 0x0D 0x001C0001; PostKey $WM_KEYUP $VK_RETURN 0xC01C0001   # Enter, with the WM_CHAR TranslateMessage would queue
Check 'Enter on About opens the About dialog' (MenuWait {@([MenuBarNative]::Dialogs($job.Pid)).Count-eq 1})
foreach($dlg in @([MenuBarNative]::Dialogs($job.Pid))){ [void][MenuBarNative]::PostMessageW($dlg,0x10,[IntPtr]::Zero,[IntPtr]::Zero) }   # WM_CLOSE
Check 'the About dialog closes' (MenuWait {@([MenuBarNative]::Dialogs($job.Pid)).Count-eq 0 -and @(Rows 'Help').Count-eq 0})
Start-Sleep -Milliseconds 300
$paneAfter=([string](Rpc 'session.text' @{})).TrimEnd()
Check 'the Enter that ran About did not reach the pane' ($paneAfter-eq $paneBefore) "before=…$($paneBefore.Substring([Math]::Max(0,$paneBefore.Length-40))) after=…$($paneAfter.Substring([Math]::Max(0,$paneAfter.Length-40)))"

# ---- The file is the truth: a key edited by hand in agwinterm.conf is applied by the next `config set` of ANOTHER
# key (it re-reads the file), and by File ▸ Reload Config — with the same steps a set applies (the cell grows).
$conf=Join-Path $appDir 'agwinterm.conf'
function Set-ConfLine([string]$key,[string]$value){ $kept=@(Get-Content $conf | Where-Object { $_ -notmatch ('^\s*'+[regex]::Escape($key)+'\s*=') }); Set-Content $conf ($kept + ($key+' = '+$value)) }   # one line per key: the parser keeps the FIRST
$fontBefore=[regex]::Match([string](Rpc 'config.get' @{key='font-size'} -NoTarget),'(\d+(\.\d+)?)\s*$').Groups[1].Value
$cellBefore=[double](Rpc 'session.metrics' @{} $session).cellHeight
Set-ConfLine 'font-size' ([string]([double]$fontBefore + 6))
$null=Rpc 'config.set' @{key='cursor-blink-ms';value='700'} -NoTarget
Check 'a hand-edited key is applied by a config set of another key' (MenuWait {([double](Rpc 'session.metrics' @{} $session).cellHeight)-gt $cellBefore}) "cell=$((Rpc 'session.metrics' @{} $session).cellHeight) before=$cellBefore font=$(Rpc 'config.get' @{key='font-size'} -NoTarget)"
Set-ConfLine 'font-size' $fontBefore
Invoke-Element (BarLabel 'File')
Check 'File opens for Reload Config' (MenuWait {$null-ne (Row 'File' 'Reload Config')})
Invoke-Element (Row 'File' 'Reload Config')
Check 'Reload Config applies the hand edit: the cell is back to its size' (MenuWait {([double](Rpc 'session.metrics' @{} $session).cellHeight)-eq $cellBefore}) "cell=$((Rpc 'session.metrics' @{} $session).cellHeight) before=$cellBefore"
Check 'and config get reads the reloaded value' (([string](Rpc 'config.get' @{key='font-size'} -NoTarget))-match ('(^|\D)'+[regex]::Escape($fontBefore)+'\s*$')) "get=$(Rpc 'config.get' @{key='font-size'} -NoTarget)"

# ---- show-menu-bar: off removes the bar and its keys; on brings it back.
$null=Rpc 'config.set' @{key='show-menu-bar';value='false'} -NoTarget
Check 'show-menu-bar = false removes the MenuBar element' (MenuWait {$null-eq (MenuBar)})
AltTap
Start-Sleep -Milliseconds 300
PostKey $WM_KEYDOWN 0x58 0x002D0001; PostKey 0x102 0x78 0x002D0001; PostKey $WM_KEYUP 0x58 0xC02D0001   # x
$xChars='['+[regex]::Escape('x'+[MenuBarNative]::KeyChar($hwnd,0x58))+']'   # the layout's character for the key, as the #358 cases above ask it
Check 'with the bar hidden an Alt tap takes no keys: the next key reaches the pane' (MenuWait {([string](Rpc 'session.text' @{}))-match ('>\S*'+$xChars)}) "now='$([MenuBarNative]::KeyChar($hwnd,0x58))' tail=$(([string](Rpc 'session.text' @{})).Trim() -replace '\s+',' ' | ForEach-Object { $_.Substring([Math]::Max(0,$_.Length-80)) })"   # \S*: the Alt+Numpad6 case above left the pane its Alt+6 (ESC 6), as a real one would
# The control for #358: with no bar Alt+V takes the same pane path the bar-shown case above expects.
$lineBefore=PaneLine
$ch=AltV
Check 'with the bar hidden Alt+V reaches the pane: its character lands on the prompt line once' (MenuWait {Appended $lineBefore 0x56 $ch}) "char='$ch' now='$([MenuBarNative]::KeyChar($hwnd,0x56))' before='$lineBefore' after='$(PaneLine)'"
Check 'and opens no menu' (-not (AnyMenuOpen))
$null=Rpc 'config.set' @{key='show-menu-bar';value='true'} -NoTarget
Check 'show-menu-bar = true brings it back' (MenuWait {$null-ne (MenuBar)})
Check 'config get reads the key' (([string](Rpc 'config.get' @{key='show-menu-bar'} -NoTarget))-match 'true')
