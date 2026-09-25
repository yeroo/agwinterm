using System.Diagnostics;
using System.Text;
using Agwinterm.Core;
using Agwinterm.Pty;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>
/// The menu bar in the custom title bar — <c>File  View  Navigate  Help</c>, agterm's menus
/// (<see cref="MenuModel"/>) as a row of labels after the sidebar toggle, each dropping the themed
/// popup of Menu.cs. On macOS agterm's menus are the system bar; on Windows the equivalent surface
/// is the title bar this app already draws, so the bar lives there and takes the Windows keyboard
/// model: a lone Alt tap or F10 focuses it (←/→ move, ↓/Enter open, Esc leaves), Alt+F / V / N / H
/// open a menu directly, and inside an open menu ←/→ switch menus. A keymap.conf binding on an
/// Alt+letter chord wins over the mnemonic — <see cref="MenuBarMnemonic"/> checks the bindings and
/// a pending leader before it opens anything — so a shell that wants Alt+F keeps it by binding it.
///
/// <para>Every row shows its EFFECTIVE shortcut (a rebind shows the rebind), a row whose enablement
/// term is false is dim and inert, and a state row's label follows the state (Hide/Show Sidebar).
/// The bar is a UIA MenuBar of MenuItems (Invoke opens a menu or runs a row), so a screen reader
/// sees it. <c>show-menu-bar = false</c> removes it; the hidden toolbar mode shows no chrome and so
/// no bar.</para>
/// </summary>
internal partial class Program
{
    private const string MenuIdPrefix = "menu:";                  // chrome-button id of a bar label: "menu:<index>"
    private int _menuBarOpen = -1;                                // the menu whose dropdown is up (Menu.cs level 0), -1 none
    private int _menuBarFocus = -1;                               // the label holding keyboard focus with no dropdown (Alt / F10), -1 none
    private bool _altArmed;                                       // Alt went down and nothing else has since: its release focuses the bar
    private bool _menuAteChar;                                    // the last key-down was the menu's: its WM_CHAR is dropped, once
    private bool _menuByKeyboard;                                 // the open dropdown came from the keyboard (its first row is preselected)
    private readonly List<(float x0, float x1, int menu)> _menuBarLabels = new();   // label hit-boxes (DIPs), rebuilt each paint
    private Rect _menuBarRect;                                    // the bar's box (DIPs) for the UIA MenuBar element
    private Rect _titleTextRect;                                  // where the title is drawn (the window-rename field lands here)
    private static readonly object WindowRenameMarker = new();    // _editing's value while the title bar's rename field is up

    private bool MenuBarShown => _config.ShowMenuBar && !ToolbarHidden && !_isQuickWindow;

    /// <summary>The menu Alt+<paramref name="vk"/> opens, or -1. Letters only: the virtual-key codes of
    /// Numpad 6 / 8 / Decimal and F7 are the ASCII codes of f / h / n / v, and casting them to a char
    /// opened menus from an Alt-code entry and from Alt+F7.</summary>
    private static int MnemonicMenu(int vk) => vk is >= 0x41 and <= 0x5A ? MenuModel.MenuForMnemonic((char)vk) : -1;

    /// <summary>Keys whose key-down is followed by a WM_CHAR: the ones a consumed menu key must eat
    /// the char of. Arrows and function keys make none, and eating a later char for them would lose
    /// a keystroke.</summary>
    private static bool KeyMakesChar(int vk) => vk is VK_SPACE or VK_RETURN or VK_ESCAPE or VK_BACK or VK_TAB
        or (>= 0x30 and <= 0x5A) or (>= 0x60 and <= 0x6F) or (>= 0xBA and <= 0xE2);
    /// <summary>The bar takes input only while nothing modal owns the keyboard: Settings, Help, a
    /// palette, the native picker and an inline rename all do; the dashboard does not (its items
    /// still act on the tree behind it, as the keyboard's do).</summary>
    private bool MenuBarUsable => MenuBarShown && !_setOpen && !_helpOpen && _palette == PaletteKind.None && _nativePick is null && _editHwnd == IntPtr.Zero;

