using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Brows.Win32.PlatformInvoke;

internal static class WindowsKeyTranslator {
    private const int VirtualKeyCount = 256;
    private const uint MapVirtualKeyToScanCodeEx = 4;
    private const uint MapVirtualKeyToUnshiftedCharacter = 2;
    private const uint ToUnicodeNoStateChange = 4;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyLeftShift = 0xa0;
    private const int VirtualKeyRightShift = 0xa1;
    private const int VirtualKeyCapital = 0x14;
    private const int VirtualKeyNumberLock = 0x90;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyMenu = 0x12;
    private const int VirtualKeyScroll = 0x91;
    private const int VirtualKeyLeftControl = 0xa2;
    private const int VirtualKeyRightControl = 0xa3;
    private const int VirtualKeyLeftMenu = 0xa4;
    private const int VirtualKeyRightMenu = 0xa5;
    private const uint ConsoleRightAltPressed = 0x0001;
    private const uint ConsoleLeftAltPressed = 0x0002;
    private const uint ConsoleRightControlPressed = 0x0004;
    private const uint ConsoleLeftControlPressed = 0x0008;
    private const uint ConsoleShiftPressed = 0x0010;
    private const uint ConsoleNumberLockOn = 0x0020;
    private const uint ConsoleScrollLockOn = 0x0040;
    private const uint ConsoleCapsLockOn = 0x0080;
    private const uint ConsoleEnhancedKey = 0x0100;
    private static readonly ConcurrentDictionary<IntPtr, Lazy<KeyboardLayoutMap>> LayoutMaps = new();

