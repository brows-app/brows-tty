namespace Brows.Ghostty;

internal sealed class TerminalFrame {
    internal NativeFrame Info { get; }
    internal IReadOnlyList<TerminalRow> ChangedRows { get; }
    internal string Title { get; }

    internal TerminalFrame(NativeFrame info, IReadOnlyList<TerminalRow> changedRows, string title) {
        Info = info;
        ChangedRows = changedRows;
        Title = title;
    }
}
