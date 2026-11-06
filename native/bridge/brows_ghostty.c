#include "brows_ghostty.h"
#include "brows_platform.h"
#include <stdlib.h>
#include <string.h>
#include <ghostty/vt.h>

_Static_assert(sizeof(bt_cell) == 24, "bt_cell ABI");
_Static_assert(sizeof(bt_frame) == 48, "bt_frame ABI");

struct bt_terminal {
    bt_mutex gate;
    GhosttyTerminal terminal;
    GhosttyRenderState render;
    GhosttyRenderStateRowIterator rows;
    GhosttyRenderStateRowCells cells;
    GhosttyKeyEncoder keys;
    GhosttyKeyEvent key;
    GhosttyMouseEncoder mouse_encoder;
    GhosttyMouseEvent mouse;
    GhosttyTrackedGridRef selection_anchor;
    GhosttyTerminalScreen selection_screen;
    bool selection_screen_valid;
    GhosttyRenderStateColors colors;
    bt_output_fn output;
    void* context;
    bt_cell* row;
    size_t row_capacity;
    uint8_t* text;
    size_t text_capacity;
    uint8_t* encoded;
    size_t encoded_capacity;
    uint16_t columns;
    bool held;
    uint64_t hold_start;
    int callback_error;
};

static uint32_t rgb(GhosttyColorRgb color) {
    return 0xff000000u | ((uint32_t)color.r << 16) | ((uint32_t)color.g << 8) | color.b;
}

static int reserve(void** pointer, size_t* capacity, size_t needed) {
    if (needed <= *capacity) { return GHOSTTY_SUCCESS; }
    size_t next = *capacity < 4096 ? 4096 : *capacity;
    while (next < needed) {
        if (next > SIZE_MAX / 2) {
            next = needed;
            break;
        }
        next *= 2;
    }
    void* replacement = realloc(*pointer, next);
    if (!replacement) { return GHOSTTY_OUT_OF_MEMORY; }
    *pointer = replacement;
    *capacity = next;
    return GHOSTTY_SUCCESS;
}

#define CHECK(call) do { int check_result = (call); if (check_result != 0) return check_result; } while (0)
#define BT_SELECTION_CANCELED 1

static void write_pty(GhosttyTerminal terminal, void* userdata, const uint8_t* data, size_t length) {
    (void)terminal;
    bt_terminal* t = userdata;
    if (t->output && length) { t->output(t->context, data, length); }
}

static void render_hold(GhosttyTerminal terminal, void* userdata, bool held) {
    bt_terminal* t = userdata;
    if (held) {
        t->callback_error = ghostty_render_state_update(t->render, terminal);
        t->hold_start = bt_now_ms();
    }
    t->held = held;
}

uint32_t bt_abi_version(void) { return 3; }
void bt_lock(bt_terminal* t) { bt_mutex_lock(&t->gate); }
void bt_unlock(bt_terminal* t) { bt_mutex_unlock(&t->gate); }

void bt_free(bt_terminal* t) {
    if (!t) { return; }
    if (t->selection_anchor) { ghostty_tracked_grid_ref_free(t->selection_anchor); }
    ghostty_mouse_event_free(t->mouse);
    ghostty_mouse_encoder_free(t->mouse_encoder);
    ghostty_key_event_free(t->key);
    ghostty_key_encoder_free(t->keys);
    ghostty_render_state_row_cells_free(t->cells);
    ghostty_render_state_row_iterator_free(t->rows);
    ghostty_render_state_free(t->render);
    ghostty_terminal_free(t->terminal);
    free(t->row);
    free(t->text);
    free(t->encoded);
    bt_mutex_destroy(&t->gate);
    free(t);
}

