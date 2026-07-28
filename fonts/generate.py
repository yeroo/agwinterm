#!/usr/bin/env python3
"""AGWin Bitmap font-pack generator.

Rasterizes the pinned JetBrainsMono Nerd Font Mono at build time into compact
versioned binary packs (.agbf) at 14/16/18/20 px cell heights, so agwinterm-lite
can render bitmap text without touching vector fonts at runtime.

Box drawing (U+2500-257F), block elements (U+2580-259F) and the solid Powerline
separators (U+E0B0/B2/B4/B6, U+E0A0 column) are SYNTHESIZED from the exact cell
geometry instead of rasterized, so adjacent cells join without gaps at every size.

Deterministic: identical inputs -> byte-identical packs (no timestamps, glyphs
packed in code-point order). Run: python fonts/generate.py [--sizes 14,16,18,20]

.agbf v1 layout (little-endian):
  0   magic  "AGBF"
  4   u32    version (1)
  8   u32    strike px (nominal cell height)
  12  u16    cellW, u16 cellH
  16  u16    baseline (ascent), u16 underlinePos, u16 underlineTh, u16 strikePos
  24  u32    glyphCount
  28  u32    recordsOff, u32 atlasOff, u32 atlasLen
  40  u32    crc32 of records+atlas
  44  64s    family name (utf-8, zero-padded)
  108 64s    source id (font + version, utf-8, zero-padded)
  172 ...    records[glyphCount] (sorted by codepoint, binary-searchable), then atlas
  record (20 bytes): u32 codepoint, u32 atlasOff, i16 bearingX, i16 bearingY,
                     u16 w, u16 h, u8 cellWidth, u8 flags(1=synthesized), u16 pad
  atlas: 8-bit alpha, each glyph w*h bytes row-major at its atlasOff
"""
import argparse, hashlib, io, struct, sys, zlib
from pathlib import Path

from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent
SOURCES = ROOT / "sources"
GENERATED = ROOT / "generated"

SRC_TTF = "JetBrainsMonoNerdFontMono-Regular.ttf"
SRC_ID = "JetBrainsMono Nerd Font Mono v3.4.0 (JetBrains Mono 2.304)"
SRC_SHA256 = "ef552a3e638f25125c6ad4c51176a6adcdce295ab1d2ffacf0db060caf8c1582"  # JetBrainsMono.tar.xz
FAMILY = "AGWin Bitmap"

BOX = range(0x2500, 0x2580)
BLOCKS = range(0x2580, 0x25A0)
PL_SOLID = {0xE0B0, 0xE0B2}          # solid triangular Powerline separators
PL_ROUND = {0xE0B4, 0xE0B6}          # rounded (half-circle) separators


def parse_manifest(path):
    cps = set()
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue
        kind, _, arg = line.partition(" ")
        arg = arg.strip()
        if kind == "RANGE":
            lo, hi = (int(x, 16) for x in arg.split("-"))
            cps.update(range(lo, hi + 1))
        elif kind == "CP":
            cps.add(int(arg, 16))
        else:
            raise SystemExit(f"manifest: bad line: {raw!r}")
    return cps


def cell_geometry(ttf_path, nominal):
    """Largest em size whose ascent+descent fits the nominal cell height."""
    font = TTFont(ttf_path)
    hhea, upm = font["hhea"], font["head"].unitsPerEm
    asc, desc = hhea.ascent, -hhea.descent
    em = nominal
    while em > 4:
        a = round(asc * em / upm)
        d = round(desc * em / upm)
        if a + d <= nominal:
            break
        em -= 1
    advance = font["hmtx"]["x"][0]  # 'x' advance == the mono advance
    cellw = round(advance * em / upm)
    post = font["post"]
    upos = round(-post.underlinePosition * em / upm) or 1
    uth = max(1, round(post.underlineThickness * em / upm))
    return em, cellw, round(asc * em / upm), round(desc * em / upm), upos, uth


