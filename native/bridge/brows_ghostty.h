#ifndef BROWS_GHOSTTY_H
#define BROWS_GHOSTTY_H
#include <stdint.h>
#include <stddef.h>

#ifdef _WIN32
#ifdef BROWS_GHOSTTY_EXPORTS
#define BT_API __declspec(dllexport)
#else
#define BT_API __declspec(dllimport)
#endif
#else
#define BT_API __attribute__((visibility("default")))
#endif
typedef struct bt_terminal bt_terminal;
typedef void (*bt_output_fn)(void* context, const uint8_t* bytes, size_t length);

/* All calls on a terminal must be serialized by the host. Callbacks may enqueue
 * output but must never reenter the terminal. Returned row pointers are borrowed
 * until the next bridge call. The ABI contains only explicitly sized scalars. */
typedef struct {
    uint32_t foreground;
    uint32_t background;
    uint32_t underline;
    uint32_t text_offset;
    uint32_t text_length;
    uint16_t flags;
    uint8_t width;
    uint8_t selected;
} bt_cell;

typedef struct {
    uint32_t columns;
    uint32_t rows;
    uint32_t background;
    uint32_t foreground;
    uint32_t cursor_color;
    uint32_t cursor_x;
    uint32_t cursor_y;
    uint32_t cursor_style;
    uint32_t cursor_visible;
    uint32_t cursor_blink;
    uint32_t dirty;
    uint32_t mouse_tracking;
} bt_frame;

BT_API uint32_t bt_abi_version(void);
BT_API void bt_lock(bt_terminal* terminal);
BT_API void bt_unlock(bt_terminal* terminal);
BT_API int bt_create(uint16_t columns, uint16_t rows, bt_output_fn output,
                     void* context, bt_terminal** result);
BT_API int bt_set_resize_pull_scrollback(bt_terminal* terminal, int enabled);
BT_API void bt_free(bt_terminal* terminal);
BT_API void bt_write(bt_terminal* terminal, const uint8_t* data, size_t length);
BT_API int bt_resize(bt_terminal* terminal, uint16_t columns, uint16_t rows,
                     uint32_t cell_width, uint32_t cell_height);
BT_API int bt_complete_output(bt_terminal* terminal);
BT_API int bt_begin_frame(bt_terminal* terminal, bt_frame* frame, int force);
BT_API int bt_next_row(bt_terminal* terminal, uint16_t* y,
                       const bt_cell** cells, const uint8_t** text, size_t* text_length);
BT_API int bt_end_frame(bt_terminal* terminal);
BT_API int bt_key(bt_terminal* terminal, uint32_t key, uint16_t modifiers,
                  uint32_t action, uint32_t unshifted_codepoint, uint16_t consumed_modifiers,
                  const uint8_t* text, size_t text_length);
BT_API int bt_paste(bt_terminal* terminal, uint8_t* text, size_t length);
BT_API int bt_focus(bt_terminal* terminal, int focused);
/* bottom is -1 for top, 0 for delta, and 1 for bottom. */
BT_API void bt_scroll(bt_terminal* terminal, int32_t lines, int bottom);
BT_API int bt_mouse(bt_terminal* terminal, uint32_t action, uint32_t button,
                    uint16_t modifiers, float x, float y, int any_pressed);
BT_API int bt_title(bt_terminal* terminal, const uint8_t** text, size_t* length);
BT_API int bt_select(bt_terminal* terminal, uint16_t start_x, uint16_t start_y,
                     uint16_t end_x, uint16_t end_y, int clear);
BT_API int bt_selection_begin(bt_terminal* terminal, uint16_t x, uint16_t y);
BT_API int bt_selection_update(bt_terminal* terminal, uint16_t x, uint16_t y);
BT_API void bt_selection_cancel(bt_terminal* terminal);
BT_API int bt_selection_text(bt_terminal* terminal, const uint8_t** text, size_t* length);

#endif
