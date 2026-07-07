using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>
/// Popup context menu hosted in a REAL top-level window (WS_POPUP + drop shadow), so it can
/// extend beyond the main window's bounds like a native menu — but rendered with the app theme
/// (Direct2D + the chrome palette). Input model mirrors native menus: the popup captures the
/// mouse while open (click inside runs, click outside dismisses and is swallowed, losing capture
/// dismisses); the main window keeps keyboard focus (WS_EX_NOACTIVATE) and forwards
/// Up/Down/Enter/Esc via <see cref="MenuKeyDown"/>.
/// </summary>
internal partial class Program
{
    private const string MenuClassName = "agwinterm-menu";
    private static bool _menuClassReady;
    private static WndProc? _menuProcKeep;                            // GC-rooted class wndproc
    private static readonly Dictionary<IntPtr, Program> _menuByHwnd = new();

    private IntPtr _menuHwnd;
    private ID2D1HwndRenderTarget? _menuRt;
    private ID2D1SolidColorBrush? _menuBrush;
    private List<PalItem> _menuItems = new();
    private int _menuSel = -1;                                        // hover/keyboard selection
    private float _menuW, _menuH;
    private bool _menuClosing;

    private const float MenuRowH = 30f, MenuSepH = 9f, MenuPadY = 5f, MenuPadX = 12f;

    /// <summary>A visual separator row (drawn as a hairline; never selectable).</summary>
    private static PalItem MenuSeparator() => new() { Label = "-" };
    private static bool IsSep(PalItem it) => it.Label == "-" && it.Run is null;

    /// <summary>Show <paramref name="items"/> as a popup menu at screen point (sx, sy).</summary>
    private void ShowMenuWindow(List<PalItem> items, int sx, int sy)
    {
        CloseMenuWindow();
        if (items.Count == 0) return;
        _menuItems = items; _menuSel = -1;

        // Size to content, clamped; height is exact (a context menu is short by construction).
        float maxW = 0f;
        foreach (var it in items)
        {
            if (IsSep(it)) continue;
            float w = MeasureText(it.Label, _uiFont) + (it.Hint.Length > 0 ? MeasureText(it.Hint, _uiSmall) + 28f : 0f);
            maxW = MathF.Max(maxW, w);
        }
        _menuW = Math.Clamp(maxW + MenuPadX * 2f + 8f, 180f, 480f);
        _menuH = MenuPadY * 2f;
        foreach (var it in items) _menuH += IsSep(it) ? MenuSepH : MenuRowH;

        // Clamp to the monitor work area; open upward when there's no room below (native behavior).
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromPoint(new POINT { x = sx, y = sy }, MONITOR_DEFAULTTONEAREST), ref mi);
        int x = Math.Clamp(sx, mi.rcWork.left, Math.Max(mi.rcWork.left, mi.rcWork.right - (int)_menuW));
        int y = sy + (int)_menuH <= mi.rcWork.bottom ? sy : Math.Max(mi.rcWork.top, sy - (int)_menuH);

