# The control API

agwinterm is scriptable through a local named pipe speaking newline-delimited JSON, with
`agwintermctl` as the CLI wrapper. `agwintermctl --help` lists the core verbs and the agent skill
(`agwintermctl install skill`) carries the full set; this page collects the
replies a script can rely on, and the ones that answer a question a script would otherwise have to
guess at.

Related pages: [launching a session command](session-commands.md), [session HUD](session-hud.md),
[workspace navigation](navigation.md), [quick terminal](quick-terminal.md),
[native picker](native-picker.md), [`image frameshm`](specs/image-frameshm.md). The subset agliteterm
shares is canonical in [`tests/conformance/control-api.json`](../tests/conformance/control-api.json).

## Reaching an instance

Inside a session you get `AGWINTERM_SESSION_ID`, `AGWINTERM_WINDOW_ID` and `AGWINTERM_PIPE`, so a
bare `agwintermctl` targets the instance and pane it runs in. `--pipe <name>` addresses another one
(a Debug build is `agwinterm-dev`). Target defaults to `$AGWINTERM_SESSION_ID` when not given. Run
`agwintermctl install skill` (or the palette entry) to teach Claude Code / Codex the full verb set.

## A tour

```powershell
agwintermctl tree --json                     # workspace/session tree (+ splits, badges, overlays, paneOverlays)
agwintermctl window state                    # sidebar/fullscreen/active read-back
agwintermctl sidebar width 300               # move the divider; the reply is the width in effect
agwintermctl session split on --axis horizontal   # stack the panes; the reply is the split pane's id
agwintermctl session split close --target <pane>  # close EITHER pane; the reply is the survivor's id
agwintermctl session swap                    # the two panes change places; every id stays where it was
agwintermctl session status blocked --sound  # report agent status (a dot + bell in the UI)
agwintermctl session new --name build        # from a pane: lands in THAT pane's workspace, not the last-clicked one
agwintermctl session new --name build --workspace-name CI --create-workspace
agwintermctl session new --workspace no-such   # refused, no session: an unknown workspace is never swapped for the active one
agwintermctl session type "npm test`n"       # type into the active session
@"
say "hi"
"@ | agwintermctl session type --stdin   # text with quotes/newlines: stdin as bytes (see below)
agwintermctl session overlay open "git diff" --size-percent 60
agwintermctl session hud "Reviewing" --spinner --position top-right  # passive status, shell stays usable
agwintermctl session hud update "Ready"                             # replace message in place
agwintermctl session hud close
agwintermctl session overlay open "lazygit" --pane right    # over the right pane only; the left pane stays live
agwintermctl session overlay text --pane right --all        # the overlay's own screen + scrollback (session text reads the shell under it)
agwintermctl session restore "npm run dev" --target <pane>   # re-run on every restart; reply names the pane
agwintermctl restore capture                 # capture every pane's running command into its restore slot NOW, not only at quit
agwintermctl session rename api              # the custom name in the sidebar and title bar
agwintermctl session context "reviewing PR 226, left pane is the diff"   # one line of what the session is FOR; survives a restart
agwintermctl dashboard build test deploy     # grid overview of chosen sessions
agwintermctl theme set "Tokyo Night"         # retint the whole window
agwintermctl window new --name scratchpad    # open a second window
agwintermctl surface cursor --target <pane>  # the caret column of a pane, as a bare integer
agwintermctl version                         # which CLI ran, and which app answered the pipe
```

## `surface cursor`

Returns the caret **column** and nothing else, so "is that agent's composer empty before I type into
it" is a comparison, not a hunt for its placeholder text. A different column is proof of a draft; the
same column is not proof of none (a draft exactly one wrap width long parks the caret where it
started), so back a match with `session text` of that row. It resolves its target exactly as
`session text` and `session type` do, so the pane you check is the pane you type into.

## `statusChangedAt` in `tree --json`

Each session reports `statusChangedAt` — epoch **seconds** of the last status write on the pane whose
status the node shows, restamped even when the same status is re-asserted. `now - statusChangedAt` is
how long ago that agent last spoke, which is what separates a working agent from one whose hook died.

In a split session it is the clock of the pane whose status won: a write to a pane that loses the
aggregate does not move it, but panes tied at the winning status all do. Two `active` panes report
the freshest of the two, so the session-level age cannot tell a dead hook from a live one beside it
(nothing reports the stamp per pane).

## `version`

Prints two greppable lines: the `cli` that ran (version and its resolved path — several
`agwintermctl.exe` can coexist off `PATH`) and the `app` that answered (version and pipe). It exits 0
and still prints the `cli` line when nothing is listening, which is the case it exists for. `--json`
gives the machine-readable form.

## `session type --stdin`

Takes the text from standard input as bytes, so quotes, newlines, runs of spaces and a leading `--`
arrive intact: the argv form re-joins positionals with one space and the option parser eats a leading
`--`, both silently.

- Exactly one trailing newline is dropped (the one the shell adds), so end with two to press Enter.
- Invalid UTF-8 is refused with its byte offset and nothing is sent.
- `--stdin` beside positional text or `--select` is refused as ambiguous.
- There is no `quick type` verb: the quick terminal is a pane whose id starts with `quick:`, so it is
  `session type --stdin --target quick:`.

`session write` is display-only: the program's next repaint, or any pane resize, paints over what it
wrote.

## `session overlay`

`open --size-percent N` and `resize --size-percent N` take 1..100 and **refuse** anything else,
naming the value and the range. `0`, `150` and `sixty` used to open a full-screen overlay and report
success; now nothing opens and `ok` is false. Omit the flag for the full content region.
`resized N%` is always the N that was asked for.

The verb's other failures are refusals too:

- `open` with no command;
- `open` and `resize` whenever no session resolves (a `--target` that matches nothing, or no target
  and no active session);
- a `close` whose `--target` names nothing;
- `open`, `close` and `resize` without `--pane` whose `--target` names one pane of a split session (a
  session-wide overlay covers the whole session — the refusal names the session id to pass instead,
  and `--pane left|right` is how one pane is named);
- `resize` with no overlay open.

The pane form has its own, each starting with agterm's phrase:

- `pane not visible` (`--pane right` on a one-pane session);
- `pane overlay already open` (a pane slot never silently replaces — the session-wide slot still
  does);
- `no overlay` (`copy` / `text` / `result` on an empty slot), `no selection`;
- `overlay still running` / `no overlay result` (`result --pane`);
- a `--pane` word other than exactly `left`/`right`, a `--target` pane id naming the other side than
  `--pane`, `--pane` with `--size-percent`, and `resize --pane` are refused with nothing sent (a pane
  overlay is always full-pane).

Not refusals:

- `close` stays `ok` when the session resolves and has no overlay, or when the target is absent,
  empty or `active` while nothing is active — there is nothing to close, and nothing is what you
  asked for.
- A `resize` whose reply says the window did not run it within 15 s is still queued and may land
  later.
- `open --block` answers the outcome of the overlay it opened: `closed` when that overlay was closed
  or replaced first, `exit 1` also when its program could not be started at all, and `ok:false` with
  the status unknown when the window closed under it. A blocking open closes its pane as it replies,
  so to tell a program that could not start from one that ran and failed, open with `--wait` instead,
  poll `overlay result` for its `exit N`, read the pane by the id the open returned, and close it by
  that id — a close whose overlay is already gone is refused, which is the same end state.

`overlay result` stays one value per window, written by whichever session's session-wide overlay
exits next. `result --pane left|right` (or `result --target <pane overlay id>` while that overlay is
up) is that slot's own `exit N`, and a pane overlay's exit never writes the window-wide value.

## `session restore`

Replies `{action, pane, session}` instead of the word "pinned".

- `pane` is the pane the target resolved to: a session name lands on its focused pane; a session id
  on the pane that carries that id while one does (pane 0 of a fresh session, either side after a
  `session swap`), and on the focused pane while none does, i.e. once the carrier was closed by any
  path — exactly as `session type` does.
- `action` is `pinned` or `cleared` (`none` clears).
- The target is mandatory, because a pin outlives whatever pane is active now.
- `tree --json` reads the pins back as `restoreCommands`, an object keyed by pane id that lists only
  pinned panes.

## `restore capture`

`restore capture [--target ID]` fills the captured-command slot of every real pane (or of one)
**now** and saves, and replies per pane with what it found. Before this verb the capture ran exactly
once, on a clean quit, which is the one exit a crash, a `Stop-Process`, a power cut or a missed update
never reaches, so the restart that most needed the command was the restart guaranteed to have none.

- A pane's `captured` is the command line its shell is running, or `null` when the shell has no child
  worth restoring (the shells on `restore-denylist.conf` never count). Null is written too: a fresh
  capture replaces an older checkpoint.
- The reply's `replayOnRestore` is the `restore-commands` setting, off by default. The capture always
  happens; the typing-back at restart only happens when that is on, so a script that wants the replay
  checks the flag rather than assuming.
- It takes one process query for all panes, so allow seconds, not milliseconds.
- An unknown target, an empty one (`--target ''` — only an OMITTED target means every pane), a
  scratch/overlay/quick pane, or a query that fails is refused and nothing is written.
- One refusal leaves something behind and says so — "captured into memory but the state file could
  not be written": the slots are filled (`tree` shows them), but this save did not put the new
  checkpoint on disk. An earlier checkpoint may remain; fix the state directory and capture again.
- `tree --json` reads the slots back as `capturedCommands`, keyed by pane id like `restoreCommands`.

## `session context` and `session rename`

`session context "<text>"` sets one line of **what a session is for**, shown dimmed after the name in
the title bar and the sidebar row and on the session palette's second line, where a name has to stay
short. It survives a restart and an undo-close, and `tree --json` carries it as `context` on the
session node, so an agent that sets it when it starts a task leaves a note every other agent can read.

- It is one line by rule: a newline, tab or other control character is refused rather than drawn (the
  control-byte class from #213), blank is refused, and more than 200 characters is refused because
  the ceiling is the width of the row, not a storage limit.
- `--clear` removes it, and `--stdin` takes it as bytes the way `session type --stdin` does.
- The reply `{session, context}` is the value **in effect** after the write, read off the session
  rather than echoed from the request.

`session rename <name>` is its neighbour: the short custom name, same target resolution, and the two
survive each other (renaming does not clear the context).

## `session split` and `session swap`

`session split` answers the **pane id** it produced instead of the word "split", so the shell you just
asked for is addressable from the reply: `on` on an already-split session answers the existing split
pane's id and changes nothing, `off` answers the survivor's.

**To get a pane, use `on`.** The bare form is a toggle: on a session that is already split it closes
the split and answers the survivor's id — the session's own shell — so a script that asks twice ends
up addressing the pane it runs in.

- `--axis vertical|horizontal` picks the arrangement in agterm's words — vertical is left/right
  panes, horizontal is top/bottom. It is remembered for the life of the session, through `off`, and
  across a restart only while the session is still split (a collapsed session writes no axis key);
  `tree --json` carries it as `axis` on a split session.
- `session split close --target <pane>` closes **either** pane and answers the survivor's id (`off`
  can only keep pane 0); a one-pane session is refused, because `session close` is that verb.
- `session swap` exchanges the two panes and keeps the axis, the divider position, the focus's pane
  and **every id**: a swap moves panes, never ids, so a handle you hold keeps reaching the same shell
  on the other side. Its reply `{session, paneIds, focusedPane, axis}` is the tree's split block after
  the swap.

## `sidebar width`

`sidebar width [N]` reads or sets the sidebar width in device-independent pixels and replies
`{width, visible, applied}` with the width **actually in effect**, so a script compares what it asked
for with what it got.

- Outside 120..600 is refused with the range named and nothing moves (`sidebar hide` is how to ask for
  none).
- A set while the sidebar is hidden is remembered and persisted but reported `applied:false` rather
  than as a width nobody can see.
- `sidebar state` reads `visible tree 220`: visibility, mode and width.
- A `sidebar` op the app cannot do is refused instead of acknowledged (`on`/`off` are real aliases of
  `show`/`hide`).

## `session new` lands beside its caller

`session new` with no `--workspace` creates the session **in the caller's own workspace**: the CLI
sends the pane it runs in (its `AGWINTERM_SESSION_ID`), and the session goes next to it. Before, it
went to the *active* workspace, a global the UI moves on every click, so an agent creating several
sessions scattered them wherever the user had last clicked. An agent gets sessions beside itself
unless it says otherwise, and `--workspace` / `--workspace-name` are how it says otherwise.

Only a CLI with no pane identity, or one whose pane has since been closed, still lands in the active
workspace; a stale caller is not refused, because that would break a working script.