int bt_create(uint16_t columns, uint16_t rows, bt_output_fn output, void* context, bt_terminal** result) {
    if (!result || !columns || !rows) { return GHOSTTY_INVALID_VALUE; }
    *result = NULL;
    bt_terminal* t = calloc(1, sizeof(*t));
    if (!t) { return GHOSTTY_OUT_OF_MEMORY; }
    if (bt_mutex_init(&t->gate)) { free(t); return GHOSTTY_OUT_OF_MEMORY; }
    t->output = output;
    t->context = context;
    int error;
#define CREATE(call) do { error = (call); if (error) { bt_free(t); return error; } } while (0)
    CREATE(ghostty_terminal_new(NULL, &t->terminal, columns, rows));
    /* The render state resolves OSC overrides only when both default colors
     * exist. Explicit defaults also let applications query and reset them. */
    GhosttyColorRgb foreground = { .r = 255, .g = 255, .b = 255 };
    GhosttyColorRgb background = { .r = 0, .g = 0, .b = 0 };
    CREATE(ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_COLOR_FOREGROUND, &foreground));
    CREATE(ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_COLOR_BACKGROUND, &background));
    CREATE(ghostty_render_state_new(NULL, &t->render));
    CREATE(ghostty_render_state_row_iterator_new(NULL, &t->rows));
    CREATE(ghostty_render_state_row_cells_new(NULL, &t->cells));
    CREATE(ghostty_key_encoder_new(NULL, &t->keys));
    CREATE(ghostty_key_event_new(NULL, &t->key));
    CREATE(ghostty_mouse_encoder_new(NULL, &t->mouse_encoder));
    CREATE(ghostty_mouse_event_new(NULL, &t->mouse));
    CREATE(ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_USERDATA, t));
    CREATE(ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_WRITE_PTY, write_pty));
    CREATE(ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_RENDER_HOLD, render_hold));
    *result = t;
    return 0;
}

int bt_set_resize_pull_scrollback(bt_terminal* t, int enabled) {
    bool value = enabled != 0;
    return ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_RESIZE_PULL_SCROLLBACK, &value);
}

void bt_write(bt_terminal* t, const uint8_t* data, size_t length) {
    ghostty_terminal_vt_write(t->terminal, data, length);
}

int bt_resize(bt_terminal* t, uint16_t columns, uint16_t rows, uint32_t width, uint32_t height) {
    if (columns == 0 || rows == 0 || width == 0 || height == 0) { return GHOSTTY_INVALID_VALUE; }
    bool screen_width_fits = width <= UINT32_MAX / columns;
    bool screen_height_fits = height <= UINT32_MAX / rows;
    if (!screen_width_fits || !screen_height_fits) { return GHOSTTY_INVALID_VALUE; }
    uint32_t screen_width = columns * width;
    uint32_t screen_height = rows * height;
    CHECK(ghostty_terminal_resize(t->terminal, columns, rows, width, height));
    GhosttyMouseEncoderSize size = GHOSTTY_INIT_SIZED(GhosttyMouseEncoderSize);
    size.screen_width = screen_width;
    size.screen_height = screen_height;
    size.cell_width = width;
    size.cell_height = height;
    return ghostty_mouse_encoder_setopt(t->mouse_encoder, GHOSTTY_MOUSE_ENCODER_OPT_SIZE, &size);
}

int bt_complete_output(bt_terminal* t) {
    if (!t->held) { return 0; }
    GhosttyTerminalModeConfig mode = { .mode = GHOSTTY_MODE_SYNC_OUTPUT, .value = false };
    int result = ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_MODE, &mode);
    if (result == 0) { t->held = false; }
    return result;
}

