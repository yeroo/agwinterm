# AGWin Bitmap — build-time raster font packs for agwinterm-lite

Lite targets slow/old machines; the goal is text rendering with **no runtime vector
rasterization**. These packs pre-rasterize a pinned Nerd Font into compact binary
strikes that the renderer can memory-map and blit.

## Source font

**JetBrainsMono Nerd Font Mono**, Nerd Fonts release **v3.4.0** (JetBrains Mono 2.304).

- archive: `JetBrainsMono.tar.xz`, sha256 `ef552a3e638f25125c6ad4c51176a6adcdce295ab1d2ffacf0db060caf8c1582`
- file used: `JetBrainsMonoNerdFontMono-Regular.ttf` → put in `fonts/sources/` (gitignored)
- license: SIL OFL 1.1 (see THIRD_PARTY_FONTS.md at the repo root)

Why JetBrainsMono over the other candidates (DejaVuSansM, Hack, Cascadia, Iosevka
Nerd Font Mono): complete practical Cyrillic + Greek, distinct `0O`/`Il1`, the
cleanest unhinted grayscale at 14–20 px in side-by-side rasters, OFL-licensed, and
the `Mono` NF variant keeps every icon inside one cell.

## Layout

```
fonts/
  sources/        pinned source fonts (downloaded, gitignored; checksums above)
  manifests/
    agwin-bitmap.txt        glyph subset for the normal family (ranges + NF sets)
  overrides/      per-strike hand-corrected glyph bitmaps (14/ 16/ 18/ 20/) — TODO
  generated/      output packs + preview sheets (gitignored; `python fonts/generate.py`)
  generate.py     the deterministic generator
```

## Strikes and geometry

Nominal size = **em size**, matching how lite numbers its TrueType faces — "AGWin
Bitmap 16" is the same visual size as "Nerd Font 16". The cell is ascent+descent
at that em; cell width = the font's mono advance.

| pack | em | cell | glyphs | size |
|---|---|---|---|---|
| agwin-bitmap-14.agbf | 14 | 8×18 | 3840 | 386 KiB |
| agwin-bitmap-16.agbf | 16 | 10×21 | 3840 | 470 KiB |
| agwin-bitmap-18.agbf | 18 | 11×23 | 3840 | 550 KiB |
| agwin-bitmap-20.agbf | 20 | 12×26 | 3840 | 656 KiB |

Box drawing (U+2500–257F), block elements (U+2580–259F) and the solid/round
Powerline separators are **synthesized from the exact cell geometry** — strokes
reach the cell edges, so adjacent cells join without gaps at every size. Everything
else is rasterized unhinted to 8-bit alpha at integer pixel positions (no ClearType,
no subpixel placement).

## .agbf v1

Little-endian; full field list in the `generate.py` docstring. Header (172 bytes,
magic `AGBF`) → glyph records (20 B each, sorted by code point → binary search) →
8-bit alpha atlas. CRC-32 over records+atlas. Deterministic: same inputs, same bytes.

## Rebuilding

```
python fonts/generate.py            # needs fontTools + Pillow
python fonts/generate.py --sizes 16 # subset of strikes
```

Validation runs inside the generator: sorted/unique index, required coverage
(box, blocks, Powerline, Cyrillic, U+FFFD), single-cell metadata.

## Runtime (agwinterm-lite)

The four packs are committed to `lite/assets/` (regenerate + re-copy deliberately)
and ship next to the exe. Selecting **AGWin Bitmap 14/16/18/20** in Properties
renders every cell from the pack atlas — a single 32bpp DIB composed per pane, no
GDI text, no vector fonts. CRC-validated load (corrupted packs are rejected),
binary-search glyph lookup, synthetic bold (1px overstrike), underline/strike from
pack metrics, and uncovered code points render as bordered hex cells (3x5 micro
digits) instead of empty boxes.

## Status / TODO (tracked for the Complete family)

- per-strike override bitmaps (double-line box set + rounded corners need hand
  polish; the synthesis covers the single/heavy sets well)
- `AGWin Bitmap Complete`: full NF repertoire + GNU Unifont fallback + emoji atlas,
  grapheme-cluster resolution; italic strikes
