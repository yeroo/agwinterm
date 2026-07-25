//! agwinterm-core: Rust port of the Agwinterm.Core terminal emulator
//! (strategy decided 2026-07: leak audit first, then this incremental port).
//!
//! Port order (each stage validated against the C# implementation as a
//! differential oracle before the next begins):
//!   1. wcwidth              — DONE
//!   2. cell + screen buffer — DONE
//!   3. VT parser            — DONE (event-stream oracle)
//!   4. emulator             — DONE (full-state oracle; images excluded → module 5)
//!   4. emulator (cursor, modes, SGR, scroll regions, alt screen, marks)
//!   5. sixel/kitty image decode
//!
//! The C ABI below is the only public surface the C# side sees. It grows
//! stage by stage; nothing is exported until its differential tests pass.

pub mod cell;
pub mod emulator;
pub mod screen;
pub mod sixel;
pub mod vtparser;
pub mod wcwidth;

use cell::{Cell, Color, ColorSpec, ColorSpecKind};
use emulator::HostAction;
use screen::ScreenBuffer;

/// Bumped whenever the exported C surface changes shape. The C# loader
/// refuses a mismatch loudly (same hard-handshake philosophy as the
/// pty-host protocol).
pub const ABI_VERSION: u32 = 12;

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

// ---- Emulator (module 4): opaque Terminal handle + canonical full-state dump ----

use emulator::Terminal;

#[unsafe(no_mangle)]
pub extern "C" fn agwcore_emu_new(cols: u32, rows: u32) -> *mut Terminal {
    if cols == 0 || rows == 0 || cols > 10_000 || rows > 10_000 {
        return core::ptr::null_mut();
    }
    Box::into_raw(Box::new(Terminal::new(cols as usize, rows as usize)))
}

/// # Safety
/// `p` from `agwcore_emu_new`, freed exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_free(p: *mut Terminal) {
    if !p.is_null() {
        drop(unsafe { Box::from_raw(p) });
    }
}

/// # Safety
/// `p` from `agwcore_emu_new`; `bytes` points to `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_feed(p: *mut Terminal, bytes: *const u8, len: u32) -> bool {
    let Some(t) = (unsafe { p.as_mut() }) else { return false };
    if bytes.is_null() {
        return false;
    }
    t.feed(unsafe { core::slice::from_raw_parts(bytes, len as usize) });
    true
}

/// # Safety
/// `p` from `agwcore_emu_new`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_resize(p: *mut Terminal, cols: u32, rows: u32) -> bool {
    let Some(t) = (unsafe { p.as_mut() }) else { return false };
    if cols == 0 || rows == 0 || cols > 10_000 || rows > 10_000 {
        return false;
    }
    t.emu.resize(cols as usize, rows as usize);
    true
}

fn dump_cell(s: &mut String, c: Cell) {
    use core::fmt::Write;
    let pc = |c: Color| ((c.r as u32) << 16) | ((c.g as u32) << 8) | c.b as u32;
    let _ = write!(
        s,
        "{:X}.{:06X}.{:06X}.{:X}.{}.{}{:02X}.{:06X}.{}{:02X}.{:06X}",
        c.rune, pc(c.foreground), pc(c.background), c.attributes, c.width,
        c.fg_spec.kind as u8, c.fg_spec.index, pc(c.fg_spec.rgb),
        c.bg_spec.kind as u8, c.bg_spec.index, pc(c.bg_spec.rgb)
    );
}

