# P14 — detached quick terminal

Authority: Boris requested P14, P15, P16 and P17 sequentially on 2026-09-09.
Codex owns implementation and merge during Claude's outage. Base: ae4d52c (P13).
Next: P15 navigation/keymap polish, P16 control.pick, P17 lite mirror; each starts
after its predecessor's exact-head tests/review/CI and merge. No release/install.

## Contract

- One lazy, app-level, non-restored quick shell in a detached tool window, not a
  library window. Normal windows, their geometry and their PTY sizes are untouched.
- `quick-terminal-size = 70` controls both dimensions, 40–90 percent of the usable
  monitor area under the pointer. Read on show and live size changes. DPI-aware.
- Ctrl+backtick, toolbar, and keymap `quick_terminal` toggle this same panel.
  Human summons dismiss on blur; API `quick on` pins it without taking focus. Hiding keeps
  the shell; shell exit discards it; closing the last library window disposes it.
- `quick-terminal-hotkey =` is opt-in with no default reservation. Accept one
  Ctrl/Alt (optional Shift) chord with letter/digit, F1–F11 or backtick. No Win,
  F12, unmodified or Shift-only chords. Register with MOD_NOREPEAT, once per app.
  Live registration conflicts refuse without changing the old binding/config;
  startup conflicts show a warning. Unregister at shutdown. No hooks or forced
  foreground workarounds; Windows may decline focus to background API callers.
- `--window quick` addresses the auxiliary surface once created; no quick entry
  is persisted or returned by the library list. Normal window read-back reports
  the shared panel's visibility. A quick shell inherits this explicit window
  selector, not a fabricated library identity.

Source: upstream agterm v0.26.0 `agterm/Views/QuickTerminal.swift` (detached,
app-level, pointer monitor, human blur/control pin), adapted to Windows.
Win32 constraints: [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey),
[SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow).

## Acceptance / safety

Parser/config bounds and geometry unit checks; owned-window integration for
singleton identity, screen sizing, no normal-window resize, hide/show shell
survival, close/exit, live hotkey conflict preservation/release and pin/blur.
Actual foreground/key injection is disposable CI only. Local GUI tests require
the canonical suite token and retained kill-on-close job ownership proof.
One Codex-only broad revmux, batch substantive findings, narrow confirmation
only when needed; exact-head green CI then normal PR merge. Do not defer bugs
that violate the contract; do not churn additional rounds on cosmetic minors.
