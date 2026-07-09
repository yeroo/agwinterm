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

/// <summary>The window procedure: WM_* dispatch for every Program window.</summary>
internal partial class Program
{
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
            case 0x003D: // WM_GETOBJECT — expose the terminal to screen readers (UIA, T2-14)
                { nint r = Uia.OnGetObject(hwnd, wParam, lParam); if (r != 0) return r; break; }

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

            case WM_DROPFILES:
                {
                    // Files/folders dropped onto the window: paste their quoted paths (space-joined)
                    // into the pane under the drop point — via PasteTextInto, so bracketed paste
                    // applies and the text can't auto-execute.
                    IntPtr hDrop = wParam;
                    try
                    {
                        DragQueryPoint(hDrop, out POINT dp);
                        uint n = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
                        var paths = new List<string>();
                        for (uint i = 0; i < n; i++)
                        {
                            uint len = DragQueryFileW(hDrop, i, null, 0);
                            if (len == 0) continue;
                            var sb = new StringBuilder((int)len + 1);
                            if (DragQueryFileW(hDrop, i, sb, len + 1) > 0) paths.Add(sb.ToString());
                        }
                        if (paths.Count > 0)
                        {
                            string text = string.Join(" ", paths.Select(p => p.IndexOfAny(new[] { ' ', '\'', '(', ')', '&', ';' }) >= 0 ? "\"" + p + "\"" : p));
                            var target = PaneAt(dp.x, dp.y)?.pane ?? ActiveSurface();
                            if (target is not null) PasteTextInto(target, text);
                        }
                    }
                    finally { DragFinish(hDrop); }
                    return IntPtr.Zero;
                }

            case WM_CONTEXTMENU:
                {
                    int sx = LoWord(lParam), sy = HiWord(lParam);
                    var pt = new POINT { x = sx, y = sy };
                    if (sx == -1 && sy == -1) { GetCursorScreen(out sx, out sy); pt.x = sx; pt.y = sy; } // keyboard menu key
                    ScreenToClient(hwnd, ref pt);
                    if (pt.x < (int)_sidebarW && pt.y >= (int)TitleBarH)
                    {
                        var item = RowAt(pt.y);
                        if (item is not null) ShowContextMenuWindow(item, sx, sy);   // screen coords
                        return IntPtr.Zero;
                    }
                    break;
                }

            case WM_PAINT:
                BeginPaint(hwnd, out PAINTSTRUCT ps);
                Render();
                _lastPaintTick = Environment.TickCount64;
                UpdateCaretPos();   // keep the (hidden) system caret on the text cursor for a11y follow
                EndPaint(hwnd, ref ps);
                return IntPtr.Zero;

            case WM_SETFOCUS:
                EnsureCaret();
                return IntPtr.Zero;

            case WM_KILLFOCUS:
                DropCaret();
                return IntPtr.Zero;

            case WM_APP_REDRAW:
            {
                System.Threading.Interlocked.Exchange(ref _redrawPending, 0);
                // Frame cap (RedrawMinIntervalMs): paint immediately when quiet; under sustained
                // output defer to a one-shot timer so floods render at ~66fps instead of per chunk.
                long since = Environment.TickCount64 - _lastPaintTick;
                if (since >= RedrawMinIntervalMs) InvalidateRect(hwnd, IntPtr.Zero, false);
                else if (!_redrawTimerArmed)
                {
                    _redrawTimerArmed = true;
                    SetTimer(hwnd, (IntPtr)RedrawTimer, (uint)Math.Max(1, RedrawMinIntervalMs - (int)since), IntPtr.Zero);
                }
                // Screen reader active: (re)arm the announce debounce — speaks once output settles.
                if (Uia.ClientsListening) SetTimer(hwnd, (IntPtr)UiaAnnounceTimer, UiaAnnounceQuietMs, IntPtr.Zero);
                return IntPtr.Zero;
            }

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
                if ((int)wParam == PromptPreviewTimer) { PromptPreviewTick(); return IntPtr.Zero; }
                if ((int)wParam == RedrawTimer)
                {
                    KillTimer(hwnd, (IntPtr)RedrawTimer); _redrawTimerArmed = false;
                    InvalidateRect(hwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;
                }
                if ((int)wParam == UiaAnnounceTimer)
                {
                    KillTimer(hwnd, (IntPtr)UiaAnnounceTimer);
                    AnnounceNewOutput();
                    return IntPtr.Zero;
                }
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
                if (!KeyDown(VK_CONTROL)) ClearLinkHover();   // Ctrl released: drop the link underline/cursor
                break;

            case WM_SETCURSOR:
                if (_linkUrl is not null && LoWord(lParam) == HTCLIENT)
                { SetCursor(LoadCursorW(IntPtr.Zero, IDC_HAND)); return (IntPtr)1; }   // hand over a hovered link
                break;

            case WM_CHAR:
                {
                    char c = (char)wParam;
                    if (_kittyAteChar) { _kittyAteChar = false; return IntPtr.Zero; }   // OnKeyDown already CSI-u-encoded this key
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
                    if (_chromeFocus) ExitChromeFocus(announce: false);   // a click leaves the F6 sidebar zone
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
                    // Quick terminal is a floating panel: its corner ✕ hides it, and a left-click anywhere
                    // in the "main window area" outside the panel also dismisses it (like a tool window).
                    if (_coverKind == 2)
                    {
                        var (qx, qy, qw, qh) = CoverRect();
                        var (bx, by, bw, bh) = CoverCloseRect(qx + qw, qy);
                        if (mx >= bx && mx < bx + bw && my >= by && my < by + bh) { HideCover(); return IntPtr.Zero; }
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
                    // Ctrl+click on a hovered link opens it (never starts a selection or reaches the app).
                    if (KeyDown(VK_CONTROL) && _linkUrl is { } lurl) { OpenLink(lurl); return IntPtr.Zero; }
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
                        h0.pane.BlockSel = KeyDown(VK_MENU) && _clickCount == 1;   // Alt+drag = rectangular
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
                    if (!_divDragging && !_sbPress && !_selecting)
                    {
                        if (KeyDown(VK_CONTROL)) UpdateLinkHover(LoWord(lParam), HiWord(lParam));   // Ctrl+hover: link detection
                        else ClearLinkHover();
                    }
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
                    // Ctrl+wheel: font zoom (Windows-wide convention), on the active surface.
                    if (KeyDown(VK_CONTROL) && pt.x >= (int)_sidebarW && pt.y >= (int)TitleBarH)
                    {
                        ChangeFontSize(HiWord(wParam) > 0 ? 1 : -1);
                        return IntPtr.Zero;
                    }
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
                _windowActive = LoWord(wParam) != 0;   // WA_ACTIVE/WA_CLICKACTIVE vs WA_INACTIVE (drives unfocused dim)
                if (_config.UnfocusedDim > 0) RequestRedraw();
                if (_windowActive && _frontmostId != Id) // this window is frontmost
                {
                    Frontmost = this; _frontmostId = Id; SaveIndex();
                }
                // Refocusing the app clears the badge of the session you're now looking at (agterm #164).
                if (_windowActive && _active is not null && _cover is null && UnreadOf(_active) > 0)
                { ClearUnread(_active); RequestRedraw(); }
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
}
