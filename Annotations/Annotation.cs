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
    MosaicBrush,
    Polyline,
    Cutout,
    Spotlight
}

/// <summary>Head of an arrow: 尖头 (swept, pointed tail), 线形 (open head), 三角 (filled head), 双向 (heads at both ends).</summary>
public enum ArrowStyle
{
    Sharp,
    Open,
    Triangle,
    Double
}

public abstract class Annotation
{
    public Color Color { get; set; } = Color.FromRgb(0xFF, 0x4D, 0x4F);

    public double Thickness { get; set; } = 3;

    public bool Fill { get; set; }

    public double CornerRadius { get; set; }

    public double Opacity { get; set; } = 1.0;

    public double Rotation { get; set; }

    /// <summary>Not drawn meanwhile (the editor's overlay shows it while a box / warp is open).</summary>
    public bool Hidden { get; set; }

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
        copy.Hidden = false; // only the live element is hidden, never a stored step
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

    /// <summary>Partly erasing strokes (eraser opacity below 100 % or a soft edge), each with how it erases.</summary>
    public IEnumerable<(Geometry Geometry, double Strength, double Softness, IReadOnlyList<Point> Points, double Width)> PartialMasks()
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
            var (points, width) = StrokeOn(stroke, bounds);
            yield return (EraseStroke.Widen(points, width), stroke.Strength, stroke.Softness, points, width);
        }
    }

    private static (List<Point> Points, double Width) StrokeOn(EraseStroke stroke, Rect bounds)
    {
        var sx = Math.Max(1, bounds.Width) / Math.Max(1, stroke.RecordedSize.Width);
        var sy = Math.Max(1, bounds.Height) / Math.Max(1, stroke.RecordedSize.Height);
        var points = stroke.Points.Select(p => new Point(bounds.X + p.X * sx, bounds.Y + p.Y * sy)).ToList();
        return (points, stroke.Width * Math.Sqrt(sx * sy));
    }

    private static Geometry StrokeGeometry(EraseStroke stroke, Rect bounds)
    {
        var (points, width) = StrokeOn(stroke, bounds);
        return EraseStroke.Widen(points, width);
    }

    /// <summary>Binds a canvas-space eraser stroke to this element.</summary>
    public void AddEraseStroke(IReadOnlyList<Point> canvasPoints, double width, double strength = 1, double softness = 0)
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
        EraseMasks.Add(new EraseStroke(local, width, new Size(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)), strength, softness));
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

    /// <summary>
    /// 选择: is the element's actual content here? (An outline without fill is only its line, a picture only its
    /// visible pixels, so what lies under the empty part can be picked.)
    /// </summary>
    public virtual bool HitContent(Point point, double tolerance) => HitTest(point, tolerance);

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

    public override bool HitContent(Point point, double tolerance)
    {
        var reach = tolerance + Thickness / 2;
        var outer = Rect;
        outer.Inflate(reach, reach);
        if (!outer.Contains(point))
        {
            return false;
        }

        if (Fill)
        {
            return true;
        }

        var inner = Rect;
        if (inner.Width <= reach * 2 || inner.Height <= reach * 2)
        {
            return true;
        }

        inner.Inflate(-reach, -reach);
        return !inner.Contains(point);
    }

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

    public override bool HitContent(Point point, double tolerance)
    {
        var reach = tolerance + Thickness / 2;
        var rx = Rect.Width / 2;
        var ry = Rect.Height / 2;
        var cx = Rect.X + rx;
        var cy = Rect.Y + ry;
        bool Within(double a, double b) => a > 0 && b > 0 && Math.Pow((point.X - cx) / a, 2) + Math.Pow((point.Y - cy) / b, 2) <= 1;
        if (!Within(rx + reach, ry + reach))
        {
            return false;
        }

        return Fill || !Within(rx - reach, ry - reach);
    }

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

    public ArrowStyle Style { get; set; }

    public override void Draw(DrawingContext dc)
    {
        var direction = End - Start;
        var length = direction.Length;
        if (length < 1)
        {
            return;
        }

        var unit = direction / length;
        if (Style != ArrowStyle.Sharp)
        {
            DrawStyled(dc, unit, length);
            return;
        }

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

    /// <summary>The other heads: an even shaft with an open or filled head (or one at each end).</summary>
    private void DrawStyled(DrawingContext dc, Vector unit, double length)
    {
        var brush = new SolidColorBrush(Color);
        var perpendicular = new Vector(-unit.Y, unit.X);
        if (Style == ArrowStyle.Open)
        {
            var open = Math.Min(Math.Max(12, Thickness * 4), length * 0.6);
            var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var head = new StreamGeometry();
            using (var context = head.Open())
            {
                context.BeginFigure(End - unit * open + perpendicular * (open * 0.55), false, false);
                context.LineTo(End, true, true);
                context.LineTo(End - unit * open - perpendicular * (open * 0.55), true, true);
                context.BeginFigure(Start, false, false);
                context.LineTo(End, true, false);
            }

            head.Freeze();
            dc.DrawGeometry(null, pen, head);
            return;
        }

        var both = Style == ArrowStyle.Double;
        var headLength = Math.Min(Math.Max(14, Thickness * 4.5), length / (both ? 2.2 : 1.2));
        var halfWidth = headLength * 0.5;
        var shaftStart = both ? Start + unit * (headLength * 0.9) : Start;
        var shaftEnd = End - unit * (headLength * 0.9);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            AddHead(context, End, unit, perpendicular, headLength, halfWidth);
            if (both)
            {
                AddHead(context, Start, -unit, perpendicular, headLength, halfWidth);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
        if ((shaftEnd - shaftStart) * unit > 0)
        {
            var shaft = new Pen(brush, Thickness) { StartLineCap = both ? PenLineCap.Flat : PenLineCap.Round, EndLineCap = PenLineCap.Flat };
            dc.DrawLine(shaft, shaftStart, shaftEnd);
        }
    }

    private static void AddHead(StreamGeometryContext context, Point tip, Vector unit, Vector perpendicular, double length, double halfWidth)
    {
        var headBase = tip - unit * length;
        context.BeginFigure(tip, true, true);
        context.LineTo(headBase + perpendicular * halfWidth, true, false);
        context.LineTo(headBase - perpendicular * halfWidth, true, false);
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

public class PenAnnotation : Annotation
{
    public List<Point> Points { get; private set; } = new();

    /// <summary>Indices of points that start/end a Shift straight segment (drawn with lines, not curves).</summary>
    public HashSet<int> Corners { get; private set; } = new();

    public override Annotation Clone()
    {
        var copy = (PenAnnotation)base.Clone();
        copy.Points = new List<Point>(Points);
        copy.Corners = new HashSet<int>(Corners);
        copy.Pressures = Pressures == null ? null : new List<float>(Pressures);
        return copy;
    }

    public bool Highlight { get; set; }

    /// <summary>软边: how much of the width fades out towards the edge (0 hard … 1 from the very middle).</summary>
    public double Softness { get; set; }

    /// <summary>
    /// Draws the stroke as nested layers, widest first, each adding a little coverage so that the sum is
    /// a smooth ramp from the edge to the solid middle. Hard strokes are the one plain layer.
    /// </summary>
    private void DrawLayers(DrawingContext dc, Color color, double width, Action<double, Brush> draw)
    {
        if (Softness < 0.01)
        {
            draw(1, new SolidColorBrush(color));
            return;
        }

        var soft = Math.Clamp(Softness, 0, 1);
        var layers = Math.Clamp((int)Math.Ceiling(width * soft / 1.5), 2, Pressures != null ? 6 : 12);
        var see = color.A < 255;
        if (see)
        {
            dc.PushOpacity(color.A / 255.0); // the layers add up first, then the colour's own transparency
        }

        var covered = 0.0;
        for (var k = 1; k <= layers; k++)
        {
            var t = k / (double)layers;
            var target = t * t * (3 - 2 * t);
            var alpha = k == layers ? 1 : 1 - (1 - target) / (1 - covered);
            covered = target;
            var scale = Math.Max(0.04, 1 - soft * (k - 1) / (layers - 1));
            draw(scale, new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B)));
        }

        if (see)
        {
            dc.Pop();
        }
    }

    /// <summary>Tablet pen pressure (0…1) of each point; null for a stroke drawn with the mouse.</summary>
    public List<float>? Pressures { get; set; }

    /// <summary>Width at a pressure: 0.5 (a pen resting normally) is the set thickness.</summary>
    public double WidthAt(float pressure) => (Highlight ? Thickness * 4 : Thickness) * Math.Clamp(0.2 + 1.6 * pressure, 0.2, 1.8);

    /// <summary>The stroke with a width per point: one filled shape, so it overlaps itself cleanly.</summary>
    private Geometry PressureShape(double scale = 1)
    {
        var shape = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = shape.Open())
        {
            void Dot(Point at, double radius)
            {
                var size = new Size(radius, radius);
                context.BeginFigure(new Point(at.X - radius, at.Y), true, true);
                context.ArcTo(new Point(at.X + radius, at.Y), size, 0, false, SweepDirection.Clockwise, true, false);
                context.ArcTo(new Point(at.X - radius, at.Y), size, 0, false, SweepDirection.Clockwise, true, false);
            }

            for (var i = 0; i < Points.Count; i++)
            {
                var radius = WidthAt(Pressures![i]) * scale / 2;
                Dot(Points[i], radius);
                if (i == 0)
                {
                    continue;
                }

                var from = Points[i - 1];
                var along = Points[i] - from;
                if (along.Length < 0.01)
                {
                    continue;
                }

                along.Normalize();
                var side = new Vector(-along.Y, along.X);
                var before = WidthAt(Pressures[i - 1]) * scale / 2;
                // Same winding as the dots: with Nonzero an opposite one would punch holes.
                context.BeginFigure(from + side * before, true, true);
                context.LineTo(from - side * before, true, false);
                context.LineTo(Points[i] - side * radius, true, false);
                context.LineTo(Points[i] + side * radius, true, false);
            }
        }

        shape.Freeze();
        return shape;
    }

    public override void Draw(DrawingContext dc)
    {
        if (Points.Count == 0)
        {
            return;
        }

        var baseColor = Highlight
            ? Color.FromArgb(90, Color.R, Color.G, Color.B)
            : Color;

        if (Pressures != null && Pressures.Count == Points.Count)
        {
            DrawLayers(dc, baseColor, Pressures.Max(WidthAt), (scale, brush) => dc.DrawGeometry(brush, null, PressureShape(scale)));
            return;
        }

        if (Points.Count == 1)
        {
            DrawLayers(dc, baseColor, Thickness, (scale, brush) => dc.DrawEllipse(brush, null, Points[0], Thickness * scale / 2, Thickness * scale / 2));
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

        var strokeWidth = Highlight ? Thickness * 4 : Thickness;
        DrawLayers(dc, baseColor, strokeWidth, (scale, brush) => dc.DrawGeometry(null, new Pen(brush, strokeWidth * scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        }, geometry));
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
/// A picture on the image editor's canvas (the opened picture, pasted ones): moved, resized, turned
/// and layered like any element. Not touched by the eraser (抠除 cuts through it).
/// </summary>
public sealed class ImageAnnotation : Annotation
{
    public Rect Rect { get; set; }

    /// <summary>What is shown (colours applied; a smaller copy while a slider moves).</summary>
    public BitmapSource? Image { get; set; }

    /// <summary>The pixels before the colour settings; null = <see cref="Image"/>.</summary>
    public BitmapSource? Origin { get; set; }

    public ImageAdjust? Adjust { get; set; }

    /// <summary>Only where the picture shows: a see-through part lets the click through to what lies under it.</summary>
    public override bool HitContent(Point point, double tolerance)
    {
        if (!Rect.Contains(point) || Rect.Width <= 0 || Rect.Height <= 0)
        {
            return false;
        }

        if (Image is not { } image || (image.Format != PixelFormats.Bgra32 && image.Format != PixelFormats.Pbgra32))
        {
            return true;
        }

        var x = Math.Clamp((int)((point.X - Rect.X) / Rect.Width * image.PixelWidth), 0, image.PixelWidth - 1);
        var y = Math.Clamp((int)((point.Y - Rect.Y) / Rect.Height * image.PixelHeight), 0, image.PixelHeight - 1);
        var reach = (int)Math.Ceiling(Math.Max(0, tolerance) * image.PixelWidth / Rect.Width);
        // Around the point (the hand is not that exact), a few pixels are enough to tell.
        foreach (var (dx, dy) in new[] { (0, 0), (-reach, 0), (reach, 0), (0, -reach), (0, reach) })
        {
            var px = Math.Clamp(x + dx, 0, image.PixelWidth - 1);
            var py = Math.Clamp(y + dy, 0, image.PixelHeight - 1);
            var pixel = new byte[4];
            image.CopyPixels(new Int32Rect(px, py, 1, 1), pixel, 4, 0);
            if (pixel[3] >= 16)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>元素样式 (canvas pixels): rounded corners, a soft drop shadow below, an outline drawn inside the edge.</summary>
    public double Radius { get; set; }

    public double ShadowSize { get; set; }

    public double BorderWidth { get; set; }

    public Color BorderColor { get; set; } = Colors.White;

    /// <summary>How far the style reaches past the picture (the shadow; a shape's outline lies outside it too).</summary>
    public double StyleReach => Math.Max(ShadowSize > 0.5 ? ShadowSize * 1.4 : 0, BorderWidth > 0.25 && IsShaped ? BorderWidth : 0);

    private BitmapSource? _shapeOf;
    private bool _shaped;
    private (BitmapSource Source, double Width, Color Color, BitmapSource Image, double Margin)? _outline;
    private (BitmapSource Source, double Size, BitmapSource Image, double Margin)? _shadow;

    /// <summary>The pixels that decide the shape (colours and a slider's small copy do not change it).</summary>
    private BitmapSource? ShapeSource => Origin ?? Image;

    /// <summary>A free shape (cut along a path, warped, a picture with see-through parts): no 圆角, the style follows the shape.</summary>
    public bool IsShaped
    {
        get
        {
            if (ShapeSource is not { } source)
            {
                return false;
            }

            if (!ReferenceEquals(_shapeOf, source))
            {
                _shapeOf = source;
                _shaped = ImageOps.HasShape(source);
            }

            return _shaped;
        }
    }

    private void DrawShapeStyle(DrawingContext dc, bool outline)
    {
        var source = ShapeSource!;
        double sx = Rect.Width / source.PixelWidth, sy = Rect.Height / source.PixelHeight;
        if (!outline)
        {
            var size = Math.Round(ShadowSize / Math.Max(0.001, (sx + sy) / 2) * 2) / 2;
            if (_shadow is not { } shadow || !ReferenceEquals(shadow.Source, source) || shadow.Size != size)
            {
                var (image, margin) = ImageOps.ShapeShadow(source, size);
                shadow = (source, size, image, margin);
                _shadow = shadow;
            }

            var drop = ShadowSize * 0.35;
            dc.DrawImage(shadow.Image, new Rect(Rect.X - shadow.Margin * sx, Rect.Y - shadow.Margin * sy + drop,
                Rect.Width + shadow.Margin * sx * 2, Rect.Height + shadow.Margin * sy * 2));
            return;
        }

        var width = Math.Round(BorderWidth / Math.Max(0.001, (sx + sy) / 2) * 4) / 4;
        if (_outline is not { } ring || !ReferenceEquals(ring.Source, source) || ring.Width != width || ring.Color != BorderColor)
        {
            var (image, margin) = ImageOps.ShapeOutline(source, width, BorderColor);
            ring = (source, width, BorderColor, image, margin);
            _outline = ring;
        }

        dc.DrawImage(ring.Image, new Rect(Rect.X - ring.Margin * sx, Rect.Y - ring.Margin * sy,
            Rect.Width + ring.Margin * sx * 2, Rect.Height + ring.Margin * sy * 2));
    }

    /// <summary>How pictures are scaled in the render going on (the editor: crisp pixels when zoomed far in).</summary>
    public static BitmapScalingMode Scaling { get; set; } = BitmapScalingMode.HighQuality;

    public override bool Erasable => false;

    public override void Draw(DrawingContext dc)
    {
        if (Image == null || Hidden || Rect.Width < 1 || Rect.Height < 1)
        {
            return;
        }

        var styled = Radius > 0.25 || ShadowSize > 0.5 || BorderWidth > 0.25;
        var shaped = styled && IsShaped;
        var radius = shaped ? 0 : Math.Clamp(Radius, 0, Math.Min(Rect.Width, Rect.Height) / 2);
        if (shaped)
        {
            if (ShadowSize > 0.5)
            {
                DrawShapeStyle(dc, outline: false);
            }

            if (BorderWidth > 0.25)
            {
                DrawShapeStyle(dc, outline: true); // under the picture, so its soft edge stays clean
            }
        }
        else if (ShadowSize > 0.5)
        {
            // A soft shadow: rings that grow and fade, a little below the picture.
            var layers = Math.Clamp((int)Math.Ceiling(ShadowSize / 2), 3, 14);
            var drop = ShadowSize * 0.35;
            var alpha = (byte)Math.Max(4, 110 / layers);
            for (var k = layers; k >= 1; k--)
            {
                var grow = ShadowSize * k / layers;
                var ring = new Rect(Rect.X - grow, Rect.Y - grow + drop, Rect.Width + grow * 2, Rect.Height + grow * 2);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)), null, ring, radius + grow, radius + grow);
            }
        }

        // Its own scaling: the canvas around it is nearest-neighbour (for the mosaic blocks).
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, Scaling);
        using (var context = group.Open())
        {
            if (radius > 0.25)
            {
                context.PushClip(new RectangleGeometry(Rect, radius, radius));
            }

            context.DrawImage(Image, Rect);
            if (radius > 0.25)
            {
                context.Pop();
            }
        }

        dc.DrawDrawing(group);
        if (BorderWidth > 0.25 && !shaped)
        {
            var inset = BorderWidth / 2;
            var inner = new Rect(Rect.X + inset, Rect.Y + inset, Math.Max(0, Rect.Width - BorderWidth), Math.Max(0, Rect.Height - BorderWidth));
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(BorderColor), BorderWidth), inner, Math.Max(0, radius - inset), Math.Max(0, radius - inset));
        }
    }

    public override Rect Bounds => Rect;

    public override void Translate(double dx, double dy) => Rect = new Rect(Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height);
}

