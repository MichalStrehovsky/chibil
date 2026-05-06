#define DOOM_IMPLEMENTATION 
#include "PureDOOM/PureDOOM.h"

// PAL function declarations (implemented in pal.c)
#ifdef REPRODUCIBLE_HARNESS
#define PAL_CALL
#else
#define PAL_CALL __cdecl
#endif

void  PAL_CALL pal_print(const char* str);
void* PAL_CALL pal_malloc(int size);
void  PAL_CALL pal_free(void* ptr);
void* PAL_CALL pal_open(const char* filename, const char* mode);
void  PAL_CALL pal_close(void* handle);
int   PAL_CALL pal_read(void* handle, void* buf, int count);
int   PAL_CALL pal_write(void* handle, const void* buf, int count);
int   PAL_CALL pal_seek(void* handle, int offset, doom_seek_t origin);
int   PAL_CALL pal_tell(void* handle);
int   PAL_CALL pal_eof(void* handle);
void  PAL_CALL pal_gettime(int* sec, int* usec);
void  PAL_CALL pal_exit(int code);
char* PAL_CALL pal_getenv(const char* var);

// PAL window / input
void PAL_CALL pal_window_create(int client_w, int client_h, const char* title);
int  PAL_CALL pal_window_pump(void);
void PAL_CALL pal_window_present(const unsigned char* rgba, int src_w, int src_h);
int  PAL_CALL pal_poll_event(int* type, int* p1, int* p2);

#ifdef REPRODUCIBLE_HARNESS
void PAL_CALL pal_harness_advance_tick(void);
void PAL_CALL pal_harness_save_bmp(const char* filename,
                           const unsigned char* rgb,
                           int width, int height);
#endif

#ifndef REPRODUCIBLE_HARNESS

// PAL event types (must match pal.c)
#define PAL_EVENT_KEYDOWN   1
#define PAL_EVENT_KEYUP     2
#define PAL_EVENT_MOUSEMOVE 3
#define PAL_EVENT_MOUSEDOWN 4
#define PAL_EVENT_MOUSEUP   5

// Windows Virtual-Key codes used below
#define VK_BACK        0x08
#define VK_TAB         0x09
#define VK_RETURN      0x0D
#define VK_SHIFT       0x10
#define VK_CONTROL     0x11
#define VK_MENU        0x12
#define VK_PAUSE       0x13
#define VK_ESCAPE      0x1B
#define VK_SPACE       0x20
#define VK_LEFT        0x25
#define VK_UP          0x26
#define VK_RIGHT       0x27
#define VK_DOWN        0x28
#define VK_F1          0x70
#define VK_F2          0x71
#define VK_F3          0x72
#define VK_F4          0x73
#define VK_F5          0x74
#define VK_F6          0x75
#define VK_F7          0x76
#define VK_F8          0x77
#define VK_F9          0x78
#define VK_F10         0x79
#define VK_F11         0x7A
#define VK_F12         0x7B
#define VK_MULTIPLY    0x6A
#define VK_OEM_1       0xBA
#define VK_OEM_PLUS    0xBB
#define VK_OEM_COMMA   0xBC
#define VK_OEM_MINUS   0xBD
#define VK_OEM_PERIOD  0xBE
#define VK_OEM_2       0xBF
#define VK_OEM_4       0xDB
#define VK_OEM_6       0xDD
#define VK_OEM_7       0xDE

static doom_key_t vk_to_doom(int vk)
{
    if (vk >= 'A' && vk <= 'Z') return (doom_key_t)(vk + 32);
    if (vk >= '0' && vk <= '9') return (doom_key_t)vk;

    switch (vk) {
    case VK_ESCAPE:     return DOOM_KEY_ESCAPE;
    case VK_RETURN:     return DOOM_KEY_ENTER;
    case VK_TAB:        return DOOM_KEY_TAB;
    case VK_SPACE:      return DOOM_KEY_SPACE;
    case VK_BACK:       return DOOM_KEY_BACKSPACE;
    case VK_LEFT:       return DOOM_KEY_LEFT_ARROW;
    case VK_UP:         return DOOM_KEY_UP_ARROW;
    case VK_RIGHT:      return DOOM_KEY_RIGHT_ARROW;
    case VK_DOWN:       return DOOM_KEY_DOWN_ARROW;
    case VK_SHIFT:      return DOOM_KEY_SHIFT;
    case VK_CONTROL:    return DOOM_KEY_CTRL;
    case VK_MENU:       return DOOM_KEY_ALT;
    case VK_PAUSE:      return DOOM_KEY_PAUSE;
    case VK_F1:         return DOOM_KEY_F1;
    case VK_F2:         return DOOM_KEY_F2;
    case VK_F3:         return DOOM_KEY_F3;
    case VK_F4:         return DOOM_KEY_F4;
    case VK_F5:         return DOOM_KEY_F5;
    case VK_F6:         return DOOM_KEY_F6;
    case VK_F7:         return DOOM_KEY_F7;
    case VK_F8:         return DOOM_KEY_F8;
    case VK_F9:         return DOOM_KEY_F9;
    case VK_F10:        return DOOM_KEY_F10;
    case VK_F11:        return DOOM_KEY_F11;
    case VK_F12:        return DOOM_KEY_F12;
    case VK_OEM_MINUS:  return DOOM_KEY_MINUS;
    case VK_OEM_PLUS:   return DOOM_KEY_EQUALS;
    case VK_OEM_4:      return DOOM_KEY_LEFT_BRACKET;
    case VK_OEM_6:      return DOOM_KEY_RIGHT_BRACKET;
    case VK_OEM_1:      return DOOM_KEY_SEMICOLON;
    case VK_OEM_7:      return DOOM_KEY_APOSTROPHE;
    case VK_OEM_COMMA:  return DOOM_KEY_COMMA;
    case VK_OEM_PERIOD: return DOOM_KEY_PERIOD;
    case VK_OEM_2:      return DOOM_KEY_SLASH;
    case VK_MULTIPLY:   return DOOM_KEY_MULTIPLY;
    default:            return DOOM_KEY_UNKNOWN;
    }
}