int bt_begin_frame(bt_terminal* t, bt_frame* frame, int force) {
    if (t->callback_error) { return t->callback_error; }
    if (t->held && bt_now_ms() - t->hold_start >= 1000) {
        GhosttyTerminalModeConfig mode = { .mode = GHOSTTY_MODE_SYNC_OUTPUT, .value = false };
        CHECK(ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_MODE, &mode));
        t->held = false;
    }
    if (!t->held) { CHECK(ghostty_render_state_update(t->render, t->terminal)); }
    GhosttyRenderStateColors colors = GHOSTTY_INIT_SIZED(GhosttyRenderStateColors);
    CHECK(ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_COLORS, &colors));
    /* OSC theme changes can change resolved cell colors without dirtying text
     * rows. Our cached rows contain resolved RGB values, so rebuild them. */
    bool colors_changed = memcmp(&t->colors.foreground, &colors.foreground, sizeof(colors.foreground)) != 0 ||
        memcmp(&t->colors.background, &colors.background, sizeof(colors.background)) != 0 ||
        memcmp(t->colors.palette, colors.palette, sizeof(colors.palette)) != 0;
    t->colors = colors;
    if (force || colors_changed) {
        GhosttyRenderStateDirty dirty = GHOSTTY_RENDER_STATE_DIRTY_FULL;
        CHECK(ghostty_render_state_set(t->render, GHOSTTY_RENDER_STATE_OPTION_DIRTY, &dirty));
    }
    memset(frame, 0, sizeof(*frame));
    uint16_t rows;
    GhosttyRenderStateDirty dirty;
    CHECK(ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_COLS, &t->columns));
    CHECK(ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_ROWS, &rows));
    CHECK(ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_DIRTY, &dirty));
    GhosttyRenderStateCursor cursor = GHOSTTY_INIT_SIZED(GhosttyRenderStateCursor);
    CHECK(ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_CURSOR, &cursor));
    bool tracking = false;
    CHECK(ghostty_terminal_get(t->terminal, GHOSTTY_TERMINAL_DATA_MOUSE_TRACKING, &tracking));
    frame->columns = t->columns;
    frame->rows = rows;
    frame->dirty = dirty;
    frame->background = rgb(t->colors.background);
    frame->foreground = rgb(t->colors.foreground);
    frame->cursor_color = rgb(t->colors.cursor_has_value ? t->colors.cursor : t->colors.foreground);
    frame->cursor_x = cursor.viewport_x;
    frame->cursor_y = cursor.viewport_y;
    frame->cursor_style = cursor.visual_style;
    frame->cursor_visible = cursor.visible && cursor.viewport_has_value;
    frame->cursor_blink = cursor.blinking;
    frame->mouse_tracking = tracking;
    CHECK(reserve((void**)&t->row, &t->row_capacity, (size_t)t->columns * sizeof(bt_cell)));
    return ghostty_render_state_get(t->render, GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR, &t->rows);
}

int bt_next_row(bt_terminal* t, uint16_t* y, const bt_cell** cells, const uint8_t** text, size_t* length) {
    if (!ghostty_render_state_row_iterator_next_dirty(t->rows, y)) { return 1; }
    CHECK(ghostty_render_state_row_get(t->rows, GHOSTTY_RENDER_STATE_ROW_DATA_CELLS, &t->cells));
    size_t used = 0;
    uint16_t x = 0;
    while (ghostty_render_state_row_cells_next(t->cells)) {
        if (x >= t->columns) { return GHOSTTY_INVALID_VALUE; }
        bt_cell* cell = &t->row[x++];
        memset(cell, 0, sizeof(*cell));
        GhosttyStyle style;
        GhosttyCell raw;
        GhosttyCellWide wide;
        CHECK(ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_STYLE, &style));
        CHECK(ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_RAW, &raw));
        CHECK(ghostty_cell_get(raw, GHOSTTY_CELL_DATA_WIDE, &wide));
        cell->width = wide == GHOSTTY_CELL_WIDE_WIDE ? 2 : wide == GHOSTTY_CELL_WIDE_NARROW ? 1 : 0;
        cell->flags = style.flags;
        bool selected = false;
        CHECK(ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_SELECTED, &selected));
        cell->selected = selected;
        GhosttyColorRgb fg = t->colors.foreground, bg = t->colors.background;
        int color_result = ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_FG_COLOR, &fg);
        if (color_result && color_result != GHOSTTY_INVALID_VALUE) { return color_result; }
        color_result = ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_BG_COLOR, &bg);
        if (color_result && color_result != GHOSTTY_INVALID_VALUE) { return color_result; }
        if (style.flags & GHOSTTY_STYLE_FLAG_INVERSE) {
            GhosttyColorRgb swap = fg; fg = bg; bg = swap;
        }
        if (style.flags & GHOSTTY_STYLE_FLAG_FAINT) {
            fg.r = (uint8_t)(((unsigned)fg.r + bg.r) / 2);
            fg.g = (uint8_t)(((unsigned)fg.g + bg.g) / 2);
            fg.b = (uint8_t)(((unsigned)fg.b + bg.b) / 2);
        }
        if (style.flags & GHOSTTY_STYLE_FLAG_INVISIBLE) { fg = bg; }
        GhosttyColorRgb underline = fg;
        if (style.underline_color.tag == GHOSTTY_STYLE_COLOR_RGB) { underline = style.underline_color.value.rgb; }
        if (style.underline_color.tag == GHOSTTY_STYLE_COLOR_PALETTE) { underline = t->colors.palette[style.underline_color.value.palette]; }
        cell->foreground = rgb(fg);
        cell->background = rgb(bg);
        cell->underline = rgb(underline);
        CHECK(reserve((void**)&t->text, &t->text_capacity, used + 128));
        GhosttyBuffer buffer = { .ptr = t->text + used, .cap = t->text_capacity - used };
        int result = ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_UTF8, &buffer);
        if (result == GHOSTTY_OUT_OF_SPACE) {
            CHECK(reserve((void**)&t->text, &t->text_capacity, used + buffer.len));
            buffer.ptr = t->text + used;
            buffer.cap = t->text_capacity - used;
            result = ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_UTF8, &buffer);
        }
        CHECK(result);
        if (used > UINT32_MAX || buffer.len > UINT32_MAX - used) { return GHOSTTY_LIMIT_EXCEEDED; }
        cell->text_offset = (uint32_t)used;
        cell->text_length = (uint32_t)buffer.len;
        used += buffer.len;
    }
    if (x != t->columns) { return GHOSTTY_INVALID_VALUE; }
    *cells = t->row;
    *text = t->text;
    *length = used;
    return 0;
}

