namespace Agwinterm.Core;

public enum CursorStyle
{
    Bar,
    Block,
    Underline,
}

/// <summary>
/// User-editable appearance/behavior settings, parsed from an agterm-style
/// <c>key = value</c> config file (<c>#</c> comments). Unknown keys are ignored;
/// missing keys keep their defaults. Grows as more of the port becomes customizable.
/// </summary>
public sealed class TerminalConfig
{
    public string FontFamily { get; set; } = "MesloLGSDZ Nerd Font";
    public double FontSize { get; set; } = 16;
    public CursorStyle CursorStyle { get; set; } = CursorStyle.Bar;
    public bool CursorBlink { get; set; } = true;
    public int CursorBlinkMs { get; set; } = 530;
    public string Theme { get; set; } = "default";

    /// <summary>Follow the Windows light/dark app setting, swapping between <see cref="ThemeLight"/> and
    /// <see cref="ThemeDark"/> as it changes. Off = the single <see cref="Theme"/> is used.</summary>
    public bool ThemeFollowSystem { get; set; }
    /// <summary>Theme applied when Windows is in dark mode (used only when <see cref="ThemeFollowSystem"/>).</summary>
    public string ThemeDark { get; set; } = "";
    /// <summary>Theme applied when Windows is in light mode (used only when <see cref="ThemeFollowSystem"/>).</summary>
    public string ThemeLight { get; set; } = "";

    /// <summary>Rows of scrollback history kept per pane (0 disables). Scroll up with the wheel / Shift+PgUp.</summary>
    public int Scrollback { get; set; } = 5000;

    /// <summary>Inject a pwsh prompt hook that emits OSC 7 so the live working directory is tracked.
    /// Off by default: the hook can override a customized prompt (e.g. oh-my-posh). Opt in for live cwd.</summary>
    public bool ShellIntegration { get; set; } = false;

    /// <summary>Re-run each pane's foreground command on restart (agterm's opt-in restore-on-restart).
    /// Off by default; honors restore-denylist.conf. Best-effort (Windows foreground-command capture).</summary>
    public bool RestoreCommands { get; set; } = false;

    /// <summary>Persist each pane's scrollback text and re-display it (dimmed) above the fresh shell on
    /// relaunch. WT's buffer-content restore. Off by default.</summary>
    public bool RestoreBuffer { get; set; } = false;

    /// <summary>Paste the clipboard on a right-click in the terminal (when the app isn't grabbing the mouse).</summary>
    public bool RightClickPaste { get; set; } = true;

    /// <summary>Auto-copy the selection to the clipboard the moment a selection is finished (mouse-up /
    /// word / line select), without needing Ctrl+C. Off by default. The selection stays highlighted.</summary>
    public bool CopyOnSelect { get; set; } = false;

    /// <summary>When text is selected, Ctrl+C copies it (with a brief "Text copied" flash) instead of
    /// sending an interrupt to the shell. With no selection, Ctrl+C still interrupts. Default on.</summary>
    public bool CopyOnCtrlC { get; set; } = true;

    /// <summary>Warn before an interactive paste whose text contains newlines (a shell runs each
    /// completed line immediately) or control characters (escape injection). Bracketed-paste-aware:
    /// multi-line into a bracketed-paste app doesn't warn. Scripted pastes via the control API never
    /// prompt. Default on (Windows Terminal / Ghostty behavior).</summary>
    public bool PasteProtection { get; set; } = true;

    /// <summary>Allow programs to write the system clipboard via OSC 52 (e.g. Claude Code's
    /// auto-copy-on-select). Set false to deny; denials are visible in AGWINTERM_VT_LOG.
    /// Reads are always refused regardless. Default on.</summary>
    public bool ClipboardWrite { get; set; } = true;

    /// <summary>Flash the taskbar button when a notification arrives while the window is in the
    /// background (agterm's Dock bounce, #215): "none" (default) | "once" | "until-focused".</summary>
    public string NotificationFlash { get; set; } = "none";

