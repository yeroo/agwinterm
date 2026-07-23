//! Faithful port of Agwinterm.Core: Color, ColorSpec, CellAttributes, Cell.
//! Behavior-identical to the C# originals; the differential oracle enforces it.

#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]
pub struct Color {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl Color {
    pub const DEFAULT_FOREGROUND: Color = Color { r: 229, g: 229, b: 229 };
    pub const DEFAULT_BACKGROUND: Color = Color { r: 0, g: 0, b: 0 };

    const STANDARD_LEVELS: [u8; 6] = [0, 95, 135, 175, 215, 255];
    const ANSI16: [Color; 16] = [
        Color { r: 0, g: 0, b: 0 },       Color { r: 205, g: 0, b: 0 },
        Color { r: 0, g: 205, b: 0 },     Color { r: 205, g: 205, b: 0 },
        Color { r: 0, g: 0, b: 238 },     Color { r: 205, g: 0, b: 205 },
        Color { r: 0, g: 205, b: 205 },   Color { r: 229, g: 229, b: 229 },
        Color { r: 127, g: 127, b: 127 }, Color { r: 255, g: 0, b: 0 },
        Color { r: 0, g: 255, b: 0 },     Color { r: 255, g: 255, b: 0 },
        Color { r: 92, g: 92, b: 255 },   Color { r: 255, g: 0, b: 255 },
        Color { r: 0, g: 255, b: 255 },   Color { r: 255, g: 255, b: 255 },
    ];

    /// xterm 256-palette resolution — mirrors Color.FromIndex (which throws outside
    /// 0..=255; u8 makes that unrepresentable here).
    pub fn from_index(palette_index: u8) -> Color {
        let i = palette_index as usize;
        if i < 16 {
            return Self::ANSI16[i];
        }
        if i < 232 {
            let c = i - 16;
            return Color {
                r: Self::STANDARD_LEVELS[c / 36],
                g: Self::STANDARD_LEVELS[(c / 6) % 6],
                b: Self::STANDARD_LEVELS[c % 6],
            };
        }
        let v = (8 + (i - 232) * 10) as u8;
        Color { r: v, g: v, b: v }
    }
}

/// Mirrors C# ColorSpecKind : byte { Default, Indexed, Rgb }.
#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]
#[repr(u8)]
pub enum ColorSpecKind {
    #[default]
    Default = 0,
    Indexed = 1,
    Rgb = 2,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]
pub struct ColorSpec {
    pub kind: ColorSpecKind,
    pub index: u8,
    pub rgb: Color,
}

impl ColorSpec {
    pub const DEFAULT: ColorSpec = ColorSpec { kind: ColorSpecKind::Default, index: 0, rgb: Color { r: 0, g: 0, b: 0 } };
    pub fn indexed(index: u8) -> ColorSpec {
        ColorSpec { kind: ColorSpecKind::Indexed, index, ..Self::DEFAULT }
    }
    pub fn from_rgb(rgb: Color) -> ColorSpec {
        ColorSpec { kind: ColorSpecKind::Rgb, index: 0, rgb }
    }
}

/// Mirrors C# [Flags] CellAttributes.
pub mod attrs {
    pub const NONE: u32 = 0;
    pub const BOLD: u32 = 1;
    pub const ITALIC: u32 = 2;
    pub const UNDERLINE: u32 = 4;
    pub const INVERSE: u32 = 8;
    pub const DIM: u32 = 16;
    pub const STRIKETHROUGH: u32 = 32;
}

/// A single grid cell. `rune` is a full Unicode codepoint; `width` is 1 for normal
/// cells, 2 for the leading cell of a double-width glyph, 0 for the trailing spacer.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct Cell {
    pub rune: i32,
    pub foreground: Color,
    pub background: Color,
    pub attributes: u32,
    pub width: u8,
    pub fg_spec: ColorSpec,
    pub bg_spec: ColorSpec,
}

impl Cell {
    pub const EMPTY: Cell = Cell {
        rune: ' ' as i32,
        foreground: Color::DEFAULT_FOREGROUND,
        background: Color::DEFAULT_BACKGROUND,
        attributes: attrs::NONE,
        width: 1,
        fg_spec: ColorSpec::DEFAULT,
        bg_spec: ColorSpec::DEFAULT,
    };
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ansi16_and_cube_and_gray() {
        assert_eq!(Color::from_index(1), Color { r: 205, g: 0, b: 0 });
        assert_eq!(Color::from_index(16), Color { r: 0, g: 0, b: 0 });
        assert_eq!(Color::from_index(196), Color { r: 255, g: 0, b: 0 }); // 16 + 180 = pure red corner
        assert_eq!(Color::from_index(232), Color { r: 8, g: 8, b: 8 });
        assert_eq!(Color::from_index(255), Color { r: 238, g: 238, b: 238 });
    }

    #[test]
    fn empty_cell_matches_csharp() {
        assert_eq!(Cell::EMPTY.rune, 32);
        assert_eq!(Cell::EMPTY.width, 1);
        assert_eq!(Cell::EMPTY.foreground, Color::DEFAULT_FOREGROUND);
    }
}