int bt_end_frame(bt_terminal* t) { return ghostty_render_state_clean(t->render); }


int bt_key(bt_terminal* t, uint32_t key, uint16_t modifiers, uint32_t action,
           uint32_t unshifted_codepoint, uint16_t consumed_modifiers,
           const uint8_t* text, size_t length) {
    ghostty_key_encoder_setopt_from_terminal(t->keys, t->terminal);
    CHECK(ghostty_key_event_set_action(t->key, (GhosttyKeyAction)action));
    CHECK(ghostty_key_event_set_key(t->key, (GhosttyKey)key));
    ghostty_key_event_set_mods(t->key, modifiers);
    ghostty_key_event_set_consumed_mods(t->key, consumed_modifiers);
    ghostty_key_event_set_utf8(t->key, (const char*)text, length);
    ghostty_key_event_set_unshifted_codepoint(t->key, unshifted_codepoint);
    CHECK(reserve((void**)&t->encoded, &t->encoded_capacity, length + 128));
    size_t written = 0;
    int result = ghostty_key_encoder_encode(t->keys, t->key, (char*)t->encoded, t->encoded_capacity, &written);
    if (result == GHOSTTY_OUT_OF_SPACE) {
        CHECK(reserve((void**)&t->encoded, &t->encoded_capacity, written));
        result = ghostty_key_encoder_encode(t->keys, t->key, (char*)t->encoded, t->encoded_capacity, &written);
    }
    CHECK(result);
    write_pty(t->terminal, t, t->encoded, written);
    return 0;
}

int bt_paste(bt_terminal* t, uint8_t* text, size_t length) {
    GhosttyTerminalModeConfig mode = { .mode = GHOSTTY_MODE_BRACKETED_PASTE };
    CHECK(ghostty_terminal_get(t->terminal, GHOSTTY_TERMINAL_DATA_MODE, &mode));
    size_t required = 0;
    int result = ghostty_paste_encode((char*)text, length, mode.value, NULL, 0, &required);
    if (result != GHOSTTY_OUT_OF_SPACE && result != 0) { return result; }
    CHECK(reserve((void**)&t->encoded, &t->encoded_capacity, required));
    CHECK(ghostty_paste_encode((char*)text, length, mode.value, (char*)t->encoded, t->encoded_capacity, &required));
    write_pty(t->terminal, t, t->encoded, required);
    return 0;
}

