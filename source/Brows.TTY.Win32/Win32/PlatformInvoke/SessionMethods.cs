using System.Runtime.InteropServices;

namespace Brows.Win32.PlatformInvoke;

internal static class SessionMethods {
    private const string Library = "brows-tty-win32";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint bt_win32_key(uint virtualKey);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int bt_session_create(string command, string? directory,
                                                 ushort columns, ushort rows, out IntPtr result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr bt_session_terminal(SessionHandle session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_session_resize(SessionHandle session, ushort columns, ushort rows);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_session_wait(SessionHandle session, out uint exitCode);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_session_error(SessionHandle session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_session_dropped_input(SessionHandle session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bt_session_stop(SessionHandle session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bt_session_free(IntPtr session);
}
