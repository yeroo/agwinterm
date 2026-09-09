# P16 native picker acceptance

- Base: P15 merge `482612bcb07813db33db1a0c57b49ff08cb2965c`.
- Release/install: none. Lite mirror/conformance remain P17.
- Build: Release solution and release Rust workspace pass.
- New focused tests: Core 23, Pty/CLI/dispatch 19, all pass.
- Private owned GUI fixture: 22/22 checks, screenshot visually inspected; artifact
  `.revmux/picker-ui-20260909T184600-8b51af`. Canonical token generation 115 released
  after retained-job zero-process proof. Clipboard/registry untouched, no queued launches.
- Earlier fixture run generation 114: four failed assertions; corrected cross-process
  EDIT probe to WM_SETTEXT, and moved foreground-changing Tab to disposable CI.
  Its cleanup also proved zero processes before release.
- Real foreground/Tab and multi-window close/ownership cases run only on disposable CI.
- Final pre-review private GUI: 25/25, generation 116 released after zero owned
  processes; `.revmux/picker-ui-20260909T184913-dd2b67`. Added readonly/quick refusal
  and immediate background-cancellation foreground check.
- Full headless Core 320 passes. Two local Pty runs hit the documented .NET #118
  completion-poller native pathology (753/773 assertions passed before host abort).
  Focused new Pty/CLI/dispatch tests pass 19/19. Full clean CI remains a merge gate.
- Review/CI exact-head receipts are recorded on the PR before merge.

## P15 final receipt carried forward

- PR #269 merged at `482612b`; candidate `309222ca0055b3c238d5ef17e542ab860e733a34`.
- Exact-head CI 34368364930 passed: conformance, clipboard/paste, Win32, HUD 51,
  Quick 57, Navigation 51, Core 297 and Pty 774.
- Broad Codex review: four healthy sources. Batched current-workspace deletion,
  stale focus, last-workspace refusal and parser/test fixes in `309222c`.
- Narrow final review: two healthy sources, no introduced findings/open questions.
  Confirmed pre-existing split/auxiliary workspace teardown is tracked separately as #270.
- Local Navigation 51/51, generation 113 released after owned-job cleanup; no shared
  clipboard/registry mutation. Artifact `.revmux/navigation-ui-20260909T180805-03bb23`
  in the P15 worktree. No further review rounds for cosmetic findings.
