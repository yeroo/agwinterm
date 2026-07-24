// agwinterm-lite M0 (issue #134): a thin Win32+GDI client over the Rust server.
//
// Contains ZERO terminal logic and ZERO ConPTY code: it spawns/attaches to
// agwinterm-ptyhost.exe (the protocol-proven Rust host), feeds the data-pipe
// byte stream into a local agwinterm-core emulator (C ABI, the oracle-validated
// crate) as its render replica, and paints the grid with classic-conhost
// technology: ExtTextOutW + lpDx advances (grid-anchored by construction),
// memory-DC double buffer, no Direct2D, no GPU requirements.
//
// M0 scope: one session, typing, scrolling, colors+inverse, resize, clean kill
// on close. M1: layout parity (sidebar/splits), styles, selection, dirty rows.

#include <windows.h>
#include <string>
#include <vector>

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

static constexpr uint32_t kRequiredAbi = 7;
static constexpr uint32_t kAttrInverse = 8;

// ---- globals (M0 single-window simplicity) ----
static HWND g_hwnd;
static HFONT g_font;
static int g_cw = 8, g_ch = 16;
static void* g_emu;
static CRITICAL_SECTION g_lock;          // guards g_emu (reader thread vs paint/resize)
static HANDLE g_control = INVALID_HANDLE_VALUE;
static HANDLE g_data = INVALID_HANDLE_VALUE;
static std::string g_sessionId = "lite-session-1";
static std::vector<FfiCell> g_grid;
static const wchar_t* kAppId = L"agwinterm-lite";

static void fatal(const wchar_t* msg) {
    MessageBoxW(nullptr, msg, L"agwinterm-lite", MB_ICONERROR);
    ExitProcess(1);
}

// ---- minimal JSON helpers (M0: we control both ends; full parser lands in M1) ----
static std::string jstr(const std::string& json, const std::string& key) {
    std::string pat = "\"" + key + "\":\"";
    size_t p = json.find(pat);
    if (p == std::string::npos) return "";
    p += pat.size();
    std::string out;
    while (p < json.size() && json[p] != '"') {
        if (json[p] == '\\' && p + 1 < json.size()) { out += json[p + 1]; p += 2; }
        else out += json[p++];
    }
    return out;
}
static int jint(const std::string& json, const std::string& key) {
    std::string pat = "\"" + key + "\":";
    size_t p = json.find(pat);
    if (p == std::string::npos) return 0;
    return atoi(json.c_str() + p + pat.size());
}
static std::string jesc(const std::string& s) {
    std::string out;
    for (char c : s) {
        if (c == '"' || c == '\\') { out += '\\'; out += c; }
        else out += c;
    }
    return out;
}

// ---- control pipe: sync request/response (one line out, one line in) ----
static std::string request(const std::string& line) {
    std::string msg = line + "\n";
    DWORD n = 0;
    if (!WriteFile(g_control, msg.data(), (DWORD)msg.size(), &n, nullptr)) return "";
    std::string reply;
    char c;
    while (ReadFile(g_control, &c, 1, &n, nullptr) && n == 1) {
        if (c == '\n') break;
        if (c != '\r') reply += c;
    }
    return reply;
}

static HANDLE openPipe(const std::wstring& name, int timeoutMs, bool overlapped) {
    // The DATA pipe must be overlapped: a non-overlapped duplex pipe SERIALIZES the
    // handle, so the reader thread's pending ReadFile would block the UI thread's
    // keystroke WriteFile forever (the same deadlock the Rust host hit and fixed).
    // The CONTROL pipe stays sync — strict request/response on one thread.
    std::wstring full = L"\\\\.\\pipe\\" + name;
    DWORD flags = overlapped ? FILE_FLAG_OVERLAPPED : 0;
    for (int waited = 0; waited <= timeoutMs; waited += 100) {
        HANDLE h = CreateFileW(full.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, flags, nullptr);
        if (h != INVALID_HANDLE_VALUE) return h;
        Sleep(100);
    }
    return INVALID_HANDLE_VALUE;
}

