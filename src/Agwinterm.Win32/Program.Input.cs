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

/// <summary>Input: broadcast, leader chords, links, selection, mark mode, search, key handling.</summary>
internal partial class Program
{
    // ---- Broadcast input (WT's toggleBroadcastInput analog): keyboard input mirrors to every
    // pane of every session in the ACTIVE WORKSPACE. Paste stays targeted (safety). ----
    private bool _broadcast;

    private void ToggleBroadcast()
    {
        _broadcast = !_broadcast;
        ShowToast(_broadcast ? "broadcast ON — typing goes to ALL sessions in this workspace" : "broadcast off");
        RequestRedraw();
    }

    private void Send(string s)
    {
        var surf = ActiveSurface();
        if (surf is { ReadOnly: true }) { ShowToast("pane is read-only"); return; }   // block input to a protected pane
        if (surf is not null) { surf.ScrollOffset = 0; surf.ClearSel(); } // typing snaps to bottom, clears selection
        if (_broadcast && _cover is null && _active is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            List<Pane> panes;
            lock (_workspaces) panes = _active.Ws.Sessions.SelectMany(x => x.Panes).ToList();
            foreach (var p in panes) { p.S.NotifyActivity(); p.S.Write(bytes); }
            return;
        }
        _session?.NotifyActivity();
        _session?.Write(Encoding.UTF8.GetBytes(s));
    }

    /// <summary>Scroll the active pane's scrollback by delta lines (or to top/bottom for ±int.MaxValue).</summary>
    /// <summary>Jump the active pane's scrollback to the previous/next FTCS prompt line.</summary>
    private void JumpPrompt(int dir)
    {
        var p = ActiveSurface();
        if (p is null) return;
        var em = p.S.Emulator;
        List<int> lines;
        int hist;
        lock (p.S.SyncRoot) { lines = em.Marks.Select(m => m.PromptLine).ToList(); hist = em.HistoryCount; }
        if (lines.Count == 0) { ShowToast("no shell marks yet (FTCS comes from the pwsh prompt wrap)"); return; }
        int curTop = hist - p.ScrollOffset;
        int target = dir < 0 ? lines.Where(l => l < curTop).DefaultIfEmpty(-1).Max()
                             : lines.Where(l => l > curTop).DefaultIfEmpty(-1).Min();
        if (target < 0) { ShowToast(dir < 0 ? "no earlier prompt" : "no later prompt"); return; }
        p.ScrollOffset = Math.Clamp(hist - target, 0, hist);
        RequestRedraw();
    }

    /// <summary>Plain-text rows [from..to] of a pane's buffer (history + live), trailing spaces trimmed.</summary>
    private static string RowsText(Pane pane, int from, int to)
    {
        var em = pane.S.Emulator;
        var sb = new StringBuilder();
        lock (pane.S.SyncRoot)
        {
            int cols = em.Screen.Cols, rows = em.Screen.Rows, hist = em.HistoryCount;
            from = Math.Max(0, from);
            to = Math.Min(hist + rows - 1, to);
            for (int line = from; line <= to; line++)
            {
                var row = new StringBuilder();
                for (int c = 0; c < cols; c++)
                {
                    Cell cell = CellAbs(em, hist, rows, cols, line, c);
                    if (cell.Width == 0 && cell.Rune == '\0') continue;
                    if (cell.Rune > 0xFFFF) row.Append(char.ConvertFromUtf32(cell.Rune));
                    else row.Append(cell.Rune == '\0' ? ' ' : (char)cell.Rune);
                }
                sb.Append(row.ToString().TrimEnd(' '));
                if (line != to) sb.Append("\r\n");
            }
        }
        return sb.ToString();
    }

    private bool ScrollActivePane(int deltaLines)
    {
        var p = ActiveSurface();
        if (p is null) return false;
        int hist = p.S.Emulator.HistoryCount;
        int no = deltaLines == int.MaxValue ? hist : deltaLines == int.MinValue ? 0
               : Math.Clamp(p.ScrollOffset + deltaLines, 0, hist);
        if (no != p.ScrollOffset) { p.ScrollOffset = no; RequestRedraw(); }
        return true;
    }

