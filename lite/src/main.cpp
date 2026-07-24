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
#include <string>
#include <vector>

#include "proto/ptyhost.pb.h"
#include "proto/pb_encode.h"
#include "proto/pb_decode.h"
#include "control.h"

// ---- agwinterm-core C ABI (ABI v7) ----
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
    uint32_t scrollTop, scrollBottom, markCount;
};
static uint32_t (*core_abi)();
static void* (*emu_new)(uint32_t, uint32_t);
static void (*emu_free)(void*);
static bool (*emu_feed)(void*, const uint8_t*, uint32_t);
static bool (*emu_resize)(void*, uint32_t, uint32_t);
static bool (*emu_info)(void*, FfiEmuInfo*);
static bool (*emu_copy_grid)(void*, FfiCell*, uint32_t);
static bool (*emu_copy_history_row)(void*, uint32_t, FfiCell*, uint32_t);

static constexpr uint32_t kRequiredAbi = 7;
static constexpr uint32_t kAttrBold = 1, kAttrItalic = 2, kAttrUnderline = 4,
                          kAttrInverse = 8, kAttrDim = 16, kAttrStrike = 32;
static constexpr uint32_t kProtocolVersion = 2;
static constexpr int kSidebarW = 180;

// ---- sessions & layout ----
struct Session {
    std::string id;
    std::string status = "idle";   // control-API agent status (sidebar dot)
    void* emu = nullptr;
    HANDLE data = INVALID_HANDLE_VALUE;
    HANDLE reader = nullptr;
    int scrollOff = 0;          // rows scrolled up into history (0 = live)
    bool exited = false;
    std::vector<FfiCell> grid;  // paint snapshot buffer
    std::vector<FfiCell> hrow;
};

static HWND g_hwnd;
static HFONT g_fonts[4];        // [bold][italic]
static int g_cw = 8, g_ch = 16;
static CRITICAL_SECTION g_lock; // guards every session's emu + the session list shape
static HANDLE g_control = INVALID_HANDLE_VALUE;
static std::vector<Session*> g_sessions;
static int g_pane[2] = { 0, -1 };   // session index per pane; pane[1] = -1 → no split
static int g_focus = 0;             // focused pane (0/1)
static int g_seq = 1;
static const wchar_t* kAppId = L"agwinterm-lite";

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
    if (!core_abi || !emu_new || !emu_feed || !emu_info || !emu_copy_grid || !emu_resize || !emu_free || !emu_copy_history_row)
        fatal(L"agwinterm_core.dll: exports missing");
    if (core_abi() != kRequiredAbi) fatal(L"agwinterm_core.dll: ABI mismatch (need v7)");
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
    int contentW = client.right - kSidebarW;
    if (g_pane[1] < 0) { *out = { contentX, 0, client.right, client.bottom }; return; }
    int half = contentW / 2;
    if (pane == 0) *out = { contentX, 0, contentX + half - 1, client.bottom };
    else *out = { contentX + half + 1, 0, client.right, client.bottom };
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
    int idx = g_pane[g_focus];
    return (idx >= 0 && idx < (int)g_sessions.size()) ? g_sessions[idx] : nullptr;
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

// ---- session lifecycle ----
static DWORD WINAPI readerThread(void* param) {
    Session* s = (Session*)param;
    std::vector<uint8_t> buf(64 * 1024);
    DWORD n;
    while ((n = ovIo(s->data, false, nullptr, buf.data(), (DWORD)buf.size())) > 0) {
        EnterCriticalSection(&g_lock);
        emu_feed(s->emu, buf.data(), n);
        LeaveCriticalSection(&g_lock);
        InvalidateRect(g_hwnd, nullptr, FALSE);
    }
    s->exited = true;   // EOF: child exited, host shut down, or we were superseded
    InvalidateRect(g_hwnd, nullptr, FALSE);
    return 0;
}

