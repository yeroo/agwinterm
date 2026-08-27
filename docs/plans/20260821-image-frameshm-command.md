# Host-side support for winterm-browser (`image.frameshm`, cell metrics, `?1016`)

> **Revision 2**, after a revmux triage panel run against the consuming plan
> (`winterm-browser/.revmux/tasks/plan-windows-port/01-initial`). Three changes: the two-slot
> tear-proof claim was wrong as argued and the real invariant is now stated and tested; two further
> host capabilities the consumer cannot work around were found, and one of them **blocks** it.
> Findings cited inline as `[triage: …]`.

## Overview

Add a control-pipe command that accepts browser-rate BGRA frames through a shared memory mapping
instead of through files, so a producer can drive a pane at video rates without a PNG encode or a
disk round-trip on every frame.

The consumer is [winterm-browser](file:///C:/Users/boris/source/winterm-browser), a Windows-native
port of terminal-browser that renders Chromium into an agwinterm pane. Its design brief is
`C:\Users\boris\source\winterm-browser\docs\design\00-port-brief.md`. That project is blocked on
this command, and this plan defines the contract it codes against — so the wire shape here is the
deliverable, not just the implementation.

**Problem it solves.** `image.frame` (`ControlServer.cs:249`) is already a frame path: it takes an
`images[]` array, content-signature-caches each id to skip re-transmits, reads pixel bytes off the
render lock, and swaps placements under a microsecond-scale lock. But every image arrives as a
`path`, so a 30fps full-pane producer must PNG-encode and write a file 30 times a second and
agwinterm must read it back. For a document viewer like docxy that is fine. For a browser it is not.

**Why this is worth doing beyond the browser.** The expensive part of `image.frame` is not the
placement machinery, which is already good — it is the format and the transport. Anything that
generates pixels rather than loading them (a video preview, a plot that animates, a remote frame
buffer) hits the same wall.

## Three deliverables, and only one of them blocks the consumer

Revision 1 had one. The triage panel found two more, and correctly separated them by urgency:

| task | what | consumer impact if absent |
|---|---|---|
| 1–6 | `image.frameshm` | **none.** winterm-browser's Tasks 1–10 have zero dependency on this plan; the file-based `image.frame` already ships every capability its milestone needs. This is an optimisation with a self-guarding precondition and an automatic fallback. |
| **6b** | **cell pixel metrics** | ~~blocking~~ → **degrades**, see below. The consumer has no other source, but a wrong-but-consistent guess costs sharpness, not correctness. |
| 6c | `?1016` SGR-Pixels mouse | degrades. The browser works with pointer resolution quantised to one character cell. |

That ordering is worth holding onto: the piece that looked like the critical path is the one nothing
waits on, and the blocking piece was not in the plan at all.

**Correction (winterm-browser Task 6, 2026-08-21).** 6b is no longer blocking, and the reason
revision 2 thought it was — "a hardcoded `(16, 32)` guess silently produces wrong click targets" —
is wrong as argued. The consumer derives *both* the canvas size (`engine/mod.rs:43-55`) and the
pointer position (`mouse_position_px`) from the same cell size, so a click on a cell lands in that
cell at any scale. A wrong scale costs a resampled image (`Program.Render.cs:77-87` draws the
placement into `Cols * cw` with linear interpolation) and a mis-sized CSS viewport. What *would*
move click targets is the two consumers disagreeing, which was a defect in the consumer and is
fixed there. 6b is now an upgrade from "works, resampled" to "works, sharp", and the consumer has
shipped both the client for it and a `TERMINAL_BROWSER_CELL_PX` override to use meanwhile.

## Context (from discovery)

- `src/Agwinterm.Pty/ControlServer.cs:249` — `image.frame` dispatch; `HandleImageFrame` at ~line 425
  is the model to follow: phase 1 resolves placements and owns pixel bytes **off** the render lock,
  phase 2 takes a brief lock for dictionary/list swaps only.
- `src/Agwinterm.Core/KittyGraphics.cs` — `KittyFormat { Rgb = 24, Rgba = 32, Png = 100 }`,
  `KittyImage(Id, Format, Width, Height, Data)`, `ImagePlacement(ImageId, Row, Col, Cols, Rows,
  SrcX, SrcY, SrcW, SrcH)`.
- `src/Agwinterm.Pty/ISession.cs:64,66` — `Inject(ReadOnlySpan<byte>)` and
  `MutateLocked(Action<ITerminalCore>)`; both take the render lock internally.
- `tests/Agwinterm.Pty.Tests/ControlServerTests.cs` and `ControlApiTests.cs` — where control-verb
  tests live.
- `tests/conformance/control-api.json` — **shared with agliteterm**; the same spec runs in both
  repositories' CI. See the constraint below.

## Constraints

- **Do not add `image.frameshm` to `tests/conformance/control-api.json`.** That file is a contract
  agliteterm must also satisfy, and adding a verb there fails agliteterm's build for a command it
  does not implement. Test this verb in agwinterm's own xunit suites only. If it should later become
  part of the shared dialect, that is a separate decision made with agliteterm in hand.
- **Frames must never tear.** A producer writing while the renderer reads is the default failure
  mode of any shared buffer; the design must make a torn read impossible, not unlikely.
- **A dead or lying producer must not take the terminal down.** The name, dimensions, stride and
  offsets all arrive from another process and every one of them is untrusted input into a pointer
  computation. Validate against the actual mapped view length before any copy.
- **`ControlServer` int args must be JSON numbers, not strings** — `GetInt` throws
  `"requires an element of type 'Number'"` otherwise. The ctl serializes `cargs` by type, so parse
  with `int.TryParse` on the CLI side.
- Backward compatibility: `image.frame` keeps working unchanged. This is an additional verb.
- Build discipline (these have cost hours before): close the running dev instance before building;
  build `src/Agwinterm.Win32` **explicitly** so its copy of `Agwinterm.Pty.dll` is not stale;
  a plain solution build outputs to a *different* directory than a project build.
- Test against an isolated instance: a Debug build auto-uses instance id `agwinterm-dev` with its own
  data dir and pipe. Drive it with `agwintermctl --pipe agwinterm-dev`. **Never** run against the
  real `agwinterm` pipe or data dir.

## Development Approach

- **Testing approach**: Regular (code first, then tests).
- Complete each task fully before moving to the next.
- Make small, focused changes.
- **CRITICAL: every task MUST include new/updated tests** for the code changes in that task.
  - unit tests for new and modified methods, listed as separate checklist items
  - both success and error scenarios — for this plan the error scenarios are the point, since
    most of the risk is malformed input from another process
- **CRITICAL: all tests must pass before starting the next task.**
- **CRITICAL: update this plan file when scope changes during implementation.**
- Maintain backward compatibility with `image.frame`.

## Testing Strategy

- **Unit tests**: required in every task. `tests/Agwinterm.Pty.Tests/` for the control verb and its
  validation; `tests/Agwinterm.Core.Tests/` for any `KittyGraphics` change.
- **Integration test**: a test that creates a real `MemoryMappedFile`, publishes a frame through the
  control server, and asserts the emulator holds the expected `KittyImage` and `ImagePlacement`.
- **No e2e/UI test in this plan.** Visual confirmation arrives with the winterm-browser plan, which
  is the real consumer. Do not build a throwaway pixel producer just to look at it.

## Progress Tracking

- Mark completed items with `[x]` immediately when done.
- Add newly discovered tasks with ➕ prefix.
- Document issues/blockers with ⚠️ prefix.
- Update the plan if implementation deviates from the original scope.

## Implementation Steps

### Task 1: Define the shared-frame wire format and header

- [x] add `docs/specs/image-frameshm.md` documenting the mapping layout and the JSON args, so the
      consuming project has a written contract rather than a reading of the source
- [x] define the mapping layout in `src/Agwinterm.Pty/ShmFrameLayout.cs`: a fixed-size header
      followed by two pixel slots, so the producer writes one slot while the renderer reads the other
- [x] header carries at minimum: a magic value, a layout version, slot count, slot byte stride,
      per-slot `(width, height, stride, format)` and a monotonically increasing `ready` sequence
      number identifying the slot that is complete
- [x] define the JSON args shape: `{"images":[{"id":N,"name":"Local\\...","slot":N,"seq":N,
      "width":N,"height":N,"stride":N,"format":N,"row":N,"col":N,"cols":N,"rows":N}]}` — every
      numeric a JSON number, `name` the mapping name, ids reusing `image.frame` semantics
- [x] **write the literal `Local\` name prefix into the spec.** Task 3 restricts accepted names to a
      fixed prefix; if the string is not in the spec, the producer picks its own and is rejected at
      runtime with a validation error that reads like a bug. Neither document currently contains the
      string a producer must actually use. [triage: major]
- [x] **state the producer slot-reuse invariant explicitly**: a producer must not begin filling a
      slot until the reply for the frame two back has returned. This is what actually makes two slots
      sufficient — see Technical Details. Without it in the spec, a producer that pipelines requests
      to hide pipe latency tears, and nothing forbids that. [triage: major]
- [x] write tests for header encode/decode round-trip
- [x] write tests for rejecting a bad magic, an unknown version and an out-of-range slot index
- [x] run tests — must pass before Task 2

### Task 2: Add BGRA as an internal pixel format

- [x] add `Bgra` to `KittyFormat` in `src/Agwinterm.Core/KittyGraphics.cs` with a value **outside**
      the Kitty wire range (24/32/100 are the protocol's; pick e.g. 132) so it can never be produced
      by parsing a real APC sequence — `Bgra = 132`, as the spec already committed to
- [x] document in the enum why it exists: Direct2D wants `B8G8R8A8_UNORM` and Electron hands out
      BGRA, so carrying BGRA end to end removes a full-frame channel swizzle per frame
- [x] handle the new format in the renderer's texture upload path, taking the no-swizzle route
- [x] verify the emulator's APC parser cannot yield the new value — add a guard if it can
      — ⚠️ **it could, in both cores.** `TerminalEmulator.FinalizeKittyImage` cast `f=` straight to
      the enum (`(KittyFormat)GetKittyInt(keys, "f", 32)`) and `finalize_kitty_image` stored the raw
      int, so terminal output saying `f=132` minted `Bgra` and would have rendered RGBA bytes down
      the no-swizzle path with red and blue exchanged. Both now clamp to the wire range via
      `KittyFormats.ParseWireFormat` / `parse_wire_format`, falling back to `Rgba`.
- [x] write tests for the renderer path selecting no-swizzle for `Bgra` and swizzle for `Rgba`
- [x] write a test asserting a crafted APC sequence declaring `f=132` does not produce `Bgra`
- [x] run tests — must pass before Task 3 — 369 pass (211 Core, 158 Pty); `dotnet format` and
      `cargo clippy` report nothing new on the touched files
- ➕ **scope note:** the conversion moved from the renderer into
      `src/Agwinterm.Core/KittyPixels.cs`. `ToPremultipliedBgra` was a private static in
      `Agwinterm.Win32/Program.Render.cs`, and that project is in no test project, so the
      format-selection branch this task adds would have been untestable where it lived.
      `Program.Render.cs` now calls `KittyPixels.ToPremultipliedBgra`; the PNG arm stays in the
      renderer, since decoding it needs an imaging stack.
- ➕ spec: `docs/specs/image-frameshm.md`'s Format section now states that BGRA alpha is
      **straight, not premultiplied** (agwinterm premultiplies on upload, as it does for `Rgba`),
      and names the two clamp functions. The spec was silent on alpha, which a producer must know.

### Task 3: Open and validate a producer's mapping safely

- [x] add `src/Agwinterm.Pty/ShmFrameReader.cs` that opens a named mapping with
      `MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read)`
- [x] validate before any copy: view length is at least header size; `stride >= width * 4`;
      `height * stride` fits within the slot; the slot's byte range lies inside the view; `width`
      and `height` are positive and within a sane maximum
- [x] restrict accepted names to a fixed prefix in the `Local\` namespace so a request cannot name
      an arbitrary existing object
- [x] return a typed failure rather than throwing for every rejection, so the control server answers
      `{"ok":false,...}` instead of tearing down the connection — `ShmFrameError` grew eleven
      reader-only cases, and `ShmFrameReader.Describe` gives each a one-line reply string
- [x] treat a vanished mapping (producer died) as an ordinary failure, not an exception path
- [x] write tests for each rejection: short view, `stride < width*4`, slot out of range, negative and
      overflowing dimensions, name outside the allowed prefix, nonexistent mapping
- [x] write a test for the success case reading known bytes out of a real `MemoryMappedFile`
- [x] run tests — must pass before Task 4 — 414 pass (203 Pty, 211 Core); `dotnet format` reports
      nothing on the touched files
- ➕ **the reader validates three things the task list did not name**, all of them because the
      alternative is worse than a rejection: `format` must be `132` or `32`, since every stride and
      size bound assumes 4 bytes per pixel and `Rgb = 24` would leave them a third too large;
      a non-zero `width`/`height`/`stride`/`format` in the args must match the slot descriptor,
      because a disagreement means one of the producer's two statements is stale and guessing which
      is worse than saying so; and `seq` may not exceed the header's `ready`, so the reader never
      copies a slot the producer's release fence has not covered. All three are now in the spec.
- ➕ **the mapping is opened per call, not cached.** A producer may tear down and recreate a mapping
      under the same name between frames, and a cached handle would keep the dead section alive and
      read it forever. The open is a syscall; the copy is megabytes.
- ⚠️ **`ShmFrameError.ShortView` is unreachable through the open path.** Windows rounds a section up
      to a page, so `MemoryMappedFile.CreateNew(name, 64)` yields a 4096-byte view. The guard still
      has to hold — it is what makes the 256-byte header read in-bounds — so its test drives the
      internal `TryReadFrame(view, …)` overload with a deliberately narrowed view. Two other tests
      were rewritten around the same rounding: a slot that overruns the view now sits a megabyte
      in, well past any page granularity.
- ➕ spec: `docs/specs/image-frameshm.md` gained a "Validation the reader applies" table listing
      every rejection and its reason, the name's exact character set and case-sensitivity, and the
      `ready`-before-request producer obligation.

### Task 4: Wire `image.frameshm` into the control server

- [x] add `"image.frameshm" => HandleImageFrameShm(s, args)` to the session-targeted dispatch in
      `src/Agwinterm.Pty/ControlServer.cs`
- [x] implement `HandleImageFrameShm` mirroring `HandleImageFrame`'s two-phase structure: resolve
      placements and copy pixel bytes out of the mapping **off** the render lock, then take the
      brief lock for `ClearPlacements` and the placement/image swap
- [x] skip re-transmitting a slot whose `(id, seq)` matches what was last accepted, the shm analogue
      of `image.frame`'s content-signature cache
- [x] return the same result shape as `image.frame` (count, transmits, bytes read) so a caller can
      tell whether a frame actually moved
- [x] write tests for a single frame producing the expected `KittyImage` and `ImagePlacement`
- [x] write tests for the `(id, seq)` cache skipping a repeat and accepting a bumped seq
- [x] write tests for malformed args: missing `images`, missing `name`, a string where a number
      belongs, an id that is not an int
- [x] **write a test where the producer publishes frames faster than the consumer drains them** —
      the case the two-slot scheme does not cover on its own. No test in revision 1 exercised a
      producer running ahead; every test was argument validation or cache behaviour. [triage: major]
- [x] run tests — must pass before Task 5 — 439 pass (228 Pty, 211 Core); `dotnet format` reports
      no new findings on the touched files (the pre-existing `ControlServer.cs` ones only shift
      by the seven lines added above them)

- ➕ **a frame is all-or-nothing.** `HandleImageFrame` skips an image it cannot read and applies the
      rest; `image.frameshm` cannot, because a producer's entries are frames of one composition and
      showing entry 1 of 2 is a visible artefact, not a degraded mode. Any rejection returns before
      phase 2, so the pane keeps the previous frame. Tested by
      `ASecondImageFailingLeavesTheFirstUnapplied`. Now in the spec.
- ➕ **numeric args are parsed by a local `TryNum`, not `GetInt`.** `GetInt` swallows a wrong type
      into its default, and the outer `catch` turns a string where a number belongs into
      `"requires an element of type 'Number'"` — which does not say *which* field was wrong. `TryNum`
      names the field and distinguishes "not a number", "not whole" and "out of 32-bit range".
- ➕ **the `(id, seq)` cache lives in its own `_shmState` table**, not in `_txState`. A file content
      signature and a producer's frame counter are unrelated number spaces keyed by the same image
      id, and letting them alias would make a signature collide with a sequence.
- ➕ **`seq: 0` is never cached.** It means "read whatever is in the slot", which by definition
      cannot be memoised; only a positive seq enters the cache.
- ⚠️ **assert on the parsed `error` string, not on the raw reply.** `JsonSerializer` escapes an
      apostrophe to `'`, so `Assert.Contains("'seq' must be…", resp)` fails against a reply
      that does contain the message. The tests parse the JSON and read `error`.
