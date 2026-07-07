namespace Agwinterm.Core;

/// <summary>
/// A single grid cell. <see cref="Rune"/> is a full Unicode codepoint (astral glyphs — emoji,
/// nerd-font plane-15/16 icons — fit in one cell). <see cref="Width"/> is 1 for normal cells,
/// 2 for the leading cell of a double-width (CJK/emoji) glyph, and 0 for the trailing spacer.
/// </summary>
public readonly record struct Cell(
    int Rune, Color Foreground, Color Background, CellAttributes Attributes, byte Width = 1,
    ColorSpec FgSpec = default, ColorSpec BgSpec = default)
{
    public static Cell Empty => new(' ', Color.DefaultForeground, Color.DefaultBackground, CellAttributes.None);
}
