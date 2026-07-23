//! agwinterm-core: Rust port of the Agwinterm.Core terminal emulator
//! (strategy decided 2026-07: leak audit first, then this incremental port).
//!
//! Port order (each stage validated against the C# implementation as a
//! differential oracle before the next begins):
//!   1. wcwidth              — DONE
//!   2. cell + screen buffer — DONE
//!   3. VT parser (CSI/OSC/DCS state machine)
//!   4. emulator (cursor, modes, SGR, scroll regions, alt screen, marks)
//!   5. sixel/kitty image decode
//!
//! The C ABI below is the only public surface the C# side sees. It grows
//! stage by stage; nothing is exported until its differential tests pass.

pub mod cell;
pub mod screen;
pub mod vtparser;
pub mod wcwidth;

use cell::{Cell, Color, ColorSpec, ColorSpecKind};
use screen::ScreenBuffer;

/// Bumped whenever the exported C surface changes shape. The C# loader
/// refuses a mismatch loudly (same hard-handshake philosophy as the
/// pty-host protocol).
pub const ABI_VERSION: u32 = 2;

#[unsafe(no_mangle)]
pub extern "C" fn agwcore_abi_version() -> u32 {
    ABI_VERSION
}

/// East-Asian display width of a codepoint: 0, 1, or 2. Mirrors Wcwidth.Of.
#[unsafe(no_mangle)]
pub extern "C" fn agwcore_wcwidth(codepoint: u32) -> u8 {
    wcwidth::of(codepoint)
}

/// xterm 256-palette entry as 0x00RRGGBB. Mirrors Color.FromIndex.
#[unsafe(no_mangle)]
pub extern "C" fn agwcore_color_from_index(palette_index: u8) -> u32 {
    let c = Color::from_index(palette_index);
    ((c.r as u32) << 16) | ((c.g as u32) << 8) | c.b as u32
}

/// FFI cell: deliberately FLAT i32/u32 fields (no packed bytes) so the layout
/// is unambiguous on both sides of the ABI — the oracle compares millions of
/// these, so "no padding surprises" beats compactness here.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct FfiCell {
    pub rune: i32,
    pub fg: u32,      // 0x00RRGGBB resolved colors
    pub bg: u32,
    pub attrs: u32,
    pub width: u32,
    pub fg_kind: u32, // ColorSpecKind: 0 default / 1 indexed / 2 rgb
    pub fg_index: u32,
    pub fg_rgb: u32,
    pub bg_kind: u32,
    pub bg_index: u32,
    pub bg_rgb: u32,
}

fn pack_color(c: Color) -> u32 {
    ((c.r as u32) << 16) | ((c.g as u32) << 8) | c.b as u32
}
fn unpack_color(v: u32) -> Color {
    Color { r: (v >> 16) as u8, g: (v >> 8) as u8, b: v as u8 }
}
fn unpack_kind(v: u32) -> ColorSpecKind {
    match v {
        1 => ColorSpecKind::Indexed,
        2 => ColorSpecKind::Rgb,
        _ => ColorSpecKind::Default,
    }
}

impl From<Cell> for FfiCell {
    fn from(c: Cell) -> FfiCell {
        FfiCell {
            rune: c.rune,
            fg: pack_color(c.foreground),
            bg: pack_color(c.background),
            attrs: c.attributes,
            width: c.width as u32,
            fg_kind: c.fg_spec.kind as u32,
            fg_index: c.fg_spec.index as u32,
            fg_rgb: pack_color(c.fg_spec.rgb),
            bg_kind: c.bg_spec.kind as u32,
            bg_index: c.bg_spec.index as u32,
            bg_rgb: pack_color(c.bg_spec.rgb),
        }
    }
}

