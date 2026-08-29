# `image.frameshm` — shared-memory frame transport

Status: **draft, layout version 1.** This page is the contract a producer codes against. Once a
consuming project ships against it, a change to the header layout or the JSON args is a breaking
change and must bump `version`.

Related: [`image.frame`](../../src/Agwinterm.Pty/ControlServer.cs) (the file-based sibling this
verb mirrors), and the implementation of the layout in
[`src/Agwinterm.Pty/ShmFrameLayout.cs`](../../src/Agwinterm.Pty/ShmFrameLayout.cs).

## What it is for

`image.frame` already does everything a frame path needs — content-signature caching, pixel reads
off the render lock, a microsecond-scale locked placement swap. Its one problem is transport: every
image arrives as a `path`, so a 30fps full-pane producer PNG-encodes and writes a file 30 times a
second and agwinterm reads it back. `image.frameshm` keeps the placement machinery and replaces the
transport with a shared memory mapping carrying raw BGRA.

The verb is an **optimisation with an automatic fallback**. A producer that cannot open a mapping,
or that gets `unknown command 'image.frameshm'` from an older build, uses `image.frame` and works.

`image.frameshm` is deliberately **not** in `tests/conformance/control-api.json`: that file is a
contract agliteterm must also satisfy, and it does not implement this verb.

## Mapping layout

A single named mapping holds a fixed **256-byte header** followed by **N pixel slots**, where
`2 <= N <= 8`. All integers are **little-endian**. Offsets are from the start of the mapped view.

### Fixed header (256 bytes)

| offset | size | field | notes |
|---|---|---|---|
| 0 | 4 | `magic` | `0x46534741` — the bytes `A` `G` `S` `F` in order |
| 4 | 4 | `version` | `1`. A reader rejects anything else. |
| 8 | 4 | `slotCount` | `2..8` |
| 12 | 4 | `flags` | reserved; producers write 0, readers ignore unknown bits |
| 16 | 8 | `slotStride` | bytes between the start of consecutive slots; `> 0` |
| 24 | 8 | `pixelOffset` | byte offset of slot 0's pixels; `>= 256` |
| 32 | 8 | `ready` | publish sequence of the newest complete frame; `0` = nothing published |
| 40 | 24 | — | reserved, zero |
| 64 | 16×8 | slot descriptors | one per slot, indices `0..7`; entries at or past `slotCount` are ignored |

Slot descriptor at offset `64 + 16 * slot`:

| offset | size | field |
|---|---|---|
| +0 | 4 | `width` — pixels |
| +4 | 4 | `height` — pixels |
| +8 | 4 | `stride` — bytes per row, `>= width * 4` (padding allowed) |
| +12 | 4 | `format` — a `KittyFormat` value; `132` (`Bgra`) is the expected one |

Slot `i`'s pixels live at `pixelOffset + i * slotStride`, and occupy `height * stride` bytes, which
must not exceed `slotStride`. The mapping must therefore be at least
`pixelOffset + slotStride * slotCount` bytes.

### Mapping name

The name is passed in the JSON args and **must begin with the literal prefix**:

```
Local\agwinterm-frame-
```

