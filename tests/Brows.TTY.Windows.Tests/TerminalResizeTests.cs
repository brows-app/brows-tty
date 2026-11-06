using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Brows;
using Brows.Ghostty;
using Brows.Win32;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalResizeTests {
    private const string ShellPrompt = "resize-ready>";
    private const string HistoryPrefix = "resize-row-";
    private const int HistoryRows = 60;
    private SynchronizationContext? _previousContext;

    private static IEnumerable<TestCaseData> NoCellCases() {
        yield return new TestCaseData(true, true).SetName("ZeroWidth_PreservesPreviousGrid");
        yield return new TestCaseData(true, false).SetName("SubCellWidth_PreservesPreviousGrid");
        yield return new TestCaseData(false, true).SetName("ZeroHeight_PreservesPreviousGrid");
        yield return new TestCaseData(false, false).SetName("SubCellHeight_PreservesPreviousGrid");
    }

    private static string CommandLine() {
        string commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return "\"" + commandProcessor + "\" /d /q /k prompt resize-ready$G";
    }

    private static HwndSource Host(TerminalEmulator terminal) {
        HwndSourceParameters parameters = new("Terminal resize integration test") {
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

    private static TerminalEmulator HostSample(double rootWidth, out FrameworkElement root, out HwndSource source) {
        MainWindow window = new();
        TerminalEmulator terminal = (TerminalEmulator)window.FindName("Console");
        terminal.AutoStart = false;
        terminal.CommandLine = CommandLine();
        root = (FrameworkElement)window.Content;
        root.Resources = window.Resources;
        window.Content = null;
        HwndSourceParameters parameters = new("Terminal sample resize integration test") {
            WindowStyle = unchecked((int)0x80000000),
            Width = 1000,
            Height = 640
        };
        source = new HwndSource(parameters) { RootVisual = root };
        ArrangeSample(root, rootWidth);
        return terminal;
    }

    private static void ArrangeSize(TerminalEmulator terminal, double width, double height) {
        terminal.Width = width;
        terminal.Height = height;
        terminal.Measure(new Size(width, height));
        terminal.Arrange(new Rect(0, 0, width, height));
        terminal.UpdateLayout();
    }

    private static void ArrangeSample(FrameworkElement root, double width) {
        root.Width = width;
        root.Height = 640;
        root.Measure(new Size(width, 640));
        root.Arrange(new Rect(0, 0, width, 640));
        root.UpdateLayout();
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
        return (T)typeof(TerminalEmulator).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
    }

    private static void ConfigureSize(TerminalEmulator terminal) {
        MethodInfo? method = typeof(TerminalEmulator).GetMethod("ConfigureSize",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        method!.Invoke(terminal, null);
    }

    private static TerminalSession Start(TerminalEmulator terminal) {
        Pump(terminal.StartAsync());
        TerminalSession session = Field<TerminalSession>(terminal, "_session");
        WaitFor(() => ReadText(session).IndexOf(ShellPrompt, StringComparison.Ordinal) >= 0);
        return session;
    }

    private static void WriteHistory(TerminalSession session) {
        session.Engine.Paste("for /L %i in (1,1," + HistoryRows.ToString(CultureInfo.InvariantCulture)
            + ") do @echo " + HistoryPrefix + "%i");
        session.Key(0x0d, 0, 1, "");
        WaitFor(() => {
            string text = ReadText(session);
            bool lastHistoryRowWasWritten = text.IndexOf(
                HistoryPrefix + HistoryRows.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) >= 0;
            bool promptWasPrinted = text.IndexOf(ShellPrompt, StringComparison.Ordinal) >= 0;
            return lastHistoryRowWasWritten && promptWasPrinted;
        });
    }

    private static void PressEnter(TerminalEmulator terminal, HwndSource source) {
        terminal.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
        terminal.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) {
            RoutedEvent = Keyboard.PreviewKeyUpEvent
        });
    }

    private static int CountOccurrences(string text, string value) {
        int count = 0;
        int index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0) {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static string ReadText(TerminalSession session) {
        TerminalFrame frame = session.Engine.ReadFrame(true);
        return string.Join("\n", frame.ChangedRows.OrderBy(row => row.Index).Select(row =>
            string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText))));
    }

    private static void AssertRestoredHistoryAndPrompt(TerminalEmulator terminal, HwndSource source,
                                                       TerminalSession session) {
        for (int press = 0; press < 2; press++) {
            int priorPromptCount = CountOccurrences(ReadText(session), ShellPrompt);
            PressEnter(terminal, source);
            WaitFor(() => CountOccurrences(ReadText(session), ShellPrompt) > priorPromptCount);
        }

        int promptsBeforeMarker = CountOccurrences(ReadText(session), ShellPrompt);
        session.Engine.Paste("echo resize-after-collapse-check");
        session.Key(0x0d, 0, 1, "");
        WaitFor(() => {
            string text = ReadText(session);
            bool markerWasWritten = text.IndexOf("resize-after-collapse-check", StringComparison.Ordinal) >= 0;
            bool promptWasPrinted = CountOccurrences(text, ShellPrompt) > promptsBeforeMarker;
            return markerWasWritten && promptWasPrinted;
        });
        string[] visibleRows = ReadText(session).Split('\n').Select(row => row.Trim()).ToArray();
        int visibleHistoryRows = CountOccurrences(string.Join("\n", visibleRows), HistoryPrefix);
        Assert.That(visibleHistoryRows, Is.GreaterThanOrEqualTo(5),
            "The restored viewport must still show multiple earlier output rows.");
        TerminalFrame promptFrame = session.Engine.ReadFrame(true);
        TerminalRow cursorRow = promptFrame.ChangedRows.Single(row => row.Index == (int)promptFrame.Info.CursorY);
        string cursorText = string.Concat(Enumerable.Range(0, cursorRow.Cells.Length).Select(cursorRow.GetText))
            .TrimEnd();
        Assert.That(cursorText, Is.EqualTo(ShellPrompt),
            "After repeated Enter, CMD must keep the prompt on its own aligned cursor row.");

        session.Engine.Scroll(-32767);
        string[] oldestRows = ReadText(session).Split('\n').Select(row => row.Trim()).ToArray();
        Assert.That(oldestRows, Does.Contain(HistoryPrefix + "1"),
            "Restoring a terminal that was fully collapsed must retain its earlier scrollback rows.");
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

    [TestCaseSource(nameof(NoCellCases))]
    public void NoFullCellFits_PreservesPreviousConptyGrid(bool shrinkingWidth, bool zeroSize) {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = CommandLine() };
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            ushort originalColumns = Field<ushort>(terminal, "_columns");
            ushort originalRows = Field<ushort>(terminal, "_lines");
            double originalWidth = terminal.ActualWidth;
            double originalHeight = terminal.ActualHeight;
            TerminalDrawing drawing = Field<TerminalDrawing>(terminal, "_drawing");
            double cellWidth = drawing.CellWidth;
            double cellHeight = drawing.CellHeight;
            bool shrinkingHeight = !shrinkingWidth;
            double width = originalWidth;
            double height = originalHeight;
            if (shrinkingWidth) {
                width = zeroSize ? 0 : cellWidth / 2;
            }
            if (shrinkingHeight) {
                height = zeroSize ? 0 : cellHeight / 2;
            }

            ArrangeSize(terminal, width, height);
            ConfigureSize(terminal);
            TerminalFrame frame = session.Engine.ReadFrame(true);

            Assert.That(terminal.ActualWidth, Is.EqualTo(width).Within(0.01));
            Assert.That(terminal.ActualHeight, Is.EqualTo(height).Within(0.01));
            Assert.That(Field<ushort>(terminal, "_columns"), Is.EqualTo(originalColumns));
            Assert.That(Field<ushort>(terminal, "_lines"), Is.EqualTo(originalRows));
            Assert.That(frame.Info.Columns, Is.EqualTo((uint)originalColumns));
            Assert.That(frame.Info.Rows, Is.EqualTo((uint)originalRows));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void UnmeasuredStart_UsesDefaultGrid() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = CommandLine() };
        try {
            TerminalSession session = Start(terminal);
            TerminalFrame frame = session.Engine.ReadFrame(true);
            Assert.That(terminal.ActualWidth, Is.Zero);
            Assert.That(terminal.ActualHeight, Is.Zero);
            Assert.That(Field<ushort>(terminal, "_columns"), Is.EqualTo(80));
            Assert.That(Field<ushort>(terminal, "_lines"), Is.EqualTo(24));
            Assert.That(frame.Info.Columns, Is.EqualTo(80));
            Assert.That(frame.Info.Rows, Is.EqualTo(24));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void ValidOneAndTwoColumnGrids_ResizeNormally() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = CommandLine() };
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            double cellWidth = Field<TerminalDrawing>(terminal, "_drawing").CellWidth;
            double height = terminal.ActualHeight;
            ArrangeSize(terminal, cellWidth * 2.5, height);
            ConfigureSize(terminal);
            TerminalFrame twoColumnFrame = session.Engine.ReadFrame(true);
            Assert.That(Field<ushort>(terminal, "_columns"), Is.EqualTo(2));
            Assert.That(twoColumnFrame.Info.Columns, Is.EqualTo(2));

            ArrangeSize(terminal, cellWidth * 1.5, height);
            ConfigureSize(terminal);
            TerminalFrame oneColumnFrame = session.Engine.ReadFrame(true);
            Assert.That(Field<ushort>(terminal, "_columns"), Is.EqualTo(1));
            Assert.That(oneColumnFrame.Info.Columns, Is.EqualTo(1));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void DirectZeroWidthCollapse_RestoresHistoryAndPromptAfterRepeatedEnter() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = CommandLine() };
        using HwndSource source = Host(terminal);
        try {
            TerminalSession session = Start(terminal);
            WriteHistory(session);
            double originalWidth = terminal.ActualWidth;
            double originalHeight = terminal.ActualHeight;
            ushort originalColumns = Field<ushort>(terminal, "_columns");

            ArrangeSize(terminal, 0, originalHeight);
            ConfigureSize(terminal);
            Assert.That(terminal.ActualWidth, Is.Zero);
            Assert.That(Field<ushort>(terminal, "_columns"), Is.EqualTo(originalColumns));

            ArrangeSize(terminal, originalWidth, originalHeight);
            ConfigureSize(terminal);
            Assert.That(Field<ushort>(terminal, "_columns"), Is.EqualTo(originalColumns));
            AssertRestoredHistoryAndPrompt(terminal, source, session);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void SampleGradualCollapse_RetainsUsefulGridHistoryAndPrompt() {
        TerminalEmulator terminal = HostSample(1000, out FrameworkElement root, out HwndSource source);
        using (source) {
            try {
                TerminalSession session = Start(terminal);
                WriteHistory(session);
                double originalWidth = root.ActualWidth;
                double terminalCellWidth = Field<TerminalDrawing>(terminal, "_drawing").CellWidth;
                ushort originalColumns = Field<ushort>(terminal, "_columns");
                double[] hostWidths = { 750, 600, 500, 400, 350, 280, 200 };
                foreach (double hostWidth in hostWidths) {
                    ArrangeSample(root, hostWidth);
                    ConfigureSize(terminal);
                    Assert.That(terminal.ActualWidth, Is.GreaterThanOrEqualTo(terminalCellWidth * 30),
                        "Shrinking the sample window must leave the terminal a useful width.");
                    Assert.That(Field<ushort>(terminal, "_columns"), Is.GreaterThanOrEqualTo(30));
                }

                ArrangeSample(root, originalWidth);
                ConfigureSize(terminal);
                Assert.That(Field<ushort>(terminal, "_columns"), Is.EqualTo(originalColumns));
                AssertRestoredHistoryAndPrompt(terminal, source, session);
                Assert.That(terminal.LastError, Is.Null);
            }
            finally {
                Pump(terminal.StopAsync());
            }
        }
    }
}
