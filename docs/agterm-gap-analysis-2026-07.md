# agterm → agwinterm gap analysis — July 2026 refresh

Snapshot date: **2026-07-07**. Three inputs, all evidence-based:

1. **agterm's merged PRs #1–#161** (the ~60 feature/fix PRs of 2026-07-01…07-07 post-date our
   [original gap analysis](agterm-gap-analysis.md) entirely) plus the 5 open PRs.
2. **Code verification**: every candidate feature checked against our source with file:line evidence
   (`Program.cs` / `Keymap.cs` / `TerminalConfig.cs` / `ControlServer.cs` / `TerminalEmulator.cs`).
3. **Community mining**: all three `umputun/agterm` *Show-and-tell* discussions (#153 quickloc,
   #138 multi-agent orchestration, #71 per-tab Claude resume) — what real users build and which
   capabilities their recipes depend on.

Status: **Done** / **Partial** / **Missing**. Effort: S (hours) / M (a day-ish) / L (multi-day).

---

## Summary

The single-window core parity of the original analysis is long closed (scratch/quick/overlay,
multi-window, MRU, search, selection/clipboard, flags, notifications are all shipped). The July
gap is different in kind: agterm spent the week on **control-API depth, hardening, and polish** —
and the community analysis shows the control API is exactly where the value is.

| Tier | Theme | Items |
|---|---|---|
| **P0 — community-load-bearing** | control API verbs recipes already depend on | `session.rename`, sidebar read-back, `session.seen`, `--version` |
| **P1 — security hardening** | agterm fixed these; we share the exposure | OSC title/cwd sanitization, OSC 52 gating, drag-drop escaping |
| **P2 — high-visibility UX** | users notice these daily | clickable links, light/dark dual themes, fullscreen, MRU persistence |
| **P3 — API completeness** | parity for scripters | placement flags, status color, overlay follow/background, wrap toggle |

---

## P0 — Community-load-bearing gaps (from show-and-tell)

What the three community recipes actually use, cross-checked against our verb inventory:

| Capability | Recipes using it | agwinterm status |
|---|---|---|
| Stable per-session id env (`AGTERM_SESSION_ID`) | **3/3** | **Done** — `AGWINTERM_SESSION_ID`, persisted in state.json |
| `tree --json` (id/name/active walk) | 2/3 | **Done** |
| `session select/new/text/type` by `--target id` | 2/3 | **Done** |
| **`session rename <name> --target <id>`** | #153 (quickloc's core primitive) | **MISSING** — we have `workspace.rename` but no `session.rename`; F2/inline only. **S** |
| Restore running commands on restart | #71, #153 | **Done** (`restore-commands`, opt-in) |
| Keymap custom commands + leader chords + `$AGW_SESSION_ID` | #153 | **Done** (single-level leader; agterm recipes use *nested* chords `ctrl+b>m>3` — ours is one follow-up chord only. **Partial**, M if nested wanted) |
| Claude Code status hooks → sidebar glyphs | #138 | **Done** (`install.hooks`) |
| `agtermctl --version` | #138 pain point (agterm lacks it too!) | **Easy win**: report version in `ping`. **S** |

Community pain points we can turn into differentiators (agterm hasn't fixed these either):

- **Per-pane session id** (#71: two `claude`s in a split collide on one session id) — we already
  expose `{AGW_PANE_ID}`; setting `AGWINTERM_PANE_ID` in each pane's env would fully solve it. **S**
- **Stale blocked-glyph after keystroke-answered prompt** (#138 needed a workaround script) — an
  auto-clear on user keystroke into a blocked session. **S**
- **Version-checkable CLI** (#138 resorted to reading Info.plist). **S**

Recipes also lean on undocumented behavior (#71: restore replays through the login shell) — worth
*documenting* our restore semantics in the README/skill so recipe authors can rely on it.

## P0 verbs agterm added this week that scripters will expect

| agterm PR | Verb / behavior | agwinterm | Effort |
|---|---|---|---|
| #156 | `session.seen` — clear unseen badge headlessly | **Missing** (badge clears only on visit, `Program.cs:779`) | S |
| #159 | sidebar visibility **read-back** | **Missing** — `sidebar` verb always returns `Ok`, state never exposed (`ControlServer.cs:167`) | S |
| #46 (older) | `session.text` read screen | **Done** |  |
| #134 | `session new/move --after/--before` placement | **Missing** — only up/down/top/bottom + workspace | S–M |

---

## P1 — Security hardening (agterm PRs #96, #102, #109, #112)

agterm treated these as vulnerabilities; we share the underlying exposure:

| agterm PR | Issue | agwinterm status | Effort |
|---|---|---|---|
| #109 | **OSC title/cwd control-char sanitization** — a hostile program can inject C0 controls into the title/cwd we later re-emit (title bar, custom-command `{AGW_CWD}` expansion → shell injection sub-case) | **Missing** — `OscDispatch` assigns `Title = text` / `Cwd = text` verbatim (`TerminalEmulator.cs:368-374`) | **S — do first** |
| #112 | **OSC 52 clipboard gating** (prompt reads, ask/deny writes) | **N/A-Missing** — no OSC 52 at all. If we ever add it, gate it from day one | M |
| #96 + #52 | **Drag-drop file paths as text**, newline-escaped | **Missing** — no `WM_DROPFILES` handling at all (feature gap + the escaping lesson comes free) | M |
| #102 | Dropped text sent as **bracketed paste** so it can't auto-execute | Bracketed paste itself **Done** (`TerminalEmulator.cs:222`); applies when drag-drop lands | — |

---

## P2 — High-visibility UX

| agterm PR | Feature | agwinterm status | Effort |
|---|---|---|---|
| #126 (+#162 open) | **Clickable links** — hand cursor on URL hover, validated open | **Missing** — no OSC 8, no URL detection, `IDC_HAND` never loaded (`Program.cs:324`) | M–L (URL-regex hover + Ctrl+click open is the M core; OSC 8 the +L) |
| #74 (open) + #146 | **Per-appearance themes** — `light:<t> dark:<t>` config form | **Missing** — `theme` is one string; no OS light/dark detection | M |
| #160 | **Native fullscreen** | **Missing** — maximize only | S (borderless WS_POPUP toggle + F11 keymap action) |
| #111 | **Ctrl+Tab MRU order persists** across relaunch | **Missing** — `_mru` in-memory only, not in `AppState` (`Program.cs:194`) | S |
| #133 | Workspace expand/collapse persisted | **Done** (`WorkspaceState.Expanded`, `Program.cs:5464`) | — |
| #122 | **Auto-follow attention** — idle-triggered jump to blocked sessions | **Missing** — manual `Ctrl+Alt+↑/↓` only | M |
| #161 | Shifted-**symbol** keybinds (`shift+comma`) | **Partial** — letters/digits/F-keys/named only; no punctuation key tokens (`Keymap.cs:206-211`) | S |
| #67 | Split toolbar icon shows *which* pane is visible when collapsed | **Missing** — static is-split glyph (`Program.cs:5081`) | S |
| #101, #70 | Confirm-close (opt-in), configurable new-session dir | **Done** (both) | — |

---

## P3 — Control-API completeness

| agterm PR | Feature | agwinterm status | Effort |
|---|---|---|---|
| #130 | **Pane-aware agent status** | **Partial** — stored per-pane (`TerminalSession.Status`) and settable per-pane, but sidebar/attention only surface the *active* pane's status; a background pane's blocked state is invisible (`Program.cs:271, 3570, 4226`) | M |
| #129 | Per-call **color override** on `session.status` | **Missing** — global `status-color-*` config only | S |
| #139 | Overlay `--follow` (no auto-switch default + opt-in) | **Partial** — the no-auto-switch default matches (`Program.cs:981`); the opt-in follow flag doesn't exist | S |
| #88 | Per-overlay **background color** | **Missing** | S |
| #68 | `session.background` **solid color** | **Partial** — ours is an image watermark, requires a path (`Program.cs:1796`) | S |
| #117/#90 | `--pane scratch` / pane-addressable `session.type` | **Partial** — any pane addressable by pane-id as `--target` (superset in one way), but no `scratch` selector token | S |
| #85 | Wrap-around session nav **as an option** | **Partial** — we always wrap (`Program.cs:1185`); agterm made it opt-in | S |
| #59 (older) | `session.resize` divider move | **Done** | — |

## Already at parity (no action)

Recent agterm work we already match: workspace expand/collapse persistence (#133), confirm-close
(#101), new-session dir (#70), right-click paste default (#63), restore commands (#61/#23),
session.text (#46), bracketed paste (DECSET 2004), full mouse reporting (X10/1002/1003/SGR),
`session.resize` (#59), status sounds + debounce (#38/#40), attention list (#35), per-session
background watermark (#32 — image form), `--workspace-name/--create-workspace` (#19), Dock/unseen
badges (#48 — as sidebar badges). agterm's dispatcher refactor series (#103–#137) is macOS
internal architecture — no port value.

## Watch list (agterm open PRs)

- **#158 terminal zoom toggle** and **#121 promote split survivor on primary exit** — small,
  likely to merge; both would be S ports.
- **#162 reveal `file://` links in Finder** — becomes relevant only after we do clickable links.

---

## Recommended order

1. **OSC sanitization** (P1, S) — security, trivial, no UX risk.
2. **`session.rename` verb** (P0, S) — unblocks the quickloc recipe pattern on Windows.
3. **`session.seen` + sidebar read-back + version-in-ping** (P0, 3×S) — one small control-API PR.
4. **MRU persistence + fullscreen + shifted-symbol keys** (P2, 3×S) — one polish PR.
5. **Clickable links (URL-regex core)** (P2, M) — the most user-visible single gap left.
6. **Pane-aware status surfacing + per-pane session id env** (P0/P3, M) — the agent-workflow pair;
   directly addresses two community pain points.
7. **light:/dark: dual themes** (P2, M) — pairs naturally with our theme system.
8. Drag-drop, auto-follow attention, OSC 52 (gated), remaining P3 flags — as demand appears.
