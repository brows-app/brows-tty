using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brows.Ghostty;
using Brows.Win32;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalRenderingRegressionTests {
    private const string IdleCommand =
        "powershell.exe -NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 600\"";

    private SynchronizationContext? _previousContext;

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

    private static void WaitFor(Func<bool> condition) {
        TaskCompletionSource<bool> completion = new();
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (sender, args) => {
            if (condition()) {
                completion.TrySetResult(true);
            }
        };
        timer.Start();
        try {
            Pump(completion.Task);
        }
        finally {
            timer.Stop();
        }
    }

    private static HwndSource Host(TerminalEmulator terminal) {
        HwndSourceParameters parameters = new("Terminal rendering integration test") {
            WindowStyle = unchecked((int)0x80000000),
            Width = 800,
            Height = 400
        };
        terminal.Width = 800;
        terminal.Height = 400;
        HwndSource source = new(parameters) { RootVisual = terminal };
        terminal.Measure(new Size(800, 400));
        terminal.Arrange(new Rect(0, 0, 800, 400));
        terminal.UpdateLayout();
        return source;
    }

    private static void Write(TerminalEngine engine, string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        engine.Write(bytes, bytes.Length);
    }

    private static T GetField<T>(object target, string name) {
        FieldInfo? field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, "Expected private field " + name + ".");
        return (T)field!.GetValue(target)!;
    }

    private static void SetField<T>(object target, string name, T value) {
        FieldInfo? field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, "Expected private field " + name + ".");
        field!.SetValue(target, value);
    }

    private static void RefreshFrame(TerminalEmulator terminal) {
        MethodInfo? method = typeof(TerminalEmulator).GetMethod(
            "RefreshFrame", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        method!.Invoke(terminal, new object[] { false });
    }

    private static IEnumerable<GlyphRun> GlyphRuns(Drawing drawing) {
        if (drawing is GlyphRunDrawing glyph) {
            yield return glyph.GlyphRun;
        }
        if (drawing is DrawingGroup group) {
            foreach (Drawing child in group.Children) {
                foreach (GlyphRun run in GlyphRuns(child)) {
                    yield return run;
                }
            }
        }
    }

    private static TerminalRow[] ReadRows(TerminalEngine engine) {
        TerminalFrame frame = engine.ReadFrame(true);
        return frame.ChangedRows.ToArray();
    }

    private static byte[] RenderPixels(Drawing drawing, double width, double height, double pixelsPerDip,
                                       double originY = 0) {
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(width * pixelsPerDip));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(height * pixelsPerDip));
        double dpi = 96 * pixelsPerDip;
        RenderTargetBitmap bitmap = new(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen()) {
            if (originY != 0) {
                context.PushTransform(new TranslateTransform(0, -originY));
            }
            context.DrawDrawing(drawing);
            if (originY != 0) {
                context.Pop();
            }
        }
        bitmap.Render(visual);
        byte[] pixels = new byte[pixelWidth * pixelHeight * 4];
        bitmap.CopyPixels(pixels, pixelWidth * 4, 0);
        return pixels;
    }

    private static void RenderControl(TerminalEmulator terminal) {
        terminal.UpdateLayout();
        RenderTargetBitmap bitmap = new(800, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(terminal);
    }

    private static int CountRedPixels(byte[] pixels) {
        int count = 0;
        for (int index = 0; index < pixels.Length; index += 4) {
            bool pixelHasRedContribution = pixels[index + 2] > pixels[index + 1]
                && pixels[index + 2] > pixels[index] && pixels[index + 3] > 0;
            if (pixelHasRedContribution) {
                count++;
            }
        }
        return count;
    }

    private static int CountRedGeometry(Drawing drawing) {
        int count = 0;
        if (drawing is GeometryDrawing geometry) {
            SolidColorBrush? fill = geometry.Brush as SolidColorBrush;
            SolidColorBrush? stroke = geometry.Pen?.Brush as SolidColorBrush;
            bool hasRedFill = fill is not null && fill.Color == Colors.Red;
            bool hasRedStroke = stroke is not null && stroke.Color == Colors.Red;
            if (hasRedFill || hasRedStroke) {
                count++;
            }
        }
        if (drawing is DrawingGroup group) {
            foreach (Drawing child in group.Children) {
                count += CountRedGeometry(child);
            }
        }
        return count;
    }

    private static byte[] SingleCellPixels(TerminalDrawing drawing, TerminalRow row) {
        DrawingGroup group = drawing.DrawRow(row);
        return RenderPixels(group, row.Cells.Length * drawing.CellWidth, drawing.CellHeight, 1,
            row.Index * drawing.CellHeight);
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
    public void DrawRow_ConcealPreservesSelectedBackgroundAndSuppressesGlyphAndColoredUnderline() {
        using TerminalEngine engine = new(8, 1, bytes => { });
        Write(engine, "\u001b[0;31;58:2::255:0:0;4:3;9;53;8mX\u001b[0m");
        TerminalRow unselected = ReadRows(engine).Single(row => row.Index == 0);
        TerminalDrawing drawing = new();
        drawing.Configure(new FontFamily("Consolas"), 16, 1);

        DrawingGroup unselectedDrawing = drawing.DrawRow(unselected);
        Assert.That(GlyphRuns(unselectedDrawing), Is.Empty);
        Assert.That(CountRedPixels(SingleCellPixels(drawing, unselected)), Is.Zero,
            "Concealed text must not expose its explicit red underline.");

        engine.Select(0, 0, 0, 0);
        TerminalRow selected = ReadRows(engine).Single(row => row.Index == 0);
        DrawingGroup selectedDrawing = drawing.DrawRow(selected);
        byte[] selectedPixels = SingleCellPixels(drawing, selected);
        int centerX = (int)Math.Round(drawing.CellWidth / 2);
        int centerY = (int)Math.Round(drawing.CellHeight / 2);
        int pixelIndex = (centerY * (int)Math.Ceiling(selected.Cells.Length * drawing.CellWidth) + centerX) * 4;

        Assert.That(selected.Cells[0].Selected, Is.EqualTo(1));
        Assert.That(unselected.Cells[0].Flags & 32, Is.Not.Zero);
        Assert.That(unselected.Cells[0].Flags & 0x700, Is.EqualTo(0x300));
        Assert.That(GlyphRuns(selectedDrawing), Is.Empty);
        Assert.That(CountRedPixels(selectedPixels), Is.Zero);
        Assert.That(selectedPixels[pixelIndex], Is.EqualTo(0x78));
        Assert.That(selectedPixels[pixelIndex + 1], Is.EqualTo(0x4f));
        Assert.That(selectedPixels[pixelIndex + 2], Is.EqualTo(0x26));
        Assert.That(engine.SelectedText(), Is.EqualTo("X"),
            "Concealment affects display while terminal selection text remains available to copy.");
    }

    [Test]
    public void DrawRow_TextBlinkHidesOnlyBlinkingForegroundUntilSgrReset() {
        using TerminalEngine engine = new(8, 1, bytes => { });
        Write(engine, "\u001b[5;4:2;58:2::255:0:0;31mB\u001b[0mR");
        TerminalRow row = ReadRows(engine).Single(row => row.Index == 0);
        TerminalDrawing drawing = new();
        drawing.Configure(new FontFamily("Consolas"), 16, 1);

        DrawingGroup visible = drawing.DrawRow(row, true);
        DrawingGroup hidden = drawing.DrawRow(row, false);
        GlyphRun[] visibleRuns = GlyphRuns(visible).ToArray();
        GlyphRun[] hiddenRuns = GlyphRuns(hidden).ToArray();
        byte[] visiblePixels = RenderPixels(visible, row.Cells.Length * drawing.CellWidth,
            drawing.CellHeight, 1);
        byte[] hiddenPixels = RenderPixels(hidden, row.Cells.Length * drawing.CellWidth,
            drawing.CellHeight, 1);

        Assert.That((row.Cells[0].Flags & 8), Is.Not.Zero);
        Assert.That((row.Cells[1].Flags & 8), Is.Zero, "SGR reset must clear blink for later output.");
        Assert.That(visibleRuns, Has.Length.EqualTo(2));
        Assert.That(hiddenRuns, Has.Length.EqualTo(1));
        Assert.That(hiddenRuns[0].GlyphIndices,
            Is.EqualTo(new[] { hiddenRuns[0].GlyphTypeface.CharacterToGlyphMap['R'] }));
        Assert.That(CountRedPixels(visiblePixels), Is.GreaterThan(0));
        Assert.That(CountRedPixels(hiddenPixels), Is.Zero);
        Assert.That(hiddenPixels, Is.Not.EqualTo(visiblePixels));
    }

    [Test]
    public void DrawRow_UnderlineStylesProduceDistinctRedPixelsAtHighDpi() {
        using TerminalEngine engine = new(16, 5, bytes => { });
        for (int style = 1; style <= 5; style++) {
            string lineBreak = style == 5 ? "" : "\r\n";
            Write(engine, "\u001b[0;4:" + style + ";58:2::255:0:0mXXXXXXXX" + lineBreak);
        }
        TerminalRow[] rows = ReadRows(engine).OrderBy(row => row.Index).ToArray();
        Assert.That(rows, Has.Length.EqualTo(5));
        TerminalDrawing drawing = new();
        drawing.Configure(new FontFamily("Consolas"), 16, 1.5);
        byte[][] images = new byte[rows.Length][];

        for (int index = 0; index < rows.Length; index++) {
            int underlineStyle = (rows[index].Cells[0].Flags >> 8) & 7;
            Assert.That(underlineStyle, Is.EqualTo(index + 1));
            Assert.That(rows[index].Cells[0].Underline, Is.EqualTo(0xffff0000u));
            DrawingGroup drawingGroup = drawing.DrawRow(rows[index]);
            images[index] = RenderPixels(drawingGroup, rows[index].Cells.Length * drawing.CellWidth,
                drawing.CellHeight, 1.5, rows[index].Index * drawing.CellHeight);
            int redGeometryCount = CountRedGeometry(drawingGroup);
            int redPixelCount = CountRedPixels(images[index]);
            Assert.That(redGeometryCount, Is.GreaterThan(0),
                "Underline style " + underlineStyle + " must carry its requested SGR 58 color.");
            Assert.That(redPixelCount, Is.GreaterThan(0),
                "Underline style " + underlineStyle + " must produce visible red pixels.");
        }

        for (int first = 0; first < images.Length; first++) {
            for (int second = first + 1; second < images.Length; second++) {
                bool pixelsAreDifferent = !images[first].SequenceEqual(images[second]);
                Assert.That(pixelsAreDifferent, Is.True,
                    "Underline styles " + (first + 1) + " and " + (second + 1) + " must render differently.");
            }
        }
    }

    [TestCase((ushort)0)]
    [TestCase((ushort)1)]
    [TestCase((ushort)2)]
    [TestCase((ushort)3)]
    public void DrawRow_MissingAsciiGlyphFallsBackForEveryStyleAndLaterRows(ushort flags) {
        FontFamily family = new("Segoe MDL2 Assets");
        Typeface requestedFace = new(family, (flags & 2) != 0 ? FontStyles.Italic : FontStyles.Normal,
            (flags & 1) != 0 ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        bool faceExists = requestedFace.TryGetGlyphTypeface(out GlyphTypeface requestedGlyph);
        Assume.That(faceExists, Is.True, "The test font must expose a glyph face on the Windows test runner.");
        bool hasAsciiGlyph = requestedGlyph.CharacterToGlyphMap.TryGetValue('A', out ushort requestedIndex)
            && requestedIndex != 0;
        Assume.That(hasAsciiGlyph, Is.False,
            "This regression case requires a style face that lacks the selected ASCII character.");

        NativeCell first = new() {
            Foreground = 0xffffffff,
            Background = 0xff000000,
            TextOffset = 0,
            TextLength = 1,
            Flags = flags,
            Width = 1
        };
        NativeCell second = new() {
            Foreground = 0xff00ff00,
            Background = 0xff000000,
            TextOffset = 1,
            TextLength = 1,
            Width = 1
        };
        TerminalRow row = new(0, new[] { first, second }, Encoding.UTF8.GetBytes("AM"));
        TerminalDrawing drawing = new();
        drawing.Configure(family, 16, 1);

        GlyphRun[] runs = GlyphRuns(drawing.DrawRow(row)).ToArray();
        Typeface fallbackFace = new(new FontFamily("Consolas"),
            (flags & 2) != 0 ? FontStyles.Italic : FontStyles.Normal,
            (flags & 1) != 0 ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        Assert.That(fallbackFace.TryGetGlyphTypeface(out GlyphTypeface fallbackGlyph), Is.True);
        ushort fallbackA = fallbackGlyph.CharacterToGlyphMap['A'];
        Typeface normalFallbackFace = new("Consolas");
        Assert.That(normalFallbackFace.TryGetGlyphTypeface(out GlyphTypeface normalFallbackGlyph), Is.True);
        ushort fallbackM = normalFallbackGlyph.CharacterToGlyphMap['M'];
        bool fallbackRenderedA = runs.Any(run => run.GlyphTypeface.FontUri == fallbackGlyph.FontUri
            && run.GlyphIndices.Contains(fallbackA));
        bool fallbackRenderedM = runs.Any(run => run.GlyphTypeface.FontUri == normalFallbackGlyph.FontUri
            && run.GlyphIndices.Contains(fallbackM));

        Assert.That(runs.All(run => run.GlyphIndices.All(index => index != 0)), Is.True);
        Assert.That(fallbackRenderedA, Is.True,
            "The missing ASCII glyph must render with the Consolas fallback face.");
        Assert.That(fallbackRenderedM, Is.True,
            "Later output must still render through the configured fallback style.");
        Assert.That(runs.All(run => run.AdvanceWidths.All(advance => advance == drawing.CellWidth)), Is.True);
    }

    [TestCase(1u)]
    [TestCase(2u)]
    [TestCase(3u)]
    public void CursorBounds_WideLeadingAndTailPositionsCoverBothCellsAndNarrowPositionsOne(uint cursorStyle) {
        using TerminalEngine engine = new(8, 1, bytes => { });
        Write(engine, "界X");
        TerminalRow row = ReadRows(engine).Single(candidate => candidate.Index == 0);
        TerminalDrawing drawing = new();
        drawing.Configure(new FontFamily("Consolas"), 16, 1);
        Assert.That(row.Cells[0].Width, Is.EqualTo(2));
        Assert.That(row.Cells[1].Width, Is.Zero);
        Assert.That(row.Cells[2].Width, Is.EqualTo(1));

        NativeFrame frame = new() { CursorY = 0, CursorStyle = cursorStyle, CursorX = 0 };
        Rect leading = drawing.CursorBounds(frame, row);
        frame.CursorX = 1;
        Rect tail = drawing.CursorBounds(frame, row);
        frame.CursorX = 2;
        Rect narrow = drawing.CursorBounds(frame, row);

        Assert.That(leading.X, Is.Zero);
        Assert.That(leading.Width, Is.EqualTo(2 * drawing.CellWidth));
        Assert.That(tail.X, Is.Zero);
        Assert.That(tail.Width, Is.EqualTo(2 * drawing.CellWidth));
        Assert.That(narrow.X, Is.EqualTo(2 * drawing.CellWidth));
        Assert.That(narrow.Width, Is.EqualTo(drawing.CellWidth));
    }

    [Test]
    public void CursorBounds_BarRemainsNarrowAtTheReportedCursorColumn() {
        using TerminalEngine engine = new(8, 1, bytes => { });
        Write(engine, "界X");
        TerminalRow row = ReadRows(engine).Single(candidate => candidate.Index == 0);
        TerminalDrawing drawing = new();
        drawing.Configure(new FontFamily("Consolas"), 16, 1);
        NativeFrame frame = new() { CursorY = 0, CursorStyle = 0, CursorX = 1 };

        Rect cursor = drawing.CursorBounds(frame, row);

        Assert.That(cursor.X, Is.EqualTo(drawing.CellWidth));
        Assert.That(cursor.Width, Is.EqualTo(2));
    }

    [Test]
    public void RefreshFrame_TextBlinkPhaseRedrawsOnlyRetainedBlinkRowsAndStopRestoresVisibility() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            Pump(terminal.StartAsync());
            GetField<DispatcherTimer>(terminal, "_timer").Stop();
            TerminalSession session = GetField<TerminalSession>(terminal, "_session");
            Write(session.Engine, "\u001b[?25l\u001b[?12l\u001b[1;1H\u001b[5mB\u001b[0m\u001b[2;1HR");
            RefreshFrame(terminal);

            DrawingGroup?[] rowDrawings = GetField<DrawingGroup?[]>(terminal, "_rows");
            TerminalRow?[] snapshots = GetField<TerminalRow?[]>(terminal, "_rowSnapshots");
            DrawingGroup blinkingRow = rowDrawings[0]!;
            DrawingGroup steadyRow = rowDrawings[1]!;
            TerminalRow blinkingSnapshot = snapshots[0]!;
            TerminalRow steadySnapshot = snapshots[1]!;
            Assert.That(GlyphRuns(blinkingRow).ToArray(), Has.Length.EqualTo(1));
            Assert.That(GlyphRuns(steadyRow).ToArray(), Has.Length.EqualTo(1));
            Assert.That(GetField<NativeFrame>(terminal, "_frame").CursorVisible, Is.Zero);
            Assert.That(GetField<NativeFrame>(terminal, "_frame").CursorBlink, Is.Zero);
            bool cursorOnBeforeBlink = GetField<bool>(terminal, "_cursorOn");

            SetField(terminal, "_lastTextBlink", DateTime.UtcNow.AddSeconds(-1));
            RefreshFrame(terminal);

            DrawingGroup?[] blinkedDrawings = GetField<DrawingGroup?[]>(terminal, "_rows");
            TerminalRow?[] retainedSnapshots = GetField<TerminalRow?[]>(terminal, "_rowSnapshots");
            Assert.That(GetField<bool>(terminal, "_textBlinkOn"), Is.False);
            Assert.That(GlyphRuns(blinkedDrawings[0]!).ToArray(), Is.Empty);
            Assert.That(GlyphRuns(blinkedDrawings[1]!).ToArray(), Has.Length.EqualTo(1));
            Assert.That(blinkedDrawings[0], Is.Not.SameAs(blinkingRow));
            Assert.That(blinkedDrawings[1], Is.SameAs(steadyRow));
            Assert.That(retainedSnapshots[0], Is.SameAs(blinkingSnapshot));
            Assert.That(retainedSnapshots[1], Is.SameAs(steadySnapshot));
            Assert.That(GetField<bool>(terminal, "_cursorOn"), Is.EqualTo(cursorOnBeforeBlink));

            Pump(terminal.StopAsync());

            DrawingGroup?[] stoppedDrawings = GetField<DrawingGroup?[]>(terminal, "_rows");
            Assert.That(GetField<bool>(terminal, "_textBlinkOn"), Is.True);
            Assert.That(GlyphRuns(stoppedDrawings[0]!).ToArray(), Has.Length.EqualTo(1));
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void FontReconfiguration_PreservesHiddenTextBlinkPhase() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            Pump(terminal.StartAsync());
            GetField<DispatcherTimer>(terminal, "_timer").Stop();
            TerminalSession session = GetField<TerminalSession>(terminal, "_session");
            Write(session.Engine, "\u001b[?25l\u001b[?12l\u001b[5mB");
            RefreshFrame(terminal);
            SetField(terminal, "_lastTextBlink", DateTime.UtcNow.AddSeconds(-1));
            RefreshFrame(terminal);
            DrawingGroup hiddenRow = GetField<DrawingGroup?[]>(terminal, "_rows")[0]!;
            Assert.That(GlyphRuns(hiddenRow).ToArray(), Is.Empty);

            terminal.FontSize += 1;
            RenderControl(terminal);

            DrawingGroup reconfiguredRow = GetField<DrawingGroup?[]>(terminal, "_rows")[0]!;
            Assert.That(GetField<bool>(terminal, "_textBlinkOn"), Is.False);
            Assert.That(GlyphRuns(reconfiguredRow).ToArray(), Is.Empty);
            Assert.That(reconfiguredRow, Is.Not.SameAs(hiddenRow));
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void ObserveExit_RestoresBlinkingTextInTheFinalFrame() {
        TerminalEmulator terminal = new() { AutoStart = false, CommandLine = IdleCommand };
        using HwndSource source = Host(terminal);
        try {
            Pump(terminal.StartAsync());
            GetField<DispatcherTimer>(terminal, "_timer").Stop();
            TerminalSession session = GetField<TerminalSession>(terminal, "_session");
            Write(session.Engine, "\u001b[?25l\u001b[?12l\u001b[5mB");
            RefreshFrame(terminal);
            SetField(terminal, "_lastTextBlink", DateTime.UtcNow.AddSeconds(-1));
            RefreshFrame(terminal);
            Assert.That(GlyphRuns(GetField<DrawingGroup?[]>(terminal, "_rows")[0]!).ToArray(), Is.Empty);

            Task stop = Task.Run(session.Stop);
            WaitFor(() => !terminal.IsRunning);
            Pump(stop);

            DrawingGroup?[] finalDrawings = GetField<DrawingGroup?[]>(terminal, "_rows");
            Assert.That(GetField<bool>(terminal, "_textBlinkOn"), Is.True);
            Assert.That(GlyphRuns(finalDrawings[0]!).ToArray(), Has.Length.EqualTo(1));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            Pump(terminal.StopAsync());
        }
    }
}