/// <summary>
/// 抠除: an eraser stroke that cuts a transparent hole through the picture and every annotation
/// (above and below it, drawn later or earlier). Not drawn itself, never selected; the holes of all
/// cut-outs clip the whole picture (<see cref="Cutouts"/>).
/// </summary>
public sealed class CutoutAnnotation : PenAnnotation
{
    private Geometry? _shape;
    private string? _shapeKey;

    public override bool Erasable => false;

    public override void Draw(DrawingContext dc)
    {
    }

    public override bool HitTest(Point point, double tolerance) => false;

    /// <summary>The hole (the stroke widened to its width).</summary>
    public Geometry Shape()
    {
        var key = $"{Points.Count}:{(Points.Count > 0 ? Points[0] : default)}:{(Points.Count > 0 ? Points[^1] : default)}:{Thickness}";
        if (_shape == null || _shapeKey != key)
        {
            _shape = EraseStroke.Widen(Points, Thickness);
            if (_shape.CanFreeze)
            {
                _shape.Freeze();
            }

            _shapeKey = key;
        }

        return _shape;
    }

    public override Annotation Clone()
    {
        var copy = (CutoutAnnotation)base.Clone();
        copy._shape = null;
        copy._shapeKey = null;
        return copy;
    }
}

/// <summary>The holes cut by 抠除.</summary>
public static class Cutouts
{
    public static bool Any(IReadOnlyList<Annotation> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is CutoutAnnotation)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The area without the holes (the area itself when there are none).</summary>
    public static Geometry Exclude(IReadOnlyList<Annotation> items, Geometry area)
    {
        if (!Any(items))
        {
            return area;
        }

        var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var cutout in items.OfType<CutoutAnnotation>())
        {
            holes.Children.Add(cutout.Shape());
        }

