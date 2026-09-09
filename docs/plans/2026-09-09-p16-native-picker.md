# P16 — native API picker

Authority: Boris requested P14–P17 sequentially. Base: P15 merge `482612b`.
Codex owns this worktree/implementation, Codex-only review, integration and normal
PR merge. No release/install. P17 mirrors the resulting contract afterward.

## Contract and design

- Wire verbs `pick.open`, `pick.result`, `pick.cancel` (roadmap `control.pick` is the
  feature name). Open args: items `{id,label,subtitle?}`, prompt/query strings,
  allowCustom/follow booleans. Root `window` uses the existing Windows dialect.
  No session target on open; result/cancel target is an exact picker ID.
- At most 1000 items; exact unique item IDs (empty allowed), nonempty labels, no
  C0/DEL in displayed text. Native safety bounds: 4096 UTF-16 units per field and
  1 MiB UTF-8 open payload/stdin. Refuse malformed/oversize input before UI mutation.
  Empty items require allowCustom. Prompt/query also reject display controls.
  Review refinement: aggregate label-unit × distinct-query-term work is capped at 64 Mi;
  repeated terms share matching work while retaining ranking weight.
- Open returns `{id}`; result returns `{pick:{result:pending|picked|custom|cancelled,
  id?,label?,index?,query?}}`. Picked ID identifies the item, index its original
  zero-based position. Cancel is idempotent for retained terminal outcomes.
- One pending picker per library window; busy refuses, never replaces. Retain eight
  answers per live window and 32 after window closure, evicting by answer order.
  Close cancels pending first. Unscoped result/cancel find exact IDs across focus and
  window closure/deletion; explicit window selectors restrict ownership. Quick refuses.
- Pure bounded Core state/filter policy; standard owned modeless Win32 edit/list/button
  controls, no dependency on the terminal's process-wide UIA provider. Keyboard arrows,
  Enter, Escape, Tab and mouse selection operate on the picker, never enter the PTY.
  Conflicting settings/rename/context surfaces refuse; close ordinary palette on open.
- Show without activating another app/window. Explicit follow requests normal OS focus;
  user activation selects the owning library context. Background cancellation never
  grabs focus. Owner close settles state and destroys native resources.
- Match pinned upstream v0.26.0 picker filtering: labels ONLY, not subtitles (which can
  contain consequence warnings). Case-insensitive whitespace terms, prefix/substring/
  subsequence ranking, label tie-break and stable input order for blank query. Filtering
  resets selection; arrows clamp. Custom row only when allowed, query nonblank and no
  item matches; custom result uses trimmed query. Item data is never executed.
- CLI `pick [open]` reads UTF-8 JSON array or plain lines from stdin (blank lines omitted,
  other line spelling retained; label=id). `--no-block` prints `{id}`. Otherwise poll
  exact ID without carrying the moving window selector: ten 100ms waits, then 500ms.
  No implicit answer deadline. Best-effort cancel on Ctrl+C/protocol/transport failure.
  The initial UI open has a withdrawable 10s wait. Once its reply exposes the ID,
  failures can cancel exactly; a lost open reply before ID receipt still needs user
  Escape/Cancel (no exactly-once transport guarantee). `--input-format lines|json`
  disambiguates bracket-prefixed plain lines; default auto recognizes leading `[`.
  Picked/custom exit0, cancelled2, one-shot pending1, transport/refusal1. Successful
  open/result output is JSON payload even under --json; cancel uses normal ack envelope.

## Grounding and acceptance

Pinned primary upstream files read: ControlPick.swift, Pick.swift, ControlDispatcher+Pick.swift,
ControlServer+Pick.swift, MiscCommands.swift Pick section, SocketClient picker helpers,
Fuzzy.swift, Palette.swift picker filtering, PaletteSearchKeys.swift, PickTests.swift,
PickCustomRowTests.swift, PickFocusGuardTests.swift, native-dir-picker cookbook.
Windows [IsDialogMessage](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-isdialogmessagew)
supports modeless controls; handled messages must not be dispatched twice.
[ShowWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow)
provides nonactivating presentation. These inform the native message/focus design.

Pure validation/filter/retention tests; dispatcher malformed/ownership cases; injectable
CLI transport/wait/output tests. Retained-job private GUI fixture under canonical suite
token: controls, filtering, original index, custom, cancel, busy, readonly/input isolation,
geometry, window lifecycle. Actual foreground/multi-window activation CI-only. Full legacy
integration disposable CI-only. One broad Codex review, batched fixes and narrow confirmation;
exact-head green CI and merge before P17. No shared conformance expansion until P17.

Carry P15 final receipts into QA here. Its pre-existing full-workspace split/auxiliary
teardown gap remains #270; do not claim the picker repairs that separate deletion path.
