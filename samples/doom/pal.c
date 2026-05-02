// pal.c - Platform Abstraction Layer for DOOM using pure Win32 APIs.
// No standard C headers or Windows headers included.

#ifdef PAL_BUILD_DLL
#define PAL_API __declspec(dllexport)
#else
#define PAL_API
#endif

// ---------------------------------------------------------------------------
// Win32 type declarations
// ---------------------------------------------------------------------------

typedef void*          HANDLE;
typedef unsigned int   DWORD;
typedef int            BOOL;
typedef unsigned short WORD;
typedef int            LONG;

#ifdef _WIN64
typedef unsigned long long ULONG_PTR;
#else
typedef unsigned int       ULONG_PTR;
#endif

typedef ULONG_PTR SIZE_T;

// ---------------------------------------------------------------------------
// Win32 constants
// ---------------------------------------------------------------------------

#define INVALID_HANDLE_VALUE ((HANDLE)(ULONG_PTR)-1)
#define STD_OUTPUT_HANDLE    ((DWORD)-11)
#define GENERIC_READ         0x80000000U
#define GENERIC_WRITE        0x40000000U
#define FILE_SHARE_READ      0x00000001U
#define FILE_SHARE_WRITE     0x00000002U
#define OPEN_EXISTING        3
#define CREATE_ALWAYS        2
#define OPEN_ALWAYS          4
#define FILE_ATTRIBUTE_NORMAL 0x80
#define FILE_BEGIN           0
#define FILE_CURRENT         1
#define FILE_END             2

// ---------------------------------------------------------------------------
// Win32 structures
// ---------------------------------------------------------------------------

typedef struct {
    DWORD dwLowDateTime;
    DWORD dwHighDateTime;
} FILETIME;

typedef struct {
    WORD wYear;
    WORD wMonth;
    WORD wDayOfWeek;
    WORD wDay;
    WORD wHour;
    WORD wMinute;
    WORD wSecond;
    WORD wMilliseconds;
} SYSTEMTIME;

// ---------------------------------------------------------------------------
// Win32 function imports (kernel32.dll)
// ---------------------------------------------------------------------------

HANDLE __stdcall GetStdHandle(DWORD nStdHandle);
BOOL   __stdcall WriteFile(HANDLE hFile,
                           const void* lpBuffer,
                           DWORD nNumberOfBytesToWrite,
                           DWORD* lpNumberOfBytesWritten,
                           void* lpOverlapped);
HANDLE __stdcall GetProcessHeap(void);
void*  __stdcall HeapAlloc(HANDLE hHeap, DWORD dwFlags, SIZE_T dwBytes);
BOOL   __stdcall HeapFree(HANDLE hHeap, DWORD dwFlags, void* lpMem);
HANDLE __stdcall CreateFileA(const char* lpFileName,
                             DWORD dwDesiredAccess,
                             DWORD dwShareMode,
                             void* lpSecurityAttributes,
                             DWORD dwCreationDisposition,
                             DWORD dwFlagsAndAttributes,
                             HANDLE hTemplateFile);
BOOL   __stdcall CloseHandle(HANDLE hObject);
BOOL   __stdcall ReadFile(HANDLE hFile,
                          void* lpBuffer,
                          DWORD nNumberOfBytesToRead,
                          DWORD* lpNumberOfBytesRead,
                          void* lpOverlapped);
DWORD  __stdcall SetFilePointer(HANDLE hFile,
                                LONG lDistanceToMove,
                                LONG* lpDistanceToMoveHigh,
                                DWORD dwMoveMethod);
void   __stdcall GetSystemTime(SYSTEMTIME* lpSystemTime);
BOOL   __stdcall SystemTimeToFileTime(const SYSTEMTIME* lpSystemTime,
                                      FILETIME* lpFileTime);
void   __stdcall ExitProcess(unsigned int uExitCode);
DWORD  __stdcall GetEnvironmentVariableA(const char* lpName,
                                         char* lpBuffer,
                                         DWORD nSize);

// ---------------------------------------------------------------------------
// Win32 windowing types
// ---------------------------------------------------------------------------

#ifdef _WIN64
typedef long long          LONG_PTR;
typedef unsigned long long UINT_PTR;
#else
typedef int                LONG_PTR;
typedef unsigned int       UINT_PTR;
#endif

typedef LONG_PTR LRESULT;
typedef UINT_PTR WPARAM;
typedef LONG_PTR LPARAM;