        var result = new CombinedGeometry(GeometryCombineMode.Exclude, area, holes);
        result.Freeze();
        return result;
    }
}

/// <summary>
/// 折线: straight segments between clicked corners. A pen stroke underneath, so moving, resizing,
/// mirroring, cropping and erasing treat it like one.
/// </summary>
public sealed class PolylineAnnotation : PenAnnotation
{
    public override void Draw(DrawingContext dc)
    {
        if (Points.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Points[0], false, false);
            context.PolyLineTo(Points.Skip(1).ToList(), true, true);
        }

        geometry.Freeze();
        var pen = new Pen(new SolidColorBrush(Color), Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        dc.DrawGeometry(null, pen, geometry);
    }
}

/// <summary>
/// A polyline being placed: a click fixes a corner and the last point follows the mouse; a drag
/// draws a segment too. Double-click / Enter / right click ends it, Backspace takes a corner back,
/// Shift keeps a segment at 45° steps.
/// </summary>
public sealed class PolylineDraft
{
    private Point _press;

    public PolylineDraft(PolylineAnnotation target, Point start)
    {
        Target = target;
        target.Points.Clear();
        target.Points.Add(start);
        target.Points.Add(start); // follows the mouse
        _press = start;
    }

    public PolylineAnnotation Target { get; }