- ➕ the producer-running-ahead item is covered by three tests, since one number cannot say it:
      `AProducerThatSerialisesOnTheReplyNeverLosesAFrame` (the obligation held — six frames over two
      slots, all intact), `AProducerRunningAheadOnTwoSlotsSubstitutesANewerFrameRatherThanTearing`
      (the obligation broken — the reply carries frame 3's whole pixels under frame 1's request,
      which is substitution, not tearing) and
      `AProducerRunningAheadWithEnoughSlotsDeliversEveryFrameIntact` (the prescribed fix).

### Task 5: Expose the verb on `agwintermctl`

- [x] add the CLI surface in `src/Agwinterm.Ctl` alongside the existing `image` verbs —
      `agwintermctl image frameshm <Local\agwinterm-frame-NAME> [--slot N] [--seq N] ...`, dispatched
      from `Program.cs` next to `image show`/`image sixel`
- [x] parse numeric options with `int.TryParse` and place **ints** into `cargs`, never strings
- [x] keep the CLI shape close to `image frame` so the two read as siblings
- [x] write tests for arg parsing, especially that numerics serialize as JSON numbers —
      `tests/Agwinterm.Pty.Tests/FrameShmCliTests.cs`, 22 tests
- [x] run tests — must pass before Task 6 — 461 pass (250 Pty, 211 Core); `dotnet format` reports
      no findings on the touched files (the pre-existing `RustParityTests.cs` ones are untouched)

