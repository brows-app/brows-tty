using System.Text;
using System.Windows;
using System.Windows.Media;
using Brows.Ghostty;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalDrawingTests {
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

    private static NativeCell CreateCell(uint textOffset, uint foreground = 0xffffffff, ushort flags = 0,
                                         uint underline = 0, uint textLength = 1) {
        return new NativeCell {
            Foreground = foreground,
            Background = 0xff000000,
            Underline = underline,
            TextOffset = textOffset,
            TextLength = textLength,
            Flags = flags,
            Width = 1
        };
    }

    private static void AssertFinitePositive(double value) {
        Assert.That(double.IsNaN(value), Is.False);
        Assert.That(double.IsInfinity(value), Is.False);
        Assert.That(value, Is.GreaterThan(0));
    }

    [Test]
    public void Configure_CompositeFontUsesUsableConsolasFallbackMetrics() {
        FontFamily family = new("Global User Interface");
        Typeface compositeFace = new(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Assert.That(compositeFace.TryGetGlyphTypeface(out _), Is.False,
            "The Global User Interface font is composite and must use the configured glyph fallback.");

        const double size = 14;
        TerminalDrawing drawing = new();
        drawing.Configure(family, size, 1);

        Typeface fallbackFace = new("Consolas");
        Assert.That(fallbackFace.TryGetGlyphTypeface(out GlyphTypeface fallbackGlyph), Is.True);
        Assert.That(fallbackGlyph.CharacterToGlyphMap.TryGetValue('M', out ushort glyphIndex), Is.True);
        double expectedCellWidth = Math.Ceiling(fallbackGlyph.AdvanceWidths[glyphIndex] * size);
        Assert.That(drawing.CellWidth, Is.EqualTo(expectedCellWidth));
        AssertFinitePositive(drawing.CellWidth);
        AssertFinitePositive(drawing.CellHeight);
    }

    [TestCase(0, 1)]
    [TestCase(-1, 1)]
    [TestCase(double.NaN, 1)]
    [TestCase(double.PositiveInfinity, 1)]
    [TestCase(double.MaxValue, 1)]
    [TestCase(14, 0)]
    [TestCase(14, -1)]
    [TestCase(14, double.NaN)]
    [TestCase(14, double.PositiveInfinity)]
    public void Configure_InvalidCellMetrics_ThrowsClearException(double size, double pixelsPerDip) {
        TerminalDrawing drawing = new();

        Assert.That(() => drawing.Configure(new FontFamily("Consolas"), size, pixelsPerDip),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("cell dimensions"));
    }

    [Test]
    public void Configure_FailedReconfigurationPreservesPreviousDrawingState() {
        FontFamily family = new("Consolas");
        TerminalDrawing drawing = new();
        drawing.Configure(family, 16, 1);
        NativeCell cell = new() {
            Foreground = 0xffffffff,
            Background = 0xff000000,
            TextOffset = 0,
            TextLength = 1,
            Flags = 3,
            Width = 1
        };
        TerminalRow row = new(0, new[] { cell }, Encoding.UTF8.GetBytes("M"));
        GlyphRun originalRun = GlyphRuns(drawing.DrawRow(row)).Single();
        double originalCellWidth = drawing.CellWidth;
        double originalCellHeight = drawing.CellHeight;

        Assert.That(() => drawing.Configure(family, 16, double.NaN),
            Throws.TypeOf<InvalidOperationException>());

        GlyphRun retainedRun = GlyphRuns(drawing.DrawRow(row)).Single();
        Assert.That(drawing.CellWidth, Is.EqualTo(originalCellWidth));
        Assert.That(drawing.CellHeight, Is.EqualTo(originalCellHeight));
        Assert.That(retainedRun.GlyphTypeface.FontUri, Is.EqualTo(originalRun.GlyphTypeface.FontUri));
        Assert.That(retainedRun.FontRenderingEmSize, Is.EqualTo(originalRun.FontRenderingEmSize));
        Assert.That(retainedRun.PixelsPerDip, Is.EqualTo(originalRun.PixelsPerDip));
    }

    [Test]
    public void DrawRow_PreservesConfiguredBoldItalicTypefaceAndCellAdvance() {
        FontFamily family = new("Consolas");
        TerminalDrawing drawing = new();
        drawing.Configure(family, 16, 1);
        NativeCell cell = new() {
            Foreground = 0xffffffff,
            Background = 0xff000000,
            TextOffset = 0,
            TextLength = 1,
            Flags = 3,
            Width = 1
        };
        TerminalRow row = new(0, new[] { cell }, Encoding.UTF8.GetBytes("M"));

        GlyphRun run = GlyphRuns(drawing.DrawRow(row)).Single();
        Typeface expectedFace = new(family, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
        Assert.That(expectedFace.TryGetGlyphTypeface(out GlyphTypeface expectedGlyph), Is.True);
        Assert.That(run.GlyphTypeface.FontUri, Is.EqualTo(expectedGlyph.FontUri));
        Assert.That(run.AdvanceWidths.Single(), Is.EqualTo(drawing.CellWidth));
    }

    [Test]
    public void DrawRow_EmitsLongCompatibleAsciiRunWithOneAdvancePerCell() {
        const int runLength = 129;
        string text = new('X', runLength);
        NativeCell[] cells = Enumerable.Range(0, runLength)
            .Select(index => CreateCell((uint)index))
            .ToArray();
        TerminalRow row = new(0, cells, Encoding.UTF8.GetBytes(text));
        TerminalDrawing drawing = new();
        drawing.Configure(new FontFamily("Consolas"), 16, 1);

        GlyphRun run = GlyphRuns(drawing.DrawRow(row)).Single();
        ushort glyphIndex = run.GlyphTypeface.CharacterToGlyphMap['X'];

        Assert.That(run.GlyphIndices, Is.EqualTo(Enumerable.Repeat(glyphIndex, runLength)));
        Assert.That(run.AdvanceWidths, Is.EqualTo(Enumerable.Repeat(drawing.CellWidth, runLength)));
    }

    [Test]
    public void DrawRow_SplitsAsciiRunsAtForegroundFlagsUnderlineAndUnicode() {
        const uint green = 0xff00ff00;
        const uint red = 0xffff0000;
        const uint blue = 0xff0000ff;
        string text = "ABCDéFGHI";
        NativeCell[] cells = new[] {
            CreateCell(0),
            CreateCell(1),
            CreateCell(2, foreground: green),
            CreateCell(3, foreground: green, flags: 1),
            CreateCell(4, foreground: green, flags: 1, textLength: 2),
            CreateCell(6, foreground: green, flags: 0x101, underline: red),
            CreateCell(7, foreground: green, flags: 0x101, underline: blue),
            CreateCell(8, foreground: green, flags: 0x101, underline: blue),
            CreateCell(9, foreground: green, flags: 0x101, underline: blue)
        };
        TerminalRow row = new(0, cells, Encoding.UTF8.GetBytes(text));
        TerminalDrawing drawing = new();
        drawing.Configure(new FontFamily("Consolas"), 16, 1);

        GlyphRun[] runs = GlyphRuns(drawing.DrawRow(row)).ToArray();
        string[] expectedGroups = { "AB", "C", "D", "é", "F", "GHI" };
        int[] expectedColumns = { 0, 2, 3, 4, 5, 6 };

        Assert.That(runs, Has.Length.EqualTo(expectedGroups.Length));
        for (int index = 0; index < runs.Length; index++) {
            GlyphRun run = runs[index];
            ushort[] expectedGlyphs = expectedGroups[index]
                .Select(character => run.GlyphTypeface.CharacterToGlyphMap[character])
                .ToArray();

            Assert.That(run.GlyphIndices, Is.EqualTo(expectedGlyphs));
            if (expectedGroups[index] == "é") {
                Assert.That(run.AdvanceWidths, Has.Count.EqualTo(1));
            }
            else {
                Assert.That(run.AdvanceWidths,
                    Is.EqualTo(Enumerable.Repeat(drawing.CellWidth, expectedGroups[index].Length)));
            }
            Assert.That(run.BaselineOrigin.X, Is.EqualTo(expectedColumns[index] * drawing.CellWidth));
        }
    }
}
