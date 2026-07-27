// agwinterm-lite M1 phase 1 (issue #134): layout core over the Rust server.
//
// M0 gave one session on the full stack (Rust pty-host + agwinterm-core replica
// + GDI ExtTextOutW/lpDx). M1p1 adds the layout skeleton with main-app parity:
//   - multiple sessions + sidebar (click to select, status marker)
//   - SPLITS from the start (Boris's #134 decision): vertical two-pane split,
//     per-pane session + focus, per-pane host resize
//   - text styles: bold/italic fonts, underline/strike lines, dim, inverse
//   - scrollback view (mouse wheel / Shift+PgUp/PgDn) over the replica history
// Keys: Ctrl+T new session · Ctrl+W close · Ctrl+Tab cycle · Ctrl+Shift+D
// split toggle · Ctrl+Shift+Left/Right focus pane.
// Still M1 phase 2: selection+clipboard, palette, astral glyphs, control API.
//
// Protocol v2 (protobuf; proto/ptyhost.proto). Zero terminal logic, zero ConPTY.

#include <windows.h>
#include <windowsx.h>
#include <commctrl.h>   // native TreeView (SysTreeView32) sidebar — ships with Windows, no external deps
#include <shlobj.h>     // SHBrowseForFolder (New Session in Folder…)
#include <dwmapi.h>     // dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE)
#include <uxtheme.h>    // SetWindowTheme — dark scrollbars on the tree
#include <string>
#include <vector>

// ---- WTL (third_party/wtl, MS-PL) — the UI layer is built on ATL/WTL -------------------------
// Header-only over Win32: same window messages and the same native controls underneath, but with
// typed control wrappers, message-map crackers and RAII GDI objects instead of hand-rolled switches.
#include <atlbase.h>
#include <atlapp.h>
CAppModule _Module;
#include <atlwin.h>
#include <atlframe.h>
#include <atlctrls.h>
#include <atlcrack.h>
#include <atlmisc.h>
#include <atlgdi.h>

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "uxtheme.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "advapi32.lib")   // registry (persisted font choice)

#include "proto/ptyhost.pb.h"
#include "proto/pb_encode.h"
#include "proto/pb_decode.h"
#include "control.h"

// ---- agwinterm-core C ABI (ABI v15) ----
struct FfiCell {
    int32_t rune;
    uint32_t fg, bg, attrs, width;
    uint32_t fgKind, fgIndex, fgRgb;
    uint32_t bgKind, bgIndex, bgRgb;
};
struct FfiEmuInfo {
    uint32_t cols, rows, cursorRow, cursorCol, cursorVisible, isAltScreen, historyCount;
    int64_t scrollGeneration;
    uint32_t mouseClick, mouseDrag, mouseMotion, mouseSgr, bracketedPaste;
    int32_t keyboardFlags;
    uint32_t scrollTop, scrollBottom, markCount, focusReporting, synchronizedOutput, win32InputMode, dynamicBg;
    int32_t cursorShape;
};
struct FfiMark {   // FTCS / OSC 133 boundary; lines are buffer-absolute, -1 = unset
    int64_t promptLine, commandLine, outputLine, endLine;
    uint32_t hasExit;
    int32_t exitCode;
};
static uint32_t (*core_abi)();
static void* (*emu_new)(uint32_t, uint32_t);
static void (*emu_free)(void*);
static bool (*emu_feed)(void*, const uint8_t*, uint32_t);
static bool (*emu_resize)(void*, uint32_t, uint32_t);
static bool (*emu_info)(void*, FfiEmuInfo*);
static bool (*emu_copy_grid)(void*, FfiCell*, uint32_t);
static bool (*emu_copy_history_row)(void*, uint32_t, FfiCell*, uint32_t);
static uint32_t (*emu_marks)(void*, FfiMark*, uint32_t);

static constexpr uint32_t kRequiredAbi = 15;
static constexpr uint32_t kAttrBold = 1, kAttrItalic = 2, kAttrUnderline = 4,
                          kAttrInverse = 8, kAttrDim = 16, kAttrStrike = 32;
static constexpr uint32_t kProtocolVersion = 2;
static constexpr int kSidebarW = 180;
static constexpr int kSplitterW = 5;   // draggable divider between the sidebar and the terminal
static constexpr int kSidebarMinW = 90;   // the splitter will not shrink the left pane past this
static int g_sidebarW = kSidebarW;     // current (resizable) sidebar width
static bool g_showSidebar = true, g_showToolbar = true, g_showStatus = true;   // View menu toggles (persisted)
static bool g_flagView = false;   // sidebar shows only flagged sessions (toolbar pennant / View menu)
static int g_focusWs = -1;        // focused workspace: the sidebar shows only this one (-1 = all)

// ---- sessions & layout ----
struct Session {
    std::string id;
    std::string status = "idle";   // control-API agent status (sidebar dot)
    std::wstring name;             // custom name (rename); empty = "session N"
    int ws = 0;                    // workspace this session belongs to (index into g_workspaces)
    bool flagged = false;          // user-flagged (working set); amber pennant in the tree, persisted
    int seenDone = 0;              // completed-command (FTCS) count when the session was last visible
    int unread = 0;                // commands finished while NOT visible — red count pill in the tree
    bool hidden = false;          // split-pane shell: a real shell, but NOT a sidebar/tree session
    std::string app, cwd;          // launch spec, remembered so the session can be restored on next launch
    std::vector<std::string> args; // ("" app = default PowerShell; empty args = wrap/bare per app)
    void* emu = nullptr;
    HANDLE data = INVALID_HANDLE_VALUE;
    HANDLE reader = nullptr;
    int scrollOff = 0;          // rows scrolled up into history (0 = live)
    bool exited = false;
    std::vector<FfiCell> grid;  // paint snapshot buffer
    std::vector<FfiCell> hrow;
};

// Agent status classification for the sidebar: BLOCKED = agent needs you (bold name),
// WORKING = agent busy (italic name + "(working…)"), NONE = plain.
enum { AGST_NONE, AGST_WORKING, AGST_BLOCKED };
static int statusClass(const std::string& s) {
    if (s == "working" || s == "busy" || s == "active" || s == "running") return AGST_WORKING;
    if (s == "blocked" || s == "waiting" || s == "attention" || s == "input") return AGST_BLOCKED;
    return AGST_NONE;
}
static HWND g_hwnd;
static HFONT g_treeItalic;      // italic variant of the sidebar font (agent "working" rows)
// Quick + scratch terminals: modal-ish popup windows, each hosting a dedicated (hidden) session. The
// overlay is a scratch that runs a one-shot command. g_focusOverride redirects input/paint focus to a
// popup's session while it's active.
static HWND g_quickHwnd, g_scratchHwnd, g_overlayHwnd;
static Session* g_quickSession, *g_scratchSession, *g_overlaySession, *g_focusOverride;
static HWND g_toolbar;          // native toolbar (New Session / New Workspace / Split)
static int g_toolbarH = 0;      // its height; the tree + terminal start below it
static HWND g_status;           // native status bar (msctls_statusbar32)
static int g_statusH = 0;       // its height; the terminal ends above it
// Effective chrome extents (respect the View toggles): content sits between these.
static int sidebarSpan() { return g_showSidebar ? g_sidebarW + kSplitterW : 0; }
static int toolbarTop()  { return g_showToolbar ? g_toolbarH : 0; }
static bool g_splitDrag = false;   // dragging the sidebar splitter
static bool g_treeDrag = false;    // dragging a session row in the sidebar (drag & drop)
static int  g_dragIdx = -1;        // session index being dragged
static HIMAGELIST g_dragImg = nullptr;   // TreeView_CreateDragImage ghost
static int  g_armIdx = -1;         // session under a fresh left-press (drag candidate)
static POINT g_armPt{};            // where that press landed (drag threshold)
static HTREEITEM g_armItem = nullptr;
static void relayout() {   // re-run the WM_SIZE layout + repaint after a toggle / splitter drag
    RECT c; GetClientRect(g_hwnd, &c);
    SendMessageW(g_hwnd, WM_SIZE, SIZE_RESTORED, MAKELPARAM(c.right, c.bottom));
    InvalidateRect(g_hwnd, nullptr, TRUE);
}
static bool inSplitter(int x, int y) {
    if (!g_showSidebar) return false;
    RECT c; GetClientRect(g_hwnd, &c);
    return x >= g_sidebarW && x < g_sidebarW + kSplitterW && y >= toolbarTop() && y < c.bottom - (g_showStatus ? g_statusH : 0);
}
static HIMAGELIST g_tbImages;   // 16x16 toolbar glyphs, drawn per theme (see drawToolbarGlyph)
static HWND g_tree;             // native SysTreeView32 sidebar (sessions)
static bool g_treeSyncing;      // suppress TVN_SELCHANGED while we rebuild the tree
static bool g_restoring;         // true while rebuilding sessions at startup (suppresses state saves)
static HTREEITEM g_ctxItem;     // right-clicked tree node (for the context menu)
static LPARAM g_ctxParam;       // its lParam: >=0 session index, <0 = -(workspace+1)
static HFONT g_fonts[4];        // [bold][italic]
static std::wstring g_ttFace;   // the bundled TrueType face (Meslo Nerd, or Consolas fallback)
// A font catalog entry: a face + the sizes it offers (cmd.exe-style face list + size dropdown).
// kind: 0 = scalable TrueType (antialiased), 1 = raster .fon (OEM charset, crisp), 2 = bitmap-embedded
// TrueType (crisp, exact strike). Size {h,w}: raster/bitmap use positive px (w 0 = auto); scalable uses
// negative h (point-ish) with w 0.
struct FontSize { const wchar_t* label; int h, w; };
struct FontEntry { const wchar_t* label; const wchar_t* face; int kind; bool avail; std::vector<FontSize> sizes; };
static std::vector<FontEntry> g_catalog;
static int g_faceIdx = 0, g_sizeIdx = 0;   // current selection into g_catalog
static bool g_haveCozette = false, g_haveTamzen = false;   // bundled bitmap fonts actually loaded
static HFONT g_uiFont;          // shell UI font (Segoe UI) for the toolbar buttons
static bool g_customColors = false;   // Properties->Colors: override the terminal's default fg/bg
static uint32_t g_defFg = 0xC0C0C0;   // packed 0xRRGGBB, legacy cmd.exe light gray on...
static uint32_t g_defBg = 0x000000;   // ...black
static bool g_dosPalette = true;      // Properties->Colors: remap ANSI indices to the muted EGA/VGA DOS palette

// ---- UI theme (Properties -> Appearance) -----------------------------------------------------
// Four modes. CLASSIC means "hands off": every control keeps whatever the OS draws, which is the
// look lite shipped with. DARK/LIGHT paint the chrome ourselves (comctl32 will not do it for us).
// AUTO follows the Windows "app mode" setting and re-resolves on WM_SETTINGCHANGE.
enum { TH_AUTO = 0, TH_DARK = 1, TH_LIGHT = 2, TH_CLASSIC = 3 };
static int g_themeMode = TH_AUTO;     // persisted as "Theme"
struct UiTheme {
    bool     dark;      // drives the DWM title bar + which control themes we ask for
    bool     classic;   // true = don't custom-draw anything, let the system do it
    COLORREF bar;       // toolbar / status bar / menu bar background
    COLORREF client;    // deepest surface (tree background)
    COLORREF text;      // normal label text
    COLORREF dim;       // secondary text
    COLORREF hot;       // hover fill
    COLORREF sel;       // selected row fill
    COLORREF accent;    // focus / hot border
    COLORREF border;    // separators, grooves
    uint32_t termFg;    // terminal default foreground (packed 0xRRGGBB)
    uint32_t termBg;    // terminal default background
};
static UiTheme g_th;
static HBRUSH g_thBrBar = nullptr, g_thBrClient = nullptr;   // cached theme brushes (dialog surfaces)

// Windows "Choose your mode" -> Dark returns 0 here.
static bool systemUsesDarkApps() {
    DWORD v = 1, sz = sizeof v;
    if (RegGetValueW(HKEY_CURRENT_USER,
                     L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
                     L"AppsUseLightTheme", RRF_RT_REG_DWORD, nullptr, &v, &sz) != ERROR_SUCCESS) return false;
    return v == 0;
}
static bool themeIsDark() { return g_themeMode == TH_DARK || (g_themeMode == TH_AUTO && systemUsesDarkApps()); }

static void resolveTheme() {
    UiTheme t{};
    t.classic = (g_themeMode == TH_CLASSIC);
    t.dark    = !t.classic && themeIsDark();
    if (t.classic) {                      // whatever the OS uses for dialogs/controls
        t.bar = GetSysColor(COLOR_BTNFACE);   t.client = GetSysColor(COLOR_WINDOW);
        t.text = GetSysColor(COLOR_BTNTEXT);  t.dim    = GetSysColor(COLOR_GRAYTEXT);
        t.hot = GetSysColor(COLOR_BTNHIGHLIGHT); t.sel  = GetSysColor(COLOR_HIGHLIGHT);
        t.accent = GetSysColor(COLOR_HIGHLIGHT); t.border = GetSysColor(COLOR_BTNSHADOW);
        t.termFg = 0xC0C0C0; t.termBg = 0x000000;   // the terminal itself stays a terminal
    } else if (t.dark) {
        t.bar = RGB(45,45,48); t.client = RGB(30,30,30);
        t.text = RGB(241,241,241); t.dim = RGB(150,150,150);
        t.hot = RGB(62,62,64); t.sel = RGB(38,79,120);
        t.accent = RGB(0,122,204); t.border = RGB(63,63,70);
        t.termFg = 0xC0C0C0; t.termBg = 0x000000;
    } else {
        t.bar = RGB(240,240,240); t.client = RGB(255,255,255);
        t.text = RGB(26,26,26); t.dim = RGB(110,110,110);
        t.hot = RGB(229,243,255); t.sel = RGB(205,232,255);
        t.accent = RGB(0,120,215); t.border = RGB(205,205,205);
        t.termFg = 0xC0C0C0; t.termBg = 0x000000;
    }
    g_th = t;
    if (g_thBrBar) DeleteObject(g_thBrBar);
    if (g_thBrClient) DeleteObject(g_thBrClient);
    g_thBrBar = CreateSolidBrush(t.bar);
    g_thBrClient = CreateSolidBrush(t.client);
}
// Terminal defaults: an explicit Properties override always wins over the theme.
static uint32_t themeFg() { return g_customColors ? g_defFg : g_th.termFg; }
static uint32_t themeBg() { return g_customColors ? g_defBg : g_th.termBg; }

