# Memory-leak churn harness for agwinterm.
#
# Drives the app through repeated create/destroy cycles (sessions, overlays, splits, themes,
# window resizes, heavy output) via agwintermctl, sampling working set, private bytes, kernel
# handles, and GDI/USER handles between phases. A leak shows up as monotonic growth that
# repeats on a SECOND round of the same churn (round 1 growth alone can be warm caches / lazy GC).
#
# Usage:  pwsh tools/profile-memory.ps1 [-Exe <path>] [-Ctl <path>] [-Cycles 30]
param(
    [string]$Exe = "$PSScriptRoot\..\src\Agwinterm.Win32\bin\Debug\net10.0-windows\win-x64\Agwinterm.Win32.exe",
    [string]$Ctl = "$PSScriptRoot\..\src\Agwinterm.Ctl\bin\Debug\net10.0-windows\agwintermctl.exe",
    [int]$Cycles = 30
)
$ErrorActionPreference = 'SilentlyContinue'

Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);' -Name Gui -Namespace Prof

$script:Samples = @()
function Sample([string]$label) {
    $p = Get-Process Agwinterm.Win32 | Select-Object -First 1
    if (-not $p) { Write-Warning "app gone at '$label'"; return }
    $p.Refresh()
    $s = [pscustomobject]@{
        Phase    = $label
        WS_MB    = [math]::Round($p.WorkingSet64 / 1MB, 1)
        Priv_MB  = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        Handles  = $p.HandleCount
        GDI      = [Prof.Gui]::GetGuiResources($p.Handle, 0)
        USER     = [Prof.Gui]::GetGuiResources($p.Handle, 1)
        Threads  = $p.Threads.Count
    }
    $script:Samples += $s
    Write-Host ("{0,-28} WS={1,7}MB priv={2,7}MB handles={3,5} gdi={4,4} user={5,4} thr={6,3}" -f
        $s.Phase, $s.WS_MB, $s.Priv_MB, $s.Handles, $s.GDI, $s.USER, $s.Threads)
}

function ChurnSessions([string]$tag) {
    for ($i = 0; $i -lt $Cycles; $i++) {
        $id = (& $Ctl session new --name "churn$i" | Out-String).Trim()
        & $Ctl session close --target $id 2>$null | Out-Null
    }
    Start-Sleep -Seconds 2; Sample "sessions x$Cycles $tag"
}
function ChurnOverlays([string]$tag) {
    for ($i = 0; $i -lt [math]::Max(10, $Cycles / 2); $i++) {
        & $Ctl session overlay open 'cmd /c echo overlay' --size-percent 40 2>$null | Out-Null
        Start-Sleep -Milliseconds 120
        & $Ctl session overlay close 2>$null | Out-Null
    }
    Start-Sleep -Seconds 2; Sample "overlays $tag"
}
function ChurnSplits([string]$tag) {
    for ($i = 0; $i -lt $Cycles; $i++) {
        & $Ctl session split on 2>$null | Out-Null
        & $Ctl session split off 2>$null | Out-Null
    }
    Start-Sleep -Seconds 2; Sample "splits x$Cycles $tag"
}
function ChurnThemes([string]$tag) {
    $themes = (& $Ctl theme list | Out-String) -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 10
    for ($i = 0; $i -lt 20; $i++) { & $Ctl theme set $themes[$i % $themes.Count].Trim() 2>$null | Out-Null }
    Start-Sleep -Seconds 2; Sample "themes x20 $tag"
}
function ChurnResize([string]$tag) {
    for ($i = 0; $i -lt $Cycles; $i++) {
        $w = 900 + ($i % 6) * 120; $h = 600 + ($i % 5) * 90
        & $Ctl window resize active $w $h 2>$null | Out-Null
    }
    Start-Sleep -Seconds 2; Sample "resize x$Cycles $tag"
}
function HeavyOutput([string]$tag) {
    # ~60k lines through one PTY: scrollback fills to its cap; growth beyond one buffer = leak.
    $id = (& $Ctl session new --name blaster --command 'powershell -NoProfile -Command "1..60000 | ForEach-Object { \"line $_ with some padding text to make it wider\" }; Start-Sleep 2"' | Out-String).Trim()
    Start-Sleep -Seconds 14
    Sample "heavy output (open) $tag"
    & $Ctl session close --target $id 2>$null | Out-Null
    Start-Sleep -Seconds 2; Sample "heavy output (closed) $tag"
}

# ---- run ----
Get-Process Agwinterm.Win32 | Stop-Process -Force; Start-Sleep 1
Start-Process $Exe
for ($i = 0; $i -lt 15; $i++) { Start-Sleep 1; if ((& $Ctl ping) -match 'agwinterm') { break } }
Start-Sleep -Seconds 3
Sample "baseline"

ChurnSessions "R1"; ChurnOverlays "R1"; ChurnSplits "R1"; ChurnThemes "R1"; ChurnResize "R1"; HeavyOutput "R1"
Write-Host "`n-- idle 15s (GC settle) --"; Start-Sleep -Seconds 15; Sample "idle after R1"

# Round 2: growth here (over 'idle after R1') is the leak signal — caches are already warm.
ChurnSessions "R2"; ChurnOverlays "R2"; ChurnSplits "R2"; ChurnThemes "R2"; ChurnResize "R2"; HeavyOutput "R2"
Write-Host "`n-- idle 15s --"; Start-Sleep -Seconds 15; Sample "idle after R2"

Write-Host "`n===== summary ====="
$script:Samples | Format-Table -AutoSize
$r1 = $script:Samples | Where-Object Phase -eq "idle after R1"
$r2 = $script:Samples | Where-Object Phase -eq "idle after R2"
if ($r1 -and $r2) {
    Write-Host ("round-2 delta (leak signal): WS {0:+0.0;-0.0}MB priv {1:+0.0;-0.0}MB handles {2:+0;-0} gdi {3:+0;-0} user {4:+0;-0} thr {5:+0;-0}" -f
        ($r2.WS_MB - $r1.WS_MB), ($r2.Priv_MB - $r1.Priv_MB), ($r2.Handles - $r1.Handles),
        ($r2.GDI - $r1.GDI), ($r2.USER - $r1.USER), ($r2.Threads - $r1.Threads))
}
Get-Process Agwinterm.Win32 | Stop-Process -Force
