namespace Agwinterm.Core;

/// <summary>
/// A single grid cell. <see cref="Width"/> is 1 for normal cells, 2 for the leading
/// cell of a double-width (CJK) glyph, and 0 for the trailing spacer that follows it.
/// </summary>
public readonly record struct Cell(char Rune, Color Foreground, Color Background, CellAttributes Attributes, byte Width = 1)
{
    public static Cell Empty => new(' ', Color.DefaultForeground, Color.DefaultBackground, CellAttributes.None);
}
