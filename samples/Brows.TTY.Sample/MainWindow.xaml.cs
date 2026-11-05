using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Brows.Windows;

namespace Brows;

public partial class MainWindow : Window {
    private bool _closePending;
    private bool _allowClose;
    private bool _windowClosed;

    private async void RestartClick(object sender, RoutedEventArgs args) {
        try {
            await Console.StopAsync();
            await Console.StartAsync();
            Status.Text = "Running";
            Console.Focus();
        }
        catch (Exception exception) { Status.Text = exception.Message; }
    }

    private void ProcessExited(object sender, TerminalExitedEventArgs args) {
        Status.Text = "Process exited with code " + args.ExitCode;
    }

    private void TerminalFailed(object sender, TerminalFailedEventArgs args) {
        Status.Text = args.Exception.Message;
    }

    private async Task StopAndCloseAsync() {
        try { await Console.StopAsync(); }
        catch (Exception exception) { Status.Text = exception.Message; }
        finally { _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(CompleteClose)); }
    }

    private void CompleteClose() {
        if (_windowClosed) { return; }
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs args) {
        base.OnClosing(args);
        if (args.Cancel) {
            if (_allowClose) {
                _allowClose = false;
                _closePending = false;
            }
            return;
        }
        if (_windowClosed) { return; }
        if (_allowClose) {
            _allowClose = false;
            return;
        }
        if (_closePending) {
            args.Cancel = true;
            return;
        }
        args.Cancel = true;
        _closePending = true;
        _ = StopAndCloseAsync();
    }

    protected override void OnClosed(EventArgs args) {
        _windowClosed = true;
        _allowClose = false;
        _closePending = false;
        base.OnClosed(args);
    }

    public MainWindow() {
        InitializeComponent();
        Loaded += (sender, args) => Console.Focus();
    }
}