    /// <summary>A click: the point under the mouse becomes a corner.</summary>
    public void Press(Point point, bool shift)
    {
        Move(point, shift);
        _press = point;
        Target.Points.Add(Target.Points[^1]);
    }

    /// <summary>Release after dragging: the dragged segment ends in a corner.</summary>
    public void Release(Point point, bool shift, double slop)
    {
        if ((point - _press).Length > slop)
        {
            Press(point, shift);
        }
    }

    public void Move(Point point, bool shift) =>
        Target.Points[^1] = shift ? Snap(Target.Points[^2], point) : point;

    /// <summary>Backspace: the last corner goes. False when only the start is left.</summary>
    public bool RemoveLast()
    {
        if (Target.Points.Count <= 2)
        {
            return false;
        }

        Target.Points.RemoveAt(Target.Points.Count - 2);
        return true;
    }

    /// <summary>Done: the point that followed the mouse and repeated points go. False = no line left.</summary>
    public bool Finish()
    {
        var points = Target.Points;
        points.RemoveAt(points.Count - 1);
        for (var i = points.Count - 1; i > 0; i--)
        {
            if ((points[i] - points[i - 1]).Length < 0.5)
            {
                points.RemoveAt(i);
            }
        }

        return points.Count >= 2;
    }

    /// <summary>The nearest 45° direction from <paramref name="from"/>.</summary>
    public static Point Snap(Point from, Point to)
    {
        var delta = to - from;
        var angle = Math.Round(Math.Atan2(delta.Y, delta.X) / (Math.PI / 4)) * (Math.PI / 4);
        var length = delta.Length;
        return new Point(from.X + Math.Cos(angle) * length, from.Y + Math.Sin(angle) * length);
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

    /// <summary>字体 (a family installed on this computer); null = Microsoft YaHei UI.</summary>
    public string? FontName { get; set; }

    private static readonly Dictionary<string, FontFamily> Families = new();

    public static FontFamily FamilyOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Family;
        }

