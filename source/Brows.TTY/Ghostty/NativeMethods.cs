using System.Runtime.InteropServices;

namespace Brows.Ghostty;

internal static class NativeMethods {
    private const string Library = "brows-ghostty";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OutputCallback(IntPtr context, IntPtr bytes, UIntPtr length);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint bt_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bt_lock(TerminalHandle terminal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bt_unlock(TerminalHandle terminal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_create(ushort columns, ushort rows, OutputCallback output,
                                         IntPtr context, out IntPtr result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bt_free(IntPtr terminal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bt_write(TerminalHandle terminal, byte[] bytes, UIntPtr length);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_resize(TerminalHandle terminal, ushort columns, ushort rows,
                                         uint cellWidth, uint cellHeight);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_complete_output(TerminalHandle terminal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_begin_frame(TerminalHandle terminal, out NativeFrame frame, int force);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_next_row(TerminalHandle terminal, out ushort y, out IntPtr cells,
                                           out IntPtr text, out UIntPtr textLength);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_end_frame(TerminalHandle terminal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int bt_key(TerminalHandle terminal, uint virtualKey, ushort modifiers,
                                             uint action, uint unshiftedCodepoint, ushort consumedModifiers, byte* text, UIntPtr length);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int bt_paste(TerminalHandle terminal, byte* text, UIntPtr length);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_focus(TerminalHandle terminal, int focused);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bt_scroll(TerminalHandle terminal, int lines, int bottom);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_mouse(TerminalHandle terminal, uint action, uint button, ushort modifiers,
                                        float x, float y, int anyPressed);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_title(TerminalHandle terminal, out IntPtr text, out UIntPtr length);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_select(TerminalHandle terminal, ushort startX, ushort startY,
                                         ushort endX, ushort endY, int clear);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_selection_begin(TerminalHandle terminal, ushort x, ushort y);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_selection_update(TerminalHandle terminal, ushort x, ushort y);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bt_selection_cancel(TerminalHandle terminal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bt_selection_text(TerminalHandle terminal, out IntPtr text, out UIntPtr length);
}
