#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static size_t realloc_count;
static size_t last_realloc_size;
static bool use_probe_allocation;
static bool fail_probe_allocation;
static uint8_t probe_allocation;

static void* counted_realloc(void* pointer, size_t size) {
    realloc_count++;
    last_realloc_size = size;
    if (use_probe_allocation) {
        if (fail_probe_allocation) { return NULL; }
        return &probe_allocation;
    }
    return realloc(pointer, size);
}

#define realloc counted_realloc
#include "../../native/bridge/brows_ghostty.c"
#undef realloc

static int test_reserve_geometric_growth(void) {
    void* allocation = NULL;
    size_t capacity = 0;
    realloc_count = 0;
    last_realloc_size = 0;
    int result = reserve(&allocation, &capacity, 1);
    if (result != GHOSTTY_SUCCESS || capacity != 4096 || realloc_count != 1 || last_realloc_size != 4096) {
        fprintf(stderr, "reserve did not use the 4096-byte minimum allocation.\n");
        free(allocation);
        return 1;
    }

    result = reserve(&allocation, &capacity, 4096);
    if (result != GHOSTTY_SUCCESS || capacity != 4096 || realloc_count != 1) {
        fprintf(stderr, "reserve reallocated when the request fit the current capacity.\n");
        free(allocation);
        return 1;
    }

    result = reserve(&allocation, &capacity, 4097);
    if (result != GHOSTTY_SUCCESS || capacity != 8192 || realloc_count != 2 || last_realloc_size != 8192) {
        fprintf(stderr, "reserve did not double capacity to cover a request above 4096 bytes.\n");
        free(allocation);
        return 1;
    }

    result = reserve(&allocation, &capacity, 8193);
    if (result != GHOSTTY_SUCCESS || capacity != 16384 || realloc_count != 3 || last_realloc_size != 16384) {
        fprintf(stderr, "reserve did not double capacity to cover a request above 8192 bytes.\n");
        free(allocation);
        return 1;
    }

    free(allocation);
    return 0;
}

static int test_reserve_size_t_overflow(void) {
    void* allocation = NULL;
    size_t capacity = SIZE_MAX / 2 + 1;
    realloc_count = 0;
    last_realloc_size = 0;
    use_probe_allocation = true;

    int result = reserve(&allocation, &capacity, SIZE_MAX);

    use_probe_allocation = false;
    if (result != GHOSTTY_SUCCESS || allocation != &probe_allocation || capacity != SIZE_MAX ||
        realloc_count != 1 || last_realloc_size != SIZE_MAX) {
        fprintf(stderr, "reserve did not fall back to the requested size near SIZE_MAX.\n");
        return 1;
    }
    return 0;
}

static int test_reserve_allocation_failure_preserves_state(void) {
    void* allocation = &probe_allocation;
    size_t capacity = 4096;
    realloc_count = 0;
    last_realloc_size = 0;
    use_probe_allocation = true;
    fail_probe_allocation = true;

    int result = reserve(&allocation, &capacity, 4097);

    use_probe_allocation = false;
    fail_probe_allocation = false;
    if (result != GHOSTTY_OUT_OF_MEMORY || allocation != &probe_allocation || capacity != 4096 ||
        realloc_count != 1 || last_realloc_size != 8192) {
        fprintf(stderr, "reserve changed its allocation state after realloc failed.\n");
        return 1;
    }
    return 0;
}

static size_t maximum_geometric_allocations(size_t needed) {
    size_t capacity = 4096;
    size_t maximum = 1;
    while (capacity < needed) {
        maximum++;
        if (capacity > SIZE_MAX / 2) { break; }
        capacity *= 2;
    }
    return maximum;
}

static int run_row_case(uint16_t columns) {
    const size_t grapheme_bytes = 31;
    const size_t grapheme_count = (size_t)columns - 1;
    const size_t input_length = grapheme_count * grapheme_bytes;
    uint8_t* input = malloc(input_length);
    if (input == NULL) {
        fprintf(stderr, "could not allocate the test input.\n");
        return 1;
    }

    for (size_t i = 0; i < grapheme_count; i++) {
        size_t offset = i * grapheme_bytes;
        input[offset] = 'a';
        for (size_t byte = 1; byte < grapheme_bytes; byte += 2) {
            input[offset + byte] = 0xcc;
            input[offset + byte + 1] = 0x81;
        }
    }

    bt_terminal* terminal = NULL;
    int result = bt_create(columns, 1, NULL, NULL, &terminal);
    if (result != GHOSTTY_SUCCESS) {
        fprintf(stderr, "bt_create failed for %u columns: %d.\n", columns, result);
        free(input);
        return 1;
    }

    bt_write(terminal, input, input_length);
    bt_frame frame = {0};
    result = bt_begin_frame(terminal, &frame, 1);
    if (result != GHOSTTY_SUCCESS) {
        fprintf(stderr, "bt_begin_frame failed for %u columns: %d.\n", columns, result);
        bt_free(terminal);
        free(input);
        return 1;
    }

    uint16_t y = UINT16_MAX;
    const bt_cell* cells = NULL;
    const uint8_t* text = NULL;
    size_t text_length = 0;
    realloc_count = 0;
    last_realloc_size = 0;
    result = bt_next_row(terminal, &y, &cells, &text, &text_length);
    size_t observed_reallocations = realloc_count;

    bool row_is_valid = result == GHOSTTY_SUCCESS && y == 0 && cells != NULL && text != NULL &&
        frame.columns == columns && frame.rows == 1 && text_length == input_length &&
        memcmp(text, input, input_length) == 0;
    if (row_is_valid) {
        for (size_t cell = 0; cell < grapheme_count; cell++) {
            if (cells[cell].text_offset != cell * grapheme_bytes || cells[cell].text_length != grapheme_bytes ||
                cells[cell].width != 1) {
                row_is_valid = false;
                break;
            }
        }
        if (cells[columns - 1].text_length != 0) { row_is_valid = false; }
    }

    int end_result = bt_end_frame(terminal);
    bt_free(terminal);
    free(input);

    if (!row_is_valid) {
        fprintf(stderr, "row data was not preserved for %u columns (result %d, bytes %zu).\n",
            columns, result, text_length);
        return 1;
    }
    if (end_result != GHOSTTY_SUCCESS) {
        fprintf(stderr, "bt_end_frame failed for %u columns: %d.\n", columns, end_result);
        return 1;
    }

    const size_t expected_maximum = maximum_geometric_allocations(input_length + 128);
    if (observed_reallocations > expected_maximum) {
        fprintf(stderr, "%u columns used %zu reallocations; geometric growth allows at most %zu.\n",
            columns, observed_reallocations, expected_maximum);
        return 1;
    }

    printf("columns=%u row-bytes=%zu reallocs=%zu maximum=%zu\n", columns, text_length,
        observed_reallocations, expected_maximum);
    return 0;
}

int main(void) {
    int result = 0;
    if (run_row_case(200) != 0) { result = 1; }
    if (run_row_case(512) != 0) { result = 1; }
    if (run_row_case(4096) != 0) { result = 1; }
    if (test_reserve_geometric_growth() != 0) { result = 1; }
    if (test_reserve_size_t_overflow() != 0) { result = 1; }
    if (test_reserve_allocation_failure_preserves_state() != 0) { result = 1; }
    return result;
}