        if (!Families.TryGetValue(name, out var family))
        {
            family = new FontFamily(name);
            Families[name] = family;
        }

        return family;
    }

    public static Typeface TypefaceFor(bool bold, bool italic = false, string? fontName = null) =>
        new(FamilyOf(fontName), italic ? FontStyles.Italic : FontStyles.Normal,
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
            TypefaceFor(Bold, Italic, FontName),
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

    /// <summary>空心: a ring in the colour with the number in the colour (else a disc with a light number).</summary>
    public bool Hollow { get; set; }

    /// <summary>描边: a light outline around the badge, so it reads on any background.</summary>
    public bool Outlined { get; set; } = true;

    /// <summary>Where an arrow from the badge points (dragged out while placing it); null = no arrow.</summary>
    public Point? Tail { get; set; }

    private double RingWidth => Math.Max(1.5, Radius * 0.16);

    public override void Draw(DrawingContext dc)
    {
        var ink = new SolidColorBrush(Color);
        var outline = Outlined ? new Pen(Brushes.White, Math.Max(1.2, Radius * 0.1)) : null;
        if (Tail is { } tail && (tail - Center).Length > Radius * 1.3 + 8)
        {
            DrawTail(dc, ink, tail, outline);
        }

        if (Hollow)
        {
            if (outline != null)
            {
                dc.DrawEllipse(null, new Pen(Brushes.White, RingWidth + outline.Thickness * 2), Center, Radius - RingWidth / 2, Radius - RingWidth / 2);
            }

            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)), new Pen(ink, RingWidth), Center, Radius - RingWidth / 2, Radius - RingWidth / 2);
        }
        else
        {
            dc.DrawEllipse(ink, outline, Center, Radius, Radius);
        }

        var digits = Number.ToString(CultureInfo.InvariantCulture);
        // Bigger figures (two digits a little smaller so they still fit the disc).
        var size = Radius * (digits.Length >= 3 ? 0.95 : digits.Length == 2 ? 1.2 : 1.42);
        var formatted = new FormattedText(
            digits,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            size,
            Hollow ? ink : Brushes.White,
            1.0);
        // Centred on the figures' ink (digits sit high in their line box).
        var top = Center.Y - (formatted.Baseline - size * 0.36);
        dc.DrawText(formatted, new Point(Center.X - formatted.Width / 2, top));
    }

    /// <summary>The arrow from the badge's edge to <paramref name="tail"/>.</summary>
    private void DrawTail(DrawingContext dc, Brush ink, Point tail, Pen? outline)
    {
        var direction = tail - Center;
        direction.Normalize();
        var from = Center + direction * (Radius + Math.Max(3, Radius * 0.3)); // a little gap after the badge
        var width = Math.Max(2, Radius * 0.2);
        var head = Math.Max(8, width * 3.2);
        var shaftEnd = tail - direction * head * 0.8;
        var normal = new Vector(-direction.Y, direction.X);
        var arrow = new StreamGeometry();
        using (var context = arrow.Open())
        {
            context.BeginFigure(tail, true, true);
            context.LineTo(tail - direction * head + normal * head * 0.55, true, true);
            context.LineTo(tail - direction * head - normal * head * 0.55, true, true);
        }

        arrow.Freeze();
        if (outline != null)
        {
            var halo = new Pen(Brushes.White, width + outline.Thickness * 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(halo, from, shaftEnd);
            dc.DrawGeometry(Brushes.White, new Pen(Brushes.White, outline.Thickness * 2) { LineJoin = PenLineJoin.Round }, arrow);
        }

        dc.DrawLine(new Pen(ink, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Flat }, from, shaftEnd);
        dc.DrawGeometry(ink, null, arrow);
    }

    public override Rect Bounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    public override void Translate(double dx, double dy)
    {
        Center = new Point(Center.X + dx, Center.Y + dy);
        if (Tail is { } tail)
        {
            Tail = new Point(tail.X + dx, tail.Y + dy);
        }
    }

    public override bool HitTest(Point point, double tolerance)
    {
        if ((point - Center).Length <= Radius + tolerance)
        {
            return true;
        }

        if (Tail is not { } tail)
        {
            return false;
        }

        // On the arrow.
        var line = tail - Center;
        var t = Math.Clamp(Vector.Multiply(point - Center, line) / Math.Max(1e-6, line.LengthSquared), 0, 1);
        return (point - (Center + line * t)).Length <= Math.Max(4, Radius * 0.2) + tolerance;
    }
}

