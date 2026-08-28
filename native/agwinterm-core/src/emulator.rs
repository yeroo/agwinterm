//! Faithful port of Agwinterm.Core/TerminalEmulator.cs (module 4).
//!
//! Scope: everything EXCEPT images (kitty APC / sixel DCS / placements — module 5)
//! and host actions (notifications, clipboard, responses — the C# oracle runs with
//! Host = null, where those are dropped, so parity holds with them absent here).
//!
//! Quirks deliberately preserved (the full-state differential oracle enforces them):
//!  - Deferred-ish wrap: CursorCol may sit == Cols after printing the last column;
//!    the NEXT print wraps. Dumps expose the raw value.
//!  - TAB clamps to Cols-1 (and can move the cursor LEFT from the post-print
//!    overflow position).
//!  - EraseLine treats any mode != 0 as "from column 0" and any mode != 1 as
//!    "to the last column" (so mode 3 == mode 2). EraseDisplay ignores modes > 2.
//!  - SGR 38/48;2 casts channel params with wrapping byte conversion; ;5 with an
//!    out-of-range index consumes params and changes nothing (C# guarded the same
//!    way after the port review found the unguarded throw).
//!  - Invalid DECSTBM resets to full screen; any DECSTBM homes the cursor.
//!  - Zero-width codepoints are dropped (v1 semantics).

use crate::cell::{Cell, Color, ColorSpec, attrs};
use crate::screen::ScreenBuffer;
use crate::sixel;
use crate::vtparser::{Performer, VtParser};
use crate::wcwidth;
use std::collections::{BTreeMap, HashMap};

const TRIM_SLACK: usize = 512;

/// Mirror of C# KittyImage (format kept as the raw transmitted int — the C# enum
/// cast stores arbitrary values unchanged).
pub struct KittyImage {
    pub id: i32,
    pub format: i32,
    pub width: i32,
    pub height: i32,
    pub data: Vec<u8>,
}

/// Mirror of C# ImagePlacement.
#[derive(Clone, Copy)]
pub struct ImagePlacement {
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

/// Convert.FromBase64String parity: whitespace ignored, length % 4 == 0, '=' only
/// as final padding (max 2), invalid → None (the C# FormatException path).
fn base64_decode(s: &str) -> Option<Vec<u8>> {
    let mut chars: Vec<u8> = Vec::with_capacity(s.len());
    for c in s.bytes() {
        if !matches!(c, b' ' | b'\t' | b'\r' | b'\n') {
            chars.push(c);
        }
    }
    if !chars.len().is_multiple_of(4) {
        return None;
    }
    if chars.is_empty() {
        return Some(Vec::new());
    }
    let val = |c: u8| -> Option<u32> {
        match c {
            b'A'..=b'Z' => Some((c - b'A') as u32),
            b'a'..=b'z' => Some((c - b'a' + 26) as u32),
            b'0'..=b'9' => Some((c - b'0' + 52) as u32),
            b'+' => Some(62),
            b'/' => Some(63),
            _ => None,
        }
    };
    let groups = chars.len() / 4;
    let mut out = Vec::with_capacity(groups * 3);
    for (gi, group) in chars.chunks(4).enumerate() {
        let last = gi == groups - 1;
        let mut acc = 0u32;
        let mut n = 4usize;
        for (k, &c) in group.iter().enumerate() {
            if c == b'=' {
                if !last || k < 2 || group[k..].iter().any(|&x| x != b'=') {
                    return None;
                }
                n = k;
                break;
            }
            acc = (acc << 6) | val(c)?;
        }
        match n {
            4 => out.extend_from_slice(&[(acc >> 16) as u8, (acc >> 8) as u8, acc as u8]),
            3 => {
                let acc = acc << 6;
                out.extend_from_slice(&[(acc >> 16) as u8, (acc >> 8) as u8]);
            }
            2 => out.push(((acc << 12) >> 16) as u8),
            _ => return None,
        }
    }
    Some(out)
}

#[derive(Clone, Copy, Default)]
pub struct ShellMark {
    pub prompt_line: i64,
    pub command_line: i64, // -1 = unset
    pub output_line: i64,
    pub end_line: i64,
    pub exit_code: Option<i32>,
}

pub struct Emulator {
    main: ScreenBuffer,
    alt: ScreenBuffer,
    on_alt: bool,

    pub cursor_row: usize,
    pub cursor_col: usize, // may equal cols (post-print overflow) — see module docs
    pub cursor_visible: bool,
    saved_row: usize,
    saved_col: usize,

    mouse_click: bool,
    mouse_drag: bool,
    mouse_motion: bool,
    mouse_sgr: bool,
    mouse_sgr_pixels: bool,
    pub bracketed_paste: bool,
    pub focus_reporting: bool,
    pub synchronized_output: bool,
    pub win32_input_mode: bool,
    pub dynamic_bg: u32,
    pub cursor_shape: i32,

    scroll_top: usize,
    scroll_bottom: usize,

    history: Vec<Vec<Cell>>,
    pub scrollback_max: usize,
    pub scroll_generation: i64,

    marks: Vec<ShellMark>,

    fg: Color,
    bg: Color,
    fg_spec: ColorSpec,
    bg_spec: ColorSpec,
    attrs: u32,

    pub title: String,
    pub cwd: String,

    kbd_stack: Vec<i32>,
    pending_high_surrogate: u16,

    images: BTreeMap<i32, KittyImage>,
    placements: Vec<ImagePlacement>,
    kitty_chunks: String,
    kitty_keys: Option<HashMap<String, String>>,
    sixel_seq: i32,
    pub cell_pixel_width: i32,
    pub cell_pixel_height: i32,

    // Host actions (the IHostActions seam) queued during feed and drained by the C# adapter
    // after each feed — the Rust core is headless, so it can't invoke the host directly.
    host_actions: Vec<HostAction>,
}

/// One host-visible side effect the embedder must perform — the Rust mirror of the C#
/// `IHostActions` members. Queued during dispatch, drained (and cleared) after each feed.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum HostAction {
    Notify { title: String, body: String },
    Progress { state: i32, value: i32 },
    Clipboard { text: String },
    Respond { reply: String },
    Bell,
    Unhandled { kind: String, detail: String },
}