int bt_focus(bt_terminal* t, int focused) {
    GhosttyTerminalModeConfig mode = { .mode = GHOSTTY_MODE_FOCUS_EVENT };
    CHECK(ghostty_terminal_get(t->terminal, GHOSTTY_TERMINAL_DATA_MODE, &mode));
    if (!mode.value) { return 0; }
    char bytes[16]; size_t length;
    CHECK(ghostty_focus_encode(focused ? GHOSTTY_FOCUS_GAINED : GHOSTTY_FOCUS_LOST, bytes, sizeof(bytes), &length));
    write_pty(t->terminal, t, (const uint8_t*)bytes, length);
    return 0;
}

void bt_scroll(bt_terminal* t, int32_t lines, int bottom) {
    GhosttyTerminalScrollViewport scroll = {
        .tag = bottom < 0 ? GHOSTTY_SCROLL_VIEWPORT_TOP
            : bottom ? GHOSTTY_SCROLL_VIEWPORT_BOTTOM : GHOSTTY_SCROLL_VIEWPORT_DELTA,
        .value = { .delta = lines }
    };
    ghostty_terminal_scroll_viewport(t->terminal, scroll);
}

int bt_mouse(bt_terminal* t, uint32_t action, uint32_t button, uint16_t modifiers,
             float x, float y, int any_pressed) {
    ghostty_mouse_encoder_setopt_from_terminal(t->mouse_encoder, t->terminal);
    bool pressed = any_pressed != 0;
    CHECK(ghostty_mouse_encoder_setopt(t->mouse_encoder, GHOSTTY_MOUSE_ENCODER_OPT_ANY_BUTTON_PRESSED, &pressed));
    CHECK(ghostty_mouse_event_set_action(t->mouse, (GhosttyMouseAction)action));
    if (button) { CHECK(ghostty_mouse_event_set_button(t->mouse, (GhosttyMouseButton)button)); }
    else { ghostty_mouse_event_clear_button(t->mouse); }
    ghostty_mouse_event_set_mods(t->mouse, modifiers);
    ghostty_mouse_event_set_position(t->mouse, (GhosttyMousePosition){x, y});
    char bytes[128]; size_t length;
    CHECK(ghostty_mouse_encoder_encode(t->mouse_encoder, t->mouse, bytes, sizeof(bytes), &length));
    write_pty(t->terminal, t, (const uint8_t*)bytes, length);
    return 0;
}

int bt_title(bt_terminal* t, const uint8_t** text, size_t* length) {
    GhosttyString title;
    CHECK(ghostty_terminal_get(t->terminal, GHOSTTY_TERMINAL_DATA_TITLE, &title));
    *text = title.ptr;
    *length = title.len;
    return 0;
}

int bt_select(bt_terminal* t, uint16_t sx, uint16_t sy, uint16_t ex, uint16_t ey, int clear) {
    if (clear) {
        bt_selection_cancel(t);
        return ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, NULL);
    }
    GhosttySelection selection = {0};
    GhosttyPoint start = { .tag = GHOSTTY_POINT_TAG_VIEWPORT, .value = { .coordinate = { .x = sx, .y = sy } } };
    GhosttyPoint end = { .tag = GHOSTTY_POINT_TAG_VIEWPORT, .value = { .coordinate = { .x = ex, .y = ey } } };
    CHECK(ghostty_terminal_grid_ref(t->terminal, start, &selection.start));
    CHECK(ghostty_terminal_grid_ref(t->terminal, end, &selection.end));
    return ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, &selection);
}

void bt_selection_cancel(bt_terminal* t) {
    if (!t) { return; }
    if (t->selection_anchor) {
        ghostty_tracked_grid_ref_free(t->selection_anchor);
        t->selection_anchor = NULL;
    }
    t->selection_screen_valid = false;
}