/// <summary>One eraser stroke stored in an element's local frame.</summary>
public sealed class EraseStroke
{
    public EraseStroke(List<Point> points, double width, Size recordedSize, double strength = 1, double softness = 0)
    {
        Points = points;
        Width = width;
        RecordedSize = recordedSize;
        Strength = Math.Clamp(strength, 0.05, 1);
        Softness = Math.Clamp(softness, 0, 1);
    }

    /// <summary>软边: how much of the width erases only partly, fading out towards the edge (0 = a hard edge).</summary>
    public double Softness { get; }

    /// <summary>How much the stroke erases (the eraser's opacity): 1 = fully.</summary>
    public double Strength { get; }

    public bool Full => Strength >= 0.999 && Softness < 0.01;

    public List<Point> Points { get; }

    public double Width { get; }

    public Size RecordedSize { get; }

    public EraseStroke Clone() => new(new List<Point>(Points), Width, RecordedSize, Strength, Softness);

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

/// <summary>
/// 聚焦: a rectangle or ellipse that stays clear while everything around it is darkened. Several of them darken the
/// picture once, together (each one's area is clear); the edge can be feathered — only the edge, the inside stays clear
/// and the outside fully dark. No outline, no colour.
/// </summary>
public sealed class SpotlightAnnotation : Annotation
{
    public Rect Rect { get; set; }

