using Brows.Ghostty;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Brows.Windows.Controls;

internal sealed class TerminalDrawing {
    private const string FallbackFontName = "Consolas";
    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    private readonly GlyphTypeface[] _glyphs = new GlyphTypeface[4];
    private readonly Typeface[] _typefaces = new Typeface[4];
    private double _fontSize;
    private double _pixelsPerDip;

    private static int FontIndex(ushort flags) {
        return flags & 3;
    }

    private static GlyphTypeface CreateFallbackGlyphTypeface() {
        Typeface fallbackFace = new(FallbackFontName);
        bool fallbackAvailable = fallbackFace.TryGetGlyphTypeface(out GlyphTypeface fallbackGlyph);
        if (!fallbackAvailable) {
            throw new InvalidOperationException("The Consolas fallback font has no usable glyph typeface.");
        }
        if (fallbackGlyph is null) {
            throw new InvalidOperationException("The Consolas fallback font has no usable glyph typeface.");
        }
        return fallbackGlyph;
    }

    private static bool TryGetMAdvance(GlyphTypeface glyph, out double advance) {
        bool hasM = glyph.CharacterToGlyphMap.TryGetValue('M', out ushort glyphIndex);
        bool mIndexIsMissing = !hasM || glyphIndex == 0;
        if (mIndexIsMissing) {
            advance = 0;
            return false;
        }
        bool hasAdvance = glyph.AdvanceWidths.TryGetValue(glyphIndex, out advance);
        bool advanceIsUsable = hasAdvance && IsFinitePositive(advance);
        if (!advanceIsUsable) {
            advance = 0;
            return false;
        }
        return true;
    }