int bt_selection_begin(bt_terminal* t, uint16_t x, uint16_t y) {
    bt_selection_cancel(t);
    GhosttyPoint point = {
        .tag = GHOSTTY_POINT_TAG_VIEWPORT,
        .value = { .coordinate = { .x = x, .y = y } }
    };
    int result = ghostty_terminal_grid_ref_track(t->terminal, point, &t->selection_anchor);
    if (result != 0) {
        bt_selection_cancel(t);
        return result;
    }
    result = ghostty_terminal_get(t->terminal, GHOSTTY_TERMINAL_DATA_ACTIVE_SCREEN, &t->selection_screen);
    if (result != 0) {
        bt_selection_cancel(t);
        return result;
    }
    t->selection_screen_valid = true;

    GhosttyGridRef anchor = {0};
    result = ghostty_tracked_grid_ref_snapshot(t->selection_anchor, &anchor);
    if (result != 0) {
        bt_selection_cancel(t);
        return result;
    }
    GhosttySelection selection = { .start = anchor, .end = anchor };
    result = ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, &selection);
    if (result != 0) { bt_selection_cancel(t); }
    return result;
}

int bt_selection_update(bt_terminal* t, uint16_t x, uint16_t y) {
    GhosttyTerminalScreen active_screen;
    int result = ghostty_terminal_get(t->terminal, GHOSTTY_TERMINAL_DATA_ACTIVE_SCREEN, &active_screen);
    if (result != 0) {
        bt_selection_cancel(t);
        ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, NULL);
        return result;
    }
    bool active_screen_changed = !t->selection_screen_valid || active_screen != t->selection_screen;
    bool anchor_is_unavailable = !t->selection_anchor || !ghostty_tracked_grid_ref_has_value(t->selection_anchor);
    if (active_screen_changed || anchor_is_unavailable) {
        bt_selection_cancel(t);
        result = ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, NULL);
        if (result != 0) {
            return result;
        }
        return BT_SELECTION_CANCELED;
    }

    GhosttySelection selection = {0};
    result = ghostty_tracked_grid_ref_snapshot(t->selection_anchor, &selection.start);
    if (result != 0) {
        bt_selection_cancel(t);
        int clear_result = ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, NULL);
        if (clear_result != 0) {
            return clear_result;
        }
        return result == GHOSTTY_NO_VALUE ? BT_SELECTION_CANCELED : result;
    }
    GhosttyPoint endpoint = {
        .tag = GHOSTTY_POINT_TAG_VIEWPORT,
        .value = { .coordinate = { .x = x, .y = y } }
    };
    result = ghostty_terminal_grid_ref(t->terminal, endpoint, &selection.end);
    if (result != 0) {
        bt_selection_cancel(t);
        int clear_result = ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, NULL);
        if (clear_result != 0) {
            return clear_result;
        }
        return result == GHOSTTY_INVALID_VALUE || result == GHOSTTY_NO_VALUE
            ? BT_SELECTION_CANCELED : result;
    }
    result = ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, &selection);
    if (result != 0) {
        bt_selection_cancel(t);
        if (result == GHOSTTY_INVALID_VALUE || result == GHOSTTY_NO_VALUE) {
            int clear_result = ghostty_terminal_set(t->terminal, GHOSTTY_TERMINAL_OPT_SELECTION, NULL);
            if (clear_result != 0) {
                return clear_result;
            }
            return BT_SELECTION_CANCELED;
        }
        return result;
    }
    return result;
}

int bt_selection_text(bt_terminal* t, const uint8_t** text, size_t* length) {
    *text = NULL;
    *length = 0;
    size_t needed = 0;
    int result = ghostty_terminal_selection_format_buf(t->terminal, NULL, NULL, 0, &needed);
    if (result == GHOSTTY_NO_VALUE) { return 0; }
    if (result != GHOSTTY_OUT_OF_SPACE && result != 0) { return result; }
    CHECK(reserve((void**)&t->encoded, &t->encoded_capacity, needed));
    CHECK(ghostty_terminal_selection_format_buf(t->terminal, NULL, t->encoded, t->encoded_capacity, &needed));
    *text = t->encoded;
    *length = needed;
    return 0;
}