    /// <summary>Periodically check the npm registry for a newer Claude Code release and surface a
    /// hint (toast + palette) when one ships. Awareness only — the update itself always stays a
    /// manual, visible <c>claude update</c> (palette → "Update Claude Code").</summary>
    public bool ClaudeUpdateCheck { get; set; } = true;

    /// <summary>Periodically check GitHub for a newer agwinterm release and surface a hint (toast +
    /// palette) when one ships. Awareness only — applying it stays a manual palette action
    /// ("Update agwinterm"), which downloads, verifies, restarts, and restores sessions.</summary>
    public bool UpdateCheck { get; set; } = true;

    /// <summary>Where terminal sessions live: "in-process" (default — the UI process owns the
    /// ConPTYs) or "server" (EXPERIMENTAL, #105 — a separate pty-host process owns them; the
    /// long-term basis for sessions surviving UI updates/crashes).</summary>
    public string SessionHost { get; set; } = "in-process";

    /// <summary>Characters that DELIMIT words for double-click selection (in addition to whitespace).
    /// Empty = whitespace only (double-click grabs a whole path/URL). WT's wordDelimiters analog.</summary>
    public string WordDelimiters { get; set; } = "";

    /// <summary>Show OS desktop notifications (tray balloon) for OSC 9/777 / notify. Off → in-app banner + badge only.</summary>
    public bool DesktopNotifications { get; set; } = true;

    /// <summary>Sound played whenever a session enters the "blocked" agent status (empty / "off" / "none" = silent).
    /// May be a system-sound name (beep, asterisk, exclamation, hand, question), a Windows sound-event alias
    /// (e.g. "SystemNotification"), or a full path to a .wav file.</summary>
    public string BlockedSound { get; set; } = "";

    /// <summary>How much to dim non-active panes in a split (0 = no dimming, 100 = fully dark). Default 35.</summary>
    public int InactivePaneDim { get; set; } = 35;

    /// <summary>Dim the whole content region when the window isn't focused (0 = off … 90). WT's
    /// unfocusedAppearance analog. Off by default.</summary>
    public int UnfocusedDim { get; set; } = 0;

    /// <summary>Draw box-drawing / block-element glyphs as vectors (pixel-perfect, seamless borders
    /// and solid blocks at any size) instead of using the font's glyphs. WT's font.builtinGlyphs. On.</summary>
    public bool BuiltinGlyphs { get; set; } = true;

    /// <summary>Render font ligatures (e.g. -&gt; ⇒ ≠ with Cascadia Code / Fira Code). On by default;
    /// set false to disable the calt/liga/clig/dlig features so each character draws standalone.</summary>
    public bool Ligatures { get; set; } = true;

    /// <summary>Whole-window opacity, 30–100 (%). 100 = opaque. Clamped so the window never disappears.</summary>
    public int WindowOpacity { get; set; } = 100;

    /// <summary>Shifts the themed sidebar shade relative to the theme base: -100 (darker) … +100 (lighter). Default 0.</summary>
    public int SidebarTint { get; set; } = 0;

    /// <summary>Font size (pt) of the workspace/session names in the sidebar. Default 13.</summary>
    public int SidebarFontSize { get; set; } = 13;

    /// <summary>Default working directory for newly created sessions (empty = inherit current behavior).</summary>
    public string NewSessionDir { get; set; } = "";

    /// <summary>Scrollback wheel speed: lines scrolled per wheel notch, 1–10. Default 3.</summary>
    public int ScrollSpeed { get; set; } = 3;

    /// <summary>oh-my-posh theme (a theme name or a full .omp.json path) applied to new pwsh sessions,
    /// overriding the profile's theme. Empty = use whatever the profile sets. Set via the in-app picker.</summary>
    public string OmpTheme { get; set; } = "";

