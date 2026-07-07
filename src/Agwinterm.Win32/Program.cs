using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DCommon;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;
using Color = Agwinterm.Core.Color;

namespace Agwinterm.Win32;

/// <summary>
/// Native Win32 + Direct2D shell for agwinterm. We own the window procedure and the
/// message pump — keyboard input arrives via WM_KEYDOWN/WM_CHAR and goes straight to
/// the PTY, with no framework focus model in the way. Rendering mirrors the WinUI
/// OnDraw path but on an ID2D1HwndRenderTarget.
/// </summary>
internal partial class Program : ISessionHost, IWindowHost
{
    private const float PadX = 8f;
    private const float PadY = 6f;
    private const string ClassName = "AgwintermWin32";

    // Kept alive for the lifetime of the process so the GC never collects the thunk.
    private static WndProc _wndProc = null!;

    // Per-window instance routing. WindowProc is a static trampoline that resolves the owning
    // Program instance by HWND and dispatches to its instance handler. Frontmost is the instance
    // un-scoped control verbs act on (single window today; the seam for future --window targeting).
    private static readonly Dictionary<IntPtr, Program> _registry = new();
    private static Program? _creating;   // instance whose window is mid-CreateWindowExW (pre-registry)
    internal static Program Frontmost = null!;

    // ---- Multi-window (Wave F1b): each open window is one Program instance with its own tree.
    // The WindowLibrary is the app-scope index (all windows, open + closed) + the frontmost id.
    private static IntPtr _hInstance;
    private sealed class WinMeta
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsOpen { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public bool Max { get; set; }
    }
    private sealed class WindowsIndexFile
    {
        public int Version { get; set; } = 1;
        public string? Frontmost { get; set; }
        public List<WinMeta> Windows { get; set; } = new();
    }
    private static readonly List<WinMeta> _windowIndex = new();      // guarded by lock(_windowIndex)
    private static readonly Dictionary<string, Program> _byId = new();
    private static string? _frontmostId;

    internal string Id { get; set; } = Guid.NewGuid().ToString();    // this window's stable id
    internal string WinName { get; set; } = "";                      // this window's custom name (shows in the title)

    private IntPtr _hwnd;
    private static ID2D1Factory _d2d = null!;
    private static IDWriteFactory _dwrite = null!;
    private static IDWriteTextFormat _format = null!;
    private ID2D1HwndRenderTarget? _rt;
    private ID2D1SolidColorBrush? _brush;

    private static TerminalConfig _config = new();
    private static Theme _theme = Theme.Default;                 // active colour theme (renderer resolves through it)
    private static Theme? _themeBeforePreview;                   // saved when the theme picker opens (Esc reverts)
    private static List<Theme> _allThemes = new();
    private static Agwinterm.Pty.ShellProfiles.Config _profileCfg = new();   // shell profiles (profiles.json); app-global
    private TerminalSession? _session;   // mirrors the ACTIVE SURFACE (active pane, or a shown cover)
    // Auxiliary "cover" terminal drawn over the content region: scratch (per-session) or quick (per-app).
    private Pane? _cover;                // the shown cover, or null
    private int _coverKind;              // 0 none, 1 scratch, 2 quick, 3 overlay
    private Pane? _quick;                // the single per-app quick terminal (lazy; kept alive)
    // Overlays (Wave B3): an ephemeral program run over a session; vanishes when the program exits.
    private Ses? _ovlOwner;              // the session whose overlay is the current cover (kind 3)
    private string _lastOverlayExit = "no overlay"; // "exit N" once an overlay's program has exited
    private int _overlayExitCode;        // the last overlay program's exit code
    private readonly System.Threading.ManualResetEventSlim _overlayDone = new(false); // signalled on overlay exit (for --block)
    private static ControlServer? _control;

    // ---- Multi-session model (agterm workspaces -> sessions), mirrors the WinUI shell ----
    private readonly List<Workspace> _workspaces = new(); // source of truth; guarded by lock(_workspaces)
    private bool _restoring;                              // suppress SaveState while rebuilding from disk
    private Ses? _active;
    private const float DividerW = 6f;                       // gutter between panes
    private bool _divDragging;                        // dragging a pane divider
    private int _divLeft;                             // left-pane index of the divider being dragged
    // Actions queued from the pipe (background) thread to run on the UI thread.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _uiActions = new();

    // Chrome geometry (custom title bar + status bar, drawn in Direct2D).
    // Compact toolbar (agterm) shrinks the custom title bar; read live so a config toggle reflows everything.
    private static float TitleBarH => _config is { CompactToolbar: true } ? 30f : 40f;
    private const float FooterH = 34f;       // toolbar at the bottom of the sidebar
    private const float SidebarWFull = 220f;
    private const float CaptionBtnW = 46f;   // native min/max/close hit width
    private float _sidebarW = SidebarWFull;            // 0 when collapsed

    // Sidebar view mode + workspace focus (Wave D1). Tree = the normal workspace→session outline;
    // Flagged = a flat working-set of every flagged session across all workspaces. Focus (tree only)
    // shows just one workspace. The two are independent and both persist.
    private enum SidebarMode { Tree, Flagged }
    private SidebarMode _sidebarMode = SidebarMode.Tree;
    private string? _focusedWorkspaceId;               // when set (tree mode), render only this workspace
    private static readonly object _showAllMarker = new();    // sidebar row sentinel: click clears workspace focus

    private float ContentX => _sidebarW + PadX;        // terminal's left origin
    private float ContentY => TitleBarH + PadY;        // terminal's top origin (below the title bar)

    // Row hit-boxes rebuilt each paint: (top, bottom, isWorkspace, item(Workspace|Ses)).
    private readonly List<(float y0, float y1, bool isWs, object item)> _sidebarRows = new();
    // Chrome button hit-boxes rebuilt each paint: (x0, x1, action).
    private readonly List<(float x0, float x1, string action)> _titleButtons = new();
    private readonly List<(float x0, float x1, string action)> _footerButtons = new();

    // Sidebar drag-reorder (in-memory only; the shell has no persistence yet).
    private bool _sbPress;         // left button pressed on a sidebar list row (click-vs-drag pending)
    private object? _pressItem;    // row under the press (Ses|Workspace|null)
    private int _pressX, _pressY;  // press point (client px)
    private bool _dragging;        // past the threshold -> reordering
    private object? _dragItem;     // Ses or Workspace being dragged
    private int _dragX, _dragY;    // current cursor (client px) while dragging
    private const int DragThreshold = 5;
    private int _hoverCaption; // 0 none, 1 min, 2 max, 3 close (for hover paint)
    private int _capPressed;   // HT* of a pressed caption button (for click + pressed paint)

    // Custom chrome buttons: hover/press states + fade animation (so our buttons feel like the caption buttons).
    private string? _hotBtn;   // chrome button id currently under the cursor
    private string? _hotPaint; // button being painted with the hover fill (lingers during fade-out)
    private float _hotAlpha;   // 0..1 hover-fill alpha (eased by the fade timer)
    private string? _pressBtn; // chrome button id pressed (fires on release over the same button)
    private bool _mouseTracking; // TrackMouseEvent armed (for WM_MOUSELEAVE)
    private const int HoverTimer = 8;   // WM_TIMER id for the hover fade
    private bool _chromeDark = true; // active theme is dark (set by RecomputeChrome)
    private string? _toastText;
    private Ses? _toastTarget;   // when the toast is a notification banner: click it to jump to this session
    private Rect _toastRect;     // last-drawn banner rect (for click-to-jump hit-testing)
    private bool _trayAdded;     // whether the Shell_NotifyIcon tray icon has been created (for OS balloons)

    // Terminal text selection drag + multi-click (word/line) tracking.
    private bool _selecting;       // left-drag selecting text in the content region
    private bool _selMoved;        // moved past a cell during the drag (distinguishes click from select)
    private int _lastClickMs, _clickCount, _lastClickLine, _lastClickCol;
    private Pane? _selPane;        // pane the current selection drag belongs to (locked at press)
    private int _selAutoDir;       // drag-autoscroll: -1 mouse above pane, +1 below, 0 in-bounds
    private int _selMouseX;        // last drag X (client px) for column tracking during autoscroll
    private const int SelAutoTimer = 7;   // WM_TIMER id for drag-autoscroll ticks

    // Command palette overlay (⌃P sessions / ⌃⇧P actions / ⌃⇧I attention).
    private enum PaletteKind { None, Sessions, Actions, Attention, Themes, Custom, Windows, Omp, NewSession }
    private sealed class PalItem
    {
        public required string Label;
        public string Secondary = "";
        public string Search = "";
        public string Hint = "";
        public AgentStatus? Dot;      // leading status dot (attention/sessions)
        public object? Data;          // e.g. the Theme for live-preview
        public Action? Run;           // null = non-actionable placeholder
    }
    private PaletteKind _palette = PaletteKind.None;

    // In-terminal search (find bar over the active pane's buffer + scrollback).
    private bool _searchActive;
    private string _searchQuery = "";
    private readonly List<(int Line, int Col0, int Col1)> _searchMatches = new(); // absolute line index (history ++ live)
    private int _searchCur;

    // Keymap: chord (canonical string) -> action id or "command:<Label>"; custom commands from keymap.conf.
    private static Dictionary<string, string> _keymap = new(StringComparer.OrdinalIgnoreCase);
    private static List<Keymap.CmdDef> _commands = new();
    private static string[] _keymapDiag = Array.Empty<string>();

    // Leader/prefix chord (tmux-style): press the leader, then a second chord resolves against
    // _leaderBindings. While pending we paint a hint and swallow keys; Esc / timeout cancels.
    private static string? _leader;   // configured leader chord (from keymap.conf; app-global config)
    private static Dictionary<string, string> _leaderBindings = new(StringComparer.OrdinalIgnoreCase);
    private bool _leaderPending;
    private long _leaderAtMs;
    private const long LeaderTimeoutMs = 3000;

    // MRU (most-recently-used) session switcher — Ctrl+Tab walks the recency stack (Alt+Tab semantics).
    // _mru holds session ids, front = most recently active. During a walk we preview-select the target
    // but suppress recency updates; releasing Ctrl commits (fronts the target); Esc cancels back to start.
    private readonly List<string> _mru = new();
    private bool _mruWalking;
    private List<Ses> _mruSnapshot = new();  // recency-ordered sessions captured at walk start
    private int _mruIdx;                      // current target index into _mruSnapshot
    private Ses? _mruStart;                   // session active when the walk began (for Esc cancel)

    private string _palQuery = "";
    private int _palSel;
    private readonly List<PalItem> _palAll = new();
    private readonly List<PalItem> _palItems = new();
    private readonly List<(float y0, float y1, int idx)> _palRows = new();
    private Rect _palPanel;    // panel bounds (for click-outside-to-close)

    // Inline rename (native child EDIT overlaid on the row being renamed).
    private const int EDIT_ID = 101;
    private IntPtr _editHwnd;
    private object? _editing;         // Ses or Workspace currently being renamed
    private static WndProc _editProc = null!; // kept alive; subclasses the EDIT to catch Enter/Esc
    private IntPtr _editOrigProc;
    private static IntPtr _editFont;          // cached HFONT for the rename box (matches the sidebar)
    private static IntPtr _editBrush;         // cached dark background brush (WM_CTLCOLOREDIT)

    private void EnsureEditGdi()
    {
        // Segoe UI ~13px to match the sidebar row text; ClearType; dark bg like the sidebar.
        if (_editFont == IntPtr.Zero)
            _editFont = CreateFontW(-13, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        if (_editBrush == IntPtr.Zero)
            _editBrush = CreateSolidBrush(RGB(41, 51, 64)); // == SbHighlight, so the box blends with the row
    }

    // Chrome fonts (native Segoe UI for text, icon font for glyphs).
    private static IDWriteTextFormat _uiFont = null!;
    private static IDWriteTextFormat _uiSmall = null!;
    private static IDWriteTextFormat _uiTitle = null!;      // single centered title-bar line (vertically centered + ellipsized)
    private static IDWriteInlineObject _ellipsis = null!;   // "…" trimming sign for the title (kept alive)
    private static IDWriteTextFormat _iconFont = null!;
    private static IDWriteTextFormat _iconSmall = null!;   // small Fluent glyphs (e.g. the row flag marker)

    /// <summary>One terminal surface within a session. A session is a left→right row of panes.</summary>
    private sealed class Pane
    {
        public required string Id;
        public required TerminalSession S;
        public string? StartCwd;   // dir the shell was launched in (fallback cwd when OSC 7 is absent)
        public float FontSize;     // per-pane font zoom (pt)
        public float Ratio = 1f;   // fraction of the session's content width (ratios in a session sum to 1)
        public int ScrollOffset;   // lines scrolled up from the live bottom (0 = live; clamped to HistoryCount)
        public int Unread;         // unread desktop-notification count (OSC 9/777 / notify) since last visit
        // Text selection (absolute line index: [0..HistoryCount) history, then the live grid rows).
        public bool HasSel;
        public int SelAncLine, SelAncCol, SelFocLine, SelFocCol;
        public void ClearSel() => HasSel = false;
    }

    private sealed class Ses
    {
        public required string Id;
        public required string Name;
        public required Workspace Ws;
        public readonly List<Pane> Panes = new();
        public int Active;         // index of the focused pane
        public bool Flagged;       // durable working-set flag (survives moves; persisted; drives flagged sidebar mode)
        public string? CustomName; // user-renamed title (null = show the cwd/OSC title in the title bar, agterm-style)
        public string? ProfileName; // shell profile this session launched with (null = default; persisted)
        public Pane? Scratch;      // per-session scratch terminal (lazy; kept alive when hidden; not restored)
        public Pane? Overlay;      // ephemeral overlay terminal running a program over this session (Wave B3)
        public int OverlaySizePercent; // 0 = full content region; 1..100 = centered floating panel
        public bool OverlayWait;   // keep the overlay after its program exits (press a key to close)
        public bool OverlayExited; // the overlay's program has exited and it's awaiting a key
        // Wave F2: per-session background watermark (a faint image drawn behind the terminal of every pane).
        public string? BgPath;      // absolute path to the copied image under AppDir\backgrounds (null = none)
        public int BgOpacity = 15;  // 0..100 (drawn opacity of the watermark)
        public string BgMode = "fit"; // fit | fill | center | tile
        public Pane ActivePane => Panes[Math.Clamp(Active, 0, Panes.Count - 1)];
        // Back-compat shims: existing code that said ses.S / ses.FontSize / ses.StartCwd
        // now refers to the ACTIVE PANE, so most single-pane logic is unchanged.
        public TerminalSession S => ActivePane.S;
        public float FontSize { get => ActivePane.FontSize; set => ActivePane.FontSize = value; }
        public string? StartCwd { get => ActivePane.StartCwd; set => ActivePane.StartCwd = value; }
    }

    private sealed class Workspace
    {
        public required string Id;
        public required string Name;
        public readonly List<Ses> Sessions = new();
        public bool Expanded = true;
    }

    private float _cellW = 8, _cellH = 16;
    private bool _cursorOn = true;
    private readonly StringBuilder _run = new(256);   // reused per text run (no per-cell alloc)
    private int _redrawPending;                       // coalesces redraw requests into one paint

    // Decoded Kitty images, keyed by the current KittyImage instance so a retransmit
    // (new bytes, same id) re-decodes and the stale texture is pruned/disposed.
    private readonly Dictionary<KittyImage, ID2D1Bitmap> _imageCache = new();
    private readonly HashSet<KittyImage> _decoding = new();               // decode in flight (UI-thread set)
    // Background-decoded pixels waiting to be uploaded to a GPU texture on the UI thread.
    // bgra == null signals a decode failure (so we can drop it from _decoding without retrying forever).
    private readonly System.Collections.Concurrent.ConcurrentQueue<(KittyImage img, byte[]? bgra, int w, int h)> _decoded = new();
    private static readonly bool _noImages = Environment.GetEnvironmentVariable("AGWINTERM_NOIMG") == "1";

    // Wave F2: session background watermarks. A separate cache keyed by the copied file path
    // (one bitmap per distinct image, at native size); decode happens off the UI thread just like
    // the Kitty pipeline, so a background image never stalls rendering — it appears on a later frame.
    private readonly Dictionary<string, ID2D1Bitmap> _bgCache = new();
    private readonly HashSet<string> _bgDecoding = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string path, byte[]? bgra, int w, int h)> _bgDecoded = new();

    [STAThread]
    private static void Main()
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        // Process-global setup (config/themes/keymap + window class + shared D2D/DWrite objects).
        _config = LoadOrCreateConfig();
        _allThemes = LoadThemes();
        _theme = FindTheme(_config.Theme);
        LoadKeymap();

