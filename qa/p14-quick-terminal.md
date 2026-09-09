# P14 acceptance and review record

Initial revision: `7a758dc` (base `ae4d52c`). Codex owns this batch and merge.
Plan: [P14](../docs/plans/2026-09-09-p14-quick-terminal.md).

## Independent review

Archive: `.revmux/tasks/feat-p14-quick-terminal/01-initial/` in the P14 worktree.
Profile `codex-only`; runner, four reviewers, synthesis and verification all Codex.
4 expected / 4 reported, no degraded source; each produced findings and used tools.
Verified result: 5 Major, 8 Minor, 1 Immaterial. No open questions/pre-existing list.

One fix batch, not repeated broad/polish rounds:

- bugs+impl-1: atomic same-directory config replacement; failed writes preserve old bytes.
- bugs+impl-2: quick DPI changes update render scale without scaling its physical frame twice.
- arch+quality-1, docs+tests-2: retain actual old OS registration through persistence;
  failed persistence releases only provisional registration. Track rare failed cleanup.
- arch+quality-2: surface-local keymaps, leader bindings and send/detached custom commands
  remain usable; tree-dependent actions refuse/notify instead of creating hidden sessions.
- adversarial-2: config writes use the exception-propagating queued UI bridge.
- bugs+impl-3, docs+tests-1: active quick font targeting uses its surface, not a library session.
- bugs+impl-4: keyboard scrollback derives its page size from ActiveSurface.
- adversarial-3: system caret follows the quick surface, despite no library session.
- docs+tests-3: disposable-CI two-window singleton/survivor check and shutdown hotkey check.
- docs+tests-5/6: teardown/render comments corrected while touching those functions.
- Immaterial arch+quality-4: removed the unused forwarding helper in the same batch.

Author checks also cover owned-modal focus, key-up cleanup, final-row hit-testing,
font-family regrid, and the existing CI capture's new explicit quick-window routing.

## Tests before focused confirmation

- Initial Core 265, Pty 761 passed (includes 19 new Core / 11 protocol checks).
- Fix batch Core 265, Pty 760 passed; one fake-only command.run refusal case replaced
  by the real-window send/new-mode checks after enabling surface-local commands.
- Initial private quick fixture: 32/32; HUD regression: 51/51.
- Fix private quick fixture: 47/47, including save failure with valid and invalid
  desired prior bindings, exact bytes preserved, native reservations, font/search,
  keymap/custom command, posted logical DPI/focus, caret, exit and shutdown.
- A new search fixture initially failed because an emulator-injected marker could be
  overwritten by pending ConPTY resize output. It now waits for real shell output.
- Actual OS hotkey injection, focus loss and two-library-window tests are CI-only;
  never injected on Boris's shared desktop. Local DPI/focus checks are explicitly logical.

Latest local receipts: token 103 (32 quick), 104 (51 HUD), 105 (43 quick),
106 (46/47, failed marker check), 107 (47 quick). All released after retained-job
proof of zero live processes and exact temporary hotkey cleanup. Clipboard and
registry untouched. Private app-data and transcripts retained, no queued launches.

Focused review and exact-head CI remain merge gates; neither is claimed complete here.
