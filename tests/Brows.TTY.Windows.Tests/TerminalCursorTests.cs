using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brows.Ghostty;
using Brows.Win32;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalCursorTests {
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
        HwndSourceParameters parameters = new("Terminal cursor integration test") {
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
            if (condition()) { completion.TrySetResult(true); }
        };
        timer.Start();
        try { Pump(completion.Task); }
        finally { timer.Stop(); }
    }

    private static void DrainDispatcher() {
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task);
    }

    private static void Write(TerminalEngine engine, string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        engine.Write(bytes, bytes.Length);
    }

    private static void RefreshFrame(RenderCountingTerminal terminal) {
        typeof(Terminal).GetMethod("RefreshFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(terminal, new object[] { false });
    }

    private static T Field<T>(Terminal terminal, string name) {
        return (T)typeof(Terminal).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
    }

    private static void SetField<T>(Terminal terminal, string name, T value) {
        typeof(Terminal).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(terminal, value);
    }

    private static byte[] RenderPixels(RenderCountingTerminal terminal) {
        terminal.UpdateLayout();
        RenderTargetBitmap bitmap = new(800, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(terminal);
        byte[] pixels = new byte[800 * 400 * 4];
        bitmap.CopyPixels(pixels, 800 * 4, 0);
        return pixels;
    }

    private static void VerifyCursorUpdate(
        string baselineSequence,
        string updateSequence,
        Action<NativeFrame, NativeFrame> assertTransition,
        bool hideBlinkingCursor) {
        RenderCountingTerminal terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            Pump(terminal.StartAsync());
            DispatcherTimer timer = Field<DispatcherTimer>(terminal, "_timer");
            timer.Stop();
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            if (baselineSequence.Length != 0) { Write(session.Engine, baselineSequence); }
            SetField(terminal, "_lastBlink", DateTime.UtcNow.AddDays(1));
            RefreshFrame(terminal);
            WaitFor(() => terminal.RenderCount > 0);
            DrainDispatcher();

            SetField(terminal, "_cursorOn", true);
            byte[] pixelsBefore = RenderPixels(terminal);
            NativeFrame before = Field<NativeFrame>(terminal, "_frame");
            DrawingGroup?[] retainedRows = (DrawingGroup?[])Field<DrawingGroup?[]>(terminal, "_rows").Clone();
            Assert.That(before.CursorVisible, Is.EqualTo(1), "The initial cursor must be visible for this check.");
            int renderCount = terminal.RenderCount;
            if (hideBlinkingCursor) { SetField(terminal, "_cursorOn", false); }

            Write(session.Engine, updateSequence);
            SetField(terminal, "_lastBlink", DateTime.UtcNow.AddDays(1));
            RefreshFrame(terminal);
            NativeFrame after = Field<NativeFrame>(terminal, "_frame");
            assertTransition(before, after);
            DrawingGroup?[] currentRows = Field<DrawingGroup?[]>(terminal, "_rows");
            Assert.That(currentRows, Has.Length.EqualTo(retainedRows.Length));
            for (int index = 0; index < retainedRows.Length; index++) {
                Assert.That(currentRows[index], Is.SameAs(retainedRows[index]),
                    "A cursor-only update must retain the row drawing at index " + index + ".");
            }
            Assert.That(timer.IsEnabled, Is.False, "The 16 ms control timer must not repaint the cursor update.");

            WaitFor(() => terminal.RenderCount > renderCount);
            byte[] pixelsAfter = RenderPixels(terminal);
            Assert.That(pixelsAfter, Is.Not.EqualTo(pixelsBefore),
                "A cursor-only VT update must change the rendered WPF output.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    private static void AssertUnrelatedFrameStateUnchanged(NativeFrame before, NativeFrame after) {
        Assert.That(after.Columns, Is.EqualTo(before.Columns));
        Assert.That(after.Rows, Is.EqualTo(before.Rows));
        Assert.That(after.Background, Is.EqualTo(before.Background));
        Assert.That(after.Foreground, Is.EqualTo(before.Foreground));
        Assert.That(after.CursorX, Is.EqualTo(before.CursorX));
        Assert.That(after.CursorY, Is.EqualTo(before.CursorY));
        Assert.That(after.CursorStyle, Is.EqualTo(before.CursorStyle));
        Assert.That(after.CursorVisible, Is.EqualTo(before.CursorVisible));
        Assert.That(after.MouseTracking, Is.EqualTo(before.MouseTracking));
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
    public void CursorColorOnlyChange_InvalidatesAndRepaintsImmediately() {
        VerifyCursorUpdate("", "\u001b]12;rgb:ffff/0000/0000\u0007", (before, after) => {
            AssertUnrelatedFrameStateUnchanged(before, after);
            Assert.That(after.CursorColor, Is.EqualTo(0xffff0000u));
            Assert.That(after.CursorColor, Is.Not.EqualTo(before.CursorColor));
            Assert.That(after.CursorBlink, Is.EqualTo(before.CursorBlink));
        }, false);
    }

    [Test]
    public void CursorBlinkOnlyChange_InvalidatesAndRepaintsImmediately() {
        VerifyCursorUpdate("\u001b[?12l", "\u001b[?12h", (before, after) => {
            AssertUnrelatedFrameStateUnchanged(before, after);
            Assert.That(before.CursorBlink, Is.Zero);
            Assert.That(after.CursorBlink, Is.EqualTo(1));
            Assert.That(after.CursorColor, Is.EqualTo(before.CursorColor));
        }, true);
    }
}
