using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Brows.Ghostty;

internal sealed class TerminalHandle : SafeHandleZeroOrMinusOneIsInvalid {
    private readonly NativeMethods.OutputCallback? _callback;
    private readonly SafeHandle? _owner;

    internal TerminalHandle(IntPtr value, NativeMethods.OutputCallback callback) : base(true) {
        _callback = callback;
        SetHandle(value);
    }

    internal TerminalHandle(IntPtr value, SafeHandle owner) : base(true) {
        bool added = false;
        owner.DangerousAddRef(ref added);
        _owner = owner;
        SetHandle(value);
    }

    protected override bool ReleaseHandle() {
        if (_owner is not null) {
            _owner.DangerousRelease();
        }
        else {
            NativeMethods.bt_free(handle);
        }
        GC.KeepAlive(_callback);
        return true;
    }
}
