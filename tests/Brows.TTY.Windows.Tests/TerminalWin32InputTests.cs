using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Brows.Ghostty;
using Brows.Win32;
using Brows.Win32.PlatformInvoke;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

/*
 * ConPTY requests win32-input-mode and translates terminal input into console
 * key records. These tests run a classic console reader through real ConPTY
 * and verify the records it receives for keys from the WPF control.
 */
[TestFixture, Apartment(ApartmentState.STA)]
internal sealed class TerminalWin32InputTests {
    private const string RecordPrefix = "key-record:";
    private const string MarkerText = "z";
    private const string MarkerCharacter = "007a";

    private SynchronizationContext? PreviousContext;

    private static void Pump(Task task) {
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timeout.Tick += (sender, args) => frame.Continue = false;
        timeout.Start();
        var dispatcher = Dispatcher.CurrentDispatcher;
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
        var parameters = new HwndSourceParameters("Terminal win32 input test") {
            WindowStyle = unchecked((int)0x80000000),
            Width = 800,
            Height = 400
        };
        terminal.Width = 800;
        terminal.Height = 400;
        var source = new HwndSource(parameters) { RootVisual = terminal };
        terminal.Measure(new Size(800, 400));
        terminal.Arrange(new Rect(0, 0, 800, 400));
        terminal.UpdateLayout();
        return source;
    }

