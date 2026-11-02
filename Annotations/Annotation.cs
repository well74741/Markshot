namespace Markshot.Annotations;

public enum AnnotationTool
{
    None,
    Select,
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Pen,
    Highlighter,
    Mosaic,
    Watermark,
    Text,
    Number,
    Eraser,
    MosaicBrush
}

public abstract class Annotation
{
    public Color Color { get; set; } = Color.FromRgb(0xFF, 0x4D, 0x4F);

    public double Thickness { get; set; } = 3;

    public bool Fill { get; set; }

    public double CornerRadius { get; set; }

    public double Opacity { get; set; } = 1.0;

    public double Rotation { get; set; }

    public abstract void Draw(DrawingContext dc);

    /// <summary>
    /// Eraser strokes bound to this element, in its own unrotated frame relative
    /// to <see cref="Bounds"/> at the time of erasing. They move, scale and rotate
    /// with the element; nothing is left behind on the canvas.
    /// </summary>
    public List<EraseStroke>? EraseMasks { get; set; }

    public virtual bool Erasable => true;

    /// <summary>Copy used by undo snapshots. Frozen bitmaps are shared.</summary>
    public virtual Annotation Clone()
    {
        var copy = (Annotation)MemberwiseClone();
        copy.EraseMasks = EraseMasks?.Select(m => m.Clone()).ToList();
        copy.MaskCache = null;
        copy.MaskCacheKey = null;
        return copy;
    }

    internal Geometry? MaskCache;
    internal string? MaskCacheKey;

    /// <summary>The element's erased area in canvas coordinates (unrotated element frame), or null.</summary>
    public Geometry? MaskGeometry()
    {
        if (EraseMasks is not { Count: > 0 })
        {
            return null;
        }

        var bounds = Bounds;
        if (bounds.IsEmpty)
        {
            return null;
        }

        if (!EraseMasks.Any(stroke => stroke.Full))
        {
            return null;
        }

        var key = $"{bounds.X:F2},{bounds.Y:F2},{bounds.Width:F2},{bounds.Height:F2},{EraseMasks.Count}";
        if (MaskCache != null && MaskCacheKey == key)
        {
            return MaskCache;
        }

        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var stroke in EraseMasks.Where(stroke => stroke.Full))
        {
            group.Children.Add(StrokeGeometry(stroke, bounds));
        }

        group.Freeze();
        MaskCache = group;
        MaskCacheKey = key;
        return group;
    }

    /// <summary>Partly erasing strokes (eraser opacity below 100 %), each with its strength.</summary>
    public IEnumerable<(Geometry Geometry, double Strength)> PartialMasks()
    {
        if (EraseMasks is not { Count: > 0 })
        {
            yield break;
        }

        var bounds = Bounds;
        if (bounds.IsEmpty)
        {
            yield break;
        }

        foreach (var stroke in EraseMasks.Where(stroke => !stroke.Full))
        {
            yield return (StrokeGeometry(stroke, bounds), stroke.Strength);
        }
    }

    private static Geometry StrokeGeometry(EraseStroke stroke, Rect bounds)
    {
        var sx = Math.Max(1, bounds.Width) / Math.Max(1, stroke.RecordedSize.Width);
        var sy = Math.Max(1, bounds.Height) / Math.Max(1, stroke.RecordedSize.Height);
        var points = stroke.Points.Select(p => new Point(bounds.X + p.X * sx, bounds.Y + p.Y * sy)).ToList();
        return EraseStroke.Widen(points, stroke.Width * Math.Sqrt(sx * sy));
    }

    /// <summary>Binds a canvas-space eraser stroke to this element.</summary>
    public void AddEraseStroke(IReadOnlyList<Point> canvasPoints, double width, double strength = 1)
    {
        var bounds = Bounds;
        if (bounds.IsEmpty || canvasPoints.Count == 0)
        {
            return;
        }

        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var local = canvasPoints
            .Select(p => GeometryUtil.Rotate(p, center, -Rotation))
            .Select(p => new Point(p.X - bounds.X, p.Y - bounds.Y))
            .ToList();
        EraseMasks ??= new List<EraseStroke>();
        EraseMasks.Add(new EraseStroke(local, width, new Size(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)), strength));
        MaskCache = null;
    }

    /// <summary>True when the canvas point lies in an erased part of the element.</summary>
    public bool IsErasedAt(Point canvasPoint)
    {
        var mask = MaskGeometry();
        if (mask == null)
        {
            return false;
        }

        var bounds = Bounds;
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        return mask.FillContains(GeometryUtil.Rotate(canvasPoint, center, -Rotation));
    }

    public virtual Rect Bounds => Rect.Empty;

    public virtual void Translate(double dx, double dy)
    {
    }

    public virtual bool HitTest(Point point, double tolerance)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        bounds.Inflate(tolerance, tolerance);
        return bounds.Contains(point);
    }
}