impl Emulator {
    pub fn new(cols: usize, rows: usize) -> Emulator {
        Emulator {
            main: ScreenBuffer::new(cols, rows),
            alt: ScreenBuffer::new(cols, rows),
            on_alt: false,
            cursor_row: 0,
            cursor_col: 0,
            cursor_visible: true,
            saved_row: 0,
            saved_col: 0,
            mouse_click: false,
            mouse_drag: false,
            mouse_motion: false,
            mouse_sgr: false,
            mouse_sgr_pixels: false,
            bracketed_paste: false,
            focus_reporting: false,
            synchronized_output: false,
            win32_input_mode: false,
            dynamic_bg: 0,
            cursor_shape: 0,
            scroll_top: 0,
            scroll_bottom: rows - 1,
            history: Vec::new(),
            scrollback_max: 5000,
            scroll_generation: 0,
            marks: Vec::new(),
            fg: Color::DEFAULT_FOREGROUND,
            bg: Color::DEFAULT_BACKGROUND,
            fg_spec: ColorSpec::DEFAULT,
            bg_spec: ColorSpec::DEFAULT,
            attrs: attrs::NONE,
            title: String::new(),
            cwd: String::new(),
            kbd_stack: Vec::new(),
            pending_high_surrogate: 0,
            images: BTreeMap::new(),
            placements: Vec::new(),
            kitty_chunks: String::new(),
            kitty_keys: None,
            sixel_seq: -1,
            cell_pixel_width: 8,
            cell_pixel_height: 18,
            host_actions: Vec::new(),
        }
    }

    /// Drain the queued host actions (Notify/Progress/Clipboard/Respond/Unhandled), clearing
    /// the queue. The C# adapter calls this after each feed and forwards them to `IHostActions`.
    pub fn take_host_actions(&mut self) -> Vec<HostAction> {
        core::mem::take(&mut self.host_actions)
    }

    fn push_action(&mut self, a: HostAction) {
        // Defensive cap: one feed rarely emits more than a handful; the C# adapter drains after
        // every feed, so this only bounds a pathological producer that the adapter never reads.
        if self.host_actions.len() < 4096 {
            self.host_actions.push(a);
        }
    }

    pub fn images(&self) -> &BTreeMap<i32, KittyImage> {
        &self.images
    }
    pub fn placements(&self) -> &[ImagePlacement] {
        &self.placements
    }

    // ---- direct image API (mirrors TerminalEmulator: host-delivered images without the
    // APC/base64 text path — used by the control pipe). ----
    pub fn clear_placements(&mut self) {
        self.placements.clear();
    }
    pub fn has_image(&self, id: i32) -> bool {
        self.images.contains_key(&id)
    }
    pub fn set_image_data(&mut self, id: i32, format: i32, width: i32, height: i32, data: Vec<u8>) {
        self.images.insert(
            id,
            KittyImage {
                id,
                format,
                width,
                height,
                data,
            },
        );
    }
    #[allow(clippy::too_many_arguments)]
    pub fn place_image(
        &mut self,
        id: i32,
        row: i64,
        col: i64,
        cols: i32,
        rows: i32,
        src_x: i32,
        src_y: i32,
        src_w: i32,
        src_h: i32,
    ) {
        self.placements.retain(|p| p.image_id != id);
        self.placements.push(ImagePlacement {
            image_id: id,
            row,
            col,
            cols,
            rows,
            src_x,
            src_y,
            src_w,
            src_h,
        });
    }
    /// Decode a sixel payload and place it at the cursor (advancing below). Public wrapper.
    pub fn place_sixel_public(&mut self, data: &[u8]) -> bool {
        self.place_sixel(data)
    }

    pub fn screen(&self) -> &ScreenBuffer {
        if self.on_alt { &self.alt } else { &self.main }
    }
    fn scr(&mut self) -> &mut ScreenBuffer {
        if self.on_alt {
            &mut self.alt
        } else {
            &mut self.main
        }
    }
    pub fn is_alt_screen(&self) -> bool {
        self.on_alt
    }
    pub fn history_count(&self) -> usize {
        self.history.len()
    }
    /// Stored cell count of a scrollback row (post-trim) — for the memory-savings test.
    pub fn history_row_stored_len(&self, row: usize) -> usize {
        self.history.get(row).map_or(0, |r| r.len())
    }
    pub fn marks(&self) -> &[ShellMark] {
        &self.marks
    }
    pub fn keyboard_flags(&self) -> i32 {
        *self.kbd_stack.last().unwrap_or(&0)
    }
    pub fn mouse_flags(&self) -> (bool, bool, bool, bool, bool) {
        (
            self.mouse_click,
            self.mouse_drag,
            self.mouse_motion,
            self.mouse_sgr,
            self.mouse_sgr_pixels,
        )
    }
    pub fn scroll_top(&self) -> usize {
        self.scroll_top
    }
    pub fn scroll_bottom(&self) -> usize {
        self.scroll_bottom
    }

    pub fn get_history_cell(&self, history_row: usize, col: usize) -> Cell {
        match self.history.get(history_row) {
            Some(row) => *row.get(col).unwrap_or(&Cell::EMPTY),
            None => Cell::EMPTY,
        }
    }

    pub fn resize(&mut self, cols: usize, rows: usize) {
        if cols == 0 || rows == 0 {
            return;
        }
        // A "resize" to the SAME geometry is not a resize: bail before the margin reset below.
        // Hosts call this on events that need not have changed the size (lite re-syncs both panes
        // on every session switch), and clearing DECSTBM under a full-screen TUI that still thinks
        // its margins are set scrambles the display — with no SIGWINCH to make it redraw.
        if cols == self.main.cols() && rows == self.main.rows() {
            return;
        }
        self.main.resize(cols, rows);
        self.alt.resize(cols, rows);
        self.scroll_top = 0;
        self.scroll_bottom = rows - 1;
        if self.cursor_row >= rows {
            self.cursor_row = rows - 1;
        }
        if self.cursor_col >= cols {
            self.cursor_col = cols - 1;
        }
    }

    // ---- content ----

    fn print_scalar(&mut self, cp: i32) {
        let w = wcwidth::of(cp as u32) as usize;
        if w == 0 {
            return; // combining/zero-width: dropped in v1
        }
        let cols = self.screen().cols();
        if self.cursor_col >= cols {
            self.cursor_col = 0;
            self.index();
        }
        if w == 2 && self.cursor_col == self.screen().cols() - 1 {
            let b = self.blank();
            let (r, c) = (self.cursor_row, self.cursor_col);
            self.scr().set(r, c, b);
            self.cursor_col = 0;
            self.index();
        }
        let cell = Cell {
            rune: cp,
            foreground: self.fg,
            background: self.bg,
            attributes: self.attrs,
            width: w as u8,
            fg_spec: self.fg_spec,
            bg_spec: self.bg_spec,
        };
        let (r, c) = (self.cursor_row, self.cursor_col);
        self.scr().set(r, c, cell);
        if w == 2 {
            let spacer = Cell {
                rune: 0,
                width: 0,
                ..cell
            };
            self.scr().set(r, c + 1, spacer);
        }
        self.cursor_col += w;
    }

    fn blank(&self) -> Cell {
        Cell {
            rune: ' ' as i32,
            foreground: Color::DEFAULT_FOREGROUND,
            background: self.bg,
            attributes: attrs::NONE,
            width: 1,
            fg_spec: ColorSpec::DEFAULT,
            bg_spec: self.bg_spec,
        }
    }

    // ---- scrolling / regions ----

