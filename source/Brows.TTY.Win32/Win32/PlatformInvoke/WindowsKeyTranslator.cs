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
    private static readonly ConcurrentDictionary<IntPtr, Lazy<KeyboardLayoutMap>> LayoutMaps = new();

    [DllImport("user32.dll", EntryPoint = "GetKeyboardLayout", ExactSpelling = true)]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

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
        byte[] keyboardState = new byte[VirtualKeyCount];
        char[] translatedText = new char[8];
        char[] unshiftedText = new char[8];

        for (int virtualKeyIndex = 0; virtualKeyIndex < VirtualKeyCount; virtualKeyIndex++) {
            uint virtualKey = (uint)virtualKeyIndex;
            uint scanCode = MapVirtualKeyEx(virtualKey, MapVirtualKeyToScanCodeEx, keyboardLayout);
            if (scanCode == 0) {
                continue;
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

        return new KeyboardLayoutMap(unshiftedCodepoints, physicalTexts, shiftConsumed);
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

    private sealed class KeyboardLayoutMap {
        private readonly uint[] _unshiftedCodepoints;
        private readonly string?[][] _physicalTexts;
        private readonly bool[] _shiftConsumed;

        internal KeyboardLayoutMap(uint[] unshiftedCodepoints, string?[][] physicalTexts,
                                   bool[] shiftConsumed) {
            _unshiftedCodepoints = unshiftedCodepoints;
            _physicalTexts = physicalTexts;
            _shiftConsumed = shiftConsumed;
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