typedef unsigned int UINT;

typedef void* HWND;
typedef void* HDC;
typedef void* HINSTANCE;
typedef void* HBRUSH;
typedef void* HICON;
typedef void* HCURSOR;
typedef void* HMENU;
typedef WORD   ATOM;

typedef LRESULT (__stdcall *WNDPROC)(HWND, UINT, WPARAM, LPARAM);

// ---------------------------------------------------------------------------
// Win32 windowing constants
// ---------------------------------------------------------------------------

#define CS_OWNDC            0x0020
#define CS_HREDRAW          0x0002
#define CS_VREDRAW          0x0001

#define WS_OVERLAPPED       0x00000000
#define WS_CAPTION          0x00C00000
#define WS_SYSMENU          0x00080000
#define WS_THICKFRAME       0x00040000
#define WS_MINIMIZEBOX      0x00020000
#define WS_MAXIMIZEBOX      0x00010000
#define WS_OVERLAPPEDWINDOW (WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU \
                            | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)

#define CW_USEDEFAULT       ((int)0x80000000)
#define SW_SHOW             5

#define WM_DESTROY          0x0002
#define WM_PAINT            0x000F
#define WM_CLOSE            0x0010
#define WM_QUIT             0x0012
#define WM_KEYDOWN          0x0100
#define WM_KEYUP            0x0101
#define WM_SYSKEYDOWN       0x0104
#define WM_SYSKEYUP         0x0105
#define WM_MOUSEMOVE        0x0200
#define WM_LBUTTONDOWN      0x0201
#define WM_LBUTTONUP        0x0202
#define WM_RBUTTONDOWN      0x0204
#define WM_RBUTTONUP        0x0205
#define WM_MBUTTONDOWN      0x0207
#define WM_MBUTTONUP        0x0208

#define PM_REMOVE           0x0001

#define DIB_RGB_COLORS      0
#define SRCCOPY             0x00CC0020
#define BI_BITFIELDS        3
#define COLORONCOLOR        3

#define IDC_ARROW           ((const char*)(ULONG_PTR)32512)

// ---------------------------------------------------------------------------
// Win32 windowing structures
// ---------------------------------------------------------------------------

typedef struct { LONG x; LONG y; } POINT;
typedef struct { LONG left; LONG top; LONG right; LONG bottom; } RECT;

typedef struct {
    UINT        style;
    WNDPROC     lpfnWndProc;
    int         cbClsExtra;
    int         cbWndExtra;
    HINSTANCE   hInstance;
    HICON       hIcon;
    HCURSOR     hCursor;
    HBRUSH      hbrBackground;
    const char *lpszMenuName;
    const char *lpszClassName;
} WNDCLASSA;

typedef struct {
    HWND   hwnd;
    UINT   message;
    WPARAM wParam;
    LPARAM lParam;
    DWORD  time;
    POINT  pt;
} MSG;

typedef struct {
    HDC             hdc;
    BOOL            fErase;
    RECT            rcPaint;
    BOOL            fRestore;
    BOOL            fIncUpdate;
    unsigned char   rgbReserved[32];
} PAINTSTRUCT;

typedef struct {
    DWORD biSize;
    LONG  biWidth;
    LONG  biHeight;
    WORD  biPlanes;
    WORD  biBitCount;
    DWORD biCompression;
    DWORD biSizeImage;
    LONG  biXPelsPerMeter;
    LONG  biYPelsPerMeter;
    DWORD biClrUsed;
    DWORD biClrImportant;
} BITMAPINFOHEADER;

// ---------------------------------------------------------------------------
// Win32 function imports (user32.dll)
// ---------------------------------------------------------------------------

ATOM     __stdcall RegisterClassA(const WNDCLASSA *);
HWND     __stdcall CreateWindowExA(DWORD, const char *, const char *, DWORD,
                                   int, int, int, int,
                                   HWND, HMENU, HINSTANCE, void *);
BOOL     __stdcall ShowWindow(HWND, int);
BOOL     __stdcall UpdateWindow(HWND);
LRESULT  __stdcall DefWindowProcA(HWND, UINT, WPARAM, LPARAM);
void     __stdcall PostQuitMessage(int);
BOOL     __stdcall DestroyWindow(HWND);
BOOL     __stdcall PeekMessageA(MSG *, HWND, UINT, UINT, UINT);
BOOL     __stdcall TranslateMessage(const MSG *);
LRESULT  __stdcall DispatchMessageA(const MSG *);
BOOL     __stdcall GetClientRect(HWND, RECT *);
HCURSOR  __stdcall LoadCursorA(HINSTANCE, const char *);
HINSTANCE __stdcall GetModuleHandleA(const char *);
BOOL     __stdcall AdjustWindowRectEx(RECT *, DWORD, BOOL, DWORD);

