#ifndef BROWS_PLATFORM_H
#define BROWS_PLATFORM_H
#include <stdint.h>

/* These are engine synchronization/clock primitives, not process transport.
 * Each native core build links only the implementation for its own platform. */
#ifdef _WIN32
#include <windows.h>
typedef CRITICAL_SECTION bt_mutex;
static inline int bt_mutex_init(bt_mutex* gate) { InitializeCriticalSection(gate); return 0; }
static inline void bt_mutex_destroy(bt_mutex* gate) { DeleteCriticalSection(gate); }
static inline void bt_mutex_lock(bt_mutex* gate) { EnterCriticalSection(gate); }
static inline void bt_mutex_unlock(bt_mutex* gate) { LeaveCriticalSection(gate); }
static inline uint64_t bt_now_ms(void) { return GetTickCount64(); }
#else
#include <pthread.h>
#include <time.h>
typedef pthread_mutex_t bt_mutex;
static inline int bt_mutex_init(bt_mutex* gate) {
    pthread_mutexattr_t attributes;
    int result = pthread_mutexattr_init(&attributes);
    if (result) { return result; }
    result = pthread_mutexattr_settype(&attributes, PTHREAD_MUTEX_RECURSIVE);
    if (!result) { result = pthread_mutex_init(gate, &attributes); }
    pthread_mutexattr_destroy(&attributes);
    return result;
}
static inline void bt_mutex_destroy(bt_mutex* gate) { pthread_mutex_destroy(gate); }
static inline void bt_mutex_lock(bt_mutex* gate) { pthread_mutex_lock(gate); }
static inline void bt_mutex_unlock(bt_mutex* gate) { pthread_mutex_unlock(gate); }
static inline uint64_t bt_now_ms(void) {
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (uint64_t)now.tv_sec * 1000 + (uint64_t)now.tv_nsec / 1000000;
}
#endif
#endif
