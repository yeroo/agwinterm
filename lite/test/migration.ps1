# agliteterm adopts a 0.17.x agwinterm-lite profile.
#
# The rename moved four things that belong to the USER: saved sessions, the .bak generation, the
# font/colour settings and the keybindings. Without migration the window comes up empty, which is
# indistinguishable from the data loss the restore matrix exists to prevent.
#
# Runs against a THROWAWAY %LOCALAPPDATA%. Migration is a whole-profile, once-ever adoption keyed on
# "does agliteterm have any session state yet", so it cannot be exercised in a profile that already
# has some — and pointing it at the real one would both fail and scribble on real state.
param([string]$Exe = "$PSScriptRoot\..\bin\agliteterm.exe")

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$ctl = "$env:LOCALAPPDATA\Programs\agwinterm\agwintermctl.exe"
$fail = 0
function Check([string]$n, $ok, [string]$d = '') {
    if ([bool]$ok) { "  PASS  $n" } else { $script:fail++; "  FAIL  $n$(if ($d) { " - $d" })" }
}
# EXACT bytes, no BOM: a stray BOM hides the V1 header, and a stray backtick lands inside a field.
function Write-State([string]$Path, [string[]]$Lines) {
    [IO.File]::WriteAllText($Path, (($Lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
}
function Start-Lite([string]$inst, [string]$root) {
    $p = Start-Process $Exe -ArgumentList @('--pipe', $inst) -PassThru -Environment @{ LOCALAPPDATA = $root }
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 400
        if (((& $ctl tree --json --pipe $inst 2>&1) -join '') -match '"ok":true') { return $p }
    }
    throw "agliteterm ($inst) did not answer its control pipe"
}
function Stop-Lite($p) {
    $p.CloseMainWindow() | Out-Null
    for ($i = 0; $i -lt 25; $i++) { Start-Sleep -Milliseconds 400; $p.Refresh(); if ($p.HasExited) { break } }
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    Start-Sleep -Seconds 1
}

"== migration =="
$inst = 'migchk'
$root = Join-Path ([IO.Path]::GetTempPath()) ("agl-mig-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$new  = Join-Path $root 'agliteterm'
$old  = Join-Path $root 'agwinterm-lite'
New-Item -ItemType Directory $old -Force | Out-Null
$TAB = [char]9
# S line: ws, name, app (empty), cwd -> four tabs, or it parses to nothing and restores no session
Write-State "$old\sessions-$inst.tsv" @('V1', "W${TAB}legacy-ws", "S${TAB}0${TAB}inherited-session${TAB}${TAB}$PWD", "A${TAB}0")
Write-State "$old\sessions-$inst.tsv.bak" @('V1', "W${TAB}legacy-ws", "S${TAB}0${TAB}older-generation${TAB}${TAB}$PWD", "A${TAB}0")

try {
    # --- 1. a 0.17.x profile, and no agliteterm state at all ---------------------------------
    $p = Start-Lite $inst $root
    Start-Sleep -Seconds 2
    $tree = (& $ctl tree --json --pipe $inst 2>&1) -join ''
    Check 'the legacy session was adopted'    ($tree -match 'inherited-session') $tree
    Check 'the legacy workspace came with it' ($tree -match 'legacy-ws')
    Check 'the .bak generation came too'      (Test-Path "$new\sessions-$inst.tsv.bak")
    Check 'the log names the adoption'        ((Get-Content "$new\agliteterm-$inst.log" -Raw) -match 'migrate: adopted')
    Check 'legacy profile COPIED, not moved'  (Test-Path "$old\sessions-$inst.tsv")
    Stop-Lite $p

    # --- 2. agliteterm state already present -> it wins, legacy untouched --------------------
    Write-State "$new\sessions-$inst.tsv" @('V1', "W${TAB}new-ws", "S${TAB}0${TAB}new-session${TAB}${TAB}$PWD", "A${TAB}0")
    $p2 = Start-Lite $inst $root
    Start-Sleep -Seconds 2
    $tree2 = (& $ctl tree --json --pipe $inst 2>&1) -join ''
    Check 'existing agliteterm state wins' (($tree2 -match 'new-session') -and -not ($tree2 -match 'inherited-session')) $tree2
    Check 'legacy state left alone'        ((Get-Content "$old\sessions-$inst.tsv" -Raw) -match 'inherited-session')
    Stop-Lite $p2

    # --- 3. the deprecated pipe name still answers -------------------------------------------
    $p3 = Start-Lite 'agliteterm' $root      # default instance: both listeners run
    $viaOld = (& $ctl tree --json --pipe agwinterm-lite 2>&1) -join ''
    Check 'the 0.17.x pipe name still answers' ($viaOld -match '"ok":true') $viaOld
    Check 'the alias use is logged' ((Get-Content "$new\agliteterm.log" -Raw) -match 'legacy pipe name')
    Stop-Lite $p3
} finally {
    Get-Process -Name 'agliteterm' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq (Resolve-Path $Exe).Path } | Stop-Process -Force -ErrorAction SilentlyContinue
    # The pty-host outlives the UI by design. Left running it holds liteingwinterm-ptyhost.exe
    # open, and the NEXT build fails copying over it - which is how this suite broke the build once.
    Start-Sleep -Seconds 1
    Get-Process -Name 'agwinterm-ptyhost' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($fail) { "migration: $fail FAILED"; exit 1 } else { "migration: all passed"; exit 0 }
