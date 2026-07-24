//! Faithful port of Agwinterm.Core/ScreenBuffer.cs. Same operations, same
//! semantics (memmove row moves, whole-row fills, top-left-anchored resize);
//! the randomized differential oracle drives both implementations with
//! identical op sequences and compares every cell.

use crate::cell::Cell;

pub struct ScreenBuffer {
    cells: Vec<Cell>,
    cols: usize,
    rows: usize,
}

impl ScreenBuffer {
    /// cols/rows must be > 0 (the C# ctor throws; FFI guards before calling).
    pub fn new(cols: usize, rows: usize) -> ScreenBuffer {
        assert!(cols > 0 && rows > 0);
        ScreenBuffer { cells: vec![Cell::EMPTY; cols * rows], cols, rows }
    }

    pub fn cols(&self) -> usize { self.cols }
    pub fn rows(&self) -> usize { self.rows }

    pub fn get(&self, row: usize, col: usize) -> Cell {
        assert!(row < self.rows && col < self.cols);
        self.cells[row * self.cols + col]
    }

    pub fn set(&mut self, row: usize, col: usize, cell: Cell) {
        assert!(row < self.rows && col < self.cols);
        self.cells[row * self.cols + col] = cell;
    }

    pub fn clear(&mut self) {
        self.cells.fill(Cell::EMPTY);
    }

    /// Block-move `count` whole rows from `src_row` to `dst_row` (memmove semantics —
    /// overlapping regions are safe).
    pub fn move_rows(&mut self, src_row: usize, dst_row: usize, count: usize) {
        if count == 0 {
            return;
        }
        assert!(src_row < self.rows && dst_row < self.rows);
        assert!(src_row + count <= self.rows && dst_row + count <= self.rows);
        self.cells.copy_within(src_row * self.cols..(src_row + count) * self.cols, dst_row * self.cols);
    }

    pub fn fill_row(&mut self, row: usize, cell: Cell) {
        assert!(row < self.rows);
        self.cells[row * self.cols..(row + 1) * self.cols].fill(cell);
    }

    pub fn copy_row_to(&self, row: usize, dest: &mut [Cell]) {
        assert!(row < self.rows);
        let n = self.cols.min(dest.len());
        dest[..n].copy_from_slice(&self.cells[row * self.cols..row * self.cols + n]);
    }

    /// Top-left-anchored resize: surviving cells keep their position, new space is empty.
    pub fn resize(&mut self, cols: usize, rows: usize) {
        assert!(cols > 0 && rows > 0);
        let mut next = vec![Cell::EMPTY; cols * rows];
        let copy_cols = cols.min(self.cols);
        let copy_rows = rows.min(self.rows);
        for r in 0..copy_rows {
            next[r * cols..r * cols + copy_cols]
                .copy_from_slice(&self.cells[r * self.cols..r * self.cols + copy_cols]);
        }
        self.cells = next;
        self.cols = cols;
        self.rows = rows;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::cell::{attrs, Cell, Color};

    fn glyph(ch: char) -> Cell {
        Cell { rune: ch as i32, attributes: attrs::BOLD, ..Cell::EMPTY }
    }

    #[test]
    fn scroll_up_via_move_rows() {
        let mut s = ScreenBuffer::new(4, 3);
        s.fill_row(0, glyph('a'));
        s.fill_row(1, glyph('b'));
        s.fill_row(2, glyph('c'));
        s.move_rows(1, 0, 2);            // scroll up one line
        assert_eq!(s.get(0, 0).rune, 'b' as i32);
        assert_eq!(s.get(1, 3).rune, 'c' as i32);
    }

    #[test]
    fn resize_keeps_top_left() {
        let mut s = ScreenBuffer::new(3, 2);
        s.set(1, 2, glyph('x'));
        s.resize(5, 4);
        assert_eq!(s.get(1, 2).rune, 'x' as i32);
        assert_eq!(s.get(3, 4), Cell::EMPTY);
        s.resize(2, 1);
        assert_eq!(s.get(0, 0), Cell::EMPTY);
    }

    #[test]
    fn overlapping_move_is_memmove() {
        let mut s = ScreenBuffer::new(1, 4);
        for (r, ch) in ['a', 'b', 'c', 'd'].iter().enumerate() {
            s.fill_row(r, glyph(*ch));
        }
        s.move_rows(0, 1, 3);            // shift down over itself
        let got: Vec<i32> = (0..4).map(|r| s.get(r, 0).rune).collect();
        assert_eq!(got, vec!['a' as i32, 'a' as i32, 'b' as i32, 'c' as i32]);
        let _ = Color::DEFAULT_BACKGROUND; // silence unused import in cfg(test)
    }
}
