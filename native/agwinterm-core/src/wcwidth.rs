//! Faithful port of Agwinterm.Core/Wcwidth.cs — minimal East-Asian width lookup.
//! Returns 2 for wide glyphs, 0 for zero-width combining marks, 1 otherwise.
//! MUST stay byte-for-byte behavior-identical to the C# oracle; every range here
//! mirrors the C# table exactly (differential tests enforce it).

/// Inclusive [lo, hi] ranges of double-width code points (BMP).
const WIDE: &[(u32, u32)] = &[
    (0x1100, 0x115F),  // Hangul Jamo
    (0x2329, 0x232A),  // angle brackets
    (0x2E80, 0x303E),  // CJK radicals, Kangxi, CJK symbols/punctuation
    (0x3041, 0x33FF),  // Hiragana, Katakana, CJK symbols, enclosed
    (0x3400, 0x4DBF),  // CJK Extension A
    (0x4E00, 0x9FFF),  // CJK Unified Ideographs
    (0xA000, 0xA4CF),  // Yi
    (0xAC00, 0xD7A3),  // Hangul Syllables
    (0xF900, 0xFAFF),  // CJK Compatibility Ideographs
    (0xFE10, 0xFE19),  // vertical forms
    (0xFE30, 0xFE6F),  // CJK compatibility / small forms
    (0xFF00, 0xFF60),  // Fullwidth forms
    (0xFFE0, 0xFFE6),  // Fullwidth signs
];

/// Inclusive [lo, hi] ranges of zero-width combining marks (BMP subset).
const ZERO_WIDTH: &[(u32, u32)] = &[
    (0x0300, 0x036F),  // combining diacritical marks
    (0x0483, 0x0489),
    (0x0591, 0x05BD),
    (0x0610, 0x061A),
    (0x064B, 0x065F),
    (0x0670, 0x0670),
    (0x06D6, 0x06DC),
    (0x200B, 0x200F),  // zero-width space / joiners / marks
    (0xFE20, 0xFE2F),  // combining half marks
];

pub fn of(codepoint: u32) -> u8 {
    if codepoint == 0 {
        return 0;
    }
    if codepoint > 0xFFFF {
        // astral: emoji/CJK extensions are wide; plane-15/16 PUA (nerd-font icons) are single
        if (0x1F300..=0x1FAFF).contains(&codepoint) {
            return 2; // emoji & pictographs
        }
        if (0x20000..=0x3FFFD).contains(&codepoint) {
            return 2; // CJK Extensions B..H
        }
        return 1;
    }
    if in_ranges(codepoint, ZERO_WIDTH) {
        return 0;
    }
    if in_ranges(codepoint, WIDE) {
        return 2;
    }
    1
}

fn in_ranges(cp: u32, ranges: &[(u32, u32)]) -> bool {
    ranges
        .binary_search_by(|&(lo, hi)| {
            if cp < lo {
                core::cmp::Ordering::Greater
            } else if cp > hi {
                core::cmp::Ordering::Less
            } else {
                core::cmp::Ordering::Equal
            }
        })
        .is_ok()
}

#[cfg(test)]
mod tests {
    use super::of;

    #[test]
    fn ascii_is_single() {
        assert_eq!(of(b'A' as u32), 1);
        assert_eq!(of(b' ' as u32), 1);
    }

    #[test]
    fn nul_is_zero() {
        assert_eq!(of(0), 0);
    }

    #[test]
    fn cjk_is_wide() {
        assert_eq!(of(0x4E2D), 2); // 中
        assert_eq!(of(0xAC00), 2); // 가
    }

    #[test]
    fn combining_is_zero() {
        assert_eq!(of(0x0301), 0); // combining acute
        assert_eq!(of(0x200D), 0); // ZWJ
    }

    #[test]
    fn astral_split() {
        assert_eq!(of(0x1F600), 2); // emoji
        assert_eq!(of(0x20001), 2); // CJK ext B
        assert_eq!(of(0xF0001), 1); // plane-15 PUA (nerd font)
    }
}
