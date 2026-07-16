using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DCommon;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;
using Color = Agwinterm.Core.Color;

namespace Agwinterm.Win32;

/// <summary>Session/workspace management: panes, MRU, covers, overlays, splits, reopen-closed, watermarks.</summary>
internal partial class Program
{
    // ---- Session/workspace management (UI thread) ----

    private List<Ses> AllSessions()
    {
        lock (_workspaces) return _workspaces.SelectMany(w => w.Sessions).ToList();
    }

    private Workspace ActiveWorkspace()
    {
        lock (_workspaces)
        {
            if (_active is not null) return _active.Ws;
            if (_workspaces.Count == 0) _workspaces.Add(new Workspace { Id = Guid.NewGuid().ToString(), Name = "workspace 1" });
            return _workspaces[0];
        }
    }

    private Workspace CreateWorkspace(string id, string? name)
    {
        Workspace ws;
        lock (_workspaces)
        {
            ws = new Workspace { Id = id, Name = name ?? $"workspace {_workspaces.Count + 1}" };
            _workspaces.Add(ws);
        }
        RequestRedraw();
        SaveState();
        return ws;
    }

    /// <summary>Create one terminal pane (its own ConPTY, env, wiring) sized for the given font.
    /// When <paramref name="command"/> is set, that argv runs as the pane's process instead of the shell.</summary>
    private Pane CreatePane(string paneId, Workspace ws, string? cwd, float fontSize, string? command = null,
        bool shellWrap = false, bool interactive = false, Dictionary<string, string>? extraEnv = null, string? profileName = null,
        bool deElevate = false, HandoffArgs? handoff = null)
    {
        var (cols, rows) = GridSizeFor(fontSize);
        // The ONLY session creation site (see ISessionBackend). Handoff panes are pinned in-process
        // (their ConPTY handles only mean something here); an unreachable pty-host demotes the pane
        // to in-process with a visible note rather than a dead surface. During restore, a server
        // pane first tries to ADOPT a surviving hosted session with its id (UI restart/crash/update)
        // — the still-running shell reconnects instead of a fresh one spawning.
        ISession session;
        bool adopted = false;
        if (handoff is not null) session = InProcessSessionBackend.Instance.Create(paneId, cols, rows);
        else if (ReferenceEquals(_sessionBackend, InProcessSessionBackend.Instance)) session = _sessionBackend.Create(paneId, cols, rows);
        else
            try
            {
                session = _sessionBackend.Create(paneId, cols, rows);
                adopted = _restoring && session is ServerSession ss && ss.TryAdopt();
            }
            catch (Exception ex)
            {
                session = InProcessSessionBackend.Instance.Create(paneId, cols, rows);
                ShowToast("pty-host unavailable (" + ex.Message + ") — session created in-process", 5000);
            }
        session.Emulator.ScrollbackMax = _config.Scrollback;
        var pane = new Pane { Id = paneId, S = session, StartCwd = cwd, FontSize = fontSize };
        // New output only snaps this pane back to the live bottom and drops the selection when the buffer
        // ACTUALLY scrolled (a line pushed into history) — not on every repaint. TUIs like Claude Code
        // redraw in place without scrolling, so a mouse selection now survives those frames and Ctrl+C can
        // copy it (previously any repaint wiped the selection microseconds after you made it). #copy-selection
        session.OutputReceived += () =>
        {
            long gen = session.Emulator.ScrollGeneration;
            if (gen != pane.LastScrollGen) { pane.LastScrollGen = gen; pane.ScrollOffset = 0; pane.ClearSel(); }
            RequestRedraw();
        };
        // On a transition INTO blocked, play the configured blocked-sound (best-effort, off the UI thread).
        AgentStatus lastStatus = session.Status;
        session.StatusChanged += () =>
        {
            var now = session.Status;
            if (now == AgentStatus.Blocked && lastStatus != AgentStatus.Blocked) PlayBlockedSound();
            lastStatus = now;
            RequestRedraw();
        };
        // An explicit --sound on session.status: play its spec (null => default alert).
        session.SoundRequested += PlayStatusSound;
        session.Emulator.Host = new PaneHost(this, pane, session);   // the host-action seam (see IHostActions)
        session.Exited += _ => Post(() => OnPaneProcessExited(pane));   // split survivor promotion (agterm #121)
        var env = new Dictionary<string, string>
        {
            ["AGWINTERM"] = "1",
            ["AGWINTERM_ENABLED"] = "1",
            ["AGWINTERM_PIPE"] = _control!.PipeName,
            ["AGWINTERM_SESSION_ID"] = paneId,       // each pane is independently targetable
            ["AGWINTERM_PANE_ID"] = paneId,          // explicit pane identity (unique per split pane)
            ["AGWINTERM_WORKSPACE_ID"] = ws.Id,
            ["AGWINTERM_WINDOW_ID"] = Id,
            // Standard terminal-identification vars so shells/prompt tools (starship, oh-my-posh, tmux,
            // scripts) can detect that they're running inside agwinterm. (agterm #203.)
            ["TERM_PROGRAM"] = "agwinterm",
            ["TERM_PROGRAM_VERSION"] = _termProgramVersion,
        };
        if (extraEnv is not null) foreach (var kv in extraEnv) env[kv.Key] = kv.Value; // custom-command $AGW_* context
        if (handoff is { } h)
            // Default-terminal handoff: attach to conhost's existing PTY instead of spawning a shell.
            session.Attach(new Microsoft.Win32.SafeHandles.SafeFileHandle(h.ConOut, true),
                           new Microsoft.Win32.SafeHandles.SafeFileHandle(h.ConIn, true),
                           new Microsoft.Win32.SafeHandles.SafeFileHandle(h.Signal, true), h.Client, h.ClientPid);
        else if (adopted) { /* reattached to the surviving shell — nothing to launch */ }
        else if (!string.IsNullOrWhiteSpace(command) && interactive)
            // Run the command in a fresh shell that STAYS OPEN afterwards (custom-command "new" mode):
            // the user's profile loads (oh-my-posh etc.), the command runs, then it's an interactive shell.
            _ = session.StartAsync("powershell.exe", new[] { "-NoLogo", "-NoExit", "-Command", command! }, extraEnv: env, cwd: cwd);
        else if (!string.IsNullOrWhiteSpace(command) && shellWrap)
            // Run the whole command line through cmd so shell syntax works AND the child's exit code
            // propagates. Verbatim so it becomes exactly `cmd.exe /c <command>` (no extra quoting,
            // which cmd's /c quote-stripping rules would otherwise mangle).
            _ = session.StartAsync("cmd.exe", new[] { "/c", command! }, verbatimCommandLine: true, extraEnv: env, cwd: cwd);
        else if (!string.IsNullOrWhiteSpace(command))
        {
            var argv = ParseArgv(command);
            if (argv.Length > 0)
                _ = session.StartAsync(argv[0], argv[1..], extraEnv: env, cwd: cwd);
            else
                LaunchShell(session, profileName, env, cwd, deElevate);
        }
        else LaunchShell(session, profileName, env, cwd, deElevate);   // launch the chosen shell profile (default = Windows PowerShell)
        return pane;
    }