    fn push_history(&mut self) {
        let cols = self.screen().cols();
        let mut row = vec![Cell::EMPTY; cols];
        self.screen().copy_row_to(0, &mut row);
        // Trim trailing Cell::EMPTY before storing: reads pad past the end (get_history_cell), so
        // this is lossless — a BCE-coloured blank (non-default bg) isn't EMPTY and survives. Full-
        // width scrollback rows dominate the heap on wide terminals (the 6-8GB lite target, #134).
        let mut len = row.len();
        while len > 0 && row[len - 1] == Cell::EMPTY {
            len -= 1;
        }
        row.truncate(len);
        self.history.push(row);
        self.scroll_generation += 1;
        if self.history.len() > self.scrollback_max + TRIM_SLACK {
            self.trim_history();
        }
    }

    /// Drop the oldest history down to the cap, moving every stored line number with it.
    ///
    /// Absolute line numbers are history-relative, so trimming renumbers them: the marks are
    /// shifted here, and a host tracking anything else by absolute line (a text selection) works
    /// the shift out from `scroll_generation` against `history.len()`.
    fn trim_history(&mut self) {
        if self.history.len() <= self.scrollback_max {
            return;
        }
        let trim = (self.history.len() - self.scrollback_max) as i64;
        self.history.drain(0..trim as usize);
        self.marks.retain_mut(|m| {
            m.prompt_line -= trim;
            if m.command_line >= 0 {
                m.command_line -= trim;
            }
            if m.output_line >= 0 {
                m.output_line -= trim;
            }
            if m.end_line >= 0 {
                m.end_line -= trim;
            }
            m.prompt_line >= 0
        });
    }

    /// Set the scrollback cap. 0 disables history entirely — nothing is pushed and what is already
    /// stored is dropped, so the setting means the same thing whenever it is applied.
    pub fn set_scrollback_max(&mut self, max: usize) {
        self.scrollback_max = max;
        self.trim_history(); // a LOWER cap must take effect now, not at the next scroll
    }

    fn index(&mut self) {
        if self.cursor_row == self.scroll_bottom {
            self.scroll_region_up();
        } else if self.cursor_row < self.screen().rows() - 1 {
            self.cursor_row += 1;
        }
    }

    fn reverse_index(&mut self) {
        if self.cursor_row == self.scroll_top {
            self.scroll_region_down();
        } else if self.cursor_row > 0 {
            self.cursor_row -= 1;
        }
    }

    fn scroll_region_up(&mut self) {
        if !self.on_alt
            && self.scrollback_max > 0
            && self.scroll_top == 0
            && self.scroll_bottom == self.screen().rows() - 1
        {
            self.push_history();
        }
        // Sixel images (negative ids; not host-managed like Kitty) scroll up with the text.
        if !self.placements.is_empty() {
            let mut i = self.placements.len();
            while i > 0 {
                i -= 1;
                if self.placements[i].image_id < 0 {
                    let mut np = self.placements[i];
                    np.row -= 1;
                    if np.row + np.rows.max(1) as i64 <= 0 {
                        self.images.remove(&np.image_id);
                        self.placements.remove(i);
                    } else {
                        self.placements[i] = np;
                    }
                }
            }
        }
        let (top, bottom) = (self.scroll_top, self.scroll_bottom);
        let b = self.blank();
        self.scr().move_rows(top + 1, top, bottom - top);
        self.scr().fill_row(bottom, b);
    }

    fn scroll_region_down(&mut self) {
        let (top, bottom) = (self.scroll_top, self.scroll_bottom);
        let b = self.blank();
        self.scr().move_rows(top, top + 1, bottom - top);
        self.scr().fill_row(top, b);
    }

    fn set_scroll_region(&mut self, top: i64, bottom: i64) {
        let rows = self.screen().rows() as i64;
        let mut top = top.clamp(0, rows - 1);
        let mut bottom = bottom.clamp(0, rows - 1);
        if top >= bottom {
            top = 0;
            bottom = rows - 1;
        }
        self.scroll_top = top as usize;
        self.scroll_bottom = bottom as usize;
        self.cursor_row = 0;
        self.cursor_col = 0;
    }

    fn insert_lines(&mut self, n: i64) {
        if self.cursor_row < self.scroll_top || self.cursor_row > self.scroll_bottom {
            return;
        }
        let n = n
            .min((self.scroll_bottom - self.cursor_row + 1) as i64)
            .max(0) as usize;
        let shift = self.scroll_bottom - self.cursor_row + 1 - n;
        let row = self.cursor_row;
        let b = self.blank();
        if shift > 0 {
            self.scr().move_rows(row, row + n, shift);
        }
        for r in row..row + n {
            self.scr().fill_row(r, b);
        }
    }

    fn delete_lines(&mut self, n: i64) {
        if self.cursor_row < self.scroll_top || self.cursor_row > self.scroll_bottom {
            return;
        }
        let n = n
            .min((self.scroll_bottom - self.cursor_row + 1) as i64)
            .max(0) as usize;
        let shift = self.scroll_bottom - self.cursor_row + 1 - n;
        let row = self.cursor_row;
        let bottom = self.scroll_bottom;
        let b = self.blank();
        if shift > 0 {
            self.scr().move_rows(row + n, row, shift);
        }
        for r in (bottom + 1 - n)..=bottom {
            self.scr().fill_row(r, b);
        }
    }

    fn insert_chars(&mut self, n: i64) {
        let cols = self.screen().cols() as i64;
        let n = n.max(0);
        let b = self.blank();
        let (row, cur) = (self.cursor_row, self.cursor_col as i64);
        let mut c = cols - 1;
        while c >= cur + n {
            let src = self.screen().get(row, (c - n) as usize);
            self.scr().set(row, c as usize, src);
            c -= 1;
        }
        let to = (cur + n).min(cols);
        for c in cur..to {
            self.scr().set(row, c as usize, b);
        }
    }

    fn delete_chars(&mut self, n: i64) {
        let cols = self.screen().cols() as i64;
        let n = n.max(0);
        let b = self.blank();
        let (row, cur) = (self.cursor_row, self.cursor_col as i64);
        for c in cur..cols {
            let cell = if c + n < cols {
                self.screen().get(row, (c + n) as usize)
            } else {
                b
            };
            self.scr().set(row, c as usize, cell);
        }
    }

    // ---- erase ----

    fn erase_chars(&mut self, count: i64) {
        let cols = self.screen().cols() as i64;
        let to = (self.cursor_col as i64 + count.max(0)).min(cols);
        let b = self.blank();
        let row = self.cursor_row;
        for c in self.cursor_col as i64..to {
            self.scr().set(row, c as usize, b);
        }
    }

    fn erase_line(&mut self, mode: i32) {
        let from = if mode == 0 { self.cursor_col } else { 0 };
        let to = if mode == 1 {
            self.cursor_col
        } else {
            self.screen().cols() - 1
        };
        let b = self.blank();
        let row = self.cursor_row;
        let mut c = from;
        while c <= to {
            self.scr().set(row, c, b);
            c += 1;
        }
    }

