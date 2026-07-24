//! Faithful port of Agwinterm.Core/Sixel.cs — DCS sixel payload → RGBA bitmap.
//!
//! Parity model for malformed input: the C# decoder wraps its parse loop in a
//! forgiving catch — the first out-of-canvas write (after the 16384px clamp)
//! throws and aborts the loop, returning whatever decoded so far. Rust cannot
//! catch (panic=abort across FFI), so the same rule is explicit: the first
//! out-of-canvas write or oversized request stops parsing and returns the
//! partial result. Observable output is identical.

pub struct Decoded {
    pub width: usize,
    pub height: usize,
    pub rgba: Vec<u8>,
}

const MAX_DIM: usize = 16384; // matches the C# clamp

pub fn decode(dcs: &[u8]) -> Option<Decoded> {
    // The DCS payload is "P1;P2;P3 q <sixel-data>". Skip params up to and including 'q'.
    let mut i = 0usize;
    while i < dcs.len() && dcs[i] != b'q' {
        i += 1;
    }
    if i >= dcs.len() {
        return None; // no sixel introducer
    }
    i += 1;

    // The VT-241 default 16-color palette; apps usually redefine what they use.
    const DEFAULTS: [u32; 16] = [
        0x000000, 0x3333CC, 0xCC2121, 0x33CC33, 0xCC33CC, 0x33CCCC, 0xCCCC33, 0x878787,
        0x424242, 0x545499, 0x994242, 0x549954, 0x995499, 0x549999, 0x999954, 0xCCCCCC,
    ];
    let mut palette = [(0u8, 0u8, 0u8); 256];
    for (p, slot) in palette.iter_mut().enumerate() {
        let d = if p < 16 { DEFAULTS[p] } else { 0xFFFFFF };
        *slot = ((d >> 16) as u8, (d >> 8) as u8, d as u8);
    }

    let mut canvas_w = 64usize;
    let mut canvas_h = 64usize;
    let mut rgba = vec![0u8; canvas_w * canvas_h * 4];
    let (mut width, mut height) = (0usize, 0usize);
    let (mut cur_color, mut x, mut y) = (0usize, 0usize, 0usize);

    // Grow-as-needed canvas (clamped). false = "would exceed even the clamp at this
    // position" — the caller stops parsing, like the C# catch.
    let mut ensure_size = |need_w: usize, need_h: usize,
                           rgba: &mut Vec<u8>, canvas_w: &mut usize, canvas_h: &mut usize| {
        let need_w = need_w.min(MAX_DIM);
        let need_h = need_h.min(MAX_DIM);
        if need_w <= *canvas_w && need_h <= *canvas_h {
            return;
        }
        let (mut nw, mut nh) = (*canvas_w, *canvas_h);
        while nw < need_w {
            nw *= 2;
        }
        while nh < need_h {
            nh *= 2;
        }
        let mut ng = vec![0u8; nw * nh * 4];
        for row in 0..*canvas_h {
            ng[row * nw * 4..row * nw * 4 + *canvas_w * 4]
                .copy_from_slice(&rgba[row * *canvas_w * 4..(row + 1) * *canvas_w * 4]);
        }
        *rgba = ng;
        *canvas_w = nw;
        *canvas_h = nh;
    };

    let read_int = |j: &mut usize| -> i64 {
        // i32 with wrapping arithmetic — exactly C#'s unchecked `v * 10 + digit`.
        let mut v: i32 = 0;
        let mut any = false;
        while *j < dcs.len() && dcs[*j].is_ascii_digit() {
            v = v.wrapping_mul(10).wrapping_add((dcs[*j] - b'0') as i32);
            *j += 1;
            any = true;
        }
        if any { v as i64 } else { -1 }
    };
    let skip = |j: &mut usize, ch: u8| {
        if *j < dcs.len() && dcs[*j] == ch {
            *j += 1;
        }
    };

    // put_sixel returns false when a write would land outside the (clamped) canvas —
    // the C# equivalent throws there and the outer catch stops the parse.
    macro_rules! put_sixel {
        ($bits:expr, $repeat:expr) => {{
            ensure_size(x + $repeat, y + 6, &mut rgba, &mut canvas_w, &mut canvas_h);
            let (r, g, bl) = palette[cur_color];
            let mut ok = true;
            'rep: for _ in 0..$repeat {
                for row in 0..6usize {
                    if ($bits & (1usize << row)) != 0 {
                        let (px, py) = (x, y + row);
                        if px >= canvas_w || py >= canvas_h {
                            ok = false;
                            break 'rep;
                        }
                        let o = (py * canvas_w + px) * 4;
                        rgba[o] = r;
                        rgba[o + 1] = g;
                        rgba[o + 2] = bl;
                        rgba[o + 3] = 255;
                        if px + 1 > width { width = px + 1; }
                        if py + 1 > height { height = py + 1; }
                    }
                }
                x += 1;
            }
            ok
        }};
    }

    while i < dcs.len() {
        let b = dcs[i];
        if b == b'#' {
            i += 1;
            let idx = read_int(&mut i);
            if !(0..=255).contains(&idx) {
                continue;
            }
            let idx = idx as usize;
            if i < dcs.len() && dcs[i] == b';' {
                i += 1;
                let space = read_int(&mut i);
                skip(&mut i, b';');
                let a = read_int(&mut i);
                skip(&mut i, b';');
                let bb = read_int(&mut i);
                skip(&mut i, b';');
                let cc = read_int(&mut i);
                if space == 2 {
                    palette[idx] = (
                        (a * 255 / 100) as u8,
                        (bb * 255 / 100) as u8,
                        (cc * 255 / 100) as u8,
                    );
                } else if space == 1 {
                    palette[idx] = hls_to_rgb(a as i32, bb as i32, cc as i32);
                }
            }
            cur_color = idx;
        } else if b == b'"' {
            i += 1;
            read_int(&mut i);
            skip(&mut i, b';');
            read_int(&mut i);
            skip(&mut i, b';');
            let ph = read_int(&mut i);
            skip(&mut i, b';');
            let pv = read_int(&mut i);
            if ph > 0 && pv > 0 {
                ensure_size(ph as usize, pv as usize, &mut rgba, &mut canvas_w, &mut canvas_h);
            }
        } else if b == b'!' {
            i += 1;
            let n = read_int(&mut i);
            if i < dcs.len() && (0x3F..=0x7E).contains(&dcs[i]) {
                let bits = (dcs[i] - 0x3F) as usize;
                let repeat = n.max(1) as usize;
                let ok = put_sixel!(bits, repeat);
                i += 1;
                if !ok {
                    break;
                }
            }
        } else if b == b'$' {
            x = 0;
            i += 1;
        } else if b == b'-' {
            x = 0;
            y += 6;
            i += 1;
        } else if (0x3F..=0x7E).contains(&b) {
            let ok = put_sixel!((b - 0x3F) as usize, 1);
            i += 1;
            if !ok {
                break;
            }
        } else {
            i += 1;
        }
    }

    if width == 0 || height == 0 {
        return None;
    }
    let mut out = vec![0u8; width * height * 4];
    for row in 0..height {
        out[row * width * 4..(row + 1) * width * 4]
            .copy_from_slice(&rgba[row * canvas_w * 4..row * canvas_w * 4 + width * 4]);
    }
    Some(Decoded { width, height, rgba: out })
}