public sealed class RectAnnotation : Annotation
{
    public Rect Rect { get; set; }

    public override void Draw(DrawingContext dc)
    {
        var pen = new Pen(new SolidColorBrush(Color), Thickness) { LineJoin = PenLineJoin.Round };
        var fill = Fill
            ? new SolidColorBrush(Color)
            : null;

        if (CornerRadius > 0.5)
        {
            dc.DrawRoundedRectangle(fill, pen, Rect, CornerRadius, CornerRadius);
        }
        else
        {
            dc.DrawRectangle(fill, pen, Rect);
        }
    }

    public override Rect Bounds => Rect;

    public override void Translate(double dx, double dy)
    {
        Rect = new Rect(Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height);
    }
}

public sealed class EllipseAnnotation : Annotation
{
    public Rect Rect { get; set; }

    public override void Draw(DrawingContext dc)
    {
        var pen = new Pen(new SolidColorBrush(Color), Thickness);
        var fill = Fill
            ? new SolidColorBrush(Color)
            : null;
        var center = new Point(Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2);
        dc.DrawEllipse(fill, pen, center, Rect.Width / 2, Rect.Height / 2);
    }

    public override Rect Bounds => Rect;

    public override void Translate(double dx, double dy)
    {
        Rect = new Rect(Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height);
    }
}

public sealed class ArrowAnnotation : Annotation
{
    public Point Start { get; set; }

    public Point End { get; set; }

    public override void Draw(DrawingContext dc)
    {
        var direction = End - Start;
        var length = direction.Length;
        if (length < 1)
        {
            return;
        }

        var unit = direction / length;
        var headLength = Math.Max(18, Thickness * 5.5);
        var headWidth = headLength * 0.78;
        var brush = new SolidColorBrush(Color);
        var perpendicular = new Vector(-unit.Y, unit.X);
        var headBase = End - unit * headLength;
        var tailHalf = Math.Max(Thickness * 0.9, 1.8);

        // Swept-back barbs and a pointed tail: the two wings sit behind the
        // head base, while the tail narrows to a point at Start.
        var barbCenter = headBase - unit * (headLength * 0.22);
        var left = barbCenter + perpendicular * (headWidth / 2);
        var right = barbCenter - perpendicular * (headWidth / 2);
        var tailLeft = headBase + perpendicular * tailHalf;
        var tailRight = headBase - perpendicular * tailHalf;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Start, true, true);
            context.LineTo(tailLeft, true, false);
            context.LineTo(left, true, false);
            context.LineTo(End, true, false);
            context.LineTo(right, true, false);
            context.LineTo(tailRight, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    public override Rect Bounds => new(Start, End);

    public override void Translate(double dx, double dy)
    {
        Start = new Point(Start.X + dx, Start.Y + dy);
        End = new Point(End.X + dx, End.Y + dy);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        return GeometryUtil.DistanceToSegment(point, Start, End) <= tolerance + Thickness;
    }
}

public sealed class LineAnnotation : Annotation
{
    public Point Start { get; set; }

    public Point End { get; set; }