- ⚠️ **`image.frame` has no CLI verb**, so "close to `image frame`" had nothing to copy. The shape
      follows `image show` instead — a positional for the resource (there a path, here the mapping
      name) plus one `--flag` per field. The two do read as siblings; the plan item assumed a ctl
      surface that was never built.
- ➕ **arg building lives in `src/Agwinterm.Ctl/FrameShmCli.cs`, not in `Program.cs`.** Top-level
      statements are not addressable from a test project, and the numeric handling is precisely the
      part that must be tested. `Program.cs` keeps only the dispatch arm.
      `tests/Agwinterm.Pty.Tests` now project-references `Agwinterm.Ctl`.
- ➕ **`--images '<json array>'` is accepted as well**, mutually exclusive with the positional name
      and forwarded verbatim. The verb applies an `images[]` array all-or-nothing and a composition
      of several mappings has no flag shape; without this the CLI could not reach a documented
      behaviour of the command.
- ➕ **an omitted flag is omitted from the JSON**, rather than sent as an explicit `0`. The server
      already defines every default; sending `0` would state a value the caller never chose.
- ➕ **`--seq` is parsed as a `long`.** `TryNum` reads `seq` as 64-bit — it is a publish counter, not
      a dimension — and truncating it at the CLI would be a ceiling invented here. Every other field
      is range-checked to 32 bits so the error names the *flag* rather than the JSON property.