So a producer creates, for example, `Local\agwinterm-frame-browser-1`. The prefix is enforced by
the reader: it keeps a request from naming an arbitrary pre-existing kernel object, and the
`Local\` namespace keeps the mapping inside the caller's session. A name outside the prefix is
rejected with `{"ok":false,...}` — this is a validation error, not a bug, so use the prefix above
verbatim.

The prefix match is **case-sensitive and literal** (`local\…` is rejected), and what follows it
must be 1..128 characters drawn from `[A-Za-z0-9._-]`. The character set is what enforces the
namespace: with no backslash available, a suffix cannot walk back out of `Local\`.

### Publishing a frame

1. Pick the slot for the frame's sequence number: `slot = seq % slotCount`. Sequences start at `1`;
   `ready == 0` means no frame has ever been published.
2. Write the slot's descriptor (`width`, `height`, `stride`, `format`) and then its pixels.
3. Atomically store `seq` into the aligned `ready` field with **release** semantics.
4. Send the `image.frameshm` request naming `slot` and `seq`.

The reader atomically loads `ready` with **acquire** semantics directly from the mapped field before
touching pixels. Copying the header and fencing a load from that private copy is not equivalent: the
copy could tear the 64-bit value and cannot acquire the producer's pixel writes. A producer that dies
mid-write therefore leaves a half-written slot that was never published, and the reader either sees
the previous frame or nothing.

## Producer obligations

These are normative. Any slot count is safe **only** because of the first one.

- **Once `seq > slotCount`, do not begin filling its slot until the reply for `seq - slotCount` has
  returned.** That is the preceding sequence which occupied the same slot. For the minimum
  `slotCount == 2`, this is the reply for the frame two back. The control pipe is request/response;
  `image.frameshm` answers only after it has copied the pixels out. The simplest conforming producer
  serialises on every reply and therefore never has a slot reuse outstanding.

  A producer that *pipelines* requests must track the outstanding reply for each slot. Merely
  raising `slotCount` above the current number of in-flight frames is not sufficient when requests
  use multiple pipe connections, because later replies can complete before an earlier request for
  the slot being reused. Reusing that slot early can tear: the release/acquire pair orders the
  producer's publish, not the terminal's copy, and an 8 MB copy for a 1920×1080 BGRA frame is not a
  narrow window. Nothing in the layout prevents this; only the per-slot reply rule does.

  The failure mode is a visibly torn frame, not a crash: the reader's bounds validation keeps the
  copy inside the view either way.
- **Never shrink the mapping or close it while a request is outstanding.** The reader copies out of
  the view during the call. A vanished mapping between calls is fine and reports as an ordinary
  failure.
- **Bump `seq` on every published frame, monotonically for the lifetime of a mapping name.** The
  terminal skips the pixel copy when `(id, name, seq)` matches the last frame it accepted, so a
  repeated sequence from the same mapping means "nothing changed, just re-place it". If a producer
  restarts its counter, it must choose a fresh mapping-name suffix for that incarnation. Recreating
  a mapping under the same name is supported only when the sequence continues monotonically.
- **Store `ready` before sending the request, never after.** A request whose `seq` is greater than
  the header's `ready` names a frame the producer has not published, and is rejected — the reader
  will not copy a slot the release fence has not covered. Send `seq: 0` (or omit it) to mean "read
  whatever is in this slot", which skips that check.

## Validation the reader applies

Every one of these answers `{"ok":false,...}` and leaves the session untouched. None of them is an
error condition for the terminal; they are all ordinary input from another process.

| rejected when | why |
|---|---|
| `name` outside `Local\agwinterm-frame-` or using characters outside `[A-Za-z0-9._-]` | naming an arbitrary kernel object |
| the mapping does not exist | the producer exited — expected, not exceptional |
| view shorter than 256 bytes, bad `magic`, `version != 1`, `slotCount` outside 2..8 | not this layout |
| `pixelOffset < 256`, non-positive `slotStride` | offsets that overlap the header or overflow |
| `slot` outside `0..slotCount-1` | out of range |
| `width` or `height` outside `1..16384` | non-positive, or larger than any real display |
| `stride < width * 4` | rows would read into each other |
| `height * stride > slotStride` | slots would overlap |
| the slot's byte range is not entirely inside the mapped view | the mapping is smaller than the header claims |
| `format` is not `132` or `32` | see below |
| `width`/`height`/`stride`/`format` in the args disagree with the slot descriptor | one of the two is stale; guessing which is worse than saying so |
| `seq` greater than the header's `ready` | the frame has not been published |
| positive `seq` with `slot != seq % slotCount` | the request names a slot that sequence did not publish |

Cache hits still open the named mapping and apply every header check above; they skip only the pixel
copy. A vanished or malformed mapping therefore cannot be hidden by repeating an accepted sequence.

The control server also rejects more than 64 entries or more than 268,435,456 aggregate copied pixel
bytes in one `images` array. At most two shared-frame requests copy concurrently, so independent pipe
clients cannot multiply staging without bound.

Before committing, the server also checks the session's retained source pixels and image ids. A
shared-frame commit may not increase retained payload beyond 268,435,456 bytes or retained image ids
beyond 256. Replacing an existing id is allowed; a request that would grow past either limit is
rejected atomically and leaves the previous composition visible.

The renderer admits at most two image conversions at once. Stable-id replacements publish the newest
image reference to those jobs; superseded work is discarded before conversion when possible and
always before upload. Intermediate frames that never receive a decode slot are coalesced away.

Two pipe connections may complete their off-lock copies out of order. When both requests carry a
positive sequence, the commit step compares `seq` with the newest frame accepted for the same
`(id, name)`. Sequence zero has no producer ordering token, so a request generation captured before
the off-lock copy orders any pair involving `seq: 0`, including requests for the same mapping.
Mapping-identity switches are generation-ordered too. Delayed older work is rejected rather than
replacing a newer displayed frame. This makes pipelining distinct slots safe with respect to display
order as well as slot reuse.

Note the format restriction: only the **4-bytes-per-pixel** formats are carried, `132` (`Bgra`) and
`32` (`Rgba`). `24` (`Rgb`) and `100` (`Png`) are valid `KittyFormat` values but not valid here —
every stride and size bound above assumes 4 bpp. Send PNG through `image.frame`, which is the path
built for it.

A non-zero `width`, `height`, `stride` or `format` in the args must match the slot descriptor; zero
means "trust the descriptor". A producer that fills the args from the same variables it wrote into
the header gets a free consistency check; one that would rather not repeat itself sends zeros.

## JSON args

```json
{
  "cmd": "image.frameshm",
  "target": "<pane id>",
  "args": {
    "images": [
      {
        "id": 1,
        "name": "Local\\agwinterm-frame-browser-1",
        "slot": 1,
        "seq": 1,
        "width": 1920,
        "height": 1080,
        "stride": 7680,
        "format": 132,
        "row": 0,
        "col": 0,
        "cols": 120,
        "rows": 30
      }
    ]
  }
}
```

Every numeric field is a **JSON number, never a string**. `seq` is a non-negative signed 64-bit JSON
integer; every other numeric field is a signed 32-bit integer. `ControlServer`'s typed parser rejects
a string and names the offending field. The CLI parses numeric tokens as `Int64`, preserves `seq`,
and range-checks/casts the remaining fields to `Int32` before putting them into `cargs`.

| field | meaning |
|---|---|
| `id` | image id, same semantics as `image.frame` — the key the terminal caches pixels under |
| `name` | mapping/cache identity; `Local\agwinterm-frame-` prefix required, and a fresh suffix is required when `seq` resets |
| `slot` | slot index to read, `0 <= slot < slotCount` |
| `seq` | the non-negative signed 64-bit publish sequence; `0` means current/uncached, positive values drive the `(id, name, seq)` re-transmit cache |
| `width`, `height`, `stride`, `format` | must agree with the slot descriptor; both are validated |
| `row`, `col` | placement origin in cells |
| `cols`, `rows` | placement size in cells; `0` means "natural size" |
| `sx`, `sy`, `sw`, `sh` | optional source crop in pixels, same as `image.frame`; `0` means "the whole image" |

Every field except `name` is optional. `id` defaults to the entry's 1-based position in `images`,
and every other number defaults to `0`.

The reply mirrors `image.frame`: `frame:<count>/<transmits>`, so a caller can tell whether a frame
actually moved. `transmits` counts the entries whose pixels were actually copied; the rest were
served from the `(id, name, seq)` cache after their live mapping and header were revalidated.

**A frame is all-or-nothing.** If any entry in `images` is rejected — a bad number, a name outside
the prefix, a slot that overruns the view, or a request limit being exceeded — the whole request
answers `{"ok":false,...}` and
*nothing* is applied, not even the entries that validated. The pane keeps showing the previous
frame rather than a half-updated one.

## Driving it from the CLI

`agwintermctl image frameshm` is the manual and scripting surface, a sibling of `image show`:

```powershell
agwintermctl image frameshm Local\agwinterm-frame-browser-1 --slot 0 --seq 4 `
    --width 1920 --height 1080 --stride 7680 --format 132 --cols 120 --rows 30
```