    public override void Draw(DrawingContext dc)
    {
        var pen = new Pen(new SolidColorBrush(Color), Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        dc.DrawLine(pen, Start, End);
    }

    public override Rect Bounds => new(Start, End);

    public override void Translate(double dx, double dy)
    {
        Start = new Point(Start.X + dx, Start.Y + dy);
        End = new Point(End.X + dx, End.Y + dy);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        return GeometryUtil.DistanceToSegment(point, Start, End) <= tolerance + Thickness;
    }
}

public sealed class PenAnnotation : Annotation
{
    public List<Point> Points { get; private set; } = new();

    /// <summary>Indices of points that start/end a Shift straight segment (drawn with lines, not curves).</summary>
    public HashSet<int> Corners { get; private set; } = new();

    public override Annotation Clone()
    {
        var copy = (PenAnnotation)base.Clone();
        copy.Points = new List<Point>(Points);
        copy.Corners = new HashSet<int>(Corners);
        return copy;
    }

    public bool Highlight { get; set; }

    public override void Draw(DrawingContext dc)
    {
        if (Points.Count == 0)
        {
            return;
        }

        var baseColor = Highlight
            ? Color.FromArgb(90, Color.R, Color.G, Color.B)
            : Color;

        if (Points.Count == 1)
        {
            dc.DrawEllipse(new SolidColorBrush(baseColor), null, Points[0], Thickness / 2, Thickness / 2);
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Points[0], false, false);
            if (Points.Count == 2)
            {
                context.LineTo(Points[1], true, true);
            }
            else
            {
                // Mid-point quadratic smoothing; Shift segments are straight
                // (a curve would bend their ends).
                for (var i = 1; i < Points.Count - 1; i++)
                {
                    var control = Points[i];
                    if (Corners.Contains(i) || Corners.Contains(i - 1))
                    {
                        context.LineTo(control, true, true);
                        continue;
                    }

                    var next = Points[i + 1];
                    context.QuadraticBezierTo(
                        control,
                        new Point((control.X + next.X) / 2, (control.Y + next.Y) / 2),
                        true,
                        true);
                }

                if (Corners.Contains(Points.Count - 1) || Corners.Contains(Points.Count - 2))
                {
                    context.LineTo(Points[^1], true, true);
                }
                else
                {
                    context.QuadraticBezierTo(Points[^1], Points[^1], true, true);
                }
            }
        }

        geometry.Freeze();

        var pen = new Pen(new SolidColorBrush(baseColor), Highlight ? Thickness * 4 : Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        dc.DrawGeometry(null, pen, geometry);
    }

    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0)
            {
                return Rect.Empty;
            }

            var minX = Points.Min(p => p.X);
            var minY = Points.Min(p => p.Y);
            var maxX = Points.Max(p => p.X);
            var maxY = Points.Max(p => p.Y);
            return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
        }
    }

    public override void Translate(double dx, double dy)
    {
        for (var i = 0; i < Points.Count; i++)
        {
            Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
        }
    }

    public override bool HitTest(Point point, double tolerance)
    {
        return GeometryUtil.DistanceToPolyline(point, Points) <= tolerance + Thickness * (Highlight ? 2 : 1);
    }
}

/// <summary>
/// Box mosaic. Like the brush mosaic it is a lens: the pixelated pattern covers
/// the whole canvas and the box only clips it, so moving, resizing or rotating
/// the box always pixelates whatever is underneath.
/// </summary>
public sealed class MosaicAnnotation : Annotation
{
    public Rect Rect { get; set; }

    /// <summary>Block size of the mosaic (its strength).</summary>
    public double BlockSize { get; set; } = 12;

    public BitmapSource? Pattern { get; set; }

    /// <summary>Canvas area the <see cref="Pattern"/> is stretched over.</summary>
    public Rect PatternRect { get; set; }

    public override void Draw(DrawingContext dc)
    {
        if (Rect.Width < 1 || Rect.Height < 1)
        {
            return;
        }

        dc.PushClip(new RectangleGeometry(Rect));
        if (Pattern != null)
        {
            // Undo the element rotation for the pattern so it stays aligned with the canvas.
            var center = new Point(Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2);
            dc.PushTransform(new RotateTransform(-Rotation, center.X, center.Y));
            MosaicBuilder.DrawPattern(dc, Pattern, PatternRect);
            dc.Pop();
        }
        else
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, 30, 30, 30)), null, Rect);
        }

        dc.Pop();
    }

    public override Rect Bounds => Rect;

    public override void Translate(double dx, double dy)
    {
        Rect = new Rect(Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height);
    }
}