// ---------------------------------------------------------------------------
// Win32 function imports (gdi32.dll)
// ---------------------------------------------------------------------------

HDC  __stdcall GetDC(HWND);
int  __stdcall ReleaseDC(HWND, HDC);
int  __stdcall StretchDIBits(HDC, int, int, int, int,
                             int, int, int, int,
                             const void *, const void *,
                             UINT, DWORD);
HDC  __stdcall BeginPaint(HWND, PAINTSTRUCT *);
BOOL __stdcall EndPaint(HWND, const PAINTSTRUCT *);
int  __stdcall SetStretchBltMode(HDC, int);

// ---------------------------------------------------------------------------
// PAL event types  (shared with doom.c via matching #defines)
// ---------------------------------------------------------------------------

#define PAL_EVENT_KEYDOWN   1
#define PAL_EVENT_KEYUP     2
#define PAL_EVENT_MOUSEMOVE 3
#define PAL_EVENT_MOUSEDOWN 4
#define PAL_EVENT_MOUSEUP   5

// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

typedef struct {
    HANDLE handle;
    int    eof_flag;
} pal_file;

static HANDLE g_heap;

static HANDLE get_heap(void)
{
    if (!g_heap)
        g_heap = GetProcessHeap();
    return g_heap;
}

static int pal_strlen(const char *s)
{
    int n = 0;
    while (s[n]) n++;
    return n;
}

// ---------------------------------------------------------------------------
// PAL: print
// ---------------------------------------------------------------------------

PAL_API void pal_print(const char *str)
{
    DWORD written;
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),
              str, (DWORD)pal_strlen(str), &written, 0);
}

// ---------------------------------------------------------------------------
// PAL: malloc / free
// ---------------------------------------------------------------------------

PAL_API void *pal_malloc(int size)
{
    return HeapAlloc(get_heap(), 0, (SIZE_T)size);
}

PAL_API void pal_free(void *ptr)
{
    if (ptr)
        HeapFree(get_heap(), 0, ptr);
}

// ---------------------------------------------------------------------------
// PAL: file I/O
// ---------------------------------------------------------------------------

PAL_API void *pal_open(const char *filename, const char *mode)
{
    DWORD access      = 0;
    DWORD disposition  = 0;
    int   has_read     = 0;
    int   has_write    = 0;
    int   has_append   = 0;
    const char *p;
    HANDLE h;
    pal_file *f;

    for (p = mode; *p; p++) {
        switch (*p) {
            case 'r': has_read   = 1; break;
            case 'w': has_write  = 1; break;
            case 'a': has_append = 1; break;
            case '+': has_read   = 1; has_write = 1; break;
        }
    }

    if (has_append) {
        access      = GENERIC_READ | GENERIC_WRITE;
        disposition  = OPEN_ALWAYS;
    } else if (has_read && has_write) {
        access      = GENERIC_READ | GENERIC_WRITE;
        disposition  = (mode[0] == 'r') ? OPEN_EXISTING : CREATE_ALWAYS;
    } else if (has_write) {
        access      = GENERIC_WRITE;
        disposition  = CREATE_ALWAYS;
    } else {
        access      = GENERIC_READ;
        disposition  = OPEN_EXISTING;
    }

    h = CreateFileA(filename, access,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    0, disposition, FILE_ATTRIBUTE_NORMAL, 0);
    if (h == INVALID_HANDLE_VALUE)
        return 0;

    f = (pal_file *)HeapAlloc(get_heap(), 0, sizeof(pal_file));
    if (!f) {
        CloseHandle(h);
        return 0;
    }
    f->handle   = h;
    f->eof_flag = 0;

    if (has_append)
        SetFilePointer(h, 0, 0, FILE_END);

    return f;
}

PAL_API void pal_close(void *handle)
{
    pal_file *f = (pal_file *)handle;
    if (f) {
        CloseHandle(f->handle);
        HeapFree(get_heap(), 0, f);
    }
}

