# agterm → agwinterm gap analysis — 2026-07-08 delta refresh

Snapshot date: **2026-07-08**. Builds on the [2026-07-07 refresh](agterm-gap-analysis-2026-07.md)
(which covered agterm PRs #1–#161). This one covers **only what's new since** — agterm PRs merged
2026-07-07…07-08 (**#162–#175**) plus the in-flight open PRs — each checked against agwinterm's
current `main` with file evidence.

Status: **Done** / **Partial** / **Missing**. Effort: S (hours) / M (a day-ish) / L (multi-day).

---

## Summary

The delta is small and mostly **S/M** — agterm spent these two days on **control-API read-back
breadth**, a **hidden-toolbar** chrome mode, and a **richer reopen-closed** (workspaces + undo grace).
agwinterm independently shipped the two headline items in parallel: **native fullscreen** (F11) and
**reopen closed sessions** (Ctrl+Shift+R) — so those are already Done. What remains are read-back
parity, a zero-chrome toolbar mode, and a few small verbs.

---

## New items (agterm #162–#175)

| agterm PR | Feature | agwinterm | Effort | Notes |
|---|---|---|---|---|
| #160 | Native full screen | **Done** | — | `toggle_fullscreen` / F11 (Keymap.cs:45) |
| #174 | Reopen recently closed **items** | **Partial** | M | We reopen closed **sessions** (Ctrl+Shift+R, palette, ring of 25 — `Program.cs:1302`). Gaps vs agterm: also reopen closed **workspaces**; a **grace-period undo** (Ctrl/Cmd-Z + toast) right after a close; **persistent** Open-Recent across restarts (our ring is in-memory); a settings toggle. |
| #167/#168/#169 | Control-API **read-back** breadth | **Partial** | M | Our `SessionSnapshot`/`WorkspaceSnapshot` (`ISessionHost.cs:6`) expose id/name/active/status/overlay/notifications/flagged/background only. agterm now also reads: focused split pane, status **blink/color**, quick-terminal visibility, **split ratio**, **window geometry**, workspace focus, **sidebar mode**, **fullscreen/zoom**, **overlay size**. Mostly additive snapshot fields. |
| #173 | **Hidden** toolbar mode | **Missing** | S–M | We have `compact-toolbar` (Normal/Compact, `Program.cs:6341`) but not the third **Hidden** state: drop the titlebar row + window controls for a full-bleed terminal, with a thin (~6px) invisible drag strip along the top. |
| #163 | `session.overlay.resize` | **Missing** | S | Resize an open overlay in place via the control API. We have `session.overlay` but no resize verb. |
| #162 | Reveal `file://` links in Explorer | **Missing** | S | agterm Ctrl-clicks `file://` → reveal in Finder. We open links but don't `explorer.exe /select,<path>` to reveal-in-folder. |
| #159 | Preserve split focus on re-show + sidebar read-back | **Partial** | S | Sidebar-visibility read-back folds into the read-back parity row; verify split-focus survives hide/show. |
| #164 | Clear notification badge on refocus | **Verify** | S | We clear unread on visit (`UnreadOf`); confirm it also clears on app **refocus** of an already-visible session. |
| #161 | Bind shifted-symbol keys (`shift+<base>`) | **Verify** | S | Our chord grammar allows `shift+<key>` (Keymap.cs); confirm shifted **symbols** (e.g. `shift+/` → `?`) resolve. |
| #175 | Sidebar row alignment / thin drag strip | **Cosmetic** | S | Minor layout polish; low priority. |
| #166 | Agent-status hooks report to wrong session when AGTERM_* leaks | **Check** | S | We namespace `AGWINTERM_*`; verify a daemon that inherits them can't misroute status. |

## In-flight (agterm open PRs — not merged, future watch)

| PR | Feature | agwinterm | Notes |
|---|---|---|---|
| #158 | Terminal **zoom toggle** | **Partial** | We have Ctrl+wheel zoom + reset-to-default (`Program.cs:1224`); a one-key toggle between zoomed/default may be worth adding. |
| #121 | Promote split survivor into the main pane on primary exit | **Check** | Split-lifecycle behavior when the primary pane exits. |
| #151 | agtermctl tests on Linux | **N/A** | Their CI/test concern. |

---

## Status (2026-07-08, PR #38)

**Done** — the whole actionable delta:
- ✅ #162 reveal file:// in Explorer · ✅ #163 `session.overlay.resize` · ✅ #167/168/169 read-back
  parity (`window.state` + paneCount/focusedPane/splitRatios/statusBlink/overlaySize in `tree`) ·
  ✅ #173 hidden toolbar mode · ✅ #164 clear badge on refocus · ✅ #161 shifted-symbol keymaps
  (already worked; documented) · ✅ #174 reopen closed **workspaces** + unified most-recent reopen.
- #160 native fullscreen and reopen-closed-**sessions** were already shipped.

**Deferred (polish, not core):** #174's grace-period **undo toast** (Ctrl-Z right after a close) and
**persisting the reopen ring across restarts** — the in-memory ring matches the browser model and
full-tree restore covers a clean relaunch.

## Triage recommendation (original)

1. **Read-back parity (#167/168/169)** — highest leverage for the automation/agent story (agterm's
   differentiator). Additive snapshot fields; do this first. **M.**
2. **Hidden toolbar mode (#173)** — nice full-bleed option, self-contained. **S–M.**
3. **Reopen parity (#174)** — extend our reopen ring to **workspaces** + optional grace-period undo +
   persist across restarts. **M.**
4. **Small verbs** — `session.overlay.resize` (#163), reveal-in-Explorer (#162), zoom toggle (#158).
   Each **S**; batch them.
5. **Verify-only** — #164, #161, #166 (likely already handled; confirm and close out).

Nothing here is **L**. The single-window + control-API core stays at parity; this is breadth polish.