/// <summary>
/// A repeated text watermark covering the whole canvas (capture selection or
/// pin image). It always sits at the bottom of the stack and has no frame:
/// hovering its glyphs highlights it, double-click edits the text.
/// </summary>
public sealed class WatermarkAnnotation : Annotation
{
    public WatermarkAnnotation()
    {
        Opacity = 0.25;
        Color = Color.FromRgb(0x80, 0x80, 0x80);
    }

    /// <summary>Area the watermark tiles; the host keeps it equal to the canvas.</summary>
    public Rect Rect { get; set; }

    public string Text { get; set; } = "Markshot";

    public double FontSize { get; set; } = 24;

    /// <summary>Tilt of the repeated text in degrees.</summary>
    public double Angle { get; set; } = -24;

    /// <summary>Shift of the tile pattern (dragging the watermark moves it).</summary>
    public Vector Shift { get; set; }

    public override bool Erasable => false;

    public override void Draw(DrawingContext dc) => Draw(dc, null);

    /// <summary>Draws the tiles; with <paramref name="highlight"/> the glyphs get an outline in that colour.</summary>
    public void Draw(DrawingContext dc, Brush? highlight)
    {
        if (Rect.Width < 4 || Rect.Height < 4 || string.IsNullOrEmpty(Text))
        {
            return;
        }

        var alpha = (byte)Math.Clamp(Opacity * 255, 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, Color.R, Color.G, Color.B));
        var text = Format(brush);
        var outline = highlight == null ? null : new Pen(highlight, Math.Max(1, FontSize / 14));

        dc.PushClip(new RectangleGeometry(Rect));
        foreach (var origin in TileOrigins(text))
        {
            dc.PushTransform(new RotateTransform(Angle, origin.X, origin.Y));
            if (outline != null)
            {
                dc.DrawGeometry(brush, outline, text.BuildGeometry(origin));
            }
            else
            {
                dc.DrawText(text, origin);
            }

            dc.Pop();
        }

        dc.Pop();
    }

    public override Rect Bounds => Rect;

    /// <summary>The watermark covers the canvas; moving it shifts the tile pattern inside it.</summary>
    public override void Translate(double dx, double dy) => Shift += new Vector(dx, dy);

    /// <summary>True when the point is on one of the repeated texts (not the gaps between them).</summary>
    public override bool HitTest(Point point, double tolerance)
    {
        if (!Rect.Contains(point) || string.IsNullOrEmpty(Text))
        {
            return false;
        }

        var text = Format(Brushes.Black);
        var box = new Rect(0, 0, text.Width, text.Height);
        box.Inflate(tolerance, tolerance);
        foreach (var origin in TileOrigins(text))
        {
            var local = new RotateTransform(-Angle, origin.X, origin.Y).Transform(point);
            if (box.Contains(new Point(local.X - origin.X, local.Y - origin.Y)))
            {
                return true;
            }
        }

        return false;
    }

    private FormattedText Format(Brush brush) => new(
        Text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily(TextAnnotation.FontFamilyName), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
        Math.Clamp(FontSize, 8, 200),
        brush,
        1.0);

    private IEnumerable<Point> TileOrigins(FormattedText text)
    {
        var stepX = Math.Max(60, text.Width + FontSize * 3);
        var stepY = Math.Max(40, text.Height + FontSize * 2.5);
        // Extend past the edges: the tiles are rotated.
        var margin = Math.Max(stepX, stepY) * 2;
        // The shift wraps by whole periods (two rows, since odd rows are staggered).
        var shiftX = Shift.X % stepX;
        var shiftY = Shift.Y % (stepY * 2);
        var row = 0;
        for (var y = Rect.Top - margin + shiftY; y < Rect.Bottom + margin + Math.Abs(shiftY); y += stepY, row++)
        {
            var offset = row % 2 == 0 ? 0 : stepX / 2;
            for (var x = Rect.Left - margin + shiftX; x < Rect.Right + margin + Math.Abs(shiftX); x += stepX)
            {
                yield return new Point(x + offset, y);
            }
        }
    }
}