    fn erase_display(&mut self, mode: i32) {
        let (rows, cols) = (self.screen().rows(), self.screen().cols());
        let b = self.blank();
        match mode {
            2 => {
                for r in 0..rows {
                    for c in 0..cols {
                        self.scr().set(r, c, b);
                    }
                }
            }
            0 => {
                self.erase_line(0);
                for r in self.cursor_row + 1..rows {
                    for c in 0..cols {
                        self.scr().set(r, c, b);
                    }
                }
            }
            1 => {
                self.erase_line(1);
                for r in 0..self.cursor_row {
                    for c in 0..cols {
                        self.scr().set(r, c, b);
                    }
                }
            }
            _ => {} // modes > 2 (e.g. 3): no-op, like the original
        }
    }

    // ---- modes / screens / cursor save ----

    fn set_private_mode(&mut self, params: &[i32], set: bool) {
        for &mode in params {
            match mode {
                1049 => {
                    if set {
                        self.save_cursor();
                        self.enter_alt_screen();
                    } else {
                        self.leave_alt_screen();
                        self.restore_cursor();
                    }
                }
                47 | 1047 => {
                    if set {
                        self.enter_alt_screen();
                    } else {
                        self.leave_alt_screen();
                    }
                }
                25 => self.cursor_visible = set,
                1000 => self.mouse_click = set,
                1002 => self.mouse_drag = set,
                1003 => self.mouse_motion = set,
                1006 => self.mouse_sgr = set,
                1016 => self.mouse_sgr_pixels = set,
                1004 => self.focus_reporting = set,
                2026 => self.synchronized_output = set,
                9001 => self.win32_input_mode = set,
                2004 => self.bracketed_paste = set,
                _ => self.push_action(HostAction::Unhandled {
                    kind: "MODE".into(),
                    detail: format!("?{mode} {}", if set { 'h' } else { 'l' }),
                }),
            }
        }
    }

    fn enter_alt_screen(&mut self) {
        if self.on_alt {
            return;
        }
        self.on_alt = true;
        self.alt.clear();
        self.placements.clear(); // images belong to a screen; start the alt screen clean
        self.cursor_row = 0;
        self.cursor_col = 0;
    }

    fn leave_alt_screen(&mut self) {
        if !self.on_alt {
            return;
        }
        self.on_alt = false;
        self.placements.clear(); // drop the alt screen's images
    }

    fn save_cursor(&mut self) {
        self.saved_row = self.cursor_row;
        self.saved_col = self.cursor_col;
    }

    fn restore_cursor(&mut self) {
        self.cursor_row = self.saved_row.min(self.screen().rows() - 1);
        self.cursor_col = self.saved_col.min(self.screen().cols() - 1);
    }

    // ---- SGR ----

    fn apply_sgr(&mut self, p: &[i32]) {
        if p.is_empty() {
            self.reset_pen();
            return;
        }
        let mut i = 0usize;
        while i < p.len() {
            let code = p[i];
            match code {
                0 => self.reset_pen(),
                1 => self.attrs |= attrs::BOLD,
                2 => self.attrs |= attrs::DIM,
                3 => self.attrs |= attrs::ITALIC,
                4 => self.attrs |= attrs::UNDERLINE,
                7 => self.attrs |= attrs::INVERSE,
                9 => self.attrs |= attrs::STRIKETHROUGH,
                22 => self.attrs &= !(attrs::BOLD | attrs::DIM),
                23 => self.attrs &= !attrs::ITALIC,
                24 => self.attrs &= !attrs::UNDERLINE,
                27 => self.attrs &= !attrs::INVERSE,
                29 => self.attrs &= !attrs::STRIKETHROUGH,
                30..=37 => {
                    self.fg = Color::from_index((code - 30) as u8);
                    self.fg_spec = ColorSpec::indexed((code - 30) as u8);
                }
                39 => {
                    self.fg = Color::DEFAULT_FOREGROUND;
                    self.fg_spec = ColorSpec::DEFAULT;
                }
                40..=47 => {
                    self.bg = Color::from_index((code - 40) as u8);
                    self.bg_spec = ColorSpec::indexed((code - 40) as u8);
                }
                49 => {
                    self.bg = Color::DEFAULT_BACKGROUND;
                    self.bg_spec = ColorSpec::DEFAULT;
                }
                90..=97 => {
                    self.fg = Color::from_index((code - 90 + 8) as u8);
                    self.fg_spec = ColorSpec::indexed((code - 90 + 8) as u8);
                }
                100..=107 => {
                    self.bg = Color::from_index((code - 100 + 8) as u8);
                    self.bg_spec = ColorSpec::indexed((code - 100 + 8) as u8);
                }
                38 => i = self.extended_color(p, i, true),
                48 => i = self.extended_color(p, i, false),
                _ => {}
            }
            i += 1;
        }
    }

    fn extended_color(&mut self, p: &[i32], i: usize, is_fg: bool) -> usize {
        if i + 1 >= p.len() {
            return i;
        }
        let mode = p[i + 1];
        if mode == 5 && i + 2 < p.len() {
            let idx = p[i + 2];
            // Out-of-range index: consume params, change nothing (matches the guarded C#).
            if (0..=255).contains(&idx) {
                let (c, s) = (Color::from_index(idx as u8), ColorSpec::indexed(idx as u8));
                if is_fg {
                    self.fg = c;
                    self.fg_spec = s;
                } else {
                    self.bg = c;
                    self.bg_spec = s;
                }
            }
            return i + 2;
        }
        if mode == 2 && i + 4 < p.len() {
            // Wrapping byte casts, matching C#'s unchecked (byte) conversion.
            let c = Color {
                r: p[i + 2] as u8,
                g: p[i + 3] as u8,
                b: p[i + 4] as u8,
            };
            let s = ColorSpec::from_rgb(c);
            if is_fg {
                self.fg = c;
                self.fg_spec = s;
            } else {
                self.bg = c;
                self.bg_spec = s;
            }
            return i + 4;
        }
        i + 1
    }

    fn reset_pen(&mut self) {
        self.fg = Color::DEFAULT_FOREGROUND;
        self.bg = Color::DEFAULT_BACKGROUND;
        self.fg_spec = ColorSpec::DEFAULT;
        self.bg_spec = ColorSpec::DEFAULT;
        self.attrs = attrs::NONE;
    }

    // ---- OSC ----

    fn strip_controls(s: &str) -> String {
        s.chars()
            .filter(|&c| c >= '\u{20}' && c != '\u{7f}' && !('\u{80}'..='\u{9f}').contains(&c))
            .collect()
    }