// Undocumented uxtheme entry points (Win10 1903+), exported by ordinal only. These are what File
// Explorer itself uses; opting the process into dark mode is what makes USER32 draw the MENU BAR and
// popup menus dark, and gives the tree dark scrollbars. Resolved defensively: absent = no-op.
namespace darkmode {
    enum PreferredAppMode { Default_, AllowDark, ForceDark, ForceLight, Max_ };
    typedef PreferredAppMode (WINAPI* fnSetPreferredAppMode)(PreferredAppMode);
    typedef BOOL (WINAPI* fnAllowDarkModeForWindow)(HWND, BOOL);
    typedef void (WINAPI* fnFlushMenuThemes)();
    typedef void (WINAPI* fnRefreshImmersiveColorPolicyState)();
    static fnSetPreferredAppMode    pSetPreferredAppMode    = nullptr;
    static fnAllowDarkModeForWindow pAllowDarkModeForWindow = nullptr;
    static fnFlushMenuThemes        pFlushMenuThemes        = nullptr;
    static fnRefreshImmersiveColorPolicyState pRefreshImmersive = nullptr;
    static void resolve() {
        static bool done = false; if (done) return; done = true;
        HMODULE ux = GetModuleHandleW(L"uxtheme.dll");
        if (!ux) ux = LoadLibraryExW(L"uxtheme.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!ux) return;
        pSetPreferredAppMode    = (fnSetPreferredAppMode)   GetProcAddress(ux, MAKEINTRESOURCEA(135));
        pAllowDarkModeForWindow = (fnAllowDarkModeForWindow)GetProcAddress(ux, MAKEINTRESOURCEA(133));
        pFlushMenuThemes        = (fnFlushMenuThemes)       GetProcAddress(ux, MAKEINTRESOURCEA(136));
        pRefreshImmersive       = (fnRefreshImmersiveColorPolicyState)GetProcAddress(ux, MAKEINTRESOURCEA(104));
    }
    static void setAppMode(int mode) {   // TH_* -> process-wide app mode
        resolve();
        if (pSetPreferredAppMode)
            pSetPreferredAppMode(mode == TH_CLASSIC ? Default_ : (themeIsDark() ? ForceDark : ForceLight));
        if (pRefreshImmersive) pRefreshImmersive();   // without this the DarkMode_* styles don't render
        if (pFlushMenuThemes) pFlushMenuThemes();
    }
    static void allowWindow(HWND h, bool dark) {
        resolve();
        if (pAllowDarkModeForWindow && h) pAllowDarkModeForWindow(h, dark ? TRUE : FALSE);
    }
}

static void darkTitleBar(HWND h, bool dark) {
    if (!h) return;
    BOOL v = dark ? TRUE : FALSE;
    if (FAILED(DwmSetWindowAttribute(h, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, &v, sizeof v)))
        DwmSetWindowAttribute(h, 19, &v, sizeof v);   // older Win10 builds used attribute 19
}

// ---- dark menu bar (WM_UAH*) ------------------------------------------------------------------
// The process dark-mode opt-in themes popup MENUS, but USER32 never themes the menu BAR. The
// undocumented WM_UAH* messages are the hook Explorer/Notepad++ use to custom-draw it while keeping
// native behaviour (checkmarks, accelerators, keyboard navigation) everywhere else.
#define WM_UAHDRAWMENU        0x0091
#define WM_UAHDRAWMENUITEM    0x0092
typedef union  { struct { DWORD cx, cy; } rgSizes[2]; } UAHMENUITEMMETRICS;
typedef struct { DWORD rgcx[4]; DWORD fUpdateMaxWidths : 2; } UAHMENUPOPUPMETRICS;
typedef struct { HMENU hmenu; HDC hdc; DWORD dwFlags; } UAHMENU;
typedef struct { int iPosition; UAHMENUITEMMETRICS umim; UAHMENUPOPUPMETRICS umpm; } UAHMENUITEM;
typedef struct { DRAWITEMSTRUCT dis; UAHMENU um; UAHMENUITEM umi; } UAHDRAWMENUITEM;

// USER32 also paints a light 3-pixel 3-D edge UNDER the menu bar, in the non-client area, that the
// UAH draw never covers — overpaint it after every non-client paint while a themed look is active.
static void themeMenuSeam(HWND h) {
    if (!GetMenu(h)) return;
    RECT wr, cr; POINT cp{ 0, 0 };
    GetWindowRect(h, &wr); GetClientRect(h, &cr); ClientToScreen(h, &cp);
    RECT strip{ cp.x - wr.left, 0, 0, cp.y - wr.top };
    strip.right = strip.left + cr.right;
    strip.top = strip.bottom - 4;
    MENUBARINFO mbi{ sizeof mbi };
    if (GetMenuBarInfo(h, OBJID_MENU, 0, &mbi)) {
        int b = mbi.rcBar.bottom - wr.top;
        if (b < strip.bottom && b > 0) strip.top = b;
    }
    if (strip.top >= strip.bottom) return;
    if (HDC dc = GetWindowDC(h)) {
        HBRUSH br = CreateSolidBrush(g_th.bar);
        FillRect(dc, &strip, br);
        DeleteObject(br);
        ReleaseDC(h, dc);
    }
}

// ---- themed dialogs ---------------------------------------------------------------------------
// The hand-built dialogs (Properties / Keyboard / New Session) are plain windows, so dark mode is:
// dark title bar, DarkMode_* visual styles on the children (buttons/lists/combos/scrollbars), and
// WM_CTLCOLOR* + WM_ERASEBKGND answered with theme brushes. Light/Classic keep the system look.
static BOOL CALLBACK themeDlgChild(HWND c, LPARAM) {
    wchar_t cls[32]{};
    GetClassNameW(c, cls, 32);
    darkmode::allowWindow(c, g_th.dark);
    if (!lstrcmpW(cls, L"ComboBox"))
        SetWindowTheme(c, g_th.dark ? L"DarkMode_CFD" : L"CFD", nullptr);
    else if (!lstrcmpW(cls, L"Button") || !lstrcmpW(cls, L"ListBox") || !lstrcmpW(cls, L"ScrollBar") ||
             !lstrcmpW(cls, L"Edit"))
        SetWindowTheme(c, g_th.dark ? L"DarkMode_Explorer" : L"Explorer", nullptr);
    else if (!lstrcmpW(cls, L"msctls_hotkey32")) {
        // dark hotkeys are fully custom-painted (see hotkeyProc); swap the light system WS_BORDER
        // for the painted one so no bright frame remains
        LONG st = (LONG)GetWindowLongPtrW(c, GWL_STYLE);
        LONG want = (g_th.dark && !g_th.classic) ? (st & ~WS_BORDER) : (st | WS_BORDER);
        if (want != st) {
            SetWindowLongPtrW(c, GWL_STYLE, want);
            SetWindowPos(c, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }
    InvalidateRect(c, nullptr, TRUE);
    return TRUE;
}
static void themeDialog(HWND dlg) {
    if (!dlg) return;
    darkmode::allowWindow(dlg, g_th.dark);
    darkTitleBar(dlg, g_th.dark);
    EnumChildWindows(dlg, themeDlgChild, 0);
    InvalidateRect(dlg, nullptr, TRUE);
}
// Owner-drawn dialog buttons (push / checkbox / radio) + combo paint — see drawDlgButton below.
// Without a comctl32 v6 manifest these are v5/user32 classic controls: DarkMode_* styles cannot
// render on them, so the themed looks draw them by hand; Classic uses DrawFrameControl, which is
// the exact classic renderer, so nothing changes there.
static void drawDlgButton(LPDRAWITEMSTRUCT d);
static LRESULT CALLBACK comboProc(HWND h, UINT m, WPARAM w, LPARAM l, UINT_PTR id, DWORD_PTR);

// Shared message handling for the dialog procs; returns true (with *r set) when the theme answered.
static bool themeDlgMsg(HWND h, UINT m, WPARAM w, LRESULT* r) {
    if (!g_th.dark || g_th.classic) return false;
    HDC dc = (HDC)w;
    switch (m) {
        case WM_ERASEBKGND: {
            RECT rc; GetClientRect(h, &rc);
            FillRect(dc, &rc, g_thBrBar);
            *r = 1; return true;
        }
        case WM_CTLCOLORSTATIC:   // also checkboxes/radios (non-push buttons report as STATIC)
        case WM_CTLCOLORBTN:
            SetTextColor(dc, g_th.text); SetBkColor(dc, g_th.bar);
            *r = (LRESULT)g_thBrBar; return true;
        case WM_CTLCOLORLISTBOX:
        case WM_CTLCOLOREDIT:
            SetTextColor(dc, g_th.text); SetBkColor(dc, g_th.client);
            *r = (LRESULT)g_thBrClient; return true;
    }
    return false;
}

// ---- dark hotkey fields -----------------------------------------------------------------------
// msctls_hotkey32 never sends WM_CTLCOLOR* and ignores SetWindowTheme, so dark mode takes over its
// WM_PAINT outright: dark field, themed border (accent when focused), and the binding text redrawn
// from HKM_GETHOTKEY (same names the control itself would show).
static LRESULT CALLBACK hotkeyProc(HWND h, UINT m, WPARAM w, LPARAM l, UINT_PTR id, DWORD_PTR) {
    if (m == WM_NCDESTROY) RemoveWindowSubclass(h, hotkeyProc, id);
    if (g_th.dark && !g_th.classic) {
        if (m == WM_NCPAINT) {   // the classic WS_BORDER ring is drawn here — paint it dark instead
            if (HDC dc = GetWindowDC(h)) {
                RECT wr; GetWindowRect(h, &wr);
                RECT r{ 0, 0, wr.right - wr.left, wr.bottom - wr.top };
                FrameRect(dc, &r, g_thBrClient);   // outer ring matches the field; painted border inside
                ReleaseDC(h, dc);
            }
            return 0;
        }
        if (m == WM_ERASEBKGND) return 1;
        if (m == WM_SETFOCUS || m == WM_KILLFOCUS) InvalidateRect(h, nullptr, TRUE);   // focus ring
        if (m == WM_PAINT) {
            PAINTSTRUCT ps;
            HDC dc = BeginPaint(h, &ps);
            RECT rc; GetClientRect(h, &rc);
            FillRect(dc, &rc, g_thBrClient);
            HBRUSH fr = CreateSolidBrush(GetFocus() == h ? g_th.accent : g_th.border);
            FrameRect(dc, &rc, fr); DeleteObject(fr);
            WORD hk = (WORD)SendMessageW(h, HKM_GETHOTKEY, 0, 0);
            std::wstring txt;
            if (!hk) txt = L"None";
            else {
                BYTE mod = HIBYTE(hk), vk = LOBYTE(hk);
                if (mod & HOTKEYF_CONTROL) txt += L"Ctrl + ";
                if (mod & HOTKEYF_SHIFT)   txt += L"Shift + ";
                if (mod & HOTKEYF_ALT)     txt += L"Alt + ";
                UINT sc = MapVirtualKeyW(vk, MAPVK_VK_TO_VSC) << 16;
                if (mod & HOTKEYF_EXT) sc |= 0x01000000;
                wchar_t name[64]{};
                txt += (GetKeyNameTextW((LONG)sc, name, 64) > 0) ? name : L"?";
            }
            HFONT of = g_uiFont ? (HFONT)SelectObject(dc, g_uiFont) : nullptr;
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, hk ? g_th.text : g_th.dim);
            RECT tr = rc; tr.left += 6;
            DrawTextW(dc, txt.c_str(), -1, &tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
            if (of) SelectObject(dc, of);
            EndPaint(h, &ps);
            return 0;
        }
    }
    return DefSubclassProc(h, m, w, l);
}

// ---- dark field bezels ------------------------------------------------------------------------
// v5 listboxes draw their WS_BORDER / WS_EX_CLIENTEDGE bezel with classic system colours that no
// theme reaches. Let the default NC paint run (it also draws the scrollbar), then repaint the outer
// bezel rings dark. Classic/light leave the system bezel alone.
static LRESULT CALLBACK fieldRingProc(HWND h, UINT m, WPARAM w, LPARAM l, UINT_PTR id, DWORD_PTR) {
    if (m == WM_NCDESTROY) RemoveWindowSubclass(h, fieldRingProc, id);
    if (m == WM_NCPAINT && g_th.dark && !g_th.classic) {
        LRESULT r = DefSubclassProc(h, m, w, l);   // border + scrollbar first
        if (HDC dc = GetWindowDC(h)) {
            RECT wr; GetWindowRect(h, &wr);
            RECT rc{ 0, 0, wr.right - wr.left, wr.bottom - wr.top };
            HBRUSH br = CreateSolidBrush(g_th.border);
            FrameRect(dc, &rc, br);                              // outer ring
            DWORD ex = (DWORD)GetWindowLongPtrW(h, GWL_EXSTYLE);
            if (ex & WS_EX_CLIENTEDGE) {                          // client edge is 2px deep
                InflateRect(&rc, -1, -1);
                HBRUSH in = CreateSolidBrush(g_th.client);
                FrameRect(dc, &rc, in);
                DeleteObject(in);
            }
            DeleteObject(br);
            ReleaseDC(h, dc);
        }
        return r;
    }
    return DefSubclassProc(h, m, w, l);
}

// ---- dark status bar --------------------------------------------------------------------------
// The native status bar ignores colour requests entirely; a full WM_PAINT takeover (via comctl32
// subclassing) is the only clean way. Reads the theme at paint time, so switching just repaints.
static LRESULT CALLBACK statusProc(HWND h, UINT m, WPARAM w, LPARAM l, UINT_PTR id, DWORD_PTR) {
    if (m == WM_NCDESTROY) RemoveWindowSubclass(h, statusProc, id);
    if (g_th.dark && !g_th.classic) {
        if (m == WM_ERASEBKGND) return 1;
        if (m == WM_PAINT) {
            InvalidateRect(h, nullptr, FALSE);   // repaint whole bar (comctl32 invalidates only the changed part)
            PAINTSTRUCT ps;
            HDC dc = BeginPaint(h, &ps);
            RECT rc; GetClientRect(h, &rc);
            HBRUSH chrome = CreateSolidBrush(g_th.bar), border = CreateSolidBrush(g_th.border);
            FillRect(dc, &rc, chrome);
            RECT line{ rc.left, rc.top, rc.right, rc.top + 1 };
            FillRect(dc, &line, border);   // hairline against the terminal
            int edges[8];
            int n = (int)SendMessageW(h, SB_GETPARTS, 8, (LPARAM)edges);
            HFONT of = g_uiFont ? (HFONT)SelectObject(dc, g_uiFont) : nullptr;
            SetBkMode(dc, TRANSPARENT); SetTextColor(dc, g_th.text);
            for (int i = 0; i < n; i++) {
                wchar_t buf[256]{};
                SendMessageW(h, SB_GETTEXTW, i, (LPARAM)buf);
                int x0 = i ? edges[i - 1] : 0, x1 = edges[i];
                if (x1 < 0 || x1 > rc.right) x1 = rc.right;
                RECT tr{ x0 + 7, rc.top + 2, x1 - 4, rc.bottom };
                DrawTextW(dc, buf, -1, &tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
                if (i < n - 1) { RECT sep{ x1, rc.top + 4, x1 + 1, rc.bottom - 3 }; FillRect(dc, &sep, border); }
            }
            if (of) SelectObject(dc, of);
            DeleteObject(chrome); DeleteObject(border);
            EndPaint(h, &ps);
            return 0;
        }
    }
    return DefSubclassProc(h, m, w, l);
}

static void buildToolbarImages();   // fwd — the image list is re-flattened per theme

// Re-theme every live window/control. Safe to call before the frame exists (all handles null-checked).
static void applyTheme() {
    resolveTheme();
#ifdef AGW_THEME_DEBUG
    { FILE* f = nullptr; _wfopen_s(&f, L"C:\\Users\\boris\\AppData\\Local\\Temp\\lite-theme.txt", L"a");
      if (f) { fwprintf(f, L"mode=%d dark=%d classic=%d tree=%p hwnd=%p client=%06X\n",
                        g_themeMode, (int)g_th.dark, (int)g_th.classic, (void*)g_tree, (void*)g_hwnd,
                        (unsigned)g_th.client); fclose(f); } }
#endif
    darkmode::setAppMode(g_themeMode);                     // menu bar + popup menus follow this
    for (HWND h : { g_hwnd, g_quickHwnd, g_scratchHwnd, g_overlayHwnd }) {
        if (!h) continue;
        darkmode::allowWindow(h, g_th.dark);
        darkTitleBar(h, g_th.dark);
    }
    if (g_tree) {
        darkmode::allowWindow(g_tree, g_th.dark);
        // A themed tree paints its own background and IGNORES TVM_SETBKCOLOR, so for the themed looks
        // we strip the visual style ("") and own the colours outright. Classic restores the default.
        SetWindowTheme(g_tree, g_th.classic ? nullptr : L"", g_th.classic ? nullptr : L"");
        TreeView_SetBkColor(g_tree,   g_th.classic ? (COLORREF)-1 : g_th.client);
        TreeView_SetTextColor(g_tree, g_th.classic ? (COLORREF)-1 : g_th.text);
        TreeView_SetLineColor(g_tree, g_th.classic ? (COLORREF)-1 : g_th.border);
        // The sunken WS_EX_CLIENTEDGE is drawn with light system colours — drop it on dark.
        DWORD ex = (DWORD)GetWindowLongPtrW(g_tree, GWL_EXSTYLE);
        DWORD want = g_th.dark ? (ex & ~WS_EX_CLIENTEDGE) : (ex | WS_EX_CLIENTEDGE);
        if (want != ex) {
            SetWindowLongPtrW(g_tree, GWL_EXSTYLE, want);
            SetWindowPos(g_tree, nullptr, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        InvalidateRect(g_tree, nullptr, TRUE);
    }
    if (g_toolbar) {
        // The classic toolbar draws a #A0A0A0/#FFFFFF 3-D divider at its top — THE white line under
        // the menu on dark. Themed looks drop it (CCS_NODIVIDER); Classic keeps its classic groove.
        LONG st = (LONG)GetWindowLongPtrW(g_toolbar, GWL_STYLE);
        LONG want = g_th.classic ? (st & ~CCS_NODIVIDER) : (st | CCS_NODIVIDER);
        if (want != st) {
            SetWindowLongPtrW(g_toolbar, GWL_STYLE, want);
            SetWindowPos(g_toolbar, nullptr, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        buildToolbarImages();   // re-flatten the PNG icons against the new bar colour
    }
    if (g_status)  InvalidateRect(g_status,  nullptr, TRUE);
    if (g_hwnd) {
        DrawMenuBar(g_hwnd);
        RedrawWindow(g_hwnd, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_FRAME);
    }
}
// Configurable key bindings for every lite action. ALL UNBOUND BY DEFAULT so no combo is stolen from
// the shell/TUI until the user assigns one in File -> Keyboard. Stored HOTKEY-format: LOBYTE = vk,
// HIBYTE = HOTKEYF_* (SHIFT 1 / CONTROL 2 / ALT 4). 0 = unbound.
enum { KB_NEW, KB_NEWWS, KB_CLOSE, KB_SPLIT, KB_NEXT, KB_PREV, KB_COPY, KB_PASTE,
       KB_PALETTE, KB_FOCUSL, KB_FOCUSR, KB_SCROLLUP, KB_SCROLLDN, KB_QUICK, KB_SCRATCH, KB_REOPEN,
       KB_ZOOMIN, KB_ZOOMOUT, KB_ZOOMRESET, KB_FLAG, KB_FLAGVIEW, KB_ATTENTION, KB_FOCUSWS, KB_COUNT };
struct KbInfo { const wchar_t* label; const wchar_t* reg; };
static const KbInfo kKbInfo[KB_COUNT] = {
    { L"New Session",      L"Key_New" },     { L"New Workspace",    L"Key_NewWs" },
    { L"Close Session",    L"Key_Close" },   { L"Split / Unsplit",  L"Key_Split" },
    { L"Next Session",     L"Key_Next" },    { L"Previous Session", L"Key_Prev" },
    { L"Copy",             L"Key_Copy" },    { L"Paste",            L"Key_Paste" },
    { L"Command Palette",  L"Key_Palette" }, { L"Focus Left Pane",  L"Key_FocusL" },
    { L"Focus Right Pane", L"Key_FocusR" },  { L"Scroll Up",        L"Key_ScrollUp" },
    { L"Scroll Down",      L"Key_ScrollDn" }, { L"Quick Terminal",   L"Key_Quick" },
    { L"Scratch Terminal", L"Key_Scratch" },  { L"Reopen Closed",    L"Key_Reopen" },
    { L"Zoom In",          L"Key_ZoomIn" },   { L"Zoom Out",         L"Key_ZoomOut" },
    { L"Zoom Reset",       L"Key_ZoomReset" }, { L"Flag / Unflag",   L"Key_Flag" },
    { L"Flagged View",     L"Key_FlagView" },  { L"Next Blocked",    L"Key_Attention" },
    { L"Focus Workspace",  L"Key_FocusWs" },
};
static WORD g_keys[KB_COUNT] = { 0 };
static bool g_swallowChar = false;   // set when a keydown was consumed by a binding, to drop its WM_CHAR
// The authentic 16-colour EGA/VGA text palette (0x00/0x55/0xAA/0xFF steps) — dimmer than modern ANSI,
// the classic MS-DOS look (e.g. Far's blue becomes 0x0000AA, not a bright 0x0000FF). Indexed in ANSI
// order (0 black, 1 red, 2 green, 3 yellow, 4 blue, 5 magenta, 6 cyan, 7 white; +8 = bright) to match
// the emulator's colour indices — NOT the CGA hardware order, or red/blue would swap.
static const uint32_t kEgaPalette[16] = {
    0x000000, 0xAA0000, 0x00AA00, 0xAA5500, 0x0000AA, 0xAA00AA, 0x00AAAA, 0xAAAAAA,
    0x555555, 0xFF5555, 0x55FF55, 0xFFFF55, 0x5555FF, 0xFF55FF, 0x55FFFF, 0xFFFFFF };

// Menu command ids reuse the palette action ids (1 new, 2 close, 3 split, 4 next, 5 copy, 6 paste).
enum { IDM_NEW = 1, IDM_CLOSE = 2, IDM_SPLIT = 3, IDM_NEXT = 4, IDM_COPY = 5, IDM_PASTE = 6, IDM_PREV = 7,
       IDM_EXIT = 100, IDM_ABOUT = 101, IDM_NEWWS = 102, IDM_RESTART = 103, IDM_SHOW = 104,
       IDM_DUP = 105, IDM_RENAME = 106, IDM_DELWS = 107, IDM_PROPERTIES = 108, IDM_KEYBOARD = 109,
       IDM_QUICK = 120, IDM_SCRATCH = 121, IDM_REOPEN = 122,
       IDM_TG_SIDEBAR = 123, IDM_TG_TOOLBAR = 124, IDM_TG_STATUS = 125,
       IDM_FLAG = 126, IDM_FLAGVIEW = 127, IDM_ATTENTION = 128, IDM_FOCUSWS = 129 };
#define IDM_MOVE_BASE 300   // "Move to workspace <w>" = IDM_MOVE_BASE + w
enum { ID_TREE = 200, ID_TRAY = 201, ID_TOOLBAR = 202, ID_STATUS = 203 };

// Toolbar: every full-app chrome button that has a lite equivalent, in the full app's order
// (sidebar toggle | session/workspace | split/scratch/quick | recent | settings). Icons are the
// full app's vector glyphs, redrawn in GDI per theme (see drawToolbarGlyph).
static const struct { int id; int img; bool check; const wchar_t* tip; } kTbButtons[] = {
    { IDM_TG_SIDEBAR, 0, false, L"Toggle Sidebar" },
    { IDM_NEW,        1, false, L"New Session" },
    { IDM_NEWWS,      2, false, L"New Workspace" },
    { IDM_SPLIT,      3, false, L"Split / Unsplit" },
    { IDM_SCRATCH,    4, false, L"Scratch Terminal" },
    { IDM_QUICK,      5, false, L"Quick Terminal" },
    { IDM_FLAGVIEW,   8, true,  L"Flagged View" },
    { IDM_ATTENTION,  9, false, L"Attention — next blocked session" },
    { IDM_REOPEN,     6, false, L"Reopen Closed Session" },
    { IDM_PROPERTIES, 7, false, L"Properties" },
};
static constexpr int kTbCount = (int)(sizeof kTbButtons / sizeof kTbButtons[0]);
static constexpr int kTbImgCount = 11;   // 0..9 per the table + 10 = the bell in the alert colour
static int tbImageOf(int cmdId) {
    for (const auto& b : kTbButtons) if (b.id == cmdId) return b.img;
    return -1;
}
#define WM_APP_REFRESHTREE (WM_APP + 3)   // posted from worker threads to rebuild the tree on the UI thread
#define WM_APP_TRAY        (WM_APP + 4)   // system-tray icon notifications
#define WM_APP_OVERLAY     (WM_APP + 5)   // control thread -> UI thread: open an overlay (creates a window)
static std::string g_pendingOverlayCmd; static int g_pendingOverlaySize;
static HICON g_appIcon;
static NOTIFYICONDATAW g_nid{};
static int g_cw = 8, g_ch = 16;
static CRITICAL_SECTION g_lock; // guards every session's emu + the session list shape
static HANDLE g_control = INVALID_HANDLE_VALUE;
static std::vector<Session*> g_sessions;
static std::vector<std::wstring> g_workspaces = { L"workspace 1" };  // session "folders" (groups)
static int g_activeWs = 0;           // workspace new sessions are created into
static int g_pane[2] = { 0, -1 };   // session index per pane; pane[1] = -1 → no split
static int g_focus = 0;             // focused pane (0/1)
static int g_seq = 1;
static const wchar_t* kAppId = L"agwinterm-lite";

// ---- selection (buffer-absolute rows; the same viewport composition as paint) ----
struct Sel {
    int pane = -1;                  // which pane the selection lives in (-1 = none)
    bool active = false;            // a drag is in progress
    int aRow = 0, aCol = 0;         // anchor (buffer-absolute row, column)
    int bRow = 0, bCol = 0;         // current end
    bool has() const { return pane >= 0 && (aRow != bRow || aCol != bCol); }
    void norm(int& r0, int& c0, int& r1, int& c1) const {
        if (aRow < bRow || (aRow == bRow && aCol <= bCol)) { r0 = aRow; c0 = aCol; r1 = bRow; c1 = bCol; }
        else { r0 = bRow; c0 = bCol; r1 = aRow; c1 = aCol; }
    }
};
static Sel g_sel;

// ---- command palette ----
static bool g_palette = false;
struct PaletteItem { const wchar_t* label; int id; };
static const PaletteItem kPalette[] = {
    { L"New session          Ctrl+T", 1 },
    { L"Close session        Ctrl+W", 2 },
    { L"Split / unsplit      Ctrl+Shift+D", 3 },
    { L"Next session         Ctrl+Tab", 4 },
    { L"Copy selection       Ctrl+C", 5 },
    { L"Paste                Ctrl+V", 6 },
};
static int g_paletteSel = 0;

static void fatal(const wchar_t* msg) {
    MessageBoxW(nullptr, msg, L"agwinterm-lite", MB_ICONERROR);
    ExitProcess(1);
}

// ---- control pipe: protobuf frames (4-byte LE length prefix) ----
static CRITICAL_SECTION g_reqLock;   // the control pipe is shared by the UI thread and the ctl server thread
static bool request(const agwinterm_ptyhost_Request& req, agwinterm_ptyhost_Reply* reply) {
    EnterCriticalSection(&g_reqLock);
    struct Unlock { ~Unlock() { LeaveCriticalSection(&g_reqLock); } } unlock;
    uint8_t buf[4096];
    pb_ostream_t os = pb_ostream_from_buffer(buf + 4, sizeof buf - 4);
    if (!pb_encode(&os, agwinterm_ptyhost_Request_fields, &req)) return false;
    uint32_t len = (uint32_t)os.bytes_written;
    memcpy(buf, &len, 4);
    DWORD n = 0;
    if (!WriteFile(g_control, buf, len + 4, &n, nullptr)) return false;

    uint32_t rlen = 0;
    DWORD got = 0, need = 4;
    while (need && ReadFile(g_control, (uint8_t*)&rlen + (4 - need), need, &got, nullptr) && got) need -= got;
    if (need || rlen > 1 << 20) return false;
    std::vector<uint8_t> payload(rlen);
    need = rlen;
    while (need && ReadFile(g_control, payload.data() + (rlen - need), need, &got, nullptr) && got) need -= got;
    if (need) return false;
    pb_istream_t is = pb_istream_from_buffer(payload.data(), rlen);
    *reply = agwinterm_ptyhost_Reply_init_default;
    return pb_decode(&is, agwinterm_ptyhost_Reply_fields, reply) && reply->ok;
}

static HANDLE openPipe(const std::wstring& name, int timeoutMs, bool overlapped) {
    // DATA pipes must be overlapped: a non-overlapped duplex pipe SERIALIZES the handle
    // (pending reader ReadFile blocks the UI thread's keystroke write — both the Rust
    // host and this client hit that identical deadlock). Control stays sync.
    std::wstring full = L"\\\\.\\pipe\\" + name;
    DWORD flags = overlapped ? FILE_FLAG_OVERLAPPED : 0;
    for (int waited = 0; waited <= timeoutMs; waited += 100) {
        HANDLE h = CreateFileW(full.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, flags, nullptr);
        if (h != INVALID_HANDLE_VALUE) return h;
        Sleep(100);
    }
    return INVALID_HANDLE_VALUE;
}

static DWORD ovIo(HANDLE h, bool write, const void* wbuf, void* rbuf, DWORD len) {
    OVERLAPPED ov{};
    ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    BOOL issued = write ? WriteFile(h, wbuf, len, nullptr, &ov) : ReadFile(h, rbuf, len, nullptr, &ov);
    DWORD n = 0;
    if (issued || GetLastError() == ERROR_IO_PENDING) {
        if (!GetOverlappedResult(h, &ov, &n, TRUE)) n = 0;
    }
    CloseHandle(ov.hEvent);
    return n;
}

static std::wstring exeDir() {
    wchar_t buf[MAX_PATH];
    GetModuleFileNameW(nullptr, buf, MAX_PATH);
    std::wstring s = buf;
    return s.substr(0, s.find_last_of(L'\\'));
}

static void loadCore() {
    HMODULE m = LoadLibraryW((exeDir() + L"\\agwinterm_core.dll").c_str());
    if (!m) fatal(L"agwinterm_core.dll not found next to the exe");
    core_abi = (decltype(core_abi))GetProcAddress(m, "agwcore_abi_version");
    emu_new = (decltype(emu_new))GetProcAddress(m, "agwcore_emu_new");
    emu_free = (decltype(emu_free))GetProcAddress(m, "agwcore_emu_free");
    emu_feed = (decltype(emu_feed))GetProcAddress(m, "agwcore_emu_feed");
    emu_resize = (decltype(emu_resize))GetProcAddress(m, "agwcore_emu_resize");
    emu_info = (decltype(emu_info))GetProcAddress(m, "agwcore_emu_info");
    emu_copy_grid = (decltype(emu_copy_grid))GetProcAddress(m, "agwcore_emu_copy_grid");
    emu_copy_history_row = (decltype(emu_copy_history_row))GetProcAddress(m, "agwcore_emu_copy_history_row");
    emu_marks = (decltype(emu_marks))GetProcAddress(m, "agwcore_emu_marks");
    if (!core_abi || !emu_new || !emu_feed || !emu_info || !emu_copy_grid || !emu_resize || !emu_free || !emu_copy_history_row || !emu_marks)
        fatal(L"agwinterm_core.dll: exports missing");
    if (core_abi() != kRequiredAbi) fatal(L"agwinterm_core.dll: ABI mismatch (need v15)");
}

static void connectControl() {
    std::wstring control = std::wstring(kAppId) + L"-ptyhost";
    g_control = openPipe(control, 0, false);
    if (g_control == INVALID_HANDLE_VALUE) {
        std::wstring cmd = L"\"" + exeDir() + L"\\agwinterm-ptyhost.exe\" --pipe " + kAppId;
        STARTUPINFOW si{ sizeof(si) };
        PROCESS_INFORMATION pi{};
        std::vector<wchar_t> buf(cmd.begin(), cmd.end());
        buf.push_back(0);
        if (!CreateProcessW(nullptr, buf.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi))
            fatal(L"could not start agwinterm-ptyhost.exe");
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        g_control = openPipe(control, 5000, false);
        if (g_control == INVALID_HANDLE_VALUE) fatal(L"pty-host control pipe never appeared");
    }
    agwinterm_ptyhost_Request req = agwinterm_ptyhost_Request_init_default;
    agwinterm_ptyhost_Reply rep = agwinterm_ptyhost_Reply_init_default;
    req.which_cmd = agwinterm_ptyhost_Request_hello_tag;
    req.cmd.hello.protocol = kProtocolVersion;
    if (!request(req, &rep) || rep.which_body != agwinterm_ptyhost_Reply_hello_tag)
        fatal(L"pty-host hello failed (protocol mismatch?)");
}

// ---- pane geometry ----
static void paneRect(int pane, RECT client, RECT* out) {
    int contentX = sidebarSpan();               // right of the sidebar + splitter (0 if hidden)
    int top = toolbarTop();                     // below the toolbar (0 if hidden)
    int bottom = client.bottom - (g_showStatus ? g_statusH : 0);   // above the status bar
    int contentW = client.right - contentX;
    if (g_pane[1] < 0) { *out = { contentX, top, client.right, bottom }; return; }
    int half = contentW / 2;
    if (pane == 0) *out = { contentX, top, contentX + half - 1, bottom };
    else *out = { contentX + half + 1, top, client.right, bottom };
}

static void paneGridSize(int pane, int* cols, int* rows) {
    RECT rc;
    GetClientRect(g_hwnd, &rc);
    RECT pr;
    paneRect(pane, rc, &pr);
    *cols = max(2L, (pr.right - pr.left) / g_cw);
    *rows = max(2L, (pr.bottom - pr.top) / g_ch);
}

static Session* focusedSession() {
    if (g_focusOverride) return g_focusOverride;   // a popup terminal owns input while it's focused
    int idx = g_pane[g_focus];
    return (idx >= 0 && idx < (int)g_sessions.size()) ? g_sessions[idx] : nullptr;
}
// The window that displays a session (a popup terminal, else the main window) — repaint target.
static HWND windowForSession(Session* s) {
    if (s == g_quickSession && g_quickHwnd) return g_quickHwnd;
    if (s == g_scratchSession && g_scratchHwnd) return g_scratchHwnd;
    if (s == g_overlaySession && g_overlayHwnd) return g_overlayHwnd;
    return g_hwnd;
}

static void hostResize(Session* s, int cols, int rows) {
    agwinterm_ptyhost_Request req = agwinterm_ptyhost_Request_init_default;
    agwinterm_ptyhost_Reply rep = agwinterm_ptyhost_Reply_init_default;
    req.which_cmd = agwinterm_ptyhost_Request_resize_tag;
    strcpy_s(req.cmd.resize.id, s->id.c_str());
    req.cmd.resize.cols = (uint32_t)cols;
    req.cmd.resize.rows = (uint32_t)rows;
    request(req, &rep);
    EnterCriticalSection(&g_lock);
    emu_resize(s->emu, cols, rows);
    LeaveCriticalSection(&g_lock);
}

static void syncPaneSizes() {
    for (int p = 0; p < 2; p++) {
        int idx = g_pane[p];
        if (idx < 0 || idx >= (int)g_sessions.size()) continue;
        int cols, rows;
        paneGridSize(p, &cols, &rows);
        hostResize(g_sessions[idx], cols, rows);
    }
}

// ---- FTCS prompt wrap: chain the user's prompt so it emits OSC 133 D;<exit> + A each turn,
// giving lite the prompt pips out of the box while preserving oh-my-posh/starship. Passed as
// -EncodedCommand (base64 of UTF-16LE) so there is zero quoting to mangle. ----
static std::string base64(const std::wstring& s) {
    static const char* T = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    const uint8_t* p = (const uint8_t*)s.data();
    size_t n = s.size() * sizeof(wchar_t);
    std::string out;
    for (size_t i = 0; i < n; i += 3) {
        uint32_t v = p[i] << 16 | (i + 1 < n ? p[i + 1] << 8 : 0) | (i + 2 < n ? p[i + 2] : 0);
        out += T[(v >> 18) & 63];
        out += T[(v >> 12) & 63];
        out += (i + 1 < n) ? T[(v >> 6) & 63] : '=';
        out += (i + 2 < n) ? T[v & 63] : '=';
    }
    return out;
}

static const wchar_t* kPromptWrap =
    L"if(-not $global:__agwLiteWrap){$global:__agwLiteWrap=$true;$global:__agwLiteP=$function:prompt;"
    L"function global:prompt{$ec=if($?){0}else{1};$e=[char]27;$b=[char]7;"
    L"[Console]::Write(\"$e]133;D;$ec$b$e]133;A$b\");"
    L"if($global:__agwLiteP){& $global:__agwLiteP}else{\"PS $($executionContext.SessionState.Path.CurrentLocation)> \"}}}";

// ---- session lifecycle ----
// Completed FTCS commands (OSC 133 with an end boundary) in the session's buffer. Call under g_lock.
static int completedMarks(Session* s) {
    FfiEmuInfo info{};
    if (!s->emu || !emu_info(s->emu, &info) || info.markCount == 0) return 0;
    std::vector<FfiMark> mk(info.markCount);
    uint32_t nm = emu_marks(s->emu, mk.data(), info.markCount);
    int done = 0;
    for (uint32_t i = 0; i < nm; i++) if (mk[i].endLine >= 0) done++;
    return done;
}

static DWORD WINAPI readerThread(void* param) {
    Session* s = (Session*)param;
    std::vector<uint8_t> buf(64 * 1024);
    DWORD n;
    while ((n = ovIo(s->data, false, nullptr, buf.data(), (DWORD)buf.size())) > 0) {
        bool bump = false;
        EnterCriticalSection(&g_lock);
        emu_feed(s->emu, buf.data(), n);
        // Unread: commands that FINISHED while the session wasn't on screen (noise-free — prompt
        // repaints don't move the completed count). Visible panes track instead of accumulating.
        if (!s->hidden) {
            bool visible = false;
            for (int p2 = 0; p2 < 2; p2++)
                if (g_pane[p2] >= 0 && g_pane[p2] < (int)g_sessions.size() && g_sessions[g_pane[p2]] == s) visible = true;
            int done = completedMarks(s);
            if (visible) { s->seenDone = done; if (s->unread) { s->unread = 0; bump = true; } }
            else { int u = done > s->seenDone ? done - s->seenDone : 0; if (u != s->unread) { s->unread = u; bump = true; } }
        }
        LeaveCriticalSection(&g_lock);
        InvalidateRect(windowForSession(s), nullptr, FALSE);
        if (bump) PostMessageW(g_hwnd, WM_APP_REFRESHTREE, 0, 0);   // repaint the badge
    }
    s->exited = true;   // EOF: child exited, host shut down, or we were superseded
    InvalidateRect(windowForSession(s), nullptr, FALSE);
    PostMessageW(g_hwnd, WM_APP_REFRESHTREE, 0, 0);   // reflect the exited marker in the tree
    return 0;
}

// A launchable shell "voice" for the New Session dialog.
struct Profile { std::wstring name; std::string app; std::vector<std::string> args; };

static bool isPwshApp(const char* app) {
    if (!app) return true;
    std::string a(app);
    for (char& c : a) c = (char)tolower((unsigned char)c);
    return a.find("powershell") != std::string::npos || a.find("pwsh") != std::string::npos;
}

// Detected shells on this machine (the "voices"). PowerShell + cmd are always present.
static std::vector<Profile> detectProfiles() {
    auto have = [](const char* p) { return GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES; };
    std::vector<Profile> v;
    v.push_back({ L"Windows PowerShell", "powershell.exe", {} });
    if (have("C:\\Program Files\\PowerShell\\7\\pwsh.exe"))
        v.push_back({ L"PowerShell 7", "C:\\Program Files\\PowerShell\\7\\pwsh.exe", {} });
    v.push_back({ L"Command Prompt", "cmd.exe", {} });
    if (have("C:\\Program Files\\Git\\bin\\bash.exe"))
        v.push_back({ L"Git Bash", "C:\\Program Files\\Git\\bin\\bash.exe", { "-i", "-l" } });
    char sys[MAX_PATH]; GetSystemDirectoryA(sys, MAX_PATH);
    std::string wsl = std::string(sys) + "\\wsl.exe";
    if (have(wsl.c_str())) v.push_back({ L"WSL", wsl, {} });
    return v;
}

// cols/rows + an optional profile (app/args) and cwd. Default (no app) = PowerShell with the prompt wrap.
static Session* newSession(int cols, int rows, const char* app = nullptr,
                           const std::vector<std::string>* pargs = nullptr, const char* cwd = nullptr) {
    char idbuf[64];
    wsprintfA(idbuf, "lite-%d", g_seq++);
    agwinterm_ptyhost_Request req = agwinterm_ptyhost_Request_init_default;
    agwinterm_ptyhost_Reply rep = agwinterm_ptyhost_Reply_init_default;
    req.which_cmd = agwinterm_ptyhost_Request_create_tag;
    strcpy_s(req.cmd.create.id, idbuf);
    req.cmd.create.cols = (uint32_t)cols;
    req.cmd.create.rows = (uint32_t)rows;
    const char* useApp = app ? app : "powershell.exe";
    strcpy_s(req.cmd.create.app, useApp);
    if (cwd && *cwd) strcpy_s(req.cmd.create.cwd, cwd);
    std::string enc;
    if (pargs && !pargs->empty()) {                     // explicit profile args -> run app + args as-is
        int n = (int)pargs->size(); if (n > 4) n = 4;
        req.cmd.create.args_count = n;
        for (int i = 0; i < n; i++) strcpy_s(req.cmd.create.args[i], (*pargs)[i].c_str());
    } else if (isPwshApp(useApp)) {                     // PowerShell: keep the interactive prompt wrap
        // -NoExit keeps the shell interactive after the wrap runs; -EncodedCommand runs AFTER the
        // profile so it chains (not replaces) the user's prompt.
        enc = base64(kPromptWrap);
        req.cmd.create.args_count = 4;
        strcpy_s(req.cmd.create.args[0], "-NoLogo");
        strcpy_s(req.cmd.create.args[1], "-NoExit");
        strcpy_s(req.cmd.create.args[2], "-EncodedCommand");
        strcpy_s(req.cmd.create.args[3], enc.c_str());
    } else {
        req.cmd.create.args_count = 0;                  // cmd / bash / wsl: launch bare
    }
    // AGWINTERM_* identity env, so the Claude skill / hooks / agwintermctl inside the
    // session auto-target LITE's control pipe (all non-UI features are protocol).
    auto setEnv = [&](int i, const char* k, const char* v) {
        strcpy_s(req.cmd.create.env[i].key, k);
        strcpy_s(req.cmd.create.env[i].value, v);
    };
    req.cmd.create.env_count = 6;
    setEnv(0, "AGWINTERM", "1");
    setEnv(1, "AGWINTERM_ENABLED", "1");
    setEnv(2, "AGWINTERM_PIPE", "agwinterm-lite");
    setEnv(3, "AGWINTERM_SESSION_ID", idbuf);
    setEnv(4, "AGWINTERM_PANE_ID", idbuf);
    setEnv(5, "TERM_PROGRAM", "agwinterm-lite");
    if (!request(req, &rep)) return nullptr;

    req = agwinterm_ptyhost_Request_init_default;
    req.which_cmd = agwinterm_ptyhost_Request_attach_tag;
    strcpy_s(req.cmd.attach.id, idbuf);
    if (!request(req, &rep) || rep.which_body != agwinterm_ptyhost_Reply_attach_tag) return nullptr;

    Session* s = new Session();
    s->id = idbuf;
    s->app = app ? app : "";        // remember the launch spec for session restore
    if (pargs) s->args = *pargs;
    s->cwd = cwd ? cwd : "";
    s->ws = (g_activeWs >= 0 && g_activeWs < (int)g_workspaces.size()) ? g_activeWs : 0;   // into the active workspace
    s->emu = emu_new(cols, rows);
    s->data = openPipe(std::wstring(rep.body.attach.pipe, rep.body.attach.pipe + strlen(rep.body.attach.pipe)), 5000, true);
    if (s->data == INVALID_HANDLE_VALUE) { emu_free(s->emu); delete s; return nullptr; }
    s->reader = CreateThread(nullptr, 0, readerThread, s, 0, nullptr);
    EnterCriticalSection(&g_lock);
    g_sessions.push_back(s);
    LeaveCriticalSection(&g_lock);
    PostMessageW(g_hwnd, WM_APP_REFRESHTREE, 0, 0);   // add the session to the tree (UI thread)
    return s;
}

static void killSession(Session* s) {
    agwinterm_ptyhost_Request req = agwinterm_ptyhost_Request_init_default;
    agwinterm_ptyhost_Reply rep = agwinterm_ptyhost_Reply_init_default;
    req.which_cmd = agwinterm_ptyhost_Request_kill_tag;
    strcpy_s(req.cmd.kill.id, s->id.c_str());
    request(req, &rep);
}

struct ClosedSpec { std::wstring name; int ws; std::string app, cwd; std::vector<std::string> args; };
static std::vector<ClosedSpec> g_closedStack;   // recently closed sessions, for Reopen Closed Session

static void closeSessionAt(int idx) {
    if (idx < 0 || idx >= (int)g_sessions.size()) return;
    Session* cs = g_sessions[idx];
    if (!cs->hidden) {   // remember the launch spec so it can be reopened (skip transient split/popup shells)
        if (g_closedStack.size() >= 16) g_closedStack.erase(g_closedStack.begin());
        g_closedStack.push_back({ cs->name, cs->ws, cs->app, cs->cwd, cs->args });
    }
    killSession(g_sessions[idx]);
    EnterCriticalSection(&g_lock);
    g_sessions.erase(g_sessions.begin() + idx);
    for (int p = 0; p < 2; p++) {
        if (g_pane[p] == idx) g_pane[p] = g_sessions.empty() ? -1 : max(0, idx - 1);
        else if (g_pane[p] > idx) g_pane[p]--;
    }
    if (g_sessions.empty()) g_pane[1] = -1;   // unsplit when the last pane dies
    LeaveCriticalSection(&g_lock);
    if (g_sessions.empty()) { DestroyWindow(g_hwnd); return; }
    syncPaneSizes();
    InvalidateRect(g_hwnd, nullptr, FALSE);
    PostMessageW(g_hwnd, WM_APP_REFRESHTREE, 0, 0);   // drop the session from the tree
}

// Reopen the most recently closed session, relaunched with its remembered profile + cwd.
static void reopenClosed() {
    if (g_closedStack.empty()) return;
    ClosedSpec sp = g_closedStack.back(); g_closedStack.pop_back();
    if (sp.ws >= 0 && sp.ws < (int)g_workspaces.size()) g_activeWs = sp.ws;
    int c, r; paneGridSize(g_focus, &c, &r);
    Session* s = newSession(c, r, sp.app.empty() ? nullptr : sp.app.c_str(),
                            sp.args.empty() ? nullptr : &sp.args, sp.cwd.empty() ? nullptr : sp.cwd.c_str());
    if (s) { s->name = sp.name; g_pane[g_focus] = (int)g_sessions.size() - 1; syncPaneSizes(); InvalidateRect(g_hwnd, nullptr, FALSE); }
}
static void toggleSplit();   // fwd
static void closeFocused() {
    if (g_focus == 1 && g_pane[1] >= 0) { toggleSplit(); return; }   // closing the split pane = unsplit
    closeSessionAt(g_pane[0]);
}

// Toggle the 2-pane split. Splitting spawns an INDEPENDENT new shell for the second pane (separate
// output) — but marked hidden, so it is NOT a sidebar/tree session (agterm: a split isn't a new
// session). Unsplitting removes that shell.
static void toggleSplit() {
    if (g_pane[1] < 0) {
        int c, r; paneGridSize(g_focus, &c, &r);   // approximate; syncPaneSizes resizes both after
        Session* s = newSession(c, r);
        if (s) { s->hidden = true; g_pane[1] = (int)g_sessions.size() - 1; g_focus = 1; }
    } else {
        int sp = g_pane[1];
        g_pane[1] = -1; g_focus = 0;
        if (sp >= 0 && sp < (int)g_sessions.size()) {   // remove the hidden split shell
            killSession(g_sessions[sp]);
            EnterCriticalSection(&g_lock);
            g_sessions.erase(g_sessions.begin() + sp);
            if (g_pane[0] > sp) g_pane[0]--;            // fix the surviving pane's index
            LeaveCriticalSection(&g_lock);
        }
    }
    syncPaneSizes();
    PostMessageW(g_hwnd, WM_APP_REFRESHTREE, 0, 0);
    InvalidateRect(g_hwnd, nullptr, FALSE);
}

// Cycle the MAIN pane through visible sessions (skips hidden split shells). dir = +1 next, -1 prev.
static void cycleSession(int dir) {
    int n = (int)g_sessions.size();
    if (n == 0) return;
    int i = g_pane[0];
    for (int k = 0; k < n; k++) {
        i = (i + dir + n) % n;
        if (i >= 0 && i < n && !g_sessions[i]->hidden) { g_pane[0] = i; g_focus = 0; break; }
    }
    syncPaneSizes();
    PostMessageW(g_hwnd, WM_APP_REFRESHTREE, 0, 0);
    InvalidateRect(g_hwnd, nullptr, FALSE);
}

// Build one GDI font for a catalog spec + weight/slant. The kind selects charset/precision/quality so
// raster (.fon) and bitmap-embedded (TTF) faces render crisp while scalable faces stay antialiased.
static HFONT createFontSpec(const FontEntry& e, const FontSize& s, bool bold, bool italic) {
    int charset = (e.kind == 1) ? OEM_CHARSET : DEFAULT_CHARSET;
    int precis  = (e.kind == 1) ? OUT_RASTER_PRECIS : (e.kind == 2 ? OUT_DEFAULT_PRECIS : OUT_TT_PRECIS);
    int quality = (e.kind == 0) ? CLEARTYPE_QUALITY : NONANTIALIASED_QUALITY;
    return CreateFontW(s.h, s.w, 0, 0, bold ? FW_BOLD : FW_NORMAL, italic, FALSE, FALSE,
                       charset, precis, CLIP_DEFAULT_PRECIS, quality, FIXED_PITCH | FF_MODERN, e.face);
}
// (Re)create the four terminal fonts from the current catalog selection, recompute the character cell
// (g_cw/g_ch) from the regular font's metrics, and relayout every session.
static void applyFont() {
    if (g_catalog.empty()) return;
    if (g_faceIdx < 0 || g_faceIdx >= (int)g_catalog.size()) g_faceIdx = 0;
    FontEntry& e = g_catalog[g_faceIdx];
    if (g_sizeIdx < 0 || g_sizeIdx >= (int)e.sizes.size()) g_sizeIdx = 0;
    FontSize& s = e.sizes[g_sizeIdx];
    HFONT nf[4];
    for (int i = 0; i < 4; i++) nf[i] = createFontSpec(e, s, i & 1, (i & 2) != 0);
    HDC dc = GetDC(nullptr);
    HGDIOBJ old = SelectObject(dc, nf[0]);
    TEXTMETRICW tm; GetTextMetricsW(dc, &tm);
    g_cw = tm.tmAveCharWidth; g_ch = tm.tmHeight;
    SelectObject(dc, old);
    ReleaseDC(nullptr, dc);
    for (int i = 0; i < 4; i++) { if (g_fonts[i]) DeleteObject(g_fonts[i]); g_fonts[i] = nf[i]; }
    if (g_hwnd) {                       // live switch: resize every session to the new cell + repaint
        if (!g_sessions.empty()) syncPaneSizes();
        InvalidateRect(g_hwnd, nullptr, TRUE);
    }
}
// Populate the font catalog: bundled Meslo + the classic cmd.exe faces (Terminal at its DOS sizes,
// Fixedsys, Consolas, Lucida Console) + the bundled bitmap fonts that actually loaded.
static void buildFontCatalog() {
    g_catalog.clear();
    g_catalog.push_back({ L"Nerd Font", g_ttFace.c_str(), 0, true,
        { {L"14",-14,0},{L"16",-16,0},{L"18",-18,0},{L"20",-20,0},{L"24",-24,0} } });
    g_catalog.push_back({ L"Terminal", L"Terminal", 1, true,
        { {L"4×6",6,4},{L"5×8",8,5},{L"6×8",8,6},{L"7×12",12,7},{L"8×8",8,8},
          {L"8×12",12,8},{L"8×16",16,8},{L"10×18",18,10},{L"12×16",16,12},{L"16×12",12,16} } });
    g_catalog.push_back({ L"Fixedsys", L"Fixedsys", 1, true, { {L"8×15",15,0} } });
    g_catalog.push_back({ L"Consolas", L"Consolas", 0, true,
        { {L"14",-14,0},{L"16",-16,0},{L"18",-18,0},{L"20",-20,0},{L"24",-24,0} } });
    g_catalog.push_back({ L"Lucida Console", L"Lucida Console", 0, true,
        { {L"14",-14,0},{L"16",-16,0},{L"18",-18,0},{L"20",-20,0},{L"24",-24,0} } });
    if (g_haveCozette)
        g_catalog.push_back({ L"Cozette", L"CozetteVector", 0, true,
            { {L"13",-13,0},{L"16",-16,0},{L"20",-20,0},{L"26",-26,0} } });
    if (g_haveTamzen)
        g_catalog.push_back({ L"Tamzen", L"TamzenForPowerline", 2, true,
            { {L"7×14",14,0},{L"8×16",16,0},{L"10×20",20,0} } });
}
static int catFace(const wchar_t* label) {
    for (int i = 0; i < (int)g_catalog.size(); i++) if (wcscmp(g_catalog[i].label, label) == 0) return i;
    return -1;
}
static void setDefaultFont() {   // Terminal 8x12 (Boris's pick) or the first catalog entry
    int t = catFace(L"Terminal");
    g_faceIdx = t >= 0 ? t : 0; g_sizeIdx = t >= 0 ? 5 : 0;   // 5 = "8x12" in the Terminal size list
}
// Persist the selection by face + size (survives catalog reordering across versions).
static void saveFontSel() {
    if (g_faceIdx < 0 || g_faceIdx >= (int)g_catalog.size()) return;
    const FontEntry& e = g_catalog[g_faceIdx]; const FontSize& s = e.sizes[g_sizeIdx];
    RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"FontFace", REG_SZ, e.face, (DWORD)((wcslen(e.face) + 1) * sizeof(wchar_t)));
    DWORD h = (DWORD)(int)s.h, w = (DWORD)(int)s.w;
    RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"FontH", REG_DWORD, &h, sizeof(h));
    RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"FontW", REG_DWORD, &w, sizeof(w));
}
static void loadFontSel() {
    wchar_t face[64] = L""; DWORD sz = sizeof(face);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"FontFace", RRF_RT_REG_SZ, nullptr, face, &sz) != ERROR_SUCCESS) { setDefaultFont(); return; }
    DWORD h = 0, w = 0, s = sizeof(DWORD);
    RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"FontH", RRF_RT_REG_DWORD, nullptr, &h, &s); s = sizeof(DWORD);
    RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"FontW", RRF_RT_REG_DWORD, nullptr, &w, &s);
    for (int fi = 0; fi < (int)g_catalog.size(); fi++) {
        if (wcscmp(g_catalog[fi].face, face) != 0) continue;
        for (int si = 0; si < (int)g_catalog[fi].sizes.size(); si++)
            if ((DWORD)(int)g_catalog[fi].sizes[si].h == h && (DWORD)(int)g_catalog[fi].sizes[si].w == w) { g_faceIdx = fi; g_sizeIdx = si; return; }
        g_faceIdx = fi; g_sizeIdx = 0; return;   // face matched, size didn't — keep the face
    }
    setDefaultFont();
}
static void loadColors() {   // Properties->Colors overrides (default fg/bg + on/off), persisted like the font
    DWORD v, sz;
    sz = sizeof(v); if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"CustomColors", RRF_RT_REG_DWORD, nullptr, &v, &sz) == ERROR_SUCCESS) g_customColors = v != 0;
    sz = sizeof(v); if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"DefFg", RRF_RT_REG_DWORD, nullptr, &v, &sz) == ERROR_SUCCESS) g_defFg = v & 0xFFFFFF;
    sz = sizeof(v); if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"DefBg", RRF_RT_REG_DWORD, nullptr, &v, &sz) == ERROR_SUCCESS) g_defBg = v & 0xFFFFFF;
    sz = sizeof(v); if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"DosPalette", RRF_RT_REG_DWORD, nullptr, &v, &sz) == ERROR_SUCCESS) g_dosPalette = v != 0;
    sz = sizeof(v); if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"Theme", RRF_RT_REG_DWORD, nullptr, &v, &sz) == ERROR_SUCCESS && v <= TH_CLASSIC) g_themeMode = (int)v;
    sz = sizeof(v); if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"SidebarW", RRF_RT_REG_DWORD, nullptr, &v, &sz) == ERROR_SUCCESS && v >= 90 && v <= 900) g_sidebarW = v;
    sz = sizeof(v); if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"ShowSidebar", RRF_RT_REG_DWORD, nullptr, &v, &sz) == ERROR_SUCCESS) g_showSidebar = v != 0;
    sz = sizeof(v); if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"ShowToolbar", RRF_RT_REG_DWORD, nullptr, &v, &sz) == ERROR_SUCCESS) g_showToolbar = v != 0;
    sz = sizeof(v); if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"ShowStatus", RRF_RT_REG_DWORD, nullptr, &v, &sz) == ERROR_SUCCESS) g_showStatus = v != 0;
    sz = sizeof(v); if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"FlagView", RRF_RT_REG_DWORD, nullptr, &v, &sz) == ERROR_SUCCESS) g_flagView = v != 0;
}
static void loadKeys() {   // configurable key bindings; absent = unbound (0)
    for (int a = 0; a < KB_COUNT; a++) {
        DWORD v = 0, sz = sizeof(v);
        if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", kKbInfo[a].reg, RRF_RT_REG_DWORD, nullptr, &v, &sz) == ERROR_SUCCESS) g_keys[a] = (WORD)v;
    }
}
static void saveKeys() {
    for (int a = 0; a < KB_COUNT; a++) {
        DWORD v = g_keys[a];
        RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", kKbInfo[a].reg, REG_DWORD, &v, sizeof(v));
    }
}
static void saveColors() {
    DWORD v;
    v = g_customColors ? 1 : 0; RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"CustomColors", REG_DWORD, &v, sizeof(v));
    v = g_defFg; RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"DefFg", REG_DWORD, &v, sizeof(v));
    v = g_defBg; RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"DefBg", REG_DWORD, &v, sizeof(v));
    v = g_dosPalette ? 1 : 0; RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"DosPalette", REG_DWORD, &v, sizeof(v));
    v = (DWORD)g_themeMode;   RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"Theme", REG_DWORD, &v, sizeof(v));
    v = g_sidebarW; RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"SidebarW", REG_DWORD, &v, sizeof(v));
    v = g_showSidebar ? 1 : 0; RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"ShowSidebar", REG_DWORD, &v, sizeof(v));
    v = g_showToolbar ? 1 : 0; RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"ShowToolbar", REG_DWORD, &v, sizeof(v));
    v = g_showStatus ? 1 : 0; RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"ShowStatus", REG_DWORD, &v, sizeof(v));
    v = g_flagView ? 1 : 0;   RegSetKeyValueW(HKEY_CURRENT_USER, L"Software\\agwinterm-lite", L"FlagView", REG_DWORD, &v, sizeof(v));
}
// Window geometry persistence. loadWindowRect resolves the saved rect (clamped onto a visible monitor
// so an unplugged screen / resolution change can't strand the window off-screen) and is applied at
// CreateWindow time so the window appears there directly — no create-then-move flash.
static bool loadWindowRect(RECT* out, bool* maxed) {
    const wchar_t* k = L"Software\\agwinterm-lite";
    DWORD x, y, w, h, mx = 0, sz;
    sz = sizeof(DWORD); if (RegGetValueW(HKEY_CURRENT_USER, k, L"WinW", RRF_RT_REG_DWORD, nullptr, &w, &sz) != ERROR_SUCCESS) return false;
    sz = sizeof(DWORD); if (RegGetValueW(HKEY_CURRENT_USER, k, L"WinH", RRF_RT_REG_DWORD, nullptr, &h, &sz) != ERROR_SUCCESS) return false;
    sz = sizeof(DWORD); if (RegGetValueW(HKEY_CURRENT_USER, k, L"WinX", RRF_RT_REG_DWORD, nullptr, &x, &sz) != ERROR_SUCCESS) return false;
    sz = sizeof(DWORD); if (RegGetValueW(HKEY_CURRENT_USER, k, L"WinY", RRF_RT_REG_DWORD, nullptr, &y, &sz) != ERROR_SUCCESS) return false;
    sz = sizeof(DWORD); RegGetValueW(HKEY_CURRENT_USER, k, L"WinMax", RRF_RT_REG_DWORD, nullptr, &mx, &sz);
    if ((int)w < 200 || (int)h < 120) return false;   // sanity guard against a corrupt/degenerate rect
    RECT rc{ (LONG)(int)x, (LONG)(int)y, (LONG)((int)x + (int)w), (LONG)((int)y + (int)h) };
    if (!MonitorFromRect(&rc, MONITOR_DEFAULTTONULL)) {   // fully off every screen -> centre on the nearest
        MONITORINFO mi{ sizeof(mi) }; GetMonitorInfoW(MonitorFromRect(&rc, MONITOR_DEFAULTTONEAREST), &mi);
        int cw = mi.rcWork.right - mi.rcWork.left, ch = mi.rcWork.bottom - mi.rcWork.top;
        int ww = min((int)w, cw), hh = min((int)h, ch);
        rc = { mi.rcWork.left + (cw - ww) / 2, mi.rcWork.top + (ch - hh) / 2, 0, 0 };
        rc.right = rc.left + ww; rc.bottom = rc.top + hh;
    }
    *out = rc; *maxed = mx != 0; return true;
}
static void saveWindowRect() {
    WINDOWPLACEMENT wp{ sizeof(wp) };
    if (!g_hwnd || !GetWindowPlacement(g_hwnd, &wp)) return;
    RECT rc = wp.rcNormalPosition;   // the restore rect (correct even while maximized/minimized)
    const wchar_t* k = L"Software\\agwinterm-lite";
    DWORD x = (DWORD)rc.left, y = (DWORD)rc.top, w = (DWORD)(rc.right - rc.left), h = (DWORD)(rc.bottom - rc.top);
    DWORD mx = (wp.showCmd == SW_SHOWMAXIMIZED || (wp.flags & WPF_RESTORETOMAXIMIZED)) ? 1 : 0;
    RegSetKeyValueW(HKEY_CURRENT_USER, k, L"WinX", REG_DWORD, &x, sizeof(x));
    RegSetKeyValueW(HKEY_CURRENT_USER, k, L"WinY", REG_DWORD, &y, sizeof(y));
    RegSetKeyValueW(HKEY_CURRENT_USER, k, L"WinW", REG_DWORD, &w, sizeof(w));
    RegSetKeyValueW(HKEY_CURRENT_USER, k, L"WinH", REG_DWORD, &h, sizeof(h));
    RegSetKeyValueW(HKEY_CURRENT_USER, k, L"WinMax", REG_DWORD, &mx, sizeof(mx));
}

