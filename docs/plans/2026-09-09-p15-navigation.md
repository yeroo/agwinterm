# P15 — navigation and small UI sweep

Authority: Boris requested P14–P17 sequentially, 2026-09-09. Codex owns delivery
during Claude's outage. Base: P14 merge 0a784b452ae8b544552f1bf32dd92612bc7b5278. No release/install.

## Contract

- `workspace go next|prev` (also `--to next|prev`, `previous` alias) wraps through
  this window's visible workspace order. Collapsed workspaces still count. Refuse
  flagged mode or fewer than two visible workspaces. Ignore no destinations silently;
  invalid/missing direction and an explicit target refuse. Return destination id.
- Match upstream v0.26.0 AppStore navigation: first session selected when present;
  an empty destination becomes the current workspace for subsequent new sessions,
  without closing the prior session or creating a shell. P2's explicit workspace or
  valid caller-pane workspace still takes precedence over this current-workspace fallback.
  Current workspace and selected
  session are deliberately separate while the destination is empty. Tree/window state
  must report the destination. Selecting a session ends the empty-workspace override.
- Add `next_workspace`, `previous_workspace`, `toggle_workspace_collapse` actions to
  keymap and action palette/menu. No new default chords that steal existing bindings.
  Fold toggling changes only current workspace expansion, including a hidden sidebar.
  Quick surface refuses these tree actions.
- `map chord | chord = action` and `map leader chord | chord = action` expand to
  independent bindings, for builtins and custom commands. Preserve existing defaults,
  single-chord grammar and last-write-wins collision behavior. An invalid/empty alternative
  rejects that entire map line with a diagnostic, never half-applies it. `leader =`
  remains one chord; pipes in command text remain literal shell syntax.
- Sidebar dwell tooltip reveals the full truncated workspace/session name, including
  flagged mode. Never displays a stale row after rename/reorder/resize/leave; no input
  interception or popup focus. Reuse native renderer's tooltip layering and DPI units.
- Tree adds per-pane foreground shell names with explicit unknown entries and primary/
  split aliases matching upstream. A Windows snapshot can conservatively report a
  recognized live shell with no descendants; any child/query failure/exited process is
  unknown, NOT an idle prompt or permission to type. No CIM child process per tree poll.
  Verify process identity and avoid OS enumeration under the workspace lock.
- Cursor style/blink already exist in config, Settings and DECSCUSR rendering. Cover
  their live behavior and add strict API validation for these existing fields rather
  than a second settings implementation; preserve compatible file parsing aliases.

## Evidence and bounds

Carry P14's final QA/roadmap receipts forward after merge. Correct its send-guard
comment to say local write failures propagate, not that every process-exit race throws;
transport acceptance is not child consumption/execution (#268). Directly verify that
wording clarification; it does not warrant another P14 review round.

Pinned primary sources: agterm v0.26.0 AppStore.swift (navigateWorkspace/currentWorkspaceID),
ControlServer+WorkspaceCommands.swift, agtermctlKit/WorkspaceCommands.swift, Keymap.swift
and ControlProjection.swift. Windows foreground inference differs from Unix process groups;
document it. No full command-line foreground capture, workspace focus-set expansion,
UIA provider rewrite (#267) or shared conformance expansion before P17.

## Acceptance / delivery

Pure navigation and keymap alternative tests; dispatcher invalid/valid/quick-host cases;
owned GUI suite for wrap/collapse/empty workspace/new placement/filter refusal, multiple
chords and leader dispatch, sidebar tooltip pixels/state, tree split/exit/child behavior,
cursor setting rejection/read-back and rendered shape/blink. Retained-job fixture with
canonical suite token; no clipboard/registry/foreground changes locally. Full legacy
integration remains disposable Windows CI only.

One full Codex-only revmux, batched fixes, narrow confirmation if needed. Update QA and
parity docs, exact-head green CI, merge normally, then start P16. Cosmetic residue does
not require additional rounds.