    fn ftcs_dispatch(&mut self, text: &str) {
        if self.on_alt || text.is_empty() {
            return;
        }
        let abs = self.history.len() as i64 + self.cursor_row as i64;
        match text.chars().next().unwrap().to_ascii_uppercase() {
            'A' => {
                if self.marks.last().map(|m| m.prompt_line) != Some(abs) {
                    self.marks.push(ShellMark {
                        prompt_line: abs,
                        command_line: -1,
                        output_line: -1,
                        end_line: -1,
                        exit_code: None,
                    });
                }
            }
            'B' => {
                if let Some(m) = self.marks.last_mut()
                    && m.command_line < 0
                {
                    m.command_line = abs;
                }
            }
            'C' => {
                if let Some(m) = self.marks.last_mut()
                    && m.output_line < 0
                {
                    m.output_line = abs;
                }
            }
            'D' => {
                if let Some(m) = self.marks.last_mut()
                    && m.end_line < 0
                {
                    m.end_line = abs;
                    if let Some(semi) = text.find(';')
                        && let Ok(ec) = text[semi + 1..].parse::<i32>()
                    {
                        m.exit_code = Some(ec);
                    }
                }
            }
            _ => {}
        }
        if self.marks.len() > 512 {
            let excess = self.marks.len() - 512;
            self.marks.drain(0..excess);
        }
    }

    // ---- kitty keyboard ----

    fn kitty_keyboard(&mut self, prefix: u8, params: &[i32]) {
        match prefix {
            b'?' => {
                // query — report the current flags back to the PTY (CSI ? <flags> u).
                let reply = format!("\u{1b}[?{}u", self.keyboard_flags());
                self.push_action(HostAction::Respond { reply });
            }
            b'>' => self.kbd_stack.push(*params.first().unwrap_or(&0)),
            b'=' => {
                let flags = *params.first().unwrap_or(&0);
                let mode = *params.get(1).unwrap_or(&1);
                let cur = self.keyboard_flags();
                let next = match mode {
                    2 => cur | flags,
                    3 => cur & !flags,
                    _ => flags,
                };
                self.kbd_stack.pop();
                self.kbd_stack.push(next);
            }
            b'<' => {
                let mut n = (*params.first().unwrap_or(&1)).max(1);
                while n > 0 && !self.kbd_stack.is_empty() {
                    self.kbd_stack.pop();
                    n -= 1;
                }
            }
            _ => {}
        }
    }

    // ---- mode dump / scrollback seed (integration surface, matches C#) ----

    pub fn dump_modes(&self) -> String {
        let mut s = String::new();
        if self.on_alt {
            s.push_str("\u{1b}[?1049h");
        }
        if !self.cursor_visible {
            s.push_str("\u{1b}[?25l");
        }
        if self.mouse_click {
            s.push_str("\u{1b}[?1000h");
        }
        if self.mouse_drag {
            s.push_str("\u{1b}[?1002h");
        }
        if self.mouse_motion {
            s.push_str("\u{1b}[?1003h");
        }
        if self.mouse_sgr {
            s.push_str("\u{1b}[?1006h");
        }
        if self.mouse_sgr_pixels {
            s.push_str("\u{1b}[?1016h");
        }
        if self.bracketed_paste {
            s.push_str("\u{1b}[?2004h");
        }
        if self.focus_reporting {
            s.push_str("\u{1b}[?1004h");
        }
        if self.win32_input_mode {
            s.push_str("\u{1b}[?9001h");
        }
        if self.cursor_shape != 0 {
            s.push_str(&format!("\u{1b}[{} q", self.cursor_shape));
        }
        if !self.title.is_empty() {
            s.push_str("\u{1b}]0;");
            s.push_str(&self.title);
            s.push('\u{7}');
        }
        if !self.cwd.is_empty() {
            s.push_str("\u{1b}]7;");
            s.push_str(&self.cwd);
            s.push('\u{7}');
        }
        s
    }

    pub fn seed_scrollback(&mut self, lines: &[&str]) {
        let cols = self.screen().cols();
        for line in lines {
            // Per-UTF-16-unit like the C# char indexing (BMP-only content expected here).
            // Store only the line's own cells (truncated to width); no trailing pad — reads past
            // the end return Cell::EMPTY, so the blank right margin of a restored buffer is free.
            let units: Vec<u16> = line.encode_utf16().collect();
            let len = units.len().min(cols);
            let mut row = Vec::with_capacity(len);
            for &ch in &units[..len] {
                row.push(Cell {
                    rune: ch as i32,
                    foreground: Color::DEFAULT_FOREGROUND,
                    background: Color::DEFAULT_BACKGROUND,
                    attributes: attrs::DIM,
                    width: 1,
                    fg_spec: ColorSpec::DEFAULT,
                    bg_spec: ColorSpec::DEFAULT,
                });
            }
            self.history.push(row);
        }
        if self.history.len() > self.scrollback_max + TRIM_SLACK {
            let trim = self.history.len() - self.scrollback_max;
            self.history.drain(0..trim);
        }
    }
}

/// The emulator + its parser, fed together (mirrors the C# object graph where
/// TerminalEmulator owns the VtParser and implements IParserPerformer itself).
pub struct Terminal {
    pub emu: Emulator,
    parser: VtParser,
}

impl Terminal {
    pub fn new(cols: usize, rows: usize) -> Terminal {
        Terminal {
            emu: Emulator::new(cols, rows),
            parser: VtParser::new(),
        }
    }

    pub fn feed(&mut self, bytes: &[u8]) {
        self.parser.feed(bytes, &mut self.emu);
    }
}

impl Performer for Emulator {
    fn print(&mut self, ch: u16) {
        // Surrogate re-pairing, mirroring TerminalEmulator.Print(char).
        if (0xD800..=0xDBFF).contains(&ch) {
            self.pending_high_surrogate = ch;
            return;
        }
        if (0xDC00..=0xDFFF).contains(&ch) {
            if self.pending_high_surrogate != 0 {
                let hs = self.pending_high_surrogate as i32;
                let cp = 0x10000 + ((hs - 0xD800) << 10) + (ch as i32 - 0xDC00);
                self.print_scalar(cp);
            }
            self.pending_high_surrogate = 0;
            return;
        }
        self.pending_high_surrogate = 0;
        self.print_scalar(ch as i32);
    }

    fn execute(&mut self, control: u8) {
        match control {
            13 => self.cursor_col = 0,
            10 => self.index(),
            8 => {
                if self.cursor_col > 0 {
                    self.cursor_col -= 1;
                }
            }
            9 => {
                let cols = self.screen().cols();
                self.cursor_col = (cols - 1).min((self.cursor_col / 8 + 1) * 8);
            }
            7 => self.push_action(HostAction::Bell), // BEL — ring the bell (host decides how)
            0 => {} // NUL — historical padding, deliberately ignored (would flood the tap)
            _ => self.push_action(HostAction::Unhandled {
                kind: "C0".into(),
                detail: format!("0x{control:02X}"),
            }),
        }
    }

    fn esc_dispatch(&mut self, ch: u8) {
        match ch {
            b'7' => self.save_cursor(),
            b'8' => self.restore_cursor(),
            b'D' => self.index(),
            b'M' => self.reverse_index(),
            b'E' => {
                self.cursor_col = 0;
                self.index();
            }
            _ => self.push_action(HostAction::Unhandled {
                kind: "ESC".into(),
                detail: (ch as char).to_string(),
            }),
        }
    }