/// Canonical full-state dump (the C# oracle builds the identical text from the
/// public TerminalEmulator surface): cursor, region, modes, kbd, title/cwd,
/// generation, marks, every history row, every grid row. Free with agwcore_free_buf.
/// # Safety
/// `p` from `agwcore_emu_new`; `out_len` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_state_dump(p: *mut Terminal, out_len: *mut u32) -> *mut u8 {
    use core::fmt::Write;
    let Some(t) = (unsafe { p.as_mut() }) else { return core::ptr::null_mut() };
    if out_len.is_null() {
        return core::ptr::null_mut();
    }
    let e = &t.emu;
    let mut s = String::new();
    let (mc, md, mm, ms) = e.mouse_flags();
    let _ = writeln!(s, "cursor:{},{},{}", e.cursor_row, e.cursor_col, e.cursor_visible as u8);
    let _ = writeln!(s, "region:{},{}", e.scroll_top(), e.scroll_bottom());
    let _ = writeln!(s, "alt:{}", e.is_alt_screen() as u8);
    let _ = writeln!(s, "mouse:{}{}{}{}", mc as u8, md as u8, mm as u8, ms as u8);
    let _ = writeln!(s, "paste:{}", e.bracketed_paste as u8);
    let _ = writeln!(s, "focus:{}", e.focus_reporting as u8);
    let _ = writeln!(s, "sync:{}", e.synchronized_output as u8);
    let _ = writeln!(s, "cursorshape:{}", e.cursor_shape);
    let _ = writeln!(s, "kbd:{}", e.keyboard_flags());
    let _ = writeln!(s, "title:{}", e.title);
    let _ = writeln!(s, "cwd:{}", e.cwd);
    let _ = writeln!(s, "gen:{}", e.scroll_generation);
    for m in e.marks() {
        let _ = writeln!(
            s, "mark:{},{},{},{},{}",
            m.prompt_line, m.command_line, m.output_line, m.end_line,
            m.exit_code.map_or("none".to_string(), |v| v.to_string())
        );
    }
    for (id, img) in e.images() {
        // FNV-1a 64 of the pixel payload — full bytes would dwarf the dump.
        let mut hash: u64 = 0xcbf29ce484222325;
        for &b in &img.data {
            hash ^= b as u64;
            hash = hash.wrapping_mul(0x100000001b3);
        }
        let _ = writeln!(s, "img:{},{},{},{},{},{:016x}", id, img.format, img.width, img.height, img.data.len(), hash);
    }
    for p in e.placements() {
        let _ = writeln!(
            s, "pl:{},{},{},{},{},{},{},{},{}",
            p.image_id, p.row, p.col, p.cols, p.rows, p.src_x, p.src_y, p.src_w, p.src_h
        );
    }
    let (cols, rows) = (e.screen().cols(), e.screen().rows());
    for h in 0..e.history_count() {
        let _ = write!(s, "h{h}:");
        for c in 0..cols {
            if c > 0 { s.push(' '); }
            dump_cell(&mut s, e.get_history_cell(h, c));
        }
        s.push('\n');
    }
    for r in 0..rows {
        let _ = write!(s, "g{r}:");
        for c in 0..cols {
            if c > 0 { s.push(' '); }
            dump_cell(&mut s, e.screen().get(r, c));
        }
        s.push('\n');
    }
    let mut buf = s.into_bytes().into_boxed_slice();
    unsafe { *out_len = buf.len() as u32 };
    let ptr = buf.as_mut_ptr();
    core::mem::forget(buf);
    ptr
}

/// Drain the queued host actions (the IHostActions seam) into a flat blob and CLEAR the queue.
/// The C# adapter calls this after each feed and forwards each action to `IHostActions`.
/// Free the returned buffer with `agwcore_free_buf`. Layout (all integers little-endian):
///   u32 count, then `count` records — u8 kind + payload:
///     1 Notify    : str title, str body
///     2 Progress  : i32 state, i32 value
///     3 Clipboard : str text
///     4 Respond   : str reply
///     5 Unhandled : str kind, str detail
///   where str = u32 byte-length + UTF-8 bytes.
/// # Safety
/// `p` from `agwcore_emu_new`; `out_len` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_take_host_actions(p: *mut Terminal, out_len: *mut u32) -> *mut u8 {
    let Some(t) = (unsafe { p.as_mut() }) else { return core::ptr::null_mut() };
    if out_len.is_null() {
        return core::ptr::null_mut();
    }
    let actions = t.emu.take_host_actions();
    if actions.is_empty() {
        // The common case — no host effects this feed. Signal "nothing" without allocating.
        unsafe { *out_len = 0 };
        return core::ptr::null_mut();
    }
    fn put_str(buf: &mut Vec<u8>, s: &str) {
        buf.extend_from_slice(&(s.len() as u32).to_le_bytes());
        buf.extend_from_slice(s.as_bytes());
    }
    let mut buf: Vec<u8> = Vec::new();
    buf.extend_from_slice(&(actions.len() as u32).to_le_bytes());
    for a in &actions {
        match a {
            HostAction::Notify { title, body } => {
                buf.push(1);
                put_str(&mut buf, title);
                put_str(&mut buf, body);
            }
            HostAction::Progress { state, value } => {
                buf.push(2);
                buf.extend_from_slice(&state.to_le_bytes());
                buf.extend_from_slice(&value.to_le_bytes());
            }
            HostAction::Clipboard { text } => {
                buf.push(3);
                put_str(&mut buf, text);
            }
            HostAction::Respond { reply } => {
                buf.push(4);
                put_str(&mut buf, reply);
            }
            HostAction::Unhandled { kind, detail } => {
                buf.push(5);
                put_str(&mut buf, kind);
                put_str(&mut buf, detail);
            }
        }
    }
    let mut boxed = buf.into_boxed_slice();
    unsafe { *out_len = boxed.len() as u32 };
    let ptr = boxed.as_mut_ptr();
    core::mem::forget(boxed);
    ptr
}

