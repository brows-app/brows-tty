#include "brows_session.h"
#include <windows.h>
#include <conpty.h>
#include <ghostty/vt/key/event.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <stdbool.h>

typedef struct input_chunk {
    struct input_chunk* next;
    size_t length;
    uint8_t data[];
} input_chunk;

struct bt_session {
    bt_terminal* terminal;
    HPCON console;
    HANDLE process;
    HANDLE input;
    HANDLE output;
    HANDLE reader;
    HANDLE writer;
    HANDLE ready;
    HANDLE stopped;
    CRITICAL_SECTION queue_gate;
    CRITICAL_SECTION console_gate;
    input_chunk* first;
    input_chunk* last;
    size_t queued_bytes;
    volatile LONG stopping;
    volatile LONG error;
    volatile LONG dropped_input;
};

static int win32_error(void) {
    return (int)HRESULT_FROM_WIN32(GetLastError());
}

static void record_error(bt_session* session, DWORD error) {
    if (error != ERROR_BROKEN_PIPE && error != ERROR_OPERATION_ABORTED) {
        InterlockedCompareExchange(&session->error, (LONG)error, 0);
    }
}

static void record_dropped_input(bt_session* session, DWORD error) {
    /* Only the input chunk is lost; the queue and its workers stay usable. */
    InterlockedExchange(&session->dropped_input, (LONG)error);
}

static void enqueue(void* context, const uint8_t* bytes, size_t length) {
    bt_session* session = context;
    if (InterlockedCompareExchange(&session->stopping, 0, 0)) { return; }
    EnterCriticalSection(&session->queue_gate);
    if (length > 16 * 1024 * 1024 - session->queued_bytes) {
        /* A full queue drops this chunk without killing the transport. */
        record_dropped_input(session, ERROR_NOT_ENOUGH_QUOTA);
        LeaveCriticalSection(&session->queue_gate);
        return;
    }
    input_chunk* chunk = malloc(sizeof(*chunk) + length);
    if (!chunk) {
        record_dropped_input(session, ERROR_NOT_ENOUGH_MEMORY);
        LeaveCriticalSection(&session->queue_gate);
        return;
    }
    chunk->next = NULL;
    chunk->length = length;
    memcpy(chunk->data, bytes, length);
    if (session->last) { session->last->next = chunk; }
    else { session->first = chunk; }
    session->last = chunk;
    session->queued_bytes += length;
    LeaveCriticalSection(&session->queue_gate);
    SetEvent(session->ready);
}

static DWORD WINAPI read_output(void* context) {
    bt_session* session = context;
    uint8_t bytes[65536];
    DWORD read;
    while (ReadFile(session->output, bytes, sizeof(bytes), &read, NULL)) {
        if (!read) { return 0; }
        bt_lock(session->terminal);
        bt_write(session->terminal, bytes, read);
        bt_unlock(session->terminal);
    }
    record_error(session, GetLastError());
    return 0;
}

static DWORD WINAPI write_input(void* context) {
    bt_session* session = context;
    for (;;) {
        WaitForSingleObject(session->ready, INFINITE);
        if (InterlockedCompareExchange(&session->stopping, 0, 0)) { return 0; }
        for (;;) {
            EnterCriticalSection(&session->queue_gate);
            input_chunk* chunk = session->first;
            if (chunk) {
                session->first = chunk->next;
                if (!session->first) { session->last = NULL; }
                session->queued_bytes -= chunk->length;
            }
            LeaveCriticalSection(&session->queue_gate);
            if (!chunk) { break; }
            size_t offset = 0;
            while (offset < chunk->length) {
                DWORD written;
                if (!WriteFile(session->input, chunk->data + offset, (DWORD)(chunk->length - offset), &written, NULL)) {
                    record_error(session, GetLastError());
                    free(chunk);
                    return 0;
                }
                if (!written) {
                    record_error(session, ERROR_WRITE_FAULT);
                    free(chunk);
                    return 0;
                }
                offset += written;
            }
            free(chunk);
            if (InterlockedCompareExchange(&session->stopping, 0, 0)) { return 0; }
        }
    }
}

void bt_session_stop(bt_session* session) {
    if (!session) { return; }
    if (InterlockedCompareExchange(&session->stopping, 1, 0)) {
        WaitForSingleObject(session->stopped, INFINITE);
        return;
    }
    SetEvent(session->ready);
    /* Closing ConPTY before a new child has attached can leave it in startup
     * error handling. Stop our root process first, then close the console and
     * drain remaining output from its attached children. */
    if (session->process && WaitForSingleObject(session->process, 0) == WAIT_TIMEOUT) {
        TerminateProcess(session->process, ERROR_CANCELLED);
        WaitForSingleObject(session->process, INFINITE);
    }
    EnterCriticalSection(&session->console_gate);
    if (session->console) {
        /* The reader remains alive while ConPTY closes, including on Windows 10. */
        ConptyClosePseudoConsole(session->console);
        session->console = NULL;
    }
    LeaveCriticalSection(&session->console_gate);
    if (session->writer) {
        CancelSynchronousIo(session->writer);
        WaitForSingleObject(session->writer, INFINITE);
    }
    if (session->reader) { WaitForSingleObject(session->reader, INFINITE); }
    SetEvent(session->stopped);
}

