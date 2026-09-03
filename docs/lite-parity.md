# agliteterm ↔ agwinterm parity tracker

**Goal: the two products expose the same features.** agliteterm is a separate product with its own
repository, not a cut-down build — so a gap here is a gap, not a design decision, unless this file
says otherwise and gives the reason.

- agwinterm **0.17.7** released (main ahead), agliteterm **0.17.13** released.
- Verb lists below were **extracted from both dispatchers** on 2026-09-02, not written from memory:
  85 verbs in agwinterm, 41 in agliteterm.
- Update this file in the PR that closes an item.
- The verbs below are cut into runnable batches (P6–P12) in
  [plans/2026-09-03-parity-batches.md](plans/2026-09-03-parity-batches.md).

The control API is the half that has to match exactly: `tests/conformance/control-api.json` in this
repo is the canonical contract, agliteterm's CI checks its copy against it, and an agent written
against one product should work against the other. The UI half can differ where the platform or the
product's purpose justifies it.

---

## Control API: 44 verbs agliteterm does not answer

Grouped by what they cost an agent, not alphabetically.

### Selection — the sharpest gap
`selection.all` · `selection.clear` · `selection.copy` · `selection.finalize`

lite has `session.copy` (the selection's text) but no way to **make**, clear or finalise one. So
copy tooling, anything reading a user's selection, and the QA cases that drive selection through the
API all stop at the door. A step that calls `selection clear` there does nothing and returns an
error most callers ignore — a setup that silently did not happen, which is the failure mode the QA
cases exist to prevent (`qa/product.md` in that repo says so).

**Size:** small. lite already has the selection model behind `session.copy`.

### Reading and driving a pane
`session.search` · `session.focus` · `session.switch` · `session.resize` · `session.background` ·
`session.readonly` · `session.bind` · `session.restore` · `session.write`

`session.write` is in flight (agliteterm #15). `session.readonly` matters most of the rest: it is how
you stop stray keys reaching a running agent.

### Images
`image.show` · `image.sixel` · `image.clear` · `image.frame`

lite renders no images at all. `image.frameshm` (shared-memory frame delivery) is the one ConPTY
makes necessary, and it is agwinterm-only. **Size:** large.

### Configuration and appearance
`config.get` · `config.list` · `config.set` · `theme.list` · `theme.set` · `omp.list` · `omp.set` ·
`font` · `settings.open` · `keymap.reload` · `profiles.list` · `profiles.reload`

lite keeps settings in the registry (`HKCU\Software\agliteterm`) with a Properties dialog, so this is
not a straight port — but `config.get`/`set` over the API is what lets an agent set up a workspace
without a human clicking. **Size:** medium as a group, small each.

### Commands and installers
`command.list` · `command.run` · `command.leader` · `install.cli` · `install.hooks` ·
`install.shell` · `app.update`

lite has `install.skill` only.

### Agent integration
`claude.adopt` · `claude.yolo` · `claude.update`

Adoption is the interesting one: pointing the terminal at an already-running Claude session.

### Everything else
`broadcast` · `notify` · `dashboard` · `restore.clear` · `workspace.move`

---

## Where agliteterm is AHEAD

Not a one-way list, and these should move the other way.

- **`session.split` returns the split's id.** agwinterm's returns nothing, and a hidden split shell
  has no other handle. **agwinterm should copy this** — tracked in `agterm-parity.md` too.
- **Splits scroll into main-screen history on the alt screen.** lite's renderer and hit-test derive
  their row from the same offset on either screen; agwinterm pins the alt screen to 0. Both are
  self-consistent (highlight and clipboard agree either way), so this is a difference to decide about
  rather than a defect — see `qa/product.md` in the lite repo.
- **An unknown workspace is refused, not silently swapped** for the active one on `session.new`.
  agwinterm falls back. Silently falling back is how a caller ends up believing it placed a session
  somewhere it did not; **agwinterm should probably refuse too**, which is a contract change worth
  making deliberately.

---

## UI and terminal features agliteterm lacks

Not exhaustive the way the verb list is — these are the gaps found while working on both products,
and the list should grow as more turn up.

| Feature | Notes |
| --- | --- |
| Mark mode (keyboard selection) | agwinterm: Ctrl+Shift+M, arrows, Enter copies |
| Select All | no equivalent |
| Drag-autoscroll | dragging past the pane edge does not extend the selection |
| Configurable scrollback | lite does not call `agwcore_emu_set_scrollback`; the cap is the core default |
| Images / graphics | see the `image.*` verbs above |
| Dashboard, quick-terminal parity, multi-window | agwinterm has a window library; lite has one window plus popups |
| Wheel over the pane | a posted `WM_MOUSEWHEEL` does not reach lite's handler (harness finding, 0.17.11) |

---

## What is deliberately different

- **Settings storage.** agwinterm reads `agwinterm.conf` and honours `--app-id`; lite uses the
  registry and a `%LOCALAPPDATA%` override. That is why the two QA adapters isolate differently, and
  it is not worth unifying.
- **Splits as sessions.** lite models a split as a hidden session; agwinterm models panes inside a
  session. Behaviour matches now (a split belongs to its session, closes with it, restores with it),
  and the internal shape can stay different.
- **The native core is shared.** Both load `agwinterm_core.dll` across the same C ABI, so emulator
  behaviour — widths, scrollback, alt screen — is common by construction. A difference there is a
  bug in one of the clients, not a parity gap.

---

*Companion to [agterm-parity.md](agterm-parity.md), which tracks both products against umputun's
agterm. This file tracks them against each other.*
