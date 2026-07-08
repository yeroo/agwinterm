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