    fn csi_dispatch(&mut self, ch: u8, params: &[i32], prefix: u8) {
        let p = |index: usize, def: i64| -> i64 {
            match params.get(index) {
                Some(&v) if v != 0 => v as i64,
                _ => def,
            }
        };

        if ch == b'u' && matches!(prefix, b'?' | b'>' | b'=' | b'<') {
            self.kitty_keyboard(prefix, params);
            return;
        }

        if prefix == b'?' {
            if ch == b'h' || ch == b'l' {
                self.set_private_mode(params, ch == b'h');
            } else if ch == b'p' && matches!(params.first(), Some(1016 | 2026)) {
                let mode = params[0];
                let set = if mode == 1016 {
                    self.mouse_sgr_pixels
                } else {
                    self.synchronized_output
                };
                // DECRQM — 1 means set, 2 means reset. Both modes are supported in either state.
                self.push_action(HostAction::Respond {
                    reply: format!("\u{1b}[?{mode};{}$y", if set { 1 } else { 2 }),
                });
            } else {
                self.push_action(HostAction::Unhandled {
                    kind: "CSI".into(),
                    detail: format!("? {} {}", join_params(params), ch as char),
                });
            }
            return;
        }

        let rows = self.screen().rows() as i64;
        let cols = self.screen().cols() as i64;
        match ch {
            b'H' | b'f' => {
                self.cursor_row = (p(0, 1) - 1).clamp(0, rows - 1) as usize;
                self.cursor_col = (p(1, 1) - 1).clamp(0, cols - 1) as usize;
            }
            b'A' => self.cursor_row = (self.cursor_row as i64 - p(0, 1)).max(0) as usize,
            b'B' => self.cursor_row = (self.cursor_row as i64 + p(0, 1)).min(rows - 1) as usize,
            b'C' => self.cursor_col = (self.cursor_col as i64 + p(0, 1)).min(cols - 1) as usize,
            b'D' => self.cursor_col = (self.cursor_col as i64 - p(0, 1)).max(0) as usize,
            b'G' => self.cursor_col = (p(0, 1) - 1).clamp(0, cols - 1) as usize,
            b'd' => self.cursor_row = (p(0, 1) - 1).clamp(0, rows - 1) as usize,
            b'J' => self.erase_display(*params.first().unwrap_or(&0)),
            b'K' => self.erase_line(*params.first().unwrap_or(&0)),
            b'X' => self.erase_chars(p(0, 1)),
            b'm' => self.apply_sgr(params),
            b'r' => {
                let bottom = match params.get(1) {
                    Some(&v) if v != 0 => v as i64 - 1,
                    _ => rows - 1,
                };
                self.set_scroll_region(p(0, 1) - 1, bottom);
            }
            b'L' => self.insert_lines(p(0, 1)),
            b'M' => self.delete_lines(p(0, 1)),
            b'@' => self.insert_chars(p(0, 1)),
            b'P' => self.delete_chars(p(0, 1)),
            b'S' => {
                for _ in 0..p(0, 1) {
                    self.scroll_region_up();
                }
            }
            b'T' => {
                for _ in 0..p(0, 1) {
                    self.scroll_region_down();
                }
            }
            b'q' if prefix == 0 => {
                // DECSCUSR (CSI Ps SP q) — cursor shape; the SP intermediate is dropped by the parser.
                let ps = *params.first().unwrap_or(&0);
                if (0..=6).contains(&ps) {
                    self.cursor_shape = ps;
                }
            }
            _ => {
                let pfx = if prefix == 0 {
                    String::new()
                } else {
                    format!("{} ", prefix as char)
                };
                self.push_action(HostAction::Unhandled {
                    kind: "CSI".into(),
                    detail: format!("{pfx}{} {}", join_params(params), ch as char),
                });
            }
        }
    }

    fn osc_dispatch(&mut self, command: i32, text: &str) {
        let text = Self::strip_controls(text);
        match command {
            0 | 2 => self.title = text,
            7 => self.cwd = text,
            // OSC 9;9 — ConEmu "set working directory". Crucially this is the cwd form that
            // conhost/ConPTY passes THROUGH to the client (it swallows OSC 7), so under a pty-host
            // it is the only way the live cwd reaches the terminal-side emulator. Other OSC 9
            // payloads (notify / 9;4 progress) fall through to the host-actions arm below.
            9 if text.starts_with("9;") => self.cwd = text[2..].trim_matches('"').to_string(),
            133 => self.ftcs_dispatch(&text),
            11 => {
                // OSC 11 — set/query the terminal background color (per-pane dynamic bg, agterm #240).
                if text.trim() == "?" {
                    let (r, g, b) = (
                        (self.dynamic_bg >> 16) & 0xFF,
                        (self.dynamic_bg >> 8) & 0xFF,
                        self.dynamic_bg & 0xFF,
                    );
                    let reply = format!(
                        "\u{1b}]11;rgb:{r:02x}{r:02x}/{g:02x}{g:02x}/{b:02x}{b:02x}\u{1b}\\"
                    );
                    self.push_action(HostAction::Respond { reply });
                } else if let Some(rgb) = parse_osc_color(&text) {
                    self.dynamic_bg = 0xFF00_0000 | rgb;
                }
            }
            111 => self.dynamic_bg = 0, // reset background to the theme default
            9 => {
                // OSC 9;<message> — body-only notification; OSC 9;4;<state>;<value> = ConEmu/WT progress.
                if let Some(rest) = text.strip_prefix("4;") {
                    let pp: Vec<&str> = rest.split(';').collect();
                    let state = pp.first().and_then(|s| s.parse::<i32>().ok()).unwrap_or(0);
                    let value = pp
                        .get(1)
                        .and_then(|s| s.parse::<i32>().ok())
                        .map(|v| v.clamp(0, 100))
                        .unwrap_or(0);
                    self.push_action(HostAction::Progress { state, value });
                } else {
                    self.push_action(HostAction::Notify {
                        title: String::new(),
                        body: text,
                    });
                }
            }
            777 => {
                // OSC 777 ; notify ; <title> ; <body>
                let parts: Vec<&str> = text.split(';').collect();
                if parts.len() >= 2 && parts[0] == "notify" {
                    let title = parts[1].to_string();
                    let body = if parts.len() >= 3 {
                        parts[2..].join(";")
                    } else {
                        String::new()
                    };
                    self.push_action(HostAction::Notify { title, body });
                }
            }
            52 => {
                // OSC 52 ; Pc ; Pd — program writes the clipboard (base64). "?" is a read-back query,
                // never answered; cap the payload as the managed core does.
                if let Some(semi) = text.find(';') {
                    let data = &text[semi + 1..];
                    if !data.is_empty() && data != "?" && data.len() <= 4_000_000 {
                        // Tolerate unpadded base64 (some emitters strip '='), like the managed
                        // core's PadRight before Convert.FromBase64String.
                        let mut padded = data.to_string();
                        while !padded.len().is_multiple_of(4) {
                            padded.push('=');
                        }
                        if let Some(bytes) = base64_decode(&padded) {
                            let decoded = String::from_utf8_lossy(&bytes).into_owned();
                            if !decoded.is_empty() {
                                self.push_action(HostAction::Clipboard { text: decoded });
                            }
                        }
                    }
                }
            }
            _ => {
                let detail = if text.chars().count() > 40 {
                    let head: String = text.chars().take(40).collect();
                    format!("{command};{head}…")
                } else {
                    format!("{command};{text}")
                };
                self.push_action(HostAction::Unhandled {
                    kind: "OSC".into(),
                    detail,
                });
            }
        }
    }

