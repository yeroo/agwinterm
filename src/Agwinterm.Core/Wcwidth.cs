namespace Agwinterm.Core;

/// <summary>
/// Minimal East-Asian character-width lookup (BMP only, matching the engine's
/// char-based storage). Returns 2 for wide glyphs, 0 for zero-width combining
/// marks, and 1 otherwise. Compact range table derived from the classic
/// Markus Kuhn wcwidth wide/zero-width intervals.
/// </summary>
public static class Wcwidth
{
    // Inclusive [lo, hi] ranges of double-width code points (BMP).
    private static readonly (int Lo, int Hi)[] Wide =
    {
        (0x1100, 0x115F), // Hangul Jamo
        (0x2329, 0x232A), // angle brackets
        (0x2E80, 0x303E), // CJK radicals, Kangxi, CJK symbols/punctuation
        (0x3041, 0x33FF), // Hiragana, Katakana, CJK symbols, enclosed
        (0x3400, 0x4DBF), // CJK Extension A
        (0x4E00, 0x9FFF), // CJK Unified Ideographs
        (0xA000, 0xA4CF), // Yi
        (0xAC00, 0xD7A3), // Hangul Syllables
        (0xF900, 0xFAFF), // CJK Compatibility Ideographs
        (0xFE10, 0xFE19), // vertical forms
        (0xFE30, 0xFE6F), // CJK compatibility / small forms
        (0xFF00, 0xFF60), // Fullwidth forms
        (0xFFE0, 0xFFE6), // Fullwidth signs
    };

    // Inclusive [lo, hi] ranges of zero-width combining marks (BMP subset).
    private static readonly (int Lo, int Hi)[] ZeroWidth =
    {
        (0x0300, 0x036F), // combining diacritical marks
        (0x0483, 0x0489),
        (0x0591, 0x05BD),
        (0x0610, 0x061A),
        (0x064B, 0x065F),
        (0x0670, 0x0670),
        (0x06D6, 0x06DC),
        (0x200B, 0x200F), // zero-width space / joiners / marks
        (0xFE20, 0xFE2F), // combining half marks
    };

    public static int Of(int codepoint)
    {
        if (codepoint == 0) return 0;
        if (codepoint > 0xFFFF)   // astral: emoji/CJK extensions are wide; plane-15/16 PUA (nerd-font icons) are single
        {
            if (codepoint is >= 0x1F300 and <= 0x1FAFF) return 2;   // emoji & pictographs
            if (codepoint is >= 0x20000 and <= 0x3FFFD) return 2;   // CJK Extensions B..H
            return 1;
        }
        if (InRanges(codepoint, ZeroWidth)) return 0;
        if (InRanges(codepoint, Wide)) return 2;
        return 1;
    }

    private static bool InRanges(int cp, (int Lo, int Hi)[] ranges)
    {
        // Ranges are sorted and non-overlapping; binary search.
        int lo = 0, hi = ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (cp < ranges[mid].Lo) hi = mid - 1;
            else if (cp > ranges[mid].Hi) lo = mid + 1;
            else return true;
        }
        return false;
    }
}
