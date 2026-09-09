# P15 acceptance and review record

Base: P14 merge `0a784b4`. Owner: Codex. [Plan](../docs/plans/2026-09-09-p15-navigation.md),
[contract](../docs/navigation.md). Candidate implementation; review/CI/merge remain gates.

- Release solution build succeeds. Native Rust release built before final headless run.
- Core 296 / Pty 774 passed, no failures or skips (including parser/navigation/selector tests).
- Initial private GUI runs exposed fixture ordering and an F6 binding conflict with the
  existing accessibility focus-zone gesture. Wait for new-session materialization and
  use unreserved F5/F7; do not remove the production accessibility gesture.
- Cursor bar/block/underline and blinking pixels, tooltip dwell/leave, shell split,
  live-child unknown and exited-shell unknown checks passed in those initial runs.
- Token generations 109/110 released after retained-job zero-process proof; clipboard
  and registry untouched. Logical messages target only the private fixture HWND.
- A lost headless runner stalled with a locked test DLL. Its exact PID, creation time,
  executable and ancestry were verified before terminating only that owned test host;
  clean build and a fresh bounded headless run passed afterward.

Full legacy GUI integration runs only in disposable Windows CI. Codex-only revmux and
exact-head CI receipts will be recorded before merge. Lite mirror remains P17.

Final pre-review private navigation fixture: **44/44**, artifact
`.revmux/navigation-ui-20260909T173834-b25abb`, token 112 released after zero owned
processes. Includes empty explicit selection, caller placement precedence, deletion
fallback, flagged-session tooltip and rename invalidation. Earlier corrected fixture
passed 38/38 (token 111). The retained tooltip screenshot was visually inspected.
