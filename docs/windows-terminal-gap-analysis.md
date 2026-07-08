# Windows Terminal → agwinterm gap analysis

Snapshot date: **2026-07-07** (agwinterm v0.6.1 vs Windows Terminal 1.22 stable → 1.25 preview).
Method: full WT feature inventory from the official docs + release notes, then every candidate
verified against agwinterm source with file:line evidence. Unlike the
[agterm analysis](agterm-gap-analysis-2026-07.md) (feature parity with the blueprint), this one is
**triage input**: WT is a general-purpose terminal — agwinterm is agent-first, so many WT features
are deliberately out of scope. Tiers below are recommendations; move rows between tiers freely.

Effort: S (hours) / M (a day-ish) / L (multi-day).

---

## Already at parity (skip)

Portable mode, command palette, copy-on-select, URL detection + Ctrl+click, fullscreen, MRU
tab-switcher (`tabSwitcherMode: mru` ≈ our Ctrl+Tab walk), scroll-speed, window opacity,
per-session background images, 580 color schemes, cursor shapes (bar/block/underline + blink),
session/layout/cwd/profile persistence + opt-in command restore, DirectWrite font fallback
(implicit), Kitty graphics, bracketed paste, mouse modes, quake-style quick terminal, custom
commands ≈ snippets/sendInput, astral Unicode (as of v0.6.1; WT needed `textMeasurement: grapheme`
for the same).

---

## Tier 1 — Backlog candidates (recommended)

High value for an agent-first Windows terminal; reasonable effort.

