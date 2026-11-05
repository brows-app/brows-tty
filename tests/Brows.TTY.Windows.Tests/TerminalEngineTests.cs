using System.Runtime.InteropServices;
using System.Text;
using Brows.Ghostty;

namespace Brows.TTY.Tests;

[TestFixture]
public sealed class TerminalEngineTests {
    private static void Write(TerminalEngine terminal, string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        terminal.Write(bytes, bytes.Length);
    }

    [Test]
    public void NativeFrame_UsesExpectedAbiAndDirtyRows() {
        Assert.That(Marshal.SizeOf<NativeCell>(), Is.EqualTo(24));
        Assert.That(Marshal.SizeOf<NativeFrame>(), Is.EqualTo(48));
        using TerminalEngine terminal = new(20, 4, _ => { });
        TerminalFrame initial = terminal.ReadFrame();
        Assert.That(initial.ChangedRows.Count, Is.EqualTo(4));
        Assert.That(terminal.ReadFrame().ChangedRows, Is.Empty);
        Write(terminal, "\u001b[3;1Hhello");
        TerminalFrame frame = terminal.ReadFrame();
        Assert.That(frame.ChangedRows.Select(row => row.Index), Does.Contain((ushort)2));
        TerminalRow row = frame.ChangedRows.Single(value => value.Index == 2);
        Assert.That(string.Concat(Enumerable.Range(0, 5).Select(row.GetText)), Is.EqualTo("hello"));
        Assert.That(frame.Info.CursorX, Is.EqualTo(5));
        Assert.That(frame.Info.CursorY, Is.EqualTo(2));
        Assert.That(terminal.ReadFrame().ChangedRows, Is.Empty);
    }

    [Test]
    public void Utf8_SplitAcrossWrites_PreservesGraphemesAndWideCells() {
        using TerminalEngine terminal = new(20, 3, _ => { });
        byte[] input = Encoding.UTF8.GetBytes("A\u754ce\u0301\U0001f642");
        foreach (byte value in input) { terminal.Write(new[] { value }, 1); }
        TerminalRow row = terminal.ReadFrame().ChangedRows[0];
        Assert.That(row.GetText(0), Is.EqualTo("A"));
        Assert.That(row.GetText(1), Is.EqualTo("\u754c"));
        Assert.That(row.Cells[1].Width, Is.EqualTo(2));
        Assert.That(row.Cells[2].Width, Is.Zero);
        Assert.That(row.GetText(3), Is.EqualTo("e\u0301"));
        Assert.That(row.GetText(4), Is.EqualTo("\U0001f642"));
    }

    [Test]
    public void LargeGraphemeFrame_CopiesCompleteRowsAndPreservesPreviousSnapshot() {
        using TerminalEngine terminal = new(512, 2, _ => { });
        string grapheme = "e" + new string('\u0301', 8);
        string text = string.Concat(Enumerable.Repeat(grapheme, 512));
        Write(terminal, text);
        TerminalRow original = terminal.ReadFrame().ChangedRows[0];
        Assert.That(original.Text.Length, Is.GreaterThan(8192));
        Assert.That(string.Concat(Enumerable.Range(0, 512).Select(original.GetText)), Is.EqualTo(text));
        Write(terminal, "\r\u001b[2Kshort");
        TerminalRow replacement = terminal.ReadFrame().ChangedRows.Single(row => row.Index == 0);
        Assert.That(string.Concat(Enumerable.Range(0, 5).Select(replacement.GetText)), Is.EqualTo("short"));
        Assert.That(string.Concat(Enumerable.Range(5, 507).Select(replacement.GetText)).Trim(), Is.Empty);
        Assert.That(string.Concat(Enumerable.Range(0, 512).Select(original.GetText)), Is.EqualTo(text),
            "A later native frame must not mutate a published managed snapshot.");
    }

    [Test]
    public void Sgr_ResolvesTrueColorInverseAndDecorations() {
        using TerminalEngine terminal = new(20, 3, _ => { });
        Write(terminal, "\u001b[38;2;10;20;30m\u001b[48;2;40;50;60m\u001b[1;3;4;7mX");
        NativeCell cell = terminal.ReadFrame().ChangedRows[0].Cells[0];
        Assert.That(cell.Foreground, Is.EqualTo(0xff28323cu));
        Assert.That(cell.Background, Is.EqualTo(0xff0a141eu));
        Assert.That(cell.Flags & 0x113, Is.EqualTo(0x113));
    }

