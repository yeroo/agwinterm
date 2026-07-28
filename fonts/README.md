# AGWin Bitmap — build-time raster font packs for agwinterm-lite

Lite targets slow/old machines; the goal is text rendering with **no runtime vector
rasterization**. These packs pre-rasterize a pinned Nerd Font into compact binary
strikes that the renderer can memory-map and blit.

## Source fonts

**JetBrainsMono Nerd Font Mono**, Nerd Fonts release **v3.4.0** (JetBrains Mono 2.304).

- archive: `JetBrainsMono.tar.xz`, sha256 `ef552a3e638f25125c6ad4c51176a6adcdce295ab1d2ffacf0db060caf8c1582`
- file used: `JetBrainsMonoNerdFontMono-Regular.ttf` → put in `fonts/sources/` (gitignored)
- license: SIL OFL 1.1 (see THIRD_PARTY_FONTS.md at the repo root)

**GNU Unifont 16.0.04** (Unicode fallback for the Complete family only).

- file: `unifont-16.0.04.otf` → save as `fonts/sources/Unifont.otf` (gitignored),
  sha256 `0e3981ab552231b5a2a870f2b61741903a4bf25c23ef5aeb05fdced1b3c7af4d` (verified by the generator)
- license: dual SIL OFL 1.1 / GPLv2+ with font-embedding exception

**Noto Color Emoji v2.048** (color emoji for the Complete family only).

- file: `fonts/NotoColorEmoji.ttf` at the noto-emoji v2.048 tag → put in
  `fonts/sources/` (gitignored), sha256
  `3ed77810c203e1a67735dc19d395f32c23f2d7c0c3696690f4f78e15e57ab816` (verified by the generator)
- license: SIL OFL 1.1

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
  overrides/      per-strike hand-corrected glyph PNGs (14/ 16/ 18/ 20/, see its README)
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
| agwin-bitmap-complete-14.agbf | 14 | 8×18 | 68671 | 4.7 MiB |
| agwin-bitmap-complete-16.agbf | 16 | 10×21 | 68671 | 6.2 MiB |
| agwin-bitmap-complete-18.agbf | 18 | 11×23 | 68671 | 7.2 MiB |
| agwin-bitmap-complete-20.agbf | 20 | 12×26 | 68671 | 8.4 MiB |

Box drawing (U+2500–257F), block elements (U+2580–259F) and the solid/round
Powerline separators are **synthesized from the exact cell geometry** — strokes
reach the cell edges, so adjacent cells join without gaps at every size. Everything
else is rasterized unhinted to 8-bit alpha at integer pixel positions (no ClearType,
no subpixel placement).

## The Complete family: Unicode fallback

`AGWin Bitmap Complete` starts from the **full cmap of the source Nerd Font**
(11,755 glyphs) and backfills **every remaining BMP code point** from pinned GNU
Unifont (55,725 more; surrogates and PUA excluded — the PUA stays the Nerd Font's
icon territory). Fallback order per the spec: hand-corrected override → Nerd Font
→ synthesized cell geometry → Unifont → hex missing-glyph cell.

Unifont glyphs are stored as **1-bit masks** (record flag 2) — Unifont is natively
1-bit, and 8-bit alpha for ~56k extra glyphs would cost ~20 MB per strike instead
of ~3.5 MB. They are scaled so ascent and descent both fit the pack's cell and
share the main face's baseline. East-Asian Wide/Fullwidth code points are marked
`cellWidth 2` and rasterized across two cells; at paint time the emulator's own
wcwidth drives layout, the record just carries matching pixels.

**Color emoji**: every single-code-point emoji in U+1F300–1FAFF (1,191 from
pinned Noto Color Emoji v2.048) is stored as a color record — flag 16, BGRA rows
with straight alpha, the 109 px CBDT strike Lanczos-scaled into the two-cell box.
That range is exactly what both emulators' wcwidth treats as wide, so layout and
pixels always agree. BMP pictographs (☀ ★ ♥ …) deliberately stay monochrome —
the emulator gives them one cell.

Known limits (deliberate, no shaping engine): Arabic renders unshaped isolated
forms, Indic/Thai combining marks render as spacing glyphs, bidi is left to the
application. ZWJ/flag/skin-tone emoji sequences cannot reach the renderer at all
today — the emulators drop zero-width joiners at the grid ("combining/zero-width:
dropped in v1"), so grapheme clustering is an emulator-architecture item
(multi-code-point cells + FFI + parity), tracked outside the font packs.

## .agbf v1

Little-endian; full field list in the `generate.py` docstring. Header (172 bytes,
magic `AGBF`) → glyph records (20 B each, sorted by code point → binary search) →
atlas: 8-bit alpha, or bit-packed 1-bit rows for flagged fallback glyphs. CRC-32
over records+atlas. Deterministic: same inputs, same bytes.

## Rebuilding

```
python fonts/generate.py                       # curated family; needs fontTools + Pillow
python fonts/generate.py --family complete     # full repertoire + Unifont fallback
python fonts/generate.py --sizes 16            # subset of strikes
```

Validation runs inside the generator: sorted/unique index, required coverage
(box, blocks, Powerline, Cyrillic, U+FFFD), cell-width metadata, and for the
Complete family spot checks that CJK/kana/hangul are wide 1-bit fallback glyphs.
The **committed** packs are additionally validated on every CI run
(`tests/Agwinterm.Core.Tests/AgbfPackTests.cs`: header sanity, CRC-32, sorted
index, atlas bounds, per-family coverage) — a corrupted regeneration can't ship.

## Runtime (agwinterm-lite)

The four packs are committed to `lite/assets/` (regenerate + re-copy deliberately)
and ship next to the exe. Selecting **AGWin Bitmap 14/16/18/20** in Properties
renders every cell from the pack atlas — a single 32bpp DIB composed per pane, no
GDI text, no vector fonts. CRC-validated load (corrupted packs are rejected),
binary-search glyph lookup, synthetic bold (1px overstrike), underline/strike from
pack metrics, and uncovered code points render as bordered hex cells (3x5 micro
digits) instead of empty boxes.

## Performance

`agwinterm-lite.exe --bench-agbf` prints the pack benchmarks (headless, no window).
Measured on the dev machine, release build, cold loads with CRC validation, 1M
random glyph lookups, 100 full 120×40 grid composes of mixed ASCII/box/CJK:

| pack | load | lookup | grid 120×40 | resident |
|---|---|---|---|---|
| agwin-bitmap-16 | 6.4 ms | 52 ns/op | 2.0 ms/frame | 470 KiB |
| agwin-bitmap-20 | 7.7 ms | 51 ns/op | 3.1 ms/frame | 656 KiB |
| agwin-bitmap-complete-16 | 9.8 ms | 79 ns/op | 2.2 ms/frame | 6.2 MiB |
| agwin-bitmap-complete-20 | 10.6 ms | 78 ns/op | 3.5 ms/frame | 8.4 MiB |

The 67k-record Complete index costs ~26 ns extra per binary-search lookup; a full
grid compose stays around 2–3 ms — repaints are nowhere near the frame budget
even on the Complete packs.

## Status / TODO (tracked for the Complete family)

- italic strikes
- grapheme-cluster resolution — blocked on the emulators (zero-width joiners are
  dropped at the grid in v1; needs multi-code-point cells + FFI + parity work)
- (the override *system* is live; no hand-drawn overrides are committed yet —
  the explicit double-box stroke tables covered the known rough spots)