| Feature | WT reference | agwinterm status (evidence) | Effort | Why it fits |
|---|---|---|---|---|
| **Shell-integration marks (FTCS/OSC 133)** + jump-to-prompt + select command/output | `autoMarkPrompts`, `scrollToMark`, `selectCommand`/`selectOutput`, `showMarksOnScrollbar` | **Missing** — OSC handler covers 0/2/7/9/777 only (`TerminalEmulator.cs:403`) | M | The single most agent-relevant WT feature: "select the last command's output" is exactly what you feed to an agent; prompt-jump makes long agent transcripts navigable |
| **Broadcast input** to multiple sessions/panes | `toggleBroadcastInput` | **Missing** — input always targets one surface | S–M | Drive several agent sessions with one instruction; pairs with the orchestration story |
| **Vertical splits + pane zoom** | `splitPane` (both axes, directional), `togglePaneZoom` | **Missing** — `PaneLayout` is columns-only, max 2 panes (`Program.cs:769`, `1075`) | M–L | Pane model is the biggest structural gap vs both WT *and* agent workflows (agent + editor stacked) |
| **Drag & drop file paths** (quoted, bracketed paste) | drop-onto-pane + `pathTranslationStyle` | **Missing** — no `WM_DROPFILES` anywhere | S–M | Already flagged in the agterm analysis (their #52/#96/#102); feeding files to agents by drop is daily-driver ergonomics |
| **Copy as HTML/RTF** | `copyFormatting` | **Missing** — `CF_UNICODETEXT` only (`Program.cs:3027`) | S–M | Paste styled agent output into docs/issues; we already track per-cell colors |
| **BEL bell handling** (visual/audible/taskbar styles) | `bellStyle`, `bellSound` | **Missing** — BEL (0x07) silently ignored (`TerminalEmulator.cs:152`) | S | TUIs signal with BEL; map it onto the existing attention/badge machinery — cheap and on-brand |
| **Ctrl+wheel font zoom** | `experimental.scrollToZoom` | **Missing** — wheel handler has no Ctrl branch (`Program.cs:2262`) | S | Muscle memory from every other Windows app |
| **CLI launch args on the exe** (`-p profile`, `-d dir`, startup actions) | `wt.exe` subcommands, `startupActions` | **Missing** — `Main()` takes no args (`Program.cs:306`) | S–M | Prereq for Explorer/jumplist integration; lets scripts/agents launch configured windows |
| **Explorer "Open agwinterm here"** | context-menu shell entry | **Missing** | S (after CLI args) | Standard Windows citizenship; opt-in installer entry like the existing PATH/hooks installers |
| **Taskbar progress (OSC 9;4)** | taskbar/tab progress bar | **Missing** | S–M | Agents report long-task progress; surfacing it on the taskbar fits the status-dot philosophy |

## Tier 2 — Worth considering (your call)

Valuable but bigger, nichier, or partially covered by an agwinterm-native answer.

| Feature | WT reference | agwinterm status | Effort | Notes |
|---|---|---|---|---|
| Word-delimiter config for double-click | `wordDelimiters` | Hardcoded space/NUL (`Program.cs:2842`) | S | Small QoL; pairs with block selection work |
| Search: regex + case toggle | `find` regex mode (1.22) | Case-insensitive only, no regex (`Program.cs:2930`) | S–M | Useful for digging through agent logs |
| Read-only panes | `toggleReadOnlyMode` | Missing | S | Protect a running agent's pane from stray keystrokes — quietly agent-relevant |
| Global summon hotkey | `globalSummon` (OS-wide) | Missing (quick terminal is in-app only) | M | Summon the agent terminal from anywhere; RegisterHotKey + restore |
| Block selection + keyboard mark mode | alt+drag, `markMode` | Missing (span selection only) | M | Power-user selection; mark mode also helps accessibility |
| Per-profile env vars / hidden / elevate | `environment`, `hidden`, `elevate` | Profile = name/command/args/cwd/icon only (`ShellProfiles.cs:10`) | S each (elevate M) | env vars are the most requested third of this |
| Unfocused-window dim/opacity | `unfocusedAppearance` | Missing (only inactive-*pane* dim) | S | Cheap polish |
| Duplicate session action (one keystroke) | `duplicateTab`/`duplicatePane` | Partial — `new-session-dir-mode=current` approximates | S | Trivial once defined as "new session, same profile+cwd" |
| Ligatures / `font.features` | OpenType features | No shaping control; grid-run renderer (`Program.cs:3603`) | M–L | Conflicts with per-cell grid advance — needs the WT trick (shape per run, snap advances) |
| Builtin box-drawing glyphs | `font.builtinGlyphs` | Font glyphs only | M | Pixel-perfect TUI borders at any size |
| Acrylic / Mica backdrop | `useAcrylic`, `useMica` | Layered-window opacity only (`Program.cs:5413`) | M | Pure aesthetics; Win11 APIs |
| Sixel graphics | v1.22, on by default | Kitty-only (`KittyGraphics.cs`) | M–L | Was already M on the agterm list; niche until agents emit sixel |
| win32-input-mode / Kitty keyboard protocol | `CSI ? 9001 h`, 1.25 | Missing | M | Keyboard fidelity for demanding TUIs (helix, neovim) |
| **Default Terminal Application** delegation | OS defterm handoff | Missing | **L** | Flagship OS integration but requires the ConPTY delegation COM contract — the deepest item here |
| UIA screen-reader support | full UIA text pattern | Missing — no `WM_GETOBJECT` provider | **L** | Custom-drawn window means implementing providers from scratch; important, not small |
| Buffer-content restore | 1.21 persistence | Layout/cwd/commands persist; buffer contents don't | M | Nice with restore-commands already in place |

## Tier 3 — Probably not (by design)

| WT feature | Why agwinterm skips it |
|---|---|
| Tabs (colors, width modes, tear-out, new-tab menu) | The **sidebar workspace/session tree is the tab model** (agterm's core UX bet). Tear-out ≈ existing multi-window |
| Dynamic profile generators + JSON fragments/extensions | `profiles.json` is the deliberate, simpler surface; SSH/VS auto-detect could someday extend `Detect()` |
| Azure Cloud Shell connector | Niche; a profile command can do it |
| Terminal Chat | The entire app is agent-first; agents live in sessions, not a sidebar chat |
| Settings UI depth (per-profile pages, actions editor, extensions page) | Settings dialog + conf files + palette already cover it, umputun-style |
| Pixel shaders / retro CRT effects | Aesthetics far off mission; opacity/themes/backgrounds suffice |
| Group Policy / Intune | Not an enterprise product (yet) |
| App-level theme objects (tabRow/tab colors) | Whole-window theming already retints all chrome per terminal theme |
| Quick Fix (winget suggestions), Suggestions UI | The agent *is* the suggestion engine here |

---

## Triage outcome (2026-07-07)

**Accepted backlog** (owner triage), in suggested execution order:

1. **Ctrl+wheel font zoom** (T1 #7, S) — quick win
2. **CLI launch args on the exe** (T1 #8, S–M) — `-p profile`, `-d dir`, window placement
3. **Drag & drop file paths** (T1 #4, S–M) — quoted + bracketed paste; also closes the agterm-list item
4. **Broadcast input** (T1 #2, S–M) — UI toggle + a `session.broadcast` control verb for orchestrators
5. **Taskbar progress OSC 9;4** (T1 #10, S–M) — taskbar + sidebar progress from agent tasks
6. **FTCS shell-integration marks** (T1 #1, M) — OSC 133 parsing, jump-to-prompt, select-command/output; the agent-workflow multiplier, scheduled last as the largest

**Rejected in triage** (revisit on demand): vertical splits + pane zoom (T1 #3), copy as
HTML/RTF (T1 #5), BEL bell styles (T1 #6), Explorer "Open here" (T1 #9).

### Tier 2 triage (2026-07-07)

**Accepted → backlog** (no execution order yet; tasks created per batch when work starts):
word-delimiter config (S) · read-only panes (S) · block selection + mark mode (M) ·
per-profile env vars (S) · elevated profile launch (M) · unfocused-window dim (S) ·
ligatures/font.features (M–L) · builtin box-drawing glyphs (M) · acrylic/Mica (M) ·
sixel graphics (M–L) · win32-input-mode + Kitty keyboard protocol (M) ·
**Default Terminal Application delegation (L)** · **UIA screen-reader accessibility (L)** ·
buffer-content restore (M)

**Dropped after review**: acrylic/Mica backdrop — Acrylic's live DWM blur is a real
GPU/battery cost for an all-day terminal, the effect is pure cosmetics, and it needs an
opaque→alpha-composited render-pipeline rework. Not worth it; owner doesn't want it (2026-07-08).

**In progress / notes**:
- Default Terminal delegation (T2-13): Stages A+B+C implemented & merged (attach-to-external-PTY,
  ITerminalHandoff3 COM server, registration tooling). CoRegisterClassObject returns S_OK. Remaining:
  the host handoff test (tools/defterm-register.ps1 → launch cmd → tools/defterm-restore-conhost.ps1);
  needs OpenConsole.exe + OpenConsoleProxy.dll (tools/defterm-fetch-openconsole.ps1).
- UIA accessibility (T2-14): **Stage 1 done** (PR #37). Raw source-gen COM IRawElementProviderSimple via
  WM_GETOBJECT — WPF-free. Reports ControlType=Document, LocalizedControlType "terminal", Name = the
  visible screen text. The first attempt crashed natively because it handed UiaReturnRawElementProvider
  the CCW's raw IUnknown* instead of a QueryInterface'd IRawElementProviderSimple* (different vtables) —
  fixed. Verified via scripted UIAutomationClient (no screen reader needed). **Stage 2 (remaining):**
  ITextProvider/ITextRangeProvider for line/caret navigation + selection — big (≈20 range methods,
  SAFEARRAY/VARIANT marshalling), best validated against a live Narrator/NVDA.

**Rejected**: search regex/case toggle, global summon hotkey, hidden profiles,
one-keystroke duplicate session.

### Tier 3 triage (2026-07-07)

Reviewed and **confirmed as-is**: none of the by-design skips move to the backlog.