    [DllImport("user32.dll", EntryPoint = "GetKeyboardLayout", ExactSpelling = true)]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll", EntryPoint = "GetKeyState", ExactSpelling = true)]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyExW", ExactSpelling = true)]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr keyboardLayout);

    [DllImport("user32.dll", EntryPoint = "ToUnicodeEx", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, [In] byte[] keyboardState,
                                          [Out] char[] buffer, int bufferLength, uint flags, IntPtr keyboardLayout);

    private static KeyboardLayoutMap GetLayoutMap() {
        IntPtr keyboardLayout = GetKeyboardLayout(0);
        Lazy<KeyboardLayoutMap> layoutMap = LayoutMaps.GetOrAdd(keyboardLayout,
            layout => new Lazy<KeyboardLayoutMap>(() => CreateLayoutMap(layout),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return layoutMap.Value;
    }

    private static KeyboardLayoutMap CreateLayoutMap(IntPtr keyboardLayout) {
        KeyboardLayoutMap? result = null;
        Exception? error = null;
        Thread worker = new(() => {
            try {
                result = CreateLayoutMapOnCurrentThread(keyboardLayout);
            }
            catch (Exception exception) {
                error = exception;
            }
        }) {
            IsBackground = true,
            Name = "Brows.TTY keyboard layout map"
        };
        worker.Start();
        worker.Join();
        if (error is not null) {
            throw new InvalidOperationException("Windows could not translate the active keyboard layout.", error);
        }
        return result!;
    }

    private static KeyboardLayoutMap CreateLayoutMapOnCurrentThread(IntPtr keyboardLayout) {
        uint[] unshiftedCodepoints = new uint[VirtualKeyCount];
        string?[][] physicalTexts = new string?[8][];
        for (int stateIndex = 0; stateIndex < physicalTexts.Length; stateIndex++) {
            physicalTexts[stateIndex] = new string?[VirtualKeyCount];
        }
        bool[] shiftConsumed = new bool[4 * VirtualKeyCount];
        var scanCodes = new ushort[VirtualKeyCount];
        var controlCharacters = new char[2 * VirtualKeyCount];
        byte[] keyboardState = new byte[VirtualKeyCount];
        char[] translatedText = new char[8];
        char[] unshiftedText = new char[8];

        for (int virtualKeyIndex = 0; virtualKeyIndex < VirtualKeyCount; virtualKeyIndex++) {
            uint virtualKey = (uint)virtualKeyIndex;
            uint scanCode = MapVirtualKeyEx(virtualKey, MapVirtualKeyToScanCodeEx, keyboardLayout);
            if (scanCode == 0) {
                continue;
            }
            /*
             * The navigation cluster shares scan codes with the numeric keypad,
             * so MAPVK_VK_TO_VSC_EX reports those keys without their prefix.
             */
            var keyNeedsExtendedPrefix = (scanCode & 0xff00) == 0 && KeyIsExtended(virtualKey);
            scanCodes[virtualKeyIndex] = (ushort)(keyNeedsExtendedPrefix ? scanCode | 0xe000 : scanCode);
            /*
             * Console key records carry the character Ctrl produces, such as LF
             * for Ctrl+Enter and SOH for Ctrl+A. Control characters do not
             * depend on Caps Lock or Num Lock.
             */
            Array.Clear(keyboardState, 0, keyboardState.Length);
            keyboardState[VirtualKeyControl] = 0x80;
            keyboardState[VirtualKeyLeftControl] = 0x80;
            for (var shiftIndex = 0; shiftIndex < 2; shiftIndex++) {
                SetShiftState(keyboardState, shiftIndex != 0);
                var controlCount = Translate(virtualKey, scanCode, keyboardState, translatedText, keyboardLayout);
                controlCharacters[shiftIndex * VirtualKeyCount + virtualKeyIndex] = controlCount == 1
                    ? translatedText[0]
                    : '\0';
            }

            Array.Clear(keyboardState, 0, keyboardState.Length);
            bool isLetterKey = virtualKey >= 'A' && virtualKey <= 'Z';
            uint mappedCharacter = isLetterKey
                ? 0
                : MapVirtualKeyEx(virtualKey, MapVirtualKeyToUnshiftedCharacter, keyboardLayout);
            uint codepointFromMap = mappedCharacter & 0x7fffffff;
            uint defaultCodepoint = 0;

            for (int capsLockIndex = 0; capsLockIndex < 2; capsLockIndex++) {
                bool capsLockIsOn = capsLockIndex != 0;
                for (int numLockIndex = 0; numLockIndex < 2; numLockIndex++) {
                    bool numLockIsOn = numLockIndex != 0;
                    SetToggleState(keyboardState, VirtualKeyCapital, capsLockIsOn);
                    SetToggleState(keyboardState, VirtualKeyNumberLock, numLockIsOn);
                    SetShiftState(keyboardState, false);

                    int unshiftedCount = Translate(virtualKey, scanCode, keyboardState, translatedText,
                        keyboardLayout);
                    int unshiftedTextIndex = TextStateIndex(capsLockIsOn, numLockIsOn, false);
                    physicalTexts[unshiftedTextIndex][virtualKeyIndex] = TextFromBuffer(unshiftedCount,
                        translatedText);
                    Array.Copy(translatedText, unshiftedText, translatedText.Length);
                    bool keyboardUsesDefaultLockState = !capsLockIsOn && !numLockIsOn;
                    if (keyboardUsesDefaultLockState) {
                        defaultCodepoint = FirstCodepoint(unshiftedCount, translatedText);
                    }

                    SetShiftState(keyboardState, true);
                    int shiftedCount = Translate(virtualKey, scanCode, keyboardState, translatedText,
                        keyboardLayout);
                    int shiftedTextIndex = TextStateIndex(capsLockIsOn, numLockIsOn, true);
                    physicalTexts[shiftedTextIndex][virtualKeyIndex] = TextFromBuffer(shiftedCount,
                        translatedText);

                    int lockStateIndex = LockStateIndex(capsLockIsOn, numLockIsOn);
                    shiftConsumed[lockStateIndex * VirtualKeyCount + virtualKeyIndex] =
                        !BuffersEqual(unshiftedText, unshiftedCount, translatedText, shiftedCount);
                }
            }

            unshiftedCodepoints[virtualKeyIndex] = codepointFromMap == 0
                ? defaultCodepoint
                : codepointFromMap;
        }

        return new KeyboardLayoutMap(unshiftedCodepoints, physicalTexts, shiftConsumed, scanCodes,
            controlCharacters);
    }

    /*
     * Keys that a keyboard reports with the 0xE0 extended prefix. WPF does not
     * expose the extended bit, and the keypad equivalents of the navigation
     * keys are reported as extended too.
     */
    private static bool KeyIsExtended(uint virtualKey) {
        switch (virtualKey) {
            case 0x03: // VK_CANCEL (Ctrl+Break)
            case 0x21: // VK_PRIOR
            case 0x22: // VK_NEXT
            case 0x23: // VK_END
            case 0x24: // VK_HOME
            case 0x25: // VK_LEFT
            case 0x26: // VK_UP
            case 0x27: // VK_RIGHT
            case 0x28: // VK_DOWN
            case 0x2c: // VK_SNAPSHOT
            case 0x2d: // VK_INSERT
            case 0x2e: // VK_DELETE
            case 0x5b: // VK_LWIN
            case 0x5c: // VK_RWIN
            case 0x5d: // VK_APPS
            case 0x6f: // VK_DIVIDE
            case 0x90: // VK_NUMLOCK
            case VirtualKeyRightControl:
            case VirtualKeyRightMenu:
                return true;
            default:
                return false;
        }
    }

    private static bool KeyIsDown(int virtualKey) {
        return (GetKeyState(virtualKey) & 0x8000) != 0;
    }

    private static uint SideState(int leftVirtualKey, int rightVirtualKey, uint leftState, uint rightState) {
        var leftIsDown = KeyIsDown(leftVirtualKey);
        var rightIsDown = KeyIsDown(rightVirtualKey);
        if (!leftIsDown && !rightIsDown) {
            return leftState;
        }
        return (leftIsDown ? leftState : 0) | (rightIsDown ? rightState : 0);
    }

    private static void SetShiftState(byte[] keyboardState, bool isPressed) {
        byte state = isPressed ? (byte)0x80 : (byte)0;
        keyboardState[VirtualKeyShift] = state;
        keyboardState[VirtualKeyLeftShift] = state;
        keyboardState[VirtualKeyRightShift] = state;
    }

    private static void SetToggleState(byte[] keyboardState, int virtualKey, bool isOn) {
        keyboardState[virtualKey] = isOn ? (byte)1 : (byte)0;
    }

    private static int TextStateIndex(bool capsLockIsOn, bool numLockIsOn, bool shiftIsPressed) {
        int capsLockIndex = capsLockIsOn ? 4 : 0;
        int numLockIndex = numLockIsOn ? 2 : 0;
        int shiftIndex = shiftIsPressed ? 1 : 0;
        return capsLockIndex | numLockIndex | shiftIndex;
    }

    private static int LockStateIndex(bool capsLockIsOn, bool numLockIsOn) {
        int capsLockIndex = capsLockIsOn ? 2 : 0;
        int numLockIndex = numLockIsOn ? 1 : 0;
        return capsLockIndex | numLockIndex;
    }

    private static int Translate(uint virtualKey, uint scanCode, byte[] keyboardState, char[] buffer,
                                 IntPtr keyboardLayout) {
        Array.Clear(buffer, 0, buffer.Length);
        return ToUnicodeEx(virtualKey, scanCode, keyboardState, buffer, buffer.Length,
            ToUnicodeNoStateChange, keyboardLayout);
    }

    private static string TextFromBuffer(int count, char[] buffer) {
        int characterCount = count < 0 ? (buffer[0] == '\0' ? 0 : 1) : Math.Min(count, buffer.Length);
        return characterCount == 0 ? "" : new string(buffer, 0, characterCount);
    }

    private static uint FirstCodepoint(int count, char[] buffer) {
        bool hasCharacter = count != 0 && buffer[0] != '\0';
        if (!hasCharacter) {
            return 0;
        }

        char first = buffer[0];
        bool hasSurrogatePair = char.IsHighSurrogate(first) && buffer[1] != '\0'
            && char.IsLowSurrogate(buffer[1]);
        if (hasSurrogatePair) {
            return (uint)char.ConvertToUtf32(first, buffer[1]);
        }
        if (char.IsSurrogate(first)) {
            return 0;
        }
        return first;
    }

    private static bool BuffersEqual(char[] left, int leftCount, char[] right, int rightCount) {
        int leftLength = leftCount < 0 ? (left[0] == '\0' ? 0 : 1) : Math.Min(leftCount, left.Length);
        int rightLength = rightCount < 0 ? (right[0] == '\0' ? 0 : 1) : Math.Min(rightCount, right.Length);
        if (leftLength != rightLength) {
            return false;
        }
        for (int index = 0; index < leftLength; index++) {
            if (left[index] != right[index]) {
                return false;
            }
        }
        return true;
    }

    internal static uint UnshiftedCodepoint(uint virtualKey) {
        return GetLayoutMap().UnshiftedCodepoint(virtualKey);
    }

    internal static ushort ConsumedModifiers(uint virtualKey, ushort modifiers) {
        bool shiftIsPressed = (modifiers & 1) != 0;
        if (!shiftIsPressed) {
            return 0;
        }
        bool capsLockIsOn = (modifiers & 16) != 0;
        bool numLockIsOn = (modifiers & 32) != 0;
        bool shiftIsConsumed = GetLayoutMap().ShiftIsConsumed(virtualKey, capsLockIsOn, numLockIsOn);
        return shiftIsConsumed ? (ushort)1 : (ushort)0;
    }

    internal static string? ExpectedPhysicalText(uint virtualKey, ushort modifiers) {
        bool shiftIsPressed = (modifiers & 1) != 0;
        bool capsLockIsOn = (modifiers & 16) != 0;
        bool numLockIsOn = (modifiers & 32) != 0;
        return GetLayoutMap().ExpectedText(virtualKey, shiftIsPressed, capsLockIsOn, numLockIsOn);
    }

    /*
     * Console key records identify modifier keys by their generic virtual key
     * and distinguish the right-hand keys with the enhanced-key flag.
     */
    internal static ushort ConsoleVirtualKey(uint virtualKey) {
        switch (virtualKey) {
            case VirtualKeyLeftShift:
            case VirtualKeyRightShift:
                return VirtualKeyShift;
            case VirtualKeyLeftControl:
            case VirtualKeyRightControl:
                return VirtualKeyControl;
            case VirtualKeyLeftMenu:
            case VirtualKeyRightMenu:
                return VirtualKeyMenu;
            default:
                return (ushort)virtualKey;
        }
    }

    /*
     * Returns the scan code with any 0xE0 or 0xE1 extended-key prefix in the
     * high byte, including the prefix for the navigation cluster.
     */
    internal static ushort ScanCode(uint virtualKey) {
        return GetLayoutMap().ScanCode(virtualKey);
    }

    /*
     * Returns the character a console key record carries for a key that has
     * no text event, or NUL when the key produces none.
     */
    internal static char ConsoleCharacter(uint virtualKey, ushort modifiers) {
        var shiftIsPressed = (modifiers & 1) != 0;
        var controlIsPressed = (modifiers & 2) != 0;
        var altIsPressed = (modifiers & 4) != 0;
        if (controlIsPressed && altIsPressed) {
            return '\0';
        }
        var layoutMap = GetLayoutMap();
        if (controlIsPressed) {
            return layoutMap.ControlCharacter(virtualKey, shiftIsPressed);
        }
        var capsLockIsOn = (modifiers & 16) != 0;
        var numLockIsOn = (modifiers & 32) != 0;
        var text = layoutMap.ExpectedText(virtualKey, shiftIsPressed, capsLockIsOn, numLockIsOn);
        return text is { Length: 1 } ? text[0] : '\0';
    }

    /*
     * Builds a console control key state. Ghostty modifiers do not record
     * sides, so the left and right Ctrl and Alt keys are read from the calling
     * thread's keyboard state, which matches the key message being handled.
     */
    internal static uint ControlKeyState(ushort modifiers, ushort scanCode) {
        uint state = 0;
        if ((modifiers & 1) != 0) {
            state |= ConsoleShiftPressed;
        }
        if ((modifiers & 2) != 0) {
            state |= SideState(VirtualKeyLeftControl, VirtualKeyRightControl, ConsoleLeftControlPressed,
                ConsoleRightControlPressed);
        }
        if ((modifiers & 4) != 0) {
            state |= SideState(VirtualKeyLeftMenu, VirtualKeyRightMenu, ConsoleLeftAltPressed,
                ConsoleRightAltPressed);
        }
        if ((modifiers & 16) != 0) {
            state |= ConsoleCapsLockOn;
        }
        if ((modifiers & 32) != 0) {
            state |= ConsoleNumberLockOn;
        }
        if ((GetKeyState(VirtualKeyScroll) & 1) != 0) {
            state |= ConsoleScrollLockOn;
        }
        var prefix = scanCode & 0xff00;
        var keyIsExtended = prefix == 0xe000 || prefix == 0xe100;
        if (keyIsExtended) {
            state |= ConsoleEnhancedKey;
        }
        return state;
    }

    private sealed class KeyboardLayoutMap {
        private readonly uint[] _unshiftedCodepoints;
        private readonly string?[][] _physicalTexts;
        private readonly bool[] _shiftConsumed;
        private readonly ushort[] ScanCodes;
        private readonly char[] ControlCharacters;

        internal KeyboardLayoutMap(uint[] unshiftedCodepoints, string?[][] physicalTexts,
                                   bool[] shiftConsumed, ushort[] scanCodes, char[] controlCharacters) {
            _unshiftedCodepoints = unshiftedCodepoints;
            _physicalTexts = physicalTexts;
            _shiftConsumed = shiftConsumed;
            ScanCodes = scanCodes;
            ControlCharacters = controlCharacters;
        }

        internal ushort ScanCode(uint virtualKey) {
            var keyIsInRange = virtualKey < VirtualKeyCount;
            return keyIsInRange ? ScanCodes[(int)virtualKey] : (ushort)0;
        }

        internal char ControlCharacter(uint virtualKey, bool shiftIsPressed) {
            var keyIsInRange = virtualKey < VirtualKeyCount;
            if (!keyIsInRange) {
                return '\0';
            }
            var shiftIndex = shiftIsPressed ? 1 : 0;
            return ControlCharacters[shiftIndex * VirtualKeyCount + (int)virtualKey];
        }

        internal uint UnshiftedCodepoint(uint virtualKey) {
            bool keyIsInRange = virtualKey < VirtualKeyCount;
            return keyIsInRange ? _unshiftedCodepoints[(int)virtualKey] : 0;
        }

        internal bool ShiftIsConsumed(uint virtualKey, bool capsLockIsOn, bool numLockIsOn) {
            bool keyIsInRange = virtualKey < VirtualKeyCount;
            if (!keyIsInRange) {
                return false;
            }
            int lockStateIndex = LockStateIndex(capsLockIsOn, numLockIsOn);
            int shiftConsumedIndex = lockStateIndex * VirtualKeyCount + (int)virtualKey;
            return _shiftConsumed[shiftConsumedIndex];
        }

        internal string? ExpectedText(uint virtualKey, bool shiftIsPressed, bool capsLockIsOn,
                                      bool numLockIsOn) {
            bool keyIsInRange = virtualKey < VirtualKeyCount;
            if (!keyIsInRange) {
                return null;
            }
            int stateIndex = TextStateIndex(capsLockIsOn, numLockIsOn, shiftIsPressed);
            return _physicalTexts[stateIndex][(int)virtualKey];
        }
    }
}
