# Post-P17 stabilization

Boris authorized fixing the unfinished P17 review and the open backlog on 2026-09-10.
Codex owns `agwinterm-stabilize` and `agliteterm-stabilize`; parked trees are untouched.
Product boundaries (Lite images/no zoom, process-local windows) remain deliberate.
No release, tag or installation is included.

## Gates

- Complete independent P17 final confirmation; recovered finder reports alone are not completion.
- Reproduce or source-verify each issue; close only with linked evidence, not merely a stale label.
- Batch related fixes with regression tests, Codex-only revmux and author verification.
- One broad review and, where needed, narrow confirmation; further rounds require substantive blockers.
- Exact-head required CI and integration tests before merge. Canonical contract before Lite mirror.
- Shared-desktop suites require the canonical token through proven cleanup; unsafe fixtures stay CI-only.

## Inventory at start

All items below are pending assessment, not claimed fixed.

| Batch | agwinterm | agliteterm |
| --- | --- | --- |
| Control truthfulness and persistence | #254, #257, #259 | #36, #42, #53, #55, #63, #64 |
| UI input, rendering, lifecycle, accessibility | #189, #196, #198, #208, #209, #251, #267, #270 | #39 |
| Concurrency and host transport | #258, #268 | #21, #27, #43 |
| Shell integration and configuration | — | #41, #58, #60, #61 |
| Shared-desktop test safety | — | #51, #56 |

P17 final confirmation: existing task `feat-p17-lite-wave3`, round `04-final-confirmation`,
scope `baaac5b..ac4896a`; completed successfully, exit 0, 2/2 healthy Codex reviewers,
zero findings/open questions/pre-existing/immaterial entries. Synthesis completed;
verification had no findings to process. Receipt posted on Lite PR #65.