// ---- Integration surface (the RustEmulatorCore adapter, beyond the oracle) ----

/// Fixed-size scalar snapshot for per-frame reads — one call instead of a dozen.
#[repr(C)]
pub struct FfiEmuInfo {
    pub cols: u32,
    pub rows: u32,
    pub cursor_row: u32,
    pub cursor_col: u32,      // may equal cols (post-print overflow)
    pub cursor_visible: u32,
    pub is_alt_screen: u32,
    pub history_count: u32,
    pub scroll_generation: i64,
    pub mouse_click: u32,
    pub mouse_drag: u32,
    pub mouse_motion: u32,
    pub mouse_sgr: u32,
    pub bracketed_paste: u32,
    pub keyboard_flags: i32,
    pub scroll_top: u32,
    pub scroll_bottom: u32,
    pub mark_count: u32,
    pub focus_reporting: u32,
    pub synchronized_output: u32,
    pub cursor_shape: i32,
}

/// # Safety
/// `p` from `agwcore_emu_new`; `out` a valid FfiEmuInfo pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_info(p: *mut Terminal, out: *mut FfiEmuInfo) -> bool {
    let Some(t) = (unsafe { p.as_mut() }) else { return false };
    if out.is_null() {
        return false;
    }
    let e = &t.emu;
    let (mc, md, mm, ms) = e.mouse_flags();
    unsafe {
        *out = FfiEmuInfo {
            cols: e.screen().cols() as u32,
            rows: e.screen().rows() as u32,
            cursor_row: e.cursor_row as u32,
            cursor_col: e.cursor_col as u32,
            cursor_visible: e.cursor_visible as u32,
            is_alt_screen: e.is_alt_screen() as u32,
            history_count: e.history_count() as u32,
            scroll_generation: e.scroll_generation,
            mouse_click: mc as u32,
            mouse_drag: md as u32,
            mouse_motion: mm as u32,
            mouse_sgr: ms as u32,
            bracketed_paste: e.bracketed_paste as u32,
            keyboard_flags: e.keyboard_flags(),
            scroll_top: e.scroll_top() as u32,
            scroll_bottom: e.scroll_bottom() as u32,
            mark_count: e.marks().len() as u32,
            focus_reporting: e.focus_reporting as u32,
            synchronized_output: e.synchronized_output as u32,
            cursor_shape: e.cursor_shape,
        };
    }
    true
}

/// One FTCS shell mark over the ABI (buffer-absolute lines; -1 = unset).
#[repr(C)]
pub struct FfiMark {
    pub prompt_line: i64,
    pub command_line: i64,
    pub output_line: i64,
    pub end_line: i64,
    pub has_exit: u32,
    pub exit_code: i32,
}

/// Copy all FTCS marks (oldest first) into `out`; returns how many were written.
/// # Safety
/// `p` from `agwcore_emu_new`; `out` points to `cap` writable FfiMarks.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_marks(p: *mut Terminal, out: *mut FfiMark, cap: u32) -> u32 {
    let Some(t) = (unsafe { p.as_mut() }) else { return 0 };
    if out.is_null() {
        return 0;
    }
    let marks = t.emu.marks();
    let n = marks.len().min(cap as usize);
    let out = unsafe { core::slice::from_raw_parts_mut(out, n) };
    for (i, m) in marks.iter().take(n).enumerate() {
        out[i] = FfiMark {
            prompt_line: m.prompt_line,
            command_line: m.command_line,
            output_line: m.output_line,
            end_line: m.end_line,
            has_exit: m.exit_code.is_some() as u32,
            exit_code: m.exit_code.unwrap_or(0),
        };
    }
    n as u32
}