// ---- session/workspace restore ----
static std::string narrow(const std::wstring& w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, 0); WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr); return s;
}
static std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w(n, 0); MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n); return w;
}
// State file: %LOCALAPPDATA%\agwinterm-lite\sessions.tsv (per-user, created on demand).
static std::wstring stateFilePath() {
    wchar_t base[MAX_PATH];
    if (GetEnvironmentVariableW(L"LOCALAPPDATA", base, MAX_PATH) == 0) return {};
    std::wstring dir = std::wstring(base) + L"\\agwinterm-lite";
    CreateDirectoryW(dir.c_str(), nullptr);
    return dir + L"\\sessions.tsv";
}
// Snapshot the workspaces + (visible) sessions so next launch can rebuild them. Tab-separated; a
// session line is: S <ws> <name> <app> <cwd> <arg0> <arg1>...  Split-shells (hidden) aren't persisted.
static void saveSessionState() {
    std::wstring path = stateFilePath();
    if (path.empty()) return;
    std::string out = "V1\n";
    for (const auto& w : g_workspaces) out += "W\t" + narrow(w) + "\n";
    EnterCriticalSection(&g_lock);
    std::string flagLine;   // "F\t<i>..." = indices (in S-line order) of flagged sessions; old builds skip it
    int saved = 0;
    for (const Session* s : g_sessions) {
        if (s->hidden) continue;
        out += "S\t" + std::to_string(s->ws) + "\t" + narrow(s->name) + "\t" + s->app + "\t" + s->cwd;
        for (const auto& a : s->args) out += "\t" + a;
        out += "\n";
        if (s->flagged) flagLine += "\t" + std::to_string(saved);
        saved++;
    }
    LeaveCriticalSection(&g_lock);
    if (!flagLine.empty()) out += "F" + flagLine + "\n";
    out += "A\t" + std::to_string(g_activeWs) + "\n";
    HANDLE f = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    DWORD wr; WriteFile(f, out.data(), (DWORD)out.size(), &wr, nullptr);
    CloseHandle(f);
}

// Select a face+size, apply it, and persist the choice (used by the Properties dialog).
static void pickFont(int faceIdx, int sizeIdx) {
    g_faceIdx = faceIdx; g_sizeIdx = sizeIdx;
    applyFont(); saveFontSel();
}
// Step the current face's size: dir>0 bigger, dir<0 smaller, dir==0 reset to the middle size.
static void fontZoom(int dir) {
    if (g_catalog.empty() || g_faceIdx < 0 || g_faceIdx >= (int)g_catalog.size()) return;
    int n = (int)g_catalog[g_faceIdx].sizes.size();
    int si = dir == 0 ? n / 2 : g_sizeIdx + (dir > 0 ? 1 : -1);
    si = max(0, min(si, n - 1));
    if (si != g_sizeIdx) pickFont(g_faceIdx, si);
}

// ---- Procedural box-drawing ----
// The raster Terminal/Fixedsys fonts have no Unicode cmap for U+2500.., so GDI draws blanks for the
// CP437/866 pseudographic borders that Far Manager (and other DOS-heritage TUIs) rely on. Draw them
// ourselves with GDI so they render crisply in ANY font. Covers the DOS single/double line set plus
// the solid/half blocks; returns 0 for runes we don't handle (those fall through to the font).
static uint8_t boxArms(uint32_t r) {
    // packed (up<<6)|(down<<4)|(left<<2)|right, each 2 bits: 0 none, 1 light, 2 double
    switch (r) {
        case 0x2500: return (1 << 2) | 1;                                     // ─
        case 0x2502: return (1 << 6) | (1 << 4);                              // │
        case 0x250C: return (1 << 4) | 1;                                     // ┌
        case 0x2510: return (1 << 4) | (1 << 2);                              // ┐
        case 0x2514: return (1 << 6) | 1;                                     // └
        case 0x2518: return (1 << 6) | (1 << 2);                              // ┘
        case 0x251C: return (1 << 6) | (1 << 4) | 1;                          // ├
        case 0x2524: return (1 << 6) | (1 << 4) | (1 << 2);                   // ┤
        case 0x252C: return (1 << 4) | (1 << 2) | 1;                          // ┬
        case 0x2534: return (1 << 6) | (1 << 2) | 1;                          // ┴
        case 0x253C: return (1 << 6) | (1 << 4) | (1 << 2) | 1;               // ┼
        case 0x2550: return (2 << 2) | 2;                                     // ═
        case 0x2551: return (2 << 6) | (2 << 4);                              // ║
        case 0x2554: return (2 << 4) | 2;                                     // ╔
        case 0x2557: return (2 << 4) | (2 << 2);                              // ╗
        case 0x255A: return (2 << 6) | 2;                                     // ╚
        case 0x255D: return (2 << 6) | (2 << 2);                              // ╝
        case 0x2560: return (2 << 6) | (2 << 4) | 2;                          // ╠
        case 0x2563: return (2 << 6) | (2 << 4) | (2 << 2);                   // ╣
        case 0x2566: return (2 << 4) | (2 << 2) | 2;                          // ╦
        case 0x2569: return (2 << 6) | (2 << 2) | 2;                          // ╩
        case 0x256C: return (2 << 6) | (2 << 4) | (2 << 2) | 2;               // ╬
        case 0x2552: return (1 << 4) | 2;                                     // ╒
        case 0x2553: return (2 << 4) | 1;                                     // ╓
        case 0x2555: return (1 << 4) | (2 << 2);                              // ╕
        case 0x2556: return (2 << 4) | (1 << 2);                              // ╖
        case 0x2558: return (1 << 6) | 2;                                     // ╘
        case 0x2559: return (2 << 6) | 1;                                     // ╙
        case 0x255B: return (1 << 6) | (2 << 2);                              // ╛
        case 0x255C: return (2 << 6) | (1 << 2);                              // ╜
        case 0x255E: return (1 << 6) | (1 << 4) | 2;                          // ╞
        case 0x255F: return (2 << 6) | (2 << 4) | 1;                          // ╟
        case 0x2561: return (1 << 6) | (1 << 4) | (2 << 2);                   // ╡
        case 0x2562: return (2 << 6) | (2 << 4) | (1 << 2);                   // ╢
        case 0x2564: return (1 << 4) | (2 << 2) | 2;                          // ╤
        case 0x2565: return (2 << 4) | (1 << 2) | 1;                          // ╥
        case 0x2567: return (1 << 6) | (2 << 2) | 2;                          // ╧
        case 0x2568: return (2 << 6) | (1 << 2) | 1;                          // ╨
        case 0x256A: return (1 << 6) | (1 << 4) | (2 << 2) | 2;               // ╪
        case 0x256B: return (2 << 6) | (2 << 4) | (1 << 2) | 1;               // ╫
    }
    return 0;
}
static bool isBoxGlyph(uint32_t r) {
    if (boxArms(r)) return true;
    switch (r) { case 0x2580: case 0x2584: case 0x2588: case 0x258C: case 0x2590: return true; }
    return false;
}
static void drawBoxGlyph(HDC dc, int x, int y, int cw, int ch, uint32_t r, COLORREF fg) {
    HBRUSH br = CreateSolidBrush(fg);
    switch (r) {   // solid + half blocks (scrollbars, shadows, fills)
        case 0x2588: { RECT b{ x, y, x + cw, y + ch }; FillRect(dc, &b, br); DeleteObject(br); return; }
        case 0x2580: { RECT b{ x, y, x + cw, y + ch / 2 }; FillRect(dc, &b, br); DeleteObject(br); return; }
        case 0x2584: { RECT b{ x, y + ch / 2, x + cw, y + ch }; FillRect(dc, &b, br); DeleteObject(br); return; }
        case 0x258C: { RECT b{ x, y, x + cw / 2, y + ch }; FillRect(dc, &b, br); DeleteObject(br); return; }
        case 0x2590: { RECT b{ x + cw / 2, y, x + cw, y + ch }; FillRect(dc, &b, br); DeleteObject(br); return; }
    }
    uint8_t a = boxArms(r);
    int up = (a >> 6) & 3, down = (a >> 4) & 3, left = (a >> 2) & 3, right = a & 3;
    int cx = x + cw / 2, cy = y + ch / 2, d = 1;   // d = half-gap between the two strokes of a double line
    auto hline = [&](int x0, int x1, int yy) { RECT rr{ x0, yy, x1, yy + 1 }; FillRect(dc, &rr, br); };
    auto vline = [&](int y0, int y1, int xx) { RECT rr{ xx, y0, xx + 1, y1 }; FillRect(dc, &rr, br); };
    // Light arms run edge->center; double arms are two parallel strokes that cross the center by d so
    // corners/junctions close cleanly.
    if (left == 1) hline(x, cx + 1, cy);
    if (left == 2) { hline(x, cx + d + 1, cy - d); hline(x, cx + d + 1, cy + d); }
    if (right == 1) hline(cx, x + cw, cy);
    if (right == 2) { hline(cx - d, x + cw, cy - d); hline(cx - d, x + cw, cy + d); }
    if (up == 1) vline(y, cy + 1, cx);
    if (up == 2) { vline(y, cy + d + 1, cx - d); vline(y, cy + d + 1, cx + d); }
    if (down == 1) vline(cy, y + ch, cx);
    if (down == 2) { vline(cy - d, y + ch, cx - d); vline(cy - d, y + ch, cx + d); }
    DeleteObject(br);
}

// ---- GDI paint ----
static COLORREF toColorRef(uint32_t packed, bool dim) {
    uint32_t r = (packed >> 16) & 0xFF, g = (packed >> 8) & 0xFF, b = packed & 0xFF;
    if (dim) { r = r * 6 / 10; g = g * 6 / 10; b = b * 6 / 10; }
    return RGB(r, g, b);
}

static HFONT styleFont(uint32_t attrs) {
    return g_fonts[((attrs & kAttrBold) ? 1 : 0) | ((attrs & kAttrItalic) ? 2 : 0)];
}

