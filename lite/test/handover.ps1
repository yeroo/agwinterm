# handover to agliteterm — Task 4 checks.
#
# This build is the LAST agwinterm-lite: its updater points at the agliteterm feed so existing
# installs discover the successor through the mechanism they already trust. What that has to get
# right, and what this file asserts:
#   - it picks the agliteterm-setup asset, NOT a lite-setup asset sitting in the same release
#   - the prompt says this is a rename, not a routine update
#   - the relaunch starts agliteterm, not this exe — the successor installs ALONGSIDE under its own
#     AppId, so relaunching GetModuleFileNameW() would leave the user staring at the old build,
#     concluding the update did nothing
#   - a missing successor falls back to relaunching this build, never to no terminal at all
#   - an unreachable feed changes nothing and nags nobody
#
# Suite rules (learned the hard way, same as the other lite checks):
#   - always a sandbox instance (--pipe <name>); never the default instance, which owns real state
#   - never inject global input (keybd_event/SendInput) — it lands wherever focus happens to be
#   - drive the app with PostMessage to ITS OWN window handles only
#
# Runs against a throwaway %LOCALAPPDATA%: the update path is gated on the exe living in
# <LOCALAPPDATA>\Programs\agwinterm-lite (updChannelInstalled), so the check needs an install
# layout it can build and delete — and pointing the updater at a real profile would write payloads
# into it.
param([string]$Exe = "$PSScriptRoot\..\bin\agwinterm-lite.exe")

$ErrorActionPreference = 'Stop'
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name$(if ($detail) { " — $detail" })" }
}

"== handover =="