/// Seed the scrollback with plain-text lines ('\n'-separated UTF-8) — the restore path.
/// # Safety
/// `p` from `agwcore_emu_new`; `text` points to `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_seed_scrollback(p: *mut Terminal, text: *const u8, len: u32) -> bool {
    let Some(t) = (unsafe { p.as_mut() }) else { return false };
    if text.is_null() {
        return len == 0;
    }
    let bytes = unsafe { core::slice::from_raw_parts(text, len as usize) };
    let s = String::from_utf8_lossy(bytes);
    let lines: Vec<&str> = s.split('\n').collect();
    t.emu.seed_scrollback(&lines);
    true
}

// ---- images (ABI v8): kitty/sixel surface for emulator-core = rust ----

/// One image placement over the ABI (mirrors ImagePlacement).
#[repr(C)]
pub struct FfiPlacement {
    pub image_id: i32,
    pub row: i64,
    pub col: i64,
    pub cols: i32,
    pub rows: i32,
    pub src_x: i32,
    pub src_y: i32,
    pub src_w: i32,
    pub src_h: i32,
}

/// Image metadata (not the pixels — those come via agwcore_emu_copy_image_data).
#[repr(C)]
pub struct FfiImageMeta {
    pub id: i32,
    pub format: i32,
    pub width: i32,
    pub height: i32,
    pub data_len: u32,
}

/// Number of live placements (for the caller to size its buffer).
/// # Safety
/// `p` from `agwcore_emu_new`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_placement_count(p: *mut Terminal) -> u32 {
    (unsafe { p.as_mut() }).map_or(0, |t| t.emu.placements().len() as u32)
}

/// Copy all placements (z-order of arrival) into `out`; returns how many were written.
/// # Safety
/// `p` from `agwcore_emu_new`; `out` points to `cap` writable FfiPlacements.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_copy_placements(p: *mut Terminal, out: *mut FfiPlacement, cap: u32) -> u32 {
    let Some(t) = (unsafe { p.as_mut() }) else { return 0 };
    if out.is_null() {
        return 0;
    }
    let pl = t.emu.placements();
    let n = pl.len().min(cap as usize);
    let out = unsafe { core::slice::from_raw_parts_mut(out, n) };
    for (i, p) in pl.iter().take(n).enumerate() {
        out[i] = FfiPlacement {
            image_id: p.image_id, row: p.row, col: p.col, cols: p.cols, rows: p.rows,
            src_x: p.src_x, src_y: p.src_y, src_w: p.src_w, src_h: p.src_h,
        };
    }
    n as u32
}

/// Copy image-id + metadata for every transmitted image (ascending id). Pixel data is fetched
/// separately (agwcore_emu_copy_image_data), so the C# side uploads a texture only once per id.
/// Returns the number written.
/// # Safety
/// `p` from `agwcore_emu_new`; `out` points to `cap` writable FfiImageMeta.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_image_metas(p: *mut Terminal, out: *mut FfiImageMeta, cap: u32) -> u32 {
    let Some(t) = (unsafe { p.as_mut() }) else { return 0 };
    if out.is_null() {
        return 0;
    }
    let imgs = t.emu.images();
    let n = imgs.len().min(cap as usize);
    let out = unsafe { core::slice::from_raw_parts_mut(out, n) };
    for (i, (id, img)) in imgs.iter().take(n).enumerate() {
        out[i] = FfiImageMeta { id: *id, format: img.format, width: img.width, height: img.height, data_len: img.data.len() as u32 };
    }
    n as u32
}

/// Copy one image's raw bytes into `out` (size it from FfiImageMeta.data_len). Returns bytes written.
/// # Safety
/// `p` from `agwcore_emu_new`; `out` points to `cap` writable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_copy_image_data(p: *mut Terminal, id: i32, out: *mut u8, cap: u32) -> u32 {
    let Some(t) = (unsafe { p.as_mut() }) else { return 0 };
    let Some(img) = t.emu.images().get(&id) else { return 0 };
    if out.is_null() || (cap as usize) < img.data.len() {
        return 0;
    }
    unsafe { core::ptr::copy_nonoverlapping(img.data.as_ptr(), out, img.data.len()) };
    img.data.len() as u32
}

