#include <windows.h>

/* Echoes raw console input as hexadecimal so integration tests can verify the
 * escape sequences that reach a child process. Enables virtual terminal input
 * so ConPTY forwards terminal input uninterpreted, and requests Kitty keyboard
 * flags 2 and 8 (report event types and all keys as escape codes). */
int main(void) {
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (!SetConsoleCP(CP_UTF8)) { return 5; }
    if (!SetConsoleMode(input, ENABLE_VIRTUAL_TERMINAL_INPUT)) { return 1; }
    const char flags[] = "\x1b[=10;1u";
    DWORD written;
    if (!WriteFile(output, flags, sizeof(flags) - 1, &written, NULL)) { return 2; }
    const char ready[] = "key-echo-ready\n";
    if (!WriteFile(output, ready, sizeof(ready) - 1, &written, NULL)) { return 3; }
    DWORD start = GetTickCount();
    for (;;) {
        if (GetTickCount() - start > 60000) { return 0; }
        if (WaitForSingleObject(input, 200) != WAIT_OBJECT_0) { continue; }
        BYTE bytes[512];
        DWORD read;
        if (!ReadFile(input, bytes, sizeof(bytes), &read, NULL) || !read) { return 0; }
        static const char hex[] = "0123456789abcdef";
        char line[sizeof("key-echo:") + sizeof(bytes) * 2 + 1] = "key-echo:";
        size_t used = sizeof("key-echo:") - 1;
        for (DWORD i = 0; i < read; i++) {
            line[used++] = hex[bytes[i] >> 4];
            line[used++] = hex[bytes[i] & 0xf];
        }
        line[used++] = '\n';
        if (!WriteFile(output, line, (DWORD)used, &written, NULL)) { return 4; }
    }
}