    public bool Ellipse { get; set; }

    /// <summary>How dark the outside is (0–1).</summary>
    public double Darkness { get; set; } = 0.55;

    /// <summary>Width of the soft edge (canvas pixels), centred on the shape's outline.</summary>
    public double Feather { get; set; } = 12;

    /// <summary>The spotlights drawn together (set by <see cref="Spotlights.Prepare"/>); the first one draws them all.</summary>
    internal List<SpotlightAnnotation>? Group;

    internal bool Lead = true;

    public override bool Erasable => false;

    public override Rect Bounds => Rect;

    public override void Translate(double dx, double dy) => Rect = new Rect(Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height);

    public override Annotation Clone()
    {
        var copy = (SpotlightAnnotation)base.Clone();
        copy.Group = null;
        copy.Lead = true;
        return copy;
    }

    public override bool HitTest(Point point, double tolerance)
    {
        var area = Rect;
        area.Inflate(tolerance, tolerance);
        return area.Contains(point);
    }

    /// <summary>选择: its edge (the inside is the picture under it).</summary>
    public override bool HitContent(Point point, double tolerance)
    {
        var reach = tolerance + 4;
        var outer = Rect;
        outer.Inflate(reach, reach);
        if (!outer.Contains(point))
        {
            return false;
        }

        var inner = Rect;
        if (inner.Width <= reach * 2 || inner.Height <= reach * 2)
        {
            return true;
        }

        inner.Inflate(-reach, -reach);
        return !inner.Contains(point);
    }