PAL_API int pal_read(void *handle, void *buf, int count)
{
    pal_file *f = (pal_file *)handle;
    DWORD bytes_read = 0;
    BOOL ok = ReadFile(f->handle, buf, (DWORD)count, &bytes_read, 0);
    if (!ok)
        return -1;
    if ((int)bytes_read < count)
        f->eof_flag = 1;
    return (int)bytes_read;
}

PAL_API int pal_write(void *handle, const void *buf, int count)
{
    pal_file *f = (pal_file *)handle;
    DWORD bytes_written = 0;
    BOOL ok = WriteFile(f->handle, buf, (DWORD)count, &bytes_written, 0);
    if (!ok)
        return -1;
    return (int)bytes_written;
}

PAL_API int pal_seek(void *handle, int offset, int origin)
{
    pal_file *f = (pal_file *)handle;
    // doom_seek_t values (SET=0,CUR=1,END=2) match FILE_BEGIN/CURRENT/END
    DWORD result = SetFilePointer(f->handle, (LONG)offset, 0, (DWORD)origin);
    if (result == (DWORD)0xFFFFFFFF)
        return -1;
    f->eof_flag = 0;
    return 0;
}

PAL_API int pal_tell(void *handle)
{
    pal_file *f = (pal_file *)handle;
    DWORD pos = SetFilePointer(f->handle, 0, 0, FILE_CURRENT);
    if (pos == (DWORD)0xFFFFFFFF)
        return -1;
    return (int)pos;
}

PAL_API int pal_eof(void *handle)
{
    pal_file *f = (pal_file *)handle;
    return f->eof_flag;
}

// ---------------------------------------------------------------------------
// PAL: gettime
// ---------------------------------------------------------------------------

#ifdef REPRODUCIBLE_HARNESS

// Deterministic clock: a tick counter mapped to 35-tics-per-second time.
// Exposed so doom.c can advance it once per frame.
static int harness_tick;

PAL_API void pal_harness_advance_tick(void)
{
    harness_tick++;
}

PAL_API void pal_gettime(int *sec, int *usec)
{
    // I_GetTime computes:  (sec - basetime) * 35 + usec * 35 / 1000000
    // We want that to equal harness_tick exactly.
    //   sec  = harness_tick / 35
    //   usec = (harness_tick % 35) * 28572
    // because 28572 * 35 / 1000000 == 1  (integer division)
    *sec  = harness_tick / 35;
    *usec = (harness_tick % 35) * 28572;
}

#else

PAL_API void pal_gettime(int *sec, int *usec)
{
    // 100-ns intervals between 1601-01-01 and 1970-01-01
    static const unsigned long long EPOCH = 116444736000000000ULL;
    SYSTEMTIME st;
    FILETIME   ft;
    unsigned long long t;

    GetSystemTime(&st);
    SystemTimeToFileTime(&st, &ft);

    t  = (unsigned long long)ft.dwLowDateTime;
    t += (unsigned long long)ft.dwHighDateTime << 32;

    *sec  = (int)((t - EPOCH) / 10000000ULL);
    *usec = (int)(st.wMilliseconds * 1000);
}

#endif

// ---------------------------------------------------------------------------
// PAL: exit
// ---------------------------------------------------------------------------

PAL_API void pal_exit(int code)
{
    ExitProcess((unsigned int)code);
}

// ---------------------------------------------------------------------------
// PAL: getenv
// ---------------------------------------------------------------------------

static char pal_env_buf[32768];

PAL_API char *pal_getenv(const char *var)
{
    DWORD len = GetEnvironmentVariableA(var, pal_env_buf, sizeof(pal_env_buf));
    if (len == 0)
        return 0;
    return pal_env_buf;
}

// ---------------------------------------------------------------------------
// PAL: event queue + window  (not needed in REPRODUCIBLE_HARNESS)
// ---------------------------------------------------------------------------

#ifndef REPRODUCIBLE_HARNESS

#define PAL_EQ_SIZE 256

typedef struct { int type; int p1; int p2; } pal_evt;

static pal_evt g_events[PAL_EQ_SIZE];
static int     g_eq_head, g_eq_tail;

static void push_event(int type, int p1, int p2)
{
    int next = (g_eq_head + 1) % PAL_EQ_SIZE;
    if (next == g_eq_tail) return;
    g_events[g_eq_head].type = type;
    g_events[g_eq_head].p1   = p1;
    g_events[g_eq_head].p2   = p2;
    g_eq_head = next;
}

