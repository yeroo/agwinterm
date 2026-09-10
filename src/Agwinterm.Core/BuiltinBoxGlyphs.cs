namespace Agwinterm.Core;

/// <summary>The vector renderer's implemented set; other box glyphs use normal styled font rendering.</summary>
public static class BuiltinBoxGlyphs
{
    public static bool Supports(int codepoint) => codepoint is >= 0x2500 and <= 0x2503
        or >= 0x250C and <= 0x254B or 0x2550 or 0x2551 or >= 0x2580 and <= 0x2593;
}