The positional is the mapping name and every other field of an `images[]` entry is a `--flag` of the
same name (`id`, `slot`, `seq`, `width`, `height`, `stride`, `format`, `row`, `col`, `cols`, `rows`,
`sx`, `sy`, `sw`, `sh`). An omitted flag is left out of the JSON entirely, so the terminal applies
its own default rather than an explicit `0` the caller never asked for. The CLI parses each one and
emits a JSON **number** — that is the whole reason the parsing exists, since a quoted number is
rejected by `ControlServer`.

The CLI rejects unknown options and contradictory input shapes before opening the pipe. In the
single-entry form it also rejects non-numeric values, values outside the JSON field's `Int32`/`Int64`
type, and mapping names outside the `Local\agwinterm-frame-` prefix. In the multi-entry form it checks
that `--images` is a JSON array and rejects the positional name or any single-entry numeric flag beside
it. Semantic field ranges, mapping headers and slot descriptors remain the reader's business and
report through the reply.

For the multi-entry, all-or-nothing case there is no flag shape, so pass the array directly:

```
agwintermctl image frameshm --images '[{"name":"Local\\agwinterm-frame-a","slot":0,"seq":4},
                                       {"name":"Local\\agwinterm-frame-b","slot":1,"seq":3}]'
```

`--images` is forwarded as the request array and is mutually exclusive with the positional name and
the single-entry numeric flags. Global routing options such as `--target` and `--pipe` remain valid.

## Sizing a pane with `session.metrics`

A producer should size its viewport and frame from the pane's live cell metrics instead of guessing
a font-dependent cell size. Send the pane id from `AGWINTERM_SESSION_ID` as the target:

```json
{"cmd":"session.metrics","target":"<pane id>"}
```

