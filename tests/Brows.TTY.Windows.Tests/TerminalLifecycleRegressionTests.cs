using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brows.Ghostty;
using Brows.Win32;
using Brows.Windows;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalLifecycleRegressionTests {
    private const string SynchronizedOutputExitCommand =
        "powershell.exe -NoLogo -NoProfile -NonInteractive -Command \"$escape=[char]27; "
        + "[Console]::Write($escape+'[?2026h'+$escape+']11;rgb:1234/5678/9abc'+[char]7+"
        + "'sync-output-final'); exit 7\"";

    private SynchronizationContext? _previousContext;

    private static string Shell => "\"" + Environment.GetEnvironmentVariable("ComSpec") + "\"";

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

    private static HwndSource Host(TerminalEmulator terminal) {
        HwndSourceParameters parameters = new("Terminal lifecycle regression test") {
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

    private static void Write(TerminalEngine engine, string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        engine.Write(bytes, bytes.Length);
    }

    private static string ReadFrameText(TerminalFrame frame) {
        List<string> lines = new();
        foreach (TerminalRow row in frame.ChangedRows) {
            string line = string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText));
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    private static async Task WaitForText(TerminalSession session, string text) {
        Stopwatch elapsed = Stopwatch.StartNew();
        string actual = "";
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10)) {
            actual = ReadFrameText(session.Engine.ReadFrame(true));
            bool textIsPresent = actual.IndexOf(text, StringComparison.Ordinal) >= 0;
            if (textIsPresent) {
                return;
            }
            session.CheckError();
            await Task.Delay(20);
        }
        Assert.Fail("Expected terminal output '" + text + "', received '" + actual + "'.");
    }

    private static async Task<uint> Complete(TerminalSession session) {
        Task completed = await Task.WhenAny(session.Completion, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.That(completed, Is.SameAs(session.Completion), "The shell did not exit within 15 seconds.");
        return await session.Completion;
    }

    private static T Field<T>(TerminalEmulator terminal, string name) {
        FieldInfo? field = typeof(TerminalEmulator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, "Expected private field " + name + ".");
        return (T)field!.GetValue(terminal)!;
    }

    private static string RetainedText(TerminalEmulator terminal) {
        TerminalRow?[] snapshots = Field<TerminalRow?[]>(terminal, "_rowSnapshots");
        List<string> lines = new();
        foreach (TerminalRow? row in snapshots) {
            if (row is null) {
                continue;
            }
            string line = string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText));
            lines.Add(line);
        }
        return string.Join("\n", lines);
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
    [TestCase(0u, 16u, "zero width")]
    [TestCase(8u, 0u, "zero height")]
    [TestCase(uint.MaxValue, 16u, "screen width overflow")]
    [TestCase(8u, uint.MaxValue, "screen height overflow")]
    public void InvalidCellMetrics_PreserveGridPixelMouseAndSynchronizedOutputState(
        uint cellWidth, uint cellHeight, string invalidGeometry) {
        List<byte> output = new();
        using TerminalEngine engine = new(20, 3, bytes => output.AddRange(bytes));
        engine.Resize(20, 3, 8, 16);
        Write(engine, "\u001b[?1000h\u001b[?1006h");
        engine.Mouse(0, 1, 0, 16, 16, true);
        byte[] expectedMouseReport = output.ToArray();
        Assert.That(Encoding.UTF8.GetString(expectedMouseReport), Is.EqualTo("\u001b[<0;3;2M"));
        output.Clear();
        Write(engine, "before");
        engine.ReadFrame();
        Write(engine, "\u001b[?2026h\rAFTER!");
        engine.ReadFrame();

        Assert.That(() => engine.Resize(40, 10, cellWidth, cellHeight), Throws.InvalidOperationException,
            "Rejected " + invalidGeometry + " must not mutate terminal state.");
        Assert.That(output, Is.Empty, "A rejected resize must not report changed pixel dimensions.");
        TerminalFrame held = engine.ReadFrame();
        Assert.That(held.Info.Columns, Is.EqualTo(20));
        Assert.That(held.Info.Rows, Is.EqualTo(3));
        Assert.That(held.ChangedRows, Is.Empty, "A rejected resize must not release synchronized output.");

        engine.Mouse(0, 1, 0, 16, 16, true);
        Assert.That(output.ToArray(), Is.EqualTo(expectedMouseReport),
            "The mouse encoder must retain its prior cell and screen geometry.");
        Write(engine, "\u001b[?2026l");
        TerminalFrame released = engine.ReadFrame(true);
        Assert.That(released.Info.Columns, Is.EqualTo(20));
        Assert.That(released.Info.Rows, Is.EqualTo(3));
        Assert.That(ReadFrameText(released), Does.Contain("AFTER!"));
    }

    [Test]
    [TestCase(0, 3, "columns")]
    [TestCase(32768, 3, "columns")]
    [TestCase(20, 0, "rows")]
    [TestCase(20, 32768, "rows")]
    public async Task SessionResize_RejectsInvalidGridBeforeGhosttyOrConptyChanges(
        int columns, int rows, string parameterName) {
        using TerminalSession session = new(Shell + " /d /q /k prompt $G", null, 20, 3);
        TerminalFrame before = session.Engine.ReadFrame(true);
        Assert.That(before.Info.Columns, Is.EqualTo(20));
        Assert.That(before.Info.Rows, Is.EqualTo(3));

        Assert.That(() => session.Resize((ushort)columns, (ushort)rows, 8, 16),
            Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo(parameterName));
        TerminalFrame after = session.Engine.ReadFrame(true);
        Assert.That(after.Info.Columns, Is.EqualTo(before.Info.Columns));
        Assert.That(after.Info.Rows, Is.EqualTo(before.Info.Rows));

        session.Engine.Paste("echo resize-ok");
        session.Key(0x0d, 0, 1, "");
        await WaitForText(session, "resize-ok");
        session.Engine.Paste("exit /b 0");
        session.Key(0x0d, 0, 1, "");
        Assert.That(await Complete(session), Is.Zero);
    }

    [Test]
    public void CompleteOutput_ReleasesHeldSnapshotAndCapturesFinalTextAndColor() {
        using TerminalEngine engine = new(20, 3, bytes => { });
        engine.Resize(20, 3, 8, 16);
        engine.ReadFrame(true);
        Write(engine, "before");
        engine.ReadFrame(true);
        Write(engine, "\u001b[?2026h\r\u001b]11;rgb:1234/5678/9abc\u0007SYNC-FINAL");

        TerminalFrame held = engine.ReadFrame();

        Assert.That(held.Info.Background, Is.Not.EqualTo(0xff12569au));
        Assert.That(held.ChangedRows, Is.Empty);
        engine.CompleteOutput();
        TerminalFrame final = engine.ReadFrame(true);

        Assert.That(final.Info.Background, Is.EqualTo(0xff12569au));
        Assert.That(ReadFrameText(final), Does.Contain("SYNC-FINAL"));
    }

    [Test]
    public void ObserveExit_CompletesSynchronizedOutputBeforeRaisingFinalFrameEvent() {
        TerminalEmulator terminal = new() {
            AutoStart = false,
            CommandLine = SynchronizedOutputExitCommand
        };
        using HwndSource source = Host(terminal);
        TerminalExitedEventArgs? exitEvent = null;
        bool finalTextWasVisible = false;
        bool finalBackgroundWasVisible = false;
        terminal.TerminalExited += (sender, args) => {
            exitEvent = args;
            finalTextWasVisible = RetainedText(terminal).IndexOf("sync-output-final", StringComparison.Ordinal) >= 0;
            finalBackgroundWasVisible = Field<NativeFrame>(terminal, "_frame").Background == 0xff12569au;
        };
        try {
            Pump(terminal.StartAsync());
            WaitFor(() => exitEvent is not null);

            Assert.That(exitEvent!.ExitCode, Is.EqualTo(7));
            Assert.That(finalTextWasVisible, Is.True,
                "The synchronized final output must be retained before TerminalExited is raised.");
            Assert.That(finalBackgroundWasVisible, Is.True,
                "The synchronized OSC background change must be applied before TerminalExited is raised.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }
}
