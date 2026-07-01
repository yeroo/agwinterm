namespace Agwinterm.Core;

/// <summary>How a cell's colour was specified, so the renderer can resolve it against the
/// active <see cref="Theme"/> at draw time (enabling live theme switching).</summary>
public enum ColorSpecKind : byte { Default, Indexed, Rgb }

/// <summary>
/// A cell colour as written by the app: a terminal default, a palette index (0–255),
/// or a direct truecolor value. Stored alongside the resolved RGB on each cell.
/// <c>default(ColorSpec)</c> is <see cref="Default"/>.
/// </summary>
public readonly record struct ColorSpec(ColorSpecKind Kind, byte Index, Color Rgb)
{
    public static readonly ColorSpec Default = new(ColorSpecKind.Default, 0, default);
    public static ColorSpec Indexed(int index) => new(ColorSpecKind.Indexed, (byte)index, default);
    public static ColorSpec FromRgb(Color rgb) => new(ColorSpecKind.Rgb, 0, rgb);
}

/// <summary>
/// A colour theme: the 16 base ANSI colours plus default foreground/background and the
/// cursor colour. Indexed colours 16–255 use the standard cube/grayscale formula
/// (<see cref="Color.FromIndex"/>) and are not themed. The renderer resolves each cell's
/// <see cref="ColorSpec"/> through the active theme, so switching themes recolours the
/// whole screen live.
/// </summary>
public sealed class Theme
{
    public string Name { get; init; } = "default";
    public Color[] Palette { get; init; } = DefaultPalette();          // 16 ANSI colours
    public Color DefaultForeground { get; init; } = Color.DefaultForeground;
    public Color DefaultBackground { get; init; } = Color.DefaultBackground;
    public Color Cursor { get; init; } = new(222, 222, 230);

    public Color ResolveFg(ColorSpec s) => s.Kind switch
    {
        ColorSpecKind.Default => DefaultForeground,
        ColorSpecKind.Indexed => s.Index < 16 ? Palette[s.Index] : Color.FromIndex(s.Index),
        _ => s.Rgb,
    };

    public Color ResolveBg(ColorSpec s) => s.Kind switch
    {
        ColorSpecKind.Default => DefaultBackground,
        ColorSpecKind.Indexed => s.Index < 16 ? Palette[s.Index] : Color.FromIndex(s.Index),
        _ => s.Rgb,
    };

    /// <summary>The 16 ANSI colours of the built-in default theme (identical to the legacy palette).</summary>
    public static Color[] DefaultPalette()
    {
        var p = new Color[16];
        for (int i = 0; i < 16; i++) p[i] = Color.FromIndex(i);
        return p;
    }

    /// <summary>Built-in default theme — same palette/defaults as before theming, so nothing changes by default.</summary>
    public static readonly Theme Default = new() { Name = "default" };
}
