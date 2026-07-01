using System.Runtime.InteropServices;
using System.Text;
using Agwinterm.Core;
using Agwinterm.Pty;
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

    // Decoded Kitty images, keyed by the current KittyImage instance so a retransmit
    // (new bytes, same id) re-decodes and the stale texture is pruned/disposed.
    private static readonly Dictionary<KittyImage, ID2D1Bitmap> _imageCache = new();
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

        session.OutputReceived += () => PostMessageW(_hwnd, WM_APP_REDRAW, IntPtr.Zero, IntPtr.Zero);

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
        switch (msg)
        {
            case WM_PAINT:
                BeginPaint(hwnd, out PAINTSTRUCT ps);
                Render();
                EndPaint(hwnd, ref ps);
                return IntPtr.Zero;

            case WM_APP_REDRAW:
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

    // ---- Kitty images (Direct2D) ----

    private static readonly string? _imgLog = Environment.GetEnvironmentVariable("AGWINTERM_IMGLOG");
    private static void Log(string m) { if (_imgLog is not null) try { File.AppendAllText(_imgLog, m + "\n"); } catch { } }

    /// <summary>Draw current image placements, decoding lazily and pruning stale textures. Called under the session lock.</summary>
    private static void DrawImages(TerminalEmulator em)
    {
        if (_rt is null) return;
        Log($"DrawImages: placements={em.Placements.Count} images={em.Images.Count}");

        // Prune textures whose image was retransmitted or deleted (bounds memory during scroll).
        if (_imageCache.Count > 0)
        {
            foreach (var stale in _imageCache.Keys.Where(k => !em.Images.Values.Contains(k)).ToList())
            {
                _imageCache[stale].Dispose();
                _imageCache.Remove(stale);
            }
        }

        foreach (var p in em.Placements)
        {
            if (!em.Images.TryGetValue(p.ImageId, out var img)) continue;
            if (!_imageCache.TryGetValue(img, out var bmp))
            {
                try { bmp = DecodeBitmap(img); }
                catch (Exception ex) { Log($"decode FAILED id={img.Id} fmt={img.Format}: {ex.GetType().Name} {ex.Message}"); bmp = null; }
                if (bmp is null) continue;
                _imageCache[img] = bmp;
                Log($"decoded id={img.Id} sizeDIP={bmp.Size.Width}x{bmp.Size.Height}");
            }
            float ix = PadX + p.Col * _cellW;
            float iy = PadY + p.Row * _cellH;
            float iw = p.Cols > 0 ? p.Cols * _cellW : bmp.Size.Width;
            float ih = p.Rows > 0 ? p.Rows * _cellH : bmp.Size.Height;
            Log($"draw id={img.Id} cell=({p.Col},{p.Row}) dest=({ix},{iy},{ix + iw},{iy + ih})");
            // Overload with an explicit destination RawRectF (Left,Top,Right,Bottom); source null = whole bitmap.
            try { _rt.DrawBitmap(bmp, new Vortice.RawRectF(ix, iy, ix + iw, iy + ih), 1f, BitmapInterpolationMode.Linear, null); }
            catch (Exception ex) { Log($"DrawBitmap FAILED: {ex.GetType().Name} {ex.Message}"); }
        }
    }

    private static readonly BitmapProperties _bmpProps =
        new(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96f, 96f);

    /// <summary>Decode a Kitty image (PNG via System.Drawing, or raw RGB/RGBA) into a premultiplied-BGRA D2D bitmap.</summary>
    private static ID2D1Bitmap? DecodeBitmap(KittyImage img)
    {
        if (_rt is null) return null;

        if (img.Format == KittyFormat.Png)
        {
            using var ms = new MemoryStream(img.Data);
            using var gdi = new System.Drawing.Bitmap(ms);
            int w = gdi.Width, h = gdi.Height;
            var data = gdi.LockBits(
                new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb); // premultiplied BGRA in memory
            try { return _rt.CreateBitmap(new SizeI(w, h), data.Scan0, (uint)data.Stride, _bmpProps); }
            finally { gdi.UnlockBits(data); }
        }

        byte[] bgra = ToPremultipliedBgra(img);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try { return _rt.CreateBitmap(new SizeI(img.Width, img.Height), handle.AddrOfPinnedObject(), (uint)(img.Width * 4), _bmpProps); }
        finally { handle.Free(); }
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

    private static void Render()
    {
        if (_rt is null || _brush is null) return;
        _rt.BeginDraw();
        _rt.Clear(C4(Color.DefaultBackground));

        var session = _session;
        if (session is not null)
        {
            lock (session.SyncRoot)
            {
                var em = session.Emulator;
                var screen = em.Screen;

                for (int r = 0; r < screen.Rows; r++)
                {
                    for (int col = 0; col < screen.Cols; col++)
                    {
                        Cell cell = screen[r, col];
                        if (cell.Width == 0) continue; // trailing spacer of a wide glyph
                        float x = PadX + col * _cellW;
                        float y = PadY + r * _cellH;

                        bool inverse = cell.Attributes.HasFlag(CellAttributes.Inverse);
                        Color fgc = inverse ? cell.Background : cell.Foreground;
                        Color bgc = inverse ? cell.Foreground : cell.Background;

                        // Faint (SGR 2): render the foreground dimmer, matching Windows Terminal.
                        if (cell.Attributes.HasFlag(CellAttributes.Dim))
                            fgc = new Color((byte)(fgc.R * 0.6f), (byte)(fgc.G * 0.6f), (byte)(fgc.B * 0.6f));

                        float w = cell.Width == 2 ? _cellW * 2 : _cellW;

                        if (bgc != Color.DefaultBackground)
                        {
                            _brush.Color = C4(bgc);
                            _rt.FillRectangle(new Rect(x, y, w, _cellH), _brush);
                        }

                        if (cell.Rune != ' ' && cell.Rune != '\0')
                        {
                            _brush.Color = C4(fgc);
                            _rt.DrawText(cell.Rune.ToString(), _format, new Rect(x, y, x + w, y + _cellH), _brush);
                        }
                    }
                }

                if (!_noImages && em.Placements.Count > 0)
                    DrawImages(em);

                if (em.CursorVisible)
                {
                    float cx = PadX + em.CursorCol * _cellW;
                    float cy = PadY + em.CursorRow * _cellH;
                    _brush.Color = new Color4(222 / 255f, 222 / 255f, 230 / 255f, 1f);

                    if (!_config.CursorBlink || _cursorOn)
                    {
                        switch (_config.CursorStyle)
                        {
                            case CursorStyle.Block:
                                _rt.FillRectangle(new Rect(cx, cy, _cellW, _cellH), _brush);
                                break;
                            case CursorStyle.Underline:
                                float uh = MathF.Max(1f, MathF.Round(_cellH * 0.12f));
                                _rt.FillRectangle(new Rect(cx, cy + _cellH - uh, _cellW, uh), _brush);
                                break;
                            default:
                                float barW = MathF.Max(1f, MathF.Round(_cellW * 0.14f));
                                _rt.FillRectangle(new Rect(cx, cy, barW, _cellH), _brush);
                                break;
                        }
                    }
                }
            }
        }

        _rt.EndDraw();
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
