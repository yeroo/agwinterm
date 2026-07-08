# Memory & leak profile — 2026-07-09

First systematic leak hunt. Method: `tools/profile-memory.ps1` drives the app through two identical
rounds of churn (30× session create/close, 15× overlay open/close, 30× split on/off, 20× theme
switches, 30× window resizes, one 60k-line output blast) via the control API, sampling working set,
private bytes, kernel handles, GDI/USER handles and thread count between phases. **Round-2 growth over
the round-1 idle point is the leak signal** — round-1 growth alone is warm caches + lazy GC.

## Finding 1 (real, fixed): every closed session leaked a thread + ConPTY handles

`TerminalSession.Dispose()` called `_connection?.Kill()` but never **disposed** the connection.
ConPTY semantics: killing the child does NOT EOF the pseudoconsole's output pipe — only closing the
pseudoconsole does. So each closed session/overlay/split-pane left its output pump blocked in
`ReadAsync` forever: one pinned thread-pool thread (~1 MB stack) + the ConPTY/pipe/process handles.

Measured round-2 delta, before → after the fix:

| signal | before | after |
|---|---|---|
| threads | **+76** | **0** |
| kernel handles | **+1,227** | **0** |
| USER handles | **+78** | **0** |
| working set | +64.9 MB | +9.7 MB |

Fix: `Dispose()` now kills **and disposes** the connection (closes pseudoconsole + pipes, unblocking
the pump), and the pump treats `ObjectDisposedException` as normal shutdown. Both `DeElevatedPty` and
`AttachedPty` already had correct `Dispose()` implementations — they were simply never invoked.

## Finding 2 (not a leak): residual ~10 MB after heavy output

Three consecutive 60k-line blasts: 128 → 133 → 132 MB. Plateau, not linear growth — the first big
scrollback sizes the GC heap segments and later rounds reuse them. Normal .NET heap behaviour.

## Clean bills of health (static audit)

- All `IDWriteTextLayout`s are `using`-scoped; the ligature typography object is cached once.
- Kitty image + watermark bitmap caches evict with `Dispose()`; device-loss (`RecreateTarget`)
  disposes every device-bound resource before rebuilding.
- `WM_SIZE` uses `rt.Resize` (no target churn). GDI count is flat at 15 through all phases (Direct2D
  doesn't consume GDI handles).
- Theme churn ×20: +0.6 MB, nothing retained.
- `_metrics` (per-font-size TextFormat cache) is never cleared, but font family/size changes are
  restart-deferred, so entries can't go stale in-process; bounded by distinct zoom levels. Watch item.

## Steady state after fix

Baseline ~88 MB WS / 644 handles / 42 threads; a full double churn round ends at ~139 MB / 675 / 42
with handles/threads returning exactly to baseline. Re-run the harness after any PTY/session-lifecycle
change: `pwsh tools/profile-memory.ps1`.

---

# Performance profile (same session)

Headline numbers (Debug build, maximized window):
- **Startup → control-API responsive: 0.38 s.** Idle CPU: **0.47 %** of one core (cursor-blink timer).
- **Sustained output throughput: 18.5 s → 1.84 s for a 60k-line / 4 MB `type`** (3.2k → **32.6k lines/s**,
  ~87 % of a bare conhost window at 37.5k). App CPU for the blast: 19.1 s → **1.3 s**.

## Finding 3 (the big one, fixed): per-cell scroll dominated sustained output

`dotnet-stack` on the one hot thread (99 % of a core) showed the pump inside
`TerminalEmulator.ScrollRegionUp()` → `ScreenBuffer.get_Item`. Every LF at the bottom row scrolled the
grid **cell-by-cell through a bounds-checked indexer** (~rows×cols calls per line; hundreds of millions
for the blast). Fix: `ScreenBuffer.MoveRows/FillRow/CopyRowTo` — one `Array.Copy` (memmove) per scroll —
used by ScrollRegionUp/Down, InsertLines, DeleteLines, PushHistory. This alone was the 10× win.

## Supporting fixes (landed with it)

- **Output-driven frame cap** (`RedrawMinIntervalMs` = 15 ms): floods render at ~66 fps instead of
  per-chunk back-to-back frames; the first frame after a quiet period still paints immediately.
- **Snapshot rendering**: `RenderTerminal` now copies the visible viewport under the session lock
  (sub-ms) and draws outside it, so a slow frame no longer blocks the PTY pump (`Monitor.Enter`
  convoy seen in stacks).
- **Dedicated sync pump thread**: the ConPTY read pipe is a synchronous handle, so `ReadAsync` went
  through the async-over-sync layer (visible as `AsyncOverSyncWithIoCancellation` in traces). The pump
  is now a plain blocking `Read` on a `LongRunning` task with a 64 KB buffer.

## Method notes (for the next profiling pass)

- Emitter choice matters: `cmd for /L` and PowerShell pipelines are themselves slow (~0.5–1k lines/s)
  and will mask terminal-side wins — benchmark with `type <pregenerated file>`.
- A quoting mistake in `session new --command` fails fast inside cmd and looks exactly like a hang;
  screenshot the pane before trusting a timing number.
- `dotnet-stack report` (per-thread) beat `dotnet-trace` percentages here: find the hot thread by
  per-thread CPU delta first, then read its stack.
- Leak harness re-run after all perf changes: round-2 delta ≈ 0 (WS −0.5 MB, +1 thread, +7 handles —
  noise). The dedicated pump threads exit on dispose.
