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
    /// <summary>Effective toolbar mode: explicit toolbar-mode, else derived from the legacy compact-toolbar bool.</summary>
    private static string ToolbarModeResolved =>
        _config?.ToolbarMode is "normal" or "compact" or "hidden" ? _config.ToolbarMode!
        : (_config is { CompactToolbar: true } ? "compact" : "normal");
    private static float TitleBarH => ToolbarModeResolved switch { "hidden" => 0f, "compact" => 30f, _ => 40f };
    private static bool ToolbarHidden => ToolbarModeResolved == "hidden";
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
    private readonly HashSet<string> _selectedIds = new();   // multi-selected session ids (Ctrl/Shift+click); >1 = batch mode
    private string? _selAnchorId;                             // anchor session for Shift+range selection
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
    private enum PaletteKind { None, Sessions, Actions, Attention, Themes, Custom, Windows, Omp, NewSession, Starship, PromptEngine, Fonts }
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
    private static IDWriteTextFormat _sidebarFont = null!;     // sidebar names (size = config sidebar-font-size)
    private static IDWriteTextFormat _sidebarSmall = null!;    // sidebar counts/badges (sidebar-font-size − 1.5)
    private static IDWriteTextFormat _uiSmallCenter = null!;   // horizontally centered (panel buttons)
    private static IDWriteTextFormat _uiTitle = null!;      // single centered title-bar line (vertically centered + ellipsized)
    private static IDWriteInlineObject _ellipsis = null!;   // "…" trimming sign for the title (kept alive)
    private static IDWriteInlineObject? _sidebarEllipsis;   // "…" trimming sign for sidebar names (rebuilt with the font)
    private static IDWriteTextFormat _iconFont = null!;
    private static IDWriteTextFormat _iconSmall = null!;   // small Fluent glyphs (e.g. the row flag marker)

    /// <summary>One terminal surface within a session. A session is a left→right row of panes.</summary>
    private sealed class Pane
    {
        public required string Id;
        public required TerminalSession S;
        public string? StartCwd;   // dir the shell was launched in (fallback cwd when OSC 7 is absent)
        public string? AgentResume; // resumable agent bound to this pane (e.g. "claude") — relaunched on restart
        public float FontSize;     // per-pane font zoom (pt)
        public float Ratio = 1f;   // fraction of the session's content width (ratios in a session sum to 1)
        public int ScrollOffset;   // lines scrolled up from the live bottom (0 = live; clamped to HistoryCount)
        public long LastScrollGen; // emulator ScrollGeneration last seen on output (detects real scroll vs in-place repaint)
        public int Unread;         // unread desktop-notification count (OSC 9/777 / notify) since last visit
        public bool ReadOnly;      // block keyboard input to this pane (protect a running agent from stray keys)
        // Text selection (absolute line index: [0..HistoryCount) history, then the live grid rows).
        public bool HasSel;
        public int SelAncLine, SelAncCol, SelFocLine, SelFocCol;
        public bool BlockSel;   // rectangular (Alt+drag) selection: clip each row to [minCol,maxCol]
        public void ClearSel() => HasSel = false;
        public int UiaAnnouncedAbs = -1;   // absolute buffer line (history + cursor row) last spoken to a screen reader
    }

    private sealed class Ses
    {
        public required string Id;
        public required string Name;
        public required Workspace Ws;
        public readonly List<Pane> Panes = new();
        public int Active;         // index of the focused pane
        public bool Flagged;       // durable working-set flag (survives moves; persisted; drives flagged sidebar mode)
        public bool Elevated;      // shell runs at High integrity (admin) — shown with a ⚡; false for de-elevated sessions
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
    // Frame cap for output-driven repaints: under sustained PTY output, paint at most ~66fps instead
    // of back-to-back full renders (which saturate a core and starve the pump via grid-lock contention).
    // The first frame after a quiet period still paints immediately — no interactive latency cost.
    private const int RedrawTimer = 10;               // WM_TIMER id for the deferred frame
    private const int RedrawMinIntervalMs = 15;
    private const int UiaAnnounceTimer = 11;          // debounce: announce new output to a screen reader
    private const int UiaAnnounceQuietMs = 350;       // speak once output has settled this long
    private long _lastPaintTick;                      // Environment.TickCount64 of the last WM_PAINT render
    private bool _redrawTimerArmed;

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

    // ---- CLI launch args (wt-style, applied to the first window): ----
    //   Agwinterm.Win32.exe [-p|--profile NAME] [-d|--dir PATH] [--maximized] [--fullscreen]
    private static bool _argNoRestore;   // --no-restore: fresh window, don't reopen the saved tree (elevated launch)
    private static string? _argPipe;     // --pipe <name>: control-pipe name (default = the instance id)
    private static string? _argProfile;

    // Instance identity: namespaces the data dir (%LOCALAPPDATA%\<id>), control pipe, and window/tray title
    // so a dev build and the installed release keep entirely separate configs, sessions, and control surface
    // and never step on each other. A Debug build is ALWAYS "agwinterm-dev"; the shipped Release build is
    // "agwinterm". Override with --app-id <name>; a Release build also inherits AGWINTERM_APP_ID (nesting).
    private static readonly string _appId = ResolveAppId();
    private static bool IsDev => _appId != "agwinterm";
    internal static string AppName => _appId;

    private static string ResolveAppId()
    {
        // An explicit --app-id always wins.
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i].Equals("--app-id", StringComparison.OrdinalIgnoreCase))
                return SanitizeId(args[i + 1]);