- ➕ **only two things are validated locally**: a non-numeric flag value, and a mapping name outside
      the `Local\agwinterm-frame-` prefix. Both are cheap, both are stable parts of the spec, and
      both otherwise cost a pipe round-trip to learn. Ranges, the header and the slot descriptor stay
      the reader's business — duplicating those would let the CLI drift into rejecting frames the
      terminal accepts.
- ➕ a flag given with no value parses as the literal `"true"` in `Program.cs`'s option splitter, so
      `FrameShmCli` skips it rather than failing on it; `--slot` with nothing after it means
      "unset", not an error. Covered by `AValuelessFlagIsIgnoredRatherThanParsedAsTrue`.
- ➕ documented the CLI in `docs/specs/image-frameshm.md` ("Driving it from the CLI"), since the spec
      is what winterm-browser codes against and the ctl is the manual way to exercise it.

### Task 6: Prove it end to end against a live dev instance

- [x] close any running dev instance, then `dotnet build src/Agwinterm.Win32` explicitly
      — no dev instance was running; the only live `Agwinterm.Win32` was the installed **release**
      build under `%LOCALAPPDATA%\Programs\agwinterm`, which is a different instance id, a different
      pipe and (very likely) the terminal this work is being done in. It was left alone.
- [x] launch the Debug build (instance id `agwinterm-dev`, its own pipe and data dir)
- [x] write an integration test that creates a mapping, fills a slot with a recognisable pattern,
      publishes it via the control pipe and asserts the emulator's image and placement state
      — `tests/Agwinterm.Pty.Tests/FrameShmPipeIntegrationTests.cs`, 6 tests over a real
      `NamedPipeServerStream`/`NamedPipeClientStream` pair