/// # Safety: `p` from `agwcore_emu_new`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_has_image(p: *mut Terminal, id: i32) -> bool {
    (unsafe { p.as_mut() }).is_some_and(|t| t.emu.has_image(id))
}

/// # Safety: `p` from `agwcore_emu_new`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_clear_placements(p: *mut Terminal) {
    if let Some(t) = (unsafe { p.as_mut() }) {
        t.emu.clear_placements();
    }
}

/// # Safety: `p` from `agwcore_emu_new`; `data` points to `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_set_image_data(p: *mut Terminal, id: i32, format: i32, width: i32, height: i32, data: *const u8, len: u32) -> bool {
    let Some(t) = (unsafe { p.as_mut() }) else { return false };
    let bytes = if data.is_null() { Vec::new() } else { unsafe { core::slice::from_raw_parts(data, len as usize) }.to_vec() };
    t.emu.set_image_data(id, format, width, height, bytes);
    true
}

/// # Safety: `p` from `agwcore_emu_new`.
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn agwcore_emu_place_image(p: *mut Terminal, id: i32, row: i64, col: i64, cols: i32, rows: i32, src_x: i32, src_y: i32, src_w: i32, src_h: i32) -> bool {
    let Some(t) = (unsafe { p.as_mut() }) else { return false };
    t.emu.place_image(id, row, col, cols, rows, src_x, src_y, src_w, src_h);
    true
}

/// # Safety: `p` from `agwcore_emu_new`; `data` points to `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_place_sixel(p: *mut Terminal, data: *const u8, len: u32) -> bool {
    let Some(t) = (unsafe { p.as_mut() }) else { return false };
    if data.is_null() {
        return false;
    }
    t.emu.place_sixel_public(unsafe { core::slice::from_raw_parts(data, len as usize) })
}

/// Copy the whole visible grid, row-major, into `out` (must hold cols*rows cells).
/// This is the renderer's per-frame snapshot path: one bulk copy under the session
/// lock instead of cols×rows interop calls.
/// # Safety
/// `p` from `agwcore_emu_new`; `out` points to `cap` writable FfiCells.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_copy_grid(p: *mut Terminal, out: *mut FfiCell, cap: u32) -> bool {
    let Some(t) = (unsafe { p.as_mut() }) else { return false };
    let e = &t.emu;
    let (cols, rows) = (e.screen().cols(), e.screen().rows());
    if out.is_null() || (cap as usize) < cols * rows {
        return false;
    }
    let out = unsafe { core::slice::from_raw_parts_mut(out, cols * rows) };
    for r in 0..rows {
        for c in 0..cols {
            out[r * cols + c] = e.screen().get(r, c).into();
        }
    }
    true
}

/// Copy one scrollback row (padded to the current width). Row 0 = oldest.
/// # Safety
/// `p` from `agwcore_emu_new`; `out` points to `cap` writable FfiCells.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_copy_history_row(p: *mut Terminal, row: u32, out: *mut FfiCell, cap: u32) -> bool {
    let Some(t) = (unsafe { p.as_mut() }) else { return false };
    let e = &t.emu;
    let cols = e.screen().cols();
    if out.is_null() || (cap as usize) < cols || (row as usize) >= e.history_count() {
        return false;
    }
    let out = unsafe { core::slice::from_raw_parts_mut(out, cols) };
    for c in 0..cols {
        out[c] = e.get_history_cell(row as usize, c).into();
    }
    true
}

/// UTF-8 string properties: 0 = title, 1 = cwd, 2 = DumpModes. Free with agwcore_free_buf.
/// # Safety
/// `p` from `agwcore_emu_new`; `out_len` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn agwcore_emu_get_text(p: *mut Terminal, which: u32, out_len: *mut u32) -> *mut u8 {
    let Some(t) = (unsafe { p.as_mut() }) else { return core::ptr::null_mut() };
    if out_len.is_null() {
        return core::ptr::null_mut();
    }
    let s = match which {
        0 => t.emu.title.clone(),
        1 => t.emu.cwd.clone(),
        2 => t.emu.dump_modes(),
        _ => return core::ptr::null_mut(),
    };
    let mut buf = s.into_bytes().into_boxed_slice();
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
