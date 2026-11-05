using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using Brows.Ghostty;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalModifierTests {
    private static void Write(TerminalEngine terminal, string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        terminal.Write(bytes, bytes.Length);
    }

    private static ushort CaptureModifiers() {
        MethodInfo method = typeof(Terminal).GetMethod("Modifiers",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (ushort)method.Invoke(null, null)!;
    }

    private static string ToHex(IEnumerable<byte> bytes) {
        StringBuilder result = new();
        foreach (byte value in bytes) {
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private static void AssertEncodedPair(TerminalEngine terminal, List<byte> output, KeyboardState keyboard,
        bool capsLock, bool numLock, string expectedBytes, string description) {
        keyboard.SetLocks(capsLock, numLock);
        Assert.That(Keyboard.IsKeyToggled(Key.CapsLock), Is.EqualTo(capsLock));
        Assert.That(Keyboard.IsKeyToggled(Key.NumLock), Is.EqualTo(numLock));
        ushort modifiers = CaptureModifiers();
        output.Clear();
        terminal.Key(TerminalKey.X, modifiers, 1, "x");
        terminal.Key(TerminalKey.X, modifiers, 0, "");
        string observedBytes = ToHex(output);
        Assert.That(observedBytes, Is.EqualTo(expectedBytes),
            description + ": expected bytes " + expectedBytes + "; observed bytes " + observedBytes + ".");
    }

    [Test]
    public void LockModifiers_AreEncodedAndClearWhenLocksAreOff() {
        using KeyboardState keyboard = new();
        List<byte> output = new();
        using TerminalEngine terminal = new(20, 3, bytes => output.AddRange(bytes));
        /* Match the key echo helper's Kitty flags: report event types and all keys. */
        Write(terminal, "\u001b[=10;1u");

        AssertEncodedPair(terminal, output, keyboard, false, false,
            "1b5b31323075" + "1b5b3132303b313a3375", "Caps Lock and Num Lock off");
        AssertEncodedPair(terminal, output, keyboard, true, false,
            "1b5b3132303b363575" + "1b5b3132303b36353a3375", "Caps Lock on");
        AssertEncodedPair(terminal, output, keyboard, false, true,
            "1b5b3132303b31323975" + "1b5b3132303b3132393a3375", "Num Lock on");
        AssertEncodedPair(terminal, output, keyboard, true, true,
            "1b5b3132303b31393375" + "1b5b3132303b3139333a3375", "Caps Lock and Num Lock on");
        AssertEncodedPair(terminal, output, keyboard, false, false,
            "1b5b31323075" + "1b5b3132303b313a3375", "Caps Lock and Num Lock cleared");
    }

    private sealed class KeyboardState : IDisposable {
        private readonly byte[] _saved = new byte[256];

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetKeyboardState([Out] byte[] state);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int SetKeyboardState([In] byte[] state);

        internal KeyboardState() {
            Assert.That(GetKeyboardState(_saved), Is.Not.Zero);
        }

        internal void SetLocks(bool capsLock, bool numLock) {
            /* SetKeyboardState changes only this STA thread's keyboard table. No
             * physical lock key is pressed, so hardware indicator lights stay put. */
            byte[] state = new byte[256];
            state[0x14] = capsLock ? (byte)1 : (byte)0;
            state[0x90] = numLock ? (byte)1 : (byte)0;
            Assert.That(SetKeyboardState(state), Is.Not.Zero);
        }

        public void Dispose() {
            Assert.That(SetKeyboardState(_saved), Is.Not.Zero);
        }
    }
}