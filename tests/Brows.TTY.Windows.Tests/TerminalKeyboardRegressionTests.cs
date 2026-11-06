using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Brows.Ghostty;
using Brows.Win32;
using Brows.Win32.PlatformInvoke;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalKeyboardRegressionTests {
    private const uint VirtualKeyE = 0x45;
    private const uint KeyboardLayoutActivate = 1;
    private const uint MapVirtualKeyToScanCodeEx = 4;

    private SynchronizationContext? _previousContext;

    private sealed class KeyboardState : IDisposable {
        private readonly byte[] _saved = new byte[256];

        [DllImport("user32.dll", EntryPoint = "GetKeyboardState", ExactSpelling = true)]
        private static extern int GetKeyboardState([Out] byte[] state);

        [DllImport("user32.dll", EntryPoint = "SetKeyboardState", ExactSpelling = true)]
        private static extern int SetKeyboardState([In] byte[] state);

        internal KeyboardState() {
            Assert.That(GetKeyboardState(_saved), Is.Not.Zero);
        }

        internal void SetModifiers(ModifierKeys modifiers, bool capsLock = false, bool numLock = false) {
            byte[] state = new byte[256];
            if ((modifiers & ModifierKeys.Control) != 0) {
                state[0x11] = state[0xa2] = 0x80;
            }
            if ((modifiers & ModifierKeys.Alt) != 0) {
                state[0x12] = state[0xa5] = 0x80;
            }
            if ((modifiers & ModifierKeys.Shift) != 0) {
                state[0x10] = state[0xa0] = 0x80;
            }
            state[0x14] = capsLock ? (byte)1 : (byte)0;
            state[0x90] = numLock ? (byte)1 : (byte)0;
            Assert.That(SetKeyboardState(state), Is.Not.Zero);
        }

        public void Dispose() {
            Assert.That(SetKeyboardState(_saved), Is.Not.Zero);
        }
    }

    private sealed class ActiveKeyboardLayout : IDisposable {
        private readonly IntPtr _previous;

        [DllImport("user32.dll", EntryPoint = "GetKeyboardLayout", ExactSpelling = true)]
        private static extern IntPtr GetKeyboardLayout(uint threadId);

        [DllImport("user32.dll", EntryPoint = "LoadKeyboardLayoutW", ExactSpelling = true,
            CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadKeyboardLayout(string layoutId, uint flags);

        [DllImport("user32.dll", EntryPoint = "ActivateKeyboardLayout", ExactSpelling = true)]
        private static extern IntPtr ActivateKeyboardLayout(IntPtr keyboardLayout, uint flags);

        private ActiveKeyboardLayout(IntPtr previous) {
            _previous = previous;
        }

        internal static ActiveKeyboardLayout? TryActivate(string layoutId) {
            IntPtr previous = GetKeyboardLayout(0);
            IntPtr layout = LoadKeyboardLayout(layoutId, KeyboardLayoutActivate);
            if (layout == IntPtr.Zero) {
                return null;
            }
            ActivateKeyboardLayout(layout, 0);
            bool requestedLayoutIsActive = GetKeyboardLayout(0) == layout;
            if (!requestedLayoutIsActive) {
                ActivateKeyboardLayout(previous, 0);
                return null;
            }
            return new ActiveKeyboardLayout(previous);
        }

        public void Dispose() {
            ActivateKeyboardLayout(_previous, 0);
        }
    }

    private sealed class PreeditComposition : TextComposition {
        internal PreeditComposition(TerminalEmulator terminal, string resultText, string preeditText)
            : base(InputManager.Current, terminal, resultText) {
            base.CompositionText = preeditText;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetKeyboardLayout", ExactSpelling = true)]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyExW", ExactSpelling = true)]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr keyboardLayout);

    [DllImport("user32.dll", EntryPoint = "VkKeyScanExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanEx(char character, IntPtr keyboardLayout);

    [DllImport("user32.dll", EntryPoint = "ToUnicodeEx", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, [In] byte[] keyboardState,
                                          [Out] char[] buffer, int bufferLength, uint flags, IntPtr keyboardLayout);

    private static void Pump(Task task) {
        DispatcherFrame frame = new();
        DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(15) };
        timeout.Tick += (sender, args) => frame.Continue = false;
        timeout.Start();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(completed => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            TaskScheduler.Default);
        if (!task.IsCompleted) {
            Dispatcher.PushFrame(frame);
        }
        timeout.Stop();
        Assert.That(task.IsCompleted, Is.True, "Dispatcher operation exceeded 15 seconds.");
        task.GetAwaiter().GetResult();
    }

    private static HwndSource Host(TerminalEmulator terminal) {
        HwndSourceParameters parameters = new("Terminal keyboard regression test") {
            WindowStyle = unchecked((int)0x80000000),
            Width = 800,
            Height = 400
        };
        terminal.Width = 800;
        terminal.Height = 400;
        HwndSource source = new(parameters) { RootVisual = terminal };
        terminal.Measure(new Size(800, 400));
        terminal.Arrange(new Rect(0, 0, 800, 400));
        terminal.UpdateLayout();
        return source;
    }

    private static void WaitFor(Func<bool> condition) {
        TaskCompletionSource<bool> completion = new();
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (sender, args) => {
            if (condition()) {
                completion.TrySetResult(true);
            }
        };
        timer.Start();
        try {
            Pump(completion.Task);
        }
        finally {
            timer.Stop();
        }
    }

    private static T Field<T>(TerminalEmulator terminal, string name) {
        return (T)typeof(TerminalEmulator).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
    }

    private static string KeyEchoCommand() {
        string architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "AMD64";
        string rid = architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "win-arm64" : "win-x64";
        return "\"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "helpers", rid, "key-echo.exe") + "\"";
    }

    private static string ReadText(TerminalSession session) {
        TerminalFrame frame = session.Engine.ReadFrame(true);
        return string.Join("\n", frame.ChangedRows.Select(row =>
            string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText))));
    }

    private static string ReadKeyBytes(TerminalSession session) {
        string output = string.Concat(ReadText(session).Split('\n').Select(line => line.TrimEnd()));
        MatchCollection matches = Regex.Matches(output, "key-echo:([0-9a-f]+)");
        return string.Concat(matches.Cast<Match>().Select(match => match.Groups[1].Value));
    }

    private static TerminalSession Start(TerminalEmulator terminal) {
        Pump(terminal.StartAsync());
        TerminalSession session = Field<TerminalSession>(terminal, "_session");
        WaitFor(() => ReadText(session).Contains("key-echo-ready"));
        return session;
    }

    private static KeyEventArgs RaiseKey(TerminalEmulator terminal, HwndSource source, Key key,
                                         RoutedEvent routedEvent, bool isRepeat = false) {
        KeyEventArgs args = new(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
            RoutedEvent = routedEvent
        };
        if (isRepeat) {
            SetRepeat(args);
        }
        terminal.RaiseEvent(args);
        return args;
    }

    private static void SetRepeat(KeyEventArgs args) {
        MethodInfo? setRepeat = typeof(KeyEventArgs).GetMethod("SetRepeat",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (setRepeat is not null) {
            setRepeat.Invoke(args, new object[] { true });
        }
        else {
            FieldInfo? field = typeof(KeyEventArgs).GetField("_isRepeat",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "WPF KeyEventArgs does not expose a repeat setter or backing field.");
            field!.SetValue(args, true);
        }
        Assert.That(args.IsRepeat, Is.True);
    }

    private static TextCompositionEventArgs RaiseTextStart(TerminalEmulator terminal, TextComposition composition) {
        TextCompositionEventArgs args = new(Keyboard.PrimaryDevice, composition) {
            RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent
        };
        terminal.RaiseEvent(args);
        return args;
    }

    private static TextCompositionEventArgs RaiseText(TerminalEmulator terminal, TextComposition composition) {
        TextCompositionEventArgs args = new(Keyboard.PrimaryDevice, composition) {
            RoutedEvent = TextCompositionManager.TextInputEvent
        };
        terminal.RaiseEvent(args);
        return args;
    }

    private static TextCompositionEventArgs RaiseText(TerminalEmulator terminal, string text) {
        return RaiseText(terminal, new TextComposition(InputManager.Current, terminal, text));
    }

    private static FrameworkTextComposition FrameworkComposition(TerminalEmulator terminal, string text) {
        Type[] parameterTypes = { typeof(InputManager), typeof(IInputElement), typeof(object) };
        ConstructorInfo? constructor = typeof(FrameworkTextComposition).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, parameterTypes, null);
        Assert.That(constructor, Is.Not.Null,
            "WPF must expose its internal IME composition constructor to reflection.");
        FrameworkTextComposition composition = (FrameworkTextComposition)constructor!.Invoke(
            new object[] { InputManager.Current, terminal, terminal });
        PropertyInfo? textProperty = typeof(TextComposition).GetProperty("Text",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? setText = textProperty?.GetSetMethod(true);
        Assert.That(setText, Is.Not.Null, "WPF must expose its IME composition text setter to reflection.");
        setText!.Invoke(composition, new object[] { text });
        return composition;
    }

    private static void SendMarker(TerminalSession session) {
        session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
        WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
    }

    private static void Write(TerminalSession session, string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        session.Engine.Write(bytes, bytes.Length);
    }

    private static void Write(TerminalEngine terminal, string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        terminal.Write(bytes, bytes.Length);
    }

    private static uint VirtualKeyForUnshiftedCharacter(char character, IntPtr keyboardLayout) {
        short keyAndModifiers = VkKeyScanEx(character, keyboardLayout);
        Assert.That(keyAndModifiers, Is.Not.EqualTo((short)-1),
            "The active test layout must map the expected character.");
        ushort keyData = unchecked((ushort)keyAndModifiers);
        byte modifiers = (byte)(keyData >> 8);
        Assert.That(modifiers, Is.Zero, "The expected test character must use no modifiers.");
        return (uint)(keyData & 0xff);
    }

    private static string Hex(IEnumerable<byte> bytes) {
        StringBuilder result = new();
        foreach (byte value in bytes) {
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private static void RaisePrintable(TerminalEmulator terminal, HwndSource source, Key key, string text) {
        RaiseKey(terminal, source, key, Keyboard.PreviewKeyDownEvent);
        RaiseText(terminal, text);
        RaiseKey(terminal, source, key, Keyboard.PreviewKeyUpEvent);
    }

    [SetUp]
    public void SetUp() {
        _previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
    }

    [TearDown]
    public void TearDown() {
        SynchronizationContext.SetSynchronizationContext(_previousContext);
    }

    [Test]
    public void PrintableTextInput_UsesRepeatActionForRepeatKeyDown() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            keyboard.SetModifiers(ModifierKeys.None);

            RaiseKey(terminal, source, Key.X, Keyboard.PreviewKeyDownEvent);
            RaiseText(terminal, "x");
            RaiseKey(terminal, source, Key.X, Keyboard.PreviewKeyDownEvent, true);
            RaiseText(terminal, "x");
            RaiseKey(terminal, source, Key.X, Keyboard.PreviewKeyUpEvent);
            SendMarker(session);

            Assert.That(ReadKeyBytes(session), Is.EqualTo(
                "1b5b31323075" + "1b5b3132303b313a3275" + "1b5b3132303b313a3375" + "21"));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void PlainTextCompositionStart_PreservesPhysicalKeyIdentity() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            keyboard.SetModifiers(ModifierKeys.None);
            KeyEventArgs press = RaiseKey(terminal, source, Key.X, Keyboard.PreviewKeyDownEvent);
            TextComposition composition = new(InputManager.Current, terminal, "x");
            RaiseTextStart(terminal, composition);
            TextCompositionEventArgs text = RaiseText(terminal, composition);
            KeyEventArgs release = RaiseKey(terminal, source, Key.X, Keyboard.PreviewKeyUpEvent);
            SendMarker(session);

            Assert.That(text.Handled, Is.True);
            Assert.That(press.Handled, Is.False);
            Assert.That(release.Handled, Is.False);
            Assert.That(ReadKeyBytes(session), Is.EqualTo(
                "1b5b31323075" + "1b5b3132303b313a3375" + "21"));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void CommittedTextWithoutPreedit_MismatchingPhysicalKeyUsesTextOnly() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            keyboard.SetModifiers(ModifierKeys.None);
            RaiseKey(terminal, source, Key.E, Keyboard.PreviewKeyDownEvent);
            TextComposition composition = new(InputManager.Current, terminal, "é");
            RaiseTextStart(terminal, composition);
            TextCompositionEventArgs text = RaiseText(terminal, composition);
            KeyEventArgs release = RaiseKey(terminal, source, Key.E, Keyboard.PreviewKeyUpEvent);
            SendMarker(session);

            Assert.That(text.Handled, Is.True);
            Assert.That(release.Handled, Is.False);
            Assert.That(ReadKeyBytes(session), Is.EqualTo("c3a9" + "21"),
                "A committed accent must not be replaced by the preceding physical E identity or its release.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void CommittedPreeditText_UsesTextOnlyAndDoesNotReleasePhysicalKey() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            keyboard.SetModifiers(ModifierKeys.None);
            RaiseKey(terminal, source, Key.E, Keyboard.PreviewKeyDownEvent);
            TextComposition composition = new PreeditComposition(terminal, "é", "´");
            RaiseTextStart(terminal, composition);
            TextCompositionEventArgs text = RaiseText(terminal, composition);
            RaiseKey(terminal, source, Key.E, Keyboard.PreviewKeyUpEvent);
            SendMarker(session);

            Assert.That(text.Handled, Is.True);
            Assert.That(ReadKeyBytes(session), Is.EqualTo("c3a9" + "21"));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void FrameworkTextComposition_UsesCommittedTextInsteadOfPhysicalIdentity() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            keyboard.SetModifiers(ModifierKeys.None);
            RaiseKey(terminal, source, Key.X, Keyboard.PreviewKeyDownEvent);
            FrameworkTextComposition composition = FrameworkComposition(terminal, "x");
            RaiseTextStart(terminal, composition);
            TextCompositionEventArgs text = RaiseText(terminal, composition);
            RaiseKey(terminal, source, Key.X, Keyboard.PreviewKeyUpEvent);
            SendMarker(session);

            Assert.That(text.Handled, Is.True);
            Assert.That(ReadKeyBytes(session), Is.EqualTo("78" + "21"),
                "A WPF IME composition stays committed text even if it matches a physical key's usual character.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void PrintableKeys_KeepKittyIdentityAndMatchingReleases() {
        using ActiveKeyboardLayout? layout = ActiveKeyboardLayout.TryActivate("00000409");
        if (layout is null) {
            Assert.Ignore("The Windows US keyboard layout is not installed.");
        }
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            keyboard.SetModifiers(ModifierKeys.None, numLock: false);
            RaisePrintable(terminal, source, Key.D1, "1");
            RaisePrintable(terminal, source, Key.OemMinus, "-");
            RaisePrintable(terminal, source, Key.Space, " ");
            keyboard.SetModifiers(ModifierKeys.None, numLock: true);
            RaisePrintable(terminal, source, Key.NumPad1, "1");
            SendMarker(session);

            Assert.That(ReadKeyBytes(session), Is.EqualTo(
                "1b5b343975" + "1b5b34393b313a3375"
                + "1b5b343575" + "1b5b34353b313a3375"
                + "1b5b333275" + "1b5b33323b313a3375"
                + "1b5b35373430303b31323975" + "1b5b35373430303b3132393a3375"
                + "21"));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void PrintableKeys_KeepLegacyTextEncoding() {
        List<byte> output = new();
        using TerminalEngine terminal = new(20, 3, bytes => output.AddRange(bytes));

        terminal.Key(TerminalKey.Digit1, 0, 1, "1");
        terminal.Key(TerminalKey.Minus, 0, 1, "-");
        terminal.Key(TerminalKey.Space, 0, 1, " ");

        Assert.That(Encoding.UTF8.GetString(output.ToArray()), Is.EqualTo("1- "));
    }

    [Test]
    public void CoreDefaultKeys_KeepKittyIdentityAndNumpadBaseline() {
        List<byte> output = new();
        using TerminalEngine terminal = new(20, 3, bytes => output.AddRange(bytes));
        Write(terminal, "\u001b[=10;1u");

        terminal.Key(TerminalKey.Digit1, 0, 1, "1");
        terminal.Key(TerminalKey.Digit1, 0, 0, "");
        terminal.Key(TerminalKey.Minus, 0, 1, "-");
        terminal.Key(TerminalKey.Minus, 0, 0, "");
        terminal.Key(TerminalKey.Space, 0, 1, " ");
        terminal.Key(TerminalKey.Space, 0, 0, "");
        terminal.Key(TerminalKey.Numpad1, 0, 1, "1");
        terminal.Key(TerminalKey.Numpad1, 0, 0, "");

        Assert.That(Hex(output), Is.EqualTo(
            "1b5b343975" + "1b5b34393b313a3375"
            + "1b5b343575" + "1b5b34353b313a3375"
            + "1b5b333275" + "1b5b33323b313a3375"
            + "1b5b353734303075" + "1b5b35373430303b313a3375"));
    }

    [Test]
    public void PrintableKey_EncodesAlternateAndAssociatedText() {
        List<byte> output = new();
        using TerminalEngine terminal = new(20, 3, bytes => output.AddRange(bytes));
        Write(terminal, "\u001b[=14;1u");
        terminal.Key(TerminalKey.Digit1, 1, 1, "!");
        terminal.Key(TerminalKey.Digit1, 1, 0, "");
        Assert.That(Hex(output), Is.EqualTo("1b5b34393a33333b3275" + "1b5b34393b323a3375"));

        output.Clear();
        Write(terminal, "\u001b[=26;1u");
        terminal.Key(TerminalKey.Digit1, 1, 1, "!");
        terminal.Key(TerminalKey.Digit1, 1, 0, "");
        Assert.That(Hex(output), Is.EqualTo("1b5b34393b323b333375" + "1b5b34393b323a3375"));
    }

    [Test]
    public void NonUsLayout_MapsUnshiftedPrintableCodepoint() {
        using ActiveKeyboardLayout? layout = ActiveKeyboardLayout.TryActivate("00000407");
        if (layout is null) {
            Assert.Ignore("The Windows German keyboard layout is not installed.");
        }
        IntPtr keyboardLayout = GetKeyboardLayout(0);
        uint virtualKey = VirtualKeyForUnshiftedCharacter('\u00df', keyboardLayout);
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            keyboard.SetModifiers(ModifierKeys.None);
            Key key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
            RaisePrintable(terminal, source, key, "\u00df");
            SendMarker(session);

            Assert.That(ReadKeyBytes(session), Is.EqualTo(
                "1b5b32323375" + "1b5b3232333b313a3375" + "21"),
                "The German unshifted sharp-s character has Kitty identity 223.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void DelayedText_UsesKeyboardLayoutCapturedAtPress() {
        using ActiveKeyboardLayout? germanLayout = ActiveKeyboardLayout.TryActivate("00000407");
        if (germanLayout is null) {
            Assert.Ignore("The Windows German keyboard layout is not installed.");
        }
        IntPtr germanKeyboardLayout = GetKeyboardLayout(0);
        uint virtualKey = VirtualKeyForUnshiftedCharacter('\u00df', germanKeyboardLayout);
        Key key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            keyboard.SetModifiers(ModifierKeys.None);
            RaiseKey(terminal, source, key, Keyboard.PreviewKeyDownEvent);
            using ActiveKeyboardLayout? usLayout = ActiveKeyboardLayout.TryActivate("00000409");
            if (usLayout is null) {
                Assert.Ignore("The Windows US keyboard layout is not installed.");
            }
            TextCompositionEventArgs delayedText = RaiseText(terminal, "\u00df");
            Assert.That(delayedText.Handled, Is.True);
            RaiseKey(terminal, source, key, Keyboard.PreviewKeyUpEvent);
            SendMarker(session);

            Assert.That(ReadKeyBytes(session), Is.EqualTo(
                "1b5b32323375" + "1b5b3232333b313a3375" + "21"),
                "A delayed text commit keeps the expected text and key identity captured with its press.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void LayoutTranslation_DoesNotConsumePendingDeadKeyState() {
        using ActiveKeyboardLayout? layout = ActiveKeyboardLayout.TryActivate("00000407");
        if (layout is null) {
            Assert.Ignore("The Windows German keyboard layout is not installed.");
        }
        using KeyboardState keyboard = new();
        keyboard.SetModifiers(ModifierKeys.None);
        IntPtr keyboardLayout = GetKeyboardLayout(0);
        uint deadKey = VirtualKeyForUnshiftedCharacter('\u00b4', keyboardLayout);
        uint deadKeyScanCode = MapVirtualKeyEx(deadKey, MapVirtualKeyToScanCodeEx, keyboardLayout);
        uint letterScanCode = MapVirtualKeyEx(VirtualKeyE, MapVirtualKeyToScanCodeEx, keyboardLayout);
        byte[] keyboardState = new byte[256];
        char[] buffer = new char[8];
        bool deadKeyStateIsPending = false;
        try {
            int deadKeyResult = ToUnicodeEx(deadKey, deadKeyScanCode, keyboardState, buffer,
                buffer.Length, 0, keyboardLayout);
            deadKeyStateIsPending = deadKeyResult < 0;
            Assert.That(deadKeyResult, Is.LessThan(0), "The German acute accent character must be a dead key.");

            Assert.That(WindowsKeyTranslator.UnshiftedCodepoint(VirtualKeyE), Is.EqualTo((uint)'e'));
            int committedResult = ToUnicodeEx(VirtualKeyE, letterScanCode, keyboardState, buffer,
                buffer.Length, 0, keyboardLayout);
            deadKeyStateIsPending = false;
            Assert.That(committedResult, Is.GreaterThan(0));
            Assert.That(new string(buffer, 0, committedResult), Is.EqualTo("\u00e9"),
                "The translator must leave the active dead-key state available for WPF text composition.");
        }
        finally {
            if (deadKeyStateIsPending) {
                uint spaceScanCode = MapVirtualKeyEx(0x20, MapVirtualKeyToScanCodeEx, keyboardLayout);
                ToUnicodeEx(0x20, spaceScanCode, keyboardState, buffer, buffer.Length, 0, keyboardLayout);
            }
        }
    }

    [Test]
    public void RussianLayout_MapsLetterIdentityThroughUnicodeTranslation() {
        using ActiveKeyboardLayout? layout = ActiveKeyboardLayout.TryActivate("00000419");
        if (layout is null) {
            Assert.Ignore("The Windows Russian keyboard layout is not available.");
        }
        IntPtr keyboardLayout = GetKeyboardLayout(0);
        uint virtualKey = VirtualKeyForUnshiftedCharacter('\u0444', keyboardLayout);
        Assert.That(virtualKey, Is.EqualTo((uint)'A'), "Russian ф is typed on the A virtual key.");

        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            keyboard.SetModifiers(ModifierKeys.None);
            Key key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
            RaisePrintable(terminal, source, key, "\u0444");
            SendMarker(session);

            Assert.That(ReadKeyBytes(session), Is.EqualTo(
                "1b5b3130393275" + "1b5b313039323b313a3375" + "21"),
                "The Russian unshifted letter has Kitty identity 1092 despite VK_A being "
                + "forced to ASCII by MapVirtualKey.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void CapsAndShift_UseLockStateWhenFindingConsumedModifiers() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            Write(session, "\u001b[=3;1u");
            keyboard.SetModifiers(ModifierKeys.Shift, capsLock: true);
            RaiseKey(terminal, source, Key.A, Keyboard.PreviewKeyDownEvent);
            RaiseText(terminal, "a");
            RaiseKey(terminal, source, Key.A, Keyboard.PreviewKeyUpEvent);
            SendMarker(session);

            Assert.That(ReadKeyBytes(session), Is.EqualTo("61" + "1b5b39373b36363a3375" + "21"),
                "Caps+Shift changes the base uppercase result, so Shift is consumed "
                + "for the generated lowercase text.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void DelayedLocalPasteText_IsSuppressedAfterModifierChangeAndLaterPressIsForwarded() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            Write(session, "\u001b[?2004h");
            Clipboard.SetText("paste-action-only");
            keyboard.SetModifiers(ModifierKeys.Control);
            KeyEventArgs localPress = RaiseKey(terminal, source, Key.V, Keyboard.PreviewKeyDownEvent);
            keyboard.SetModifiers(ModifierKeys.None);
            TextCompositionEventArgs delayedText = RaiseText(terminal, "v");
            KeyEventArgs localRelease = RaiseKey(terminal, source, Key.V, Keyboard.PreviewKeyUpEvent);

            KeyEventArgs forwardedPress = RaiseKey(terminal, source, Key.V, Keyboard.PreviewKeyDownEvent);
            TextCompositionEventArgs forwardedText = RaiseText(terminal, "v");
            KeyEventArgs forwardedRelease = RaiseKey(terminal, source, Key.V, Keyboard.PreviewKeyUpEvent);
            SendMarker(session);

            Assert.That(localPress.Handled, Is.True);
            Assert.That(delayedText.Handled, Is.True);
            Assert.That(localRelease.Handled, Is.True);
            Assert.That(forwardedPress.Handled, Is.False);
            Assert.That(forwardedText.Handled, Is.True);
            Assert.That(forwardedRelease.Handled, Is.False);
            Assert.That(ReadKeyBytes(session), Is.EqualTo(
                "1b5b3230307e70617374652d616374696f6e2d6f6e6c791b5b3230317e"
                + "1b5b31313875" + "1b5b3131383b313a3375" + "21"));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void DelayedEnterNewline_AfterModifierChangeStaysLocal() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            Write(session, "\u001b[2J\u001b[Hcopy");
            session.Engine.Select(0, 0, 3, 0);
            keyboard.SetModifiers(ModifierKeys.None);
            KeyEventArgs localPress = RaiseKey(terminal, source, Key.Enter, Keyboard.PreviewKeyDownEvent);
            keyboard.SetModifiers(ModifierKeys.Control);
            TextCompositionEventArgs delayedNewline = RaiseText(terminal, "\r\n");
            KeyEventArgs localRelease = RaiseKey(terminal, source, Key.Enter, Keyboard.PreviewKeyUpEvent);
            SendMarker(session);

            Assert.That(localPress.Handled, Is.True);
            Assert.That(delayedNewline.Handled, Is.True);
            Assert.That(localRelease.Handled, Is.True);
            Assert.That(Clipboard.GetText(), Is.EqualTo("copy"));
            Assert.That(ReadKeyBytes(session), Is.EqualTo("21"),
                "Newline text from the consumed Enter press must not reach the shell after modifiers change.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

}