// One overlapped op, blocking-style with its own event.
static DWORD ovIo(bool write, const void* wbuf, void* rbuf, DWORD len) {
    OVERLAPPED ov{};
    ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    BOOL issued = write
        ? WriteFile(g_data, wbuf, len, nullptr, &ov)
        : ReadFile(g_data, rbuf, len, nullptr, &ov);
    DWORD n = 0;
    if (issued || GetLastError() == ERROR_IO_PENDING) {
        if (!GetOverlappedResult(g_data, &ov, &n, TRUE)) n = 0;
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
    std::wstring dll = exeDir() + L"\\agwinterm_core.dll";
    HMODULE m = LoadLibraryW(dll.c_str());
    if (!m) fatal(L"agwinterm_core.dll not found next to the exe");
    core_abi = (decltype(core_abi))GetProcAddress(m, "agwcore_abi_version");
    emu_new = (decltype(emu_new))GetProcAddress(m, "agwcore_emu_new");
    emu_free = (decltype(emu_free))GetProcAddress(m, "agwcore_emu_free");
    emu_feed = (decltype(emu_feed))GetProcAddress(m, "agwcore_emu_feed");
    emu_resize = (decltype(emu_resize))GetProcAddress(m, "agwcore_emu_resize");
    emu_info = (decltype(emu_info))GetProcAddress(m, "agwcore_emu_info");
    emu_copy_grid = (decltype(emu_copy_grid))GetProcAddress(m, "agwcore_emu_copy_grid");
    if (!core_abi || !emu_new || !emu_feed || !emu_info || !emu_copy_grid || !emu_resize || !emu_free)
        fatal(L"agwinterm_core.dll: exports missing");
    if (core_abi() != kRequiredAbi) fatal(L"agwinterm_core.dll: ABI mismatch (need v7)");
}

static void connectHost(int cols, int rows) {
    std::wstring control = std::wstring(kAppId) + L"-ptyhost";
    g_control = openPipe(control, 0, false);
    if (g_control == INVALID_HANDLE_VALUE) {
        // Spawn the Rust host (lite's own instance id — the main app keeps its own).
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
    std::string hello = request("{\"cmd\":\"hello\",\"protocol\":1}");
    if (hello.find("\"ok\":true") == std::string::npos) fatal(L"pty-host hello failed");

    char create[512];
    wsprintfA(create,
        "{\"cmd\":\"create\",\"id\":\"%s\",\"cols\":%d,\"rows\":%d,"
        "\"app\":\"powershell.exe\",\"args\":[\"-NoLogo\"]}",
        g_sessionId.c_str(), cols, rows);
    std::string created = request(create);
    if (created.find("\"ok\":true") == std::string::npos) fatal(L"pty-host create failed");

    char attach[256];
    wsprintfA(attach, "{\"cmd\":\"attach\",\"id\":\"%s\"}", g_sessionId.c_str());
    std::string att = request(attach);
    std::string dataName = jstr(att, "pipe");
    if (dataName.empty()) fatal(L"pty-host attach failed");
    g_data = openPipe(std::wstring(dataName.begin(), dataName.end()), 5000, true);
    if (g_data == INVALID_HANDLE_VALUE) fatal(L"data pipe connect failed");
}

// ---- data-pipe reader: bytes → replica emulator → repaint ----
static DWORD WINAPI readerThread(void*) {
    std::vector<uint8_t> buf(64 * 1024);
    DWORD n;
    while ((n = ovIo(false, nullptr, buf.data(), (DWORD)buf.size())) > 0) {
        EnterCriticalSection(&g_lock);
        emu_feed(g_emu, buf.data(), n);
        LeaveCriticalSection(&g_lock);
        InvalidateRect(g_hwnd, nullptr, FALSE);
    }
    return 0;
}

static void sendInput(const char* bytes, int len) {
    if (g_data != INVALID_HANDLE_VALUE) ovIo(true, bytes, nullptr, (DWORD)len);
}

// ---- GDI paint: color runs + ExtTextOutW with lpDx (grid-anchored by construction) ----
static COLORREF toColorRef(uint32_t packed) {  // 0x00RRGGBB → COLORREF 0x00BBGGRR
    return RGB((packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF);
}

static void paint(HDC dc, int width, int height) {
    FfiEmuInfo info{};
    EnterCriticalSection(&g_lock);
    emu_info(g_emu, &info);
    size_t need = (size_t)info.cols * info.rows;
    if (g_grid.size() < need) g_grid.resize(need);
    emu_copy_grid(g_emu, g_grid.data(), (uint32_t)g_grid.size());
    LeaveCriticalSection(&g_lock);

    HDC mem = CreateCompatibleDC(dc);
    HBITMAP bmp = CreateCompatibleBitmap(dc, width, height);
    HGDIOBJ oldBmp = SelectObject(mem, bmp);
    HGDIOBJ oldFont = SelectObject(mem, g_font);
    RECT all{ 0, 0, width, height };
    HBRUSH bg = CreateSolidBrush(RGB(0, 0, 0));
    FillRect(mem, &all, bg);
    DeleteObject(bg);

    std::vector<wchar_t> text;
    std::vector<INT> dx;
    for (uint32_t r = 0; r < info.rows; r++) {
        uint32_t c = 0;
        while (c < info.cols) {
            const FfiCell& cell = g_grid[r * info.cols + c];
            if (cell.width == 0) { c++; continue; }
            uint32_t fg = cell.fg, bgc = cell.bg;
            if (cell.attrs & kAttrInverse) { uint32_t t = fg; fg = bgc; bgc = t; }
            // Coalesce a same-color run; every cell gets its own lpDx advance so
            // fallback glyphs can NEVER derail the grid (the #120 lesson, solved
            // in GDI by construction).
            uint32_t start = c;
            text.clear();
            dx.clear();
            while (c < info.cols) {
                const FfiCell& cc = g_grid[r * info.cols + c];
                if (cc.width == 0) { c++; continue; }
                uint32_t f2 = cc.fg, b2 = cc.bg;
                if (cc.attrs & kAttrInverse) { uint32_t t = f2; f2 = b2; b2 = t; }
                if (f2 != fg || b2 != bgc) break;
                if (cc.rune > 0xFFFF) {
                    text.push_back(0xFFFD);           // astral: placeholder in M0
                    dx.push_back(g_cw * (int)cc.width);
                } else {
                    text.push_back((wchar_t)(cc.rune ? cc.rune : L' '));
                    dx.push_back(g_cw * (int)cc.width);
                }
                c += cc.width;
            }
            SetTextColor(mem, toColorRef(fg));
            SetBkColor(mem, toColorRef(bgc));
            SetBkMode(mem, OPAQUE);
            RECT clip{ (LONG)(start * g_cw), (LONG)(r * g_ch), (LONG)(c * g_cw), (LONG)((r + 1) * g_ch) };
            ExtTextOutW(mem, start * g_cw, r * g_ch, ETO_OPAQUE | ETO_CLIPPED, &clip,
                        text.data(), (UINT)text.size(), dx.data());
        }
    }

    if (info.cursorVisible && info.cursorCol < info.cols) {
        RECT cur{ (LONG)(info.cursorCol * g_cw), (LONG)(info.cursorRow * g_ch),
                  (LONG)((info.cursorCol + 1) * g_cw), (LONG)((info.cursorRow + 1) * g_ch) };
        InvertRect(mem, &cur);
    }

    BitBlt(dc, 0, 0, width, height, mem, 0, 0, SRCCOPY);
    SelectObject(mem, oldFont);
    SelectObject(mem, oldBmp);
    DeleteObject(bmp);
    DeleteDC(mem);
}

// ---- input encoding ----
static void sendUtf8(wchar_t wc) {
    char utf8[8];
    int n = WideCharToMultiByte(CP_UTF8, 0, &wc, 1, utf8, sizeof utf8, nullptr, nullptr);
    if (n > 0) sendInput(utf8, n);
}

static bool sendSpecialKey(WPARAM vk) {
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
    sendInput(seq, (int)strlen(seq));
    return true;
}

static void resizeToClient(int width, int height) {
    int cols = width / g_cw, rows = height / g_ch;
    if (cols < 2 || rows < 2) return;
    EnterCriticalSection(&g_lock);
    emu_resize(g_emu, cols, rows);
    LeaveCriticalSection(&g_lock);
    char msg[256];
    wsprintfA(msg, "{\"cmd\":\"resize\",\"id\":\"%s\",\"cols\":%d,\"rows\":%d}", g_sessionId.c_str(), cols, rows);
    request(msg);
}

static LRESULT CALLBACK wndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
        case WM_PAINT: {
            PAINTSTRUCT ps;
            HDC dc = BeginPaint(hwnd, &ps);
            RECT rc;
            GetClientRect(hwnd, &rc);
            paint(dc, rc.right, rc.bottom);
            EndPaint(hwnd, &ps);
            return 0;
        }
        case WM_ERASEBKGND:
            return 1; // double-buffered
        case WM_CHAR: {
            wchar_t wc = (wchar_t)wp;
            if (wc == L'\r') { sendInput("\r", 1); return 0; }
            sendUtf8(wc);
            return 0;
        }
        case WM_KEYDOWN:
            if (sendSpecialKey(wp)) return 0;
            break;
        case WM_SIZE:
            if (wp != SIZE_MINIMIZED && g_emu) resizeToClient(LOWORD(lp), HIWORD(lp));
            return 0;
        case WM_DESTROY: {
            // M0 hygiene: explicit close kills the session (adoption arrives in M1).
            char msg2[256];
            wsprintfA(msg2, "{\"cmd\":\"kill\",\"id\":\"%s\"}", g_sessionId.c_str());
            request(msg2);
            request("{\"cmd\":\"shutdown\"}");
            PostQuitMessage(0);
            return 0;
        }
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR, int show) {
    InitializeCriticalSection(&g_lock);
    loadCore();

    // Bundled Meslo Nerd Font (Boris's favourite): loaded process-private, so lite
    // looks right — prompt glyphs included — on machines with nothing installed.
    std::wstring ttf = exeDir() + L"\\MesloLGLDZNerdFont-Regular.ttf";
    bool haveMeslo = AddFontResourceExW(ttf.c_str(), FR_PRIVATE, 0) > 0;
    g_font = CreateFontW(-16, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                         OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                         FIXED_PITCH | FF_MODERN,
                         haveMeslo ? L"MesloLGLDZ Nerd Font" : L"Consolas");
    {
        HDC dc = GetDC(nullptr);
        HGDIOBJ old = SelectObject(dc, g_font);
        TEXTMETRICW tm;
        GetTextMetricsW(dc, &tm);
        g_cw = tm.tmAveCharWidth;
        g_ch = tm.tmHeight;
        SelectObject(dc, old);
        ReleaseDC(nullptr, dc);
    }

    int cols = 100, rows = 30;
    g_emu = emu_new(cols, rows);
    connectHost(cols, rows);

    WNDCLASSW wc{};
    wc.lpfnWndProc = wndProc;
    wc.hInstance = inst;
    wc.lpszClassName = L"AgwintermLite";
    wc.hCursor = LoadCursorW(nullptr, (LPCWSTR)IDC_IBEAM);
    RegisterClassW(&wc);
    RECT want{ 0, 0, cols * g_cw, rows * g_ch };
    AdjustWindowRect(&want, WS_OVERLAPPEDWINDOW, FALSE);
    g_hwnd = CreateWindowW(L"AgwintermLite", L"agwinterm lite", WS_OVERLAPPEDWINDOW,
                           CW_USEDEFAULT, CW_USEDEFAULT, want.right - want.left, want.bottom - want.top,
                           nullptr, nullptr, inst, nullptr);
    ShowWindow(g_hwnd, show);

    CreateThread(nullptr, 0, readerThread, nullptr, 0, nullptr);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return 0;
}
