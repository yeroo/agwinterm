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

## Broad review and one fix batch

Round `01-initial`, candidate `4389171`: 4 expected/4 reported Codex sources, no
degradation. Runner, reviewers, synthesis and verification all Codex. Final report:
two Critical entries for the same wrong-workspace deletion mechanism (keymap/palette,
independently found by bugs+impl and adversarial), one Major stale-focus navigation
failure, four Minors (parser globals, DECSCUSR test, reserved F6 test, tooltip comments).
One pre-existing Major and two Immaterials; no open questions.

- Both delete-current actions now share a CurrentWorkspace-based helper. Named-row
  context deletion still uses its named workspace; terminal broadcast intentionally
  follows the visible selected terminal. Swept all `_active.Ws` and deletion call sites.
- Workspace deletion clears matching focus and empty-placement state. The existing
  last-workspace false-success defect is fixed in the same deletion path: queued UI
  execution returns the actual membership/count refusal before mutation.
- Parser rule sets are private/frozen and exposed defaults are read-only.
- Added live F6 conflict/ownership, both delete action paths, focused deletion,
  last-workspace refusal, and nonzero DECSCUSR shape/blink precedence acceptance.
- Broadened the two tooltip comments while editing the same file.
- Immaterials left unchanged: duplicate-but-equal cursor grammar and redundant target
  check. Neither changes execution; no extra polish round is warranted.

Initial candidate passed [CI 34365129644](https://github.com/yeroo/agwinterm/actions/runs/34365129644):
conformance, clipboard/paste, Win32, HUD 51, quick 57, navigation 44, Core 296/Pty 774.
Fix batch: Release build passed; Core 297/Pty 774 passed; private navigation **51/51**.
Artifact `.revmux/navigation-ui-20260909T180805-03bb23`, token 113 released after zero
owned processes. Clipboard/registry untouched, no queued launches. Narrow Codex
confirmation and exact-head CI remain the final gates.
