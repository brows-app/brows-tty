#ifndef BROWS_SESSION_H
#define BROWS_SESSION_H
#include "brows_ghostty.h"
#include <wchar.h>
#define BT_WIN32_API __declspec(dllexport)

typedef struct bt_session bt_session;
BT_WIN32_API int bt_session_create(const wchar_t* command, const wchar_t* directory,
    uint16_t columns, uint16_t rows, bt_session** result);
BT_WIN32_API bt_terminal* bt_session_terminal(bt_session* session);
BT_WIN32_API int bt_session_resize(bt_session* session, uint16_t columns, uint16_t rows);
BT_WIN32_API int bt_session_wait(bt_session* session, uint32_t* exit_code);
BT_WIN32_API int bt_session_error(bt_session* session);
BT_WIN32_API int bt_session_dropped_input(bt_session* session);
BT_WIN32_API void bt_session_stop(bt_session* session);
BT_WIN32_API void bt_session_free(bt_session* session);
BT_WIN32_API uint32_t bt_win32_key(uint32_t virtual_key);
#endif