    [Test]
    public void ApplicationBackground_UpdatesExistingCellsAndResets() {
        using TerminalEngine terminal = new(20, 3, _ => { });
        Write(terminal, "existing text");
        TerminalFrame initial = terminal.ReadFrame();
        Write(terminal, "\u001b]11;rgb:1212/3434/abab\u0007");
        TerminalFrame changed = terminal.ReadFrame();
        Assert.That(changed.Info.Background, Is.EqualTo(0xff1234abu));
        Assert.That(changed.ChangedRows.Count, Is.EqualTo(3));
        Assert.That(changed.ChangedRows.SelectMany(row => row.Cells).Select(cell => cell.Background),
            Is.All.EqualTo(0xff1234abu), "Existing text and empty cells must use the changed default background.");
        Write(terminal, "\u001b]111\u0007");
        TerminalFrame reset = terminal.ReadFrame();
        Assert.That(reset.Info.Background, Is.EqualTo(initial.Info.Background));
        Assert.That(reset.ChangedRows.Count, Is.EqualTo(3));
        Assert.That(reset.ChangedRows.SelectMany(row => row.Cells).Select(cell => cell.Background),
            Is.All.EqualTo(initial.Info.Background));
    }

    [Test]
    public void ApplicationForegroundAndPalette_RecolorExistingCells() {
        using TerminalEngine terminal = new(20, 3, _ => { });
        Write(terminal, "A\u001b[34mB");
        terminal.ReadFrame();
        Write(terminal, "\u001b]10;rgb:abab/3434/1212\u0007\u001b]4;4;rgb:1212/3434/abab\u0007");
        TerminalFrame changed = terminal.ReadFrame();
        Assert.That(changed.ChangedRows.Count, Is.EqualTo(3));
        TerminalRow row = changed.ChangedRows[0];
        Assert.That(row.Cells[0].Foreground, Is.EqualTo(0xffab3412u));
        Assert.That(row.Cells[1].Foreground, Is.EqualTo(0xff1234abu));
        Assert.That(terminal.ReadFrame().ChangedRows, Is.Empty, "An unchanged theme must not repaint every frame.");
    }

    [Test]
    public void DefaultColorQueries_ReportTheRenderedColors() {
        List<byte> output = new();
        using TerminalEngine terminal = new(20, 3, bytes => output.AddRange(bytes));
        Write(terminal, "\u001b]10;?\u0007\u001b]11;?\u0007");
        string reply = Encoding.UTF8.GetString(output.ToArray());
        Assert.That(reply, Does.Contain("10;rgb:ffff/ffff/ffff"));
        Assert.That(reply, Does.Contain("11;rgb:0000/0000/0000"));
        TerminalFrame frame = terminal.ReadFrame();
        Assert.That(frame.Info.Foreground, Is.EqualTo(0xffffffffu));
        Assert.That(frame.Info.Background, Is.EqualTo(0xff000000u));
    }

    [Test]
    public void AlternateScreen_RestoresPrimaryContents() {
        using TerminalEngine terminal = new(20, 3, _ => { });
        Write(terminal, "primary\u001b[?1049h\u001b[Halternate");
        TerminalRow alternate = terminal.ReadFrame().ChangedRows[0];
        Assert.That(string.Concat(Enumerable.Range(0, 9).Select(alternate.GetText)), Is.EqualTo("alternate"));
        Write(terminal, "\u001b[?1049l");
        TerminalRow primary = terminal.ReadFrame().ChangedRows[0];
        Assert.That(string.Concat(Enumerable.Range(0, 7).Select(primary.GetText)), Is.EqualTo("primary"));
    }

    [Test]
    public void Resize_ReflowsTextAndChangesDimensions() {
        using TerminalEngine terminal = new(10, 4, _ => { });
        Write(terminal, "abcdefghijklmnop");
        terminal.Resize(5, 6, 8, 16);
        TerminalFrame frame = terminal.ReadFrame();
        Assert.That(frame.Info.Columns, Is.EqualTo(5));
        Assert.That(frame.Info.Rows, Is.EqualTo(6));
        string text = string.Concat(frame.ChangedRows.SelectMany(row => Enumerable.Range(0, 5).Select(row.GetText)));
        Assert.That(text, Does.StartWith("abcdefghijklmnop"));
    }