    fn apc_dispatch(&mut self, data: &str) {
        if !data.starts_with('G') {
            // only Kitty graphics (_G...); everything else is a debug-tap Unhandled.
            let detail = if data.chars().count() > 24 {
                let head: String = data.chars().take(24).collect();
                format!("{head}…")
            } else {
                data.to_string()
            };
            self.push_action(HostAction::Unhandled {
                kind: "APC".into(),
                detail,
            });
            return;
        }
        let body = &data[1..];
        let (control, payload) = match body.find(';') {
            Some(semi) => (&body[..semi], &body[semi + 1..]),
            None => (body, ""),
        };
        let keys = parse_kitty_keys(control);
        if self.kitty_chunks.is_empty() {
            self.kitty_keys = Some(keys.clone()); // first chunk carries the metadata
        }
        self.kitty_chunks.push_str(payload);
        let more = keys.get("m").map(|v| v == "1").unwrap_or(false);
        if more {
            return; // accumulate until the final chunk (m=0 / absent)
        }
        self.finalize_kitty_image();
    }

    fn dcs_dispatch(&mut self, payload: &[u8]) {
        if self.place_sixel(payload) {
            return;
        }
        // Non-sixel DCS (DECRQSS "$q", XTGETTCAP "+q", …): identify by its readable prefix.
        let n = payload.len().min(16);
        let head: String = payload[..n].iter().map(|&b| b as char).collect();
        self.push_action(HostAction::Unhandled {
            kind: "DCS".into(),
            detail: format!("{} bytes \"{head}\"", payload.len()),
        });
    }
}

fn parse_kitty_keys(control: &str) -> HashMap<String, String> {
    let mut d = HashMap::new();
    for pair in control.split(',') {
        if let Some(eq) = pair.find('=')
            && eq > 0
        {
            d.insert(pair[..eq].to_string(), pair[eq + 1..].to_string());
        }
    }
    d
}

/// Clamp an APC `f=` value to the three formats the Kitty protocol can carry on the wire
/// (24 RGB / 32 RGBA / 100 PNG), falling back to RGBA. `f=` is untrusted terminal output, and
/// the host-only `KittyFormat.Bgra` (132) must not be reachable from it: it would send the
/// renderer down the no-swizzle upload path with RGBA bytes. Mirrors C#
/// `KittyFormats.ParseWireFormat`.
fn parse_wire_format(f: i32) -> i32 {
    match f {
        24 | 32 | 100 => f,
        _ => 32,
    }
}

fn kitty_int(d: &HashMap<String, String>, key: &str, def: i32) -> i32 {
    d.get(key)
        .and_then(|v| v.parse::<i32>().ok())
        .unwrap_or(def)
}

/// Parse an OSC color spec (rgb:RR/GG/BB[BB…], #RRGGBB, #RGB) into 0x00RRGGBB. (agterm #240)
fn parse_osc_color(s: &str) -> Option<u32> {
    let s = s.trim();
    if let Some(rest) = s.strip_prefix("rgb:") {
        let parts: Vec<&str> = rest.split('/').collect();
        if parts.len() != 3 {
            return None;
        }
        let comp = |h: &str| -> Option<u32> {
            if h.is_empty() || h.len() > 4 {
                return None;
            }
            let raw = u32::from_str_radix(h, 16).ok()?;
            Some(raw * 255 / ((1u32 << (4 * h.len())) - 1)) // scale to 8-bit (xparse rule)
        };
        return Some((comp(parts[0])? << 16) | (comp(parts[1])? << 8) | comp(parts[2])?);
    }
    if let Some(h) = s.strip_prefix('#') {
        if h.len() == 6 {
            return u32::from_str_radix(h, 16).ok();
        }
        if h.len() == 3 {
            let v = u32::from_str_radix(h, 16).ok()?;
            let (r, g, b) = ((v >> 8) & 0xF, (v >> 4) & 0xF, v & 0xF);
            return Some(((r * 17) << 16) | ((g * 17) << 8) | (b * 17));
        }
    }
    None
}

/// Semicolon-join CSI parameters for the Unhandled debug tap (mirrors C#'s `string.Join(';', …)`).
fn join_params(params: &[i32]) -> String {
    params
        .iter()
        .map(|p| p.to_string())
        .collect::<Vec<_>>()
        .join(";")
}

impl Emulator {
    fn finalize_kitty_image(&mut self) {
        let keys = self.kitty_keys.take().unwrap_or_default();
        let b64 = core::mem::take(&mut self.kitty_chunks);

        let id = kitty_int(&keys, "i", 0);
        let format = parse_wire_format(kitty_int(&keys, "f", 32));
        let w = kitty_int(&keys, "s", 0);
        let h = kitty_int(&keys, "v", 0);
        let action = keys.get("a").map(String::as_str).unwrap_or("t");

        if action == "d" {
            if id != 0 {
                self.placements.retain(|p| p.image_id != id);
            } else {
                self.placements.clear();
            }
            return;
        }

        if !b64.is_empty() {
            let Some(bytes) = base64_decode(&b64) else {
                return;
            }; // malformed → drop, like the C# catch
            self.images.insert(
                id,
                KittyImage {
                    id,
                    format,
                    width: w,
                    height: h,
                    data: bytes,
                },
            );
        }

        if action == "T" || action == "p" {
            let cols = kitty_int(&keys, "c", 0);
            let rows = kitty_int(&keys, "r", 0);
            self.placements.retain(|p| p.image_id != id);
            self.placements.push(ImagePlacement {
                image_id: id,
                row: self.cursor_row as i64,
                col: self.cursor_col as i64,
                cols,
                rows,
                src_x: 0,
                src_y: 0,
                src_w: 0,
                src_h: 0,
            });
        }
    }

