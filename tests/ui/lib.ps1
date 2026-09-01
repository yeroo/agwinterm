# Shared plumbing for the UI checks — the ones that need a real window, real mouse buttons and a
# real modifier key, which no unit test can reach.
#
# Suite rules, and they are not negotiable:
#   - ALWAYS a sandbox instance: --pipe <name> AND --app-id <name>. A %LOCALAPPDATA% override does
#     NOT isolate agwinterm — .NET resolves LocalApplicationData through the known-folder API, so an
#     override is ignored and the app reads the real profile. --app-id is the seam that works.
#     Getting this wrong means a check writes to the user's session list.
#   - NEVER inject global input. No keybd_event, no SendInput. Everything is PostMessage to this
#     instance's own window handles, so whatever the user is typing in stays untouched. Ctrl+C is the
#     one thing PostMessage cannot express alone (the modifier must be visible to GetKeyState), and
#     it is done by attaching to THIS instance's input queue and setting the shared key state.
#   - Capture with PrintWindow, never CopyFromScreen, which grabs whatever is on top.
#
# Dot-source this, then Start-Sandbox / Send-Ctl / Stop-Sandbox.

# Build layouts differ by where the build ran: locally bin\Release\..., in CI bin\x64\Release\...
# because the solution config sets a platform. BOTH exist on a dev machine and only one is fresh —
# launching the stale root is how a fix once "proved" it did nothing. So binaries are FOUND, newest
# first, and a miss reports what it looked at.
function Resolve-Binary([string]$explicit, [string]$name, [string]$projectDir) {
    $roots = @()
    if ($explicit) { $roots += $explicit }
    $built = Join-Path (Join-Path $PSScriptRoot '..\..') "src\$projectDir\bin"
    if (Test-Path $built) {
        $roots += (Get-ChildItem $built -Recurse -Filter $name -ErrorAction SilentlyContinue |
                   Sort-Object LastWriteTime -Descending | Select-Object -ExpandProperty FullName)
    }
    $roots += "$env:LOCALAPPDATA\Programs\agwinterm\$name"
    foreach ($r in $roots) { if ($r -and (Test-Path $r)) { return $r } }
    return $null
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AgwUi {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] s);
    [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] s);

    static IntPtr Pt(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }

    /// <summary>Press, drag, release. Client coordinates, like the button messages carry.</summary>
    public static void Drag(IntPtr h, int x1, int y1, int x2, int y2) {
        PostMessageW(h, 0x0201, (IntPtr)1, Pt(x1, y1));
        for (int i = 1; i <= 8; i++) {
            PostMessageW(h, 0x0200, (IntPtr)1, Pt(x1 + (x2 - x1) * i / 8, y1 + (y2 - y1) * i / 8));
            System.Threading.Thread.Sleep(40);
        }
        PostMessageW(h, 0x0202, IntPtr.Zero, Pt(x2, y2));
        System.Threading.Thread.Sleep(250);
    }

    /// <summary>As Drag, but HOLDS the button outside the pane for holdMs first. Drag-autoscroll only
    /// ticks while the button is down and the cursor is off the pane: release too early and its 50ms
    /// timer never runs, so a check that means to exercise it silently exercises nothing.</summary>
    public static void DragHold(IntPtr h, int x1, int y1, int x2, int y2, int holdMs) {
        PostMessageW(h, 0x0201, (IntPtr)1, Pt(x1, y1));
        for (int i = 1; i <= 8; i++) {
            PostMessageW(h, 0x0200, (IntPtr)1, Pt(x1 + (x2 - x1) * i / 8, y1 + (y2 - y1) * i / 8));
            System.Threading.Thread.Sleep(40);
        }
        for (int i = 0; i < holdMs / 100; i++) {
            PostMessageW(h, 0x0200, (IntPtr)1, Pt(x2, y2));
            System.Threading.Thread.Sleep(100);
        }
        PostMessageW(h, 0x0202, IntPtr.Zero, Pt(x2, y2));
        System.Threading.Thread.Sleep(250);
    }

    public static void Click(IntPtr h, int button, int x, int y) {
        uint down = button == 2 ? 0x0204u : 0x0201u, up = button == 2 ? 0x0205u : 0x0202u;
        PostMessageW(h, down, (IntPtr)(button == 2 ? 2 : 1), Pt(x, y));
        System.Threading.Thread.Sleep(120);
        PostMessageW(h, up, IntPtr.Zero, Pt(x, y));
        System.Threading.Thread.Sleep(300);
    }

    /// <summary>WM_MOUSEWHEEL carries SCREEN coordinates, unlike the button messages.</summary>
    public static void Wheel(IntPtr h, int cx, int cy, int notches) {
        var p = new POINT(); p.X = cx; p.Y = cy; ClientToScreen(h, ref p);
        for (int i = 0; i < notches; i++) {
            PostMessageW(h, 0x020A, (IntPtr)(120 << 16), Pt(p.X, p.Y));
            System.Threading.Thread.Sleep(60);
        }
        System.Threading.Thread.Sleep(300);
    }

    /// <summary>A key with no modifiers, n times.</summary>
    public static void Key(IntPtr h, int vk, int times) {
        for (int i = 0; i < times; i++) {
            PostMessageW(h, 0x0100, (IntPtr)vk, (IntPtr)1);
            PostMessageW(h, 0x0101, (IntPtr)vk, (IntPtr)1);
            System.Threading.Thread.Sleep(30);
        }
        System.Threading.Thread.Sleep(250);
    }

    /// <summary>Ctrl (+Shift) + key. A posted WM_KEYDOWN cannot make GetKeyState see the modifier,
    /// so this attaches to the target's input queue and sets the shared state for the duration —
    /// scoped to this instance, nothing injected globally.</summary>
    public static void Chord(IntPtr h, int vk, bool shift) {
        uint me = GetCurrentThreadId(), it = GetWindowThreadProcessId(h, IntPtr.Zero);
        AttachThreadInput(me, it, true);
        var st = new byte[256]; GetKeyboardState(st);
        st[0x11] = 0x80; st[0xA2] = 0x80;                      // VK_CONTROL, VK_LCONTROL
        if (shift) { st[0x10] = 0x80; st[0xA0] = 0x80; }       // VK_SHIFT, VK_LSHIFT
        SetKeyboardState(st);
        PostMessageW(h, 0x0100, (IntPtr)vk, (IntPtr)1);
        System.Threading.Thread.Sleep(400);
        st[0x11] = 0; st[0xA2] = 0; st[0x10] = 0; st[0xA0] = 0;
        SetKeyboardState(st);
        PostMessageW(h, 0x0101, (IntPtr)vk, (IntPtr)1);
        AttachThreadInput(me, it, false);
        System.Threading.Thread.Sleep(300);
    }
}
'@

