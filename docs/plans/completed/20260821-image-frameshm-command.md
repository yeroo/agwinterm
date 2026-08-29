# Host-side support for winterm-browser (`image.frameshm`, cell metrics, `?1016`)

> **Revision 3**, after implementation and consumer validation. Revision 2 corrected the two-slot
> tear-proof claim and added two host capabilities found by a revmux triage panel
> (`winterm-browser/.revmux/tasks/plan-windows-port/01-initial`). Later consumer work established
> that all three deliverables are optional upgrades rather than blockers: shared memory improves
> throughput, live metrics improve sharpness, and `?1016` improves pointer resolution. Findings are
> cited inline as `[triage: …]`.

## Overview

Add a control-pipe command that accepts browser-rate BGRA frames through a shared memory mapping
instead of through files, so a producer can drive a pane at video rates without a PNG encode or a
disk round-trip on every frame.

The consumer is [winterm-browser](file:///C:/Users/boris/source/winterm-browser), a Windows-native
port of terminal-browser that renders Chromium into an agwinterm pane. Its design brief is
`C:\Users\boris\source\winterm-browser\docs\design\00-port-brief.md`. It can use this command as an
optional fast path while retaining the file-frame fallback. This plan defines the contract it codes
against — so the wire shape here is the deliverable, not just the implementation.

**Problem it solves.** `image.frame` (`ControlServer.cs:249`) is already a frame path: it takes an
`images[]` array, content-signature-caches each id to skip re-transmits, reads pixel bytes off the
render lock, and swaps placements under a microsecond-scale lock. But every image arrives as a
`path`, so a 30fps full-pane producer must PNG-encode and write a file 30 times a second and
agwinterm must read it back. For a document viewer like docxy that is fine. For a browser it is not.

**Why this is worth doing beyond the browser.** The expensive part of `image.frame` is not the
placement machinery, which is already good — it is the format and the transport. Anything that
generates pixels rather than loading them (a video preview, a plot that animates, a remote frame
buffer) hits the same wall.

## Three optional host upgrades

Revision 1 had one. The triage panel found two more, and correctly separated them by urgency:

| task | what | consumer impact if absent |
|---|---|---|
| 1–6 | `image.frameshm` | **none.** winterm-browser's Tasks 1–10 have zero dependency on this plan; the file-based `image.frame` already ships every capability its milestone needs. This is an optimisation with a self-guarding precondition and an automatic fallback. |
| **6b** | **cell pixel metrics** | ~~blocking~~ → **degrades**, see below. The consumer has no other source, but a wrong-but-consistent guess costs sharpness, not correctness. |
| 6c | `?1016` SGR-Pixels mouse | degrades. The browser works with pointer resolution quantised to one character cell. |

None is a consumer blocker: shared memory replaces a slower working transport, metrics replace a
consistent-but-resampled fallback, and SGR-Pixels replaces cell-quantised pointer coordinates.

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
- **`ControlServer` numeric args must be JSON numbers, not strings** — its typed readers reject
  strings. The ctl parses tokens as signed 64-bit integers, preserves non-negative `seq` as the
  publish counter, and range-checks/casts every other numeric field to signed 32-bit.
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
      slot until the reply for the previous sequence in that slot (`seq - slotCount`) has returned.
      For two slots that is the frame two back. Without the general rule, a producer using three or
      more slots and out-of-order replies can follow the narrower wording and still tear. [triage:
      major; corrected in second review]
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
      read it forever. Reuse of the name must continue its sequence; a restarted sequence uses a
      fresh name. The open is a syscall; the copy is megabytes.
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
- [x] skip re-transmitting a slot whose `(id, name, seq)` matches what was last accepted, the shm
      analogue of `image.frame`'s content-signature cache; revalidate the live mapping/header on a
      hit so cached pixels cannot bypass name, lifetime, slot or geometry checks
- [x] return `frame:<count>/<transmits>` like `image.frame` so a caller can tell whether a frame
      actually moved; record bytes read only in the optional performance log
- [x] write tests for a single frame producing the expected `KittyImage` and `ImagePlacement`
- [x] write tests for the `(id, name, seq)` cache skipping a repeat, accepting a bumped seq and
      retransmitting the same id/sequence from a different producer
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
- ➕ **the frame cache records its source and identity as well as its token.** A file content
      signature and a producer's frame counter are unrelated number spaces keyed by the same image
      id, and two mappings can publish the same sequence. Source, path/name, token and live image
      identity must all agree before a hit is trusted.
- ➕ **`seq: 0` is never cached.** It means "read whatever is in the slot", which by definition
      cannot be memoised; only a positive seq enters the cache.
- ➕ **request staging is bounded.** The server rejects more than 64 entries or 256 MiB of aggregate
      copied pixels before phase 2, and permits at most two shared-frame readers concurrently. The
      reader accepts the remaining request budget and checks it before allocating the next pixel
      array, so a repeated ordinary mapping cannot accumulate an unbounded `ops` list.
- ➕ **committed storage is bounded too.** Before phase 2 mutates the pane, the server projects the
      retained image count and source-pixel bytes after every replacement. A shared-frame request
      cannot grow a session beyond 256 ids or 256 MiB, so sequential requests with fresh ids cannot
      evade the per-request staging limit and exhaust managed/GPU memory.
- ➕ **concurrent positive sequences commit monotonically.** Two allowed readers can finish their
      off-lock copies in either order; under the session/cache lock, a delayed `(id, name, seq)` is
      rejected if a greater sequence already committed. A deterministic test pauses the older
      request after phase 1, commits the newer request, and proves the older pixels cannot replace it.
- ➕ **mixed sequenced/unsequenced commits follow request generation.** `seq: 0` has no producer
      ordering token, so any same-mapping pair involving it is ordered by the generation captured
      before phase 1. Regressions cover both delayed-zero/newer-positive and
      delayed-positive/newer-zero completion order.
- ➕ **the Rust-backed core registers direct-image metadata only.** The renderer-facing managed
      cache already owns the copied frame bytes; phase 2 now gives native only id/format/geometry for
      placement bookkeeping instead of duplicating the whole payload under the render lock.
- ⚠️ **assert on the parsed `error` string, not on the raw reply.** `JsonSerializer` escapes an
      apostrophe to `'`, so `Assert.Contains("'seq' must be…", resp)` fails against a reply
      that does contain the message. The tests parse the JSON and read `error`.
- ➕ the producer-running-ahead item is covered by three tests, since one number cannot say it:
      `AProducerThatSerialisesOnTheReplyNeverLosesAFrame` (the obligation held — six frames over two
      slots, all intact), `AProducerRunningAheadOnTwoSlotsSubstitutesANewerFrameRatherThanTearing`
      (the obligation broken — the reply carries frame 3's whole pixels under frame 1's request,
      which is substitution, not tearing) and
      `AProducerRunningAheadWithEnoughSlotsDeliversEveryFrameIntact` (frames are drained before any
      slot is reused; extra slots alone are not the invariant).

### Task 5: Expose the verb on `agwintermctl`

- [x] add the CLI surface in `src/Agwinterm.Ctl` alongside the existing `image` verbs —
      `agwintermctl image frameshm <Local\agwinterm-frame-NAME> [--slot N] [--seq N] ...`, dispatched
      from `Program.cs` next to `image show`/`image sixel`
- [x] parse numeric options as JSON numbers, preserving `seq` as Int64 and range-checking/casting
      every other numeric field to Int32; never place numeric strings into `cargs`
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
- ➕ **the CLI validates its own syntax and JSON types locally**: unknown options, contradictory
      positional/`--images`/single-entry flag shapes, malformed or non-array `--images`, non-numeric
      flag values, `Int32` overflow, and mapping names outside the `Local\agwinterm-frame-` prefix.
      Semantic field ranges, the mapping header and the slot descriptor stay the reader's business —
      duplicating those would let the CLI drift into rejecting frames the terminal accepts.
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
- ➕ **cache hits revalidate the live mapping.** The original integration test allowed a dead
      producer's last frame to remain re-placeable because the old two-field cache key answered
      before `OpenExisting`.
      Second review found that this also let another mapping/incarnation reuse stale pixels and let
      cached requests bypass header validation. Hits are now `(id, name, seq)`, open and validate the
      current header, and skip only the pixel copy; a vanished producer consistently returns an
      ordinary error while the already displayed frame remains unchanged.
- ➕ **measured with a persistent pipe client, not the ctl.** Spawning `agwintermctl.exe` per frame
      caps the loop at ~9fps on process start alone, which measures Windows, not the transport. The
      recorded numbers come from one connection held open across 120 frames; the harness is
      `.ralphex/tmp/bench-frameshm.ps1` (throwaway, not committed).
- ➕ **the measured control-transport time is 6.0–8.4 ms per full-pane 1080p frame** — ~120–165
      request/reply cycles per second against 8,294,400 bytes copied once by the transport. The
      asynchronous premultiplication and GPU upload are outside that round trip, so the figure is not
      an end-to-end display-fps claim. The earlier 0.10–0.13 ms cache-hit number is no longer quoted:
      it predated the required live mapping/header validation. The full table, its caveats (Debug
      build, PowerShell producer) and what a consumer should conclude are in the spec's "Measured
      throughput".
- ⚠️ **no screenshot, deliberately.** The Testing Strategy says visual confirmation belongs to the
      consuming project and not to build a throwaway pixel producer to look at; the live evidence
      here is the dev instance accepting ~800 8 MB frames and still answering `ping` and `tree`.

### Task 6b: Publish cell pixel metrics — optional, removes resampling

A revmux triage panel on the consuming plan found that nothing in agwinterm publishes cell metrics
and initially treated that absence as blocking. Consumer Task 6 later established that its consistent
fallback preserves click correctness; live metrics improve viewport sizing and sharpness by avoiding
resampling, but the port does not stall without them.
[triage: major, original absence verified exhaustively against the full verb dispatch; urgency later corrected]

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
- [x] implement the chosen mechanism, reading the live `_cellW`/`_cellH`, not a constant.
      **The wire shape the consumer already codes against** (`pixel-core/src/agwinterm.rs`,
      `ControlClient::pane_metrics`):
      request `{"cmd":"session.metrics","target":"<pane id>"}`, reply via `OkRaw` — the way `tree`
      and `window.state` answer, not `Ok` —
      `{"ok":true,"result":{"cols":132,"rows":37,"cellWidth":9,"cellHeight":19,"widthPx":1221,"heightPx":703}}`.
      camelCase, matching `window.state`'s `sidebarVisible`. `widthPx`/`heightPx` are the exact grid
      extent in device pixels and are authoritative for a sharp frame. `cellWidth`/`cellHeight` are
      rounded compatibility hints: multiplying one rounded cell by the grid accumulates the
      renderer's fractional advance and can still resample. A zero exact extent means "no metrics";
      `unknown command 'session.metrics'` is a capability gap the consumer latches and stops asking
      about.
      - ? implemented as a session-targeted `session.metrics` dispatch returning an `OkRaw` object.
        `ISessionHost.PaneMetrics` is the host seam; the Win32 host resolves an exact pane (including
        split-pane ids), marshals synchronously to the UI thread, and measures the target's current
        grid, layout and DPI. The repository has evolved beyond the plan's single base
        `_cellW`/`_cellH`: terminal rendering and regridding now use per-pane
        `Metrics(pane.FontSize)`, so the verb reads those same live values and correctly follows
        per-pane font zoom instead of reporting only the base chrome font.
      - ➕ **review correction:** the first implementation rounded one cell before publishing it,
        while rendering deliberately keeps the exact DirectWrite advance. `PaneMetricsSnapshot`
        now accumulates that fractional advance across the whole grid before rounding once into
        `widthPx`/`heightPx`; tests pin a case where `cols * cellWidth` differs by 33 pixels.
- [x] note that `Agwinterm.App/MainWindow.xaml.cs` is **dead code** - absent from `Agwinterm.slnx`.
      The live values are in `Agwinterm.Win32`. Do not implement against the dead project.
      - ? the control host and spec both state this explicitly; the implementation is only in
        `Agwinterm.Win32/Program.ControlHost.cs`.
- [x] write tests for the response carrying the live metrics, and for it tracking a font-size change
      - ? `SessionMetricsTests` has ten tests pinning the six camelCase fields, object-valued
        `OkRaw` result, live font-size and pane-size changes, target resolution, zero fallback and
        the older-build capability-probe distinction.
- [x] if XTWINOPS: write tests that the new `t` arm does not swallow sequences previously `Unhandled`
      - ? skipped - not applicable: the chosen mechanism is the control verb, so no `t` arm changed.
- [x] document the mechanism in `docs/specs/image-frameshm.md` or its own spec, and tell
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
- [x] run tests - must pass before Task 7
      - ? 477 pass in Release (266 Pty, including 10 new metrics tests; 211 Core). The explicit
        `Agwinterm.Win32` Release build also passes. `dotnet format --verify-no-changes` finds no
        issue in the added ranges; its remaining findings are the pre-existing whitespace in the
        touched legacy files already noted by earlier tasks.

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

- [x] add `1016` to the mode handlers in **both** cores, keeping them in agreement
- [x] answer DECRQM for `?1016$p` so a client can discover support instead of timing out
- [x] add a pixel-coordinate branch to `SendMouse` that emits pixel values when `?1016` is active,
      leaving the cell path untouched when it is not
- [x] write tests for mode set/reset, the DECRQM reply, and the encoder emitting pixel coordinates
      only when the mode is on
- [x] write a test that sub-cell movement produces distinct reports under `?1016` and identical ones
      without it — the whole point of the change
- [x] run tests — must pass before Task 7
      — 485 pass in Release (219 Core, 266 Pty), including the C#/Rust differential adapter;
        the Rust crate's 29 unit tests pass, the explicit `Agwinterm.Win32` Release build is clean,
        and the ABI declarations agree at v17. Targeted `dotnet format` passes. `cargo clippy --lib`
        completes with only the repository's pre-existing warnings outside the added ranges.

- ➕ **`?1016` is persisted across a pty-host reattach.** Both cores include it in `DumpModes`, just
      like `?1006`; otherwise a surviving browser would silently fall back to cell coordinates after
      the UI reconnects while still believing its pixel-mode request was active.
- ➕ **pixel reports use device pixels, not DIPs.** The Win32 renderer lays out in DIPs but
      `session.metrics` and the browser bitmap contract are device-pixel values. `SendMouse` keeps
      the old DIP-to-cell calculation byte-for-byte, while its new branch retains the raw Windows
      device coordinates and subtracts the pane origin in the same coordinate space.
- ➕ **the encoder moved into `Agwinterm.Core/MouseReport.cs`.** The Win32 executable has no test
      reference, so isolating the wire encoding is what lets tests prove that two points within one
      cell differ under `?1016` and remain identical without it.
- ⚠️ **surfacing the Rust mode requires a C ABI change.** `FfiEmuInfo` gained `mouse_sgr_pixels`, so
      both ABI declarations advance from 16 to 17; the ABI agreement check and adapter tests guard
      the layout rather than leaving a mismatched struct to corrupt later fields.

### Task 7: Verify acceptance criteria

- [x] verify `image.frame` still behaves exactly as before (no regression in the file path)
      — `ImageFrame_AtomicallyReplacesWithCellDims` and the two existing file-path image tests pass
      unchanged in the full suite.
- [x] verify every rejection in Task 3 answers `{"ok":false,...}` and leaves the session usable
      — `Task3MappingRejectionsAreErrorRepliesAndLeaveTheSessionUsable` drives every rejection
      reachable through `OpenExisting` through the control verb and pings after each one. The
      sub-header view guard remains covered through the narrowed-view reader overload because
      Windows page-rounding makes it unreachable through a named mapping.
- [x] verify a producer killed mid-frame does not wedge or crash the terminal
      — `AProducerKilledMidFrameIsAnOrdinaryFailureNotACrash` writes half the next slot without
      publishing `ready`, destroys the mapping, and proves the previous frame and pipe survive.
- [x] verify a producer running ahead of the consumer either cannot tear or is rejected — whichever
      the Task 1 invariant chose — and that the spec says which
      — the invariant chose serialisation on the reply: the six-frame/two-slot test proves that
      conforming producer cannot wrap onto a slot being copied. The two running-ahead tests pin the
      documented consequence of violating it; the spec's normative "Producer obligations" section
      now generalises the rule to waiting for `seq - slotCount`, including out-of-order replies on
      multiple pipe connections.
- [x] verify the cell metrics from Task 6b track a live font-size change rather than being sampled once
      — `TracksAFontSizeChangeRatherThanBeingSampledOnce` changes the host's live measurements
      between two requests and observes the new values.
- [x] verify `?1016` off is byte-identical to today's mouse encoding, so existing apps see no change
      — `Without1016LegacyEncodingIsByteIdentical` pins press and release bytes, while
      `Without1016SgrEncodingRemainsCellBased` pins the SGR path.
- [x] confirm `tests/conformance/control-api.json` is **unchanged** — note this now covers three new
      capabilities, none of which agliteterm implements
      — its blob hash is `143f9ce9a5d8058e648f4fddf502872eb258bda3`, identical to the
      pre-implementation commit; the strict 49-case conformance run also passes.
- [x] run the full unit test suite
      — 486 .NET tests pass in Release (267 Pty, 219 Core), including the C#/Rust differential
      adapter; all 29 native Rust tests pass, and the explicit Rust Release build succeeds. A
      confirmation run hit the documented .NET 10 test-host native crash and passed on the CI
      policy's single retry.
- [x] run the linter — all issues fixed
      — `dotnet format --verify-no-changes` passes for every Task 7 file and `cargo clippy --lib`
      completes with no failure. A repository-wide audit still reports the pre-existing legacy
      whitespace and Rust warning backlog already recorded in Tasks 2, 6b and 6c; Task 7 introduced
      none of it, and folding a whole-repository mechanical reformat into this acceptance task would
      violate the plan's focused-change constraint.
- [x] verify test coverage meets the project standard
      — the repository configures no numeric coverage collector or threshold. Its stated standard is
      unit success/error coverage plus a real integration path: 87 focused Pty tests now cover the
      reader, verb, pipe, dead/malformed producers and live metrics, with Core tests covering both
      mouse modes and byte encodings.
- ➕ **second-review validation:** 509 .NET tests and 29 native Rust tests pass; repository-wide
      `dotnet format`, Rust formatting, strict all-target clippy, zero-warning Release builds, ABI
      agreement, `git diff --check`, and the live strict control-API conformance suite all pass.
- ➕ **later review validation:** publication now uses an atomic acquire load directly from the
      mapping, renderer conversions are bounded and coalesced, mapping switches cannot resurrect an
      older request, and Rust stable-id replacements carry a native content revision. The latter
      advances the C ABI from v17 to v18. All 517 .NET tests and 30 native Rust tests pass, along with
      repository-wide formatting, warnings-as-errors clippy, a zero-warning Release build, ABI
      agreement, `git diff --check`, and the live strict control-API conformance suite.
- ➕ **final all-findings review validation:** the acquire now also precedes the published slot
      descriptor read, renderer decode and texture identities are scoped by terminal core for split
      panes, the release/acquire pair has a 10,000-publication concurrent mapped-view regression, and
      the CLI/spec consistently preserve `seq` as Int64. All 523 .NET tests and 30 native Rust tests
      pass, together with repository-wide .NET/Rust formatting, strict all-target clippy, a
      zero-warning Release build, ABI v18 agreement, `git diff --check`, and strict live control-API
      conformance.

### Task 8: [Final] Update documentation

- [x] update `docs/agterm-gap-analysis.md:41`, whose "Image protocols / graphics" line currently
      reads *Partial* and describes only the out-of-band file path
      - ? it remains *Partial* because iTerm2 is absent, but now names the Kitty-compatible and
        sixel paths, the file-backed image/frame verbs and the raw BGRA/RGBA shared-memory verb.
- [x] update the agent skill's image section (`src/Agwinterm.Pty/AgentSkill.cs`) if the verb should
      be discoverable to agents
      - ? it should be discoverable as a specialised producer path, with the exact mapping-name
        prefix, a pointer to the versioned contract and explicit guidance to keep ordinary files on
        `image show`. `AgentSkillTests` pins those details.
- [x] cross-link `docs/specs/image-frameshm.md` from the winterm-browser brief
      - ? `winterm-browser/docs/design/00-port-brief.md` now links to the portable GitHub location
        and adds a dated host update without rewriting its historical account of the PNG fallback
        that winterm-browser actually shipped.
- ? validation: 487 .NET tests pass in Release (268 Pty, 219 Core), all 29 native Rust tests pass,
    the explicit `Agwinterm.Win32` Release build succeeds, targeted `dotnet format` passes and both
    repositories pass `git diff --check` plus the local cross-link target check.

## Technical Details

**Why two slots and a sequence number.** The producer fills a slot completely, then publishes with
an atomic release store of `ready = seq`; the renderer performs an atomic acquire load directly from
that mapped field and copies from the slot it names. No lock is shared across the process boundary,
and a producer that dies mid-write leaves a half-written slot that is simply never published.

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

For a pipelined producer, the general invariant is per slot: sequence `seq` may reuse its slot only
after the reply for `seq - slotCount`. A larger ring reduces how often a producer waits, but counting
total in-flight frames is not enough when multiple pipe connections allow replies to finish out of
order. This remains a **property of the producer, not of the layout** — which is exactly why it has
to be written into the spec rather than left implicit. A layout that enforces it itself would need
an in-use word per slot or a consumer-published released sequence.

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
