namespace Agwinterm.Core;

/// <summary>
/// The pane theme's default colors in the pane's environment, <c>AGWINTERM_THEME_COLORS=rrggbb;rrggbb</c>
/// (foreground;background). A pty-host reads it from the Create request so it can answer OSC 10/11 while
/// no client is attached (#339): a slow-starting app may ask then, and would keep the answer.
/// </summary>
public static class ThemeColors
{
    public const string EnvVar = "AGWINTERM_THEME_COLORS";

    public static string Format(Color fg, Color bg) => $"{fg.R:x2}{fg.G:x2}{fg.B:x2};{bg.R:x2}{bg.G:x2}{bg.B:x2}";

    /// <summary>The colors in <paramref name="value"/>, or null when it is missing or malformed.</summary>
    public static (Color Foreground, Color Background)? Parse(string? value)
    {
        var parts = value?.Split(';');
        if (parts is not { Length: 2 } || !TryHex(parts[0], out var fg) || !TryHex(parts[1], out var bg)) return null;
        return (fg, bg);
    }

    private static bool TryHex(string s, out Color c)
    {
        c = default;
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return false;
        c = new Color((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}