void bt_session_free(bt_session* session) {
    if (!session) { return; }
    bt_session_stop(session);
    if (session->reader) { CloseHandle(session->reader); }
    if (session->writer) { CloseHandle(session->writer); }
    if (session->process) { CloseHandle(session->process); }
    if (session->input) { CloseHandle(session->input); }
    if (session->output) { CloseHandle(session->output); }
    if (session->ready) { CloseHandle(session->ready); }
    if (session->stopped) { CloseHandle(session->stopped); }
    input_chunk* chunk = session->first;
    while (chunk) { input_chunk* next = chunk->next; free(chunk); chunk = next; }
    bt_free(session->terminal);
    DeleteCriticalSection(&session->console_gate);
    DeleteCriticalSection(&session->queue_gate);
    free(session);
}

int bt_session_create(const wchar_t* command, const wchar_t* directory,
    uint16_t columns, uint16_t rows, bt_session** result) {
    if (!result) { return E_INVALIDARG; }
    *result = NULL;
    if (!command || !command[0] || !columns || !rows || columns > 32767 || rows > 32767) {
        return E_INVALIDARG;
    }
    bt_session* session = calloc(1, sizeof(*session));
    if (!session) { return E_OUTOFMEMORY; }
    InitializeCriticalSection(&session->queue_gate);
    InitializeCriticalSection(&session->console_gate);
    HANDLE input_read = NULL, output_write = NULL;
    LPPROC_THREAD_ATTRIBUTE_LIST attributes = NULL;
    bool attributes_initialized = false;
    wchar_t* mutable_command = NULL;
    int error = 0;
    session->ready = CreateEventW(NULL, FALSE, FALSE, NULL);
    session->stopped = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!session->ready || !session->stopped) { error = win32_error(); goto done; }
    error = bt_create(columns, rows, enqueue, session, &session->terminal);
    if (error) { error = E_OUTOFMEMORY; goto done; }
    /* ConPTY has no scrollback to pull back into its active screen on resize.
     * Match its viewport so subsequent absolute cursor moves stay aligned. */
    error = bt_set_resize_pull_scrollback(session->terminal, 0);
    if (error) { error = E_FAIL; goto done; }
    if (!CreatePipe(&input_read, &session->input, NULL, 0) ||
        !CreatePipe(&session->output, &output_write, NULL, 0)) {
        error = win32_error(); goto done;
    }
    error = ConptyCreatePseudoConsole((COORD){ (SHORT)columns, (SHORT)rows }, input_read, output_write, 0, &session->console);
    if (error) { goto done; }
    SIZE_T bytes = 0;
    InitializeProcThreadAttributeList(NULL, 1, 0, &bytes);
    attributes = malloc(bytes);
    if (!attributes) { error = E_OUTOFMEMORY; goto done; }
    if (!InitializeProcThreadAttributeList(attributes, 1, 0, &bytes)) { error = win32_error(); goto done; }
    attributes_initialized = true;
    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
        session->console, sizeof(HPCON), NULL, NULL)) { error = win32_error(); goto done; }
    mutable_command = _wcsdup(command);
    if (!mutable_command) { error = E_OUTOFMEMORY; goto done; }
    /* Start both IO workers before the child can emit output or query the terminal. */
    session->reader = CreateThread(NULL, 0, read_output, session, 0, NULL);
    if (!session->reader) { error = win32_error(); goto done; }
    session->writer = CreateThread(NULL, 0, write_input, session, 0, NULL);
    if (!session->writer) { error = win32_error(); goto done; }
    STARTUPINFOEXW startup = {0};
    startup.StartupInfo.cb = sizeof(startup);
    /* Null standard handles prevent inheriting the host's redirected streams.
     * ConPTY supplies console handles during child console initialization.
     * https://github.com/microsoft/terminal/discussions/15814 */
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.lpAttributeList = attributes;
    PROCESS_INFORMATION process = {0};
    if (!CreateProcessW(NULL, mutable_command, NULL, NULL, FALSE,
        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, NULL, directory,
        &startup.StartupInfo, &process)) { error = win32_error(); goto done; }
    session->process = process.hProcess;
    CloseHandle(process.hThread);
