# AGWin Bitmap — implementation report

Final deliverable of the AGWin Bitmap spec (2026-07-28). Everything below shipped
across PRs **#163, #164, #166, #167–#169 (sizing + double box), #170, #171, #172,
#173, #174**, each merged on green CI with live-verification evidence in the PR body.

## What was built

Two bitmap font families for agwinterm-lite, pre-rasterized at build time into
versioned binary packs (`.agbf`) — **no vector font is parsed or rasterized at
runtime** for these families:

- **AGWin Bitmap** — curated ~3.8k-glyph subset of JetBrainsMono Nerd Font Mono
  (manifest-driven: Latin/Greek/Cyrillic, punctuation, arrows/math, box/blocks,
  Braille, the NF icon sets). 386–656 KiB per strike.
- **AGWin Bitmap Complete** — the full NF cmap (11,755) + all remaining BMP code
  points from GNU Unifont (55,725, 1-bit) + 1,191 color emoji from Noto Color
  Emoji. 68,671 glyphs, 4.7–8.4 MiB per strike.

Both ship at four strikes — **14/16/18/20 = em sizes**, deliberately matching how
lite numbers TrueType faces ("AGWin Bitmap 16" ≡ "Nerd Font 16" visually; the
cell is ascent+descent at that em). Strikes are independent rasterizations, never
scaled bitmaps, and are raster pixel strikes — not points.

## Fallback order (per spec)

corrected override → Nerd Font → synthesized cell geometry → Unifont (Complete
only) → color emoji (Complete only) → hex missing-glyph cell.

- **Synthesized**: box drawing U+2500–257F (incl. an explicit 29-glyph stroke
  table for the double set — corners are corners, Far Manager frames are the
  acceptance test), block elements, solid/round Powerline — all drawn on the
  exact cell grid so adjacent cells join gap-free at every strike.
- **Overrides**: `fonts/overrides/<px>/<hex>.png`, grayscale = alpha, exact cell
  size (or 2×w for wide), flag 8; an all-black PNG blanks a glyph. System live,
  no hand-drawn overrides committed yet.
- **Unifont**: 1-bit masks (flag 2) — ~3.5 MB per strike instead of ~20 MB at
  8-bit; scaled so ascent AND descent fit the pack cell on a shared baseline.
  East-Asian Wide/Fullwidth get `cellWidth 2`.
- **Emoji**: single-code-point U+1F300–1FAFF — exactly the range both emulators'
  wcwidth reports wide, so layout and pixels always agree. BGRA straight-alpha
  records (flag 16), 109 px CBDT strike Lanczos-scaled into the two-cell box.
- **Missing**: bordered hex cells (3×5 micro-digit font) — uncovered code points
  stay identifiable.

## Format (.agbf v1)

172-byte header (magic/version/strike/cell/baseline/underline/counts/CRC-32/
family/source) → 20-byte records sorted by code point (binary search) → atlas.
Atlas rows per glyph: 8-bit alpha, 1-bit packed (flag 2), or BGRA color
(flag 16). Full field list in the `fonts/generate.py` docstring.

## Pinned sources (sha256-verified by the generator)

| source | version | role |
|---|---|---|
| JetBrainsMono Nerd Font Mono | NF v3.4.0 (JB Mono 2.304) | main face, both families |
| GNU Unifont | 16.0.04 | BMP fallback, Complete |
| Noto Color Emoji | v2.048 | emoji, Complete |

Chosen over DejaVuSansM/Hack/Cascadia/Iosevka for complete practical
Cyrillic+Greek, distinct `0O`/`Il1`, and the cleanest unhinted grayscale at
14–20 px in side-by-side rasters. Licenses: THIRD_PARTY_FONTS.md.

## Determinism & validation

- Generator is deterministic: identical inputs → byte-identical packs (verified
  each phase); no timestamps; glyphs packed in code-point order.
- In-generator validation: sorted/unique index, required coverage, cell-width
  metadata, per-family spot checks (wide 1-bit CJK, color emoji).
- CI golden tests (`tests/Agwinterm.Core.Tests/AgbfPackTests.cs`) validate the
  **committed** packs the installer ships: header layout, CRC-32, strictly
  sorted index, per-record atlas bounds (stride-aware), family coverage.

## Runtime (lite)

Kind-3 catalog entries gated on pack presence; CRC-validated load (ntdll
RtlComputeCrc32, corrupted packs rejected); per-pane 32bpp DIB composed from the
atlas — **no GDI text**; binary-search lookup; synthetic bold = 1 px overstrike
(skipped for color); underline/strike from pack metrics; wide glyphs render
across two cells driven by the emulator's `cell.width`.

## Performance (`agwinterm-lite.exe --bench-agbf`, release build)

Cold CRC-validated loads ≤ 11 ms (even 8.4 MiB packs); lookups 51–52 ns
(curated) / 77–82 ns (Complete, 17.6× more records); full 120×40 mixed-content
grid compose 1.4–3.5 ms/frame; resident cost = file size, one flat allocation.
Full table: fonts/README.md § Performance.

## Deliberate limits

- **No shaping engine**: Arabic renders unshaped isolated forms; Indic/Thai
  combining marks render as spacing glyphs; bidi is the application's job.
- **No ZWJ/flag/skin-tone emoji sequences**: both emulators drop zero-width
  joiners at the grid ("combining/zero-width: dropped in v1"), so clusters never
  reach the renderer. Grapheme clustering therefore needs multi-code-point
  cells + FFI + parity work in the emulators first — tracked in the emulator
  backlog, not the font packs.
- **SMP beyond emoji** (mahjong, dominoes, Unifont Upper territory) still shows
  hex cells — wcwidth treats it single-width; revisit with Unifont Upper if it
  ever matters.
- **Italic strikes**: not built (spec listed them as optional; nothing in lite
  requests italic bitmap faces yet).
