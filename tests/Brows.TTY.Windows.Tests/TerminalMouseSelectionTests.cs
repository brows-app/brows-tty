using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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
public sealed class TerminalMouseSelectionTests {
    private const string WheelRowPrefix = "mouse-wheel-row-";
    private SynchronizationContext? _previousContext;
    private KeyboardState? _keyboardState;
    private readonly Dictionary<TerminalSession, int> _consumedInputOffsets = new();
    private KeyboardState Keyboard => _keyboardState!;

    private static HwndSource Host(TerminalEmulator terminal) {
        HwndSourceParameters parameters = new("Terminal mouse integration test") {
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
        FieldInfo? field = typeof(TerminalEmulator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, "Expected terminal state field " + name + ".");
        return (T)field!.GetValue(terminal)!;
    }

    private static object? Invoke(TerminalEmulator terminal, string name, params object?[] arguments) {
        MethodInfo? method = typeof(TerminalEmulator).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Expected terminal operation " + name + ".");
        return method!.Invoke(terminal, arguments);
    }

    private static string KeyEchoCommand() {
        string architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "AMD64";
        string rid = architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "win-arm64" : "win-x64";
        string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "helpers", rid, "key-echo.exe");
        return "\"" + executable + "\"";
    }

    private static string CommandProcessor() {
        return "\"" + (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") + "\"";
    }

    private static TerminalEmulator CreateTerminal(string? commandLine = null) {
        return new TerminalEmulator {
            AutoStart = false,
            CommandLine = commandLine ?? KeyEchoCommand()
        };
    }

    private static TerminalSession Start(TerminalEmulator terminal) {
        Pump(terminal.StartAsync());
        TerminalSession session = Field<TerminalSession>(terminal, "_session");
        WaitFor(() => ReadText(session).IndexOf("key-echo-ready", StringComparison.Ordinal) >= 0);
        return session;
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

    private static string ReadDisplayedText(TerminalEmulator terminal) {
        TerminalRow?[] rows = Field<TerminalRow?[]>(terminal, "_rowSnapshots");
        return string.Join("\n", rows.Select(row => row is null
            ? ""
            : string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText))));
    }

    private static string ReadKeyBytes(TerminalSession session) {
        string output = string.Concat(ReadRows(session).Select(line => line.TrimEnd()));
        MatchCollection matches = Regex.Matches(output, "key-echo:([0-9a-f]+)");
        return string.Concat(matches.Cast<Match>().Select(match => match.Groups[1].Value));
    }

