using Brows.Windows.Controls;
using System.Windows;

namespace Brows.Windows;

/// <summary>
/// Describes a terminal startup, rendering, input, or process transport failure.
/// </summary>
public sealed class TerminalFailedEventArgs : RoutedEventArgs {
    /// <inheritdoc />
    protected override void InvokeEventHandler(Delegate genericHandler, object genericTarget) {
        if (genericHandler is EventHandler<TerminalFailedEventArgs> handler) {
            handler(genericTarget, this);
            return;
        }
        base.InvokeEventHandler(genericHandler, genericTarget);
    }

    /// <summary>
    /// Gets the failure.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Creates terminal failure event data.
    /// </summary>
    public TerminalFailedEventArgs(Exception exception) : base(TerminalEmulator.TerminalFailedEvent) {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }
}