public sealed class TextAnnotation : Annotation
{
    public const string FontFamilyName = "Microsoft YaHei UI";

    /// <summary>Line height as a multiple of the font size; the inline editor uses the same value.</summary>
    public const double LineSpacing = 1.3;

    private static readonly FontFamily Family = new(FontFamilyName);

    /// <summary>Top-left corner of the first line of text (excluding the background padding).</summary>
    public Point Position { get; set; }

    public string Text { get; set; } = string.Empty;

    public double FontSize { get; set; } = 20;

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public Color StrokeColor { get; set; } = Colors.White;

    public double StrokeThickness { get; set; }

    public double LineHeight => FontSize * LineSpacing;

    /// <summary>With <see cref="Annotation.Fill"/> the text sits on a rounded label in <see cref="Annotation.Color"/>.</summary>
    public double Padding => Fill ? Math.Round(FontSize * 0.3, 1) : 0;

    public Color TextColor => Fill ? ContrastOn(Color) : Color;

    public static Typeface TypefaceFor(bool bold, bool italic = false) =>
        new(Family, italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);

    public override void Draw(DrawingContext dc)
    {
        if (Fill)
        {
            var box = Bounds;
            if (!box.IsEmpty)
            {
                var radius = Math.Min(FontSize * 0.22, box.Height / 2);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color), null, box, radius, radius);
            }
        }

        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var formatted = Format(Text, new SolidColorBrush(TextColor));
        if (StrokeThickness > 0)
        {
            var geometry = formatted.BuildGeometry(Position);
            // Stroke first and fill on top so the outline sits outside the glyphs.
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(StrokeColor), StrokeThickness * 2)
            {
                LineJoin = PenLineJoin.Round
            }, geometry);
            dc.DrawGeometry(new SolidColorBrush(TextColor), null, geometry);
        }
        else
        {
            dc.DrawText(formatted, Position);
        }
    }

    public override Rect Bounds
    {
        get
        {
            var size = MeasureText();
            var padding = Padding;
            return new Rect(
                Position.X - padding,
                Position.Y - padding,
                size.Width + padding * 2,
                size.Height + padding * 2);
        }
    }

    /// <summary>Size of the laid-out text. An empty string still measures one caret-wide line.</summary>
    public Size MeasureText()
    {
        if (string.IsNullOrEmpty(Text))
        {
            return new Size(FontSize * 0.5, LineHeight);
        }

        var formatted = Format(Text, Brushes.Black);
        var lines = Text.Split('\n').Length;
        return new Size(
            Math.Max(FontSize * 0.5, formatted.WidthIncludingTrailingWhitespace),
            Math.Max(formatted.Height, lines * LineHeight));
    }

    public override bool HitTest(Point point, double tolerance)
    {
        var bounds = Bounds;
        bounds.Inflate(tolerance + 2, tolerance + 2);
        return bounds.Contains(point);
    }

    public override void Translate(double dx, double dy)
    {
        Position = new Point(Position.X + dx, Position.Y + dy);
    }

    public FormattedText Format(string text, Brush brush)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            TypefaceFor(Bold, Italic),
            FontSize,
            brush,
            1.0)
        {
            LineHeight = LineHeight
        };
    }

    public static Color ContrastOn(Color background)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
        return luminance > 0.6 ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White;
    }
}

public sealed class NumberAnnotation : Annotation
{
    public Point Center { get; set; }

    public int Number { get; set; } = 1;

    public double Radius { get; set; } = 14;

    public override void Draw(DrawingContext dc)
    {
        var brush = new SolidColorBrush(Color);
        dc.DrawEllipse(brush, new Pen(Brushes.White, Math.Max(1, Thickness / 2)), Center, Radius, Radius);

        var formatted = new FormattedText(
            Number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            Radius * 1.15,
            Brushes.White,
            1.0);

        dc.DrawText(formatted, new Point(Center.X - formatted.Width / 2, Center.Y - formatted.Height / 2 - 1));
    }