// Render one session's viewport into rect pr. `pane` selects the selection-highlight span (-1 = none,
// e.g. popup windows); `showCursor` draws the cursor (the focused main pane, or a popup terminal).
static void paintPane(HDC mem, RECT pr, Session* s, int pane, bool showCursor) {
    if (!s) return;
    FfiEmuInfo info{};
    EnterCriticalSection(&g_lock);
    emu_info(s->emu, &info);
    size_t need = (size_t)info.cols * info.rows;
    if (s->grid.size() < need) s->grid.resize(need);
    if (s->hrow.size() < info.cols) s->hrow.resize(info.cols);
    emu_copy_grid(s->emu, s->grid.data(), (uint32_t)s->grid.size());
    int off = min(s->scrollOff, (int)info.historyCount);
    s->scrollOff = off;
    // Compose the viewport: history tail above, live grid below (main-app semantics).
    std::vector<FfiCell> view((size_t)info.cols * info.rows);
    for (uint32_t r = 0; r < info.rows; r++) {
        int abs = (int)info.historyCount - off + (int)r;
        if (abs < (int)info.historyCount) {
            if (emu_copy_history_row(s->emu, (uint32_t)abs, s->hrow.data(), info.cols))
                memcpy(&view[r * info.cols], s->hrow.data(), info.cols * sizeof(FfiCell));
        } else {
            int live = abs - (int)info.historyCount;
            if (live < (int)info.rows)
                memcpy(&view[r * info.cols], &s->grid[live * info.cols], info.cols * sizeof(FfiCell));
        }
    }
    LeaveCriticalSection(&g_lock);

    std::vector<wchar_t> text;
    std::vector<INT> dx;
    for (uint32_t r = 0; r < info.rows; r++) {
        int y = pr.top + (int)r * g_ch;
        if (y + g_ch > pr.bottom) break;
        uint32_t c = 0;
        while (c < info.cols) {
            const FfiCell& cell = view[r * info.cols + c];
            if (cell.width == 0) { c++; continue; }
            uint32_t attrs = cell.attrs;
            uint32_t fg = cell.fg, bgc = cell.bg;
            if (g_customColors) { if (cell.fgKind == 0) fg = g_defFg; if (cell.bgKind == 0) bgc = g_defBg; }
            if (g_dosPalette) {   // remap ANSI indices to the muted DOS palette (fg brightens on bold)
                if (cell.fgKind == 1) { int ix = cell.fgIndex & 15; if (ix < 8 && (attrs & kAttrBold)) ix += 8; fg = kEgaPalette[ix]; }
                if (cell.bgKind == 1) bgc = kEgaPalette[cell.bgIndex & 15];
            }
            if (attrs & kAttrInverse) { uint32_t t = fg; fg = bgc; bgc = t; }
            uint32_t styleKey = attrs & (kAttrBold | kAttrItalic | kAttrUnderline | kAttrStrike | kAttrDim);
            uint32_t start = c;
            text.clear();
            dx.clear();
            while (c < info.cols) {
                const FfiCell& cc = view[r * info.cols + c];
                if (cc.width == 0) { c++; continue; }
                uint32_t f2 = cc.fg, b2 = cc.bg, a2 = cc.attrs;
                if (g_customColors) { if (cc.fgKind == 0) f2 = g_defFg; if (cc.bgKind == 0) b2 = g_defBg; }
                if (g_dosPalette) {
                    if (cc.fgKind == 1) { int ix = cc.fgIndex & 15; if (ix < 8 && (a2 & kAttrBold)) ix += 8; f2 = kEgaPalette[ix]; }
                    if (cc.bgKind == 1) b2 = kEgaPalette[cc.bgIndex & 15];
                }
                if (a2 & kAttrInverse) { uint32_t t = f2; f2 = b2; b2 = t; }
                if (f2 != fg || b2 != bgc ||
                    (a2 & (kAttrBold | kAttrItalic | kAttrUnderline | kAttrStrike | kAttrDim)) != styleKey) break;
                if (cc.rune > 0xFFFF) {
                    // Astral: surrogate pair with the advance on the FIRST unit (GDI draws the
                    // pair as one glyph when the font covers it; U+FFFD look comes free otherwise).
                    uint32_t v = cc.rune - 0x10000;
                    text.push_back((wchar_t)(0xD800 + (v >> 10)));
                    dx.push_back(g_cw * (int)cc.width);
                    text.push_back((wchar_t)(0xDC00 + (v & 0x3FF)));
                    dx.push_back(0);
                } else {
                    // Box-drawing runes are painted procedurally after the run (see below); feed the
                    // font a space so ETO_OPAQUE still lays down the background cell.
                    text.push_back(isBoxGlyph(cc.rune) ? L' ' : (wchar_t)(cc.rune ? cc.rune : L' '));
                    dx.push_back(g_cw * (int)cc.width);
                }
                c += cc.width;
            }
            int x = pr.left + (int)start * g_cw;
            if (x >= pr.right) break;
            SelectObject(mem, styleFont(styleKey));
            SetTextColor(mem, toColorRef(fg, (styleKey & kAttrDim) != 0));
            SetBkColor(mem, toColorRef(bgc, false));
            SetBkMode(mem, OPAQUE);
            RECT clip{ x, y, min((LONG)(pr.left + (LONG)c * g_cw), pr.right), y + g_ch };
            ExtTextOutW(mem, x, y, ETO_OPAQUE | ETO_CLIPPED, &clip, text.data(), (UINT)text.size(), dx.data());
            // Overlay CP437/866 pseudographics with GDI primitives (all cells in this run share fg).
            {
                COLORREF boxCol = toColorRef(fg, (styleKey & kAttrDim) != 0);
                for (uint32_t col = start; col < c; ) {
                    const FfiCell& bc = view[r * info.cols + col];
                    uint32_t w = bc.width ? bc.width : 1;
                    int bx = pr.left + (int)col * g_cw;
                    if (bx >= pr.right) break;
                    if (isBoxGlyph(bc.rune)) drawBoxGlyph(mem, bx, y, g_cw, g_ch, bc.rune, boxCol);
                    col += w;
                }
            }
            if (styleKey & (kAttrUnderline | kAttrStrike)) {
                HBRUSH b = CreateSolidBrush(toColorRef(fg, (styleKey & kAttrDim) != 0));
                if (styleKey & kAttrUnderline) { RECT u{ x, y + g_ch - 2, clip.right, y + g_ch - 1 }; FillRect(mem, &u, b); }
                if (styleKey & kAttrStrike) { RECT k{ x, y + g_ch / 2, clip.right, y + g_ch / 2 + 1 }; FillRect(mem, &k, b); }
                DeleteObject(b);
            }
        }
    }

    // Selection highlight (invert the selected span, buffer-absolute rows mapped into the view).
    if (g_sel.has() && g_sel.pane == pane) {
        int r0, c0, r1, c1;
        g_sel.norm(r0, c0, r1, c1);
        int base = (int)info.historyCount - off;   // buffer-absolute row of the top visible line
        for (uint32_t r = 0; r < info.rows; r++) {
            int abs = base + (int)r;
            if (abs < r0 || abs > r1) continue;
            int from = (abs == r0) ? c0 : 0;
            int to = (abs == r1) ? c1 : (int)info.cols;   // exclusive end column
            from = max(0, min(from, (int)info.cols));
            to = max(0, min(to, (int)info.cols));
            if (to <= from) continue;
            RECT sr{ pr.left + from * g_cw, pr.top + (int)r * g_ch,
                     min((LONG)(pr.left + to * g_cw), pr.right), pr.top + (int)(r + 1) * g_ch };
            InvertRect(mem, &sr);
        }
    }

    // Cursor (only at live view, only in the focused pane, not while selecting).
    if (off == 0 && info.cursorVisible && showCursor && info.cursorCol < info.cols && !g_sel.has()) {
        RECT cur{ pr.left + (LONG)info.cursorCol * g_cw, pr.top + (LONG)info.cursorRow * g_ch,
                  pr.left + (LONG)(info.cursorCol + 1) * g_cw, pr.top + (LONG)(info.cursorRow + 1) * g_ch };
        if (cur.right <= pr.right) InvertRect(mem, &cur);
    }
    // FTCS prompt pips (OSC 133): a small right-edge marker at each prompt line — green ok,
    // red failed, accent for a still-running command. The agent-status cue for Claude sessions.
    int doneMarks = 0;   // completed commands seen this paint (unread bookkeeping)
    if (info.markCount > 0) {
        std::vector<FfiMark> marks(info.markCount);
        EnterCriticalSection(&g_lock);
        uint32_t nm = emu_marks(s->emu, marks.data(), info.markCount);
        LeaveCriticalSection(&g_lock);
        int base = (int)info.historyCount - off;   // buffer-absolute row of the top visible line
        for (uint32_t mi = 0; mi < nm; mi++) {
            if (marks[mi].endLine >= 0) doneMarks++;
            int vr = (int)marks[mi].promptLine - base;
            if (vr < 0 || vr >= (int)info.rows) continue;
            COLORREF col = RGB(120, 130, 200);   // running (no end yet)
            if (marks[mi].endLine >= 0)
                col = (marks[mi].hasExit && marks[mi].exitCode == 0) ? RGB(60, 180, 90)
                    : marks[mi].hasExit ? RGB(210, 70, 70) : RGB(120, 130, 200);
            RECT pip{ pr.right - 3, pr.top + vr * g_ch + 2, pr.right, pr.top + vr * g_ch + g_ch - 2 };
            HBRUSH b = CreateSolidBrush(col);
            FillRect(mem, &pip, b);
            DeleteObject(b);
        }
    }
    if (!s->hidden) {   // painting = visible: mark as seen, clear the badge the moment you land here
        s->seenDone = doneMarks;
        if (s->unread) { s->unread = 0; PostMessageW(g_hwnd, WM_APP_REFRESHTREE, 0, 0); }
    }

    // Scrollback indicator: thin right-edge stripe while scrolled.
    if (off > 0) {
        RECT bar{ pr.right - 3, pr.top, pr.right, pr.bottom };
        HBRUSH b = CreateSolidBrush(RGB(90, 140, 200));
        FillRect(mem, &bar, b);
        DeleteObject(b);
    }
}

static void paint(HDC dc, RECT rc) {
    HDC mem = CreateCompatibleDC(dc);
    HBITMAP bmp = CreateCompatibleBitmap(dc, rc.right, rc.bottom);
    HGDIOBJ oldBmp = SelectObject(mem, bmp);
    HBRUSH bg = CreateSolidBrush(g_customColors ? toColorRef(g_defBg, false) : RGB(0, 0, 0));
    FillRect(mem, &rc, bg);
    DeleteObject(bg);

    // The sidebar is now the native SysTreeView32 child (0..kSidebarW); WS_CLIPCHILDREN keeps this
    // paint out of it. Only the terminal content area (>= kSidebarW) is drawn here.
    for (int p = 0; p < 2; p++) {
        if (g_pane[p] < 0 || g_pane[p] >= (int)g_sessions.size()) continue;
        RECT pr;
        paneRect(p, rc, &pr);
        paintPane(mem, pr, g_sessions[g_pane[p]], p, p == g_focus);
    }
    if (g_pane[1] >= 0) {   // split divider
        RECT pr0;
        paneRect(0, rc, &pr0);
        RECT div{ pr0.right, pr0.top, pr0.right + 2, pr0.bottom };
        HBRUSH b = CreateSolidBrush(g_th.classic ? RGB(60, 62, 70) : g_th.border);
        FillRect(mem, &div, b);
        DeleteObject(b);
    }
    if (g_showSidebar) {   // draggable splitter bar between the sidebar and the terminal
        RECT sp{ g_sidebarW, toolbarTop(), g_sidebarW + kSplitterW, rc.bottom - (g_showStatus ? g_statusH : 0) };
        HBRUSH f = CreateSolidBrush(g_th.bar);
        FillRect(mem, &sp, f); DeleteObject(f);
        // Classic keeps the 3-D etched groove; the themed looks use a flat 1px rule instead, because
        // DrawEdge only ever draws the system's light/shadow pair and reads as a bright seam on dark.
        if (g_th.classic) DrawEdge(mem, &sp, EDGE_ETCHED, BF_LEFT | BF_RIGHT);
        else { RECT ln{ sp.left, sp.top, sp.left + 1, sp.bottom }; HBRUSH lb = CreateSolidBrush(g_th.border);
               FillRect(mem, &ln, lb); ln.left = sp.right - 1; ln.right = sp.right; FillRect(mem, &ln, lb); DeleteObject(lb); }
    }

    if (g_palette) {
        int n = (int)(sizeof kPalette / sizeof kPalette[0]);
        int pw = 460, ph = (n + 1) * (g_ch + 8) + 12;
        int px = sidebarSpan() + ((rc.right - sidebarSpan()) - pw) / 2, py = 60;
        RECT box{ px, py, px + pw, py + ph };
        HBRUSH bb = CreateSolidBrush(RGB(28, 30, 38));
        FillRect(mem, &box, bb);
        DeleteObject(bb);
        FrameRect(mem, &box, (HBRUSH)GetStockObject(GRAY_BRUSH));
        SelectObject(mem, g_fonts[0]);
        SetBkMode(mem, TRANSPARENT);
        SetTextColor(mem, RGB(150, 150, 160));
        RECT hdr{ px + 14, py + 8, px + pw - 8, py + 8 + g_ch };
        DrawTextW(mem, L"command palette  (↑↓ Enter · Esc)", -1, &hdr, DT_LEFT | DT_SINGLELINE);
        for (int i = 0; i < n; i++) {
            int iy = py + 8 + (i + 1) * (g_ch + 8);
            if (i == g_paletteSel) {
                RECT sel{ px + 4, iy - 3, px + pw - 4, iy + g_ch + 3 };
                HBRUSH sb = CreateSolidBrush(RGB(50, 90, 150));
                FillRect(mem, &sel, sb);
                DeleteObject(sb);
            }
            SetTextColor(mem, i == g_paletteSel ? RGB(240, 240, 245) : RGB(200, 200, 210));
            RECT ir{ px + 16, iy, px + pw - 8, iy + g_ch };
            DrawTextW(mem, kPalette[i].label, -1, &ir, DT_LEFT | DT_SINGLELINE);
        }
    }

    BitBlt(dc, 0, 0, rc.right, rc.bottom, mem, 0, 0, SRCCOPY);
    SelectObject(mem, oldBmp);
    DeleteObject(bmp);
    DeleteDC(mem);
}

// ---- selection: pixel → (pane, buffer-absolute row, col) ----
static bool hitTest(int x, int y, int* pane, int* absRow, int* col) {
    RECT rc;
    GetClientRect(g_hwnd, &rc);
    for (int p = 0; p < 2; p++) {
        if (g_pane[p] < 0) continue;
        RECT pr;
        paneRect(p, rc, &pr);
        if (x < pr.left || x >= pr.right || y < pr.top || y >= pr.bottom) continue;
        Session* s = g_sessions[g_pane[p]];
        FfiEmuInfo info{};
        EnterCriticalSection(&g_lock);
        emu_info(s->emu, &info);
        LeaveCriticalSection(&g_lock);
        int r = (y - pr.top) / g_ch;
        int c = (x - pr.left) / g_cw;
        *pane = p;
        *absRow = (int)info.historyCount - min(s->scrollOff, (int)info.historyCount) + r;
        *col = max(0, min(c, (int)info.cols));
        return true;
    }
    return false;
}

// Extract the selected text (buffer-absolute rows), trailing spaces trimmed per line.
static std::string selectionText() {
    if (!g_sel.has()) return "";
    Session* s = g_sessions[g_pane[g_sel.pane]];
    int r0, c0, r1, c1;
    g_sel.norm(r0, c0, r1, c1);
    std::string out;
    FfiEmuInfo info{};
    EnterCriticalSection(&g_lock);
    emu_info(s->emu, &info);
    std::vector<FfiCell> row(info.cols);
    for (int abs = r0; abs <= r1; abs++) {
        bool got = false;
        if (abs < (int)info.historyCount) got = emu_copy_history_row(s->emu, (uint32_t)abs, row.data(), info.cols);
        else {
            int live = abs - (int)info.historyCount;
            if (live < (int)info.rows && s->grid.size() >= (size_t)info.cols * info.rows) {
                memcpy(row.data(), &s->grid[live * info.cols], info.cols * sizeof(FfiCell));
                got = true;
            }
        }
        if (!got) { out += '\n'; continue; }
        int from = (abs == r0) ? c0 : 0;
        int to = (abs == r1) ? c1 : (int)info.cols;
        std::string line;
        for (int c = from; c < to && c < (int)info.cols; c++) {
            const FfiCell& cell = row[c];
            if (cell.width == 0) continue;
            int cp = cell.rune ? cell.rune : ' ';
            wchar_t wb[2];
            int wn = 0;
            if (cp > 0xFFFF) { wb[wn++] = (wchar_t)(0xD800 + ((cp - 0x10000) >> 10)); wb[wn++] = (wchar_t)(0xDC00 + ((cp - 0x10000) & 0x3FF)); }
            else wb[wn++] = (wchar_t)cp;
            char u8[8];
            int n8 = WideCharToMultiByte(CP_UTF8, 0, wb, wn, u8, sizeof u8, nullptr, nullptr);
            line.append(u8, n8);
        }
        while (!line.empty() && line.back() == ' ') line.pop_back();
        out += line;
        if (abs < r1) out += "\r\n";
    }
    LeaveCriticalSection(&g_lock);
    return out;
}

static void copySelection() {
    std::string utf8 = selectionText();
    if (utf8.empty() || !OpenClipboard(g_hwnd)) return;
    EmptyClipboard();
    int wn = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), (int)utf8.size(), nullptr, 0);
    HGLOBAL h = GlobalAlloc(GMEM_MOVEABLE, (wn + 1) * sizeof(wchar_t));
    if (h) {
        wchar_t* p = (wchar_t*)GlobalLock(h);
        MultiByteToWideChar(CP_UTF8, 0, utf8.data(), (int)utf8.size(), p, wn);
        p[wn] = 0;
        GlobalUnlock(h);
        SetClipboardData(CF_UNICODETEXT, h);
    }
    CloseClipboard();
}

// ---- input ----
static void sendBytes(const char* bytes, int len) {
    Session* s = focusedSession();
    if (s && s->data != INVALID_HANDLE_VALUE) ovIo(s->data, true, bytes, nullptr, (DWORD)len);
}

static void pasteClipboard() {
    Session* s = focusedSession();
    if (!s || s->data == INVALID_HANDLE_VALUE || !OpenClipboard(g_hwnd)) return;
    HANDLE h = GetClipboardData(CF_UNICODETEXT);
    if (h) {
        wchar_t* w = (wchar_t*)GlobalLock(h);
        if (w) {
            int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
            std::string u8(n > 0 ? n - 1 : 0, 0);
            if (!u8.empty()) WideCharToMultiByte(CP_UTF8, 0, w, -1, &u8[0], n, nullptr, nullptr);
            // Bracketed paste when the app enabled it (safer multiline paste), else raw.
            FfiEmuInfo info{};
            EnterCriticalSection(&g_lock);
            emu_info(s->emu, &info);
            LeaveCriticalSection(&g_lock);
            if (info.bracketedPaste) { ovIo(s->data, true, "\x1b[200~", nullptr, 6); }
            ovIo(s->data, true, u8.data(), nullptr, (DWORD)u8.size());
            if (info.bracketedPaste) { ovIo(s->data, true, "\x1b[201~", nullptr, 6); }
            GlobalUnlock(h);
        }
    }
    CloseClipboard();
}

static void runPaletteItem(int id);   // fwd

static void sendUtf8(wchar_t wc) {
    char utf8[8];
    int n = WideCharToMultiByte(CP_UTF8, 0, &wc, 1, utf8, sizeof utf8, nullptr, nullptr);
    if (n > 0) sendBytes(utf8, n);
}

static bool ctrlDown() { return (GetKeyState(VK_CONTROL) & 0x8000) != 0; }
static bool shiftDown() { return (GetKeyState(VK_SHIFT) & 0x8000) != 0; }
static bool altDown() { return (GetKeyState(VK_MENU) & 0x8000) != 0; }

// Forward a mouse event to the app when the pane under (x,y) has mouse reporting on (so full-screen
// apps like Far Manager get clicks/drags/wheel). Returns true if it was forwarded OR deliberately
// swallowed (a reporting pane), so the caller skips selection/paste; false = do the normal UI action.
// cb: 0 left, 1 middle, 2 right, 64 wheel-up, 65 wheel-down.
static bool mouseReport(int x, int y, int cb, bool press, bool motion) {
    RECT rc; GetClientRect(g_hwnd, &rc);
    for (int p = 0; p < 2; p++) {
        if (g_pane[p] < 0) continue;
        RECT pr; paneRect(p, rc, &pr);
        if (x < pr.left || x >= pr.right || y < pr.top || y >= pr.bottom) continue;
        Session* s = g_sessions[g_pane[p]];
        FfiEmuInfo info{};
        EnterCriticalSection(&g_lock);
        emu_info(s->emu, &info);
        LeaveCriticalSection(&g_lock);
        if (!info.mouseClick && !info.mouseDrag && !info.mouseMotion) return false;  // no reporting -> selection path
        if (motion && !info.mouseDrag && !info.mouseMotion) return true;             // click-only app: swallow motion
        if (s->data == INVALID_HANDLE_VALUE) return true;
        int col = (x - pr.left) / g_cw + 1;
        int row = (y - pr.top) / g_ch + 1;
        int mods = (shiftDown() ? 4 : 0) + (altDown() ? 8 : 0) + (ctrlDown() ? 16 : 0);
        char buf[48];
        int len;
        if (info.mouseSgr) {
            int b = cb + (motion ? 32 : 0) + mods;
            len = wsprintfA(buf, "\x1b[<%d;%d;%d%c", b, col, row, press ? 'M' : 'm');   // SGR 1006
        } else {
            int b = (press ? cb : 3) + (motion ? 32 : 0) + mods;                       // legacy X10/normal
            int cc = col > 223 ? 0 : col, rr = row > 223 ? 0 : row;
            len = wsprintfA(buf, "\x1b[M%c%c%c", 32 + b, 32 + cc, 32 + rr);
        }
        ovIo(s->data, true, buf, nullptr, (DWORD)len);
        return true;
    }
    return false;
}

static void scrollFocused(int deltaRows) {
    Session* s = focusedSession();
    if (!s) return;
    FfiEmuInfo info{};
    EnterCriticalSection(&g_lock);
    emu_info(s->emu, &info);
    LeaveCriticalSection(&g_lock);
    int off = s->scrollOff + deltaRows;
    s->scrollOff = max(0, min(off, (int)info.historyCount));
    InvalidateRect(g_hwnd, nullptr, FALSE);
}

static void togglePalette() {
    g_palette = !g_palette;
    g_paletteSel = 0;
    InvalidateRect(g_hwnd, nullptr, FALSE);
}

static void runPaletteItem(int id) {
    g_palette = false;
    switch (id) {
        case 1: { int c, r; paneGridSize(g_focus, &c, &r); Session* s = newSession(c, r); if (s) g_pane[g_focus] = (int)g_sessions.size() - 1; break; }
        case 2: closeFocused(); break;
        case 3: toggleSplit(); break;   // independent new shell for the second pane
        case 4: cycleSession(1); break;    // next session
        case 5: copySelection(); break;
        case 6: pasteClipboard(); break;
        case 7: cycleSession(-1); break;   // previous session
    }
    InvalidateRect(g_hwnd, nullptr, FALSE);
}

static void togglePopupTerminal(bool scratch);   // fwd (quick/scratch popup windows, defined below)
static void toggleFlag(Session* s);              // fwd (flagged sessions, defined below)
static void toggleFlagView();                    // fwd
static void nextBlocked();                       // fwd (attention bell)
static void toggleFocusWs(int w);                // fwd (workspace focus)
static void runKbAction(int a) {
    switch (a) {
        case KB_NEW: { int c, r; paneGridSize(g_focus, &c, &r); Session* s = newSession(c, r); if (s) { g_pane[g_focus] = (int)g_sessions.size() - 1; InvalidateRect(g_hwnd, nullptr, FALSE); } break; }
        case KB_NEWWS: SendMessageW(g_hwnd, WM_COMMAND, IDM_NEWWS, 0); break;
        case KB_CLOSE: closeFocused(); break;
        case KB_SPLIT: toggleSplit(); break;
        case KB_NEXT: cycleSession(1); break;
        case KB_PREV: cycleSession(-1); break;
        case KB_COPY: copySelection(); break;
        case KB_PASTE: pasteClipboard(); break;
        case KB_PALETTE: togglePalette(); break;
        case KB_FOCUSL: g_focus = 0; InvalidateRect(g_hwnd, nullptr, FALSE); break;
        case KB_FOCUSR: if (g_pane[1] >= 0) g_focus = 1; InvalidateRect(g_hwnd, nullptr, FALSE); break;
        case KB_SCROLLUP: scrollFocused(+10); break;
        case KB_SCROLLDN: scrollFocused(-10); break;
        case KB_QUICK: togglePopupTerminal(false); break;
        case KB_SCRATCH: togglePopupTerminal(true); break;
        case KB_REOPEN: reopenClosed(); break;
        case KB_ZOOMIN: fontZoom(+1); break;
        case KB_ZOOMOUT: fontZoom(-1); break;
        case KB_ZOOMRESET: fontZoom(0); break;
        case KB_FLAG: toggleFlag(focusedSession()); break;
        case KB_FLAGVIEW: toggleFlagView(); break;
        case KB_ATTENTION: nextBlocked(); break;
        case KB_FOCUSWS: toggleFocusWs(g_focusWs >= 0 ? g_focusWs : g_activeWs); break;
    }
}
static bool handleKeyDown(WPARAM vk) {
    if (g_palette) {   // palette captures navigation while open
        int n = (int)(sizeof kPalette / sizeof kPalette[0]);
        if (vk == VK_ESCAPE) { g_palette = false; InvalidateRect(g_hwnd, nullptr, FALSE); return true; }
        if (vk == VK_UP) { g_paletteSel = (g_paletteSel + n - 1) % n; InvalidateRect(g_hwnd, nullptr, FALSE); return true; }
        if (vk == VK_DOWN) { g_paletteSel = (g_paletteSel + 1) % n; InvalidateRect(g_hwnd, nullptr, FALSE); return true; }
        if (vk == VK_RETURN) { runPaletteItem(kPalette[g_paletteSel].id); return true; }
        return true;
    }
    // Configurable key bindings (all unbound by default, so every combo otherwise reaches the shell).
    // Match the pressed vk + modifiers against the user's bindings; the same actions are always on the
    // menu + toolbar. Checked before the xterm-key encoding so a bound combo wins over the default key.
    {
        BYTE mods = (BYTE)((shiftDown() ? HOTKEYF_SHIFT : 0) | (ctrlDown() ? HOTKEYF_CONTROL : 0) | (altDown() ? HOTKEYF_ALT : 0));
        WORD combo = MAKEWORD((BYTE)vk, mods);
        if (mods) for (int a = 0; a < KB_COUNT; a++) if (g_keys[a] == combo) { runKbAction(a); return true; }
    }

    // Terminal special keys, encoded with xterm modifiers (mod = 1 + shift + 2*alt + 4*ctrl) so
    // full-screen apps like Far Manager get F1-F12 and Ctrl/Shift/Alt combinations. Three forms:
    //   csiFinal  -> ESC [ [1;mod] <A/B/C/D/H/F>   (arrows, Home, End)
    //   ss3       -> ESC O <P/Q/R/S>  or  ESC [ 1;mod <P/Q/R/S>   (F1-F4)
    //   tilde     -> ESC [ <n> [;mod] ~   (Insert, Delete, PgUp/Dn, F5-F12)
    int mod = 1 + (shiftDown() ? 1 : 0) + (altDown() ? 2 : 0) + (ctrlDown() ? 4 : 0);
    const char* csiFinal = nullptr; char ss3 = 0; int tilde = 0;
    switch (vk) {
        case VK_UP: csiFinal = "A"; break;
        case VK_DOWN: csiFinal = "B"; break;
        case VK_RIGHT: csiFinal = "C"; break;
        case VK_LEFT: csiFinal = "D"; break;
        case VK_HOME: csiFinal = "H"; break;
        case VK_END: csiFinal = "F"; break;
        case VK_INSERT: tilde = 2; break;
        case VK_DELETE: tilde = 3; break;
        case VK_PRIOR: tilde = 5; break;
        case VK_NEXT: tilde = 6; break;
        case VK_F1: ss3 = 'P'; break;
        case VK_F2: ss3 = 'Q'; break;
        case VK_F3: ss3 = 'R'; break;
        case VK_F4: ss3 = 'S'; break;
        case VK_F5: tilde = 15; break;
        case VK_F6: tilde = 17; break;
        case VK_F7: tilde = 18; break;
        case VK_F8: tilde = 19; break;
        case VK_F9: tilde = 20; break;
        case VK_F10: tilde = 21; break;
        case VK_F11: tilde = 23; break;
        case VK_F12: tilde = 24; break;
        case VK_TAB: if (shiftDown()) { sendBytes("\x1b[Z", 3); if (Session* s = focusedSession()) s->scrollOff = 0; return true; } return false; // Shift+Tab = back-tab; plain Tab -> WM_CHAR
        default: return false;
    }
    char buf[32];
    if (csiFinal) {
        if (mod > 1) wsprintfA(buf, "\x1b[1;%d%s", mod, csiFinal);
        else wsprintfA(buf, "\x1b[%s", csiFinal);
    } else if (ss3) {
        if (mod > 1) wsprintfA(buf, "\x1b[1;%d%c", mod, ss3);
        else wsprintfA(buf, "\x1bO%c", ss3);
    } else {
        if (mod > 1) wsprintfA(buf, "\x1b[%d;%d~", tilde, mod);
        else wsprintfA(buf, "\x1b[%d~", tilde);
    }
    if (Session* s = focusedSession()) s->scrollOff = 0;   // typing snaps back to live
    sendBytes(buf, (int)strlen(buf));
    return true;
}

static void newSessionDialog(const char* cwd = nullptr);   // fwd (defined below, used by the context menu)

