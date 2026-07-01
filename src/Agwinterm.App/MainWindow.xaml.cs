using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Agwinterm.Core;
using Agwinterm.Pty;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.System;
using WinColor = Windows.UI.Color;

namespace Agwinterm.App;

public sealed partial class MainWindow : Window, ISessionHost
{
    private const float PadX = 8f;
    private const float PadY = 6f;

    private sealed class Ses
    {
        public required string Id;
        public required string Name;
        public required TerminalSession S;
        public required Workspace Ws;
    }

    private sealed class Workspace
    {
        public required string Id;
        public required string Name;
        public readonly List<Ses> Sessions = new();
        public bool Expanded = true;
    }

    private readonly List<Workspace> _workspaces = new(); // source of truth; guarded by lock (_workspaces)
    private Ses? _activeRef;
    private object? _editing; // Ses or Workspace currently being renamed inline
    private TerminalSession? _session;   // mirrors the active session (rendering/input use this)
    private bool _started;
    private ControlServer? _control;

    private readonly string _windowId = Guid.NewGuid().ToString();
    private readonly TerminalConfig _config;
    private readonly CanvasTextFormat _format;
    private bool _cursorOn = true;

    private float _cellW;
    private float _cellH;
    private bool _measured;
    private static readonly bool _noImages = Environment.GetEnvironmentVariable("AGWINTERM_NOIMG") == "1";

    private static string ConfigPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agwinterm", "agwinterm.conf");

    private readonly Dictionary<(string, int), CanvasBitmap> _imageCache = new();
    private readonly HashSet<(string, int)> _decoding = new();

    public MainWindow()
    {
        this.InitializeComponent();
        AppWindow.Resize(new SizeInt32(1040, 620));
        Title = "agwinterm";

        _config = LoadOrCreateConfig();
        _format = new CanvasTextFormat
        {
            FontFamily = _config.FontFamily,
            FontSize = (float)_config.FontSize,
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top,
        };

        if (_config.CursorBlink)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(_config.CursorBlinkMs);
            timer.Tick += (_, _) => { _cursorOn = !_cursorOn; GridCanvas.Invalidate(); };
            timer.Start();
        }

        // Custom title bar: extend content into the caption; the title-bar grid is the drag region.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Make the native caption buttons (min/max/close) visible on the dark bar.
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = Colors.White;
        tb.ButtonInactiveForegroundColor = WinColor.FromArgb(255, 138, 145, 153);
        tb.ButtonHoverForegroundColor = Colors.White;
        tb.ButtonHoverBackgroundColor = WinColor.FromArgb(48, 255, 255, 255);
        tb.ButtonPressedForegroundColor = Colors.White;
        tb.ButtonPressedBackgroundColor = WinColor.FromArgb(80, 255, 255, 255);
        SidebarToggle.Click += (_, _) => { ToggleSidebar(); GridCanvas.Focus(FocusState.Programmatic); };
        AttentionBell.Click += (_, _) => { GoToNextAttention(); };