- [x] confirm `--pipe agwinterm-dev tree` shows the fresh dev tree, not the real sessions, before
      trusting any result — one workspace, one idle `session 1`, before and after ~800 frames
- [x] measure and record: frames per second sustained, and bytes copied per frame, for a full-pane
      1920x1080 BGRA frame — the winterm-browser plan needs this number to size its frame budget
- [x] write the measurement into `docs/specs/image-frameshm.md`
- [x] run tests — must pass before Task 7

- ➕ **the integration test goes through the pipe *and* through `FrameShmCli`.** Building the
      request line with the ctl's own arg builder puts the CLI's number parsing inside the
      integration path instead of re-implementing it beside it, so a regression that quotes a
      numeric field fails here as well as in `FrameShmCliTests`.
- ➕ **the producer stand-in moved to `tests/Agwinterm.Pty.Tests/ShmTestProducer.cs`**, shared by
      the in-process verb tests and the new pipe tests. A second copy would have been free to drift
      out of agreement with the spec's publish order, which is the one thing both files rely on.
- ➕ **a dead producer's last frame stays re-placeable.** `AProducerThatDiesBetweenFramesIs…`
      first asserted a failure after `Dispose` and got `ok:true`: the `(id, seq)` cache answers
      before the mapping is opened, so re-sending the accepted sequence still succeeds with
      `frame:1/0`. That is correct and worth having — the test now pins both it and the genuine
      failure a *bumped* sequence produces.