done:
    if (attributes_initialized) { DeleteProcThreadAttributeList(attributes); }
    free(attributes);
    free(mutable_command);
    if (input_read) { CloseHandle(input_read); }
    if (output_write) { CloseHandle(output_write); }
    if (error) {
        /* Without a reader, closing the pipe is required to avoid ConPTY shutdown deadlock. */
        if (!session->reader && session->output) { CloseHandle(session->output); session->output = NULL; }
        bt_session_free(session);
        return error;
    }
    *result = session;
    return 0;
}

bt_terminal* bt_session_terminal(bt_session* session) { return session->terminal; }

int bt_session_resize(bt_session* session, uint16_t columns, uint16_t rows) {
    if (!columns || !rows || columns > 32767 || rows > 32767) { return E_INVALIDARG; }
    if (!TryEnterCriticalSection(&session->console_gate)) { return S_FALSE; }
    int result = session->console ? ConptyResizePseudoConsole(session->console, (COORD){ (SHORT)columns, (SHORT)rows }) : S_FALSE;
    LeaveCriticalSection(&session->console_gate);
    return result;
}

int bt_session_wait(bt_session* session, uint32_t* exit_code) {
    if (WaitForSingleObject(session->process, INFINITE) == WAIT_FAILED) { return win32_error(); }
    DWORD code;
    if (!GetExitCodeProcess(session->process, &code)) { return win32_error(); }
    *exit_code = code;
    return 0;
}

int bt_session_error(bt_session* session) {
    return (int)HRESULT_FROM_WIN32((DWORD)InterlockedCompareExchange(&session->error, 0, 0));
}

int bt_session_dropped_input(bt_session* session) {
    /* Observing a dropped input chunk clears it; the session stays usable. */
    DWORD dropped = (DWORD)InterlockedExchange(&session->dropped_input, 0);
    return dropped ? (int)HRESULT_FROM_WIN32(dropped) : 0;
}

