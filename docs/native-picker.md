# Native picker

P16 adds a native, window-owned picker to agwinterm. Item data is never executed.
The agliteterm mirror and shared conformance expansion are P17 work.

```powershell
'Alpha','Beta' | agwintermctl pick --prompt 'Choose a project'
'[{"id":"repo-a","label":"Alpha","subtitle":"Read-only checkout"}]' |
    agwintermctl pick --query al --no-block
agwintermctl pick result PICKER-ID
agwintermctl pick cancel PICKER-ID
```

`pick [open]` reads UTF-8 JSON items or plain lines from stdin until EOF. Empty/whitespace
lines are omitted; other line spelling is preserved as both label and ID. Duplicate IDs
refuse, including duplicate plain lines. JSON IDs are exact/case-sensitive; empty IDs and
duplicate labels are legal. Each item needs string `id` and nonempty `label`; `subtitle`
is optional. `--prompt`, `--query`, `--allow-custom`, `--window`, `--follow`, `--no-block`
are open options. `--pipe`/`--socket` choose transport. There is no session target or
implicit `$AGWINTERM_SESSION_ID` selection for a picker.

Open displays without stealing another application's foreground. `--follow` requests
normal OS activation (not guaranteed by Windows). Arrows clamp, typing resets selection,
Enter/Select/double-click choose, Escape/Cancel/window close cancel, Tab moves between
native controls. Keyboard input remains in the picker, including when the owner receives
a key message. Explicit API writes to a terminal remain independent.

Filtering searches labels only, **never consequence subtitles**. Case-insensitive whitespace
terms use prefix, substring, then subsequence ranking. Ties use ordinal case-insensitive
label order; blank queries preserve caller order. This native filter operates on UTF-16
with invariant case folding. `--allow-custom` offers a custom row only for a nonblank query
with no matching items; its result contains the trimmed query.

## Wire and results

Requests use the existing root `window` selector (not `args.window`).

```json
{"cmd":"pick.open","window":"active","args":{"items":[{"id":"a","label":"Alpha"}],"allowCustom":false,"follow":false}}
{"cmd":"pick.result","target":"EXACT-PICKER-ID"}
{"cmd":"pick.cancel","target":"EXACT-PICKER-ID"}
```

Wire replies have the normal `{ok,result}` / `{ok:false,error}` envelope. Open's result
is `{id}`; result's is `{pick:OUTCOME}`; cancel's is the string `cancelled`.

| Outcome | Fields | CLI exit |
|---|---|---|
| pending | `result: "pending"` | 1 for one-shot result |
| picked | `result: "picked", id, label, index` | 0 |
| custom | `result: "custom", query` | 0 |
| cancelled | `result: "cancelled"` | 2 |

Picked ID belongs to the item; `index` is its original zero-based input position.
The CLI deliberately prints payload JSON: `{id}` for no-block, the outcome for result
or a completed blocking open, even with `--json`. Cancel prints `cancelled`, or the
normal envelope under `--json`. Refusal/transport/protocol failures exit 1; invalid CLI
arguments/input exit 2, with diagnostics on stderr rather than a success payload.

A blocking open waits without an overall answer deadline: ten 100ms waits, then 500ms.
Each transport operation is bounded. Polls and best-effort failure/Ctrl+C cancellation
use the exact ID **without retaining the moving window selector**. `--no-block` transfers
pending ownership to the caller, who must later read/cancel it. One-shot result/cancel
may supply `--window` to restrict ownership; omitted window searches exact IDs globally.

## Bounds and lifetime

One pending picker per open library window; a second open refuses rather than replacing.
Conflicting settings, rename, context-menu, dashboard or drag operations refuse opening;
an ordinary command palette closes. Quick terminal is not a picker owner. Readonly
terminal state does not prohibit this separate native UI.

At most 1000 items, 4096 UTF-16 units per field, 1 MiB UTF-8 serialized open args and
1 MiB stdin. Labels/subtitles/prompt/query reject C0 and DEL display controls. IDs are
opaque data. Empty items require `allowCustom`. Results are in-memory, never restored:
eight completed answers per live window, 32 across closed windows, evicted by answer
order rather than window-close order. Owner closure cancels pending before retention.
Global exact-ID reads work after owner closure/deletion until eviction. An explicit
selector must still resolve the owning library metadata; deleted metadata no longer does.
Cancel is idempotent for retained completed results and cannot overwrite a choice.

Standard native controls supply their own accessibility; this does not repair the
terminal's process-wide UIA provider limitation (#267) or workspace teardown issue #270.