The successful reply has an object-valued `result` (not a string containing JSON):

```json
{
  "ok": true,
  "result": {
    "cols": 132,
    "rows": 37,
    "cellWidth": 9,
    "cellHeight": 19,
    "widthPx": 1221,
    "heightPx": 703
  }
}
```

All pixel fields are **device pixels**, the same coordinate space as a BGRA frame. `widthPx` and
`heightPx` are the exact rendered grid extent and should size a sharp producer viewport. They are
computed by accumulating the renderer's fractional DIP cell advance across `cols`/`rows` before the
single device-pixel rounding step. `cellWidth` and `cellHeight` are rounded integer compatibility
hints; multiplying either by the grid can accumulate error and cause resampling. A zero exact extent
means metrics are unavailable and the producer should use its configured fallback. An older terminal
reports `unknown command 'session.metrics'`; a client may latch that capability miss and stop probing.

The Win32 host measures on every request from the target pane's current font size, layout and DPI.
It uses the same per-pane `Metrics(pane.FontSize)` values as rendering and regridding, so a font-size
change or pane resize is reflected in the next reply rather than waiting for a new session. The
control-pipe surface is implemented in the live `Agwinterm.Win32` project; the similarly named
`Agwinterm.App/MainWindow.xaml.cs` is not part of `Agwinterm.slnx` and is not an implementation.

For manual inspection, the ctl exposes the same call as
`agwintermctl session metrics [<pane-id>] --json`. winterm-browser consumes the JSON form in
`pixel-core`'s `ControlClient::pane_metrics`.

## Format

BGRA end to end (`KittyFormat.Bgra = 132`). Chromium's `paint` hands out BGRA and Direct2D wants
`B8G8R8A8_UNORM`, so a swizzle at either end would be pure loss. `132` sits outside the Kitty wire
range (24/32/100), and both APC parsers clamp `f=` to that range
(`KittyFormats.ParseWireFormat`, and `parse_wire_format` in `native/agwinterm-core/src/emulator.rs`),
so the value can never be produced by parsing a real graphics sequence — only a host path such as
this verb can mint it.

**Alpha is straight, not premultiplied.** Byte order within a pixel is `B, G, R, A`, and the colour
channels are *not* scaled by alpha; agwinterm premultiplies during upload, exactly as it does for
`KittyFormat.Rgba`. A producer with no transparency writes `A = 255` and need not think about it.

## Why copy at all

The renderer could map the producer's pixels directly, but then a producer that exits invalidates a
view the renderer is still using. Copying costs one memcpy and removes an entire class of lifetime
bug. A genuine zero-copy path — a D3D11 shared texture handle opened directly by Direct2D — is
separate, later work.

## Measured throughput

Measured 2026-08-27 against a live dev instance (a Debug build under instance id `agwinterm-dev`),
on a Ryzen 9 7940HS / Windows 11. The producer was a PowerShell script holding one persistent
control-pipe connection, writing a full-pane **1920×1080 BGRA** frame into the mapping and
serialising on each reply — the obligation stated above, so never more than one frame outstanding
over two slots. Six passes of 120 frames each, after a warm-up frame.

**Transport bytes copied per frame: 8,294,400** (`1080 × 7680`, i.e. 7.9 MiB), exactly once, on the
request thread and off the render lock. The reader packs straight from the mapped view into the
`KittyImage` the emulator keeps, and the placement swap moves references only. Rendering later
allocates and fills a second full-size premultiplied-BGRA buffer and uploads it to Direct2D. That work
runs asynchronously and is not awaited by the control reply, so it is outside these measurements.

| | per frame | rate |
|---|---|---|
| control transport (request written → reply read; async conversion/upload excluded) | **6.0–8.4 ms** | **~120–165 replies/s**, ≈1.0–1.4 GB/s |
| producer write + control round trip (rendering still excluded) | 8.5–18.2 ms | 55–117 cycles/s, 435–927 MB/s |

Two things a consumer should take from this. First, the control transport fits comfortably inside a
60fps frame budget, but this benchmark does **not** establish end-to-end displayed frame rate because
it excludes the asynchronous conversion and GPU upload. Second, the producer's own write into the
mapping is the same order of cost as the measured transport — the 2.5–11.2 ms spread above is a
PowerShell `WriteArray`, and a producer that already has its pixels in shared memory (Chromium
painting straight into the slot) skips it entirely, which is the whole point of the mapping.

The numbers are a Debug build's, so they are a floor rather than a ceiling. What they establish is
that the transport budget is dominated by the shared-memory copy, and that a frame the producer
knows is unchanged costs about a tenth of a millisecond — cheap enough to re-place on every tick
rather than tracking whether a re-place is needed. Rendering throughput needs a separate measurement.
