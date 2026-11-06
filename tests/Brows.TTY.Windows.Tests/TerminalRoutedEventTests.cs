using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Brows.Windows;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalRoutedEventTests {
    private SynchronizationContext? _previousContext;

    private static HwndSource Host(Grid parent, TerminalEmulator terminal) {
        parent.Children.Add(terminal);
        parent.Width = 800;
        parent.Height = 400;
        HwndSourceParameters parameters = new("Terminal routed event integration test") {
            WindowStyle = unchecked((int)0x80000000),
            Width = 800,
            Height = 400
        };
        HwndSource source = new(parameters) { RootVisual = parent };
        parent.Measure(new Size(800, 400));
        parent.Arrange(new Rect(0, 0, 800, 400));
        parent.UpdateLayout();
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

    private static void ReportFailure(TerminalEmulator terminal, Exception exception) {
        MethodInfo? method = typeof(TerminalEmulator).GetMethod("ReportFailure",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "The terminal failure reporter should remain available to its handlers.");
        method!.Invoke(terminal, new object[] { exception });
    }

    private static string ExitCommand() {
        string commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return "\"" + commandProcessor + "\" /d /q /c exit /b 37";
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
    public void EventArgs_InitializeRoutedEventAndValidatePayloads() {
        TerminalExitedEventArgs exited = new(73);
        InvalidOperationException failure = new("routed failure payload");
        TerminalFailedEventArgs failed = new(failure);

        Assert.That(exited.ExitCode, Is.EqualTo(73));
        Assert.That(exited.RoutedEvent, Is.SameAs(TerminalEmulator.TerminalExitedEvent));
        Assert.That(failed.Exception, Is.SameAs(failure));
        Assert.That(failed.RoutedEvent, Is.SameAs(TerminalEmulator.TerminalFailedEvent));
        Assert.That(() => new TerminalFailedEventArgs(null!), Throws.ArgumentNullException);
    }

    [Test]
    public void TerminalExitedEvent_BubblesWithPayloadToTypedAndBaseHandlers() {
        Grid parent = new();
        TerminalEmulator terminal = new() { AutoStart = false };
        using HwndSource source = Host(parent, terminal);
        TerminalExitedEventArgs expected = new(73);
        object? childSender = null;
        object? parentTypedSender = null;
        object? parentRoutedSender = null;
        TerminalExitedEventArgs? childArgs = null;
        TerminalExitedEventArgs? parentTypedArgs = null;
        RoutedEventArgs? parentRoutedArgs = null;
        terminal.TerminalExited += (sender, args) => {
            childSender = sender;
            childArgs = args;
        };
        EventHandler<TerminalExitedEventArgs> typedHandler = (sender, args) => {
            parentTypedSender = sender;
            parentTypedArgs = args;
        };
        RoutedEventHandler routedHandler = (sender, args) => {
            parentRoutedSender = sender;
            parentRoutedArgs = args;
        };
        parent.AddHandler(TerminalEmulator.TerminalExitedEvent, typedHandler);
        parent.AddHandler(TerminalEmulator.TerminalExitedEvent, routedHandler);

        terminal.RaiseEvent(expected);

        Assert.That(TerminalEmulator.TerminalExitedEvent.RoutingStrategy, Is.EqualTo(RoutingStrategy.Bubble));
        Assert.That(childSender, Is.SameAs(terminal));
        Assert.That(childArgs, Is.SameAs(expected));
        Assert.That(parentTypedSender, Is.SameAs(parent));
        Assert.That(parentTypedArgs, Is.SameAs(expected));
        Assert.That(parentRoutedSender, Is.SameAs(parent));
        Assert.That(parentRoutedArgs, Is.SameAs(expected));
        Assert.That(expected.RoutedEvent, Is.SameAs(TerminalEmulator.TerminalExitedEvent));
        Assert.That(expected.Source, Is.SameAs(terminal));
        Assert.That(expected.OriginalSource, Is.SameAs(terminal));
        Assert.That(expected.ExitCode, Is.EqualTo(73));
    }

    [Test]
    public void TerminalExitedEvent_HandledSuppressesNormalParentAndNotHandledEventsToo() {
        Grid parent = new();
        TerminalEmulator terminal = new() { AutoStart = false };
        using HwndSource source = Host(parent, terminal);
        int childCalls = 0;
        int normalParentCalls = 0;
        int handledParentCalls = 0;
        terminal.TerminalExited += (sender, args) => {
            childCalls++;
            args.Handled = true;
        };
        EventHandler<TerminalExitedEventArgs> normalParentHandler = (sender, args) => normalParentCalls++;
        RoutedEventHandler handledParentHandler = (sender, args) => handledParentCalls++;
        parent.AddHandler(TerminalEmulator.TerminalExitedEvent, normalParentHandler);
        parent.AddHandler(TerminalEmulator.TerminalExitedEvent, handledParentHandler, true);

        TerminalExitedEventArgs args = new(19);
        terminal.RaiseEvent(args);

        Assert.That(childCalls, Is.EqualTo(1));
        Assert.That(normalParentCalls, Is.Zero);
        Assert.That(handledParentCalls, Is.EqualTo(1));
        Assert.That(args.Handled, Is.True);
    }

    [Test]
    public void TerminalExitedEvent_RemovingTypedHandlerStopsNotifications() {
        Grid parent = new();
        TerminalEmulator terminal = new() { AutoStart = false };
        using HwndSource source = Host(parent, terminal);
        int calls = 0;
        EventHandler<TerminalExitedEventArgs> handler = (sender, args) => calls++;
        terminal.TerminalExited += handler;
        terminal.TerminalExited -= handler;

        terminal.RaiseEvent(new TerminalExitedEventArgs(11));

        Assert.That(calls, Is.Zero);
    }

    [Test]
    public void TerminalFailedEvent_BubblesWithPayloadToTypedAndBaseHandlers() {
        Grid parent = new();
        TerminalEmulator terminal = new() { AutoStart = false };
        using HwndSource source = Host(parent, terminal);
        InvalidOperationException failure = new("routed failure payload");
        TerminalFailedEventArgs expected = new(failure);
        object? childSender = null;
        object? parentTypedSender = null;
        object? parentRoutedSender = null;
        TerminalFailedEventArgs? childArgs = null;
        TerminalFailedEventArgs? parentTypedArgs = null;
        RoutedEventArgs? parentRoutedArgs = null;
        terminal.TerminalFailed += (sender, args) => {
            childSender = sender;
            childArgs = args;
        };
        EventHandler<TerminalFailedEventArgs> typedHandler = (sender, args) => {
            parentTypedSender = sender;
            parentTypedArgs = args;
        };
        RoutedEventHandler routedHandler = (sender, args) => {
            parentRoutedSender = sender;
            parentRoutedArgs = args;
        };
        parent.AddHandler(TerminalEmulator.TerminalFailedEvent, typedHandler);
        parent.AddHandler(TerminalEmulator.TerminalFailedEvent, routedHandler);

        terminal.RaiseEvent(expected);

        Assert.That(TerminalEmulator.TerminalFailedEvent.RoutingStrategy, Is.EqualTo(RoutingStrategy.Bubble));
        Assert.That(childSender, Is.SameAs(terminal));
        Assert.That(childArgs, Is.SameAs(expected));
        Assert.That(parentTypedSender, Is.SameAs(parent));
        Assert.That(parentTypedArgs, Is.SameAs(expected));
        Assert.That(parentRoutedSender, Is.SameAs(parent));
        Assert.That(parentRoutedArgs, Is.SameAs(expected));
        Assert.That(expected.RoutedEvent, Is.SameAs(TerminalEmulator.TerminalFailedEvent));
        Assert.That(expected.Source, Is.SameAs(terminal));
        Assert.That(expected.OriginalSource, Is.SameAs(terminal));
        Assert.That(expected.Exception, Is.SameAs(failure));
    }

    [Test]
    public void TerminalFailedEvent_HandledSuppressesNormalParentAndNotHandledEventsToo() {
        Grid parent = new();
        TerminalEmulator terminal = new() { AutoStart = false };
        using HwndSource source = Host(parent, terminal);
        int childCalls = 0;
        int normalParentCalls = 0;
        int handledParentCalls = 0;
        terminal.TerminalFailed += (sender, args) => {
            childCalls++;
            args.Handled = true;
        };
        EventHandler<TerminalFailedEventArgs> normalParentHandler = (sender, args) => normalParentCalls++;
        RoutedEventHandler handledParentHandler = (sender, args) => handledParentCalls++;
        parent.AddHandler(TerminalEmulator.TerminalFailedEvent, normalParentHandler);
        parent.AddHandler(TerminalEmulator.TerminalFailedEvent, handledParentHandler, true);

        TerminalFailedEventArgs args = new(new InvalidOperationException("routed failure"));
        terminal.RaiseEvent(args);

        Assert.That(childCalls, Is.EqualTo(1));
        Assert.That(normalParentCalls, Is.Zero);
        Assert.That(handledParentCalls, Is.EqualTo(1));
        Assert.That(args.Handled, Is.True);
    }

    [Test]
    public void TerminalFailedEvent_RemovingTypedHandlerStopsNotifications() {
        Grid parent = new();
        TerminalEmulator terminal = new() { AutoStart = false };
        using HwndSource source = Host(parent, terminal);
        int calls = 0;
        EventHandler<TerminalFailedEventArgs> handler = (sender, args) => calls++;
        terminal.TerminalFailed += handler;
        terminal.TerminalFailed -= handler;

        terminal.RaiseEvent(new TerminalFailedEventArgs(new InvalidOperationException("removed handler")));

        Assert.That(calls, Is.Zero);
    }

    [Test]
    public void TerminalExitedNotification_BubblesFromRealConPtyExit() {
        Grid parent = new();
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = ExitCommand() };
        using HwndSource source = Host(parent, terminal);
        TaskCompletionSource<TerminalExitedEventArgs> notification = new();
        object? sender = null;
        bool isRunningAtNotification = true;
        EventHandler<TerminalExitedEventArgs> handler = (eventSender, args) => {
            sender = eventSender;
            isRunningAtNotification = terminal.IsRunning;
            args.Handled = true;
            notification.TrySetResult(args);
        };
        parent.AddHandler(TerminalEmulator.TerminalExitedEvent, handler);
        try {
            Pump(terminal.StartAsync());
            Pump(notification.Task);

            Assert.That(sender, Is.SameAs(parent));
            Assert.That(notification.Task.Result.ExitCode, Is.EqualTo(37));
            Assert.That(notification.Task.Result.Source, Is.SameAs(terminal));
            Assert.That(notification.Task.Result.OriginalSource, Is.SameAs(terminal));
            Assert.That(isRunningAtNotification, Is.False);
            Assert.That(terminal.IsRunning, Is.False);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void TerminalFailedNotification_BubblesFromTerminalFailureReporter() {
        Grid parent = new();
        TerminalEmulator terminal = new() { AutoStart = false };
        using HwndSource source = Host(parent, terminal);
        InvalidOperationException failure = new("routed failure");
        TerminalFailedEventArgs? received = null;
        object? sender = null;
        Exception? lastErrorAtNotification = null;
        EventHandler<TerminalFailedEventArgs> handler = (eventSender, args) => {
            sender = eventSender;
            received = args;
            lastErrorAtNotification = terminal.LastError;
            args.Handled = true;
        };
        parent.AddHandler(TerminalEmulator.TerminalFailedEvent, handler);

        ReportFailure(terminal, failure);

        Assert.That(sender, Is.SameAs(parent));
        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Exception, Is.SameAs(failure));
        Assert.That(received.RoutedEvent, Is.SameAs(TerminalEmulator.TerminalFailedEvent));
        Assert.That(received.Source, Is.SameAs(terminal));
        Assert.That(received.OriginalSource, Is.SameAs(terminal));
        Assert.That(lastErrorAtNotification, Is.SameAs(failure));
        Assert.That(terminal.LastError, Is.SameAs(failure));
    }
}
