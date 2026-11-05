#include <windows.h>
#include <string.h>

static DWORD WINAPI receive_colors(void* context) {
    HANDLE input = context;
    char reply[1024] = {0};
    size_t used = 0;
    while (used < sizeof(reply) - 1) {
        DWORD count;
        if (!ReadFile(input, reply + used, (DWORD)(sizeof(reply) - 1 - used), &count, NULL) || !count) {
            return 1;
        }
        used += count;
        if (strstr(reply, "10;rgb:ffff/ffff/ffff") && strstr(reply, "11;rgb:0000/0000/0000")) {
            return 0;
        }
    }
    return 2;
}

int main(void) {
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD mode;
    if (!SetConsoleMode(input, ENABLE_VIRTUAL_TERMINAL_INPUT) || !GetConsoleMode(output, &mode) ||
        !SetConsoleMode(output, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING)) { return 10; }
    HANDLE reader = CreateThread(NULL, 0, receive_colors, input, 0, NULL);
    if (!reader) { return 11; }
    const char query[] = "\x1b]10;?\x1b\\\x1b]11;?\x1b\\";
    DWORD written;
    if (!WriteFile(output, query, sizeof(query) - 1, &written, NULL)) { return 12; }
    if (WaitForSingleObject(reader, 5000) != WAIT_OBJECT_0) { return 13; }
    DWORD result;
    if (!GetExitCodeThread(reader, &result)) { return 14; }
    CloseHandle(reader);
    if (result) { return 15; }
    const char success[] = "\x1b]11;#0d1117\x07" "color-query-roundtrip-ok";
    if (!WriteFile(output, success, sizeof(success) - 1, &written, NULL)) { return 16; }
    return 0;
}