    fn place_sixel(&mut self, data: &[u8]) -> bool {
        let Some(s) = sixel::decode(data) else {
            return false;
        };
        if s.width == 0 || s.height == 0 {
            return false;
        }
        let id = self.sixel_seq;
        self.sixel_seq -= 1;
        self.images.insert(
            id,
            KittyImage {
                id,
                format: 32, // KittyFormat.Rgba
                width: s.width as i32,
                height: s.height as i32,
                data: s.rgba,
            },
        );
        let cols = (((s.width as i32) + self.cell_pixel_width - 1) / self.cell_pixel_width).max(1);
        let rows =
            (((s.height as i32) + self.cell_pixel_height - 1) / self.cell_pixel_height).max(1);
        self.placements.push(ImagePlacement {
            image_id: id,
            row: self.cursor_row as i64,
            col: self.cursor_col as i64,
            cols,
            rows,
            src_x: 0,
            src_y: 0,
            src_w: 0,
            src_h: 0,
        });
        for _ in 0..rows {
            self.index(); // advance the cursor below the image (sixel scrolling)
        }
        self.cursor_col = 0;
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn term(cols: usize, rows: usize, input: &[u8]) -> Terminal {
        let mut t = Terminal::new(cols, rows);
        t.feed(input);
        t
    }

    fn row_text(t: &Terminal, r: usize) -> String {
        let mut s = String::new();
        for c in 0..t.emu.screen().cols() {
            let cell = t.emu.screen().get(r, c);
            if cell.width == 0 {
                continue;
            }
            if let Some(ch) = char::from_u32(cell.rune as u32) {
                s.push(ch);
            }
        }
        s.trim_end().to_string()
    }

    #[test]
    fn print_wrap_and_scroll() {
        let t = term(5, 3, b"abcdefgh\r\nxyz");
        assert_eq!(row_text(&t, 0), "abcde");
        assert_eq!(row_text(&t, 1), "fgh");
        assert_eq!(row_text(&t, 2), "xyz");
    }

    #[test]
    fn wide_glyph_wraps_before_edge() {
        let t = term(4, 2, "ab\u{4E2D}".as_bytes()); // CJK at col 2-3 fits
        assert_eq!(t.emu.screen().get(0, 2).width, 2);
        assert_eq!(t.emu.screen().get(0, 3).width, 0);
        let t = term(4, 2, "abc\u{4E2D}".as_bytes()); // would straddle → wraps
        assert_eq!(t.emu.screen().get(1, 0).rune, 0x4E2D);
    }

    #[test]
    fn scroll_region_and_history() {
        let mut t = Terminal::new(4, 3);
        t.feed(b"1\r\n2\r\n3\r\n4\r\n5"); // full-height scrolling pushes history
        assert_eq!(t.emu.history_count(), 2);
        assert_eq!(t.emu.scroll_generation, 2);
        t.feed(b"\x1b[1;2r"); // region rows 0-1; homes cursor
        t.feed(b"\x1b[2;1Ha\nb\nc"); // scroll inside region only
        assert_eq!(t.emu.history_count(), 2); // partial region: no history
    }

    #[test]
    fn scrollback_rows_are_trimmed() {
        let mut t = Terminal::new(80, 3);
        t.feed(b"hi\r\n\r\n\r\n\r\n"); // "hi" then blank rows scroll into history
        assert!(t.emu.history_count() >= 1);
        // The "hi" row stores 2 cells, not 80; a blank row stores 0.
        assert_eq!(t.emu.history_row_stored_len(0), 2);
        // Reads still pad to full width — behaviour is unchanged.
        assert_eq!(t.emu.get_history_cell(0, 2), Cell::EMPTY);
        assert_eq!(t.emu.get_history_cell(0, 79), Cell::EMPTY);
    }

    #[test]
    fn bce_blank_survives_trim() {
        let mut t = Terminal::new(10, 2);
        // Paint a red background across the row (BCE), then scroll it off.
        t.feed(b"\x1b[41m\x1b[2K\r\n\r\n\r\n");
        // The coloured trailing blanks are NOT Cell::EMPTY, so they are preserved (not trimmed).
        assert_eq!(t.emu.history_row_stored_len(0), 10);
    }

    #[test]
    fn alt_screen_round_trip() {
        let mut t = Terminal::new(6, 2);
        t.feed(b"main\x1b[?1049h");
        assert!(t.emu.is_alt_screen());
        t.feed(b"ALT");
        t.feed(b"\x1b[?1049l");
        assert!(!t.emu.is_alt_screen());
        assert_eq!(row_text(&t, 0), "main");
    }

    #[test]
    fn sgr_pen_and_bce() {
        let mut t = Terminal::new(4, 2);
        t.feed(b"\x1b[41mX\x1b[K");
        let x = t.emu.screen().get(0, 0);
        assert_eq!(x.background, Color::from_index(1));
        let erased = t.emu.screen().get(0, 2);
        assert_eq!(erased.background, Color::from_index(1)); // BCE carries the pen bg
        assert_eq!(erased.rune, ' ' as i32);
    }

    #[test]
    fn osc_title_cwd_marks() {
        let mut t = Terminal::new(10, 3);
        t.feed(b"\x1b]2;my\x07\x1b]7;file://h/c\x07\x1b]133;A\x07ok\r\n\x1b]133;D;3\x07");
        assert_eq!(t.emu.title, "my");
        assert_eq!(t.emu.cwd, "file://h/c");
        assert_eq!(t.emu.marks().len(), 1);
        assert_eq!(t.emu.marks()[0].exit_code, Some(3));
    }

    #[test]
    fn resize_to_same_geometry_keeps_scroll_region() {
        // lite re-syncs both panes on every session switch, so most resize calls ask for the
        // geometry already in effect. Those must not clear DECSTBM: a full-screen TUI still
        // believes its margins are set and gets no SIGWINCH telling it to redraw.
        let mut t = Terminal::new(20, 10);
        t.feed(b"[3;8r");
        assert_eq!((t.emu.scroll_top(), t.emu.scroll_bottom()), (2, 7));

        t.emu.resize(20, 10);
        assert_eq!(
            (t.emu.scroll_top(), t.emu.scroll_bottom()),
            (2, 7),
            "a same-size resize must not reset the scroll region"
        );

        // A REAL size change still resets the margins (xterm behaviour).
        t.emu.resize(20, 12);
        assert_eq!((t.emu.scroll_top(), t.emu.scroll_bottom()), (0, 11));
    }

    #[test]
    fn sgr_pixels_mode_and_decrqm_track_set_and_reset() {
        let mut t = Terminal::new(20, 5);
        t.feed(b"\x1b[?1016$p");
        assert!(!t.emu.mouse_sgr_pixels);
        assert_eq!(
            t.emu.take_host_actions(),
            vec![HostAction::Respond {
                reply: "\x1b[?1016;2$y".into()
            }]
        );

        t.feed(b"\x1b[?1016h\x1b[?1016$p");
        assert!(t.emu.mouse_sgr_pixels);
        assert!(t.emu.dump_modes().contains("\x1b[?1016h"));
        assert_eq!(
            t.emu.take_host_actions(),
            vec![HostAction::Respond {
                reply: "\x1b[?1016;1$y".into()
            }]
        );

        t.feed(b"\x1b[?1016l");
        assert!(!t.emu.mouse_sgr_pixels);
        assert!(!t.emu.dump_modes().contains("\x1b[?1016h"));
    }
}