static void process_pal_events(void)
{
    int type, p1, p2;
    while (pal_poll_event(&type, &p1, &p2)) {
        switch (type) {
        case PAL_EVENT_KEYDOWN: {
            doom_key_t dk = vk_to_doom(p1);
            if (dk != DOOM_KEY_UNKNOWN) doom_key_down(dk);
            break;
        }
        case PAL_EVENT_KEYUP: {
            doom_key_t dk = vk_to_doom(p1);
            if (dk != DOOM_KEY_UNKNOWN) doom_key_up(dk);
            break;
        }
        case PAL_EVENT_MOUSEMOVE:
            doom_mouse_move(p1, p2);
            break;
        case PAL_EVENT_MOUSEDOWN:
            doom_button_down((doom_button_t)p1);
            break;
        case PAL_EVENT_MOUSEUP:
            doom_button_up((doom_button_t)p1);
            break;
        }
    }
}

#endif

static void pal_setup(void)
{
    doom_set_print(pal_print);
    doom_set_malloc(pal_malloc, pal_free);
    doom_set_file_io(pal_open, pal_close, pal_read, pal_write,
                     pal_seek, pal_tell, pal_eof);
    doom_set_gettime(pal_gettime);
    doom_set_exit(pal_exit);
    doom_set_getenv(pal_getenv);
}

#ifdef REPRODUCIBLE_HARNESS

#define HARNESS_MAX_FRAMES 50

#ifdef VALIDATE_CHECKSUM
#define HARNESS_EXPECTED_CHECKSUM 0x0f3e80d6560e2c90ULL

static unsigned long long harness_checksum = 14695981039346656037ULL;

static void harness_hash_frame(const unsigned char* rgb, int size)
{
    int i;
    for (i = 0; i < size; i++) {
        harness_checksum ^= (unsigned long long)rgb[i];
        harness_checksum *= 1099511628211ULL;
    }
}

static void harness_write_hex(char* dst, unsigned long long value)
{
    const char* hex = "0123456789abcdef";
    int i;

    for (i = 0; i < 16; i++) {
        dst[15 - i] = hex[(int)(value & 15)];
        value >>= 4;
    }
}

static void harness_validate_checksum(void)
{
    if (harness_checksum != HARNESS_EXPECTED_CHECKSUM) {
        char actual[] = "actual:   0000000000000000\n";
        char expected[] = "expected: 0000000000000000\n";

        harness_write_hex(actual + 10, harness_checksum);
        harness_write_hex(expected + 10, HARNESS_EXPECTED_CHECKSUM);
        pal_print("checksum mismatch\n");
        pal_print(actual);
        pal_print(expected);
        pal_exit(1);
    }
}
#endif

int main()
{
    pal_setup();

    char* argvData = "doom";
    char** argv = &argvData;
    doom_init(1, argv, 0);

    unsigned char prev_frame[SCREENWIDTH * SCREENHEIGHT * 3];
    doom_memset(prev_frame, 0, sizeof(prev_frame));
    int saved = 0;

    while (saved < HARNESS_MAX_FRAMES)
    {
        doom_force_update();
        pal_harness_advance_tick();

        const unsigned char* fb = doom_get_framebuffer(3);

        // Check if frame changed
        int changed = 0;
        {
            int i;
            for (i = 0; i < SCREENWIDTH * SCREENHEIGHT * 3; i++) {
                if (fb[i] != prev_frame[i]) { changed = 1; break; }
            }
        }

        if (changed)
        {
            doom_memcpy(prev_frame, fb, SCREENWIDTH * SCREENHEIGHT * 3);

#ifdef VALIDATE_CHECKSUM
            harness_hash_frame(fb, SCREENWIDTH * SCREENHEIGHT * 3);
#else
            // Build filename: frame_0000.bmp .. frame_0049.bmp
            char name[] = "frame_0000.bmp";
            {
                int n = saved;
                name[9] = '0' + (n % 10); n /= 10;
                name[8] = '0' + (n % 10); n /= 10;
                name[7] = '0' + (n % 10); n /= 10;
                name[6] = '0' + (n % 10);
            }

            pal_harness_save_bmp(name, fb, SCREENWIDTH, SCREENHEIGHT);
#endif
            saved++;
        }
    }

#ifdef VALIDATE_CHECKSUM
    harness_validate_checksum();
#endif

    pal_exit(0);
}

#else

int main()
{
    pal_setup();
    pal_window_create(960, 600, "DOOM");

    char* argvData = "doom";
    char** argv = &argvData;
    doom_init(1, argv, 0);

    while (pal_window_pump())
    {
        process_pal_events();
        doom_update();
        pal_window_present(doom_get_framebuffer(4), SCREENWIDTH, SCREENHEIGHT);
    }

    pal_exit(0);
}

#endif