    public override Rect Bounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    public override void Translate(double dx, double dy)
    {
        Center = new Point(Center.X + dx, Center.Y + dy);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        return (point - Center).Length <= Radius + tolerance;
    }
}

/// <summary>One eraser stroke stored in an element's local frame.</summary>
public sealed class EraseStroke
{
    public EraseStroke(List<Point> points, double width, Size recordedSize, double strength = 1)
    {
        Points = points;
        Width = width;
        RecordedSize = recordedSize;
        Strength = Math.Clamp(strength, 0.05, 1);
    }

    /// <summary>How much the stroke erases (the eraser's opacity): 1 = fully.</summary>
    public double Strength { get; }

    public bool Full => Strength >= 0.999;

    public List<Point> Points { get; }

    public double Width { get; }

    public Size RecordedSize { get; }

    public EraseStroke Clone() => new(new List<Point>(Points), Width, RecordedSize, Strength);

    /// <summary>The area a round brush of <paramref name="width"/> covers along the points.</summary>
    public static Geometry Widen(IReadOnlyList<Point> points, double width)
    {
        if (points.Count == 1)
        {
            return new EllipseGeometry(points[0], width / 2, width / 2);
        }

        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToList(), true, true);
        }

        line.Freeze();
        var pen = new Pen(Brushes.Black, width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        return line.GetWidenedPathGeometry(pen);
    }
}

/// <summary>
/// Mosaic painted with a brush. The pattern covers the whole canvas and the
/// stroke only clips it, so the stroke works like a lens over the image.
/// </summary>
public sealed class MosaicBrushAnnotation : Annotation
{
    public List<Point> Points { get; private set; } = new();

    /// <summary>Indices of points that start/end a Shift straight segment.</summary>
    public HashSet<int> Corners { get; private set; } = new();

    /// <summary>Block size of the mosaic (its strength).</summary>
    public double BlockSize { get; set; } = 12;

    public BitmapSource? Pattern { get; set; }

    /// <summary>Canvas area the <see cref="Pattern"/> is stretched over.</summary>
    public Rect PatternRect { get; set; }

    public override Annotation Clone()
    {
        var copy = (MosaicBrushAnnotation)base.Clone();
        copy.Points = new List<Point>(Points);
        copy.Corners = new HashSet<int>(Corners);
        return copy;
    }

    public override void Draw(DrawingContext dc)
    {
        if (Points.Count == 0)
        {
            return;
        }

        var area = EraseStroke.Widen(Points, Thickness);
        dc.PushClip(area);
        if (Pattern != null)
        {
            var bounds = Bounds;
            var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            dc.PushTransform(new RotateTransform(-Rotation, center.X, center.Y));
            MosaicBuilder.DrawPattern(dc, Pattern, PatternRect);
            dc.Pop();
        }
        else
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, 30, 30, 30)), null, area.Bounds);
        }

        dc.Pop();
    }

    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0)
            {
                return Rect.Empty;
            }

            var minX = Points.Min(p => p.X);
            var minY = Points.Min(p => p.Y);
            var maxX = Points.Max(p => p.X);
            var maxY = Points.Max(p => p.Y);
            return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
        }
    }

    public override void Translate(double dx, double dy)
    {
        for (var i = 0; i < Points.Count; i++)
        {
            Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
        }
    }

    public override bool HitTest(Point point, double tolerance) =>
        GeometryUtil.DistanceToPolyline(point, Points) <= tolerance + Thickness / 2;
}

