using System.Text;

namespace Brows.Ghostty;

internal sealed class TerminalRow {
    internal ushort Index { get; }
    internal NativeCell[] Cells { get; }
    internal byte[] Text { get; }

    internal TerminalRow(ushort index, NativeCell[] cells, byte[] text) {
        Index = index;
        Cells = cells;
        Text = text;
    }

    internal string GetText(int column) {
        NativeCell cell = Cells[column];
        return Encoding.UTF8.GetString(Text, checked((int)cell.TextOffset), checked((int)cell.TextLength));
    }
}