    /// <summary>The shape grown (or shrunk, negative) by <paramref name="by"/> pixels, turned with the element.</summary>
    internal Geometry Shape(double by)
    {
        var area = Rect;
        var grow = Math.Max(by, -Math.Min(area.Width, area.Height) / 2 + 0.5);
        area.Inflate(grow, grow);
        Geometry shape = Ellipse ? new EllipseGeometry(area) : new RectangleGeometry(area);
        if (Math.Abs(Rotation) >= 0.01)
        {
            shape.Transform = new RotateTransform(Rotation, Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2);
        }

        return shape;
    }

    public override void Draw(DrawingContext dc)
    {
        if (!Lead)
        {
            return; // drawn with the group's first spotlight
        }

        Spotlights.DrawGroup(dc, Group ?? new List<SpotlightAnnotation> { this });
    }
}

/// <summary>Draws the darkened outside of one or more spotlights at once.</summary>
public static class Spotlights
{
    private static readonly Rect Everywhere = new(-100000, -100000, 200000, 200000); // clipped to the picture by whoever draws

    /// <summary>Before a canvas is drawn: its spotlights are grouped, the first draws them all.</summary>
    public static void Prepare(IEnumerable<Annotation> items)
    {
        var all = items.OfType<SpotlightAnnotation>().Where(spot => !spot.Hidden).ToList();
        for (var i = 0; i < all.Count; i++)
        {
            all[i].Group = all;
            all[i].Lead = i == 0;
        }
    }

    public static void DrawGroup(DrawingContext dc, IReadOnlyList<SpotlightAnnotation> group)
    {
        var shown = group.Where(spot => !spot.Hidden && spot.Rect.Width > 0.5 && spot.Rect.Height > 0.5).ToList();
        if (shown.Count == 0)
        {
            return;
        }

        var darkness = Math.Clamp(shown.Max(spot => spot.Darkness), 0, 1);
        Geometry Holes(double t) // t: 0 = the inner edge of the feather, 1 = its outer edge
        {
            var union = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var spot in shown)
            {
                var feather = Math.Max(0, spot.Feather);
                union.Children.Add(spot.Shape(feather * (t - 0.5)));
            }

            return union;
        }

        Brush Dark(double alpha)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), 0, 0, 0));
            brush.Freeze();
            return brush;
        }

        var everywhere = new RectangleGeometry(Everywhere);
        var soft = shown.Any(spot => spot.Feather >= 0.5);
        if (!soft)
        {
            dc.DrawGeometry(Dark(darkness), null, new CombinedGeometry(GeometryCombineMode.Exclude, everywhere, Holes(0.5)));
            return;
        }

        // The soft edge in steps from clear (inside) to the full darkness (outside).
        const int steps = 12;
        dc.DrawGeometry(Dark(darkness), null, new CombinedGeometry(GeometryCombineMode.Exclude, everywhere, Holes(1)));
        var inner = Holes(0);
        for (var i = 0; i < steps; i++)
        {
            var outer = Holes((i + 1) / (double)steps);
            dc.DrawGeometry(Dark(darkness * (i + 0.5) / steps), null, new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner));
            inner = outer;
        }
    }
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

        // The average colour of each block (premultiplied while summing): one sampled pixel could be a
        // coloured edge of anti-aliased text, which made white text come out in every colour.
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(new Int32Rect(x, y, width, height), pixels, width * 4, 0);
        Parallel.For(0, smallHeight, by =>
        {
            var y0 = by * blockSize;
            var y1 = Math.Min(height, y0 + blockSize);
            for (var bx = 0; bx < smallWidth; bx++)
            {
                var x0 = bx * blockSize;
                var x1 = Math.Min(width, x0 + blockSize);
                double b = 0, g = 0, r = 0, alpha = 0;
                var count = 0;
                for (var py = y0; py < y1; py++)
                {
                    var i = (py * width + x0) * 4;
                    for (var px = x0; px < x1; px++, i += 4)
                    {
                        var a = pixels[i + 3];
                        b += pixels[i] * a;
                        g += pixels[i + 1] * a;
                        r += pixels[i + 2] * a;
                        alpha += a;
                        count++;
                    }
                }

                var t = (by * smallWidth + bx) * 4;
                if (alpha > 0)
                {
                    small[t] = (byte)Math.Round(b / alpha);
                    small[t + 1] = (byte)Math.Round(g / alpha);
                    small[t + 2] = (byte)Math.Round(r / alpha);
                }

                small[t + 3] = (byte)Math.Round(alpha / Math.Max(1, count));
            }
        });

        var pattern = BitmapSource.Create(
            smallWidth, smallHeight, 96, 96, PixelFormats.Bgra32, null, small, smallWidth * 4);
        pattern.Freeze();
        return pattern;
    }
}
