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
#include <gdiplus.h>    // load the PNG toolbar icons (ships with Windows, no external deps)
#include <string>
#include <vector>
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "advapi32.lib")   // registry (persisted font choice)
#pragma comment(lib, "gdiplus.lib")    // PNG decode for the toolbar icons

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

// ---- sessions & layout ----
struct Session {
    std::string id;
    std::string status = "idle";   // control-API agent status (sidebar dot)
    std::wstring name;             // custom name (rename); empty = "session N"
    int ws = 0;                    // workspace this session belongs to (index into g_workspaces)
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
static HWND g_quickHwnd, g_scratchHwnd;
static Session* g_quickSession, *g_scratchSession, *g_focusOverride;
static HWND g_toolbar;          // native toolbar (New Session / New Workspace / Split)
static int g_toolbarH = 0;      // its height; the tree + terminal start below it
static HIMAGELIST g_tbImages;   // 16x16 Silk icons for the toolbar buttons
static ULONG_PTR g_gdiplusTok;  // GDI+ token (PNG decode)
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
// Configurable key bindings for every lite action. ALL UNBOUND BY DEFAULT so no combo is stolen from
// the shell/TUI until the user assigns one in File -> Keyboard. Stored HOTKEY-format: LOBYTE = vk,
// HIBYTE = HOTKEYF_* (SHIFT 1 / CONTROL 2 / ALT 4). 0 = unbound.
enum { KB_NEW, KB_NEWWS, KB_CLOSE, KB_SPLIT, KB_NEXT, KB_PREV, KB_COPY, KB_PASTE,
       KB_PALETTE, KB_FOCUSL, KB_FOCUSR, KB_SCROLLUP, KB_SCROLLDN, KB_QUICK, KB_SCRATCH, KB_COUNT };
struct KbInfo { const wchar_t* label; const wchar_t* reg; };
static const KbInfo kKbInfo[KB_COUNT] = {
    { L"New Session",      L"Key_New" },     { L"New Workspace",    L"Key_NewWs" },
    { L"Close Session",    L"Key_Close" },   { L"Split / Unsplit",  L"Key_Split" },
    { L"Next Session",     L"Key_Next" },    { L"Previous Session", L"Key_Prev" },
    { L"Copy",             L"Key_Copy" },    { L"Paste",            L"Key_Paste" },
    { L"Command Palette",  L"Key_Palette" }, { L"Focus Left Pane",  L"Key_FocusL" },
    { L"Focus Right Pane", L"Key_FocusR" },  { L"Scroll Up",        L"Key_ScrollUp" },
    { L"Scroll Down",      L"Key_ScrollDn" }, { L"Quick Terminal",   L"Key_Quick" },
    { L"Scratch Terminal", L"Key_Scratch" },
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
       IDM_QUICK = 120, IDM_SCRATCH = 121 };
#define IDM_MOVE_BASE 300   // "Move to workspace <w>" = IDM_MOVE_BASE + w
enum { ID_TREE = 200, ID_TRAY = 201, ID_TOOLBAR = 202 };
#define WM_APP_REFRESHTREE (WM_APP + 3)   // posted from worker threads to rebuild the tree on the UI thread
#define WM_APP_TRAY        (WM_APP + 4)   // system-tray icon notifications
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
    int contentX = kSidebarW;
    int top = g_toolbarH;   // terminal content starts below the toolbar
    int contentW = client.right - kSidebarW;
    if (g_pane[1] < 0) { *out = { contentX, top, client.right, client.bottom }; return; }
    int half = contentW / 2;
    if (pane == 0) *out = { contentX, top, contentX + half - 1, client.bottom };
    else *out = { contentX + half + 1, top, client.right, client.bottom };
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
static DWORD WINAPI readerThread(void* param) {
    Session* s = (Session*)param;
    std::vector<uint8_t> buf(64 * 1024);
    DWORD n;
    while ((n = ovIo(s->data, false, nullptr, buf.data(), (DWORD)buf.size())) > 0) {
        EnterCriticalSection(&g_lock);
        emu_feed(s->emu, buf.data(), n);
        LeaveCriticalSection(&g_lock);
        InvalidateRect(windowForSession(s), nullptr, FALSE);
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

static void closeSessionAt(int idx) {
    if (idx < 0 || idx >= (int)g_sessions.size()) return;
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
    for (const Session* s : g_sessions) {
        if (s->hidden) continue;
        out += "S\t" + std::to_string(s->ws) + "\t" + narrow(s->name) + "\t" + s->app + "\t" + s->cwd;
        for (const auto& a : s->args) out += "\t" + a;
        out += "\n";
    }
    LeaveCriticalSection(&g_lock);
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
    if (info.markCount > 0) {
        std::vector<FfiMark> marks(info.markCount);
        EnterCriticalSection(&g_lock);
        uint32_t nm = emu_marks(s->emu, marks.data(), info.markCount);
        LeaveCriticalSection(&g_lock);
        int base = (int)info.historyCount - off;   // buffer-absolute row of the top visible line
        for (uint32_t mi = 0; mi < nm; mi++) {
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
        RECT div{ pr0.right, 0, pr0.right + 2, rc.bottom };
        HBRUSH b = CreateSolidBrush(RGB(60, 62, 70));
        FillRect(mem, &div, b);
        DeleteObject(b);
    }

    if (g_palette) {
        int n = (int)(sizeof kPalette / sizeof kPalette[0]);
        int pw = 460, ph = (n + 1) * (g_ch + 8) + 12;
        int px = kSidebarW + ((rc.right - kSidebarW) - pw) / 2, py = 60;
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
static void refreshTree() {
    if (!g_tree) return;
    g_treeSyncing = true;
    TreeView_DeleteAllItems(g_tree);
    HTREEITEM sel = nullptr;
    int focusIdx = g_pane[g_focus];
    // Group sessions under their workspace ("folder"). lParam encodes the node: >=0 session index,
    // <0 = -(workspace index + 1).
    for (int w = 0; w < (int)g_workspaces.size(); w++) {
        int count = 0;
        for (auto* s : g_sessions) if (s->ws == w && !s->hidden) count++;   // hidden split shells don't count
        wchar_t wlabel[96];
        wsprintfW(wlabel, L"%s  (%d)", g_workspaces[w].c_str(), count);
        TVINSERTSTRUCTW wt{};
        wt.hParent = TVI_ROOT;
        wt.hInsertAfter = TVI_LAST;
        wt.item.mask = TVIF_TEXT | TVIF_PARAM;
        wt.item.pszText = wlabel;
        wt.item.lParam = -(w + 1);
        HTREEITEM wh = TreeView_InsertItem(g_tree, &wt);
        int vis = 0;   // visible session number within the workspace
        for (int i = 0; i < (int)g_sessions.size(); i++) {
            if (g_sessions[i]->ws != w || g_sessions[i]->hidden) continue;   // skip split shells
            Session* s = g_sessions[i];
            ++vis;
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
    if (sel) TreeView_SelectItem(g_tree, sel);
    g_treeSyncing = false;
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
    refreshTree();
}

// Right-click menu on a tree node — session or workspace, mirroring the full app's sidebar menus.
// Acts on the RIGHT-CLICKED node (g_ctxParam), not the focused session, and dispatches inline via
// TPM_RETURNCMD (no WM_COMMAND re-entrancy, no selection change — so the active terminal doesn't jump).
static void showTreeContextMenu() {
    bool isSession = g_ctxParam >= 0;
    int si = isSession ? (int)g_ctxParam : -1;
    int cws = isSession ? (si < (int)g_sessions.size() ? g_sessions[si]->ws : 0) : (int)(-g_ctxParam - 1);
    POINT pt; GetCursorPos(&pt);
    HMENU m = CreatePopupMenu();
    if (isSession) {   // ---- session node ----
        AppendMenuW(m, MF_STRING, IDM_NEW, L"&New Session…");
        AppendMenuW(m, MF_STRING, IDM_DUP, L"&Duplicate Session");
        AppendMenuW(m, MF_STRING, IDM_RENAME, L"Re&name");
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
        case IDM_CLOSE:
            if (isSession) closeSessionAt(si);
            break;
        case IDM_DELWS:
            if (!isSession) deleteWorkspace(cws);
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
    for (const auto& p : profs) SendMessageW(g_dlgList, LB_ADDSTRING, 0, (LPARAM)p.name.c_str());
    SendMessageW(g_dlgList, LB_SETCURSEL, 0, 0);
    HWND ok = CreateWindowExW(0, L"BUTTON", L"OK", WS_CHILD | WS_VISIBLE | BS_DEFPUSHBUTTON,
                              cr.right - 176, cr.bottom - 40, 78, 26, dlg, (HMENU)IDOK, inst, nullptr);
    HWND cancel = CreateWindowExW(0, L"BUTTON", L"Cancel", WS_CHILD | WS_VISIBLE,
                                  cr.right - 90, cr.bottom - 40, 78, 26, dlg, (HMENU)IDCANCEL, inst, nullptr);
    for (HWND c : { lbl, g_dlgList, ok, cancel }) SendMessageW(c, WM_SETFONT, (WPARAM)gui, TRUE);
    g_dlgResult = -1;
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
enum { PID_FONTLIST = 3001, PID_SIZECOMBO = 3002, PID_USECOLORS = 3030, PID_TEXT = 3010, PID_BG = 3011, PID_APPLY = 3020, PID_DOSPAL = 3031 };
static const int SW_X0 = 16, SW_Y = 186, SW = 20, SW_GAP = 22;   // swatch grid geometry (WM_PAINT + hit-test)
// Working copies edited by the dialog; committed to the globals on OK/Apply.
static int g_pFace, g_pSize; static uint32_t g_pFg, g_pBg; static int g_pTarget; static bool g_pUse, g_pDos;
static HFONT g_pPrev; static HWND g_pHwnd, g_pSizeCombo;

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
    g_customColors = g_pUse; g_defFg = g_pFg; g_defBg = g_pBg; g_dosPalette = g_pDos; saveColors();
    InvalidateRect(g_hwnd, nullptr, TRUE);
}
static LRESULT CALLBACK propDlgProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
        case WM_COMMAND:
            switch (LOWORD(w)) {
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
                case PID_USECOLORS: g_pUse = SendMessageW((HWND)l, BM_GETCHECK, 0, 0) == BST_CHECKED; InvalidateRect(h, nullptr, TRUE); break;
                case PID_DOSPAL: g_pDos = SendMessageW((HWND)l, BM_GETCHECK, 0, 0) == BST_CHECKED; break;
                case PID_TEXT: g_pTarget = 0; InvalidateRect(h, nullptr, TRUE); break;
                case PID_BG:   g_pTarget = 1; InvalidateRect(h, nullptr, TRUE); break;
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
            SetTextColor(dc, GetSysColor(COLOR_BTNTEXT));
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
    if (g_pPrev) DeleteObject(g_pPrev);
    g_pPrev = makePreviewFontSel();
    const int W = 396, H = 452;
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
    for (const auto& e : g_catalog) SendMessageW(fl, LB_ADDSTRING, 0, (LPARAM)e.label);
    SendMessageW(fl, LB_SETCURSEL, g_pFace, 0);
    mk(L"STATIC", L"Size:", 0, 228, 12, 120, 16, 0);
    g_pSizeCombo = mk(L"COMBOBOX", L"", WS_BORDER | WS_VSCROLL | CBS_DROPDOWNLIST, 228, 30, 140, 240, PID_SIZECOMBO);
    fillSizeCombo(g_pSize);
    mk(L"BUTTON", L"Override default colors", BS_AUTOCHECKBOX, 16, 134, 172, 18, PID_USECOLORS);
    CheckDlgButton(g_pHwnd, PID_USECOLORS, g_pUse ? BST_CHECKED : BST_UNCHECKED);
    mk(L"BUTTON", L"MS-DOS palette (EGA)", BS_AUTOCHECKBOX, 194, 134, 180, 18, PID_DOSPAL);
    CheckDlgButton(g_pHwnd, PID_DOSPAL, g_pDos ? BST_CHECKED : BST_UNCHECKED);
    mk(L"BUTTON", L"Screen &Text", WS_GROUP | BS_AUTORADIOBUTTON, 28, 158, 110, 18, PID_TEXT);
    mk(L"BUTTON", L"Screen &Background", BS_AUTORADIOBUTTON, 150, 158, 150, 18, PID_BG);
    CheckDlgButton(g_pHwnd, PID_TEXT, BST_CHECKED);
    mk(L"BUTTON", L"OK", BS_DEFPUSHBUTTON, 120, 380, 78, 26, IDOK);
    mk(L"BUTTON", L"Cancel", 0, 204, 380, 78, 26, IDCANCEL);
    mk(L"BUTTON", L"Apply", 0, 288, 380, 78, 26, PID_APPLY);
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
        SendMessageW(g_kbCtl[a], HKM_SETRULES, HKCOMB_NONE, MAKEWORD(HOTKEYF_CONTROL, 0));   // bare key -> add Ctrl
        SendMessageW(g_kbCtl[a], HKM_SETHOTKEY, g_keys[a], 0);
    }
    int by = 50 + KB_COUNT * 26 + 8;
    mk(L"BUTTON", L"Clear all", 0, 16, by, 90, 26, KBID_CLEAR);
    mk(L"BUTTON", L"OK", BS_DEFPUSHBUTTON, W - 190, by, 82, 26, IDOK);
    mk(L"BUTTON", L"Cancel", 0, W - 100, by, 82, 26, IDCANCEL);
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
// Load a PNG into an HBITMAP with its alpha flattened onto bg (the toolbar's face colour) so it
// blends seamlessly when added to a plain colour image list. Returns null on failure.
static HBITMAP loadPngFlattened(const std::wstring& path, COLORREF bg) {
    Gdiplus::Bitmap img(path.c_str());
    if (img.GetLastStatus() != Gdiplus::Ok) return nullptr;
    HBITMAP hb = nullptr;
    img.GetHBITMAP(Gdiplus::Color(GetRValue(bg), GetGValue(bg), GetBValue(bg)), &hb);
    return hb;
}
// Build the toolbar image list from the bundled Silk PNGs (order: New / Workspace / Split).
static void buildToolbarImages() {
    g_tbImages = ImageList_Create(16, 16, ILC_COLOR24, 3, 0);
    COLORREF face = GetSysColor(COLOR_BTNFACE);
    std::wstring dir = exeDir();
    for (const wchar_t* f : { L"tb_new.png", L"tb_workspace.png", L"tb_split.png" }) {
        HBITMAP hb = loadPngFlattened(dir + L"\\" + f, face);
        if (hb) { ImageList_Add(g_tbImages, hb, nullptr); DeleteObject(hb); }
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
    Session* s = (h == g_quickHwnd) ? g_quickSession : g_scratchSession;
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
        case WM_CLOSE: ShowWindow(h, SW_HIDE); SetForegroundWindow(g_hwnd); return 0;   // hide; keep the session alive
    }
    return DefWindowProcW(h, m, w, l);
}
// Toggle a quick (scratch=false) or scratch (scratch=true) popup terminal: show/hide, creating its
// window + dedicated hidden session on first use. Owned by the main window so it floats above it,
// hides when the main window minimizes, and never sits behind it.
static void togglePopupTerminal(bool scratch) {
    HWND& hw = scratch ? g_scratchHwnd : g_quickHwnd;
    Session*& sess = scratch ? g_scratchSession : g_quickSession;
    if (hw && IsWindowVisible(hw)) { ShowWindow(hw, SW_HIDE); SetForegroundWindow(g_hwnd); return; }
    HINSTANCE inst = GetModuleHandleW(nullptr);
    if (!hw) {
        static bool reg = false;
        if (!reg) {
            WNDCLASSW wc{};
            wc.lpfnWndProc = popupProc; wc.hInstance = inst; wc.lpszClassName = L"AgwintermLitePopup";
            wc.hCursor = LoadCursorW(nullptr, (LPCWSTR)IDC_IBEAM); wc.hIcon = g_appIcon;
            RegisterClassW(&wc); reg = true;
        }
        RECT pw; GetWindowRect(g_hwnd, &pw);
        RECT want{ 0, 0, 80 * g_cw, 24 * g_ch };
        AdjustWindowRect(&want, WS_OVERLAPPEDWINDOW, FALSE);
        hw = CreateWindowExW(0, L"AgwintermLitePopup", scratch ? L"agwinterm lite — scratch" : L"agwinterm lite — quick",
                             WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN, pw.left + 80, pw.top + 60,
                             want.right - want.left, want.bottom - want.top, g_hwnd, nullptr, inst, nullptr);
        RECT rc; GetClientRect(hw, &rc);
        sess = newSession(max(1, (int)(rc.right / g_cw)), max(1, (int)(rc.bottom / g_ch)));   // windowForSession routes to hw (set above)
        if (sess) { sess->hidden = true; sess->name = scratch ? L"scratch" : L"quick"; }      // not in the sidebar / not persisted
    }
    ShowWindow(hw, SW_SHOW);
    SetForegroundWindow(hw);
    g_focusOverride = sess;
    InvalidateRect(hw, nullptr, FALSE);
}

static LRESULT CALLBACK wndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
        case WM_PAINT: {
            PAINTSTRUCT ps;
            HDC dc = BeginPaint(hwnd, &ps);
            RECT rc;
            GetClientRect(hwnd, &rc);
            paint(dc, rc);
            EndPaint(hwnd, &ps);
            return 0;
        }
        case WM_ERASEBKGND:
            return 1;
        case WM_CHAR: {
            wchar_t wc = (wchar_t)wp;
            if (g_palette) return 0;
            if (g_swallowChar) { g_swallowChar = false; return 0; }   // this char belongs to a keydown a binding consumed
            if (Session* s = focusedSession()) s->scrollOff = 0;
            if (wc == L'\r') { sendBytes("\r", 1); return 0; }
            sendUtf8(wc);
            return 0;
        }
        case WM_KEYDOWN:
        case WM_SYSKEYDOWN:   // F10 and Alt-combos arrive here (menu keys); route them to the terminal
            // Reset per keydown; if a binding handled it, swallow the WM_CHAR that TranslateMessage emits.
            g_swallowChar = handleKeyDown(wp);
            if (g_swallowChar) return 0;   // handled (e.g. a bound combo, or F10 -> ESC[21~)
            break;
        case WM_MOUSEWHEEL: {
            POINT pt{ GET_X_LPARAM(lp), GET_Y_LPARAM(lp) };
            ScreenToClient(hwnd, &pt);   // wheel coords are screen-relative
            bool up = GET_WHEEL_DELTA_WPARAM(wp) > 0;
            if (mouseReport(pt.x, pt.y, up ? 64 : 65, true, false)) return 0;   // to the app if it reports mouse
            scrollFocused(up ? 3 : -3);
            return 0;
        }
        case WM_LBUTTONDOWN: {
            int x = GET_X_LPARAM(lp), y = GET_Y_LPARAM(lp);
            if (g_palette) { g_palette = false; InvalidateRect(hwnd, nullptr, FALSE); SetFocus(hwnd); return 0; }
            // The sidebar is the native tree child, so clicks here are always in the terminal area.
            int pane, absRow, col;
            if (hitTest(x, y, &pane, &absRow, &col)) {
                g_focus = pane;
                // App wants mouse events? Forward the click and skip selection.
                if (mouseReport(x, y, 0, true, false)) { SetFocus(hwnd); InvalidateRect(hwnd, nullptr, FALSE); return 0; }
                g_sel = { pane, true, absRow, col, absRow, col };   // begin drag-select
                SetCapture(hwnd);
                InvalidateRect(hwnd, nullptr, FALSE);
            }
            SetFocus(hwnd);
            return 0;
        }
        case WM_MOUSEMOVE: {
            int x = GET_X_LPARAM(lp), y = GET_Y_LPARAM(lp);
            if (wp & (MK_LBUTTON | MK_RBUTTON | MK_MBUTTON)) {   // report drags to a mouse-aware app
                int held = (wp & MK_LBUTTON) ? 0 : (wp & MK_RBUTTON) ? 2 : 1;
                if (mouseReport(x, y, held, true, true)) return 0;
            }
            if (g_sel.active && (wp & MK_LBUTTON)) {
                int pane, absRow, col;
                if (hitTest(x, y, &pane, &absRow, &col) && pane == g_sel.pane) {
                    g_sel.bRow = absRow;
                    g_sel.bCol = col;
                    InvalidateRect(hwnd, nullptr, FALSE);
                }
            }
            return 0;
        }
        case WM_LBUTTONUP: {
            int x = GET_X_LPARAM(lp), y = GET_Y_LPARAM(lp);
            if (mouseReport(x, y, 0, false, false)) return 0;   // release to a mouse-aware app
            if (g_sel.active) {
                g_sel.active = false;
                ReleaseCapture();
                if (g_sel.has()) copySelection();   // auto-copy on release (terminal convention)
            }
            return 0;
        }
        case WM_RBUTTONDOWN: {
            int x = GET_X_LPARAM(lp), y = GET_Y_LPARAM(lp);
            if (x < kSidebarW) return 0;                        // sidebar: no paste (was inserting into the prompt)
            if (mouseReport(x, y, 2, true, false)) return 0;    // right-click to a mouse-aware app
            pasteClipboard();                                   // else right-click pastes into the terminal
            return 0;
        }
        case WM_RBUTTONUP: {
            int x = GET_X_LPARAM(lp), y = GET_Y_LPARAM(lp);
            mouseReport(x, y, 2, false, false);                 // release to a mouse-aware app (harmless otherwise)
            return 0;
        }
        case WM_SIZE:
            if (wp != SIZE_MINIMIZED) {
                if (g_toolbar) {   // toolbar spans the top; capture its height for the layout below it
                    SendMessageW(g_toolbar, TB_AUTOSIZE, 0, 0);
                    RECT tr; GetWindowRect(g_toolbar, &tr); g_toolbarH = tr.bottom - tr.top;
                }
                if (g_tree) MoveWindow(g_tree, 0, g_toolbarH, kSidebarW, HIWORD(lp) - g_toolbarH, TRUE);   // dock left, below the toolbar
                if (!g_sessions.empty()) syncPaneSizes();
            }
            return 0;
        case WM_APP_REFRESHTREE:   // posted from worker threads after a session-list / status change
            refreshTree();
            return 0;
        case WM_APP_TRAY:
            if (LOWORD(lp) == WM_RBUTTONUP || LOWORD(lp) == WM_CONTEXTMENU) showTrayMenu();
            else if (LOWORD(lp) == WM_LBUTTONDBLCLK) showMainWindow();
            return 0;
        case WM_NOTIFY: {
            auto* nm = (NMHDR*)lp;
            if (nm->idFrom == ID_TREE && nm->code == NM_CUSTOMDRAW) {   // italicise "working" agent rows
                auto* cd = (NMTVCUSTOMDRAW*)lp;
                if (cd->nmcd.dwDrawStage == CDDS_PREPAINT) return CDRF_NOTIFYITEMDRAW;
                if (cd->nmcd.dwDrawStage == CDDS_ITEMPREPAINT) {
                    LPARAM p = cd->nmcd.lItemlParam;
                    if (p >= 0 && p < (LPARAM)g_sessions.size() && !g_sessions[p]->exited &&
                        statusClass(g_sessions[p]->status) == AGST_WORKING && g_treeItalic) {
                        SelectObject(cd->nmcd.hdc, g_treeItalic);
                        return CDRF_NEWFONT;
                    }
                }
                return CDRF_DODEFAULT;
            }
            if (nm->code == TBN_GETINFOTIPW) {   // toolbar button hover tooltips ("hints")
                auto* it = (NMTBGETINFOTIPW*)lp;
                const wchar_t* tip = it->iItem == IDM_NEW ? L"New Session"
                                   : it->iItem == IDM_NEWWS ? L"New Workspace"
                                   : it->iItem == IDM_SPLIT ? L"Split / Unsplit" : L"";
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
                        InvalidateRect(hwnd, nullptr, FALSE);
                    }
                } else {                                        // workspace node -> make it the active "folder"
                    int w = (int)(-p - 1);
                    if (w >= 0 && w < (int)g_workspaces.size()) g_activeWs = w;
                }
                SetFocus(hwnd);   // keep typing going to the terminal, not the tree
            }
            if (nm->idFrom == ID_TREE && nm->code == NM_RCLICK) {   // right-click a node -> context menu
                POINT cp; GetCursorPos(&cp); ScreenToClient(g_tree, &cp);
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
                        SetWindowTextW(ed, bn.c_str());
                    } else {
                        int w = (int)(-di->item.lParam - 1);
                        if (w >= 0 && w < (int)g_workspaces.size()) SetWindowTextW(ed, g_workspaces[w].c_str());
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
                    PostMessageW(g_hwnd, WM_APP_REFRESHTREE, 0, 0);   // re-decorate with status/count
                }
                return 0;   // FALSE — we refresh the label ourselves
            }
            return 0;
        }
        case WM_COMMAND: {
            int id = LOWORD(wp);
            switch (id) {
                case IDM_NEW: newSessionDialog(); break;                                    // profile picker
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
                case IDM_RESTART: restartApp(); break;
                case IDM_SHOW: showMainWindow(); break;
                case IDM_EXIT: DestroyWindow(hwnd); break;
                case IDM_ABOUT:
                    MessageBoxW(hwnd, L"agwinterm lite\nA lightweight native terminal over the Rust pty-host.",
                                L"About", MB_OK | MB_ICONINFORMATION);
                    break;
                default:   // menu-bar / tray: close / split / next / copy / paste / previous
                    if (id >= IDM_CLOSE && id <= IDM_PREV) { runPaletteItem(id); InvalidateRect(hwnd, nullptr, FALSE); SetFocus(hwnd); }
                    break;
            }
            return 0;
        }
        case WM_EXITSIZEMOVE: saveWindowRect(); return 0;   // remember geometry after a user move/resize
        case WM_DESTROY: {
            saveWindowRect();                        // remember window size + position for next launch
            Shell_NotifyIconW(NIM_DELETE, &g_nid);   // remove the tray icon
            for (Session* s : g_sessions) killSession(s);
            agwinterm_ptyhost_Request req = agwinterm_ptyhost_Request_init_default;
            agwinterm_ptyhost_Reply rep = agwinterm_ptyhost_Reply_init_default;
            req.which_cmd = agwinterm_ptyhost_Request_shutdown_tag;
            request(req, &rep);
            PostQuitMessage(0);
            return 0;
        }
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}


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
    struct Spec { int ws; std::string name, app, cwd; std::vector<std::string> args; };
    std::vector<Spec> specs;
    int activeWs = 0;
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
        } else if (ff[0] == "A" && ff.size() >= 2) activeWs = atoi(ff[1].c_str());
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
        if (s) { s->name = widen(sp.name); if (firstIdx < 0) firstIdx = (int)g_sessions.size() - 1; }
    }
    g_restoring = false;
    if (firstIdx < 0) return false;
    g_pane[0] = firstIdx; g_pane[1] = -1; g_focus = 0;
    g_activeWs = (activeWs >= 0 && activeWs < (int)g_workspaces.size()) ? activeWs : 0;
    syncPaneSizes();
    refreshTree();
    return true;
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR, int show) {
    InitializeCriticalSection(&g_lock);
    InitializeCriticalSection(&g_reqLock);
    loadCore();
    Gdiplus::GdiplusStartupInput gsi;
    Gdiplus::GdiplusStartup(&g_gdiplusTok, &gsi, nullptr);   // for PNG toolbar-icon decode

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
    WNDCLASSW wc{};
    wc.lpfnWndProc = wndProc;
    wc.hInstance = inst;
    wc.lpszClassName = L"AgwintermLite";
    wc.hCursor = LoadCursorW(nullptr, (LPCWSTR)IDC_IBEAM);
    wc.hIcon = g_appIcon;   // 2000's-style terminal icon (window + taskbar)
    RegisterClassW(&wc);
    RECT want{ 0, 0, kSidebarW + 100 * g_cw, 30 * g_ch };
    // WS_CLIPCHILDREN keeps the terminal paint out of the native tree child.
    AdjustWindowRect(&want, WS_OVERLAPPEDWINDOW, TRUE);   // TRUE = has a menu bar
    int wx = CW_USEDEFAULT, wy = CW_USEDEFAULT, ww = want.right - want.left, wh = want.bottom - want.top;
    RECT sr; bool startMax = false;
    if (loadWindowRect(&sr, &startMax)) { wx = sr.left; wy = sr.top; ww = sr.right - sr.left; wh = sr.bottom - sr.top; }
    g_hwnd = CreateWindowW(L"AgwintermLite", L"agwinterm lite", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                           wx, wy, ww, wh, nullptr, buildMenuBar(), inst, nullptr);

    // Native SysTreeView32 sidebar docked on the left; picking a node selects that session.
    RECT cr; GetClientRect(g_hwnd, &cr);
    g_tree = CreateWindowExW(WS_EX_CLIENTEDGE, WC_TREEVIEWW, L"",
                             WS_CHILD | WS_VISIBLE | TVS_SHOWSELALWAYS | TVS_NOHSCROLL |
                             TVS_HASBUTTONS | TVS_HASLINES | TVS_LINESATROOT | TVS_EDITLABELS,
                             0, 0, kSidebarW, cr.bottom, g_hwnd, (HMENU)ID_TREE, inst, nullptr);
    SendMessageW(g_tree, WM_SETFONT, (WPARAM)(HFONT)GetStockObject(DEFAULT_GUI_FONT), TRUE);
    { LOGFONTW lf{}; GetObjectW((HFONT)GetStockObject(DEFAULT_GUI_FONT), sizeof(lf), &lf); lf.lfItalic = TRUE; g_treeItalic = CreateFontIndirectW(&lf); }   // "working" rows

    // Native toolbar across the top: New Session / New Workspace / Split, as classic 16px Silk icons
    // with hover tooltips (TBSTYLE_TOOLTIPS). No TBSTYLE_FLAT: flat toolbars hot-track and can leave a
    // button stuck "hot"; classic raised 3D buttons have no hover state and suit the old-skool look.
    buildToolbarImages();
    g_toolbar = CreateWindowExW(0, TOOLBARCLASSNAMEW, nullptr,
                                WS_CHILD | WS_VISIBLE | TBSTYLE_TOOLTIPS | CCS_TOP,
                                0, 0, 0, 0, g_hwnd, (HMENU)ID_TOOLBAR, inst, nullptr);
    SendMessageW(g_toolbar, TB_BUTTONSTRUCTSIZE, sizeof(TBBUTTON), 0);
    SendMessageW(g_toolbar, TB_SETIMAGELIST, 0, (LPARAM)g_tbImages);
    TBBUTTON tb[3] = {};
    tb[0].iBitmap = 0; tb[0].idCommand = IDM_NEW;   tb[0].fsState = TBSTATE_ENABLED; tb[0].fsStyle = BTNS_AUTOSIZE;
    tb[1].iBitmap = 1; tb[1].idCommand = IDM_NEWWS; tb[1].fsState = TBSTATE_ENABLED; tb[1].fsStyle = BTNS_AUTOSIZE;
    tb[2].iBitmap = 2; tb[2].idCommand = IDM_SPLIT; tb[2].fsState = TBSTATE_ENABLED; tb[2].fsStyle = BTNS_AUTOSIZE;
    SendMessageW(g_toolbar, TB_ADDBUTTONS, 3, (LPARAM)tb);
    SendMessageW(g_toolbar, TB_AUTOSIZE, 0, 0);
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

    ShowWindow(g_hwnd, startMax ? SW_SHOWMAXIMIZED : show);   // restore a maximized session maximized

    if (!restoreSessions()) {   // rebuild the saved workspaces/sessions, or open a fresh default one
        int cols, rows;
        paneGridSize(0, &cols, &rows);
        if (!newSession(cols, rows)) fatal(L"could not create the first session");
        refreshTree();
    }
    CreateThread(nullptr, 0, ctlServerThread, nullptr, 0, nullptr);   // agwintermctl --pipe agwinterm-lite
    InvalidateRect(g_hwnd, nullptr, FALSE);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return 0;
}
