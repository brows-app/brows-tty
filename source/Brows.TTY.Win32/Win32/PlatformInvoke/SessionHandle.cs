using Microsoft.Win32.SafeHandles;

namespace Brows.Win32.PlatformInvoke;

internal sealed class SessionHandle : SafeHandleZeroOrMinusOneIsInvalid {
    internal SessionHandle(IntPtr value) : base(true) {
        SetHandle(value);
    }

    protected override bool ReleaseHandle() {
        SessionMethods.bt_session_free(handle);
        return true;
    }
}
