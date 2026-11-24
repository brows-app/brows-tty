using Brows.Ghostty;
using Brows.Win32.PlatformInvoke;
using System.Runtime.InteropServices;

namespace Brows.Win32;

internal sealed class TerminalSession : IDisposable {
    private readonly SessionHandle _handle;
    private int _disposed;
    private readonly Dictionary<uint, char> Win32KeyPresses = new();
    private readonly
#if NET9_0_OR_GREATER
        Lock
#else
        object
#endif
        Win32KeyLocker = new();

    /*
     * ConPTY turns terminal input into console key records. It drops key
     * sequences it does not parse, such as Ghostty's CSI 27;2;13~ for
     * Shift+Enter, and legacy encodings cannot carry modifiers on Enter, Tab,
     * or Backspace. ConPTY requests win32-input-mode for this reason; each
     * record carries the virtual key, scan code, character, and modifier state
     * that classic console applications read. Applications that enable Kitty
     * keyboard flags or xterm modifyOtherKeys mode 2 keep Ghostty's encoding, which ConPTY passes through to
     * virtual terminal input unchanged but would not produce from records.
     */
    private bool TrySendWin32Key(uint virtualKey, ushort modifiers, uint action, string text) {
        var keyIsReleased = action == 0;
        var keyIsRecord = keyIsReleased
            ? TryReleaseWin32Key(virtualKey, out var character)
            : TryPressWin32Key(virtualKey, modifiers, text, out character);
        if (!keyIsRecord) {
            return false;
        }
        var scanCode = WindowsKeyTranslator.ScanCode(virtualKey);
        Marshal.ThrowExceptionForHR(SessionMethods.bt_session_win32_key(_handle,
            WindowsKeyTranslator.ConsoleVirtualKey(virtualKey), (ushort)(scanCode & 0xff), character,
            keyIsReleased ? 0 : 1, WindowsKeyTranslator.ControlKeyState(modifiers, scanCode), 1));
        return true;
    }

    private bool TryPressWin32Key(uint virtualKey, ushort modifiers, string text, out char character) {
        character = '\0';
        var textFitsRecord = text.Length <= 1;
        var recordIsUsable = textFitsRecord && SessionMethods.bt_session_win32_input(_handle) != 0;
        if (!recordIsUsable) {
            lock (Win32KeyLocker) {
                Win32KeyPresses.Remove(virtualKey);
            }
            return false;
        }
        character = text.Length == 1 ? text[0] : WindowsKeyTranslator.ConsoleCharacter(virtualKey, modifiers);
        lock (Win32KeyLocker) {
            Win32KeyPresses[virtualKey] = character;
        }
        return true;
    }

    /*
     * A release follows its press, so a key pressed as a record is released as
     * a record even if the input mode has changed since.
     */
    private bool TryReleaseWin32Key(uint virtualKey, out char character) {
        lock (Win32KeyLocker) {
            var keyWasPressedAsRecord = Win32KeyPresses.TryGetValue(virtualKey, out character);
            if (!keyWasPressedAsRecord) {
                return false;
            }
            Win32KeyPresses.Remove(virtualKey);
            return true;
        }
    }

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
        bool columnsAreInvalid = columns == 0 || columns > 32767;
        if (columnsAreInvalid) {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }
        bool rowsAreInvalid = rows == 0 || rows > 32767;
        if (rowsAreInvalid) {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }
        Engine.Resize(columns, rows, width, height);
        Marshal.ThrowExceptionForHR(SessionMethods.bt_session_resize(_handle, columns, rows));
    }

    internal void Key(uint virtualKey, ushort modifiers, uint action, string text) {
        uint unshiftedCodepoint = WindowsKeyTranslator.UnshiftedCodepoint(virtualKey);
        ushort consumedModifiers = string.IsNullOrEmpty(text)
            ? (ushort)0
            : WindowsKeyTranslator.ConsumedModifiers(virtualKey, modifiers);
        Key(virtualKey, modifiers, action, unshiftedCodepoint, consumedModifiers, text);
    }

    internal uint UnshiftedCodepoint(uint virtualKey) {
        return WindowsKeyTranslator.UnshiftedCodepoint(virtualKey);
    }

    internal ushort ConsumedModifiers(uint virtualKey, ushort modifiers) {
        return WindowsKeyTranslator.ConsumedModifiers(virtualKey, modifiers);
    }

    internal string? ExpectedPhysicalText(uint virtualKey, ushort modifiers) {
        return WindowsKeyTranslator.ExpectedPhysicalText(virtualKey, modifiers);
    }

    internal void Key(uint virtualKey, ushort modifiers, uint action, uint unshiftedCodepoint,
                      ushort consumedModifiers, string text) {
        if (TrySendWin32Key(virtualKey, modifiers, action, text)) {
            return;
        }
        Engine.Key((TerminalKey)SessionMethods.bt_win32_key(virtualKey), modifiers, action,
            unshiftedCodepoint, consumedModifiers, text);
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
