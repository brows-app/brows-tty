#include <windows.h>
#include <stdio.h>

/* Reports console key event records as a classic console application reads
 * them, without virtual terminal input, so integration tests can verify the
 * key identity and modifier state that reach the child through ConPTY. Each
 * line is key-record:<down>,<virtual key>,<character>,<control key state>,
 * with hexadecimal fields. */
int main(void) {
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (!SetConsoleMode(input, ENABLE_EXTENDED_FLAGS)) { return 1; }
    const char ready[] = "key-records-ready\r\n";
    DWORD written;
    if (!WriteFile(output, ready, sizeof(ready) - 1, &written, NULL)) { return 2; }
    DWORD start = GetTickCount();
    for (;;) {
        if (GetTickCount() - start > 60000) { return 0; }
        if (WaitForSingleObject(input, 200) != WAIT_OBJECT_0) { continue; }
        INPUT_RECORD records[16];
        DWORD read;
        if (!ReadConsoleInputW(input, records, 16, &read)) { return 3; }
        for (DWORD i = 0; i < read; i++) {
            if (records[i].EventType != KEY_EVENT) { continue; }
            const KEY_EVENT_RECORD* key = &records[i].Event.KeyEvent;
            char line[96];
            int length = snprintf(line, sizeof(line), "key-record:%d,%02x,%04x,%04lx\r\n",
                key->bKeyDown ? 1 : 0, key->wVirtualKeyCode, key->uChar.UnicodeChar,
                (unsigned long)key->dwControlKeyState);
            if (length <= 0 || !WriteFile(output, line, (DWORD)length, &written, NULL)) { return 4; }
        }
    }
}