    private static bool IsFinitePositive(double value) {
        return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    internal double CellWidth { get; private set; }
    internal double CellHeight { get; private set; }

    internal void Configure(FontFamily family, double size, double pixelsPerDip) {
        bool scaleIsUsable = IsFinitePositive(size) && IsFinitePositive(pixelsPerDip);
        if (!scaleIsUsable) {
            throw new InvalidOperationException("The configured font produced invalid terminal cell dimensions.");
        }
        GlyphTypeface[] glyphs = new GlyphTypeface[4];
        Typeface[] typefaces = new Typeface[4];
        GlyphTypeface? fallbackGlyph = null;
        for (int index = 0; index < 4; index++) {
            Typeface face = new(family, (index & 2) != 0 ? FontStyles.Italic : FontStyles.Normal,
                (index & 1) != 0 ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            typefaces[index] = face;
            bool glyphAvailable = face.TryGetGlyphTypeface(out GlyphTypeface glyph);
            if (glyphAvailable) {
                if (glyph is not null) {
                    glyphs[index] = glyph;
                    continue;
                }
            }
            fallbackGlyph ??= CreateFallbackGlyphTypeface();
            glyphs[index] = fallbackGlyph;
        }
        GlyphTypeface normal = glyphs[0];
        bool hasUsableMAdvance = TryGetMAdvance(normal, out double mAdvance);
        if (!hasUsableMAdvance) {
            fallbackGlyph ??= CreateFallbackGlyphTypeface();
            normal = fallbackGlyph;
            glyphs[0] = fallbackGlyph;
            hasUsableMAdvance = TryGetMAdvance(normal, out mAdvance);
        }
        if (!hasUsableMAdvance) {
            throw new InvalidOperationException("Neither the configured font nor Consolas has a usable M glyph.");
        }
        double cellWidth = Math.Ceiling(mAdvance * size * pixelsPerDip) / pixelsPerDip;
        double cellHeight = Math.Ceiling(Math.Max(family.LineSpacing, 1.2) * size * pixelsPerDip) / pixelsPerDip;
        bool metricsAreUsable = IsFinitePositive(cellWidth) && IsFinitePositive(cellHeight);
        if (!metricsAreUsable) {
            throw new InvalidOperationException("The configured font produced invalid terminal cell dimensions.");
        }
        Array.Copy(glyphs, _glyphs, glyphs.Length);
        Array.Copy(typefaces, _typefaces, typefaces.Length);
        _fontSize = size;
        _pixelsPerDip = pixelsPerDip;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
    }

    internal SolidColorBrush Brush(uint value) {
        if (_brushes.TryGetValue(value, out SolidColorBrush? brush)) {
            return brush;
        }
        if (_brushes.Count >= 4096) {
            _brushes.Clear();
        }
        brush = new SolidColorBrush(Color.FromArgb(
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value));
        brush.Freeze();
        _brushes.Add(value, brush);
        return brush;
    }

    internal DrawingGroup DrawRow(TerminalRow row) {
        DrawingGroup group = new();
        using (DrawingContext context = group.Open()) {
            double top = row.Index * CellHeight;
            context.PushClip(new RectangleGeometry(new Rect(0, top, row.Cells.Length * CellWidth, CellHeight)));
            for (int x = 0; x < row.Cells.Length;) {
                NativeCell cell = row.Cells[x];
                uint background = cell.Selected != 0 ? 0xff264f78 : cell.Background;
                int end = x + 1;
                while (end < row.Cells.Length) {
                    NativeCell next = row.Cells[end];
                    uint nextBackground = next.Selected != 0 ? 0xff264f78 : next.Background;
                    if (nextBackground != background) {
                        break;
                    }
                    end++;
                }
                context.DrawRectangle(Brush(background), null,
                    new Rect(x * CellWidth, top, (end - x) * CellWidth, CellHeight));
                x = end;
            }
            for (int x = 0; x < row.Cells.Length;) {
                NativeCell cell = row.Cells[x];
                if (cell.Width == 0) {
                    x++;
                    continue;
                }
                int font = FontIndex(cell.Flags);
                GlyphTypeface glyph = _glyphs[font];
                double baseline = top + (_glyphs[0].Baseline * _fontSize) + (CellHeight - _fontSize) / 2;
                bool ascii = cell.TextLength == 1 && row.Text[cell.TextOffset] >= 32
                    && row.Text[cell.TextOffset] < 127;
                int end = x + 1;
                if (ascii) {
                    end = x;
                    while (end < row.Cells.Length) {
                        NativeCell next = row.Cells[end];
                        bool matches = next.TextLength == 1 && next.Width == 1 && next.Foreground == cell.Foreground
                            && next.Flags == cell.Flags && next.Underline == cell.Underline
                            && row.Text[next.TextOffset] >= 32 && row.Text[next.TextOffset] < 127;
                        if (!matches) {
                            break;
                        }
                        end++;
                    }
                    int runLength = end - x;
                    ushort[] indices = new ushort[runLength];
                    double[] advances = new double[runLength];
                    for (int index = 0; index < runLength; index++) {
                        NativeCell next = row.Cells[x + index];
                        indices[index] = glyph.CharacterToGlyphMap[row.Text[next.TextOffset]];
                        advances[index] = CellWidth;
                    }
                    GlyphRun run = new(glyph, 0, false, _fontSize, (float)_pixelsPerDip, indices,
                        new Point(x * CellWidth, baseline), advances, null, null, null, null, null, null);
                    context.DrawGlyphRun(Brush(cell.Foreground), run);
                }
                else if (cell.TextLength != 0) {
                    FormattedText text = new(row.GetText(x), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        _typefaces[font], _fontSize, Brush(cell.Foreground), _pixelsPerDip);
                    context.PushClip(new RectangleGeometry(
                        new Rect(x * CellWidth, top, cell.Width * CellWidth, CellHeight)));
                    context.DrawText(text, new Point(x * CellWidth, baseline - text.Baseline));
                    context.Pop();
                }
                double width = Math.Max(end - x, cell.Width) * CellWidth;
                if ((cell.Flags & 0x700) != 0) {
                    context.DrawRectangle(Brush(cell.Underline), null,
                        new Rect(x * CellWidth, baseline + 1, width, 1));
                }
                if ((cell.Flags & 64) != 0) {
                    context.DrawRectangle(Brush(cell.Foreground), null,
                        new Rect(x * CellWidth, top + CellHeight / 2, width, 1));
                }
                if ((cell.Flags & 128) != 0) {
                    context.DrawRectangle(Brush(cell.Foreground), null, new Rect(x * CellWidth, top + 1, width, 1));
                }
                x = end;
            }
            context.Pop();
        }
        group.Freeze();
        return group;
    }
}
