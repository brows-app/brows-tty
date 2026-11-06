using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brows.Ghostty;
using Brows.Win32;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalPerformanceTests {
    private const string IdleCommand =
        "powershell.exe -NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 600\"";
    private SynchronizationContext? _previousContext;

    private sealed class RenderCountingTerminal : Terminal {
        internal int RenderCount { get; private set; }

        protected override void OnRender(DrawingContext drawingContext) {
            RenderCount++;
            base.OnRender(drawingContext);
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
        if (!task.IsCompleted) { Dispatcher.PushFrame(frame); }
        timeout.Stop();
        Assert.That(task.IsCompleted, Is.True, "Dispatcher operation exceeded 15 seconds.");
        task.GetAwaiter().GetResult();
    }

    private static HwndSource Host(RenderCountingTerminal terminal) {
        HwndSourceParameters parameters = new("Terminal performance integration test") {
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

    private static void ArrangeSize(RenderCountingTerminal terminal, double width, double height) {
        terminal.Width = width;
        terminal.Height = height;
        terminal.Measure(new Size(width, height));
        terminal.Arrange(new Rect(0, 0, width, height));
        terminal.UpdateLayout();
    }

    private static TerminalSession Start(RenderCountingTerminal terminal) {
        Pump(terminal.StartAsync());
        Field<DispatcherTimer>(terminal, "_timer").Stop();
        return Field<TerminalSession>(terminal, "_session");
    }

    private static void Write(TerminalEngine engine, string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        engine.Write(bytes, bytes.Length);
    }

    private static T Field<T>(Terminal terminal, string name) {
        return (T)typeof(Terminal).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
    }

    private static void SetField<T>(Terminal terminal, string name, T value) {
        typeof(Terminal).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(terminal, value);
    }

    private static void Invoke(Terminal terminal, string name, params object?[] arguments) {
        MethodInfo? method = typeof(Terminal).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Expected terminal operation " + name + ".");
        method!.Invoke(terminal, arguments);
    }

    private static void QueueSelection(Terminal terminal, Point endpoint) {
        Invoke(terminal, "QueueSelection", endpoint);
    }

    private static void BeginSelection(Terminal terminal, TerminalSession session) {
        session.Engine.Select(0, 0, 0, 0);
        SetField(terminal, "_selectionStart", new Point(0, 0));
        SetField(terminal, "_selectionEnd", new Point(0, 0));
    }

    private static void RefreshFrame(Terminal terminal) {
        Invoke(terminal, "RefreshFrame", false);
    }

    private static void DrainDispatcher() {
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task);
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
    public void SelectionMoves_AreCoalescedUntilTheNextTickAndSkipTheSameCell() {
        RenderCountingTerminal terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            Write(session.Engine, "ABCDEFGHIJ");
            BeginSelection(terminal, session);

            QueueSelection(terminal, new Point(0, 0));
            Assert.That(Field<bool>(terminal, "_selectionPending"), Is.False,
                "The mouse-down cell is already selected.");
            QueueSelection(terminal, new Point(2, 0));
            QueueSelection(terminal, new Point(2, 0));
            QueueSelection(terminal, new Point(4, 0));
            Assert.That(session.Engine.SelectedText(), Is.EqualTo("A"),
                "Mouse moves must not read and rebuild a frame immediately.");
            Assert.That(Field<bool>(terminal, "_selectionPending"), Is.True);

            Invoke(terminal, "OnTick", null, EventArgs.Empty);

            Assert.That(session.Engine.SelectedText(), Is.EqualTo("ABCDE"),
                "One render tick must apply the latest cell endpoint.");
            Assert.That(Field<bool>(terminal, "_selectionPending"), Is.False);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void MouseUp_FlushesTheFinalPendingSelectionEndpoint() {
        RenderCountingTerminal terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            Write(session.Engine, "ABCDEFGHIJ");
            BeginSelection(terminal, session);
            QueueSelection(terminal, new Point(4, 0));

            terminal.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                RoutedEvent = Mouse.MouseUpEvent
            });

            Assert.That(session.Engine.SelectedText(), Is.EqualTo("ABCDE"));
            Assert.That(Field<bool>(terminal, "_selectionPending"), Is.False);
            Assert.That(Field<Point?>(terminal, "_selectionStart"), Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void CopySelection_FlushesTheFinalPendingSelectionEndpoint() {
        RenderCountingTerminal terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            Write(session.Engine, "ABCDEFGHIJ");
            BeginSelection(terminal, session);
            QueueSelection(terminal, new Point(4, 0));

            terminal.CopySelection();

            Assert.That(Clipboard.GetText(), Is.EqualTo("ABCDE"));
            Assert.That(Field<bool>(terminal, "_selectionPending"), Is.False);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void LocalScroll_FlushesPendingSelectionBeforeEndingTheDrag() {
        RenderCountingTerminal terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            Write(session.Engine, "ABCDEFGHIJ");
            BeginSelection(terminal, session);
            QueueSelection(terminal, new Point(4, 0));

            terminal.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) {
                RoutedEvent = Mouse.MouseWheelEvent
            });

            Assert.That(session.Engine.SelectedText(), Is.EqualTo("ABCDE"));
            Assert.That(Field<bool>(terminal, "_selectionPending"), Is.False);
            Assert.That(Field<Point?>(terminal, "_selectionStart"), Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void StopAsync_ClearsPendingSelectionBeforeTheNextSession() {
        RenderCountingTerminal terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            TerminalSession firstSession = Start(terminal);
            Write(firstSession.Engine, "ABCDEFGHIJ");
            BeginSelection(terminal, firstSession);
            QueueSelection(terminal, new Point(4, 0));

            Pump(terminal.StopAsync());
            Assert.That(Field<bool>(terminal, "_selectionPending"), Is.False);
            Assert.That(Field<Point?>(terminal, "_selectionStart"), Is.Null);

            TerminalSession secondSession = Start(terminal);
            Write(secondSession.Engine, "12345");
            Invoke(terminal, "OnTick", null, EventArgs.Empty);
            Assert.That(secondSession.Engine.SelectedText(), Is.Empty,
                "A queued endpoint from the previous session must not affect a restart.");
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void SubCellSizeChange_DoesNotForceAFullFrame() {
        RenderCountingTerminal terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            Start(terminal);
            SetField(terminal, "_forceFrame", false);
            ushort columns = Field<ushort>(terminal, "_columns");
            double actualWidth = terminal.ActualWidth;
            double cellWidth = Field<TerminalDrawing>(terminal, "_drawing").CellWidth;
            double slack = actualWidth - columns * cellWidth;
            bool canShrinkWithinCell = slack > 0.01;
            double availableWidth = canShrinkWithinCell ? slack : cellWidth - slack;
            double delta = Math.Min(cellWidth / 4, availableWidth / 2);
            double newWidth = canShrinkWithinCell ? actualWidth - delta : actualWidth + delta;
            DrawingGroup? firstRow = Field<DrawingGroup?[]>(terminal, "_rows").First(row => row is not null);

            ArrangeSize(terminal, newWidth, terminal.ActualHeight);
            Assert.That(terminal.ActualWidth, Is.EqualTo(newWidth).Within(0.01));
            Invoke(terminal, "ConfigureSize");
            Invoke(terminal, "RefreshFrame", false);

            Assert.That(Field<ushort>(terminal, "_columns"), Is.EqualTo(columns));
            Assert.That(Field<bool>(terminal, "_forceFrame"), Is.False,
                "A pixel-only render-size notification must not resize Ghostty or force a full snapshot.");
            Assert.That(Field<DrawingGroup?[]>(terminal, "_rows")[0], Is.SameAs(firstRow),
                "A sub-cell layout change must retain the existing row drawing.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void GridSizeChange_StillResizesAndRebuildsRows() {
        RenderCountingTerminal terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            Start(terminal);
            ushort originalColumns = Field<ushort>(terminal, "_columns");
            double cellWidth = Field<TerminalDrawing>(terminal, "_drawing").CellWidth;
            DrawingGroup? firstRow = Field<DrawingGroup?[]>(terminal, "_rows").First(row => row is not null);

            ArrangeSize(terminal, terminal.ActualWidth - 2 * cellWidth, terminal.ActualHeight);
            Invoke(terminal, "ConfigureSize");
            ushort resizedColumns = Field<ushort>(terminal, "_columns");
            Invoke(terminal, "RefreshFrame", false);

            Assert.That(resizedColumns, Is.LessThan(originalColumns));
            Assert.That(Field<NativeFrame>(terminal, "_frame").Columns, Is.EqualTo(resizedColumns));
            Assert.That(Field<DrawingGroup?[]>(terminal, "_rows")[0], Is.Not.SameAs(firstRow));
            Assert.That(Field<bool>(terminal, "_forceFrame"), Is.False);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void ExpiredBlinkDeadline_DoesNotInvalidateHiddenOrNonBlinkingCursor() {
        RenderCountingTerminal terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            string[] inactiveCursorModes = { "\u001b[?25l", "\u001b[?25h\u001b[?12l" };
            foreach (string sequence in inactiveCursorModes) {
                Write(session.Engine, sequence);
                RefreshFrame(terminal);
                DrainDispatcher();
                NativeFrame frame = Field<NativeFrame>(terminal, "_frame");
                bool cursorIsInactive = sequence == "\u001b[?25l"
                    ? frame.CursorVisible == 0
                    : frame.CursorBlink == 0;
                Assert.That(cursorIsInactive, Is.True);
                int renderCount = terminal.RenderCount;
                SetField(terminal, "_lastBlink", DateTime.UtcNow.AddSeconds(-2));

                RefreshFrame(terminal);
                DrainDispatcher();

                Assert.That(terminal.RenderCount, Is.EqualTo(renderCount),
                    "A hidden or non-blinking cursor must not trigger elapsed-time rendering.");
            }
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void ExpiredBlinkDeadline_StillInvalidatesAVisibleBlinkingCursor() {
        RenderCountingTerminal terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            Write(session.Engine, "\u001b[?25h\u001b[?12h");
            RefreshFrame(terminal);
            DrainDispatcher();
            NativeFrame frame = Field<NativeFrame>(terminal, "_frame");
            Assert.That(frame.CursorVisible, Is.EqualTo(1));
            Assert.That(frame.CursorBlink, Is.EqualTo(1));
            int renderCount = terminal.RenderCount;
            SetField(terminal, "_lastBlink", DateTime.UtcNow.AddSeconds(-2));

            RefreshFrame(terminal);
            DrainDispatcher();

            Assert.That(terminal.RenderCount, Is.GreaterThan(renderCount),
                "Visible blinking cursors must continue to repaint on their blink interval.");
        }
        finally { Pump(terminal.StopAsync()); }
    }
}