        if (!_menuClassReady)
        {
            _menuProcKeep = MenuProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = CS_HREDRAW | CS_VREDRAW | CS_DROPSHADOW,
                lpfnWndProc = _menuProcKeep,
                hInstance = _hInstance,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                lpszClassName = MenuClassName,
            };
            if (RegisterClassExW(ref wc) == 0) return;
            _menuClassReady = true;
        }

        _menuHwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, MenuClassName, "",
            WS_POPUP, x, y, (int)_menuW, (int)_menuH, _hwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_menuHwnd == IntPtr.Zero) return;
        _menuByHwnd[_menuHwnd] = this;

        var props = new RenderTargetProperties
        {
            Type = RenderTargetType.Default,
            PixelFormat = new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
            DpiX = 96f,
            DpiY = 96f,
        };
        var hp = new HwndRenderTargetProperties { Hwnd = _menuHwnd, PixelSize = new SizeI((int)_menuW, (int)_menuH), PresentOptions = PresentOptions.None };
        _menuRt = _d2d.CreateHwndRenderTarget(props, hp);
        _menuRt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _menuBrush = _menuRt.CreateSolidColorBrush(ChromeText);

        ShowWindow(_menuHwnd, SW_SHOWNOACTIVATE);
        SetCapture(_menuHwnd);   // native-menu input model: all mouse routes here until dismissed
    }

    private void CloseMenuWindow()
    {
        if (_menuHwnd == IntPtr.Zero || _menuClosing) return;
        _menuClosing = true;
        var h = _menuHwnd; _menuHwnd = IntPtr.Zero;
        _menuByHwnd.Remove(h);
        ReleaseCapture();
        _menuBrush?.Dispose(); _menuBrush = null;
        _menuRt?.Dispose(); _menuRt = null;
        DestroyWindow(h);
        _menuClosing = false;
    }

    /// <summary>Actionable row index at a popup-client point, or -1 (separators/disabled aren't hits).</summary>
    private int MenuIndexAt(int mx, int my)
    {
        if (mx < 0 || mx >= _menuW) return -1;
        float y = MenuPadY;
        for (int i = 0; i < _menuItems.Count; i++)
        {
            float h = IsSep(_menuItems[i]) ? MenuSepH : MenuRowH;
            if (my >= y && my < y + h) return _menuItems[i].Run is null ? -1 : i;
            y += h;
        }
        return -1;
    }

    /// <summary>Keyboard while the menu is up (the main window keeps focus and forwards here):
    /// ↑/↓ move, Enter runs, Esc closes; everything else is swallowed like a native menu.</summary>
    private bool MenuKeyDown(int vk)
    {
        switch (vk)
        {
            case VK_ESCAPE: CloseMenuWindow(); return true;
            case VK_UP:
            case VK_DOWN:
            {
                int dir = vk == VK_DOWN ? 1 : -1, n = _menuItems.Count, i = _menuSel;
                for (int step = 0; step < n; step++)
                {
                    i = ((i + dir) % n + n) % n;
                    if (_menuItems[i].Run is not null) { _menuSel = i; break; }
                }
                if (_menuHwnd != IntPtr.Zero) InvalidateRect(_menuHwnd, IntPtr.Zero, false);
                return true;
            }
            case VK_RETURN:
                if (_menuSel >= 0 && _menuSel < _menuItems.Count && _menuItems[_menuSel].Run is { } run)
                { CloseMenuWindow(); run(); }
                return true;
            default: return true;
        }
    }

    private void RenderMenu()
    {
        if (_menuRt is null || _menuBrush is null) return;
        var rt = _menuRt; var brush = _menuBrush;
        rt.BeginDraw();
        rt.Clear(PalBg);
        float y = MenuPadY;
        for (int i = 0; i < _menuItems.Count; i++)
        {
            var it = _menuItems[i];
            if (IsSep(it))
            {
                brush.Color = ChromeBorder;
                rt.DrawLine(new System.Numerics.Vector2(MenuPadX - 4f, y + MenuSepH / 2f),
                            new System.Numerics.Vector2(_menuW - MenuPadX + 4f, y + MenuSepH / 2f), brush, 1f);
                y += MenuSepH;
                continue;
            }
            if (i == _menuSel) { brush.Color = PalSel; rt.FillRectangle(new Rect(3f, y, _menuW - 6f, MenuRowH), brush); }
            brush.Color = it.Run is null ? ChromeDim : (i == _menuSel ? SbActiveText : ChromeText);
            rt.DrawText(it.Label, _uiFont, new Rect(MenuPadX, y + (MenuRowH - 18f) / 2f, _menuW - MenuPadX * 2f - (it.Hint.Length > 0 ? 60f : 0f), 18f), brush, DrawTextOptions.Clip);
            if (it.Hint.Length > 0)
            {
                float hw = MeasureText(it.Hint, _uiSmall);
                brush.Color = ChromeDim;
                rt.DrawText(it.Hint, _uiSmall, new Rect(_menuW - MenuPadX - hw, y + (MenuRowH - 15f) / 2f, hw + 2f, 15f), brush);
            }
            y += MenuRowH;
        }
        brush.Color = PalBorder;
        rt.DrawRectangle(new Rect(0.5f, 0.5f, _menuW - 1f, _menuH - 1f), brush, 1f);
        rt.EndDraw();
    }

    private static IntPtr MenuProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!_menuByHwnd.TryGetValue(hwnd, out var self)) return DefWindowProcW(hwnd, msg, wParam, lParam);
        switch (msg)
        {
            case WM_MOUSEACTIVATE: return (IntPtr)MA_NOACTIVATE;   // never steal focus from the main window
            case WM_PAINT:
            {
                BeginPaint(hwnd, out PAINTSTRUCT ps);
                try { self.RenderMenu(); } catch { /* rendering is best-effort; the menu closes on any click */ }
                EndPaint(hwnd, ref ps);
                return IntPtr.Zero;
            }
            case WM_MOUSEMOVE:
            {
                int i = self.MenuIndexAt(LoWord(lParam), HiWord(lParam));
                if (i != self._menuSel) { self._menuSel = i; InvalidateRect(hwnd, IntPtr.Zero, false); }
                return IntPtr.Zero;
            }
            case WM_LBUTTONDOWN:
            case WM_RBUTTONDOWN:
            {
                // Outside the menu (coords can be negative under capture): dismiss and swallow.
                int mx = LoWord(lParam), my = HiWord(lParam);
                if (mx < 0 || my < 0 || mx >= self._menuW || my >= self._menuH) self.CloseMenuWindow();
                return IntPtr.Zero;
            }
            case WM_LBUTTONUP:
            {
                int i = self.MenuIndexAt(LoWord(lParam), HiWord(lParam));
                if (i >= 0 && self._menuItems[i].Run is { } run) { self.CloseMenuWindow(); run(); }
                return IntPtr.Zero;
            }
            case WM_CAPTURECHANGED:
                if (!self._menuClosing) self.CloseMenuWindow();   // capture stolen (alt-tab, other app) — dismiss
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
