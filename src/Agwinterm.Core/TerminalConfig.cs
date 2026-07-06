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

    /// <summary>Rows of scrollback history kept per pane (0 disables). Scroll up with the wheel / Shift+PgUp.</summary>
    public int Scrollback { get; set; } = 5000;

    /// <summary>Inject a pwsh prompt hook that emits OSC 7 so the live working directory is tracked.
    /// Off by default: the hook can override a customized prompt (e.g. oh-my-posh). Opt in for live cwd.</summary>
    public bool ShellIntegration { get; set; } = false;

    /// <summary>Re-run each pane's foreground command on restart (agterm's opt-in restore-on-restart).
    /// Off by default; honors restore-denylist.conf. Best-effort (Windows foreground-command capture).</summary>
    public bool RestoreCommands { get; set; } = false;

    /// <summary>Paste the clipboard on a right-click in the terminal (when the app isn't grabbing the mouse).</summary>
    public bool RightClickPaste { get; set; } = true;

    /// <summary>Auto-copy the selection to the clipboard the moment a selection is finished (mouse-up /
    /// word / line select), without needing Ctrl+C. On by default. The selection stays highlighted.</summary>
    public bool CopyOnSelect { get; set; } = true;

    /// <summary>Show OS desktop notifications (tray balloon) for OSC 9/777 / notify. Off → in-app banner + badge only.</summary>
    public bool DesktopNotifications { get; set; } = true;

    /// <summary>Sound played whenever a session enters the "blocked" agent status (empty / "off" / "none" = silent).
    /// May be a system-sound name (beep, asterisk, exclamation, hand, question), a Windows sound-event alias
    /// (e.g. "SystemNotification"), or a full path to a .wav file.</summary>
    public string BlockedSound { get; set; } = "";

    /// <summary>How much to dim non-active panes in a split (0 = no dimming, 100 = fully dark). Default 35.</summary>
    public int InactivePaneDim { get; set; } = 35;

    /// <summary>Whole-window opacity, 30–100 (%). 100 = opaque. Clamped so the window never disappears.</summary>
    public int WindowOpacity { get; set; } = 100;

    /// <summary>Shifts the themed sidebar shade relative to the theme base: -100 (darker) … +100 (lighter). Default 0.</summary>
    public int SidebarTint { get; set; } = 0;

    /// <summary>Default working directory for newly created sessions (empty = inherit current behavior).</summary>
    public string NewSessionDir { get; set; } = "";

    /// <summary>Scrollback wheel speed: lines scrolled per wheel notch, 1–10. Default 3.</summary>
    public int ScrollSpeed { get; set; } = 3;

    /// <summary>oh-my-posh theme (a theme name or a full .omp.json path) applied to new pwsh sessions,
    /// overriding the profile's theme. Empty = use whatever the profile sets. Set via the in-app picker.</summary>
    public string OmpTheme { get; set; } = "";

    /// <summary>oh-my-posh integration: show the theme picker and inject the persisted omp-theme into new
    /// sessions. Turn OFF if you use a different prompt (e.g. starship) — agwinterm then leaves the shell
    /// prompt alone and hides the "oh-my-posh Theme…" selector.</summary>
    public bool OmpIntegration { get; set; } = true;

    /// <summary>Where new sessions open: home (user profile) | current (active session's cwd) | custom (NewSessionDir).</summary>
    public string NewSessionDirMode { get; set; } = "home";

    /// <summary>Ask for confirmation before a user closes a session (Ctrl+Shift+W / menu Close). Off by default.</summary>
    public bool ConfirmCloseSession { get; set; } = false;

    /// <summary>Compact toolbar: a shorter title bar. Off by default.</summary>
    public bool CompactToolbar { get; set; } = false;

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

        # Scrollback: rows of history to keep per pane (0 disables). Scroll with the wheel or Shift+PgUp/PgDn.
        scrollback-lines = 5000

        # Paste the clipboard on right-click in the terminal (when the app isn't using the mouse).
        right-click-paste = true

        # Auto-copy the selection to the clipboard as soon as it's made (no Ctrl+C needed).
        copy-on-select = true

        # Desktop notifications: show an OS tray-balloon for OSC 9/777 / `agwintermctl notify`
        # (the in-app banner + sidebar badge always show regardless). Set false to suppress the OS toast.
        desktop-notifications = true

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

        # Dim non-active panes in a split (0 = off, 100 = fully dark).
        inactive-pane-dim = 35

        # Whole-window opacity, 30-100 (%). 100 = opaque.
        window-opacity = 100

        # Sidebar tint relative to the theme: -100 (darker) .. +100 (lighter).
        sidebar-tint = 0

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

        # Where new sessions open: home (user profile) | current (active session's dir) | custom (new-session-dir).
        new-session-dir-mode = home

        # Ask before closing a session (Ctrl+Shift+W / right-click Close).
        confirm-close-session = false

        # Compact toolbar: a shorter title bar.
        compact-toolbar = false

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
                case "shell-integration": cfg.ShellIntegration = ParseBool(val, cfg.ShellIntegration); break;
                case "restore-commands": cfg.RestoreCommands = ParseBool(val, cfg.RestoreCommands); break;
                case "right-click-paste": cfg.RightClickPaste = ParseBool(val, cfg.RightClickPaste); break;
                case "copy-on-select": cfg.CopyOnSelect = ParseBool(val, cfg.CopyOnSelect); break;
                case "desktop-notifications": cfg.DesktopNotifications = ParseBool(val, cfg.DesktopNotifications); break;
                case "blocked-sound": cfg.BlockedSound = val; break;
                case "inactive-pane-dim": if (int.TryParse(val, out var ipd)) cfg.InactivePaneDim = System.Math.Clamp(ipd, 0, 100); break;
                case "window-opacity": if (int.TryParse(val, out var wo)) cfg.WindowOpacity = System.Math.Clamp(wo, 30, 100); break;
                case "sidebar-tint": if (int.TryParse(val, out var st)) cfg.SidebarTint = System.Math.Clamp(st, -100, 100); break;
                case "new-session-dir": cfg.NewSessionDir = val; break;
                case "scroll-speed": if (int.TryParse(val, out var ss)) cfg.ScrollSpeed = System.Math.Clamp(ss, 1, 10); break;
                case "omp-theme": cfg.OmpTheme = val; break;
                case "omp-integration": cfg.OmpIntegration = ParseBool(val, cfg.OmpIntegration); break;
                case "new-session-dir-mode": { var m = val.ToLowerInvariant(); if (m is "home" or "current" or "custom") cfg.NewSessionDirMode = m; break; }
                case "confirm-close-session": cfg.ConfirmCloseSession = ParseBool(val, cfg.ConfirmCloseSession); break;
                case "compact-toolbar": cfg.CompactToolbar = ParseBool(val, cfg.CompactToolbar); break;
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