    private static string Hex(string text) {
        StringBuilder result = new();
        foreach (byte value in Encoding.ASCII.GetBytes(text)) {
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private static void Write(TerminalSession session, string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        session.Engine.Write(bytes, bytes.Length);
    }

    private static void EnableMouse(TerminalEmulator terminal, TerminalSession session, int trackingMode) {
        Write(session, "\u001b[?" + trackingMode.ToString(CultureInfo.InvariantCulture) + "h\u001b[?1006h");
        Invoke(terminal, "RefreshFrame", false);
        Assert.That(Field<NativeFrame>(terminal, "_frame").MouseTracking, Is.Not.Zero);
    }

    private static void ArrangeGrid(TerminalEmulator terminal, int columns, int rows) {
        TerminalDrawing drawing = Field<TerminalDrawing>(terminal, "_drawing");
        double width = drawing.CellWidth * columns + 1;
        double height = drawing.CellHeight * rows + 1;
        terminal.Width = width;
        terminal.Height = height;
        terminal.Measure(new Size(width, height));
        terminal.Arrange(new Rect(0, 0, width, height));
        terminal.UpdateLayout();
        Invoke(terminal, "ConfigureSize");
    }

    private static Point CellCenter(TerminalEmulator terminal, int column, int row) {
        TerminalDrawing drawing = Field<TerminalDrawing>(terminal, "_drawing");
        return new Point((column + 0.5) * drawing.CellWidth, (row + 0.5) * drawing.CellHeight);
    }

    private static MouseButtonEventArgs MouseButtonEvent(MouseButton button, RoutedEvent routedEvent) {
        return new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) {
            RoutedEvent = routedEvent
        };
    }

    private static void MouseDown(TerminalEmulator terminal, MouseButton button, Point position) {
        MouseButtonEventArgs args = MouseButtonEvent(button, Mouse.MouseDownEvent);
        Invoke(terminal, "HandleMouseDown", args, position, false);
        Assert.That(args.Handled, Is.True);
    }

    private static void MouseMove(TerminalEmulator terminal, Point position) {
        Invoke(terminal, "HandleMouseMove", position);
    }

    private static void MouseUp(TerminalEmulator terminal, MouseButton button, Point position) {
        MouseButtonEventArgs args = MouseButtonEvent(button, Mouse.MouseUpEvent);
        Invoke(terminal, "HandleMouseUp", args, position);
        Assert.That(args.Handled, Is.True);
    }

    private static void MouseWheel(TerminalEmulator terminal, int delta, Point position) {
        MouseWheelEventArgs args = new(Mouse.PrimaryDevice, Environment.TickCount, delta) {
            RoutedEvent = Mouse.MouseWheelEvent
        };
        Invoke(terminal, "HandleMouseWheel", args, position);
        Assert.That(args.Handled, Is.True);
    }

    private string DrainChildInput(TerminalSession session) {
        string beforeBarrier = ReadKeyBytes(session);
        bool hasConsumedOffset = _consumedInputOffsets.TryGetValue(session, out int consumedOffset);
        if (!hasConsumedOffset) {
            consumedOffset = 0;
        }
        if (consumedOffset > beforeBarrier.Length) {
            consumedOffset = beforeBarrier.Length;
        }
        session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
        string current = beforeBarrier;
        WaitFor(() => {
            current = ReadKeyBytes(session);
            bool newBarrierWasEchoed = current.Length > beforeBarrier.Length
                && current.EndsWith(Hex("!"), StringComparison.Ordinal);
            return newBarrierWasEchoed;
        });
        _consumedInputOffsets[session] = current.Length;
        return current.Substring(consumedOffset);
    }

    private void WaitForMouseBytes(TerminalSession session, string bytes) {
        string expected = Hex(bytes);
        string current = "";
        WaitFor(() => {
            current = ReadKeyBytes(session);
            return current.EndsWith(expected, StringComparison.Ordinal);
        });
        _consumedInputOffsets[session] = current.Length;
        Assert.That(current, Does.EndWith(expected));
    }

    private static void WriteHistory(TerminalSession session, string prefix, int count) {
        StringBuilder history = new("\r\n");
        for (int row = 1; row <= count; row++) {
            history.Append(prefix).Append(row.ToString("D3", CultureInfo.InvariantCulture)).Append("\r\n");
        }
        Write(session, history.ToString());
    }

    private static int FirstHistoryRow(TerminalSession session, string prefix) {
        foreach (string row in ReadRows(session)) {
            int marker = row.IndexOf(prefix, StringComparison.Ordinal);
            if (marker < 0) {
                continue;
            }
            int start = marker + prefix.Length;
            bool parsed = int.TryParse(row.Substring(start, 3), NumberStyles.None,
                CultureInfo.InvariantCulture, out int number);
            if (parsed) {
                return number;
            }
        }
        Assert.Fail("The visible viewport contains no generated history row.");
        return 0;
    }

    private static void SendExit(TerminalSession session, TaskCompletionSource<uint> exitCode) {
        session.Engine.Paste("exit");
        session.Key(0x0d, 0, 1, "");
        Pump(exitCode.Task);
        Assert.That(exitCode.Task.Result, Is.Zero);
    }

    [SetUp]
    public void SetUp() {
        _previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        _keyboardState = new KeyboardState();
    }

    [TearDown]
    public void TearDown() {
        _keyboardState?.Dispose();
        SynchronizationContext.SetSynchronizationContext(_previousContext);
    }

    [Test]
    public void ResizeDuringLocalDrag_CancelsTheGestureAndKeepsFramesUpdating() {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            MouseDown(terminal, MouseButton.Left, CellCenter(terminal, 70, 0));
            MouseMove(terminal, CellCenter(terminal, 75, 0));

            ArrangeGrid(terminal, 25, 12);

            Assert.That(Field<Point?>(terminal, "_selectionStart"), Is.Null,
                "A grid resize must retire the old viewport anchor before another endpoint is applied.");
            Assert.That(Field<HashSet<MouseButton>>(terminal, "_localMouseButtons"), Is.Empty);
            Assert.That(Field<DispatcherTimer>(terminal, "_timer").IsEnabled, Is.True,
                "Cancelling a local selection must leave frame updates scheduled.");
            Assert.That(terminal.IsRunning, Is.True);

            Write(session, "resize-frame-ok");
            WaitFor(() => ReadDisplayedText(terminal).IndexOf("resize-frame-ok", StringComparison.Ordinal) >= 0);

            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void SelectionAnchor_FollowsOriginalContentAcrossOutputScrollAndCopy() {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            ArrangeGrid(terminal, 20, 3);
            Write(session, "\u001b[2J\u001b[Hone\r\ntwo\r\nthree");
            Invoke(terminal, "RefreshFrame", false);

            MouseDown(terminal, MouseButton.Left, CellCenter(terminal, 0, 0));
            MouseMove(terminal, CellCenter(terminal, 2, 0));
            Invoke(terminal, "OnTick", null, EventArgs.Empty);
            Assert.That(session.Engine.SelectedText(), Does.Contain("one"));

            Write(session, "\r\nfour");
            Invoke(terminal, "RefreshFrame", false);
            MouseMove(terminal, CellCenter(terminal, 3, 0));
            MouseUp(terminal, MouseButton.Left, CellCenter(terminal, 3, 0));

            string selected = session.Engine.SelectedText();
            string trimmedSelection = selected.TrimEnd();
            Assert.That(trimmedSelection, Does.StartWith("one"),
                "The selection must retain the content where the drag began.");
            Assert.That(trimmedSelection, Does.EndWith("two"),
                "The endpoint must resolve in the current viewport and stop at its row.");
            Assert.That(selected, Does.Not.Contain("three"));
            Assert.That(selected, Does.Not.Contain("four"));
            terminal.CopySelection();
            Assert.That(Clipboard.GetText(), Is.EqualTo(selected));
            Assert.That(session.Engine.SelectedText(), Is.Empty);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void AlternateScreenChange_CancelsTheTrackedAnchorWithoutStoppingFrames() {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            Write(session, "\u001b[2J\u001b[Hprimary-screen-content");
            Invoke(terminal, "RefreshFrame", false);
            MouseDown(terminal, MouseButton.Left, CellCenter(terminal, 0, 0));
            MouseMove(terminal, CellCenter(terminal, 5, 0));
            Write(session, "\u001b[?1049h");

            Invoke(terminal, "OnTick", null, EventArgs.Empty);

            Assert.That(Field<Point?>(terminal, "_selectionStart"), Is.Null);
            Assert.That(Field<HashSet<MouseButton>>(terminal, "_localMouseButtons"), Is.Empty);
            Assert.That(Field<DispatcherTimer>(terminal, "_timer").IsEnabled, Is.True,
                "A selection anchor lost on a screen switch must not stop rendering.");
            Assert.That(terminal.IsRunning, Is.True);
            Assert.That(terminal.LastError, Is.Null);

            Write(session, "alt-frame-ok");
            WaitFor(() => ReadDisplayedText(terminal).IndexOf("alt-frame-ok", StringComparison.Ordinal) >= 0);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void ClearedScrollback_CancelsAnEvictedTrackedAnchorWithoutStoppingFrames() {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            ArrangeGrid(terminal, 20, 3);
            Write(session, "\u001b[2J\u001b[Hevict-anchor-content");
            Invoke(terminal, "RefreshFrame", false);
            MouseDown(terminal, MouseButton.Left, CellCenter(terminal, 0, 0));
            MouseMove(terminal, CellCenter(terminal, 2, 0));
            Invoke(terminal, "OnTick", null, EventArgs.Empty);

            Write(session, "\r\nscroll-one\r\nscroll-two\r\nscroll-three");
            session.Engine.ScrollToTop();
            TerminalFrame historyFrame = ReadFrame(session);
            bool anchorWasMovedIntoHistory = historyFrame.ChangedRows.Any(row =>
                string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText))
                    .IndexOf("evict-anchor-content", StringComparison.Ordinal) >= 0);
            Assert.That(anchorWasMovedIntoHistory, Is.True,
                "The selection anchor must enter scrollback before the test removes that history.");

            Write(session, "\u001b[3J");
            MouseMove(terminal, CellCenter(terminal, 3, 1));
            Invoke(terminal, "OnTick", null, EventArgs.Empty);

            Assert.That(Field<Point?>(terminal, "_selectionStart"), Is.Null,
                "A tracked point removed by clearing scrollback must cancel the gesture.");
            Assert.That(Field<HashSet<MouseButton>>(terminal, "_localMouseButtons"), Is.Empty);
            Assert.That(session.Engine.SelectedText(), Is.Empty);
            Assert.That(Field<DispatcherTimer>(terminal, "_timer").IsEnabled, Is.True,
                "An evicted anchor is a normal cancellation and must leave frame updates scheduled.");
            Assert.That(terminal.IsRunning, Is.True);
            Assert.That(terminal.LastError, Is.Null);

            Write(session, "\u001b[Hevict-frame-ok");
            WaitFor(() => ReadDisplayedText(terminal).IndexOf("evict-frame-ok",
                StringComparison.Ordinal) >= 0);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [TestCase(1002, MouseButton.Left, 0u, 32u)]
    [TestCase(1002, MouseButton.Middle, 1u, 33u)]
    [TestCase(1002, MouseButton.Right, 2u, 34u)]
    [TestCase(1003, MouseButton.Left, 0u, 32u)]
    [TestCase(1003, MouseButton.Middle, 1u, 33u)]
    [TestCase(1003, MouseButton.Right, 2u, 34u)]
    public void ApplicationDrag_MotionKeepsThePressedButtonIdentity(int trackingMode, MouseButton button,
                                                                    uint pressCode, uint motionCode) {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            EnableMouse(terminal, session, trackingMode);
            Point pressPoint = CellCenter(terminal, 0, 0);
            Point movePoint = CellCenter(terminal, 2, 1);
            string press = "\u001b[<" + pressCode.ToString(CultureInfo.InvariantCulture) + ";1;1M";
            string motion = "\u001b[<" + motionCode.ToString(CultureInfo.InvariantCulture) + ";3;2M";
            string release = "\u001b[<" + pressCode.ToString(CultureInfo.InvariantCulture) + ";3;2m";

            MouseDown(terminal, button, pressPoint);
            WaitForMouseBytes(session, press);
            MouseMove(terminal, movePoint);
            WaitForMouseBytes(session, motion);
            MouseUp(terminal, button, movePoint);
            WaitForMouseBytes(session, release);

            Assert.That(ReadKeyBytes(session), Does.EndWith(Hex(press + motion + release)));
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [TestCase(MouseButton.XButton1, 128u)]
    [TestCase(MouseButton.XButton2, 129u)]
    public void ExtendedMouseButtons_KeepTheirGhosttyIdentity(MouseButton button, uint encodedButton) {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            EnableMouse(terminal, session, 1002);
            Point point = CellCenter(terminal, 0, 0);
            string buttonCode = encodedButton.ToString(CultureInfo.InvariantCulture);
            string motionCode = (encodedButton + 32).ToString(CultureInfo.InvariantCulture);

            MouseDown(terminal, button, point);
            WaitForMouseBytes(session, "\u001b[<" + buttonCode + ";1;1M");
            Point motionPoint = CellCenter(terminal, 1, 1);
            MouseMove(terminal, motionPoint);
            WaitForMouseBytes(session, "\u001b[<" + motionCode + ";2;2M");
            MouseUp(terminal, button, motionPoint);
            WaitForMouseBytes(session, "\u001b[<" + buttonCode + ";2;2m");
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void MultipleApplicationButtons_KeepMotionAndCaptureLossReleasesPaired() {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            EnableMouse(terminal, session, 1002);
            Point leftPoint = CellCenter(terminal, 0, 0);
            Point rightPoint = CellCenter(terminal, 1, 0);
            Point motionPoint = CellCenter(terminal, 2, 1);

            MouseDown(terminal, MouseButton.Left, leftPoint);
            WaitForMouseBytes(session, "\u001b[<0;1;1M");
            MouseDown(terminal, MouseButton.Right, rightPoint);
            WaitForMouseBytes(session, "\u001b[<2;2;1M");
            MouseMove(terminal, motionPoint);
            WaitForMouseBytes(session, "\u001b[<34;3;2M");
            MouseUp(terminal, MouseButton.Right, motionPoint);
            WaitForMouseBytes(session, "\u001b[<2;3;2m");
            MouseMove(terminal, rightPoint);
            WaitForMouseBytes(session, "\u001b[<32;2;1M");

            Invoke(terminal, "HandleMouseCaptureLost", rightPoint);
            WaitForMouseBytes(session, "\u001b[<0;2;1m");

            Assert.That(Field<Dictionary<MouseButton, uint>>(terminal, "_applicationMouseButtons"), Is.Empty);
            Assert.That(Field<List<MouseButton>>(terminal, "_applicationMouseButtonOrder"), Is.Empty);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [TestCase(1002)]
    [TestCase(1003)]
    public void ApplicationDrag_ReportsMotionBeyondTheViewportWhileHeld(int trackingMode) {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            EnableMouse(terminal, session, trackingMode);
            Point inside = CellCenter(terminal, 0, 0);
            TerminalDrawing drawing = Field<TerminalDrawing>(terminal, "_drawing");
            Point outside = new(-drawing.CellWidth, -drawing.CellHeight);

            MouseDown(terminal, MouseButton.Left, inside);
            WaitForMouseBytes(session, "\u001b[<0;1;1M");
            MouseMove(terminal, outside);
            WaitForMouseBytes(session, "\u001b[<32;1;1M");
            MouseUp(terminal, MouseButton.Left, outside);
            WaitForMouseBytes(session, "\u001b[<0;1;1m");
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void KeyboardFocusLoss_ReleasesApplicationOwnedMouseButtons() {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            EnableMouse(terminal, session, 1000);
            MouseDown(terminal, MouseButton.Left, CellCenter(terminal, 0, 0));
            WaitForMouseBytes(session, "\u001b[<0;1;1M");

            Invoke(terminal, "HandleKeyboardFocusLost");

            WaitForMouseBytes(session, "\u001b[<0;1;1m");
            Assert.That(Field<Dictionary<MouseButton, uint>>(terminal, "_applicationMouseButtons"), Is.Empty);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void KeyboardFocusLoss_CancelsLocalSelectionGesture() {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            Write(session, "\u001b[2J\u001b[Hfocus-selection-content");
            Invoke(terminal, "RefreshFrame", false);
            MouseDown(terminal, MouseButton.Left, CellCenter(terminal, 0, 0));
            MouseMove(terminal, CellCenter(terminal, 5, 0));

            Invoke(terminal, "HandleKeyboardFocusLost");

            Assert.That(Field<Point?>(terminal, "_selectionStart"), Is.Null);
            Assert.That(Field<HashSet<MouseButton>>(terminal, "_localMouseButtons"), Is.Empty);
            Assert.That(Field<bool>(terminal, "_selectionPending"), Is.False);
            Assert.That(Field<DispatcherTimer>(terminal, "_timer").IsEnabled, Is.True);
            Assert.That(terminal.IsRunning, Is.True);
            Assert.That(terminal.LastError, Is.Null);

            Write(session, "focus-frame-ok");
            WaitFor(() => ReadDisplayedText(terminal).IndexOf("focus-frame-ok",
                StringComparison.Ordinal) >= 0);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void NaturalExit_CancelsHeldApplicationMouseOwnership() {
        TerminalEmulator terminal = CreateTerminal(CommandProcessor() + " /d /c \"ping 127.0.0.1 -n 3 >nul\"");
        using HwndSource source = Host(terminal);
        TaskCompletionSource<uint> exitCode = new();
        terminal.TerminalExited += (sender, args) => exitCode.TrySetResult(args.ExitCode);
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            EnableMouse(terminal, session, 1000);
            MouseDown(terminal, MouseButton.Left, CellCenter(terminal, 0, 0));
            bool leftButtonIsApplicationOwned = Field<Dictionary<MouseButton, uint>>(terminal,
                "_applicationMouseButtons").ContainsKey(MouseButton.Left);
            Assert.That(leftButtonIsApplicationOwned, Is.True,
                "The test must hold an application-owned button before exit.");

            Pump(exitCode.Task);
            WaitFor(() => !terminal.IsRunning);

            Assert.That(Field<Dictionary<MouseButton, uint>>(terminal, "_applicationMouseButtons"), Is.Empty);
            Assert.That(Field<List<MouseButton>>(terminal, "_applicationMouseButtonOrder"), Is.Empty);
            Assert.That(Field<HashSet<MouseButton>>(terminal, "_localMouseButtons"), Is.Empty);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void MouseTrackingHover_ReportsOnlyInAnyEventMode() {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            Point hoverPoint = CellCenter(terminal, 1, 1);
            EnableMouse(terminal, session, 1002);
            MouseMove(terminal, hoverPoint);
            string buttonTrackingBytes = DrainChildInput(session);
            Assert.That(buttonTrackingBytes, Is.EqualTo(Hex("!")),
                "Button tracking must not emit motion without a pressed button.");

            Write(session, "\u001b[?1002l\u001b[?1003h");
            Invoke(terminal, "RefreshFrame", false);
            MouseMove(terminal, hoverPoint);
            string anyEventBytes = DrainChildInput(session);
            Assert.That(anyEventBytes, Is.EqualTo(Hex("\u001b[<35;2;2M!")),
                "Any-event tracking must report hover motion with no button held.");
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void ShiftChangesDuringGestures_DoNotChangeButtonOwnership() {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            EnableMouse(terminal, session, 1000);
            Write(session, "\u001b[2J\u001b[Habc");
            Invoke(terminal, "RefreshFrame", false);
            Point start = CellCenter(terminal, 0, 0);
            Point end = CellCenter(terminal, 2, 0);

            Keyboard.SetModifiers(ModifierKeys.Shift);
            MouseDown(terminal, MouseButton.Left, start);
            MouseMove(terminal, end);
            Keyboard.SetModifiers(ModifierKeys.None);
            MouseUp(terminal, MouseButton.Left, end);
            Assert.That(session.Engine.SelectedText(), Is.EqualTo("abc"),
                "A locally owned Shift selection must remain local through mouse-up.");
            Assert.That(DrainChildInput(session), Is.EqualTo(Hex("!")),
                "The local gesture must not produce an orphaned application release.");

            MouseDown(terminal, MouseButton.Left, start);
            Keyboard.SetModifiers(ModifierKeys.Shift);
            MouseUp(terminal, MouseButton.Left, start);
            string expectedApplicationEvents = "\u001b[<0;1;1M\u001b[<4;1;1m!";
            Assert.That(DrainChildInput(session), Is.EqualTo(Hex(expectedApplicationEvents)),
                "An application-owned press must receive a release even after Shift changes.");
        }
        finally {
            Keyboard.SetModifiers(ModifierKeys.None);
            Pump(terminal.StopAsync());
        }
    }

    [TestCase(0, -1, 24, 0)]
    [TestCase(2, 0, 24, 0)]
    [TestCase(2, -1, 24, -48)]
    [TestCase(-2, -1, 24, 48)]
    [TestCase(2, 4, 24, -8)]
    public void WheelScrollRows_UsesDisabledCustomAndPageScrollSettings(int detents, int setting,
                                                                        int viewportRows, int expectedRows) {
        MethodInfo? method = typeof(TerminalEmulator).GetMethod("WheelScrollRows",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);

        int actualRows = (int)method!.Invoke(null, new object[] { (long)detents, setting, viewportRows })!;

        Assert.That(actualRows, Is.EqualTo(expectedRows));
    }

    [Test]
    public void LocalWheel_PreservesPartialAndMultipleDetentsAndWindowsRowCount() {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            WriteHistory(session, WheelRowPrefix, 180);
            Invoke(terminal, "RefreshFrame", false);
            int viewportRows = checked((int)ReadFrame(session).Info.Rows);
            int rowsPerDetent = SystemParameters.WheelScrollLines == -1
                ? viewportRows : Math.Max(0, SystemParameters.WheelScrollLines);
            int bottomFirst = FirstHistoryRow(session, WheelRowPrefix);
            Point point = CellCenter(terminal, 0, 0);

            MouseWheel(terminal, 0, point);
            MouseWheel(terminal, 60, point);
            Assert.That(FirstHistoryRow(session, WheelRowPrefix), Is.EqualTo(bottomFirst),
                "Zero and partial deltas must not scroll a complete row group.");

            MouseWheel(terminal, 60, point);
            int afterOneDetent = FirstHistoryRow(session, WheelRowPrefix);
            Assert.That(afterOneDetent, Is.EqualTo(bottomFirst - rowsPerDetent));

            MouseWheel(terminal, 240, point);
            Assert.That(FirstHistoryRow(session, WheelRowPrefix), Is.EqualTo(afterOneDetent - 2 * rowsPerDetent),
                "A 240 delta must apply two configured wheel steps.");
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void ApplicationWheel_AccumulatesDetentsAndResetsWhenRoutingChanges() {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            EnableMouse(terminal, session, 1000);
            Point point = CellCenter(terminal, 0, 0);

            MouseWheel(terminal, 0, point);
            MouseWheel(terminal, 60, point);
            Assert.That(DrainChildInput(session), Is.EqualTo(Hex("!")),
                "Zero and partial deltas must not emit a wheel event.");

            MouseWheel(terminal, 60, point);
            Assert.That(DrainChildInput(session), Is.EqualTo(Hex("\u001b[<64;1;1M!")),
                "Two half-detents must emit one application wheel event.");

            MouseWheel(terminal, 240, point);
            Assert.That(DrainChildInput(session), Is.EqualTo(Hex("\u001b[<64;1;1M\u001b[<64;1;1M!")),
                "A multi-detent delta must emit every complete application wheel event.");

            MouseWheel(terminal, 60, point);
            Keyboard.SetModifiers(ModifierKeys.Shift);
            MouseWheel(terminal, 60, point);
            Keyboard.SetModifiers(ModifierKeys.None);
            MouseWheel(terminal, 60, point);
            Assert.That(DrainChildInput(session), Is.EqualTo(Hex("!")),
                "A partial detent must not carry across local and application routing.");

            MouseWheel(terminal, 60, point);
            Assert.That(DrainChildInput(session), Is.EqualTo(Hex("\u001b[<64;1;1M!")));
        }
        finally {
            Keyboard.SetModifiers(ModifierKeys.None);
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void CancelMouseInteraction_ReleasesApplicationButtonsAndStopRestartClearsWheelState() {
        TerminalEmulator terminal = CreateTerminal();
        using HwndSource source = Host(terminal);
        try {
            TerminalSession firstSession = Start(terminal);
            EnableMouse(terminal, firstSession, 1000);
            Point point = CellCenter(terminal, 0, 0);
            MouseDown(terminal, MouseButton.Left, point);
            WaitForMouseBytes(firstSession, "\u001b[<0;1;1M");

            Invoke(terminal, "CancelMouseInteraction", true);
            WaitForMouseBytes(firstSession, "\u001b[<0;1;1m");
            Assert.That(Field<Dictionary<MouseButton, uint>>(terminal, "_applicationMouseButtons"), Is.Empty);

            MouseWheel(terminal, 60, point);
            Assert.That(Field<long>(terminal, "_pendingWheelDelta"), Is.EqualTo(60));
            Pump(terminal.StopAsync());
            Assert.That(Field<long>(terminal, "_pendingWheelDelta"), Is.Zero);
            Assert.That(Field<bool?>(terminal, "_wheelReportsToApplication"), Is.Null);

            TerminalSession secondSession = Start(terminal);
            EnableMouse(terminal, secondSession, 1000);
            MouseWheel(terminal, 60, point);
            Assert.That(DrainChildInput(secondSession), Is.EqualTo(Hex("!")),
                "A stopped session's partial wheel input must not affect a restarted session.");
            MouseWheel(terminal, 60, point);
            Assert.That(DrainChildInput(secondSession), Is.EqualTo(Hex("\u001b[<64;1;1M!")));
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void ExitedMouseAwareSession_RetainedOutputStillSupportsLocalWheelAndSelection() {
        TerminalEmulator terminal = CreateTerminal(CommandProcessor() + " /d /q /k");
        using HwndSource source = Host(terminal);
        TaskCompletionSource<uint> exitCode = new();
        terminal.TerminalExited += (sender, args) => exitCode.TrySetResult(args.ExitCode);
        try {
            TerminalSession session = StartCommandProcessor(terminal);
            Write(session, "\u001b[?1000h\u001b[?1006h\u001b[2J\u001b[Hmouse-aware-retained-output");
            WriteHistory(session, "exited-wheel-row-", 120);
            SendExit(session, exitCode);
            WaitFor(() => !terminal.IsRunning);
            Invoke(terminal, "RefreshFrame", false);
            Assert.That(Field<NativeFrame>(terminal, "_frame").MouseTracking, Is.Not.Zero);

            int viewportRows = checked((int)ReadFrame(session).Info.Rows);
            int rowsPerDetent = SystemParameters.WheelScrollLines == -1
                ? viewportRows : Math.Max(0, SystemParameters.WheelScrollLines);
            int beforeWheel = FirstHistoryRow(session, "exited-wheel-row-");
            MouseWheel(terminal, 120, CellCenter(terminal, 0, 0));
            Assert.That(FirstHistoryRow(session, "exited-wheel-row-"), Is.EqualTo(beforeWheel - rowsPerDetent),
                "A completed mouse-aware process must use local history scrolling on its retained frame.");

            session.Engine.Scroll(0, true);
            Write(session, "\u001b[2J\u001b[Hretained-output-marker");
            Invoke(terminal, "RefreshFrame", false);
            MouseDown(terminal, MouseButton.Left, CellCenter(terminal, 0, 0));
            MouseMove(terminal, CellCenter(terminal, "retained-output-marker".Length - 1, 0));
            MouseUp(terminal, MouseButton.Left, CellCenter(terminal, "retained-output-marker".Length - 1, 0));

            Assert.That(session.Engine.SelectedText(), Is.EqualTo("retained-output-marker"),
                "A completed mouse-aware process must still allow local selection from its retained output.");
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    private static TerminalSession StartCommandProcessor(TerminalEmulator terminal) {
        Pump(terminal.StartAsync());
        TerminalSession session = Field<TerminalSession>(terminal, "_session");
        WaitFor(() => ReadText(session).IndexOf(">", StringComparison.Ordinal) >= 0);
        return session;
    }

    private sealed class KeyboardState : IDisposable {
        private readonly byte[] _saved = new byte[256];

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetKeyboardState([Out] byte[] state);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int SetKeyboardState([In] byte[] state);

        internal KeyboardState() {
            Assert.That(GetKeyboardState(_saved), Is.Not.Zero);
            SetModifiers(ModifierKeys.None);
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
}
