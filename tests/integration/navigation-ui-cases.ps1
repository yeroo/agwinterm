# Dot-sourced by hud-ui.ps1 inside its private app/profile and canonical token/owned-job lifetime.
function NavTree { (Rpc 'tree').workspaces }
function CurrentWs { @((NavTree)|Where-Object active)[0].id }
function NavWait([scriptblock]$condition){for($i=0;$i-lt 60;$i++){if(& $condition){return $true};Start-Sleep -Milliseconds 100};return $false}
function NavKey([int]$key){[void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]$key,[IntPtr]1)}
function NavPixels([string]$name){
    Start-Sleep -Milliseconds 180
    $rc=[HudOwnedJob+RECT]::new();[void][HudOwnedJob]::GetClientRect($hwnd,[ref]$rc)
    $bitmap=[Drawing.Bitmap]::new($rc.right,$rc.bottom);$g=[Drawing.Graphics]::FromImage($bitmap)
    try{
        $dc=$g.GetHdc();try{if(-not [HudOwnedJob]::PrintWindow($hwnd,$dc,3)){throw 'Navigation PrintWindow failed'}}finally{$g.ReleaseHdc($dc)}
        $bitmap.Save((Join-Path $artifact "$name.png"))
        $bits=$bitmap.LockBits([Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height),[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try{$pixels=[int[]]::new($bitmap.Width*$bitmap.Height);[Runtime.InteropServices.Marshal]::Copy($bits.Scan0,$pixels,0,$pixels.Length);return @{pixels=$pixels;width=$bitmap.Width;height=$bitmap.Height}}
        finally{$bitmap.UnlockBits($bits)}
    }finally{$g.Dispose();$bitmap.Dispose()}
}
function PixelDifference($a,$b,[int]$x=0,[int]$y=0,[int]$w=0,[int]$h=0){
    if($a.width-ne $b.width -or $a.height-ne $b.height){throw 'Unexpected image resize'}
    if($w-le 0){$w=$a.width};if($h-le 0){$h=$a.height};$different=0
    for($row=$y;$row-lt [Math]::Min($a.height,$y+$h);$row++){for($col=$x;$col-lt [Math]::Min($a.width,$x+$w);$col++){
        $index=$row*$a.width+$col;if($a.pixels[$index]-ne $b.pixels[$index]){$different++}
    }};return $different
}
$first=[string](CurrentWs);$beforeText=Rpc 'session.text' @{} $session
. "$PSScriptRoot/workspace-teardown-cases.ps1"
$esc=[string][char]27
$null=Rpc 'config.set' @{key='cursor-blink';value='false'}
$null=Rpc 'session.write' @{text=($esc+'[?9001hMODIFIER-SELECTION-PROBE')} $session
$null=Rpc 'selection.all' @{} $session
$selectedBefore=Rpc 'session.copy' @{} $session
NavKey 17
Check 'modifier down preserves selection in Win32 input mode' ((Rpc 'session.copy' @{} $session)-ceq $selectedBefore)
[void][HudOwnedJob]::SendMessageW($hwnd,0x101,[IntPtr]17,[IntPtr]1)
Check 'modifier up preserves selection in Win32 input mode' ((Rpc 'session.copy' @{} $session)-ceq $selectedBefore)
$null=Rpc 'selection.clear' @{} $session
$null=Rpc 'session.write' @{text=($esc+'[?9001l'+$esc+'[?1049hALT-HELP-PROBE')} $session
$helpBefore=NavPixels 'alt-help-before';NavKey 112
$helpShown=NavPixels 'alt-help-shown'
Check 'F1 opens app help while alternate screen is active' ((PixelDifference $helpBefore $helpShown)-gt 1000)
NavKey 112
$helpClosed=NavPixels 'alt-help-closed'
Check 'F1 closes app help without forwarding it to the terminal' ((PixelDifference $helpBefore $helpClosed)-lt 250)
$null=Rpc 'session.write' @{text=($esc+'[?1049l')} $session
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class NavWheel {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x,y; }
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd,ref POINT point);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeoutW(IntPtr h,uint m,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);
    public static bool Responsive(IntPtr h) { IntPtr result;return SendMessageTimeoutW(h,0,IntPtr.Zero,IntPtr.Zero,3,2000,out result)!=IntPtr.Zero; }
}
'@
$null=Rpc 'sidebar' @{op='hide'}
$wheelPane=[string](Rpc 'session.split' @{op='on';axis='vertical'} $session)
$null=Rpc 'session.focus' @{dir='left'}
$lines=(1..100|ForEach-Object {"WHEEL-LINE-$_"})-join "`r`n"
$null=Rpc 'session.write' @{text=($esc+'[?1000h')} $session
$null=Rpc 'session.write' @{text=($esc+'[?1000l'+$lines)} $wheelPane
$wheelBefore=NavPixels 'wheel-under-pointer-before'
$wheelPoint=[NavWheel+POINT]::new();$wheelPoint.x=[int]($wheelBefore.width*0.75);$wheelPoint.y=[int]($wheelBefore.height*0.5)
if(-not [NavWheel]::ClientToScreen($hwnd,[ref]$wheelPoint)){throw 'Wheel coordinate conversion failed'}
$wheelPosition=[IntPtr]([int64](($wheelPoint.y-band 0xffff)-shl 16)-bor ($wheelPoint.x-band 0xffff))
[void][HudOwnedJob]::SendMessageW($hwnd,0x20a,[IntPtr](120-shl 16),$wheelPosition)
$wheelAfter=NavPixels 'wheel-under-pointer-after'
Check 'wheel scrolls unfocused history while focused sibling reports mouse' ((PixelDifference $wheelBefore $wheelAfter ([int]($wheelBefore.width*0.55)) 80 ([int]($wheelBefore.width*0.4)) ([int]($wheelBefore.height*0.65)))-gt 100)
$null=Rpc 'session.write' @{text=($esc+'[?1000l')} $session
$wheelReady=Join-Path $artifact 'wheel-reader-ready';$wheelBytes=Join-Path $artifact 'wheel-reader-bytes'
$wheelScript=@'
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class RawWheel { [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n); [DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr h,out uint m); [DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr h,uint m); }'
$inputHandle=[RawWheel]::GetStdHandle(-10);[uint32]$oldMode=0
if(-not [RawWheel]::GetConsoleMode($inputHandle,[ref]$oldMode)){throw 'GetConsoleMode'}
if(-not [RawWheel]::SetConsoleMode($inputHandle,($oldMode-band (-bnot 6))-bor 512)){throw 'SetConsoleMode'}
try {
    $inputStream=[Console]::OpenStandardInput();$bytes=[Collections.Generic.List[byte]]::new();$one=[byte[]]::new(1)
    [IO.File]::WriteAllText('__READY__','ready')
    while($bytes.Count-lt 256 -and $inputStream.Read($one,0,1)-eq 1){$bytes.Add($one[0]);if($one[0]-eq 77){break}}
    [IO.File]::WriteAllText('__BYTES__',[Text.Encoding]::ASCII.GetString($bytes.ToArray()))
    Start-Sleep 600
} finally {[void][RawWheel]::SetConsoleMode($inputHandle,$oldMode)}
'@
$wheelScript=$wheelScript.Replace('__READY__',$wheelReady.Replace("'","''")).Replace('__BYTES__',$wheelBytes.Replace("'","''"))
$wheelEncoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($wheelScript))
$null=Rpc 'session.type' @{text="powershell.exe -NoLogo -NoProfile -EncodedCommand $wheelEncoded`r"} $wheelPane
for($i=0;$i-lt 300 -and -not (Test-Path $wheelReady);$i++){Start-Sleep -Milliseconds 100}
if(-not (Test-Path $wheelReady)){throw 'Owned raw-input mouse reader was not ready'}
$null=Rpc 'session.write' @{text=($esc+'[?1000h'+$esc+'[?1006h'+$lines)} $wheelPane
$reportBefore=NavPixels 'wheel-report-before'
[void][HudOwnedJob]::SendMessageW($hwnd,0x20a,[IntPtr](120-shl 16),$wheelPosition)
Check 'wheel report reaches the unfocused raw-input reader' (NavWait {(Test-Path $wheelBytes) -and (Get-Content $wheelBytes -Raw)-match '^\x1b\[<64;[0-9]+;[0-9]+M$'})
$reportAfter=NavPixels 'wheel-report-after'
Check 'reported wheel leaves the hovered history viewport unchanged' ((PixelDifference $reportBefore $reportAfter ([int]($reportBefore.width*0.55)) 80 ([int]($reportBefore.width*0.4)) ([int]($reportBefore.height*0.65)))-lt 100)
$null=Rpc 'session.split.close' @{} $wheelPane
$null=Rpc 'sidebar' @{op='show'}
Check 'readonly rejects an unknown operation without changing protection' (-not (Rpc 'session.readonly' @{op='typo'} $session -AllowError).ok -and (Rpc 'session.readonly' @{op='state'} $session)-eq 'off')
$null=Rpc 'session.readonly' @{op='on'} $session
Check 'readonly typo cannot remove existing protection' (-not (Rpc 'session.readonly' @{op='typo'} $session -AllowError).ok -and (Rpc 'session.readonly' @{op='get'} $session)-eq 'on')
$null=Rpc 'session.readonly' @{op='off'} $session
Check 'missing readonly target refuses without protecting the active pane' (-not (Rpc 'session.readonly' @{op='on'} 'missing-pane' -AllowError).ok -and (Rpc 'session.readonly' @{op='state'} $session)-eq 'off')
$null=Rpc 'session.write' @{text="`r`nSTABILIZATION-SEARCH-MARKER`r`n"} $session
$null=Rpc 'config.set' @{key='cursor-blink';value='false'} # FIFO barrier: search's sent message can overtake the posted write.
$findBefore=Rpc 'session.search' @{query='STABILIZATION-SEARCH-MARKER'} $session
Check 'search mutation guard has a real match' ($findBefore-match 'of [1-9]')
Check 'missing search target refuses without replacing active query' (-not (Rpc 'session.search' @{query='MISSING-SEARCH-QUERY'} 'missing-pane' -AllowError).ok -and (Rpc 'session.search' @{} $session)-eq $findBefore)
$null=Rpc 'session.search' @{action='close'} $session
Check 'unknown switch operation refuses' (-not (Rpc 'session.switch' @{op='typo'} -NoTarget -AllowError).ok)
$mixedCommand='Tool --Path C:/CaseSensitive/Project --Key AbC'
$null=Rpc 'session.bind' @{agent=$mixedCommand} $session
Check 'binding preserves command case in the saved pane' (NavWait {
    foreach($file in Get-ChildItem (Join-Path $appDir 'windows') -Filter '*.json') {
        try {$saved=Get-Content $file.FullName -Raw|ConvertFrom-Json} catch {continue}
        foreach($ws in $saved.Workspaces){foreach($ses in $ws.Sessions){foreach($pane in $ses.Panes){
            if($pane.Id-eq $session -and $pane.AgentResume-ceq $mixedCommand){return $true}
        }}}
    };return $false
})
$null=Rpc 'session.bind' @{agent='NoNe'} $session
Check 'one workspace navigation refuses' (-not (Rpc 'workspace.go' @{to='next'} -NoTarget -AllowError).ok)
Check 'last workspace deletion refuses without removing its terminal' (-not (Rpc 'workspace.delete' @{} $first -AllowError).ok -and @(NavTree).Count-eq 1 -and $null-ne (Node $session))
$second=[string](Rpc 'workspace.new' @{name='P15-empty'})
Check 'empty workspace created' (NavWait {@(NavTree|Where-Object id -eq $second).Count-eq 1})
Check 'go reaches empty workspace and reports id' ((Rpc 'workspace.go' @{to='next'} -NoTarget)-eq $second -and (CurrentWs)-eq $second)
Check 'empty destination leaves selected terminal intact' ((Rpc 'window.state').activeWorkspace-eq 'P15-empty' -and (Node $session).active)
$placed=[string](Rpc 'session.new' @{name='P15-placed';wait=$true})
Check 'new session without caller lands in current empty workspace' (NavWait {@((NavTree|Where-Object id -eq $second).sessions|Where-Object id -eq $placed).Count-eq 1 -and (Node $placed).active})
foreach($badOp in 'typo',$false,42,$null,@(),@{}) {
    Check 'invalid switch operation preserves focus with two live sessions' (-not (Rpc 'session.switch' @{op=$badOp} -NoTarget -AllowError).ok -and (Node $placed).active)
    $null=Rpc 'session.readonly' @{op='on'} $placed
    Check 'malformed readonly operation preserves protection' (-not (Rpc 'session.readonly' @{op=$badOp} $placed -AllowError).ok -and (Rpc 'session.readonly' @{op='state'} $placed)-eq 'on')
}
$null=Rpc 'session.readonly' @{op='off'} $placed
$refusalName=([string][char]1)+'refuse:valid-name'
$null=Rpc 'session.rename' @{name=$refusalName} $placed
$switchReply=Rpc 'session.switch' @{op='begin'} -NoTarget -AllowError
Check 'valid session name cannot turn switch success into an error' ($switchReply.ok -and $switchReply.result-ceq $refusalName)
$null=Rpc 'session.switch' @{op='cancel'} -NoTarget
$null=Rpc 'session.rename' @{name='P15-placed'} $placed
$null=Rpc 'workspace.collapse' @{} $second
Check 'collapsed workspace readback' (NavWait {(NavTree|Where-Object id -eq $second).collapsed})
Check 'previous wraps without skipping collapsed destinations' ((Rpc 'workspace.go' @{to='prev'} -NoTarget)-eq $first -and (Rpc 'workspace.go' @{to='next'} -NoTarget)-eq $second)
$null=Rpc 'workspace.select' @{} $first
NavKey 116 # F5 (F6 remains the reserved accessibility focus-zone gesture)
Check 'first alternative chord navigates' ((CurrentWs)-eq $second)
NavKey 118 # F7
Check 'second alternative chord navigates and wraps' ((CurrentWs)-eq $first)
NavKey 119 # F8
Check 'previous workspace keymap navigates' ((CurrentWs)-eq $second)
NavKey 120 # F9
Check 'collapse action expands current workspace' (-not (NavTree|Where-Object id -eq $second).collapsed)
$null=Rpc 'sidebar' @{op='hide'}
NavKey 120
Check 'collapse action works while sidebar hidden' ((NavTree|Where-Object id -eq $second).collapsed)
$null=Rpc 'sidebar' @{op='show'}
NavKey 121;NavKey 65 # leader F10, A
Check 'first leader alternative navigates' ((CurrentWs)-eq $first)
NavKey 121;NavKey 66 # leader F10, B
Check 'second leader alternative navigates' ((CurrentWs)-eq $second)
NavKey 117 # reserved F6 enters sidebar, despite its conflicting map
NavKey 116 # F5 would navigate if the sidebar did not own input
Check 'reserved F6 owns sidebar keys ahead of conflicting mappings' ((CurrentWs)-eq $second)
NavKey 117 # back to terminal; must not execute next_workspace
Check 'reserved F6 returns without executing its map' ((CurrentWs)-eq $second)
$null=Rpc 'workspace.focus' @{op='on'}
Check 'single focused workspace refuses navigation' (-not (Rpc 'workspace.go' @{to='next'} -NoTarget -AllowError).ok)
$null=Rpc 'workspace.focus' @{op='off'}
$null=Rpc 'sidebar' @{op='mode:flagged'}
Check 'flagged mode refuses workspace navigation' (-not (Rpc 'workspace.go' @{to='prev'} -NoTarget -AllowError).ok)
$null=Rpc 'sidebar' @{op='mode:tree'}
Check 'explicit target refuses without moving' (-not (Rpc 'workspace.go' @{to='next'} $first -AllowError).ok -and (CurrentWs)-eq $second)
Check 'quick window refuses library navigation' (-not (Rpc 'workspace.go' @{to='next'} -NoTarget -Window quick -AllowError).ok)
$ctl=Join-Path $root 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$cli=& $ctl workspace go --to next --pipe $pipe --window active
Check 'CLI --to routes to workspace go' ($LASTEXITCODE-eq 0 -and ([string]$cli).Trim()-eq $first)
$cli=& $ctl workspace go prev --pipe $pipe --window active
Check 'CLI positional direction works' ($LASTEXITCODE-eq 0 -and ([string]$cli).Trim()-eq $second)
$null=& $ctl workspace go bogus --pipe $pipe --window active 2>$null
Check 'CLI invalid direction refuses before send' ($LASTEXITCODE-eq 2 -and (CurrentWs)-eq $second)
$empty=[string](Rpc 'workspace.new' @{name='P15-explicit-empty'})
Check 'third empty workspace materializes' (NavWait {@(NavTree|Where-Object id -eq $empty).Count-eq 1})
$null=Rpc 'workspace.select' @{} $empty
Check 'explicit empty selection reports current workspace' ((CurrentWs)-eq $empty)
$callerPlaced=[string](Rpc 'session.new' @{name='P15-caller';caller=$session;'no-select'=$true})
Check 'valid caller wins over empty placement without selecting' (NavWait {@((NavTree|Where-Object id -eq $first).sessions|Where-Object id -eq $callerPlaced).Count-eq 1 -and (CurrentWs)-eq $empty})
NavKey 114 # F3 = delete_workspace: current empty workspace, not selected terminal's workspace
Check 'delete action removes current empty workspace and preserves selected terminal' (NavWait {@(NavTree|Where-Object id -eq $empty).Count-eq 0 -and (CurrentWs)-eq $second -and $null-ne (Node $placed) -and $null-ne (Node $session)})
$empty=[string](Rpc 'workspace.new' @{name='P15-palette-delete'})
$null=NavWait {@(NavTree|Where-Object id -eq $empty).Count-eq 1}
$null=Rpc 'workspace.select' @{} $empty
NavKey 115 # F4 = action palette
foreach($letter in 'Delete Active Workspace'.ToCharArray()){[void][HudOwnedJob]::SendMessageW($hwnd,0x102,[IntPtr][int]$letter,[IntPtr]1)}
NavKey 13
Check 'palette delete uses current workspace and preserves both live workspaces' (NavWait {@(NavTree|Where-Object id -eq $empty).Count-eq 0 -and @(NavTree).Count-eq 2 -and $null-ne (Node $placed) -and $null-ne (Node $session)})
$empty=[string](Rpc 'workspace.new' @{name='P15-focused-delete'})
$null=NavWait {@(NavTree|Where-Object id -eq $empty).Count-eq 1}
$null=Rpc 'workspace.select' @{} $empty
$null=Rpc 'workspace.focus' @{op='on'}
$null=Rpc 'workspace.delete' @{} $empty
Check 'deleting focused workspace clears navigation filter' ((Rpc 'workspace.go' @{to='next'} -NoTarget)-eq $first)
$null=Rpc 'workspace.select' @{} $first
Check 'live shell name appears per pane and primary alias' (NavWait {(Node $session).foregroundShell-eq 'cmd' -and (Node $session).foregroundShells[0]-eq 'cmd'})
$split=[string](Rpc 'session.split' @{op='on'} $session)
Check 'split shell names follow pane order' (NavWait {(Node $session).splitForegroundShell-eq 'cmd' -and (Node $session).foregroundShells.Count-eq 2})
$null=Rpc 'session.split' @{op='off'} $session
$childDone=Join-Path $artifact 'child-done.txt'
$null=Rpc 'session.type' @{text="powershell.exe -NoProfile -Command Start-Sleep 2 & echo done>`"$childDone`"`r"} $session
Check 'child command makes foreground shell unknown' (NavWait {$null-eq (Node $session).foregroundShell})
Check 'shell hint returns after child completes' (NavWait {(Test-Path $childDone) -and (Node $session).foregroundShell-eq 'cmd'})
foreach($pair in @(@('cursor-style','nonsense'),@('cursor-blink','perhaps'),@('cursor-blink-ms','0'),@('cursor-blink-ms','x'))){
    $old=Rpc 'config.get' @{key=$pair[0]}
    Check "invalid $($pair[0]) refuses and preserves value" (-not (Rpc 'config.set' @{key=$pair[0];value=$pair[1]} -AllowError).ok -and (Rpc 'config.get' @{key=$pair[0]})-eq $old)
}
$null=Rpc 'config.set' @{key='cursor-blink';value='off'}
$null=Rpc 'config.set' @{key='cursor-blink-ms';value='60000'} # manual timer messages below, no clock-dependent samples
$null=Rpc 'session.type' @{text="echo P15-READY`r"} $session
Check 'shell output settled before cursor paint checks' (NavWait {([string](Rpc 'session.text' @{} $session))-match '(?m)^P15-READY\s*$'})
$null=Rpc 'session.write' @{text=([string][char]27+'[10;10H'+[char]27+'[0 q')} $session
[void][HudOwnedJob]::SendMessageW($hwnd,7,[IntPtr]::Zero,[IntPtr]::Zero)
$caret=[HudOwnedJob]::CaretRect($hwnd);$metrics=Rpc 'session.metrics' @{} $session
$null=Rpc 'config.set' @{key='cursor-style';value='bar'};$bar=NavPixels 'cursor-bar'
$null=Rpc 'config.set' @{key='cursor-style';value='block'};$block=NavPixels 'cursor-block'
Check 'cursor style changes rendered cell without resizing' ((PixelDifference $bar $block $caret.left $caret.top ($metrics.cellWidth+2) ($metrics.cellHeight+2))-gt 5 -and (Rpc 'session.metrics' @{} $session).cols-eq $metrics.cols)
$null=Rpc 'config.set' @{key='cursor-style';value='underline'};$under=NavPixels 'cursor-underline'
Check 'underline differs from block cursor' ((PixelDifference $block $under $caret.left $caret.top ($metrics.cellWidth+2) ($metrics.cellHeight+2))-gt 5)
[void][HudOwnedJob]::SendMessageW($hwnd,0x113,[IntPtr]1,[IntPtr]::Zero);$steady=NavPixels 'cursor-steady'
Check 'disabled blinking keeps cursor stable' ((PixelDifference $under $steady $caret.left $caret.top ($metrics.cellWidth+2) ($metrics.cellHeight+2))-eq 0)
$null=Rpc 'config.set' @{key='cursor-blink';value='true'}
$a=NavPixels 'cursor-blink-a';[void][HudOwnedJob]::SendMessageW($hwnd,0x113,[IntPtr]1,[IntPtr]::Zero);$b=NavPixels 'cursor-blink-b'
Check 'enabled blinking changes rendered cursor' ((PixelDifference $a $b $caret.left $caret.top ($metrics.cellWidth+2) ($metrics.cellHeight+2))-gt 0)
$null=Rpc 'session.write' @{text=([string][char]27+'[2 q')} $session # steady block overrides both config fields
$null=Rpc 'config.set' @{key='cursor-style';value='bar'}
$override=NavPixels 'cursor-decscusr-block'
Check 'DECSCUSR block overrides configured bar' ((PixelDifference $block $override $caret.left $caret.top ($metrics.cellWidth+2) ($metrics.cellHeight+2))-eq 0)
[void][HudOwnedJob]::SendMessageW($hwnd,0x113,[IntPtr]1,[IntPtr]::Zero)
$overrideTick=NavPixels 'cursor-decscusr-steady'
Check 'DECSCUSR steady overrides enabled configured blinking' ((PixelDifference $override $overrideTick $caret.left $caret.top ($metrics.cellWidth+2) ($metrics.cellHeight+2))-eq 0)
$null=Rpc 'session.write' @{text=([string][char]27+'[0 q')} $session
$null=Rpc 'config.set' @{key='cursor-blink';value='false'}
[void][HudOwnedJob]::SendMessageW($hwnd,8,[IntPtr]::Zero,[IntPtr]::Zero)
# Sidebar tooltip checks use only owned-window posted hover and screenshots, no desktop mouse movement.
$long='P15 workspace full truncated name — alpha beta gamma delta epsilon zeta eta theta'
$null=Rpc 'workspace.rename' @{name=$long} $first
$null=Rpc 'sidebar' @{op='width';width=160}
Start-Sleep -Milliseconds 100
$baseline=NavPixels 'tooltip-before'
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class NavDpi { [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h); }'
$dpiScale=[NavDpi]::GetDpiForWindow($hwnd)/96.0
$x=[int](40*$dpiScale);$y=[int](50*$dpiScale)
[void][HudOwnedJob]::SendMessageW($hwnd,0x200,[IntPtr]::Zero,[IntPtr](($y-shl 16)-bor $x))
Start-Sleep -Milliseconds 650
$tip=NavPixels 'tooltip-long-workspace'
Check 'truncated workspace name paints a tooltip' ((PixelDifference $baseline $tip)-gt 150)
[void][HudOwnedJob]::SendMessageW($hwnd,0x2a3,[IntPtr]::Zero,[IntPtr]::Zero)
$gone=NavPixels 'tooltip-left'
Check 'mouse leave clears tooltip' ((PixelDifference $tip $gone)-gt 150)
$null=Rpc 'workspace.rename' @{name='short'} $first
$short=NavPixels 'tooltip-short-before'
[void][HudOwnedJob]::SendMessageW($hwnd,0x200,[IntPtr]::Zero,[IntPtr](($y-shl 16)-bor $x))
Start-Sleep -Milliseconds 650
$shortAfter=NavPixels 'tooltip-short-after'
Check 'untruncated name does not paint a tooltip' ((PixelDifference $short $shortAfter)-eq 0)
[void][HudOwnedJob]::SendMessageW($hwnd,0x2a3,[IntPtr]::Zero,[IntPtr]::Zero)
$null=Rpc 'session.rename' @{name='P15 session full truncated name alpha beta gamma delta epsilon'} $session
$null=Rpc 'session.flag' @{op='on'} $session
$null=Rpc 'sidebar' @{op='mode:flagged'}
$sessionBefore=NavPixels 'tooltip-session-before'
$rowDip=[Math]::Max($metrics.cellHeight/$dpiScale+8,24)
$sy=[int]((50+$rowDip)*$dpiScale)
[void][HudOwnedJob]::SendMessageW($hwnd,0x200,[IntPtr]::Zero,[IntPtr](($sy-shl 16)-bor $x))
Start-Sleep -Milliseconds 650
$sessionTip=NavPixels 'tooltip-session-flagged'
Check 'flagged session truncated name paints tooltip' ((PixelDifference $sessionBefore $sessionTip)-gt 150)
$null=Rpc 'session.rename' @{name='short session'} $session
$renamed=NavPixels 'tooltip-session-renamed'
[void][HudOwnedJob]::SendMessageW($hwnd,0x2a3,[IntPtr]::Zero,[IntPtr]::Zero)
$renamedLeft=NavPixels 'tooltip-session-renamed-left'
Check 'renaming hovered row invalidates tooltip without a move' ((PixelDifference $renamed $renamedLeft)-eq 0)
$null=Rpc 'sidebar' @{op='mode:tree'}
$null=Rpc 'session.type' @{text="exit`r"} $session
Check 'exited shell does not retain shell-name hint' (NavWait {$null-eq (Node $session).foregroundShell})

$fontOriginal=Rpc 'config.get' @{key='font-family'}
$fontSizeOriginal=Rpc 'config.get' @{key='font-size'}
$libraryWindow=[string](@((Rpc 'window.list').windows|Where-Object open)[0].id)
$installed=[Drawing.Text.InstalledFontCollection]::new()
try {
    $familyNames=@($installed.Families|ForEach-Object Name)
    $fallback=@('Cascadia Mono','Consolas','Lucida Console','Courier New'|Where-Object {$_-in $familyNames})[0]
    if(-not $fallback){throw 'No expected monospace face on integration runner'}
    $null=Rpc 'config.set' @{key='font-family';value=$fallback}
    $null=Rpc 'session.write' @{text=($esc+'[2J'+$esc+'[H'+$esc+'[1mBOLD FALLBACK 0123456789'+$esc+'[0m'+"`r`n"+$esc+'[3mITALIC FALLBACK 0123456789'+$esc+'[0m')} $session
    $styledExpected=NavPixels 'fallback-styled-expected'
    $fallbackMetrics=Rpc 'session.metrics' @{} $session|ConvertTo-Json -Compress
    $missing='Agwinterm-Missing-'+[guid]::NewGuid().ToString('N')
    $null=Rpc 'config.set' @{key='font-family';value=$missing}
    Check 'missing font uses the installed monospace fallback metrics' ((Rpc 'session.metrics' @{} $session|ConvertTo-Json -Compress)-eq $fallbackMetrics)
    Check 'font fallback preserves the requested preference' ((Rpc 'config.get' @{key='font-family'})-eq $missing)
    $styledActual=NavPixels 'fallback-styled-missing'
    Check 'missing font uses the same bold and italic glyphs' ((PixelDifference $styledExpected $styledActual 0 40 $styledExpected.width 120)-lt 100)
    # Consolas covers double-line corners; these must enter the text run, not stall it.
    $null=Rpc 'config.set' @{key='font-family';value='Consolas'}
    $null=Rpc 'session.write' @{text=($esc+'[2J'+$esc+'[H'+[string][char]0x2554+[string][char]0x2557+[string][char]0x255A+[string][char]0x255D)} $session
    Start-Sleep -Milliseconds 200
    $responsive=[NavWheel]::Responsive($hwnd)
    Check 'font-rendered unsupported box glyphs cannot stall the UI' $responsive
    if(-not $responsive){throw 'Render thread stalled; stop before any unbounded screenshot call'}
    $null=NavPixels 'unsupported-box-glyphs'
    $fontWindow=[string](Rpc 'window.new' @{name='Font geometry peer'})
    try {
        if(-not (NavWait {@((Rpc 'window.list').windows|Where-Object {$_.id-eq $fontWindow -and $_.open}).Count-eq 1})){throw 'Font peer window did not open'}
        $null=Rpc 'config.set' @{key='font-family';value='Courier New'} -Window $libraryWindow
        $sharedMetrics=Rpc 'session.metrics' -Window $fontWindow|ConvertTo-Json -Compress
        $null=Rpc 'font' @{op='reset'} -Window $fontWindow
        $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -Window $fontWindow # FIFO UI barrier behind font reset
        Check 'shared font change already regrids another library window' (NavWait {(Rpc 'session.metrics' -Window $fontWindow|ConvertTo-Json -Compress)-eq $sharedMetrics})
        # Verify again after another family change, and include the singleton quick surface.
        $null=Rpc 'quick' @{op='on'} -Window $libraryWindow
        $null=Rpc 'config.set' @{key='font-family';value='Consolas'} -Window $libraryWindow
        $quickMetrics=Rpc 'session.metrics' -Window quick|ConvertTo-Json -Compress
        $null=Rpc 'font' @{op='reset'} -Window quick
        $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -Window quick
        Check 'shared font change already regrids quick' (NavWait {(Rpc 'session.metrics' -Window quick|ConvertTo-Json -Compress)-eq $quickMetrics})
        $peerBefore=Rpc 'session.metrics' -Window $fontWindow
        $quickBefore=Rpc 'session.metrics' -Window quick
        $null=Rpc 'config.set' @{key='font-size';value=([int]$fontSizeOriginal+3).ToString()} -Window $libraryWindow
        Check 'live default font size reaches existing peer and quick panes' ((Rpc 'session.metrics' -Window $fontWindow).cellHeight-gt $peerBefore.cellHeight -and (Rpc 'session.metrics' -Window quick).cellHeight-gt $quickBefore.cellHeight)
        $null=Rpc 'font' @{op='inc'} -Window $fontWindow
        $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -Window $fontWindow
        $zoomed=Rpc 'session.metrics' -Window $fontWindow
        $null=Rpc 'config.set' @{key='font-size';value=([int]$fontSizeOriginal+5).ToString()} -Window $libraryWindow
        Check 'live default preserves explicit peer zoom' ((Rpc 'session.metrics' -Window $fontWindow).cellHeight-eq $zoomed.cellHeight)
        $null=Rpc 'font' @{op='reset'} -Window $fontWindow
        $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -Window $fontWindow
        Check 'reset resumes current default size' ((Rpc 'session.metrics' -Window $fontWindow).cellHeight-eq (Rpc 'session.metrics' -Window quick).cellHeight)
        $fontOwner=[string](Rpc 'tree' -Window $fontWindow).workspaces[0].sessions[0].id
        foreach($surfaceKind in 'scratch','pane overlay','session overlay'){
            $null=Rpc 'config.set' @{key='font-size';value='20'} -Window $libraryWindow
            if($surfaceKind-eq 'scratch'){$null=Rpc 'session.scratch' @{op='on'} $fontOwner -Window $fontWindow}
            else{
                $overlayArgs=@{action='open';command='cmd /d /k echo FONT-SURFACE-READY'}
                if($surfaceKind-eq 'pane overlay'){$overlayArgs.pane='left'}
                $null=Rpc 'session.overlay' $overlayArgs $fontOwner -Window $fontWindow
            }
            $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -Window $fontWindow
            $beforeSurface=Rpc 'session.metrics' -Window $fontWindow
            $null=Rpc 'config.set' @{key='font-size';value='22'} -Window $libraryWindow
            Check "inherited $surfaceKind follows live font default" ((Rpc 'session.metrics' -Window $fontWindow).cellHeight-gt $beforeSurface.cellHeight)
            $null=Rpc 'font' @{op='inc'} -Window $fontWindow
            $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -Window $fontWindow
            $surfaceZoom=Rpc 'session.metrics' -Window $fontWindow
            $null=Rpc 'config.set' @{key='font-size';value='24'} -Window $libraryWindow
            Check "explicit $surfaceKind zoom survives default change" ((Rpc 'session.metrics' -Window $fontWindow).cellHeight-eq $surfaceZoom.cellHeight)
            $null=Rpc 'font' @{op='reset'} -Window $fontWindow
            $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -Window $fontWindow
            Check "$surfaceKind reset resumes inheritance" ((Rpc 'session.metrics' -Window $fontWindow).cellHeight-eq (Rpc 'session.metrics' -Window quick).cellHeight)
            if($surfaceKind-eq 'scratch'){$null=Rpc 'session.scratch' @{op='off'} $fontOwner -Window $fontWindow}
            else{
                $closeArgs=@{action='close'};if($surfaceKind-eq 'pane overlay'){$closeArgs.pane='left'}
                $null=Rpc 'session.overlay' $closeArgs $fontOwner -Window $fontWindow
            }
            $null=Rpc 'config.set' @{key='cursor-blink';value='false'} -Window $fontWindow
        }
        $null=Rpc 'quick' @{op='off'} -Window $libraryWindow
    } finally {
        $null=Rpc 'window.close' @{} $fontWindow
        $null=Rpc 'window.select' @{} $libraryWindow
    }
} finally {
    $installed.Dispose()
    $null=Rpc 'config.set' @{key='font-family';value=$fontOriginal} -Window $libraryWindow
    $null=Rpc 'config.set' @{key='font-size';value=$fontSizeOriginal} -Window $libraryWindow
}
. "$PSScriptRoot/uia-window-cases.ps1"
