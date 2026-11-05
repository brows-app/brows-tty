namespace Brows.Windows;

/// <summary>
/// Describes the exit of the terminal's root process after its final output was drained.
/// </summary>
public sealed class TerminalExitedEventArgs : EventArgs {
    /// <summary>
    /// Gets the native process exit code.
    /// </summary>
    public uint ExitCode { get; }

    /// <summary>
    /// Creates process exit event data.
    /// </summary>
    public TerminalExitedEventArgs(uint exitCode) {
        ExitCode = exitCode;
    }
}
