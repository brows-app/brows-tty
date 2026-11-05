using System.Runtime.InteropServices;

namespace Brows.Ghostty;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFrame {
    internal uint Columns;
    internal uint Rows;
    internal uint Background;
    internal uint Foreground;
    internal uint CursorColor;
    internal uint CursorX;
    internal uint CursorY;
    internal uint CursorStyle;
    internal uint CursorVisible;
    internal uint CursorBlink;
    internal uint Dirty;
    internal uint MouseTracking;
}
