//! agwinterm-core: Rust port of the Agwinterm.Core terminal emulator
//! (strategy decided 2026-07: leak audit first, then this incremental port).
//!
//! Port order (each stage validated against the C# implementation as a
//! differential oracle before the next begins):
//!   1. wcwidth              — DONE
//!   2. cell + screen buffer — DONE
//!   3. VT parser            — DONE (event-stream oracle)
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
pub const ABI_VERSION: u32 = 3;

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

// ---- VT parser event-stream oracle (module 3) ----
// Feed bytes through the Rust parser with a RECORDING performer; return the
// event log as one UTF-8 string. The C# oracle test records the SAME canonical
// encoding from its own performer and compares strings. Canonical event forms:
//   P:XXXX          Print, 4-hex UTF-16 code unit
//   E:XX            Execute, 2-hex control byte
//   ESC:XX          EscDispatch, 2-hex final
//   CSI:XX:YY:a,b   CsiDispatch, 2-hex final, 2-hex prefix (00 = none), params
//   OSC:n:text      OscDispatch
//   APC:text        ApcDispatch (byte-as-char payload)
//   DCS:hexbytes    DcsDispatch, payload as lowercase hex
// joined with '\n'.

struct RecordingPerformer(String);

impl RecordingPerformer {
    fn push(&mut self, s: &str) {
        if !self.0.is_empty() {
            self.0.push('\n');
        }
        self.0.push_str(s);
    }
}

impl vtparser::Performer for RecordingPerformer {
    fn print(&mut self, ch: u16) {
        self.push(&format!("P:{ch:04X}"));
    }
    fn execute(&mut self, byte: u8) {
        self.push(&format!("E:{byte:02X}"));
    }
    fn esc_dispatch(&mut self, ch: u8) {
        self.push(&format!("ESC:{ch:02X}"));
    }
    fn csi_dispatch(&mut self, ch: u8, params: &[i32], prefix: u8) {
        let ps: Vec<String> = params.iter().map(|p| p.to_string()).collect();
        self.push(&format!("CSI:{ch:02X}:{prefix:02X}:{}", ps.join(",")));
    }
    fn osc_dispatch(&mut self, command: i32, text: &str) {
        self.push(&format!("OSC:{command}:{text}"));
    }
    fn apc_dispatch(&mut self, text: &str) {
        self.push(&format!("APC:{text}"));
    }
    fn dcs_dispatch(&mut self, payload: &[u8]) {
        let mut s = String::with_capacity(4 + payload.len() * 2);
        s.push_str("DCS:");
        for b in payload {
            s.push_str(&format!("{b:02x}"));
        }
        self.push(&s);
    }
}

/// Parse `len` bytes and return the recorded event log as a heap buffer
/// (UTF-8, no terminator). Caller MUST free it with `agwcore_free_buf`.
/// Returns null (len 0) on null input.
/// # Safety
/// `bytes` must point to `len` readable bytes; `out_len` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_vtparse_events(bytes: *const u8, len: u32, out_len: *mut u32) -> *mut u8 {
    if bytes.is_null() || out_len.is_null() {
        return core::ptr::null_mut();
    }
    let input = unsafe { core::slice::from_raw_parts(bytes, len as usize) };
    let mut parser = vtparser::VtParser::new();
    let mut rec = RecordingPerformer(String::new());
    parser.feed(input, &mut rec);
    let mut buf = rec.0.into_bytes().into_boxed_slice();
    unsafe { *out_len = buf.len() as u32 };
    let ptr = buf.as_mut_ptr();
    core::mem::forget(buf);
    ptr
}

/// Free a buffer returned by `agwcore_vtparse_events`.
/// # Safety
/// `ptr`/`len` must be exactly what `agwcore_vtparse_events` returned, freed once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_free_buf(ptr: *mut u8, len: u32) {
    if !ptr.is_null() {
        drop(unsafe { Box::from_raw(core::slice::from_raw_parts_mut(ptr, len as usize)) });
    }
}