def synth_box(cp, w, h):
    """Box drawing / blocks / Powerline drawn on the exact cell grid (edge-to-edge)."""
    img = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(img)
    cx, cy = w // 2, h // 2
    lt = max(1, h // 14)                       # light stroke
    hv = max(lt + 1, h // 7)                   # heavy stroke
    def hline(x0, x1, th=lt, y=None):
        if x1 - 1 >= x0: d.rectangle([x0, (y or cy) - th // 2, x1 - 1, (y or cy) - th // 2 + th - 1], fill=255)
    def vline(y0, y1, th=lt, x=None):
        if y1 - 1 >= y0: d.rectangle([(x or cx) - th // 2, y0, (x or cx) - th // 2 + th - 1, y1 - 1], fill=255)
    C = cp
    if C in BLOCKS:  # block elements: exact fractional fills
        if C == 0x2588: d.rectangle([0, 0, w - 1, h - 1], fill=255)
        elif 0x2581 <= C <= 0x2587: d.rectangle([0, h - (C - 0x2580) * h // 8, w - 1, h - 1], fill=255)
        elif C == 0x2580: d.rectangle([0, 0, w - 1, h // 2 - 1], fill=255)
        elif 0x2589 <= C <= 0x258F: d.rectangle([0, 0, max(1, (0x2590 - C) * w // 8) - 1, h - 1], fill=255)
        elif C == 0x2590: d.rectangle([w // 2, 0, w - 1, h - 1], fill=255)
        elif C in (0x2591, 0x2592, 0x2593):    # shades: ordered dither patterns
            level = {0x2591: 1, 0x2592: 2, 0x2593: 3}[C]
            for y in range(h):
                for x in range(w):
                    if (x + 2 * y) % 4 < level: img.putpixel((x, y), 255)
        elif C == 0x2594: d.rectangle([0, 0, w - 1, h // 8 - 1], fill=255)
        elif C == 0x2595: d.rectangle([w - max(1, w // 8), 0, w - 1, h - 1], fill=255)
        elif 0x2596 <= C <= 0x259F:            # quadrants
            q = {0x2596: "bl", 0x2597: "br", 0x2598: "tl", 0x2599: "tl bl br", 0x259A: "tl br",
                 0x259B: "tl tr bl", 0x259C: "tl tr br", 0x259D: "tr", 0x259E: "tr bl", 0x259F: "tr bl br"}[C]
            for part in q.split():
                x0, x1 = (0, w // 2 - 1) if "l" in part else (w // 2, w - 1)
                y0, y1 = (0, h // 2 - 1) if "t" in part else (h // 2, h - 1)
                d.rectangle([x0, y0, x1, y1], fill=255)
        return img
    if C in PL_SOLID:                          # gap-free triangles using full cell
        pts = [(0, 0), (w, cy), (0, h)] if C == 0xE0B0 else [(w, 0), (0, cy), (w, h)]
        d.polygon(pts, fill=255)
        return img
    if C in PL_ROUND:
        box = [-w, 0, w - 1, h - 1] if C == 0xE0B4 else [0, 0, 2 * w - 1, h - 1]
        d.ellipse(box, fill=255)
        return img
    # box drawing: build from up/down/left/right stroke flags
    L = {}
    def seg(up=0, dn=0, lf=0, rt=0):
        if lf: hline(0, cx + max(lt, hv) // 2 + 1, lf)
        if rt: hline(cx - max(lt, hv) // 2, w, rt)
        if up: vline(0, cy + max(lt, hv) // 2 + 1, up)
        if dn: vline(cy - max(lt, hv) // 2, h, dn)
    t = {0x2500: (0, 0, lt, lt), 0x2501: (0, 0, hv, hv), 0x2502: (lt, lt, 0, 0), 0x2503: (hv, hv, 0, 0),
         0x250C: (0, lt, 0, lt), 0x250F: (0, hv, 0, hv), 0x2510: (0, lt, lt, 0), 0x2513: (0, hv, hv, 0),
         0x2514: (lt, 0, 0, lt), 0x2517: (hv, 0, 0, hv), 0x2518: (lt, 0, lt, 0), 0x251B: (hv, 0, hv, 0),
         0x251C: (lt, lt, 0, lt), 0x2523: (hv, hv, 0, hv), 0x2524: (lt, lt, lt, 0), 0x252B: (hv, hv, hv, 0),
         0x252C: (0, lt, lt, lt), 0x2533: (0, hv, hv, hv), 0x2534: (lt, 0, lt, lt), 0x253B: (hv, 0, hv, hv),
         0x253C: (lt, lt, lt, lt), 0x254B: (hv, hv, hv, hv),
         0x2574: (0, 0, lt, 0), 0x2575: (lt, 0, 0, 0), 0x2576: (0, 0, 0, lt), 0x2577: (0, lt, 0, 0)}
    if C in t:
        seg(*t[C]); return img
    if 0x2550 <= C <= 0x256C:
        # Double box set, each glyph as explicit stroke geometry (corners are CORNERS, not
        # crossings — Far Manager's frame is the acceptance test). d = half-gap between the pair.
        dd = max(2, h // 8)
        yO, yI, xO, xI = cy - dd, cy + dd, cx - dd, cx + dd   # outer/inner double coordinates
        def H(y, x0, x1): hline(max(0, x0), min(w, x1 + 1), lt, y)
        def V(x, y0, y1): vline(max(0, y0), min(h, y1 + 1), lt, x)
        T = {
            0x2550: [("H", yO, 0, w), ("H", yI, 0, w)],                                   # ═
            0x2551: [("V", xO, 0, h), ("V", xI, 0, h)],                                   # ║
            0x2552: [("H", yO, cx, w), ("H", yI, cx, w), ("V", cx, yO, h)],               # ╒
            0x2553: [("H", cy, xO, w), ("V", xO, cy, h), ("V", xI, cy, h)],               # ╓
            0x2554: [("H", yO, xO, w), ("H", yI, xI, w), ("V", xO, yO, h), ("V", xI, yI, h)],  # ╔
            0x2555: [("H", yO, 0, cx), ("H", yI, 0, cx), ("V", cx, yO, h)],               # ╕
            0x2556: [("H", cy, 0, xI), ("V", xO, cy, h), ("V", xI, cy, h)],               # ╖
            0x2557: [("H", yO, 0, xI), ("H", yI, 0, xO), ("V", xI, yO, h), ("V", xO, yI, h)],  # ╗
            0x2558: [("H", yO, cx, w), ("H", yI, cx, w), ("V", cx, 0, yI)],               # ╘
            0x2559: [("H", cy, xO, w), ("V", xO, 0, cy), ("V", xI, 0, cy)],               # ╙
            0x255A: [("H", yI, xO, w), ("H", yO, xI, w), ("V", xO, 0, yI), ("V", xI, 0, yO)],  # ╚
            0x255B: [("H", yO, 0, cx), ("H", yI, 0, cx), ("V", cx, 0, yI)],               # ╛
            0x255C: [("H", cy, 0, xI), ("V", xO, 0, cy), ("V", xI, 0, cy)],               # ╜
            0x255D: [("H", yI, 0, xI), ("H", yO, 0, xO), ("V", xI, 0, yI), ("V", xO, 0, yO)],  # ╝
            0x255E: [("V", cx, 0, h), ("H", yO, cx, w), ("H", yI, cx, w)],                # ╞
            0x255F: [("V", xO, 0, h), ("V", xI, 0, h), ("H", cy, xI, w)],                 # ╟
            0x2560: [("V", xO, 0, h), ("V", xI, 0, yO), ("V", xI, yI, h),
                     ("H", yO, xI, w), ("H", yI, xI, w)],                                 # ╠
            0x2561: [("V", cx, 0, h), ("H", yO, 0, cx), ("H", yI, 0, cx)],                # ╡
            0x2562: [("V", xO, 0, h), ("V", xI, 0, h), ("H", cy, 0, xO)],                 # ╢
            0x2563: [("V", xI, 0, h), ("V", xO, 0, yO), ("V", xO, yI, h),
                     ("H", yO, 0, xO), ("H", yI, 0, xO)],                                 # ╣
            0x2564: [("H", yO, 0, w), ("H", yI, 0, w), ("V", cx, yI, h)],                 # ╤
            0x2565: [("H", cy, 0, w), ("V", xO, cy, h), ("V", xI, cy, h)],                # ╥
            0x2566: [("H", yO, 0, w), ("H", yI, 0, xO), ("H", yI, xI, w),
                     ("V", xO, yI, h), ("V", xI, yI, h)],                                 # ╦
            0x2567: [("H", yI, 0, w), ("H", yO, 0, w), ("V", cx, 0, yO)],                 # ╧
            0x2568: [("H", cy, 0, w), ("V", xO, 0, cy), ("V", xI, 0, cy)],                # ╨
            0x2569: [("H", yI, 0, w), ("H", yO, 0, xO), ("H", yO, xI, w),
                     ("V", xO, 0, yO), ("V", xI, 0, yO)],                                 # ╩
            0x256A: [("V", cx, 0, h), ("H", yO, 0, w), ("H", yI, 0, w)],                  # ╪
            0x256B: [("H", cy, 0, w), ("V", xO, 0, h), ("V", xI, 0, h)],                  # ╫
            0x256C: [("V", xO, 0, yO), ("V", xI, 0, yO), ("V", xO, yI, h), ("V", xI, yI, h),
                     ("H", yO, 0, xO), ("H", yI, 0, xO), ("H", yO, xI, w), ("H", yI, xI, w)],  # ╬
        }
        for op in T.get(C, []):
            if op[0] == "H": H(op[1], op[2], op[3])
            else: V(op[1], op[2], op[3])
        return img
    if 0x256D <= C <= 0x2570:  # rounded corners: quarter arcs
        r = min(cx, cy)
        arcbox = {0x256D: [cx, cy, cx + 2 * r, cy + 2 * r], 0x256E: [cx - 2 * r, cy, cx, cy + 2 * r],
                  0x256F: [cx - 2 * r, cy - 2 * r, cx, cy], 0x2570: [cx, cy - 2 * r, cx + 2 * r, cy]}[C]
        start = {0x256D: 180, 0x256E: 270, 0x256F: 0, 0x2570: 90}[C]
        d.arc(arcbox, start, start + 90, fill=255, width=lt)
        if C in (0x256D, 0x256E): vline(cy + r, h)
        else: vline(0, cy - r + 1)
        if C in (0x256D, 0x2570): hline(cx + r, w)
        else: hline(0, cx - r + 1)
        return img
    if C == 0x2571: d.line([(0, h - 1), (w - 1, 0)], fill=255, width=lt); return img
    if C == 0x2572: d.line([(0, 0), (w - 1, h - 1)], fill=255, width=lt); return img
    if C == 0x2573:
        d.line([(0, h - 1), (w - 1, 0)], fill=255, width=lt)
        d.line([(0, 0), (w - 1, h - 1)], fill=255, width=lt); return img
    if 0x2504 <= C <= 0x250B or 0x254C <= C <= 0x254F:  # dashed
        horiz = C in (0x2504, 0x2505, 0x2508, 0x2509, 0x254C, 0x254D)
        th = hv if C in (0x2505, 0x2509, 0x254D, 0x254F) else lt
        n = 3 if C in (0x2504, 0x2505, 0x2506, 0x2507, 0x254C, 0x254D) else 4
        if horiz:
            for i in range(n): hline(i * w // n, i * w // n + max(1, w // n - 1), th)
        else:
            for i in range(n): vline(i * h // n, i * h // n + max(1, h // n - 1), th)
        return img
    seg(lt, lt, lt, lt)  # anything else in the box range: safe cross
    return img


def rasterize(cps, ttf_path, nominal):
    em, cellw, asc, desc, upos, uth = cell_geometry(ttf_path, nominal)
    cellh = nominal
    pil = ImageFont.truetype(str(ttf_path), em)
    cmap = TTFont(ttf_path).getBestCmap()
    records, atlas = [], bytearray()
    for cp in sorted(cps):
        synth = cp in BOX or cp in BLOCKS or cp in PL_SOLID or cp in PL_ROUND or cp == 0xE0A0
        if not synth and cp not in cmap:
            continue
        if synth:
            img = synth_box(cp, cellw, cellh) if cp != 0xE0A0 else synth_box(0x2502, cellw, cellh)
            bx = by = 0
        else:
            ch = chr(cp)
            img8 = Image.new("L", (cellw * 3, cellh * 3), 0)
            d = ImageDraw.Draw(img8)
            d.text((cellw, asc), ch, font=pil, fill=255, anchor="ls")
            bbox = img8.getbbox()
            if bbox is None:  # blank (e.g. space): zero-size record keeps lookup complete
                records.append((cp, len(atlas), 0, 0, 0, 0, 1, 0))
                continue
            x0, y0, x1, y1 = bbox
            img = img8.crop(bbox)
            bx, by = x0 - cellw, y0 - 0  # bearings vs cell origin (top-left)
        wpx, hpx = img.size
        records.append((cp, len(atlas), bx, by, wpx, hpx, 1, 1 if synth else 0))
        atlas.extend(img.tobytes())
    return records, bytes(atlas), dict(em=em, cellw=cellw, cellh=cellh, asc=asc, upos=upos, uth=uth)


def write_pack(path, nominal, records, atlas, geo):
    recs = b"".join(struct.pack("<IIhhHHBBH", cp, off, bx, by, w, h, cw, fl, 0)
                    for cp, off, bx, by, w, h, cw, fl in records)
    crc = zlib.crc32(recs + atlas) & 0xFFFFFFFF
    hdr = struct.pack("<4sIIHHHHHHI III I",
                      b"AGBF", 1, nominal, geo["cellw"], geo["cellh"],
                      geo["asc"], geo["asc"] + geo["upos"], geo["uth"], geo["asc"] - geo["cellh"] * 3 // 10,
                      len(records), 172, 172 + len(recs), len(atlas), crc)
    hdr += FAMILY.encode().ljust(64, b"\0") + SRC_ID.encode().ljust(64, b"\0")
    assert len(hdr) == 172, len(hdr)
    path.write_bytes(hdr + recs + atlas)
    return crc


def preview(path, records, atlas, geo, nominal):
    text_rows = ["The quick brown fox jumps over the lazy dog. 0123456789 Il1 O0 {}[]()<>/\\|_",
                 "Съешь ещё этих мягких французских булок, да выпей чаю.",
                 "┌────────────┬──────────┐ ╭───────╮ ░░▒▒▓▓████ ▀▄▌▐",
                 "│ AGWin OK   │ main   │ ╰───────╯      "]
    idx = {r[0]: r for r in records}
    W, H = geo["cellw"] * 80, geo["cellh"] * (len(text_rows) + 1)
    img = Image.new("L", (W, H), 0)
    for row, line in enumerate(text_rows):
        for col, ch in enumerate(line[:79]):
            r = idx.get(ord(ch))
            if not r or r[4] == 0:
                continue
            cp, off, bx, by, w, h, cw, fl = r
            g = Image.frombytes("L", (w, h), atlas[off:off + w * h])
            img.paste(g, (col * geo["cellw"] + bx, row * geo["cellh"] + by), g)
    img.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sizes", default="14,16,18,20")
    ap.add_argument("--src", default=str(SOURCES / SRC_TTF))
    args = ap.parse_args()
    src = Path(args.src)
    if not src.exists():
        raise SystemExit(f"source font missing: {src}\n(download JetBrainsMono.tar.xz v3.4.0, "
                         f"sha256 {SRC_SHA256}, extract {SRC_TTF} into fonts/sources/)")
    cps = parse_manifest(ROOT / "manifests" / "agwin-bitmap.txt")
    GENERATED.mkdir(exist_ok=True)
    for nominal in (int(s) for s in args.sizes.split(",")):
        records, atlas, geo = rasterize(cps, src, nominal)
        out = GENERATED / f"agwin-bitmap-{nominal}.agbf"
        crc = write_pack(out, nominal, records, atlas, geo)
        preview(GENERATED / f"agwin-bitmap-{nominal}-preview.png", records, atlas, geo, nominal)
        # validation: sorted unique index, box+powerline+cyrillic coverage, mono cell widths
        cps_out = [r[0] for r in records]
        assert cps_out == sorted(set(cps_out)), "index not sorted/unique"
        for need in (0x2500, 0x2502, 0x253C, 0x2588, 0x2591, 0xE0B0, 0x0410, 0x044F, 0xFFFD):
            assert need in set(cps_out), f"missing required glyph U+{need:04X}"
        assert all(r[6] == 1 for r in records), "non-single-cell glyph in v1"
        print(f"agwin-bitmap-{nominal}.agbf: {len(records)} glyphs, cell {geo['cellw']}x{geo['cellh']} "
              f"(em {geo['em']}), atlas {len(atlas)//1024} KiB, crc {crc:08x}, "
              f"file {out.stat().st_size//1024} KiB")


if __name__ == "__main__":
    main()
