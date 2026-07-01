using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
internal static class Program
{
    private const float PadX = 8f;
    private const float PadY = 6f;
    private const string ClassName = "AgwintermWin32";

    // Kept alive for the lifetime of the process so the GC never collects the thunk.
    private static WndProc _wndProc = null!;

    private static IntPtr _hwnd;
    private static ID2D1Factory _d2d = null!;
    private static IDWriteFactory _dwrite = null!;
    private static IDWriteTextFormat _format = null!;
    private static ID2D1HwndRenderTarget? _rt;
    private static ID2D1SolidColorBrush? _brush;

    private static TerminalConfig _config = new();
    private static TerminalSession? _session;
    private static ControlServer? _control;

    private static float _cellW = 8, _cellH = 16;
    private static bool _cursorOn = true;
    private static readonly StringBuilder _run = new(256);   // reused per text run (no per-cell alloc)
    private static int _redrawPending;                       // coalesces redraw requests into one paint

    // Decoded Kitty images, keyed by the current KittyImage instance so a retransmit
    // (new bytes, same id) re-decodes and the stale texture is pruned/disposed.
    private static readonly Dictionary<KittyImage, ID2D1Bitmap> _imageCache = new();
    private static readonly HashSet<KittyImage> _decoding = new();               // decode in flight (UI-thread set)
    // Background-decoded pixels waiting to be uploaded to a GPU texture on the UI thread.
    // bgra == null signals a decode failure (so we can drop it from _decoding without retrying forever).
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(KittyImage img, byte[]? bgra, int w, int h)> _decoded = new();
    private static readonly bool _noImages = Environment.GetEnvironmentVariable("AGWINTERM_NOIMG") == "1";

    [STAThread]
    private static void Main()
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        _config = LoadOrCreateConfig();