    /// <summary>Minimal argv split (whitespace-separated, double-quotes group). For session --command.</summary>
    private static string[] ParseArgv(string s)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        bool inQuote = false, has = false;
        foreach (char ch in s)
        {
            if (ch == '"') { inQuote = !inQuote; has = true; }
            else if (char.IsWhiteSpace(ch) && !inQuote) { if (has) { args.Add(cur.ToString()); cur.Clear(); has = false; } }
            else { cur.Append(ch); has = true; }
        }
        if (has) args.Add(cur.ToString());
        return args.ToArray();
    }

    /// <summary>
    /// pwsh launch args: an interactive shell (the user's profile / oh-my-posh loads normally) PLUS an
    /// out-of-the-box live-cwd wrap. The wrap is passed via -EncodedCommand so it runs AFTER the profile
    /// and WRAPS the existing prompt to emit OSC 7 (composes with oh-my-posh, never replaces it); it's
    /// guarded by the same $__agwSI sentinel as the opt-in $PROFILE installer, so the two never
    /// double-wrap. -NoExit keeps the shell interactive after the wrap runs.
    /// </summary>
    private static string[] ShellArgs()
    {
        string wrap = Agwinterm.Pty.ShellIntegrationInstaller.PromptWrap;
        // Prompt-engine injection runs after the profile, then the wrap captures the (new) prompt so
        // live cwd (OSC 7) still works. profile = wrap only; omp with no theme = profile's own omp;
        // vanilla = reset to the stock "PS C:\>" prompt (overriding profile customizations).
        string script = wrap;
        switch (_config.PromptEngine)
        {
            case "vanilla":
                script = VanillaPromptReset + "\n" + wrap;
                break;
            case "omp" when !string.IsNullOrWhiteSpace(_config.OmpTheme)
                            && Agwinterm.Pty.OmpThemes.Resolve(_config.OmpTheme) is { } omp:
                script = "oh-my-posh init pwsh --config '" + omp.Replace("'", "''") + "' | Invoke-Expression\n" + wrap;
                break;
            case "starship":
            {
                string? cfgPath = Agwinterm.Pty.StarshipPresets.ConfigFor(_config.StarshipTheme, AppDir);
                // UTF-8 both ways: console CP for native writes + the .NET encodings Windows
                // PowerShell 5.1 uses to decode captured native output (starship's init captures).
                script = "chcp 65001 >$null; $OutputEncoding=[Console]::InputEncoding=[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n"
                       + (cfgPath is not null ? "$env:STARSHIP_CONFIG='" + cfgPath.Replace("'", "''") + "'\n" : "")
                       + "Invoke-Expression (&starship init powershell)\n" + wrap;
                break;
            }
        }
        string enc = System.Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return new[] { "-NoLogo", "-NoExit", "-EncodedCommand", enc };
    }

    /// <summary>Restore PowerShell's stock prompt (the "vanilla" engine) — profiles often redefine it.</summary>
    private const string VanillaPromptReset =
        "function global:prompt { \"PS $($executionContext.SessionState.Path.CurrentLocation)$('>' * ($nestedPromptLevel + 1)) \" }";

    /// <summary>Resolve a profile by name (case-insensitive), else the configured default, else null.</summary>
    private static Agwinterm.Pty.ShellProfile? ResolveProfile(string? name)
    {
        var profs = _profileCfg.Profiles;
        if (profs.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var m = profs.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }
        return profs.FirstOrDefault(p => p.Name.Equals(_profileCfg.Default, StringComparison.OrdinalIgnoreCase)) ?? profs[0];
    }

    /// <summary>Launch a session's shell from its profile. PowerShell profiles (powershell.exe / pwsh) with
    /// no custom args get the OSC-7 cwd wrap + omp injection (today's default behavior); everything else
    /// (cmd, Git Bash, WSL, custom) launches its raw command + args. AGWINTERM_* env is passed regardless.</summary>
    private void LaunchShell(ISession session, string? profileName, Dictionary<string, string> env, string? cwd, bool deElevate = false)
    {
        var prof = ResolveProfile(profileName);
        string cmd = prof?.Command is { Length: > 0 } c ? c : "powershell.exe";
        string[]? pargs = prof?.Args;
        string? pcwd = string.IsNullOrEmpty(cwd) ? prof?.Cwd : cwd;
        // Per-profile env vars (WT parity). AGWINTERM_* set by CreatePane win, so profile env can't
        // clobber our identity vars; a custom-command's $AGW_* extraEnv still overrides last.
        if (prof?.Env is { Count: > 0 } penv)
            foreach (var kv in penv) if (!kv.Key.StartsWith("AGWINTERM", StringComparison.OrdinalIgnoreCase)) env[kv.Key] = kv.Value;
        string exe = Path.GetFileName(cmd).ToLowerInvariant();
        bool isPwsh = exe is "powershell.exe" or "pwsh.exe" or "pwsh";
        if (isPwsh && (pargs is null || pargs.Length == 0))
            _ = session.StartAsync(cmd, ShellArgs(), extraEnv: env, cwd: pcwd, deElevate: deElevate);        // wrap + omp (cwd-in-title)
        else
            _ = session.StartAsync(cmd, pargs ?? Array.Empty<string>(), extraEnv: env, cwd: pcwd, deElevate: deElevate); // raw shell
    }

    /// <summary>Per-pane implementation of the emulator's host-action seam. Emulator calls arrive on
    /// the feed/pump thread (under the session lock) — UI-touching actions marshal via Post; Respond
    /// writes straight back to the PTY (it must not wait on the UI thread).</summary>
    private sealed class PaneHost : IHostActions
    {
        private readonly Program _app; private readonly Pane _pane; private readonly ISession _s;
        public PaneHost(Program app, Pane pane, ISession s) { _app = app; _pane = pane; _s = s; }
        public void Notify(string title, string body) => _app.Post(() => _app.OnNotified(_pane, title, body));
        public void Progress(int state, int value) => _app.Post(() => _app.OnProgress(state, value));   // OSC 9;4 -> taskbar
        public void ClipboardWrite(string text)                                                        // OSC 52 -> system clipboard
        {
            // Policy at the seam (Ghostty's clipboard-write = allow|deny): the emulator emits the
            // action unconditionally; the host decides. Denials are visible in the VT log.
            if (!Program._config.ClipboardWrite) { VtLog.Write(_pane.Id, "OSC", $"52 clipboard write DENIED by config ({text.Length} chars)"); return; }
            _app.Post(() => _app.ClipboardSet(text));
        }
        public void Respond(string reply) { _s.NotifyActivity(); _s.Write(Encoding.UTF8.GetBytes(reply)); } // query reply -> PTY
        public void Unhandled(string kind, string detail) => VtLog.Write(_pane.Id, kind, detail);       // AGWINTERM_VT_LOG tap
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int tokenInformationClass, out uint info, uint length, out uint returnLength);

    /// <summary>Whether the process <paramref name="pid"/> runs elevated (High integrity), by reading its
    /// token's actual elevation state. Null if it can't be queried (already exited / access denied).</summary>
    private static bool? IsProcessElevated(int pid)
    {
        IntPtr proc = IntPtr.Zero, token = IntPtr.Zero;
        try
        {
            proc = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, (uint)pid);
            if (proc == IntPtr.Zero) return null;
            if (!OpenProcessToken(proc, 0x0008 /*TOKEN_QUERY*/, out token)) return null;
            return GetTokenInformation(token, 20 /*TokenElevation*/, out uint elevated, sizeof(uint), out _) ? elevated != 0 : null;
        }
        catch { return null; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); if (proc != IntPtr.Zero) CloseHandle(proc); }
    }

    /// <summary>Refine a session's ⚡ flag from its shell's ACTUAL integrity once the child is running
    /// (the constructor value is only the spawn intent). Cheap, one-shot, off the UI thread.</summary>
    private void DetectSessionElevation(Ses ses)
    {
        var s = ses.ActivePane.S;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 50 && s.ChildProcessId is null; i++) await Task.Delay(100);
            if (s.ChildProcessId is int pid && IsProcessElevated(pid) is bool elev)
                Post(() => { if (ses.Elevated != elev) { ses.Elevated = elev; RequestRedraw(); } });
        });
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize; public uint cntUsage; public uint th32ProcessID; public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID; public uint cntThreads; public uint th32ParentProcessID; public int pcPriClassBase;
        public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    /// <summary>Whether <paramref name="pid"/> has any live direct child process — i.e. the shell is still
    /// running a foreground command. One Toolhelp snapshot pass (~ms), no WMI. Null when the snapshot
    /// itself fails (caller should fall back to a fixed delay rather than treat it as "idle").</summary>
    private static bool? HasLiveChildProcess(int pid)
    {
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /*TH32CS_SNAPPROCESS*/, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return null;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return null;
            do { if (e.th32ParentProcessID == (uint)pid) return true; } while (Process32NextW(snap, ref e));
            return false;
        }
        finally { CloseHandle(snap); }
    }

    /// <summary>True if this process is running elevated (admin). Uses the token's actual elevation
    /// state (TokenElevation), NOT group membership — a non-elevated admin user is correctly false.</summary>
    private static bool IsElevated()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /*TOKEN_QUERY*/, out token)) return false;
            // TokenElevation (20): nonzero => the token is elevated.
            return GetTokenInformation(token, 20, out uint elevated, sizeof(uint), out _) && elevated != 0;
        }
        catch { return false; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }

    /// <summary>Open a separate ELEVATED agwinterm window for a profile (WT's elevate model — elevated
    /// and unelevated sessions can't share a window). UAC prompts; a fresh window opens with the profile.</summary>
    private void LaunchElevated(string profileName)
    {
        try
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Agwinterm.Win32.exe");
            ShellExecuteW(_hwnd, "runas", exe, $"--profile \"{profileName}\" --no-restore", null, SW_SHOW);
        }
        catch (Exception ex) { ShowToast("elevated launch failed: " + ex.Message); }
    }

    private Ses CreateSession(string id, string? name, string? cwd, Workspace ws, bool makeActive, float? fontSize = null,
        string? command = null, bool interactive = false, Dictionary<string, string>? extraEnv = null, string? profileName = null,
        bool deElevate = false, HandoffArgs? handoff = null)
    {
        // Elevated profile from a non-elevated app: hand off to a separate elevated window (UAC).
        if (profileName is not null && !IsElevated()
            && _profileCfg.Profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase)) is { Elevate: true })
        {
            LaunchElevated(profileName);
            return _active ?? ws.Sessions.FirstOrDefault() ?? CreateSession(id, name, cwd, ws, makeActive, fontSize, command, interactive, extraEnv, null);
        }
        float fs = fontSize is > 0 ? fontSize.Value : (float)_config.FontSize;
        // No explicit cwd → resolve by the new-session-directory mode (home | current | custom).
        if (string.IsNullOrEmpty(cwd))
        {
            string? want = _config.NewSessionDirMode switch
            {
                "current" => _active is not null ? CurrentDirOf(_active) : null,
                "custom" => _config.NewSessionDir,
                _ /*home*/ => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (!string.IsNullOrWhiteSpace(want) && Directory.Exists(want)) cwd = want;
        }
        int ordinal;
        lock (_workspaces) ordinal = ws.Sessions.Count + 1;
        if (name is null && handoff is { Title.Length: > 0 } ht) name = ht.Title;   // label handoff sessions with the app's title
        var ses = new Ses { Id = id, Name = name ?? $"session {ordinal}", Ws = ws, ProfileName = profileName, Elevated = IsElevated() && !deElevate };
        ses.Panes.Add(CreatePane(id, ws, cwd, fs, command, interactive: interactive, extraEnv: extraEnv, profileName: profileName, deElevate: deElevate, handoff: handoff));   // first pane shares the session id (control-API back-compat)
        ses.Active = 0;
        DetectSessionElevation(ses);   // refine ⚡ from the shell's real integrity once it's running

        lock (_workspaces) { ws.Sessions.Add(ses); ws.Expanded = true; }

        if (makeActive || _active is null) SetActive(ses);
        else RequestRedraw();
        SaveState();
        return ses;
    }

    /// <summary>Append an extra pane to an existing session (used by split-layout restore).</summary>
    private Pane AppendPane(Ses ses, string paneId, string? cwd, float fontSize)
    {
        var p = CreatePane(paneId, ses.Ws, cwd, fontSize, profileName: ses.ProfileName);
        lock (_workspaces) ses.Panes.Add(p);
        return p;
    }

    // ---- Pane layout ----

    /// <summary>Content region (px) available to the terminal, right of the sidebar and below the title bar.</summary>
    private (float x0, float y0, float w, float h) ContentArea()
    {
        float x0 = _sidebarW + PadX, y0 = TitleBarH + PadY;
        float w = MathF.Max(1f, ClientW() - _sidebarW - 2 * PadX);
        float h = MathF.Max(1f, ClientH() - TitleBarH - 2 * PadY);
        return (x0, y0, w, h);
    }

    /// <summary>Lay out a session's panes as columns (px) by ratio, with a divider gutter between.</summary>
    private List<(Pane pane, float x, float y, float w, float h)> PaneLayout(Ses ses)
    {
        var (x0, y0, totalW, totalH) = ContentArea();
        int n = ses.Panes.Count;
        float avail = MathF.Max(n, totalW - (n - 1) * DividerW);
        float sum = ses.Panes.Sum(p => p.Ratio);
        if (sum <= 0) { foreach (var p in ses.Panes) p.Ratio = 1f / n; sum = 1f; }
        var list = new List<(Pane, float, float, float, float)>(n);
        float x = x0;
        for (int i = 0; i < n; i++)
        {
            float w = avail * (ses.Panes[i].Ratio / sum);
            list.Add((ses.Panes[i], x, y0, w, totalH));
            x += w + DividerW;
        }
        return list;
    }

    /// <summary>Regrid EVERY session (not just the active one) to the current window/sidebar size, so an
    /// inactive session's PTY never lags behind the window. Without this, switching to a session that was
    /// inactive during a resize would resize its PTY on activation — which clears a TUI's alt-screen buffer
    /// and leaves the pane blank until the app repaints (and an idle Claude Code may not repaint until you
    /// type). Keeping all sessions window-sized means switching never resizes, so it's instant and never blank.</summary>
    private void RegridAllSessions()
    {
        foreach (var s in AllSessions()) RegridSession(s);
        if (_cover is not null) RegridCover();
    }

    /// <summary>Resize every pane's PTY grid to fit its column using the pane's own font metrics.</summary>
    private void RegridSession(Ses ses)
    {
        foreach (var (pane, _, _, w, h) in PaneLayout(ses))
        {
            var (_, cw, ch) = Metrics(pane.FontSize);
            int cols = Math.Max(1, (int)(w / cw)), rows = Math.Max(1, (int)(h / ch));
            if (pane.S.Cols != cols || pane.S.Rows != rows) pane.S.Resize(cols, rows);
            pane.ScrollOffset = Math.Clamp(pane.ScrollOffset, 0, pane.S.Emulator.HistoryCount);
        }
    }

    private void SetActive(Ses ses)
    {
        // Navigating to a session outside the multi-selection drops the selection (single-select again).
        if (!_selectedIds.Contains(ses.Id)) _selectedIds.Clear();
        _active = ses;
        ClearUnread(ses);   // visiting a session clears its notification badge (agterm's "cleared when seen")
        // Auto-reset-on-select: a status marked auto-reset clears to idle once the user looks at the session.
        foreach (var p in ses.Panes)
            if (p.S.AutoReset && p.S.Status is AgentStatus.Completed or AgentStatus.Blocked)
                p.S.SetStatus(AgentStatus.Idle);
        // A scratch/overlay cover belongs to the previous session; drop it (quick is per-app, kept).
        if (_coverKind is 1 or 3) { _cover = null; _coverKind = 0; _ovlOwner = null; }
        // If the newly-active session has an overlay up, re-show it as the cover.
        if (ses.Overlay is not null) { _cover = ses.Overlay; _coverKind = 3; _ovlOwner = ses; }
        _session = ActiveSurface()?.S;                          // a quick cover (if any) keeps input; else the new pane
        RegridSession(ses);
        if (_cover is not null) RegridCover();
        TouchMru(ses);   // move to front of the recency stack (suppressed mid-walk so previews don't reorder)
        RequestRedraw();
        SaveState();
    }

    // ---- MRU session recency stack (Ctrl+Tab switcher) ----

    /// <summary>Move a session to the front of the recency stack. No-op while a walk is in progress
    /// (preview-selects during a walk must not rewrite the stack — only the commit does).</summary>
    private void TouchMru(Ses ses)
    {
        if (_mruWalking) return;
        _mru.Remove(ses.Id);
        _mru.Insert(0, ses.Id);
    }

    /// <summary>Drop dead ids and append any live session not yet tracked (unseen go to the back).</summary>
    private void EnsureMru()
    {
        var live = AllSessions();
        _mru.RemoveAll(id => !live.Any(s => s.Id == id));
        foreach (var s in live) if (!_mru.Contains(s.Id)) _mru.Add(s.Id);
    }

    private Ses? FindSes(string id)
    {
        lock (_workspaces) return _workspaces.SelectMany(w => w.Sessions).FirstOrDefault(s => s.Id == id);
    }

    /// <summary>Begin a walk: snapshot the recency order and park the cursor on the active session.
    /// Returns false (no walk) when fewer than 2 sessions exist.</summary>
    private bool StartWalk()
    {
        EnsureMru();
        var order = _mru.Select(FindSes).Where(s => s is not null).Cast<Ses>().ToList();
        if (order.Count < 2) return false;               // 1 session: no-op, no HUD
        _mruWalking = true;
        _mruSnapshot = order;
        _mruStart = _active;
        _mruIdx = _active is null ? 0 : Math.Max(0, order.IndexOf(_active));
        return true;
    }

    /// <summary>Advance the Ctrl+Tab walk by dir (+1 = older/next, -1 = newer/prev), previewing the target.
    /// Starting a walk (first tap) snapshots the recency order and lands on the previous session.</summary>
    private void MruWalk(int dir)
    {
        if (!_mruWalking && !StartWalk()) return;
        int n = _mruSnapshot.Count;
        _mruIdx = ((_mruIdx + dir) % n + n) % n;
        SetActive(_mruSnapshot[_mruIdx]);                // preview (TouchMru suppressed while walking)
        RequestRedraw();
    }

    /// <summary>Ctrl released: finalize the walk — the highlighted session becomes active + MRU front.</summary>
    private void MruCommit()
    {
        if (!_mruWalking) return;
        _mruWalking = false;
        Ses? target = _mruIdx >= 0 && _mruIdx < _mruSnapshot.Count ? _mruSnapshot[_mruIdx] : _active;
        _mruSnapshot = new(); _mruStart = null;
        if (target is not null) SetActive(target);        // not walking now → fronts it in the stack
        RequestRedraw();
    }

    /// <summary>Esc during a walk: return to the session that was active when the walk began; no MRU change.</summary>
    private void MruCancel()
    {
        if (!_mruWalking) return;
        _mruWalking = false;
        Ses? start = _mruStart;
        _mruSnapshot = new(); _mruStart = null;
        if (start is not null) SetActive(start);          // start was already front, so the stack is unchanged
        RequestRedraw();
    }

    /// <summary>Drive the MRU walk state machine directly (same methods the Ctrl+Tab keys call).
    /// Exists so the control API / tests can exercise begin→advance→commit deterministically with
    /// zero global key injection. Returns the current active session name (or a short status).</summary>
    private string SwitchOp(string op)
    {
        switch (op)
        {
            case "begin": StartWalk(); break;
            case "advance": case "next": MruWalk(1); break;
            case "advance-back": case "back": case "prev": case "previous": MruWalk(-1); break;
            case "commit": MruCommit(); break;
            case "cancel": MruCancel(); break;
            default: return $"unknown op '{op}'";
        }
        return _active?.Name ?? "(none)";
    }

    // ---- Cover terminals (scratch / quick) ----

    /// <summary>The surface that receives input/render focus: a shown cover, else the active pane.</summary>
    private Pane? ActiveSurface() => _cover ?? _active?.ActivePane;

    private void SyncSession() => _session = ActiveSurface()?.S;

    /// <summary>A real filesystem cwd for a session (OSC 7 if reported, else the pane's launch dir).</summary>
    private string? CwdOf(Ses ses)
    {
        string raw = SafeCwd(ses);
        return string.IsNullOrWhiteSpace(raw) ? ses.StartCwd : PrettyCwd(raw);
    }

    private void ShowCover(Pane p, int kind) { _cover = p; _coverKind = kind; SyncSession(); RegridCover(); RequestRedraw(); }

    private void HideCover()
    {
        _cover = null; _coverKind = 0; SyncSession();
        if (_active is not null) RegridSession(_active);
        RequestRedraw();
    }

    /// <summary>Dismiss whatever cover is up (the "close_cover" keymap action): overlays close
    /// (their program is ephemeral), scratch/quick just hide (their shells stay alive).</summary>
    private void CloseCover()
    {
        if (_cover is null) return;
        if (_coverKind == 3) CloseActiveOverlay(); else HideCover();
    }

    /// <summary>The rect (px) the current cover occupies: the full content region, or — for a floating overlay — a centered panel sized by percent.</summary>
    private (float x, float y, float w, float h) CoverRect()
    {
        var (x0, y0, w, h) = ContentArea();
        if (_coverKind == 3 && _ovlOwner is { OverlaySizePercent: > 0 and <= 100 } o)
        {
            float fw = w * o.OverlaySizePercent / 100f, fh = h * o.OverlaySizePercent / 100f;
            return (x0 + (w - fw) / 2f, y0 + (h - fh) / 2f, fw, fh);
        }
        if (_coverKind == 2)   // quick terminal: a centered floating panel over the main window (~85%)
        {
            float fw = w * 0.85f, fh = h * 0.85f;
            return (x0 + (w - fw) / 2f, y0 + (h - fh) / 2f, fw, fh);
        }
        return (x0, y0, w, h);
    }

    /// <summary>Resize the shown cover's PTY to fill its cover rect using its own metrics.</summary>
    private void RegridCover()
    {
        if (_cover is null) return;
        var (_, _, w, h) = CoverRect();
        var (_, cw, ch) = Metrics(_cover.FontSize);
        int cols = Math.Max(1, (int)(w / cw)), rows = Math.Max(1, (int)(h / ch));
        if (_cover.S.Cols != cols || _cover.S.Rows != rows) _cover.S.Resize(cols, rows);
        _cover.ScrollOffset = Math.Clamp(_cover.ScrollOffset, 0, _cover.S.Emulator.HistoryCount);
    }

    /// <summary>Show a session's scratch terminal (creating it lazily in the session's cwd).</summary>
    private void ShowScratch(Ses ses)
    {
        ses.Scratch ??= CreatePane(ses.Id + ":scratch:" + Guid.NewGuid().ToString("N")[..6], ses.Ws, CwdOf(ses), ses.FontSize);
        ShowCover(ses.Scratch, 1);
    }

    private void ShowQuick()
    {
        _quick ??= CreatePane("quick:" + Guid.NewGuid().ToString("N")[..6], ActiveWorkspace(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), (float)_config.FontSize);
        ShowCover(_quick, 2);
    }

    /// <summary>scratch op on|off|toggle for a session's scratch cover.</summary>
    private void ScratchOp(Ses ses, string op)
    {
        bool showing = _coverKind == 1 && ReferenceEquals(_cover, ses.Scratch) && ses.Scratch is not null;
        if (op == "off") { if (showing) HideCover(); return; }
        if (op == "on") { if (!showing) ShowScratch(ses); return; }
        if (showing) HideCover(); else ShowScratch(ses); // toggle
    }

    private void QuickOp(string op)
    {
        bool showing = _coverKind == 2;
        if (op == "off") { if (showing) HideCover(); return; }
        if (op == "on") { if (!showing) ShowQuick(); return; }
        if (showing) HideCover(); else ShowQuick(); // toggle
    }

    // ---- Overlays (Wave B3): an ephemeral program run over a session ----

    /// <summary>Open an overlay on a session: run <paramref name="command"/> in an ephemeral terminal over it.
    /// sizePercent 0 = full content region; 1..100 = a centered floating panel. Returns "id PID".</summary>
    private string OverlayOpen(Ses ses, string command, int sizePercent, bool wait, Dictionary<string, string>? extraEnv = null)
    {
        CloseOverlayOf(ses);                    // one overlay per session; replace any existing one
        _overlayDone.Reset();
        _lastOverlayExit = "no overlay"; _overlayExitCode = 0;
        string id = ses.Id + ":overlay:" + Guid.NewGuid().ToString("N")[..6];
        var pane = CreatePane(id, ses.Ws, CwdOf(ses), ses.FontSize, command, shellWrap: true, extraEnv: extraEnv);
        ses.Overlay = pane;
        ses.OverlaySizePercent = Math.Clamp(sizePercent, 0, 100);
        ses.OverlayWait = wait;
        ses.OverlayExited = false;
        if (ReferenceEquals(ses, _active)) { _cover = pane; _coverKind = 3; _ovlOwner = ses; SyncSession(); RegridCover(); }
        RequestRedraw();
        WatchOverlayExit(ses, pane);
        return id;
    }

    /// <summary>Tear down a session's overlay (hiding its cover if shown, disposing its PTY).</summary>
    private void CloseOverlayOf(Ses ses)
    {
        var pane = ses.Overlay;
        if (pane is null) return;
        if (_coverKind == 3 && ReferenceEquals(_cover, pane)) { _cover = null; _coverKind = 0; _ovlOwner = null; SyncSession(); if (_active is not null) RegridSession(_active); }
        ses.Overlay = null; ses.OverlayExited = false; ses.OverlaySizePercent = 0; ses.OverlayWait = false;
        try { pane.S.Dispose(); } catch { }
        RequestRedraw();
    }

    /// <summary>Close the currently-shown overlay cover (from a keystroke / close verb).</summary>
    private void CloseActiveOverlay() { if (_ovlOwner is not null) CloseOverlayOf(_ovlOwner); }

    /// <summary>When an overlay's program exits, record its code and either close the overlay or (with --wait) mark it exited.</summary>
    private void WatchOverlayExit(Ses ses, Pane pane)
    {
        void OnExit(int code)
        {
            _overlayExitCode = code;
            _lastOverlayExit = $"exit {code}";
            _overlayDone.Set();
            Post(() =>
            {
                if (!ReferenceEquals(ses.Overlay, pane)) return;   // already replaced/closed
                if (ses.OverlayWait) { ses.OverlayExited = true; RequestRedraw(); }
                else CloseOverlayOf(ses);
            });
        }
        pane.S.Exited += OnExit;
        if (pane.S.HasExited) OnExit(pane.S.ExitCode ?? 0);        // already gone before we subscribed
    }

    /// <summary>App version for TERM_PROGRAM_VERSION — the entry assembly's informational version
    /// (stamped by release builds via -p:Version; "dev" in unstamped builds), metadata stripped.</summary>
    private static readonly string _termProgramVersion = ComputeTermProgramVersion();
    private static string ComputeTermProgramVersion()
    {
        string v = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "dev";
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    /// <summary>Change one pane's font zoom (delta 0 = reset to the config default). Caller reflows.</summary>
    private void ChangeFontSizeOfPane(Pane p, int delta)
        => p.FontSize = delta == 0 ? (float)_config.FontSize : Math.Clamp(p.FontSize + delta, 6f, 48f);

    /// <summary>Resolve a pane by id across split panes, per-session scratch/overlay, and the quick terminal
    /// (so <c>agwintermctl font --target &lt;paneId&gt;</c> can zoom a specific pane). Null if no pane matches.</summary>
    /// <summary>Claude's per-project transcript dir name: the cwd with every non-alphanumeric byte turned
    /// into '-' (matches the encoding in the ClaudeIntegration wrapper and Claude Code itself).</summary>
    private static string EncodeClaudeProject(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (char c in path)
            sb.Append((c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9') ? c : '-');
        return sb.ToString();
    }

    /// <summary>A real filesystem cwd for a pane: live OSC-7 cwd if valid, else its launch dir.</summary>
    private static string PaneCwd(Pane p)
    {
        string cwd = PrettyCwd(SafeCwd(p));
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd)) cwd = p.StartCwd ?? "";
        return cwd;
    }

    /// <summary>The Claude transcript directory for a folder (honors CLAUDE_CONFIG_DIR; same project-dir
    /// encoding as Claude Code and the launcher wrapper), or null for a blank cwd.</summary>
    private static string? ClaudeProjectDir(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        string? cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string projects = string.IsNullOrWhiteSpace(cfg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects")
            : Path.Combine(cfg, "projects");
        return Path.Combine(projects, EncodeClaudeProject(cwd));
    }

    /// <summary>Kill hosted sessions no live pane claims (server mode): pane ids ARE hosted-session
    /// ids, so anything unclaimed belongs to a closed pane (its kill lost to a crash) or already
    /// exited. Every surface counts as a claim — panes, scratch, overlay, quick — across all
    /// windows. Best-effort; runs once, well after restore/adoption has claimed everything.</summary>
    private static void ReapOrphanedHostedSessions(string appId)
    {
        try
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<Program> wins;
            lock (_windowIndex) wins = _byId.Values.ToList();
            foreach (var w in wins)
            {
                lock (w._workspaces)
                    foreach (var s in w._workspaces.SelectMany(x => x.Sessions))
                    {
                        foreach (var p in s.Panes) claimed.Add(p.Id);
                        if (s.Scratch is { } sc) claimed.Add(sc.Id);
                        if (s.Overlay is { } ov) claimed.Add(ov.Id);
                    }
                if (w._quick is { } q) claimed.Add(q.Id);
            }
            using var probe = Agwinterm.Pty.PtyHostClient.Connect(appId);
            foreach (var info in probe.List())
                if (!claimed.Contains(info.Id) || info.HasExited)
                    try { probe.Kill(info.Id); } catch { }
        }
        catch { }   // host not running / racing shutdown — nothing to reap
    }

    /// <summary>Whether a Claude conversation transcript with this exact id exists for the folder.
    /// The launcher wrapper keys Claude's session id to the PANE id, so this is the per-pane test.</summary>
    private static bool ClaudeTranscriptExists(string cwd, string id)
    {
        try { return ClaudeProjectDir(cwd) is { } dir && File.Exists(Path.Combine(dir, id + ".jsonl")); }
        catch { return false; }
    }

    /// <summary>The id of the newest Claude conversation for a folder not already in <paramref name="claimed"/>,
    /// or null. A folder-level heuristic: with several panes in one folder it CANNOT tell their conversations
    /// apart, so callers must prefer per-pane evidence (pane-id transcript, running command line) first.</summary>
    private static string? NewestClaudeSessionId(string cwd, ISet<string>? claimed = null)
    {
        string? dir = ClaudeProjectDir(cwd);
        if (dir is null || !Directory.Exists(dir)) return null;
        try
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                string id = Path.GetFileNameWithoutExtension(f.Name);
                if (claimed is null || !claimed.Contains(id)) return id;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>Extract an explicit conversation id (--resume/-r/--session-id &lt;uuid&gt;) from a Claude
    /// command line, or null when it doesn't carry one.</summary>
    private static string? ExtractClaudeSessionArg(string commandLine)
    {
        var m = System.Text.RegularExpressions.Regex.Match(commandLine,
            @"(?:--resume|-r|--session-id)[\s=]+([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Restart the Claude Code running in a pane in YOLO mode (--dangerously-skip-permissions),
    /// resuming its current conversation. Sends a double Ctrl+C to quit the running Claude, then relaunches
    /// resumed + dangerous, and persists that as the pane's relaunch command so restarts stay YOLO.</summary>
    private string RestartClaudeYolo(Pane p)
    {
        var pane = p;
        _ = Task.Run(async () =>
        {
            // Resolve WHICH conversation to resume — per-pane, off the UI thread, and BEFORE quitting
            // Claude (its command line is the evidence). Precedence: the pane's own transcript (the
            // launcher wrapper keys Claude's session id to the pane id) → an explicit id on the running
            // Claude's command line → the folder's newest transcript. The last is folder-level and used
            // to bind every same-folder pane to ONE conversation when it ran first.
            string cwd = PaneCwd(pane);
            string? id;
            if (ClaudeTranscriptExists(cwd, pane.Id)) id = pane.Id;
            else
            {
                // Generous timeout: a cold CIM/powershell start can take >4s, and we're already off
                // the UI thread — better to wait than to mis-bind the pane.
                id = pane.S.ChildProcessId is int spid
                     && CaptureForegroundCommands(new[] { spid }, timeoutMs: 15000).TryGetValue(spid, out var running)
                    ? ExtractClaudeSessionArg(running) : null;
                id ??= NewestClaudeSessionId(cwd);
            }
            // Resume the existing conversation if we found one; otherwise start a fresh YOLO session.
            string cmd = id is null
                ? "claude --dangerously-skip-permissions"
                : $"claude --resume {id} --dangerously-skip-permissions";
            Post(() => { pane.AgentResume = cmd; SaveState(); });   // future restarts stay YOLO
            await QuitClaudeAndRelaunch(pane, cmd);
        });
        return "restarting Claude in YOLO mode";
    }

    /// <summary>Quit the Claude running in a pane and type <paramref name="cmd"/> into the freed shell.
    /// Sends Ctrl+C twice (= Claude's exit), then WAITS for the foreground child to actually be gone —
    /// a fixed delay raced Claude's exit on slow machines and the relaunch landed in Claude's own
    /// prompt instead of the shell. Polling is capped so a hung/ignoring process can't stall forever.</summary>
    private async Task QuitClaudeAndRelaunch(Pane pane, string cmd)
    {
        try { pane.S.Write(new byte[] { 0x03 }); } catch { }
        await Task.Delay(350);
        try { pane.S.Write(new byte[] { 0x03 }); } catch { }
        int shellPid = pane.S.ChildProcessId ?? 0;
        bool sawIdle = false;
        if (shellPid > 0)
            for (int i = 0; i < 100; i++)   // ≤ 30s
            {
                await Task.Delay(300);
                bool? busy = HasLiveChildProcess(shellPid);
                if (busy is null) break;               // snapshot failed → fixed-delay fallback below
                if (busy is false) { sawIdle = true; break; }
            }
        if (!sawIdle) await Task.Delay(1200);           // old behavior when we couldn't confirm the exit
        await Task.Delay(300);                          // let the shell repaint its prompt and start reading input
        try { pane.S.Write(System.Text.Encoding.UTF8.GetBytes(cmd + "\r")); } catch { }
    }

    /// <summary>One background version check ("be aware when a new Claude Code ships"): compare the
    /// installed CLI against the npm registry; when a NEW newer version appears, toast once and arm
    /// the palette hint. Silent when claude isn't installed, offline, or already current.</summary>
    private async Task CheckClaudeUpdateOnce()
    {
        string? installed = Agwinterm.Pty.ClaudeUpdate.InstalledVersion();
        if (installed is null) return;
        string? latest = await Agwinterm.Pty.ClaudeUpdate.FetchLatestAsync();
        if (latest is null) return;
        if (!Agwinterm.Pty.ClaudeUpdate.IsNewer(latest, installed)) { _claudeLatest = null; return; }
        bool fresh = !string.Equals(_claudeLatest, latest, StringComparison.Ordinal);
        _claudeLatest = latest;
        if (fresh) Post(() => ShowToast($"Claude Code {latest} is out (you have {installed}) — palette → Update Claude Code", 6000));
    }

    /// <summary>The "Update Claude Code" workflow (palette / <c>agwintermctl claude update</c>):
    /// check installed vs shipped, run <c>claude update</c> in an overlay terminal over the active
    /// session (visible progress), and when it exits cleanly restart every pane with a live Claude
    /// so the new version is picked up — each resumes its own conversation, YOLO stays YOLO.</summary>
    private string UpdateClaudeCode()
    {
        var ses = _active;
        if (ses is null) return "open a session first";
        if (_claudeUpdating) return "Claude Code update already running";
        _claudeUpdating = true;
        _ = Task.Run(async () =>
        {
            string? installed = Agwinterm.Pty.ClaudeUpdate.InstalledVersion();
            if (installed is null)
            {
                Post(() => { _claudeUpdating = false; ShowToast("claude not found on PATH — install Claude Code first", 4000); });
                return;
            }
            string? latest = await Agwinterm.Pty.ClaudeUpdate.FetchLatestAsync();
            if (latest is not null && !Agwinterm.Pty.ClaudeUpdate.IsNewer(latest, installed))
            {
                Post(() => { _claudeUpdating = false; _claudeLatest = null; ShowToast($"Claude Code {installed} is already the latest", 4000); });
                return;
            }
            // A newer version shipped — or the registry was unreachable, in which case `claude update`
            // itself is the authority (it no-ops harmlessly when already current).
            Post(() =>
            {
                OverlayOpen(ses, "claude update", 60, wait: true);
                var ovl = ses.Overlay;
                if (ovl is null) { _claudeUpdating = false; return; }
                ShowToast(latest is null ? $"running claude update (you have {installed})…"
                                         : $"updating Claude Code {installed} → {latest}…", 4000);
                int fired = 0;   // one-shot: the event and the already-exited fallback can race
                void Once(int code) { if (Interlocked.Exchange(ref fired, 1) == 0) OnClaudeUpdateExited(code, installed); }
                ovl.S.Exited += Once;
                if (ovl.S.HasExited) Once(ovl.S.ExitCode ?? 0);
            });
        });
        return "updating Claude Code…";
    }

    /// <summary>Continuation for the update overlay: verify the install actually moved, then fan out
    /// the session restarts. Never restarts on a failed or no-op update — bouncing every Claude for
    /// nothing is worse than asking the user to re-run.</summary>
    private void OnClaudeUpdateExited(int code, string wasInstalled)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (code != 0)
                {
                    Post(() => ShowToast($"claude update failed (exit {code}) — sessions not restarted", 5000));
                    return;
                }
                string? now = Agwinterm.Pty.ClaudeUpdate.InstalledVersion();
                if (now is null || !Agwinterm.Pty.ClaudeUpdate.IsNewer(now, wasInstalled))
                {
                    Post(() => ShowToast($"Claude Code unchanged ({now ?? wasInstalled}) — sessions not restarted", 5000));
                    return;
                }
                Post(() => { _claudeLatest = null; RequestRedraw(); });
                int n = RestartAllClaudeSessions();
                Post(() => ShowToast(n == 0
                    ? $"Claude Code updated {wasInstalled} → {now} (no running Claude sessions to restart)"
                    : $"Claude Code updated {wasInstalled} → {now} — restarting {n} Claude session(s)…", 6000));
            }
            finally { _claudeUpdating = false; }
        });
    }

    /// <summary>Restart every pane whose shell is currently running Claude Code (foreground-child
    /// evidence), so a freshly-installed version is picked up. Relaunch command precedence: the pane's
    /// bound AgentResume (the wrapper re-binds and resumes by pane id) → derived from the running
    /// command line (explicit --resume id + permission flags survive) → the pane's own transcript →
    /// bare "claude" (the wrapper resolves the conversation). Runs OFF the UI thread. Returns the
    /// number of panes being restarted.</summary>
    private int RestartAllClaudeSessions()
    {
        var panes = new List<Pane>();
        lock (_workspaces)
            foreach (var s in _workspaces.SelectMany(w => w.Sessions))
            {
                panes.AddRange(s.Panes);
                if (s.Scratch is { } sc) panes.Add(sc);   // claude runs in scratch terminals too
            }
        if (_quick is { } q) panes.Add(q);

        var byPid = CaptureForegroundCommands(
            panes.Select(p => p.S.ChildProcessId).Where(id => id is > 0).Select(id => id!.Value), timeoutMs: 15000);

        int n = 0;
        foreach (var p in panes)
        {
            if (p.S.ChildProcessId is not int pid || !byPid.TryGetValue(pid, out var running)) continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(running, @"(?i)\bclaude\b")) continue;
            string cmd = p.AgentResume ?? DeriveClaudeRelaunch(p, running);
            var pane = p;
            Post(() => { pane.AgentResume = cmd; SaveState(); });
            _ = QuitClaudeAndRelaunch(pane, cmd);
            n++;
        }
        return n;
    }

    /// <summary>Build a relaunch command for a Claude pane with no binding, from the running command
    /// line: keep its permission mode, resume an explicit id or the pane's own transcript. Falls back
    /// to bare "claude" — inside agwinterm the launcher wrapper resolves the pane's conversation.</summary>
    private static string DeriveClaudeRelaunch(Pane pane, string running)
    {
        bool yolo = running.Contains("--dangerously-skip-permissions", StringComparison.Ordinal);
        string? id = ExtractClaudeSessionArg(running);
        if (id is null && ClaudeTranscriptExists(PaneCwd(pane), pane.Id)) id = pane.Id;
        return "claude" + (id is null ? "" : $" --resume {id}") + (yolo ? " --dangerously-skip-permissions" : "");
    }

    /// <summary>One-time migration for sessions started BEFORE the claude launcher existed: for each pane,
    /// find the newest Claude transcript for its cwd (its current conversation) and bind the pane to resume
    /// exactly that on restart. Idempotent; re-running just refreshes the bindings. Returns a summary.</summary>
    internal string AdoptClaudeSessions()
    {
        List<Pane> panes;
        lock (_workspaces) panes = _workspaces.SelectMany(w => w.Sessions).SelectMany(s => s.Panes).ToList();

        // One process query for all panes up front: a running Claude's explicit --resume/--session-id is
        // the best per-pane evidence. (Blocks the UI thread a moment — adopt is an explicit one-shot verb;
        // generous timeout because a cold CIM start can take >4s and a mis-bind is worse than a wait.)
        var byPid = CaptureForegroundCommands(
            panes.Select(p => p.S.ChildProcessId).Where(id => id is > 0).Select(id => id!.Value), timeoutMs: 15000);

        // Pass 1 — strong per-pane evidence claims its conversation: the pane's own transcript (the
        // launcher wrapper keys Claude's session id to the pane id) or the running command line's id.
        // Pass 2 — the rest fall back to the folder's newest UNCLAIMED transcript, so several panes in
        // one folder can never all be bound to the same conversation (a pane gets nothing rather than
        // a sibling's conversation — a wrong resume is worse than no resume).
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var strong = new Dictionary<Pane, string>();
        foreach (var p in panes)
        {
            string? id = ClaudeTranscriptExists(PaneCwd(p), p.Id) ? p.Id
                : p.S.ChildProcessId is int pid && byPid.TryGetValue(pid, out var running)
                    ? ExtractClaudeSessionArg(running) : null;
            if (id is not null) { strong[p] = id; claimed.Add(id); }
        }
        int adopted = 0;
        foreach (var p in panes)
        {
            string? id = strong.TryGetValue(p, out var s) ? s : NewestClaudeSessionId(PaneCwd(p), claimed);
            if (id is null) continue;
            if (!strong.ContainsKey(p)) claimed.Add(id);
            // Store the full resume command; the restore path types it verbatim and the claude wrapper (or the
            // real CLI) honors --resume, so the exact conversation comes back on every relaunch.
            p.AgentResume = "claude --resume " + id;
            adopted++;
        }
        if (adopted > 0) { SaveState(); RequestRedraw(); }
        return adopted == 0
            ? "no adoptable Claude sessions found (no transcript matched any pane's folder)"
            : $"adopted {adopted} Claude session(s) — restart agwinterm to resume them (or type the resume yourself now)";
    }

    private (Pane pane, Ses? ses, bool cover)? FindPaneById(string id)
    {
        // Exact match first, then id prefix — the control API's documented target semantics
        // (matches Resolve/PaneForTarget). Exact-only here made prefix-targeted verbs (e.g.
        // `claude yolo --target <prefix>`) silently fall back to the ACTIVE pane instead.
        return FindPaneBy(p => p.Id == id) ?? FindPaneBy(p => p.Id.StartsWith(id, StringComparison.Ordinal));
    }

    private (Pane pane, Ses? ses, bool cover)? FindPaneBy(Func<Pane, bool> match)
    {
        lock (_workspaces)
            foreach (var w in _workspaces)
                foreach (var s in w.Sessions)
                {
                    foreach (var p in s.Panes) if (match(p)) return (p, s, false);
                    if (s.Scratch is { } sc && match(sc)) return (sc, s, true);
                    if (s.Overlay is { } ov && match(ov)) return (ov, s, true);
                }
        if (_quick is { } q && match(q)) return (q, null, true);
        return null;
    }

    /// <summary>Zoom one specific pane's font (delta 0 = reset) and reflow the surface it belongs to.</summary>
    private void ZoomPane((Pane pane, Ses? ses, bool cover) hit, int delta)
    {
        ChangeFontSizeOfPane(hit.pane, delta);
        if (hit.cover) { if (ReferenceEquals(_cover, hit.pane)) RegridCover(); }   // inactive covers regrid on show
        else if (hit.ses is not null) RegridSession(hit.ses);
        RequestRedraw();
        SaveState();
    }

    /// <summary>Change the active pane's font zoom (delta 0 = reset to config default), reflow + repaint.</summary>
    private void ChangeFontSizeOf(Ses ses, int delta)
    {
        float cur = ses.FontSize;
        float ns = delta == 0 ? (float)_config.FontSize : Math.Clamp(cur + delta, 6f, 48f);
        if (ns == cur) return;
        ses.FontSize = ns;               // active pane
        RegridSession(ses);
        if (ReferenceEquals(_active, ses)) RequestRedraw();
        SaveState();
    }

    private void ChangeFontSize(int delta)
    {
        if (_cover is not null) { ChangeFontSizeOfPane(_cover, delta); RegridCover(); RequestRedraw(); }
        else if (_active is not null) ChangeFontSizeOf(_active, delta);
    }

    // ---- Splits ----

    private void SplitActivePane()
    {
        var ses = _active;
        if (ses is null) return;
        if (ses.Panes.Count >= 2) return;   // agterm model: strictly primary + one split (no 3+ panes)
        var cur = ses.ActivePane;
        string? cwd = string.IsNullOrEmpty(cur.StartCwd) ? null : cur.StartCwd;
        var np = CreatePane(Guid.NewGuid().ToString(), ses.Ws, cwd, cur.FontSize, profileName: ses.ProfileName);
        float half = cur.Ratio / 2f;
        cur.Ratio = half; np.Ratio = half;
        int idx = ses.Panes.IndexOf(cur);
        lock (_workspaces) ses.Panes.Insert(idx + 1, np);   // exclude the control-pipe reader mid-enumeration (issue #85)
        ses.Active = idx + 1;            // focus the new pane
        _session = ses.S;
        RegridSession(ses);
        RequestRedraw();
        SaveState();
    }

    private void FocusPane(int dir)
    {
        var ses = _active;
        if (ses is null || ses.Panes.Count < 2) return;
        ses.Active = Math.Clamp(ses.Active + dir, 0, ses.Panes.Count - 1);
        _session = ses.S;
        RequestRedraw();
    }

    /// <summary>Confirm a user-initiated session close (Yes/No), unless confirm-close-session is off.</summary>
    private bool ConfirmCloseOk()
        => !_config.ConfirmCloseSession
           || MessageBoxW(_hwnd, "Close this session and end what's running in it?", "Close session",
                          MB_YESNO | MB_ICONQUESTION) == IDYES;

    /// <summary>Close the focused pane; if it's the last pane, close the whole session.</summary>
    private void CloseActivePane()
    {
        var ses = _active;
        if (ses is null) return;
        if (ses.Panes.Count <= 1) { if (ConfirmCloseOk()) CloseSessionInternal(ses); return; }
        var cur = ses.ActivePane;
        int idx = ses.Panes.IndexOf(cur);
        try { cur.S.Dispose(); } catch { }
        lock (_workspaces) ses.Panes.RemoveAt(idx);   // exclude the control-pipe reader mid-enumeration (issue #85)
        float freed = cur.Ratio / ses.Panes.Count;
        foreach (var p in ses.Panes) p.Ratio += freed;   // redistribute the freed width
        ses.Active = Math.Clamp(idx, 0, ses.Panes.Count - 1);
        _session = ses.S;
        RegridSession(ses);
        RequestRedraw();
        SaveState();
    }

    /// <summary>A pane's shell process exited: if it was one side of a split, collapse to the survivor
    /// (promote it into the main pane). Single-pane sessions keep the exited shell visible. (agterm #121.)</summary>
    private void OnPaneProcessExited(Pane p)
    {
        Ses? ses;
        lock (_workspaces)
        {
            ses = _workspaces.SelectMany(w => w.Sessions).FirstOrDefault(s => s.Panes.Contains(p));
            if (ses is null || ses.Panes.Count <= 1) return;   // not a live split pane → leave the shell as-is
            int idx = ses.Panes.IndexOf(p);
            ses.Panes.RemoveAt(idx);
            float freed = p.Ratio / ses.Panes.Count;
            foreach (var q in ses.Panes) q.Ratio += freed;     // survivor grows to fill the freed width
            ses.Active = Math.Clamp(idx, 0, ses.Panes.Count - 1);
        }
        try { p.S.Dispose(); } catch { }
        if (ReferenceEquals(_active, ses)) _session = ses.S;
        RegridSession(ses);
        if (ReferenceEquals(_active, ses)) RequestRedraw();
        SaveState();
    }

    /// <summary>A recently-closed session's restorable identity (IDE "reopen closed" / browser Ctrl+Shift+T).
    /// SessionId is the original id, reused on reopen so control-API/agent references stay stable.</summary>
    private sealed record ClosedSession(string? CustomName, string? ProfileName, string Cwd,
        string WorkspaceId, string WorkspaceName, bool Flagged, string Display, string Name,
        string SessionId = "", long Seq = 0);
    /// <summary>A recently-closed workspace (its name + all its sessions), so it can be reopened whole.
    /// WorkspaceId is the original id, reused on reopen to keep it stable across close→reopen.</summary>
    private sealed record ClosedWorkspace(string Name, IReadOnlyList<ClosedSession> Sessions, long Seq, string WorkspaceId = "");
    private readonly List<ClosedSession> _closedSessions = new();
    private readonly List<ClosedWorkspace> _closedWorkspaces = new();
    private const int MaxClosedSessions = 25;
    private long _closeSeq;   // monotonic; orders closed sessions vs workspaces for "most recent"

    /// <summary>Snapshot a session's restorable identity (stamped with the next close sequence).</summary>
    private ClosedSession CaptureClosedSessionData(Ses ses)
    {
        var p = ses.ActivePane;
        string live = PrettyCwd(SafeCwd(p));
        string cwd = live.Length > 0 ? live : (p.StartCwd ?? "");
        string display = !string.IsNullOrEmpty(ses.CustomName) ? ses.CustomName!
            : (!string.IsNullOrEmpty(cwd) ? Path.GetFileName(cwd.TrimEnd('\\', '/')) : ses.Name);
        if (string.IsNullOrEmpty(display)) display = ses.Name;
        return new ClosedSession(ses.CustomName, ses.ProfileName, cwd, ses.Ws.Id, ses.Ws.Name, ses.Flagged, display, ses.Name, ses.Id, ++_closeSeq);
    }

    /// <summary>Remember a session's identity so it can be reopened after it's closed.</summary>
    private void CaptureClosedSession(Ses ses)
    {
        try { _closedSessions.Add(CaptureClosedSessionData(ses)); if (_closedSessions.Count > MaxClosedSessions) _closedSessions.RemoveAt(0); }
        catch { }
    }

    /// <summary>Remember a whole workspace (name + its sessions) so it can be reopened after it's deleted.</summary>
    private void CaptureClosedWorkspace(Workspace ws, IReadOnlyList<Ses> sessions)
    {
        try
        {
            if (sessions.Count == 0) return;
            var items = sessions.Select(CaptureClosedSessionData).ToList();
            _closedWorkspaces.Add(new ClosedWorkspace(ws.Name, items, ++_closeSeq, ws.Id));
            if (_closedWorkspaces.Count > MaxClosedSessions) _closedWorkspaces.RemoveAt(0);
        }
        catch { }
    }

    /// <summary>True if any live workspace currently uses this id.</summary>
    private bool WorkspaceIdInUse(string id) { lock (_workspaces) return _workspaces.Any(w => w.Id == id); }
    /// <summary>True if any live session (in any workspace) currently uses this id.</summary>
    private bool SessionIdInUse(string id) { lock (_workspaces) return _workspaces.Any(w => w.Sessions.Any(s => s.Id == id)); }
    /// <summary>Reuse the preferred id when it's non-empty and free — so ids stay stable across close→reopen
    /// and restore (agent / control-API references survive); otherwise mint a fresh id to fold the collision.</summary>
    private string StableWorkspaceId(string? preferred)
        => !string.IsNullOrEmpty(preferred) && !WorkspaceIdInUse(preferred!) ? preferred! : Guid.NewGuid().ToString();
    private string StableSessionId(string? preferred)
        => !string.IsNullOrEmpty(preferred) && !SessionIdInUse(preferred!) ? preferred! : Guid.NewGuid().ToString();

    /// <summary>Reopen the most recently closed item — a session or a whole workspace, whichever was closed last.</summary>
    private void ReopenMostRecent()
    {
        long sSeq = _closedSessions.Count > 0 ? _closedSessions[^1].Seq : -1;
        long wSeq = _closedWorkspaces.Count > 0 ? _closedWorkspaces[^1].Seq : -1;
        if (sSeq < 0 && wSeq < 0) { ShowToast("No recently closed items"); return; }
        if (wSeq > sSeq) ReopenClosedWorkspace(_closedWorkspaces[^1]);
        else ReopenClosedSession();
    }

    /// <summary>Reopen the most recently closed session (or a specific one) — restoring its profile, cwd,
    /// custom name and flag, in its original workspace if it still exists.</summary>
    private void ReopenClosedSession(ClosedSession? which = null)
    {
        ClosedSession c;
        if (which is not null) { c = which; _closedSessions.Remove(which); }
        else if (_closedSessions.Count > 0) { c = _closedSessions[^1]; _closedSessions.RemoveAt(_closedSessions.Count - 1); }
        else { ShowToast("No recently closed sessions"); return; }

        Workspace ws;
        lock (_workspaces)
            ws = _workspaces.FirstOrDefault(w => w.Id == c.WorkspaceId)
                 ?? _workspaces.FirstOrDefault(w => string.Equals(w.Name, c.WorkspaceName, StringComparison.OrdinalIgnoreCase))
                 ?? ActiveWorkspace();
        string? cwd = !string.IsNullOrEmpty(c.Cwd) && Directory.Exists(c.Cwd) ? c.Cwd : null;
        var ses = CreateSession(StableSessionId(c.SessionId), null, cwd, ws, makeActive: true, profileName: c.ProfileName);
        if (!string.IsNullOrEmpty(c.CustomName)) { ses.CustomName = c.CustomName; ses.Name = c.CustomName!; } // mirror rename
        else if (!string.IsNullOrEmpty(c.Name)) ses.Name = c.Name;   // restore the original label
        if (c.Flagged) ses.Flagged = true;
        RequestRedraw();
        SaveState();
    }

    /// <summary>Reopen a closed workspace whole — recreate it and all its sessions (profile/cwd/name/flag).</summary>
    private void ReopenClosedWorkspace(ClosedWorkspace c)
    {
        _closedWorkspaces.Remove(c);
        var ws = CreateWorkspace(StableWorkspaceId(c.WorkspaceId), c.Name);
        Ses? first = null;
        foreach (var cs in c.Sessions)
        {
            string? cwd = !string.IsNullOrEmpty(cs.Cwd) && Directory.Exists(cs.Cwd) ? cs.Cwd : null;
            var ses = CreateSession(StableSessionId(cs.SessionId), null, cwd, ws, makeActive: false, profileName: cs.ProfileName);
            if (!string.IsNullOrEmpty(cs.CustomName)) { ses.CustomName = cs.CustomName; ses.Name = cs.CustomName!; }
            else if (!string.IsNullOrEmpty(cs.Name)) ses.Name = cs.Name;
            if (cs.Flagged) ses.Flagged = true;
            first ??= ses;
        }
        if (first is not null) SetActive(first);
        RequestRedraw();
        SaveState();
    }

    private void CloseSessionInternal(Ses ses)
    {
        CaptureClosedSession(ses);   // remember it so Reopen Closed Session can bring it back
        foreach (var p in ses.Panes) { try { p.S.Dispose(); } catch { } }
        // Dismiss + dispose this session's scratch cover if it belongs here.
        if (ses.Scratch is not null) { if (_coverKind == 1 && ReferenceEquals(_cover, ses.Scratch)) HideCover(); try { ses.Scratch.S.Dispose(); } catch { } ses.Scratch = null; }
        if (ses.Overlay is not null) CloseOverlayOf(ses); // dismiss + dispose this session's overlay
        bool wasActive = ReferenceEquals(_active, ses);
        _mru.Remove(ses.Id);
        EvictWatermark(ses.BgPath); SweepBackground(ses.Id); // drop the session's watermark file + texture
        lock (_workspaces) ses.Ws.Sessions.Remove(ses);
        if (wasActive)
        {
            Ses? next = AllSessions().FirstOrDefault();
            if (next is not null) SetActive(next);
            else CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), makeActive: true);
        }
        RequestRedraw();
        SaveState();
    }

    // ---- Wave F2: session background watermark storage/lifecycle (UI thread) ----

    /// <summary>Copy the chosen image into AppDir\backgrounds\&lt;sessionId&gt;&lt;ext&gt; and set the session's spec.</summary>
    private string SetBackground(Ses ses, string source, int opacity, string? mode)
    {
        if (!File.Exists(source)) return "file not found: " + source;
        string ext = Path.GetExtension(source);
        if (string.IsNullOrEmpty(ext)) ext = ".img";
        string dst = Path.Combine(BackgroundsDir, ses.Id + ext);
        try
        {
            Directory.CreateDirectory(BackgroundsDir);
            SweepBackground(ses.Id);          // remove any prior copy (possibly a different extension)
            EvictWatermark(ses.BgPath);
            File.Copy(source, dst, overwrite: true);
        }
        catch (Exception ex) { return "copy failed: " + ex.Message; }

        ses.BgPath = dst;
        if (opacity >= 0) ses.BgOpacity = System.Math.Clamp(opacity, 0, 100);
        if (!string.IsNullOrWhiteSpace(mode))
        {
            string m = mode!.Trim().ToLowerInvariant();
            if (m is "fit" or "fill" or "center" or "tile") ses.BgMode = m;
        }
        EvictWatermark(dst);                   // force a fresh decode of the new bytes
        SaveState();
        RequestRedraw();
        return $"background set ({ses.BgMode}, {ses.BgOpacity}%)";
    }

    private void ClearBackground(Ses ses)
    {
        EvictWatermark(ses.BgPath);
        SweepBackground(ses.Id);
        ses.BgPath = null;
        SaveState();
        RequestRedraw();
    }

    /// <summary>Delete any copied background file(s) for a session id (best-effort).</summary>
    private static void SweepBackground(string sessionId)
    {
        try
        {
            if (!Directory.Exists(BackgroundsDir)) return;
            foreach (var f in Directory.EnumerateFiles(BackgroundsDir, sessionId + ".*"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    /// <summary>Sweep watermark files for every session in a (possibly closed) window's snapshot.</summary>
    private static void SweepWindowBackgrounds(string windowId)
    {
        try
        {
            string p = Path.Combine(AppDir, "windows", windowId + ".json");
            if (!File.Exists(p)) return;
            var st = JsonSerializer.Deserialize<AppState>(File.ReadAllText(p));
            if (st is null) return;
            foreach (var ws in st.Workspaces)
                foreach (var s in ws.Sessions)
                    if (!string.IsNullOrEmpty(s.Id)) SweepBackground(s.Id);
        }
        catch { }
    }

    private void CycleSession(int dir)
    {
        var all = AllSessions();
        if (all.Count < 2) return;
        int i = _active is null ? 0 : all.IndexOf(_active);
        SetActive(all[((i + dir) % all.Count + all.Count) % all.Count]);
    }

    // ---- Wave A1 internal helpers (called on the UI thread via Host/Post) ----

    private void SessionGoInternal(string dir)
    {
        switch (dir)
        {
            case "next": CycleSession(1); break;
            case "prev": case "previous": CycleSession(-1); break;
            case "first": { var f = AllSessions().FirstOrDefault(); if (f is not null) SetActive(f); break; }
            case "last": { var l = AllSessions().LastOrDefault(); if (l is not null) SetActive(l); break; }
            case "next-attention": GoToNextAttention(1); break;
            case "prev-attention": case "previous-attention": GoToNextAttention(-1); break;
        }
    }

    /// <summary>Move an item within its list: dir = up|down|top|bottom (caller holds any needed lock).</summary>
    private static void ReorderInList<T>(List<T> list, T item, string dir)
    {
        int i = list.IndexOf(item);
        if (i < 0) return;
        list.RemoveAt(i);
        int j = dir switch { "up" => i - 1, "down" => i + 1, "top" => 0, "bottom" => list.Count, _ => i };
        list.Insert(Math.Clamp(j, 0, list.Count), item);
    }

    private Workspace? FindWs(string? target)
    {
        lock (_workspaces)
        {
            if (string.IsNullOrEmpty(target) || target == "active") return _active?.Ws ?? _workspaces.FirstOrDefault();
            return _workspaces.FirstOrDefault(w => w.Id == target) ?? _workspaces.FirstOrDefault(w => w.Id.StartsWith(target));
        }
    }

    private void SplitOp(string op)
    {
        int panes = _active?.Panes.Count ?? 1;
        switch (op)
        {
            case "on": if (panes <= 1) SplitActivePane(); break;
            case "off": if (panes > 1) CollapseToSinglePane(); break;
            default: if (panes > 1) CollapseToSinglePane(); else SplitActivePane(); break; // toggle
        }
    }

    /// <summary>Collapse the active session to just its focused pane (dispose the rest).</summary>
    private void CollapseToSinglePane()
    {
        var ses = _active;
        if (ses is null || ses.Panes.Count <= 1) return;
        var keep = ses.Panes[0];   // agterm: collapsing the split keeps the primary (left) pane, not whichever is focused
        foreach (var p in ses.Panes) if (!ReferenceEquals(p, keep)) { try { p.S.Dispose(); } catch { } }
        // Clear+Add as one atomic step so the control-pipe reader never sees an empty pane list (issue #85).
        lock (_workspaces) { ses.Panes.Clear(); ses.Panes.Add(keep); }
        keep.Ratio = 1f; ses.Active = 0;
        _session = ses.S;
        RegridSession(ses); RequestRedraw(); SaveState();
    }

    /// <summary>Set the split boundary next to the active pane: an absolute left ratio, or grow by columns.</summary>
    private void ResizeActiveSplitInternal(double? ratio, int growLeft, int growRight)
    {
        var ses = _active;
        if (ses is null || ses.Panes.Count < 2) return;
        int i = Math.Min(ses.Active, ses.Panes.Count - 2); // boundary between pane i and i+1
        var a = ses.Panes[i]; var b = ses.Panes[i + 1];
        float total = a.Ratio + b.Ratio;
        if (ratio is double r)
        {
            float ra = (float)Math.Clamp(r, 0.05, 0.95) * total;
            a.Ratio = ra; b.Ratio = total - ra;
        }
        else
        {
            var (_, _, totalW, _) = ContentArea();
            var (_, cw, _) = Metrics(a.FontSize);
            float shift = ((growRight - growLeft) * cw / MathF.Max(1f, totalW)) * total;
            float ra = Math.Clamp(a.Ratio + shift, 0.05f * total, 0.95f * total);
            a.Ratio = ra; b.Ratio = total - ra;
        }
        RegridSession(ses); RequestRedraw(); SaveState();
    }

    private void SidebarOpInternal(string op)
    {
        switch (op)
        {
            case "show": if (_sidebarW <= 0) ToggleSidebar(); break;
            case "hide": if (_sidebarW > 0) ToggleSidebar(); break;
            case "toggle": ToggleSidebar(); break;
            case "expand": lock (_workspaces) foreach (var w in _workspaces) w.Expanded = true; RequestRedraw(); SaveState(); break;
            case "collapse": lock (_workspaces) foreach (var w in _workspaces) w.Expanded = false; RequestRedraw(); SaveState(); break;
            case "mode:tree": SetSidebarMode(SidebarMode.Tree); break;
            case "mode:flagged": SetSidebarMode(SidebarMode.Flagged); break;
            case "mode:toggle": ToggleFlaggedView(); break;
        }
    }

    /// <summary>Set the sidebar view mode (tree/flagged) and repaint + persist.</summary>
    private void SetSidebarMode(SidebarMode mode)
    {
        if (_sidebarMode == mode) return;
        _sidebarMode = mode;
        RequestRedraw(); SaveState();
    }

    /// <summary>Toggle between the tree outline and the flat flagged working-set view.</summary>
    private void ToggleFlaggedView()
        => SetSidebarMode(_sidebarMode == SidebarMode.Flagged ? SidebarMode.Tree : SidebarMode.Flagged);

    /// <summary>Flag operation on a session (or all): op = on|off|toggle|clear (clear unflags every session).</summary>
    private void FlagOp(Ses? s, string op)
    {
        switch (op)
        {
            case "clear": foreach (var x in AllSessions()) x.Flagged = false; break;
            case "on": if (s is not null) s.Flagged = true; break;
            case "off": if (s is not null) s.Flagged = false; break;
            default: if (s is not null) s.Flagged = !s.Flagged; break; // toggle
        }
        RequestRedraw(); SaveState();
    }

    /// <summary>Focus a single workspace (the active one) or clear focus: op = on|off|toggle. Tree mode only.</summary>
    private void WorkspaceFocusOp(string op) => WorkspaceFocusOp(op, ActiveWorkspace().Id);

    private void WorkspaceFocusOp(string op, string? wsId)
    {
        _focusedWorkspaceId = op switch
        {
            "on" => wsId,
            "off" => null,
            _ => _focusedWorkspaceId is not null ? null : wsId, // toggle
        };
        RequestRedraw(); SaveState();
    }

    private Ses? Find(string? target)
    {
        lock (_workspaces)
        {
            if (string.IsNullOrEmpty(target) || target == "active") return _active;
            var all = _workspaces.SelectMany(w => w.Sessions).ToList();
            return all.FirstOrDefault(x => x.Id == target) ?? all.FirstOrDefault(x => x.Id.StartsWith(target));
        }
    }

    /// <summary>Run an action on the UI thread (pipe callbacks arrive on a background thread).</summary>
    private void Post(Action a)
    {
        _uiActions.Enqueue(a);
        PostMessageW(_hwnd, WM_APP_ACTION, IntPtr.Zero, IntPtr.Zero);
    }

    // Synchronous UI-thread invoke (for control verbs that mutate UI state AND return a value,
    // e.g. session.search). SendMessageW blocks the caller until the UI thread runs the func.
    private Func<string>? _syncFn;
    private string _syncResult = "";
    private readonly object _syncLock = new();
    private string InvokeOnUi(Func<string> fn)
    {
        lock (_syncLock)
        {
            _syncFn = fn;
            SendMessageW(_hwnd, WM_APP_SYNC, IntPtr.Zero, IntPtr.Zero);
            _syncFn = null;
            return _syncResult;
        }
    }
}