// Rebuild the native TreeView sidebar from the session list; select the focused pane's session.
// UI-thread only (worker threads post WM_APP_REFRESHTREE instead).
// Fill the status bar's four parts: workspace · session count · terminal size · font.
static void updateStatus() {
    if (!g_status) return;
    wchar_t buf[160];
    const std::wstring& ws = (g_activeWs >= 0 && g_activeWs < (int)g_workspaces.size()) ? g_workspaces[g_activeWs] : g_workspaces[0];
    std::wstring ws0 = (g_focusWs >= 0) ? ws + L"  (focused)" : ws;
    SendMessageW(g_status, SB_SETTEXTW, 0, (LPARAM)ws0.c_str());
    int n = 0; for (auto* s : g_sessions) if (!s->hidden) n++;
    wsprintfW(buf, L"%d session%s  \x00B7  Rust pty-host", n, n == 1 ? L"" : L"s");
    SendMessageW(g_status, SB_SETTEXTW, 1, (LPARAM)buf);
    if (Session* s = focusedSession()) {
        FfiEmuInfo info{}; EnterCriticalSection(&g_lock); emu_info(s->emu, &info); LeaveCriticalSection(&g_lock);
        wsprintfW(buf, L"%u \x00D7 %u", info.cols, info.rows); SendMessageW(g_status, SB_SETTEXTW, 2, (LPARAM)buf);
    }
    if (!g_catalog.empty() && g_faceIdx >= 0 && g_faceIdx < (int)g_catalog.size()) {
        const FontEntry& e = g_catalog[g_faceIdx];
        const wchar_t* sz = (g_sizeIdx >= 0 && g_sizeIdx < (int)e.sizes.size()) ? e.sizes[g_sizeIdx].label : L"";
        wsprintfW(buf, L"%s %s", e.label, sz); SendMessageW(g_status, SB_SETTEXTW, 3, (LPARAM)buf);
    }
}
static void refreshTree() {
    if (!g_tree) return;
    g_treeSyncing = true;
    TreeView_DeleteAllItems(g_tree);
    HTREEITEM sel = nullptr;
    int focusIdx = g_pane[g_focus];
    // Group sessions under their workspace ("folder"). lParam encodes the node: >=0 session index,
    // <0 = -(workspace index + 1).
    bool anyShown = false;
    for (int w = 0; w < (int)g_workspaces.size(); w++) {
        if (g_focusWs >= 0 && w != g_focusWs) continue;   // focused workspace: show only it
        int count = 0, flaggedCount = 0;
        for (auto* s : g_sessions) if (s->ws == w && !s->hidden) { count++; if (s->flagged) flaggedCount++; }
        if (g_flagView && flaggedCount == 0) continue;   // flagged view: only workspaces with flagged sessions
        wchar_t wlabel[96];
        wsprintfW(wlabel, L"%s  (%d)", g_workspaces[w].c_str(), g_flagView ? flaggedCount : count);
        TVINSERTSTRUCTW wt{};
        wt.hParent = TVI_ROOT;
        wt.hInsertAfter = TVI_LAST;
        wt.item.mask = TVIF_TEXT | TVIF_PARAM;
        wt.item.pszText = wlabel;
        wt.item.lParam = -(w + 1);
        HTREEITEM wh = TreeView_InsertItem(g_tree, &wt);
        anyShown = true;
        int vis = 0;   // visible session number within the workspace
        for (int i = 0; i < (int)g_sessions.size(); i++) {
            if (g_sessions[i]->ws != w || g_sessions[i]->hidden) continue;   // skip split shells
            Session* s = g_sessions[i];
            ++vis;   // stable numbering: count ALL the workspace's sessions, filtered or not
            if (g_flagView && !s->flagged) continue;                         // flagged view filter
            // Agent status cue: name goes bold when the agent needs you (blocked), italic + "(working…)"
            // while it's busy (italic applied in the tree's NM_CUSTOMDRAW). Others show plain.
            int cls = s->exited ? AGST_NONE : statusClass(s->status);
            std::wstring label = s->name.empty() ? (L"session " + std::to_wstring(vis)) : s->name;
            if (s->exited) label += L"  (exited)";
            else if (cls == AGST_WORKING) label += L"  (working…)";
            TVINSERTSTRUCTW tis{};
            tis.hParent = wh;
            tis.hInsertAfter = TVI_LAST;
            tis.item.mask = TVIF_TEXT | TVIF_PARAM | TVIF_STATE;
            tis.item.stateMask = TVIS_BOLD;
            tis.item.state = (cls == AGST_BLOCKED) ? TVIS_BOLD : 0;
            tis.item.pszText = (LPWSTR)label.c_str();
            tis.item.lParam = i;
            HTREEITEM h = TreeView_InsertItem(g_tree, &tis);
            if (i == focusIdx) sel = h;
        }
        TreeView_Expand(g_tree, wh, TVE_EXPAND);
    }
    if (g_flagView && !anyShown) {   // hint row; lParam sentinel is out of range for every handler
        TVINSERTSTRUCTW ti{};
        ti.hParent = TVI_ROOT; ti.hInsertAfter = TVI_LAST;
        ti.item.mask = TVIF_TEXT | TVIF_PARAM;
        ti.item.pszText = (LPWSTR)L"No flagged sessions (right-click one to flag)";
        ti.item.lParam = -100000;
        TreeView_InsertItem(g_tree, &ti);
    }
    if (sel) TreeView_SelectItem(g_tree, sel);
    g_treeSyncing = false;
    updateStatus();
    if (!g_restoring) saveSessionState();   // persist the workspace/session structure on every change
}

// Remove a workspace; its sessions fall back to the first workspace (indices shift down).
static void deleteWorkspace(int w) {
    if ((int)g_workspaces.size() <= 1 || w < 0 || w >= (int)g_workspaces.size()) return;
    g_workspaces.erase(g_workspaces.begin() + w);
    for (auto* s : g_sessions) {
        if (s->ws == w) s->ws = 0;
        else if (s->ws > w) s->ws--;
    }
    if (g_activeWs == w) g_activeWs = 0;
    else if (g_activeWs > w) g_activeWs--;
    if (g_focusWs == w) g_focusWs = -1;
    else if (g_focusWs > w) g_focusWs--;
    refreshTree();
}