        IntPtr hInstance = GetModuleHandleW(null);
        _wndProc = WindowProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
            style = CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS,
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
            lpszClassName = ClassName,
        };
        if (RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("RegisterClassExW failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());

        // Direct2D / DirectWrite (shared across windows; DWrite objects are device-independent).
        _d2d = D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _format = CreateTextFormat(_config);
        _uiFont = NewChromeFormat("Segoe UI", 13f, center: false);
        _uiSmall = NewChromeFormat("Segoe UI", 11.5f, center: false);
        _uiTitle = NewChromeFormat("Segoe UI", 13f, center: true); // vertically centered single line
        _ellipsis = _dwrite.CreateEllipsisTrimmingSign(_uiTitle);
        _uiTitle.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, _ellipsis);
        _iconFont = NewChromeFormat("Segoe Fluent Icons", 14f, center: true);
        _iconSmall = NewChromeFormat("Segoe Fluent Icons", 10.5f, center: true);

        // Multi-window: load the window library (migrating a legacy state.json), then open every
        // window that was open at last quit. Each open window is its own Program instance routed by
        // HWND; Frontmost is what un-scoped control verbs act on.
        _hInstance = hInstance;
        _profileCfg = Agwinterm.Pty.ShellProfiles.Load(AppDir);   // shell profiles (seed profiles.json on first run)
        LoadOrMigrateIndex();
        List<WinMeta> toOpen;
        lock (_windowIndex)
        {
            toOpen = _windowIndex.Where(m => m.IsOpen).ToList();
            if (toOpen.Count == 0 && _windowIndex.Count > 0) { _windowIndex[0].IsOpen = true; toOpen.Add(_windowIndex[0]); }
        }
        Program? front = null;
        foreach (var m in toOpen)
        {
            var w = CreateWindowInstance(m);
            if (m.Id == _frontmostId) front = w;
            front ??= w;
        }
        Frontmost = front!;

        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    /// <summary>Per-window bootstrap: create the HWND, its render target, and the initial session.</summary>
    private void Boot(IntPtr hInstance)
    {
        RecomputeChrome();
        MeasureCell();

        // Window size/position comes from the WindowLibrary entry (set by CreateWindowInstance);
        // _geoValid/_geo* are already populated for a restored/positioned window.
        // Created hidden (no WS_VISIBLE) so we can position it before the first paint; shown at the end.
        _creating = this;                    // so the WindowProc trampoline can resolve us during CreateWindowExW
        _hwnd = _geoValid
            ? CreateWindowExW(0, ClassName, "agwinterm", WS_OVERLAPPEDWINDOW,
                _geoX, _geoY, _geoW, _geoH, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero)
            : CreateWindowExW(0, ClassName, "agwinterm", WS_OVERLAPPEDWINDOW,
                CW_USEDEFAULT, CW_USEDEFAULT, 1040, 660, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        _creating = null;
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowExW failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        _registry[_hwnd] = this;

        // Win11 rounded corners: our custom frame (WM_NCCALCSIZE) suppresses the automatic rounding,
        // so opt in explicitly. Harmless no-op on Win10 / older.
        try { int pref = DWMWCP_ROUND; DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); } catch { }

        // App icon for the window (taskbar + alt-tab); the exe icon comes from <ApplicationIcon>.
        try
        {
            string icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "agwinterm.ico");
            if (System.IO.File.Exists(icoPath))
            {
                IntPtr hIconBig = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                IntPtr hIconSm = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                if (hIconBig != IntPtr.Zero) SendMessageW(_hwnd, WM_SETICON, (IntPtr)ICON_BIG, hIconBig);
                if (hIconSm != IntPtr.Zero) SendMessageW(_hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hIconSm);
            }
        }
        catch { /* icon is cosmetic; ignore load failures */ }

        CreateRenderTarget();
        StartSession();

        // Apply the custom frame (WM_NCCALCSIZE strips the OS title bar) before showing.
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        // Drives the cursor pulse (when cursor-blink is on) AND the agent-status blink pulse, so it
        // runs unconditionally; the cursor render itself stays solid when cursor-blink is disabled.
        SetTimer(_hwnd, (IntPtr)1, (uint)_config.CursorBlinkMs, IntPtr.Zero);

        ShowWindow(_hwnd, _geoValid && _geoMax ? SW_MAXIMIZE : SW_SHOW);
        _wasMaximized = IsZoomed(_hwnd);
        ApplyWindowOpacity();
        UpdateWindow(_hwnd);
    }

    private static IDWriteTextFormat CreateTextFormat(TerminalConfig cfg)
    {
        float px = (float)cfg.FontSize;
        try { return NewFormat(cfg.FontFamily, px); }
        catch { return NewFormat("Consolas", px); }
    }

    private static IDWriteTextFormat NewFormat(string family, float px)
    {
        var f = _dwrite.CreateTextFormat(family, null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px);
        f.WordWrapping = WordWrapping.NoWrap;
        f.TextAlignment = TextAlignment.Leading;
        f.ParagraphAlignment = ParagraphAlignment.Near;
        return f;
    }

    private static IDWriteTextFormat NewChromeFormat(string family, float px, bool center)
    {
        IDWriteTextFormat f;
        try { f = _dwrite.CreateTextFormat(family, null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px); }
        catch { f = _dwrite.CreateTextFormat("Segoe UI", null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px); }
        f.WordWrapping = WordWrapping.NoWrap;
        f.TextAlignment = center ? TextAlignment.Center : TextAlignment.Leading;
        f.ParagraphAlignment = ParagraphAlignment.Center; // vertically centre within the row rect
        return f;
    }

    private void MeasureCell()
    {
        using var run = _dwrite.CreateTextLayout(new string('M', 10), _format, 4096f, 4096f);
        using var one = _dwrite.CreateTextLayout("M", _format, 4096f, 4096f);
        // Use the font's exact advance (do NOT round): text is drawn a run at a time, so the
        // glyph advance must equal the grid step or long runs (e.g. horizontal box-drawing
        // borders) drift off the per-column grid and stop meeting the vertical borders.
        _cellW = run.Metrics.Width / 10f;
        _cellH = MathF.Round(one.Metrics.Height);
        if (_cellW < 1) _cellW = 8;
        if (_cellH < 1) _cellH = 16;
    }

    // Per-font-size terminal metrics (text format + cell size), built on demand and cached.
    // The chrome (sidebar/title bar) keeps the base _cellW/_cellH/_format; only the terminal
    // content region scales with the ACTIVE session's font zoom.
    private static readonly Dictionary<float, (IDWriteTextFormat Fmt, float CellW, float CellH)> _metrics = new();

    private static (IDWriteTextFormat Fmt, float CellW, float CellH) Metrics(float px)
    {
        if (_metrics.TryGetValue(px, out var m)) return m;
        IDWriteTextFormat fmt;
        try { fmt = NewFormat(_config.FontFamily, px); }
        catch { fmt = NewFormat("Consolas", px); }
        using var run = _dwrite.CreateTextLayout(new string('M', 10), fmt, 4096f, 4096f);
        using var one = _dwrite.CreateTextLayout("M", fmt, 4096f, 4096f);
        float cw = run.Metrics.Width / 10f, ch = MathF.Round(one.Metrics.Height);
        if (cw < 1) cw = 8;
        if (ch < 1) ch = 16;
        m = (fmt, cw, ch);
        _metrics[px] = m;
        return m;
    }

    private float ActiveFontSize() => _active is { FontSize: > 0 } a ? a.FontSize : (float)_config.FontSize;
    private (IDWriteTextFormat Fmt, float CellW, float CellH) CurrentMetrics() => Metrics(ActiveFontSize());

    private void CreateRenderTarget()
    {
        GetClientRect(_hwnd, out RECT rc);
        int w = Math.Max(1, rc.right - rc.left);
        int h = Math.Max(1, rc.bottom - rc.top);

        var props = new RenderTargetProperties
        {
            Type = RenderTargetType.Default,
            PixelFormat = new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
            DpiX = 96f,
            DpiY = 96f,
        };
        var hwndProps = new HwndRenderTargetProperties
        {
            Hwnd = _hwnd,
            PixelSize = new SizeI(w, h),
            PresentOptions = PresentOptions.None,
        };
        _rt = _d2d.CreateHwndRenderTarget(props, hwndProps);
        // Grayscale AA (like Windows Terminal): glyphs sit at fractional x on the grid, and
        // ClearType would fringe vertical strokes (e.g. box-drawing borders) with colour.
        _rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _brush = _rt.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
    }

    /// <summary>Grid size for the content region using the given font size's cell metrics.</summary>
    private (int cols, int rows) GridSizeFor(float px)
    {
        var (_, cw, ch) = Metrics(px);
        GetClientRect(_hwnd, out RECT rc);
        int w = rc.right - rc.left, h = rc.bottom - rc.top;
        int cols = Math.Max(1, (int)((w - _sidebarW - 2 * PadX) / cw));
        int rows = Math.Max(1, (int)((h - TitleBarH - 2 * PadY) / ch));
        return (cols, rows);
    }

    private (int cols, int rows) GridSize() => GridSizeFor(ActiveFontSize());

    private void StartSession()
    {
        // One control server for the whole app (one pipe); it resolves --window through the library.
        if (_control is null)
        {
            _control = new ControlServer(this, this, "agwinterm");
            _control.Start();
        }
        if (TryRestoreState()) return;
        var ws = CreateWorkspace(Guid.NewGuid().ToString(), null);
        CreateSession(Guid.NewGuid().ToString(), null, null, ws, makeActive: true);
    }

    // ---- Session/workspace management (UI thread) ----

    private List<Ses> AllSessions()
    {
        lock (_workspaces) return _workspaces.SelectMany(w => w.Sessions).ToList();
    }

    private Workspace ActiveWorkspace()
    {
        lock (_workspaces)
        {
            if (_active is not null) return _active.Ws;
            if (_workspaces.Count == 0) _workspaces.Add(new Workspace { Id = Guid.NewGuid().ToString(), Name = "workspace 1" });
            return _workspaces[0];
        }
    }

    private Workspace CreateWorkspace(string id, string? name)
    {
        Workspace ws;
        lock (_workspaces)
        {
            ws = new Workspace { Id = id, Name = name ?? $"workspace {_workspaces.Count + 1}" };
            _workspaces.Add(ws);
        }
        RequestRedraw();
        SaveState();
        return ws;
    }

    /// <summary>Create one terminal pane (its own ConPTY, env, wiring) sized for the given font.
    /// When <paramref name="command"/> is set, that argv runs as the pane's process instead of the shell.</summary>
    private Pane CreatePane(string paneId, Workspace ws, string? cwd, float fontSize, string? command = null,
        bool shellWrap = false, bool interactive = false, Dictionary<string, string>? extraEnv = null, string? profileName = null)
    {
        var (cols, rows) = GridSizeFor(fontSize);
        var session = new TerminalSession(cols, rows);
        session.Emulator.ScrollbackMax = _config.Scrollback;
        var pane = new Pane { Id = paneId, S = session, StartCwd = cwd, FontSize = fontSize };
        // New output snaps this pane back to the live bottom (so output jumps you out of scrollback)
        // and clears any selection (its absolute coordinates would otherwise shift under new history).
        session.OutputReceived += () => { pane.ScrollOffset = 0; pane.ClearSel(); RequestRedraw(); };
        // On a transition INTO blocked, play the configured blocked-sound (best-effort, off the UI thread).
        AgentStatus lastStatus = session.Status;
        session.StatusChanged += () =>
        {
            var now = session.Status;
            if (now == AgentStatus.Blocked && lastStatus != AgentStatus.Blocked) PlayBlockedSound();
            lastStatus = now;
            RequestRedraw();
        };
        // An explicit --sound on session.status: play its spec (null => default alert).
        session.SoundRequested += PlayStatusSound;
        session.Emulator.Notified += (title, body) => Post(() => OnNotified(pane, title, body));
        var env = new Dictionary<string, string>
        {
            ["AGWINTERM"] = "1",
            ["AGWINTERM_ENABLED"] = "1",
            ["AGWINTERM_PIPE"] = _control!.PipeName,
            ["AGWINTERM_SESSION_ID"] = paneId,       // each pane is independently targetable
            ["AGWINTERM_WORKSPACE_ID"] = ws.Id,
            ["AGWINTERM_WINDOW_ID"] = Id,
        };
        if (extraEnv is not null) foreach (var kv in extraEnv) env[kv.Key] = kv.Value; // custom-command $AGW_* context
        if (!string.IsNullOrWhiteSpace(command) && interactive)
            // Run the command in a fresh shell that STAYS OPEN afterwards (custom-command "new" mode):
            // the user's profile loads (oh-my-posh etc.), the command runs, then it's an interactive shell.
            _ = session.StartAsync("powershell.exe", new[] { "-NoLogo", "-NoExit", "-Command", command! }, extraEnv: env, cwd: cwd);
        else if (!string.IsNullOrWhiteSpace(command) && shellWrap)
            // Run the whole command line through cmd so shell syntax works AND the child's exit code
            // propagates. Verbatim so it becomes exactly `cmd.exe /c <command>` (no extra quoting,
            // which cmd's /c quote-stripping rules would otherwise mangle).
            _ = session.StartAsync("cmd.exe", new[] { "/c", command! }, verbatimCommandLine: true, extraEnv: env, cwd: cwd);
        else if (!string.IsNullOrWhiteSpace(command))
        {
            var argv = ParseArgv(command);
            if (argv.Length > 0)
                _ = session.StartAsync(argv[0], argv[1..], extraEnv: env, cwd: cwd);
            else
                LaunchShell(session, profileName, env, cwd);
        }
        else LaunchShell(session, profileName, env, cwd);   // launch the chosen shell profile (default = Windows PowerShell)
        return pane;
    }

    /// <summary>Minimal argv split (whitespace-separated, double-quotes group). For session --command.</summary>
    private static string[] ParseArgv(string s)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        bool inQuote = false, has = false;
        foreach (char ch in s)
        {
            if (ch == '"') { inQuote = !inQuote; has = true; }
            else if (char.IsWhiteSpace(ch) && !inQuote) { if (has) { args.Add(cur.ToString()); cur.Clear(); has = false; } }
            else { cur.Append(ch); has = true; }
        }
        if (has) args.Add(cur.ToString());
        return args.ToArray();
    }

    /// <summary>
    /// pwsh launch args: an interactive shell (the user's profile / oh-my-posh loads normally) PLUS an
    /// out-of-the-box live-cwd wrap. The wrap is passed via -EncodedCommand so it runs AFTER the profile
    /// and WRAPS the existing prompt to emit OSC 7 (composes with oh-my-posh, never replaces it); it's
    /// guarded by the same $__agwSI sentinel as the opt-in $PROFILE installer, so the two never
    /// double-wrap. -NoExit keeps the shell interactive after the wrap runs.
    /// </summary>
    private static string[] ShellArgs()
    {
        string wrap = Agwinterm.Pty.ShellIntegrationInstaller.PromptWrap;
        // If an oh-my-posh theme is configured, apply it after the profile, then let the wrap capture
        // that (new) prompt so live cwd (OSC 7) still works.
        string? omp = (!_config.OmpIntegration || string.IsNullOrWhiteSpace(_config.OmpTheme)) ? null : Agwinterm.Pty.OmpThemes.Resolve(_config.OmpTheme);
        string script = omp is not null
            ? "oh-my-posh init pwsh --config '" + omp.Replace("'", "''") + "' | Invoke-Expression\n" + wrap
            : wrap;
        string enc = System.Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return new[] { "-NoLogo", "-NoExit", "-EncodedCommand", enc };
    }

    /// <summary>Resolve a profile by name (case-insensitive), else the configured default, else null.</summary>
    private static Agwinterm.Pty.ShellProfile? ResolveProfile(string? name)
    {
        var profs = _profileCfg.Profiles;
        if (profs.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var m = profs.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }
        return profs.FirstOrDefault(p => p.Name.Equals(_profileCfg.Default, StringComparison.OrdinalIgnoreCase)) ?? profs[0];
    }

    /// <summary>Launch a session's shell from its profile. PowerShell profiles (powershell.exe / pwsh) with
    /// no custom args get the OSC-7 cwd wrap + omp injection (today's default behavior); everything else
    /// (cmd, Git Bash, WSL, custom) launches its raw command + args. AGWINTERM_* env is passed regardless.</summary>
    private void LaunchShell(TerminalSession session, string? profileName, Dictionary<string, string> env, string? cwd)
    {
        var prof = ResolveProfile(profileName);
        string cmd = prof?.Command is { Length: > 0 } c ? c : "powershell.exe";
        string[]? pargs = prof?.Args;
        string? pcwd = string.IsNullOrEmpty(cwd) ? prof?.Cwd : cwd;
        string exe = Path.GetFileName(cmd).ToLowerInvariant();
        bool isPwsh = exe is "powershell.exe" or "pwsh.exe" or "pwsh";
        if (isPwsh && (pargs is null || pargs.Length == 0))
            _ = session.StartAsync(cmd, ShellArgs(), extraEnv: env, cwd: pcwd);        // wrap + omp (cwd-in-title)
        else
            _ = session.StartAsync(cmd, pargs ?? Array.Empty<string>(), extraEnv: env, cwd: pcwd); // raw shell
    }

    private Ses CreateSession(string id, string? name, string? cwd, Workspace ws, bool makeActive, float? fontSize = null,
        string? command = null, bool interactive = false, Dictionary<string, string>? extraEnv = null, string? profileName = null)
    {
        float fs = fontSize is > 0 ? fontSize.Value : (float)_config.FontSize;
        // No explicit cwd → resolve by the new-session-directory mode (home | current | custom).
        if (string.IsNullOrEmpty(cwd))
        {
            string? want = _config.NewSessionDirMode switch
            {
                "current" => _active is not null ? CurrentDirOf(_active) : null,
                "custom" => _config.NewSessionDir,
                _ /*home*/ => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (!string.IsNullOrWhiteSpace(want) && Directory.Exists(want)) cwd = want;
        }
        int ordinal;
        lock (_workspaces) ordinal = ws.Sessions.Count + 1;
        var ses = new Ses { Id = id, Name = name ?? $"session {ordinal}", Ws = ws, ProfileName = profileName };
        ses.Panes.Add(CreatePane(id, ws, cwd, fs, command, interactive: interactive, extraEnv: extraEnv, profileName: profileName));   // first pane shares the session id (control-API back-compat)
        ses.Active = 0;

        lock (_workspaces) { ws.Sessions.Add(ses); ws.Expanded = true; }

        if (makeActive || _active is null) SetActive(ses);
        else RequestRedraw();
        SaveState();
        return ses;
    }

    /// <summary>Append an extra pane to an existing session (used by split-layout restore).</summary>
    private Pane AppendPane(Ses ses, string paneId, string? cwd, float fontSize)
    {
        var p = CreatePane(paneId, ses.Ws, cwd, fontSize, profileName: ses.ProfileName);
        lock (_workspaces) ses.Panes.Add(p);
        return p;
    }

    // ---- Pane layout ----

    /// <summary>Content region (px) available to the terminal, right of the sidebar and below the title bar.</summary>
    private (float x0, float y0, float w, float h) ContentArea()
    {
        float x0 = _sidebarW + PadX, y0 = TitleBarH + PadY;
        float w = MathF.Max(1f, ClientW() - _sidebarW - 2 * PadX);
        float h = MathF.Max(1f, ClientH() - TitleBarH - 2 * PadY);
        return (x0, y0, w, h);
    }

    /// <summary>Lay out a session's panes as columns (px) by ratio, with a divider gutter between.</summary>
    private List<(Pane pane, float x, float y, float w, float h)> PaneLayout(Ses ses)
    {
        var (x0, y0, totalW, totalH) = ContentArea();
        int n = ses.Panes.Count;
        float avail = MathF.Max(n, totalW - (n - 1) * DividerW);
        float sum = ses.Panes.Sum(p => p.Ratio);
        if (sum <= 0) { foreach (var p in ses.Panes) p.Ratio = 1f / n; sum = 1f; }
        var list = new List<(Pane, float, float, float, float)>(n);
        float x = x0;
        for (int i = 0; i < n; i++)
        {
            float w = avail * (ses.Panes[i].Ratio / sum);
            list.Add((ses.Panes[i], x, y0, w, totalH));
            x += w + DividerW;
        }
        return list;
    }

    /// <summary>Resize every pane's PTY grid to fit its column using the pane's own font metrics.</summary>
    private void RegridSession(Ses ses)
    {
        foreach (var (pane, _, _, w, h) in PaneLayout(ses))
        {
            var (_, cw, ch) = Metrics(pane.FontSize);
            int cols = Math.Max(1, (int)(w / cw)), rows = Math.Max(1, (int)(h / ch));
            if (pane.S.Cols != cols || pane.S.Rows != rows) pane.S.Resize(cols, rows);
            pane.ScrollOffset = Math.Clamp(pane.ScrollOffset, 0, pane.S.Emulator.HistoryCount);
        }
    }

    private void SetActive(Ses ses)
    {
        _active = ses;
        ClearUnread(ses);   // visiting a session clears its notification badge (agterm's "cleared when seen")
        // Auto-reset-on-select: a status marked auto-reset clears to idle once the user looks at the session.
        foreach (var p in ses.Panes)
            if (p.S.AutoReset && p.S.Status is AgentStatus.Completed or AgentStatus.Blocked)
                p.S.SetStatus(AgentStatus.Idle);
        // A scratch/overlay cover belongs to the previous session; drop it (quick is per-app, kept).
        if (_coverKind is 1 or 3) { _cover = null; _coverKind = 0; _ovlOwner = null; }
        // If the newly-active session has an overlay up, re-show it as the cover.
        if (ses.Overlay is not null) { _cover = ses.Overlay; _coverKind = 3; _ovlOwner = ses; }
        _session = ActiveSurface()?.S;                          // a quick cover (if any) keeps input; else the new pane
        RegridSession(ses);
        if (_cover is not null) RegridCover();
        TouchMru(ses);   // move to front of the recency stack (suppressed mid-walk so previews don't reorder)
        RequestRedraw();
        SaveState();
    }

    // ---- MRU session recency stack (Ctrl+Tab switcher) ----

    /// <summary>Move a session to the front of the recency stack. No-op while a walk is in progress
    /// (preview-selects during a walk must not rewrite the stack — only the commit does).</summary>
    private void TouchMru(Ses ses)
    {
        if (_mruWalking) return;
        _mru.Remove(ses.Id);
        _mru.Insert(0, ses.Id);
    }

    /// <summary>Drop dead ids and append any live session not yet tracked (unseen go to the back).</summary>
    private void EnsureMru()
    {
        var live = AllSessions();
        _mru.RemoveAll(id => !live.Any(s => s.Id == id));
        foreach (var s in live) if (!_mru.Contains(s.Id)) _mru.Add(s.Id);
    }

    private Ses? FindSes(string id)
    {
        lock (_workspaces) return _workspaces.SelectMany(w => w.Sessions).FirstOrDefault(s => s.Id == id);
    }

    /// <summary>Begin a walk: snapshot the recency order and park the cursor on the active session.
    /// Returns false (no walk) when fewer than 2 sessions exist.</summary>
    private bool StartWalk()
    {
        EnsureMru();
        var order = _mru.Select(FindSes).Where(s => s is not null).Cast<Ses>().ToList();
        if (order.Count < 2) return false;               // 1 session: no-op, no HUD
        _mruWalking = true;
        _mruSnapshot = order;
        _mruStart = _active;
        _mruIdx = _active is null ? 0 : Math.Max(0, order.IndexOf(_active));
        return true;
    }

    /// <summary>Advance the Ctrl+Tab walk by dir (+1 = older/next, -1 = newer/prev), previewing the target.
    /// Starting a walk (first tap) snapshots the recency order and lands on the previous session.</summary>
    private void MruWalk(int dir)
    {
        if (!_mruWalking && !StartWalk()) return;
        int n = _mruSnapshot.Count;
        _mruIdx = ((_mruIdx + dir) % n + n) % n;
        SetActive(_mruSnapshot[_mruIdx]);                // preview (TouchMru suppressed while walking)
        RequestRedraw();
    }

    /// <summary>Ctrl released: finalize the walk — the highlighted session becomes active + MRU front.</summary>
    private void MruCommit()
    {
        if (!_mruWalking) return;
        _mruWalking = false;
        Ses? target = _mruIdx >= 0 && _mruIdx < _mruSnapshot.Count ? _mruSnapshot[_mruIdx] : _active;
        _mruSnapshot = new(); _mruStart = null;
        if (target is not null) SetActive(target);        // not walking now → fronts it in the stack
        RequestRedraw();
    }

    /// <summary>Esc during a walk: return to the session that was active when the walk began; no MRU change.</summary>
    private void MruCancel()
    {
        if (!_mruWalking) return;
        _mruWalking = false;
        Ses? start = _mruStart;
        _mruSnapshot = new(); _mruStart = null;
        if (start is not null) SetActive(start);          // start was already front, so the stack is unchanged
        RequestRedraw();
    }

    /// <summary>Drive the MRU walk state machine directly (same methods the Ctrl+Tab keys call).
    /// Exists so the control API / tests can exercise begin→advance→commit deterministically with
    /// zero global key injection. Returns the current active session name (or a short status).</summary>
    private string SwitchOp(string op)
    {
        switch (op)
        {
            case "begin": StartWalk(); break;
            case "advance": case "next": MruWalk(1); break;
            case "advance-back": case "back": case "prev": case "previous": MruWalk(-1); break;
            case "commit": MruCommit(); break;
            case "cancel": MruCancel(); break;
            default: return $"unknown op '{op}'";
        }
        return _active?.Name ?? "(none)";
    }

    // ---- Cover terminals (scratch / quick) ----

    /// <summary>The surface that receives input/render focus: a shown cover, else the active pane.</summary>
    private Pane? ActiveSurface() => _cover ?? _active?.ActivePane;

    private void SyncSession() => _session = ActiveSurface()?.S;

    /// <summary>A real filesystem cwd for a session (OSC 7 if reported, else the pane's launch dir).</summary>
    private string? CwdOf(Ses ses)
    {
        string raw = SafeCwd(ses);
        return string.IsNullOrWhiteSpace(raw) ? ses.StartCwd : PrettyCwd(raw);
    }

    private void ShowCover(Pane p, int kind) { _cover = p; _coverKind = kind; SyncSession(); RegridCover(); RequestRedraw(); }

    private void HideCover()
    {
        _cover = null; _coverKind = 0; SyncSession();
        if (_active is not null) RegridSession(_active);
        RequestRedraw();
    }

    /// <summary>The rect (px) the current cover occupies: the full content region, or — for a floating overlay — a centered panel sized by percent.</summary>
    private (float x, float y, float w, float h) CoverRect()
    {
        var (x0, y0, w, h) = ContentArea();
        if (_coverKind == 3 && _ovlOwner is { OverlaySizePercent: > 0 and <= 100 } o)
        {
            float fw = w * o.OverlaySizePercent / 100f, fh = h * o.OverlaySizePercent / 100f;
            return (x0 + (w - fw) / 2f, y0 + (h - fh) / 2f, fw, fh);
        }
        if (_coverKind == 2)   // quick terminal: a centered floating panel over the main window (~85%)
        {
            float fw = w * 0.85f, fh = h * 0.85f;
            return (x0 + (w - fw) / 2f, y0 + (h - fh) / 2f, fw, fh);
        }
        return (x0, y0, w, h);
    }

    /// <summary>Resize the shown cover's PTY to fill its cover rect using its own metrics.</summary>
    private void RegridCover()
    {
        if (_cover is null) return;
        var (_, _, w, h) = CoverRect();
        var (_, cw, ch) = Metrics(_cover.FontSize);
        int cols = Math.Max(1, (int)(w / cw)), rows = Math.Max(1, (int)(h / ch));
        if (_cover.S.Cols != cols || _cover.S.Rows != rows) _cover.S.Resize(cols, rows);
        _cover.ScrollOffset = Math.Clamp(_cover.ScrollOffset, 0, _cover.S.Emulator.HistoryCount);
    }

    /// <summary>Show a session's scratch terminal (creating it lazily in the session's cwd).</summary>
    private void ShowScratch(Ses ses)
    {
        ses.Scratch ??= CreatePane(ses.Id + ":scratch:" + Guid.NewGuid().ToString("N")[..6], ses.Ws, CwdOf(ses), ses.FontSize);
        ShowCover(ses.Scratch, 1);
    }

    private void ShowQuick()
    {
        _quick ??= CreatePane("quick:" + Guid.NewGuid().ToString("N")[..6], ActiveWorkspace(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), (float)_config.FontSize);
        ShowCover(_quick, 2);
    }

    /// <summary>scratch op on|off|toggle for a session's scratch cover.</summary>
    private void ScratchOp(Ses ses, string op)
    {
        bool showing = _coverKind == 1 && ReferenceEquals(_cover, ses.Scratch) && ses.Scratch is not null;
        if (op == "off") { if (showing) HideCover(); return; }
        if (op == "on") { if (!showing) ShowScratch(ses); return; }
        if (showing) HideCover(); else ShowScratch(ses); // toggle
    }

    private void QuickOp(string op)
    {
        bool showing = _coverKind == 2;
        if (op == "off") { if (showing) HideCover(); return; }
        if (op == "on") { if (!showing) ShowQuick(); return; }
        if (showing) HideCover(); else ShowQuick(); // toggle
    }

    // ---- Overlays (Wave B3): an ephemeral program run over a session ----

    /// <summary>Open an overlay on a session: run <paramref name="command"/> in an ephemeral terminal over it.
    /// sizePercent 0 = full content region; 1..100 = a centered floating panel. Returns "id PID".</summary>
    private string OverlayOpen(Ses ses, string command, int sizePercent, bool wait, Dictionary<string, string>? extraEnv = null)
    {
        CloseOverlayOf(ses);                    // one overlay per session; replace any existing one
        _overlayDone.Reset();
        _lastOverlayExit = "no overlay"; _overlayExitCode = 0;
        string id = ses.Id + ":overlay:" + Guid.NewGuid().ToString("N")[..6];
        var pane = CreatePane(id, ses.Ws, CwdOf(ses), ses.FontSize, command, shellWrap: true, extraEnv: extraEnv);
        ses.Overlay = pane;
        ses.OverlaySizePercent = Math.Clamp(sizePercent, 0, 100);
        ses.OverlayWait = wait;
        ses.OverlayExited = false;
        if (ReferenceEquals(ses, _active)) { _cover = pane; _coverKind = 3; _ovlOwner = ses; SyncSession(); RegridCover(); }
        RequestRedraw();
        WatchOverlayExit(ses, pane);
        return id;
    }

    /// <summary>Tear down a session's overlay (hiding its cover if shown, disposing its PTY).</summary>
    private void CloseOverlayOf(Ses ses)
    {
        var pane = ses.Overlay;
        if (pane is null) return;
        if (_coverKind == 3 && ReferenceEquals(_cover, pane)) { _cover = null; _coverKind = 0; _ovlOwner = null; SyncSession(); if (_active is not null) RegridSession(_active); }
        ses.Overlay = null; ses.OverlayExited = false; ses.OverlaySizePercent = 0; ses.OverlayWait = false;
        try { pane.S.Dispose(); } catch { }
        RequestRedraw();
    }

    /// <summary>Close the currently-shown overlay cover (from a keystroke / close verb).</summary>
    private void CloseActiveOverlay() { if (_ovlOwner is not null) CloseOverlayOf(_ovlOwner); }

    /// <summary>When an overlay's program exits, record its code and either close the overlay or (with --wait) mark it exited.</summary>
    private void WatchOverlayExit(Ses ses, Pane pane)
    {
        void OnExit(int code)
        {
            _overlayExitCode = code;
            _lastOverlayExit = $"exit {code}";
            _overlayDone.Set();
            Post(() =>
            {
                if (!ReferenceEquals(ses.Overlay, pane)) return;   // already replaced/closed
                if (ses.OverlayWait) { ses.OverlayExited = true; RequestRedraw(); }
                else CloseOverlayOf(ses);
            });
        }
        pane.S.Exited += OnExit;
        if (pane.S.HasExited) OnExit(pane.S.ExitCode ?? 0);        // already gone before we subscribed
    }

    /// <summary>Change one pane's font zoom (delta 0 = reset to the config default). Caller reflows.</summary>
    private void ChangeFontSizeOfPane(Pane p, int delta)
        => p.FontSize = delta == 0 ? (float)_config.FontSize : Math.Clamp(p.FontSize + delta, 6f, 48f);

    /// <summary>Change the active pane's font zoom (delta 0 = reset to config default), reflow + repaint.</summary>
    private void ChangeFontSizeOf(Ses ses, int delta)
    {
        float cur = ses.FontSize;
        float ns = delta == 0 ? (float)_config.FontSize : Math.Clamp(cur + delta, 6f, 48f);
        if (ns == cur) return;
        ses.FontSize = ns;               // active pane
        RegridSession(ses);
        if (ReferenceEquals(_active, ses)) RequestRedraw();
        SaveState();
    }

    private void ChangeFontSize(int delta)
    {
        if (_cover is not null) { ChangeFontSizeOfPane(_cover, delta); RegridCover(); RequestRedraw(); }
        else if (_active is not null) ChangeFontSizeOf(_active, delta);
    }

    // ---- Splits ----

    private void SplitActivePane()
    {
        var ses = _active;
        if (ses is null) return;
        if (ses.Panes.Count >= 2) return;   // agterm model: strictly primary + one split (no 3+ panes)
        var cur = ses.ActivePane;
        string? cwd = string.IsNullOrEmpty(cur.StartCwd) ? null : cur.StartCwd;
        var np = CreatePane(Guid.NewGuid().ToString(), ses.Ws, cwd, cur.FontSize, profileName: ses.ProfileName);
        float half = cur.Ratio / 2f;
        cur.Ratio = half; np.Ratio = half;
        int idx = ses.Panes.IndexOf(cur);
        ses.Panes.Insert(idx + 1, np);
        ses.Active = idx + 1;            // focus the new pane
        _session = ses.S;
        RegridSession(ses);
        RequestRedraw();
        SaveState();
    }

    private void FocusPane(int dir)
    {
        var ses = _active;
        if (ses is null || ses.Panes.Count < 2) return;
        ses.Active = Math.Clamp(ses.Active + dir, 0, ses.Panes.Count - 1);
        _session = ses.S;
        RequestRedraw();
    }

    /// <summary>Confirm a user-initiated session close (Yes/No), unless confirm-close-session is off.</summary>
    private bool ConfirmCloseOk()
        => !_config.ConfirmCloseSession
           || MessageBoxW(_hwnd, "Close this session and end what's running in it?", "Close session",
                          MB_YESNO | MB_ICONQUESTION) == IDYES;

    /// <summary>Close the focused pane; if it's the last pane, close the whole session.</summary>
    private void CloseActivePane()
    {
        var ses = _active;
        if (ses is null) return;
        if (ses.Panes.Count <= 1) { if (ConfirmCloseOk()) CloseSessionInternal(ses); return; }
        var cur = ses.ActivePane;
        int idx = ses.Panes.IndexOf(cur);
        try { cur.S.Dispose(); } catch { }
        ses.Panes.RemoveAt(idx);
        float freed = cur.Ratio / ses.Panes.Count;
        foreach (var p in ses.Panes) p.Ratio += freed;   // redistribute the freed width
        ses.Active = Math.Clamp(idx, 0, ses.Panes.Count - 1);
        _session = ses.S;
        RegridSession(ses);
        RequestRedraw();
        SaveState();
    }

    private void CloseSessionInternal(Ses ses)
    {
        foreach (var p in ses.Panes) { try { p.S.Dispose(); } catch { } }
        // Dismiss + dispose this session's scratch cover if it belongs here.
        if (ses.Scratch is not null) { if (_coverKind == 1 && ReferenceEquals(_cover, ses.Scratch)) HideCover(); try { ses.Scratch.S.Dispose(); } catch { } ses.Scratch = null; }
        if (ses.Overlay is not null) CloseOverlayOf(ses); // dismiss + dispose this session's overlay
        bool wasActive = ReferenceEquals(_active, ses);
        _mru.Remove(ses.Id);
        EvictWatermark(ses.BgPath); SweepBackground(ses.Id); // drop the session's watermark file + texture
        lock (_workspaces) ses.Ws.Sessions.Remove(ses);
        if (wasActive)
        {
            Ses? next = AllSessions().FirstOrDefault();
            if (next is not null) SetActive(next);
            else CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), makeActive: true);
        }
        RequestRedraw();
        SaveState();
    }

    // ---- Wave F2: session background watermark storage/lifecycle (UI thread) ----

    /// <summary>Copy the chosen image into AppDir\backgrounds\&lt;sessionId&gt;&lt;ext&gt; and set the session's spec.</summary>
    private string SetBackground(Ses ses, string source, int opacity, string? mode)
    {
        if (!File.Exists(source)) return "file not found: " + source;
        string ext = Path.GetExtension(source);
        if (string.IsNullOrEmpty(ext)) ext = ".img";
        string dst = Path.Combine(BackgroundsDir, ses.Id + ext);
        try
        {
            Directory.CreateDirectory(BackgroundsDir);
            SweepBackground(ses.Id);          // remove any prior copy (possibly a different extension)
            EvictWatermark(ses.BgPath);
            File.Copy(source, dst, overwrite: true);
        }
        catch (Exception ex) { return "copy failed: " + ex.Message; }

        ses.BgPath = dst;
        if (opacity >= 0) ses.BgOpacity = System.Math.Clamp(opacity, 0, 100);
        if (!string.IsNullOrWhiteSpace(mode))
        {
            string m = mode!.Trim().ToLowerInvariant();
            if (m is "fit" or "fill" or "center" or "tile") ses.BgMode = m;
        }
        EvictWatermark(dst);                   // force a fresh decode of the new bytes
        SaveState();
        RequestRedraw();
        return $"background set ({ses.BgMode}, {ses.BgOpacity}%)";
    }

    private void ClearBackground(Ses ses)
    {
        EvictWatermark(ses.BgPath);
        SweepBackground(ses.Id);
        ses.BgPath = null;
        SaveState();
        RequestRedraw();
    }

    /// <summary>Delete any copied background file(s) for a session id (best-effort).</summary>
    private static void SweepBackground(string sessionId)
    {
        try
        {
            if (!Directory.Exists(BackgroundsDir)) return;
            foreach (var f in Directory.EnumerateFiles(BackgroundsDir, sessionId + ".*"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    /// <summary>Sweep watermark files for every session in a (possibly closed) window's snapshot.</summary>
    private static void SweepWindowBackgrounds(string windowId)
    {
        try
        {
            string p = Path.Combine(AppDir, "windows", windowId + ".json");
            if (!File.Exists(p)) return;
            var st = JsonSerializer.Deserialize<AppState>(File.ReadAllText(p));
            if (st is null) return;
            foreach (var ws in st.Workspaces)
                foreach (var s in ws.Sessions)
                    if (!string.IsNullOrEmpty(s.Id)) SweepBackground(s.Id);
        }
        catch { }
    }

    private void CycleSession(int dir)
    {
        var all = AllSessions();
        if (all.Count < 2) return;
        int i = _active is null ? 0 : all.IndexOf(_active);
        SetActive(all[((i + dir) % all.Count + all.Count) % all.Count]);
    }

    // ---- Wave A1 internal helpers (called on the UI thread via Host/Post) ----

    private void SessionGoInternal(string dir)
    {
        switch (dir)
        {
            case "next": CycleSession(1); break;
            case "prev": case "previous": CycleSession(-1); break;
            case "first": { var f = AllSessions().FirstOrDefault(); if (f is not null) SetActive(f); break; }
            case "last": { var l = AllSessions().LastOrDefault(); if (l is not null) SetActive(l); break; }
            case "next-attention": GoToNextAttention(1); break;
            case "prev-attention": case "previous-attention": GoToNextAttention(-1); break;
        }
    }

    /// <summary>Move an item within its list: dir = up|down|top|bottom (caller holds any needed lock).</summary>
    private static void ReorderInList<T>(List<T> list, T item, string dir)
    {
        int i = list.IndexOf(item);
        if (i < 0) return;
        list.RemoveAt(i);
        int j = dir switch { "up" => i - 1, "down" => i + 1, "top" => 0, "bottom" => list.Count, _ => i };
        list.Insert(Math.Clamp(j, 0, list.Count), item);
    }

    private Workspace? FindWs(string? target)
    {
        lock (_workspaces)
        {
            if (string.IsNullOrEmpty(target) || target == "active") return _active?.Ws ?? _workspaces.FirstOrDefault();
            return _workspaces.FirstOrDefault(w => w.Id == target) ?? _workspaces.FirstOrDefault(w => w.Id.StartsWith(target));
        }
    }

    private void SplitOp(string op)
    {
        int panes = _active?.Panes.Count ?? 1;
        switch (op)
        {
            case "on": if (panes <= 1) SplitActivePane(); break;
            case "off": if (panes > 1) CollapseToSinglePane(); break;
            default: if (panes > 1) CollapseToSinglePane(); else SplitActivePane(); break; // toggle
        }
    }

    /// <summary>Collapse the active session to just its focused pane (dispose the rest).</summary>
    private void CollapseToSinglePane()
    {
        var ses = _active;
        if (ses is null || ses.Panes.Count <= 1) return;
        var keep = ses.Panes[0];   // agterm: collapsing the split keeps the primary (left) pane, not whichever is focused
        foreach (var p in ses.Panes) if (!ReferenceEquals(p, keep)) { try { p.S.Dispose(); } catch { } }
        ses.Panes.Clear(); ses.Panes.Add(keep); keep.Ratio = 1f; ses.Active = 0;
        _session = ses.S;
        RegridSession(ses); RequestRedraw(); SaveState();
    }

    /// <summary>Set the split boundary next to the active pane: an absolute left ratio, or grow by columns.</summary>
    private void ResizeActiveSplitInternal(double? ratio, int growLeft, int growRight)
    {
        var ses = _active;
        if (ses is null || ses.Panes.Count < 2) return;
        int i = Math.Min(ses.Active, ses.Panes.Count - 2); // boundary between pane i and i+1
        var a = ses.Panes[i]; var b = ses.Panes[i + 1];
        float total = a.Ratio + b.Ratio;
        if (ratio is double r)
        {
            float ra = (float)Math.Clamp(r, 0.05, 0.95) * total;
            a.Ratio = ra; b.Ratio = total - ra;
        }
        else
        {
            var (_, _, totalW, _) = ContentArea();
            var (_, cw, _) = Metrics(a.FontSize);
            float shift = ((growRight - growLeft) * cw / MathF.Max(1f, totalW)) * total;
            float ra = Math.Clamp(a.Ratio + shift, 0.05f * total, 0.95f * total);
            a.Ratio = ra; b.Ratio = total - ra;
        }
        RegridSession(ses); RequestRedraw(); SaveState();
    }

    private void SidebarOpInternal(string op)
    {
        switch (op)
        {
            case "show": if (_sidebarW <= 0) ToggleSidebar(); break;
            case "hide": if (_sidebarW > 0) ToggleSidebar(); break;
            case "toggle": ToggleSidebar(); break;
            case "expand": lock (_workspaces) foreach (var w in _workspaces) w.Expanded = true; RequestRedraw(); SaveState(); break;
            case "collapse": lock (_workspaces) foreach (var w in _workspaces) w.Expanded = false; RequestRedraw(); SaveState(); break;
            case "mode:tree": SetSidebarMode(SidebarMode.Tree); break;
            case "mode:flagged": SetSidebarMode(SidebarMode.Flagged); break;
            case "mode:toggle": ToggleFlaggedView(); break;
        }
    }

    /// <summary>Set the sidebar view mode (tree/flagged) and repaint + persist.</summary>
    private void SetSidebarMode(SidebarMode mode)
    {
        if (_sidebarMode == mode) return;
        _sidebarMode = mode;
        RequestRedraw(); SaveState();
    }

    /// <summary>Toggle between the tree outline and the flat flagged working-set view.</summary>
    private void ToggleFlaggedView()
        => SetSidebarMode(_sidebarMode == SidebarMode.Flagged ? SidebarMode.Tree : SidebarMode.Flagged);

    /// <summary>Flag operation on a session (or all): op = on|off|toggle|clear (clear unflags every session).</summary>
    private void FlagOp(Ses? s, string op)
    {
        switch (op)
        {
            case "clear": foreach (var x in AllSessions()) x.Flagged = false; break;
            case "on": if (s is not null) s.Flagged = true; break;
            case "off": if (s is not null) s.Flagged = false; break;
            default: if (s is not null) s.Flagged = !s.Flagged; break; // toggle
        }
        RequestRedraw(); SaveState();
    }

    /// <summary>Focus a single workspace (the active one) or clear focus: op = on|off|toggle. Tree mode only.</summary>
    private void WorkspaceFocusOp(string op) => WorkspaceFocusOp(op, ActiveWorkspace().Id);

    private void WorkspaceFocusOp(string op, string? wsId)
    {
        _focusedWorkspaceId = op switch
        {
            "on" => wsId,
            "off" => null,
            _ => _focusedWorkspaceId is not null ? null : wsId, // toggle
        };
        RequestRedraw(); SaveState();
    }

    private Ses? Find(string? target)
    {
        lock (_workspaces)
        {
            if (string.IsNullOrEmpty(target) || target == "active") return _active;
            var all = _workspaces.SelectMany(w => w.Sessions).ToList();
            return all.FirstOrDefault(x => x.Id == target) ?? all.FirstOrDefault(x => x.Id.StartsWith(target));
        }
    }

    /// <summary>Run an action on the UI thread (pipe callbacks arrive on a background thread).</summary>
    private void Post(Action a)
    {
        _uiActions.Enqueue(a);
        PostMessageW(_hwnd, WM_APP_ACTION, IntPtr.Zero, IntPtr.Zero);
    }

    // Synchronous UI-thread invoke (for control verbs that mutate UI state AND return a value,
    // e.g. session.search). SendMessageW blocks the caller until the UI thread runs the func.
    private Func<string>? _syncFn;
    private string _syncResult = "";
    private readonly object _syncLock = new();
    private string InvokeOnUi(Func<string> fn)
    {
        lock (_syncLock)
        {
            _syncFn = fn;
            SendMessageW(_hwnd, WM_APP_SYNC, IntPtr.Zero, IntPtr.Zero);
            _syncFn = null;
            return _syncResult;
        }
    }

    // ---- IWindowHost bridge (Wave F1b): app-level window management for the control API. Content
    // verbs resolve through ResolveWindow(--window); window.* verbs act on the library. These use
    // static library state + Frontmost, so they work regardless of which instance the server holds. ----
    public ISessionHost? ResolveWindow(string? selector)
    {
        if (string.IsNullOrEmpty(selector) || selector == "active") return Frontmost;
        return ResolveOpen(selector);
    }

    public IReadOnlyList<WindowSnapshot> Windows()
    {
        lock (_windowIndex)
            return _windowIndex.Select(m => new WindowSnapshot(m.Id, m.Name, m.IsOpen, m.Id == _frontmostId)).ToList();
    }

    public string WindowNew(string? name)
    {
        string id = Guid.NewGuid().ToString();
        Frontmost.Post(() =>
        {
            var m = new WinMeta { Id = id, Name = name ?? "", IsOpen = true };
            CascadeGeometry(m);
            lock (_windowIndex) _windowIndex.Add(m);
            var win = CreateWindowInstance(m);
            // The new window becomes frontmost; set both the static instance and the id together so the
            // library's "active" flag and un-scoped (--window active) content verbs stay consistent even
            // if the OS doesn't deliver WM_ACTIVATE (e.g. created while another process holds foreground).
            Frontmost = win; _frontmostId = id;
            SetForegroundWindow(win._hwnd);
            SaveIndex();
        });
        return id;
    }

    public bool WindowSelect(string? selector)
    {
        var p = ResolveOpen(selector);
        if (p is null) return false;
        Frontmost.Post(() => { if (IsIconic(p._hwnd)) ShowWindow(p._hwnd, SW_RESTORE); SetForegroundWindow(p._hwnd); });
        return true;
    }

    public bool WindowClose(string? selector)
    {
        var p = ResolveOpen(selector);
        if (p is null) return false;
        Frontmost.Post(() => DestroyWindow(p._hwnd)); // WM_DESTROY does teardown + index bookkeeping
        return true;
    }

    public bool WindowDelete(string? selector)
    {
        var target = ResolveMeta(selector);
        if (target is null) return false;
        lock (_windowIndex) if (_windowIndex.Count <= 1) return false; // never delete the last window
        Frontmost.Post(() =>
        {
            Program? open; lock (_windowIndex) _byId.TryGetValue(target.Id, out open);
            if (open is not null) DestroyWindow(open._hwnd);
            lock (_windowIndex)
            {
                _windowIndex.RemoveAll(m => m.Id == target.Id);
                if (_frontmostId == target.Id) _frontmostId = _windowIndex.FirstOrDefault(m => m.IsOpen)?.Id ?? _windowIndex.FirstOrDefault()?.Id;
            }
            SweepWindowBackgrounds(target.Id); // remove watermark files for that window's sessions
            try { File.Delete(Path.Combine(AppDir, "windows", target.Id + ".json")); } catch { }
            SaveIndex();
        });
        return true;
    }

    public bool WindowRename(string? selector, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var target = ResolveMeta(selector);
        if (target is null) return false;
        Frontmost.Post(() =>
        {
            lock (_windowIndex) target.Name = name;
            if (_byId.TryGetValue(target.Id, out var p)) { p.WinName = name; p.RequestRedraw(); }
            SaveIndex();
        });
        return true;
    }

    public bool WindowResize(string? selector, int w, int h)
    {
        var p = ResolveOpen(selector);
        if (p is null || w <= 0 || h <= 0) return false;
        Frontmost.Post(() => { SetWindowPos(p._hwnd, IntPtr.Zero, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE); p.SaveState(); });
        return true;
    }

    public bool WindowMove(string? selector, int x, int y)
    {
        var p = ResolveOpen(selector);
        if (p is null) return false;
        Frontmost.Post(() => { SetWindowPos(p._hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); p.SaveState(); });
        return true;
    }

    public bool WindowZoom(string? selector)
    {
        var p = ResolveOpen(selector);
        if (p is null) return false;
        Frontmost.Post(() => { ShowWindow(p._hwnd, IsZoomed(p._hwnd) ? SW_RESTORE : SW_MAXIMIZE); p.SaveState(); });
        return true;
    }

    private static Program? ResolveOpen(string? selector)
    {
        lock (_windowIndex)
        {
            if (string.IsNullOrEmpty(selector) || selector == "active") return Frontmost;
            if (_byId.TryGetValue(selector, out var exact)) return exact;
            foreach (var kv in _byId) if (kv.Key.StartsWith(selector)) return kv.Value;
        }
        return null;
    }

    private static WinMeta? ResolveMeta(string? selector)
    {
        lock (_windowIndex)
        {
            if (string.IsNullOrEmpty(selector) || selector == "active")
                return _windowIndex.FirstOrDefault(m => m.Id == _frontmostId) ?? _windowIndex.FirstOrDefault();
            return _windowIndex.FirstOrDefault(m => m.Id == selector) ?? _windowIndex.FirstOrDefault(m => m.Id.StartsWith(selector));
        }
    }

    private static void CascadeGeometry(WinMeta m)
    {
        try
        {
            if (Frontmost is not null && GetWindowRect(Frontmost._hwnd, out RECT r))
            { m.X = r.left + 32; m.Y = r.top + 32; m.W = Math.Max(400, r.right - r.left); m.H = Math.Max(300, r.bottom - r.top); }
        }
        catch { }
    }

    // ---- ISessionHost bridge (Program is the host) so the control server / agwintermctl drive
    // this window. Un-scoped verbs act on this instance; the seam for future --window targeting. ----
        public TerminalSession? Resolve(string? target)
        {
            if (string.IsNullOrEmpty(target) || target == "active") return ActiveSurface()?.S;
            lock (_workspaces)
            {
                var panes = _workspaces.SelectMany(w => w.Sessions).SelectMany(s => s.Panes).ToList();
                var p = panes.FirstOrDefault(x => x.Id == target) ?? panes.FirstOrDefault(x => x.Id.StartsWith(target));
                if (p is not null) return p.S;   // target any pane by id (e.g. docxy's AGWINTERM_SESSION_ID)
            }
            return Find(target)?.S;
        }

        public IReadOnlyList<WorkspaceSnapshot> Tree()
        {
            lock (_workspaces)
                return _workspaces.Select(w => new WorkspaceSnapshot(
                    w.Id, w.Name, _active is not null && ReferenceEquals(_active.Ws, w),
                    w.Sessions.Select(s => new SessionSnapshot(s.Id, s.Name, ReferenceEquals(s, _active), s.S.Status, s.Overlay is not null, UnreadOf(s), s.Flagged, s.BgPath is not null)).ToList()
                )).ToList();
        }

        public string NewSession(string? name, string? cwd, string? workspace, string? command = null,
            string? workspaceName = null, bool createWorkspace = false, string? profile = null)
        {
            string id = Guid.NewGuid().ToString();
            Post(() =>
            {
                Workspace ws;
                if (!string.IsNullOrEmpty(workspace))
                    lock (_workspaces)
                        ws = _workspaces.FirstOrDefault(w => w.Id == workspace)
                             ?? _workspaces.FirstOrDefault(w => w.Id.StartsWith(workspace)) ?? ActiveWorkspace();
                else if (!string.IsNullOrEmpty(workspaceName))
                {
                    Workspace? byName;
                    lock (_workspaces) byName = _workspaces.FirstOrDefault(w => string.Equals(w.Name, workspaceName, StringComparison.OrdinalIgnoreCase));
                    ws = byName ?? (createWorkspace ? CreateWorkspace(Guid.NewGuid().ToString(), workspaceName) : ActiveWorkspace());
                }
                else ws = ActiveWorkspace();
                CreateSession(id, name, cwd, ws, makeActive: true, command: command, profileName: profile);
            });
            return id;
        }

        public bool SelectSession(string target)
        {
            var ses = Find(target);
            if (ses is null) return false;
            Post(() => SetActive(ses));
            return true;
        }

        public bool CloseSession(string target)
        {
            var ses = Find(target);
            if (ses is null) return false;
            Post(() => CloseSessionInternal(ses));
            return true;
        }

        public string NewWorkspace(string? name)
        {
            string id = Guid.NewGuid().ToString();
            Post(() => CreateWorkspace(id, name));
            return id;
        }

        public bool SetFontSize(string? target, string op)
        {
            var ses = Find(target);
            if (ses is null) return false;
            int delta = op switch { "inc" => 1, "dec" => -1, _ => 0 }; // reset otherwise
            Post(() => ChangeFontSizeOf(ses, delta));
            return true;
        }

        // ---- Wave A1 verbs ----
        public void SessionGo(string dir) => Post(() => SessionGoInternal(dir));

        public bool SessionReorder(string? target, string dir)
        {
            var s = Find(target);
            if (s is null) return false;
            Post(() => { lock (_workspaces) ReorderInList(s.Ws.Sessions, s, dir); RequestRedraw(); SaveState(); });
            return true;
        }

        public bool SessionToWorkspace(string? target, string workspace)
        {
            var s = Find(target); var ws = FindWs(workspace);
            if (s is null || ws is null) return false;
            Post(() => MoveSession(s, ws));
            return true;
        }

        public bool WorkspaceRename(string? target, string name)
        {
            var ws = FindWs(target);
            if (ws is null || string.IsNullOrWhiteSpace(name)) return false;
            Post(() => { ws.Name = name; RequestRedraw(); SaveState(); });
            return true;
        }

        public bool WorkspaceDelete(string? target)
        {
            var ws = FindWs(target);
            if (ws is null) return false;
            Post(() => DeleteWorkspace(ws));
            return true;
        }

        public bool WorkspaceSelect(string? target)
        {
            var ws = FindWs(target);
            if (ws is null) return false;
            Post(() => { var s = ws.Sessions.FirstOrDefault(); if (s is not null) SetActive(s); });
            return true;
        }

        public bool WorkspaceReorder(string? target, string dir)
        {
            var ws = FindWs(target);
            if (ws is null) return false;
            Post(() => { lock (_workspaces) ReorderInList(_workspaces, ws, dir); RequestRedraw(); SaveState(); });
            return true;
        }

        public void Split(string op) => Post(() => SplitOp(op));

        public void FocusPaneDir(string dir)
        {
            int delta = dir switch
            {
                "left" => -1,
                "right" => 1,
                _ => (_active is not null && _active.Active == 0) ? 1 : -1, // "other"
            };
            Post(() => FocusPane(delta));
        }

        public void ResizeSplit(double? ratio, int growLeft, int growRight)
            => Post(() => ResizeActiveSplitInternal(ratio, growLeft, growRight));

        public IReadOnlyList<string> ThemeList() => _allThemes.Select(t => t.Name).ToList();

        public bool ThemeSet(string name)
        {
            if (!_allThemes.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
            Post(() => CommitTheme(FindTheme(name)));
            return true;
        }

        public string KeymapReload() { Post(ReloadKeymap); return "keymap reload requested"; }

        public string ConfigSet(string key, string value) => InvokeOnUi(() => ConfigSetInternal(key, value));
        public string ConfigGet(string key) => InvokeOnUi(() => ConfigValue(key.Trim().ToLowerInvariant()));
        public string ConfigList() => InvokeOnUi(() => string.Join("\n", ConfigKeys.Select(k => $"{k} = {ConfigValue(k)}")));
        public string SettingsOpen() { Post(OpenSettingsWindow); return "settings opened"; }

        public string ProfilesList() => InvokeOnUi(() => string.Join("\n", _profileCfg.Profiles.Select(p =>
            $"{(p.Name.Equals(_profileCfg.Default, StringComparison.OrdinalIgnoreCase) ? "*" : " ")} {p.Name}\t{p.Command}{(p.Args is { Length: > 0 } a ? " " + string.Join(" ", a) : "")}")));
        public string ProfilesReload() => InvokeOnUi(() => { _profileCfg = Agwinterm.Pty.ShellProfiles.Load(AppDir); RequestRedraw(); return $"{_profileCfg.Profiles.Count} profiles loaded"; });

        public string RestoreClear()
        {
            try { if (File.Exists(StatePath)) { File.Delete(StatePath); return "restore state cleared"; } return "no restore state"; }
            catch (Exception ex) { return "error: " + ex.Message; }
        }

        public void SidebarOp(string op) => Post(() => SidebarOpInternal(op));

        public string SessionCopy(string? target)
        {
            var s = Resolve(target);
            if (s is null) return "";
            Pane? pane;
            lock (_workspaces)
                pane = _workspaces.SelectMany(w => w.Sessions).SelectMany(x => x.Panes)
                                  .FirstOrDefault(p => ReferenceEquals(p.S, s));
            return pane is not null ? SelectionText(pane) : ""; // reads under the session lock; safe off-UI-thread
        }

        // Selection/clipboard control API. Clipboard + selection are UI-thread concepts, so hop on-thread.
        public string SelectionAll(string? target) => InvokeOnUi(() =>
        {
            var p = PaneForTarget(target); if (p is null) return "no session";
            SelectAll(p); return p.HasSel ? "selected all" : "empty";
        });

        public string SelectionCopy(string? target) => InvokeOnUi(() =>
        {
            var p = PaneForTarget(target); if (p is null) return "no session";
            if (!p.HasSel) return "no selection";
            string t = SelectionText(p); CopySelection(p); return $"copied {t.Length} chars";
        });

        public string SelectionClear(string? target) => InvokeOnUi(() =>
        {
            var p = PaneForTarget(target); if (p is null) return "no session";
            p.ClearSel(); RequestRedraw(); return "cleared";
        });

        // Test/observability hook: run the same finalize path a mouse-up runs (honors copy-on-select).
        public string SelectionFinalize(string? target) => InvokeOnUi(() =>
        {
            var p = PaneForTarget(target); if (p is null) return "no session";
            FinalizeSelection(p);
            return _config.CopyOnSelect ? (p.HasSel ? "finalized (copied)" : "finalized (empty)") : "finalized (copy-on-select off)";
        });

        public string SessionPaste(string? target, string? text) => InvokeOnUi(() =>
        {
            var p = PaneForTarget(target); if (p is null) return "no session";
            PasteTextInto(p, text ?? ClipboardGet());
            return "pasted";
        });

        // Search operates on the active pane's find bar (a UI-thread concept); run it synchronously
        // on the UI thread and return the "N of M" status. (target is accepted for API shape; v1
        // searches the active session.)
        public string SessionSearch(string? target, string? query, string? action) => InvokeOnUi(() =>
        {
            if (_active is null) return "no session";
            if (action == "close") { CloseSearch(); return "closed"; }
            if (!_searchActive) _searchActive = true;
            if (!string.IsNullOrEmpty(query)) { _searchQuery = query!; RecomputeSearch(); _searchCur = 0; ScrollToMatch(); }
            else if (action == "next") SearchStep(1);
            else if (action == "prev") SearchStep(-1);
            else { RecomputeSearch(); ScrollToMatch(); }
            RequestRedraw();
            return SearchStatus();
        });

        public bool SessionScratch(string? target, string op)
        {
            var ses = FindSesForTarget(target);
            if (ses is null) return false;
            Post(() => ScratchOp(ses, op));
            return true;
        }

        public void Quick(string op) => Post(() => QuickOp(op));

        public string SessionOverlay(string? target, string action, string? command, int sizePercent, bool wait, bool block)
        {
            switch (action)
            {
                case "result":
                    return _lastOverlayExit;
                case "close":
                {
                    var ses = FindSesForTarget(target);
                    if (ses?.Overlay is null) return "no overlay";
                    Post(() => CloseOverlayOf(ses));
                    return "closed";
                }
                default: // "open"
                {
                    if (string.IsNullOrWhiteSpace(command)) return "no command";
                    if (block)
                    {
                        // Open on the UI thread, then wait (on this pipe thread) for the program to exit.
                        InvokeOnUi(() => { var s = FindSesForTarget(target); return s is null ? "no session" : OverlayOpen(s, command!, sizePercent, false); });
                        _overlayDone.Wait();
                        return _lastOverlayExit;   // "exit N"
                    }
                    return InvokeOnUi(() => { var s = FindSesForTarget(target); return s is null ? "no session" : OverlayOpen(s, command!, sizePercent, wait); });
                }
            }
        }

        public bool Notify(string? target, string? title, string body)
        {
            var ses = FindSesForTarget(target);
            if (ses is null) return false;
            Post(() => OnNotified(ses.ActivePane, title ?? "", body));
            return true;
        }

        public bool SessionFlag(string? target, string op)
        {
            if (op == "clear") { Post(() => FlagOp(null, "clear")); return true; } // clear is global; no target needed
            var ses = FindSesForTarget(target);
            if (ses is null) return false;
            Post(() => FlagOp(ses, op));
            return true;
        }

        public string SessionBackground(string? target, string action, string? path, int opacity, string? mode) => InvokeOnUi(() =>
        {
            var ses = FindSesForTarget(target);
            if (ses is null) return "no session";
            if (action == "clear") { ClearBackground(ses); return "cleared"; }
            if (string.IsNullOrWhiteSpace(path)) return "background set needs a path";
            return SetBackground(ses, path!, opacity, mode);
        });

        public void WorkspaceFocus(string op) => Post(() => WorkspaceFocusOp(op));

        public string SessionSwitch(string op) => InvokeOnUi(() => SwitchOp(op));

        public string CommandRun(string nameOrCommand, string? mode) => InvokeOnUi(() =>
        {
            var cmd = _commands.FirstOrDefault(c => string.Equals(c.Label, nameOrCommand, StringComparison.OrdinalIgnoreCase));
            string text = cmd?.Text ?? nameOrCommand;
            // A configured command uses its mode unless overridden; a raw command defaults to a new session.
            string useMode = mode ?? cmd?.Mode ?? "new";
            string expanded = RunCommandText(text, useMode);
            return $"{useMode}: {expanded}";
        });

        public string CommandList() => InvokeOnUi(CommandListText);

        public string CommandLeader(string op) => InvokeOnUi(() => LeaderOp(op));

    /// <summary>Resolve a control-API target ("active"/null/id/prefix) to its owning session.</summary>
    /// <summary>Resolve a control-API target to a specific pane: the active surface for "active"/empty,
    /// else a pane by (prefix) id, else the target session's active pane.</summary>
    private Pane? PaneForTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target == "active") return ActiveSurface();
        lock (_workspaces)
        {
            var panes = _workspaces.SelectMany(w => w.Sessions).SelectMany(s => s.Panes).ToList();
            var p = panes.FirstOrDefault(x => x.Id == target) ?? panes.FirstOrDefault(x => x.Id.StartsWith(target));
            if (p is not null) return p;
        }
        return FindSesForTarget(target)?.ActivePane;
    }

    private Ses? FindSesForTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target == "active") return _active;
        lock (_workspaces)
        {
            var all = _workspaces.SelectMany(w => w.Sessions).ToList();
            foreach (var s in all)
                if (s.Id == target || s.Panes.Any(p => p.Id == target)) return s;
            foreach (var s in all)
                if (s.Id.StartsWith(target) || s.Panes.Any(p => p.Id.StartsWith(target))) return s;
        }
        return null;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Resolve the owning instance by HWND; during CreateWindowExW it isn't registered yet, so
        // fall back to the instance currently booting (and register it on the first message).
        if (!_registry.TryGetValue(hwnd, out Program? inst))
        {
            inst = _creating;
            if (inst is not null) { inst._hwnd = hwnd; _registry[hwnd] = inst; }
        }
        if (inst is null) return DefWindowProcW(hwnd, msg, wParam, lParam);
        try
        {
            IntPtr r = inst.WindowProcCore(hwnd, msg, wParam, lParam);
            if (msg == 0x0082 /* WM_NCDESTROY */) _registry.Remove(hwnd);
            return r;
        }
        catch (Exception ex) { Perf($"wndproc ex msg=0x{msg:X}: {ex.GetType().Name} {ex.Message}"); return DefWindowProcW(hwnd, msg, wParam, lParam); }
    }

    private IntPtr WindowProcCore(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE:
                if (wParam != IntPtr.Zero) { AdjustClientRect(hwnd, lParam); return IntPtr.Zero; }
                break;

            case WM_NCHITTEST:
                return (IntPtr)HitTest(hwnd, LoWord(lParam), HiWord(lParam));

            case WM_NCMOUSELEAVE:
                if (_hoverCaption != 0) { _hoverCaption = 0; RequestRedraw(); }
                break;

            case WM_MOUSELEAVE:
                _mouseTracking = false;
                if (_hotBtn is not null) { _hotBtn = null; SetTimer(hwnd, (IntPtr)HoverTimer, 15, IntPtr.Zero); RequestRedraw(); }
                return IntPtr.Zero;

            case WM_NCLBUTTONDOWN:
                {
                    int ht = (int)wParam;
                    // Consume caption-button presses so DefWindowProc doesn't start its own
                    // (unreliable) loop; we perform min/max/close ourselves on button-up.
                    if (ht == HTMINBUTTON || ht == HTMAXBUTTON || ht == HTCLOSE) { _capPressed = ht; RequestRedraw(); return IntPtr.Zero; }
                    break; // HTCAPTION drag / HTTOP.. resize -> DefWindowProc
                }
            case WM_NCLBUTTONUP:
                {
                    int ht = (int)wParam;
                    if (_capPressed != 0)
                    {
                        int pressed = _capPressed; _capPressed = 0; RequestRedraw();
                        if (ht == pressed)
                        {
                            if (pressed == HTMINBUTTON) ShowWindow(hwnd, SW_MINIMIZE);
                            else if (pressed == HTMAXBUTTON) ShowWindow(hwnd, IsZoomed(hwnd) ? SW_RESTORE : SW_MAXIMIZE);
                            else if (pressed == HTCLOSE) PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        }
                        return IntPtr.Zero;
                    }
                    break;
                }

            case WM_COMMAND:
                if (LoWord(wParam) == EDIT_ID && HiWord(wParam) == EN_KILLFOCUS) CommitRename();
                return IntPtr.Zero;

            case WM_CTLCOLOREDIT: // highlight-matching background + white text (wParam = HDC)
                SetTextColor(wParam, RGB(255, 255, 255));
                SetBkColor(wParam, RGB(41, 51, 64));
                return _editBrush;

            case WM_CONTEXTMENU:
                {
                    int sx = LoWord(lParam), sy = HiWord(lParam);
                    var pt = new POINT { x = sx, y = sy };
                    if (sx == -1 && sy == -1) { GetCursorScreen(out sx, out sy); pt.x = sx; pt.y = sy; } // keyboard menu key
                    ScreenToClient(hwnd, ref pt);
                    if (pt.x < (int)_sidebarW && pt.y >= (int)TitleBarH)
                    {
                        var item = RowAt(pt.y);
                        if (item is not null) ShowContextMenu(item, sx, sy);
                        return IntPtr.Zero;
                    }
                    break;
                }

            case WM_PAINT:
                BeginPaint(hwnd, out PAINTSTRUCT ps);
                Render();
                EndPaint(hwnd, ref ps);
                return IntPtr.Zero;

            case WM_APP_REDRAW:
                System.Threading.Interlocked.Exchange(ref _redrawPending, 0);
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;

            case WM_APP_ACTION:
                while (_uiActions.TryDequeue(out var act))
                    try { act(); } catch (Exception ex) { Perf($"uiaction ex: {ex.Message}"); }
                return IntPtr.Zero;

            case WM_APP_SYNC:
                try { _syncResult = _syncFn?.Invoke() ?? ""; } catch (Exception ex) { _syncResult = ""; Perf($"sync ex: {ex.Message}"); }
                return IntPtr.Zero;

            case WM_TIMER:
                if ((int)wParam == 2) { _toastText = null; _toastTarget = null; KillTimer(hwnd, (IntPtr)2); InvalidateRect(hwnd, IntPtr.Zero, false); return IntPtr.Zero; }
                if ((int)wParam == SelAutoTimer) { SelAutoscrollTick(); return IntPtr.Zero; }
                if ((int)wParam == HoverTimer) { HoverTick(); return IntPtr.Zero; }
                _cursorOn = !_cursorOn;
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;

            case WM_SIZE:
                if (_rt is not null)
                {
                    int w = LoWord(lParam), h = HiWord(lParam);
                    if (w > 0 && h > 0)
                    {
                        _rt.Resize(new SizeI(w, h));
                        if (_active is not null) RegridSession(_active);
                        if (_cover is not null) RegridCover();
                        InvalidateRect(hwnd, IntPtr.Zero, false);
                    }
                }
                // Persist on maximize/restore transitions (button clicks, not per-pixel drag).
                { bool z = IsZoomed(hwnd); if (z != _wasMaximized) { _wasMaximized = z; SaveState(); } }
                return IntPtr.Zero;

            case WM_EXITSIZEMOVE:
                SaveState(); // persist geometry after a manual move/resize drag
                return IntPtr.Zero;

            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                if (_dragging && (int)wParam == VK_ESCAPE)
                {
                    _dragging = false; _sbPress = false; _pressItem = null; _dragItem = null;
                    ReleaseCapture(); RequestRedraw(); return IntPtr.Zero;
                }
                if (OnKeyDown((int)wParam)) return IntPtr.Zero;
                break;

            case WM_KEYUP:
                // Committing the MRU walk happens when Ctrl is finally released (any key-up where Ctrl
                // is no longer held is exactly the Ctrl-up event; a Tab-up with Ctrl still down is ignored).
                if (_mruWalking && !KeyDown(VK_CONTROL)) { MruCommit(); return IntPtr.Zero; }
                break;

            case WM_CHAR:
                {
                    char c = (char)wParam;
                    if (_coverKind == 3 && _ovlOwner is { OverlayExited: true }) { CloseActiveOverlay(); return IntPtr.Zero; }
                    if (_setOpen)
                    {
                        if (_ddRow is not null && c >= 0x20 && c != 0x7f) { _ddQuery += c; FilterDropdown(); RequestRedraw(); }
                        return IntPtr.Zero;
                    }
                    if (_palette != PaletteKind.None)
                    {
                        if (c >= 0x20 && c != 0x7f) { _palQuery += c; _palSel = 0; FilterPalette(); RequestRedraw(); }
                        return IntPtr.Zero;
                    }
                    if (_searchActive)
                    {
                        if (c >= 0x20 && c != 0x7f) { _searchQuery += c; RecomputeSearch(); _searchCur = 0; ScrollToMatch(); RequestRedraw(); }
                        return IntPtr.Zero;
                    }
                    if (c >= 0x20 && c != 0x7f) Send(c.ToString());
                    return IntPtr.Zero;
                }

            case WM_LBUTTONDOWN:
                {
                    int mx = LoWord(lParam), my = HiWord(lParam);
                    if (_setOpen) { SettingsClick(mx, my); return IntPtr.Zero; }
                    if (_palette != PaletteKind.None) { PaletteClick(mx, my); return IntPtr.Zero; }
                    // Notification banner: clicking it jumps to the raising session and dismisses.
                    if (_toastText is not null && _toastTarget is not null &&
                        mx >= _toastRect.Left && mx <= _toastRect.Right && my >= _toastRect.Top && my <= _toastRect.Bottom)
                    {
                        var t = _toastTarget;
                        _toastText = null; _toastTarget = null; KillTimer(hwnd, (IntPtr)2);
                        SetActive(t);
                        return IntPtr.Zero;
                    }
                    // Quick terminal is a floating panel: a left-click anywhere in the "main window area"
                    // outside the panel dismisses it (like a tool window that hides on focus loss).
                    if (_coverKind == 2)
                    {
                        var (qx, qy, qw, qh) = CoverRect();
                        if (mx < qx || mx >= qx + qw || my < qy || my >= qy + qh) { HideCover(); return IntPtr.Zero; }
                    }
                    if (my < (int)TitleBarH)
                    {
                        string? id = ChromeHit(_titleButtons, mx);
                        if (id is not null) { _pressBtn = id; _hotBtn = id; _hotPaint = id; SetCapture(hwnd); RequestRedraw(); }
                        return IntPtr.Zero;
                    }
                    if (mx < (int)_sidebarW)
                    {
                        if (my >= ClientH() - (int)FooterH)
                        {
                            string? id = ChromeHit(_footerButtons, mx);
                            if (id is not null) { _pressBtn = id; _hotBtn = id; _hotPaint = id; SetCapture(hwnd); RequestRedraw(); }
                            return IntPtr.Zero;
                        }
                        // List row: begin click-vs-drag (act on release). Skip while renaming.
                        if (_editHwnd == IntPtr.Zero)
                        {
                            _sbPress = true; _pressItem = RowAt(my); _pressX = mx; _pressY = my;
                            _dragging = false; _dragItem = null;
                            SetCapture(hwnd);
                        }
                        return IntPtr.Zero;
                    }
                    int di = _cover is null ? DividerAtX(mx, my) : -1;
                    if (di >= 0) { _divDragging = true; _divLeft = di; SetCapture(hwnd); return IntPtr.Zero; }
                    if (_cover is null) FocusPaneAtX(mx); // covers capture the whole content region
                    bool shiftDn = KeyDown(VK_SHIFT);
                    var em0 = _session?.Emulator;
                    if (em0 is not null && em0.MouseReporting && !shiftDn) { SendMousePx(0, lParam, true); SetCapture(hwnd); return IntPtr.Zero; }
                    // Text selection (app not grabbing the mouse, or Shift held to override).
                    if (PaneAt(mx, my) is { } h0)
                    {
                        var (line, col) = CellAtPx(h0.pane, h0.ox, h0.oy, h0.cw, h0.ch, mx, my);
                        if (Environment.TickCount - _lastClickMs < 400 && _clickCount >= 2)  // triple-click -> line
                        { SelectLine(h0.pane, line); _clickCount = 3; _selMoved = true; }
                        else { BeginSelect(h0.pane, line, col, shiftDn); _clickCount = 1; }
                        _lastClickMs = Environment.TickCount;
                        _selPane = h0.pane; _selAutoDir = 0; _selMouseX = mx;
                        _selecting = true; SetCapture(hwnd); RequestRedraw();
                    }
                    return IntPtr.Zero;
                }
            case WM_LBUTTONUP:
                if (_setOpen) { SettingsMouseUp(); return IntPtr.Zero; }
                if (_pressBtn is not null)
                {
                    ReleaseCapture();
                    int ux = LoWord(lParam), uy = HiWord(lParam);
                    string? over = uy < (int)TitleBarH ? ChromeHit(_titleButtons, ux)
                        : (ux < (int)_sidebarW && uy >= ClientH() - (int)FooterH ? ChromeHit(_footerButtons, ux) : null);
                    string fired = _pressBtn; _pressBtn = null; RequestRedraw();
                    if (over == fired) ChromeAction(fired);
                    return IntPtr.Zero;
                }
                if (_divDragging) { _divDragging = false; ReleaseCapture(); RequestRedraw(); return IntPtr.Zero; }
                if (_sbPress)
                {
                    ReleaseCapture();
                    int ux = LoWord(lParam), uy = HiWord(lParam);
                    if (_dragging && _dragItem is not null) DropDrag(_dragItem, uy);
                    else SidebarClick(ux, uy);   // was a click, not a drag
                    _sbPress = false; _pressItem = null; _dragItem = null; _dragging = false;
                    RequestRedraw();
                    return IntPtr.Zero;
                }
                if (_selecting)
                {
                    _selecting = false; ReleaseCapture(); StopSelAutoscroll();
                    var sp = _selPane; _selPane = null;
                    if (!_selMoved) { ActiveSurface()?.ClearSel(); RequestRedraw(); } // plain click clears
                    else if (sp is not null) FinalizeSelection(sp);                   // copy-on-select
                    return IntPtr.Zero;
                }
                if (InContent(lParam)) { SendMousePx(0, lParam, false); }
                ReleaseCapture(); return IntPtr.Zero;

            case WM_LBUTTONDBLCLK:
                {
                    int mx = LoWord(lParam), my = HiWord(lParam);
                    if (mx < (int)_sidebarW && my >= (int)TitleBarH && my < ClientH() - (int)FooterH)
                    {
                        var item = RowAt(my);
                        if (item is not null) { StartRename(item); return IntPtr.Zero; }
                    }
                    else if (PaneAt(mx, my) is { } h0)  // double-click selects the word
                    {
                        var (line, col) = CellAtPx(h0.pane, h0.ox, h0.oy, h0.cw, h0.ch, mx, my);
                        SelectWord(h0.pane, line, col);
                        _clickCount = 2; _lastClickMs = Environment.TickCount; _selMoved = true; _selecting = false;
                        FinalizeSelection(h0.pane);
                        RequestRedraw();
                    }
                    return IntPtr.Zero;
                }
            case WM_MBUTTONDOWN: if (InContent(lParam)) SendMousePx(1, lParam, true); return IntPtr.Zero;
            case WM_MBUTTONUP: if (InContent(lParam)) SendMousePx(1, lParam, false); return IntPtr.Zero;
            case WM_RBUTTONDOWN:
                if (InContent(lParam))
                {
                    var em2 = _session?.Emulator;
                    if (em2 is not null && em2.MouseReporting) SendMousePx(2, lParam, true);
                    else if (_config.RightClickPaste && (PaneAt(LoWord(lParam), HiWord(lParam))?.pane ?? ActiveSurface()) is { } pp)
                        PasteInto(pp);
                    return IntPtr.Zero;
                }
                break; // sidebar/chrome: let DefWindowProc raise WM_CONTEXTMENU so the row menu shows
            case WM_RBUTTONUP:
                if (InContent(lParam)) { SendMousePx(2, lParam, false); return IntPtr.Zero; }
                break; // sidebar/chrome: fall through to DefWindowProc -> WM_CONTEXTMENU

            case WM_MOUSEMOVE:
                {
                    if (_setOpen) { if (_setDragRow is not null) SettingsDrag(LoWord(lParam)); return IntPtr.Zero; }
                    if (!_mouseTracking)
                    {
                        var tme = new TRACKMOUSEEVENT { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = hwnd, dwHoverTime = 0 };
                        TrackMouseEvent(ref tme); _mouseTracking = true;
                    }
                    if (!_divDragging && !_sbPress && !_selecting) UpdateChromeHover(LoWord(lParam), HiWord(lParam));
                    if (_divDragging && ((long)wParam & MK_LBUTTON) != 0) { DragDivider(LoWord(lParam)); return IntPtr.Zero; }
                    if (_sbPress && ((long)wParam & MK_LBUTTON) != 0)
                    {
                        int mx = LoWord(lParam), my = HiWord(lParam);
                        if (!_dragging && _pressItem is not null &&
                            Math.Abs(mx - _pressX) + Math.Abs(my - _pressY) > DragThreshold)
                        { _dragging = true; _dragItem = _pressItem; }
                        if (_dragging) { _dragX = mx; _dragY = my; RequestRedraw(); }
                        return IntPtr.Zero;
                    }
                    if (_selecting && ((long)wParam & MK_LBUTTON) != 0)
                    {
                        int mmx = LoWord(lParam), mmy = HiWord(lParam);
                        _selMouseX = mmx;
                        if (_selPane is { } sp && PaneBox(sp) is { } bx)
                        {
                            float top = bx.oy, bottom = bx.oy + bx.rows * bx.ch;
                            _selAutoDir = mmy < top ? -1 : (mmy >= bottom ? 1 : 0);
                            // Track focus at the vertically-clamped point so horizontal moves register out of bounds.
                            int cy = (int)Math.Clamp(mmy, top, bottom - 1);
                            var (line, col) = CellAtPx(sp, bx.ox, bx.oy, bx.cw, bx.ch, mmx, cy);
                            UpdateSelect(sp, line, col);
                            if (_selAutoDir != 0) SetTimer(hwnd, (IntPtr)SelAutoTimer, 50, IntPtr.Zero);
                            else StopSelAutoscroll();
                            RequestRedraw();
                        }
                        return IntPtr.Zero;
                    }
                    var em = _session?.Emulator;
                    if (em is not null && em.MouseReportsMotion && InContent(lParam))
                        SendMousePx(32 + (((long)wParam & MK_LBUTTON) != 0 ? 0 : 3), lParam, true);
                    return IntPtr.Zero;
                }

            case WM_MOUSEWHEEL:
                {
                    if (_setOpen) { SettingsWheel(HiWord(wParam) > 0 ? 1 : -1); return IntPtr.Zero; }
                    var pt = new POINT { x = LoWord(lParam), y = HiWord(lParam) }; // wheel gives screen coords
                    ScreenToClient(_hwnd, ref pt);
                    var em = _session?.Emulator;
                    if (em is not null && em.MouseReporting) // app wants the wheel (forward to the active pane)
                    {
                        if (pt.x >= (int)_sidebarW && pt.y >= (int)TitleBarH)
                            SendMouse(HiWord(wParam) > 0 ? 64 : 65, pt.x, pt.y, true);
                        return IntPtr.Zero;
                    }
                    // Otherwise scroll the pane under the cursor through its scrollback history.
                    if (_cover is not null && pt.x >= (int)_sidebarW && pt.y >= (int)TitleBarH)
                    {
                        int hn = _cover.S.Emulator.HistoryCount;
                        int step = Math.Clamp(_config.ScrollSpeed, 1, 10);
                        int no = Math.Clamp(_cover.ScrollOffset + (HiWord(wParam) > 0 ? step : -step), 0, hn);
                        if (no != _cover.ScrollOffset) { _cover.ScrollOffset = no; RequestRedraw(); }
                        return IntPtr.Zero;
                    }
                    if (_active is not null && pt.x >= (int)_sidebarW && pt.y >= (int)TitleBarH)
                        foreach (var (p, x, _, w, _) in PaneLayout(_active))
                            if (pt.x >= x && pt.x < x + w)
                            {
                                int histN = p.S.Emulator.HistoryCount;
                                int dir = HiWord(wParam) > 0 ? 1 : -1; // wheel up scrolls back into history
                                int no = Math.Clamp(p.ScrollOffset + dir * Math.Clamp(_config.ScrollSpeed, 1, 10), 0, histN);
                                if (no != p.ScrollOffset) { p.ScrollOffset = no; RequestRedraw(); }
                                break;
                            }
                    return IntPtr.Zero;
                }

            case WM_ACTIVATE:
                if (LoWord(wParam) != 0 && _frontmostId != Id) // WA_ACTIVE/WA_CLICKACTIVE -> this window is frontmost
                {
                    Frontmost = this; _frontmostId = Id; SaveIndex();
                }
                return DefWindowProcW(hwnd, msg, wParam, lParam);

            case WM_DESTROY:
                RemoveTrayIcon();                 // drop the shell tray balloon icon
                SaveState(captureCommands: true); // persist this window's tree before tearing down its sessions
                foreach (var s in AllSessions()) { foreach (var p in s.Panes) { try { p.S.Dispose(); } catch { } } try { s.Scratch?.S.Dispose(); } catch { } try { s.Overlay?.S.Dispose(); } catch { } }
                try { _quick?.S.Dispose(); } catch { }
                bool lastWindow;
                lock (_windowIndex)
                {
                    _byId.Remove(Id);
                    int otherOpen = _windowIndex.Count(m => m.IsOpen && m.Id != Id);
                    lastWindow = otherOpen == 0;
                    var meta = _windowIndex.FirstOrDefault(m => m.Id == Id);
                    // Explicit close (others remain) -> mark closed so it won't auto-reopen. App quit
                    // (this was the last open window) -> keep IsOpen=true so it reopens next launch.
                    if (meta is not null && !lastWindow) meta.IsOpen = false;
                    if (ReferenceEquals(Frontmost, this))
                    {
                        var nf = _byId.Values.FirstOrDefault();
                        if (nf is not null) { Frontmost = nf; _frontmostId = nf.Id; }
                    }
                }
                SaveIndex();
                if (lastWindow) PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Send(string s)
    {
        var surf = ActiveSurface();
        if (surf is not null) { surf.ScrollOffset = 0; surf.ClearSel(); } // typing snaps to bottom, clears selection
        _session?.NotifyActivity();
        _session?.Write(Encoding.UTF8.GetBytes(s));
    }

    /// <summary>Scroll the active pane's scrollback by delta lines (or to top/bottom for ±int.MaxValue).</summary>
    private bool ScrollActivePane(int deltaLines)
    {
        var p = ActiveSurface();
        if (p is null) return false;
        int hist = p.S.Emulator.HistoryCount;
        int no = deltaLines == int.MaxValue ? hist : deltaLines == int.MinValue ? 0
               : Math.Clamp(p.ScrollOffset + deltaLines, 0, hist);
        if (no != p.ScrollOffset) { p.ScrollOffset = no; RequestRedraw(); }
        return true;
    }

    /// <summary>Dispatch a keymap action id (or "command:&lt;Label&gt;") to the matching behavior.</summary>
    private void RunAction(string action)
    {
        if (action.StartsWith("command:", StringComparison.OrdinalIgnoreCase))
        {
            string label = action["command:".Length..];
            var cmd = _commands.FirstOrDefault(c => string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase));
            if (cmd is not null) RunCustomCommand(cmd);
            else ShowToast($"no command '{label}'");
            return;
        }
        switch (action)
        {
            case "new_session": CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), true); break;
            case "new_workspace": CreateWorkspace(Guid.NewGuid().ToString(), null); break;
            case "close_session": case "close_pane": if (_coverKind == 3) CloseActiveOverlay(); else if (_cover is not null) HideCover(); else CloseActivePane(); break;
            case "split_pane": SplitOp("toggle"); break;   // toggle 1<->2 panes (agterm-style), not add
            case "toggle_scratch": if (_active is not null) ScratchOp(_active, "toggle"); break;
            case "quick_terminal": QuickOp("toggle"); break;
            case "focus_left_pane": FocusPane(-1); break;
            case "focus_right_pane": FocusPane(1); break;
            case "next_session": CycleSession(1); break;
            case "previous_session": CycleSession(-1); break;
            case "toggle_sidebar": ToggleSidebar(); break;
            case "rename_session": if (_active is not null) StartRename(_active); break;
            case "delete_workspace": if (_active is not null) DeleteWorkspace(_active.Ws); break;
            case "session_palette": TogglePalette(PaletteKind.Sessions); break;
            case "action_palette": TogglePalette(PaletteKind.Actions); break;
            case "attention_list": TogglePalette(PaletteKind.Attention); break;
            case "custom_palette": TogglePalette(PaletteKind.Custom); break;
            case "new_window": WindowNew(null); break;
            case "close_window": DestroyWindow(_hwnd); break; // WM_DESTROY tears down + updates the library
            case "switch_window": TogglePalette(PaletteKind.Windows); break;
            case "next_attention": GoToNextAttention(1); break;
            case "previous_attention": GoToNextAttention(-1); break;
            case "reload_keymap": ReloadKeymap(); break;
            case "toggle_search": ToggleSearch(); break;
            case "increase_font_size": ChangeFontSize(1); break;
            case "decrease_font_size": ChangeFontSize(-1); break;
            case "reset_font_size": ChangeFontSize(0); break;
            case "install_shell_integration": InstallShellIntegration(); break;
            case "toggle_flag": if (_active is not null) FlagOp(_active, "toggle"); break;
            case "toggle_flagged_view": ToggleFlaggedView(); break;
            case "focus_workspace": WorkspaceFocusOp("toggle"); break;
            case "select_all": if (ActiveSurface() is { } sa) SelectAll(sa); break;
            case "copy_selection": if (ActiveSurface() is { } sc && sc.HasSel) CopySelection(sc); break;
            case "paste": if (ActiveSurface() is { } sp2) PasteInto(sp2); break;
        }
    }

    /// <summary>Install the $PROFILE OSC-7 shell integration off the UI thread, then toast the result.</summary>
    private void InstallShellIntegration() => RunInstaller(Agwinterm.Pty.ShellIntegrationInstaller.Install);

    /// <summary>Opt-in: add agwintermctl to the user PATH (agterm's "Install Command Line Tool").</summary>
    private void InstallCli() => RunInstaller(Agwinterm.Pty.CliInstaller.Install);
    /// <summary>Opt-in: install Claude Code / Codex / generic agent status hooks.</summary>
    private void InstallHooks() => RunInstaller(Agwinterm.Pty.AgentHooks.Install);
    /// <summary>Opt-in: install the agent skill (teaches agents to drive agwinterm via agwintermctl).</summary>
    private void InstallSkill() => RunInstaller(Agwinterm.Pty.AgentSkill.Install);

    /// <summary>Run an installer off the UI thread and toast its result.</summary>
    private void RunInstaller(Func<string> installer)
        => System.Threading.Tasks.Task.Run(() => { string r = installer(); Post(() => ShowToast(r)); });

    /// <summary>Run a configured custom command per its mode (send|new|overlay|detached), expanding {AGW_*}.</summary>
    private void RunCustomCommand(Keymap.CmdDef cmd) => RunCommandText(cmd.Text, cmd.Mode);

    /// <summary>Run an arbitrary command string in a mode, expanding {AGW_*} tokens and injecting $AGW_*
    /// env from the active session. Returns the expanded command line (for the control API / observability).</summary>
    private string RunCommandText(string text, string? mode)
    {
        var ctx = _active;
        string expanded = ExpandAgwTokens(text, ctx);
        switch ((mode ?? "send").ToLowerInvariant())
        {
            case "new":
                CreateSession(Guid.NewGuid().ToString(), null, RawCwdOf(ctx), ActiveWorkspace(), makeActive: true,
                    command: expanded, interactive: true, extraEnv: AgwEnv(ctx));
                break;
            case "overlay":
                if (ctx is not null) OverlayOpen(ctx, expanded, 0, false, AgwEnv(ctx));
                else ShowToast("no session for overlay command");
                break;
            case "detached":
                RunDetached(expanded, RawCwdOf(ctx), AgwEnv(ctx));
                break;
            default: // send — type it into the active session, as if the user typed it + Enter
                Send(expanded.Replace("\r", "").Replace("\n", "") + "\r");
                break;
        }
        return expanded;
    }

    /// <summary>The active pane's real working directory (Windows path), or "" if unknown.</summary>
    private static string RawCwdOf(Ses? ses)
    {
        if (ses is null) return "";
        string live = PrettyCwd(SafeCwd(ses));
        return live.Length > 0 ? live : (ses.StartCwd ?? "");
    }

    /// <summary>Best-effort path to agwintermctl.exe (next to us), else just "agwintermctl" (assume PATH).</summary>
    private static string CtlPath()
    {
        try { string p = Path.Combine(AppContext.BaseDirectory, "agwintermctl.exe"); if (File.Exists(p)) return p; }
        catch { }
        return "agwintermctl";
    }

    /// <summary>The {AGW_*} / $AGW_* values for a session context (active by default).</summary>
    private static Dictionary<string, string> AgwValues(Ses? ses) => new(StringComparer.Ordinal)
    {
        ["AGW_SESSION"] = ses?.Name ?? "",
        ["AGW_SESSION_ID"] = ses?.Id ?? "",
        ["AGW_WORKSPACE"] = ses?.Ws.Name ?? "",
        ["AGW_CWD"] = RawCwdOf(ses),
        ["AGW_PANE_ID"] = ses?.ActivePane.Id ?? "",
        ["AGW_APP"] = CtlPath(),
    };

    /// <summary>Expand {AGW_*} tokens; unknown {AGW_*} tokens expand to "".</summary>
    private static string ExpandAgwTokens(string text, Ses? ses)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("{AGW_", StringComparison.Ordinal) < 0) return text;
        var vals = AgwValues(ses);
        return System.Text.RegularExpressions.Regex.Replace(text, @"\{AGW_[A-Z0-9_]+\}",
            m => vals.TryGetValue(m.Value[1..^1], out var v) ? v : "");
    }

    /// <summary>$AGW_* environment (+ AGWINTERM=1) for a launched custom-command process.</summary>
    private static Dictionary<string, string> AgwEnv(Ses? ses)
    {
        var d = AgwValues(ses);
        d["AGWINTERM"] = "1";
        return d;
    }

    /// <summary>Detached run: an independent OS process (no PTY, not tied to a session), via cmd /c so
    /// shell syntax (redirection, `start &lt;url&gt;`) works. Non-blocking; inherits the $AGW_* context + cwd.</summary>
    private void RunDetached(string command, string? cwd, Dictionary<string, string> env)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(cwd) ? "" : cwd!,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { ShowToast("detached: " + ex.Message); }
    }

    // ---- Leader/prefix chord state machine (used by OnKeyDown + the command.leader control verb) ----

    private void BeginLeader() { _leaderPending = true; _leaderAtMs = Environment.TickCount64; RequestRedraw(); }
    private void CancelLeader() { if (_leaderPending) { _leaderPending = false; RequestRedraw(); } }

    /// <summary>Resolve a second chord against the leader bindings (runs it or toasts); clears pending.</summary>
    private string ResolveLeader(string chord)
    {
        _leaderPending = false; RequestRedraw();
        if (_leaderBindings.TryGetValue(chord, out var action)) { RunAction(action); return "ran " + action; }
        ShowToast($"leader: no binding for {chord}");
        return "no leader binding";
    }

    /// <summary>Build the human-readable command list (label, mode, resolved chord, text) for command.list.</summary>
    private static string CommandListText()
    {
        string ChordOf(string label)
        {
            string want = "command:" + label;
            foreach (var kv in _keymap) if (string.Equals(kv.Value, want, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            foreach (var kv in _leaderBindings) if (string.Equals(kv.Value, want, StringComparison.OrdinalIgnoreCase)) return "leader " + kv.Key;
            return "";
        }
        if (_commands.Count == 0) return "(no custom commands defined in keymap.conf)";
        var sb = new StringBuilder();
        foreach (var c in _commands)
            sb.Append(c.Label).Append('\t').Append(c.Mode).Append('\t')
              .Append(ChordOf(c.Label) is { Length: > 0 } ch ? ch : "-").Append('\t').Append(c.Text).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>UI-thread driver for the command.leader control verb (state|begin|cancel|key:&lt;chord&gt;).</summary>
    private string LeaderOp(string op)
    {
        if (op == "begin") { if (_leader is null) return "no leader configured"; BeginLeader(); return "pending"; }
        if (op == "cancel") { CancelLeader(); return "idle"; }
        if (op.StartsWith("key:", StringComparison.OrdinalIgnoreCase))
        {
            string? chord = Keymap.Canonicalize(op[4..]);
            if (chord is null) return "bad chord";
            if (!_leaderPending) BeginLeader();
            return ResolveLeader(chord);
        }
        return _leaderPending ? "pending" : "idle"; // "state"
    }

    private static string KeymapPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm", "keymap.conf");

    private static void LoadKeymap()
    {
        try
        {
            string path = KeymapPath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Keymap.StarterText);
            }
            var parsed = Keymap.Parse(File.ReadAllText(path));
            _keymap = parsed.Bindings;
            _commands = parsed.Commands;
            _leader = parsed.Leader;
            _leaderBindings = parsed.LeaderBindings;
            _keymapDiag = parsed.Diagnostics.ToArray();
        }
        catch
        {
            _keymap = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (c, a) in Keymap.DefaultBindings) _keymap[c] = a;
            _commands = new();
            _leader = null;
            _leaderBindings = new(StringComparer.OrdinalIgnoreCase);
            _keymapDiag = Array.Empty<string>();
        }
    }

    private void ReloadKeymap()
    {
        LoadKeymap();
        _leaderPending = false;   // any in-progress leader sequence is abandoned on reload
        ShowToast(_keymapDiag.Length == 0
            ? "keymap reloaded"
            : $"keymap: {_keymapDiag.Length} issue(s) — {_keymapDiag[0]}");
    }

    /// <summary>Coalesce many output/decode notifications into a single pending repaint.</summary>
    private void RequestRedraw()
    {
        if (System.Threading.Interlocked.Exchange(ref _redrawPending, 1) == 0)
            PostMessageW(_hwnd, WM_APP_REDRAW, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Report a mouse event given raw client pixels packed in lParam (button code already encoded).</summary>
    private void SendMousePx(int btn, IntPtr lParam, bool press)
        => SendMouse(btn, LoWord(lParam), HiWord(lParam), press);

    /// <summary>Origin (px) + metrics of the ACTIVE pane, for mouse→cell mapping.</summary>
    private (float ox, float oy, float cw, float ch) ActivePaneView()
    {
        if (_active is not null)
            foreach (var (pane, x, y, _, _) in PaneLayout(_active))
                if (ReferenceEquals(pane, _active.ActivePane)) { var (_, cw, ch) = Metrics(pane.FontSize); return (x, y, cw, ch); }
        var (_, c2, h2) = CurrentMetrics();
        return (ContentX, ContentY, c2, h2);
    }

    /// <summary>Focus the pane of the active session under client-x (no-op if single pane / no session).</summary>
    private void FocusPaneAtX(int px)
    {
        if (_active is null || _active.Panes.Count < 2) return;
        foreach (var (pane, x, _, w, _) in PaneLayout(_active))
            if (px >= x && px < x + w + DividerW) { _active.Active = _active.Panes.IndexOf(pane); _session = _active.S; RequestRedraw(); return; }
    }

    /// <summary>Left-pane index of the divider gutter under client-(x,y), or -1.</summary>
    private int DividerAtX(int px, int py)
    {
        if (_active is null || _active.Panes.Count < 2) return -1;
        var lay = PaneLayout(_active);
        if (py < (int)lay[0].y || py > (int)(lay[0].y + lay[0].h)) return -1;
        for (int i = 0; i < lay.Count - 1; i++)
        {
            float gx = lay[i].x + lay[i].w;
            if (px >= gx - 2 && px <= gx + DividerW + 2) return i;
        }
        return -1;
    }

    /// <summary>Drag a divider: shift width between the two adjacent panes (clamped).</summary>
    private void DragDivider(int px)
    {
        if (_active is null || _divLeft < 0 || _divLeft + 1 >= _active.Panes.Count) return;
        var (x0, _, totalW, _) = ContentArea();
        float avail = MathF.Max(_active.Panes.Count, totalW - (_active.Panes.Count - 1) * DividerW);
        float sum = _active.Panes.Sum(p => p.Ratio);
        var lay = PaneLayout(_active);
        float leftStart = lay[_divLeft].x;
        float pairRatio = _active.Panes[_divLeft].Ratio + _active.Panes[_divLeft + 1].Ratio;
        float newLeftW = Math.Clamp(px - leftStart, 24f, (pairRatio / sum) * avail - 24f);
        float newLeftRatio = (newLeftW / avail) * sum;
        _active.Panes[_divLeft + 1].Ratio = pairRatio - newLeftRatio;
        _active.Panes[_divLeft].Ratio = newLeftRatio;
        RegridSession(_active);
        RequestRedraw();
    }

    /// <summary>Encode a mouse event (SGR or legacy) and send it to the child. Mirrors the WinUI shell.</summary>
    private void SendMouse(int btn, int pxX, int pxY, bool press)
    {
        var em = _session?.Emulator;
        if (em is null || !em.MouseReporting) return;
        var (ox, oy, cw, ch) = ActivePaneView();
        int col = Math.Clamp((int)((pxX - ox) / cw), 0, em.Screen.Cols - 1);
        int row = Math.Clamp((int)((pxY - oy) / ch), 0, em.Screen.Rows - 1);
        string seq = em.MouseSgr
            ? $"\x1b[<{btn};{col + 1};{row + 1}{(press ? 'M' : 'm')}"
            : "\x1b[M" + (char)(32 + (press ? btn : 3)) + (char)(33 + col) + (char)(33 + row);
        _session?.Write(Encoding.UTF8.GetBytes(seq));
    }

    // ---- Text selection + clipboard ----

    /// <summary>Pane of the active session under a client point + its origin/metrics, or null.</summary>
    private (Pane pane, float ox, float oy, float cw, float ch)? PaneAt(int px, int py)
    {
        if (px < (int)_sidebarW || py < (int)TitleBarH || py >= ClientH() - (int)FooterH) return null;
        if (_cover is not null) { var (cx, cy, _, _) = CoverRect(); var (_, ccw, cch) = Metrics(_cover.FontSize); return (_cover, cx, cy, ccw, cch); }
        if (_active is null) return null;
        foreach (var (pane, x, y, w, _) in PaneLayout(_active))
            if (px >= x && px < x + w + DividerW)
            {
                var (_, cw, ch) = Metrics(pane.FontSize);
                return (pane, x, y, cw, ch);
            }
        return null;
    }

    /// <summary>Origin/cell-size/row-count of a specific pane (for drag-autoscroll), or null if not laid out.</summary>
    private (float ox, float oy, float cw, float ch, int rows)? PaneBox(Pane pane)
    {
        if (_cover is not null && ReferenceEquals(pane, _cover))
        {
            var (cx, cy, _, chh) = CoverRect();
            var (_, ccw, cch) = Metrics(_cover.FontSize);
            return (cx, cy, ccw, cch, Math.Max(1, (int)(chh / cch)));
        }
        if (_active is null) return null;
        foreach (var (p, x, y, _, h) in PaneLayout(_active))
            if (ReferenceEquals(p, pane))
            {
                var (_, cw, ch) = Metrics(pane.FontSize);
                return (x, y, cw, ch, Math.Max(1, (int)(h / ch)));
            }
        return null;
    }

    /// <summary>Map a client point to an ABSOLUTE (line,col) in the pane (history + live grid).</summary>
    private (int line, int col) CellAtPx(Pane pane, float ox, float oy, float cw, float ch, int px, int py)
    {
        var em = pane.S.Emulator;
        int cols, rows, hist;
        lock (pane.S.SyncRoot) { cols = em.Screen.Cols; rows = em.Screen.Rows; hist = em.HistoryCount; }
        int off = Math.Clamp(pane.ScrollOffset, 0, hist);
        int vr = Math.Clamp((int)((py - oy) / ch), 0, rows - 1);
        int col = Math.Clamp((int)((px - ox) / cw), 0, cols - 1);
        int line = Math.Clamp(hist - off + vr, 0, hist + rows - 1);
        return (line, col);
    }

    private static Cell CellAbs(TerminalEmulator em, int hist, int rows, int cols, int line, int col)
    {
        col = Math.Clamp(col, 0, cols - 1);
        if (line < hist) return em.GetHistoryCell(line, col);
        int live = line - hist;
        return (live >= 0 && live < rows) ? em.Screen[live, col] : Cell.Empty;
    }

    private static void NormSel(Pane p, out int l0, out int c0, out int l1, out int c1)
    {
        bool ancFirst = p.SelAncLine < p.SelFocLine || (p.SelAncLine == p.SelFocLine && p.SelAncCol <= p.SelFocCol);
        (l0, c0) = ancFirst ? (p.SelAncLine, p.SelAncCol) : (p.SelFocLine, p.SelFocCol);
        (l1, c1) = ancFirst ? (p.SelFocLine, p.SelFocCol) : (p.SelAncLine, p.SelAncCol);
    }

    private static string SelectionText(Pane pane)
    {
        if (!pane.HasSel) return "";
        var em = pane.S.Emulator;
        var sb = new StringBuilder();
        lock (pane.S.SyncRoot)
        {
            int cols = em.Screen.Cols, rows = em.Screen.Rows, hist = em.HistoryCount;
            NormSel(pane, out int l0, out int c0, out int l1, out int c1);
            for (int line = l0; line <= l1; line++)
            {
                int from = line == l0 ? c0 : 0;
                int to = (line == l1 ? c1 : cols - 1) + 1; // inclusive end col
                var row = new StringBuilder();
                for (int c = Math.Max(0, from); c < Math.Min(cols, to); c++)
                {
                    Cell cell = CellAbs(em, hist, rows, cols, line, c);
                    if (cell.Width == 0) continue; // trailing spacer of a wide glyph
                    row.Append(cell.Rune == '\0' ? ' ' : cell.Rune);
                }
                sb.Append(row.ToString().TrimEnd(' '));
                if (line != l1) sb.Append("\r\n");
            }
        }
        return sb.ToString();
    }

    private void BeginSelect(Pane p, int line, int col, bool extend)
    {
        if (extend && p.HasSel) { p.SelFocLine = line; p.SelFocCol = col; _selMoved = true; }
        else { p.SelAncLine = p.SelFocLine = line; p.SelAncCol = p.SelFocCol = col; p.HasSel = false; _selMoved = false; }
    }

    private void UpdateSelect(Pane p, int line, int col)
    {
        p.SelFocLine = line; p.SelFocCol = col; p.HasSel = true; _selMoved = true;
    }

    private void SelectWord(Pane p, int line, int col)
    {
        var em = p.S.Emulator;
        lock (p.S.SyncRoot)
        {
            int cols = em.Screen.Cols, rows = em.Screen.Rows, hist = em.HistoryCount;
            bool Word(int c) { char ch = CellAbs(em, hist, rows, cols, line, c).Rune; return ch != ' ' && ch != '\0'; }
            if (col < 0 || col >= cols || !Word(col)) return;
            int a = col, b = col;
            while (a > 0 && Word(a - 1)) a--;
            while (b < cols - 1 && Word(b + 1)) b++;
            p.SelAncLine = p.SelFocLine = line; p.SelAncCol = a; p.SelFocCol = b; p.HasSel = true;
        }
    }

    private void SelectLine(Pane p, int line)
    {
        int cols; lock (p.S.SyncRoot) cols = p.S.Emulator.Screen.Cols;
        p.SelAncLine = p.SelFocLine = line; p.SelAncCol = 0; p.SelFocCol = cols - 1; p.HasSel = true;
    }

    private void CopySelection(Pane pane, bool clear = true)
    {
        string t = SelectionText(pane);
        if (t.Length > 0) ClipboardSet(t);
        if (clear) pane.ClearSel();
        RequestRedraw();
    }

    /// <summary>Select the pane's entire buffer — all scrollback history through the last live row.</summary>
    private void SelectAll(Pane p)
    {
        var em = p.S.Emulator;
        int cols, rows, hist;
        lock (p.S.SyncRoot) { cols = em.Screen.Cols; rows = em.Screen.Rows; hist = em.HistoryCount; }
        p.SelAncLine = 0; p.SelAncCol = 0;
        p.SelFocLine = hist + rows - 1; p.SelFocCol = cols - 1;
        p.HasSel = (hist + rows) > 0 && cols > 0;
        RequestRedraw();
    }

    /// <summary>Called when a selection is finished (drag mouse-up / word / line select). Honors copy-on-select
    /// by copying to the clipboard without clearing the highlight.</summary>
    private void FinalizeSelection(Pane p)
    {
        if (_config.CopyOnSelect && p.HasSel) CopySelection(p, clear: false);
    }

    private void StopSelAutoscroll()
    {
        if (_selAutoDir != 0) { _selAutoDir = 0; KillTimer(_hwnd, (IntPtr)SelAutoTimer); }
    }

    /// <summary>Drag-autoscroll tick: while the mouse is held above/below the selection pane, scroll its
    /// scrollback one line toward the cursor and extend the selection to the newly-revealed edge.</summary>
    private void SelAutoscrollTick()
    {
        if (!_selecting || _selAutoDir == 0 || _selPane is not { } sp || PaneBox(sp) is not { } bx) { StopSelAutoscroll(); return; }
        int cols, rows, hist;
        lock (sp.S.SyncRoot) { cols = sp.S.Emulator.Screen.Cols; rows = sp.S.Emulator.Screen.Rows; hist = sp.S.Emulator.HistoryCount; }
        // dir -1 (mouse above) reveals older lines -> larger ScrollOffset; dir +1 (below) -> smaller.
        int no = Math.Clamp(sp.ScrollOffset - _selAutoDir, 0, hist);
        sp.ScrollOffset = no;
        int vr = _selAutoDir < 0 ? 0 : rows - 1;                      // extend to the top/bottom visible row
        int line = Math.Clamp(hist - no + vr, 0, hist + rows - 1);
        int col = Math.Clamp((int)((_selMouseX - bx.ox) / bx.cw), 0, cols - 1);
        UpdateSelect(sp, line, col);
        RequestRedraw();
    }

    private void PasteInto(Pane pane) => PasteTextInto(pane, ClipboardGet());

    /// <summary>Paste literal text into a pane, honoring bracketed-paste mode (shared by Ctrl+V and the API).</summary>
    private void PasteTextInto(Pane pane, string t)
    {
        if (t.Length == 0) return;
        t = t.Replace("\r\n", "\r").Replace("\n", "\r");
        if (pane.S.Emulator.BracketedPaste) t = "\x1b[200~" + t + "\x1b[201~";
        pane.ScrollOffset = 0; pane.S.NotifyActivity();
        pane.S.Write(Encoding.UTF8.GetBytes(t));
        RequestRedraw();
    }

    // ---- In-terminal search (find bar over the active pane's buffer + scrollback) ----

    private void ToggleSearch()
    {
        if (_searchActive) { CloseSearch(); return; }
        _searchActive = true; _searchQuery = ""; _searchMatches.Clear(); _searchCur = 0;
        RequestRedraw();
    }

    private void CloseSearch() { _searchActive = false; _searchMatches.Clear(); RequestRedraw(); }

    /// <summary>Recompute all case-insensitive matches for _searchQuery over the active pane's history + live grid.</summary>
    private void RecomputeSearch()
    {
        _searchMatches.Clear();
        var ap = ActiveSurface();
        if (ap is null || _searchQuery.Length == 0) return;
        string q = _searchQuery.ToLowerInvariant();
        var em = ap.S.Emulator;
        lock (ap.S.SyncRoot)
        {
            int cols = em.Screen.Cols, rows = em.Screen.Rows, hist = em.HistoryCount;
            var sb = new StringBuilder(cols);
            for (int line = 0; line < hist + rows; line++)
            {
                sb.Clear();
                for (int c = 0; c < cols; c++)
                {
                    Cell cell = CellAbs(em, hist, rows, cols, line, c);
                    sb.Append(cell.Rune == '\0' ? ' ' : cell.Rune);
                }
                string text = sb.ToString().ToLowerInvariant();
                int idx = 0;
                while ((idx = text.IndexOf(q, idx, StringComparison.Ordinal)) >= 0)
                {
                    _searchMatches.Add((line, idx, idx + q.Length - 1));
                    idx += q.Length;
                }
            }
        }
        if (_searchCur >= _searchMatches.Count) _searchCur = 0;
    }

    private void SearchStep(int dir)
    {
        if (_searchMatches.Count == 0) return;
        int n = _searchMatches.Count;
        _searchCur = ((_searchCur + dir) % n + n) % n;
        ScrollToMatch(); RequestRedraw();
    }

    private void ScrollToMatch()
    {
        var ap = ActiveSurface();
        if (ap is null || _searchCur < 0 || _searchCur >= _searchMatches.Count) return;
        int ml = _searchMatches[_searchCur].Line;
        var em = ap.S.Emulator;
        int rows, hist; lock (ap.S.SyncRoot) { rows = em.Screen.Rows; hist = em.HistoryCount; }
        ap.ScrollOffset = Math.Clamp(hist - ml + rows / 2, 0, hist); // centre the match; live grid => snaps to 0
    }

    private string SearchStatus()
        => _searchMatches.Count == 0 ? (_searchQuery.Length == 0 ? "" : "no matches")
           : $"{_searchCur + 1} of {_searchMatches.Count}";

    private bool SearchKeyDown(int vk)
    {
        bool shift = KeyDown(VK_SHIFT);
        switch (vk)
        {
            case VK_ESCAPE: CloseSearch(); return true;
            case VK_RETURN: SearchStep(shift ? -1 : 1); return true;
            case 0x72: SearchStep(shift ? -1 : 1); return true; // F3
            case VK_BACK:
                if (_searchQuery.Length > 0) { _searchQuery = _searchQuery[..^1]; RecomputeSearch(); _searchCur = 0; ScrollToMatch(); RequestRedraw(); }
                return true;
        }
        return true; // consume all keys while the find bar is open
    }

    /// <summary>Open the clipboard with retries: clipboard history / cloud sync / managers briefly
    /// hold it open on every change, so a single failed OpenClipboard is common and transient.</summary>
    private bool OpenClipboardRetry()
    {
        for (int i = 0; ; i++)
        {
            if (OpenClipboard(_hwnd)) return true;
            if (i >= 9) return false;
            System.Threading.Thread.Sleep(10);
        }
    }

    private void ClipboardSet(string text)
    {
        // Build the handle before touching the clipboard so a failed alloc never leaves it emptied.
        byte[] buf = Encoding.Unicode.GetBytes(text + "\0");
        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)buf.Length);
        if (h == IntPtr.Zero) return;
        IntPtr p = GlobalLock(h);
        if (p == IntPtr.Zero) { GlobalFree(h); return; }
        Marshal.Copy(buf, 0, p, buf.Length);
        GlobalUnlock(h);

        if (!OpenClipboardRetry()) { GlobalFree(h); return; }
        try
        {
            EmptyClipboard();
            if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero)
                GlobalFree(h); // ownership only transfers to the clipboard on success
        }
        finally { CloseClipboard(); }
    }

    private string ClipboardGet()
    {
        if (!OpenClipboardRetry()) return "";
        try
        {
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return "";
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Selection text of the target session's active pane (for the control API).</summary>
    private static string SessionSelectionText(Ses s) => SelectionText(s.ActivePane);

    /// <summary>Returns true if the key was consumed (matches the WinUI key table).</summary>
    private bool OnKeyDown(int vk)
    {
        bool ctrl = KeyDown(VK_CONTROL), shift = KeyDown(VK_SHIFT), alt = KeyDown(VK_MENU);

        // A --wait overlay whose program has exited hangs around; any key dismisses it.
        if (_coverKind == 3 && _ovlOwner is { OverlayExited: true }) { CloseActiveOverlay(); return true; }

        // Escape during an MRU walk cancels back to where the walk began.
        if (_mruWalking && vk == VK_ESCAPE) { MruCancel(); return true; }

        // While a palette is open it owns the keyboard (nav here, typing via WM_CHAR).
        if (_setOpen) return SettingsKey(vk);
        if (_palette != PaletteKind.None) return PaletteKeyDown(vk);
        // While the find bar is open it owns the keyboard (Enter/F3 nav, Esc close, Backspace edit).
        if (_searchActive) return SearchKeyDown(vk);

        // Leader/prefix sequence (tmux-style). When pending, the next chord resolves against the leader
        // bindings; Esc / timeout cancels; modifier-only keydowns stay pending. Checked before the normal
        // chord/clipboard/terminal paths so the leader chord and its follow-up key are captured.
        if (_leaderPending)
        {
            if (Environment.TickCount64 - _leaderAtMs > LeaderTimeoutMs) CancelLeader(); // expired — fall through to normal handling
            else
            {
                if (vk == VK_ESCAPE) { CancelLeader(); return true; }
                string? lc = Keymap.ChordFor(vk, ctrl, alt, shift);
                if (lc is null) return true;   // lone modifier — keep waiting, swallow
                ResolveLeader(lc);
                return true;
            }
        }
        if (_leader is not null && Keymap.ChordFor(vk, ctrl, alt, shift) == _leader) { BeginLeader(); return true; }

        // Shift+PageUp/PageDown (and Home/End) scroll this pane's scrollback; never reach the PTY.
        if (shift && !ctrl && !alt && _active is not null)
        {
            int page = Math.Max(1, _active.ActivePane.S.Rows - 1);
            switch (vk)
            {
                case VK_PRIOR: return ScrollActivePane(page);        // Shift+PageUp — older
                case VK_NEXT: return ScrollActivePane(-page);        // Shift+PageDown — newer
                case VK_HOME: return ScrollActivePane(int.MaxValue); // oldest
                case VK_END: return ScrollActivePane(int.MinValue);  // live bottom
            }
        }

        // Fixed font-zoom shortcuts (Ctrl +/-/0, incl. numpad). Handled here, not via keymap.conf,
        // because '+' clashes with the chord joiner (matching agterm's constraint).
        if (ctrl && !alt)
        {
            switch (vk)
            {
                case 0xBB: case 0x6B: ChangeFontSize(1); return true;   // Ctrl+= / Ctrl+numpad-plus
                case 0xBD: case 0x6D: ChangeFontSize(-1); return true;  // Ctrl+- / Ctrl+numpad-minus
                case 0x30: case 0x60: ChangeFontSize(0); return true;   // Ctrl+0 / Ctrl+numpad-0
                case 0xC0: QuickOp("toggle"); return true;              // Ctrl+` — quick terminal (VK_OEM_3, can't be a chord)
            }
        }

        // Clipboard: Ctrl+Shift+C / Ctrl+C-with-selection copies; Ctrl+V / Ctrl+Shift+V pastes.
        // Uses ActiveSurface() (not _active.ActivePane) so selections in a cover — quick terminal,
        // scratch, overlay — copy too instead of falling through and interrupting the shell.
        if (ctrl && !alt && ActiveSurface() is { } ap)
        {
            if (vk == 0x43) // C
            {
                if (ap.HasSel) { CopySelection(ap); return true; }
                if (shift) return true;   // Ctrl+Shift+C, no selection: consume (don't send ^C)
                // plain Ctrl+C with no selection falls through to the interrupt below
            }
            else if (vk == 0x56) { PasteInto(ap); return true; } // Ctrl+V / Ctrl+Shift+V
        }

        // Ctrl+Tab / Ctrl+Shift+Tab drive the MRU session walk (needs WM_KEYUP to commit, so it lives
        // here rather than the keymap dispatch). Honoured only while the chord is still bound to the
        // session-cycle action (default) — a user rebind of the chord falls through to keymap dispatch.
        if (ctrl && !alt && vk == VK_TAB)
        {
            string mruChord = shift ? "ctrl+shift+tab" : "ctrl+tab";
            if (!_keymap.TryGetValue(mruChord, out var mruAct) || mruAct is "next_session" or "previous_session")
            { MruWalk(shift ? -1 : 1); return true; }
        }

        // Keymap dispatch: build the chord and run its bound action (defaults overlaid by keymap.conf).
        // Unbound chords fall through to terminal input below.
        string? chord = Keymap.ChordFor(vk, ctrl, alt, shift);
        if (chord is not null && _keymap.TryGetValue(chord, out var action)) { RunAction(action); return true; }

        if (_session is null) return false;

        if (ctrl && !alt)
        {
            if (vk >= VK_A && vk <= VK_Z) { Send(((char)(vk - VK_A + 1)).ToString()); return true; }
            if (vk == VK_SPACE) { Send("\0"); return true; }
        }

        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string m = mod > 1 ? $"1;{mod}" : "";

        string? seq = vk switch
        {
            VK_RETURN => "\r",
            VK_BACK => "\x7f",
            VK_TAB => shift ? "\x1b[Z" : "\t",
            VK_ESCAPE => "\x1b",
            VK_UP => $"\x1b[{m}A",
            VK_DOWN => $"\x1b[{m}B",
            VK_RIGHT => $"\x1b[{m}C",
            VK_LEFT => $"\x1b[{m}D",
            VK_HOME => $"\x1b[{m}H",
            VK_END => $"\x1b[{m}F",
            VK_PRIOR => mod > 1 ? $"\x1b[5;{mod}~" : "\x1b[5~",
            VK_NEXT => mod > 1 ? $"\x1b[6;{mod}~" : "\x1b[6~",
            VK_INSERT => "\x1b[2~",
            VK_DELETE => "\x1b[3~",
            0x70 => "\x1bOP", 0x71 => "\x1bOQ", 0x72 => "\x1bOR", 0x73 => "\x1bOS", // F1-F4
            0x74 => "\x1b[15~", 0x75 => "\x1b[17~", 0x76 => "\x1b[18~", 0x77 => "\x1b[19~", // F5-F8
            0x78 => "\x1b[20~", 0x79 => "\x1b[21~", 0x7A => "\x1b[23~", 0x7B => "\x1b[24~", // F9-F12
            _ => null,
        };
        if (seq is not null) { Send(seq); return true; }
        return false;
    }

    private static Color4 C4(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, 1f);

    /// <summary>Effective foreground for a cell, resolved through the active theme: inverse swap,
    /// then faint (SGR 2) dimming. Resolving via the theme (not the baked RGB) is what makes a
    /// theme switch recolour already-printed content.</summary>
    private static Color EffectiveFg(Cell cell)
    {
        Color fg = cell.Attributes.HasFlag(CellAttributes.Inverse) ? _theme.ResolveBg(cell.BgSpec) : _theme.ResolveFg(cell.FgSpec);
        if (cell.Attributes.HasFlag(CellAttributes.Dim))
            fg = new Color((byte)(fg.R * 0.6f), (byte)(fg.G * 0.6f), (byte)(fg.B * 0.6f));
        return fg;
    }

    /// <summary>Effective background for a cell, resolved through the active theme (inverse swaps with fg).</summary>
    private static Color EffectiveBg(Cell cell)
        => cell.Attributes.HasFlag(CellAttributes.Inverse) ? _theme.ResolveFg(cell.FgSpec) : _theme.ResolveBg(cell.BgSpec);

    // ---- Kitty images (Direct2D) ----

    private static readonly string? _imgLog = Environment.GetEnvironmentVariable("AGWINTERM_IMGLOG");
    private static void Log(string m) { if (_imgLog is not null) try { File.AppendAllText(_imgLog, m + "\n"); } catch { } }

    private static readonly string? _perfLog = Environment.GetEnvironmentVariable("AGWINTERM_PERF");
    private static void Perf(string m) { if (_perfLog is not null) try { File.AppendAllText(_perfLog, m + "\n"); } catch { } }
    private int _uploadCount;
    private double _uploadMs;

    /// <summary>
    /// Draw current image placements. Decoding (PNG decompress) happens on a background
    /// thread so the UI never blocks; only the cheap GPU upload runs here. An image simply
    /// appears on the next redraw once its pixels are ready. Called under the session lock.
    /// </summary>
    private void DrawImages(TerminalEmulator em, float ox, float oy, float cw, float ch)
    {
        if (_rt is null) return;

        // 1) Upload any pixels decoded on background threads (cheap; UI thread only).
        while (_decoded.TryDequeue(out var d))
        {
            _decoding.Remove(d.img);
            if (d.bgra is null || _imageCache.ContainsKey(d.img)) continue;
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                var handle = GCHandle.Alloc(d.bgra, GCHandleType.Pinned);
                try { _imageCache[d.img] = _rt.CreateBitmap(new SizeI(d.w, d.h), handle.AddrOfPinnedObject(), (uint)(d.w * 4), _bmpProps); }
                finally { handle.Free(); }
                _uploadCount++; _uploadMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                Log($"uploaded id={d.img.Id} {d.w}x{d.h}");
            }
            catch (Exception ex) { Log($"upload FAILED id={d.img.Id}: {ex.GetType().Name} {ex.Message}"); }
        }

        // 2) Prune textures whose image was retransmitted or deleted (bounds memory during scroll).
        if (_imageCache.Count > 0)
        {
            var live = new HashSet<KittyImage>(em.Images.Values);
            foreach (var stale in _imageCache.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _imageCache[stale].Dispose();
                _imageCache.Remove(stale);
            }
        }

        // 3) Draw what's ready; kick off background decodes for what isn't.
        foreach (var p in em.Placements)
        {
            if (!em.Images.TryGetValue(p.ImageId, out var img)) continue;
            if (!_imageCache.TryGetValue(img, out var bmp))
            {
                if (_decoding.Add(img)) // not already decoding
                    _ = Task.Run(() => DecodePixelsAsync(img));
                continue; // will render on a later frame once uploaded
            }
            float ix = ox + p.Col * cw;
            float iy = oy + p.Row * ch;
            float iw = p.Cols > 0 ? p.Cols * cw : bmp.Size.Width;
            float ih = p.Rows > 0 ? p.Rows * ch : bmp.Size.Height;
            var dest = new Vortice.RawRectF(ix, iy, ix + iw, iy + ih);
            // Optional pixel source crop: scrolling just moves this window over the cached texture
            // (no re-transmit/re-decode/re-upload). null = whole image.
            Vortice.RawRectF? src = (p.SrcW > 0 && p.SrcH > 0)
                ? new Vortice.RawRectF(p.SrcX, p.SrcY, p.SrcX + p.SrcW, p.SrcY + p.SrcH)
                : null;
            try { _rt.DrawBitmap(bmp, dest, 1f, BitmapInterpolationMode.Linear, src); }
            catch (Exception ex) { Log($"DrawBitmap FAILED: {ex.GetType().Name} {ex.Message}"); }
        }
    }

    private static readonly BitmapProperties _bmpProps =
        new(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96f, 96f);

    /// <summary>
    /// Draw a session's background watermark into a pane's content rect (behind the cells; text is
    /// painted on top so it stays readable). Decode is off the UI thread (like DrawImages); the
    /// image simply appears once ready. Scrollback doesn't move it — it's pinned to the pane.
    /// </summary>
    private void DrawWatermark(Ses ses, float ox, float oy, float pw, float ph)
    {
        if (_rt is null || string.IsNullOrEmpty(ses.BgPath) || ses.BgOpacity <= 0 || pw <= 1 || ph <= 1) return;
        string path = ses.BgPath!;

        // Upload anything decoded on background threads (cheap; UI thread only).
        while (_bgDecoded.TryDequeue(out var d))
        {
            _bgDecoding.Remove(d.path);
            if (d.bgra is null || _bgCache.ContainsKey(d.path)) continue;
            try
            {
                var h = GCHandle.Alloc(d.bgra, GCHandleType.Pinned);
                try { _bgCache[d.path] = _rt.CreateBitmap(new SizeI(d.w, d.h), h.AddrOfPinnedObject(), (uint)(d.w * 4), _bmpProps); }
                finally { h.Free(); }
            }
            catch (Exception ex) { Log($"watermark upload FAILED {path}: {ex.Message}"); }
        }

        if (!_bgCache.TryGetValue(path, out var bmp))
        {
            if (_bgDecoding.Add(path)) _ = Task.Run(() => DecodeBackgroundAsync(path));
            return; // renders on a later frame once uploaded
        }

        float iw = bmp.Size.Width, ih = bmp.Size.Height;
        if (iw < 1 || ih < 1) return;
        float opacity = System.Math.Clamp(ses.BgOpacity, 0, 100) / 100f;

        _rt.PushAxisAlignedClip(new Vortice.RawRectF(ox, oy, ox + pw, oy + ph), AntialiasMode.Aliased);
        try
        {
            switch (ses.BgMode)
            {
                case "tile":
                    for (float ty = oy; ty < oy + ph; ty += ih)
                        for (float tx = ox; tx < ox + pw; tx += iw)
                            _rt.DrawBitmap(bmp, new Vortice.RawRectF(tx, ty, tx + iw, ty + ih), opacity, BitmapInterpolationMode.Linear, null);
                    break;
                case "center":
                {
                    float cx = ox + (pw - iw) / 2f, cy = oy + (ph - ih) / 2f;
                    _rt.DrawBitmap(bmp, new Vortice.RawRectF(cx, cy, cx + iw, cy + ih), opacity, BitmapInterpolationMode.Linear, null);
                    break;
                }
                default: // "fit" (letterbox) and "fill" (cover) — same math, min vs max scale; clip handles overflow.
                {
                    float scale = ses.BgMode == "fill" ? MathF.Max(pw / iw, ph / ih) : MathF.Min(pw / iw, ph / ih);
                    float dw = iw * scale, dh = ih * scale;
                    float dx = ox + (pw - dw) / 2f, dy = oy + (ph - dh) / 2f;
                    _rt.DrawBitmap(bmp, new Vortice.RawRectF(dx, dy, dx + dw, dy + dh), opacity, BitmapInterpolationMode.Linear, null);
                    break;
                }
            }
        }
        catch (Exception ex) { Log($"watermark draw FAILED {path}: {ex.Message}"); }
        finally { _rt.PopAxisAlignedClip(); }
    }

    /// <summary>Background: decode a watermark image file to premultiplied BGRA, enqueue for UI upload.</summary>
    private void DecodeBackgroundAsync(string path)
    {
        try
        {
            using var gdi = new System.Drawing.Bitmap(path); // PNG/JPG/BMP/GIF via GDI+
            int w = gdi.Width, h = gdi.Height;
            var data = gdi.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            byte[] buf;
            try
            {
                buf = new byte[w * 4 * h];
                if (data.Stride == w * 4) Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                else for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * w * 4, w * 4);
            }
            finally { gdi.UnlockBits(data); }
            _bgDecoded.Enqueue((path, buf, w, h));
        }
        catch (Exception ex)
        {
            Log($"watermark decode FAILED {path}: {ex.Message}");
            _bgDecoded.Enqueue((path, null, 0, 0)); // stop retrying
        }
        finally { RequestRedraw(); }
    }

    /// <summary>Drop a cached watermark bitmap (after clear/replace) so the file handle/texture is freed.</summary>
    private void EvictWatermark(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (_bgCache.Remove(path!, out var bmp)) { try { bmp.Dispose(); } catch { } }
        _bgDecoding.Remove(path!);
    }

    /// <summary>Background: decode to premultiplied BGRA pixels (no D2D), enqueue for UI upload, ask for a redraw.</summary>
    private void DecodePixelsAsync(KittyImage img)
    {
        try
        {
            var (bgra, w, h) = DecodePixels(img);
            _decoded.Enqueue((img, bgra, w, h));
        }
        catch (Exception ex)
        {
            Log($"decode FAILED id={img.Id} fmt={img.Format}: {ex.GetType().Name} {ex.Message}");
            _decoded.Enqueue((img, null, 0, 0)); // signal failure so we stop retrying
        }
        finally { RequestRedraw(); }
    }

    /// <summary>Thread-safe decode (PNG via System.Drawing, or raw RGB/RGBA) to a premultiplied-BGRA buffer.</summary>
    private static (byte[] bgra, int w, int h) DecodePixels(KittyImage img)
    {
        if (img.Format == KittyFormat.Png)
        {
            using var ms = new MemoryStream(img.Data);
            using var gdi = new System.Drawing.Bitmap(ms);
            int w = gdi.Width, h = gdi.Height;
            var data = gdi.LockBits(
                new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb); // premultiplied BGRA, stride == w*4 for 32bpp
            try
            {
                var buf = new byte[w * 4 * h];
                if (data.Stride == w * 4)
                    Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                else // defensive: copy row by row if padded
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * w * 4, w * 4);
                return (buf, w, h);
            }
            finally { gdi.UnlockBits(data); }
        }

        return (ToPremultipliedBgra(img), img.Width, img.Height);
    }

    private static byte[] ToPremultipliedBgra(KittyImage img)
    {
        var d = img.Data;
        var outb = new byte[img.Width * img.Height * 4];
        if (img.Format == KittyFormat.Rgba)
        {
            for (int i = 0, j = 0; i + 3 < d.Length && j + 3 < outb.Length; i += 4, j += 4)
            {
                byte r = d[i], g = d[i + 1], b = d[i + 2], a = d[i + 3];
                outb[j] = (byte)(b * a / 255); outb[j + 1] = (byte)(g * a / 255);
                outb[j + 2] = (byte)(r * a / 255); outb[j + 3] = a;
            }
        }
        else // Rgb: opaque
        {
            for (int i = 0, j = 0; i + 2 < d.Length && j + 3 < outb.Length; i += 3, j += 4)
            {
                outb[j] = d[i + 2]; outb[j + 1] = d[i + 1]; outb[j + 2] = d[i]; outb[j + 3] = 255;
            }
        }
        return outb;
    }

    /// <summary>Dispose the device-bound render target + textures and rebuild them (after a device/GPU reset).</summary>
    private void RecreateTarget()
    {
        foreach (var b in _imageCache.Values) { try { b.Dispose(); } catch { } }
        _imageCache.Clear();
        _decoding.Clear();
        while (_decoded.TryDequeue(out _)) { }
        foreach (var b in _bgCache.Values) { try { b.Dispose(); } catch { } } // watermark textures are device-bound too
        _bgCache.Clear();
        _bgDecoding.Clear();
        while (_bgDecoded.TryDequeue(out _)) { }
        try { _brush?.Dispose(); } catch { }
        try { _rt?.Dispose(); } catch { }
        _brush = null; _rt = null;
        try { CreateRenderTarget(); } catch (Exception ex) { Perf($"recreate FAILED: {ex.Message}"); }
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    // D2DERR_RECREATE_TARGET: the device is lost and every device-bound resource must be rebuilt.
    private const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

    private void Render()
    {
        if (_rt is null || _brush is null) return;
        long tStart = Stopwatch.GetTimestamp();
        _uploadCount = 0; _uploadMs = 0;
        try
        {
            RenderBody();
            Result end = _rt.EndDraw(out _, out _);
            if (end.Failure)
            {
                if (end.Code == D2DERR_RECREATE_TARGET) { RecreateTarget(); return; }
                Perf($"EndDraw fail 0x{end.Code:X8}");
            }
        }
        catch (SharpGenException ex)
        {
            // Device loss can surface as an exception from any draw call; rebuild and move on.
            if (ex.ResultCode.Code == D2DERR_RECREATE_TARGET) { RecreateTarget(); return; }
            Perf($"render ex {ex.ResultCode.Code:X8}: {ex.Message}");
            RecreateTarget();
            return;
        }
        catch (Exception ex) { Perf($"render ex: {ex.GetType().Name} {ex.Message}"); RecreateTarget(); return; }

        if (_perfLog is not null)
            Perf($"frame render={Stopwatch.GetElapsedTime(tStart).TotalMilliseconds:F2}ms uploads={_uploadCount} uploadMs={_uploadMs:F2} t={DateTime.Now:HH:mm:ss.fff}");
    }

    /// <summary>Draw one terminal surface (cells, images, cursor) at origin (ox,oy) with its font metrics.</summary>
    private void RenderTerminal(TerminalSession session, float ox, float oy, IDWriteTextFormat fmt, float cw, float ch, int scrollOffset, Pane? selPane = null)
    {
        var rt = _rt!;
        var brush = _brush!;
        lock (session.SyncRoot)
        {
            var em = session.Emulator;
            var screen = em.Screen;
            int cols = screen.Cols, rows = screen.Rows;
            int hist = em.HistoryCount;
            int off = em.IsAltScreen ? 0 : Math.Clamp(scrollOffset, 0, hist);
            bool hasSel = selPane is { HasSel: true };
            int sl0 = 0, sc0 = 0, sl1 = 0, sc1 = 0;
            if (hasSel) NormSel(selPane!, out sl0, out sc0, out sl1, out sc1);
            bool searchHere = _searchActive && selPane is not null && ReferenceEquals(selPane, ActiveSurface());
            // Visible row r maps into [history ++ live grid], shifted up by `off`.
            Cell CellAt(int r, int c)
            {
                if (off <= 0) return screen[r, c];
                int vi = hist - off + r;
                if (vi < 0) return Cell.Empty;
                if (vi < hist) return em.GetHistoryCell(vi, c);
                int live = vi - hist;
                return live < rows ? screen[live, c] : Cell.Empty;
            }
            for (int r = 0; r < rows; r++)
            {
                float y = oy + r * ch;
                int c = 0;
                while (c < cols)  // Pass 1: coalesced background fills
                {
                    Cell cell = CellAt(r, c);
                    Color bg = EffectiveBg(cell);
                    if (bg == _theme.DefaultBackground) { c++; continue; }
                    int start = c;
                    while (c < cols)
                    {
                        Cell cc = CellAt(r, c);
                        if (cc.Width != 0 && EffectiveBg(cc) != bg) break;
                        c++;
                    }
                    brush.Color = C4(bg);
                    rt.FillRectangle(new Rect(ox + start * cw, y, (c - start) * cw, ch), brush);
                }
                int abs = hist - off + r;
                if (hasSel && abs >= sl0 && abs <= sl1)  // selection highlight (behind the text)
                {
                    int from = Math.Clamp(abs == sl0 ? sc0 : 0, 0, cols - 1);
                    int to = Math.Clamp(abs == sl1 ? sc1 : cols - 1, 0, cols - 1);
                    if (to >= from)
                    {
                        brush.Color = new Color4(40 / 255f, 90 / 255f, 150 / 255f, 1f);
                        rt.FillRectangle(new Rect(ox + from * cw, y, (to - from + 1) * cw, ch), brush);
                    }
                }
                if (searchHere)  // search-match highlight (amber; current match brighter)
                {
                    for (int mi = 0; mi < _searchMatches.Count; mi++)
                    {
                        var m = _searchMatches[mi];
                        if (m.Line != abs) continue;
                        int from = Math.Clamp(m.Col0, 0, cols - 1), to = Math.Clamp(m.Col1, 0, cols - 1);
                        brush.Color = mi == _searchCur ? new Color4(235 / 255f, 175 / 255f, 45 / 255f, 1f)
                                                       : new Color4(150 / 255f, 110 / 255f, 25 / 255f, 1f);
                        rt.FillRectangle(new Rect(ox + from * cw, y, (to - from + 1) * cw, ch), brush);
                    }
                }
                c = 0;
                while (c < cols)  // Pass 2: coalesced same-colour text runs
                {
                    Cell cell = CellAt(r, c);
                    if (cell.Width == 0 || cell.Rune == ' ' || cell.Rune == '\0') { c++; continue; }
                    Color runFg = EffectiveFg(cell);
                    if (cell.Width == 2)
                    {
                        brush.Color = C4(runFg);
                        float wx = ox + c * cw;
                        rt.DrawText(cell.Rune.ToString(), fmt, new Rect(wx, y, wx + 2 * cw, y + ch), brush);
                        c++;
                        continue;
                    }
                    int start = c;
                    _run.Clear();
                    int lastNonBlank = 0;
                    while (c < cols)
                    {
                        Cell cc = CellAt(r, c);
                        if (cc.Width == 2 || cc.Width == 0) break;
                        bool blank = cc.Rune == ' ' || cc.Rune == '\0';
                        if (!blank && EffectiveFg(cc) != runFg) break;
                        _run.Append(blank ? ' ' : cc.Rune);
                        c++;
                        if (!blank) lastNonBlank = _run.Length;
                    }
                    if (lastNonBlank > 0)
                    {
                        _run.Length = lastNonBlank;
                        brush.Color = C4(runFg);
                        float rx = ox + start * cw;
                        rt.DrawText(_run.ToString(), fmt, new Rect(rx, y, rx + _run.Length * cw, y + ch), brush);
                    }
                }
            }

            if (!_noImages && em.Placements.Count > 0 && off == 0)
                DrawImages(em, ox, oy, cw, ch);

            if (off > 0) // scrollback position indicator on the pane's right edge
            {
                float trackX = ox + cols * cw - 3f, trackH = rows * ch, total = hist + rows;
                float thumbH = MathF.Max(12f, trackH * rows / total);
                float thumbY = oy + (trackH - thumbH) * (hist - off) / MathF.Max(1, hist);
                brush.Color = WithA(ChromeText, 0.10f);
                rt.FillRectangle(new Rect(trackX, oy, 3f, trackH), brush);
                brush.Color = WithA(ChromeText, 0.35f);
                rt.FillRectangle(new Rect(trackX, thumbY, 3f, thumbH), brush);
            }

            if (em.CursorVisible && off == 0)
            {
                float cx = ox + em.CursorCol * cw, cy = oy + em.CursorRow * ch;
                brush.Color = C4(_theme.Cursor);
                if (!_config.CursorBlink || _cursorOn)
                {
                    switch (_config.CursorStyle)
                    {
                        case CursorStyle.Block: rt.FillRectangle(new Rect(cx, cy, cw, ch), brush); break;
                        case CursorStyle.Underline:
                            float uh = MathF.Max(1f, MathF.Round(ch * 0.12f));
                            rt.FillRectangle(new Rect(cx, cy + ch - uh, cw, uh), brush); break;
                        default:
                            float barW = MathF.Max(1f, MathF.Round(cw * 0.14f));
                            rt.FillRectangle(new Rect(cx, cy, barW, ch), brush); break;
                    }
                }
            }
        }
    }

    private void RenderBody()
    {
        var rt = _rt!;
        var brush = _brush!;
        rt.BeginDraw();
        rt.Clear(C4(_theme.DefaultBackground));

        // Quick terminal (kind 2) and a sized floating overlay (kind 3) both render as a centered
        // panel over the live main window — a "tool window" look. Scratch (1) / full overlay fill.
        bool floatingPanel = _cover is not null && ((_coverKind == 3 && _ovlOwner is { OverlaySizePercent: > 0 }) || _coverKind == 2);
        if (_cover is not null && !floatingPanel)
        {
            var (ox, oy, cw0, _) = ContentArea();
            var (fmt, cw, ch) = Metrics(_cover.FontSize);
            RenderTerminal(_cover.S, ox, oy, fmt, cw, ch, _cover.ScrollOffset, _cover);
            DrawCoverBadge(rt, brush, ox + cw0, oy);
            DrawOverlayFooter(rt, brush);
        }
        else
        {
            if (_active is not null) RenderPanes(rt, brush, _active);   // the main window shows behind
            if (floatingPanel)
            {
                var (cx0, cy0, cw0, ch0) = ContentArea();
                brush.Color = new Color4(0f, 0f, 0f, 0.45f);                 // dim scrim over the session
                rt.FillRectangle(new Rect(cx0, cy0, cw0, ch0), brush);
                var (fx, fy, fw, fh) = CoverRect();
                brush.Color = C4(_theme.DefaultBackground);                  // opaque panel
                rt.FillRectangle(new Rect(fx, fy, fw, fh), brush);
                var (fmt, cw, ch) = Metrics(_cover!.FontSize);
                RenderTerminal(_cover.S, fx, fy, fmt, cw, ch, _cover.ScrollOffset, _cover);
                brush.Color = ChromeAccent;                                  // 1px frame
                rt.DrawRectangle(new Rect(fx - 1f, fy - 1f, fw + 2f, fh + 2f), brush, 1f);
                DrawCoverBadge(rt, brush, fx + fw, fy);
                if (_coverKind == 3) DrawOverlayFooter(rt, brush);          // overlay-only footer
            }
        }
        DrawSidebar(rt, brush);
        DrawTitleBar(rt, brush);
        DrawSearchBar(rt, brush);
        DrawToast(rt, brush);
        DrawLeaderHint(rt, brush);
        DrawPalette(rt, brush);
        DrawSettingsPanel(rt, brush);
        DrawSwitcher(rt, brush);
    }

    /// <summary>The Ctrl+Tab switcher HUD: a centered panel listing sessions in recency order with the
    /// current walk target highlighted. Painted only while a walk is in progress; never takes focus.</summary>
    private void DrawSwitcher(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (!_mruWalking || _mruSnapshot.Count < 2) return;
        int cw = ClientW(), ch = ClientH();
        brush.Color = PalScrim;
        rt.FillRectangle(new Rect(0, 0, cw, ch), brush);

        const float rowH = 36f, headH = 30f;
        int n = _mruSnapshot.Count;
        float pw = MathF.Min(460f, cw - 80f);
        float ph = headH + n * rowH + 10f;
        float px = (cw - pw) / 2f, py = MathF.Max(TitleBarH + 16f, (ch - ph) / 2f);
        var panel = new Rect(px, py, pw, ph);

        brush.Color = PalBg;
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = panel, RadiusX = 10f, RadiusY = 10f }, brush);
        brush.Color = PalBorder;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = panel, RadiusX = 10f, RadiusY = 10f }, brush, 1f);

        brush.Color = ChromeDim;
        rt.DrawText("Recent sessions — hold Ctrl, Tab to cycle", _uiSmall, new Rect(px + 16f, py + 8f, pw - 32f, 18f), brush);

        for (int i = 0; i < n; i++)
        {
            var s = _mruSnapshot[i];
            float ry = py + headH + i * rowH;
            if (i == _mruIdx) { brush.Color = PalSel; rt.FillRectangle(new Rect(px + 4f, ry, pw - 8f, rowH), brush); }
            brush.Color = StatusDot(s.ActivePane.S.Status);
            rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(px + 18f, ry + rowH / 2f), 4.5f, 4.5f), brush);
            brush.Color = i == _mruIdx ? SbActiveText : ChromeText;
            rt.DrawText(s.Name, _uiFont, new Rect(px + 32f, ry + (rowH - 20f) / 2f, pw - 150f, 20f), brush);
            brush.Color = ChromeDim;
            float wsw = MeasureText(s.Ws.Name, _uiSmall);
            rt.DrawText(s.Ws.Name, _uiSmall, new Rect(px + pw - 16f - wsw, ry + (rowH - 16f) / 2f, wsw + 2f, 16f), brush);
        }
    }

    /// <summary>Render a session's pane grid (terminals + split dividers + focused-pane accent).</summary>
    private void RenderPanes(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, Ses ses)
    {
        var layout = PaneLayout(ses);
        foreach (var (pane, ox, oy, pw, ph) in layout)
        {
            var (fmt, cw, ch) = Metrics(pane.FontSize);
            DrawWatermark(ses, ox, oy, pw, ph);   // faint session background, behind the cells
            RenderTerminal(pane.S, ox, oy, fmt, cw, ch, pane.ScrollOffset, pane);
            // Dim non-active panes in a split so the focused one stands out.
            if (layout.Count > 1 && !ReferenceEquals(pane, ses.ActivePane) && _config.InactivePaneDim > 0)
            {
                brush.Color = WithA(new Color4(0f, 0f, 0f, 1f), System.Math.Clamp(_config.InactivePaneDim, 0, 100) / 100f * 0.9f);
                rt.FillRectangle(new Rect(ox, oy, pw, ph), brush);
            }
        }
        if (layout.Count > 1)
        {
            for (int i = 0; i < layout.Count - 1; i++)
            {
                float dx = layout[i].x + layout[i].w + DividerW / 2f;
                brush.Color = ChromeBorder;
                rt.FillRectangle(new Rect(dx - 0.5f, layout[i].y, 1f, layout[i].h), brush);
            }
            var ap = layout.First(l => ReferenceEquals(l.pane, ses.ActivePane));
            brush.Color = ChromeAccent;
            rt.FillRectangle(new Rect(ap.x, TitleBarH + 1f, ap.w, 2f), brush); // accent marks the focused pane
        }
    }

    /// <summary>Corner badge naming the current cover (scratch / quick / overlay); rightX/topY = cover top-right.</summary>
    private void DrawCoverBadge(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float rightX, float topY)
    {
        string badge = _coverKind switch { 1 => "scratch", 2 => "quick", 3 => "overlay", _ => "" };
        if (badge.Length == 0) return;
        float bw = MeasureText(badge, _uiSmall) + 16f;
        brush.Color = WithA(SbHighlight, 0.92f);
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(rightX - bw - 6f, topY + 4f, bw, 20f), RadiusX = 5f, RadiusY = 5f }, brush);
        brush.Color = ChromeText;
        rt.DrawText(badge, _uiSmall, new Rect(rightX - bw + 2f, topY + 4f, bw - 8f, 20f), brush);
    }

    /// <summary>When a --wait overlay's program has exited, a footer banner in the cover inviting a key to close.</summary>
    private void DrawOverlayFooter(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (_coverKind != 3 || _ovlOwner is not { OverlayExited: true }) return;
        var (fx, fy, fw, fh) = CoverRect();
        string msg = $"  exited ({_overlayExitCode}) — press any key to close  ";
        float bh = 22f;
        brush.Color = WithA(ChromeAccent, 0.95f);
        rt.FillRectangle(new Rect(fx, fy + fh - bh, fw, bh), brush);
        brush.Color = new Color4(1f, 1f, 1f, 1f);
        rt.DrawText(msg, _uiSmall, new Rect(fx + 4f, fy + fh - bh, fw - 8f, bh), brush);
    }

    /// <summary>Find bar (top-right of the content region) shown while search is active.</summary>
    private void DrawSearchBar(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (!_searchActive) return;
        float barW = 340f, barH = 30f;
        float x = Math.Max(_sidebarW + 8f, ClientW() - barW - 12f), y = TitleBarH + 8f;
        var rr = new RoundedRectangle { Rect = new Rect(x, y, barW, barH), RadiusX = 6f, RadiusY = 6f };
        brush.Color = PalBg;
        rt.FillRoundedRectangle(rr, brush);
        brush.Color = ChromeAccent;
        rt.DrawRoundedRectangle(rr, brush, 1f);
        brush.Color = ChromeDim;
        rt.DrawText("", _iconFont, new Rect(x + 6f, y, 22f, barH), brush); // magnifier
        bool empty = _searchQuery.Length == 0;
        brush.Color = empty ? ChromeDim : ChromeText;
        rt.DrawText(empty ? "Find" : _searchQuery + "▏", _uiFont, new Rect(x + 30f, y, barW - 116f, barH), brush);
        string status = SearchStatus();
        if (status.Length > 0)
        {
            brush.Color = ChromeDim;
            rt.DrawText(status, _uiSmall, new Rect(x + barW - 82f, y, 76f, barH), brush);
        }
    }

    // ---- Sidebar (custom Direct2D outline: workspaces -> sessions) ----

    // Chrome colours are DERIVED from the active theme (see RecomputeChrome), so a theme switch
    // recolours the whole window, not just the terminal cells. Defaults match the old dark chrome.
    private Color4 SbBg = new(0.055f, 0.063f, 0.071f, 1f);
    private Color4 SbHighlight = new(0.16f, 0.20f, 0.25f, 1f);
    private Color4 SbHeaderText = new(0.75f, 0.78f, 0.82f, 1f);
    private Color4 SbActiveText = new(1f, 1f, 1f, 1f);
    private Color4 SbDimText = new(0.60f, 0.63f, 0.67f, 1f);

    // Status-glyph colors are user-configurable (Agent Status settings tab); defaults match the originals.
    private Color4 StatusDot(AgentStatus s) => s switch
    {
        AgentStatus.Active => HexColor4(_config.StatusColorActive, new(60 / 255f, 140 / 255f, 255 / 255f, 1f)),
        AgentStatus.Blocked => HexColor4(_config.StatusColorBlocked, new(240 / 255f, 160 / 255f, 40 / 255f, 1f)),
        AgentStatus.Completed => HexColor4(_config.StatusColorCompleted, new(60 / 255f, 200 / 255f, 90 / 255f, 1f)),
        _ => new(90 / 255f, 96 / 255f, 102 / 255f, 1f),
    };

    /// <summary>Parse "#RRGGBB" (or "#RGB") to a Color4; returns <paramref name="fallback"/> on any failure.</summary>
    private static Color4 HexColor4(string hex, Color4 fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length < 6) return fallback;
        try { return new Color4(Convert.ToInt32(h[..2], 16) / 255f, Convert.ToInt32(h.Substring(2, 2), 16) / 255f, Convert.ToInt32(h.Substring(4, 2), 16) / 255f, 1f); }
        catch { return fallback; }
    }

    private void DrawSidebar(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        _sidebarRows.Clear();
        _footerButtons.Clear();
        if (_sidebarW <= 0) return;

        brush.Color = SbBg;
        rt.FillRectangle(new Rect(0, TitleBarH, _sidebarW, ClientH()), brush);

        float rowsBottom = ClientH() - FooterH; // stop the list above the footer toolbar
        float rowH = _cellH + 8f;
        float y = TitleBarH + PadY;

        if (_sidebarMode == SidebarMode.Flagged) { DrawFlaggedList(rt, brush, ref y, rowH, rowsBottom); }
        else { DrawTreeList(rt, brush, ref y, rowH, rowsBottom); }

        if (_dragging && _dragItem is not null) DrawDropIndicator(rt, brush);
        DrawSidebarFooter(rt, brush);
    }

    /// <summary>Tree mode: the workspace→session outline (or, when a workspace is focused, only that one + a "show all" banner).</summary>
    private void DrawTreeList(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, ref float y, float rowH, float rowsBottom)
    {
        List<Workspace> wss;
        lock (_workspaces) wss = _workspaces.ToList();

        Workspace? focused = _focusedWorkspaceId is null ? null : wss.FirstOrDefault(w => w.Id == _focusedWorkspaceId);
        if (focused is not null)
        {
            // "Focused" banner: a full-width accent strip; clicking it clears the focus (back to all workspaces).
            brush.Color = Mix(SbBg, ChromeAccent, 0.32f);
            rt.FillRectangle(new Rect(0, y, _sidebarW, rowH), brush);
            brush.Color = Mix(ChromeAccent, new Color4(1f, 1f, 1f, 1f), 0.35f);
            rt.DrawText("‹  Focused — show all", _uiSmall, new Rect(12f, y, _sidebarW - 20f, rowH), brush);
            _sidebarRows.Add((y, y + rowH, false, _showAllMarker));
            y += rowH;
            wss = new List<Workspace> { focused };
        }

        foreach (var ws in wss)
        {
            if (y + rowH > rowsBottom) break;
            List<Ses> sessions;
            bool expanded;
            lock (_workspaces) { sessions = ws.Sessions.ToList(); expanded = ws.Expanded; }

            brush.Color = SbHeaderText;
            rt.DrawText(expanded ? "▾" : "▸", _format, TextRect(6f, y, 18f, rowH), brush); // chevron (mono, top-aligned)
            if (!ReferenceEquals(_editing, ws)) // the rename box covers the name while editing
                rt.DrawText(ws.Name, _uiFont, new Rect(24f, y, _sidebarW - 48f, rowH), brush);
            rt.DrawText(sessions.Count.ToString(), _uiSmall, new Rect(_sidebarW - 28f, y, 22f, rowH), brush);
            _sidebarRows.Add((y, y + rowH, true, ws));
            y += rowH;

            if (!expanded) continue;
            foreach (var s in sessions)
            {
                if (y + rowH > rowsBottom) break;
                DrawSessionRow(rt, brush, s, y, rowH);
                y += rowH;
            }
        }
    }

    /// <summary>Flagged mode: a flat working-set of every flagged session across all workspaces (no headers).</summary>
    private void DrawFlaggedList(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, ref float y, float rowH, float rowsBottom)
    {
        brush.Color = SbHeaderText;
        rt.DrawText("FLAGGED", _uiSmall, new Rect(12f, y, _sidebarW - 20f, rowH), brush);
        y += rowH;

        var flagged = AllSessions().Where(s => s.Flagged).ToList();
        if (flagged.Count == 0)
        {
            brush.Color = SbDimText;
            rt.DrawText("No flagged sessions", _uiSmall, new Rect(16f, y, _sidebarW - 24f, rowH), brush); y += rowH;
            if (y + rowH <= rowsBottom) rt.DrawText("Ctrl+Shift+F flags the active one", _uiSmall, new Rect(16f, y, _sidebarW - 24f, rowH), brush);
            return;
        }
        foreach (var s in flagged)
        {
            if (y + rowH > rowsBottom) break;
            DrawSessionRow(rt, brush, s, y, rowH);
            y += rowH;
        }
    }

    /// <summary>Draw one session row (shared by tree + flagged modes): highlight, flag marker, name, unread badge, status dot.</summary>
    private void DrawSessionRow(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, Ses s, float y, float rowH)
    {
        bool active = ReferenceEquals(_active, s);
        if (active)
        {
            brush.Color = SbHighlight;
            rt.FillRectangle(new Rect(0, y, _sidebarW, rowH), brush);
        }
        // Flag marker in the left gutter (before the name, which starts at x=26).
        if (s.Flagged)
        {
            brush.Color = new Color4(0.96f, 0.76f, 0.26f, 1f); // amber flag
            rt.DrawText("", _iconSmall, new Rect(6f, y, 18f, rowH), brush);
        }
        bool isDrag = _dragging && ReferenceEquals(s, _dragItem);
        brush.Color = isDrag ? new Color4(0.5f, 0.53f, 0.57f, 0.45f) : (active ? SbActiveText : SbDimText);
        if (!ReferenceEquals(_editing, s)) // the rename box covers the name while editing
            rt.DrawText(s.Name, _uiFont, new Rect(26f, y, _sidebarW - 26f - 22f, rowH), brush);
        // Unread-notification count badge, just left of the status circle (can be hidden; the count still tracks).
        int unread = UnreadOf(s);
        if (unread > 0 && _config.NotificationBadges)
        {
            string bn = unread > 99 ? "99+" : unread.ToString();
            float bw = MeasureText(bn, _uiSmall) + 10f, bx = _sidebarW - 30f - bw;
            brush.Color = new Color4(0.90f, 0.30f, 0.24f, 1f); // notification red pill
            rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(bx, y + rowH / 2f - 8f, bw, 16f), RadiusX = 8f, RadiusY = 8f }, brush);
            brush.Color = new Color4(1f, 1f, 1f, 1f);
            rt.DrawText(bn, _uiSmall, new Rect(bx + 5f, y + rowH / 2f - 8f, bw - 8f, 16f), brush);
        }
        // Status circle right-aligned in the row (agterm layout); pulse it if blink was requested.
        var dot = StatusDot(s.S.Status);
        if (s.S.Blink && !_cursorOn) dot = new Color4(dot.R, dot.G, dot.B, 0.22f);
        brush.Color = dot;
        rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(_sidebarW - 16f, y + rowH / 2f), 4.5f, 4.5f), brush);
        _sidebarRows.Add((y, y + rowH, false, s));
    }

    /// <summary>Sidebar footer (agterm layout): new-workspace, add-session menu | spacer | focus pill, flag toggle.</summary>
    private void DrawSidebarFooter(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        float y = ClientH() - FooterH;
        brush.Color = ChromeBg;
        rt.FillRectangle(new Rect(0, y, _sidebarW, FooterH), brush);

        float bwid = 34f, bx = 6f;
        // New Workspace (vector card + plus)
        {
            var c = ChromeBtnBg(rt, brush, bx, y, bwid, FooterH, "new-workspace", _footerButtons, ChromeDim);
            DrawNewWorkspaceGlyph(rt, brush, bx + bwid / 2f, y + FooterH / 2f, c);
        }
        bx += bwid;
        // Add Session (opens a menu: New Session / Open Directory…)
        {
            var c = ChromeBtnBg(rt, brush, bx, y, bwid, FooterH, "add-session", _footerButtons, ChromeDim);
            brush.Color = c;
            rt.DrawText(GlyphAdd, _iconFont, new Rect(bx, y, bwid, FooterH), brush);
        }

        // Flag / flagged-view toggle (far right)
        float fx = _sidebarW - bwid - 4f;
        bool flagged = _sidebarMode == SidebarMode.Flagged;
        {
            var baseC = flagged ? new Color4(0.96f, 0.76f, 0.26f, 1f) : ChromeDim; // amber when active
            var c = ChromeBtnBg(rt, brush, fx, y, bwid, FooterH, "flag", _footerButtons, baseC);
            DrawFlagGlyph(rt, brush, fx + bwid / 2f, y + FooterH / 2f, c, flagged);
        }

        // Focus pill (only when a workspace is focused): shows its name + ✕, click clears focus.
        if (_focusedWorkspaceId is not null)
        {
            Workspace? fw; lock (_workspaces) fw = _workspaces.FirstOrDefault(w => w.Id == _focusedWorkspaceId);
            if (fw is not null)
            {
                string label = fw.Name.Length > 12 ? fw.Name[..12] + "…" : fw.Name;
                float tw = MeasureText(label, _uiSmall);
                float pw = tw + 34f, ph = 20f;
                float px = MathF.Max(bx + bwid + 6f, fx - pw - 8f), py = y + (FooterH - ph) / 2f;
                var c = ChromeBtnBg(rt, brush, px, py - (FooterH - ph) / 2f + 0f, pw, FooterH, "unfocus", _footerButtons, ChromeDim);
                brush.Color = WithA(ChromeAccent, 0.22f);
                rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(px, py, pw, ph), RadiusX = 10f, RadiusY = 10f }, brush);
                brush.Color = c;
                rt.DrawText(label, _uiSmall, new Rect(px + 10f, py, tw + 4f, ph), brush);
                rt.DrawText(GlyphClose, _iconFont, new Rect(px + pw - 20f, py, 18f, ph), brush); // ChromeClose (✕)
            }
        }
    }

    // DrawText layout rect is Left/Top/Right/Bottom; vertically centre the glyph cell in the row.
    private Rect TextRect(float x, float y, float w, float rowH)
        => new(x, y + (rowH - _cellH) / 2f, x + w, y + (rowH + _cellH) / 2f);

    private void SidebarClick(int mx, int my)
    {
        foreach (var (y0, y1, isWs, item) in _sidebarRows)
        {
            if (my < y0 || my >= y1) continue;
            if (ReferenceEquals(item, _showAllMarker)) { _focusedWorkspaceId = null; RequestRedraw(); SaveState(); }
            else if (isWs && item is Workspace ws) { lock (_workspaces) ws.Expanded = !ws.Expanded; RequestRedraw(); SaveState(); }
            else if (!isWs && item is Ses s) SetActive(s);
            return;
        }
    }

    private object? RowAt(int my)
    {
        foreach (var (y0, y1, _, item) in _sidebarRows) if (my >= y0 && my < y1) return item;
        return null;
    }

    private static void GetCursorScreen(out int x, out int y) { GetCursorPos(out POINT p); x = p.x; y = p.y; }

    // ---- Inline rename (native child EDIT overlaid on the sidebar row) ----

    private void StartRename(object item)
    {
        if (item is not (Ses or Workspace)) return; // e.g. the "show all" focus banner isn't renamable
        if (_editHwnd != IntPtr.Zero) CommitRename();
        bool isWs = item is Workspace;
        float ry0 = -1, ry1 = -1;
        foreach (var (y0, y1, _, it) in _sidebarRows) if (ReferenceEquals(it, item)) { ry0 = y0; ry1 = y1; break; }
        if (ry0 < 0) { RequestRedraw(); return; } // row not currently visible
        string name = item is Ses s ? s.Name : ((Workspace)item).Name;
        EnsureEditGdi();
        // Fill the whole row (matches the highlight band); a left text-margin puts the text exactly
        // where the row name is drawn, so nothing shifts when editing starts.
        // The single-line EDIT centres its text ~1px higher and its glyph sits ~1px right of the
        // margin vs DirectWrite; offsets tuned by pixel-measuring the box against an unedited row.
        int leftMargin = isWs ? 23 : 25;
        int ey = (int)ry0 + 4, eh = (int)(ry1 - ry0);
        _editHwnd = CreateWindowExW(0, "EDIT", name, WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
            0, ey, (int)_sidebarW, eh, _hwnd, (IntPtr)EDIT_ID, GetModuleHandleW(null), IntPtr.Zero);
        if (_editHwnd == IntPtr.Zero) return;
        SendMessageW(_editHwnd, WM_SETFONT, _editFont, (IntPtr)1);
        SendMessageW(_editHwnd, EM_SETMARGINS, (IntPtr)(EC_LEFTMARGIN | EC_RIGHTMARGIN), (IntPtr)(leftMargin | (8 << 16)));
        SendMessageW(_editHwnd, (uint)EM_SETSEL, IntPtr.Zero, (IntPtr)(-1)); // select all
        SetFocus(_editHwnd);
        _editProc = EditProc; // keep alive
        _editOrigProc = SetWindowLongPtrW(_editHwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_editProc));
        _editing = item;
        RequestRedraw();
    }

    // Subclass of the EDIT: commit on Enter, cancel on Escape, and swallow those chars (no beep).
    private static IntPtr EditProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var w = Frontmost; // the rename EDIT belongs to the frontmost window
        if (msg == WM_KEYDOWN)
        {
            if ((int)wParam == VK_RETURN) { w.CommitRename(); return IntPtr.Zero; }
            if ((int)wParam == VK_ESCAPE) { w.CancelRename(); return IntPtr.Zero; }
        }
        else if (msg == WM_CHAR)
        {
            int c = (int)wParam;
            if (c == VK_RETURN || c == VK_ESCAPE || c == '\t') return IntPtr.Zero;
        }
        return CallWindowProcW(w._editOrigProc, hwnd, msg, wParam, lParam);
    }

    private void CommitRename()
    {
        if (_editHwnd == IntPtr.Zero) return;
        var h = _editHwnd; var item = _editing;
        _editHwnd = IntPtr.Zero; _editing = null; // clear first: EN_KILLFOCUS during destroy is then a no-op
        var sb = new StringBuilder(256);
        GetWindowTextW(h, sb, 256);
        string name = sb.ToString().Trim();
        if (name.Length > 0)
        {
            if (item is Ses s) { s.Name = name; s.CustomName = name; } // CustomName drives the title bar
            else if (item is Workspace w) w.Name = name;
        }
        DestroyEditWindow(h);
        RequestRedraw();
        SaveState();
    }

    private void CancelRename()
    {
        if (_editHwnd == IntPtr.Zero) return;
        var h = _editHwnd; _editHwnd = IntPtr.Zero; _editing = null;
        DestroyEditWindow(h);
        RequestRedraw();
    }

    private void DestroyEditWindow(IntPtr h)
    {
        if (_editOrigProc != IntPtr.Zero) { SetWindowLongPtrW(h, GWLP_WNDPROC, _editOrigProc); _editOrigProc = IntPtr.Zero; }
        DestroyWindow(h);
        SetFocus(_hwnd);
    }

    // ---- Context menus (native Win32 popup) ----

    private const uint IDM_NEW_SESSION = 1, IDM_OPEN_DIR = 2, IDM_RENAME = 3, IDM_CLOSE = 4, IDM_CLEAR = 5, IDM_DELETE_WS = 6, IDM_FLAG = 7, IDM_FOCUS_WS = 8;
    private const uint IDM_MOVE_BASE = 1000;
    private const uint IDM_PROFILE_BASE = 2000;   // "New Session ▸ <profile>" items: IDM_PROFILE_BASE + index

    /// <summary>A popup menu listing the shell profiles (item id = IDM_PROFILE_BASE + index).</summary>
    private static IntPtr BuildProfilesMenu()
    {
        IntPtr m = CreatePopupMenu();
        var profs = _profileCfg.Profiles;
        for (int i = 0; i < profs.Count; i++) AppendMenuW(m, MF_STRING, (UIntPtr)(IDM_PROFILE_BASE + (uint)i), profs[i].Name);
        return m;
    }

    /// <summary>If cmd is a profile item, create a session with that profile in <paramref name="ws"/>. Returns true if handled.</summary>
    private bool TryCreateSessionForProfileCmd(int cmd, Workspace ws, string? cwd)
    {
        int idx = cmd - (int)IDM_PROFILE_BASE;
        if (idx < 0 || idx >= _profileCfg.Profiles.Count) return false;
        CreateSession(Guid.NewGuid().ToString(), null, cwd, ws, makeActive: true, profileName: _profileCfg.Profiles[idx].Name);
        return true;
    }

    private void ShowContextMenu(object item, int sx, int sy)
    {
        IntPtr menu = CreatePopupMenu();
        if (item is Ses ses)
        {
            if (_profileCfg.Profiles.Count > 1) AppendMenuW(menu, MF_POPUP, (UIntPtr)(ulong)BuildProfilesMenu(), "New Session");
            else AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_NEW_SESSION, "New Session");
            AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_OPEN_DIR, "Open Directory…");
            AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_RENAME, "Rename");
            AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_FLAG, ses.Flagged ? "Unflag Session" : "Flag Session");
            List<Workspace> targets;
            lock (_workspaces) targets = _workspaces.Where(w => !ReferenceEquals(w, ses.Ws)).ToList();
            IntPtr sub = CreatePopupMenu();
            for (int i = 0; i < targets.Count; i++) AppendMenuW(sub, MF_STRING, (UIntPtr)(IDM_MOVE_BASE + (uint)i), targets[i].Name);
            AppendMenuW(menu, MF_POPUP | (targets.Count == 0 ? MF_GRAYED : 0), (UIntPtr)(ulong)sub, "Move to");
            if (ses.S.Status != AgentStatus.Idle) AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_CLEAR, "Clear Status");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, "");
            AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_CLOSE, "Close Session");
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LEFTALIGN, sx, sy, _hwnd, IntPtr.Zero);
            DestroyMenu(menu);
            switch ((uint)cmd)
            {
                case IDM_NEW_SESSION: CreateSession(Guid.NewGuid().ToString(), null, null, ses.Ws, true); break;
                case IDM_OPEN_DIR: { var d = PickFolder(); if (d is not null) CreateSession(Guid.NewGuid().ToString(), null, d, ses.Ws, true); break; }
                case IDM_RENAME: StartRename(ses); break;
                case IDM_FLAG: FlagOp(ses, "toggle"); break;
                case IDM_CLEAR: ses.S.SetStatus(AgentStatus.Idle); RequestRedraw(); break;
                case IDM_CLOSE: if (ConfirmCloseOk()) CloseSessionInternal(ses); break;
                default:
                    if (cmd >= (int)IDM_MOVE_BASE && cmd < (int)IDM_MOVE_BASE + targets.Count)
                        MoveSession(ses, targets[cmd - (int)IDM_MOVE_BASE]);
                    else TryCreateSessionForProfileCmd(cmd, ses.Ws, null);   // "New Session ▸ <profile>"
                    break;
            }
        }
        else if (item is Workspace ws)
        {
            bool wsFocused = _focusedWorkspaceId == ws.Id;
            int wsCount; lock (_workspaces) wsCount = _workspaces.Count;
            if (_profileCfg.Profiles.Count > 1) AppendMenuW(menu, MF_POPUP, (UIntPtr)(ulong)BuildProfilesMenu(), "New Session");
            else AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_NEW_SESSION, "New Session");
            AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_OPEN_DIR, "Open Directory…");
            AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_RENAME, "Rename");
            AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_FOCUS_WS, wsFocused ? "Unfocus" : "Focus");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, "");
            AppendMenuW(menu, MF_STRING | (wsCount <= 1 ? MF_GRAYED : 0), (UIntPtr)IDM_DELETE_WS, "Delete Workspace");
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LEFTALIGN, sx, sy, _hwnd, IntPtr.Zero);
            DestroyMenu(menu);
            switch ((uint)cmd)
            {
                case IDM_NEW_SESSION: CreateSession(Guid.NewGuid().ToString(), null, null, ws, true); break;
                case IDM_OPEN_DIR: { var d = PickFolder(); if (d is not null) CreateSession(Guid.NewGuid().ToString(), null, d, ws, true); break; }
                case IDM_RENAME: StartRename(ws); break;
                case IDM_FOCUS_WS: WorkspaceFocusOp(wsFocused ? "off" : "on", ws.Id); break;
                case IDM_DELETE_WS: DeleteWorkspace(ws); break;
                default: TryCreateSessionForProfileCmd(cmd, ws, null); break;   // "New Session ▸ <profile>"
            }
        }
        else DestroyMenu(menu);
    }

    /// <summary>Footer add-session button: popup menu (New Session / Open Directory…) above the button.</summary>
    private void ShowAddSessionMenu()
    {
        float bx0 = 6f;
        foreach (var b in _footerButtons) if (b.action == "add-session") { bx0 = b.x0; break; }
        var pt = new POINT { x = (int)bx0, y = ClientH() - (int)FooterH };
        ClientToScreen(_hwnd, ref pt);
        IntPtr menu = CreatePopupMenu();
        var profs = _profileCfg.Profiles;                       // one "New <profile>" item per shell profile
        for (int i = 0; i < profs.Count; i++) AppendMenuW(menu, MF_STRING, (UIntPtr)(IDM_PROFILE_BASE + (uint)i), profs[i].Name);
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, "");
        AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_OPEN_DIR, "Open Directory…");
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_LEFTALIGN | TPM_BOTTOMALIGN, pt.x, pt.y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        var ws = ActiveWorkspace();
        if (cmd == (int)IDM_OPEN_DIR) { var d = PickFolder(); if (d is not null) CreateSession(Guid.NewGuid().ToString(), null, d, ws, true); }
        else TryCreateSessionForProfileCmd(cmd, ws, null);
    }

    private void MoveSession(Ses ses, Workspace target)
    {
        lock (_workspaces) { ses.Ws.Sessions.Remove(ses); target.Sessions.Add(ses); ses.Ws = target; target.Expanded = true; }
        SetActive(ses);
    }

    // ---- Sidebar drag-reorder (in-memory) ----

    private void DropDrag(object item, int my)
    {
        if (item is Ses s) DropSession(s, my);
        else if (item is Workspace w) DropWorkspace(w, my);
    }

    /// <summary>The workspace whose region contains <paramref name="my"/> (last header at/above it; first if above all).</summary>
    private Workspace? TargetWorkspaceAt(int my)
    {
        Workspace? cur = null;
        foreach (var (y0, _, isWs, it) in _sidebarRows)
        {
            if (!isWs || it is not Workspace w) continue;
            if (cur is null) cur = w;   // default to the first workspace
            if (y0 <= my) cur = w;      // the last header at/above my wins
        }
        return cur;
    }

    private void DropSession(Ses drag, int my)
    {
        lock (_workspaces)
        {
            var target = TargetWorkspaceAt(my);
            if (target is null) return;
            // Insert index = visible target-ws session rows (excluding the dragged one) above my.
            int idx = 0;
            foreach (var (y0, y1, isWs, it) in _sidebarRows)
                if (!isWs && it is Ses r && ReferenceEquals(r.Ws, target) && !ReferenceEquals(r, drag)
                    && (y0 + y1) / 2f < my) idx++;
            drag.Ws.Sessions.Remove(drag);
            idx = Math.Clamp(idx, 0, target.Sessions.Count);
            target.Sessions.Insert(idx, drag);
            drag.Ws = target;
            target.Expanded = true;
        }
        SetActive(drag);
    }

    private void DropWorkspace(Workspace drag, int my)
    {
        lock (_workspaces)
        {
            int idx = 0;
            foreach (var (y0, y1, isWs, it) in _sidebarRows)
                if (isWs && it is Workspace w && !ReferenceEquals(w, drag) && (y0 + y1) / 2f < my) idx++;
            _workspaces.Remove(drag);
            idx = Math.Clamp(idx, 0, _workspaces.Count);
            _workspaces.Insert(idx, drag);
        }
        RequestRedraw();
        SaveState();
    }

    /// <summary>Pixel Y of the insertion line for the in-progress drag (-1 if none).</summary>
    private float DropIndicatorY()
    {
        if (_dragItem is Workspace)
        {
            float lastY1 = -1;
            foreach (var (y0, y1, isWs, it) in _sidebarRows)
                if (isWs && it is Workspace w && !ReferenceEquals(w, _dragItem))
                {
                    if (_dragY < (y0 + y1) / 2f) return y0;
                    lastY1 = y1;
                }
            return lastY1;
        }
        if (_dragItem is Ses)
        {
            var target = TargetWorkspaceAt(_dragY);
            if (target is null) return -1;
            float headerBottom = -1, lastRowY1 = -1;
            foreach (var (y0, y1, isWs, it) in _sidebarRows)
            {
                if (isWs && it is Workspace w && ReferenceEquals(w, target)) headerBottom = y1;
                else if (!isWs && it is Ses r && ReferenceEquals(r.Ws, target) && !ReferenceEquals(r, _dragItem))
                {
                    if (_dragY < (y0 + y1) / 2f) return y0;
                    lastRowY1 = y1;
                }
            }
            return lastRowY1 > 0 ? lastRowY1 : headerBottom;
        }
        return -1;
    }

    private void DrawDropIndicator(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        float lineY = DropIndicatorY();
        if (lineY >= 0)
        {
            brush.Color = new Color4(0.30f, 0.60f, 0.98f, 1f);
            rt.FillRectangle(new Rect(0, lineY - 1f, _sidebarW, 2f), brush);
        }
        string label = _dragItem is Ses s ? s.Name : _dragItem is Workspace w ? w.Name : "";
        if (label.Length > 0)
        {
            brush.Color = new Color4(1f, 1f, 1f, 0.92f);
            rt.DrawText(label, _uiFont, new Rect(24f, _dragY - 9f, _sidebarW - 8f, _dragY + 11f), brush);
        }
    }

    private void DeleteWorkspace(Workspace ws)
    {
        lock (_workspaces) if (_workspaces.Count <= 1) return; // agterm: can't delete the last workspace
        List<Ses> sessions;
        bool hadActive = _active is not null && ReferenceEquals(_active.Ws, ws);
        lock (_workspaces)
        {
            sessions = ws.Sessions.ToList();
            ws.Sessions.Clear();
            _workspaces.Remove(ws);
            if (_workspaces.Count == 0) _workspaces.Add(new Workspace { Id = Guid.NewGuid().ToString(), Name = "workspace 1" });
        }
        foreach (var s in sessions) { try { s.S.Dispose(); } catch { } }
        if (hadActive)
        {
            var next = AllSessions().FirstOrDefault();
            if (next is not null) SetActive(next);
            else CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), true);
        }
        RequestRedraw();
        SaveState();
    }

    /// <summary>Modal folder picker (native shell). Returns the chosen path or null.</summary>
    private string? PickFolder()
    {
        var bi = new BROWSEINFO
        {
            hwndOwner = _hwnd,
            lpszTitle = "Open Directory",
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE,
        };
        IntPtr pidl = SHBrowseForFolderW(ref bi);
        if (pidl == IntPtr.Zero) return null;
        var sb = new StringBuilder(260);
        bool ok = SHGetPathFromIDListW(pidl, sb);
        CoTaskMemFree(pidl);
        return ok && sb.Length > 0 ? sb.ToString() : null;
    }

    // ---- Agent attention (title-bar bell + jump-to-next) ----

    private (bool blocked, bool active) AttentionState()
    {
        bool blocked = false, active = false;
        foreach (var s in AllSessions())
        {
            var st = s.S.Status;
            if (st == AgentStatus.Blocked) blocked = true;
            else if (st is AgentStatus.Active or AgentStatus.Completed) active = true;
        }
        return (blocked, active);
    }

    private void GoToNextAttention(int dir)
    {
        var list = AllSessions().Where(s => s.S.Status is AgentStatus.Blocked or AgentStatus.Completed).ToList();
        if (list.Count == 0) { ShowToast("no sessions need attention"); return; }
        int idx = _active is not null ? list.IndexOf(_active) : -1;
        int next = idx < 0 ? (dir > 0 ? 0 : list.Count - 1) : ((idx + dir) % list.Count + list.Count) % list.Count;
        SetActive(list[next]);
    }

    /// <summary>True if any attention-worthy session has its blink flag set (drives the bell pulse).</summary>
    private bool AnyBlinkAttention()
    {
        foreach (var s in AllSessions())
            if (s.S.Blink && s.S.Status is AgentStatus.Blocked or AgentStatus.Active or AgentStatus.Completed) return true;
        return false;
    }

    // ---- Agent-status sounds (winmm / System.Media; all playback is async / off the UI thread) ----

    [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);
    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_ALIAS = 0x00010000, SND_FILENAME = 0x00020000;
    // MessageBeep types (standard system-sound events; async, no extra assembly needed).
    private const uint MB_OK = 0x0, MB_ICONHAND = 0x10, MB_ICONQUESTION = 0x20, MB_ICONEXCLAMATION = 0x30, MB_ICONASTERISK = 0x40;

    /// <summary>Play the config's blocked-sound (silent when unset / "off" / "none").</summary>
    private static void PlayBlockedSound()
    {
        string bs = _config.BlockedSound;
        if (string.IsNullOrWhiteSpace(bs) || bs.Equals("off", StringComparison.OrdinalIgnoreCase)
            || bs.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        PlayStatusSound(bs);
    }

    /// <summary>Play a sound spec: null/"default" => system alert; a known system-sound name; a .wav path;
    /// otherwise a Windows sound-event alias. Never throws.</summary>
    private static void PlayStatusSound(string? spec)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(spec) || spec.Equals("default", StringComparison.OrdinalIgnoreCase))
            { MessageBeep(MB_ICONASTERISK); return; }
            switch (spec.ToLowerInvariant())
            {
                case "beep": MessageBeep(MB_OK); return;
                case "asterisk": MessageBeep(MB_ICONASTERISK); return;
                case "exclamation": MessageBeep(MB_ICONEXCLAMATION); return;
                case "hand" or "error" or "critical": MessageBeep(MB_ICONHAND); return;
                case "question": MessageBeep(MB_ICONQUESTION); return;
            }
            if (spec.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(spec))
            { PlaySound(spec, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT); return; }
            // Treat anything else as a Windows sound-event alias (e.g. "SystemNotification").
            PlaySound(spec, IntPtr.Zero, SND_ASYNC | SND_ALIAS | SND_NODEFAULT);
        }
        catch { /* audio is best-effort */ }
    }

    // ---- Command palette (⌃P sessions / ⌃⇧P actions / ⌃⇧I attention) ----

    private static readonly Color4 PalScrim = new(0f, 0f, 0f, 0.45f);
    private Color4 PalBg = new(0.11f, 0.12f, 0.145f, 1f);
    private Color4 PalBorder = new(0.30f, 0.34f, 0.42f, 1f);
    private Color4 PalSel = new(0.20f, 0.30f, 0.46f, 1f);

    private void TogglePalette(PaletteKind kind)
    {
        if (_palette == kind) { ClosePalette(); return; }
        if (kind == PaletteKind.Themes) _themeBeforePreview = _theme;
        _palette = kind; _palQuery = ""; _palSel = 0;
        BuildPaletteItems();
        FilterPalette();          // calls PreviewSelectedTheme() at its end
        RequestRedraw();
    }

    private void ClosePalette()
    {
        _palette = PaletteKind.None;
        _palAll.Clear(); _palItems.Clear(); _palRows.Clear();
        RequestRedraw();
    }

    private void BuildPaletteItems()
    {
        _palAll.Clear();
        switch (_palette)
        {
            case PaletteKind.Windows:
            {
                foreach (var w in Windows())
                {
                    var sel = w.Id;
                    string label = string.IsNullOrEmpty(w.Name) ? w.Id[..Math.Min(8, w.Id.Length)] : w.Name;
                    _palAll.Add(new PalItem
                    {
                        Label = label + (w.Active ? "  (active)" : ""),
                        Secondary = (w.Open ? "open" : "closed") + "  ·  " + w.Id[..Math.Min(8, w.Id.Length)],
                        Search = $"{label} {w.Id}",
                        Run = () => WindowSelect(sel),
                    });
                }
                if (_palAll.Count == 0) _palAll.Add(new PalItem { Label = "No windows", Run = null });
                break;
            }
            case PaletteKind.Sessions:
            {
                foreach (var s in AllSessions())
                {
                    var sx = s;
                    string cwd = PrettyCwd(SafeCwd(sx));
                    _palAll.Add(new PalItem
                    {
                        Label = sx.Name,
                        Secondary = cwd.Length > 0 ? $"{sx.Ws.Name}  ·  {cwd}" : sx.Ws.Name,
                        Search = $"{sx.Name} {sx.Ws.Name} {cwd}",
                        Dot = sx.S.Status,
                        Run = () => { lock (_workspaces) sx.Ws.Expanded = true; SetActive(sx); },
                    });
                }
                break;
            }
            case PaletteKind.Actions:
            {
                void A(string label, string hint, Action run) => _palAll.Add(new PalItem { Label = label, Hint = hint, Search = label, Run = run });
                A("New Session", "Ctrl+Shift+T", () => CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), true));
                A("New Session…", "", () => TogglePalette(PaletteKind.NewSession));   // pick a shell profile
                A("New Workspace", "Ctrl+Shift+N", () => CreateWorkspace(Guid.NewGuid().ToString(), null));
                A("New Window", "Ctrl+Alt+N", () => WindowNew(null));
                A("Close Window", "", () => DestroyWindow(_hwnd));
                A("Switch Window…", "", () => TogglePalette(PaletteKind.Windows));
                A("Rename Active Session", "F2", () => { if (_active is not null) StartRename(_active); });
                A("Close Pane / Session", "Ctrl+Shift+W", CloseActivePane);
                A("Split Pane", "Ctrl+D", () => SplitOp("toggle"));
                A("Focus Left Pane", "Ctrl+Alt+Left", () => FocusPane(-1));
                A("Focus Right Pane", "Ctrl+Alt+Right", () => FocusPane(1));
                A("Delete Active Workspace", "", () => { if (_active is not null) DeleteWorkspace(_active.Ws); });
                A("Flag / Unflag Session", "Ctrl+Shift+F", () => { if (_active is not null) FlagOp(_active, "toggle"); });
                A("Show Flagged / All Sessions", "", ToggleFlaggedView);
                A("Focus Workspace", "", () => WorkspaceFocusOp("toggle"));
                A("Toggle Sidebar", "", ToggleSidebar);
                A("Select All", "Ctrl+Shift+A", () => { if (ActiveSurface() is { } p) SelectAll(p); });
                A("Copy Selection", "Ctrl+C", () => { if (ActiveSurface() is { } p && p.HasSel) CopySelection(p); });
                A("Paste", "Ctrl+V", () => { if (ActiveSurface() is { } p) PasteInto(p); });
                A("Next Session", "Ctrl+Tab", () => CycleSession(1));
                A("Previous Session", "Ctrl+Shift+Tab", () => CycleSession(-1));
                A("Next Attention", "Ctrl+Alt+Down", () => GoToNextAttention(1));
                A("Previous Attention", "Ctrl+Alt+Up", () => GoToNextAttention(-1));
                A("Increase Font Size", "Ctrl+=", () => ChangeFontSize(1));
                A("Decrease Font Size", "Ctrl+-", () => ChangeFontSize(-1));
                A("Reset Font Size", "Ctrl+0", () => ChangeFontSize(0));
                A("Scratch Terminal", "Ctrl+J", () => { if (_active is not null) ScratchOp(_active, "toggle"); });
                A("Quick Terminal", "Ctrl+`", () => QuickOp("toggle"));
                A("Select Theme…", "", () => TogglePalette(PaletteKind.Themes));
                // Only when oh-my-posh integration is on AND oh-my-posh is actually installed (themes found).
                if (_config.OmpIntegration && Agwinterm.Pty.OmpThemes.List().Count > 0)
                    A("oh-my-posh Theme…", "", () => TogglePalette(PaletteKind.Omp));
                A("Settings…", "", OpenSettingsWindow);
                A("Custom Commands…", "Ctrl+Shift+O", () => TogglePalette(PaletteKind.Custom));
                // Opt-in integrations (agterm's Help-menu trio + shell) — the installer stays minimal.
                A("Install Command-Line Tool (PATH)", "", InstallCli);
                A("Install Agent Status Hooks", "", InstallHooks);
                A("Install Agent Skill", "", InstallSkill);
                A("Install Shell Integration", "", InstallShellIntegration);
                A("Reload Keymap", "", ReloadKeymap);
                break;
            }
            case PaletteKind.Custom:
            {
                if (_commands.Count == 0)
                {
                    _palAll.Add(new PalItem { Label = "No custom commands", Secondary = "define them in keymap.conf", Run = null });
                    break;
                }
                foreach (var c in _commands)
                {
                    var cc = c;
                    string sec = cc.Mode == "send" ? cc.Text : $"[{cc.Mode}]  {cc.Text}";
                    _palAll.Add(new PalItem { Label = cc.Label, Secondary = sec, Search = $"{cc.Label} {cc.Text}", Run = () => RunCustomCommand(cc) });
                }
                break;
            }
            case PaletteKind.Themes:
            {
                foreach (var th in _allThemes)
                {
                    var tx = th;
                    _palAll.Add(new PalItem { Label = tx.Name, Search = tx.Name, Data = tx, Run = () => CommitTheme(tx) });
                }
                break;
            }
            case PaletteKind.Omp:
            {
                var themes = Agwinterm.Pty.OmpThemes.List();
                if (themes.Count == 0)
                {
                    _palAll.Add(new PalItem { Label = "No oh-my-posh themes found",
                        Secondary = "install oh-my-posh or set $env:POSH_THEMES_PATH", Run = null });
                    break;
                }
                foreach (var (nm, pth) in themes)
                {
                    var p = pth; // applies live + persists so new sessions keep it
                    _palAll.Add(new PalItem { Label = nm, Search = nm, Run = () => ApplyOmp(p, persist: true) });
                }
                break;
            }
            case PaletteKind.NewSession:
            {
                foreach (var p in _profileCfg.Profiles)
                {
                    var name = p.Name;
                    string cmd = p.Command + (p.Args is { Length: > 0 } aa ? " " + string.Join(" ", aa) : "");
                    bool def = name.Equals(_profileCfg.Default, StringComparison.OrdinalIgnoreCase);
                    _palAll.Add(new PalItem
                    {
                        Label = name + (def ? "  (default)" : ""),
                        Secondary = cmd,
                        Search = name,
                        Run = () => CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), true, profileName: name),
                    });
                }
                _palAll.Add(new PalItem
                {
                    Label = "Open Directory…",
                    Secondary = "pick a folder for a new session",
                    Search = "open directory folder",
                    Run = () => { var d = PickFolder(); if (d is not null) CreateSession(Guid.NewGuid().ToString(), null, d, ActiveWorkspace(), true); },
                });
                break;
            }
            case PaletteKind.Attention:
            {
                var att = AllSessions().Where(s => s.S.Status != AgentStatus.Idle)
                    .OrderBy(s => s.S.Status == AgentStatus.Blocked ? 0 : s.S.Status == AgentStatus.Active ? 1 : 2)
                    .ToList();
                if (att.Count == 0) { _palAll.Add(new PalItem { Label = "No sessions need attention", Run = null }); break; }
                foreach (var s in att)
                {
                    var sx = s;
                    _palAll.Add(new PalItem
                    {
                        Label = sx.Name,
                        Secondary = $"{sx.Ws.Name}  ·  {sx.S.Status.ToString().ToLowerInvariant()}",
                        Search = $"{sx.Name} {sx.Ws.Name}",
                        Dot = sx.S.Status,
                        Run = () => { lock (_workspaces) sx.Ws.Expanded = true; SetActive(sx); },
                    });
                }
                break;
            }
        }
    }

    private void FilterPalette()
    {
        _palItems.Clear();
        if (_palQuery.Length == 0) { _palItems.AddRange(_palAll); }
        else
        {
            string q = _palQuery.ToLowerInvariant();
            _palItems.AddRange(_palAll
                .Select(it => (it, sc: FuzzyScore(q, it.Search.ToLowerInvariant())))
                .Where(x => x.sc >= 0)
                .OrderByDescending(x => x.sc).ThenBy(x => x.it.Label, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.it));
        }
        if (_palSel >= _palItems.Count) _palSel = Math.Max(0, _palItems.Count - 1);
        PreviewSelectedTheme();
    }

    /// <summary>Subsequence fuzzy score: -1 = no match; higher = better (contiguity + earliness).</summary>
    private static int FuzzyScore(string q, string text)
    {
        if (q.Length == 0) return 0;
        int ti = 0, score = 0, streak = 0, first = -1;
        foreach (char qc in q)
        {
            int found = text.IndexOf(qc, ti);
            if (found < 0) return -1;
            if (first < 0) first = found;
            if (found == ti) { streak++; score += 8 + streak * 2; } else { streak = 0; score += 1; }
            ti = found + 1;
        }
        return score + Math.Max(0, 12 - first);
    }

    private bool PaletteKeyDown(int vk)
    {
        switch (vk)
        {
            case VK_ESCAPE:
                if (_palette == PaletteKind.Themes && _themeBeforePreview is not null) ApplyTheme(_themeBeforePreview);
                ClosePalette(); return true;
            case VK_RETURN: RunPaletteSelection(); return true;
            case VK_UP: if (_palSel > 0) { _palSel--; PreviewSelectedTheme(); RequestRedraw(); } return true;
            case VK_DOWN: if (_palSel < _palItems.Count - 1) { _palSel++; PreviewSelectedTheme(); RequestRedraw(); } return true;
            case VK_BACK: if (_palQuery.Length > 0) { _palQuery = _palQuery[..^1]; _palSel = 0; FilterPalette(); RequestRedraw(); } return true;
        }
        return true; // swallow everything else from the terminal; printable arrives via WM_CHAR
    }

    private void RunPaletteSelection()
    {
        Action? run = (_palSel >= 0 && _palSel < _palItems.Count) ? _palItems[_palSel].Run : null;
        ClosePalette();
        run?.Invoke();
    }

    private void PaletteClick(int mx, int my)
    {
        foreach (var r in _palRows)
            if (my >= r.y0 && my < r.y1) { _palSel = r.idx; RunPaletteSelection(); return; }
        bool inPanel = mx >= _palPanel.X && mx < _palPanel.X + _palPanel.Width && my >= _palPanel.Y && my < _palPanel.Y + _palPanel.Height;
        if (!inPanel)
        {
            if (_palette == PaletteKind.Themes && _themeBeforePreview is not null) ApplyTheme(_themeBeforePreview);
            ClosePalette(); // click outside closes; inside (padding/query) is ignored
        }
    }

    private void DrawPalette(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (_palette == PaletteKind.None) return;
        _palRows.Clear();
        int cw = ClientW(), ch = ClientH();
        brush.Color = PalScrim;
        rt.FillRectangle(new Rect(0, 0, cw, ch), brush);

        const float queryH = 42f, rowH = 40f;
        const int maxRows = 12;
        float pw = MathF.Min(560f, cw - 80f);
        float px = (cw - pw) / 2f;
        int shown = Math.Min(_palItems.Count, maxRows);
        float ph = queryH + Math.Max(1, shown) * rowH + 8f;
        float py = MathF.Max(TitleBarH + 16f, ch * 0.14f);
        _palPanel = new Rect(px, py, pw, ph);

        brush.Color = PalBg;
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = _palPanel, RadiusX = 10f, RadiusY = 10f }, brush);
        brush.Color = PalBorder;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = _palPanel, RadiusX = 10f, RadiusY = 10f }, brush, 1f);

        // Query line (placeholder when empty) + blinking caret.
        string placeholder = _palette switch { PaletteKind.Sessions => "Go to session…", PaletteKind.Actions => "Run action…", PaletteKind.Themes => "Select theme…", PaletteKind.Omp => "oh-my-posh theme…", PaletteKind.Custom => "Run command…", PaletteKind.Windows => "Switch window…", PaletteKind.NewSession => "New session — pick a shell…", _ => "Attention" };
        brush.Color = _palQuery.Length > 0 ? ChromeText : ChromeDim;
        rt.DrawText(_palQuery.Length > 0 ? _palQuery : placeholder, _uiFont, new Rect(px + 16f, py + 9f, pw - 32f, queryH - 10f), brush);
        if (_cursorOn && _palQuery.Length > 0)
        {
            float qx = px + 16f + MeasureText(_palQuery, _uiFont) + 1f;
            brush.Color = ChromeText;
            rt.DrawLine(new System.Numerics.Vector2(qx, py + 11f), new System.Numerics.Vector2(qx, py + queryH - 10f), brush, 1.2f);
        }
        brush.Color = ChromeBorder;
        rt.DrawLine(new System.Numerics.Vector2(px + 8f, py + queryH), new System.Numerics.Vector2(px + pw - 8f, py + queryH), brush, 1f);

        int start = _palSel >= maxRows ? _palSel - maxRows + 1 : 0;
        for (int i = 0; i < shown; i++)
        {
            int idx = start + i;
            if (idx >= _palItems.Count) break;
            var it = _palItems[idx];
            float ry = py + queryH + i * rowH;
            if (idx == _palSel) { brush.Color = PalSel; rt.FillRectangle(new Rect(px + 4f, ry, pw - 8f, rowH), brush); }
            float tx = px + 16f;
            if (it.Dot is AgentStatus ds) { brush.Color = StatusDot(ds); rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(px + 16f, ry + rowH / 2f), 4.5f, 4.5f), brush); tx = px + 30f; }
            bool hasSub = it.Secondary.Length > 0;
            brush.Color = it.Run is null ? ChromeDim : (idx == _palSel ? SbActiveText : ChromeText);
            rt.DrawText(it.Label, _uiFont, new Rect(tx, ry + (hasSub ? 3f : 0f), pw - (tx - px) - 80f, hasSub ? 20f : rowH), brush);
            if (hasSub) { brush.Color = ChromeDim; rt.DrawText(it.Secondary, _uiSmall, new Rect(tx, ry + 20f, pw - (tx - px) - 20f, 16f), brush); }
            if (it.Hint.Length > 0) { brush.Color = ChromeDim; float hw = MeasureText(it.Hint, _uiSmall); rt.DrawText(it.Hint, _uiSmall, new Rect(px + pw - 16f - hw, ry + (rowH - 16f) / 2f, hw + 2f, 16f), brush); }
            _palRows.Add((ry, ry + rowH, idx));
        }
    }

    // ---- Custom title bar / status bar (frameless chrome) ----

    private Color4 ChromeBg = new(0.043f, 0.051f, 0.059f, 1f);
    private Color4 ChromeText = new(0.92f, 0.93f, 0.95f, 1f);
    private Color4 ChromeDim = new(0.55f, 0.58f, 0.62f, 1f);
    private Color4 ChromeAccent = new(0.30f, 0.55f, 0.95f, 1f);   // theme accent (selection, active markers, focus)
    private Color4 ChromeBorder = new(0.22f, 0.24f, 0.28f, 1f);   // dividers / separators

    // ---- Chrome palette derivation (all chrome colours track the active theme) ----

    private static float Lum(Color4 c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
    private static Color4 Mix(Color4 a, Color4 b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);
    private static Color4 WithA(Color4 c, float a) => new(c.R, c.G, c.B, a);
    /// <summary>amt &gt; 0 lightens (toward white), amt &lt; 0 darkens (toward black).</summary>
    private static Color4 Shade(Color4 c, float amt)
        => amt >= 0 ? Mix(c, new Color4(1f, 1f, 1f, 1f), amt) : Mix(c, new Color4(0f, 0f, 0f, 1f), -amt);

    /// <summary>Recompute all chrome colours from the active theme + sidebar-tint. Called on load and every theme/config change.</summary>
    private void RecomputeChrome()
    {
        var bg = C4(_theme.DefaultBackground);
        var fg = C4(_theme.DefaultForeground);
        bool dark = Lum(bg) < 0.5f;
        _chromeDark = dark;

        // Panels sit at distinct luminance layers relative to the terminal background.
        ChromeBg = Shade(bg, dark ? -0.42f : -0.06f);   // title bar + footer toolbar
        var sidebar = Shade(bg, dark ? -0.28f : -0.09f);
        float tint = System.Math.Clamp(_config.SidebarTint, -100, 100) / 100f;
        SbBg = Shade(sidebar, tint * (dark ? 0.6f : 0.4f)); // sidebar-tint nudges the shade
        PalBg = Shade(bg, dark ? 0.07f : -0.04f);         // overlays/HUD pop slightly above the terminal

        // Accent: the theme's bright-blue (index 12), falling back to blue (4) then a safe blue, kept vivid.
        var accent = C4(_theme.Palette.Length > 12 ? _theme.Palette[12] : _theme.DefaultForeground);
        if (Lum(accent) < 0.22f && _theme.Palette.Length > 4) accent = C4(_theme.Palette[4]);
        if (Lum(accent) < 0.18f) accent = new Color4(0.30f, 0.55f, 0.95f, 1f);
        ChromeAccent = accent;

        ChromeText = Mix(fg, bg, 0.04f);
        ChromeDim = Mix(fg, bg, dark ? 0.42f : 0.40f);
        SbActiveText = fg;
        SbHeaderText = Mix(fg, bg, 0.20f);
        SbDimText = ChromeDim;
        SbHighlight = Mix(SbBg, fg, dark ? 0.14f : 0.14f);
        ChromeBorder = Mix(bg, fg, dark ? 0.20f : 0.26f);
        PalBorder = Mix(PalBg, fg, dark ? 0.28f : 0.30f);
        PalSel = Mix(PalBg, accent, dark ? 0.45f : 0.32f);
    }

    private int ClientW() { GetClientRect(_hwnd, out RECT rc); return rc.right - rc.left; }
    private int ClientH() { GetClientRect(_hwnd, out RECT rc); return rc.bottom - rc.top; }

    private bool InContent(IntPtr lParam)
    {
        int x = LoWord(lParam), y = HiWord(lParam);
        return x >= (int)_sidebarW && y >= (int)TitleBarH;
    }

    /// <summary>WM_NCCALCSIZE: reclaim the OS caption into the client so we draw our own title bar.</summary>
    private void AdjustClientRect(IntPtr hwnd, IntPtr lParam)
    {
        // When maximized, inset by the frame so content isn't pushed off-screen / under the taskbar.
        if (IsZoomed(hwnd))
        {
            var rc = Marshal.PtrToStructure<RECT>(lParam);
            int fx = GetSystemMetrics(SM_CXFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
            int fy = GetSystemMetrics(SM_CYFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
            rc.left += fx; rc.right -= fx; rc.top += fy; rc.bottom -= fy;
            Marshal.StructureToPtr(rc, lParam, false);
        }
        // Otherwise leave the proposed rect: client fills the whole window (borderless); resize via NCHITTEST.
    }

    /// <summary>WM_NCHITTEST: resize borders, caption buttons (system-handled), draggable caption, else client.</summary>
    private int HitTest(IntPtr hwnd, int sx, int sy)
    {
        var pt = new POINT { x = sx, y = sy };
        ScreenToClient(hwnd, ref pt);
        int cw = ClientW(), ch = ClientH();
        const int B = 8;
        if (!IsZoomed(hwnd))
        {
            bool top = pt.y < B, bot = pt.y >= ch - B, left = pt.x < B, right = pt.x >= cw - B;
            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bot && left) return HTBOTTOMLEFT;
            if (bot && right) return HTBOTTOMRIGHT;
            if (top) return HTTOP;
            if (bot) return HTBOTTOM;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
        }
        if (pt.y < (int)TitleBarH)
        {
            int cap = CaptionButtonAt(pt.x, cw);
            if (cap != _hoverCaption) { _hoverCaption = cap; RequestRedraw(); }
            if (cap == 3) return HTCLOSE;
            if (cap == 2) return HTMAXBUTTON;
            if (cap == 1) return HTMINBUTTON;
            foreach (var b in _titleButtons) if (pt.x >= b.x0 && pt.x < b.x1) return HTCLIENT; // our action buttons
            return HTCAPTION; // draggable
        }
        if (_hoverCaption != 0) { _hoverCaption = 0; RequestRedraw(); }
        return HTCLIENT;
    }

    private int CaptionButtonAt(int x, int cw)
    {
        if (x >= cw - (int)CaptionBtnW) return 3;              // close
        if (x >= cw - 2 * (int)CaptionBtnW) return 2;         // max/restore
        if (x >= cw - 3 * (int)CaptionBtnW) return 1;         // min
        return 0;
    }

    private string? ChromeHit(List<(float x0, float x1, string action)> buttons, int mx)
    {
        foreach (var b in buttons) if (mx >= b.x0 && mx < b.x1) return b.action;
        return null;
    }

    /// <summary>Update the hovered chrome button from a client-area move; arms the fade timer on change.</summary>
    private void UpdateChromeHover(int mx, int my)
    {
        string? hit = null;
        if (my < (int)TitleBarH) hit = ChromeHit(_titleButtons, mx);
        else if (mx < (int)_sidebarW && my >= ClientH() - (int)FooterH) hit = ChromeHit(_footerButtons, mx);
        if (hit == _hotBtn) return;
        _hotBtn = hit;
        if (hit is not null) { _hotPaint = hit; _hotAlpha = 1f; }   // light instantly on hover-in
        else SetTimer(_hwnd, (IntPtr)HoverTimer, 15, IntPtr.Zero);  // fade out when leaving
        RequestRedraw();
    }

    /// <summary>Ease the hover-fill alpha toward the target; stop the timer once settled.</summary>
    private void HoverTick()
    {
        float target = _hotBtn is not null ? 1f : 0f;
        const float step = 0.20f;
        if (_hotAlpha < target) _hotAlpha = MathF.Min(target, _hotAlpha + step);
        else if (_hotAlpha > target) _hotAlpha = MathF.Max(target, _hotAlpha - step);
        if (_hotAlpha == target) { KillTimer(_hwnd, (IntPtr)HoverTimer); if (target == 0f) _hotPaint = null; }
        RequestRedraw();
    }

    /// <summary>Paint a chrome button's hover/press background + record its hit-box; returns the tinted icon colour.</summary>
    private Color4 ChromeBtnBg(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush,
        float x, float y, float w, float h, string id, List<(float x0, float x1, string action)> list, Color4 baseColor)
    {
        list.Add((x, x + w, id));
        float a = id == _hotPaint ? _hotAlpha : 0f;
        bool pressed = _pressBtn == id && _hotBtn == id;
        const float padY = 5f;
        var r = new Rect(x + 3f, y + padY, w - 6f, h - 2f * padY); // XYWH
        if (pressed)
        {
            brush.Color = WithA(ChromeText, _chromeDark ? 0.30f : 0.22f);
            rt.FillRoundedRectangle(new RoundedRectangle { Rect = r, RadiusX = 6f, RadiusY = 6f }, brush);
        }
        else if (a > 0.01f)
        {
            brush.Color = WithA(ChromeText, (_chromeDark ? 0.20f : 0.14f) * a);
            rt.FillRoundedRectangle(new RoundedRectangle { Rect = r, RadiusX = 6f, RadiusY = 6f }, brush);
        }
        float bright = pressed ? 1f : a;
        return bright > 0f ? Mix(baseColor, ChromeText, bright * 0.85f) : baseColor;
    }

    private void ChromeAction(string a)
    {
        switch (a)
        {
            case "toggle": ToggleSidebar(); break;
            case "add-session": TogglePalette(PaletteKind.NewSession); break;   // themed picker: profiles + Open Directory…
            case "new-workspace": CreateWorkspace(Guid.NewGuid().ToString(), null); break;
            case "attention": GoToNextAttention(1); break;
            case "scratch": if (_active is not null) ScratchOp(_active, "toggle"); break;
            case "split": SplitOp("toggle"); break;   // title-bar split button toggles the split on/off
            case "quick terminal": QuickOp("toggle"); break;
            case "flag": ToggleFlaggedView(); break;   // footer flag button toggles the flagged working-set view
            case "unfocus": _focusedWorkspaceId = null; RequestRedraw(); SaveState(); break;
            case "settings": OpenSettingsWindow(); break;
            default: ShowToast(a + " not implemented yet"); break;
        }
    }

    private void ToggleSidebar()
    {
        _sidebarW = _sidebarW > 0 ? 0 : SidebarWFull;
        if (_active is not null) RegridSession(_active);
        if (_cover is not null) RegridCover();
        RequestRedraw();
        SaveState();
    }

    private void ShowToast(string text)
    {
        _toastText = text;
        _toastTarget = null;
        SetTimer(_hwnd, (IntPtr)2, 1900, IntPtr.Zero);
        RequestRedraw();
    }

    // ---- Notifications (OSC 9 / OSC 777 / notify) ----

    /// <summary>Find the session that owns a pane (any pane, its scratch, or its overlay; or the quick cover).</summary>
    private Ses? OwningSes(Pane p)
    {
        lock (_workspaces)
            foreach (var w in _workspaces)
                foreach (var s in w.Sessions)
                    if (s.Panes.Contains(p) || ReferenceEquals(s.Scratch, p) || ReferenceEquals(s.Overlay, p))
                        return s;
        return null;
    }

    /// <summary>A pane raised a desktop notification. Runs on the UI thread (marshaled from the pump).</summary>
    private void OnNotified(Pane p, string title, string body)
    {
        var ses = OwningSes(p);
        // Count it against the session unless that pane is the surface you're looking at right now.
        if (!ReferenceEquals(p, ActiveSurface())) p.Unread++;
        string label = string.IsNullOrEmpty(title) ? body : $"{title}: {body}";
        _toastText = label.Length == 0 ? "(notification)" : label;
        _toastTarget = ses;                       // clicking the banner jumps to the raising session
        SetTimer(_hwnd, (IntPtr)2, 4000, IntPtr.Zero);
        if (_config.DesktopNotifications) TrayNotify(title, body);
        RequestRedraw();
    }

    /// <summary>Total unread notifications across a session's panes (for the sidebar badge).</summary>
    private static int UnreadOf(Ses s) { int n = 0; foreach (var p in s.Panes) n += p.Unread; return n; }

    private static void ClearUnread(Ses s) { foreach (var p in s.Panes) p.Unread = 0; if (s.Scratch is not null) s.Scratch.Unread = 0; if (s.Overlay is not null) s.Overlay.Unread = 0; }

    /// <summary>Show an OS desktop notification via a Shell_NotifyIcon tray balloon (no AUMID/shortcut needed).</summary>
    private void TrayNotify(string title, string body)
    {
        try
        {
            var d = new NOTIFYICONDATAW
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_INFO | (_trayAdded ? 0u : (NIF_ICON | NIF_TIP)),
                hIcon = _trayAdded ? IntPtr.Zero : LoadIconW(IntPtr.Zero, (IntPtr)32512), // IDI_APPLICATION
                szTip = "agwinterm",
                szInfoTitle = string.IsNullOrEmpty(title) ? "agwinterm" : title,
                szInfo = body.Length == 0 ? " " : body,
                dwInfoFlags = 0,
            };
            if (!_trayAdded) { _trayAdded = Shell_NotifyIconW(NIM_ADD, ref d); }
            Shell_NotifyIconW(NIM_MODIFY, ref d);
        }
        catch { /* balloon is best-effort; the in-app banner + badge are the reliable surface */ }
    }

    private void RemoveTrayIcon()
    {
        if (!_trayAdded) return;
        try { var d = new NOTIFYICONDATAW { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATAW>(), hWnd = _hwnd, uID = 1 }; Shell_NotifyIconW(NIM_DELETE, ref d); } catch { }
        _trayAdded = false;
    }

    private static string SafeCwd(Ses s) { lock (s.S.SyncRoot) return s.S.Emulator.Cwd; }
    private static string SafeCwd(Pane p) { lock (p.S.SyncRoot) return p.S.Emulator.Cwd; }

    /// <summary>An existing directory for a session's "current" dir: live OSC-7 cwd if valid, else its launch dir.</summary>
    private static string? CurrentDirOf(Ses s)
    {
        var live = SafeCwd(s);
        if (!string.IsNullOrWhiteSpace(live)) { var p = PrettyCwd(live); if (Directory.Exists(p)) return p; }
        return s.ActivePane.StartCwd;
    }

    /// <summary>The path shown in the title bar for a session: live OSC 7 cwd if the shell reports it,
    /// else the pane's launch dir, else the process cwd — so a real path always shows, out of the box.</summary>
    private static string TitleCwd(Ses s)
    {
        string live = SafeCwd(s);
        if (!string.IsNullOrWhiteSpace(live)) return PrettyCwd(live);
        string? start = s.ActivePane.StartCwd;
        return string.IsNullOrWhiteSpace(start) ? Environment.CurrentDirectory : start!;
    }

    /// <summary>Title-bar display name (agterm precedence): custom name → program OSC title → cwd basename → app name.</summary>
    private static string SessionDisplayName(Ses s)
    {
        if (!string.IsNullOrWhiteSpace(s.CustomName)) return s.CustomName!;
        // A program's OSC title (e.g. "claude") — but ignore the shell's default console title,
        // which on Windows is the bare exe path (…\powershell.exe) or an absolute path (noise).
        string osc = s.ActivePane.S.Emulator.Title;
        bool oscMeaningful = !string.IsNullOrWhiteSpace(osc)
            && !osc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !(osc.Length >= 2 && osc[1] == ':' && osc.Contains('\\')); // not a bare C:\… path
        if (oscMeaningful) return osc;
        // Single-row title = the full current path (agterm-style "just the path"), home collapsed to ~.
        string cwd = PrettyCwd(TitleCwd(s)).TrimEnd('\\', '/');
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(home))
        {
            if (string.Equals(cwd, home, StringComparison.OrdinalIgnoreCase)) return "~";
            if (cwd.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase)) return "~" + cwd[home.Length..];
        }
        return string.IsNullOrWhiteSpace(cwd) ? "agwinterm" : cwd;
    }

    private static string PrettyCwd(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string s = raw;
        if (s.StartsWith("file://")) { s = s[7..]; int slash = s.IndexOf('/'); if (slash > 0) s = s[slash..]; }
        try { s = Uri.UnescapeDataString(s); } catch { }
        return s.TrimStart('/').Replace('/', '\\');
    }

    private static float MeasureText(string s, IDWriteTextFormat fmt)
    {
        using var tl = _dwrite.CreateTextLayout(s, fmt, 4096f, 100f);
        return tl.Metrics.Width;
    }

    // Segoe Fluent Icons glyphs (present on Win11).
    private const string GlyphSidebar = "";  // GlobalNavButton (hamburger)
    private const string GlyphBell = "";     // Ringer
    private const string GlyphTerminal = ""; // CommandPrompt (quick terminal)
    private const string GlyphGear = "";     // Settings
    private static readonly string GlyphAdd = ((char)0xE710).ToString();   // Add (add-session)
    private static readonly string GlyphClose = ((char)0xE8BB).ToString(); // ChromeClose (pill x)

    private void DrawTitleBar(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        _titleButtons.Clear();
        int cw = ClientW();
        brush.Color = ChromeBg;
        rt.FillRectangle(new Rect(0, 0, cw, TitleBarH), brush);

        // 1. App-mark logo at the far left (vector rendition of the app icon).
        DrawLogo(rt, brush, 10f, TitleBarH / 2f);

        // 2. Sidebar toggle at the sidebar column's right edge (tracks _sidebarW), or right of the logo when hidden.
        float togW = 36f;
        float togX = _sidebarW > 0 ? MathF.Max(34f, _sidebarW - togW - 2f) : 34f;
        {
            var c = ChromeBtnBg(rt, brush, togX, 0, togW, TitleBarH, "toggle", _titleButtons, ChromeText);
            brush.Color = c;
            rt.DrawText(GlyphSidebar, _iconFont, new Rect(togX, 0, togW, TitleBarH), brush);
        }

        // 6. Right group (pinned left of the caption buttons): scratch, split, | , quick-terminal, gear.
        float bw = 38f;
        float rgRight = cw - 3 * CaptionBtnW - 6f;   // right edge of the gear
        float gearX = rgRight - bw;
        float quickX = gearX - bw;
        float divX = quickX - 5f;                    // hairline divider in the gap
        float splitX = quickX - 10f - bw;
        float scratchX = splitX - bw;

        // 3. Title at the terminal's leading edge (right of the sidebar): a SINGLE centered row
        // showing the path (agterm-style): custom name -> program OSC title -> full cwd path.
        string title = _active is not null ? SessionDisplayName(_active) : "agwinterm";
        // 4. Attention bell (can be hidden via settings; when hidden it reserves no space).
        bool showBell = _config.AttentionButton;
        float titleX = _sidebarW > 0 ? _sidebarW + 10f : togX + togW + 8f;
        float bellW = showBell ? 34f : 0f, bellGap = showBell ? 8f : 0f;
        float titleAvail = scratchX - 14f - bellW - bellGap - titleX;
        float titleMeasured = MeasureText(title, _uiFont);
        float titleW = MathF.Max(30f, MathF.Min(titleMeasured, titleAvail));
        brush.Color = ChromeText;
        rt.DrawText(title, _uiTitle, new Rect(titleX, 0f, titleW, TitleBarH), brush);  // one vertically-centered, ellipsized row

        // dim = nothing, plain = active/completed, blocked-color = any blocked (uses the configured status color).
        if (showBell)
        {
            float bellX = MathF.Min(titleX + titleW + bellGap, scratchX - bellW - 14f);
            var (bellBlocked, bellActive) = AttentionState();
            var bellBase = bellBlocked ? StatusDot(AgentStatus.Blocked) : (bellActive ? ChromeText : ChromeDim);
            if ((bellBlocked || bellActive) && !_cursorOn && AnyBlinkAttention())
                bellBase = new Color4(bellBase.R, bellBase.G, bellBase.B, 0.30f); // pulse when a blinking session needs attention
            var c = ChromeBtnBg(rt, brush, bellX, 0, bellW, TitleBarH, "attention", _titleButtons, bellBase);
            DrawBellGlyph(rt, brush, bellX + bellW / 2f, TitleBarH / 2f, c);
        }

        // scratch (rounded rectangle; filled when active)
        bool scratchOn = _coverKind == 1;
        {
            var c = ChromeBtnBg(rt, brush, scratchX, 0, bw, TitleBarH, "scratch", _titleButtons, scratchOn ? ChromeAccent : ChromeDim);
            DrawScratchGlyph(rt, brush, scratchX + bw / 2f, TitleBarH / 2f, c, scratchOn);
        }
        // split (two panes, reflects split state)
        bool splitOn = _active is not null && _active.Panes.Count > 1;
        {
            var c = ChromeBtnBg(rt, brush, splitX, 0, bw, TitleBarH, "split", _titleButtons, splitOn ? ChromeAccent : ChromeDim);
            DrawSplitGlyph(rt, brush, splitX + bw / 2f, TitleBarH / 2f, c, splitOn);
        }
        // hairline divider between per-session toggles and the window-level quick terminal
        brush.Color = WithA(ChromeText, 0.25f);
        rt.DrawLine(new System.Numerics.Vector2(divX, 12f), new System.Numerics.Vector2(divX, TitleBarH - 12f), brush, 1f);
        // quick terminal (accent when active)
        bool quickOn = _coverKind == 2;
        {
            var c = ChromeBtnBg(rt, brush, quickX, 0, bw, TitleBarH, "quick terminal", _titleButtons, quickOn ? ChromeAccent : ChromeDim);
            brush.Color = c;
            rt.DrawText(GlyphTerminal, _iconFont, new Rect(quickX, 0, bw, TitleBarH), brush);
        }
        // gear (settings) — kept per user's choice
        {
            var c = ChromeBtnBg(rt, brush, gearX, 0, bw, TitleBarH, "settings", _titleButtons, ChromeDim);
            brush.Color = c;
            rt.DrawText(GlyphGear, _iconFont, new Rect(gearX, 0, bw, TitleBarH), brush);
        }

        DrawCaption(rt, brush, cw);
    }

    /// <summary>Vector app-mark: cyan terminal chevron + block cursor + green agent-status dot (matches the app icon).</summary>
    private void DrawLogo(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float x, float cy)
    {
        var cyan = new Color4(0x38 / 255f, 0xD7 / 255f, 0xF0 / 255f, 1f);
        var cyanLt = new Color4(0x8A / 255f, 0xE2 / 255f, 0xF2 / 255f, 1f);
        var green = new Color4(0x3D / 255f, 0xDC / 255f, 0x84 / 255f, 1f);
        brush.Color = cyan;
        float top = cy - 6f, mid = cy, bot = cy + 6f, lx = x + 1f, rx = x + 7f;
        rt.DrawLine(new System.Numerics.Vector2(lx, top), new System.Numerics.Vector2(rx, mid), brush, 2.2f);
        rt.DrawLine(new System.Numerics.Vector2(rx, mid), new System.Numerics.Vector2(lx, bot), brush, 2.2f);
        brush.Color = cyanLt;
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(x + 10f, cy + 1f, 7f, 4.5f), RadiusX = 1.5f, RadiusY = 1.5f }, brush);
        brush.Color = green;
        rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(x + 15.5f, cy - 5f), 2.6f, 2.6f), brush);
    }

    /// <summary>Scratch glyph: a rounded rectangle (outline, or filled when active).</summary>
    private void DrawScratchGlyph(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float cx, float cy, Color4 color, bool filled)
    {
        brush.Color = color;
        var r = new RoundedRectangle { Rect = new Rect(cx - 7f, cy - 5.5f, 14f, 11f), RadiusX = 2.5f, RadiusY = 2.5f };
        if (filled) rt.FillRoundedRectangle(r, brush); else rt.DrawRoundedRectangle(r, brush, 1.4f);
    }

    /// <summary>Attention bell glyph (plain outline bell + clapper). Never slashed; color conveys state.</summary>
    private void DrawBellGlyph(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float cx, float cy, Color4 color)
    {
        brush.Color = color;
        var g = _d2d.CreatePathGeometry();
        using (var sink = g.Open())
        {
            sink.BeginFigure(new System.Numerics.Vector2(cx - 5.5f, cy + 3.5f), FigureBegin.Hollow); // bottom-left rim
            sink.AddLine(new System.Numerics.Vector2(cx - 4f, cy - 1.5f));                            // left wall
            sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = new System.Numerics.Vector2(cx - 4f, cy - 6.2f), Point2 = new System.Numerics.Vector2(cx, cy - 6.2f) });
            sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = new System.Numerics.Vector2(cx + 4f, cy - 6.2f), Point2 = new System.Numerics.Vector2(cx + 4f, cy - 1.5f) });
            sink.AddLine(new System.Numerics.Vector2(cx + 5.5f, cy + 3.5f));                           // right wall to rim
            sink.EndFigure(FigureEnd.Closed);                                                          // bottom rim
            sink.Close();
        }
        rt.DrawGeometry(g, brush, 1.4f);
        g.Dispose();
        rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(cx, cy + 5.4f), 1.4f, 1.4f), brush);   // clapper
        rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(cx, cy - 6.6f), 1.1f, 1.1f), brush);   // top nub
    }

    /// <summary>Split glyph: two side-by-side panes (right pane filled when split is active).</summary>
    private void DrawSplitGlyph(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float cx, float cy, Color4 color, bool active)
    {
        brush.Color = color;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = new Rect(cx - 7f, cy - 5.5f, 14f, 11f), RadiusX = 2.5f, RadiusY = 2.5f }, brush, 1.4f);
        rt.DrawLine(new System.Numerics.Vector2(cx, cy - 5.5f), new System.Numerics.Vector2(cx, cy + 5.5f), brush, 1.2f);
        if (active) rt.FillRectangle(new Rect(cx + 1f, cy - 4f, 5f, 8f), brush);
    }

    /// <summary>New-workspace glyph: a card with a plus.</summary>
    private void DrawNewWorkspaceGlyph(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float cx, float cy, Color4 color)
    {
        brush.Color = color;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = new Rect(cx - 7f, cy - 6f, 10f, 10f), RadiusX = 2f, RadiusY = 2f }, brush, 1.3f);
        rt.DrawLine(new System.Numerics.Vector2(cx - 3.5f, cy + 5.5f), new System.Numerics.Vector2(cx + 4.5f, cy + 5.5f), brush, 1.3f);
        rt.DrawLine(new System.Numerics.Vector2(cx + 4.5f, cy - 3f), new System.Numerics.Vector2(cx + 4.5f, cy + 1f), brush, 1.4f);
        rt.DrawLine(new System.Numerics.Vector2(cx + 2.5f, cy - 1f), new System.Numerics.Vector2(cx + 6.5f, cy - 1f), brush, 1.4f);
    }

    /// <summary>Flag glyph (pennant); filled when the flagged view is active.</summary>
    private void DrawFlagGlyph(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float cx, float cy, Color4 color, bool filled)
    {
        brush.Color = color;
        rt.DrawLine(new System.Numerics.Vector2(cx - 5f, cy - 6f), new System.Numerics.Vector2(cx - 5f, cy + 6f), brush, 1.4f);
        var g = _d2d.CreatePathGeometry();
        using (var sink = g.Open())
        {
            sink.BeginFigure(new System.Numerics.Vector2(cx - 5f, cy - 6f), filled ? FigureBegin.Filled : FigureBegin.Hollow);
            sink.AddLine(new System.Numerics.Vector2(cx + 6f, cy - 4f));
            sink.AddLine(new System.Numerics.Vector2(cx - 5f, cy - 1f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        if (filled) rt.FillGeometry(g, brush); else rt.DrawGeometry(g, brush, 1.3f);
        g.Dispose();
    }

    private void DrawCaption(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, int cw)
    {
        int pressedIdx = _capPressed == HTMINBUTTON ? 1 : _capPressed == HTMAXBUTTON ? 2 : _capPressed == HTCLOSE ? 3 : 0;
        for (int i = 1; i <= 3; i++) // 1 min, 2 max/restore, 3 close (left to right)
        {
            float x0 = cw - (4 - i) * CaptionBtnW;
            bool hot = _hoverCaption == i || pressedIdx == i;
            if (hot)
            {
                brush.Color = i == 3 ? new Color4(0.86f, 0.15f, 0.18f, 1f) : SbHighlight;
                rt.FillRectangle(new Rect(x0, 0, CaptionBtnW, TitleBarH), brush);
            }
            brush.Color = ((_hoverCaption == 3 || pressedIdx == 3) && i == 3) ? new Color4(1f, 1f, 1f, 1f) : ChromeText;
            float cx = x0 + CaptionBtnW / 2f, cy = TitleBarH / 2f;
            if (i == 1) rt.DrawLine(new System.Numerics.Vector2(cx - 5, cy), new System.Numerics.Vector2(cx + 5, cy), brush, 1f);
            else if (i == 2)
            {
                if (IsZoomed(_hwnd))
                {
                    rt.DrawRectangle(new Rect(cx - 5, cy - 3, 8, 8), brush, 1f);
                    rt.DrawLine(new System.Numerics.Vector2(cx - 2, cy - 3), new System.Numerics.Vector2(cx - 2, cy - 5), brush, 1f);
                    rt.DrawLine(new System.Numerics.Vector2(cx - 2, cy - 5), new System.Numerics.Vector2(cx + 5, cy - 5), brush, 1f);
                    rt.DrawLine(new System.Numerics.Vector2(cx + 5, cy - 5), new System.Numerics.Vector2(cx + 5, cy + 3), brush, 1f);
                    rt.DrawLine(new System.Numerics.Vector2(cx + 5, cy + 3), new System.Numerics.Vector2(cx + 3, cy + 3), brush, 1f);
                }
                else rt.DrawRectangle(new Rect(cx - 5, cy - 5, 10, 10), brush, 1f);
            }
            else
            {
                rt.DrawLine(new System.Numerics.Vector2(cx - 5, cy - 5), new System.Numerics.Vector2(cx + 5, cy + 5), brush, 1f);
                rt.DrawLine(new System.Numerics.Vector2(cx - 5, cy + 5), new System.Numerics.Vector2(cx + 5, cy - 5), brush, 1f);
            }
        }
    }

    private void DrawToast(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (_toastText is null) return;
        int cw = ClientW(), ch = ClientH();
        float tw = MeasureText(_toastText, _uiFont) + 32f, th = 34f;
        float cx = _sidebarW + ((cw - _sidebarW) - tw) / 2f;
        float ty = ch - th - 24f;
        _toastRect = new Rect(cx, ty, tw, th);   // recorded for click-to-jump hit-testing
        brush.Color = Mix(ChromeBg, ChromeText, 0.14f);
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(cx, ty, tw, th), RadiusX = 8f, RadiusY = 8f }, brush);
        brush.Color = ChromeText;
        rt.DrawText(_toastText, _uiFont, new Rect(cx + 16f, ty, tw - 24f, th), brush);
    }

    /// <summary>While a leader sequence is pending, a small pill hint (bottom-left of the content region).</summary>
    private void DrawLeaderHint(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (!_leaderPending) return;
        string txt = $"leader  {_leader}  —  press a key…";
        float tw = MeasureText(txt, _uiFont) + 32f, th = 34f;
        float x = _sidebarW + 24f, y = ClientH() - th - 24f;
        brush.Color = new Color4(0.22f, 0.17f, 0.33f, 1f);   // purple-ish so it reads distinctly from a toast
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(x, y, tw, th), RadiusX = 8f, RadiusY = 8f }, brush);
        brush.Color = new Color4(1f, 1f, 1f, 1f);             // fixed light text (pill bg is always dark)
        rt.DrawText(txt, _uiFont, new Rect(x + 16f, y, tw - 24f, th), brush);
    }

    // ---- Config (mirrors the WinUI shell) ----

    // ---- Theming ----

    private void ApplyTheme(Theme t) { _theme = t; RecomputeChrome(); RequestRedraw(); }

    /// <summary>Apply the window-opacity config via a layered window (LWA_ALPHA). 100% removes the layered style.</summary>
    private void ApplyWindowOpacity()
    {
        if (_hwnd == IntPtr.Zero) return;
        int pct = System.Math.Clamp(_config.WindowOpacity, 30, 100);
        long ex = (long)GetWindowLongPtrW(_hwnd, GWL_EXSTYLE);
        if (pct >= 100)
        {
            if ((ex & WS_EX_LAYERED) != 0) SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, (IntPtr)(ex & ~WS_EX_LAYERED));
            return;
        }
        if ((ex & WS_EX_LAYERED) == 0) SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_LAYERED));
        SetLayeredWindowAttributes(_hwnd, 0, (byte)(pct * 255 / 100), LWA_ALPHA);
    }

    private void CommitTheme(Theme t)
    {
        ApplyTheme(t);
        _config.Theme = t.Name;
        _themeBeforePreview = null;
        try { SaveThemeConfig(t.Name); } catch { }
    }

    /// <summary>Live-preview the theme under the selection while the theme picker is open.</summary>
    private void PreviewSelectedTheme()
    {
        if (_palette != PaletteKind.Themes) return;
        if (_palSel >= 0 && _palSel < _palItems.Count && _palItems[_palSel].Data is Theme th) ApplyTheme(th);
    }

    // ---- oh-my-posh scheme changer (in-app extension) ----

    /// <summary>Apply an oh-my-posh theme (name or .omp.json path) live to the active session's shell,
    /// re-running oh-my-posh init and RE-APPLYING our OSC-7 prompt wrap (init redefines `prompt`, which
    /// would drop the wrap). Persists to config when asked so new sessions launch with it.</summary>
    public string OmpSet(string nameOrPath, bool persist)
    {
        string? path = Agwinterm.Pty.OmpThemes.Resolve(nameOrPath);
        if (path is null) return "oh-my-posh theme not found: " + nameOrPath;
        Post(() => ApplyOmp(path, persist));
        return "oh-my-posh theme set: " + nameOrPath;
    }

    private void ApplyOmp(string path, bool persist)
    {
        if (ActiveSurface() is { } pane)
        {
            string q = path.Replace("'", "''");
            // Re-init oh-my-posh with the chosen config (which redefines `prompt`), then wrap that NEW
            // prompt to keep emitting OSC 7 so the title's live cwd survives the switch. One clean line.
            string line =
                "oh-my-posh init pwsh --config '" + q + "' | Invoke-Expression; " +
                "$__o=$function:prompt; function global:prompt { " +
                "[Console]::Write(\"$([char]27)]7;file://$env:COMPUTERNAME/$(((Get-Location).ProviderPath -replace '\\\\','/'))$([char]7)\"); " +
                "& $__o }\r";
            pane.S.NotifyActivity();
            pane.S.Write(Encoding.UTF8.GetBytes(line));
            RequestRedraw();
        }
        if (persist) { try { WriteConfigKey("omp-theme", path); } catch { } _config.OmpTheme = path; }
    }

    private static Theme FindTheme(string name)
        => _allThemes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Theme.Default;

    /// <summary>Persist the chosen theme by rewriting (or appending) the `theme =` line in the config.</summary>
    private static void SaveThemeConfig(string name) => WriteConfigKey("theme", name);

    /// <summary>All config keys the Settings window / config verbs manage.</summary>
    private static readonly string[] ConfigKeys =
    {
        "font-family", "font-size", "cursor-style", "cursor-blink", "cursor-blink-ms", "theme",
        "scrollback-lines", "inactive-pane-dim", "window-opacity", "sidebar-tint", "scroll-speed",
        "new-session-dir", "right-click-paste", "copy-on-select", "desktop-notifications", "shell-integration",
        "restore-commands", "blocked-sound", "omp-theme", "omp-integration",
        "new-session-dir-mode", "confirm-close-session", "compact-toolbar", "notification-badges",
        "attention-button", "status-color-active", "status-color-blocked", "status-color-completed",
    };

    /// <summary>Rewrite (or append) a single `key = value` line in agwinterm.conf, preserving the rest.</summary>
    private static void WriteConfigKey(string key, string value)
    {
        string path = ConfigPath;
        string text = File.Exists(path) ? File.ReadAllText(path) : TerminalConfig.DefaultText;
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        int idx = lines.FindIndex(l =>
        {
            var t = l.TrimStart();
            int eq = l.IndexOf('=');
            return !t.StartsWith("#") && eq > 0 && string.Equals(l[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase);
        });
        string ln = $"{key} = {value}";
        if (idx >= 0) lines[idx] = ln; else { lines.Add(ln); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join(Environment.NewLine, lines));
    }

    /// <summary>Current value of a config key as a string (for config get/list + the Settings window).</summary>
    private static string ConfigValue(string key) => key switch
    {
        "font-family" => _config.FontFamily,
        "font-size" => _config.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "cursor-style" => _config.CursorStyle.ToString().ToLowerInvariant(),
        "cursor-blink" => _config.CursorBlink ? "true" : "false",
        "cursor-blink-ms" => _config.CursorBlinkMs.ToString(),
        "theme" => _config.Theme,
        "scrollback-lines" => _config.Scrollback.ToString(),
        "inactive-pane-dim" => _config.InactivePaneDim.ToString(),
        "window-opacity" => _config.WindowOpacity.ToString(),
        "sidebar-tint" => _config.SidebarTint.ToString(),
        "scroll-speed" => _config.ScrollSpeed.ToString(),
        "new-session-dir" => _config.NewSessionDir,
        "right-click-paste" => _config.RightClickPaste ? "true" : "false",
        "copy-on-select" => _config.CopyOnSelect ? "true" : "false",
        "desktop-notifications" => _config.DesktopNotifications ? "true" : "false",
        "shell-integration" => _config.ShellIntegration ? "true" : "false",
        "restore-commands" => _config.RestoreCommands ? "true" : "false",
        "blocked-sound" => _config.BlockedSound,
        "omp-theme" => _config.OmpTheme,
        "omp-integration" => _config.OmpIntegration ? "true" : "false",
        "new-session-dir-mode" => _config.NewSessionDirMode,
        "confirm-close-session" => _config.ConfirmCloseSession ? "true" : "false",
        "compact-toolbar" => _config.CompactToolbar ? "true" : "false",
        "notification-badges" => _config.NotificationBadges ? "true" : "false",
        "attention-button" => _config.AttentionButton ? "true" : "false",
        "status-color-active" => _config.StatusColorActive,
        "status-color-blocked" => _config.StatusColorBlocked,
        "status-color-completed" => _config.StatusColorCompleted,
        _ => "",
    };

    /// <summary>Persist + apply a config key live. Runs on the UI thread. Returns an ack string.</summary>
    private string ConfigSetInternal(string key, string value)
    {
        key = key.Trim().ToLowerInvariant();
        if (Array.IndexOf(ConfigKeys, key) < 0) return "error: unknown key '" + key + "'";
        WriteConfigKey(key, value.Trim());
        _config = TerminalConfig.Load(ConfigPath);       // reparse so clamping/validation is centralized
        if (key == "theme") _theme = FindTheme(_config.Theme);
        if (key == "cursor-blink-ms" && _hwnd != IntPtr.Zero) SetTimer(_hwnd, (IntPtr)1, (uint)_config.CursorBlinkMs, IntPtr.Zero);
        RecomputeChrome();
        ApplyWindowOpacity();
        if (key == "compact-toolbar")                     // title-bar height changed → reflow the terminal grid
        { if (_active is not null) RegridSession(_active); if (_cover is not null) RegridCover(); }
        RequestRedraw();
        RefreshSettingsControls();                        // keep an open Settings window in sync
        bool deferred = key is "font-family" or "font-size" or "scrollback-lines" or "shell-integration" or "restore-commands";
        return $"{key} = {ConfigValue(key)}" + (deferred ? "  (applies to new sessions)" : "");
    }

    /// <summary>
    /// Built-in themes, then the curated ghostty files bundled next to the exe (themes/),
    /// then any user files in %LOCALAPPDATA%\agwinterm\themes\. Deduped by name
    /// (case-insensitive), first-seen wins — so a compiled built-in beats a bundled/user
    /// file of the same name, and bundled beats user.
    /// </summary>
    private static List<Theme> LoadThemes()
    {
        var builtins = new List<Theme>
        {
            Theme.Default, // "default"
            Make("Solarized Dark",
                new[]{"#073642","#dc322f","#859900","#b58900","#268bd2","#d33682","#2aa198","#eee8d5",
                      "#002b36","#cb4b16","#586e75","#657b83","#839496","#6c71c4","#93a1a1","#fdf6e3"},
                "#839496","#002b36","#93a1a1"),
            Make("Solarized Light",
                new[]{"#073642","#dc322f","#859900","#b58900","#268bd2","#d33682","#2aa198","#eee8d5",
                      "#002b36","#cb4b16","#586e75","#657b83","#839496","#6c71c4","#93a1a1","#fdf6e3"},
                "#657b83","#fdf6e3","#586e75"),
            Make("Nord",
                new[]{"#3b4252","#bf616a","#a3be8c","#ebcb8b","#81a1c1","#b48ead","#88c0d0","#e5e9f0",
                      "#4c566a","#bf616a","#a3be8c","#ebcb8b","#81a1c1","#b48ead","#8fbcbb","#eceff4"},
                "#d8dee9","#2e3440","#d8dee9"),
            Make("Gruvbox Dark",
                new[]{"#282828","#cc241d","#98971a","#d79921","#458588","#b16286","#689d6a","#a89984",
                      "#928374","#fb4934","#b8bb26","#fabd2f","#83a598","#d3869b","#8ec07c","#ebdbb2"},
                "#ebdbb2","#282828","#ebdbb2"),
            Make("Tokyo Night",
                new[]{"#15161e","#f7768e","#9ece6a","#e0af68","#7aa2f7","#bb9af7","#7dcfff","#a9b1d6",
                      "#414868","#f7768e","#9ece6a","#e0af68","#7aa2f7","#bb9af7","#7dcfff","#c0caf5"},
                "#c0caf5","#1a1b26","#c0caf5"),
            Make("One Dark",
                new[]{"#282c34","#e06c75","#98c379","#e5c07b","#61afef","#c678dd","#56b6c2","#abb2bf",
                      "#545862","#e06c75","#98c379","#e5c07b","#61afef","#c678dd","#56b6c2","#c8ccd4"},
                "#abb2bf","#282c34","#528bff"),
        };

        var list = new List<Theme>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(Theme t) { if (seen.Add(t.Name)) list.Add(t); }

        foreach (var t in builtins) Add(t);

        // Bundled themes ship in themes/ next to the exe; user themes live under LOCALAPPDATA.
        string bundledDir = Path.Combine(AppContext.BaseDirectory, "themes");
        string userDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm", "themes");
        foreach (var dir in new[] { bundledDir, userDir })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir))
                    if (ParseGhosttyTheme(f) is Theme th) Add(th);
            }
            catch { }
        }
        return list;
    }

    private static Theme Make(string name, string[] hex16, string fg, string bg, string cursor) => new()
    {
        Name = name,
        Palette = hex16.Select(Hex).ToArray(),
        DefaultForeground = Hex(fg),
        DefaultBackground = Hex(bg),
        Cursor = Hex(cursor),
    };

    private static Color Hex(string h)
    {
        h = h.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length < 6) return new Color(0, 0, 0);
        try { return new Color(Convert.ToByte(h[..2], 16), Convert.ToByte(h.Substring(2, 2), 16), Convert.ToByte(h.Substring(4, 2), 16)); }
        catch { return new Color(0, 0, 0); }
    }

    /// <summary>Parse a ghostty-format theme file (palette = N=#rrggbb, background/foreground/cursor-color).</summary>
    private static Theme? ParseGhosttyTheme(string path)
    {
        try
        {
            var pal = Theme.DefaultPalette();
            Color fg = Color.DefaultForeground, bg = Color.DefaultBackground, cur = new(222, 222, 230);
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('='); if (eq <= 0) continue;
                string key = line[..eq].Trim().ToLowerInvariant(); string val = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "palette": int c = val.IndexOf('='); if (c > 0 && int.TryParse(val[..c].Trim(), out int pi) && pi is >= 0 and < 16) pal[pi] = Hex(val[(c + 1)..]); break;
                    case "background": bg = Hex(val); break;
                    case "foreground": fg = Hex(val); break;
                    case "cursor-color": cur = Hex(val); break;
                }
            }
            return new Theme { Name = Path.GetFileNameWithoutExtension(path), Palette = pal, DefaultForeground = fg, DefaultBackground = bg, Cursor = cur };
        }
        catch { return null; }
    }

    // ---- Persistence: workspace/session tree + selection + sidebar state ----

    private sealed class PaneState { public string Id { get; set; } = ""; public string Cwd { get; set; } = ""; public float FontSize { get; set; } public float Ratio { get; set; } = 1f; public string Command { get; set; } = ""; }
    // Cwd/FontSize kept for backward-compat with pre-splits state.json (one pane per session).
    private sealed class SessionState { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string? CustomName { get; set; } public string? Profile { get; set; } public int Active { get; set; } public bool Flagged { get; set; } public List<PaneState> Panes { get; set; } = new(); public string Cwd { get; set; } = ""; public float FontSize { get; set; }
        // Wave F2: background watermark (BgFile = the copied file's name under backgrounds\; null = none).
        public string? BgFile { get; set; } public int BgOpacity { get; set; } = 15; public string BgMode { get; set; } = "fit"; }
    private sealed class WorkspaceState { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public bool Expanded { get; set; } = true; public List<SessionState> Sessions { get; set; } = new(); }
    private sealed class AppState
    {
        public List<WorkspaceState> Workspaces { get; set; } = new();
        public string? ActiveId { get; set; }
        public float SidebarWidth { get; set; } = SidebarWFull;
        public bool SidebarVisible { get; set; } = true;
        // Window geometry (restore rect; 0 width = unset). WindowMaximized reopens maximized.
        public int WindowX { get; set; }
        public int WindowY { get; set; }
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }
        // Wave D1: sidebar view mode ("tree"|"flagged") + focused workspace id (null = show all).
        public string SidebarMode { get; set; } = "tree";
        public string? FocusedWorkspaceId { get; set; }
    }

    private static readonly JsonSerializerOptions _stateJson = new() { WriteIndented = true };

    private static string AppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm");
    private static string WindowsIndexPath => Path.Combine(AppDir, "windows.json");
    private static string LegacyStatePath => Path.Combine(AppDir, "state.json");
    private static string BackgroundsDir => Path.Combine(AppDir, "backgrounds"); // copied per-session watermark images
    // Per-window tree snapshot. Multi-window (F1b): each window persists to windows/<id>.json.
    private string StatePath => Path.Combine(AppDir, "windows", Id + ".json");

    /// <summary>Load the window library index; migrate a legacy single-window state.json; ensure it's non-empty.</summary>
    private static void LoadOrMigrateIndex()
    {
        lock (_windowIndex)
        {
            _windowIndex.Clear();
            try
            {
                if (File.Exists(WindowsIndexPath))
                {
                    var idx = JsonSerializer.Deserialize<WindowsIndexFile>(File.ReadAllText(WindowsIndexPath));
                    if (idx?.Windows is { Count: > 0 })
                    {
                        _windowIndex.AddRange(idx.Windows.Where(m => !string.IsNullOrEmpty(m.Id)));
                        _frontmostId = idx.Frontmost;
                    }
                }
            }
            catch { _windowIndex.Clear(); }

            if (_windowIndex.Count == 0)
            {
                // Migrate a legacy state.json into windows/<id>.json, or seed a fresh window.
                var m = new WinMeta { Id = Guid.NewGuid().ToString(), Name = "", IsOpen = true };
                try
                {
                    if (File.Exists(LegacyStatePath))
                    {
                        var st = JsonSerializer.Deserialize<AppState>(File.ReadAllText(LegacyStatePath));
                        if (st is not null)
                        {
                            m.X = st.WindowX; m.Y = st.WindowY; m.W = st.WindowWidth; m.H = st.WindowHeight; m.Max = st.WindowMaximized;
                        }
                        Directory.CreateDirectory(Path.Combine(AppDir, "windows"));
                        File.Copy(LegacyStatePath, Path.Combine(AppDir, "windows", m.Id + ".json"), overwrite: true);
                        try { File.Move(LegacyStatePath, LegacyStatePath + ".migrated", overwrite: true); } catch { }
                    }
                }
                catch { }
                _windowIndex.Add(m);
                _frontmostId = m.Id;
            }
            if (!_windowIndex.Any(w => w.IsOpen)) _windowIndex[0].IsOpen = true;
            if (_frontmostId is null || !_windowIndex.Any(w => w.Id == _frontmostId))
                _frontmostId = _windowIndex.First(w => w.IsOpen).Id;
        }
    }

    /// <summary>Persist the window library index (atomic). Best-effort.</summary>
    private static void SaveIndex()
    {
        try
        {
            List<WinMeta> copy;
            lock (_windowIndex) copy = _windowIndex.Select(m => new WinMeta { Id = m.Id, Name = m.Name, IsOpen = m.IsOpen, X = m.X, Y = m.Y, W = m.W, H = m.H, Max = m.Max }).ToList();
            var idx = new WindowsIndexFile { Version = 1, Frontmost = _frontmostId, Windows = copy };
            Directory.CreateDirectory(AppDir);
            string tmp = WindowsIndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(idx, _stateJson));
            File.Move(tmp, WindowsIndexPath, overwrite: true);
        }
        catch { }
    }

    /// <summary>Create a Program instance for a library entry and boot its window (UI thread). Seeds if no tree file exists.</summary>
    private static Program CreateWindowInstance(WinMeta m)
    {
        var win = new Program { Id = m.Id, WinName = m.Name };
        if (m.W > 0 && m.H > 0) { win._geoX = m.X; win._geoY = m.Y; win._geoW = m.W; win._geoH = m.H; win._geoMax = m.Max; win._geoValid = true; }
        lock (_windowIndex) { _byId[m.Id] = win; if (!_windowIndex.Any(x => x.Id == m.Id)) _windowIndex.Add(m); m.IsOpen = true; }
        win.Boot(_hInstance);
        return win;
    }

    private List<Pane> PanesOf(Ses s) { lock (_workspaces) return s.Panes.ToList(); }

    private static string DenylistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm", "restore-denylist.conf");

    /// <summary>Exe/process names (no extension) that restore-commands never re-runs. Seeds a starter file.</summary>
    private static HashSet<string> LoadDenylist()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "powershell", "pwsh", "cmd", "conhost", "wsl", "ssh", "bash", "oh-my-posh", "git", "windowsterminal" };
        try
        {
            string path = DenylistPath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path,
                    "# agwinterm restore-denylist: process/exe names (no extension) NOT re-run on restart.\n" +
                    "# One per line; '#' starts a comment. Defaults cover shells and prompt helpers.\n" +
                    "powershell\npwsh\ncmd\nconhost\nwsl\nssh\nbash\noh-my-posh\ngit\nWindowsTerminal\n");
            }
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                set.Add(StripExe(line));
            }
        }
        catch { }
        return set;
    }

    private static string StripExe(string name)
    {
        name = name.Trim().Trim('"');
        int slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    /// <summary>
    /// Best-effort foreground-command capture: for each shell PID, the command line of its most
    /// recently started non-denylisted child. One CIM process snapshot for all panes; ~1s at quit.
    /// </summary>
    private static Dictionary<int, string> CaptureForegroundCommands(IEnumerable<int> shellPids)
    {
        var result = new Dictionary<int, string>();
        var pids = shellPids.Distinct().ToHashSet();
        if (pids.Count == 0) return result;
        var deny = LoadDenylist();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CommandLine,@{n='C';e={if($_.CreationDate){$_.CreationDate.Ticks}else{0}}} | ConvertTo-Json -Compress\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return result;
            string json = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(4000)) { try { proc.Kill(); } catch { } return result; }
            if (string.IsNullOrWhiteSpace(json)) return result;

            using var doc = JsonDocument.Parse(json);
            var rows = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };
            var byParent = new Dictionary<int, List<(long created, string cmd)>>();
            foreach (var e in rows)
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                int ppid = e.TryGetProperty("ParentProcessId", out var pv) && pv.TryGetInt32(out var pp) ? pp : -1;
                if (!pids.Contains(ppid)) continue;
                string name = e.TryGetProperty("Name", out var nv) && nv.ValueKind == JsonValueKind.String ? (nv.GetString() ?? "") : "";
                string cmd = e.TryGetProperty("CommandLine", out var cv) && cv.ValueKind == JsonValueKind.String ? (cv.GetString() ?? "") : "";
                long created = e.TryGetProperty("C", out var tv) && tv.ValueKind == JsonValueKind.Number && tv.TryGetInt64(out var t) ? t : 0;
                if (cmd.Length == 0 || deny.Contains(StripExe(name))) continue;
                if (!byParent.TryGetValue(ppid, out var list)) byParent[ppid] = list = new();
                list.Add((created, cmd));
            }
            foreach (var (ppid, list) in byParent)
                result[ppid] = list.OrderByDescending(x => x.created).First().cmd;
        }
        catch { }
        return result;
    }

    /// <summary>Snapshot the tree/selection/sidebar to disk atomically. No-op while restoring; ignores IO errors.
    /// <paramref name="captureCommands"/> (quit only) captures each pane's foreground command when restore-commands is on.</summary>
    private void SaveState(bool captureCommands = false)
    {
        if (_restoring) return;
        try
        {
            // Snapshot rows under the workspaces lock, then read each cwd (which locks the
            // session) OUTSIDE that lock to keep lock ordering consistent.
            List<(string id, string name, bool expanded, List<Ses> sessions)> rows;
            lock (_workspaces)
                rows = _workspaces.Select(w => (w.Id, w.Name, w.Expanded, w.Sessions.ToList())).ToList();
            string? activeId = _active?.Id;

            // Foreground-command capture (opt-in, quit only): one process snapshot for all panes.
            Dictionary<int, string> cmdByPid = (captureCommands && _config.RestoreCommands)
                ? CaptureForegroundCommands(rows.SelectMany(r => r.sessions).SelectMany(s => PanesOf(s))
                    .Select(p => p.S.ChildProcessId).Where(id => id is > 0).Select(id => id!.Value))
                : new Dictionary<int, string>();

            var st = new AppState
            {
                ActiveId = activeId,
                SidebarWidth = _sidebarW > 0 ? _sidebarW : SidebarWFull,
                SidebarVisible = _sidebarW > 0,
                SidebarMode = _sidebarMode == SidebarMode.Flagged ? "flagged" : "tree",
                FocusedWorkspaceId = _focusedWorkspaceId,
            };
            CaptureGeometry(st);
            foreach (var (id, name, expanded, sessions) in rows)
            {
                var wss = new WorkspaceState { Id = id, Name = name, Expanded = expanded };
                foreach (var s in sessions)
                {
                    var ss = new SessionState { Id = s.Id, Name = s.Name, CustomName = s.CustomName, Profile = s.ProfileName, Active = s.Active, Flagged = s.Flagged,
                        BgFile = s.BgPath is null ? null : Path.GetFileName(s.BgPath), BgOpacity = s.BgOpacity, BgMode = s.BgMode };
                    List<Pane> panes;
                    lock (_workspaces) panes = s.Panes.ToList();
                    foreach (var p in panes)
                    {
                        string live = PrettyCwd(SafeCwd(p));                       // OSC 7 cwd if the shell reports it
                        string cwd = live.Length > 0 ? live : (p.StartCwd ?? ""); // else the launch dir
                        string cmd = (p.S.ChildProcessId is int pid && cmdByPid.TryGetValue(pid, out var c)) ? c : "";
                        ss.Panes.Add(new PaneState { Id = p.Id, Cwd = cwd, FontSize = p.FontSize, Ratio = p.Ratio, Command = cmd });
                    }
                    wss.Sessions.Add(ss);
                }
                st.Workspaces.Add(wss);
            }

            string path = StatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(st, _stateJson));
            File.Move(tmp, path, overwrite: true); // atomic replace so a crash never leaves a truncated file

            // Mirror name + geometry into the window-library entry so windows.json can position the
            // window at next launch without loading the per-window tree first.
            lock (_windowIndex)
            {
                var meta = _windowIndex.FirstOrDefault(m => m.Id == Id);
                if (meta is not null)
                {
                    meta.Name = WinName;
                    if (st.WindowWidth > 0) { meta.X = st.WindowX; meta.Y = st.WindowY; meta.W = st.WindowWidth; meta.H = st.WindowHeight; meta.Max = st.WindowMaximized; }
                }
            }
            SaveIndex();
        }
        catch { /* persistence is best-effort */ }
    }

    // Window geometry loaded from state.json at startup (applied at window creation).
    private bool _geoValid;
    private int _geoX, _geoY, _geoW, _geoH;
    private bool _geoMax;
    private bool _wasMaximized;

    /// <summary>Fill AppState with the window's restore rect + maximized flag (GetWindowPlacement).</summary>
    private void CaptureGeometry(AppState st)
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(_hwnd, ref wp)) return;
            var r = wp.rcNormalPosition;                 // restore rect even when maximized
            st.WindowX = r.left; st.WindowY = r.top;
            st.WindowWidth = r.right - r.left; st.WindowHeight = r.bottom - r.top;
            st.WindowMaximized = wp.showCmd == SW_MAXIMIZE; // SW_SHOWMAXIMIZED == 3
        }
        catch { }
    }

    /// <summary>Rebuild the tree from state.json via the normal Create* paths. Returns false to fall back to a default.</summary>
    private bool TryRestoreState()
    {
        AppState? st;
        try
        {
            string path = StatePath;
            if (!File.Exists(path)) return false;
            st = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path));
        }
        catch
        {
            try { File.Move(StatePath, StatePath + ".bad", overwrite: true); } catch { } // keep the corrupt file for inspection
            return false;
        }
        if (st?.Workspaces is null || !st.Workspaces.Any(w => w.Sessions is { Count: > 0 })) return false;

        _restoring = true;
        try
        {
            _sidebarW = st.SidebarVisible ? (st.SidebarWidth > 0 ? st.SidebarWidth : SidebarWFull) : 0;
            foreach (var w in st.Workspaces)
            {
                // Recreate every saved workspace, including empty ones (agterm keeps empty workspaces).
                var ws = CreateWorkspace(string.IsNullOrEmpty(w.Id) ? Guid.NewGuid().ToString() : w.Id, w.Name);
                foreach (var s in w.Sessions ?? new List<SessionState>())
                {
                    // Back-compat: a pre-splits session has no Panes — synthesize one from its Cwd/FontSize.
                    var pl = (s.Panes is { Count: > 0 })
                        ? s.Panes
                        : new List<PaneState> { new() { Id = s.Id, Cwd = s.Cwd, FontSize = s.FontSize, Ratio = 1f } };
                    var first = pl[0];
                    var ses = CreateSession(
                        string.IsNullOrEmpty(s.Id) ? Guid.NewGuid().ToString() : s.Id,
                        string.IsNullOrWhiteSpace(s.Name) ? null : s.Name,
                        string.IsNullOrWhiteSpace(first.Cwd) ? null : first.Cwd,
                        ws, makeActive: s.Id == st.ActiveId,
                        fontSize: first.FontSize > 0 ? first.FontSize : (float?)null,
                        profileName: string.IsNullOrWhiteSpace(s.Profile) ? null : s.Profile);
                    for (int i = 1; i < pl.Count; i++)
                        AppendPane(ses,
                            string.IsNullOrEmpty(pl[i].Id) ? Guid.NewGuid().ToString() : pl[i].Id,
                            string.IsNullOrWhiteSpace(pl[i].Cwd) ? null : pl[i].Cwd,
                            pl[i].FontSize > 0 ? pl[i].FontSize : (float)_config.FontSize);
                    lock (_workspaces)
                    {
                        for (int i = 0; i < pl.Count && i < ses.Panes.Count; i++)
                            ses.Panes[i].Ratio = pl[i].Ratio > 0 ? pl[i].Ratio : 1f;
                        ses.Active = Math.Clamp(s.Active, 0, ses.Panes.Count - 1);
                    }
                    ses.Flagged = s.Flagged;
                    ses.CustomName = string.IsNullOrWhiteSpace(s.CustomName) ? null : s.CustomName;
                    if (!string.IsNullOrEmpty(s.BgFile)) // restore the watermark if its copied file still exists
                    {
                        string bg = Path.Combine(BackgroundsDir, s.BgFile!);
                        if (File.Exists(bg)) { ses.BgPath = bg; ses.BgOpacity = s.BgOpacity; ses.BgMode = string.IsNullOrWhiteSpace(s.BgMode) ? "fit" : s.BgMode; }
                    }
                    if (ReferenceEquals(_active, ses)) _session = ses.S;
                    RegridSession(ses);

                    // Opt-in: re-run each pane's captured foreground command once the shell is ready.
                    if (_config.RestoreCommands)
                    {
                        var deny = LoadDenylist();
                        for (int i = 0; i < pl.Count && i < ses.Panes.Count; i++)
                        {
                            string cmd = pl[i].Command ?? "";
                            if (cmd.Length == 0) continue;
                            string lead = StripExe(cmd.TrimStart('"').Split(' ', 2)[0]);
                            if (deny.Contains(lead)) continue;
                            var pane = ses.Panes[i];
                            // Prefix the call operator: a captured command line starts with a quoted exe
                            // path, which pwsh would otherwise parse as a bare string literal.
                            string run = "& " + cmd;
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(2500); // let the shell/profile settle; pwsh buffers stdin until ready
                                try { pane.S.Write(System.Text.Encoding.UTF8.GetBytes(run + "\r")); } catch { }
                            });
                        }
                    }
                }
                lock (_workspaces) ws.Expanded = w.Expanded; // CreateSession forces Expanded=true; restore the saved state
            }
        }
        finally { _restoring = false; }

        // Restore sidebar mode + workspace focus (Wave D1). Focus that no longer resolves is dropped.
        _sidebarMode = string.Equals(st.SidebarMode, "flagged", StringComparison.OrdinalIgnoreCase) ? SidebarMode.Flagged : SidebarMode.Tree;
        _focusedWorkspaceId = st.FocusedWorkspaceId;
        if (_focusedWorkspaceId is not null)
            lock (_workspaces) if (!_workspaces.Any(w => w.Id == _focusedWorkspaceId)) _focusedWorkspaceId = null;

        if (_active is null) { var f = AllSessions().FirstOrDefault(); if (f is not null) SetActive(f); }
        if (AllSessions().Count == 0) return false; // nothing usable came back -> default
        SaveState();
        RequestRedraw();
        return true;
    }

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm", "agwinterm.conf");

    private static TerminalConfig LoadOrCreateConfig()
    {
        try
        {
            string path = ConfigPath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, TerminalConfig.DefaultText);
            }
            return TerminalConfig.Load(path);
        }
        catch { return new TerminalConfig(); }
    }
}
