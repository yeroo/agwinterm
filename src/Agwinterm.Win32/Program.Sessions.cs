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
        var session = new TerminalSession(cols, rows);
        session.Emulator.ScrollbackMax = _config.Scrollback;
        var pane = new Pane { Id = paneId, S = session, StartCwd = cwd, FontSize = fontSize };
        // New output snaps this pane back to the live bottom (so output jumps you out of scrollback)
        // and clears any selection (its absolute coordinates would otherwise shift under new history).
        session.OutputReceived += () => { pane.ScrollOffset = 0; pane.ClearSel(); RequestRedraw(); };
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
        session.Emulator.Notified += (title, body) => Post(() => OnNotified(pane, title, body));
        session.Emulator.Progress += (st, val) => Post(() => OnProgress(st, val));   // OSC 9;4 -> taskbar
        session.Emulator.Respond += reply => { session.NotifyActivity(); session.Write(Encoding.UTF8.GetBytes(reply)); };   // Kitty query response -> PTY
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
    private void LaunchShell(TerminalSession session, string? profileName, Dictionary<string, string> env, string? cwd, bool deElevate = false)
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
    private (Pane pane, Ses? ses, bool cover)? FindPaneById(string id)
    {
        lock (_workspaces)
            foreach (var w in _workspaces)
                foreach (var s in w.Sessions)
                {
                    foreach (var p in s.Panes) if (p.Id == id) return (p, s, false);
                    if (s.Scratch is { } sc && sc.Id == id) return (sc, s, true);
                    if (s.Overlay is { } ov && ov.Id == id) return (ov, s, true);
                }
        if (_quick is { } q && q.Id == id) return (q, null, true);
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
        ses.Panes.Insert(idx + 1, np);
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
        ses.Panes.RemoveAt(idx);
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
        ses.Panes.Clear(); ses.Panes.Add(keep); keep.Ratio = 1f; ses.Active = 0;
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
