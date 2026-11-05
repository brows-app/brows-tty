using System.Runtime.InteropServices;

namespace Brows.Ghostty;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCell {
    internal uint Foreground;
    internal uint Background;
    internal uint Underline;
    internal uint TextOffset;
    internal uint TextLength;
    internal ushort Flags;
    internal byte Width;
    internal byte Selected;
}