    /// <summary>oh-my-posh integration: show the theme picker and inject the persisted omp-theme into new
    /// sessions. Turn OFF if you use a different prompt (e.g. starship) — agwinterm then leaves the shell
    /// prompt alone and hides the "oh-my-posh Theme…" selector. Legacy alias: false maps to
    /// prompt-engine = vanilla when no explicit prompt-engine is set.</summary>
    public bool OmpIntegration { get; set; } = true;

    /// <summary>Prompt engine injected into new pwsh sessions and offered a theme picker:
    /// omp (oh-my-posh) | starship | vanilla (reset to the plain "PS C:\&gt;" prompt, overriding
    /// profile customizations) | profile (no injection at all — $PROFILE rules). Pick live via
    /// the action palette's "Prompt Engine…".</summary>
    public string PromptEngine { get; set; } = "omp";

    /// <summary>starship preset (see `starship preset --list`) applied to new pwsh sessions when
    /// prompt-engine = starship. Empty = starship's default config. Set via the in-app picker.</summary>
    public string StarshipTheme { get; set; } = "";

    /// <summary>Where new sessions open: home (user profile) | current (active session's cwd) | custom (NewSessionDir).</summary>
    public string NewSessionDirMode { get; set; } = "home";

    /// <summary>Ask for confirmation before a user closes a session (Ctrl+Shift+W / menu Close). Off by default.</summary>
    public bool ConfirmCloseSession { get; set; } = false;

    /// <summary>Compact toolbar: a shorter title bar. Off by default. Legacy — superseded by
    /// <see cref="ToolbarMode"/>; kept as a decode shim so old settings still open.</summary>
    public bool CompactToolbar { get; set; } = false;

    /// <summary>Title-bar chrome: "normal" (40px), "compact" (30px), or "hidden" (0 — full-bleed terminal
    /// with a thin top drag strip, no title/buttons). Null = derive from <see cref="CompactToolbar"/>.
    /// Stored raw so an unknown future value degrades to the default rather than failing the whole parse.</summary>
    public string? ToolbarMode { get; set; }

    /// <summary>Draw the red unread-count pill on sidebar rows (the count still tracks when off). On by default.</summary>
    public bool NotificationBadges { get; set; } = true;

    /// <summary>Show the title-bar attention bell (hidden entirely when off). On by default.</summary>
    public bool AttentionButton { get; set; } = true;

    /// <summary>Sidebar/bell status-glyph colors (#RRGGBB). Defaults match the built-in dot colors.</summary>
    public string StatusColorActive { get; set; } = "#3C8CFF";
    public string StatusColorBlocked { get; set; } = "#F0A028";
    public string StatusColorCompleted { get; set; } = "#3CC85A";