// Right-click menu on a tree node — session or workspace, mirroring the full app's sidebar menus.
// Acts on the RIGHT-CLICKED node (g_ctxParam), not the focused session, and dispatches inline via
// TPM_RETURNCMD (no WM_COMMAND re-entrancy, no selection change — so the active terminal doesn't jump).
static void showTreeContextMenu() {
    bool isSession = g_ctxParam >= 0;
    int si = isSession ? (int)g_ctxParam : -1;
    int cws = isSession ? (si < (int)g_sessions.size() ? g_sessions[si]->ws : 0) : (int)(-g_ctxParam - 1);
    if (!isSession && (cws < 0 || cws >= (int)g_workspaces.size())) return;   // hint row etc.
    POINT pt; GetCursorPos(&pt);
    HMENU m = CreatePopupMenu();
    if (isSession) {   // ---- session node ----
        AppendMenuW(m, MF_STRING, IDM_NEW, L"&New Session…");
        AppendMenuW(m, MF_STRING, IDM_DUP, L"&Duplicate Session");
        AppendMenuW(m, MF_STRING, IDM_RENAME, L"Re&name");
        AppendMenuW(m, MF_STRING, IDM_FLAG,
                    (si < (int)g_sessions.size() && g_sessions[si]->flagged) ? L"Unfla&g" : L"Fla&g");
        if ((int)g_workspaces.size() > 1) {
            HMENU sub = CreatePopupMenu();
            for (int w = 0; w < (int)g_workspaces.size(); w++)
                if (w != cws) AppendMenuW(sub, MF_STRING, IDM_MOVE_BASE + w, g_workspaces[w].c_str());
            AppendMenuW(m, MF_POPUP, (UINT_PTR)sub, L"&Move to");
        }
        AppendMenuW(m, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(m, MF_STRING, IDM_CLOSE, L"&Close Session");
    } else {           // ---- workspace node ----
        AppendMenuW(m, MF_STRING, IDM_NEW, L"&New Session");
        AppendMenuW(m, MF_STRING, IDM_NEWWS, L"New &Workspace");
        AppendMenuW(m, MF_STRING, IDM_RENAME, L"Re&name");
        AppendMenuW(m, MF_STRING, IDM_FOCUSWS, g_focusWs == cws ? L"Unf&ocus Workspace" : L"F&ocus Workspace");
        AppendMenuW(m, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(m, MF_STRING | ((int)g_workspaces.size() <= 1 ? MF_GRAYED : 0), IDM_DELWS, L"&Delete Workspace");
    }
    SetForegroundWindow(g_hwnd);
    int id = (int)TrackPopupMenu(m, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, 0, g_hwnd, nullptr);
    DestroyMenu(m);
    if (id == 0) return;   // dismissed
    switch (id) {
        case IDM_NEW: g_activeWs = cws; newSessionDialog(); break;
        case IDM_NEWWS: {
            wchar_t nm[32]; wsprintfW(nm, L"workspace %d", (int)g_workspaces.size() + 1);
            g_workspaces.push_back(nm); g_activeWs = (int)g_workspaces.size() - 1; refreshTree();
            break;
        }
        case IDM_DUP:
            if (isSession) {
                g_activeWs = cws;
                int c, r; paneGridSize(g_focus, &c, &r);
                Session* s = newSession(c, r);
                if (s) { g_pane[g_focus] = (int)g_sessions.size() - 1; syncPaneSizes(); InvalidateRect(g_hwnd, nullptr, FALSE); }
            }
            break;
        case IDM_RENAME:
            if (g_ctxItem) { SetFocus(g_tree); TreeView_EditLabel(g_tree, g_ctxItem); }   // inline edit
            break;
        case IDM_FLAG:
            if (isSession && si < (int)g_sessions.size()) toggleFlag(g_sessions[si]);
            break;
        case IDM_CLOSE:
            if (isSession) closeSessionAt(si);
            break;
        case IDM_DELWS:
            if (!isSession) deleteWorkspace(cws);
            break;
        case IDM_FOCUSWS:
            if (!isSession) toggleFocusWs(cws);
            break;
        default:
            if (isSession && id >= IDM_MOVE_BASE && id < IDM_MOVE_BASE + (int)g_workspaces.size()
                && si < (int)g_sessions.size()) {
                g_sessions[si]->ws = id - IDM_MOVE_BASE;   // move to workspace
                refreshTree();
            }
            break;
    }
}

static HMENU buildMenuBar() {
    HMENU file = CreatePopupMenu();
    AppendMenuW(file, MF_STRING, IDM_NEW, L"&New Session…");
    AppendMenuW(file, MF_STRING, IDM_NEWWS, L"New &Workspace");
    AppendMenuW(file, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(file, MF_STRING, IDM_CLOSE, L"&Close Session");
    AppendMenuW(file, MF_STRING, IDM_REOPEN, L"Reop&en Closed Session");
    AppendMenuW(file, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(file, MF_STRING, IDM_KEYBOARD, L"&Keyboard…");
    AppendMenuW(file, MF_STRING, IDM_PROPERTIES, L"P&roperties…");
    AppendMenuW(file, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(file, MF_STRING, IDM_RESTART, L"&Restart everything");
    AppendMenuW(file, MF_STRING, IDM_EXIT, L"E&xit");
    HMENU edit = CreatePopupMenu();
    AppendMenuW(edit, MF_STRING, IDM_COPY, L"&Copy");
    AppendMenuW(edit, MF_STRING, IDM_PASTE, L"&Paste");
    HMENU view = CreatePopupMenu();
    AppendMenuW(view, MF_STRING, IDM_SPLIT, L"&Split / Unsplit");
    AppendMenuW(view, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(view, MF_STRING, IDM_NEXT, L"&Next Session");
    AppendMenuW(view, MF_STRING, IDM_PREV, L"&Previous Session");
    AppendMenuW(view, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(view, MF_STRING, IDM_QUICK, L"&Quick Terminal");
    AppendMenuW(view, MF_STRING, IDM_SCRATCH, L"Sc&ratch Terminal");
    AppendMenuW(view, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(view, MF_STRING, IDM_FLAG, L"Fla&g / Unflag Session");
    AppendMenuW(view, MF_STRING | (g_flagView ? MF_CHECKED : 0), IDM_FLAGVIEW, L"Flagged Vie&w");
    AppendMenuW(view, MF_STRING | (g_focusWs >= 0 ? MF_CHECKED : 0), IDM_FOCUSWS, L"F&ocus Workspace");
    AppendMenuW(view, MF_STRING, IDM_ATTENTION, L"Next Bloc&ked Session");
    AppendMenuW(view, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(view, MF_STRING | (g_showSidebar ? MF_CHECKED : 0), IDM_TG_SIDEBAR, L"Side&bar");
    AppendMenuW(view, MF_STRING | (g_showToolbar ? MF_CHECKED : 0), IDM_TG_TOOLBAR, L"&Toolbar");
    AppendMenuW(view, MF_STRING | (g_showStatus ? MF_CHECKED : 0), IDM_TG_STATUS, L"Status &Bar");
    // Font selection lives in File -> Properties now (no separate View -> Font submenu).
    HMENU help = CreatePopupMenu();
    AppendMenuW(help, MF_STRING, IDM_ABOUT, L"&About agwinterm lite");
    HMENU bar = CreateMenu();
    AppendMenuW(bar, MF_POPUP, (UINT_PTR)file, L"&File");
    AppendMenuW(bar, MF_POPUP, (UINT_PTR)edit, L"&Edit");
    AppendMenuW(bar, MF_POPUP, (UINT_PTR)view, L"&View");
    AppendMenuW(bar, MF_POPUP, (UINT_PTR)help, L"&Help");
    return bar;
}

// ---- New Session modal dialog (native popup + listbox, no .rc resource) ----
static HWND g_dlgList;
static int g_dlgResult;   // -1 = cancel, else selected profile index

static LRESULT CALLBACK profileDlgProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    { LRESULT tr; if (themeDlgMsg(h, m, w, &tr)) return tr; }   // dark background + control colours
    if (m == WM_DRAWITEM) { drawDlgButton((LPDRAWITEMSTRUCT)l); return TRUE; }
    if (m == DM_GETDEFID) return MAKELRESULT(IDOK, DC_HASDEFID);
    switch (m) {
        case WM_COMMAND:
            if (LOWORD(w) == IDOK || (LOWORD(w) == 1000 && HIWORD(w) == LBN_DBLCLK)) {
                g_dlgResult = (int)SendMessageW(g_dlgList, LB_GETCURSEL, 0, 0);
                DestroyWindow(h);
                return 0;
            }
            if (LOWORD(w) == IDCANCEL) { g_dlgResult = -1; DestroyWindow(h); return 0; }
            break;
        case WM_CLOSE: g_dlgResult = -1; DestroyWindow(h); return 0;
    }
    return DefWindowProcW(h, m, w, l);
}

// Modal profile picker; returns the chosen index or -1. Runs a local loop with the parent disabled.
static int pickProfileDialog(const std::vector<Profile>& profs) {
    static bool reg = false;
    HINSTANCE inst = GetModuleHandleW(nullptr);
    if (!reg) {
        WNDCLASSW wc{};
        wc.lpfnWndProc = profileDlgProc;
        wc.hInstance = inst;
        wc.lpszClassName = L"AgwintermLiteDlg";
        wc.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1);
        wc.hCursor = LoadCursorW(nullptr, (LPCWSTR)IDC_ARROW);
        RegisterClassW(&wc);
        reg = true;
    }
    const int W = 300, H = 250;
    RECT pw; GetWindowRect(g_hwnd, &pw);
    HWND dlg = CreateWindowExW(WS_EX_DLGMODALFRAME, L"AgwintermLiteDlg", L"New Session",
                               WS_POPUP | WS_CAPTION | WS_SYSMENU,
                               pw.left + 70, pw.top + 70, W, H, g_hwnd, nullptr, inst, nullptr);
    HFONT gui = (HFONT)GetStockObject(DEFAULT_GUI_FONT);
    RECT cr; GetClientRect(dlg, &cr);
    HWND lbl = CreateWindowExW(0, L"STATIC", L"Choose a shell:", WS_CHILD | WS_VISIBLE,
                               12, 10, cr.right - 24, 18, dlg, nullptr, inst, nullptr);
    g_dlgList = CreateWindowExW(WS_EX_CLIENTEDGE, L"LISTBOX", L"",
                                WS_CHILD | WS_VISIBLE | WS_VSCROLL | LBS_NOTIFY,
                                12, 32, cr.right - 24, cr.bottom - 84, dlg, (HMENU)1000, inst, nullptr);
    SetWindowSubclass(g_dlgList, fieldRingProc, 1, 0);   // dark bezel over the classic client edge
    for (const auto& p : profs) SendMessageW(g_dlgList, LB_ADDSTRING, 0, (LPARAM)p.name.c_str());
    SendMessageW(g_dlgList, LB_SETCURSEL, 0, 0);
    HWND ok = CreateWindowExW(0, L"BUTTON", L"OK", WS_CHILD | WS_VISIBLE | BS_OWNERDRAW,
                              cr.right - 176, cr.bottom - 40, 78, 26, dlg, (HMENU)IDOK, inst, nullptr);
    HWND cancel = CreateWindowExW(0, L"BUTTON", L"Cancel", WS_CHILD | WS_VISIBLE | BS_OWNERDRAW,
                                  cr.right - 90, cr.bottom - 40, 78, 26, dlg, (HMENU)IDCANCEL, inst, nullptr);
    for (HWND c : { lbl, g_dlgList, ok, cancel }) SendMessageW(c, WM_SETFONT, (WPARAM)gui, TRUE);
    g_dlgResult = -1;
    themeDialog(dlg);
    EnableWindow(g_hwnd, FALSE);
    ShowWindow(dlg, SW_SHOW);
    SetFocus(g_dlgList);
    MSG msg;
    while (IsWindow(dlg)) {
        if (!GetMessageW(&msg, nullptr, 0, 0)) { PostQuitMessage((int)msg.wParam); break; }  // WM_QUIT — bail
        if (!IsDialogMessageW(dlg, &msg)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    }
    EnableWindow(g_hwnd, TRUE);
    SetForegroundWindow(g_hwnd);
    SetFocus(g_hwnd);
    return g_dlgResult;
}

// Open the New Session dialog and create the chosen shell (in an optional folder).
static void newSessionDialog(const char* cwd) {
    auto profs = detectProfiles();
    int i = pickProfileDialog(profs);
    if (i < 0 || i >= (int)profs.size()) return;
    int c, r; paneGridSize(g_focus, &c, &r);
    Session* s = newSession(c, r, profs[i].app.c_str(), &profs[i].args, cwd);
    if (s) { g_pane[g_focus] = (int)g_sessions.size() - 1; syncPaneSizes(); InvalidateRect(g_hwnd, nullptr, FALSE); }
}

// ---- Properties dialog (cmd.exe-style: font + colors, live preview) ----
// The legacy Windows console 16-colour palette, so the swatches feel like the old cmd.exe Colors tab.
static const COLORREF kConsolePalette[16] = {
    RGB(0,0,0),     RGB(0,0,128),   RGB(0,128,0),   RGB(0,128,128),
    RGB(128,0,0),   RGB(128,0,128), RGB(128,128,0), RGB(192,192,192),
    RGB(128,128,128),RGB(0,0,255),  RGB(0,255,0),   RGB(0,255,255),
    RGB(255,0,0),   RGB(255,0,255), RGB(255,255,0), RGB(255,255,255),
};
enum { PID_FONTLIST = 3001, PID_SIZECOMBO = 3002, PID_THEME = 3003, PID_USECOLORS = 3030, PID_TEXT = 3010, PID_BG = 3011, PID_APPLY = 3020, PID_DOSPAL = 3031 };
static const int SW_X0 = 16, SW_Y = 186, SW = 20, SW_GAP = 22;   // swatch grid geometry (WM_PAINT + hit-test)
static const wchar_t* kThemeNames[4] = { L"Auto (follow Windows)", L"Dark", L"Light", L"Classic" };
// Working copies edited by the dialog; committed to the globals on OK/Apply.
static int g_pFace, g_pSize, g_pTheme; static uint32_t g_pFg, g_pBg; static int g_pTarget; static bool g_pUse, g_pDos;
static HFONT g_pPrev; static HWND g_pHwnd, g_pSizeCombo;

// ---- owner-drawn dialog buttons ---------------------------------------------------------------
// Roles are known by id; check/radio state lives in the working copies (the buttons are plain
// BS_OWNERDRAW, so nothing auto-toggles — the WM_COMMAND handlers flip the state and repaint).
static bool dlgBtnChecked(int id) {
    switch (id) {
        case PID_USECOLORS: return g_pUse;
        case PID_DOSPAL:    return g_pDos;
        case PID_TEXT:      return g_pTarget == 0;
        case PID_BG:        return g_pTarget == 1;
    }
    return false;
}
static void drawDlgButton(LPDRAWITEMSTRUCT d) {
    if (!d || d->CtlType != ODT_BUTTON) return;
    HDC dc = d->hDC; RECT rc = d->rcItem;
    int id = (int)d->CtlID;
    bool check = (id == PID_USECOLORS || id == PID_DOSPAL);
    bool radio = (id == PID_TEXT || id == PID_BG);
    bool push  = !check && !radio;
    bool sel   = (d->itemState & ODS_SELECTED) != 0;
    bool focus = (d->itemState & ODS_FOCUS) != 0;
    wchar_t txt[128]{};
    GetWindowTextW(d->hwndItem, txt, 128);
    HFONT of = g_uiFont ? (HFONT)SelectObject(dc, g_uiFont) : nullptr;
    SetBkMode(dc, TRANSPARENT);
    if (g_th.classic) {   // DrawFrameControl IS the classic renderer — pixel-faithful
        if (push) {
            DrawFrameControl(dc, &rc, DFC_BUTTON, DFCS_BUTTONPUSH | (sel ? DFCS_PUSHED : 0));
            RECT tr = rc; if (sel) OffsetRect(&tr, 1, 1);
            SetTextColor(dc, GetSysColor(COLOR_BTNTEXT));
            DrawTextW(dc, txt, -1, &tr, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
            if (focus) { RECT fr = rc; InflateRect(&fr, -4, -4); DrawFocusRect(dc, &fr); }
        } else {
            HBRUSH bg = (HBRUSH)(COLOR_BTNFACE + 1);
            FillRect(dc, &rc, bg);
            RECT gl{ rc.left, (rc.top + rc.bottom) / 2 - 7, rc.left + 13, (rc.top + rc.bottom) / 2 + 6 };
            DrawFrameControl(dc, &gl, DFC_BUTTON,
                             (radio ? DFCS_BUTTONRADIO : DFCS_BUTTONCHECK) | (dlgBtnChecked(id) ? DFCS_CHECKED : 0));
            RECT tr{ rc.left + 18, rc.top, rc.right, rc.bottom };
            SetTextColor(dc, GetSysColor(COLOR_BTNTEXT));
            DrawTextW(dc, txt, -1, &tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
            if (focus) { RECT fr = tr; fr.bottom = fr.top + (rc.bottom - rc.top); DrawFocusRect(dc, &fr); }
        }
    } else {   // themed: flat fill + painted glyphs, dark and light alike
        FillRect(dc, &rc, g_thBrBar);
        SetTextColor(dc, g_th.text);
        if (push) {
            HBRUSH face = CreateSolidBrush(sel ? g_th.sel : g_th.hot);
            FillRect(dc, &rc, face); DeleteObject(face);
            HBRUSH fr = CreateSolidBrush(focus || sel ? g_th.accent : g_th.border);
            FrameRect(dc, &rc, fr); DeleteObject(fr);
            DrawTextW(dc, txt, -1, &rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        } else {
            int cy = (rc.top + rc.bottom) / 2;
            RECT gl{ rc.left, cy - 7, rc.left + 13, cy + 6 };
            HBRUSH bb = CreateSolidBrush(g_th.dim);
            if (radio) {   // circle + dot
                HPEN pen = CreatePen(PS_SOLID, 1, g_th.dim);
                HGDIOBJ op = SelectObject(dc, pen), ob = SelectObject(dc, GetStockObject(NULL_BRUSH));
                Ellipse(dc, gl.left, gl.top, gl.right, gl.bottom);
                SelectObject(dc, op); SelectObject(dc, ob); DeleteObject(pen);
                if (dlgBtnChecked(id)) {
                    HBRUSH dot = CreateSolidBrush(g_th.accent);
                    HGDIOBJ od = SelectObject(dc, dot); HPEN np = CreatePen(PS_SOLID, 1, g_th.accent);
                    HGDIOBJ onp = SelectObject(dc, np);
                    Ellipse(dc, gl.left + 4, gl.top + 4, gl.right - 4, gl.bottom - 4);
                    SelectObject(dc, od); SelectObject(dc, onp); DeleteObject(dot); DeleteObject(np);
                }
            } else {       // box + check mark
                FrameRect(dc, &gl, bb);
                if (dlgBtnChecked(id)) {
                    HPEN pen = CreatePen(PS_SOLID, 2, g_th.accent);
                    HGDIOBJ op = SelectObject(dc, pen);
                    MoveToEx(dc, gl.left + 3, gl.top + 6, nullptr);
                    LineTo(dc, gl.left + 5, gl.top + 9);
                    LineTo(dc, gl.left + 10, gl.top + 3);
                    SelectObject(dc, op); DeleteObject(pen);
                }
            }
            DeleteObject(bb);
            RECT tr{ rc.left + 18, rc.top, rc.right, rc.bottom };
            DrawTextW(dc, txt, -1, &tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
            if (focus) { HBRUSH fr = CreateSolidBrush(g_th.border); RECT fb = rc; FrameRect(dc, &fb, fr); DeleteObject(fr); }
        }
    }
    if (of) SelectObject(dc, of);
}

// v5 combo boxes draw a classic light face + arrow that no theme can reach; themed looks take over
// the whole closed-field paint. The dropdown list is already dark via WM_CTLCOLORLISTBOX.
static LRESULT CALLBACK comboProc(HWND h, UINT m, WPARAM w, LPARAM l, UINT_PTR id, DWORD_PTR) {
    if (m == WM_NCDESTROY) RemoveWindowSubclass(h, comboProc, id);
    if (m == WM_NCPAINT && g_th.dark && !g_th.classic) {   // the WS_BORDER ring outside our paint
        LRESULT r = DefSubclassProc(h, m, w, l);
        if (HDC dc = GetWindowDC(h)) {
            RECT wr; GetWindowRect(h, &wr);
            RECT rc{ 0, 0, wr.right - wr.left, wr.bottom - wr.top };
            HBRUSH br = CreateSolidBrush(g_th.border);
            FrameRect(dc, &rc, br); DeleteObject(br);
            ReleaseDC(h, dc);
        }
        return r;
    }
    if (!g_th.classic && (m == WM_PAINT || m == WM_ERASEBKGND)) {
        if (m == WM_ERASEBKGND) return 1;
        PAINTSTRUCT ps;
        HDC dc = BeginPaint(h, &ps);
        RECT rc; GetClientRect(h, &rc);
        bool dropped = SendMessageW(h, CB_GETDROPPEDSTATE, 0, 0) != 0;
        bool focus = GetFocus() == h;
        FillRect(dc, &rc, g_thBrClient);
        HBRUSH fr = CreateSolidBrush((dropped || focus) ? g_th.accent : g_th.border);
        FrameRect(dc, &rc, fr); DeleteObject(fr);
        int sel = (int)SendMessageW(h, CB_GETCURSEL, 0, 0);
        wchar_t txt[128]{};
        if (sel >= 0) SendMessageW(h, CB_GETLBTEXT, sel, (LPARAM)txt);
        HFONT of = g_uiFont ? (HFONT)SelectObject(dc, g_uiFont) : nullptr;
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, IsWindowEnabled(h) ? g_th.text : g_th.dim);
        RECT tr{ rc.left + 6, rc.top, rc.right - 20, rc.bottom };
        DrawTextW(dc, txt, -1, &tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
        int cx = rc.right - 11, cy = (rc.top + rc.bottom) / 2 - 1;   // ▼
        POINT a[3] = { { cx - 4, cy - 1 }, { cx + 4, cy - 1 }, { cx, cy + 3 } };
        HBRUSH ab = CreateSolidBrush(g_th.text); HPEN ap = CreatePen(PS_SOLID, 1, g_th.text);
        HGDIOBJ ob = SelectObject(dc, ab), op = SelectObject(dc, ap);
        Polygon(dc, a, 3);
        SelectObject(dc, ob); SelectObject(dc, op); DeleteObject(ab); DeleteObject(ap);
        if (of) SelectObject(dc, of);
        EndPaint(h, &ps);
        return 0;
    }
    return DefSubclassProc(h, m, w, l);
}

static HFONT makePreviewFontSel() {
    if (g_pFace < 0 || g_pFace >= (int)g_catalog.size()) return nullptr;
    FontEntry& e = g_catalog[g_pFace];
    int si = (g_pSize >= 0 && g_pSize < (int)e.sizes.size()) ? g_pSize : 0;
    return createFontSpec(e, e.sizes[si], false, false);
}
static void refreshPreview(HWND h) {
    if (g_pPrev) DeleteObject(g_pPrev);
    g_pPrev = makePreviewFontSel();
    InvalidateRect(h, nullptr, TRUE);
}
static void fillSizeCombo(int sel) {   // sizes for the current face; disabled if the face has only one
    SendMessageW(g_pSizeCombo, CB_RESETCONTENT, 0, 0);
    if (g_pFace < 0 || g_pFace >= (int)g_catalog.size()) return;
    FontEntry& e = g_catalog[g_pFace];
    for (auto& s : e.sizes) SendMessageW(g_pSizeCombo, CB_ADDSTRING, 0, (LPARAM)s.label);
    SendMessageW(g_pSizeCombo, CB_SETCURSEL, (sel >= 0 && sel < (int)e.sizes.size()) ? sel : 0, 0);
    EnableWindow(g_pSizeCombo, e.sizes.size() > 1);
}
static void propCommit() {
    pickFont(g_pFace, g_pSize);   // applies the font, persists face+size
    g_customColors = g_pUse; g_defFg = g_pFg; g_defBg = g_pBg; g_dosPalette = g_pDos;
    if (g_pTheme != g_themeMode) {   // theme switch: re-skin everything live, incl. this open dialog
        g_themeMode = g_pTheme;
        applyTheme();
        themeDialog(g_pHwnd);
    }
    saveColors();
    InvalidateRect(g_hwnd, nullptr, TRUE);
}
static LRESULT CALLBACK propDlgProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    { LRESULT tr; if (themeDlgMsg(h, m, w, &tr)) return tr; }   // dark background + control colours
    if (m == WM_DRAWITEM) { drawDlgButton((LPDRAWITEMSTRUCT)l); return TRUE; }
    if (m == DM_GETDEFID) return MAKELRESULT(IDOK, DC_HASDEFID);   // Enter = OK (owner-draw lost BS_DEFPUSHBUTTON)
    switch (m) {
        case WM_COMMAND:
            switch (LOWORD(w)) {
                case PID_THEME:
                    if (HIWORD(w) == CBN_SELCHANGE) g_pTheme = (int)SendMessageW((HWND)l, CB_GETCURSEL, 0, 0);
                    break;
                case PID_FONTLIST:
                    if (HIWORD(w) == LBN_SELCHANGE) {
                        g_pFace = (int)SendMessageW((HWND)l, LB_GETCURSEL, 0, 0);
                        g_pSize = 0; fillSizeCombo(0);   // new face -> repopulate sizes, default first
                        refreshPreview(h);
                    }
                    break;
                case PID_SIZECOMBO:
                    if (HIWORD(w) == CBN_SELCHANGE) { g_pSize = (int)SendMessageW((HWND)l, CB_GETCURSEL, 0, 0); refreshPreview(h); }
                    break;
                // Owner-drawn buttons don't auto-toggle: flip the working state and repaint them.
                case PID_USECOLORS: g_pUse = !g_pUse; InvalidateRect((HWND)l, nullptr, TRUE); InvalidateRect(h, nullptr, TRUE); break;
                case PID_DOSPAL: g_pDos = !g_pDos; InvalidateRect((HWND)l, nullptr, TRUE); break;
                case PID_TEXT: case PID_BG:
                    g_pTarget = (LOWORD(w) == PID_BG) ? 1 : 0;
                    InvalidateRect(GetDlgItem(h, PID_TEXT), nullptr, TRUE);
                    InvalidateRect(GetDlgItem(h, PID_BG), nullptr, TRUE);
                    InvalidateRect(h, nullptr, TRUE);
                    break;
                case PID_APPLY: propCommit(); break;
                case IDOK: propCommit(); DestroyWindow(h); break;
                case IDCANCEL: DestroyWindow(h); break;
            }
            return 0;
        case WM_LBUTTONDOWN: {
            int mx = GET_X_LPARAM(l), my = GET_Y_LPARAM(l);
            if (my >= SW_Y && my < SW_Y + SW) {
                int i = (mx - SW_X0) / SW_GAP;
                if (i >= 0 && i < 16 && mx >= SW_X0 + i * SW_GAP && mx < SW_X0 + i * SW_GAP + SW) {
                    COLORREF cr = kConsolePalette[i];
                    uint32_t packed = (GetRValue(cr) << 16) | (GetGValue(cr) << 8) | GetBValue(cr);
                    if (g_pTarget == 0) g_pFg = packed; else g_pBg = packed;
                    if (!g_pUse) { g_pUse = true; CheckDlgButton(h, PID_USECOLORS, BST_CHECKED); }
                    InvalidateRect(h, nullptr, TRUE);
                }
            }
            return 0;
        }
        case WM_PAINT: {
            PAINTSTRUCT ps; HDC dc = BeginPaint(h, &ps);
            HGDIOBJ uiOld = SelectObject(dc, g_uiFont ? g_uiFont : (HFONT)GetStockObject(DEFAULT_GUI_FONT));   // never the System bitmap default
            SetTextColor(dc, g_th.classic ? GetSysColor(COLOR_BTNTEXT) : g_th.text);   // labels follow the theme
            // Colour swatches
            for (int i = 0; i < 16; i++) {
                RECT s{ SW_X0 + i * SW_GAP, SW_Y, SW_X0 + i * SW_GAP + SW, SW_Y + SW };
                HBRUSH b = CreateSolidBrush(kConsolePalette[i]); FillRect(dc, &s, b); DeleteObject(b);
                FrameRect(dc, &s, (HBRUSH)GetStockObject(BLACK_BRUSH));
            }
            // Selected text/bg colour chips
            auto chip = [&](int x, const wchar_t* lbl, uint32_t packed) {
                RECT lr{ x, SW_Y + 30, x + 90, SW_Y + 46 };
                SetBkMode(dc, TRANSPARENT); DrawTextW(dc, lbl, -1, &lr, DT_LEFT | DT_SINGLELINE);
                RECT cr{ x + 92, SW_Y + 28, x + 118, SW_Y + 48 };
                HBRUSH b = CreateSolidBrush(RGB((packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF));
                FillRect(dc, &cr, b); DeleteObject(b); FrameRect(dc, &cr, (HBRUSH)GetStockObject(BLACK_BRUSH));
            };
            chip(16, g_pTarget == 0 ? L"\x25B6 Text" : L"Text", g_pFg);
            chip(150, g_pTarget == 1 ? L"\x25B6 Background" : L"Background", g_pBg);
            // Live preview: sample terminal text in the working font + colours
            RECT pv{ 16, SW_Y + 60, 372, SW_Y + 170 };
            HBRUSH pb = CreateSolidBrush(RGB((g_pBg >> 16) & 0xFF, (g_pBg >> 8) & 0xFF, g_pBg & 0xFF));
            FillRect(dc, &pv, pb); DeleteObject(pb);
            FrameRect(dc, &pv, (HBRUSH)GetStockObject(GRAY_BRUSH));
            HGDIOBJ of = SelectObject(dc, g_pPrev ? g_pPrev : (HFONT)GetStockObject(OEM_FIXED_FONT));
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, RGB((g_pFg >> 16) & 0xFF, (g_pFg >> 8) & 0xFF, g_pFg & 0xFF));
            TextOutW(dc, pv.left + 6, pv.top + 6,  L"C:\\> dir", 8);
            TextOutW(dc, pv.left + 6, pv.top + 24, L"Volume in drive C is SYSTEM", 27);
            TextOutW(dc, pv.left + 6, pv.top + 42, L"abcdefghij 0123456789 +-*/=", 27);
            SelectObject(dc, of);
            SelectObject(dc, uiOld);
            EndPaint(h, &ps);
            return 0;
        }
        case WM_CLOSE: DestroyWindow(h); return 0;
        case WM_DESTROY: if (g_pPrev) { DeleteObject(g_pPrev); g_pPrev = nullptr; } return 0;
    }
    return DefWindowProcW(h, m, w, l);
}
static void showPropertiesDialog() {
    static bool reg = false;
    HINSTANCE inst = GetModuleHandleW(nullptr);
    if (!reg) {
        WNDCLASSW wc{};
        wc.lpfnWndProc = propDlgProc; wc.hInstance = inst; wc.lpszClassName = L"AgwintermLiteProps";
        wc.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1); wc.hCursor = LoadCursorW(nullptr, (LPCWSTR)IDC_ARROW);
        RegisterClassW(&wc); reg = true;
    }
    // Seed working state from the live settings.
    g_pFace = g_faceIdx; g_pSize = g_sizeIdx; g_pFg = g_defFg; g_pBg = g_defBg; g_pUse = g_customColors; g_pDos = g_dosPalette; g_pTarget = 0;
    g_pTheme = g_themeMode;
    if (g_pPrev) DeleteObject(g_pPrev);
    g_pPrev = makePreviewFontSel();
    const int W = 396, H = 490;   // grew for the Theme row (was 452)
    RECT pw; GetWindowRect(g_hwnd, &pw);
    g_pHwnd = CreateWindowExW(WS_EX_DLGMODALFRAME, L"AgwintermLiteProps", L"agwinterm lite — Properties",
                              WS_POPUP | WS_CAPTION | WS_SYSMENU | WS_CLIPCHILDREN,   // erase-on-repaint won't flicker the controls
                              pw.left + 60, pw.top + 40, W, H, g_hwnd, nullptr, inst, nullptr);
    HFONT gui = g_uiFont ? g_uiFont : (HFONT)GetStockObject(DEFAULT_GUI_FONT);
    auto mk = [&](const wchar_t* cls, const wchar_t* txt, DWORD st, int x, int y, int w, int hh, int id) {
        HWND c = CreateWindowExW(0, cls, txt, WS_CHILD | WS_VISIBLE | st, x, y, w, hh, g_pHwnd, (HMENU)(INT_PTR)id, inst, nullptr);
        SendMessageW(c, WM_SETFONT, (WPARAM)gui, TRUE); return c;
    };
    mk(L"STATIC", L"Font:", 0, 16, 12, 120, 16, 0);
    HWND fl = mk(L"LISTBOX", L"", WS_BORDER | WS_VSCROLL | LBS_NOTIFY, 16, 30, 200, 96, PID_FONTLIST);
    SetWindowSubclass(fl, fieldRingProc, 1, 0);   // dark bezel
    for (const auto& e : g_catalog) SendMessageW(fl, LB_ADDSTRING, 0, (LPARAM)e.label);
    SendMessageW(fl, LB_SETCURSEL, g_pFace, 0);
    mk(L"STATIC", L"Size:", 0, 228, 12, 120, 16, 0);
    g_pSizeCombo = mk(L"COMBOBOX", L"", WS_BORDER | WS_VSCROLL | CBS_DROPDOWNLIST, 228, 30, 140, 240, PID_SIZECOMBO);
    fillSizeCombo(g_pSize);
    // All buttons are BS_OWNERDRAW (drawDlgButton): the v5 classic controls can't be themed, and in
    // Classic mode DrawFrameControl reproduces the stock look exactly. State lives in g_pUse etc.
    mk(L"BUTTON", L"Override default colors", BS_OWNERDRAW, 16, 134, 172, 18, PID_USECOLORS);
    mk(L"BUTTON", L"MS-DOS palette (EGA)", BS_OWNERDRAW, 194, 134, 180, 18, PID_DOSPAL);
    mk(L"BUTTON", L"Screen &Text", WS_GROUP | BS_OWNERDRAW, 28, 158, 110, 18, PID_TEXT);
    mk(L"BUTTON", L"Screen &Background", BS_OWNERDRAW, 150, 158, 150, 18, PID_BG);
    mk(L"STATIC", L"Theme:", 0, 16, 372, 56, 16, 0);
    HWND th = mk(L"COMBOBOX", L"", WS_BORDER | WS_VSCROLL | CBS_DROPDOWNLIST, 76, 368, 180, 140, PID_THEME);
    for (const wchar_t* n : kThemeNames) SendMessageW(th, CB_ADDSTRING, 0, (LPARAM)n);
    SendMessageW(th, CB_SETCURSEL, g_pTheme, 0);
    SetWindowSubclass(th, comboProc, 1, 0);              // themed closed-field paint (v5 combo)
    SetWindowSubclass(g_pSizeCombo, comboProc, 1, 0);
    mk(L"BUTTON", L"OK", BS_OWNERDRAW, 120, 408, 78, 26, IDOK);
    mk(L"BUTTON", L"Cancel", BS_OWNERDRAW, 204, 408, 78, 26, IDCANCEL);
    mk(L"BUTTON", L"Apply", BS_OWNERDRAW, 288, 408, 78, 26, PID_APPLY);
    themeDialog(g_pHwnd);   // dark title bar + DarkMode styles when the dark theme is active
    EnableWindow(g_hwnd, FALSE);
    ShowWindow(g_pHwnd, SW_SHOW);
    MSG msg;
    while (IsWindow(g_pHwnd)) {
        if (!GetMessageW(&msg, nullptr, 0, 0)) { PostQuitMessage((int)msg.wParam); break; }
        if (!IsDialogMessageW(g_pHwnd, &msg)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    }
    EnableWindow(g_hwnd, TRUE);
    SetForegroundWindow(g_hwnd);
    SetFocus(g_hwnd);
}

// ---- Keyboard bindings dialog (native hotkey controls; all unbound by default) ----
static HWND g_kbHwnd, g_kbCtl[KB_COUNT];
enum { KBID_CLEAR = 4001, KBID_BASE = 4100 };   // KBID_BASE + action = that action's hotkey control
static LRESULT CALLBACK kbDlgProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    { LRESULT tr; if (themeDlgMsg(h, m, w, &tr)) return tr; }   // dark background + control colours
    if (m == WM_DRAWITEM) { drawDlgButton((LPDRAWITEMSTRUCT)l); return TRUE; }
    if (m == DM_GETDEFID) return MAKELRESULT(IDOK, DC_HASDEFID);
    switch (m) {
        case WM_COMMAND:
            switch (LOWORD(w)) {
                case KBID_CLEAR: for (int a = 0; a < KB_COUNT; a++) SendMessageW(g_kbCtl[a], HKM_SETHOTKEY, 0, 0); return 0;
                case IDOK:
                    for (int a = 0; a < KB_COUNT; a++) g_keys[a] = (WORD)SendMessageW(g_kbCtl[a], HKM_GETHOTKEY, 0, 0);
                    saveKeys(); DestroyWindow(h); return 0;
                case IDCANCEL: DestroyWindow(h); return 0;
            }
            return 0;
        case WM_CLOSE: DestroyWindow(h); return 0;
    }
    return DefWindowProcW(h, m, w, l);
}
static void showKeyboardDialog() {
    static bool reg = false;
    HINSTANCE inst = GetModuleHandleW(nullptr);
    if (!reg) {
        WNDCLASSW wc{};
        wc.lpfnWndProc = kbDlgProc; wc.hInstance = inst; wc.lpszClassName = L"AgwintermLiteKeys";
        wc.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1); wc.hCursor = LoadCursorW(nullptr, (LPCWSTR)IDC_ARROW);
        RegisterClassW(&wc); reg = true;
    }
    const int W = 360, H = 96 + KB_COUNT * 26 + 56;
    RECT pw; GetWindowRect(g_hwnd, &pw);
    g_kbHwnd = CreateWindowExW(WS_EX_DLGMODALFRAME, L"AgwintermLiteKeys", L"agwinterm lite — Keyboard",
                               WS_POPUP | WS_CAPTION | WS_SYSMENU, pw.left + 60, pw.top + 40, W, H, g_hwnd, nullptr, inst, nullptr);
    HFONT gui = g_uiFont ? g_uiFont : (HFONT)GetStockObject(DEFAULT_GUI_FONT);
    auto mk = [&](const wchar_t* cls, const wchar_t* txt, DWORD st, int x, int y, int ww, int hh, int id) {
        HWND c = CreateWindowExW(0, cls, txt, WS_CHILD | WS_VISIBLE | st, x, y, ww, hh, g_kbHwnd, (HMENU)(INT_PTR)id, inst, nullptr);
        SendMessageW(c, WM_SETFONT, (WPARAM)gui, TRUE); return c;
    };
    mk(L"STATIC", L"Assign a shortcut to each action (Backspace clears; unset = passed to the shell):",
       0, 16, 10, W - 40, 30, 0);
    for (int a = 0; a < KB_COUNT; a++) {
        int y = 50 + a * 26;
        mk(L"STATIC", kKbInfo[a].label, SS_CENTERIMAGE, 16, y, 150, 22, 0);
        g_kbCtl[a] = mk(HOTKEY_CLASSW, L"", WS_BORDER, 176, y, 160, 22, KBID_BASE + a);
        SetWindowSubclass(g_kbCtl[a], hotkeyProc, 1, 0);   // dark-theme paint takeover
        SendMessageW(g_kbCtl[a], HKM_SETRULES, HKCOMB_NONE, MAKEWORD(HOTKEYF_CONTROL, 0));   // bare key -> add Ctrl
        SendMessageW(g_kbCtl[a], HKM_SETHOTKEY, g_keys[a], 0);
    }
    int by = 50 + KB_COUNT * 26 + 8;
    mk(L"BUTTON", L"Clear all", BS_OWNERDRAW, 16, by, 90, 26, KBID_CLEAR);
    mk(L"BUTTON", L"OK", BS_OWNERDRAW, W - 190, by, 82, 26, IDOK);
    mk(L"BUTTON", L"Cancel", BS_OWNERDRAW, W - 100, by, 82, 26, IDCANCEL);
    themeDialog(g_kbHwnd);
    EnableWindow(g_hwnd, FALSE);
    ShowWindow(g_kbHwnd, SW_SHOW);
    MSG msg;
    while (IsWindow(g_kbHwnd)) {
        if (!GetMessageW(&msg, nullptr, 0, 0)) { PostQuitMessage((int)msg.wParam); break; }
        if (!IsDialogMessageW(g_kbHwnd, &msg)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    }
    EnableWindow(g_hwnd, TRUE);
    SetForegroundWindow(g_hwnd);
    SetFocus(g_hwnd);
}

// Restart everything: relaunch a fresh instance AFTER this one (and its pty-host) has fully exited,
// then quit. The ~1s ping delay avoids the new instance connecting to the dying pty-host.
static void restartApp() {
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring cmd = L"cmd.exe /c ping -n 2 127.0.0.1 >nul & start \"\" \"" + std::wstring(exe) + L"\"";
    STARTUPINFOW si{ sizeof si };
    PROCESS_INFORMATION pi{};
    if (CreateProcessW(nullptr, &cmd[0], nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
    }
    DestroyWindow(g_hwnd);   // WM_DESTROY kills sessions + shuts down the pty-host
}

static void showMainWindow() {
    ShowWindow(g_hwnd, SW_SHOW);
    if (IsIconic(g_hwnd)) ShowWindow(g_hwnd, SW_RESTORE);
    SetForegroundWindow(g_hwnd);
}

static void showTrayMenu() {
    POINT pt; GetCursorPos(&pt);
    HMENU m = CreatePopupMenu();
    AppendMenuW(m, MF_STRING, IDM_SHOW, L"&Show agwinterm lite");
    AppendMenuW(m, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(m, MF_STRING, IDM_NEW, L"&New Session…");
    AppendMenuW(m, MF_STRING, IDM_RESTART, L"&Restart");
    AppendMenuW(m, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(m, MF_STRING, IDM_EXIT, L"E&xit");
    SetForegroundWindow(g_hwnd);   // Win32 quirk: needed so the menu dismisses on click-away
    TrackPopupMenu(m, TPM_RIGHTBUTTON, pt.x, pt.y, 0, g_hwnd, nullptr);   // posts WM_COMMAND
    DestroyMenu(m);
}

// A 2000's-style app icon drawn at runtime (no .ico asset, no deps): a classic gray 3D-beveled tile
// with a sunken black "screen" and a green >_ prompt — the Win2000/XP-era terminal look.
// Toolbar icons are the full app's vector glyphs drawn in GDI at runtime (no PNG assets, no GDI+).
// Toolbar icons: exactly what the full app shows. Four of its buttons ARE Segoe Fluent Icons font
// glyphs (hamburger/add/terminal/gear) — we render the same codepoints with the icon font. The other
// four are its custom D2D vector glyphs (card+plus, split panes, scratch pad, recents clock) — we
// redraw those 4x supersampled with round-capped pens and HALFTONE-downscale, so the stroke weight
// (~1.5px) and smoothness match the font glyphs.
static const wchar_t kTbFontGlyph[kTbImgCount] = {
    0xE700,   // 0 sidebar  — GlobalNavButton (same as the full app)
    0xE710,   // 1 new sess — Add
    0,        // 2 new ws   — custom (card + plus)
    0,        // 3 split    — custom (two panes)
    0,        // 4 scratch  — custom (pad)
    0xE756,   // 5 quick    — CommandPrompt
    0,        // 6 reopen   — custom (recents clock)
    0xE713,   // 7 settings — Settings gear
    0,        // 8 flag     — custom (pennant, DrawFlagGlyph)
    0,        // 9 bell     — custom (DrawBellGlyph)
    0,        // 10 bell    — same shape, alert colour (any session blocked)
};
// Custom glyphs drawn on a 64x64 canvas (scaled 4x from the full app's 16px-cell geometry).
static void drawToolbarGlyph4x(HDC dc, int idx, COLORREF c) {
    LOGBRUSH lb{ BS_SOLID, c, 0 };
    HPEN pen = ExtCreatePen(PS_GEOMETRIC | PS_SOLID | PS_ENDCAP_ROUND | PS_JOIN_ROUND, 6, &lb, 0, nullptr);
    HGDIOBJ op = SelectObject(dc, pen), ob = SelectObject(dc, GetStockObject(NULL_BRUSH));
    auto ln = [&](int x0, int y0, int x1, int y1) { MoveToEx(dc, x0, y0, nullptr); LineTo(dc, x1, y1); };
    switch (idx) {
        case 2:   // new workspace: card upper-left + plus lower-right (DrawNewWorkspaceGlyph)
            RoundRect(dc, 8, 8, 46, 46, 14, 14);
            ln(50, 26, 50, 50); ln(38, 38, 62, 38);
            break;
        case 3:   // split: two panes, centre divider (DrawSplitGlyph)
            RoundRect(dc, 4, 12, 60, 52, 14, 14);
            ln(32, 12, 32, 51);
            break;
        case 4:   // scratch: rounded pad (DrawScratchGlyph)
            RoundRect(dc, 4, 12, 60, 52, 18, 18);
            break;
        case 6:   // reopen closed: recents clock (DrawClockGlyph)
            Ellipse(dc, 6, 6, 58, 58);
            ln(32, 32, 32, 15); ln(32, 32, 43, 39);
            break;
        case 8: { // flagged view: pennant (DrawFlagGlyph)
            ln(22, 8, 22, 56);
            POINT p[3] = { { 22, 10 }, { 50, 19 }, { 22, 28 } };
            Polygon(dc, p, 3);
            break;
        }
        case 9:
        case 10:  // attention bell (DrawBellGlyph); 10 = alert-coloured build of the same shape
            Arc(dc, 16, 10, 48, 42, 48, 26, 16, 26);   // dome
            ln(16, 26, 13, 42); ln(48, 26, 51, 42);    // walls
            ln(9, 42, 55, 42);                          // rim
            Ellipse(dc, 28, 46, 36, 53);                // clapper
            break;
    }
    SelectObject(dc, op); SelectObject(dc, ob);
    DeleteObject(pen);
}
static void buildToolbarImages() {
    // Rebuilt on every theme switch: icons are composed onto the bar colour in the theme's text
    // colour (no alpha image lists without a v6 manifest, and none needed).
    if (g_tbImages) ImageList_Destroy(g_tbImages);
    g_tbImages = ImageList_Create(16, 16, ILC_COLOR24, kTbImgCount, 0);
    COLORREF bg = g_th.classic ? GetSysColor(COLOR_BTNFACE) : g_th.bar;
    COLORREF fg = g_th.classic ? GetSysColor(COLOR_BTNTEXT) : g_th.text;
    // The icon font: Segoe Fluent Icons on Win11; Segoe MDL2 Assets carries the same codepoints on
    // Win10. ANTIALIASED (grayscale) rather than ClearType: no subpixel fringing on the bar colour.
    HFONT icoFont = CreateFontW(-15, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET,
                                OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
                                DEFAULT_PITCH, L"Segoe Fluent Icons");
    HDC sdc = GetDC(nullptr);
    for (int i = 0; i < kTbImgCount; i++) {
        COLORREF gfg = (i == 10) ? RGB(230, 150, 50) : fg;   // the alert bell is amber in every theme
        HDC mem = CreateCompatibleDC(sdc);
        HBITMAP bm = CreateCompatibleBitmap(sdc, 16, 16);
        HGDIOBJ obm = SelectObject(mem, bm);
        RECT r{ 0, 0, 16, 16 };
        HBRUSH bb = CreateSolidBrush(bg);
        FillRect(mem, &r, bb);
        if (kTbFontGlyph[i]) {   // authentic Fluent glyph, centred
            HGDIOBJ of = SelectObject(mem, icoFont);
            wchar_t ch[2] = { kTbFontGlyph[i], 0 };
            // Fall back to MDL2 if Fluent isn't installed (pre-Win11): same codepoints there.
            wchar_t face[LF_FACESIZE] = L"";
            GetTextFaceW(mem, LF_FACESIZE, face);
            if (lstrcmpiW(face, L"Segoe Fluent Icons") != 0) {
                static HFONT mdl2 = CreateFontW(-15, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET,
                                                OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
                                                DEFAULT_PITCH, L"Segoe MDL2 Assets");
                SelectObject(mem, mdl2);
            }
            SetBkMode(mem, TRANSPARENT);
            SetTextColor(mem, gfg);
            SIZE sz{};
            GetTextExtentPoint32W(mem, ch, 1, &sz);
            TextOutW(mem, (16 - sz.cx) / 2, (16 - sz.cy) / 2, ch, 1);
            SelectObject(mem, of);
        } else {                 // custom vector glyph: 4x supersample -> HALFTONE downscale
            HDC big = CreateCompatibleDC(sdc);
            HBITMAP bigBm = CreateCompatibleBitmap(sdc, 64, 64);
            HGDIOBJ obig = SelectObject(big, bigBm);
            RECT br{ 0, 0, 64, 64 };
            FillRect(big, &br, bb);
            drawToolbarGlyph4x(big, i, gfg);
            SetStretchBltMode(mem, HALFTONE);
            SetBrushOrgEx(mem, 0, 0, nullptr);
            StretchBlt(mem, 0, 0, 16, 16, big, 0, 0, 64, 64, SRCCOPY);
            SelectObject(big, obig); DeleteObject(bigBm); DeleteDC(big);
        }
        DeleteObject(bb);
        SelectObject(mem, obm); DeleteDC(mem);
        ImageList_Add(g_tbImages, bm, nullptr);
        DeleteObject(bm);
    }
    ReleaseDC(nullptr, sdc);
    DeleteObject(icoFont);
    if (g_toolbar) {
        SendMessageW(g_toolbar, TB_SETIMAGELIST, 0, (LPARAM)g_tbImages);
        InvalidateRect(g_toolbar, nullptr, TRUE);
    }
}
static HICON makeRetroIcon() {
    const int S = 32;
    HDC dc = GetDC(nullptr);
    HDC mem = CreateCompatibleDC(dc);
    HBITMAP color = CreateCompatibleBitmap(dc, S, S);
    HBITMAP mask = CreateBitmap(S, S, 1, 1, nullptr);
    HGDIOBJ ob = SelectObject(mem, color);
    RECT r{ 0, 0, S, S };
    HBRUSH gray = CreateSolidBrush(RGB(192, 192, 192));
    FillRect(mem, &r, gray);
    DeleteObject(gray);
    DrawEdge(mem, &r, EDGE_RAISED, BF_RECT);          // raised 3D outer bezel
    RECT scr{ 5, 5, S - 5, S - 5 };
    HBRUSH black = CreateSolidBrush(RGB(0, 0, 0));
    FillRect(mem, &scr, black);
    DeleteObject(black);
    DrawEdge(mem, &scr, EDGE_SUNKEN, BF_RECT);         // sunken 3D screen
    SetBkMode(mem, TRANSPARENT);
    SetTextColor(mem, RGB(0, 220, 0));
    HFONT f = CreateFontW(-11, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                          CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY, FIXED_PITCH | FF_MODERN, L"Consolas");
    HGDIOBJ of = SelectObject(mem, f);
    TextOutW(mem, 7, 9, L">_", 2);
    SelectObject(mem, of);
    DeleteObject(f);
    HDC memMask = CreateCompatibleDC(dc);
    HGDIOBJ omb = SelectObject(memMask, mask);
    PatBlt(memMask, 0, 0, S, S, BLACKNESS);            // all-0 AND-mask = fully opaque icon
    SelectObject(memMask, omb);
    DeleteDC(memMask);
    SelectObject(mem, ob);
    ICONINFO ii{ TRUE, 0, 0, mask, color };
    HICON icon = CreateIconIndirect(&ii);
    DeleteObject(color); DeleteObject(mask);
    DeleteDC(mem);
    ReleaseDC(nullptr, dc);
    return icon;
}

// ---- Quick / Scratch popup terminals ----
static void paintPopup(HWND h, Session* s) {
    PAINTSTRUCT ps; HDC dc = BeginPaint(h, &ps);
    RECT rc; GetClientRect(h, &rc);
    HDC mem = CreateCompatibleDC(dc);
    HBITMAP bmp = CreateCompatibleBitmap(dc, rc.right, rc.bottom);
    HGDIOBJ ob = SelectObject(mem, bmp);
    HBRUSH bg = CreateSolidBrush(g_customColors ? toColorRef(g_defBg, false) : RGB(0, 0, 0));
    FillRect(mem, &rc, bg); DeleteObject(bg);
    if (s) paintPane(mem, rc, s, -1, true);   // -1 pane = no selection span; cursor always shown
    BitBlt(dc, 0, 0, rc.right, rc.bottom, mem, 0, 0, SRCCOPY);
    SelectObject(mem, ob); DeleteObject(bmp); DeleteDC(mem);
    EndPaint(h, &ps);
}
static LRESULT CALLBACK popupProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    Session* s = (h == g_quickHwnd) ? g_quickSession : (h == g_scratchHwnd) ? g_scratchSession : g_overlaySession;
    switch (m) {
        case WM_SETFOCUS:  g_focusOverride = s; return 0;
        case WM_KILLFOCUS: if (g_focusOverride == s) g_focusOverride = nullptr; return 0;
        case WM_ERASEBKGND: return 1;
        case WM_PAINT: paintPopup(h, s); return 0;
        case WM_SIZE:
            if (s && w != SIZE_MINIMIZED) {
                RECT rc; GetClientRect(h, &rc);
                hostResize(s, max(1, (int)(rc.right / g_cw)), max(1, (int)(rc.bottom / g_ch)));
                InvalidateRect(h, nullptr, FALSE);
            }
            return 0;
        case WM_KEYDOWN:
        case WM_SYSKEYDOWN:
            g_focusOverride = s;
            g_swallowChar = handleKeyDown(w);
            InvalidateRect(h, nullptr, FALSE);
            if (g_swallowChar) return 0;
            break;
        case WM_CHAR: {
            g_focusOverride = s;
            if (g_swallowChar) { g_swallowChar = false; return 0; }
            wchar_t wc = (wchar_t)w;
            if (s) s->scrollOff = 0;
            if (wc == L'\r') sendBytes("\r", 1); else sendUtf8(wc);
            return 0;
        }
        case WM_MOUSEWHEEL:
            if (s) { s->scrollOff = max(0, s->scrollOff + (GET_WHEEL_DELTA_WPARAM(w) > 0 ? 3 : -3)); InvalidateRect(h, nullptr, FALSE); }
            return 0;
        case WM_SYSCOMMAND:
            if ((w & 0xFFF0) == SC_MINIMIZE) { ShowWindow(g_hwnd, SW_MINIMIZE); return 0; }   // minimize -> all windows
            break;
        case WM_CLOSE:
            // Overlay and SCRATCH are transient: closing tears the window AND its session down (a
            // scratch pad you closed is gone — reopening starts fresh). Quick hides and keeps its
            // session, that being the point of a quick terminal.
            if (h == g_overlayHwnd || h == g_scratchHwnd) { DestroyWindow(h); return 0; }
            ShowWindow(h, SW_HIDE); SetForegroundWindow(g_hwnd); return 0;
        case WM_DESTROY: {
            Session** slot = (h == g_overlayHwnd) ? &g_overlaySession
                           : (h == g_scratchHwnd) ? &g_scratchSession : nullptr;
            if (slot) {   // kill the transient window's session + clear its state
                if (g_focusOverride == *slot) g_focusOverride = nullptr;
                if (*slot)
                    for (int i = 0; i < (int)g_sessions.size(); i++)
                        if (g_sessions[i] == *slot) { closeSessionAt(i); break; }
                *slot = nullptr;
                if (h == g_overlayHwnd) g_overlayHwnd = nullptr; else g_scratchHwnd = nullptr;
                SetForegroundWindow(g_hwnd);
            }
            return 0;
        }
    }
    return DefWindowProcW(h, m, w, l);
}
static void ensurePopupClass() {
    static bool reg = false;
    if (reg) return;
    WNDCLASSW wc{};
    wc.lpfnWndProc = popupProc; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"AgwintermLitePopup";
    wc.hCursor = LoadCursorW(nullptr, (LPCWSTR)IDC_IBEAM); wc.hIcon = g_appIcon;
    RegisterClassW(&wc); reg = true;
}
// A popup terminal window sized wf x hf fractions of the main window, owned by it (floats above, hides
// when it minimizes, never behind it).
static HWND createPopupWindow(const wchar_t* title, double wf, double hf) {
    ensurePopupClass();
    RECT mw; GetWindowRect(g_hwnd, &mw);
    int W = max(30 * g_cw, (int)((mw.right - mw.left) * wf)), H = max(8 * g_ch, (int)((mw.bottom - mw.top) * hf));
    HWND h = CreateWindowExW(0, L"AgwintermLitePopup", title, WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                             mw.left + 80, mw.top + 60, W, H, g_hwnd, nullptr, GetModuleHandleW(nullptr), nullptr);
    darkTitleBar(h, g_th.dark);   // popup terminals follow the theme's title bar
    return h;
}
// Toggle a quick (scratch=false) or scratch (scratch=true) popup terminal: show/hide, creating its
// window + dedicated hidden session on first use.
static void togglePopupTerminal(bool scratch) {
    HWND& hw = scratch ? g_scratchHwnd : g_quickHwnd;
    Session*& sess = scratch ? g_scratchSession : g_quickSession;
    if (hw && IsWindowVisible(hw)) {
        // Dismissing: quick hides (its session is the point), scratch is torn down — a dismissed
        // scratch pad is gone, however you dismissed it (toggle key, X button, anything).
        if (scratch) DestroyWindow(hw);
        else ShowWindow(hw, SW_HIDE);
        SetForegroundWindow(g_hwnd);
        return;
    }
    if (!hw) {
        hw = createPopupWindow(scratch ? L"agwinterm lite — scratch" : L"agwinterm lite — quick", 0.66, 0.6);
        RECT rc; GetClientRect(hw, &rc);
        sess = newSession(max(1, (int)(rc.right / g_cw)), max(1, (int)(rc.bottom / g_ch)));   // windowForSession routes to hw (set above)
        if (sess) { sess->hidden = true; sess->name = scratch ? L"scratch" : L"quick"; }      // not in the sidebar / not persisted
    }
    ShowWindow(hw, SW_SHOW);
    SetForegroundWindow(hw);
    g_focusOverride = sess;
    InvalidateRect(hw, nullptr, FALSE);
}
// Overlay: run a command in a popup over the active session (control-API session.overlay). One at a
// time; opening a new overlay replaces the previous. An empty command opens a plain shell.
static void openOverlay(const std::string& command, int sizePct) {
    if (g_overlayHwnd) DestroyWindow(g_overlayHwnd);   // one at a time; WM_DESTROY kills the old session + clears state
    double f = sizePct > 0 ? min(0.95, sizePct / 100.0) : 0.7;
    g_overlayHwnd = createPopupWindow(L"agwinterm lite — overlay", f, f);
    RECT rc; GetClientRect(g_overlayHwnd, &rc);
    g_overlaySession = newSession(max(1, (int)(rc.right / g_cw)), max(1, (int)(rc.bottom / g_ch)),
                                  command.empty() ? nullptr : command.c_str());
    if (g_overlaySession) { g_overlaySession->hidden = true; g_overlaySession->name = L"overlay"; }
    ShowWindow(g_overlayHwnd, SW_SHOW);
    SetForegroundWindow(g_overlayHwnd);
    g_focusOverride = g_overlaySession;
    InvalidateRect(g_overlayHwnd, nullptr, FALSE);
}

// ---- sidebar drag & drop ----------------------------------------------------------------------
// Reorder g_sessions: take `from` out, put it into `targetWs`, inserted before `insertBefore`
// (session index in the PRE-move vector; -1 = append at the end). Panes hold indices, so they are
// remapped through the same removal+insertion the vector undergoes.
static void moveSessionTo(int from, int targetWs, int insertBefore) {
    int n = (int)g_sessions.size();
    if (from < 0 || from >= n || targetWs < 0 || targetWs >= (int)g_workspaces.size()) return;
    if (insertBefore == from) insertBefore = -1;   // dropping on yourself = just a workspace move
    EnterCriticalSection(&g_lock);
    Session* moved = g_sessions[from];
    g_sessions.erase(g_sessions.begin() + from);
    int ins = insertBefore < 0 ? (int)g_sessions.size()
            : (insertBefore > from ? insertBefore - 1 : insertBefore);   // index after the removal
    g_sessions.insert(g_sessions.begin() + ins, moved);
    moved->ws = targetWs;
    auto remap = [&](int idx) {
        if (idx < 0) return idx;
        if (idx == from) return ins;
        int t = idx > from ? idx - 1 : idx;   // removal shift
        return t >= ins ? t + 1 : t;          // insertion shift
    };
    g_pane[0] = remap(g_pane[0]);
    g_pane[1] = remap(g_pane[1]);
    LeaveCriticalSection(&g_lock);
    refreshTree();   // rebuild labels/lParams + persist the new order
    InvalidateRect(g_hwnd, nullptr, FALSE);
}
static void endTreeDrag(bool drop, POINT treePt) {   // treePt in TREE-client coords
    if (!g_treeDrag) return;
    g_treeDrag = false;   // FIRST: ReleaseCapture re-enters via WM_CAPTURECHANGED
    ImageList_DragLeave(g_tree);
    ImageList_EndDrag();
    if (g_dragImg) { ImageList_Destroy(g_dragImg); g_dragImg = nullptr; }
    ReleaseCapture();
    TreeView_SelectDropTarget(g_tree, nullptr);
    int from = g_dragIdx; g_dragIdx = -1;
    if (!drop || from < 0) return;
    TVHITTESTINFO ht{};
    ht.pt = treePt;
    HTREEITEM it = TreeView_HitTest(g_tree, &ht);
    if (!it) return;
    TVITEMW ti{}; ti.mask = TVIF_PARAM; ti.hItem = it;
    TreeView_GetItem(g_tree, &ti);
    if (ti.lParam >= 0 && ti.lParam < (LPARAM)g_sessions.size()) {          // onto a session: insert before it
        int tj = (int)ti.lParam;
        if (tj != from) moveSessionTo(from, g_sessions[tj]->ws, tj);
    } else {                                                                 // onto a workspace: append there
        int w = (int)(-ti.lParam - 1);
        if (w >= 0 && w < (int)g_workspaces.size()) moveSessionTo(from, w, -1);
    }
}
// Drag & drop lives in a tree subclass: comctl32's own TVN_BEGINDRAG detection proved unreliable
// with TVS_EDITLABELS in play, so the press/threshold/move/drop loop is ours — the same
// deterministic approach as the rest of this port. All coordinates are tree-client.
static LRESULT CALLBACK treeProc(HWND h, UINT m, WPARAM w, LPARAM l, UINT_PTR id, DWORD_PTR) {
    if (m == WM_NCDESTROY) RemoveWindowSubclass(h, treeProc, id);
    switch (m) {
        case WM_LBUTTONDOWN: {
            g_armIdx = -1;
            TVHITTESTINFO ht{};
            ht.pt = { GET_X_LPARAM(l), GET_Y_LPARAM(l) };
            HTREEITEM it = TreeView_HitTest(h, &ht);
            if (it && (ht.flags & (TVHT_ONITEM | TVHT_ONITEMRIGHT | TVHT_ONITEMINDENT))) {
                TVITEMW ti{}; ti.mask = TVIF_PARAM; ti.hItem = it;
                TreeView_GetItem(h, &ti);
                if (ti.lParam >= 0 && ti.lParam < (LPARAM)g_sessions.size()) {
                    g_armIdx = (int)ti.lParam; g_armPt = ht.pt; g_armItem = it;   // drag candidate
                }
            }
            break;   // default handling still selects the row
        }
        case WM_MOUSEMOVE: {
            POINT pt{ GET_X_LPARAM(l), GET_Y_LPARAM(l) };
            if (g_treeDrag) {   // live drag: move the ghost + highlight the drop target
                ImageList_DragShowNolock(FALSE);
                TVHITTESTINFO ht{}; ht.pt = pt;
                TreeView_SelectDropTarget(h, TreeView_HitTest(h, &ht));
                ImageList_DragShowNolock(TRUE);
                ImageList_DragMove(pt.x, pt.y);
                return 0;
            }
            if (g_armIdx >= 0 && (w & MK_LBUTTON) &&
                (abs(pt.x - g_armPt.x) > 4 || abs(pt.y - g_armPt.y) > 4)) {   // passed the drag threshold
                g_dragIdx = g_armIdx; g_armIdx = -1;
                g_dragImg = TreeView_CreateDragImage(h, g_armItem);
                if (g_dragImg) { ImageList_BeginDrag(g_dragImg, 0, 8, 8); ImageList_DragEnter(h, pt.x, pt.y); }
                g_treeDrag = true;
                SetCapture(h);
                return 0;
            }
            break;
        }
        case WM_LBUTTONUP:
            if (g_treeDrag) { endTreeDrag(true, { GET_X_LPARAM(l), GET_Y_LPARAM(l) }); return 0; }
            g_armIdx = -1;
            break;
        case WM_CAPTURECHANGED:
            if (g_treeDrag) endTreeDrag(false, { 0, 0 });   // something stole the mouse: cancel cleanly
            break;
        case WM_KEYDOWN:
            if (g_treeDrag && w == VK_ESCAPE) { endTreeDrag(false, { 0, 0 }); return 0; }
            break;
    }
    return DefSubclassProc(h, m, w, l);
}

// ---- flagged sessions + attention -------------------------------------------------------------
static bool anyBlocked() {
    for (auto* s : g_sessions)
        if (!s->hidden && !s->exited && statusClass(s->status) == AGST_BLOCKED) return true;
    return false;
}
// Jump to the next blocked session (cycling from the current one). The agent-terminal loop: the
// bell lights, you click it, you're on the session that needs you.
static void nextBlocked() {
    int n = (int)g_sessions.size();
    if (n == 0) return;
    int start = g_pane[0] >= 0 ? g_pane[0] : 0;
    for (int k = 1; k <= n; k++) {
        int i = (start + k) % n;
        Session* s = g_sessions[i];
        if (s->hidden || s->exited || statusClass(s->status) != AGST_BLOCKED) continue;
        g_pane[0] = i; g_focus = 0;
        g_activeWs = s->ws;
        if (g_focusWs >= 0) g_focusWs = s->ws;   // focus follows the jump (the row must be visible)
        syncPaneSizes();
        refreshTree();   // re-selects the tree row for the new pane-0 session
        InvalidateRect(g_hwnd, nullptr, FALSE);
        SetFocus(g_hwnd);
        return;
    }
    MessageBeep(MB_OK);   // nothing blocked right now
}
static void toggleFlag(Session* s) {
    if (!s || s->hidden) return;   // popup/split shells aren't tree sessions
    s->flagged = !s->flagged;
    refreshTree();                 // repaints the pennant + persists via saveSessionState
}
static void toggleFlagView() {
    g_flagView = !g_flagView;
    if (g_hwnd) CheckMenuItem(GetMenu(g_hwnd), IDM_FLAGVIEW, MF_BYCOMMAND | (g_flagView ? MF_CHECKED : MF_UNCHECKED));
    if (g_toolbar) SendMessageW(g_toolbar, TB_CHECKBUTTON, IDM_FLAGVIEW, MAKELPARAM(g_flagView, 0));
    refreshTree();
    saveColors();   // FlagView persists with the other view toggles
}
// Focus a workspace: the sidebar narrows to it (the full app's focus pill, lite-style — the toggle
// lives on the workspace's context menu and in View). Focusing again, or focusing -1, unfocuses.
static void toggleFocusWs(int w) {
    g_focusWs = (g_focusWs == w || w < 0 || w >= (int)g_workspaces.size()) ? -1 : w;
    if (g_focusWs >= 0) g_activeWs = g_focusWs;   // new sessions land in the focused workspace
    if (g_hwnd) CheckMenuItem(GetMenu(g_hwnd), IDM_FOCUSWS, MF_BYCOMMAND | (g_focusWs >= 0 ? MF_CHECKED : MF_UNCHECKED));
    refreshTree();   // re-filters + updates the status bar + persists (O record)
}

// ---- main frame (WTL) -------------------------------------------------------------------------
// CFrameWindowImpl gives the frame window traits, class registration and the message-map plumbing;
// the sidebar tree / toolbar / status bar are WTL control wrappers over the same native controls.
// Message crackers (MSG_WM_*) replace the old hand-rolled switch — the semantics are unchanged.
class CMainFrame : public CFrameWindowImpl<CMainFrame> {
public:
    DECLARE_FRAME_WND_CLASS_EX(L"AgwintermLite", 0, CS_DBLCLKS, COLOR_WINDOW)

    CTreeViewCtrl  m_tree;      // sidebar (sessions grouped by workspace)
    CToolBarCtrl   m_toolbar;   // New Session / New Workspace / Split
    CStatusBarCtrl m_status;    // workspace · count · grid · font

    BEGIN_MSG_MAP(CMainFrame)
        MSG_WM_PAINT(OnPaint)
        MSG_WM_ERASEBKGND(OnEraseBkgnd)
        MSG_WM_CHAR(OnChar)
        MSG_WM_MOUSEWHEEL(OnMouseWheel)
        MSG_WM_LBUTTONDOWN(OnLButtonDown)
        MSG_WM_MOUSEMOVE(OnMouseMove)
        MSG_WM_LBUTTONUP(OnLButtonUp)
        MSG_WM_RBUTTONDOWN(OnRButtonDown)
        MSG_WM_RBUTTONUP(OnRButtonUp)
        MSG_WM_SIZE(OnSize)
        MSG_WM_EXITSIZEMOVE(OnExitSizeMove)
        MSG_WM_SETTINGCHANGE(OnSettingChange)
        MSG_WM_DESTROY(OnDestroy)
        MESSAGE_HANDLER(WM_KEYDOWN, OnKey)
        MESSAGE_HANDLER(WM_SYSKEYDOWN, OnKey)
        MESSAGE_HANDLER(WM_SETCURSOR, OnSetCursor)
        MESSAGE_HANDLER(WM_APP_REFRESHTREE, OnRefreshTree)
        MESSAGE_HANDLER(WM_APP_TRAY, OnTray)
        MESSAGE_HANDLER(WM_APP_OVERLAY, OnOverlay)
        MESSAGE_HANDLER(WM_NOTIFY, OnNotify)
        MESSAGE_HANDLER(WM_COMMAND, OnCommand)
        MESSAGE_HANDLER(WM_UAHDRAWMENU, OnUahDrawMenu)
        MESSAGE_HANDLER(WM_UAHDRAWMENUITEM, OnUahDrawMenuItem)
        MESSAGE_HANDLER(WM_NCPAINT, OnNcPaintSeam)
        MESSAGE_HANDLER(WM_NCACTIVATE, OnNcPaintSeam)
        MESSAGE_HANDLER(WM_EXITMENULOOP, OnMenuSeamTouch)
        MESSAGE_HANDLER(WM_MENUSELECT, OnMenuSeamTouch)
        CHAIN_MSG_MAP(CFrameWindowImpl<CMainFrame>)
    END_MSG_MAP()

    // ---- dark menu bar ----
    LRESULT OnUahDrawMenu(UINT, WPARAM, LPARAM lp, BOOL& bHandled) {
        if (!g_th.dark) { bHandled = FALSE; return 0; }
        auto* um = (UAHMENU*)lp;
        MENUBARINFO mbi{ sizeof mbi };
        if (!GetMenuBarInfo(m_hWnd, OBJID_MENU, 0, &mbi)) { bHandled = FALSE; return 0; }
        RECT wr; GetWindowRect(&wr);
        RECT r = mbi.rcBar; OffsetRect(&r, -wr.left, -wr.top);
        HBRUSH b = CreateSolidBrush(g_th.bar);
        FillRect(um->hdc, &r, b);
        DeleteObject(b);
        return TRUE;
    }
    LRESULT OnUahDrawMenuItem(UINT, WPARAM, LPARAM lp, BOOL& bHandled) {
        if (!g_th.dark) { bHandled = FALSE; return 0; }
        auto* dmi = (UAHDRAWMENUITEM*)lp;
        wchar_t txt[256]{};
        MENUITEMINFOW mii{ sizeof mii, MIIM_STRING };
        mii.dwTypeData = txt; mii.cch = 255;
        GetMenuItemInfoW(dmi->um.hmenu, dmi->umi.iPosition, TRUE, &mii);
        DWORD st = dmi->dis.itemState;
        bool hot = (st & (ODS_HOTLIGHT | ODS_SELECTED)) != 0;
        HBRUSH b = CreateSolidBrush(hot ? g_th.hot : g_th.bar);
        FillRect(dmi->um.hdc, &dmi->dis.rcItem, b);
        DeleteObject(b);
        SetBkMode(dmi->um.hdc, TRANSPARENT);
        SetTextColor(dmi->um.hdc, (st & (ODS_GRAYED | ODS_DISABLED)) ? g_th.dim : g_th.text);
        HFONT of = g_uiFont ? (HFONT)SelectObject(dmi->um.hdc, g_uiFont) : nullptr;
        DrawTextW(dmi->um.hdc, txt, -1, &dmi->dis.rcItem,
                  DT_CENTER | DT_VCENTER | DT_SINGLELINE | ((st & ODS_NOACCEL) ? DT_HIDEPREFIX : 0));
        if (of) SelectObject(dmi->um.hdc, of);
        return TRUE;
    }
    LRESULT OnNcPaintSeam(UINT msg, WPARAM wp, LPARAM lp, BOOL&) {
        LRESULT r = DefWindowProc(msg, wp, lp);
        if (g_th.dark) themeMenuSeam(m_hWnd);
        return r;
    }
    LRESULT OnMenuSeamTouch(UINT msg, WPARAM wp, LPARAM lp, BOOL&) {
        LRESULT r = DefWindowProc(msg, wp, lp);
        if (g_th.dark) themeMenuSeam(m_hWnd);
        return r;
    }

    // ---- painting ----
    void OnPaint(CDCHandle) {
        CPaintDC dc(m_hWnd);
        RECT rc; GetClientRect(&rc);
        paint(dc.m_hDC, rc);
    }
    BOOL OnEraseBkgnd(CDCHandle) { return TRUE; }   // everything is double-buffered in paint()

    // ---- keyboard ----
    void OnChar(TCHAR chr, UINT, UINT) {
        if (g_palette) return;
        if (g_swallowChar) { g_swallowChar = false; return; }   // belongs to a keydown a binding consumed
        if (Session* s = focusedSession()) s->scrollOff = 0;
        if (chr == L'\r') { sendBytes("\r", 1); return; }
        sendUtf8((wchar_t)chr);
    }
    LRESULT OnKey(UINT, WPARAM wp, LPARAM, BOOL& bHandled) {
        if (g_treeDrag && wp == VK_ESCAPE) { endTreeDrag(false, { 0, 0 }); return 0; }   // cancel the drag
        // Reset per keydown; if a binding handled it, swallow the WM_CHAR TranslateMessage emits.
        g_swallowChar = handleKeyDown(wp);
        if (g_swallowChar) return 0;
        bHandled = FALSE;   // unhandled: let DefWindowProc do its thing (menu keys etc.)
        return 0;
    }

    // ---- mouse ----
    BOOL OnMouseWheel(UINT nFlags, short zDelta, CPoint pt) {
        if (nFlags & MK_CONTROL) { fontZoom(zDelta > 0 ? +1 : -1); return TRUE; }   // Ctrl+wheel = font zoom
        ScreenToClient(&pt);                                                        // wheel coords are screen-relative
        bool up = zDelta > 0;
        if (mouseReport(pt.x, pt.y, up ? 64 : 65, true, false)) return TRUE;        // to the app if it reports mouse
        scrollFocused(up ? 3 : -3);
        return TRUE;
    }
    void OnLButtonDown(UINT, CPoint pt) {
        if (inSplitter(pt.x, pt.y)) { g_splitDrag = true; SetCapture(); return; }   // grab the sidebar splitter
        if (g_palette) { g_palette = false; Invalidate(FALSE); SetFocus(); return; }
        // The sidebar is the native tree child, so clicks here are always in the terminal area.
        int pane, absRow, col;
        if (hitTest(pt.x, pt.y, &pane, &absRow, &col)) {
            g_focus = pane;
            if (mouseReport(pt.x, pt.y, 0, true, false)) { SetFocus(); Invalidate(FALSE); return; }
            g_sel = { pane, true, absRow, col, absRow, col };   // begin drag-select
            SetCapture();
            Invalidate(FALSE);
        }
        SetFocus();
    }
    void OnMouseMove(UINT nFlags, CPoint pt) {
        if (g_splitDrag) {   // the splitter resizes the LEFT pane (the sidebar); terminal takes the rest
            RECT c; GetClientRect(&c);
            g_sidebarW = max(kSidebarMinW, min((int)(c.right * 0.6), (int)pt.x));
            relayout();
            return;
        }
        if (nFlags & (MK_LBUTTON | MK_RBUTTON | MK_MBUTTON)) {   // report drags to a mouse-aware app
            int held = (nFlags & MK_LBUTTON) ? 0 : (nFlags & MK_RBUTTON) ? 2 : 1;
            if (mouseReport(pt.x, pt.y, held, true, true)) return;
        }
        if (g_sel.active && (nFlags & MK_LBUTTON)) {
            int pane, absRow, col;
            if (hitTest(pt.x, pt.y, &pane, &absRow, &col) && pane == g_sel.pane) {
                g_sel.bRow = absRow; g_sel.bCol = col;
                Invalidate(FALSE);
            }
        }
    }
    void OnLButtonUp(UINT, CPoint pt) {
        if (g_splitDrag) { g_splitDrag = false; ReleaseCapture(); saveColors(); return; }   // persist the new width
        if (mouseReport(pt.x, pt.y, 0, false, false)) return;
        if (g_sel.active) {
            g_sel.active = false;
            ReleaseCapture();
            if (g_sel.has()) copySelection();   // auto-copy on release (terminal convention)
        }
    }
    void OnRButtonDown(UINT, CPoint pt) {
        if (pt.x < sidebarSpan()) return;                        // sidebar/splitter: no paste
        if (mouseReport(pt.x, pt.y, 2, true, false)) return;     // right-click to a mouse-aware app
        pasteClipboard();                                        // else right-click pastes
    }
    void OnRButtonUp(UINT, CPoint pt) { mouseReport(pt.x, pt.y, 2, false, false); }

    LRESULT OnSetCursor(UINT, WPARAM wp, LPARAM lp, BOOL& bHandled) {
        // Children (toolbar/tree/status) forward WM_SETCURSOR here; wParam names the window the
        // cursor is actually in. Only claim the cursor for OUR client area — otherwise the toolbar
        // ends up with an I-beam whenever the sidebar is hidden (sidebarSpan() becomes 0).
        if ((HWND)wp == m_hWnd && LOWORD(lp) == HTCLIENT) {
            POINT p; GetCursorPos(&p); ScreenToClient(&p);
            if (inSplitter(p.x, p.y)) { SetCursor(LoadCursorW(nullptr, (LPCWSTR)IDC_SIZEWE)); return TRUE; }
            if (p.x >= sidebarSpan()) { SetCursor(LoadCursorW(nullptr, (LPCWSTR)IDC_IBEAM)); return TRUE; }
        }
        bHandled = FALSE;   // a child's cursor is the child's business
        return 0;
    }

    // ---- layout ----
    void OnSize(UINT nType, CSize size) {
        if (nType == SIZE_MINIMIZED) return;
        if (m_toolbar.IsWindow()) {   // standard toolbar spans the top; capture height (0 when hidden)
            m_toolbar.ShowWindow(g_showToolbar ? SW_SHOW : SW_HIDE);
            if (g_showToolbar) {
                m_toolbar.AutoSize();
                RECT tr; m_toolbar.GetWindowRect(&tr); g_toolbarH = tr.bottom - tr.top;
            }
        }
        if (m_status.IsWindow()) {    // standard status bar auto-docks bottom; capture its height
            m_status.ShowWindow(g_showStatus ? SW_SHOW : SW_HIDE);
            if (g_showStatus) {
                m_status.SendMessage(WM_SIZE, 0, 0);
                RECT sr; m_status.GetWindowRect(&sr); g_statusH = sr.bottom - sr.top;
            }
        }
        if (m_tree.IsWindow()) {      // resizable sidebar between the toolbar and the status bar
            m_tree.ShowWindow(g_showSidebar ? SW_SHOW : SW_HIDE);
            if (g_showSidebar)
                m_tree.SetWindowPos(nullptr, 0, toolbarTop(), g_sidebarW,
                                    size.cy - toolbarTop() - (g_showStatus ? g_statusH : 0),
                                    SWP_NOZORDER | SWP_NOACTIVATE);
        }
        if (!g_sessions.empty()) syncPaneSizes();
    }
    void OnExitSizeMove() { saveWindowRect(); }   // remember geometry after a user move/resize

    // Windows "app mode" flipped (or any policy change): AUTO re-resolves, the rest are unaffected.
    void OnSettingChange(UINT, LPCTSTR lpszSection) {
        if (g_themeMode != TH_AUTO) return;
        if (lpszSection && lstrcmpiW(lpszSection, L"ImmersiveColorSet") != 0) return;
        applyTheme();
    }

    // ---- app messages ----
    LRESULT OnRefreshTree(UINT, WPARAM, LPARAM, BOOL&) {
        refreshTree();
        if (g_toolbar) ::InvalidateRect(g_toolbar, nullptr, FALSE);   // bell re-reads anyBlocked()
        return 0;
    }
    LRESULT OnTray(UINT, WPARAM, LPARAM lp, BOOL&) {
        if (LOWORD(lp) == WM_RBUTTONUP || LOWORD(lp) == WM_CONTEXTMENU) showTrayMenu();
        else if (LOWORD(lp) == WM_LBUTTONDBLCLK) showMainWindow();
        return 0;
    }
    LRESULT OnOverlay(UINT, WPARAM, LPARAM, BOOL&) {   // marshaled from the control thread
        std::string cmd; int sz;
        EnterCriticalSection(&g_lock); cmd = g_pendingOverlayCmd; sz = g_pendingOverlaySize; LeaveCriticalSection(&g_lock);
        openOverlay(cmd, sz);
        return 0;
    }

    // ---- notifications ----
    LRESULT OnNotify(UINT, WPARAM, LPARAM lp, BOOL&) {
        auto* nm = (NMHDR*)lp;
        if (nm->idFrom == ID_TREE && nm->code == NM_CUSTOMDRAW) {
            auto* cd = (NMTVCUSTOMDRAW*)lp;
            if (cd->nmcd.dwDrawStage == CDDS_PREPAINT) return CDRF_NOTIFYITEMDRAW;
            if (cd->nmcd.dwDrawStage == CDDS_ITEMPREPAINT) {
                LRESULT r = CDRF_DODEFAULT;
                // Themed looks: comctl32 would otherwise draw the selected row with the system
                // highlight, which is a bright box on a dark sidebar. Paint it from the palette.
                if (!g_th.classic) {
                    bool sel = (cd->nmcd.uItemState & (CDIS_SELECTED | CDIS_FOCUS)) != 0;
                    cd->clrText   = g_th.text;
                    cd->clrTextBk = sel ? g_th.sel : g_th.client;
                    cd->nmcd.uItemState &= ~(CDIS_SELECTED | CDIS_FOCUS);
                    r = CDRF_NEWFONT;
                }
                LPARAM p = cd->nmcd.lItemlParam;   // italicise "working" agent rows
                if (p >= 0 && p < (LPARAM)g_sessions.size() && !g_sessions[p]->exited &&
                    statusClass(g_sessions[p]->status) == AGST_WORKING && g_treeItalic) {
                    SelectObject(cd->nmcd.hdc, g_treeItalic);
                    r = CDRF_NEWFONT;
                }
                if (p >= 0 && p < (LPARAM)g_sessions.size() && (g_sessions[p]->flagged || g_sessions[p]->unread > 0))
                    r |= CDRF_NOTIFYPOSTPAINT;   // pennant / unread badge drawn after the row
                return r;
            }
            if (cd->nmcd.dwDrawStage == CDDS_ITEMPOSTPAINT) {
                LPARAM p = cd->nmcd.lItemlParam;
                if (p >= 0 && p < (LPARAM)g_sessions.size()) {
                    RECT rr;
                    if (TreeView_GetItemRect(g_tree, (HTREEITEM)cd->nmcd.dwItemSpec, &rr, FALSE)) {
                        HDC dc = cd->nmcd.hdc;
                        int cy = (rr.top + rr.bottom) / 2;
                        if (g_sessions[p]->flagged) {   // amber pennant (full app's flag marker)
                            int x = rr.right - 15;
                            COLORREF amber = RGB(245, 194, 66);
                            HPEN pen = CreatePen(PS_SOLID, 1, amber);
                            HBRUSH br = CreateSolidBrush(amber);
                            HGDIOBJ op = SelectObject(dc, pen), ob = SelectObject(dc, br);
                            MoveToEx(dc, x, cy - 6, nullptr); LineTo(dc, x, cy + 6);
                            POINT tri[3] = { { x, cy - 6 }, { x + 8, cy - 3 }, { x, cy } };
                            Polygon(dc, tri, 3);
                            SelectObject(dc, op); SelectObject(dc, ob);
                            DeleteObject(pen); DeleteObject(br);
                        }
                        if (g_sessions[p]->unread > 0) {   // red count pill (full app's notification badge)
                            wchar_t bn[8];
                            wsprintfW(bn, L"%d", g_sessions[p]->unread > 99 ? 99 : g_sessions[p]->unread);
                            HFONT bf = g_uiFont ? g_uiFont : (HFONT)GetStockObject(DEFAULT_GUI_FONT);
                            HGDIOBJ of = SelectObject(dc, bf);
                            SIZE sz{}; GetTextExtentPoint32W(dc, bn, lstrlenW(bn), &sz);
                            int w2 = sz.cx + 10, x1 = rr.right - 20, x0 = x1 - w2;
                            RECT pill{ x0, cy - 8, x1, cy + 8 };
                            HBRUSH rb = CreateSolidBrush(RGB(205, 72, 58));
                            HPEN rp = CreatePen(PS_SOLID, 1, RGB(205, 72, 58));
                            HGDIOBJ ob2 = SelectObject(dc, rb), op2 = SelectObject(dc, rp);
                            RoundRect(dc, pill.left, pill.top, pill.right, pill.bottom, 12, 12);
                            SelectObject(dc, ob2); SelectObject(dc, op2);
                            DeleteObject(rb); DeleteObject(rp);
                            SetBkMode(dc, TRANSPARENT);
                            SetTextColor(dc, RGB(255, 255, 255));
                            DrawTextW(dc, bn, -1, &pill, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
                            SelectObject(dc, of);
                        }
                    }
                }
                return CDRF_DODEFAULT;
            }
            return CDRF_DODEFAULT;
        }
        if (nm->idFrom == ID_TOOLBAR && nm->code == NM_CUSTOMDRAW && !g_th.classic) {
            auto* cd = (NMTBCUSTOMDRAW*)lp;   // flat dark/light toolbar; comctl32 draws 3-D otherwise
            if (cd->nmcd.dwDrawStage == CDDS_PREPAINT) {
                RECT r; m_toolbar.GetClientRect(&r);
                HBRUSH b = CreateSolidBrush(g_th.bar); FillRect(cd->nmcd.hdc, &r, b); DeleteObject(b);
                return CDRF_NOTIFYITEMDRAW;
            }
            if (cd->nmcd.dwDrawStage == CDDS_ITEMPREPAINT) {
                // comctl32 v5 (no manifest) ignores TBCDRF_NOBACKGROUND and paints its raised 3-D
                // face over any fill — so draw the whole button ourselves and skip the default.
                bool hot = (cd->nmcd.uItemState & CDIS_HOT) != 0;
                bool prs = (cd->nmcd.uItemState & (CDIS_SELECTED | CDIS_CHECKED)) != 0;
                RECT rc = cd->nmcd.rc;
                HBRUSH b = CreateSolidBrush(prs ? g_th.sel : hot ? g_th.hot : g_th.bar);
                FillRect(cd->nmcd.hdc, &rc, b); DeleteObject(b);
                int img = tbImageOf((int)cd->nmcd.dwItemSpec);
                if (cd->nmcd.dwItemSpec == IDM_ATTENTION && anyBlocked()) img = 10;   // amber bell
                int ix = rc.left + (rc.right - rc.left - 16) / 2;
                int iy = rc.top + (rc.bottom - rc.top - 16) / 2 + (prs ? 1 : 0);   // classic 1px press nudge
                if (img >= 0) ImageList_Draw(g_tbImages, img, cd->nmcd.hdc, ix, iy, ILD_NORMAL);
                if (hot || prs) { HBRUSH f = CreateSolidBrush(g_th.accent); FrameRect(cd->nmcd.hdc, &rc, f); DeleteObject(f); }
                return CDRF_SKIPDEFAULT;
            }
            return CDRF_DODEFAULT;
        }
        if (nm->code == TBN_GETINFOTIPW) {   // toolbar button hover tooltips ("hints")
            auto* it = (NMTBGETINFOTIPW*)lp;
            const wchar_t* tip = L"";
            for (const auto& b : kTbButtons) if (b.id == it->iItem) { tip = b.tip; break; }
            lstrcpynW(it->pszText, tip, it->cchTextMax);
            return 0;
        }
        if (nm->idFrom == ID_TREE && nm->code == TVN_SELCHANGEDW && !g_treeSyncing) {
            auto* nt = (NMTREEVIEWW*)lp;
            LPARAM p = nt->itemNew.lParam;
            if (p >= 0) {                                   // session node -> show it in the MAIN pane
                int i = (int)p;
                if (i < (int)g_sessions.size()) {
                    g_pane[0] = i; g_focus = 0;             // tree drives the main pane, not the split shell
                    g_activeWs = g_sessions[i]->ws;         // new sessions follow the selected one's workspace
                    syncPaneSizes();
                    Invalidate(FALSE);
                }
            } else {                                        // workspace node -> make it the active "folder"
                int w = (int)(-p - 1);
                if (w >= 0 && w < (int)g_workspaces.size()) g_activeWs = w;
            }
            SetFocus();   // keep typing going to the terminal, not the tree
        }
        if (nm->idFrom == ID_TREE && nm->code == NM_RCLICK) {   // right-click a node -> context menu
            POINT cp; GetCursorPos(&cp); ::ScreenToClient(g_tree, &cp);
            TVHITTESTINFO ht{}; ht.pt = cp;
            HTREEITEM it = TreeView_HitTest(g_tree, &ht);
            if (it) {
                TVITEMW ti{}; ti.mask = TVIF_PARAM; ti.hItem = it;
                TreeView_GetItem(g_tree, &ti);
                g_ctxItem = it; g_ctxParam = ti.lParam;   // right-click doesn't change the selection
                showTreeContextMenu();
            }
            return 1;
        }
        if (nm->idFrom == ID_TREE && nm->code == TVN_BEGINLABELEDITW) {   // seed the edit box with the bare name
            auto* di = (NMTVDISPINFOW*)lp;
            if (HWND ed = TreeView_GetEditControl(g_tree)) {
                if (di->item.lParam >= 0) {
                    int i = (int)di->item.lParam;
                    std::wstring bn = (i < (int)g_sessions.size() && !g_sessions[i]->name.empty())
                                      ? g_sessions[i]->name : (L"session " + std::to_wstring(i + 1));
                    ::SetWindowTextW(ed, bn.c_str());
                } else {
                    int w = (int)(-di->item.lParam - 1);
                    if (w >= 0 && w < (int)g_workspaces.size()) ::SetWindowTextW(ed, g_workspaces[w].c_str());
                }
            }
            return 0;   // FALSE = allow the edit
        }
        if (nm->idFrom == ID_TREE && nm->code == TVN_ENDLABELEDITW) {   // apply the new name
            auto* di = (NMTVDISPINFOW*)lp;
            if (di->item.pszText && di->item.pszText[0]) {
                std::wstring txt = di->item.pszText;
                if (di->item.lParam >= 0) { int i = (int)di->item.lParam; if (i < (int)g_sessions.size()) g_sessions[i]->name = txt; }
                else { int w = (int)(-di->item.lParam - 1); if (w >= 0 && w < (int)g_workspaces.size()) g_workspaces[w] = txt; }
                ::PostMessageW(g_hwnd, WM_APP_REFRESHTREE, 0, 0);   // re-decorate with status/count
            }
            return 0;   // FALSE — we refresh the label ourselves
        }
        return 0;
    }

    // ---- commands ----
    LRESULT OnCommand(UINT, WPARAM wp, LPARAM lp, BOOL&) {
        int id = LOWORD(wp);
        // Accept only menu/accelerator commands (lParam == 0) and toolbar button clicks. The tree
        // FORWARDS its label-edit control's EN_* notifications here as WM_COMMAND, and their control
        // id can collide with command ids (EN_CHANGE arrived as id 1 == IDM_NEW — so renaming a
        // workspace opened the New Session dialog).
        if (lp != 0 && (HWND)lp != g_toolbar) return 0;
        if (HIWORD(wp) > 1) return 0;   // BN_CLICKED/menu/accel only, never EN_*/CBN_*
        switch (id) {
            case IDM_NEW: newSessionDialog(); break;                                     // profile picker
            case IDM_NEWWS: {                                                            // new workspace ("folder")
                wchar_t nm[32]; wsprintfW(nm, L"workspace %d", (int)g_workspaces.size() + 1);
                g_workspaces.push_back(nm);
                g_activeWs = (int)g_workspaces.size() - 1;
                refreshTree();
                break;
            }
            case IDM_PROPERTIES: showPropertiesDialog(); break;
            case IDM_KEYBOARD: showKeyboardDialog(); break;
            case IDM_QUICK: togglePopupTerminal(false); break;
            case IDM_SCRATCH: togglePopupTerminal(true); break;
            case IDM_REOPEN: reopenClosed(); break;
            case IDM_TG_SIDEBAR: case IDM_TG_TOOLBAR: case IDM_TG_STATUS: {
                bool& b = id == IDM_TG_SIDEBAR ? g_showSidebar : id == IDM_TG_TOOLBAR ? g_showToolbar : g_showStatus;
                b = !b;
                CheckMenuItem(GetMenu(), id, MF_BYCOMMAND | (b ? MF_CHECKED : MF_UNCHECKED));
                relayout(); saveColors();
                break;
            }
            case IDM_FLAG: toggleFlag(focusedSession()); break;
            case IDM_FLAGVIEW: toggleFlagView(); break;
            case IDM_FOCUSWS: toggleFocusWs(g_focusWs >= 0 ? g_focusWs : g_activeWs); break;   // toggle on active ws
            case IDM_ATTENTION: nextBlocked(); break;
            case IDM_RESTART: restartApp(); break;
            case IDM_SHOW: showMainWindow(); break;
            case IDM_EXIT: DestroyWindow(); break;
            case IDM_ABOUT:
                MessageBoxW(L"agwinterm lite\nA lightweight native terminal over the Rust pty-host.",
                            L"About", MB_OK | MB_ICONINFORMATION);
                break;
            default:   // menu-bar / tray: close / split / next / copy / paste / previous
                if (id >= IDM_CLOSE && id <= IDM_PREV) { runPaletteItem(id); Invalidate(FALSE); SetFocus(); }
                break;
        }
        return 0;
    }

    void OnDestroy() {
        saveWindowRect();                        // remember window size + position for next launch
        Shell_NotifyIconW(NIM_DELETE, &g_nid);   // remove the tray icon
        for (Session* s : g_sessions) killSession(s);
        agwinterm_ptyhost_Request req = agwinterm_ptyhost_Request_init_default;
        agwinterm_ptyhost_Reply rep = agwinterm_ptyhost_Reply_init_default;
        req.which_cmd = agwinterm_ptyhost_Request_shutdown_tag;
        request(req, &rep);
        PostQuitMessage(0);
    }
};

static CMainFrame g_frame;


// ---- control-API server (newline JSON, agwintermctl/skill-compatible subset) ----
static Session* resolveTarget(const std::string& target) {
    if (target.empty() || target == "active") return focusedSession();
    for (Session* s : g_sessions)
        if (s->id == target || (target.size() >= 4 && s->id.compare(0, target.size(), target) == 0))
            return s;
    return nullptr;
}

static std::string dumpBufferText(Session* s) {
    FfiEmuInfo info{};
    std::string out;
    EnterCriticalSection(&g_lock);
    emu_info(s->emu, &info);
    std::vector<FfiCell> row(info.cols);
    auto appendRow = [&](const FfiCell* cells) {
        std::string line;
        for (uint32_t c = 0; c < info.cols; c++) {
            const FfiCell& cell = cells[c];
            if (cell.width == 0) continue;
            int cp = cell.rune ? cell.rune : ' ';
            wchar_t wbuf[2];
            int wn = 0;
            if (cp > 0xFFFF) {
                wbuf[wn++] = (wchar_t)(0xD800 + ((cp - 0x10000) >> 10));
                wbuf[wn++] = (wchar_t)(0xDC00 + ((cp - 0x10000) & 0x3FF));
            } else wbuf[wn++] = (wchar_t)cp;
            char u8[8];
            int n8 = WideCharToMultiByte(CP_UTF8, 0, wbuf, wn, u8, sizeof u8, nullptr, nullptr);
            line.append(u8, n8);
        }
        while (!line.empty() && line.back() == ' ') line.pop_back();
        out += line;
        out += '\n';
    };
    for (uint32_t h = 0; h < info.historyCount; h++)
        if (emu_copy_history_row(s->emu, h, row.data(), info.cols)) appendRow(row.data());
    if (s->grid.size() >= (size_t)info.cols * info.rows)
        for (uint32_t r = 0; r < info.rows; r++) appendRow(&s->grid[r * info.cols]);
    LeaveCriticalSection(&g_lock);
    while (out.size() >= 2 && out[out.size() - 1] == '\n' && out[out.size() - 2] == '\n') out.pop_back();
    return out;
}

static std::string ctlDispatch(const std::string& line) {
    JsonReq req;
    size_t i = 0;
    if (!jsonParseObject(line, i, "", req)) return ctlErr("invalid JSON");
    const std::string& cmd = req.get("cmd");

    if (cmd == "ping") return ctlOkStr("agwinterm-lite 0.1");
    if (cmd == "tree") {
        std::string sess;
        for (int i2 = 0; i2 < (int)g_sessions.size(); i2++) {
            if (i2) sess += ",";
            Session* s = g_sessions[i2];
            sess += "{\"id\":\"" + jsonEscape(s->id) + "\",\"name\":\"" + jsonEscape(s->id) +
                    "\",\"active\":" + (g_pane[g_focus] == i2 ? "true" : "false") +
                    ",\"status\":\"" + jsonEscape(s->status) + "\"}";
        }
        return ctlOk("{\"workspaces\":[{\"id\":\"lite\",\"name\":\"workspace 1\",\"active\":true,\"sessions\":[" + sess + "]}]}");
    }
    if (cmd == "session.new") {
        int cols, rows;
        paneGridSize(g_focus, &cols, &rows);
        Session* s = newSession(cols, rows);
        if (!s) return ctlErr("create failed");
        g_pane[g_focus] = (int)g_sessions.size() - 1;
        InvalidateRect(g_hwnd, nullptr, FALSE);
        return ctlOkStr(s->id);
    }
    Session* target = resolveTarget(req.get("target"));
    if (cmd == "session.select") {
        if (!target) return ctlErr("session not found");
        for (int i2 = 0; i2 < (int)g_sessions.size(); i2++)
            if (g_sessions[i2] == target) g_pane[g_focus] = i2;
        InvalidateRect(g_hwnd, nullptr, FALSE);
        return ctlOkStr("selected");
    }
    if (cmd == "session.type") {
        if (!target) return ctlErr("session not found");
        std::string text = req.get("args.text");
        // \n → \r (keystroke semantics, like the main app's session.type)
        for (char& ch : text) if (ch == '\n') ch = '\r';
        if (target->data != INVALID_HANDLE_VALUE)
            ovIo(target->data, true, text.data(), nullptr, (DWORD)text.size());
        return ctlOkStr("typed");
    }
    if (cmd == "session.text") {
        if (!target) return ctlErr("session not found");
        return ctlOkStr(dumpBufferText(target));
    }
    if (cmd == "session.status") {
        if (!target) return ctlErr("session not found");
        std::string st = req.get("args.status");
        if (st.empty()) return ctlErr("session status needs a state");
        target->status = st;
        InvalidateRect(g_hwnd, nullptr, FALSE);
        PostMessageW(g_hwnd, WM_APP_REFRESHTREE, 0, 0);   // update the tree's status label
        return ctlOkStr("status set");
    }
    if (cmd == "session.close") {
        if (!target) return ctlErr("session not found");
        for (int i2 = 0; i2 < (int)g_sessions.size(); i2++)
            if (g_sessions[i2] == target) { g_pane[g_focus] = i2; closeFocused(); break; }
        return ctlOkStr("closed");
    }
    if (cmd == "session.overlay") {   // run a command in an overlay popup over the active session
        std::string action = req.get("args.action");
        if (action == "close") { if (g_overlayHwnd) PostMessageW(g_overlayHwnd, WM_CLOSE, 0, 0); return ctlOkStr("closed"); }
        std::string command = req.get("args.command");
        int sizePct = atoi(req.get("args.size").c_str());
        EnterCriticalSection(&g_lock); g_pendingOverlayCmd = command; g_pendingOverlaySize = sizePct; LeaveCriticalSection(&g_lock);
        PostMessageW(g_hwnd, WM_APP_OVERLAY, 0, 0);   // create on the UI thread
        return ctlOkStr("overlay opened");
    }
    return ctlErr("unknown command '" + cmd + "' (lite subset)");
}

static DWORD WINAPI ctlClientThread(void* param) {
    HANDLE pipe = (HANDLE)param;
    std::string line;
    char ch;
    DWORD n;
    while (ReadFile(pipe, &ch, 1, &n, nullptr) && n == 1) {
        if (ch == '\n') {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (!line.empty()) {
                std::string reply = ctlDispatch(line) + "\n";
                DWORD w;
                if (!WriteFile(pipe, reply.data(), (DWORD)reply.size(), &w, nullptr)) break;
            }
            line.clear();
        } else line += ch;
    }
    CloseHandle(pipe);
    return 0;
}

static DWORD WINAPI ctlServerThread(void*) {
    for (;;) {
        HANDLE pipe = CreateNamedPipeW(L"\\\\.\\pipe\\agwinterm-lite", PIPE_ACCESS_DUPLEX,
                                       PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                                       PIPE_UNLIMITED_INSTANCES, 64 * 1024, 64 * 1024, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) return 1;
        BOOL ok = ConnectNamedPipe(pipe, nullptr);
        if (!ok && GetLastError() != ERROR_PIPE_CONNECTED) { CloseHandle(pipe); continue; }
        CreateThread(nullptr, 0, ctlClientThread, pipe, 0, nullptr);
    }
}

// Rebuild the saved workspaces + sessions on launch. Returns false (caller opens a default session) if
// there's nothing to restore. Sessions relaunch with their remembered profile + creation cwd.
static bool restoreSessions() {
    std::wstring path = stateFilePath();
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;
    std::string data; char buf[4096]; DWORD rd;
    while (ReadFile(f, buf, sizeof buf, &rd, nullptr) && rd) data.append(buf, rd);
    CloseHandle(f);
    if (data.empty()) return false;

    std::vector<std::wstring> wss;
    struct Spec { int ws; std::string name, app, cwd; std::vector<std::string> args; bool flagged = false; };
    std::vector<Spec> specs;
    int activeWs = 0, focusWs = -1;
    size_t i = 0;
    auto split = [](const std::string& l) {
        std::vector<std::string> ff; size_t p = 0;
        for (;;) { size_t t = l.find('\t', p); ff.push_back(l.substr(p, t == std::string::npos ? std::string::npos : t - p)); if (t == std::string::npos) break; p = t + 1; }
        return ff;
    };
    while (i < data.size()) {
        size_t e = data.find('\n', i);
        std::string l = data.substr(i, e == std::string::npos ? std::string::npos : e - i);
        i = (e == std::string::npos) ? data.size() : e + 1;
        if (!l.empty() && l.back() == '\r') l.pop_back();
        if (l.empty()) continue;
        auto ff = split(l);
        if (ff[0] == "W" && ff.size() >= 2) wss.push_back(widen(ff[1]));
        else if (ff[0] == "S" && ff.size() >= 5) {
            Spec sp; sp.ws = atoi(ff[1].c_str()); sp.name = ff[2]; sp.app = ff[3]; sp.cwd = ff[4];
            for (size_t k = 5; k < ff.size(); k++) sp.args.push_back(ff[k]);
            specs.push_back(sp);
        } else if (ff[0] == "F") {   // flagged indices, in S-line order
            for (size_t k = 1; k < ff.size(); k++) {
                int fi = atoi(ff[k].c_str());
                if (fi >= 0 && fi < (int)specs.size()) specs[fi].flagged = true;
            }
        } else if (ff[0] == "O" && ff.size() >= 2) focusWs = atoi(ff[1].c_str());
        else if (ff[0] == "A" && ff.size() >= 2) activeWs = atoi(ff[1].c_str());
    }
    if (specs.empty()) return false;

    g_restoring = true;
    if (!wss.empty()) g_workspaces = wss;
    int cols, rows; paneGridSize(0, &cols, &rows);
    int firstIdx = -1;
    for (const auto& sp : specs) {
        g_activeWs = (sp.ws >= 0 && sp.ws < (int)g_workspaces.size()) ? sp.ws : 0;
        Session* s = newSession(cols, rows, sp.app.empty() ? nullptr : sp.app.c_str(),
                                sp.args.empty() ? nullptr : &sp.args, sp.cwd.empty() ? nullptr : sp.cwd.c_str());
        if (s) { s->name = widen(sp.name); s->flagged = sp.flagged; if (firstIdx < 0) firstIdx = (int)g_sessions.size() - 1; }
    }
    g_restoring = false;
    if (firstIdx < 0) return false;
    g_pane[0] = firstIdx; g_pane[1] = -1; g_focus = 0;
    g_activeWs = (activeWs >= 0 && activeWs < (int)g_workspaces.size()) ? activeWs : 0;
    g_focusWs = (focusWs >= 0 && focusWs < (int)g_workspaces.size()) ? focusWs : -1;
    syncPaneSizes();
    refreshTree();
    return true;
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR, int show) {
    _Module.Init(nullptr, inst);   // ATL/WTL module (window class registration lives here)
    InitializeCriticalSection(&g_lock);
    InitializeCriticalSection(&g_reqLock);
    loadCore();

    // Bundled fonts (process-private): Meslo Nerd (default TrueType), plus the optional bitmap fonts
    // Cozette + Tamzen if their .ttf shipped next to the exe. Consolas is the fallback if Meslo is absent.
    std::wstring dir = exeDir();
    bool haveMeslo = AddFontResourceExW((dir + L"\\MesloLGLDZNerdFont-Regular.ttf").c_str(), FR_PRIVATE, 0) > 0;
    g_ttFace = haveMeslo ? L"MesloLGLDZ Nerd Font" : L"Consolas";
    g_haveCozette = AddFontResourceExW((dir + L"\\CozetteVector.ttf").c_str(), FR_PRIVATE, 0) > 0;
    AddFontResourceExW((dir + L"\\CozetteVectorBold.ttf").c_str(), FR_PRIVATE, 0);
    int tam = 0;
    for (const wchar_t* f : { L"TamzenForPowerline7x14r.ttf", L"TamzenForPowerline7x14b.ttf",
                              L"TamzenForPowerline8x16r.ttf", L"TamzenForPowerline8x16b.ttf",
                              L"TamzenForPowerline10x20r.ttf", L"TamzenForPowerline10x20b.ttf" })
        tam += AddFontResourceExW((dir + L"\\" + f).c_str(), FR_PRIVATE, 0);
    g_haveTamzen = tam > 0;
    buildFontCatalog();
    loadColors();      // Properties->Colors overrides, remembered across restarts
    loadKeys();        // configurable key bindings (unbound by default)
    loadFontSel();     // resolve the remembered face+size (first run -> Terminal 8x12)
    applyFont();       // creates g_fonts + sets g_cw/g_ch (g_hwnd still null, so no relayout yet)

    connectControl();

    INITCOMMONCONTROLSEX icc{ sizeof icc, ICC_TREEVIEW_CLASSES | ICC_BAR_CLASSES | ICC_HOTKEY_CLASS };
    InitCommonControlsEx(&icc);

    g_appIcon = makeRetroIcon();
    applyTheme();   // resolve the saved theme BEFORE any window exists, so nothing flashes light

    RECT want{ 0, 0, kSidebarW + 100 * g_cw, 30 * g_ch };
    // WS_CLIPCHILDREN keeps the terminal paint out of the native tree child.
    AdjustWindowRect(&want, WS_OVERLAPPEDWINDOW, TRUE);   // TRUE = has a menu bar
    int wx = CW_USEDEFAULT, wy = CW_USEDEFAULT, ww = want.right - want.left, wh = want.bottom - want.top;
    RECT sr; bool startMax = false;
    if (loadWindowRect(&sr, &startMax)) { wx = sr.left; wy = sr.top; ww = sr.right - sr.left; wh = sr.bottom - sr.top; }
    bool haveRect = (wx != CW_USEDEFAULT);
    RECT frameRc{ wx, wy, wx + ww, wy + wh };
    // WTL frame: CFrameWinTraits already carries WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN.
    g_hwnd = g_frame.CreateEx(nullptr, haveRect ? &frameRc : nullptr);
    if (!g_hwnd) fatal(L"could not create the main window");
    g_frame.SetWindowText(L"agwinterm lite");
    g_frame.SetMenu(buildMenuBar());
    g_frame.SetIcon(g_appIcon, TRUE);    // 2000's-style terminal icon (window + taskbar)
    g_frame.SetIcon(g_appIcon, FALSE);
    if (wx == CW_USEDEFAULT) g_frame.SetWindowPos(nullptr, 0, 0, ww, wh, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);

    // Native SysTreeView32 sidebar docked on the left; picking a node selects that session.
    RECT cr; GetClientRect(g_hwnd, &cr);
    RECT trc{ 0, 0, g_sidebarW, cr.bottom };
    g_frame.m_tree.Create(g_hwnd, trc, nullptr,
                          WS_CHILD | WS_VISIBLE | TVS_SHOWSELALWAYS | TVS_NOHSCROLL |
                          TVS_HASBUTTONS | TVS_HASLINES | TVS_LINESATROOT | TVS_EDITLABELS,
                          WS_EX_CLIENTEDGE, (UINT)ID_TREE);
    g_tree = g_frame.m_tree;   // the rest of the file talks to the raw handle
    SetWindowSubclass(g_tree, treeProc, 1, 0);   // session drag & drop (own drag-detect loop)
    SendMessageW(g_tree, WM_SETFONT, (WPARAM)(HFONT)GetStockObject(DEFAULT_GUI_FONT), TRUE);
    { LOGFONTW lf{}; GetObjectW((HFONT)GetStockObject(DEFAULT_GUI_FONT), sizeof(lf), &lf); lf.lfItalic = TRUE; g_treeItalic = CreateFontIndirectW(&lf); }   // "working" rows

    // Native status bar (msctls_statusbar32) — a real standard control, docks itself at the bottom.
    RECT zr{ 0, 0, 0, 0 };
    g_frame.m_status.Create(g_hwnd, zr, nullptr, WS_CHILD | WS_VISIBLE | SBARS_SIZEGRIP, 0, (UINT)ID_STATUS);
    g_status = g_frame.m_status;
    { int parts[4] = { 120, 360, 470, -1 }; g_frame.m_status.SetParts(4, parts); }
    SetWindowSubclass(g_status, statusProc, 1, 0);   // dark paint takeover (no-op in light/classic)

    // Native toolbar across the top: New Session / New Workspace / Split, as classic 16px Silk icons
    // with hover tooltips (TBSTYLE_TOOLTIPS). No TBSTYLE_FLAT: flat toolbars hot-track and can leave a
    // button stuck "hot"; classic raised 3D buttons have no hover state and suit the old-skool look.
    buildToolbarImages();
    g_frame.m_toolbar.Create(g_hwnd, zr, nullptr,
                             WS_CHILD | WS_VISIBLE | TBSTYLE_TOOLTIPS | CCS_TOP, 0, (UINT)ID_TOOLBAR);
    g_toolbar = g_frame.m_toolbar;
    g_frame.m_toolbar.SetButtonStructSize(sizeof(TBBUTTON));
    g_frame.m_toolbar.SetImageList(g_tbImages);
    TBBUTTON tb[kTbCount] = {};
    for (int i = 0; i < kTbCount; i++) {
        tb[i].iBitmap = kTbButtons[i].img;
        tb[i].idCommand = kTbButtons[i].id;
        tb[i].fsState = TBSTATE_ENABLED;
        if (kTbButtons[i].check && kTbButtons[i].id == IDM_FLAGVIEW && g_flagView) tb[i].fsState |= TBSTATE_CHECKED;
        tb[i].fsStyle = BTNS_AUTOSIZE | (kTbButtons[i].check ? BTNS_CHECK : 0);
    }
    g_frame.m_toolbar.AddButtons(kTbCount, tb);
    g_frame.m_toolbar.AutoSize();
    RECT tbr; GetWindowRect(g_toolbar, &tbr); g_toolbarH = tbr.bottom - tbr.top;

    // Shell UI font (Segoe UI on Win10/11) for the dialogs (Properties / New Session).
    NONCLIENTMETRICSW ncm{ sizeof(ncm) };
    SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof(ncm), &ncm, 0);
    g_uiFont = CreateFontIndirectW(&ncm.lfMessageFont);

    // System-tray icon (right-click for a menu incl. Restart / Exit; double-click restores).
    g_nid.cbSize = sizeof g_nid;
    g_nid.hWnd = g_hwnd;
    g_nid.uID = ID_TRAY;
    g_nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    g_nid.uCallbackMessage = WM_APP_TRAY;
    g_nid.hIcon = g_appIcon;
    wcscpy_s(g_nid.szTip, L"agwinterm lite");
    Shell_NotifyIconW(NIM_ADD, &g_nid);

    applyTheme();   // now that the controls exist, colour them (and the title bar) for real

    ShowWindow(g_hwnd, startMax ? SW_SHOWMAXIMIZED : show);   // restore a maximized session maximized

    if (!restoreSessions()) {   // rebuild the saved workspaces/sessions, or open a fresh default one
        int cols, rows;
        paneGridSize(0, &cols, &rows);
        if (!newSession(cols, rows)) fatal(L"could not create the first session");
        refreshTree();
    }
    CreateThread(nullptr, 0, ctlServerThread, nullptr, 0, nullptr);   // agwintermctl --pipe agwinterm-lite
    InvalidateRect(g_hwnd, nullptr, FALSE);

    CMessageLoop loop;          // WTL message pump (adds PreTranslateMessage / OnIdle hooks)
    _Module.AddMessageLoop(&loop);
    int rc = loop.Run();
    _Module.RemoveMessageLoop();
    _Module.Term();
    return rc;
}
