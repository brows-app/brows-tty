namespace Brows.Windows;

/// <summary>
/// Describes a terminal startup, rendering, input, or process transport failure.
/// </summary>
public sealed class TerminalFailedEventArgs : EventArgs {
    /// <summary>
    /// Gets the failure.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Creates terminal failure event data.
    /// </summary>
    public TerminalFailedEventArgs(Exception exception) {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }
}
