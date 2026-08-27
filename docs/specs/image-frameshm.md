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

### Publishing a frame

1. Pick the slot for the frame's sequence number: `slot = seq % slotCount`. Sequences start at `1`;
   `ready == 0` means no frame has ever been published.
2. Write the slot's descriptor (`width`, `height`, `stride`, `format`) and then its pixels.
3. Issue a **release** fence, then store `seq` into `ready`.
4. Send the `image.frameshm` request naming `slot` and `seq`.

The reader loads `ready` with an **acquire** fence before touching pixels. A producer that dies
mid-write therefore leaves a half-written slot that was never published, and the reader either sees
the previous frame or nothing.

## Producer obligations

These are normative. Two slots are sufficient **only** because of the first one.

- **Do not begin filling a slot until the reply for the frame two back has returned.** The control
  pipe is request/response; `image.frameshm` answers only after it has copied the pixels out. A
  producer that serialises on the reply can never have two unacknowledged frames outstanding, so it
  can never wrap back onto the slot being copied.

  A producer that *pipelines* requests to hide pipe latency will wrap, and it will tear — the
  release/acquire pair orders the producer's publish, not the terminal's copy, and an 8 MB copy for
  a 1920×1080 BGRA frame is not a narrow window. Nothing in the layout prevents this; only this
  rule does. If you need pipelining, raise `slotCount` above the number of frames you keep in
  flight, or ask for an in-use protocol — do not assume two slots cover it.

  The failure mode is a visibly torn frame, not a crash: the reader's bounds validation keeps the
  copy inside the view either way.
- **Never shrink the mapping or close it while a request is outstanding.** The reader copies out of
  the view during the call. A vanished mapping between calls is fine and reports as an ordinary
  failure.
- **Bump `seq` on every published frame, monotonically.** The terminal skips re-transmitting a slot
  whose `(id, seq)` matches the last one it accepted, so a repeated `seq` means "nothing changed,
  just re-place it".

## JSON args

```json
{
  "cmd": "image.frameshm",
  "target": "<pane id>",
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
```

Every numeric field is a **JSON number, never a string**. `ControlServer`'s `GetInt` throws
`"requires an element of type 'Number'"` on a string, so a CLI producer must `int.TryParse` and put
an int into `cargs`.

| field | meaning |
|---|---|
| `id` | image id, same semantics as `image.frame` — the key the terminal caches pixels under |
| `name` | mapping name, `Local\agwinterm-frame-` prefix required |
| `slot` | slot index to read, `0 <= slot < slotCount` |
| `seq` | the frame's publish sequence; drives the `(id, seq)` re-transmit cache |
| `width`, `height`, `stride`, `format` | must agree with the slot descriptor; both are validated |
| `row`, `col` | placement origin in cells |
| `cols`, `rows` | placement size in cells; `0` means "natural size" |

The reply mirrors `image.frame`: `frame:<count>/<transmits>`, so a caller can tell whether a frame
actually moved.

## Format

BGRA end to end (`KittyFormat.Bgra = 132`). Chromium's `paint` hands out BGRA and Direct2D wants
`B8G8R8A8_UNORM`, so a swizzle at either end would be pure loss. `132` sits outside the Kitty wire
range (24/32/100), so it can never be produced by parsing a real APC graphics sequence.

## Why copy at all

The renderer could map the producer's pixels directly, but then a producer that exits invalidates a
view the renderer is still using. Copying costs one memcpy and removes an entire class of lifetime
bug. A genuine zero-copy path — a D3D11 shared texture handle opened directly by Direct2D — is
separate, later work.

## Measured throughput

_To be filled in by Task 6 of `docs/plans/20260821-image-frameshm-command.md`: sustained frames per
second and bytes copied per frame for a full-pane 1920×1080 BGRA frame._
