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

    /// <summary>Show OS desktop notifications (tray balloon) for OSC 9/777 / notify. Off → in-app banner + badge only.</summary>
    public bool DesktopNotifications { get; set; } = true;

    /// <summary>Sound played whenever a session enters the "blocked" agent status (empty / "off" / "none" = silent).
    /// May be a system-sound name (beep, asterisk, exclamation, hand, question), a Windows sound-event alias
    /// (e.g. "SystemNotification"), or a full path to a .wav file.</summary>
    public string BlockedSound { get; set; } = "";

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
                case "desktop-notifications": cfg.DesktopNotifications = ParseBool(val, cfg.DesktopNotifications); break;
                case "blocked-sound": cfg.BlockedSound = val; break;
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