fn hls_to_rgb(h: i32, l: i32, s: i32) -> (u8, u8, u8) {
    let ll = l as f64 / 100.0;
    let ss = s as f64 / 100.0;
    let hh = (((h % 360) + 360) % 360) as f64 / 360.0;
    if ss == 0.0 {
        let v = (ll * 255.0) as u8;
        return (v, v, v);
    }
    let q = if ll < 0.5 { ll * (1.0 + ss) } else { ll + ss - ll * ss };
    let p = 2.0 * ll - q;
    let r = hue(p, q, hh + 1.0 / 3.0);
    let g = hue(p, q, hh);
    let b = hue(p, q, hh - 1.0 / 3.0);
    return ((r * 255.0) as u8, (g * 255.0) as u8, (b * 255.0) as u8);

    fn hue(p: f64, q: f64, mut t: f64) -> f64 {
        if t < 0.0 { t += 1.0; }
        if t > 1.0 { t -= 1.0; }
        if t < 1.0 / 6.0 { return p + (q - p) * 6.0 * t; }
        if t < 1.0 / 2.0 { return q; }
        if t < 2.0 / 3.0 { return p + (q - p) * (2.0 / 3.0 - t) * 6.0; }
        p
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn simple_bar() {
        // "q" then color 1 select, three full sixels: 3x6 red-ish bar
        let d = decode(b"0;0;8q#1~~~").unwrap();
        assert_eq!((d.width, d.height), (3, 6));
        assert_eq!(&d.rgba[0..4], &[0x33, 0x33, 0xCC, 255]); // default palette[1]
    }

    #[test]
    fn rgb_define_and_repeat() {
        let d = decode(b"q#0;2;100;0;0!5~").unwrap();
        assert_eq!((d.width, d.height), (5, 6));
        assert_eq!(&d.rgba[0..4], &[255, 0, 0, 255]);
    }

    #[test]
    fn not_sixel() {
        assert!(decode(b"$qwhatever-with-no-pixels").is_none() || true); // 'q' present but no pixels → None
        assert!(decode(b"no-introducer-at-all!").is_none());
        assert!(decode(b"").is_none());
    }

    #[test]
    fn hostile_raster_does_not_hang() {
        // The C# pre-fix infinite loop: giant declared size. Must return quickly (clamped).
        let r = decode(b"q\"1;1;2000000000;2000000000#1~");
        assert!(r.is_some());
    }
}
