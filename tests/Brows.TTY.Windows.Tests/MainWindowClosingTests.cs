using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Brows;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class MainWindowClosingTests {
    private SynchronizationContext? _previousContext;
    private Dispatcher? _dispatcher;
    private readonly List<Exception> _dispatcherExceptions = new();
    private readonly List<MainWindow> _windows = new();

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

    private static TerminalEmulator GetTerminal(MainWindow window) {
        return (TerminalEmulator)window.FindName("Console")!;
    }

    private MainWindow CreateWindow() {
        MainWindow window = new();
        GetTerminal(window).AutoStart = false;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStyle = WindowStyle.None;
        window.Opacity = 0;
        window.Left = -32000;
        window.Top = -32000;
        _windows.Add(window);
        window.Show();
        return window;
    }

    private static SemaphoreSlim GetLifecycle(TerminalEmulator terminal) {
        return (SemaphoreSlim)typeof(TerminalEmulator).GetField("_lifecycle",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
    }

    private static void DrainDispatcher() {
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => {
        }, DispatcherPriority.ApplicationIdle).Task);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args) {
        _dispatcherExceptions.Add(args.Exception);
        args.Handled = true;
    }

    [SetUp]
    public void SetUp() {
        _dispatcherExceptions.Clear();
        _windows.Clear();
        _previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        _dispatcher = Dispatcher.CurrentDispatcher;
        _dispatcher.UnhandledException += OnDispatcherUnhandledException;
    }

    [TearDown]
    public void TearDown() {
        try {
            foreach (MainWindow window in _windows) {
                TerminalEmulator terminal = GetTerminal(window);
                if (terminal.IsRunning) {
                    Pump(terminal.StopAsync());
                }
                if (!window.IsVisible) {
                    continue;
                }
                TaskCompletionSource<bool> closed = new();
                window.Closed += (sender, args) => closed.TrySetResult(true);
                window.Close();
                Pump(closed.Task);
            }
        }
        finally {
            _windows.Clear();
            _dispatcher!.UnhandledException -= OnDispatcherUnhandledException;
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }
    }

    [Test]
    public void CloseWithoutSession_DefersCloseUntilInitialClosingCallbackReturns() {
        MainWindow window = CreateWindow();
        TaskCompletionSource<bool> closed = new();
        int closingCount = 0;
        int closingCountBeforeCloseReturned = 0;
        bool closeReturned = false;
        window.Closing += (sender, args) => {
            closingCount++;
            if (!closeReturned) {
                closingCountBeforeCloseReturned++;
            }
        };
        window.Closed += (sender, args) => closed.TrySetResult(true);

        window.Close();
        closeReturned = true;

        Assert.That(closingCountBeforeCloseReturned, Is.EqualTo(1),
            "The synchronous StopAsync path must not call Close reentrantly.");
        Assert.That(closed.Task.IsCompleted, Is.False,
            "The window must remain open until the dispatcher handles the deferred close.");
        Pump(closed.Task);
        Assert.That(closingCount, Is.EqualTo(2), "The final close should raise Closing exactly once more.");
    }

    [Test]
    public void ClosingSubscriberCanCancelInitialCloseAndRetry() {
        MainWindow window = CreateWindow();
        TerminalEmulator terminal = GetTerminal(window);
        terminal.CommandLine = "\"" + Environment.GetEnvironmentVariable("ComSpec") + "\" /d /q";
        TaskCompletionSource<bool> closed = new();
        int closingCount = 0;
        bool cancelFirstClose = true;
        window.Closing += (sender, args) => {
            closingCount++;
            if (cancelFirstClose) {
                args.Cancel = true;
                cancelFirstClose = false;
            }
        };
        window.Closed += (sender, args) => closed.TrySetResult(true);

        try {
            Pump(terminal.StartAsync());
            Assert.That(terminal.IsRunning, Is.True);

            window.Close();
            DrainDispatcher();

            Assert.That(closingCount, Is.EqualTo(1));
            Assert.That(closed.Task.IsCompleted, Is.False);
            Assert.That(terminal.IsRunning, Is.True,
                "A subscriber's initial cancellation must prevent terminal cleanup.");
            window.Close();
            Assert.That(closed.Task.IsCompleted, Is.False);
            Pump(closed.Task);
            Assert.That(terminal.IsRunning, Is.False,
                "A later close request must stop the session before the window closes.");
            Assert.That(closingCount, Is.EqualTo(3),
                "A later close request should clean up and then raise the deferred Closing event.");
        }
        finally {
            if (terminal.IsRunning) {
                Pump(terminal.StopAsync());
            }
        }
    }

    [Test]
    public void RepeatedCloseWhileStopIsPending_QueuesOneFinalClose() {
        MainWindow window = CreateWindow();
        TerminalEmulator terminal = GetTerminal(window);
        SemaphoreSlim lifecycle = GetLifecycle(terminal);
        TaskCompletionSource<bool> closed = new();
        TaskCompletionSource<bool> deferredCloseCancelled = new();
        int closingCount = 0;
        window.Closing += (sender, args) => {
            closingCount++;
            if (closingCount == 4) {
                args.Cancel = true;
                deferredCloseCancelled.TrySetResult(true);
            }
        };
        window.Closed += (sender, args) => closed.TrySetResult(true);
        lifecycle.Wait();
        int closingCountBeforeRelease = 0;
        bool closedBeforeRelease = false;
        try {
            window.Close();
            window.Close();
            window.Close();
            closingCountBeforeRelease = closingCount;
            closedBeforeRelease = closed.Task.IsCompleted;
        }
        finally {
            lifecycle.Release();
        }

        Pump(deferredCloseCancelled.Task);
        DrainDispatcher();
        Assert.That(closingCountBeforeRelease, Is.EqualTo(3), "Each explicit request raises Closing once.");
        Assert.That(closedBeforeRelease, Is.False,
            "The window must stay open while StopAsync is waiting for the lifecycle gate.");
        Assert.That(closingCount, Is.EqualTo(4), "One deferred Close should reach the subscriber.");
        Assert.That(closed.Task.IsCompleted, Is.False,
            "Canceling the single deferred close must leave the window open.");
        window.Close();
        Pump(closed.Task);
        Assert.That(closingCount, Is.EqualTo(6), "A later close request must run one new stop and final close.");
    }

    [Test]
    public void CancelledDeferredClose_AllowsRestartAndStopsTheNewSessionOnRetry() {
        MainWindow window = CreateWindow();
        TerminalEmulator terminal = GetTerminal(window);
        terminal.AutoStart = false;
        terminal.CommandLine = "\"" + Environment.GetEnvironmentVariable("ComSpec") + "\" /d /q";
        TaskCompletionSource<bool> deferredCloseCancelled = new();
        TaskCompletionSource<bool> closed = new();
        int closingCount = 0;
        window.Closing += (sender, args) => {
            closingCount++;
            if (closingCount == 2) {
                args.Cancel = true;
                deferredCloseCancelled.TrySetResult(true);
            }
        };
        window.Closed += (sender, args) => closed.TrySetResult(true);
        try {
            window.Close();
            Pump(deferredCloseCancelled.Task);
            Assert.That(closed.Task.IsCompleted, Is.False,
                "A subscriber's cancellation of the deferred close must keep the window open.");
            Pump(terminal.StartAsync());
            Assert.That(terminal.IsRunning, Is.True,
                "The still-open window must be able to restart its terminal.");

            window.Close();
            Pump(closed.Task);

            Assert.That(terminal.IsRunning, Is.False, "A later close request must stop the restarted terminal.");
            Assert.That(closingCount, Is.EqualTo(4), "The retry should perform one stop and one final close.");
        }
        finally {
            if (terminal.IsRunning) {
                Pump(terminal.StopAsync());
            }
        }
    }

    [Test]
    public void StopFailure_IsReportedAndDoesNotEscapeClosingCallback() {
        MainWindow window = CreateWindow();
        TerminalEmulator terminal = GetTerminal(window);
        TextBlock status = (TextBlock)window.FindName("Status")!;
        GetLifecycle(terminal).Dispose();
        string initialStatus = status.Text;
        string expectedError = string.Empty;
        try {
            terminal.StopAsync().GetAwaiter().GetResult();
        }
        catch (ObjectDisposedException exception) {
            expectedError = exception.Message;
        }
        Assert.That(expectedError, Is.Not.Empty, "The forced stop failure must be an ObjectDisposedException.");
        TaskCompletionSource<bool> closed = new();
        window.Closed += (sender, args) => closed.TrySetResult(true);

        Assert.That(() => window.Close(), Throws.Nothing,
            "A StopAsync failure must not escape the async closing work onto the dispatcher.");
        Pump(closed.Task);
        DrainDispatcher();

        Assert.That(status.Text, Is.Not.EqualTo(initialStatus),
            "The sample must update its status for the stop failure.");
        Assert.That(status.Text, Is.EqualTo(expectedError), "The sample must report the actual stop exception.");
        Assert.That(_dispatcherExceptions, Is.Empty, "No StopAsync exception may escape onto the dispatcher.");
    }
}
