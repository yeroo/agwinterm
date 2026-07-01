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
                case "cursor-style": cfg.CursorStyle = ParseCursorStyle(val, cfg.CursorStyle); break;
                case "cursor-blink": cfg.CursorBlink = ParseBool(val, cfg.CursorBlink); break;
                case "cursor-blink-ms": if (int.TryParse(val, out var ms) && ms > 0) cfg.CursorBlinkMs = ms; break;
                case "theme": if (val.Length > 0) cfg.Theme = val; break;
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
