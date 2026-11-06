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
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalClipboardShortcutTests {
    private SynchronizationContext? _previousContext;

    private sealed class KeyboardState : IDisposable {
        private readonly byte[] _saved = new byte[256];

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetKeyboardState([Out] byte[] state);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int SetKeyboardState([In] byte[] state);

        internal KeyboardState() {
            Assert.That(GetKeyboardState(_saved), Is.Not.Zero);
        }

        internal void SetModifiers(ModifierKeys modifiers) {
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
            Assert.That(SetKeyboardState(state), Is.Not.Zero);
        }

        public void Dispose() {
            Assert.That(SetKeyboardState(_saved), Is.Not.Zero);
        }
    }

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
        HwndSourceParameters parameters = new("Terminal clipboard shortcut test") {
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

    private static string ReadText(TerminalSession session) {
        TerminalFrame frame = session.Engine.ReadFrame(true);
        return string.Join("\n", frame.ChangedRows.Select(row =>
            string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText))));
    }

    private static string KeyEchoCommand() {
        string architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "AMD64";
        string rid = architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "win-arm64" : "win-x64";
        return "\"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "helpers", rid, "key-echo.exe") + "\"";
    }

    private static string ReadKeyBytes(TerminalSession session) {
        return string.Concat(ReadText(session).Split('\n').Select(line => line.Trim())
            .Where(line => line.StartsWith("key-echo:", StringComparison.Ordinal))
            .Select(line => line.Substring("key-echo:".Length)));
    }

    private static T Field<T>(TerminalEmulator terminal, string name) {
        return (T)typeof(TerminalEmulator).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
    }

    private static TerminalSession Start(TerminalEmulator terminal) {
        Pump(terminal.StartAsync());
        TerminalSession session = Field<TerminalSession>(terminal, "_session");
        WaitFor(() => ReadText(session).Contains("key-echo-ready"));
        return session;
    }

    private static KeyEventArgs RaiseKey(TerminalEmulator terminal, HwndSource source, Key key,
                                         RoutedEvent routedEvent) {
        KeyEventArgs args = new(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
            RoutedEvent = routedEvent
        };
        terminal.RaiseEvent(args);
        return args;
    }

    private static TextCompositionEventArgs RaiseText(TerminalEmulator terminal, string text) {
        TextComposition composition = new(InputManager.Current, terminal, text);
        TextCompositionEventArgs args = new(Keyboard.PrimaryDevice, composition) {
            RoutedEvent = TextCompositionManager.TextInputEvent
        };
        terminal.RaiseEvent(args);
        return args;
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
    public void EnterWithSelection_CopiesOnRepeatAndSuppressesNewlines() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            keyboard.SetModifiers(ModifierKeys.None);
            Field<DispatcherTimer>(terminal, "_timer").Stop();
            byte[] output = Encoding.UTF8.GetBytes("\u001b[2J\u001b[Hcopied-text");
            session.Engine.Write(output, output.Length);
            session.Engine.Select(0, 0, 5, 0);
            Clipboard.SetText("unchanged");

            KeyEventArgs press = RaiseKey(terminal, source, Key.Enter, Keyboard.PreviewKeyDownEvent);
            KeyEventArgs repeat = RaiseKey(terminal, source, Key.Enter, Keyboard.PreviewKeyDownEvent);
            TextCompositionEventArgs carriageReturn = RaiseText(terminal, "\r");
            TextCompositionEventArgs lineFeed = RaiseText(terminal, "\n");
            KeyEventArgs release = RaiseKey(terminal, source, Key.Enter, Keyboard.PreviewKeyUpEvent);
            byte[] newline = Encoding.UTF8.GetBytes("\r\n");
            session.Engine.Write(newline, newline.Length);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));

            Assert.That(ReadKeyBytes(session), Is.EqualTo("21"),
                "Copy, repeated Enter, its text input, and release must not reach the child.");
            Assert.That(press.Handled, Is.True);
            Assert.That(repeat.Handled, Is.True, "An autorepeat after the selection is cleared must stay local.");
            Assert.That(carriageReturn.Handled, Is.True);
            Assert.That(lineFeed.Handled, Is.True);
            Assert.That(release.Handled, Is.True);
            Assert.That(Clipboard.GetText(), Is.EqualTo("copied"));
            Assert.That(session.Engine.SelectedText(), Is.Empty);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void EnterWithoutSelection_ForwardsPressAndRelease() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            keyboard.SetModifiers(ModifierKeys.None);
            KeyEventArgs press = RaiseKey(terminal, source, Key.Enter, Keyboard.PreviewKeyDownEvent);
            KeyEventArgs release = RaiseKey(terminal, source, Key.Enter, Keyboard.PreviewKeyUpEvent);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));

            Assert.That(press.Handled, Is.True);
            Assert.That(release.Handled, Is.False);
            Assert.That(ReadKeyBytes(session), Is.EqualTo("1b5b3133751b5b31333b313a337521"),
                "Without a selection, Enter's press and release must reach the child.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [TestCase(Key.V, ModifierKeys.Control, "v")]
    [TestCase(Key.V, ModifierKeys.Control | ModifierKeys.Shift, "V")]
    [TestCase(Key.Insert, ModifierKeys.Shift, "")]
    public void PasteShortcut_PastesClipboardOnceWithoutKeyInput(Key key, ModifierKeys modifiers, string text) {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            byte[] bracketedPasteMode = Encoding.UTF8.GetBytes("\u001b[?2004h");
            session.Engine.Write(bracketedPasteMode, bracketedPasteMode.Length);
            Clipboard.SetText("paste-text");
            keyboard.SetModifiers(modifiers);

            KeyEventArgs press = RaiseKey(terminal, source, key, Keyboard.PreviewKeyDownEvent);
            TextCompositionEventArgs? composition = text.Length == 0 ? null : RaiseText(terminal, text);
            keyboard.SetModifiers(ModifierKeys.None);
            KeyEventArgs release = RaiseKey(terminal, source, key, Keyboard.PreviewKeyUpEvent);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));

            Assert.That(ReadKeyBytes(session), Is.EqualTo("1b5b3230307e70617374652d746578741b5b3230317e21"),
                "Only the bracketed clipboard paste and the marker must reach the child.");
            Assert.That(press.Handled, Is.True);
            Assert.That(composition?.Handled ?? true, Is.True);
            Assert.That(release.Handled, Is.True);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void ControlAltV_PreservesAltGrTextAndDoesNotPaste() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = Start(terminal);
            DataObject clipboard = new();
            clipboard.SetData(DataFormats.UnicodeText, "unchanged");
            /*
             * This test only reads the clipboard in-process and does not need persistent ownership.
             */
            Clipboard.SetDataObject(clipboard, false);
            keyboard.SetModifiers(ModifierKeys.Control | ModifierKeys.Alt);
            RaiseKey(terminal, source, Key.V, Keyboard.PreviewKeyDownEvent);
            TextCompositionEventArgs composition = RaiseText(terminal, "\u20ac");
            keyboard.SetModifiers(ModifierKeys.None);
            RaiseKey(terminal, source, Key.V, Keyboard.PreviewKeyUpEvent);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));

            Assert.That(composition.Handled, Is.True);
            Assert.That(Clipboard.GetText(), Is.EqualTo("unchanged"));
            Assert.That(ReadKeyBytes(session), Is.EqualTo("e282ac21"),
                "Ctrl+Alt+V text must be sent as AltGr text without pasting the clipboard.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }
}