        NewWorkspaceButton.Click += (_, _) => { CreateWorkspace(Guid.NewGuid().ToString(), null); GridCanvas.Focus(FocusState.Programmatic); };
        NewSessionButton.Click += (_, _) => CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), makeActive: true);
        FlagButton.Click += (_, _) => ToggleCollapseAll();

        GridCanvas.Loaded += (_, _) => GridCanvas.Focus(FocusState.Programmatic);
        this.Activated += (_, _) => GridCanvas.Focus(FocusState.Programmatic);
        GridCanvas.CharacterReceived += OnCharacterReceived;
        GridCanvas.KeyDown += OnKeyDown;
        GridCanvas.SizeChanged += OnSizeChanged;
        GridCanvas.PointerPressed += OnPointerPressed;
        GridCanvas.PointerReleased += OnPointerReleased;
        GridCanvas.PointerWheelChanged += OnPointerWheel;
        GridCanvas.PointerMoved += OnPointerMoved;
    }

    private static TerminalConfig LoadOrCreateConfig()
    {
        try
        {
            string path = ConfigPath;
            if (!System.IO.File.Exists(path))
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllText(path, TerminalConfig.DefaultText);
            }
            return TerminalConfig.Load(path);
        }
        catch { return new TerminalConfig(); }
    }

    private (int Cols, int Rows) ComputeGridSize()
    {
        int cols = Math.Max(1, (int)((GridCanvas.ActualWidth - 2 * PadX) / _cellW));
        int rows = Math.Max(1, (int)((GridCanvas.ActualHeight - 2 * PadY) / _cellH));
        return (cols, rows);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_measured) return;
        var (cols, rows) = ComputeGridSize();
        foreach (var s in AllSessions())
            if (cols != s.S.Cols || rows != s.S.Rows)
                s.S.Resize(cols, rows);
        GridCanvas.Invalidate();
    }

    // ---- Model helpers ----

    private List<Ses> AllSessions()
    {
        lock (_workspaces) return _workspaces.SelectMany(w => w.Sessions).ToList();
    }

    private Workspace ActiveWorkspace()
    {
        lock (_workspaces)
        {
            if (_activeRef is not null) return _activeRef.Ws;
            if (_workspaces.Count == 0) _workspaces.Add(new Workspace { Id = Guid.NewGuid().ToString(), Name = "workspace 1" });
            return _workspaces[0];
        }
    }

    // ---- Lifecycle (UI thread) ----

    private void StartControlServerOnce()
    {
        if (_control is not null) return;
        _control = new ControlServer(this, "agwinterm");
        _control.Start();
    }

    private Workspace CreateWorkspace(string id, string? name)
    {
        Workspace ws;
        lock (_workspaces)
        {
            ws = new Workspace { Id = id, Name = name ?? $"workspace {_workspaces.Count + 1}" };
            _workspaces.Add(ws);
        }
        RebuildSidebar();
        return ws;
    }

    private void CreateSession(string id, string? name, string? cwd, Workspace ws, bool makeActive)
    {
        StartControlServerOnce();
        var (cols, rows) = ComputeGridSize();
        var session = new TerminalSession(cols, rows);
        int ordinal;
        lock (_workspaces) ordinal = ws.Sessions.Count + 1;
        var ses = new Ses { Id = id, Name = name ?? $"session {ordinal}", S = session, Ws = ws };

        session.OutputReceived += () => DispatcherQueue.TryEnqueue(async () =>
        {
            if (ReferenceEquals(_activeRef, ses)) { await DecodeNewImagesAsync(); GridCanvas.Invalidate(); UpdateChrome(); }
        });
        session.StatusChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            RebuildSidebar();
            if (ReferenceEquals(_activeRef, ses)) UpdateTitle(); else UpdateChrome();
        });

        lock (_workspaces) { ws.Sessions.Add(ses); ws.Expanded = true; }

        var env = new Dictionary<string, string>
        {
            ["AGWINTERM"] = "1",
            ["AGWINTERM_ENABLED"] = "1",
            ["AGWINTERM_PIPE"] = _control!.PipeName,
            ["AGWINTERM_SESSION_ID"] = id,
            ["AGWINTERM_WORKSPACE_ID"] = ws.Id,
            ["AGWINTERM_WINDOW_ID"] = _windowId,
        };
        _ = StartSessionAsync(session, env, cwd);

        if (makeActive || _activeRef is null) SetActive(ses);
        else RebuildSidebar();
    }

    private async Task StartSessionAsync(TerminalSession session, Dictionary<string, string> env, string? cwd)
    {
        try { await session.StartAsync("powershell.exe", new[] { "-NoLogo" }, extraEnv: env, cwd: cwd); }
        catch (Exception ex) { Title = "agwinterm — shell failed: " + ex.Message; }
    }

    private void SetActive(Ses ses)
    {
        _activeRef = ses;
        _session = ses.S;
        UpdateTitle();
        RebuildSidebar();
        _ = DecodeNewImagesAsync();
        GridCanvas.Invalidate();
        DispatcherQueue.TryEnqueue(() => GridCanvas.Focus(FocusState.Programmatic));
    }

    private void CloseSessionInternal(Ses ses)
    {
        try { ses.S.Dispose(); } catch { }
        bool wasActive = ReferenceEquals(_activeRef, ses);
        lock (_workspaces) ses.Ws.Sessions.Remove(ses);

        if (wasActive)
        {
            Ses? next = AllSessions().FirstOrDefault();
            if (next is not null) SetActive(next);
            else CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), makeActive: true);
        }
        RebuildSidebar();
        GridCanvas.Invalidate();
    }

    private void CycleSession(int dir)
    {
        var all = AllSessions();
        if (all.Count < 2) return;
        int i = _activeRef is null ? 0 : all.IndexOf(_activeRef);
        SetActive(all[((i + dir) % all.Count + all.Count) % all.Count]);
    }

    private void ToggleCollapseAll()
    {
        lock (_workspaces)
        {
            bool anyExpanded = _workspaces.Any(w => w.Expanded);
            foreach (var w in _workspaces) w.Expanded = !anyExpanded;
        }
        RebuildSidebar();
    }

    private static WinColor StatusColor(AgentStatus s) => s switch
    {
        AgentStatus.Active => WinColor.FromArgb(255, 60, 140, 255),
        AgentStatus.Blocked => WinColor.FromArgb(255, 240, 160, 40),
        AgentStatus.Completed => WinColor.FromArgb(255, 60, 200, 90),
        _ => WinColor.FromArgb(255, 90, 96, 102),
    };

    // ---- Hierarchical sidebar (agterm workspace→session outline) ----

    private void RebuildSidebar()
    {
        SidebarPanel.Children.Clear();
        Workspace[] wss;
        lock (_workspaces) wss = _workspaces.ToArray();

        foreach (var ws in wss)
        {
            SidebarPanel.Children.Add(BuildWorkspaceRow(ws));
            if (!ws.Expanded) continue;
            Ses[] sessions;
            lock (_workspaces) sessions = ws.Sessions.ToArray();
            foreach (var ses in sessions)
                SidebarPanel.Children.Add(BuildSessionRow(ses));
        }
    }

    private FrameworkElement BuildRenameBox(string name, Action<string> commit)
    {
        var tb = new TextBox { Text = name, Margin = new Thickness(2), FontSize = 13, MinWidth = 120 };
        tb.Loaded += (_, _) => { tb.Focus(FocusState.Programmatic); tb.SelectAll(); };
        tb.KeyDown += (s, e) =>
        {
            if (e.Key == VirtualKey.Enter) { commit(tb.Text); e.Handled = true; }
            else if (e.Key == VirtualKey.Escape) { _editing = null; RebuildSidebar(); GridCanvas.Focus(FocusState.Programmatic); e.Handled = true; }
        };
        tb.LostFocus += (_, _) => { if (_editing is not null) commit(tb.Text); };
        return tb;
    }

    private void StartRename(object item) { _editing = item; RebuildSidebar(); }

    private FrameworkElement BuildWorkspaceRow(Workspace ws)
    {
        if (ReferenceEquals(_editing, ws))
            return BuildRenameBox(ws.Name, n => { if (!string.IsNullOrWhiteSpace(n)) ws.Name = n.Trim(); _editing = null; RebuildSidebar(); UpdateChrome(); GridCanvas.Focus(FocusState.Programmatic); });

        int count;
        lock (_workspaces) count = ws.Sessions.Count;
        var tri = new TextBlock { Text = ws.Expanded ? "▾" : "▸", Foreground = new SolidColorBrush(Colors.Gray), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var name = new TextBlock { Text = ws.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White), VerticalAlignment = VerticalAlignment.Center };
        var num = new TextBlock { Text = count.ToString(), Foreground = new SolidColorBrush(Colors.Gray), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(tri); row.Children.Add(name); row.Children.Add(num);

        var btn = new Button
        {
            Content = row,
            Tag = ws,
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 6, 4),
        };
        btn.Click += (_, _) => { lock (_workspaces) ws.Expanded = !ws.Expanded; RebuildSidebar(); };
        btn.DoubleTapped += (_, _) => StartRename(ws);
        btn.ContextFlyout = WorkspaceMenu(ws);
        return btn;
    }

    private FrameworkElement BuildSessionRow(Ses ses)
    {
        if (ReferenceEquals(_editing, ses))
            return BuildRenameBox(ses.Name, n => { if (!string.IsNullOrWhiteSpace(n)) ses.Name = n.Trim(); _editing = null; RebuildSidebar(); UpdateChrome(); GridCanvas.Focus(FocusState.Programmatic); });

        bool active = ReferenceEquals(ses, _activeRef);
        // Persistent status dot: dim grey when idle, colored for active/blocked/completed.
        var dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Fill = new SolidColorBrush(StatusColor(ses.S.Status)) };
        var name = new TextBlock { Text = ses.Name, Foreground = new SolidColorBrush(Colors.White), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0) };
        row.Children.Add(dot); row.Children.Add(name);

        var btn = new Button
        {
            Content = row,
            Tag = ses,
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 3, 6, 3),
            Background = active ? new SolidColorBrush(WinColor.FromArgb(255, 38, 44, 52)) : new SolidColorBrush(Colors.Transparent),
        };
        btn.Click += (_, _) => SetActive(ses);
        btn.DoubleTapped += (_, _) => StartRename(ses);
        btn.ContextFlyout = SessionMenu(ses);
        return btn;
    }

    private MenuFlyout WorkspaceMenu(Workspace ws)
    {
        var m = new MenuFlyout();
        var newSes = new MenuFlyoutItem { Text = "New Session" };
        newSes.Click += (_, _) => CreateSession(Guid.NewGuid().ToString(), null, null, ws, true);
        var rename = new MenuFlyoutItem { Text = "Rename" };
        rename.Click += (_, _) => StartRename(ws);
        var del = new MenuFlyoutItem { Text = "Delete Workspace" };
        del.Click += (_, _) => DeleteWorkspace(ws);
        lock (_workspaces) del.IsEnabled = _workspaces.Count > 1;
        m.Items.Add(newSes);
        m.Items.Add(rename);
        m.Items.Add(del);
        return m;
    }

    private MenuFlyout SessionMenu(Ses ses)
    {
        var m = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Rename" };
        rename.Click += (_, _) => StartRename(ses);
        m.Items.Add(rename);

        // Move to ▸ [other workspaces]
        Workspace[] others;
        lock (_workspaces) others = _workspaces.Where(w => !ReferenceEquals(w, ses.Ws)).ToArray();
        if (others.Length > 0)
        {
            var move = new MenuFlyoutSubItem { Text = "Move to" };
            foreach (var w in others)
            {
                var it = new MenuFlyoutItem { Text = w.Name };
                var target = w;
                it.Click += (_, _) => MoveSession(ses, target);
                move.Items.Add(it);
            }
            m.Items.Add(move);
        }

        if (ses.S.Status != AgentStatus.Idle)
        {
            var clear = new MenuFlyoutItem { Text = "Clear Status" };
            clear.Click += (_, _) => ses.S.SetStatus(AgentStatus.Idle);
            m.Items.Add(clear);
        }
        var close = new MenuFlyoutItem { Text = "Close Session" };
        close.Click += (_, _) => CloseSessionInternal(ses);
        m.Items.Add(close);
        return m;
    }

    private void MoveSession(Ses ses, Workspace target)
    {
        lock (_workspaces)
        {
            ses.Ws.Sessions.Remove(ses);
            ses.Ws = target;
            target.Sessions.Add(ses);
            target.Expanded = true;
        }
        RebuildSidebar();
    }

    private void DeleteWorkspace(Workspace ws)
    {
        lock (_workspaces) { if (_workspaces.Count <= 1) return; }
        foreach (var s in ws.Sessions.ToArray()) { try { s.S.Dispose(); } catch { } }
        bool hadActive = _activeRef is not null && ReferenceEquals(_activeRef.Ws, ws);
        lock (_workspaces) _workspaces.Remove(ws);
        if (hadActive)
        {
            var next = AllSessions().FirstOrDefault();
            if (next is not null) SetActive(next);
            else CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), true);
        }
        RebuildSidebar();
    }

    // ---- ISessionHost (control-server threads) ----

    private Ses? Find(string? target)
    {
        lock (_workspaces)
        {
            if (string.IsNullOrEmpty(target) || target == "active") return _activeRef;
            var all = _workspaces.SelectMany(w => w.Sessions).ToList();
            return all.FirstOrDefault(x => x.Id == target) ?? all.FirstOrDefault(x => x.Id.StartsWith(target));
        }
    }

    public TerminalSession? Resolve(string? target) => Find(target)?.S;

    public IReadOnlyList<WorkspaceSnapshot> Tree()
    {
        lock (_workspaces)
            return _workspaces.Select(w => new WorkspaceSnapshot(
                w.Id, w.Name,
                _activeRef is not null && ReferenceEquals(_activeRef.Ws, w),
                w.Sessions.Select(s => new SessionSnapshot(s.Id, s.Name, ReferenceEquals(s, _activeRef), s.S.Status)).ToList()
            )).ToList();
    }

    public string NewSession(string? name, string? cwd, string? workspace)
    {
        string id = Guid.NewGuid().ToString();
        DispatcherQueue.TryEnqueue(() =>
        {
            Workspace ws;
            if (!string.IsNullOrEmpty(workspace))
            {
                lock (_workspaces)
                    ws = _workspaces.FirstOrDefault(w => w.Id == workspace)
                         ?? _workspaces.FirstOrDefault(w => w.Id.StartsWith(workspace)) ?? ActiveWorkspace();
            }
            else ws = ActiveWorkspace();
            CreateSession(id, name, cwd, ws, makeActive: true);
        });
        return id;
    }

    public bool SelectSession(string target)
    {
        var ses = Find(target);
        if (ses is null) return false;
        DispatcherQueue.TryEnqueue(() => SetActive(ses));
        return true;
    }

    public bool CloseSession(string target)
    {
        var ses = Find(target);
        if (ses is null) return false;
        DispatcherQueue.TryEnqueue(() => CloseSessionInternal(ses));
        return true;
    }

    public string NewWorkspace(string? name)
    {
        string id = Guid.NewGuid().ToString();
        DispatcherQueue.TryEnqueue(() => CreateWorkspace(id, name));
        return id;
    }

    private void UpdateTitle()
    {
        string glyph = _session?.Status switch
        {
            AgentStatus.Active => "🔵 ",
            AgentStatus.Blocked => "🟠 ",
            AgentStatus.Completed => "🟢 ",
            _ => "",
        };
        Title = glyph + "agwinterm";
        UpdateChrome();
    }

    private bool _sidebarVisible = true;

    private void ToggleSidebar()
    {
        _sidebarVisible = !_sidebarVisible;
        Sidebar.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = _sidebarVisible ? new GridLength(200) : new GridLength(0);
        GridCanvas.Invalidate();
    }

    private void GoToNextAttention()
    {
        var all = AllSessions();
        var attention = all.Where(s => s.S.Status != AgentStatus.Idle).ToList();
        if (attention.Count == 0) return;
        int start = _activeRef is null ? -1 : attention.IndexOf(_activeRef);
        SetActive(attention[(start + 1) % attention.Count]);
    }

    /// <summary>Update the title-bar title/subtitle (cwd) and the 3-state attention bell.</summary>
    private void UpdateChrome()
    {
        TitleText.Text = _activeRef?.Name ?? "agwinterm";
        SubtitleText.Text = _activeRef is not null ? PrettyCwd(_activeRef.S.Emulator.Cwd) : "";

        var all = AllSessions();
        bool blocked = all.Any(s => s.S.Status == AgentStatus.Blocked);
        bool attention = all.Any(s => s.S.Status != AgentStatus.Idle);
        BellIcon.Opacity = attention ? 1.0 : 0.5;
        BellIcon.Foreground = new SolidColorBrush(
            blocked ? WinColor.FromArgb(255, 240, 160, 40)
                    : attention ? Colors.White : WinColor.FromArgb(255, 138, 145, 153));
        ToolTipService.SetToolTip(AttentionBell,
            attention ? "Show sessions that need attention" : "No sessions need attention");
    }

    private static string PrettyCwd(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "";
        try
        {
            if (cwd.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return new Uri(cwd).LocalPath;
        }
        catch { }
        return cwd;
    }

    // ---- Input ----

    private void Send(string s)
    {
        _session?.NotifyActivity();
        _session?.Write(Encoding.UTF8.GetBytes(s));
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        char c = args.Character;
        if (c >= 0x20 && c != 0x7f) Send(c.ToString());
    }

    private static bool KeyHeld(VirtualKey k)
        => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k)
              .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = KeyHeld(VirtualKey.Control);
        bool shift = KeyHeld(VirtualKey.Shift);
        bool alt = KeyHeld(VirtualKey.Menu);

        if (ctrl && e.Key == VirtualKey.Tab) { CycleSession(shift ? -1 : 1); e.Handled = true; return; }
        if (ctrl && shift && e.Key == VirtualKey.T) { CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), true); e.Handled = true; return; }
        if (ctrl && shift && e.Key == VirtualKey.N) { CreateWorkspace(Guid.NewGuid().ToString(), null); e.Handled = true; return; }
        if (ctrl && shift && e.Key == VirtualKey.W && _activeRef is not null) { CloseSessionInternal(_activeRef); e.Handled = true; return; }

        if (_session is null) return;

        if (ctrl && !alt)
        {
            if (e.Key >= VirtualKey.A && e.Key <= VirtualKey.Z)
            {
                Send(((char)(e.Key - VirtualKey.A + 1)).ToString());
                e.Handled = true;
                return;
            }
            if (e.Key == VirtualKey.Space) { Send("\0"); e.Handled = true; return; }
        }

        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string m = mod > 1 ? $"1;{mod}" : "";

        string? seq = e.Key switch
        {
            VirtualKey.Enter => "\r",
            VirtualKey.Back => "\x7f",
            VirtualKey.Tab => shift ? "\x1b[Z" : "\t",
            VirtualKey.Escape => "\x1b",
            VirtualKey.Up => $"\x1b[{m}A",
            VirtualKey.Down => $"\x1b[{m}B",
            VirtualKey.Right => $"\x1b[{m}C",
            VirtualKey.Left => $"\x1b[{m}D",
            VirtualKey.Home => $"\x1b[{m}H",
            VirtualKey.End => $"\x1b[{m}F",
            VirtualKey.PageUp => mod > 1 ? $"\x1b[5;{mod}~" : "\x1b[5~",
            VirtualKey.PageDown => mod > 1 ? $"\x1b[6;{mod}~" : "\x1b[6~",
            VirtualKey.Insert => "\x1b[2~",
            VirtualKey.Delete => "\x1b[3~",
            VirtualKey.F1 => "\x1bOP",
            VirtualKey.F2 => "\x1bOQ",
            VirtualKey.F3 => "\x1bOR",
            VirtualKey.F4 => "\x1bOS",
            VirtualKey.F5 => "\x1b[15~",
            VirtualKey.F6 => "\x1b[17~",
            VirtualKey.F7 => "\x1b[18~",
            VirtualKey.F8 => "\x1b[19~",
            VirtualKey.F9 => "\x1b[20~",
            VirtualKey.F10 => "\x1b[21~",
            VirtualKey.F11 => "\x1b[23~",
            VirtualKey.F12 => "\x1b[24~",
            _ => null,
        };
        if (seq is not null) { Send(seq); e.Handled = true; }
    }

    // ---- Mouse ----

    private (int Col, int Row) CellAt(Windows.Foundation.Point p)
        => (Math.Max(0, (int)((p.X - PadX) / _cellW)), Math.Max(0, (int)((p.Y - PadY) / _cellH)));

    private static int ButtonFromKind(Microsoft.UI.Input.PointerUpdateKind k) => k switch
    {
        Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed or Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased => 0,
        Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed or Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased => 1,
        Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed or Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased => 2,
        _ => -1,
    };

    private void SendMouse(int btn, int col, int row, bool press)
    {
        var em = _session?.Emulator;
        if (em is null || !em.MouseReporting) return;
        col = Math.Clamp(col, 0, em.Screen.Cols - 1);
        row = Math.Clamp(row, 0, em.Screen.Rows - 1);
        string seq = em.MouseSgr
            ? $"\x1b[<{btn};{col + 1};{row + 1}{(press ? 'M' : 'm')}"
            : "\x1b[M" + (char)(32 + (press ? btn : 3)) + (char)(33 + col) + (char)(33 + row);
        _session?.Write(Encoding.UTF8.GetBytes(seq));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        GridCanvas.Focus(FocusState.Programmatic);
        var em = _session?.Emulator;
        if (em is null || !em.MouseReporting) return;
        var pt = e.GetCurrentPoint(GridCanvas);
        int btn = ButtonFromKind(pt.Properties.PointerUpdateKind);
        if (btn < 0) return;
        var (col, row) = CellAt(pt.Position);
        SendMouse(btn, col, row, press: true);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var em = _session?.Emulator;
        if (em is null || !em.MouseReporting) return;
        var pt = e.GetCurrentPoint(GridCanvas);
        int btn = ButtonFromKind(pt.Properties.PointerUpdateKind);
        if (btn < 0) return;
        var (col, row) = CellAt(pt.Position);
        SendMouse(btn, col, row, press: false);
        e.Handled = true;
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var em = _session?.Emulator;
        if (em is null || !em.MouseReporting) return;
        var pt = e.GetCurrentPoint(GridCanvas);
        int btn = pt.Properties.MouseWheelDelta > 0 ? 64 : 65;
        var (col, row) = CellAt(pt.Position);
        SendMouse(btn, col, row, press: true);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var em = _session?.Emulator;
        if (em is null || !em.MouseReportsMotion) return;
        var pt = e.GetCurrentPoint(GridCanvas);
        int btn = 32 + (pt.Properties.IsLeftButtonPressed ? 0 : 3);
        var (col, row) = CellAt(pt.Position);
        SendMouse(btn, col, row, press: true);
        e.Handled = true;
    }

    // ---- Images ----

    private static WinColor ToWin(Color c) => WinColor.FromArgb(255, c.R, c.G, c.B);

    private async Task DecodeNewImagesAsync()
    {
        var active = _activeRef;
        if (active is null) return;
        var todo = new List<KittyImage>();
        lock (active.S.SyncRoot)
        {
            foreach (var p in active.S.Emulator.Placements)
            {
                var key = (active.Id, p.ImageId);
                if (_imageCache.ContainsKey(key) || _decoding.Contains(key)) continue;
                if (active.S.Emulator.Images.TryGetValue(p.ImageId, out var img)) { _decoding.Add(key); todo.Add(img); }
            }
        }
        foreach (var img in todo)
        {
            var key = (active.Id, img.Id);
            try { _imageCache[key] = await DecodeAsync(img); }
            catch { }
            finally { _decoding.Remove(key); }
        }
        if (todo.Count > 0) GridCanvas.Invalidate();
    }

    private async Task<CanvasBitmap> DecodeAsync(KittyImage img)
    {
        if (img.Format == KittyFormat.Png)
        {
            using var ms = new MemoryStream(img.Data);
            return await CanvasBitmap.LoadAsync(GridCanvas, ms.AsRandomAccessStream());
        }
        byte[] rgba = img.Format == KittyFormat.Rgba ? img.Data : ExpandRgbToRgba(img.Data);
        return CanvasBitmap.CreateFromBytes(GridCanvas, rgba, img.Width, img.Height,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
    }

    private static byte[] ExpandRgbToRgba(byte[] rgb)
    {
        var rgba = new byte[rgb.Length / 3 * 4];
        for (int i = 0, j = 0; i + 2 < rgb.Length; i += 3, j += 4)
        {
            rgba[j] = rgb[i]; rgba[j + 1] = rgb[i + 1]; rgba[j + 2] = rgb[i + 2]; rgba[j + 3] = 255;
        }
        return rgba;
    }

    // ---- Render ----

    private void EnsureMetrics(ICanvasResourceCreator rc)
    {
        if (_measured) return;
        using var run = new CanvasTextLayout(rc, new string('M', 10), _format, 0f, 0f);
        using var one = new CanvasTextLayout(rc, "M", _format, 0f, 0f);
        _cellW = MathF.Round((float)run.LayoutBounds.Width / 10f);
        _cellH = MathF.Round((float)one.LayoutBounds.Height);
        _measured = true;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        EnsureMetrics(sender);

        if (!_started && _measured)
        {
            _started = true;
            CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), makeActive: true);
        }

        var active = _activeRef;
        var ds = args.DrawingSession;
        ds.Clear(ToWin(Color.DefaultBackground));
        if (active is null) return;

        lock (active.S.SyncRoot)
        {
            var em = active.S.Emulator;
            var screen = em.Screen;
            int cursorRow = em.CursorRow;
            int cursorCol = em.CursorCol;

            for (int r = 0; r < screen.Rows; r++)
            {
                for (int col = 0; col < screen.Cols; col++)
                {
                    Cell cell = screen[r, col];
                    float x = PadX + col * _cellW;
                    float y = PadY + r * _cellH;

                    bool inverse = cell.Attributes.HasFlag(CellAttributes.Inverse);
                    Color fgc = inverse ? cell.Background : cell.Foreground;
                    Color bgc = inverse ? cell.Foreground : cell.Background;

                    if (bgc != Color.DefaultBackground)
                        ds.FillRectangle(x, y, _cellW, _cellH, ToWin(bgc));

                    if (cell.Rune != ' ' && cell.Rune != '\0')
                    {
                        float glyphW = cell.Width == 2 ? _cellW * 2 : _cellW;
                        using var layout = new CanvasTextLayout(sender, cell.Rune.ToString(), _format, glyphW, _cellH);
                        ds.DrawTextLayout(layout, x, y, ToWin(fgc));
                    }
                }
            }

            if (!_noImages)
            {
                foreach (var p in em.Placements)
                {
                    if (_imageCache.TryGetValue((active.Id, p.ImageId), out var bmp))
                    {
                        float ix = PadX + p.Col * _cellW;
                        float iy = PadY + p.Row * _cellH;
                        double iw = p.Cols > 0 ? p.Cols * _cellW : bmp.Size.Width;
                        double ih = p.Rows > 0 ? p.Rows * _cellH : bmp.Size.Height;
                        ds.DrawImage(bmp, new Windows.Foundation.Rect(ix, iy, iw, ih));
                    }
                }
            }

            bool showCursor = em.CursorVisible && (!_config.CursorBlink || _cursorOn);
            if (showCursor)
            {
                float cx = PadX + cursorCol * _cellW;
                float cy = PadY + cursorRow * _cellH;
                var cur = WinColor.FromArgb(255, 222, 222, 230);
                switch (_config.CursorStyle)
                {
                    case CursorStyle.Block:
                        ds.FillRectangle(cx, cy, _cellW, _cellH, cur);
                        var under = screen[cursorRow, cursorCol];
                        if (under.Rune is not ' ' and not '\0')
                        {
                            using var gl = new CanvasTextLayout(sender, under.Rune.ToString(), _format, _cellW, _cellH);
                            ds.DrawTextLayout(gl, cx, cy, ToWin(under.Background));
                        }
                        break;
                    case CursorStyle.Underline:
                        float uh = MathF.Max(1f, MathF.Round(_cellH * 0.12f));
                        ds.FillRectangle(cx, cy + _cellH - uh, _cellW, uh, cur);
                        break;
                    default:
                        float barW = MathF.Max(1f, MathF.Round(_cellW * 0.14f));
                        ds.FillRectangle(cx, cy, barW, _cellH, cur);
                        break;
                }
            }
        }
    }
}
