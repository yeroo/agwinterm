# Per-strike glyph overrides ("corrected" source)

Hand-drawn replacement bitmaps with the **highest precedence** in the fallback
order (corrected → Nerd Font → synthesized → Unifont → missing-glyph cell).
They apply to both families.

- Location: `fonts/overrides/<px>/<hex>.png` — one directory per strike
  (`14/ 16/ 18/ 20/`), file name is the bare hex code point (e.g. `2554.png`
  for ╔, `e0b0.png` for the Powerline separator).
- Format: grayscale PNG, pixel value = alpha. Size must be exactly the strike's
  cell (`8×18`, `10×21`, `11×23`, `12×26`) — or twice the cell width for a wide
  glyph. The generator errors on any other size.
- An all-black PNG deliberately blanks the glyph (zero-size record, cell shows
  background only).
- Records carry flag 8 so a pack can be audited for hand-corrected glyphs.

Overrides are per-strike on purpose: a shape that reads well at 10×21 rarely
survives scaling to 8×18. Draw each size by hand.
