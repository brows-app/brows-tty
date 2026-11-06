using Brows.Ghostty;
using Brows.Interop;
using System.Runtime.InteropServices;

namespace Brows.Win32.PlatformInvoke;

internal static class LibraryLoader {
    static LibraryLoader() {
#if NET5_0_OR_GREATER
        if (!OperatingSystem.IsWindows()) {
            throw new PlatformNotSupportedException("ConPTY requires Windows.");
        }
#endif
        TerminalLibrary.EnsureLoaded();
        Module = NativeLibraries.Load("brows-tty-win32", typeof(LibraryLoader).Assembly);
#if NET5_0_OR_GREATER
        NativeLibrary.SetDllImportResolver(typeof(LibraryLoader).Assembly,
            (name, assembly, path) => name == "brows-tty-win32" ? Module : IntPtr.Zero);
#endif
    }

    private static readonly IntPtr Module;

    internal static void EnsureLoaded() {
        GC.KeepAlive(Module);
    }
}