internal static class GeometryUtil
{
    public static Point Rotate(Point point, Point center, double degrees)
    {
        if (Math.Abs(degrees) < 0.001)
        {
            return point;
        }

        var radians = degrees * Math.PI / 180;
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return new Point(
            center.X + dx * Math.Cos(radians) - dy * Math.Sin(radians),
            center.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
    }

    public static double DistanceToSegment(Point point, Point start, Point end)
    {
        var segment = end - start;
        var lengthSquared = segment.X * segment.X + segment.Y * segment.Y;
        if (lengthSquared < 0.0001)
        {
            return (point - start).Length;
        }

        var toPoint = point - start;
        var t = (toPoint.X * segment.X + toPoint.Y * segment.Y) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        var projection = start + segment * t;
        return (point - projection).Length;
    }

    public static double DistanceToPolyline(Point point, IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
        {
            return double.MaxValue;
        }

        if (points.Count == 1)
        {
            return (point - points[0]).Length;
        }

        var best = double.MaxValue;
        for (var i = 1; i < points.Count; i++)
        {
            best = Math.Min(best, DistanceToSegment(point, points[i - 1], points[i]));
        }

        return best;
    }
}

public static class MosaicBuilder
{
    /// <summary>(Re)builds the canvas-wide pattern a mosaic element clips.</summary>
    public static void Rebuild(Annotation annotation, BitmapSource source)
    {
        switch (annotation)
        {
            case MosaicAnnotation box:
                (box.Pattern, box.PatternRect) = BuildCanvas(source, box.BlockSize);
                break;
            case MosaicBrushAnnotation brush:
                (brush.Pattern, brush.PatternRect) = BuildCanvas(source, brush.BlockSize);
                break;
        }
    }

    /// <summary>
    /// One sample per block for the whole canvas; the result is drawn stretched
    /// over (blocks × block size) so every block is exactly <paramref name="blockSize"/> px.
    /// </summary>
    public static (BitmapSource? Pattern, Rect Area) BuildCanvas(BitmapSource source, double blockSize)
    {
        var block = Math.Clamp((int)Math.Round(blockSize), 3, 64);
        var pattern = Build(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight), block);
        return pattern == null
            ? (null, Rect.Empty)
            : (pattern, new Rect(0, 0, pattern.PixelWidth * block, pattern.PixelHeight * block));
    }

    /// <summary>Draws a mosaic pattern with hard block edges (no smoothing) in any drawing context.</summary>
    public static void DrawPattern(DrawingContext dc, BitmapSource pattern, Rect area)
    {
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        group.Children.Add(new ImageDrawing(pattern, area));
        group.Freeze();
        dc.DrawDrawing(group);
    }

    public static BitmapSource? Build(BitmapSource source, Rect rect, int blockSize = 12)
    {
        var x = (int)Math.Floor(rect.X);
        var y = (int)Math.Floor(rect.Y);
        var width = (int)Math.Ceiling(rect.Width);
        var height = (int)Math.Ceiling(rect.Height);

        x = Math.Max(0, Math.Min(x, source.PixelWidth - 1));
        y = Math.Max(0, Math.Min(y, source.PixelHeight - 1));
        width = Math.Min(width, source.PixelWidth - x);
        height = Math.Min(height, source.PixelHeight - y);

        if (width < 1 || height < 1)
        {
            return null;
        }

        blockSize = Math.Clamp(blockSize, 3, 64);

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var smallWidth = Math.Max(1, (width + blockSize - 1) / blockSize);
        var smallHeight = Math.Max(1, (height + blockSize - 1) / blockSize);
        var small = new byte[smallWidth * smallHeight * 4];

        // Only the sampled row of each block row is read, not the whole image.
        var row = new byte[width * 4];
        for (var by = 0; by < smallHeight; by++)
        {
            var sy = Math.Min(height - 1, by * blockSize + blockSize / 2);
            converted.CopyPixels(new Int32Rect(x, y + sy, width, 1), row, width * 4, 0);
            for (var bx = 0; bx < smallWidth; bx++)
            {
                var sx = Math.Min(width - 1, bx * blockSize + blockSize / 2);
                var sourceIndex = sx * 4;
                var targetIndex = (by * smallWidth + bx) * 4;
                small[targetIndex] = row[sourceIndex];
                small[targetIndex + 1] = row[sourceIndex + 1];
                small[targetIndex + 2] = row[sourceIndex + 2];
                small[targetIndex + 3] = row[sourceIndex + 3];
            }
        }

        var pattern = BitmapSource.Create(
            smallWidth, smallHeight, 96, 96, PixelFormats.Bgra32, null, small, smallWidth * 4);
        pattern.Freeze();
        return pattern;
    }
}