    /// <summary>Paint the labels from <paramref name="x"/>; returns the bar's right edge, or
    /// <paramref name="x"/> unchanged when the bar is hidden. A label that would run into the right
    /// button group (<paramref name="limit"/>) is dropped rather than drawn over it.</summary>
    private float DrawMenuBar(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float x, float limit)
    {
        _menuBarLabels.Clear();
        _menuBarRect = default;
        if (!MenuBarShown) return x;
        float start = x;
        for (int i = 0; i < MenuModel.Menus.Count; i++)
        {
            string title = MenuModel.Menus[i].Title;
            float lw = MeasureText(title, _uiFont) + 20f;
            if (x + lw > limit) break;
            bool lit = i == _menuBarOpen || i == _menuBarFocus;
            var c = ChromeBtnBg(rt, brush, x, 0, lw, TitleBarH, MenuIdPrefix + i, _titleButtons, lit ? SbActiveText : ChromeText);
            if (lit)
            {
                // The open / focused label keeps the pressed look for as long as its menu is up.
                brush.Color = WithA(ChromeText, _chromeDark ? 0.30f : 0.22f);
                rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(x + 3f, 5f, lw - 6f, TitleBarH - 10f), RadiusX = 6f, RadiusY = 6f }, brush);
                c = SbActiveText;
            }
            brush.Color = c;
            rt.DrawText(title, _uiFont, new Rect(x + 10f, (TitleBarH - 18f) / 2f, lw - 20f, 18f), brush, DrawTextOptions.Clip);
            _menuBarLabels.Add((x, x + lw, i));
            x += lw + 2f;
        }
        if (_menuBarLabels.Count == 0) return start;
        _menuBarRect = new Rect(start, 0f, x - 2f - start, TitleBarH);
        return x - 2f;
    }

    /// <summary>The bar label under a client DIP x on the title row, or null.</summary>
    private int? MenuBarLabelAt(float mx)
    {
        foreach (var (x0, x1, menu) in _menuBarLabels) if (mx >= x0 && mx < x1) return menu;
        return null;
    }

    /// <summary>The bar label under a SCREEN point (the popup routes its captured mouse here), or null.</summary>
    private int? MenuBarLabelAtScreen(POINT sp)
    {
        if (!MenuBarShown) return null;
        var pt = sp;
        ScreenToClient(_hwnd, ref pt);
        float mx = ToDip(pt.x), my = ToDip(pt.y);
        return my >= 0 && my < TitleBarH ? MenuBarLabelAt(mx) : null;
    }

    private static int MenuBarNeighbour(int menu, int dir)
    {
        int n = MenuModel.Menus.Count;
        return ((menu + dir) % n + n) % n;
    }

    /// <summary>Drop menu <paramref name="menu"/> under its label. From the keyboard the first
    /// row is preselected, as a native menu does when opened with Enter or a mnemonic.</summary>
    private void OpenMenuBar(int menu, bool keyboard = false)
    {
        if (!MenuBarShown || menu < 0 || menu >= MenuModel.Menus.Count) return;
        var items = BuildMenuItems(menu);
        float x0 = 8f;
        foreach (var (lx0, _, m) in _menuBarLabels) if (m == menu) { x0 = lx0; break; }
        var pt = new POINT { x = (int)(x0 * Scale), y = (int)(TitleBarH * Scale) };
        ClientToScreen(_hwnd, ref pt);
        _menuBarFocus = -1;
        var barTop = new POINT { x = 0, y = 0 };
        ClientToScreen(_hwnd, ref barTop);
        ShowMenuWindow(items, pt.x, pt.y, barTop.y);   // closes whatever was open, which clears _menuBarOpen; flips above the bar, never over it
        if (_menuLevels.Count == 0) return;
        _menuBarOpen = menu;
        _menuByKeyboard = keyboard;
        if (keyboard) MenuSelectEdge(_menuLevels[0], first: true);
        if (Uia.ClientsListening) _uia.Announce(MenuModel.Menus[menu].Title + " menu");
        RequestRedraw();
    }

    /// <summary>Alt tap / F10 / Esc-out-of-a-dropdown: keyboard focus on a bar label, no dropdown.</summary>
    private void FocusMenuBar(int menu)
    {
        if (!MenuBarShown) return;
        if (_menuLevels.Count > 0) CloseMenuWindow();   // focus on a label is the state WITHOUT a dropdown
        _menuBarFocus = Math.Clamp(menu, 0, MenuModel.Menus.Count - 1);
        if (Uia.ClientsListening) _uia.Announce("Menu bar. " + MenuModel.Menus[_menuBarFocus].Title);
        _uia.RaiseFocus(Uia.NodeKind.MenuItem, _menuBarFocus);
        RequestRedraw();
    }

    private void LeaveMenuBar()
    {
        if (_menuBarFocus < 0) return;
        _menuBarFocus = -1;
        _uia.RaiseFocus(Uia.NodeKind.Terminal, 0);
        RequestRedraw();
    }

    /// <summary>Keys while a bar label has the focus and no dropdown is up (the Windows menu
    /// model): ←/→ move along the bar, ↓/Enter/Space open, a mnemonic letter opens its menu,
    /// Esc / F10 / Alt leave; anything else leaves and is swallowed.</summary>
    private bool MenuBarKey(int vk)
    {
        switch (vk)
        {
            case VK_LEFT: FocusMenuBar(MenuBarNeighbour(_menuBarFocus, -1)); return true;
            case VK_RIGHT: FocusMenuBar(MenuBarNeighbour(_menuBarFocus, 1)); return true;
            case VK_DOWN: case VK_RETURN: case VK_SPACE: OpenMenuBar(_menuBarFocus, keyboard: true); return true;
            case VK_ESCAPE: case 0x79 /* F10 */: case VK_MENU: LeaveMenuBar(); return true;
            default:
                if (MnemonicMenu(vk) is >= 0 and var mn) { OpenMenuBar(mn, keyboard: true); return true; }
                if (Keymap.IsModifierKey(vk)) return true;
                LeaveMenuBar();
                return true;
        }
    }

    /// <summary>Alt+letter with the context bit set (a real or a posted WM_SYSKEYDOWN): open that
    /// menu — unless the chord is the user's: bound in keymap.conf, the leader chord itself, a leader
    /// follow-up, or any key while a leader sequence is pending. Then this returns false and the
    /// ordinary dispatch runs. Called from the window procedure, where the lParam context bit is,
    /// rather than from OnKeyDown, whose Alt comes from GetKeyState and is blind to posted input.</summary>
    private bool MenuBarMnemonic(int vk)
    {
        if (!MenuBarUsable || KeyDown(VK_CONTROL) || KeyDown(VK_SHIFT) || _leaderPending) return false;
        int menu = MnemonicMenu(vk);
        if (menu < 0) return false;
        if (Keymap.ChordFor(vk, ctrl: false, alt: true, shift: false) is { } chord
            && (_keymap.ContainsKey(chord) || _leader == chord || _leaderBindings.ContainsKey(chord))) return false;
        OpenMenuBar(menu, keyboard: true);   // closes an open dropdown first, so this is also how a menu switches
        return true;
    }

    // ---- UIA ----

    // A row's UIA identity names the MENU it belongs to (and, for a flyout row, the parent row), so an
    // element a client kept from File does not resolve to the same row of View after a switch:
    //   label:       menu                         (0..3)
    //   row:         100 + menu*100 + row         (100..499)
    //   flyout row:  10000 + (menu*100 + parentRow)*100 + row
    private static int MenuRowUiaIndex(int menu, int row) => 100 + menu * 100 + row;
    private static int MenuFlyoutUiaIndex(int menu, int parentRow, int row) => 10000 + (menu * 100 + parentRow) * 100 + row;
    private int MenuRowUiaIndex(MenuLevel lv, int row) => lv.ParentRow < 0 ? MenuRowUiaIndex(_menuBarOpen, row) : MenuFlyoutUiaIndex(_menuBarOpen, lv.ParentRow, row);

    /// <summary>The bar as a UIA MenuBar of MenuItems — the labels, and under the open one its rows
    /// (indices above; a flyout's rows under their parent row). Invoke opens or runs.</summary>
    private void AddMenuBarUiaNodes(List<Uia.Node> nodes, List<int> rootKids)
    {
        if (_menuBarLabels.Count == 0) return;
        int bar = nodes.Count;
        rootKids.Add(bar);
        nodes.Add(new Uia.Node { Kind = Uia.NodeKind.MenuBar, Index = 0, Name = "Menu bar", Parent = 0, Rect = ScreenRect(_menuBarRect.X, _menuBarRect.Y, _menuBarRect.Width, _menuBarRect.Height) });
        var kids = new List<int>();
        foreach (var (x0, x1, menu) in _menuBarLabels)
        {
            int label = nodes.Count;
            kids.Add(label);
            nodes.Add(new Uia.Node { Kind = Uia.NodeKind.MenuItem, Index = menu, Name = MenuModel.Menus[menu].Title, Parent = bar, Focused = _menuBarFocus == menu, Rect = ScreenRect(x0, 0, x1 - x0, TitleBarH) });
            if (menu == _menuBarOpen && _menuLevels.Count > 0) nodes[label].Children = MenuLevelUiaNodes(nodes, _menuLevels[0], label).ToArray();
        }
        nodes[bar].Children = kids.ToArray();
    }

    private List<int> MenuLevelUiaNodes(List<Uia.Node> nodes, MenuLevel lv, int parent)
    {
        var kids = new List<int>();
        for (int i = 0; i < lv.Items.Count; i++)
        {
            var it = lv.Items[i];
            if (IsSep(it)) continue;
            int node = nodes.Count;
            kids.Add(node);
            float top = MenuRowTop(lv, i);
            nodes.Add(new Uia.Node
            {
                Kind = Uia.NodeKind.MenuItem, Index = MenuRowUiaIndex(lv, i), Name = it.Label, Parent = parent,
                Enabled = MenuActionable(it), Accelerator = it.Hint,
                Focused = lv.Sel == i && ReferenceEquals(lv, _menuLevels[^1]),
                Rect = new UiaRect { Left = lv.X, Top = lv.Y + top * Scale, Width = lv.W * Scale, Height = MenuRowH * Scale },
            });
            if (it.Submenu is not null && _menuLevels.Count > 1 && ReferenceEquals(lv, _menuLevels[0]) && _menuLevels[1].ParentRow == i)
                nodes[node].Children = MenuLevelUiaNodes(nodes, _menuLevels[1], node).ToArray();
        }
        return kids;
    }

    /// <summary>A UIA client invoked a bar label (open / close its menu) or a row (run it, or open its flyout).</summary>
    private void MenuUiaInvoke(int index)
    {
        if (index < 100)
        {
            if (!MenuBarUsable) return;
            if (_menuBarOpen == index) CloseMenuWindow(); else OpenMenuBar(index);
            return;
        }
        int level, row, menu;
        if (index < 10000) { level = 0; menu = (index - 100) / 100; row = (index - 100) % 100; }
        else { int x = index - 10000; level = 1; menu = x / 10000; row = x % 100; if ((x / 100) % 100 != (_menuLevels.Count > 1 ? _menuLevels[1].ParentRow : -1)) return; }
        if (menu != _menuBarOpen || level >= _menuLevels.Count) return;   // an element kept from another menu, or from a closed one
        var lv = _menuLevels[level];
        if (row < 0 || row >= lv.Items.Count || !MenuActionable(lv.Items[row])) return;
        lv.Sel = row;
        if (lv.Items[row].Submenu is not null) { OpenMenuFlyout(row); return; }
        var run = lv.Items[row].Run!;
        CloseMenuWindow();
        run();
    }

    // ---- The rows ----

    /// <summary>Menu <paramref name="menu"/>'s rows for right now: labels by state, hints from the
    /// effective keymap, enablement from the terms, flyouts built lazily when opened.</summary>
    private List<PalItem> BuildMenuItems(int menu)
    {
        var list = new List<PalItem>();
        foreach (var def in MenuModel.Menus[menu].Items)
        {
            if (def.IsSeparator) { list.Add(MenuSeparator()); continue; }
            string label = def.AltLabel is not null && MenuStateOn(def.Id) ? def.AltLabel : def.Label;
            var item = new PalItem { Label = label, Search = label, Hint = def.Action is null ? "" : ChordHintFor(def.Action) };
            if (def.Term is { } term) item.Enabled = () => MenuTermOk(term);
            if (def.Flyout != MenuFlyout.None) { var fly = def.Flyout; item.Submenu = () => BuildMenuFlyout(fly); }
            else { var id = def.Id; item.Run = () => RunMenuCommand(id); }
            list.Add(item);
        }
        return list;
    }

    private List<PalItem> BuildMenuFlyout(MenuFlyout kind)
    {
        var list = new List<PalItem>();
        switch (kind)
        {
            case MenuFlyout.OpenWindow:
                // The window library, a check mark on the open ones: picking a closed one opens it,
                // an open one raises it.
                foreach (var w in Windows())
                {
                    string id = w.Id;
                    string name = w.Name.Length > 0 ? w.Name : "window " + (id.Length > 8 ? id[..8] : id);
                    list.Add(new PalItem { Label = name, Search = name, Checked = w.Open, Run = () => OpenLibraryWindow(id) });
                }
                break;
            case MenuFlyout.OpenRecent:
                if (_closedSessions.Count == 0 && _closedWorkspaces.Count == 0)
                { list.Add(new PalItem { Label = "No Recent Items" }); break; }
                if (_closedSessions.Count > 0)
                {
                    list.Add(new PalItem { Label = "Sessions" });
                    for (int ci = _closedSessions.Count - 1; ci >= 0 && ci >= _closedSessions.Count - 10; ci--)
                    { var cs = _closedSessions[ci]; list.Add(new PalItem { Label = cs.Display, Search = cs.Display, Run = () => ReopenClosedSession(cs) }); }
                }
                if (_closedWorkspaces.Count > 0)
                {
                    if (list.Count > 0) list.Add(MenuSeparator());
                    list.Add(new PalItem { Label = "Workspaces" });
                    for (int wi = _closedWorkspaces.Count - 1; wi >= 0 && wi >= _closedWorkspaces.Count - 10; wi--)
                    { var cw = _closedWorkspaces[wi]; string l = $"{cw.Name} ({cw.Sessions.Count})"; list.Add(new PalItem { Label = l, Search = l, Run = () => ReopenClosedWorkspace(cw) }); }
                }
                list.Add(MenuSeparator());
                list.Add(new PalItem { Label = "Clear Menu", Search = "Clear Menu", Run = () => { _closedSessions.Clear(); _closedWorkspaces.Clear(); } });
                break;
        }
        return list;
    }

    /// <summary>The effective chord for a keymap action as the row shows it: the default chord while
    /// it still maps to the action, else the first keymap.conf chord that does; the chords the app
    /// keeps out of the keymap (font zoom, Ctrl+`, the dashboard) come from a fixed table.</summary>
    private static string ChordHintFor(string action)
    {
        string? chord = null;
        foreach (var (c, a) in Keymap.DefaultBindings)
            if (a == action && _keymap.TryGetValue(c, out var bound) && bound == action) { chord = c; break; }
        if (chord is null)
            foreach (var kv in _keymap.OrderBy(k => k.Key, StringComparer.Ordinal))
                if (kv.Value == action) { chord = kv.Key; break; }
        if (chord is not null) return Keymap.DisplayChord(chord);
        return action switch
        {
            "increase_font_size" => "Ctrl+=",
            "decrease_font_size" => "Ctrl+-",
            "reset_font_size" => "Ctrl+0",
            "quick_terminal" => "Ctrl+`",
            "dashboard" => "Ctrl+Shift+D",
            _ => "",
        };
    }

    /// <summary>Whether a state row's ALTERNATE label applies (Hide → Show, Flag → Unflag, …).</summary>
    private bool MenuStateOn(string id) => id switch
    {
        "toggle_sidebar" => _sidebarW <= 0,
        "toggle_workspace_collapse" => CurrentWorkspace() is { Expanded: false },
        "toggle_flagged_view" => _sidebarMode == SidebarMode.Flagged,
        "toggle_flag" => _active is { Flagged: true },
        "focus_workspace" => _focusedWorkspaceId is not null,
        "toggle_scratch" => _coverKind == 1,
        "focus_left" or "focus_right" => _active is { Axis: SplitAxes.Horizontal },
        _ => false,
    };

    /// <summary>The enablement terms of <see cref="MenuModel"/>, read off the live tree.</summary>
    private bool MenuTermOk(string term)
    {
        switch (term)
        {
            case "session": return _active is not null;
            case "workspace": return CurrentWorkspace() is not null;
            case "workspaces": lock (_workspaces) return _workspaces.Count > 1;
            case "windows": lock (_windowIndex) return _windowIndex.Count > 1;
            case "openwindows": lock (_windowIndex) return _windowIndex.Count(m => m.IsOpen && _byId.ContainsKey(m.Id)) > 1;
            case "closed": return _closedSessions.Count > 0 || _closedWorkspaces.Count > 0;
            case "tree": return _sidebarMode == SidebarMode.Tree;
            case "flags": return _sidebarMode == SidebarMode.Flagged || AllSessions().Any(s => s.Flagged);
            case "anyflag": return AllSessions().Any(s => s.Flagged);
            case "split": return _active is { } s && s.Panes.Count > 1;
            case "status": return _active is { } a && AggStatus(a) != AgentStatus.Idle;
            case "sessions": return AllSessions().Count > 1;
            case "attention": return AllSessions().Any(s => AggStatus(s) != AgentStatus.Idle);
            default: return true;
        }
    }

    // ---- The commands ----

    private void RunMenuCommand(string id)
    {
        switch (id)
        {
            // File
            case "new_window": WindowNew(null); break;
            case "rename_window": StartWindowRename(); break;
            case "delete_window": WindowDelete(Id); break;
            case "new_workspace": CreateWorkspace(Guid.NewGuid().ToString(), null); break;
            case "rename_workspace": if (CurrentWorkspace() is { } rw) StartSidebarRename(rw); break;
            case "delete_workspace": DeleteCurrentWorkspace(); break;
            case "new_session": CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), true); break;
            case "open_directory": { var d = PickFolder(); if (d is not null) CreateSession(Guid.NewGuid().ToString(), null, d, ActiveWorkspace(), true); break; }
            case "reopen_recent": case "reopen_closed": ReopenMostRecent(); break;
            case "rename_session": if (_active is not null) StartSidebarRename(_active); break;
            case "duplicate_session": DuplicateSession(_active); break;
            case "reveal": RevealActiveInExplorer(); break;
            case "close_session": CloseActivePane(); break;   // the cover-first ladder Ctrl+Shift+W runs
            case "clear_status": if (_active is not null) { foreach (var p in _active.Panes) p.S.SetStatus(AgentStatus.Idle); RequestRedraw(); } break;
            case "edit_keymap": OpenInEditor(KeymapPath); break;
            case "reload_keymap": ReloadKeymap(); break;
            case "edit_config": OpenInEditor(ConfigPath); break;
            case "reload_config": ReloadConfigFromDisk(); break;
            // View
            case "increase_font": ChangeFontSize(1); break;
            case "decrease_font": ChangeFontSize(-1); break;
            case "reset_font": ChangeFontSize(0); break;
            case "select_theme": TogglePalette(PaletteKind.Themes); break;
            case "toggle_sidebar": ToggleSidebar(); break;
            case "expand_workspaces": SidebarOpInternal("expand"); break;
            case "collapse_workspaces": SidebarOpInternal("collapse"); break;
            case "toggle_workspace_collapse": ToggleWorkspaceCollapse(); break;
            case "toggle_flagged_view": ToggleFlaggedView(); break;
            case "toggle_flag": if (_active is not null) FlagOp(_active, "toggle"); break;
            case "clear_flagged": FlagOp(null, "clear"); break;
            case "focus_workspace": WorkspaceFocusOp("toggle"); break;
            case "toggle_split_v": SplitOp("toggle", _active, SplitAxes.Vertical); break;
            case "toggle_split_h": SplitOp("toggle", _active, SplitAxes.Horizontal); break;
            case "swap_panes": if (_active is not null) SwapPanes(_active); break;
            case "toggle_scratch": if (_active is not null) ScratchOp(_active, "toggle"); break;
            case "find": ToggleSearch(); break;
            case "quick_terminal": QuickOp("toggle"); break;
            case "toggle_fullscreen": ToggleFullscreen(); break;
            // Navigate
            case "session_palette": TogglePalette(PaletteKind.Sessions); break;
            case "action_palette": TogglePalette(PaletteKind.Actions); break;
            case "custom_palette": TogglePalette(PaletteKind.Custom); break;
            case "attention_list": TogglePalette(PaletteKind.Attention); break;
            case "dashboard": ToggleDashboard(); break;
            case "previous_session": CycleSession(-1); break;
            case "next_session": CycleSession(1); break;
            case "previous_attention": GoToNextAttention(-1); break;
            case "next_attention": GoToNextAttention(1); break;
            case "first_session": SessionGoInternal("first"); break;
            case "last_session": SessionGoInternal("last"); break;
            case "previous_workspace": NavigateWorkspace("prev"); break;
            case "next_workspace": NavigateWorkspace("next"); break;
            case "previous_window": StepWindow(-1); break;
            case "next_window": StepWindow(1); break;
            case "focus_left": FocusPane(-1); break;
            case "focus_right": FocusPane(1); break;
            // Help
            case "docs": OpenLink("https://github.com/yeroo/agwinterm/blob/main/docs/control-api.md"); break;
            case "install_cli": InstallCli(); break;
            case "install_hooks": InstallHooks(); break;
            case "install_skill": InstallSkill(); break;
            case "install_shell": InstallShellIntegration(); break;
            case "check_updates": ShowToast(UpdateAgwinterm(), 3000); break;
            case "about": ShowAbout(); break;
            default: ShowToast(id + " not implemented"); break;
        }
    }

    /// <summary>Rename a sidebar row from the menu: the row must be on screen for the inline field,
    /// so a hidden sidebar is shown first.</summary>
    private void StartSidebarRename(object item)
    {
        if (_sidebarW <= 0) ToggleSidebar();
        StartRename(item);
    }

    /// <summary>File ▸ Rename Window…: the sidebar's inline rename field, over the title text.</summary>
    private void StartWindowRename()
    {
        if (_editHwnd != IntPtr.Zero) CommitRename();
        if (_titleTextRect.Width <= 0) { ShowToast("no title bar to rename in"); return; }
        EnsureEditGdi();
        int ex = ToDevice(_titleTextRect.X), ey = ToDevice(_titleTextRect.Y + 6), ew = ToDevice(Math.Max(160f, _titleTextRect.Width)),
            eh = ToDevice(TitleBarH - 12f), margin = ToDevice(6);
        _editHwnd = CreateWindowExW(0, "EDIT", WinName, WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
            ex, ey, ew, eh, _hwnd, (IntPtr)EDIT_ID, GetModuleHandleW(null), IntPtr.Zero);
        if (_editHwnd == IntPtr.Zero) return;
        SendMessageW(_editHwnd, WM_SETFONT, _editFont, (IntPtr)1);
        SendMessageW(_editHwnd, EM_SETMARGINS, (IntPtr)(EC_LEFTMARGIN | EC_RIGHTMARGIN), (IntPtr)(margin | (margin << 16)));
        SendMessageW(_editHwnd, (uint)EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
        SetFocus(_editHwnd);
        _editProc = EditProc;
        _editOrigProc = SetWindowLongPtrW(_editHwnd, GWLP_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_editProc));
        _editing = WindowRenameMarker;
        RequestRedraw();
    }

    /// <summary>What the rename field commits for the window: the library entry and the title.</summary>
    private void RenameThisWindow(string name)
    {
        lock (_windowIndex) { var m = _windowIndex.FirstOrDefault(x => x.Id == Id); if (m is not null) m.Name = name; }
        WinName = name;
        RequestRedraw();
        SaveIndex();
    }

    /// <summary>File ▸ Open Window ▸ an entry: raise it when open, boot it from the library when closed.</summary>
    private void OpenLibraryWindow(string id)
    {
        if (WindowSelect(id)) return;
        WinMeta? m;
        lock (_windowIndex) m = _windowIndex.FirstOrDefault(x => x.Id == id);
        if (m is null) { ShowToast("that window is gone from the library"); return; }
        var win = CreateWindowInstance(m);
        Frontmost = win; _frontmostId = m.Id;
        SetForegroundWindow(win._hwnd);
        SaveIndex();
    }

    /// <summary>Navigate ▸ Previous / Next Window: step the OPEN windows in library order, wrapping.</summary>
    private void StepWindow(int dir)
    {
        List<string> open;
        lock (_windowIndex) open = _windowIndex.Where(m => m.IsOpen && _byId.ContainsKey(m.Id)).Select(m => m.Id).ToList();
        if (open.Count < 2) { ShowToast("no other window"); return; }
        int i = open.IndexOf(Id);
        string target = open[((i + dir) % open.Count + open.Count) % open.Count];
        WindowSelect(target);
    }

    private void RevealActiveInExplorer()
    {
        if (_active is null) return;
        string cwd = PaneCwd(_active.ActivePane);
        if (cwd.Length == 0 || !Directory.Exists(cwd)) { ShowToast("no directory to reveal"); return; }
        ShellExecuteW(IntPtr.Zero, "open", "explorer.exe", "/select,\"" + cwd + "\"", null, SW_SHOW);   // the directory selected in its parent, as agterm's Reveal does
    }

    /// <summary>Edit Keymap… / Edit agwinterm.conf…: the file in whatever the shell associates with
    /// it (agterm opens $EDITOR in an overlay; Windows has no $EDITOR convention).</summary>
    private void OpenInEditor(string path)
    {
        try
        {
            if (!File.Exists(path)) { if (path == KeymapPath) LoadKeymap(); else File.WriteAllText(path, TerminalConfig.DefaultText); }
            ShellExecuteW(_hwnd, "open", path, null, null, SW_SHOW);
        }
        catch (Exception ex) { ShowToast("could not open " + Path.GetFileName(path) + ": " + ex.Message, 3000); }
    }

    /// <summary>File ▸ Reload Config: re-read agwinterm.conf and apply every key it changed, exactly
    /// as a <c>config set</c> applies its key (<see cref="ReloadConfigApplying"/>), the file's
    /// quick-terminal hotkey registered whenever it is not the registered one (so a refusal at
    /// startup or by the last reload is retried). One toast, wrapped: the count, then every note the
    /// steps produced (a refusal, a backend switch) on its own line — a toast has one slot, and a
    /// later one would replace an earlier one before it was ever drawn.</summary>
    private void ReloadConfigFromDisk()
    {
        var notes = new List<string>();
        var changed = ReloadConfigApplying(null, notes);
        string summary = changed.Count == 0 ? "agwinterm.conf reloaded — nothing changed" : $"agwinterm.conf reloaded — {changed.Count} setting(s) applied";
        ShowToast(notes.Count == 0 ? summary : summary + "\n" + string.Join("\n", notes), notes.Count == 0 ? 2500 : 7000);
    }

    private void ShowAbout()
    {
        string full = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "dev";
        int plus = full.IndexOf('+');
        string commit = plus > 0 ? full[(plus + 1)..] : "";
        var sb = new StringBuilder();
        sb.Append("agwinterm ").Append(_termProgramVersion);
        if (commit.Length > 0) sb.Append(" (").Append(commit.Length > 12 ? commit[..12] : commit).Append(')');
        sb.Append("\n\nA native Windows terminal built for AI coding agents —\na homage to umputun's agterm.\n\nhttps://github.com/yeroo/agwinterm\n\nMIT © Boris Kudriashov");
        MessageBoxW(_hwnd, sb.ToString(), "About agwinterm", MB_OK | MB_ICONINFORMATION);
    }
}
