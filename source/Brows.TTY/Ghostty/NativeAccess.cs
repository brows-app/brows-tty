namespace Brows.Ghostty;

internal readonly struct NativeAccess : IDisposable {
    private readonly TerminalHandle _handle;

    internal NativeAccess(TerminalHandle handle) {
        _handle = handle;
        NativeMethods.bt_lock(handle);
    }

    public void Dispose() {
        NativeMethods.bt_unlock(_handle);
    }
}
