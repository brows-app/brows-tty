using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brows.Ghostty;
using Brows.Win32;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalFontShortcutTests {
    private SynchronizationContext? _previousContext;

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

    private static HwndSource Host(Terminal terminal) {
        HwndSourceParameters parameters = new("Terminal font shortcut test") {
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

    private static void DrainInput() {
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task);
    }

    private static void Render(Terminal terminal) {
        terminal.UpdateLayout();
        RenderTargetBitmap bitmap = new(800, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(terminal);
    }

    private static T Field<T>(Terminal terminal, string name) {
        return (T)typeof(Terminal).GetField(name,
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
        return string.Concat(ReadText(session).Split('\n').Select(line => line.Trim())
            .Where(line => line.StartsWith("key-echo:", StringComparison.Ordinal))
            .Select(line => line.Substring("key-echo:".Length)));
    }

    private static KeyEventArgs RaiseKey(Terminal terminal, HwndSource source, Key key, RoutedEvent routedEvent) {
        KeyEventArgs args = new(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
            RoutedEvent = routedEvent
        };
        terminal.RaiseEvent(args);
        return args;
    }

    private static void RaiseFontShortcut(Terminal terminal, HwndSource source, KeyboardState keyboard,
        Key key, ModifierKeys modifiers, string? text = null) {
        keyboard.SetModifiers(modifiers);
        KeyEventArgs press = RaiseKey(terminal, source, key, Keyboard.PreviewKeyDownEvent);
        Assert.That(press.Handled, Is.True, "The local font shortcut must consume its key press.");
        if (text is not null) {
            InputComposition composition = new(terminal, text);
            terminal.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
                RoutedEvent = TextCompositionManager.TextInputEvent
            });
        }
        KeyEventArgs release = RaiseKey(terminal, source, key, Keyboard.PreviewKeyUpEvent);
        Assert.That(release.Handled, Is.True, "The local font shortcut must consume its key release.");
        keyboard.SetModifiers(ModifierKeys.None);
    }

    [SetUp]
    public void SetUp() {
        _previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
    }

    [TearDown]
    public void TearDown() {
        SynchronizationContext.SetSynchronizationContext(_previousContext);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FontShortcuts_ZoomAndResetBindingBaseline(bool running) {
        FontSizeSource settings = new() { Size = 18 };
        Terminal terminal = new() { AutoStart = false };
        BindingOperations.SetBinding(terminal, Control.FontSizeProperty,
            new Binding(nameof(FontSizeSource.Size)) { Source = settings, Mode = BindingMode.OneWay });
        if (running) {
            terminal.CommandLine = KeyEchoCommand();
        }
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        TerminalSession? session = null;
        try {
            if (running) {
                Pump(terminal.StartAsync());
                session = Field<TerminalSession>(terminal, "_session");
                TerminalSession runningSession = session;
                WaitFor(() => ReadText(runningSession).IndexOf("key-echo-ready", StringComparison.Ordinal) >= 0);
            }
            Render(terminal);
            TerminalDrawing drawing = Field<TerminalDrawing>(terminal, "_drawing");
            double initialCellHeight = drawing.CellHeight;

            RaiseFontShortcut(terminal, source, keyboard, Key.OemPlus, ModifierKeys.Control, "=");
            Assert.That(terminal.FontSize, Is.EqualTo(19));
            Render(terminal);
            Assert.That(drawing.CellHeight, Is.GreaterThan(initialCellHeight),
                "The font shortcut must rebuild terminal cell metrics.");
            RaiseFontShortcut(terminal, source, keyboard, Key.OemPlus,
                ModifierKeys.Control | ModifierKeys.Shift, "+");
            Assert.That(terminal.FontSize, Is.EqualTo(20));
            RaiseFontShortcut(terminal, source, keyboard, Key.Add, ModifierKeys.Control, "+");
            Assert.That(terminal.FontSize, Is.EqualTo(21));
            RaiseFontShortcut(terminal, source, keyboard, Key.OemMinus, ModifierKeys.Control, "-");
            Assert.That(terminal.FontSize, Is.EqualTo(20));
            RaiseFontShortcut(terminal, source, keyboard, Key.Subtract, ModifierKeys.Control, "-");
            Assert.That(terminal.FontSize, Is.EqualTo(19));
            RaiseFontShortcut(terminal, source, keyboard, Key.D0, ModifierKeys.Control, "0");
            Assert.That(terminal.FontSize, Is.EqualTo(18));

            double updatedConfiguredSize = running ? 24 : 200;
            settings.Size = updatedConfiguredSize;
            DrainInput();
            Assert.That(terminal.FontSize, Is.EqualTo(updatedConfiguredSize));
            RaiseFontShortcut(terminal, source, keyboard, Key.OemPlus,
                ModifierKeys.Control | ModifierKeys.Shift, "+");
            Assert.That(terminal.FontSize, Is.EqualTo(updatedConfiguredSize + 1));
            RaiseFontShortcut(terminal, source, keyboard, Key.NumPad0, ModifierKeys.Control, "0");
            Assert.That(terminal.FontSize, Is.EqualTo(updatedConfiguredSize),
                "Reset must use the latest configured size, including valid values above 128.");
            Assert.That(BindingOperations.GetBindingExpression(terminal, Control.FontSizeProperty),
                Is.Not.Null, "Zoom and reset must preserve the FontSize binding.");

            if (running) {
                TerminalSession runningSession = session!;
                runningSession.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
                WaitFor(() => ReadKeyBytes(runningSession).EndsWith("21", StringComparison.Ordinal));
                Assert.That(ReadKeyBytes(runningSession), Is.EqualTo("21"),
                    "Shortcut presses, releases, and generated text must not reach the child process.");
            }
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            keyboard.SetModifiers(ModifierKeys.None);
            if (running) {
                Pump(terminal.StopAsync());
            }
        }
    }

    [Test]
    public void FontShortcuts_LeaveShiftedMinusAndAltGrPlusAsTerminalInput() {
        Terminal terminal = new() {
            AutoStart = false,
            FontSize = 18,
            CommandLine = KeyEchoCommand()
        };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).IndexOf("key-echo-ready", StringComparison.Ordinal) >= 0);

            keyboard.SetModifiers(ModifierKeys.Control | ModifierKeys.Shift);
            Assert.That(Keyboard.Modifiers, Is.EqualTo(ModifierKeys.Control | ModifierKeys.Shift));
            KeyEventArgs shiftedMinus = RaiseKey(terminal, source, Key.OemMinus, Keyboard.PreviewKeyDownEvent);
            Assert.That(shiftedMinus.Handled, Is.True, "Ctrl+Shift+Minus remains terminal input.");
            Assert.That(terminal.FontSize, Is.EqualTo(18));
            HashSet<Key> forwardedKeys = Field<HashSet<Key>>(terminal, "_forwardedKeys");
            Assert.That(forwardedKeys, Does.Contain(Key.OemMinus));
            RaiseKey(terminal, source, Key.OemMinus, Keyboard.PreviewKeyUpEvent);
            Assert.That(forwardedKeys, Does.Not.Contain(Key.OemMinus));

            keyboard.SetModifiers(ModifierKeys.Control | ModifierKeys.Alt);
            KeyEventArgs altGrPlus = RaiseKey(terminal, source, Key.OemPlus, Keyboard.PreviewKeyDownEvent);
            Assert.That(altGrPlus.Handled, Is.False, "Ctrl+Alt+Plus can still produce AltGr text.");
            InputComposition composition = new(terminal, "+");
            terminal.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
                RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent
            });
            terminal.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
                RoutedEvent = TextCompositionManager.TextInputEvent
            });
            keyboard.SetModifiers(ModifierKeys.None);
            RaiseKey(terminal, source, Key.OemPlus, Keyboard.PreviewKeyUpEvent);
            DrainInput();

            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            const string expectedInput = "2b21";
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            Assert.That(ReadKeyBytes(session), Is.EqualTo(expectedInput),
                "AltGr plus must reach the child exactly once as text.");
            Assert.That(terminal.FontSize, Is.EqualTo(18));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            keyboard.SetModifiers(ModifierKeys.None);
            Pump(terminal.StopAsync());
        }
    }

    [TestCase(0.5)]
    [TestCase(1.0)]
    public void FontZoomOut_AtOrBelowOneDipLeavesSizeAndResetBaseline(double fontSize) {
        Terminal terminal = new() { AutoStart = false, FontSize = fontSize };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        RaiseFontShortcut(terminal, source, keyboard, Key.OemMinus, ModifierKeys.Control, "-");
        Assert.That(terminal.FontSize, Is.EqualTo(fontSize));
        RaiseFontShortcut(terminal, source, keyboard, Key.OemPlus, ModifierKeys.Control, "=");
        Assert.That(terminal.FontSize, Is.EqualTo(fontSize + 1));
        RaiseFontShortcut(terminal, source, keyboard, Key.D0, ModifierKeys.Control, "0");
        Assert.That(terminal.FontSize, Is.EqualTo(fontSize),
            "Reset must restore a valid configured size below the zoom-out bound.");
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

    private sealed class InputComposition : TextComposition {
        internal InputComposition(Terminal terminal, string text)
            : base(InputManager.Current, terminal, text) { }
    }

    private sealed class FontSizeSource : INotifyPropertyChanged {
        public event PropertyChangedEventHandler? PropertyChanged;

        public double Size {
            get;
            set {
                if (field == value) {
                    return;
                }
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Size)));
            }
        }
    }
}