    /// <summary>Default config file contents (also used to seed the file on first run).</summary>
    public const string DefaultText =
        """
        # agwinterm configuration (key = value, '#' starts a comment)

        # Font
        font-family = MesloLGSDZ Nerd Font
        font-size   = 16

        # Cursor: style = bar | block | underline
        cursor-style    = bar
        cursor-blink    = true
        cursor-blink-ms = 530

        # Color theme (pick live from the action palette; Ctrl+Shift+P -> Select Theme)
        theme = default

        # Follow the Windows light/dark app setting, swapping between theme-dark and theme-light
        # as it changes (Settings -> Appearance). false = use the single `theme` above.
        theme-follow-system = false
        theme-dark =
        theme-light =

        # Scrollback: rows of history to keep per pane (0 disables). Scroll with the wheel or Shift+PgUp/PgDn.
        scrollback-lines = 5000

        # Paste the clipboard on right-click in the terminal (when the app isn't using the mouse).
        right-click-paste = true

        # Auto-copy the selection to the clipboard as soon as it's made (no Ctrl+C needed).
        copy-on-select = false

        # When text is selected, Ctrl+C copies it (with a brief "Text copied" flash) instead of sending
        # an interrupt. With nothing selected, Ctrl+C still interrupts the shell.
        copy-on-ctrl-c = true

        # Warn before pasting text with newlines (a shell runs each line immediately) or control
        # characters. Pastes into bracketed-paste apps and scripted control-API pastes never prompt.
        paste-protection = true

        # Allow programs to write the system clipboard via OSC 52 (e.g. Claude Code's auto-copy).
        # Reads are always refused regardless of this setting.
        clipboard-write = true

        # Extra word-delimiter characters for double-click selection (besides whitespace).
        # Empty = whitespace only, so double-click grabs a whole path or URL. Example: /\.:;,="'()[]{}
        word-delimiters =

        # Desktop notifications: show an OS tray-balloon for OSC 9/777 / `agwintermctl notify`
        # (the in-app banner + sidebar badge always show regardless). Set false to suppress the OS toast.
        desktop-notifications = true

        # Flash the taskbar button when a notification arrives while the window is in the background:
        # none | once | until-focused
        notification-flash = none

        # Check the npm registry in the background for a newer Claude Code release and show a hint
        # when one ships. Updating is always manual: palette -> "Update Claude Code" (which runs
        # `claude update` in an overlay terminal, then restarts your Claude sessions).
        claude-update-check = true

        # Check GitHub in the background for a newer agwinterm release and show a hint when one
        # ships. Applying it is always manual: palette -> "Update agwinterm" (downloads the release,
        # verifies its SHA-256, restarts, and your sessions restore). Installs managed by a package
        # manager (scoop/chocolatey) are never self-updated - the hint points at the manager instead.
        update-check = true

        # Where terminal sessions live. "in-process" (default): the window process owns them.
        # "server" (EXPERIMENTAL - see github issue #105): a separate pty-host process owns them,
        # so shells KEEP RUNNING when the UI quits, updates, or crashes - the next start reconnects
        # every pane to its live session (closing a pane still closes its shell). Changing this
        # applies to new sessions immediately; restart agwinterm to migrate existing ones.
        session-host = in-process

        # Blocked sound: play a sound whenever a session enters the "blocked" agent status (needs
        # your attention). Empty / off / none = silent. Accepts a system-sound name (beep, asterisk,
        # exclamation, hand, question), a Windows sound-event alias, or a path to a .wav file.
        blocked-sound =

        # Shell integration: inject a pwsh prompt hook (OSC 7) to track the live working
        # directory so it persists/restores accurately. Off by default because the hook can
        # override a customized prompt (e.g. oh-my-posh). Set true to opt in to live-cwd tracking.
        shell-integration = false

        # Restore running commands: on restart, re-run each pane's foreground command (agterm's
        # opt-in restore-on-restart). Off by default. Edit restore-denylist.conf to exclude commands.
        restore-commands = false

        # Restore each pane's scrollback text (dimmed) above the fresh shell on relaunch.
        restore-buffer = false

        # Dim non-active panes in a split (0 = off, 100 = fully dark).
        inactive-pane-dim = 35

        # Dim the terminal content when this window isn't focused (0 = off .. 90).
        unfocused-dim = 0

        # Draw box-drawing / block-element glyphs as vectors (crisp, seamless borders at any size).
        builtin-glyphs = true

        # Render font ligatures (-> ⇒ ≠ …) with a ligature font. Set false to disable them.
        ligatures = true

        # Whole-window opacity, 30-100 (%). 100 = opaque.
        window-opacity = 100

        # Sidebar tint relative to the theme: -100 (darker) .. +100 (lighter).
        sidebar-tint = 0

        # Sidebar font size (pt) for workspace/session names (9..20).
        sidebar-font-size = 13

        # Default directory for new sessions (empty = current behavior).
        new-session-dir =

        # Scrollback wheel speed: lines per wheel notch (1-10).
        scroll-speed = 3

        # oh-my-posh theme for new pwsh sessions (a theme name or a full .omp.json path); empty = use
        # the profile's theme. Pick live from the action palette -> "oh-my-posh Theme..." (--persist saves here).
        omp-theme =

        # oh-my-posh integration: show the theme picker + inject omp-theme into new sessions. Set false if
        # you use a different prompt (e.g. starship) — agwinterm then leaves the prompt alone and hides the picker.
        omp-integration = true

        # Prompt engine for new pwsh sessions: omp (oh-my-posh) | starship | vanilla (plain
        # "PS C:\>" prompt, overriding profile customizations) | profile (no injection - $PROFILE
        # rules). Pick live from the action palette -> "Prompt Engine...". Overrides omp-integration.
        prompt-engine = omp

        # starship preset for new pwsh sessions when prompt-engine = starship (`starship preset --list`);
        # empty = starship's default config. Pick live from the action palette -> "Starship Theme...".
        starship-theme =

        # Where new sessions open: home (user profile) | current (active session's dir) | custom (new-session-dir).
        new-session-dir-mode = home

        # Ask before closing a session (Ctrl+Shift+W / right-click Close).
        confirm-close-session = false

        # Title-bar chrome: normal | compact | hidden (hidden = full-bleed terminal, thin top drag strip).
        toolbar-mode = normal

        # Notifications: show the red unread-count pill on sidebar rows; show the title-bar attention bell.
        notification-badges = true
        attention-button = true

        # Agent-status glyph colors (#RRGGBB) for the sidebar dot + title-bar bell.
        status-color-active = #3C8CFF
        status-color-blocked = #F0A028
        status-color-completed = #3CC85A
        """;

