# P13 — passive session HUD

Boris requested P13 end-to-end on 2026-09-09. Codex owns `agwinterm-p13`,
branch `feat/p13-session-hud`, base e0e5282. Claude's parked worktree is untouched.

## Scope

Implement the roadmap's agwinterm HUD, using agterm v0.26.0's HUD API as the reference:
`session.hud.open/update/close`, CLI `session hud [open] MESSAGE`, `update MESSAGE`,
`close`. Nine positions plus top/bottom aliases, detail, five spinner styles/none,
optional background/text colors and width percent. Open replaces an existing HUD;
update requires one and replaces the spec except for the original background color.
Close is idempotent and never closes a program overlay. No expiry is implied:
transient means until closed/replaced/session teardown, never persisted.

The HUD is session-wide native drawing, not another terminal or process. Keyboard,
mouse, selection, terminal geometry/history, focus and notification state are unchanged.
An active program overlay refuses HUD open/update. Opening a session-wide program
overlay replaces the HUD; overlay close and the ordinary close shortcut dismiss it.
Pane overlays may coexist underneath. Scratch/quick covers and dashboard obscure it.
Explicit auxiliary-cover or split-pane-only targets refuse rather than widen silently;
use the owning session id. Unknown targets/options/invalid types change nothing.

Tree exposes the effective `hud` object with normalized position/spinner, effective
width limit and retained background. Input text is at most 256 normalized Unicode
scalars per field, nonblank message, no control characters. Plain text only. Requested
width 1..100 is visibly bounded to 10..80%; height follows wrapped content, capped at
80%. Off-center anchors retain a 10% edge margin; content is clipped on tiny windows.
Colors use six hexadecimal digits (optional #). Rendering uses DirectWrite Unicode
layout and Direct2D, with a window-owned animation timer and no helper filesystem.

## Validation / delivery

Pure validation, all nine layouts and spinner tests; dispatcher refusal/state tests;
Win32 integration covering all anchors, update/close/replacement, stable terminal text
and geometry, typing through the panel, split and auxiliary targeting, and lifecycle.
Build/Core+Pty tests plus exact-head Windows CI. Local GUI runs require canonical suite
token through exact owned-process exit; use a dedicated HUD fixture that does not touch
clipboard/shared configuration. Existing full integration remains on disposable CI.
Visually inspect the actual rendered panel. One broad Codex-only revmux review, batched
fixes, narrow confirmation only as needed. Merge only the tested head; no release/tag.

Update the agterm tracker and record that lite's Wave-3 mirror is still owed (P17).
Do not expand the shared conformance floor ahead of that mirror: P13's executable
contract coverage is agwinterm-only integration plus dispatcher tests for now.

Reference: https://github.com/umputun/agterm/tree/v0.26.0/agtermCore/Sources/agtermCore