PAL_API int pal_poll_event(int *type, int *p1, int *p2)
{
    if (g_eq_tail == g_eq_head) return 0;
    *type = g_events[g_eq_tail].type;
    *p1   = g_events[g_eq_tail].p1;
    *p2   = g_events[g_eq_tail].p2;
    g_eq_tail = (g_eq_tail + 1) % PAL_EQ_SIZE;
    return 1;
}

// ---------------------------------------------------------------------------
// PAL: window
// ---------------------------------------------------------------------------

static HWND g_hwnd;
static HDC  g_hdc;
static int  g_mouse_x, g_mouse_y, g_mouse_init;

static LRESULT __stdcall WndProc(HWND hwnd, UINT msg,
                                 WPARAM wParam, LPARAM lParam)
{
    switch (msg) {
    case WM_CLOSE:
        DestroyWindow(hwnd);
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    case WM_PAINT: {
        PAINTSTRUCT ps;
        BeginPaint(hwnd, &ps);
        EndPaint(hwnd, &ps);
        return 0;
    }
    case WM_KEYDOWN:
    case WM_SYSKEYDOWN:
        if (!(lParam & (1 << 30)))   // ignore auto-repeat
            push_event(PAL_EVENT_KEYDOWN, (int)wParam, 0);
        return 0;
    case WM_KEYUP:
    case WM_SYSKEYUP:
        push_event(PAL_EVENT_KEYUP, (int)wParam, 0);
        return 0;
    case WM_MOUSEMOVE: {
        int mx = (int)(short)(lParam & 0xFFFF);
        int my = (int)(short)((lParam >> 16) & 0xFFFF);
        if (g_mouse_init)
            push_event(PAL_EVENT_MOUSEMOVE, mx - g_mouse_x, my - g_mouse_y);
        g_mouse_x    = mx;
        g_mouse_y    = my;
        g_mouse_init = 1;
        return 0;
    }
    case WM_LBUTTONDOWN: push_event(PAL_EVENT_MOUSEDOWN, 0, 0); return 0;
    case WM_LBUTTONUP:   push_event(PAL_EVENT_MOUSEUP,   0, 0); return 0;
    case WM_RBUTTONDOWN: push_event(PAL_EVENT_MOUSEDOWN, 1, 0); return 0;
    case WM_RBUTTONUP:   push_event(PAL_EVENT_MOUSEUP,   1, 0); return 0;
    case WM_MBUTTONDOWN: push_event(PAL_EVENT_MOUSEDOWN, 2, 0); return 0;
    case WM_MBUTTONUP:   push_event(PAL_EVENT_MOUSEUP,   2, 0); return 0;
    }
    return DefWindowProcA(hwnd, msg, wParam, lParam);
}

PAL_API void pal_window_create(int client_w, int client_h, const char *title)
{
    HINSTANCE hinst = GetModuleHandleA(0);
    WNDCLASSA wc;
    RECT rc;
    DWORD style = WS_OVERLAPPEDWINDOW;
    int i;

    // zero-fill wc
    { unsigned char *p = (unsigned char *)&wc;
      for (i = 0; i < (int)sizeof(wc); i++) p[i] = 0; }

    wc.style         = CS_OWNDC | CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc   = WndProc;
    wc.hInstance      = hinst;
    wc.hCursor        = LoadCursorA(0, IDC_ARROW);
    wc.lpszClassName  = "DoomWnd";

    RegisterClassA(&wc);

    rc.left = 0; rc.top = 0;
    rc.right = client_w; rc.bottom = client_h;
    AdjustWindowRectEx(&rc, style, 0, 0);

    g_hwnd = CreateWindowExA(0, "DoomWnd", title, style,
                             CW_USEDEFAULT, CW_USEDEFAULT,
                             rc.right - rc.left, rc.bottom - rc.top,
                             0, 0, hinst, 0);

    ShowWindow(g_hwnd, SW_SHOW);
    UpdateWindow(g_hwnd);

    g_hdc = GetDC(g_hwnd);
    SetStretchBltMode(g_hdc, COLORONCOLOR);
}

PAL_API int pal_window_pump(void)
{
    MSG msg;
    while (PeekMessageA(&msg, 0, 0, 0, PM_REMOVE)) {
        if (msg.message == WM_QUIT)
            return 0;
        TranslateMessage(&msg);
        DispatchMessageA(&msg);
    }
    return 1;
}

