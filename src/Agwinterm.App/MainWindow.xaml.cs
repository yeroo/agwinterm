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

    /// <summary>One live session and its metadata.</summary>
    private sealed class Ses
    {
        public required string Id;
        public required string Name;
        public required TerminalSession S;
    }

    private readonly List<Ses> _all = new();   // ordered; guarded by 'lock (_all)' for control-thread reads
    private Ses? _activeRef;
    private TerminalSession? _session;          // mirrors the active session (all rendering/input uses this)
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

    // Decoded Kitty images cached by (sessionId, imageId) so sessions don't collide.
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

        NewSessionButton.Click += (_, _) => CreateSession(Guid.NewGuid().ToString(), null, null, makeActive: true);
        GridCanvas.Loaded += (_, _) => GridCanvas.Focus(FocusState.Programmatic);
        this.Activated += (_, _) => GridCanvas.Focus(FocusState.Programmatic);
        GridCanvas.CharacterReceived += OnCharacterReceived;
        GridCanvas.KeyDown += OnKeyDown;
        GridCanvas.SizeChanged += OnSizeChanged;
        GridCanvas.PointerPressed += OnPointerPressed;
        GridCanvas.PointerReleased += OnPointerReleased;
        GridCanvas.PointerWheelChanged += OnPointerWheel;
        GridCanvas.PointerMoved += OnPointerMoved;
        // Control server + first session are created once we know cell metrics + canvas size (first OnDraw).
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
        Ses[] sessions;
        lock (_all) sessions = _all.ToArray();
        foreach (var s in sessions)
            if (cols != s.S.Cols || rows != s.S.Rows)
                s.S.Resize(cols, rows);
        GridCanvas.Invalidate();
    }

    // ---- Session lifecycle (UI thread) ----

    private void StartControlServerOnce()
    {
        if (_control is not null) return;
        _control = new ControlServer(this, "agwinterm");
        _control.Start();
    }

    private void CreateSession(string id, string? name, string? cwd, bool makeActive)
    {
        StartControlServerOnce();
        var (cols, rows) = ComputeGridSize();
        var session = new TerminalSession(cols, rows);
        int ordinal;
        lock (_all) ordinal = _all.Count + 1;
        var ses = new Ses { Id = id, Name = name ?? $"session {ordinal}", S = session };

        session.OutputReceived += () => DispatcherQueue.TryEnqueue(async () =>
        {
            if (ReferenceEquals(_activeRef, ses))
            {
                await DecodeNewImagesAsync();
                GridCanvas.Invalidate();
            }
        });
        session.StatusChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            RebuildSidebar();
            if (ReferenceEquals(_activeRef, ses)) UpdateTitle();
        });

        lock (_all) _all.Add(ses);

        var env = new Dictionary<string, string>
        {
            ["AGWINTERM"] = "1",
            ["AGWINTERM_ENABLED"] = "1",
            ["AGWINTERM_PIPE"] = _control!.PipeName,
            ["AGWINTERM_SESSION_ID"] = id,
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
        // Defer focus to the next tick so it sticks after layout/activation (a Focus() call
        // made during the first draw is dropped, which is why typing only worked after re-activating).
        DispatcherQueue.TryEnqueue(() => GridCanvas.Focus(FocusState.Programmatic));
    }

    private void CloseSessionInternal(Ses ses)
    {
        try { ses.S.Dispose(); } catch { }
        bool wasActive = ReferenceEquals(_activeRef, ses);
        lock (_all) _all.Remove(ses);

        if (wasActive)
        {
            Ses? next;
            lock (_all) next = _all.FirstOrDefault();
            if (next is not null) SetActive(next);
            else CreateSession(Guid.NewGuid().ToString(), null, null, makeActive: true); // keep >= 1
        }
        RebuildSidebar();
        GridCanvas.Invalidate();
    }

    private void CycleSession(int dir)
    {
        Ses? target = null;
        lock (_all)
        {
            if (_all.Count < 2) return;
            int i = _activeRef is null ? 0 : _all.IndexOf(_activeRef);
            target = _all[((i + dir) % _all.Count + _all.Count) % _all.Count];
        }
        if (target is not null) SetActive(target);
    }

    private static Brush StatusBrush(AgentStatus s) => new SolidColorBrush(s switch
    {
        AgentStatus.Active => WinColor.FromArgb(255, 60, 140, 255),
        AgentStatus.Blocked => WinColor.FromArgb(255, 240, 160, 40),
        AgentStatus.Completed => WinColor.FromArgb(255, 60, 200, 90),
        _ => WinColor.FromArgb(255, 90, 96, 102),
    });

    private void RebuildSidebar()
    {
        SidebarPanel.Children.Clear();
        Ses[] sessions;
        lock (_all) sessions = _all.ToArray();
        foreach (var ses in sessions)
        {
            var dot = new Ellipse { Width = 9, Height = 9, Fill = StatusBrush(ses.S.Status), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var label = new TextBlock { Text = ses.Name, Foreground = new SolidColorBrush(Colors.White), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(label);
            var btn = new Button
            {
                Content = row,
                Tag = ses,
                IsTabStop = false, // don't steal keyboard focus from the terminal
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = ReferenceEquals(ses, _activeRef)
                    ? new SolidColorBrush(WinColor.FromArgb(255, 38, 44, 52))
                    : new SolidColorBrush(Colors.Transparent),
            };
            btn.Click += (s, _) => { if (((Button)s).Tag is Ses t) SetActive(t); };
            SidebarPanel.Children.Add(btn);
        }
    }

    // ---- ISessionHost (called from the control server's background threads) ----

    public TerminalSession? Resolve(string? target)
    {
        lock (_all)
        {
            if (string.IsNullOrEmpty(target) || target == "active") return _activeRef?.S;
            return (_all.FirstOrDefault(x => x.Id == target) ?? _all.FirstOrDefault(x => x.Id.StartsWith(target)))?.S;
        }
    }

    public IReadOnlyList<SessionSnapshot> Snapshot()
    {
        lock (_all)
            return _all.Select(x => new SessionSnapshot(x.Id, x.Name, ReferenceEquals(x, _activeRef), x.S.Status)).ToList();
    }

    public string NewSession(string? name, string? cwd)
    {
        string id = Guid.NewGuid().ToString();
        DispatcherQueue.TryEnqueue(() => CreateSession(id, name, cwd, makeActive: true));
        return id;
    }

    public bool SelectSession(string target)
    {
        Ses? ses;
        lock (_all) ses = _all.FirstOrDefault(x => x.Id == target) ?? _all.FirstOrDefault(x => x.Id.StartsWith(target));
        if (ses is null) return false;
        DispatcherQueue.TryEnqueue(() => SetActive(ses));
        return true;
    }

    public bool CloseSession(string target)
    {
        Ses? ses;
        lock (_all) ses = _all.FirstOrDefault(x => x.Id == target) ?? _all.FirstOrDefault(x => x.Id.StartsWith(target));
        if (ses is null) return false;
        DispatcherQueue.TryEnqueue(() => CloseSessionInternal(ses));
        return true;
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

        // Reserved app chords for multi-session management (checked before terminal keys).
        if (ctrl && e.Key == VirtualKey.Tab) { CycleSession(shift ? -1 : 1); e.Handled = true; return; }
        if (ctrl && shift && e.Key == VirtualKey.T) { CreateSession(Guid.NewGuid().ToString(), null, null, true); e.Handled = true; return; }
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
                if (active.S.Emulator.Images.TryGetValue(p.ImageId, out var img))
                {
                    _decoding.Add(key);
                    todo.Add(img);
                }
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
            CreateSession(Guid.NewGuid().ToString(), null, null, makeActive: true);
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
