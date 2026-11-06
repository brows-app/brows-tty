using Brows.Interop;
using System.Runtime.InteropServices;

namespace Brows.Ghostty;

internal static class TerminalLibrary {
    static TerminalLibrary() {
        Module = NativeLibraries.Load("brows-ghostty", typeof(TerminalLibrary).Assembly);
#if NET5_0_OR_GREATER
        NativeLibrary.SetDllImportResolver(typeof(TerminalLibrary).Assembly,
            (name, assembly, path) => name == "brows-ghostty" ? Module : IntPtr.Zero);
#endif
    }

    /*
     * Keep the module loaded through SafeHandle finalization, as in the original loader.
     */
    private static readonly IntPtr Module;

    internal static void EnsureLoaded() {
        GC.KeepAlive(Module);
        if (NativeMethods.bt_abi_version() != 2) {
            throw new NotSupportedException("The native terminal ABI is incompatible with this managed library.");
        }
    }
}