<#
.SYNOPSIS Launch an isolated agwinterm and wait until its control pipe answers.
.PARAMETER Conf Lines for the instance's agwinterm.conf, e.g. "scrollback-lines = 0".
#>
function Start-Sandbox {
    param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Ctl,
          [Parameter(Mandatory)][string]$Pipe, [string[]]$Conf = @(), [int]$Width = 1100, [int]$Height = 700)

    # An id nothing else can be using, so a forgotten instance from an earlier run cannot answer.
    $appId  = "$Pipe-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $appDir = Join-Path $env:LOCALAPPDATA $appId
    New-Item -ItemType Directory -Force $appDir | Out-Null
    if ($Conf.Count) { ($Conf -join "`n") | Set-Content (Join-Path $appDir 'agwinterm.conf') -Encoding UTF8 }

    # A check must never inherit the pane it is being RUN from, or `session type` lands in the
    # caller's own terminal instead of the sandbox.
    foreach ($v in 'AGWINTERM_SESSION_ID', 'AGWINTERM_PANE_ID', 'AGWINTERM_PIPE') {
        Remove-Item "env:$v" -ErrorAction SilentlyContinue
    }

    $p = Start-Process $Exe -ArgumentList @('--pipe', $Pipe, '--app-id', $appId, '--no-restore') -PassThru
    $s = [pscustomobject]@{ Proc = $p; Ctl = $Ctl; Pipe = $Pipe; AppDir = $appDir; Hwnd = [IntPtr]::Zero }
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 500
        if ((Send-Ctl $s @('ping')) -match '"ok":true') { break }
    }
    Start-Sleep 5
    $s.Hwnd = $p.MainWindowHandle
    if ($s.Hwnd -ne [IntPtr]::Zero) {
        # A maximised window makes every coordinate in every check depend on the monitor.
        if ([AgwUi]::IsZoomed($s.Hwnd)) { [void][AgwUi]::ShowWindow($s.Hwnd, 9); Start-Sleep 1 }
        [void][AgwUi]::SetWindowPos($s.Hwnd, [IntPtr]::Zero, 150, 100, $Width, $Height, 0x0004)
        Start-Sleep 2
    }
    return $s
}