    private static void WaitFor(Func<bool> condition) {
        var completion = new TaskCompletionSource<bool>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
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

    private static string ReadText(TerminalSession session) {
        var frame = session.Engine.ReadFrame(true);
        return string.Join("\n", frame.ChangedRows.Select(row =>
            string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText))));
    }

    private static string HelperCommand(string name) {
        var architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "AMD64";
        var rid = architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "win-arm64" : "win-x64";
        return "\"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "helpers", rid, name) + "\"";
    }

    private static List<string> ReadLines(TerminalSession session, string prefix) {
        return ReadText(session).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line.Substring(prefix.Length))
            .ToList();
    }

    /*
     * Sends text that bypasses key identity as an end marker, waits for its
     * record, and returns the records the child received before it. The marker
     * is unshifted so ConPTY does not synthesize a Shift record before it.
     */
    private static List<string> ReadRecordsBeforeMarker(TerminalSession session) {
        session.Engine.Key(TerminalKey.Unidentified, 0, 1, MarkerText);
        WaitFor(() => ReadLines(session, RecordPrefix).Any(IsMarker));
        var records = ReadLines(session, RecordPrefix);
        return records.Take(records.FindIndex(IsMarker)).ToList();
    }

    private static bool IsMarker(string record) {
        return record.Split(',')[2] == MarkerCharacter;
    }

    private static string Record(bool keyDown, int virtualKey, int character, int controlKeyState) {
        return string.Format(CultureInfo.InvariantCulture, "{0},{1:x2},{2:x4},{3:x4}",
            keyDown ? 1 : 0, virtualKey, character, controlKeyState);
    }

    private static T Field<T>(TerminalEmulator terminal, string name) {
        return (T)typeof(TerminalEmulator).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
    }

    private static TerminalSession Start(TerminalEmulator terminal, string readyText) {
        Pump(terminal.StartAsync());
        var session = Field<TerminalSession>(terminal, "_session");
        WaitFor(() => ReadText(session).Contains(readyText));
        return session;
    }

    private static KeyEventArgs RaiseKey(TerminalEmulator terminal, HwndSource source, Key key,
                                         RoutedEvent routedEvent) {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
            RoutedEvent = routedEvent
        };
        terminal.RaiseEvent(args);
        return args;
    }

    private static void PressAndRelease(TerminalEmulator terminal, HwndSource source, Key key) {
        RaiseKey(terminal, source, key, Keyboard.PreviewKeyDownEvent);
        RaiseKey(terminal, source, key, Keyboard.PreviewKeyUpEvent);
    }

    private static TextCompositionEventArgs RaiseText(TerminalEmulator terminal, string text) {
        var composition = new TextComposition(InputManager.Current, terminal, text);
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
            RoutedEvent = TextCompositionManager.TextInputEvent
        };
        terminal.RaiseEvent(args);
        return args;
    }

    /*
     * Runs the key records helper in the control with the given modifiers held
     * on this thread, and returns the records produced by the action.
     */
    private static List<string> CaptureRecords(ModifierKeys modifiers,
                                               Action<TerminalEmulator, HwndSource> action) {
        var terminal = new TerminalEmulator { AutoStart = false, CommandLine = HelperCommand("key-records.exe") };
        using var source = Host(terminal);
        using var keyboard = new KeyboardState();
        try {
            var session = Start(terminal, "key-records-ready");
            keyboard.SetModifiers(modifiers);
            action(terminal, source);
            keyboard.SetModifiers(ModifierKeys.None);
            var records = ReadRecordsBeforeMarker(session);
            Assert.That(terminal.LastError, Is.Null);
            return records;
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [SetUp]
    public void SetUp() {
        PreviousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
    }

    [TearDown]
    public void TearDown() {
        SynchronizationContext.SetSynchronizationContext(PreviousContext);
    }

    [Test]
    public void ShiftEnter_ReachesConsoleApplicationWithShift() {
        var records = CaptureRecords(ModifierKeys.Shift,
            (terminal, source) => PressAndRelease(terminal, source, Key.Enter));

        Assert.That(records, Is.EqualTo(new[] {
            Record(true, 0x0d, '\r', 0x0010),
            Record(false, 0x0d, '\r', 0x0010)
        }), "Shift+Enter must reach a console application as Enter with SHIFT_PRESSED.");
    }

    [Test]
    public void ControlEnter_ReachesConsoleApplicationAsLineFeedWithLeftControl() {
        var records = CaptureRecords(ModifierKeys.Control,
            (terminal, source) => PressAndRelease(terminal, source, Key.Enter));

        Assert.That(records, Is.EqualTo(new[] {
            Record(true, 0x0d, '\n', 0x0008),
            Record(false, 0x0d, '\n', 0x0008)
        }));
    }

    [Test]
    public void UpArrow_ReachesConsoleApplicationAsEnhancedKey() {
        var records = CaptureRecords(ModifierKeys.None,
            (terminal, source) => PressAndRelease(terminal, source, Key.Up));

        Assert.That(records, Is.EqualTo(new[] {
            Record(true, 0x26, 0, 0x0100),
            Record(false, 0x26, 0, 0x0100)
        }));
    }

    [Test]
    public void PhysicalText_ReachesConsoleApplicationWithKeyIdentity() {
        var text = WindowsKeyTranslator.ExpectedPhysicalText(0x41, 0);
        Assume.That(text, Has.Length.EqualTo(1), "The active keyboard layout must map VK_A to one character.");

        var records = CaptureRecords(ModifierKeys.None, (terminal, source) => {
            RaiseKey(terminal, source, Key.A, Keyboard.PreviewKeyDownEvent);
            RaiseText(terminal, text!);
            RaiseKey(terminal, source, Key.A, Keyboard.PreviewKeyUpEvent);
        });

        Assert.That(records, Is.EqualTo(new[] {
            Record(true, 0x41, text![0], 0),
            Record(false, 0x41, text[0], 0)
        }));
    }

    [Test]
    public void ComposedText_ReachesConsoleApplicationAsCharacter() {
        var records = CaptureRecords(ModifierKeys.None, (terminal, source) => RaiseText(terminal, "é"));

        Assert.That(records, Is.EqualTo(new[] {
            Record(true, 0, 0x00e9, 0),
            Record(false, 0, 0x00e9, 0)
        }), "Text without a physical key must still reach a console application.");
    }

    [Test]
    public void ModifiedKeys_FollowModifyOtherKeysModeChangesInTheSameSession() {
        var terminal = new TerminalEmulator {
            AutoStart = false,
            CommandLine = HelperCommand("key-echo.exe") + " --modify-other-keys"
        };
        using var source = Host(terminal);
        using var keyboard = new KeyboardState();
        try {
            var session = Start(terminal, "key-echo-ready");
            keyboard.SetModifiers(ModifierKeys.Shift);
            PressAndRelease(terminal, source, Key.Enter);
            keyboard.SetModifiers(ModifierKeys.Control);
            PressAndRelease(terminal, source, Key.Enter);
            keyboard.SetModifiers(ModifierKeys.Shift);
            PressAndRelease(terminal, source, Key.Tab);
            keyboard.SetModifiers(ModifierKeys.None);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadText(session).Contains("key-echo-reset"));

            // The helper cleared mode 2. ConPTY encodes a Shift+Enter record as CR for this VT reader.
            keyboard.SetModifiers(ModifierKeys.Shift);
            PressAndRelease(terminal, source, Key.Enter);
            keyboard.SetModifiers(ModifierKeys.None);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "#");
            WaitFor(() => string.Concat(ReadLines(session, "key-echo:")).EndsWith("23", StringComparison.Ordinal));

            const string expected = "\u001b[27;2;13~\u001b[27;5;13~\u001b[27;2;9~!\r#";
            var expectedHex = BitConverter.ToString(Encoding.ASCII.GetBytes(expected)).Replace("-", "").ToLowerInvariant();
            Assert.That(string.Concat(ReadLines(session, "key-echo:")), Is.EqualTo(expectedHex));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void ShiftEnter_KeepsKittyEncodingForKittyApplications() {
        var terminal = new TerminalEmulator { AutoStart = false, CommandLine = HelperCommand("key-echo.exe") };
        using var source = Host(terminal);
        using var keyboard = new KeyboardState();
        try {
            var session = Start(terminal, "key-echo-ready");
            keyboard.SetModifiers(ModifierKeys.Shift);
            PressAndRelease(terminal, source, Key.Enter);
            keyboard.SetModifiers(ModifierKeys.None);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => string.Concat(ReadLines(session, "key-echo:")).EndsWith("21", StringComparison.Ordinal));

            Assert.That(string.Concat(ReadLines(session, "key-echo:")),
                Is.EqualTo("1b5b31333b3275" + "1b5b31333b323a3375" + "21"),
                "An application that enables Kitty flags must receive Kitty Shift+Enter press and release.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    private sealed class KeyboardState : IDisposable {
        private readonly byte[] Saved = new byte[256];

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetKeyboardState([Out] byte[] state);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int SetKeyboardState([In] byte[] state);

        internal KeyboardState() {
            Assert.That(GetKeyboardState(Saved), Is.Not.Zero);
        }

        internal void SetModifiers(ModifierKeys modifiers) {
            /*
             * SetKeyboardState changes only this STA thread's keyboard table.
             * Left-hand keys are held, and no lock keys are toggled.
             */
            var state = new byte[256];
            if ((modifiers & ModifierKeys.Control) != 0) {
                state[0x11] = state[0xa2] = 0x80;
            }
            if ((modifiers & ModifierKeys.Alt) != 0) {
                state[0x12] = state[0xa4] = 0x80;
            }
            if ((modifiers & ModifierKeys.Shift) != 0) {
                state[0x10] = state[0xa0] = 0x80;
            }
            Assert.That(SetKeyboardState(state), Is.Not.Zero);
        }

        public void Dispose() {
            Assert.That(SetKeyboardState(Saved), Is.Not.Zero);
        }
    }
}