impl From<FfiCell> for Cell {
    fn from(f: FfiCell) -> Cell {
        Cell {
            rune: f.rune,
            foreground: unpack_color(f.fg),
            background: unpack_color(f.bg),
            attributes: f.attrs,
            width: f.width as u8,
            fg_spec: ColorSpec { kind: unpack_kind(f.fg_kind), index: f.fg_index as u8, rgb: unpack_color(f.fg_rgb) },
            bg_spec: ColorSpec { kind: unpack_kind(f.bg_kind), index: f.bg_index as u8, rgb: unpack_color(f.bg_rgb) },
        }
    }
}

// ---- ScreenBuffer over the ABI: an opaque handle + guarded operations.
// Invalid arguments return false/null where the C# original throws.

#[unsafe(no_mangle)]
pub extern "C" fn agwcore_screen_new(cols: u32, rows: u32) -> *mut ScreenBuffer {
    if cols == 0 || rows == 0 || cols > 10_000 || rows > 10_000 {
        return core::ptr::null_mut();
    }
    Box::into_raw(Box::new(ScreenBuffer::new(cols as usize, rows as usize)))
}

/// # Safety
/// `p` must be a pointer from `agwcore_screen_new`, freed exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_screen_free(p: *mut ScreenBuffer) {
    if !p.is_null() {
        drop(unsafe { Box::from_raw(p) });
    }
}

unsafe fn sb<'a>(p: *mut ScreenBuffer) -> Option<&'a mut ScreenBuffer> {
    unsafe { p.as_mut() }
}

/// # Safety
/// `p` from `agwcore_screen_new`; `out` a valid FfiCell pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_screen_get(p: *mut ScreenBuffer, row: u32, col: u32, out: *mut FfiCell) -> bool {
    let Some(s) = (unsafe { sb(p) }) else { return false };
    if out.is_null() || row as usize >= s.rows() || col as usize >= s.cols() {
        return false;
    }
    unsafe { *out = s.get(row as usize, col as usize).into() };
    true
}

/// # Safety
/// `p` from `agwcore_screen_new`; `cell` a valid FfiCell pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_screen_set(p: *mut ScreenBuffer, row: u32, col: u32, cell: *const FfiCell) -> bool {
    let Some(s) = (unsafe { sb(p) }) else { return false };
    if cell.is_null() || row as usize >= s.rows() || col as usize >= s.cols() {
        return false;
    }
    s.set(row as usize, col as usize, unsafe { *cell }.into());
    true
}

/// # Safety
/// `p` from `agwcore_screen_new`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_screen_clear(p: *mut ScreenBuffer) -> bool {
    let Some(s) = (unsafe { sb(p) }) else { return false };
    s.clear();
    true
}

/// # Safety
/// `p` from `agwcore_screen_new`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_screen_move_rows(p: *mut ScreenBuffer, src: u32, dst: u32, count: u32) -> bool {
    let Some(s) = (unsafe { sb(p) }) else { return false };
    let (src, dst, count) = (src as usize, dst as usize, count as usize);
    if count == 0 {
        return true;
    }
    if src >= s.rows() || dst >= s.rows() || src + count > s.rows() || dst + count > s.rows() {
        return false;
    }
    s.move_rows(src, dst, count);
    true
}

/// # Safety
/// `p` from `agwcore_screen_new`; `cell` a valid FfiCell pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_screen_fill_row(p: *mut ScreenBuffer, row: u32, cell: *const FfiCell) -> bool {
    let Some(s) = (unsafe { sb(p) }) else { return false };
    if cell.is_null() || row as usize >= s.rows() {
        return false;
    }
    s.fill_row(row as usize, unsafe { *cell }.into());
    true
}

/// # Safety
/// `p` from `agwcore_screen_new`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_screen_resize(p: *mut ScreenBuffer, cols: u32, rows: u32) -> bool {
    let Some(s) = (unsafe { sb(p) }) else { return false };
    if cols == 0 || rows == 0 || cols > 10_000 || rows > 10_000 {
        return false;
    }
    s.resize(cols as usize, rows as usize);
    true
}