static Session* newSession(int cols, int rows) {
    char idbuf[64];
    wsprintfA(idbuf, "lite-%d", g_seq++);
    agwinterm_ptyhost_Request req = agwinterm_ptyhost_Request_init_default;
    agwinterm_ptyhost_Reply rep = agwinterm_ptyhost_Reply_init_default;
    req.which_cmd = agwinterm_ptyhost_Request_create_tag;
    strcpy_s(req.cmd.create.id, idbuf);
    req.cmd.create.cols = (uint32_t)cols;
    req.cmd.create.rows = (uint32_t)rows;
    strcpy_s(req.cmd.create.app, "powershell.exe");
    req.cmd.create.args_count = 1;
    strcpy_s(req.cmd.create.args[0], "-NoLogo");
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
    s->emu = emu_new(cols, rows);
    s->data = openPipe(std::wstring(rep.body.attach.pipe, rep.body.attach.pipe + strlen(rep.body.attach.pipe)), 5000, true);
    if (s->data == INVALID_HANDLE_VALUE) { emu_free(s->emu); delete s; return nullptr; }
    s->reader = CreateThread(nullptr, 0, readerThread, s, 0, nullptr);
    EnterCriticalSection(&g_lock);
    g_sessions.push_back(s);
    LeaveCriticalSection(&g_lock);
    return s;
}

static void killSession(Session* s) {
    agwinterm_ptyhost_Request req = agwinterm_ptyhost_Request_init_default;
    agwinterm_ptyhost_Reply rep = agwinterm_ptyhost_Reply_init_default;
    req.which_cmd = agwinterm_ptyhost_Request_kill_tag;
    strcpy_s(req.cmd.kill.id, s->id.c_str());
    request(req, &rep);
}

static void closeFocused() {
    Session* s = focusedSession();
    if (!s) return;
    killSession(s);
    EnterCriticalSection(&g_lock);
    int idx = g_pane[g_focus];
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

static void paintPane(HDC mem, int pane, RECT pr) {
    int idx = g_pane[pane];
    if (idx < 0 || idx >= (int)g_sessions.size()) return;
    Session* s = g_sessions[idx];

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
            if (attrs & kAttrInverse) { uint32_t t = fg; fg = bgc; bgc = t; }
            uint32_t styleKey = attrs & (kAttrBold | kAttrItalic | kAttrUnderline | kAttrStrike | kAttrDim);
            uint32_t start = c;
            text.clear();
            dx.clear();
            while (c < info.cols) {
                const FfiCell& cc = view[r * info.cols + c];
                if (cc.width == 0) { c++; continue; }
                uint32_t f2 = cc.fg, b2 = cc.bg, a2 = cc.attrs;
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
                    text.push_back((wchar_t)(cc.rune ? cc.rune : L' '));
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
            if (styleKey & (kAttrUnderline | kAttrStrike)) {
                HBRUSH b = CreateSolidBrush(toColorRef(fg, (styleKey & kAttrDim) != 0));
                if (styleKey & kAttrUnderline) { RECT u{ x, y + g_ch - 2, clip.right, y + g_ch - 1 }; FillRect(mem, &u, b); }
                if (styleKey & kAttrStrike) { RECT k{ x, y + g_ch / 2, clip.right, y + g_ch / 2 + 1 }; FillRect(mem, &k, b); }
                DeleteObject(b);
            }
        }
    }

    // Cursor (only at live view, only in the focused pane).
    if (off == 0 && info.cursorVisible && pane == g_focus && info.cursorCol < info.cols) {
        RECT cur{ pr.left + (LONG)info.cursorCol * g_cw, pr.top + (LONG)info.cursorRow * g_ch,
                  pr.left + (LONG)(info.cursorCol + 1) * g_cw, pr.top + (LONG)(info.cursorRow + 1) * g_ch };
        if (cur.right <= pr.right) InvertRect(mem, &cur);
    }
    // Scrollback indicator: thin right-edge stripe while scrolled.
    if (off > 0) {
        RECT bar{ pr.right - 3, pr.top, pr.right, pr.bottom };
        HBRUSH b = CreateSolidBrush(RGB(90, 140, 200));
        FillRect(mem, &bar, b);
        DeleteObject(b);
    }
}