- ➕ **measured with a persistent pipe client, not the ctl.** Spawning `agwintermctl.exe` per frame
      caps the loop at ~9fps on process start alone, which measures Windows, not the transport. The
      recorded numbers come from one connection held open across 120 frames; the harness is
      `.ralphex/tmp/bench-frameshm.ps1` (throwaway, not committed).
- ➕ **the number that matters is 6.0–8.4 ms per full-pane 1080p frame** — agwinterm's own share of
      the round trip, ~120–165fps, against 8,294,400 bytes copied once per frame. A cached
      `(id, seq)` re-place costs 0.10–0.13 ms. The full table, its caveats (Debug build, PowerShell
      producer) and what a consumer should conclude are in the spec's "Measured throughput".
- ⚠️ **no screenshot, deliberately.** The Testing Strategy says visual confirmation belongs to the
      consuming project and not to build a throwaway pixel producer to look at; the live evidence
      here is the dev instance accepting ~800 8 MB frames and still answering `ping` and `tree`.

### Task 6b: Publish cell pixel metrics — **blocking for winterm-browser**

A revmux triage panel on the consuming plan found that nothing in agwinterm publishes cell metrics,
and that the consumer has no other way to get them. This is a second cross-repo dependency, and
unlike `image.frameshm` it is not an optimisation — the port stalls without an answer.
[triage: major, verified exhaustively against the full verb dispatch]

Established by that review, so do not re-derive it: the global switch (`ControlServer.cs:140-234`)
and the session-targeted switch (`:241-250`) contain no verb reporting pane geometry, pixel size or
cell metrics. `csi_dispatch` (`native/agwinterm-core/src/emulator.rs:964-1045`) matches
`H f A B C D G d J K X m r L M @ P S T q` and, under `?`, only `h`, `l` and DECRQM `?2026$p` —
there is no `t` arm, so `CSI 14t`/`16t`/`18t` fall to `Unhandled`. The session env dictionary
(`MainWindow.xaml.cs:227-234`) exports `AGWINTERM`, `AGWINTERM_ENABLED`, `AGWINTERM_PIPE`,
`AGWINTERM_SESSION_ID`, `AGWINTERM_WORKSPACE_ID`, `AGWINTERM_WINDOW_ID` — nothing dimensional.

The metrics **exist**: `Agwinterm.Win32/Program.cs:336` computes them, `Program.Render.cs:356` pushes
them into the emulator as `CellPixelWidth`/`CellPixelHeight` (`ITerminalCore.cs:66`,
`TerminalEmulator.cs:479`), and they are used for sixel cell-span at `TerminalEmulator.cs:502`. They
are simply not published.

- [x] choose one: add `CSI 16 t` (and probably `14t`/`18t`) to `csi_dispatch`, or add cell metrics to
      a control-pipe response. XTWINOPS is the standard answer and costs the consumer nothing to
      adopt, since `pixel-core` already queries it at `terminal.rs:880`; a control verb is easier to
      version. Record the reasoning either way.
      — **Chosen by the consumer: a control verb, `session.metrics`.** winterm-browser's Task 6
      settled this and recorded the reasoning in
      `C:\Users\boris\source\winterm-browser\docs\design\04-cell-metrics.md`. Three reasons, all
      specific to that consumer: it builds a control-pipe client for the frame path anyway, so one
      more verb is a method rather than a mechanism; its Windows console backend reads input on a
      reader thread through an inbox, and layering a second deadline-bounded write-then-read-the-
      reply parse on that (for a value wanted at construction *and* on every resize) is the part
      most likely to fail intermittently; and one round trip answers `cols`, `rows`, the cell size
      **and** the pane's pixel box, where `GetConsoleScreenBufferInfo` gives cells only and nothing
      gives the pixel box. This is not an argument against also adding XTWINOPS — it would help
      every other client — only that this consumer neither needs it nor should be blocked on it.
