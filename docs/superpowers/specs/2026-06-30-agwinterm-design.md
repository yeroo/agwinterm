# agwinterm — Native Windows Terminal for AI Coding Agents

**Design spec** · 2026-06-30
A Windows-native reimplementation of [umputun/agterm](https://github.com/umputun/agterm) (macOS, Swift/libghostty), built on the Microsoft-native stack with first-class Kitty graphics support.

---

## 1. Goal & Philosophy

agterm is a macOS terminal purpose-built for managing multiple concurrent AI coding-agent sessions: it organizes terminals into a **workspace → session** tree (not tabs), shows per-session **agent status** (active / blocked / completed), and exposes a **control API** plus a bundled agent skill so agents can drive their own layouts and self-report progress.

**agwinterm** delivers the same product on Windows, with these commitments:

- **Feature-complete parity** with agterm.
- **Fully native, no web rendering.** No WebView2, no xterm.js. The terminal grid is GPU-rendered with native Windows APIs.
- **Microsoft's way as much as possible:** C# + WinUI 3 (Windows App SDK), ConPTY, MSIX/toast notifications. Third-party dependencies limited to a few MIT libraries.
- **First-class Kitty graphics protocol** in v1, with a measurable performance target (see §8).

**Strategy:** reuse agterm's *design* and its *portable application logic*; reimplement only the *terminal engine*, which Windows cannot share (libghostty does not and will not run on Windows — see §4).

---

## 2. Architecture Overview

```
┌─ WinUI 3 shell (C#) ──────────────────────────────────────────────┐
│  sidebar · workspace/session tree · tabs · splits · command        │
│  palette · overlays · fuzzy switcher        ← ported from           │
│                                                agterm `agtermCore`  │
├─ SwapChainPanel (XAML never draws the grid → no XAML on hot path) ─┤
│  Renderer (Direct3D 11 frame):                                      │
│    • Win2D glyph-atlas text pass (AtlasEngine-style)                │
│    • Kitty image-compositing pass (GPU texture cache by image ID)   │
├─ Terminal core — PURE C#, behind ITerminalEngine façade ──────────┤
│  VT parser + cell grid + scrollback + Kitty graphics layer          │
│  (ported from SwiftTerm, seeded by VtNetCore)                       │
├─ ConPTY connection layer ─────────────────────────────────────────┤
│  Porta.Pty (pure ConPTY, MIT) · 1 HPCON + 1 reader Task per session │
│  · Windows 10 1809+                                                 │
└────────────────────────────────────────────────────────────────────┘
   Control API: named pipe \\.\pipe\agwinterm   (agterm used a unix socket)
   Agent hooks: PowerShell wrappers → ~/.claude/settings.json, Codex notify
   Persistence: %LocalAppData%\agwinterm\
```

### Layer choices (settled)

| Layer | Choice | Rationale |
|---|---|---|
| App language | **C#** | Microsoft's first-party app language; the role-equivalent of Swift on macOS. |
| UI framework | **WinUI 3 / Windows App SDK** | Win32-based (not UWP — it is the *un-doing* of the Win8/UWP sandbox model); the strategic Microsoft direction and the SwiftUI/AppKit parallel. Draws chrome only. |
| Grid host | **`SwapChainPanel`** | Purpose-built XAML host for GPU content; keeps XAML entirely off the terminal hot path. |
| Text rendering | **Win2D** glyph-atlas, AtlasEngine-style | GPU-accelerated, good C# ergonomics, MIT. Escape hatch to raw Direct2D/D3D11 via **Vortice.Windows** if a ceiling is hit. |
| Image rendering | **Direct3D 11 image-compositing pass**, texture cache keyed by Kitty image ID | Stable image IDs make caching trivial; this is the key to the performance target. |
| Terminal core | **Pure C#**, parser ported from **SwiftTerm**, seeded by **VtNetCore** source | Grid lives in managed memory → renderer reads directly with zero FFI marshalling; single-language debugging; long-term maintainability. SwiftTerm already implements Kitty graphics, giving a correct reference to port. |
| PTY | **Porta.Pty** (ConPTY, MIT) | Pure ConPTY (no winpty baggage); clean per-session lifetime. |
| Unicode width | **Wcwidth** (MIT) | CJK / wide-char column handling. |

### Why these and not the alternatives (decision record)

- **Not WebView2 + xterm.js** — rejected by hard "no web" constraint, despite being lowest-risk.
- **Not vendoring Windows Terminal's C++ core** — it is C++/WinRT with no supported embedding API; consuming it from a separate C# WinUI 3 app is unreliable (CsWinRT version-pinning issues). Used as a *reference design* only (AtlasEngine renderer, ConPTY sample).
- **Not FFI to a native core (alacritty_terminal / libvterm)** — viable and faster to robustness, but adds a second toolchain and cross-language grid marshalling each frame. Rejected in favor of a managed core for maintainability and zero-marshalling rendering.
- **Not libghostty** — no Windows support, not planned, C API not ready. The `ITerminalEngine` façade preserves the option to swap to `libghostty-vt` later if/when its C API ships on Windows.
- **Not "port Ghostty to Windows first"** — front-loads an unsolved, multi-month upstream port (new GPU backend + ConPTY + windowing) that the Ghostty project itself has deferred, while the UI still requires a 100% rewrite regardless. Disproportionate cost for a swappable component.

---

## 3. The `ITerminalEngine` Façade (the key seam)

A stable C# interface that mirrors agterm's surface contract (`GhosttySurfaceView.swift`), so the engine implementation stays swappable (managed core now; potentially `libghostty-vt` later):

```csharp
public interface ITerminalEngine : IDisposable {
    // input / lifecycle
    void Write(ReadOnlySpan<byte> bytes);     // bytes from PTY output OR injected text
    void Resize(int cols, int rows);
    void SendInput(ReadOnlySpan<byte> encodedKeys);

    // read access for renderer / control API
    IGridSnapshot GetGridSnapshot();          // cells, attrs, cursor, selection, damage
    string ReadSelection();
    IReadOnlyList<ImagePlacement> EnumerateImagePlacements();

    // search
    void StartSearch(string query, SearchOptions opts);
    void NavigateSearch(SearchDirection dir);

    // events
    event Action<DamageRegion> OnDamage;
    event Action<string> OnTitleChanged;
    event Action<string> OnCwdChanged;        // OSC 7
    event Action OnBell;
    event Action<ImagePlacement> OnImageAdded;
    event Action<uint> OnImageRemoved;        // by image ID
}
```

The renderer, control server, and UI all depend on this interface — never on the concrete engine.

---

## 4. Ported from agterm `agtermCore` (the product layer)

agterm already isolates host-free logic in a SwiftPM package (`agtermCore`) importing only Foundation/Observation — **no GhosttyKit, AppKit, or Metal**. That boundary is the porting line. These port nearly verbatim into C#:

| Component | Port notes |
|---|---|
| **Workspace → session tree** | Two-level tree; `AppStore` owns tree + selection, every structural mutation calls `save()`. `WindowLibrary` = multi-window, each window its own store. |
| **Persistence** | `Codable` JSON snapshots → `%LocalAppData%\agwinterm\windows\<id>.json`, with legacy-file migration. |
| **Control protocol + `agwintermctl` CLI** | Newline-delimited JSON, one request/connection. `ControlRequest{cmd,target,args}` / `ControlResponse{ok,result,error}`; 50+ commands (`tree`, `session.new/type/go/split/scratch/overlay/search/status`, `workspace.*`, `window.*`, `font.*`, `theme.set`, `keymap.reload`). Target resolution: UUID / unique prefix / `active`. **Transport: Unix socket → named pipe `\\.\pipe\agwinterm`** (perms-equivalent ACL, size cap, deadline). Each session shell gets `AGWINTERM_SESSION_ID/WORKSPACE_ID/WINDOW_ID/PIPE`. |
| **Agent status model + hook installer** | State is *pushed in* via `agwintermctl session status <idle\|active\|completed\|blocked>` (`--auto-reset/--blink/--sound`) — not parsed from output. Installer idempotently edits **`~/.claude/settings.json`**: `UserPromptSubmit`/`PostToolUse`→`active --blink`, `Stop`→`completed --auto-reset`, `Notification`(permission_prompt)→`blocked`; plus **Codex** `notify` and generic shell integration. **Wrapper scripts become PowerShell (`.ps1`/`.cmd`).** |
| **git-status parsing** | `git status --porcelain=v2`. |
| **keymap parser, settings model** | Direct port; `keymap.conf` semantics preserved (rebinds, custom commands, leader sequences). |

### Engine-specific (rewritten, not ported)
Everything under agterm's `agterm/Ghostty/`: VT parse, render, PTY I/O, `inject(text:)`, `readSelection()`, search callbacks, OSC-7 cwd, binding actions, surface lifecycle, and the surface-touching branches of the control server (`session.type/copy/search`, `font.*`).

---

## 5. Rendering Design

Single Direct3D 11 frame, presented via the SwapChainPanel swap chain (`CreateSwapChainForComposition` + `ISwapChainPanelNative.SetSwapChain`, WinUI interop header). Two passes:

### Text pass (AtlasEngine-style)
- Fixed monospace **cell matrix**; one instanced GPU quad per cell sampling its glyph from a **texture atlas**.
- Glyphs **rasterized once** (DirectWrite via Win2D) on first appearance, cached in a grow-only atlas keyed by Unicode cluster.
- **Shape per run of like-attributed cells** (not per cell) for ligatures; snap ligature advances to integer cell multiples; **offer a ligature-disable toggle**.
- **Font fallback** via custom `IDWriteFontFallback` so the user's monospace wins, with spillover for CJK / symbols / Powerline / box-drawing (box-drawing generated procedurally for seam-perfect rendering).
- **Wide chars** get 2 cells (East Asian Width via Wcwidth).
- **Dirty-row / dirty-cell** invalidation; redraws driven by PTY output, **not** a free-running loop.

### Image pass (Kitty graphics)
- **Decode once** (PNG/RGBA → GPU texture) and **cache by Kitty image ID**; never re-decode or re-upload on scroll/redraw.
- Composite image quads ordered by **z-index** (under/over text), clipped to the scroll region; placements scroll with grid content and are occluded correctly by text.
- **Memory-capped texture cache with LRU eviction** by image ID (covers high-DPI/large images).
- Animation (deferred nice-to-have): swap cached frames on a timer with no re-upload.

### Performance pitfalls explicitly avoided
Per-frame text shaping/rasterization; full-grid redraws; per-frame glyph re-rasterization; per-frame image re-decode/re-upload; many small draw calls / managed↔native crossings (batch to one instanced draw + one Present per frame); any retained-mode XAML for the grid.

---

## 6. Terminal Core Design

Pure-C#, ported from SwiftTerm's `EscapeSequenceParser` (table-driven Paul Williams DEC-ANSI state machine), cross-checked against Rust `vte`, seeded by VtNetCore where useful.

- **Parser:** incremental byte-at-a-time state machine + UTF-8 decode; CSI/SGR (incl. 256-color + truecolor), cursor/erase/scroll regions, OSC 0/2/7/8, alt buffer, DEC private modes, mouse reporting, bracketed paste.
- **Grid:** cell matrix (codepoint + attributes + hyperlink/image refs), main + alt screens, scrollback, cursor, selection, dirty-row damage tracking, reflow-on-resize.
- **Graphics layer:** Kitty **APC `_G…`** parsing — chunked transmission, RGB/RGBA/PNG, placement, delete/replace, z-index; placements anchored to cells in the grid model. (Ported from SwiftTerm's Kitty implementation.)

---

## 7. ConPTY Connection Layer

- **Porta.Pty** (pure ConPTY, MIT) as the primitive; vendor microsoft/terminal's `GUIConsole` P/Invoke layer only if zero-dependency control is later required.
- **Per session:** one HPCON + input-write handle + output-read handle + one dedicated **reader Task** (blocking `Stream.Read` loop — idle reads park in `ReadFile`, no CPU). Input serialized through a per-session `Channel<byte[]>`.
- **Scaling past a few dozen sessions:** switch reader to overlapped/IOCP `ReadAsync` to drop the per-session thread cost.
- **Requires Windows 10 1809+** (ConPTY exports); winpty fallback explicitly out of scope.
- Gotchas handled: never read+write one session's pipes on one thread; UTF-8 buffering across reads; teardown ordering (drain output during `ClosePseudoConsole`); Ctrl+C via `GenerateConsoleCtrlEvent` where a written `\x03` is insufficient.

---

## 8. Performance Requirements (Kitty graphics — benchmarkable)

Windows Terminal does **not** support the Kitty protocol, so the comparison set is kitty / Ghostty / WezTerm (and WT only for a future Sixel phase). Concrete, verifiable targets:

1. **Scrolling** a screen full of cached Kitty images holds the **display refresh rate**, with zero per-frame re-decode/re-upload.
2. **First-paint** of a newly transmitted static image is bounded and validated against kitty/Ghostty on identical hardware + content (docxy output as the fixture).
3. **Memory:** texture cache is capped with LRU eviction by image ID; no unbounded growth under repeated transmit/replace.

A benchmark harness using real docxy output is a deliverable of the graphics-renderer phase (§9, Phase 4) and gates that phase.

---

## 9. Phasing

Ordered to de-risk the hardest, least-differentiating work (the engine) first.

1. **Engine spike** — ConPTY (Porta.Pty) + minimal VT parser + Win2D text grid renders a working interactive shell in one WinUI 3 window. *Proves the riskiest path end-to-end.*
2. **Core hardening** — port SwiftTerm parser to shell+agent grade; conformance + golden-file tests against vim/tmux/htop/fzf and Claude Code/Codex TUIs.
3. **Graphics core** — port SwiftTerm's Kitty implementation: APC parsing + placement model in the grid.
4. **Graphics renderer** — GPU texture cache by image ID + z-ordered compositing pass; **benchmark gate (§8).**
5. **App layer** — port `agtermCore`: workspace/session tree, JSON persistence, multi-window.
6. **Control API + agent hooks** — named-pipe server, `agwintermctl` CLI, PowerShell hook installer, bundled agent skill.
7. **Parity features** — splits, scratch/quick overlays, search, fuzzy switcher, action palette, MRU switcher, keymap config, custom commands.
8. **Polish** — toast notifications, system sounds, command restoration (off by default, with denylist), ligature toggle, theming. *(Optional: Sixel as a second protocol for the literal "beat WT at Sixel" win.)*

---

## 10. Testing Strategy

- **Engine:** golden-file VT conformance (esctest-style) + unit tests on parser and grid mutations.
- **Graphics:** decode/cache unit tests; the §8 benchmark harness with docxy fixtures.
- **Control API:** protocol round-trip tests driving the named-pipe server via `agwintermctl`.
- **Agent hooks:** assert `~/.claude/settings.json` edits are idempotent and reversible.
- **Manual parity matrix:** the agterm feature list exercised against agwinterm.

---

## 11. v1 Non-Goals

- **Sixel and iTerm2 inline images** — Kitty covers docxy; addable later (Sixel slots into Phase 8).
- **Animation / video-like Kitty frames** — supported structurally but not the optimization target (docxy emits static images).
- **Full grapheme-cluster segmentation and pixel-perfect reflow** — basic wide-char/reflow only in v1.
- **SSH / remote / multiplexer backends** — local shell sessions only in v1; the connection layer is abstracted to allow them later.
- **winpty fallback** for pre-1809 Windows.

---

## 12. Key Dependencies (all MIT unless noted)

| Dependency | Purpose | License |
|---|---|---|
| Windows App SDK / WinUI 3 | App shell | MIT |
| Microsoft.Graphics.Win2D | Text rendering | MIT (verify bundled LICENSE) |
| Vortice.Windows | D3D11/DXGI/DirectWrite escape hatch | MIT |
| Porta.Pty | ConPTY PTY | MIT |
| Wcwidth | Column width | MIT |
| SwiftTerm (parser, ported — not a binary dep) | Reference for VT + Kitty parser | MIT |
| VtNetCore (source, seed reference) | Reference for C# grid/parser | MIT |
