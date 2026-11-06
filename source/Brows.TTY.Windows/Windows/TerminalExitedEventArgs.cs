using Brows.Windows.Controls;
using System.Windows;

namespace Brows.Windows;

/// <summary>
/// Describes the exit of the terminal's root process after its final output was drained.
/// </summary>
public sealed class TerminalExitedEventArgs : RoutedEventArgs {
    /// <inheritdoc />
    protected override void InvokeEventHandler(Delegate genericHandler, object genericTarget) {
        if (genericHandler is EventHandler<TerminalExitedEventArgs> handler) {
            handler(genericTarget, this);
            return;
        }
        base.InvokeEventHandler(genericHandler, genericTarget);
    }

    /// <summary>
    /// Gets the native process exit code.
    /// </summary>
    public uint ExitCode { get; }

    /// <summary>
    /// Creates process exit event data.
    /// </summary>
    public TerminalExitedEventArgs(uint exitCode) : base(TerminalEmulator.TerminalExitedEvent) {
        ExitCode = exitCode;
    }
}