- [ ] implement the chosen mechanism, reading the live `_cellW`/`_cellH`, not a constant.
      **The wire shape the consumer already codes against** (`pixel-core/src/agwinterm.rs`,
      `ControlClient::pane_metrics`):
      request `{"cmd":"session.metrics","target":"<pane id>"}`, reply via `OkRaw` — the way `tree`
      and `window.state` answer, not `Ok` —
      `{"ok":true,"result":{"cols":132,"rows":37,"cellWidth":9,"cellHeight":19,"widthPx":1188,"heightPx":703}}`.
      camelCase, matching `window.state`'s `sidebarVisible`. `cellWidth`/`cellHeight` are the live
      values in device pixels and are the only two fields that matter; the consumer reads a reply
      missing either, or with either zero, as "no metrics" rather than as an error, and reads
      `unknown command 'session.metrics'` as a capability gap it latches and stops asking about.
      `cols`/`rows`/`widthPx`/`heightPx` are optional and default to zero.
- [ ] note that `Agwinterm.App/MainWindow.xaml.cs` is **dead code** — absent from `Agwinterm.slnx`.
      The live values are in `Agwinterm.Win32`. Do not implement against the dead project.
- [ ] write tests for the response carrying the live metrics, and for it tracking a font-size change
- [ ] if XTWINOPS: write tests that the new `t` arm does not swallow sequences previously `Unhandled`
- [ ] document the mechanism in `docs/specs/image-frameshm.md` or its own spec, and tell
      winterm-browser's Task 6, which is where it is consumed
      — ➕ the telling has already happened in the other direction: winterm-browser's Task 6 shipped
      the client, fixed the wire shape above, and recorded it in its own
      `docs/design/04-cell-metrics.md`. What is left here is agwinterm's own spec page.
      ⚠️ **The consumer is no longer blocked.** Its Task 6 established that a wrong-but-*consistent*
      cell size does not move click targets — the canvas (`engine/mod.rs:43-55`) and the pointer
      (`mouse_position_px`) are both derived from the same number, so a click on a cell lands in
      that cell whatever the scale. What it costs is resolution (agwinterm resamples the placement
      with `BitmapInterpolationMode.Linear` at `Program.Render.cs:77-87`) and a mis-sized CSS
      viewport. The consumer therefore ships with `TERMINAL_BROWSER_CELL_PX` as an explicit
      override and a logged fallback, and this task upgrades it from "works, resampled" to "works,
      sharp" rather than unblocking it.
- [ ] run tests — must pass before Task 7

### Task 6c: `?1016` SGR-Pixels mouse — optional, lifts a product ceiling

Not blocking: without it the browser works, with pointer resolution quantised to one character cell.
Worth doing because that ceiling lands on the headline feature. [triage: major, confirmed]

`Program.Input.cs:442-453` computes `col = (pxX - ox) / cw` and `row = (pxY - oy) / ch` as integers
and emits `\x1b[<btn;col+1;row+1M` — the sub-cell offset is discarded at the encode site and is
unrecoverable downstream. `?1016` appears nowhere in `src` or `native`; both cores handle
1000/1002/1003/1006 only (`TerminalEmulator.cs:402-405`, `emulator.rs:620-623`). The consumer
already probes for it — `pixel-core` requests `\x1b[?1016h` at `terminal.rs:361` and sends the DECRQM
`\x1b[?1016$p` at `:945-948` — and agwinterm's DECRQM handler (`emulator.rs:768-774`) answers only
for mode 2026, so the probe times out and `reports_pixel_mouse()` is false. That flag gates real
behaviour in the consumer: hover and pairing get no position at all (`engine/pointer.rs:61`).

- [ ] add `1016` to the mode handlers in **both** cores, keeping them in agreement
- [ ] answer DECRQM for `?1016$p` so a client can discover support instead of timing out
- [ ] add a pixel-coordinate branch to `SendMouse` that emits pixel values when `?1016` is active,
      leaving the cell path untouched when it is not
