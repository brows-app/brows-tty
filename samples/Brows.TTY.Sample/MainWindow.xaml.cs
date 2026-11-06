using Brows.Windows;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Brows;

sealed partial class MainWindow : Window {
    private bool _closePending;
    private bool _allowClose;
    private bool _windowClosed;
    private bool _restartInProgress;

    private async void RestartCommandPromptClick(object sender, RoutedEventArgs args) {
        string commandLine = "\"" + Path.Combine(Environment.SystemDirectory, "cmd.exe") + "\"";
        await RestartShellAsync(commandLine);
    }

    private async void RestartPowerShellClick(object sender, RoutedEventArgs args) {
        await RestartShellAsync("powershell.exe -NoLogo");
    }

    private async Task RestartShellAsync(string commandLine) {
        bool restartIsBlocked = _closePending || _windowClosed || _restartInProgress;
        if (restartIsBlocked) {
            return;
        }
        _restartInProgress = true;
        RestartCommandPromptButton.IsEnabled = false;
        RestartPowerShellButton.IsEnabled = false;
        try {
            Status.Text = "Restarting shell…";
            await Console.StopAsync();
            bool windowIsClosing = _closePending || _windowClosed;
            if (windowIsClosing) {
                return;
            }
            Console.CommandLine = commandLine;
            await Console.StartAsync();
            bool windowIsOpen = !_closePending && !_windowClosed;
            if (!windowIsOpen) {
                return;
            }
            Status.Text = "Running";
            Console.Focus();
        }
        catch (Exception exception) {
            bool windowIsOpen = !_closePending && !_windowClosed;
            if (windowIsOpen) {
                Status.Text = exception.Message;
            }
        }
        finally {
            _restartInProgress = false;
            bool windowIsOpen = !_closePending && !_windowClosed;
            if (windowIsOpen) {
                RestartCommandPromptButton.IsEnabled = true;
                RestartPowerShellButton.IsEnabled = true;
            }
        }
    }

    private void TerminalExited(object sender, TerminalExitedEventArgs args) {
        Status.Text = "Process exited with code " + args.ExitCode;
    }

    private void TerminalFailed(object sender, TerminalFailedEventArgs args) {
        Status.Text = args.Exception.Message;
    }

    private async Task StopAndCloseAsync() {
        try {
            await Console.StopAsync();
        }
        catch (Exception exception) {
            Status.Text = exception.Message;
        }
        finally {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(CompleteClose));
        }
    }

    private void CompleteClose() {
        if (_windowClosed) {
            return;
        }
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
        if (_windowClosed) {
            return;
        }
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
