using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
#if NETFRAMEWORK
using System.ComponentModel;
#endif

namespace Brows.Interop;

internal static class NativeLibraries {
#if NETFRAMEWORK
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
#endif

    internal static IntPtr Load(string name, Assembly assembly) {
        string rid;
        string filename;
#if NET5_0_OR_GREATER
        string architecture = RuntimeInformation.ProcessArchitecture switch {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("The terminal requires an x64 or ARM64 process.")
        };
        if (OperatingSystem.IsWindows()) {
            rid = "win-" + architecture;
            filename = name + ".dll";
        }
        else if (OperatingSystem.IsLinux()) {
            rid = "linux-" + architecture;
            filename = "lib" + name + ".so";
        }
        else if (OperatingSystem.IsMacOS()) {
            rid = "osx-" + architecture;
            filename = "lib" + name + ".dylib";
        }
        else {
            throw new PlatformNotSupportedException("Unsupported native terminal platform.");
        }
#else
        if (!Environment.Is64BitProcess) {
            throw new PlatformNotSupportedException("The terminal requires an x64 or ARM64 process.");
        }
        if (IsWow64Process2(GetCurrentProcess(), out ushort process, out ushort native) == 0) {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        ushort machine = process == 0 ? native : process;
        rid = machine switch {
            0x8664 => "win-x64",
            0xaa64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("Unsupported process architecture.")
        };
        filename = name + ".dll";
#endif
        string location = assembly.Location;
        string directory = string.IsNullOrEmpty(location)
            ? AppContext.BaseDirectory : Path.GetDirectoryName(location)!;
        string path = Path.Combine(directory, "runtimes", rid, "native", filename);
        if (!File.Exists(path)) {
            path = Path.Combine(directory, filename);
        }
#if NET5_0_OR_GREATER
        return NativeLibrary.Load(path, assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
#else
        IntPtr module = LoadLibraryExW(path, IntPtr.Zero, 0x00000100 | 0x00001000);
        if (module == IntPtr.Zero) {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load native terminal library: " + path);
        }
        return module;
#endif
    }
}