#if DEBUG
        // A Debug build is dev PERIOD — it must not adopt a release identity just because it was launched
        // from inside a release session (which exports AGWINTERM_APP_ID), or it would collide with it.
        return "agwinterm-dev";
#else
        // Release: inherit AGWINTERM_APP_ID so a nested agwinterm shares its parent's identity, else default.
        if (Environment.GetEnvironmentVariable("AGWINTERM_APP_ID") is { Length: > 0 } env)
            return SanitizeId(env);
        return "agwinterm";
#endif
    }

    /// <summary>Reduce an app-id to a safe file/pipe token (letters, digits, dash, underscore, dot).</summary>
    private static string SanitizeId(string s)
    {
        var chars = s.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray();
        var id = new string(chars);
        return string.IsNullOrEmpty(id) ? "agwinterm" : id;
    }
    private static string? _argDir;
    private static bool _argMaximized, _argFullscreen;

    private static void ParseLaunchArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-p" or "--profile" when i + 1 < args.Length: _argProfile = args[++i]; break;
                case "-d" or "--dir" or "--startingdirectory" when i + 1 < args.Length: _argDir = args[++i]; break;
                case "--maximized": _argMaximized = true; break;
                case "--fullscreen": _argFullscreen = true; break;
                case "--no-restore": _argNoRestore = true; break;
                case "--pipe" when i + 1 < args.Length: _argPipe = args[++i]; break;
                case "--app-id" when i + 1 < args.Length: ++i; break; // consumed by ResolveAppId (namespaces data dir + pipe)
                // unknown args are ignored (forward compatibility)
            }
        }
        if (_argDir is not null)
            try { _argDir = Path.GetFullPath(_argDir); if (!Directory.Exists(_argDir)) _argDir = null; } catch { _argDir = null; }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        ParseLaunchArgs(args);
        // Publish the resolved instance id so child processes and shared (Pty) helpers resolve the same
        // data dir — and a nested agwinterm inherits the dev/release identity of the one that launched it.
        Environment.SetEnvironmentVariable("AGWINTERM_APP_ID", _appId);
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
        RebuildSidebarFonts();
        _uiSmallCenter = NewChromeFormat("Segoe UI", 11.5f, center: true);
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

    /// <summary>Clamp a restored window frame onto the nearest visible monitor work area, so a frame
    /// saved on a display that's since been unplugged or rearranged can't restore off-screen.</summary>
    private static void ClampGeoToVisibleScreen(ref int x, ref int y, ref int w, ref int h)
    {
        var rc = new RECT { left = x, top = y, right = x + w, bottom = y + h };
        IntPtr mon = MonitorFromRect(ref rc, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfoW(mon, ref mi)) return;
        var wa = mi.rcWork;
        w = Math.Min(w, wa.right - wa.left);
        h = Math.Min(h, wa.bottom - wa.top);
        x = Math.Clamp(x, wa.left, wa.right - w);
        y = Math.Clamp(y, wa.top, wa.bottom - h);
    }

    /// <summary>Per-window bootstrap: create the HWND, its render target, and the initial session.</summary>
    private void Boot(IntPtr hInstance)
    {
        RecomputeChrome();
        MeasureCell();

        // Window size/position comes from the WindowLibrary entry (set by CreateWindowInstance);
        // _geoValid/_geo* are already populated for a restored/positioned window.
        // Created hidden (no WS_VISIBLE) so we can position it before the first paint; shown at the end.
        // A restored frame can land off every monitor (a display was unplugged / rearranged since save),
        // leaving the window invisible and unreachable — clamp it onto the nearest visible work area first.
        if (_geoValid) ClampGeoToVisibleScreen(ref _geoX, ref _geoY, ref _geoW, ref _geoH);
        _creating = this;                    // so the WindowProc trampoline can resolve us during CreateWindowExW
        _hwnd = _geoValid
            ? CreateWindowExW(0, ClassName, AppName, WS_OVERLAPPEDWINDOW,
                _geoX, _geoY, _geoW, _geoH, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero)
            : CreateWindowExW(0, ClassName, AppName, WS_OVERLAPPEDWINDOW,
                CW_USEDEFAULT, CW_USEDEFAULT, 1040, 660, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        _creating = null;
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowExW failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        _registry[_hwnd] = this;

        // Win11 rounded corners: our custom frame (WM_NCCALCSIZE) suppresses the automatic rounding,
        // so opt in explicitly. Harmless no-op on Win10 / older.
        try { int pref = DWMWCP_ROUND; DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); } catch { }

        // App icon for the window (taskbar + alt-tab); the exe icon comes from <ApplicationIcon>.
        // Single-file portable exe has no assets\ dir — extract the icon compiled into the exe instead.
        try
        {
            IntPtr hIconBig = IntPtr.Zero, hIconSm = IntPtr.Zero;
            string icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "agwinterm.ico");
            if (System.IO.File.Exists(icoPath))
            {
                hIconBig = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                hIconSm = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            }
            else if (Environment.ProcessPath is { } exe)
            {
                ExtractIconExW(exe, 0, out hIconBig, out hIconSm, 1);
            }
            if (hIconBig != IntPtr.Zero) SendMessageW(_hwnd, WM_SETICON, (IntPtr)ICON_BIG, hIconBig);
            if (hIconSm != IntPtr.Zero) SendMessageW(_hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hIconSm);
        }
        catch { /* icon is cosmetic; ignore load failures */ }

        DragAcceptFiles(_hwnd, true);   // drop files/folders onto a pane -> quoted paths pasted

        CreateRenderTarget();
        StartSession();

        ApplySystemTheme();   // if "follow Windows light/dark" is on, pick light/dark before the first paint

        // Apply the custom frame (WM_NCCALCSIZE strips the OS title bar) before showing.
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        // Drives the cursor pulse (when cursor-blink is on) AND the agent-status blink pulse, so it
        // runs unconditionally; the cursor render itself stays solid when cursor-blink is disabled.
        SetTimer(_hwnd, (IntPtr)1, (uint)_config.CursorBlinkMs, IntPtr.Zero);

        ShowWindow(_hwnd, _argMaximized || (_geoValid && _geoMax) ? SW_MAXIMIZE : SW_SHOW);
        if (_argFullscreen) { _argFullscreen = false; ToggleFullscreen(); }   // first window only
        _argMaximized = false;
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

    private IDWriteTypography? _noLig;
    /// <summary>Typography disabling ligature-forming features (calt/liga/clig/dlig/hlig) — the
    /// ligatures=off path. Cached; created lazily on first use.</summary>
    private IDWriteTypography NoLigTypography()
    {
        if (_noLig is null)
        {
            _noLig = _dwrite.CreateTypography();
            foreach (var tag in new[] { FontFeatureTag.StandardLigatures, FontFeatureTag.ContextualAlternates,
                                        FontFeatureTag.ContextualLigatures, FontFeatureTag.DiscretionaryLigatures,
                                        FontFeatureTag.HistoricalLigatures })
                _noLig.AddFontFeature(new FontFeature { NameTag = tag, Parameter = 0 });
        }
        return _noLig;
    }

    /// <summary>Draw a same-colour text run — plain DrawText (font default: ligatures on), or a text
    /// layout with ligature features disabled when <c>ligatures = false</c>.</summary>
    private void DrawRun(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, string text, IDWriteTextFormat fmt,
        float rx, float y, float w, float h)
    {
        if (_config.Ligatures) { rt.DrawText(text, fmt, new Rect(rx, y, rx + w, y + h), brush); return; }
        using var layout = _dwrite.CreateTextLayout(text, fmt, w, h);
        layout.SetTypography(NoLigTypography(), new TextRange(0, (uint)text.Length));
        rt.DrawTextLayout(new System.Numerics.Vector2(rx, y), layout, brush);
    }

    /// <summary>(Re)create the sidebar name/badge fonts from the configured sidebar-font-size. Shared across
    /// windows; called at startup and when the setting changes so it applies live.</summary>
    private static void RebuildSidebarFonts()
    {
        float px = System.Math.Clamp(_config.SidebarFontSize, 9, 20);
        _sidebarFont?.Dispose(); _sidebarSmall?.Dispose();
        try { _sidebarEllipsis?.Dispose(); } catch { }
        _sidebarFont = NewChromeFormat("Segoe UI", px, center: false);
        _sidebarSmall = NewChromeFormat("Segoe UI", System.Math.Max(9f, px - 1.5f), center: false);
        // Trim over-long names with a "…" (esp. once the font is enlarged) so they never spill over the
        // right-aligned status dot. Paired with DrawTextOptions.Clip at the draw sites.
        _sidebarEllipsis = _dwrite.CreateEllipsisTrimmingSign(_sidebarFont);
        _sidebarFont.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, _sidebarEllipsis);
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

    // ---- UIA control tree (fragment tree the reader scans/Tabs through) ----

    private UiaRect ScreenRect(float x, float y, float w, float h)
    {
        var p = new POINT { x = (int)x, y = (int)y };
        ClientToScreen(_hwnd, ref p);
        return new UiaRect { Left = p.x, Top = p.y, Width = Math.Max(0, w), Height = Math.Max(0, h) };
    }

    /// <summary>Build the accessibility tree snapshot: root → [terminal, sidebar → session items], with
    /// names, focus, and on-screen bounding rectangles. Runs on the UIA thread.</summary>
    private Uia.TreeSnapshot BuildUiaTree()
    {
        var nodes = new List<Uia.Node> { new() { Kind = Uia.NodeKind.Root, Name = "agwinterm" } };

        // Help is modal: only the help document is exposed while it's open, named with the full
        // help text so a reader can read the whole guide via the element.
        if (_helpOpen)
        {
            int doc = nodes.Count;
            nodes.Add(new Uia.Node
            {
                Kind = Uia.NodeKind.HelpDoc, Name = HelpText(), Parent = 0, Focused = true,
                Rect = ScreenRect(_helpCard.Left, _helpCard.Top, _helpCard.Right - _helpCard.Left, _helpCard.Bottom - _helpCard.Top),
            });
            nodes[0].Children = new[] { doc };
            return new Uia.TreeSnapshot { Nodes = nodes.ToArray() };
        }

        // Settings is modal: while it's open, expose ONLY the dialog (tabs + the current tab's
        // controls) so a reader can't wander into the inert main-window elements behind it.
        if (_setOpen)
        {
            int grp = nodes.Count;
            nodes.Add(new Uia.Node
            {
                Kind = Uia.NodeKind.SettingsGroup, Name = "Settings", Parent = 0,
                Rect = ScreenRect(_setCard.Left, _setCard.Top, _setCard.Right - _setCard.Left, _setCard.Bottom - _setCard.Top),
            });
            var gkids = new List<int>();
            for (int i = 0; i < SetTabNames.Length; i++)   // tab items (invokable; current one marked)
            {
                gkids.Add(nodes.Count);
                nodes.Add(new Uia.Node
                {
                    Kind = Uia.NodeKind.SettingsTab, Index = i, Parent = grp,
                    Name = SetTabNames[i] + (i == _setTab ? " tab, selected" : " tab"),
                    Selected = i == _setTab,
                    Rect = ScreenRect(_setCard.Left + 8f, _navHit[i * 2], SetNavW - 16f, _navHit[i * 2 + 1] - _navHit[i * 2]),
                });
            }
            var srows = FocusableRows();
            for (int i = 0; i < srows.Count; i++)
            {
                var r = srows[i];
                gkids.Add(nodes.Count);
                nodes.Add(new Uia.Node
                {
                    Kind = Uia.NodeKind.SettingsControl, Index = i, Parent = grp,
                    Name = SettingsControlName(r),
                    Focused = ReferenceEquals(r, _setFocus),
                    Rect = r.Vis ? ScreenRect(r.Hx0, r.Hy0, Math.Max(1, r.Hx1 - r.Hx0), Math.Max(1, r.Hy1 - r.Hy0)) : default,
                });
            }
            nodes[grp].Children = gkids.ToArray();
            nodes[0].Children = new[] { grp };
            return new Uia.TreeSnapshot { Nodes = nodes.ToArray() };
        }

        int term = nodes.Count;
        float contentBottom = ClientH() - FooterH;
        nodes.Add(new Uia.Node
        {
            Kind = Uia.NodeKind.Terminal, Name = "terminal", Parent = 0,
            Focused = !_chromeFocus && !_setOpen,
            Rect = ScreenRect(_sidebarW, TitleBarH, ClientW() - _sidebarW, contentBottom - TitleBarH),
        });

        int list = nodes.Count;
        bool sidebarShown = _sidebarW > 0;
        nodes.Add(new Uia.Node
        {
            Kind = Uia.NodeKind.Sidebar, Name = "Sessions", Parent = 0,
            Rect = sidebarShown ? ScreenRect(0, TitleBarH, _sidebarW, contentBottom - TitleBarH) : default,
        });

        // Map each session to its sidebar row rect (from the last render), for bounding rectangles.
        var rowRect = new Dictionary<Ses, UiaRect>();
        foreach (var (y0, y1, isWs, item) in _sidebarRows)
            if (!isWs && item is Ses s && sidebarShown) rowRect[s] = ScreenRect(0, y0, _sidebarW, y1 - y0);

        var sessions = AllSessions();
        var kids = new List<int>();
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            kids.Add(nodes.Count);
            nodes.Add(new Uia.Node
            {
                Kind = Uia.NodeKind.Session, Index = i, Name = s.Name, Parent = list,
                Focused = _chromeFocus && ReferenceEquals(_focusRow, s),
                Selected = ReferenceEquals(_active, s),
                Rect = rowRect.TryGetValue(s, out var r) ? r : default,
            });
        }
        nodes[list].Children = kids.ToArray();

        var rootKids = new List<int> { term, list };
        // Chrome buttons (title bar + footer) as invokable Button elements.
        var btns = ChromeButtonsForUia();
        for (int i = 0; i < btns.Count; i++)
        {
            rootKids.Add(nodes.Count);
            nodes.Add(new Uia.Node { Kind = Uia.NodeKind.ChromeButton, Index = i, Name = btns[i].label, Parent = 0, Rect = btns[i].rect });
        }
        nodes[0].Children = rootKids.ToArray();
        return new Uia.TreeSnapshot { Nodes = nodes.ToArray() };
    }

    /// <summary>Title-bar + footer chrome buttons as (action, spoken label, screen rect) for the UIA tree.</summary>
    private List<(string action, string label, UiaRect rect)> ChromeButtonsForUia()
    {
        var list = new List<(string, string, UiaRect)>();
        foreach (var (x0, x1, action) in _titleButtons)
            list.Add((action, ChromeButtonLabel(action), ScreenRect(x0, 0, x1 - x0, TitleBarH)));
        float fy = ClientH() - FooterH;
        foreach (var (x0, x1, action) in _footerButtons)
            list.Add((action, ChromeButtonLabel(action), ScreenRect(x0, fy, x1 - x0, FooterH)));
        return list;
    }

    private string SettingsControlName(SetRow r)
    {
        if (r.Kind == SW.Button) return r.Label;
        string val = r.Kind switch
        {
            SW.Toggle => IsOn(ConfigValue(r.Key)) ? "on" : "off",
            SW.Slider => ConfigValue(r.Key),
            SW.Dropdown or SW.Sound => CurrentDropdownText(r),
            SW.Path => ConfigValue(r.Key) is { Length: > 0 } p ? p : "not set",
            SW.Profile => string.Equals(r.Key, _profileCfg.Default, StringComparison.OrdinalIgnoreCase) ? "default" : "not default",
            _ => "",
        };
        return val.Length > 0 ? $"{r.Label}, {val}" : r.Label;
    }

    /// <summary>UIA Invoke on a button/control — run the same action a click/keypress would.</summary>
    private void HandleUiaInvoke(Uia.NodeKind kind, int index)
    {
        if (kind == Uia.NodeKind.ChromeButton)
        {
            var b = ChromeButtonsForUia();
            if (index >= 0 && index < b.Count) ChromeAction(b[index].action);
        }
        else if (kind == Uia.NodeKind.SettingsControl && _setOpen)
        {
            var rows = FocusableRows();
            if (index >= 0 && index < rows.Count) { _setFocus = rows[index]; ActivateSetFocus(); }
        }
        else if (kind == Uia.NodeKind.SettingsTab && _setOpen && index >= 0 && index < SetTabNames.Length)
        {
            _setTab = index; _setScroll = 0;
            _setFocus = FocusableRows().FirstOrDefault();
            Uia.Announce($"{SetTabNames[index]} tab");
            RequestRedraw();
        }
    }

    /// <summary>A UIA client (or Narrator) called SetFocus on a tree element — move our internal focus.</summary>
    private void HandleUiaSetFocus(Uia.NodeKind kind, int index)
    {
        switch (kind)
        {
            case Uia.NodeKind.Terminal: ExitChromeFocus(announce: false); break;
            case Uia.NodeKind.Sidebar: EnterChromeFocus(); break;
            case Uia.NodeKind.Session:
                var list = AllSessions();
                if (index >= 0 && index < list.Count)
                {
                    if (_sidebarW <= 0) ToggleSidebar();
                    _chromeFocus = true; _focusRow = list[index]; RequestRedraw();
                }
                break;
        }
    }

    private void RaiseUiaFocusForRow()
    {
        if (_focusRow is null) return;
        int idx = AllSessions().IndexOf(_focusRow);
        if (idx >= 0) Uia.RaiseFocus(Uia.NodeKind.Session, idx);
    }

    /// <summary>Build the UIA text-pattern snapshot: the active pane's buffer (recent scrollback + the
    /// live screen) flattened into one document, with the caret offset and the mapping from document
    /// lines to on-screen rows (screen px) so ranges can report bounding rectangles. Runs on the UIA
    /// thread; locks the session.</summary>
    private Uia.TextSnapshot? BuildUiaTextSnapshot()
    {
        var p = ActiveSurface();
        if (p is null) return null;
        var (oxC, oyC, cw, ch) = ActivePaneView();
        var origin = new POINT { x = (int)oxC, y = (int)oyC };
        ClientToScreen(_hwnd, ref origin);   // bounding rectangles are screen coords
        lock (p.S.SyncRoot)
        {
            var em = p.S.Emulator;
            int hist = em.HistoryCount, rows = em.Screen.Rows;
            const int MaxHist = 2000;                       // bound the document the reader walks
            int keepHist = Math.Min(hist, MaxHist);
            int histStart = hist - keepHist;
            int n = keepHist + rows;
            var lines = new string[n];
            for (int i = 0; i < keepHist; i++) lines[i] = em.DumpHistoryRow(histStart + i);
            for (int r = 0; r < rows; r++) lines[keepHist + r] = em.DumpRow(r);
            var starts = new int[n + 1];
            for (int i = 0; i < n; i++) starts[i + 1] = starts[i] + lines[i].Length + 1;   // +1 = '\n' joiner
            int total = n > 0 ? starts[n] - 1 : 0;          // no trailing newline after the last line

            int caretLine = keepHist + Math.Clamp(em.CursorRow, 0, rows - 1);
            int caretCol = Math.Min(em.CursorCol, lines[caretLine].Length);
            int caretOff = starts[caretLine] + caretCol;

            int off = em.IsAltScreen ? 0 : Math.Clamp(p.ScrollOffset, 0, hist);
            int firstVisibleDoc = (hist - off) - histStart;  // Lines[] index of the top on-screen row

            return new Uia.TextSnapshot
            {
                Lines = lines, LineStart = starts, TotalLength = total, CaretOffset = caretOff,
                FirstVisibleLine = firstVisibleDoc, VisibleRows = rows,
                ScreenX = origin.x, ScreenY = origin.y, CellW = cw, CellH = ch,
            };
        }
    }

    // ---- Focus zones (F6): move keyboard focus between the terminal and the sidebar ----
    // The terminal owns the keyboard by default (everything goes to the shell). F6 lifts focus OUT
    // to the sidebar so it can be walked without the mouse — the accessible way to leave the terminal.
    private bool _chromeFocus;    // keyboard focus is in the sidebar zone (not the terminal)
    private Ses? _focusRow;       // the focused sidebar session while _chromeFocus

    private void EnterChromeFocus()
    {
        if (_sidebarW <= 0) ToggleSidebar();   // reveal the sidebar so the focus ring is visible
        _chromeFocus = true;
        _focusRow = _active ?? AllSessions().FirstOrDefault();
        Uia.Announce("Sidebar");
        AnnounceFocusRow();
        RequestRedraw();
    }

    private void ExitChromeFocus(bool announce = true)
    {
        if (!_chromeFocus) return;
        _chromeFocus = false;
        if (announce) { Uia.Announce("Terminal"); Uia.RaiseFocus(Uia.NodeKind.Terminal, 0); }
        RequestRedraw();
    }

    private void AnnounceFocusRow()
    {
        if (_focusRow is null) return;
        ShowToast(_focusRow.Name);   // a visible hint alongside the spoken one
        RaiseUiaFocusForRow();       // move UIA focus to this element (Narrator announces it)
        bool current = ReferenceEquals(_focusRow, _active);
        int unread = UnreadOf(_focusRow);
        string extra = (current ? ", current" : "") + (unread > 0 ? $", {unread} unread" : "");
        Uia.Announce($"{_focusRow.Name}, session{extra}");
    }

    /// <summary>Keyboard handling while the sidebar zone has focus (F6). Up/Down walk sessions, Enter/Space
    /// opens one, Escape/F6 returns to the terminal. Swallows everything else so it can't reach the shell.</summary>
    private bool SidebarZoneKey(int vk)
    {
        var list = AllSessions();
        if (list.Count == 0) { ExitChromeFocus(); return true; }
        int idx = _focusRow is not null ? list.IndexOf(_focusRow) : 0;
        if (idx < 0) idx = 0;
        switch (vk)
        {
            case VK_DOWN: _focusRow = list[Math.Min(idx + 1, list.Count - 1)]; AnnounceFocusRow(); RequestRedraw(); return true;
            case VK_UP: _focusRow = list[Math.Max(idx - 1, 0)]; AnnounceFocusRow(); RequestRedraw(); return true;
            case VK_HOME: _focusRow = list[0]; AnnounceFocusRow(); RequestRedraw(); return true;
            case VK_END: _focusRow = list[^1]; AnnounceFocusRow(); RequestRedraw(); return true;
            case VK_RETURN: case VK_SPACE:
                if (_focusRow is not null) SetActive(_focusRow);
                ExitChromeFocus();
                return true;
            case VK_ESCAPE: case 0x75 /* F6 */: ExitChromeFocus(); return true;
        }
        return true;
    }

    // ---- System caret (accessibility: Magnifier follow, IME placement, screen-reader caret) ----
    // We render our own cursor, so the system caret is created HIDDEN — it exists only so assistive
    // tech can read the text-cursor position (via GetCaretPos / the caret's location-change events).
    private bool _caretOwned;

    private void EnsureCaret()
    {
        if (_caretOwned || _hwnd == IntPtr.Zero) return;
        var (_, _, cw, ch) = ActivePaneView();
        if (CreateCaret(_hwnd, IntPtr.Zero, Math.Max(1, (int)cw), Math.Max(1, (int)ch)))
        {
            _caretOwned = true;
            HideCaret(_hwnd);   // don't double-draw over our rendered cursor
            UpdateCaretPos();
        }
    }

    private void DropCaret()
    {
        if (!_caretOwned) return;
        DestroyCaret();
        _caretOwned = false;
    }

    /// <summary>Move the hidden system caret onto the active pane's text cursor (client px).</summary>
    private void UpdateCaretPos()
    {
        if (!_caretOwned || _active is null) return;
        var (ox, oy, cw, ch) = ActivePaneView();
        var p = ActiveSurface();
        if (p is null) return;
        int col, row;
        lock (p.S.SyncRoot) { col = p.S.Emulator.CursorCol; row = p.S.Emulator.CursorRow; }
        SetCaretPos((int)(ox + col * cw), (int)(oy + row * ch));
    }

    /// <summary>Apply a font-family / default-font-size change live: rebuild the base format, dispose and
    /// clear the per-size metrics cache (each entry bakes in the family), remeasure, and reflow every
    /// session so the change is visible immediately instead of only on new sessions.</summary>
    private void RebuildFont()
    {
        var old = _format;
        _format = CreateTextFormat(_config);
        try { old?.Dispose(); } catch { }
        foreach (var m in _metrics.Values) { try { m.Fmt.Dispose(); } catch { } }   // COM formats — dispose before dropping
        _metrics.Clear();
        MeasureCell();
        foreach (var s in AllSessions()) RegridSession(s);
        if (_cover is not null) RegridCover();
        RequestRedraw();
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
            _control = new ControlServer(this, this, _argPipe ?? _appId);
            _control.Start();
            // Default-terminal handoff (T2-13): register the COM class factory so conhost can hand
            // console sessions to this instance. Each handoff opens a new attached session.
            DefTerm.OnHandoff = args => Post(() =>
                CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), makeActive: true, handoff: args));
            // Dev builds don't register as the default-terminal COM server — that CLSID is owned by the
            // installed release, and a dev instance must not intercept the OS's console handoffs.
            if (!IsDev) { try { DefTerm.RegisterServer(); } catch { /* defterm not registered / unsupported */ } }
            // UIA (T2-14): expose the active pane's visible text to screen readers.
            Uia.GetVisibleText = () =>
            {
                var p = ActiveSurface();
                if (p is null) return "terminal";
                lock (p.S.SyncRoot)
                {
                    var em = p.S.Emulator; var sb = new StringBuilder();
                    for (int r = 0; r < em.Screen.Rows; r++) sb.Append(em.DumpRow(r)).Append('\n');
                    return sb.ToString().TrimEnd() is { Length: > 0 } t ? t : "terminal";
                }
            };
            // Full text-pattern snapshot (ITextProvider): the flattened buffer document + caret +
            // visible-line mapping, so Narrator can read/navigate by line/word/char and box the caret.
            Uia.GetTextSnapshot = BuildUiaTextSnapshot;
            // Fragment tree (root → terminal + sidebar/sessions) so the reader scans/Tabs the controls.
            Uia.GetTree = BuildUiaTree;
            Uia.OnSetFocus = (kind, index) => Post(() => HandleUiaSetFocus(kind, index));
            Uia.OnInvoke = (kind, index) => Post(() => HandleUiaInvoke(kind, index));
        }
        // CLI args (first window only; consumed so extra windows don't re-apply them).
        string? argProfile = _argProfile, argDir = _argDir;
        _argProfile = null; _argDir = null;

        // --no-restore (elevated relaunch): a fresh window with just the requested profile, no tree reopen.
        if (!_argNoRestore && TryRestoreState())
        {
            // Restored: an explicit -p/-d still means "and open me this session".
            if (argProfile is not null || argDir is not null)
                CreateSession(Guid.NewGuid().ToString(), null, argDir, ActiveWorkspace(), makeActive: true, profileName: argProfile);
            return;
        }
        var ws = CreateWorkspace(Guid.NewGuid().ToString(), null);
        CreateSession(Guid.NewGuid().ToString(), null, argDir, ws, makeActive: true, profileName: argProfile);
    }
}
