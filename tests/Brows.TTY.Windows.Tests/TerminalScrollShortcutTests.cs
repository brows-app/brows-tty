using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Brows.Ghostty;
using Brows.Win32;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalScrollShortcutTests {
    private const string RowPrefix = "scroll-row-";
    private const ModifierKeys ScrollModifiers = ModifierKeys.Control | ModifierKeys.Shift;
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

    private static HwndSource Host(TerminalEmulator terminal) {
        HwndSourceParameters parameters = new("Terminal scroll shortcut integration test") {
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

    private static TerminalFrame ReadFrame(TerminalSession session) {
        return session.Engine.ReadFrame(true);
    }

    private static string[] ReadRows(TerminalSession session) {
        return ReadFrame(session).ChangedRows.OrderBy(row => row.Index)
            .Select(row => string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText)))
            .ToArray();
    }

    private static string ReadText(TerminalSession session) {
        return string.Join("\n", ReadRows(session));
    }

    private static int FirstHistoryRow(TerminalSession session) {
        foreach (string row in ReadRows(session)) {
            int marker = row.IndexOf(RowPrefix, StringComparison.Ordinal);
            if (marker < 0) {
                continue;
            }
            int start = marker + RowPrefix.Length;
            bool parsed = int.TryParse(row.Substring(start, 3), NumberStyles.None,
                CultureInfo.InvariantCulture, out int number);
            if (parsed) {
                return number;
            }
        }
        Assert.Fail("The visible viewport contains no generated scrollback row.");
        return 0;
    }

    private static void WriteHistory(TerminalSession session, int count) {
        StringBuilder history = new();
        for (int row = 1; row <= count; row++) {
            history.Append(RowPrefix).Append(row.ToString("D3", CultureInfo.InvariantCulture)).Append("\r\n");
        }
        byte[] bytes = Encoding.UTF8.GetBytes(history.ToString());
        session.Engine.Write(bytes, bytes.Length);
    }

    private static void RaiseKey(TerminalEmulator terminal, HwndSource source, Key key, RoutedEvent routedEvent) {
        terminal.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
            RoutedEvent = routedEvent
        });
    }

    private static void RaiseKeyPair(TerminalEmulator terminal, HwndSource source, KeyboardState keyboard,
                                     Key key, ModifierKeys modifiers, out bool downHandled, out bool upHandled) {
        keyboard.SetModifiers(modifiers);
        Assert.That(Keyboard.Modifiers, Is.EqualTo(modifiers), "The requested modifier state must be active.");
        KeyEventArgs down = new(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        terminal.RaiseEvent(down);
        KeyEventArgs up = new(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
            RoutedEvent = Keyboard.PreviewKeyUpEvent
        };
        terminal.RaiseEvent(up);
        keyboard.SetModifiers(ModifierKeys.None);
        downHandled = down.Handled;
        upHandled = up.Handled;
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

    private static string ReadKeyBytes(TerminalSession session) {
        return string.Concat(ReadRows(session).Select(line => line.Trim())
            .Where(line => line.StartsWith("key-echo:", StringComparison.Ordinal))
            .Select(line => line.Substring("key-echo:".Length)));
    }

    private static void SendBarrier(TerminalSession session) {
        session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
        WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
        Assert.That(ReadKeyBytes(session), Is.EqualTo("21"),
            "Only the barrier should reach the key echo process; local scroll shortcuts must not leak to it.");
    }

    private static void Write(TerminalSession session, string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        session.Engine.Write(bytes, bytes.Length);
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
    public void ScrollShortcuts_UseConfiguredRowsPagesAndHistoryBoundsWithoutSendingKeys() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            WriteHistory(session, 180);

            TerminalFrame atBottom = ReadFrame(session);
            int viewportRows = checked((int)atBottom.Info.Rows);
            int bottomFirst = FirstHistoryRow(session);
            Assert.That(ReadText(session), Does.Contain(RowPrefix + "180"));
            int wheelScrollLines = SystemParameters.WheelScrollLines;
            int expectedWheelDelta = wheelScrollLines == -1 ? viewportRows : Math.Max(0, wheelScrollLines);

            RaiseKeyPair(terminal, source, keyboard, Key.Up, ScrollModifiers,
                out bool upHandled, out bool upReleaseHandled);
            Assert.That(upHandled, Is.True);
            Assert.That(upReleaseHandled, Is.True);
            Assert.That(FirstHistoryRow(session), Is.EqualTo(bottomFirst - expectedWheelDelta),
                "Ctrl+Shift+Up follows the Windows wheel row setting, including page-scroll mode.");

            RaiseKeyPair(terminal, source, keyboard, Key.Down, ScrollModifiers, out _, out _);
            Assert.That(FirstHistoryRow(session), Is.EqualTo(bottomFirst),
                "Ctrl+Shift+Down returns by the same configured row count.");

            RaiseKeyPair(terminal, source, keyboard, Key.PageUp, ScrollModifiers, out _, out _);
            Assert.That(FirstHistoryRow(session), Is.EqualTo(bottomFirst - viewportRows),
                "Ctrl+Shift+PageUp moves by one viewport.");
            RaiseKeyPair(terminal, source, keyboard, Key.PageDown, ScrollModifiers, out _, out _);
            Assert.That(FirstHistoryRow(session), Is.EqualTo(bottomFirst),
                "Ctrl+Shift+PageDown restores the latest viewport.");

            RaiseKeyPair(terminal, source, keyboard, Key.Home, ScrollModifiers, out _, out _);
            string earliest = ReadText(session);
            Assert.That(earliest, Does.Contain("key-echo-ready"));
            Assert.That(earliest, Does.Not.Contain(RowPrefix + "180"));
            session.Engine.Scroll(-32767);
            Assert.That(ReadText(session), Is.EqualTo(earliest),
                "Ctrl+Shift+Home must match the engine's earliest available history viewport.");
            RaiseKeyPair(terminal, source, keyboard, Key.End, ScrollModifiers, out _, out _);
            string latest = ReadText(session);
            Assert.That(latest, Does.Contain(RowPrefix + "180"));
            session.Engine.Scroll(0, true);
            Assert.That(ReadText(session), Is.EqualTo(latest),
                "Ctrl+Shift+End must match the engine's latest viewport.");

            SendBarrier(session);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void NonExactModifiers_ForwardPageKeyToChild() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));

            RaiseKeyPair(terminal, source, keyboard, Key.PageUp, ModifierKeys.Control,
                out bool downHandled, out bool upHandled);
            Assert.That(downHandled, Is.True);
            Assert.That(upHandled, Is.False, "Forwarded key releases remain available to the host.");
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            Assert.That(ReadKeyBytes(session), Is.Not.EqualTo("21"),
                "Ctrl+PageUp lacks Shift and must be forwarded as a terminal key.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void AlternateScreenScrollShortcuts_StayOnActiveScreenAndDoNotSendKeys() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            Write(session, "\u001b[?1049h");
            WriteHistory(session, 60);
            string activeScreen = ReadText(session);
            Assert.That(activeScreen, Does.Contain(RowPrefix + "060"));

            foreach (Key key in new[] { Key.Up, Key.Down, Key.PageUp, Key.PageDown, Key.Home, Key.End }) {
                RaiseKeyPair(terminal, source, keyboard, key, ScrollModifiers, out _, out _);
                Assert.That(ReadText(session), Is.EqualTo(activeScreen),
                    "A local scroll gesture must not move an alternate screen without scrollback "
                    + "or leak to the child.");
            }
            SendBarrier(session);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
}
}