static void paintSidebar(HDC mem, RECT rc) {
    RECT side{ 0, 0, kSidebarW, rc.bottom };
    HBRUSH bg = CreateSolidBrush(RGB(18, 18, 22));
    FillRect(mem, &side, bg);
    DeleteObject(bg);
    SelectObject(mem, g_fonts[0]);
    SetBkMode(mem, TRANSPARENT);
    SetTextColor(mem, RGB(140, 140, 150));
    RECT title{ 10, 8, kSidebarW - 8, 8 + g_ch };
    DrawTextW(mem, L"sessions  (Ctrl+T new)", -1, &title, DT_LEFT | DT_SINGLELINE);
    for (int i = 0; i < (int)g_sessions.size(); i++) {
        int y = 12 + (i + 1) * (g_ch + 8);
        bool inPane = g_pane[0] == i || g_pane[1] == i;
        bool focused = g_pane[g_focus] == i;
        if (focused) {
            RECT row{ 0, y - 4, kSidebarW, y + g_ch + 4 };
            HBRUSH hb = CreateSolidBrush(RGB(38, 40, 48));
            FillRect(mem, &row, hb);
            DeleteObject(hb);
        }
        SetTextColor(mem, g_sessions[i]->exited ? RGB(120, 90, 90)
                          : focused ? RGB(230, 230, 235)
                          : inPane ? RGB(190, 190, 200) : RGB(150, 150, 160));
        // Status dot (control-API agent status): idle gray, active blue, blocked red, completed green.
        COLORREF dot = RGB(110, 110, 120);
        const std::string& st = g_sessions[i]->status;
        if (g_sessions[i]->exited) dot = RGB(140, 80, 80);
        else if (st == "active") dot = RGB(80, 140, 230);
        else if (st == "blocked") dot = RGB(220, 80, 80);
        else if (st == "completed") dot = RGB(70, 190, 100);
        RECT d{ 4, y + g_ch / 2 - 3, 10, y + g_ch / 2 + 3 };
        HBRUSH db = CreateSolidBrush(dot);
        FillRect(mem, &d, db);
        DeleteObject(db);
        wchar_t name[64];
        wsprintfW(name, L"%s%S", g_sessions[i]->exited ? L"× " : L"", g_sessions[i]->id.c_str());
        RECT row{ 14, y, kSidebarW - 8, y + g_ch };
        DrawTextW(mem, name, -1, &row, DT_LEFT | DT_SINGLELINE);
    }
}

static void paint(HDC dc, RECT rc) {
    HDC mem = CreateCompatibleDC(dc);
    HBITMAP bmp = CreateCompatibleBitmap(dc, rc.right, rc.bottom);
    HGDIOBJ oldBmp = SelectObject(mem, bmp);
    HBRUSH bg = CreateSolidBrush(RGB(0, 0, 0));
    FillRect(mem, &rc, bg);
    DeleteObject(bg);

    paintSidebar(mem, rc);
    for (int p = 0; p < 2; p++) {
        if (g_pane[p] < 0) continue;
        RECT pr;
        paneRect(p, rc, &pr);
        paintPane(mem, p, pr);
    }
    if (g_pane[1] >= 0) {   // split divider
        RECT pr0;
        paneRect(0, rc, &pr0);
        RECT div{ pr0.right, 0, pr0.right + 2, rc.bottom };
        HBRUSH b = CreateSolidBrush(RGB(60, 62, 70));
        FillRect(mem, &div, b);
        DeleteObject(b);
    }

    BitBlt(dc, 0, 0, rc.right, rc.bottom, mem, 0, 0, SRCCOPY);
    SelectObject(mem, oldBmp);
    DeleteObject(bmp);
    DeleteDC(mem);
}