    [Test]
    public void Input_UsesApplicationCursorAndBracketedPasteModes() {
        List<byte> output = new();
        using TerminalEngine terminal = new(20, 3, bytes => output.AddRange(bytes));
        terminal.Key(TerminalKey.ArrowUp, 0, 1, "");
        Assert.That(Encoding.UTF8.GetString(output.ToArray()), Is.EqualTo("\u001b[A"));
        output.Clear();
        Write(terminal, "\u001b[?1h\u001b[?2004h");
        terminal.Key(TerminalKey.ArrowUp, 0, 1, "");
        terminal.Paste("hello\nworld");
        Assert.That(Encoding.UTF8.GetString(output.ToArray()),
            Is.EqualTo("\u001bOA\u001b[200~hello\nworld\u001b[201~"));
    }

    [Test]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(32)]
    [TestCase(64)]
    [TestCase(2048)]
    public void Input_EncodesUnicodeAcrossStackAndPooledBuffers(int repetitions) {
        List<byte> output = new();
        using TerminalEngine terminal = new(20, 3, bytes => output.AddRange(bytes));
        string text = string.Concat(Enumerable.Repeat("\u00e9\u754c\U0001f642", repetitions));
        terminal.Key(TerminalKey.Unidentified, 0, 1, text);
        Assert.That(Encoding.UTF8.GetString(output.ToArray()), Is.EqualTo(text));
        output.Clear();
        terminal.Paste(text);
        Assert.That(Encoding.UTF8.GetString(output.ToArray()), Is.EqualTo(text));
        output.Clear();
        terminal.Paste("after");
        Assert.That(Encoding.UTF8.GetString(output.ToArray()), Is.EqualTo("after"),
            "A later short input must not include stale bytes from a previous buffer.");
    }

    [Test]
    public void Queries_ReturnCursorReportAndTitle() {
        List<byte> output = new();
        using TerminalEngine terminal = new(20, 3, bytes => output.AddRange(bytes));
        Write(terminal, "abc\u001b[6n\u001b]2;test title\u0007");
        Assert.That(Encoding.UTF8.GetString(output.ToArray()), Is.EqualTo("\u001b[1;4R"));
        Assert.That(terminal.ReadFrame().Title, Is.EqualTo("test title"));
    }

    [Test]
    public void Title_UnchangedFramesReuseDecodedString() {
        using TerminalEngine terminal = new(20, 3, _ => { });
        Assert.That(terminal.ReadFrame().Title, Is.Empty);
        string title = "shell \u00e9\u754c\U0001f642 " + new string('x', 1000);
        Write(terminal, "\u001b]2;" + title + "\u0007");
        string cached = terminal.ReadFrame().Title;
        Assert.That(cached, Is.EqualTo(title));
        Assert.That(terminal.ReadFrame().Title, Is.SameAs(cached),
            "An idle frame must reuse the decoded title.");
        Write(terminal, "unrelated output");
        Assert.That(terminal.ReadFrame(true).Title, Is.SameAs(cached),
            "Forced and dirty frames must reuse an unchanged title.");
        Write(terminal, "\u001b]2;" + title + "\u0007");
        Assert.That(terminal.ReadFrame().Title, Is.SameAs(cached),
            "An identical OSC title must not replace the cached string.");
    }

    [Test]
    public void Title_EqualLengthUnicodeChangesPreservePreviousSnapshot() {
        using TerminalEngine terminal = new(20, 3, _ => { });
        Write(terminal, "\u001b]2;caf\u00e9\u0007");
        TerminalFrame original = terminal.ReadFrame();
        Write(terminal, "\u001b]2;caf\u00f1\u0007");
        TerminalFrame changed = terminal.ReadFrame();
        Assert.That(changed.Title, Is.EqualTo("caf\u00f1"),
            "Equal-length titles with different UTF-8 bytes must update.");
        Assert.That(original.Title, Is.EqualTo("caf\u00e9"),
            "Later native title writes must not change an earlier frame.");
        Assert.That(terminal.ReadFrame().Title, Is.SameAs(changed.Title));
    }

    [Test]
    public void Title_ShrinkClearAndRestoreDiscardStaleBytes() {
        using TerminalEngine terminal = new(20, 3, _ => { });
        Write(terminal, "\u001b]2;long title \u00e9\u754c\U0001f642\u0007");
        TerminalFrame original = terminal.ReadFrame();
        Write(terminal, "\u001b]2;short\u0007");
        TerminalFrame shortened = terminal.ReadFrame();
        Assert.That(shortened.Title, Is.EqualTo("short"));
        Write(terminal, "\u001b]2;\u0007");
        Assert.That(terminal.ReadFrame().Title, Is.Empty);
        Assert.That(terminal.ReadFrame().Title, Is.Empty);
        Write(terminal, "\u001b]2;short\u0007");
        TerminalFrame restored = terminal.ReadFrame();
        Assert.That(restored.Title, Is.EqualTo("short"));
        Assert.That(terminal.ReadFrame().Title, Is.SameAs(restored.Title));
        Assert.That(shortened.Title, Is.EqualTo("short"));
        Assert.That(original.Title, Is.EqualTo("long title \u00e9\u754c\U0001f642"));
    }

    [Test]
    public void SynchronizedOutput_PreservesFrameUntilReleased() {
        using TerminalEngine terminal = new(20, 3, _ => { });
        Write(terminal, "before\u001b[?2026h\rAFTER!");
        TerminalRow held = terminal.ReadFrame().ChangedRows[0];
        Assert.That(string.Concat(Enumerable.Range(0, 6).Select(held.GetText)), Is.EqualTo("before"));
        Write(terminal, "\u001b[?2026l");
        TerminalRow released = terminal.ReadFrame().ChangedRows[0];
        Assert.That(string.Concat(Enumerable.Range(0, 6).Select(released.GetText)), Is.EqualTo("AFTER!"));
    }

    [Test]
    public void InvalidDimensionsAndDisposedUse_AreRejected() {
        Assert.That(() => new TerminalEngine(0, 3, _ => { }), Throws.InvalidOperationException);
        TerminalEngine terminal = new(20, 3, _ => { });
        Assert.That(() => terminal.Resize(0, 3, 8, 16), Throws.InvalidOperationException);
        Assert.That(() => terminal.Write(new byte[1], 2), Throws.TypeOf<ArgumentOutOfRangeException>());
        terminal.Dispose();
        terminal.Dispose();
        Assert.That(() => terminal.ReadFrame(), Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void Selection_ExtractsUnicodeAndCanBeCleared() {
        using TerminalEngine terminal = new(20, 3, _ => { });
        Write(terminal, "A\u754ce\u0301Z");
        terminal.Select(1, 0, 3, 0);
        Assert.That(terminal.SelectedText(), Is.EqualTo("\u754ce\u0301"));
        TerminalRow row = terminal.ReadFrame().ChangedRows[0];
        Assert.That(row.Cells[0].Selected, Is.Zero);
        Assert.That(row.Cells[1].Selected, Is.EqualTo(1));
        Assert.That(row.Cells[3].Selected, Is.EqualTo(1));
        terminal.Select(0, 0, 0, 0, true);
        Assert.That(terminal.SelectedText(), Is.Empty);
    }

    [Test]
    public void Scrollback_ReturnsEarlierLinesAndCanReturnToPrompt() {
        using TerminalEngine terminal = new(20, 3, _ => { });
        Write(terminal, "one\r\ntwo\r\nthree\r\nfour\r\nfive");
        terminal.Scroll(-2);
        TerminalRow history = terminal.ReadFrame().ChangedRows[0];
        Assert.That(string.Concat(Enumerable.Range(0, 3).Select(history.GetText)), Is.EqualTo("one"));
        terminal.Scroll(0, true);
        TerminalRow active = terminal.ReadFrame().ChangedRows[0];
        Assert.That(string.Concat(Enumerable.Range(0, 5).Select(active.GetText)), Is.EqualTo("three"));
    }

    [Test]
    public void FocusMouseAndControlKeys_UseNativeEncoding() {
        List<byte> output = new();
        using TerminalEngine terminal = new(20, 3, bytes => output.AddRange(bytes));
        terminal.Focus(true);
        Assert.That(output, Is.Empty);
        Write(terminal, "\u001b[?1004h\u001b[?1000h\u001b[?1006h");
        terminal.Resize(20, 3, 8, 16);
        terminal.Focus(true);
        terminal.Focus(false);
        terminal.Key(TerminalKey.C, 2, 1, "");
        terminal.Mouse(0, 1, 0, 16, 16, true);
        Assert.That(Encoding.UTF8.GetString(output.ToArray()), Is.EqualTo("\u001b[I\u001b[O\u0003\u001b[<0;3;2M"));
    }
}
