# Third-party fonts

Every font bundled with agwinterm / agwinterm-lite or rasterized into generated
assets, with exact versions and licenses. License texts ship next to the font files
in `lite/assets/` (and inside the installers).

| Project | Version | URL | License | Used as | Modified? |
|---|---|---|---|---|---|
| Meslo LGL DZ Nerd Font | NF release (bundled) | https://github.com/ryanoasis/nerd-fonts | OFL 1.1 (Meslo: Apache 2.0) | `MesloLGLDZNerdFont-Regular.ttf` — default TrueType face (main app + lite) | no |
| Cozette | (bundled) | https://github.com/the-moonwitch/Cozette | MIT | `CozetteVector*.ttf` — lite catalog | no |
| Tamzen (For Powerline) | (bundled) | https://github.com/sunaku/tamzen-font | free redistribution (see Tamzen-LICENSE.txt) | `TamzenForPowerline*.ttf` — lite catalog | no |
| Terminus (TTF) | 4.49.3 | https://files.ax86.net/terminus-ttf/ | SIL OFL 1.1 | `TerminusTTF*.ttf` — lite catalog | no |
| Spleen | 2.1.0 | https://github.com/fcambus/spleen | BSD 2-Clause, © Frederic Cambus | `Spleen-*.otf` — lite catalog | no |
| unscii | 2.1 | http://viznut.fi/unscii/ | Public Domain | `unscii-16.ttf`, `unscii-8.ttf` — lite catalog | unscii-8: name table renamed to "unscii-8" (family collision); glyphs untouched |
| GNU Unifont | 16.0.04, sha256 `0e3981ab…af4d` (unifont-16.0.04.otf) | https://unifoundry.com/unifont/ | dual SIL OFL 1.1 / GPLv2+ with font embedding exception (redistributed under OFL) | `Unifont.otf` — lite catalog; also **rasterized** (1-bit) into `agwin-bitmap-complete-*.agbf` as the BMP fallback | rasterized to 1-bit bitmaps in the Complete packs; the bundled OTF is untouched |
| JetBrainsMono Nerd Font Mono | Nerd Fonts v3.4.0 (JetBrains Mono 2.304), sha256 `ef552a3e…c1582` (JetBrainsMono.tar.xz) | https://github.com/ryanoasis/nerd-fonts | SIL OFL 1.1 (JetBrains Mono © 2020 JetBrains); NF-patched icon sets carry their own licenses (see the NF release's LICENSE files) | **rasterized** into `fonts/generated/agwin-bitmap-*.agbf` (AGWin Bitmap) | rasterized to bitmaps; box/block/Powerline glyphs replaced by cell-geometry synthesis |
| Noto Color Emoji | v2.048, sha256 `3ed77810…b816` (NotoColorEmoji.ttf) | https://github.com/googlefonts/noto-emoji | SIL OFL 1.1 | **rasterized** (BGRA color records) into `agwin-bitmap-complete-*.agbf` — single-code-point emoji U+1F300–1FAFF | CBDT 109 px strike scaled to the two-cell box |

Notes:
- Nerd Fonts patched fonts aggregate glyphs from several icon projects (Font
  Awesome, Devicons, Octicons, Codicons, Powerline Extra, Seti-UI, Weather, Font
  Logos, Pomicons); licensing/attribution per set is documented in the pinned Nerd
  Fonts release. The AGWin Bitmap packs inherit those obligations.
- SIL OFL 1.1 permits redistribution and format conversion (including
  rasterization) provided the fonts are not sold standalone; agwinterm complies.
