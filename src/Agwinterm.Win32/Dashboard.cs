using System;
using System.Collections.Generic;
using System.Linq;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>
/// Dashboard grid overlay (agterm #202): Ctrl+Shift+D tiles the most-recently-used sessions as a grid
/// of live terminal surfaces over the content area. Arrow keys move a highlight; Enter or double-click
/// drops into the highlighted session; Esc closes. View-only — the grid owns input while it's up.
/// </summary>
internal partial class Program
{
    private bool _dashboardOpen;
    private readonly List<Ses> _dashSessions = new();
    private int _dashSel;
    private readonly List<Rect> _dashCells = new();   // per-cell rects, for mouse hit-testing
    private const int DashMax = 9;

    private void ToggleDashboard() { if (_dashboardOpen) CloseDashboard(); else OpenDashboard(); }

    private void OpenDashboard()
    {
        EnsureMru();
        var order = _mru.Select(FindSes).Where(s => s is not null).Cast<Ses>().Take(DashMax).ToList();
        if (order.Count == 0) { ShowToast("No sessions to show"); return; }
        _dashSessions.Clear(); _dashSessions.AddRange(order);
        _dashSel = Math.Max(0, _dashSessions.FindIndex(s => ReferenceEquals(s, _active)));
        _dashboardOpen = true;
        Uia.Announce($"Dashboard, {_dashSessions.Count} sessions. Arrow keys to move, Enter to open, Escape to close.");
        RequestRedraw();
    }

    private void CloseDashboard() { if (!_dashboardOpen) return; _dashboardOpen = false; RequestRedraw(); }

    private static (int cols, int rows) DashGrid(int n)
    {
        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling(n / (double)cols);
        return (cols, rows);
    }

    private void DrawDashboard(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (!_dashboardOpen) return;
        _dashCells.Clear();
        var (ax, ay, aw, ah) = ContentArea();
        brush.Color = C4(_theme.DefaultBackground);
        rt.FillRectangle(new Rect(ax, ay, aw, ah), brush);

        int n = _dashSessions.Count;
        var (cols, rows) = DashGrid(n);
        const float pad = 8f, labelH = 20f, footH = 22f;
        float gridH = ah - footH;
        float cellW = (aw - pad * (cols + 1)) / cols;
        float cellH = (gridH - pad * (rows + 1)) / rows;

        // One uniform font size for the whole grid: shrink so the largest session's cols×rows fit a cell.
        const float BASE = 14f;
        var (_, cwB, chB) = Metrics(BASE);
        int maxCols = 1, maxRows = 1;
        foreach (var s in _dashSessions)
            lock (s.S.SyncRoot) { maxCols = Math.Max(maxCols, s.S.Emulator.Screen.Cols); maxRows = Math.Max(maxRows, s.S.Emulator.Screen.Rows); }
        float termW = MathF.Max(1f, cellW - 8f), termH = MathF.Max(1f, cellH - labelH - 6f);
        float fs = Math.Clamp(MathF.Floor(MathF.Min(termW / maxCols / cwB * BASE, termH / maxRows / chB * BASE)), 5f, BASE);
        var (fmt, cw, ch) = Metrics(fs);

        for (int i = 0; i < n; i++)
        {
            int col = i % cols, row = i / cols;
            float cx = ax + pad + col * (cellW + pad);
            float cy = ay + pad + row * (cellH + pad);
            bool sel = i == _dashSel;
            var s = _dashSessions[i];
            _dashCells.Add(new Rect(cx, cy, cellW, cellH));

            brush.Color = C4(_theme.DefaultBackground);
            rt.FillRectangle(new Rect(cx, cy, cellW, cellH), brush);

            // Title strip
            brush.Color = sel ? ChromeAccent : Mix(C4(_theme.DefaultBackground), ChromeDim, 0.5f);
            rt.FillRectangle(new Rect(cx, cy, cellW, labelH), brush);
            brush.Color = sel ? new Color4(1f, 1f, 1f, 1f) : ChromeText;
            rt.DrawText($"{s.Ws.Name} / {s.Name}", _uiSmall, new Rect(cx + 6f, cy + 1f, cellW - 12f, labelH - 1f), brush, DrawTextOptions.Clip);

            // Live terminal preview, clipped to the cell body
            float tx = cx + 4f, ty = cy + labelH + 2f, tw = MathF.Max(1f, cellW - 8f), th = MathF.Max(1f, cellH - labelH - 6f);
            rt.PushAxisAlignedClip(new Rect(tx, ty, tw, th), AntialiasMode.Aliased);
            RenderTerminal(s.S, tx, ty, fmt, cw, ch, 0);
            rt.PopAxisAlignedClip();

            brush.Color = sel ? ChromeAccent : PalBorder;
            rt.DrawRectangle(new Rect(cx, cy, cellW, cellH), brush, sel ? 2f : 1f);
        }

        brush.Color = ChromeDim;
        rt.DrawText("Dashboard — arrows move · Enter open · Esc close", _uiSmall,
            new Rect(ax + 8f, ay + ah - footH + 3f, aw - 16f, footH - 4f), brush);
    }

    /// <summary>Key handling while the dashboard is up; swallows everything (view-only, modal).</summary>
    private bool DashboardKey(int vk)
    {
        var (cols, _) = DashGrid(_dashSessions.Count);
        int n = _dashSessions.Count;
        switch (vk)
        {
            case VK_ESCAPE: CloseDashboard(); return true;
            case VK_RETURN: case VK_SPACE:
                if (_dashSel >= 0 && _dashSel < n) { var s = _dashSessions[_dashSel]; CloseDashboard(); SetActive(s); }
                return true;
            case VK_LEFT: if (_dashSel % cols != 0) DashSelect(_dashSel - 1); return true;
            case VK_RIGHT: if (_dashSel % cols != cols - 1 && _dashSel + 1 < n) DashSelect(_dashSel + 1); return true;
            case VK_UP: if (_dashSel - cols >= 0) DashSelect(_dashSel - cols); return true;
            case VK_DOWN: if (_dashSel + cols < n) DashSelect(_dashSel + cols); return true;
            case VK_HOME: DashSelect(0); return true;
            case VK_END: DashSelect(n - 1); return true;
        }
        return true;
    }

    private void DashSelect(int i)
    {
        if (i < 0 || i >= _dashSessions.Count || i == _dashSel) return;
        _dashSel = i;
        Uia.Announce(_dashSessions[i].Name);
        RequestRedraw();
    }

    /// <summary>Mouse in the dashboard: single click highlights a cell, double-click drops into it.</summary>
    private void DashboardClick(int mx, int my, bool doubleClick)
    {
        for (int i = 0; i < _dashCells.Count; i++)
        {
            var c = _dashCells[i];
            if (mx >= c.Left && mx < c.Right && my >= c.Top && my < c.Bottom)
            {
                if (doubleClick) { var s = _dashSessions[i]; CloseDashboard(); SetActive(s); }
                else DashSelect(i);
                return;
            }
        }
    }
}