        IntPtr hInstance = GetModuleHandleW(null);
        _wndProc = WindowProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
            lpszClassName = ClassName,
        };
        if (RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("RegisterClassExW failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());

        _hwnd = CreateWindowExW(0, ClassName, "agwinterm", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            CW_USEDEFAULT, CW_USEDEFAULT, 1040, 660, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowExW failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());

        // Direct2D / DirectWrite.
        _d2d = D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _format = CreateTextFormat(_config);
        MeasureCell();

        CreateRenderTarget();
        StartSession();

        if (_config.CursorBlink)
            SetTimer(_hwnd, (IntPtr)1, (uint)_config.CursorBlinkMs, IntPtr.Zero);

        ShowWindow(_hwnd, SW_SHOW);
        UpdateWindow(_hwnd);

        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
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

    private static void MeasureCell()
    {
        using var run = _dwrite.CreateTextLayout(new string('M', 10), _format, 4096f, 4096f);
        using var one = _dwrite.CreateTextLayout("M", _format, 4096f, 4096f);
        _cellW = MathF.Round(run.Metrics.Width / 10f);
        _cellH = MathF.Round(one.Metrics.Height);
        if (_cellW < 1) _cellW = 8;
        if (_cellH < 1) _cellH = 16;
    }

    private static void CreateRenderTarget()
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
        _brush = _rt.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
    }

    private static (int cols, int rows) GridSize()
    {
        GetClientRect(_hwnd, out RECT rc);
        int w = rc.right - rc.left, h = rc.bottom - rc.top;
        int cols = Math.Max(1, (int)((w - 2 * PadX) / _cellW));
        int rows = Math.Max(1, (int)((h - 2 * PadY) / _cellH));
        return (cols, rows);
    }

    private static void StartSession()
    {
        var (cols, rows) = GridSize();
        var session = new TerminalSession(cols, rows);
        _session = session;

        _control = new ControlServer(session, "agwinterm");
        _control.Start();

        session.OutputReceived += RequestRedraw;

        string id = Guid.NewGuid().ToString();
        var env = new Dictionary<string, string>
        {
            ["AGWINTERM"] = "1",
            ["AGWINTERM_ENABLED"] = "1",
            ["AGWINTERM_PIPE"] = _control.PipeName,
            ["AGWINTERM_SESSION_ID"] = id,
            ["AGWINTERM_WORKSPACE_ID"] = Guid.NewGuid().ToString(),
            ["AGWINTERM_WINDOW_ID"] = Guid.NewGuid().ToString(),
        };
        _ = session.StartAsync("powershell.exe", new[] { "-NoLogo" }, extraEnv: env, cwd: null);
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try { return WindowProcCore(hwnd, msg, wParam, lParam); }
        catch (Exception ex) { Perf($"wndproc ex msg=0x{msg:X}: {ex.GetType().Name} {ex.Message}"); return DefWindowProcW(hwnd, msg, wParam, lParam); }
    }

    private static IntPtr WindowProcCore(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                BeginPaint(hwnd, out PAINTSTRUCT ps);
                Render();
                EndPaint(hwnd, ref ps);
                return IntPtr.Zero;

            case WM_APP_REDRAW:
                System.Threading.Interlocked.Exchange(ref _redrawPending, 0);
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;

            case WM_TIMER:
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
                        var (cols, rows) = GridSize();
                        if (_session is not null && (cols != _session.Cols || rows != _session.Rows))
                            _session.Resize(cols, rows);
                        InvalidateRect(hwnd, IntPtr.Zero, false);
                    }
                }
                return IntPtr.Zero;

            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                if (OnKeyDown((int)wParam)) return IntPtr.Zero;
                break;

            case WM_CHAR:
                {
                    char c = (char)wParam;
                    if (c >= 0x20 && c != 0x7f) Send(c.ToString());
                    return IntPtr.Zero;
                }

            case WM_LBUTTONDOWN: SendMousePx(0, lParam, true); SetCapture(hwnd); return IntPtr.Zero;
            case WM_LBUTTONUP: SendMousePx(0, lParam, false); ReleaseCapture(); return IntPtr.Zero;
            case WM_MBUTTONDOWN: SendMousePx(1, lParam, true); return IntPtr.Zero;
            case WM_MBUTTONUP: SendMousePx(1, lParam, false); return IntPtr.Zero;
            case WM_RBUTTONDOWN: SendMousePx(2, lParam, true); return IntPtr.Zero;
            case WM_RBUTTONUP: SendMousePx(2, lParam, false); return IntPtr.Zero;

            case WM_MOUSEMOVE:
                {
                    var em = _session?.Emulator;
                    if (em is not null && em.MouseReportsMotion)
                        SendMousePx(32 + (((long)wParam & MK_LBUTTON) != 0 ? 0 : 3), lParam, true);
                    return IntPtr.Zero;
                }

            case WM_MOUSEWHEEL:
                {
                    var em = _session?.Emulator;
                    if (em is not null && em.MouseReporting)
                    {
                        var pt = new POINT { x = LoWord(lParam), y = HiWord(lParam) }; // wheel gives screen coords
                        ScreenToClient(_hwnd, ref pt);
                        SendMouse(HiWord(wParam) > 0 ? 64 : 65, pt.x, pt.y, true);
                    }
                    return IntPtr.Zero;
                }

            case WM_DESTROY:
                _session?.Dispose();
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void Send(string s)
    {
        _session?.NotifyActivity();
        _session?.Write(Encoding.UTF8.GetBytes(s));
    }

    /// <summary>Coalesce many output/decode notifications into a single pending repaint.</summary>
    private static void RequestRedraw()
    {
        if (System.Threading.Interlocked.Exchange(ref _redrawPending, 1) == 0)
            PostMessageW(_hwnd, WM_APP_REDRAW, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Report a mouse event given raw client pixels packed in lParam (button code already encoded).</summary>
    private static void SendMousePx(int btn, IntPtr lParam, bool press)
        => SendMouse(btn, LoWord(lParam), HiWord(lParam), press);

    /// <summary>Encode a mouse event (SGR or legacy) and send it to the child. Mirrors the WinUI shell.</summary>
    private static void SendMouse(int btn, int pxX, int pxY, bool press)
    {
        var em = _session?.Emulator;
        if (em is null || !em.MouseReporting) return;
        int col = Math.Clamp((int)((pxX - PadX) / _cellW), 0, em.Screen.Cols - 1);
        int row = Math.Clamp((int)((pxY - PadY) / _cellH), 0, em.Screen.Rows - 1);
        string seq = em.MouseSgr
            ? $"\x1b[<{btn};{col + 1};{row + 1}{(press ? 'M' : 'm')}"
            : "\x1b[M" + (char)(32 + (press ? btn : 3)) + (char)(33 + col) + (char)(33 + row);
        _session?.Write(Encoding.UTF8.GetBytes(seq));
    }

    /// <summary>Returns true if the key was consumed (matches the WinUI key table).</summary>
    private static bool OnKeyDown(int vk)
    {
        if (_session is null) return false;
        bool ctrl = KeyDown(VK_CONTROL), shift = KeyDown(VK_SHIFT), alt = KeyDown(VK_MENU);

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

    /// <summary>Effective foreground for a cell: inverse swap, then faint (SGR 2) dimming.</summary>
    private static Color Dimmed(Cell cell)
    {
        Color fg = cell.Attributes.HasFlag(CellAttributes.Inverse) ? cell.Background : cell.Foreground;
        if (cell.Attributes.HasFlag(CellAttributes.Dim))
            fg = new Color((byte)(fg.R * 0.6f), (byte)(fg.G * 0.6f), (byte)(fg.B * 0.6f));
        return fg;
    }

    // ---- Kitty images (Direct2D) ----

    private static readonly string? _imgLog = Environment.GetEnvironmentVariable("AGWINTERM_IMGLOG");
    private static void Log(string m) { if (_imgLog is not null) try { File.AppendAllText(_imgLog, m + "\n"); } catch { } }

    private static readonly string? _perfLog = Environment.GetEnvironmentVariable("AGWINTERM_PERF");
    private static void Perf(string m) { if (_perfLog is not null) try { File.AppendAllText(_perfLog, m + "\n"); } catch { } }
    private static int _uploadCount;
    private static double _uploadMs;

    /// <summary>
    /// Draw current image placements. Decoding (PNG decompress) happens on a background
    /// thread so the UI never blocks; only the cheap GPU upload runs here. An image simply
    /// appears on the next redraw once its pixels are ready. Called under the session lock.
    /// </summary>
    private static void DrawImages(TerminalEmulator em)
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
            float ix = PadX + p.Col * _cellW;
            float iy = PadY + p.Row * _cellH;
            float iw = p.Cols > 0 ? p.Cols * _cellW : bmp.Size.Width;
            float ih = p.Rows > 0 ? p.Rows * _cellH : bmp.Size.Height;
            // Overload with an explicit destination RawRectF (Left,Top,Right,Bottom); source null = whole bitmap.
            try { _rt.DrawBitmap(bmp, new Vortice.RawRectF(ix, iy, ix + iw, iy + ih), 1f, BitmapInterpolationMode.Linear, null); }
            catch (Exception ex) { Log($"DrawBitmap FAILED: {ex.GetType().Name} {ex.Message}"); }
        }
    }

    private static readonly BitmapProperties _bmpProps =
        new(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96f, 96f);

    /// <summary>Background: decode to premultiplied BGRA pixels (no D2D), enqueue for UI upload, ask for a redraw.</summary>
    private static void DecodePixelsAsync(KittyImage img)
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
    private static void RecreateTarget()
    {
        foreach (var b in _imageCache.Values) { try { b.Dispose(); } catch { } }
        _imageCache.Clear();
        _decoding.Clear();
        while (_decoded.TryDequeue(out _)) { }
        try { _brush?.Dispose(); } catch { }
        try { _rt?.Dispose(); } catch { }
        _brush = null; _rt = null;
        try { CreateRenderTarget(); } catch (Exception ex) { Perf($"recreate FAILED: {ex.Message}"); }
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    // D2DERR_RECREATE_TARGET: the device is lost and every device-bound resource must be rebuilt.
    private const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

    private static void Render()
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

    private static void RenderBody()
    {
        var rt = _rt!;
        var brush = _brush!;
        rt.BeginDraw();
        rt.Clear(C4(Color.DefaultBackground));

        var session = _session;
        if (session is not null)
        {
            lock (session.SyncRoot)
            {
                var em = session.Emulator;
                var screen = em.Screen;
                int cols = screen.Cols, rows = screen.Rows;

                // Text is drawn in runs of same-colour cells rather than one DrawText per
                // cell: on a full-screen repaint that cuts thousands of shaping/layout calls
                // (and per-cell string allocations) down to a few dozen — the difference
                // between a ~20ms frame and a ~3ms one while scrolling.
                for (int r = 0; r < rows; r++)
                {
                    float y = PadY + r * _cellH;

                    // Pass 1: background fills, coalesced across equal-bg spans.
                    int c = 0;
                    while (c < cols)
                    {
                        Cell cell = screen[r, c];
                        Color bg = cell.Attributes.HasFlag(CellAttributes.Inverse) ? cell.Foreground : cell.Background;
                        if (bg == Color.DefaultBackground) { c++; continue; }
                        int start = c;
                        while (c < cols)
                        {
                            Cell cc = screen[r, c];
                            Color b2 = cc.Attributes.HasFlag(CellAttributes.Inverse) ? cc.Foreground : cc.Background;
                            if (cc.Width != 0 && b2 != bg) break;
                            c++;
                        }
                        brush.Color = C4(bg);
                        rt.FillRectangle(new Rect(PadX + start * _cellW, y, (c - start) * _cellW, _cellH), brush);
                    }

                    // Pass 2: foreground text, coalesced into same-colour runs.
                    c = 0;
                    while (c < cols)
                    {
                        Cell cell = screen[r, c];
                        if (cell.Width == 0 || cell.Rune == ' ' || cell.Rune == '\0') { c++; continue; }

                        Color runFg = Dimmed(cell);
                        if (cell.Width == 2) // draw wide (CJK) glyphs individually; advances differ
                        {
                            brush.Color = C4(runFg);
                            float wx = PadX + c * _cellW;
                            rt.DrawText(cell.Rune.ToString(), _format, new Rect(wx, y, wx + 2 * _cellW, y + _cellH), brush);
                            c++;
                            continue;
                        }

                        int start = c;
                        _run.Clear();
                        int lastNonBlank = 0;
                        while (c < cols)
                        {
                            Cell cc = screen[r, c];
                            if (cc.Width == 2 || cc.Width == 0) break; // wide glyph starts a new run
                            bool blank = cc.Rune == ' ' || cc.Rune == '\0';
                            if (!blank && Dimmed(cc) != runFg) break;  // colour change ends the run
                            _run.Append(blank ? ' ' : cc.Rune);
                            c++;
                            if (!blank) lastNonBlank = _run.Length;
                        }
                        if (lastNonBlank > 0)
                        {
                            _run.Length = lastNonBlank; // drop trailing blanks (nothing to shape)
                            brush.Color = C4(runFg);
                            float rx = PadX + start * _cellW;
                            rt.DrawText(_run.ToString(), _format, new Rect(rx, y, rx + _run.Length * _cellW, y + _cellH), brush);
                        }
                    }
                }

                if (!_noImages && em.Placements.Count > 0)
                    DrawImages(em);

                if (em.CursorVisible)
                {
                    float cx = PadX + em.CursorCol * _cellW;
                    float cy = PadY + em.CursorRow * _cellH;
                    brush.Color = new Color4(222 / 255f, 222 / 255f, 230 / 255f, 1f);

                    if (!_config.CursorBlink || _cursorOn)
                    {
                        switch (_config.CursorStyle)
                        {
                            case CursorStyle.Block:
                                rt.FillRectangle(new Rect(cx, cy, _cellW, _cellH), brush);
                                break;
                            case CursorStyle.Underline:
                                float uh = MathF.Max(1f, MathF.Round(_cellH * 0.12f));
                                rt.FillRectangle(new Rect(cx, cy + _cellH - uh, _cellW, uh), brush);
                                break;
                            default:
                                float barW = MathF.Max(1f, MathF.Round(_cellW * 0.14f));
                                rt.FillRectangle(new Rect(cx, cy, barW, _cellH), brush);
                                break;
                        }
                    }
                }
            }
        }
    }

    // ---- Config (mirrors the WinUI shell) ----

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