uint32_t bt_win32_key(uint32_t key) {
    if (key >= 'A' && key <= 'Z') { return GHOSTTY_KEY_A + key - 'A'; }
    if (key >= '0' && key <= '9') { return GHOSTTY_KEY_DIGIT_0 + key - '0'; }
    if (key >= VK_F1 && key <= VK_F24) { return GHOSTTY_KEY_F1 + key - VK_F1; }
    if (key >= VK_NUMPAD0 && key <= VK_NUMPAD9) { return GHOSTTY_KEY_NUMPAD_0 + key - VK_NUMPAD0; }
    switch (key) {
        case VK_BACK: return GHOSTTY_KEY_BACKSPACE;
        case VK_TAB: return GHOSTTY_KEY_TAB;
        case VK_RETURN: return GHOSTTY_KEY_ENTER;
        case VK_ESCAPE: return GHOSTTY_KEY_ESCAPE;
        case VK_SPACE: return GHOSTTY_KEY_SPACE;
        case VK_PRIOR: return GHOSTTY_KEY_PAGE_UP;
        case VK_NEXT: return GHOSTTY_KEY_PAGE_DOWN;
        case VK_HOME: return GHOSTTY_KEY_HOME;
        case VK_END: return GHOSTTY_KEY_END;
        case VK_INSERT: return GHOSTTY_KEY_INSERT;
        case VK_DELETE: return GHOSTTY_KEY_DELETE;
        case VK_LEFT: return GHOSTTY_KEY_ARROW_LEFT;
        case VK_RIGHT: return GHOSTTY_KEY_ARROW_RIGHT;
        case VK_UP: return GHOSTTY_KEY_ARROW_UP;
        case VK_DOWN: return GHOSTTY_KEY_ARROW_DOWN;
        case VK_OEM_MINUS: return GHOSTTY_KEY_MINUS;
        case VK_OEM_PLUS: return GHOSTTY_KEY_EQUAL;
        case VK_OEM_COMMA: return GHOSTTY_KEY_COMMA;
        case VK_OEM_PERIOD: return GHOSTTY_KEY_PERIOD;
        case VK_OEM_1: return GHOSTTY_KEY_SEMICOLON;
        case VK_OEM_2: return GHOSTTY_KEY_SLASH;
        case VK_OEM_3: return GHOSTTY_KEY_BACKQUOTE;
        case VK_OEM_4: return GHOSTTY_KEY_BRACKET_LEFT;
        case VK_OEM_5: return GHOSTTY_KEY_BACKSLASH;
        case VK_OEM_6: return GHOSTTY_KEY_BRACKET_RIGHT;
        case VK_OEM_7: return GHOSTTY_KEY_QUOTE;
        case VK_OEM_102: return GHOSTTY_KEY_INTL_BACKSLASH;
        /* Modifiers; WPF reports the side-specific virtual keys, and the
         * generic codes are left over from non-WPF callers. */
        case VK_SHIFT: return GHOSTTY_KEY_SHIFT_LEFT;
        case VK_LSHIFT: return GHOSTTY_KEY_SHIFT_LEFT;
        case VK_RSHIFT: return GHOSTTY_KEY_SHIFT_RIGHT;
        case VK_CONTROL: return GHOSTTY_KEY_CONTROL_LEFT;
        case VK_LCONTROL: return GHOSTTY_KEY_CONTROL_LEFT;
        case VK_RCONTROL: return GHOSTTY_KEY_CONTROL_RIGHT;
        case VK_MENU: return GHOSTTY_KEY_ALT_LEFT;
        case VK_LMENU: return GHOSTTY_KEY_ALT_LEFT;
        case VK_RMENU: return GHOSTTY_KEY_ALT_RIGHT;
        case VK_LWIN: return GHOSTTY_KEY_META_LEFT;
        case VK_RWIN: return GHOSTTY_KEY_META_RIGHT;
        /* Locks and other non-text control keys. */
        case VK_CAPITAL: return GHOSTTY_KEY_CAPS_LOCK;
        case VK_NUMLOCK: return GHOSTTY_KEY_NUM_LOCK;
        case VK_SCROLL: return GHOSTTY_KEY_SCROLL_LOCK;
        case VK_SNAPSHOT: return GHOSTTY_KEY_PRINT_SCREEN;
        case VK_PAUSE: return GHOSTTY_KEY_PAUSE;
        case VK_APPS: return GHOSTTY_KEY_CONTEXT_MENU;
        case VK_SLEEP: return GHOSTTY_KEY_SLEEP;
        case VK_HELP: return GHOSTTY_KEY_HELP;
        case VK_CLEAR: return GHOSTTY_KEY_NUMPAD_CLEAR;
        /* Numpad operators; the digits are handled above. */
        case VK_MULTIPLY: return GHOSTTY_KEY_NUMPAD_MULTIPLY;
        case VK_ADD: return GHOSTTY_KEY_NUMPAD_ADD;
        case VK_SEPARATOR: return GHOSTTY_KEY_NUMPAD_SEPARATOR;
        case VK_SUBTRACT: return GHOSTTY_KEY_NUMPAD_SUBTRACT;
        case VK_DECIMAL: return GHOSTTY_KEY_NUMPAD_DECIMAL;
        case VK_DIVIDE: return GHOSTTY_KEY_NUMPAD_DIVIDE;
        /* Media and browser keys. */
        case VK_BROWSER_BACK: return GHOSTTY_KEY_BROWSER_BACK;
        case VK_BROWSER_FORWARD: return GHOSTTY_KEY_BROWSER_FORWARD;
        case VK_BROWSER_REFRESH: return GHOSTTY_KEY_BROWSER_REFRESH;
        case VK_BROWSER_STOP: return GHOSTTY_KEY_BROWSER_STOP;
        case VK_BROWSER_SEARCH: return GHOSTTY_KEY_BROWSER_SEARCH;
        case VK_BROWSER_FAVORITES: return GHOSTTY_KEY_BROWSER_FAVORITES;
        case VK_BROWSER_HOME: return GHOSTTY_KEY_BROWSER_HOME;
        case VK_VOLUME_MUTE: return GHOSTTY_KEY_AUDIO_VOLUME_MUTE;
        case VK_VOLUME_DOWN: return GHOSTTY_KEY_AUDIO_VOLUME_DOWN;
        case VK_VOLUME_UP: return GHOSTTY_KEY_AUDIO_VOLUME_UP;
        case VK_MEDIA_NEXT_TRACK: return GHOSTTY_KEY_MEDIA_TRACK_NEXT;
        case VK_MEDIA_PREV_TRACK: return GHOSTTY_KEY_MEDIA_TRACK_PREVIOUS;
        case VK_MEDIA_STOP: return GHOSTTY_KEY_MEDIA_STOP;
        case VK_MEDIA_PLAY_PAUSE: return GHOSTTY_KEY_MEDIA_PLAY_PAUSE;
        case VK_LAUNCH_MAIL: return GHOSTTY_KEY_LAUNCH_MAIL;
        case VK_LAUNCH_MEDIA_SELECT: return GHOSTTY_KEY_MEDIA_SELECT;
        case VK_LAUNCH_APP1: return GHOSTTY_KEY_LAUNCH_APP_1;
        case VK_LAUNCH_APP2: return GHOSTTY_KEY_LAUNCH_APP_2;
        default: return GHOSTTY_KEY_UNIDENTIFIED;
    }
}