    /// <summary>Dispatch a keymap action id (or "command:&lt;Label&gt;") to the matching behavior.</summary>
    private void RunAction(string action)
    {
        if (action.StartsWith("command:", StringComparison.OrdinalIgnoreCase))
        {
            string label = action["command:".Length..];
            var cmd = _commands.FirstOrDefault(c => string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase));
            if (cmd is not null) RunCustomCommand(cmd);
            else ShowToast($"no command '{label}'");
            return;
        }
        switch (action)
        {
            case "new_session": CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), true); break;
            case "reopen_session": ReopenMostRecent(); break;
            case "new_workspace": CreateWorkspace(Guid.NewGuid().ToString(), null); break;
            case "close_session": case "close_pane": if (_coverKind == 3) CloseActiveOverlay(); else if (_cover is not null) HideCover(); else CloseActivePane(); break;
            case "split_pane": SplitOp("toggle"); break;   // toggle 1<->2 panes (agterm-style), not add
            case "toggle_scratch": if (_active is not null) ScratchOp(_active, "toggle"); break;
            case "quick_terminal": QuickOp("toggle"); break;
            case "focus_left_pane": FocusPane(-1); break;
            case "focus_right_pane": FocusPane(1); break;
            case "next_session": CycleSession(1); break;
            case "previous_session": CycleSession(-1); break;
            case "toggle_sidebar": ToggleSidebar(); break;
            case "rename_session": if (_active is not null) StartRename(_active); break;
            case "delete_workspace": if (_active is not null) DeleteWorkspace(_active.Ws); break;
            case "session_palette": TogglePalette(PaletteKind.Sessions); break;
            case "action_palette": TogglePalette(PaletteKind.Actions); break;
            case "attention_list": TogglePalette(PaletteKind.Attention); break;
            case "custom_palette": TogglePalette(PaletteKind.Custom); break;
            case "new_window": WindowNew(null); break;
            case "close_window": DestroyWindow(_hwnd); break; // WM_DESTROY tears down + updates the library
            case "switch_window": TogglePalette(PaletteKind.Windows); break;
            case "next_attention": GoToNextAttention(1); break;
            case "previous_attention": GoToNextAttention(-1); break;
            case "reload_keymap": ReloadKeymap(); break;
            case "toggle_search": ToggleSearch(); break;
            case "increase_font_size": ChangeFontSize(1); break;
            case "decrease_font_size": ChangeFontSize(-1); break;
            case "reset_font_size": ChangeFontSize(0); break;
            case "install_shell_integration": InstallShellIntegration(); break;
            case "toggle_flag": if (_active is not null) FlagOp(_active, "toggle"); break;
            case "toggle_flagged_view": ToggleFlaggedView(); break;
            case "focus_workspace": WorkspaceFocusOp("toggle"); break;
            case "close_cover": CloseCover(); break;
            case "toggle_fullscreen": ToggleFullscreen(); break;
            case "toggle_broadcast": ToggleBroadcast(); break;
            case "mark_mode": ToggleMarkMode(); break;
            case "toggle_read_only": if (ActiveSurface() is { } rp) { rp.ReadOnly = !rp.ReadOnly; ShowToast(rp.ReadOnly ? "pane read-only ON" : "pane read-only off"); RequestRedraw(); } break;
            case "previous_prompt": JumpPrompt(-1); break;
            case "next_prompt": JumpPrompt(1); break;
            case "select_all": if (ActiveSurface() is { } sa) SelectAll(sa); break;
            case "copy_selection": if (ActiveSurface() is { } sc && sc.HasSel) CopySelection(sc); break;
            case "paste": if (ActiveSurface() is { } sp2) PasteInto(sp2); break;
        }
    }

    /// <summary>Install the $PROFILE OSC-7 shell integration off the UI thread, then toast the result.</summary>
    private void InstallShellIntegration() => RunInstaller(Agwinterm.Pty.ShellIntegrationInstaller.Install);

    /// <summary>Opt-in: add agwintermctl to the user PATH (agterm's "Install Command Line Tool").</summary>
    private void InstallCli() => RunInstaller(Agwinterm.Pty.CliInstaller.Install);
    /// <summary>Opt-in: install Claude Code / Codex / generic agent status hooks.</summary>
    private void InstallHooks() => RunInstaller(Agwinterm.Pty.AgentHooks.Install);
    /// <summary>Opt-in: install the agent skill (teaches agents to drive agwinterm via agwintermctl).</summary>
    private void InstallSkill() => RunInstaller(Agwinterm.Pty.AgentSkill.Install);

    /// <summary>Run an installer off the UI thread and toast its result.</summary>
    private void RunInstaller(Func<string> installer)
        => System.Threading.Tasks.Task.Run(() => { string r = installer(); Post(() => ShowToast(r)); });

    /// <summary>Run a configured custom command per its mode (send|new|overlay|detached), expanding {AGW_*}.</summary>
    private void RunCustomCommand(Keymap.CmdDef cmd) => RunCommandText(cmd.Text, cmd.Mode);

    /// <summary>Run an arbitrary command string in a mode, expanding {AGW_*} tokens and injecting $AGW_*
    /// env from the active session. Returns the expanded command line (for the control API / observability).</summary>
    private string RunCommandText(string text, string? mode)
    {
        var ctx = _active;
        string expanded = ExpandAgwTokens(text, ctx);
        switch ((mode ?? "send").ToLowerInvariant())
        {
            case "new":
                CreateSession(Guid.NewGuid().ToString(), null, RawCwdOf(ctx), ActiveWorkspace(), makeActive: true,
                    command: expanded, interactive: true, extraEnv: AgwEnv(ctx));
                break;
            case "overlay":
                if (ctx is not null) OverlayOpen(ctx, expanded, 0, false, AgwEnv(ctx));
                else ShowToast("no session for overlay command");
                break;
            case "detached":
                RunDetached(expanded, RawCwdOf(ctx), AgwEnv(ctx));
                break;
            default: // send — type it into the active session, as if the user typed it + Enter
                Send(expanded.Replace("\r", "").Replace("\n", "") + "\r");
                break;
        }
        return expanded;
    }

    /// <summary>The active pane's real working directory (Windows path), or "" if unknown.</summary>
    private static string RawCwdOf(Ses? ses)
    {
        if (ses is null) return "";
        string live = PrettyCwd(SafeCwd(ses));
        return live.Length > 0 ? live : (ses.StartCwd ?? "");
    }

    /// <summary>Best-effort path to agwintermctl.exe (next to us), else just "agwintermctl" (assume PATH).</summary>
    private static string CtlPath()
    {
        try { string p = Path.Combine(AppContext.BaseDirectory, "agwintermctl.exe"); if (File.Exists(p)) return p; }
        catch { }
        return "agwintermctl";
    }

    /// <summary>The {AGW_*} / $AGW_* values for a session context (active by default).</summary>
    private static Dictionary<string, string> AgwValues(Ses? ses) => new(StringComparer.Ordinal)
    {
        ["AGW_SESSION"] = ses?.Name ?? "",
        ["AGW_SESSION_ID"] = ses?.Id ?? "",
        ["AGW_WORKSPACE"] = ses?.Ws.Name ?? "",
        ["AGW_CWD"] = RawCwdOf(ses),
        ["AGW_PANE_ID"] = ses?.ActivePane.Id ?? "",
        ["AGW_APP"] = CtlPath(),
    };

    /// <summary>Expand {AGW_*} tokens; unknown {AGW_*} tokens expand to "".</summary>
    private static string ExpandAgwTokens(string text, Ses? ses)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("{AGW_", StringComparison.Ordinal) < 0) return text;
        var vals = AgwValues(ses);
        return System.Text.RegularExpressions.Regex.Replace(text, @"\{AGW_[A-Z0-9_]+\}",
            m => vals.TryGetValue(m.Value[1..^1], out var v) ? v : "");
    }

    /// <summary>$AGW_* environment (+ AGWINTERM=1) for a launched custom-command process.</summary>
    private static Dictionary<string, string> AgwEnv(Ses? ses)
    {
        var d = AgwValues(ses);
        d["AGWINTERM"] = "1";
        return d;
    }

    /// <summary>Detached run: an independent OS process (no PTY, not tied to a session), via cmd /c so
    /// shell syntax (redirection, `start &lt;url&gt;`) works. Non-blocking; inherits the $AGW_* context + cwd.</summary>
    private void RunDetached(string command, string? cwd, Dictionary<string, string> env)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(cwd) ? "" : cwd!,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { ShowToast("detached: " + ex.Message); }
    }

    // ---- Leader/prefix chord state machine (used by OnKeyDown + the command.leader control verb) ----

    private void BeginLeader() { _leaderPending = true; _leaderAtMs = Environment.TickCount64; RequestRedraw(); }
    private void CancelLeader() { if (_leaderPending) { _leaderPending = false; RequestRedraw(); } }

    /// <summary>Resolve a second chord against the leader bindings (runs it or toasts); clears pending.</summary>
    private string ResolveLeader(string chord)
    {
        _leaderPending = false; RequestRedraw();
        if (_leaderBindings.TryGetValue(chord, out var action)) { RunAction(action); return "ran " + action; }
        ShowToast($"leader: no binding for {chord}");
        return "no leader binding";
    }

    /// <summary>Build the human-readable command list (label, mode, resolved chord, text) for command.list.</summary>
    private static string CommandListText()
    {
        string ChordOf(string label)
        {
            string want = "command:" + label;
            foreach (var kv in _keymap) if (string.Equals(kv.Value, want, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            foreach (var kv in _leaderBindings) if (string.Equals(kv.Value, want, StringComparison.OrdinalIgnoreCase)) return "leader " + kv.Key;
            return "";
        }
        if (_commands.Count == 0) return "(no custom commands defined in keymap.conf)";
        var sb = new StringBuilder();
        foreach (var c in _commands)
            sb.Append(c.Label).Append('\t').Append(c.Mode).Append('\t')
              .Append(ChordOf(c.Label) is { Length: > 0 } ch ? ch : "-").Append('\t').Append(c.Text).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>UI-thread driver for the command.leader control verb (state|begin|cancel|key:&lt;chord&gt;).</summary>
    private string LeaderOp(string op)
    {
        if (op == "begin") { if (_leader is null) return "no leader configured"; BeginLeader(); return "pending"; }
        if (op == "cancel") { CancelLeader(); return "idle"; }
        if (op.StartsWith("key:", StringComparison.OrdinalIgnoreCase))
        {
            string? chord = Keymap.Canonicalize(op[4..]);
            if (chord is null) return "bad chord";
            if (!_leaderPending) BeginLeader();
            return ResolveLeader(chord);
        }
        return _leaderPending ? "pending" : "idle"; // "state"
    }

    private static string KeymapPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm", "keymap.conf");

    private static void LoadKeymap()
    {
        try
        {
            string path = KeymapPath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Keymap.StarterText);
            }
            var parsed = Keymap.Parse(File.ReadAllText(path));
            _keymap = parsed.Bindings;
            _commands = parsed.Commands;
            _leader = parsed.Leader;
            _leaderBindings = parsed.LeaderBindings;
            _keymapDiag = parsed.Diagnostics.ToArray();
        }
        catch
        {
            _keymap = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (c, a) in Keymap.DefaultBindings) _keymap[c] = a;
            _commands = new();
            _leader = null;
            _leaderBindings = new(StringComparer.OrdinalIgnoreCase);
            _keymapDiag = Array.Empty<string>();
        }
    }

    private void ReloadKeymap()
    {
        LoadKeymap();
        _leaderPending = false;   // any in-progress leader sequence is abandoned on reload
        ShowToast(_keymapDiag.Length == 0
            ? "keymap reloaded"
            : $"keymap: {_keymapDiag.Length} issue(s) — {_keymapDiag[0]}");
    }

    /// <summary>Coalesce many output/decode notifications into a single pending repaint.</summary>
    private void RequestRedraw()
    {
        if (System.Threading.Interlocked.Exchange(ref _redrawPending, 1) == 0)
            PostMessageW(_hwnd, WM_APP_REDRAW, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Report a mouse event given raw client pixels packed in lParam (button code already encoded).</summary>
    private void SendMousePx(int btn, IntPtr lParam, bool press)
        => SendMouse(btn, LoWord(lParam), HiWord(lParam), press);

    /// <summary>Origin (px) + metrics of the ACTIVE pane, for mouse→cell mapping.</summary>
    private (float ox, float oy, float cw, float ch) ActivePaneView()
    {
        if (_active is not null)
            foreach (var (pane, x, y, _, _) in PaneLayout(_active))
                if (ReferenceEquals(pane, _active.ActivePane)) { var (_, cw, ch) = Metrics(pane.FontSize); return (x, y, cw, ch); }
        var (_, c2, h2) = CurrentMetrics();
        return (ContentX, ContentY, c2, h2);
    }

    /// <summary>Focus the pane of the active session under client-x (no-op if single pane / no session).</summary>
    private void FocusPaneAtX(int px)
    {
        if (_active is null || _active.Panes.Count < 2) return;
        foreach (var (pane, x, _, w, _) in PaneLayout(_active))
            if (px >= x && px < x + w + DividerW) { _active.Active = _active.Panes.IndexOf(pane); _session = _active.S; RequestRedraw(); return; }
    }

    /// <summary>Left-pane index of the divider gutter under client-(x,y), or -1.</summary>
    private int DividerAtX(int px, int py)
    {
        if (_active is null || _active.Panes.Count < 2) return -1;
        var lay = PaneLayout(_active);
        if (py < (int)lay[0].y || py > (int)(lay[0].y + lay[0].h)) return -1;
        for (int i = 0; i < lay.Count - 1; i++)
        {
            float gx = lay[i].x + lay[i].w;
            if (px >= gx - 2 && px <= gx + DividerW + 2) return i;
        }
        return -1;
    }

    /// <summary>Drag a divider: shift width between the two adjacent panes (clamped).</summary>
    private void DragDivider(int px)
    {
        if (_active is null || _divLeft < 0 || _divLeft + 1 >= _active.Panes.Count) return;
        var (x0, _, totalW, _) = ContentArea();
        float avail = MathF.Max(_active.Panes.Count, totalW - (_active.Panes.Count - 1) * DividerW);
        float sum = _active.Panes.Sum(p => p.Ratio);
        var lay = PaneLayout(_active);
        float leftStart = lay[_divLeft].x;
        float pairRatio = _active.Panes[_divLeft].Ratio + _active.Panes[_divLeft + 1].Ratio;
        float newLeftW = Math.Clamp(px - leftStart, 24f, (pairRatio / sum) * avail - 24f);
        float newLeftRatio = (newLeftW / avail) * sum;
        _active.Panes[_divLeft + 1].Ratio = pairRatio - newLeftRatio;
        _active.Panes[_divLeft].Ratio = newLeftRatio;
        RegridSession(_active);
        RequestRedraw();
    }

    /// <summary>Encode a mouse event (SGR or legacy) and send it to the child. Mirrors the WinUI shell.</summary>
    private void SendMouse(int btn, int pxX, int pxY, bool press)
    {
        var em = _session?.Emulator;
        if (em is null || !em.MouseReporting) return;
        var (ox, oy, cw, ch) = ActivePaneView();
        int col = Math.Clamp((int)((pxX - ox) / cw), 0, em.Screen.Cols - 1);
        int row = Math.Clamp((int)((pxY - oy) / ch), 0, em.Screen.Rows - 1);
        string seq = em.MouseSgr
            ? $"\x1b[<{btn};{col + 1};{row + 1}{(press ? 'M' : 'm')}"
            : "\x1b[M" + (char)(32 + (press ? btn : 3)) + (char)(33 + col) + (char)(33 + row);
        _session?.Write(Encoding.UTF8.GetBytes(seq));
    }

    // ---- Clickable links: Ctrl+hover underlines a URL (hand cursor); Ctrl+click opens it ----

    private static readonly System.Text.RegularExpressions.Regex LinkRx = new(
        @"(?:https?|file)://[^\s""'<>\[\]{}|\\^`]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private string? _linkUrl;      // hovered link (null = none); drawn underlined in RenderTerminal
    private Pane? _linkPane;
    private int _linkLine, _linkC0, _linkC1;   // absolute line + column span

    /// <summary>Trailing punctuation is almost never part of a URL pasted into terminal output.</summary>
    private static string TrimLink(string s)
    {
        while (s.Length > 0 && s[^1] is '.' or ',' or ';' or ':' or '!' or '?' or ')' or '\'' or '"') s = s[..^1];
        return s;
    }

    /// <summary>Refresh the Ctrl+hover link state for the mouse point; true when over a link.</summary>
    private bool UpdateLinkHover(int mx, int my)
    {
        string? url = null; Pane? pane = null; int line = 0, c0 = 0, c1 = 0;
        if (PaneAt(mx, my) is { } h)
        {
            var (ln, col) = CellAtPx(h.pane, h.ox, h.oy, h.cw, h.ch, mx, my);
            var em = h.pane.S.Emulator;
            var sb = new StringBuilder();
            lock (h.pane.S.SyncRoot)
            {
                int cols = em.Screen.Cols, rows = em.Screen.Rows, hist = em.HistoryCount;
                for (int c = 0; c < cols; c++)   // string index == screen column (spacers/astral -> ' '; URLs are ASCII)
                {
                    Cell cell = CellAbs(em, hist, rows, cols, ln, c);
                    sb.Append(cell.Width == 0 || cell.Rune == '\0' || cell.Rune > 0xFFFF ? ' ' : (char)cell.Rune);
                }
            }
            string row = sb.ToString();
            foreach (System.Text.RegularExpressions.Match m in LinkRx.Matches(row))
            {
                int end = m.Index + TrimLink(m.Value).Length;
                if (col >= m.Index && col < end)
                { url = row[m.Index..end]; pane = h.pane; line = ln; c0 = m.Index; c1 = end - 1; break; }
            }
        }
        if (url != _linkUrl || !ReferenceEquals(pane, _linkPane) || line != _linkLine || c0 != _linkC0)
        { _linkUrl = url; _linkPane = pane; _linkLine = line; _linkC0 = c0; _linkC1 = c1; RequestRedraw(); }
        return _linkUrl is not null;
    }

    private void ClearLinkHover()
    {
        if (_linkUrl is null) return;
        _linkUrl = null; _linkPane = null; RequestRedraw();
    }

    /// <summary>Validated open: http/https to the shell; file:// reveals the target in Explorer
    /// (selects the file, or opens the folder for a directory). Everything else is blocked.</summary>
    private void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) { ShowToast("invalid link"); return; }
        if (u.Scheme is "http" or "https") { ShellExecuteW(IntPtr.Zero, "open", url, null, null, SW_SHOW); return; }
        if (u.Scheme == "file")
        {
            string path = u.LocalPath;
            try
            {
                if (Directory.Exists(path))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                else if (File.Exists(path))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else ShowToast("path not found: " + path);
            }
            catch (Exception ex) { ShowToast("reveal failed: " + ex.Message); }
            return;
        }
        ShowToast("blocked non-http(s) link");
    }

    // ---- Text selection + clipboard ----

    /// <summary>Pane of the active session under a client point + its origin/metrics, or null.</summary>
    private (Pane pane, float ox, float oy, float cw, float ch)? PaneAt(int px, int py)
    {
        if (px < (int)_sidebarW || py < (int)TitleBarH || py >= ClientH() - (int)FooterH) return null;
        if (_cover is not null) { var (cx, cy, _, _) = CoverRect(); var (_, ccw, cch) = Metrics(_cover.FontSize); return (_cover, cx, cy, ccw, cch); }
        if (_active is null) return null;
        foreach (var (pane, x, y, w, _) in PaneLayout(_active))
            if (px >= x && px < x + w + DividerW)
            {
                var (_, cw, ch) = Metrics(pane.FontSize);
                return (pane, x, y, cw, ch);
            }
        return null;
    }

    /// <summary>Origin/cell-size/row-count of a specific pane (for drag-autoscroll), or null if not laid out.</summary>
    private (float ox, float oy, float cw, float ch, int rows)? PaneBox(Pane pane)
    {
        if (_cover is not null && ReferenceEquals(pane, _cover))
        {
            var (cx, cy, _, chh) = CoverRect();
            var (_, ccw, cch) = Metrics(_cover.FontSize);
            return (cx, cy, ccw, cch, Math.Max(1, (int)(chh / cch)));
        }
        if (_active is null) return null;
        foreach (var (p, x, y, _, h) in PaneLayout(_active))
            if (ReferenceEquals(p, pane))
            {
                var (_, cw, ch) = Metrics(pane.FontSize);
                return (x, y, cw, ch, Math.Max(1, (int)(h / ch)));
            }
        return null;
    }

    /// <summary>Map a client point to an ABSOLUTE (line,col) in the pane (history + live grid).</summary>
    private (int line, int col) CellAtPx(Pane pane, float ox, float oy, float cw, float ch, int px, int py)
    {
        var em = pane.S.Emulator;
        int cols, rows, hist;
        lock (pane.S.SyncRoot) { cols = em.Screen.Cols; rows = em.Screen.Rows; hist = em.HistoryCount; }
        int off = Math.Clamp(pane.ScrollOffset, 0, hist);
        int vr = Math.Clamp((int)((py - oy) / ch), 0, rows - 1);
        int col = Math.Clamp((int)((px - ox) / cw), 0, cols - 1);
        int line = Math.Clamp(hist - off + vr, 0, hist + rows - 1);
        return (line, col);
    }

    private static Cell CellAbs(TerminalEmulator em, int hist, int rows, int cols, int line, int col)
    {
        col = Math.Clamp(col, 0, cols - 1);
        if (line < hist) return em.GetHistoryCell(line, col);
        int live = line - hist;
        return (live >= 0 && live < rows) ? em.Screen[live, col] : Cell.Empty;
    }

    private static void NormSel(Pane p, out int l0, out int c0, out int l1, out int c1)
    {
        bool ancFirst = p.SelAncLine < p.SelFocLine || (p.SelAncLine == p.SelFocLine && p.SelAncCol <= p.SelFocCol);
        (l0, c0) = ancFirst ? (p.SelAncLine, p.SelAncCol) : (p.SelFocLine, p.SelFocCol);
        (l1, c1) = ancFirst ? (p.SelFocLine, p.SelFocCol) : (p.SelAncLine, p.SelAncCol);
    }

    private static string SelectionText(Pane pane)
    {
        if (!pane.HasSel) return "";
        var em = pane.S.Emulator;
        var sb = new StringBuilder();
        lock (pane.S.SyncRoot)
        {
            int cols = em.Screen.Cols, rows = em.Screen.Rows, hist = em.HistoryCount;
            NormSel(pane, out int l0, out int c0, out int l1, out int c1);
            // Block selection: every row uses the same [minCol, maxCol] column span (rectangle).
            int blkFrom = Math.Min(pane.SelAncCol, pane.SelFocCol), blkTo = Math.Max(pane.SelAncCol, pane.SelFocCol);
            for (int line = l0; line <= l1; line++)
            {
                int from = pane.BlockSel ? blkFrom : (line == l0 ? c0 : 0);
                int to = (pane.BlockSel ? blkTo : (line == l1 ? c1 : cols - 1)) + 1; // inclusive end col
                var row = new StringBuilder();
                for (int c = Math.Max(0, from); c < Math.Min(cols, to); c++)
                {
                    Cell cell = CellAbs(em, hist, rows, cols, line, c);
                    if (cell.Width == 0) continue; // trailing spacer of a wide glyph
                    if (cell.Rune > 0xFFFF) row.Append(char.ConvertFromUtf32(cell.Rune));   // full fidelity for copy
                    else row.Append(cell.Rune == '\0' ? ' ' : (char)cell.Rune);
                }
                sb.Append(row.ToString().TrimEnd(' '));
                if (line != l1) sb.Append("\r\n");
            }
        }
        return sb.ToString();
    }

    // ---- Keyboard mark mode (WT's markMode): build a selection without the mouse ----
    private bool _markMode;

    private void ToggleMarkMode()
    {
        if (_markMode) { _markMode = false; ShowToast("mark mode off"); RequestRedraw(); return; }
        if (ActiveSurface() is not { } p) return;
        var em = p.S.Emulator;
        int hist, row, col;
        lock (p.S.SyncRoot) { hist = em.HistoryCount; row = em.CursorRow; col = em.CursorCol; }
        int abs = hist - Math.Clamp(p.ScrollOffset, 0, hist) + row;   // buffer-absolute caret line
        p.SelAncLine = p.SelFocLine = abs; p.SelAncCol = p.SelFocCol = col; p.HasSel = true; p.BlockSel = false;
        _markMode = true;
        ShowToast("mark mode: arrows select · Shift+arrows by word/line · Enter copies · Esc exits");
        RequestRedraw();
    }

    /// <summary>Returns true if the key was consumed by mark mode.</summary>
    private bool MarkModeKey(int vk, bool ctrl)
    {
        if (ActiveSurface() is not { } p) { _markMode = false; return false; }
        var em = p.S.Emulator;
        int cols, rows, hist;
        lock (p.S.SyncRoot) { cols = em.Screen.Cols; rows = em.Screen.Rows; hist = em.HistoryCount; }
        int maxLine = hist + rows - 1;
        switch (vk)
        {
            case VK_ESCAPE: _markMode = false; p.ClearSel(); RequestRedraw(); return true;
            case VK_LEFT: p.SelFocCol = Math.Max(0, p.SelFocCol - 1); break;
            case VK_RIGHT: p.SelFocCol = Math.Min(cols - 1, p.SelFocCol + 1); break;
            case VK_UP: p.SelFocLine = Math.Max(0, p.SelFocLine - 1); break;
            case VK_DOWN: p.SelFocLine = Math.Min(maxLine, p.SelFocLine + 1); break;
            case VK_HOME: p.SelFocCol = 0; break;
            case VK_END: p.SelFocCol = cols - 1; break;
            case VK_RETURN: CopySelection(p, clear: false); _markMode = false; ShowToast("copied"); RequestRedraw(); return true;
            case 0x43 when ctrl: CopySelection(p, clear: false); _markMode = false; ShowToast("copied"); RequestRedraw(); return true; // Ctrl+C
            default: return true;   // swallow everything else while in mark mode
        }
        p.HasSel = true;
        // keep the focus line visible (scroll the pane if the caret walked off the top)
        int topAbs = hist - Math.Clamp(p.ScrollOffset, 0, hist);
        if (p.SelFocLine < topAbs) p.ScrollOffset = Math.Clamp(hist - p.SelFocLine, 0, hist);
        else if (p.SelFocLine >= topAbs + rows) p.ScrollOffset = Math.Clamp(hist - (p.SelFocLine - rows + 1), 0, hist);
        RequestRedraw();
        return true;
    }

    private void BeginSelect(Pane p, int line, int col, bool extend)
    {
        if (extend && p.HasSel) { p.SelFocLine = line; p.SelFocCol = col; _selMoved = true; }
        else { p.SelAncLine = p.SelFocLine = line; p.SelAncCol = p.SelFocCol = col; p.HasSel = false; _selMoved = false; }
    }

    private void UpdateSelect(Pane p, int line, int col)
    {
        p.SelFocLine = line; p.SelFocCol = col; p.HasSel = true; _selMoved = true;
    }

    private void SelectWord(Pane p, int line, int col)
    {
        var em = p.S.Emulator;
        lock (p.S.SyncRoot)
        {
            int cols = em.Screen.Cols, rows = em.Screen.Rows, hist = em.HistoryCount;
            string delims = _config.WordDelimiters;
            bool Word(int c)
            {
                int ch = CellAbs(em, hist, rows, cols, line, c).Rune;
                return ch != ' ' && ch != '\0' && (delims.Length == 0 || ch > 0xFFFF || delims.IndexOf((char)ch) < 0);
            }
            if (col < 0 || col >= cols || !Word(col)) return;
            int a = col, b = col;
            while (a > 0 && Word(a - 1)) a--;
            while (b < cols - 1 && Word(b + 1)) b++;
            p.SelAncLine = p.SelFocLine = line; p.SelAncCol = a; p.SelFocCol = b; p.HasSel = true;
        }
    }

    private void SelectLine(Pane p, int line)
    {
        int cols; lock (p.S.SyncRoot) cols = p.S.Emulator.Screen.Cols;
        p.SelAncLine = p.SelFocLine = line; p.SelAncCol = 0; p.SelFocCol = cols - 1; p.HasSel = true;
    }

    private void CopySelection(Pane pane, bool clear = true)
    {
        string t = SelectionText(pane);
        if (t.Length > 0) ClipboardSet(t);
        if (clear) pane.ClearSel();
        RequestRedraw();
    }

    /// <summary>Select the pane's entire buffer — all scrollback history through the last live row.</summary>
    private void SelectAll(Pane p)
    {
        var em = p.S.Emulator;
        int cols, rows, hist;
        lock (p.S.SyncRoot) { cols = em.Screen.Cols; rows = em.Screen.Rows; hist = em.HistoryCount; }
        p.SelAncLine = 0; p.SelAncCol = 0;
        p.SelFocLine = hist + rows - 1; p.SelFocCol = cols - 1;
        p.HasSel = (hist + rows) > 0 && cols > 0;
        RequestRedraw();
    }

    /// <summary>Called when a selection is finished (drag mouse-up / word / line select). Honors copy-on-select
    /// by copying to the clipboard without clearing the highlight.</summary>
    private void FinalizeSelection(Pane p)
    {
        if (_config.CopyOnSelect && p.HasSel) CopySelection(p, clear: false);
    }

    private void StopSelAutoscroll()
    {
        if (_selAutoDir != 0) { _selAutoDir = 0; KillTimer(_hwnd, (IntPtr)SelAutoTimer); }
    }

    /// <summary>Drag-autoscroll tick: while the mouse is held above/below the selection pane, scroll its
    /// scrollback one line toward the cursor and extend the selection to the newly-revealed edge.</summary>
    private void SelAutoscrollTick()
    {
        if (!_selecting || _selAutoDir == 0 || _selPane is not { } sp || PaneBox(sp) is not { } bx) { StopSelAutoscroll(); return; }
        int cols, rows, hist;
        lock (sp.S.SyncRoot) { cols = sp.S.Emulator.Screen.Cols; rows = sp.S.Emulator.Screen.Rows; hist = sp.S.Emulator.HistoryCount; }
        // dir -1 (mouse above) reveals older lines -> larger ScrollOffset; dir +1 (below) -> smaller.
        int no = Math.Clamp(sp.ScrollOffset - _selAutoDir, 0, hist);
        sp.ScrollOffset = no;
        int vr = _selAutoDir < 0 ? 0 : rows - 1;                      // extend to the top/bottom visible row
        int line = Math.Clamp(hist - no + vr, 0, hist + rows - 1);
        int col = Math.Clamp((int)((_selMouseX - bx.ox) / bx.cw), 0, cols - 1);
        UpdateSelect(sp, line, col);
        RequestRedraw();
    }

    private void PasteInto(Pane pane) => PasteTextInto(pane, ClipboardGet());

    /// <summary>Paste literal text into a pane, honoring bracketed-paste mode (shared by Ctrl+V and the API).</summary>
    private void PasteTextInto(Pane pane, string t)
    {
        if (pane.ReadOnly) { ShowToast("pane is read-only"); return; }
        if (t.Length == 0) return;
        t = t.Replace("\r\n", "\r").Replace("\n", "\r");
        if (pane.S.Emulator.BracketedPaste) t = "\x1b[200~" + t + "\x1b[201~";
        pane.ScrollOffset = 0; pane.S.NotifyActivity();
        pane.S.Write(Encoding.UTF8.GetBytes(t));
        RequestRedraw();
    }

    // ---- In-terminal search (find bar over the active pane's buffer + scrollback) ----

    private void ToggleSearch()
    {
        if (_searchActive) { CloseSearch(); return; }
        _searchActive = true; _searchQuery = ""; _searchMatches.Clear(); _searchCur = 0;
        RequestRedraw();
    }

    private void CloseSearch() { _searchActive = false; _searchMatches.Clear(); RequestRedraw(); }

    /// <summary>Recompute all case-insensitive matches for _searchQuery over the active pane's history + live grid.</summary>
    private void RecomputeSearch()
    {
        _searchMatches.Clear();
        var ap = ActiveSurface();
        if (ap is null || _searchQuery.Length == 0) return;
        string q = _searchQuery.ToLowerInvariant();
        var em = ap.S.Emulator;
        lock (ap.S.SyncRoot)
        {
            int cols = em.Screen.Cols, rows = em.Screen.Rows, hist = em.HistoryCount;
            var sb = new StringBuilder(cols);
            for (int line = 0; line < hist + rows; line++)
            {
                sb.Clear();
                for (int c = 0; c < cols; c++)
                {
                    Cell cell = CellAbs(em, hist, rows, cols, line, c);
                    // astral -> one replacement char so string index keeps matching the column
                    sb.Append(cell.Rune == '\0' ? ' ' : cell.Rune > 0xFFFF ? '�' : (char)cell.Rune);
                }
                string text = sb.ToString().ToLowerInvariant();
                int idx = 0;
                while ((idx = text.IndexOf(q, idx, StringComparison.Ordinal)) >= 0)
                {
                    _searchMatches.Add((line, idx, idx + q.Length - 1));
                    idx += q.Length;
                }
            }
        }
        if (_searchCur >= _searchMatches.Count) _searchCur = 0;
    }

    private void SearchStep(int dir)
    {
        if (_searchMatches.Count == 0) return;
        int n = _searchMatches.Count;
        _searchCur = ((_searchCur + dir) % n + n) % n;
        ScrollToMatch(); RequestRedraw();
    }

    private void ScrollToMatch()
    {
        var ap = ActiveSurface();
        if (ap is null || _searchCur < 0 || _searchCur >= _searchMatches.Count) return;
        int ml = _searchMatches[_searchCur].Line;
        var em = ap.S.Emulator;
        int rows, hist; lock (ap.S.SyncRoot) { rows = em.Screen.Rows; hist = em.HistoryCount; }
        ap.ScrollOffset = Math.Clamp(hist - ml + rows / 2, 0, hist); // centre the match; live grid => snaps to 0
    }

    private string SearchStatus()
        => _searchMatches.Count == 0 ? (_searchQuery.Length == 0 ? "" : "no matches")
           : $"{_searchCur + 1} of {_searchMatches.Count}";

    private bool SearchKeyDown(int vk)
    {
        bool shift = KeyDown(VK_SHIFT);
        switch (vk)
        {
            case VK_ESCAPE: CloseSearch(); return true;
            case VK_RETURN: SearchStep(shift ? -1 : 1); return true;
            case 0x72: SearchStep(shift ? -1 : 1); return true; // F3
            case VK_BACK:
                if (_searchQuery.Length > 0) { _searchQuery = _searchQuery[..^1]; RecomputeSearch(); _searchCur = 0; ScrollToMatch(); RequestRedraw(); }
                return true;
        }
        return true; // consume all keys while the find bar is open
    }

    /// <summary>Open the clipboard with retries: clipboard history / cloud sync / managers briefly
    /// hold it open on every change, so a single failed OpenClipboard is common and transient.</summary>
    private bool OpenClipboardRetry()
    {
        for (int i = 0; ; i++)
        {
            if (OpenClipboard(_hwnd)) return true;
            if (i >= 9) return false;
            System.Threading.Thread.Sleep(10);
        }
    }

    private void ClipboardSet(string text)
    {
        // Build the handle before touching the clipboard so a failed alloc never leaves it emptied.
        byte[] buf = Encoding.Unicode.GetBytes(text + "\0");
        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)buf.Length);
        if (h == IntPtr.Zero) return;
        IntPtr p = GlobalLock(h);
        if (p == IntPtr.Zero) { GlobalFree(h); return; }
        Marshal.Copy(buf, 0, p, buf.Length);
        GlobalUnlock(h);

        if (!OpenClipboardRetry()) { GlobalFree(h); return; }
        try
        {
            EmptyClipboard();
            if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero)
                GlobalFree(h); // ownership only transfers to the clipboard on success
        }
        finally { CloseClipboard(); }
    }

    private string ClipboardGet()
    {
        if (!OpenClipboardRetry()) return "";
        try
        {
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return "";
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Selection text of the target session's active pane (for the control API).</summary>
    private static string SessionSelectionText(Ses s) => SelectionText(s.ActivePane);

    /// <summary>Returns true if the key was consumed (matches the WinUI key table).</summary>
    private bool OnKeyDown(int vk)
    {
        bool ctrl = KeyDown(VK_CONTROL), shift = KeyDown(VK_SHIFT), alt = KeyDown(VK_MENU);

        // While the popup context menu is up it owns the keyboard (↑↓ Enter Esc; the popup window
        // never takes focus, so keys arrive here and are forwarded).
        if (_menuHwnd != IntPtr.Zero) return MenuKeyDown(vk);

        // Keyboard mark mode (mouseless selection): arrows move the selection focus, Enter/Ctrl+C
        // copies, Esc exits. Owns the keyboard while active.
        if (_markMode && MarkModeKey(vk, ctrl)) return true;

        // A --wait overlay whose program has exited hangs around; any key dismisses it.
        if (_coverKind == 3 && _ovlOwner is { OverlayExited: true }) { CloseActiveOverlay(); return true; }

        // Escape during an MRU walk cancels back to where the walk began.
        if (_mruWalking && vk == VK_ESCAPE) { MruCancel(); return true; }

        // While a palette is open it owns the keyboard (nav here, typing via WM_CHAR).
        if (_setOpen) return SettingsKey(vk);
        if (_palette != PaletteKind.None) return PaletteKeyDown(vk);
        // While the find bar is open it owns the keyboard (Enter/F3 nav, Esc close, Backspace edit).
        if (_searchActive) return SearchKeyDown(vk);

        // Leader/prefix sequence (tmux-style). When pending, the next chord resolves against the leader
        // bindings; Esc / timeout cancels; modifier-only keydowns stay pending. Checked before the normal
        // chord/clipboard/terminal paths so the leader chord and its follow-up key are captured.
        if (_leaderPending)
        {
            if (Environment.TickCount64 - _leaderAtMs > LeaderTimeoutMs) CancelLeader(); // expired — fall through to normal handling
            else
            {
                if (vk == VK_ESCAPE) { CancelLeader(); return true; }
                string? lc = Keymap.ChordFor(vk, ctrl, alt, shift);
                if (lc is null) return true;   // lone modifier — keep waiting, swallow
                ResolveLeader(lc);
                return true;
            }
        }
        if (_leader is not null && Keymap.ChordFor(vk, ctrl, alt, shift) == _leader) { BeginLeader(); return true; }

        // Shift+PageUp/PageDown (and Home/End) scroll this pane's scrollback; never reach the PTY.
        if (shift && !ctrl && !alt && _active is not null)
        {
            int page = Math.Max(1, _active.ActivePane.S.Rows - 1);
            switch (vk)
            {
                case VK_PRIOR: return ScrollActivePane(page);        // Shift+PageUp — older
                case VK_NEXT: return ScrollActivePane(-page);        // Shift+PageDown — newer
                case VK_HOME: return ScrollActivePane(int.MaxValue); // oldest
                case VK_END: return ScrollActivePane(int.MinValue);  // live bottom
            }
        }

        // Fixed font-zoom shortcuts (Ctrl +/-/0, incl. numpad). Handled here, not via keymap.conf,
        // because '+' clashes with the chord joiner (matching agterm's constraint).
        if (ctrl && !alt)
        {
            switch (vk)
            {
                case 0xBB: case 0x6B: ChangeFontSize(1); return true;   // Ctrl+= / Ctrl+numpad-plus
                case 0xBD: case 0x6D: ChangeFontSize(-1); return true;  // Ctrl+- / Ctrl+numpad-minus
                case 0x30: case 0x60: ChangeFontSize(0); return true;   // Ctrl+0 / Ctrl+numpad-0
                case 0xC0: QuickOp("toggle"); return true;              // Ctrl+` — quick terminal (VK_OEM_3, can't be a chord)
            }
        }

        // Clipboard: Ctrl+Shift+C / Ctrl+C-with-selection copies; Ctrl+V / Ctrl+Shift+V pastes.
        // Uses ActiveSurface() (not _active.ActivePane) so selections in a cover — quick terminal,
        // scratch, overlay — copy too instead of falling through and interrupting the shell.
        if (ctrl && !alt && ActiveSurface() is { } ap)
        {
            if (vk == 0x43) // C
            {
                if (ap.HasSel) { CopySelection(ap); return true; }
                if (shift) return true;   // Ctrl+Shift+C, no selection: consume (don't send ^C)
                // plain Ctrl+C with no selection falls through to the interrupt below
            }
            else if (vk == 0x56) { PasteInto(ap); return true; } // Ctrl+V / Ctrl+Shift+V
        }

        // Ctrl+Tab / Ctrl+Shift+Tab drive the MRU session walk (needs WM_KEYUP to commit, so it lives
        // here rather than the keymap dispatch). Honoured only while the chord is still bound to the
        // session-cycle action (default) — a user rebind of the chord falls through to keymap dispatch.
        if (ctrl && !alt && vk == VK_TAB)
        {
            string mruChord = shift ? "ctrl+shift+tab" : "ctrl+tab";
            if (!_keymap.TryGetValue(mruChord, out var mruAct) || mruAct is "next_session" or "previous_session")
            { MruWalk(shift ? -1 : 1); return true; }
        }

        // Keymap dispatch: build the chord and run its bound action (defaults overlaid by keymap.conf).
        // Unbound chords fall through to terminal input below.
        string? chord = Keymap.ChordFor(vk, ctrl, alt, shift);
        if (chord is not null && _keymap.TryGetValue(chord, out var action))
        {
            // close_cover only applies while a cover is up — otherwise its chord (typically a bare
            // Escape) falls through so the key still reaches the terminal.
            if (action is not "close_cover" || _cover is not null) { RunAction(action); return true; }
        }

        if (_session is null) return false;

        // Kitty keyboard protocol: when an app has enabled it, encode keys in CSI-u form.
        if ((ActiveSurface()?.S.Emulator.KeyboardFlags ?? 0) != 0 && KittyKeyEncode(vk, ctrl, alt, shift) is { } ku)
        { Send(ku); _kittyAteChar = true; return true; }

        if (ctrl && !alt)
        {
            if (vk >= VK_A && vk <= VK_Z) { Send(((char)(vk - VK_A + 1)).ToString()); return true; }
            if (vk == VK_SPACE) { Send("\0"); return true; }
        }

        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string m = mod > 1 ? $"1;{mod}" : "";

        string? seq = vk switch
        {
            VK_RETURN => "\r",
            VK_BACK => "\x7f",
            VK_TAB => shift ? "\x1b[Z" : "\t",
            VK_ESCAPE => "\x1b",
            VK_UP => $"\x1b[{m}A",
            VK_DOWN => $"\x1b[{m}B",
            VK_RIGHT => $"\x1b[{m}C",
            VK_LEFT => $"\x1b[{m}D",
            VK_HOME => $"\x1b[{m}H",
            VK_END => $"\x1b[{m}F",
            VK_PRIOR => mod > 1 ? $"\x1b[5;{mod}~" : "\x1b[5~",
            VK_NEXT => mod > 1 ? $"\x1b[6;{mod}~" : "\x1b[6~",
            VK_INSERT => "\x1b[2~",
            VK_DELETE => "\x1b[3~",
            0x70 => "\x1bOP", 0x71 => "\x1bOQ", 0x72 => "\x1bOR", 0x73 => "\x1bOS", // F1-F4
            0x74 => "\x1b[15~", 0x75 => "\x1b[17~", 0x76 => "\x1b[18~", 0x77 => "\x1b[19~", // F5-F8
            0x78 => "\x1b[20~", 0x79 => "\x1b[21~", 0x7A => "\x1b[23~", 0x7B => "\x1b[24~", // F9-F12
            _ => null,
        };
        if (seq is not null) { Send(seq); return true; }
        return false;
    }

    // Kitty keyboard: OnKeyDown encoded this key, so its following WM_CHAR must be dropped (else
    // e.g. Ctrl+A would send both the CSI-u sequence and a literal 0x01).
    private bool _kittyAteChar;

    /// <summary>Encode a key press in the Kitty keyboard protocol's CSI-u form, or null to fall back
    /// to legacy/WM_CHAR. Conservative: functional keys + Enter/Tab/Backspace/Esc + Ctrl/Alt-modified
    /// text keys are escape-encoded; plain typing (no Ctrl/Alt) still flows through WM_CHAR.</summary>
    private static string? KittyKeyEncode(int vk, bool ctrl, bool alt, bool shift)
    {
        int mods = (shift ? 1 : 0) | (alt ? 2 : 0) | (ctrl ? 4 : 0);
        string m = mods > 0 ? $";{mods + 1}" : "";
        // Arrows / Home / End — letter-final CSI (legacy form, with modifier when present).
        string? fin = vk switch { VK_UP => "A", VK_DOWN => "B", VK_RIGHT => "C", VK_LEFT => "D", VK_HOME => "H", VK_END => "F", _ => null };
        if (fin is not null) return mods > 0 ? $"\x1b[1{m}{fin}" : $"\x1b[{fin}";
        // PgUp/Dn, Insert, Delete, F5-F12 — number-final CSI ~.
        int num = vk switch { VK_PRIOR => 5, VK_NEXT => 6, VK_INSERT => 2, VK_DELETE => 3,
            0x74 => 15, 0x75 => 17, 0x76 => 18, 0x77 => 19, 0x78 => 20, 0x79 => 21, 0x7A => 23, 0x7B => 24, _ => -1 };
        if (num >= 0) return $"\x1b[{num}{m}~";
        // F1-F4.
        string? pf = vk switch { 0x70 => "P", 0x71 => "Q", 0x72 => "R", 0x73 => "S", _ => null };
        if (pf is not null) return mods > 0 ? $"\x1b[1{m}{pf}" : $"\x1bO{pf}";
        // Text/special keys → CSI codepoint u (Kitty uses the unshifted/base codepoint).
        int cp = vk switch
        {
            VK_RETURN => 13, VK_TAB => 9, VK_BACK => 127, VK_ESCAPE => 27, VK_SPACE => 32,
            >= VK_A and <= VK_Z => 97 + (vk - VK_A),   // lowercase base letter
            >= 0x30 and <= 0x39 => vk,                  // digits
            _ => -1,
        };
        if (cp < 0) return null;   // unmapped (e.g. OEM punctuation) → legacy / WM_CHAR
        bool special = vk is VK_RETURN or VK_TAB or VK_BACK or VK_ESCAPE;
        if (!special && !ctrl && !alt) return null;   // plain typing → WM_CHAR unchanged
        return mods > 0 ? $"\x1b[{cp};{mods + 1}u" : $"\x1b[{cp}u";
    }

    private static Color4 C4(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, 1f);

    /// <summary>Effective foreground for a cell, resolved through the active theme: inverse swap,
    /// then faint (SGR 2) dimming. Resolving via the theme (not the baked RGB) is what makes a
    /// theme switch recolour already-printed content.</summary>
    private static Color EffectiveFg(Cell cell)
    {
        Color fg = cell.Attributes.HasFlag(CellAttributes.Inverse) ? _theme.ResolveBg(cell.BgSpec) : _theme.ResolveFg(cell.FgSpec);
        if (cell.Attributes.HasFlag(CellAttributes.Dim))
            fg = new Color((byte)(fg.R * 0.6f), (byte)(fg.G * 0.6f), (byte)(fg.B * 0.6f));
        return fg;
    }

    /// <summary>Effective background for a cell, resolved through the active theme (inverse swaps with fg).</summary>
    private static Color EffectiveBg(Cell cell)
        => cell.Attributes.HasFlag(CellAttributes.Inverse) ? _theme.ResolveFg(cell.FgSpec) : _theme.ResolveBg(cell.BgSpec);
}