- [ ] write tests for mode set/reset, the DECRQM reply, and the encoder emitting pixel coordinates
      only when the mode is on
- [ ] write a test that sub-cell movement produces distinct reports under `?1016` and identical ones
      without it — the whole point of the change
- [ ] run tests — must pass before Task 7

### Task 7: Verify acceptance criteria

- [ ] verify `image.frame` still behaves exactly as before (no regression in the file path)
- [ ] verify every rejection in Task 3 answers `{"ok":false,...}` and leaves the session usable
- [ ] verify a producer killed mid-frame does not wedge or crash the terminal
- [ ] verify a producer running ahead of the consumer either cannot tear or is rejected — whichever
      the Task 1 invariant chose — and that the spec says which
- [ ] verify the cell metrics from Task 6b track a live font-size change rather than being sampled once
- [ ] verify `?1016` off is byte-identical to today's mouse encoding, so existing apps see no change
- [ ] confirm `tests/conformance/control-api.json` is **unchanged** — note this now covers three new
      capabilities, none of which agliteterm implements
- [ ] run the full unit test suite
- [ ] run the linter — all issues fixed
- [ ] verify test coverage meets the project standard

### Task 8: [Final] Update documentation

- [ ] update `docs/agterm-gap-analysis.md:41`, whose "Image protocols / graphics" line currently
      reads *Partial* and describes only the out-of-band file path
- [ ] update the agent skill's image section (`src/Agwinterm.Pty/AgentSkill.cs`) if the verb should
      be discoverable to agents
- [ ] cross-link `docs/specs/image-frameshm.md` from the winterm-browser brief

## Technical Details

**Why two slots and a sequence number.** The producer fills a slot completely, then publishes by
writing `ready = seq` with a release barrier; the renderer reads `ready` with an acquire barrier and
copies from the slot it names. No lock is shared across the process boundary, and a producer that
dies mid-write leaves a half-written slot that is simply never published.

**What that alternation does and does not guarantee** — an earlier draft of this paragraph claimed
"a frame in flight is therefore never the frame being read", and that does not follow.
[triage: major, two findings] What two slots plus the barrier guarantee is narrower: *a half-written
slot is never published*. With only two slots, a producer running two frames ahead of the consumer
wraps back onto the slot being copied, and the barrier orders the producer's publish, not the
consumer's copy. An 8 MB copy for a 1920×1080 BGRA frame is not a narrow window.

What actually closes it is the control pipe being **request/response**: `HandleImageFrame` returns
`Ok($"frame:{count}/{transmits}")` at `ControlServer.cs:481`, after the phase-1 copy at `:457` and
the locked swap at `:467-478`. A producer that serializes on the reply can never have two
unacknowledged frames outstanding, so for that producer two slots are sufficient and tearing is
genuinely impossible.

That makes the invariant a **property of the producer, not of the layout** — which is exactly why it
has to be written into the spec rather than left implicit. The verb is advertised here as
general-purpose beyond the browser; a future producer that pipelines to hide pipe latency is doing
something no document forbids, and it tears. If the verb should be safe for pipelining producers,
that is a larger design: an in-use word per slot that the reader sets and clears, or more slots plus
a consumer-published released-sequence. Decide which before the layout is a published contract.

The consequence of getting it wrong is a visibly torn frame, not a crash — Task 3's bounds validation
keeps the copy inside the view either way.

**Why copy at all.** The renderer could map the producer's pixels directly, but then a producer that
exits invalidates a view the renderer is still using. Copying under a brief lock costs one memcpy and
removes an entire class of lifetime bug. A genuine zero-copy path — Electron's D3D11 shared texture
handle opened directly by Direct2D — is a later, separate piece of work and is explicitly out of
scope here.

**Format.** BGRA end to end. Electron's `paint` gives BGRA, Direct2D wants `B8G8R8A8_UNORM`, so a
swizzle would be pure loss at both ends.

## Post-Completion

**Manual verification**:
- Sustained-load behaviour: leave a producer running at full rate for a long stretch and watch for
  drift in memory and handles.
- Behaviour when the pane is not visible — agwinterm gates repaint on visibility, so a background
  pane should stop costing anything.

**External system updates**:
- winterm-browser codes against `docs/specs/image-frameshm.md`; a change to the layout after that
  project starts is a breaking change for it.
- agliteterm speaks the agwintermctl dialect via the shared conformance contract. This verb is
  deliberately outside it; revisit only with agliteterm in hand.