PAL_API void pal_window_present(const unsigned char *rgba, int src_w, int src_h)
{
    RECT rc;
    struct { BITMAPINFOHEADER hdr; DWORD masks[3]; } bmi;
    int i;

    // zero-fill bmi
    { unsigned char *p = (unsigned char *)&bmi;
      for (i = 0; i < (int)sizeof(bmi); i++) p[i] = 0; }

    bmi.hdr.biSize        = sizeof(BITMAPINFOHEADER);
    bmi.hdr.biWidth       = src_w;
    bmi.hdr.biHeight      = -src_h;        // negative = top-down
    bmi.hdr.biPlanes      = 1;
    bmi.hdr.biBitCount    = 32;
    bmi.hdr.biCompression = BI_BITFIELDS;
    bmi.masks[0] = 0x000000FFU;             // R
    bmi.masks[1] = 0x0000FF00U;             // G
    bmi.masks[2] = 0x00FF0000U;             // B

    GetClientRect(g_hwnd, &rc);
    StretchDIBits(g_hdc,
                  0, 0, rc.right, rc.bottom,    // dest
                  0, 0, src_w, src_h,           // src
                  rgba, &bmi,
                  DIB_RGB_COLORS, SRCCOPY);
}

#endif // !REPRODUCIBLE_HARNESS

// ---------------------------------------------------------------------------
// PAL: harness BMP writer  (REPRODUCIBLE_HARNESS only)
// ---------------------------------------------------------------------------

#ifdef REPRODUCIBLE_HARNESS

static void write_le16(unsigned char *p, unsigned short v)
{
    p[0] = (unsigned char)(v & 0xFF);
    p[1] = (unsigned char)((v >> 8) & 0xFF);
}

static void write_le32(unsigned char *p, unsigned int v)
{
    p[0] = (unsigned char)(v & 0xFF);
    p[1] = (unsigned char)((v >> 8) & 0xFF);
    p[2] = (unsigned char)((v >> 16) & 0xFF);
    p[3] = (unsigned char)((v >> 24) & 0xFF);
}

PAL_API void pal_harness_save_bmp(const char *filename,
                          const unsigned char *rgb,
                          int width, int height)
{
    int row_bytes  = width * 3;
    int padding    = (4 - (row_bytes & 3)) & 3;
    int padded_row = row_bytes + padding;
    int pixel_size = padded_row * height;
    int file_size  = 14 + 40 + pixel_size;
    unsigned char hdr[54];
    int y, x;
    void *f;

    // -- BITMAPFILEHEADER (14 bytes) --
    hdr[0] = 'B'; hdr[1] = 'M';
    write_le32(hdr + 2,  (unsigned int)file_size);
    write_le16(hdr + 6,  0);          // reserved1
    write_le16(hdr + 8,  0);          // reserved2
    write_le32(hdr + 10, 54);         // offset to pixel data

    // -- BITMAPINFOHEADER (40 bytes) --
    write_le32(hdr + 14, 40);         // header size
    write_le32(hdr + 18, (unsigned int)width);
    write_le32(hdr + 22, (unsigned int)height);  // positive = bottom-up
    write_le16(hdr + 26, 1);          // planes
    write_le16(hdr + 28, 24);         // bits per pixel
    write_le32(hdr + 30, 0);          // compression (BI_RGB)
    write_le32(hdr + 34, (unsigned int)pixel_size);
    write_le32(hdr + 38, 0);          // X pels/meter
    write_le32(hdr + 42, 0);          // Y pels/meter
    write_le32(hdr + 46, 0);          // colors used
    write_le32(hdr + 50, 0);          // colors important

    f = pal_open(filename, "wb");
    if (!f) return;

    pal_write(f, hdr, 54);

    // Pixel rows, bottom-up, RGB → BGR
    for (y = height - 1; y >= 0; y--) {
        unsigned char row[960 + 4]; // 320*3 + max 3 bytes padding
        const unsigned char *src_row = rgb + y * width * 3;
        for (x = 0; x < width; x++) {
            row[x * 3 + 0] = src_row[x * 3 + 2]; // B
            row[x * 3 + 1] = src_row[x * 3 + 1]; // G
            row[x * 3 + 2] = src_row[x * 3 + 0]; // R
        }
        for (x = 0; x < padding; x++)
            row[row_bytes + x] = 0;
        pal_write(f, row, padded_row);
    }

    pal_close(f);
}

#endif