# EnumWindows, not a FindWindowEx walk: FindWindowExW(NULL, prev, NULL, NULL) returns nothing on
# Windows 11 with a null class, so the walk silently enumerated zero windows and every dialog check
# "passed" as absent. Compiled as a type so the callback delegate stays alive for the call.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class HoWin {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetDlgItemTextW(IntPtr d, int id, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    static string Text(IntPtr h) { var b = new StringBuilder(512); GetWindowTextW(h, b, b.Capacity); return b.ToString(); }
    // A MessageBox is class #32770. Returns "<hwnd>|<title>|<body>" for the first one this pid owns.
    public static string Dialog(int pid) {
        string found = null;
        EnumWindows((h, l) => {
            uint owner; GetWindowThreadProcessId(h, out owner);
            if (owner != (uint)pid) return true;
            var c = new StringBuilder(64); GetClassNameW(h, c, c.Capacity);
            if (c.ToString() != "#32770") return true;
            var body = new StringBuilder(4096); GetDlgItemTextW(h, 0xFFFF, body, body.Capacity);
            found = h.ToInt64() + "|" + Text(h) + "|" + body.ToString();
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@

function Get-Dialog([int]$ownerPid) {
    $raw = [HoWin]::Dialog($ownerPid)
    if (-not $raw) { return $null }
    $parts = $raw -split '\|', 3
    return [pscustomobject]@{ Handle = [IntPtr][int64]$parts[0]; Title = $parts[1]; Body = $parts[2] }
}
# Waits for the dialog to be READABLE, not merely to exist. A MessageBox creates its window before
# its static text child, so a reader that returns on the window alone intermittently gets an empty
# body — and then every assertion about what the prompt SAYS fails while the title still matches.
# That is the shape of the flake this suite hit: 4 body checks red, everything around them green.
function Wait-Dialog([int]$ownerPid, [int]$seconds = 25, [switch]$AllowEmptyBody) {
    for ($i = 0; $i -lt $seconds * 4; $i++) {
        $d = Get-Dialog $ownerPid
        if ($d -and ($AllowEmptyBody -or $d.Body)) { return $d }
        Start-Sleep -Milliseconds 250
    }
    # One last read, so a genuinely bodyless dialog is REPORTED rather than silently timed out.
    return Get-Dialog $ownerPid
}
# Start-Process returns before the window exists, and the cached MainWindowHandle stays 0 until the
# object is refreshed — posting to IntPtr.Zero is a silent no-op, which reads exactly like "the app
# ignored the menu command".
function Wait-Window($proc, [int]$seconds = 30) {
    for ($i = 0; $i -lt $seconds * 4; $i++) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return $proc.MainWindowHandle }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}
function Close-Dialog($dlg, [int]$button) { [void][HoWin]::PostMessageW($dlg.Handle, 0x0111, [IntPtr]$button, [IntPtr]::Zero) }

$root = Join-Path ([IO.Path]::GetTempPath()) ("lite-handover-" + [Guid]::NewGuid().ToString('N'))
$installed = Join-Path $root 'Programs\agwinterm-lite'
New-Item -ItemType Directory -Path $installed -Force | Out-Null
# The whole payload, not just the exes: lite refuses to start without agwinterm_core.dll beside
# it, and a half-populated install directory produces a startup error box that a naive dialog
# check happily mistakes for the update prompt.
Get-ChildItem (Split-Path $Exe) -File |
    Where-Object { $_.Extension -in '.exe', '.dll', '.ttf', '.otf' } |
    Copy-Item -Destination $installed
$app = Join-Path $installed 'agwinterm-lite.exe'
$updates = Join-Path $root 'agwinterm-lite\updates'

# The release the successor repo will actually publish: an agliteterm-setup asset. The DECOY comes
# first on purpose — a lite-setup asset in the same feed must not be what this build downloads,
# which is the whole point of retargeting the asset name alongside the URL.
$payload = Join-Path $root 'agliteterm-setup-0.17.5.exe'
Set-Content -Path $payload -Value 'pretend this is the agliteterm installer' -NoNewline
$sha = (Get-FileHash $payload -Algorithm SHA256).Hash.ToLower()
$decoy = Join-Path $root 'agwinterm-lite-setup-0.17.5.exe'
Set-Content -Path $decoy -Value 'the wrong asset' -NoNewline
$feed = Join-Path $root 'feed.json'
# Forward slashes: the payload URL is read as a literal path by updFetch's local-file seam, and a
# backslash in a JSON string would have to be escaped — which this substring parser does not undo.
@"
{"tag_name":"v0.17.5","assets":[
 {"name":"agwinterm-lite-setup-0.17.5.exe","browser_download_url":"$($decoy -replace '\\','/')","digest":"sha256:0000"},
 {"name":"agliteterm-setup-0.17.5.exe","browser_download_url":"$($payload -replace '\\','/')","digest":"sha256:$sha"}]}
"@ | Set-Content -Path $feed -Encoding utf8

$env0 = @{ LOCALAPPDATA = $root; AGWINTERM_VERSION_OVERRIDE = '0.17.4'; AGWINTERM_UPDATE_API = $feed }

# --- 1. an install of today's 0.17.x finds, downloads and offers the successor -----------------
$p = Start-Process $app -ArgumentList @('--pipe', 'handover') -PassThru -Environment $env0
Start-Sleep -Seconds 7
$hwnd = Wait-Window $p
Check 'the sandbox instance came up' ($hwnd -ne [IntPtr]::Zero)
[void][HoWin]::PostMessageW($hwnd, 0x0111, [IntPtr]131, [IntPtr]::Zero)   # Help -> Check for Updates
$dlg = Wait-Dialog $p.Id
Check 'the successor release is offered' ($null -ne $dlg) 'no dialog appeared'

if ($dlg) {
    # The prompt has to say what is happening. A user who reads "0.17.4 -> 0.17.5" and then finds a
    # differently-named app in their Start menu has been surprised by their terminal, which is the
    # one thing a terminal must never do.
    Check 'the prompt names the rename' ($dlg.Title -match 'now agliteterm')
    Check 'it says this is not a routine update' ($dlg.Body -match 'own product' -and $dlg.Body -match 'not a routine update')
    Check 'it promises the profile comes across' ($dlg.Body -match 'sessions, settings and fonts')
    Check 'it says the old build stays' ($dlg.Body -match 'stays installed')
    Check 'it still reports the verification' ($dlg.Body -match 'SHA-256')

    Check 'the agliteterm asset was downloaded' (Test-Path (Join-Path $updates 'agliteterm-setup-0.17.5.exe'))
    Check 'the lite-setup decoy was ignored' (-not (Test-Path (Join-Path $updates 'agwinterm-lite-setup-0.17.5.exe')))

    Close-Dialog $dlg 2   # IDCANCEL — declining must leave the install exactly as it was
    Start-Sleep -Seconds 2
    $p.Refresh()
    Check 'declining leaves lite running' (-not $p.HasExited)
}

$helper = Join-Path $updates 'apply-update.ps1'
Check 'the apply helper was written' (Test-Path $helper)

$p.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 3
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

# --- 2. the relaunch starts agliteterm, not this build -----------------------------------------
# Drives the helper the app just wrote — the real script, extracted from the real exe — with stand-in
# executables, so the assertion is about which one it starts rather than about Inno Setup.
if (Test-Path $helper) {
    $marker = Join-Path $root 'relaunched.txt'
    $setup = Join-Path $root 'setup.cmd'
    $old   = Join-Path $root 'old.cmd'
    $succ  = Join-Path $root 'successor.cmd'
    # %1/%2/%3 rather than %*: the interesting question is where the argument BOUNDARIES fall,
    # and a flat tail cannot tell "--pipe" "my win" from "--pipe" "my" "win".
    Set-Content $setup "@echo setup 1=[%1] 2=[%2] 3=[%3] >> `"$marker`"" -Encoding ascii
    Set-Content $old   "@echo old 1=[%1] 2=[%2] 3=[%3] >> `"$marker`"" -Encoding ascii
    Set-Content $succ  "@echo successor 1=[%1] 2=[%2] 3=[%3] >> `"$marker`"" -Encoding ascii

    function Run-Helper([string]$successor, [string]$instance) {
        Remove-Item $marker, "$setup.log" -ErrorAction SilentlyContinue
        $dead = Start-Process cmd.exe -ArgumentList '/c', 'exit' -PassThru -WindowStyle Hidden
        $dead.WaitForExit()
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $helper,
               '-ProcId', $dead.Id, '-Payload', $setup, '-Exe', $old)
        if ($successor) { $a += @('-Successor', $successor) }
        if ($instance)  { $a += @('-Instance', $instance) }
        # The call operator quotes each argument; Start-Process joins the list with spaces and quotes
        # nothing, which would split '-Instance my win' here and measure the harness, not the helper.
        & powershell.exe @a | Out-Null
        Start-Sleep -Seconds 2
        return @{
            Marker = $(if (Test-Path $marker) { Get-Content $marker -Raw } else { '' })
            Log    = $(if (Test-Path "$setup.log") { Get-Content "$setup.log" -Raw } else { '' })
        }
    }

    $r = Run-Helper $succ 'my win'
    Check 'the installer ran' ($r.Marker -match 'setup 1=\[/VERYSILENT\]') $r.Marker
    Check 'the successor is what gets relaunched' ($r.Marker -match '(?m)^successor ') $r.Marker
    Check 'this build is NOT relaunched' ($r.Marker -notmatch '(?m)^old ')
    # Long-standing behaviour worth keeping honest through the rename: a name with a space must
    # survive as ONE argument, or the app comes back on a different pipe AND a different state file.
    # Regression: passing the name as its own -ArgumentList element does NOT quote it, so the
    # relaunch used to arrive as --pipe my win — a different pipe and a different state file, i.e.
    # "the update ate my sessions" caused by the update itself.
    Check 'a spaced instance name survives' ($r.Marker -match '2=\["?my win"?\]') $r.Marker

    # --- 3. error case: the successor is not where we expect ----------------------------------
    $r2 = Run-Helper (Join-Path $root 'nope\agliteterm.exe') ''
    Check 'a missing successor falls back to this build' ($r2.Marker -match '(?m)^old ')
    Check 'and says so in the update log' ($r2.Log -match 'successor missing')
}

# --- 4. error case: the successor feed is unreachable -------------------------------------------
# Nothing to download, nothing to say. The check must not nag on startup and must not leave a
# half-written payload behind — an install that never sees the handover keeps working as it is.
Remove-Item (Join-Path $updates '*') -Recurse -ErrorAction SilentlyContinue
$envDown = @{ LOCALAPPDATA = $root; AGWINTERM_VERSION_OVERRIDE = '0.17.4'
              AGWINTERM_UPDATE_API = (Join-Path $root 'no-such-feed.json') }
$p2 = Start-Process $app -ArgumentList @('--pipe', 'handover-down') -PassThru -Environment $envDown
Start-Sleep -Seconds 12        # past the background check's 8s startup delay
$p2.Refresh()
Check 'an unreachable feed does not stop lite' (-not $p2.HasExited)
Check 'and does not nag on startup' ($null -eq (Get-Dialog $p2.Id))

[void][HoWin]::PostMessageW((Wait-Window $p2), 0x0111, [IntPtr]131, [IntPtr]::Zero)
$dlg2 = Wait-Dialog $p2.Id 15
Check 'an explicit check reports the failure' ($null -ne $dlg2 -and $dlg2.Body -match 'update check failed')
Check 'nothing was downloaded' (-not (Test-Path (Join-Path $updates '*setup*')))
if ($dlg2) { Close-Dialog $dlg2 1 }
Start-Sleep -Seconds 2

$p2.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 3
if (-not $p2.HasExited) { Stop-Process -Id $p2.Id -Force }

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue

if ($fail) { "handover: $fail FAILED"; exit 1 }
"handover: all passed"
exit 0