<#
.SYNOPSIS Re-attach to a sandbox this session already launched.
An agent runs a case file across several shell invocations and shell state does not survive them,
while the instance does. This rebuilds the handle object from the running process, found by the
--pipe it was launched with, so the next step can carry on driving the same window.
#>
function Connect-Sandbox {
    param([Parameter(Mandatory)][string]$Ctl, [Parameter(Mandatory)][string]$Pipe)
    $proc = Get-CimInstance Win32_Process -Filter "Name = 'Agwinterm.Win32.exe'" |
            Where-Object { $_.CommandLine -match "--pipe\s+$([regex]::Escape($Pipe))(\s|$)" } |
            Select-Object -First 1
    if (-not $proc) { throw "no sandbox instance is running on pipe '$Pipe'" }
    $p = Get-Process -Id $proc.ProcessId
    $appId = ([regex]::Match($proc.CommandLine, '--app-id\s+(\S+)')).Groups[1].Value
    [pscustomobject]@{
        Proc = $p; Ctl = $Ctl; Pipe = $Pipe; Hwnd = $p.MainWindowHandle
        AppDir = if ($appId) { Join-Path $env:LOCALAPPDATA $appId } else { $null }
    }
}

function Stop-Sandbox {
    param([Parameter(Mandatory)]$S)
    if ($S.Proc -and -not $S.Proc.HasExited) { $S.Proc.CloseMainWindow() | Out-Null; Start-Sleep 3 }
    if ($S.Proc -and -not $S.Proc.HasExited) { Stop-Process -Id $S.Proc.Id -Force }
    if ($S.AppDir) { Remove-Item $S.AppDir -Recurse -Force -ErrorAction SilentlyContinue }
}

function Send-Ctl {
    param([Parameter(Mandatory)]$S, [Parameter(Mandatory)][string[]]$Argv)
    # EVERY call, not just at launch. A verb with no --target resolves the CALLER's session from
    # these variables, and a QA run is itself started from inside an agwinterm pane: leave them set
    # and `session text` answers about a session the sandbox has never heard of (empty), while
    # `session type` is accepted and discarded. Nothing errors, and a whole suite goes green having
    # driven nothing at all.
    foreach ($v in 'AGWINTERM_SESSION_ID', 'AGWINTERM_PANE_ID', 'AGWINTERM_PIPE') {
        Remove-Item "env:$v" -ErrorAction SilentlyContinue
    }
    (& $S.Ctl @Argv --pipe $S.Pipe --json 2>&1) -join ''
}

function Get-CtlResult {
    param([Parameter(Mandatory)]$S, [Parameter(Mandatory)][string[]]$Argv)
    $raw = Send-Ctl $S $Argv
    try { (ConvertFrom-Json $raw).result } catch { '' }
}

function Get-PaneText { param([Parameter(Mandatory)]$S, [string]$Target)
    if ($Target) { Get-CtlResult $S @('session', 'text', '--target', $Target) }
    else { Get-CtlResult $S @('session', 'text') }
}

function Get-PaneSelection { param([Parameter(Mandatory)]$S) Get-CtlResult $S @('session', 'copy') }

<#
.SYNOPSIS Write a .ps1 into $Dir and return a path safe to type into a shell.
Forward slashes throughout: a Windows path typed through `session type` is fine with them, and it
keeps backslash escapes out of every quoted string in the checks.
#>
function New-ScriptFile {
    param([Parameter(Mandatory)][string]$Dir, [Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][string[]]$Lines)
    $path = Join-Path $Dir $Name
    $Lines | Set-Content $path -Encoding UTF8
    return $path.Replace([char]92, '/')
}