    public static TerminalConfig Parse(string text)
    {
        var cfg = new TerminalConfig();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim().ToLowerInvariant();
            string val = line[(eq + 1)..].Trim();

            switch (key)
            {
                case "font-family": if (val.Length > 0) cfg.FontFamily = val; break;
                case "font-size": if (double.TryParse(val, out var fs) && fs > 0) cfg.FontSize = fs; break;
                case "scrollback-lines": if (int.TryParse(val, out var sb) && sb >= 0) cfg.Scrollback = sb; break;
                case "cursor-style": cfg.CursorStyle = ParseCursorStyle(val, cfg.CursorStyle); break;
                case "cursor-blink": cfg.CursorBlink = ParseBool(val, cfg.CursorBlink); break;
                case "cursor-blink-ms": if (int.TryParse(val, out var ms) && ms > 0) cfg.CursorBlinkMs = ms; break;
                case "theme": if (val.Length > 0) cfg.Theme = val; break;
                case "theme-follow-system": cfg.ThemeFollowSystem = ParseBool(val, cfg.ThemeFollowSystem); break;
                case "theme-dark": cfg.ThemeDark = val; break;
                case "theme-light": cfg.ThemeLight = val; break;
                case "shell-integration": cfg.ShellIntegration = ParseBool(val, cfg.ShellIntegration); break;
                case "restore-commands": cfg.RestoreCommands = ParseBool(val, cfg.RestoreCommands); break;
                case "restore-buffer": cfg.RestoreBuffer = ParseBool(val, cfg.RestoreBuffer); break;
                case "right-click-paste": cfg.RightClickPaste = ParseBool(val, cfg.RightClickPaste); break;
                case "copy-on-select": cfg.CopyOnSelect = ParseBool(val, cfg.CopyOnSelect); break;
                case "copy-on-ctrl-c": cfg.CopyOnCtrlC = ParseBool(val, cfg.CopyOnCtrlC); break;
                case "paste-protection": cfg.PasteProtection = ParseBool(val, cfg.PasteProtection); break;
                case "clipboard-write": cfg.ClipboardWrite = ParseBool(val, cfg.ClipboardWrite); break;
                case "notification-flash":
                    if (val is "none" or "once" or "until-focused") cfg.NotificationFlash = val;
                    break;
                case "claude-update-check": cfg.ClaudeUpdateCheck = ParseBool(val, cfg.ClaudeUpdateCheck); break;
                case "update-check": cfg.UpdateCheck = ParseBool(val, cfg.UpdateCheck); break;
                case "session-host":
                    if (val is "in-process" or "server") cfg.SessionHost = val;
                    break;
                case "word-delimiters": cfg.WordDelimiters = val; break;
                case "desktop-notifications": cfg.DesktopNotifications = ParseBool(val, cfg.DesktopNotifications); break;
                case "blocked-sound": cfg.BlockedSound = val; break;
                case "inactive-pane-dim": if (int.TryParse(val, out var ipd)) cfg.InactivePaneDim = System.Math.Clamp(ipd, 0, 100); break;
                case "unfocused-dim": if (int.TryParse(val, out var ufd)) cfg.UnfocusedDim = System.Math.Clamp(ufd, 0, 90); break;
                case "builtin-glyphs": cfg.BuiltinGlyphs = ParseBool(val, cfg.BuiltinGlyphs); break;
                case "ligatures": cfg.Ligatures = ParseBool(val, cfg.Ligatures); break;
                case "window-opacity": if (int.TryParse(val, out var wo)) cfg.WindowOpacity = System.Math.Clamp(wo, 30, 100); break;
                case "sidebar-tint": if (int.TryParse(val, out var st)) cfg.SidebarTint = System.Math.Clamp(st, -100, 100); break;
                case "sidebar-font-size": if (int.TryParse(val, out var sfs)) cfg.SidebarFontSize = System.Math.Clamp(sfs, 9, 20); break;
                case "new-session-dir": cfg.NewSessionDir = val; break;
                case "scroll-speed": if (int.TryParse(val, out var ss)) cfg.ScrollSpeed = System.Math.Clamp(ss, 1, 10); break;
                case "omp-theme": cfg.OmpTheme = val; break;
                case "omp-integration":   // legacy alias: false = profile/no-injection (an explicit prompt-engine line later wins)
                    cfg.OmpIntegration = ParseBool(val, cfg.OmpIntegration);
                    if (!cfg.OmpIntegration) cfg.PromptEngine = "profile";
                    break;
                case "prompt-engine":
                    if (val.ToLowerInvariant() is "omp" or "oh-my-posh" or "starship" or "vanilla" or "profile" or "none")
                        cfg.PromptEngine = val.ToLowerInvariant() switch { "oh-my-posh" => "omp", "none" => "profile", var v => v };
                    break;
                case "starship-theme": cfg.StarshipTheme = val; break;
                case "new-session-dir-mode": { var m = val.ToLowerInvariant(); if (m is "home" or "current" or "custom") cfg.NewSessionDirMode = m; break; }
                case "confirm-close-session": cfg.ConfirmCloseSession = ParseBool(val, cfg.ConfirmCloseSession); break;
                case "compact-toolbar": cfg.CompactToolbar = ParseBool(val, cfg.CompactToolbar); break;
                case "toolbar-mode": { var m = val.Trim().ToLowerInvariant(); if (m is "normal" or "compact" or "hidden") cfg.ToolbarMode = m; break; }
                case "notification-badges": cfg.NotificationBadges = ParseBool(val, cfg.NotificationBadges); break;
                case "attention-button": cfg.AttentionButton = ParseBool(val, cfg.AttentionButton); break;
                case "status-color-active": if (val.Length > 0) cfg.StatusColorActive = val; break;
                case "status-color-blocked": if (val.Length > 0) cfg.StatusColorBlocked = val; break;
                case "status-color-completed": if (val.Length > 0) cfg.StatusColorCompleted = val; break;
            }
        }
        return cfg;
    }

    public static TerminalConfig Load(string path)
        => System.IO.File.Exists(path) ? Parse(System.IO.File.ReadAllText(path)) : new TerminalConfig();

    private static CursorStyle ParseCursorStyle(string v, CursorStyle fallback) => v.ToLowerInvariant() switch
    {
        "bar" or "beam" or "line" => CursorStyle.Bar,
        "block" or "box" => CursorStyle.Block,
        "underline" or "underscore" => CursorStyle.Underline,
        _ => fallback,
    };

    private static bool ParseBool(string v, bool fallback) => v.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => fallback,
    };
}
