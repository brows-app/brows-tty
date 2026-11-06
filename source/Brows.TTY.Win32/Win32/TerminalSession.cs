using Brows.Ghostty;
using Brows.Win32.PlatformInvoke;
using System.Runtime.InteropServices;

namespace Brows.Win32;

internal sealed class TerminalSession : IDisposable {
    private readonly SessionHandle _handle;
    private int _disposed;

    private uint WaitForExit() {
        Marshal.ThrowExceptionForHR(SessionMethods.bt_session_wait(_handle, out uint code));
        SessionMethods.bt_session_stop(_handle);
        /*
         * Transport errors are reported separately through CheckError so that
         * a failed transport never turns the exit report itself into a fault.
         */
        return code;
    }

    internal TerminalEngine Engine { get; }
    internal Task<uint> Completion { get; }
    internal bool Faulted => SessionMethods.bt_session_error(_handle) != 0;

    internal TerminalSession(string command, string? directory, ushort columns, ushort rows) {
        if (string.IsNullOrWhiteSpace(command)) {
            throw new ArgumentException("A command is required.", nameof(command));
        }
        if (command.IndexOf('\0') >= 0) {
            throw new ArgumentException("Commands cannot contain NUL.", nameof(command));
        }
        if (directory?.IndexOf('\0') >= 0) {
            throw new ArgumentException("Directories cannot contain NUL.", nameof(directory));
        }
        LibraryLoader.EnsureLoaded();
        Marshal.ThrowExceptionForHR(
            SessionMethods.bt_session_create(command, directory, columns, rows, out IntPtr handle));
        _handle = new SessionHandle(handle);
        try {
            Engine = new TerminalEngine(SessionMethods.bt_session_terminal(_handle), _handle);
            Completion = Task.Factory.StartNew(WaitForExit, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch {
            _handle.Dispose();
            throw;
        }
    }

    internal void CheckError() {
        /*
         * A dead reader or writer latches permanently; dropped input is
         * observed once and leaves the session usable.
         */
        Marshal.ThrowExceptionForHR(SessionMethods.bt_session_error(_handle));
        Marshal.ThrowExceptionForHR(SessionMethods.bt_session_dropped_input(_handle));
    }

    internal void Resize(ushort columns, ushort rows, uint width, uint height) {
        Engine.Resize(columns, rows, width, height);
        Marshal.ThrowExceptionForHR(SessionMethods.bt_session_resize(_handle, columns, rows));
    }

    internal void Key(uint virtualKey, ushort modifiers, uint action, string text) {
        Engine.Key((TerminalKey)SessionMethods.bt_win32_key(virtualKey), modifiers, action, text);
    }

    internal void Stop() {
        SessionMethods.bt_session_stop(_handle);
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }
        try {
            Stop();
            Completion.GetAwaiter().GetResult();
        }
        finally {
            Engine.Dispose();
            _handle.Dispose();
        }
    }
}
