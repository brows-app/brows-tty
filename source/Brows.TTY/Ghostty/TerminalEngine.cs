using System.Runtime.InteropServices;
using System.Text;
#if NET5_0_OR_GREATER
using System.Buffers;
#endif

namespace Brows.Ghostty;

internal sealed class TerminalEngine : IDisposable {
    private const int SelectionCanceled = 1;

#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly TerminalHandle _handle;
    private Exception? _callbackError;
    private bool _disposed;
    private byte[] _titleBytes = Array.Empty<byte>();
    private string _title = "";

    private void Check(int result) {
        if (_callbackError is not null) {
            Exception error = _callbackError;
            _callbackError = null;
            throw new InvalidOperationException("Terminal output callback failed.", error);
        }
        if (result != 0) {
            throw new InvalidOperationException("Ghostty bridge returned error " + result + ".");
        }
    }

    private void VerifyOpen() {
        if (_disposed) {
            throw new ObjectDisposedException(nameof(TerminalEngine));
        }
    }

    private unsafe void SendInput(byte* bytes, int length, bool paste, uint key, ushort modifiers, uint action,
                                  uint unshiftedCodepoint, ushort consumedModifiers) {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            int result = paste
                ? NativeMethods.bt_paste(_handle, bytes, (UIntPtr)(uint)length)
                : NativeMethods.bt_key(_handle, key, modifiers, action, unshiftedCodepoint, consumedModifiers,
                    bytes, (UIntPtr)(uint)length);
            Check(result);
        }
    }

    private unsafe void EncodeInput(string text, bool paste, uint key = 0, ushort modifiers = 0, uint action = 0,
                                    uint unshiftedCodepoint = 0, ushort consumedModifiers = 0) {
#if NET5_0_OR_GREATER
        int length = Encoding.UTF8.GetByteCount(text);
        byte[]? rented = null;
        Span<byte> bytes = length <= 512
            ? stackalloc byte[512]
            : (rented = ArrayPool<byte>.Shared.Rent(length));
        try {
            Encoding.UTF8.GetBytes(text.AsSpan(), bytes);
            fixed (byte* pointer = bytes) {
                SendInput(pointer, length, paste, key, modifiers, action, unshiftedCodepoint, consumedModifiers);
            }
        }
        finally {
            /*
             * Native encoding and queueing finish before returning; no pointer escapes.
             * Clear pooled input because it may contain passwords or pasted secrets.
             */
            if (rented is not null) {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
#else
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* pointer = bytes) {
            SendInput(pointer, bytes.Length, paste, key, modifiers, action, unshiftedCodepoint, consumedModifiers);
        }
#endif
    }

    private static uint DefaultUnshiftedCodepoint(TerminalKey key) {
        uint keyValue = (uint)key;
        uint letterStart = (uint)TerminalKey.A;
        uint letterEnd = (uint)TerminalKey.Z;
        bool isLetterKey = keyValue >= letterStart && keyValue <= letterEnd;
        if (isLetterKey) {
            return keyValue - letterStart + 'a';
        }

        uint digitStart = (uint)TerminalKey.Digit0;
        uint digitEnd = (uint)TerminalKey.Digit9;
        bool isDigitKey = keyValue >= digitStart && keyValue <= digitEnd;
        if (isDigitKey) {
            return keyValue - digitStart + '0';
        }

        switch (key) {
            case TerminalKey.Backquote:
                return '`';
            case TerminalKey.Backslash:
            case TerminalKey.IntlBackslash:
                return '\\';
            case TerminalKey.BracketLeft:
                return '[';
            case TerminalKey.BracketRight:
                return ']';
            case TerminalKey.Comma:
                return ',';
            case TerminalKey.Equal:
                return '=';
            case TerminalKey.Minus:
                return '-';
            case TerminalKey.Period:
                return '.';
            case TerminalKey.Quote:
                return '\'';
            case TerminalKey.Semicolon:
                return ';';
            case TerminalKey.Slash:
                return '/';
            case TerminalKey.Space:
                return ' ';
            default:
                return 0;
        }
    }

    private unsafe string ReadTitle() {
        Check(NativeMethods.bt_title(_handle, out IntPtr title, out UIntPtr titleLength));
        int length = checked((int)titleLength.ToUInt64());
        bool changed = length != _titleBytes.Length;
        if (!changed) {
            byte* bytes = (byte*)title;
            for (int index = 0; index < length; index++) {
                if (bytes[index] != _titleBytes[index]) {
                    changed = true;
                    break;
                }
            }
        }
        if (!changed) {
            return _title;
        }
        /*
         * ReadFrame holds NativeAccess while examining the borrowed native bytes.
         * Cache an owned copy; native title storage may change on the next write.
         */
        byte[] copy = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length != 0) {
            Marshal.Copy(title, copy, 0, length);
        }
        string decoded = Encoding.UTF8.GetString(copy);
        _titleBytes = copy;
        _title = decoded;
        return decoded;
    }

    internal TerminalEngine(ushort columns, ushort rows, Action<byte[]> output) {
        if (output is null) {
            throw new ArgumentNullException(nameof(output));
        }
        TerminalLibrary.EnsureLoaded();
        NativeMethods.OutputCallback callback = (context, bytes, length) => {
            try {
                byte[] copy = new byte[checked((int)length.ToUInt64())];
                Marshal.Copy(bytes, copy, 0, copy.Length);
                output(copy);
            }
            catch (Exception exception) {
                _callbackError = exception;
            }
        };
        Check(NativeMethods.bt_create(columns, rows, callback, IntPtr.Zero, out IntPtr value));
        _handle = new TerminalHandle(value, callback);
    }

    internal TerminalEngine(IntPtr value, SafeHandle owner) {
        _handle = new TerminalHandle(value, owner);
    }

    internal void Write(byte[] bytes, int length) {
        if (bytes is null) {
            throw new ArgumentNullException(nameof(bytes));
        }
        bool invalid = length < 0 || length > bytes.Length;
        if (invalid) {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            NativeMethods.bt_write(_handle, bytes, (UIntPtr)(uint)length);
            Check(0);
        }
    }

    internal void Resize(ushort columns, ushort rows, uint width, uint height) {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            Check(NativeMethods.bt_resize(_handle, columns, rows, width, height));
        }
    }

    internal void CompleteOutput() {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            Check(NativeMethods.bt_complete_output(_handle));
        }
    }

    internal unsafe TerminalFrame ReadFrame(bool force = false) {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            Check(NativeMethods.bt_begin_frame(_handle, out NativeFrame frame, force ? 1 : 0));
            List<TerminalRow> rows = new();
            if (frame.Dirty != 0) {
                while (true) {
                    int result = NativeMethods.bt_next_row(_handle, out ushort y, out IntPtr cells,
                        out IntPtr text, out UIntPtr length);
                    if (result == 1) {
                        break;
                    }
                    Check(result);
#if NET5_0_OR_GREATER
                    /*
                     * These blittable buffers are completely overwritten before publication.
                     */
                    NativeCell[] cellCopy = GC.AllocateUninitializedArray<NativeCell>((int)frame.Columns);
#else
                    NativeCell[] cellCopy = new NativeCell[frame.Columns];
#endif
                    fixed (NativeCell* destination = cellCopy) {
                        long count = (long)frame.Columns * sizeof(NativeCell);
                        Buffer.MemoryCopy((void*)cells, destination, count, count);
                    }
#if NET5_0_OR_GREATER
                    byte[] textCopy = GC.AllocateUninitializedArray<byte>(checked((int)length.ToUInt64()));
#else
                    byte[] textCopy = new byte[checked((int)length.ToUInt64())];
#endif
                    if (textCopy.Length != 0) {
                        Marshal.Copy(text, textCopy, 0, textCopy.Length);
                    }
                    rows.Add(new TerminalRow(y, cellCopy, textCopy));
                }
            }
            string titleCopy = ReadTitle();
            Check(NativeMethods.bt_end_frame(_handle));
            return new TerminalFrame(frame, rows, titleCopy);
        }
    }

    internal void Key(TerminalKey key, ushort modifiers, uint action, string text) {
        uint unshiftedCodepoint = DefaultUnshiftedCodepoint(key);
        ushort consumedModifiers = string.IsNullOrEmpty(text) ? (ushort)0 : (ushort)1;
        Key(key, modifiers, action, unshiftedCodepoint, consumedModifiers, text);
    }

    internal void Key(TerminalKey key, ushort modifiers, uint action, uint unshiftedCodepoint,
                      ushort consumedModifiers, string text) {
        EncodeInput(text, false, (uint)key, modifiers, action, unshiftedCodepoint, consumedModifiers);
    }

    internal void Paste(string text) {
        EncodeInput(text, true);
    }

    internal void Focus(bool focused) {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            Check(NativeMethods.bt_focus(_handle, focused ? 1 : 0));
        }
    }

    internal void Scroll(int lines, bool bottom = false) {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            NativeMethods.bt_scroll(_handle, lines, bottom ? 1 : 0);
        }
    }

    internal void ScrollToTop() {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            NativeMethods.bt_scroll(_handle, 0, -1);
        }
    }

    internal void Mouse(uint action, uint button, ushort modifiers, float x, float y, bool pressed) {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            Check(NativeMethods.bt_mouse(_handle, action, button, modifiers, x, y, pressed ? 1 : 0));
        }
    }

    internal void Select(ushort startX, ushort startY, ushort endX, ushort endY, bool clear = false) {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            Check(NativeMethods.bt_select(_handle, startX, startY, endX, endY, clear ? 1 : 0));
        }
    }

    internal void BeginSelection(ushort x, ushort y) {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            Check(NativeMethods.bt_selection_begin(_handle, x, y));
        }
    }

    internal bool UpdateSelection(ushort x, ushort y) {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            int result = NativeMethods.bt_selection_update(_handle, x, y);
            if (result == SelectionCanceled) {
                return false;
            }
            Check(result);
            return true;
        }
    }

    internal void CancelSelection() {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            NativeMethods.bt_selection_cancel(_handle);
        }
    }

    internal unsafe string SelectedText() {
        lock (_gate) {
            VerifyOpen();
            using NativeAccess access = new(_handle);
            Check(NativeMethods.bt_selection_text(_handle, out IntPtr text, out UIntPtr length));
            if (length == UIntPtr.Zero) {
                return "";
            }
            return Encoding.UTF8.GetString((byte*)text, checked((int)length.ToUInt64()));
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            _handle.Dispose();
        }
    }
}
