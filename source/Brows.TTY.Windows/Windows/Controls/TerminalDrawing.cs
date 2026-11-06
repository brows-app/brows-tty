using Brows.Ghostty;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Brows.Windows.Controls;

internal sealed class TerminalDrawing {
    private const string FallbackFontName = "Consolas";
    private const ushort BlinkFlag = 8;
    private const ushort InvisibleFlag = 32;
    private const ushort UnderlineMask = 0x700;
    private const int UnderlineShift = 8;
    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    private readonly GlyphTypeface[] _glyphs = new GlyphTypeface[4];
    private readonly GlyphTypeface?[] _fallbackGlyphs = new GlyphTypeface?[4];
    private readonly Typeface[] _typefaces = new Typeface[4];
    private double _fontSize;
    private double _pixelsPerDip;

    private static int FontIndex(ushort flags) {
        return flags & 3;
    }

    private static GlyphTypeface CreateFallbackGlyphTypeface(int font) {
        Typeface fallbackFace = new(new FontFamily(FallbackFontName),
            (font & 2) != 0 ? FontStyles.Italic : FontStyles.Normal,
            (font & 1) != 0 ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
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

    private static bool TryGetGlyphIndex(GlyphTypeface glyph, char character, out ushort glyphIndex) {
        bool hasGlyph = glyph.CharacterToGlyphMap.TryGetValue(character, out glyphIndex);
        bool glyphIsUsable = hasGlyph && glyphIndex != 0;
        if (!glyphIsUsable) {
            glyphIndex = 0;
        }
        return glyphIsUsable;
    }

    private bool TryGetGlyph(int font, char character, out GlyphTypeface glyph, out ushort glyphIndex) {
        glyph = _glyphs[font];
        if (TryGetGlyphIndex(glyph, character, out glyphIndex)) {
            return true;
        }
        GlyphTypeface fallback = _fallbackGlyphs[font] ??= CreateFallbackGlyphTypeface(font);
        bool currentFontIsFallback = glyph.FontUri == fallback.FontUri;
        bool fallbackCannotImproveGlyph = currentFontIsFallback;
        if (!fallbackCannotImproveGlyph) {
            fallbackCannotImproveGlyph = !TryGetGlyphIndex(fallback, character, out glyphIndex);
        }
        if (fallbackCannotImproveGlyph) {
            glyphIndex = 0;
            return false;
        }
        glyph = fallback;
        return true;
    }

    private double SnapToDevicePixel(double value) {
        return Math.Round(value * _pixelsPerDip) / _pixelsPerDip;
    }

    private void DrawUnderline(DrawingContext context, uint color, int style, double x, double top, double baseline,
                               double width) {
        double pixel = 1.0 / _pixelsPerDip;
        double snappedX = SnapToDevicePixel(x);
        double underlineY = Math.Min(baseline + pixel, top + CellHeight - pixel);
        double snappedY = SnapToDevicePixel(underlineY);
        Brush brush = Brush(color);
        if (style == 1) {
            context.DrawRectangle(brush, null, new Rect(snappedX, snappedY, width, pixel));
            return;
        }
        if (style == 2) {
            context.DrawRectangle(brush, null, new Rect(snappedX, snappedY - (2 * pixel), width, pixel));
            context.DrawRectangle(brush, null, new Rect(snappedX, snappedY, width, pixel));
            return;
        }
        if (style == 3) {
            StreamGeometry geometry = new();
            using (StreamGeometryContext geometryContext = geometry.Open()) {
                double centerY = snappedY + pixel / 2;
                geometryContext.BeginFigure(new Point(snappedX, centerY), false, false);
                double position = pixel;
                while (position < width) {
                    double wave = Math.Sin(position * Math.PI / (2 * pixel)) * pixel;
                    geometryContext.LineTo(new Point(snappedX + position, centerY + wave), true, false);
                    position += pixel;
                }
                geometryContext.LineTo(new Point(snappedX + width, centerY), true, false);
            }
            geometry.Freeze();
            Pen curlyPen = new(brush, pixel) {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            context.DrawGeometry(null, curlyPen, geometry);
            return;
        }
        bool underlineStyleIsDottedOrDashed = style == 4 || style == 5;
        if (underlineStyleIsDottedOrDashed) {
            DashStyle dashStyle = style == 4 ? DashStyles.Dot : DashStyles.Dash;
            Pen pen = new(brush, pixel) {
                DashStyle = dashStyle,
                StartLineCap = style == 4 ? PenLineCap.Round : PenLineCap.Flat,
                EndLineCap = style == 4 ? PenLineCap.Round : PenLineCap.Flat
            };
            context.DrawLine(pen, new Point(snappedX, snappedY + pixel / 2),
                new Point(snappedX + width, snappedY + pixel / 2));
        }
    }

    internal double CellWidth { get; private set; }
    internal double CellHeight { get; private set; }

    internal void Configure(FontFamily family, double size, double pixelsPerDip) {
        bool scaleIsUsable = IsFinitePositive(size) && IsFinitePositive(pixelsPerDip);
        if (!scaleIsUsable) {
            throw new InvalidOperationException("The configured font produced invalid terminal cell dimensions.");
        }
        GlyphTypeface[] glyphs = new GlyphTypeface[4];
        GlyphTypeface?[] fallbackGlyphs = new GlyphTypeface?[4];
        Typeface[] typefaces = new Typeface[4];
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
            fallbackGlyphs[index] = CreateFallbackGlyphTypeface(index);
            glyphs[index] = fallbackGlyphs[index]!;
        }
        GlyphTypeface normal = glyphs[0];
        bool hasUsableMAdvance = TryGetMAdvance(normal, out double mAdvance);
        if (!hasUsableMAdvance) {
            fallbackGlyphs[0] ??= CreateFallbackGlyphTypeface(0);
            normal = fallbackGlyphs[0]!;
            glyphs[0] = normal;
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
        Array.Copy(fallbackGlyphs, _fallbackGlyphs, fallbackGlyphs.Length);
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

    internal Rect CursorBounds(NativeFrame frame, TerminalRow? row) {
        uint cursorX = frame.CursorX;
        double cursorWidth = CellWidth;
        bool cursorIsNotBar = frame.CursorStyle != 0;
        if (row is not null) {
            bool rowMatchesCursor = row.Index == frame.CursorY;
            bool cursorColumnIsInRow = cursorX < row.Cells.Length;
            bool cursorUsesCellGeometry = cursorIsNotBar && rowMatchesCursor && cursorColumnIsInRow;
            if (cursorUsesCellGeometry) {
                NativeCell cell = row.Cells[cursorX];
                bool cursorIsOnWideTail = cell.Width == 0 && cursorX > 0 && row.Cells[cursorX - 1].Width == 2;
                if (cursorIsOnWideTail) {
                    cursorX--;
                    cursorWidth = 2 * CellWidth;
                }
                else if (cell.Width == 2) {
                    cursorWidth = 2 * CellWidth;
                }
            }
        }
        if (frame.CursorStyle == 0) {
            cursorWidth = 2;
        }
        return new Rect(cursorX * CellWidth, frame.CursorY * CellHeight, cursorWidth, CellHeight);
    }

    internal DrawingGroup DrawRow(TerminalRow row, bool textBlinkOn = true) {
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
                bool concealed = (cell.Flags & InvisibleFlag) != 0;
                bool blinkingTextIsHidden = (cell.Flags & BlinkFlag) != 0 && !textBlinkOn;
                bool textIsHidden = concealed || blinkingTextIsHidden;
                if (textIsHidden) {
                    x++;
                    continue;
                }
                int font = FontIndex(cell.Flags);
                double baseline = top + (_glyphs[0].Baseline * _fontSize) + (CellHeight - _fontSize) / 2;
                bool ascii = cell.TextLength == 1 && row.Text[cell.TextOffset] >= 32
                    && row.Text[cell.TextOffset] < 127;
                int end = x + 1;
                GlyphTypeface glyph = null!;
                ushort glyphIndex = 0;
                bool hasAsciiGlyph = false;
                if (ascii) {
                    hasAsciiGlyph = TryGetGlyph(font, (char)row.Text[cell.TextOffset], out glyph, out glyphIndex);
                }
                bool canDrawAsciiGlyphRun = ascii && hasAsciiGlyph;
                if (canDrawAsciiGlyphRun) {
                    int runLength = 1;
                    while (end < row.Cells.Length) {
                        NativeCell next = row.Cells[end];
                        bool matches = next.TextLength == 1 && next.Width == 1 && next.Foreground == cell.Foreground
                            && next.Flags == cell.Flags && next.Underline == cell.Underline
                            && row.Text[next.TextOffset] >= 32 && row.Text[next.TextOffset] < 127;
                        if (!matches) {
                            break;
                        }
                        bool hasCompatibleGlyph = TryGetGlyph(font, (char)row.Text[next.TextOffset],
                            out GlyphTypeface nextGlyph, out ushort nextGlyphIndex);
                        bool usesSameGlyphTypeface = hasCompatibleGlyph && nextGlyph.FontUri == glyph.FontUri;
                        if (!usesSameGlyphTypeface) {
                            break;
                        }
                        runLength++;
                        end++;
                    }
                    ushort[] indices = new ushort[runLength];
                    double[] advances = new double[runLength];
                    indices[0] = glyphIndex;
                    advances[0] = CellWidth;
                    for (int index = 1; index < runLength; index++) {
                        NativeCell next = row.Cells[x + index];
                        bool hasGlyph = TryGetGlyph(font, (char)row.Text[next.TextOffset],
                            out GlyphTypeface nextGlyph, out ushort nextGlyphIndex);
                        bool usesSameGlyphTypeface = hasGlyph && nextGlyph.FontUri == glyph.FontUri;
                        if (usesSameGlyphTypeface) {
                            indices[index] = nextGlyphIndex;
                        }
                        else {
                            bool hasReplacementGlyph = TryGetGlyphIndex(glyph, '?', out ushort replacementGlyph);
                            indices[index] = hasReplacementGlyph ? replacementGlyph : glyphIndex;
                        }
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
                int underlineStyle = (cell.Flags & UnderlineMask) >> UnderlineShift;
                bool hasVisibleUnderlineStyle = underlineStyle >= 1 && underlineStyle <= 5;
                if (hasVisibleUnderlineStyle) {
                    DrawUnderline(context, cell.Underline, underlineStyle, x * CellWidth, top, baseline, width);
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
