# Passive session HUD (P13)

A HUD displays short status text over a session without writing to its terminal, changing
its dimensions, taking keyboard focus, capturing the mouse or changing notification badges.
It is native presentation: no shell, helper process, temporary message file or restore state.

```powershell
agwintermctl session hud "Preparing review" --detail "Reading the diff" --spinner --position top-right
agwintermctl session hud update "Review ready" --text-color '#80ff80'
agwintermctl session hud close
```

`open` is optional before the message. Use explicit `open` for a message that is literally
`open`, `update` or `close`. `--target ID` and `--window ID` use normal control routing.
Use the owning **session** id, not the secondary split pane or an auxiliary cover's id.
A session name is accepted; an ambiguous/unknown target refuses. With `--target active`,
an active scratch/quick/session-wide program cover refuses; name the session explicitly instead.
Pane overlays may coexist, including on the focused pane. Empty target/window selectors refuse.

## Options and state

- `--detail TEXT`: optional second text block.
- `--position`: `top-left`, `top-center`, `top-right`, `center-left`, `center`,
  `center-right`, `bottom-left`, `bottom-center`, `bottom-right`. Default `center`.
  `top` and `bottom` normalize to `top-center` and `bottom-center`.
- `--spinner`: animate the default ASCII bar. `--spinner-style` accepts `bar`, `braille`,
  `circle`, `blocks`, `dot` or `none`; an explicit style wins over the flag.
- `--size-percent N`: requested width 1–100, bounded to 10–80% of the content area;
  tree read-back reports the bounded value. Omitted width fits the text within those
  limits. Height fits wrapped text up to 80%; it is not an independently settable size.
- `--background-color HEX` / `--text-color HEX`: six hex digits, optional `#`.
  Otherwise the current theme's background/foreground is used. On **update**, the
  original background is retained; a newly supplied valid background is ignored.
  Open again to change it. Text color can change on each update.

Message and detail each allow 256 Unicode scalars after NFC normalization. The message
must be nonblank; control characters, including newline/tab/escape, are refused. Text is
plain, not ANSI/Markdown. DirectWrite wraps Unicode; small windows may clip content.
The nine anchors apply to the whole session's content rectangle, not the focused split.
An off-center anchor holds a 10% edge margin on the corresponding axis.

Open replaces an existing HUD. Update requires an existing HUD and replaces its whole
specification except background: omitted detail/spinner/position/width/text color reset
to their defaults. Close is idempotent on a valid session, but unknown targets refuse.
There is no automatic timeout. Transient means it is not restored: close, replacement,
session teardown or app exit ends it. A crashed agent may leave its HUD until closed.

## Program overlays and input

A session-wide program overlay owns the same logical slot: HUD open/update refuse while
that program is present. HUD close never kills it. Opening a session-wide program overlay
replaces a HUD, and `session overlay close` also dismisses a HUD. The ordinary close
shortcut dismisses the HUD before closing a pane. Pane overlays may remain underneath;
scratch/quick covers and the dashboard obscure the HUD while shown.

The HUD itself is not a terminal surface. `session text/copy/type/paste` continue to
address their normal terminal targets. A HUD cannot be read with `overlay text`, resized
with `overlay resize`, or awaited with `overlay --block`; inspect `tree` instead.

## Wire API

`session.hud.open`, `session.hud.update`, `session.hud.close`. Open/update args:
`message`, optional `detail`, `spinner` (style string), `position`, `size-percent` (JSON integer),
`color` (background), `text-color`. Close accepts no display arguments. Invalid types,
unknown arguments and unsupported options refuse before mutation. Replies are
`{session, hud}` inside the usual `ok/result` envelope; close reports `hud:null`.
The session's `tree` node includes the same `hud` object while set:
`message`, `detail`, `spinner`, `backgroundColor`, `textColor`, `sizePercent`, `position`.
Absent optional values are null; no HUD means the tree omits `hud`.

This is the native Win32 implementation. Hosts without HUD support explicitly refuse.
The shared lite conformance floor is unchanged until the Wave-3 mirror; lite does not
yet implement these verbs. No release/tag is included in P13.

Reference: [agterm v0.26.0 HUD implementation](https://github.com/umputun/agterm/blob/v0.26.0/agtermCore/Sources/agtermCore/Hud.swift).
The nine positions, passive behavior and replacement semantics follow that interface;
rendering is native rather than an auxiliary terminal, and pane/cover targeting is refused
instead of silently widening to the whole session.