// ---- input ----
static void sendBytes(const char* bytes, int len) {
    Session* s = focusedSession();
    if (s && s->data != INVALID_HANDLE_VALUE) ovIo(s->data, true, bytes, nullptr, (DWORD)len);
}

static void sendUtf8(wchar_t wc) {
    char utf8[8];
    int n = WideCharToMultiByte(CP_UTF8, 0, &wc, 1, utf8, sizeof utf8, nullptr, nullptr);
    if (n > 0) sendBytes(utf8, n);
}

static bool ctrlDown() { return (GetKeyState(VK_CONTROL) & 0x8000) != 0; }
static bool shiftDown() { return (GetKeyState(VK_SHIFT) & 0x8000) != 0; }

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

static bool handleKeyDown(WPARAM vk) {
    if (ctrlDown() && shiftDown()) {
        switch (vk) {
            case 'D': {   // split toggle
                if (g_pane[1] < 0) {
                    // Prefer a DIFFERENT session for the second pane (wraps); same-session
                    // mirroring still works if there is only one.
                    g_pane[1] = (int)g_sessions.size() > 1
                                ? (g_pane[0] + 1) % (int)g_sessions.size() : g_pane[0];
                } else { g_pane[1] = -1; g_focus = 0; }
                syncPaneSizes();
                InvalidateRect(g_hwnd, nullptr, FALSE);
                return true;
            }
            case VK_LEFT: g_focus = 0; InvalidateRect(g_hwnd, nullptr, FALSE); return true;
            case VK_RIGHT: if (g_pane[1] >= 0) g_focus = 1; InvalidateRect(g_hwnd, nullptr, FALSE); return true;
        }
    }
    if (ctrlDown()) {
        switch (vk) {
            case 'T': {
                int cols, rows;
                paneGridSize(g_focus, &cols, &rows);
                Session* s = newSession(cols, rows);
                if (s) { g_pane[g_focus] = (int)g_sessions.size() - 1; InvalidateRect(g_hwnd, nullptr, FALSE); }
                return true;
            }
            case 'W': closeFocused(); return true;
            case VK_TAB: {
                if (!g_sessions.empty()) {
                    g_pane[g_focus] = (g_pane[g_focus] + 1) % (int)g_sessions.size();
                    syncPaneSizes();
                    InvalidateRect(g_hwnd, nullptr, FALSE);
                }
                return true;
            }
        }
    }
    if (shiftDown() && vk == VK_PRIOR) { scrollFocused(+10); return true; }
    if (shiftDown() && vk == VK_NEXT) { scrollFocused(-10); return true; }
    const char* seq = nullptr;
    switch (vk) {
        case VK_UP: seq = "\x1b[A"; break;
        case VK_DOWN: seq = "\x1b[B"; break;
        case VK_RIGHT: seq = "\x1b[C"; break;
        case VK_LEFT: seq = "\x1b[D"; break;
        case VK_HOME: seq = "\x1b[H"; break;
        case VK_END: seq = "\x1b[F"; break;
        case VK_DELETE: seq = "\x1b[3~"; break;
        case VK_PRIOR: seq = "\x1b[5~"; break;
        case VK_NEXT: seq = "\x1b[6~"; break;
        default: return false;
    }
    if (Session* s = focusedSession()) s->scrollOff = 0;   // typing snaps back to live
    sendBytes(seq, (int)strlen(seq));
    return true;
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
            if (ctrlDown() && (wc == 20 || wc == 23 || wc == 4)) return 0;   // eaten by shortcuts (T/W/D)
            if (Session* s = focusedSession()) s->scrollOff = 0;
            if (wc == L'\r') { sendBytes("\r", 1); return 0; }
            sendUtf8(wc);
            return 0;
        }
        case WM_KEYDOWN:
            if (handleKeyDown(wp)) return 0;
            break;
        case WM_MOUSEWHEEL:
            scrollFocused(GET_WHEEL_DELTA_WPARAM(wp) > 0 ? 3 : -3);
            return 0;
        case WM_LBUTTONDOWN: {
            int x = GET_X_LPARAM(lp), y = GET_Y_LPARAM(lp);
            if (x < kSidebarW) {   // sidebar row hit-test
                int i = (y - 12 - 8) / (g_ch + 8) - 0;   // rows start at 12 + (i+1)*(g_ch+8)
                i = (y - 12) / (g_ch + 8) - 1;
                if (i >= 0 && i < (int)g_sessions.size()) {
                    g_pane[g_focus] = i;
                    syncPaneSizes();
                    InvalidateRect(hwnd, nullptr, FALSE);
                }
            } else if (g_pane[1] >= 0) {   // click focuses a pane
                RECT rc;
                GetClientRect(hwnd, &rc);
                RECT p0;
                paneRect(0, rc, &p0);
                g_focus = (x <= p0.right) ? 0 : 1;
                InvalidateRect(hwnd, nullptr, FALSE);
            }
            SetFocus(hwnd);
            return 0;
        }
        case WM_SIZE:
            if (wp != SIZE_MINIMIZED && !g_sessions.empty()) syncPaneSizes();
            return 0;
        case WM_DESTROY: {
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
int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR, int show) {
    InitializeCriticalSection(&g_lock);
    InitializeCriticalSection(&g_reqLock);
    loadCore();

    // Bundled Meslo Nerd Font (process-private); Consolas fallback.
    std::wstring ttf = exeDir() + L"\\MesloLGLDZNerdFont-Regular.ttf";
    bool haveMeslo = AddFontResourceExW(ttf.c_str(), FR_PRIVATE, 0) > 0;
    const wchar_t* face = haveMeslo ? L"MesloLGLDZ Nerd Font" : L"Consolas";
    for (int i = 0; i < 4; i++)
        g_fonts[i] = CreateFontW(-16, 0, 0, 0, (i & 1) ? FW_BOLD : FW_NORMAL, (i & 2) ? TRUE : FALSE,
                                 FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                                 CLEARTYPE_QUALITY, FIXED_PITCH | FF_MODERN, face);
    {
        HDC dc = GetDC(nullptr);
        HGDIOBJ old = SelectObject(dc, g_fonts[0]);
        TEXTMETRICW tm;
        GetTextMetricsW(dc, &tm);
        g_cw = tm.tmAveCharWidth;
        g_ch = tm.tmHeight;
        SelectObject(dc, old);
        ReleaseDC(nullptr, dc);
    }

    connectControl();

    WNDCLASSW wc{};
    wc.lpfnWndProc = wndProc;
    wc.hInstance = inst;
    wc.lpszClassName = L"AgwintermLite";
    wc.hCursor = LoadCursorW(nullptr, (LPCWSTR)IDC_IBEAM);
    RegisterClassW(&wc);
    RECT want{ 0, 0, kSidebarW + 100 * g_cw, 30 * g_ch };
    AdjustWindowRect(&want, WS_OVERLAPPEDWINDOW, FALSE);
    g_hwnd = CreateWindowW(L"AgwintermLite", L"agwinterm lite", WS_OVERLAPPEDWINDOW,
                           CW_USEDEFAULT, CW_USEDEFAULT, want.right - want.left, want.bottom - want.top,
                           nullptr, nullptr, inst, nullptr);
    ShowWindow(g_hwnd, show);

    int cols, rows;
    paneGridSize(0, &cols, &rows);
    if (!newSession(cols, rows)) fatal(L"could not create the first session");
    CreateThread(nullptr, 0, ctlServerThread, nullptr, 0, nullptr);   // agwintermctl --pipe agwinterm-lite
    InvalidateRect(g_hwnd, nullptr, FALSE);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return 0;
}
