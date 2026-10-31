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
    Eraser
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
        var tailHalf = Math.Max(Thickness * 0.32, 0.9);

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
    public List<Point> Points { get; } = new();

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
                for (var i = 1; i < Points.Count - 1; i++)
                {
                    var control = Points[i];
                    var next = Points[i + 1];
                    context.QuadraticBezierTo(
                        control,
                        new Point((control.X + next.X) / 2, (control.Y + next.Y) / 2),
                        true,
                        true);
                }

                context.QuadraticBezierTo(Points[^1], Points[^1], true, true);
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

public sealed class MosaicAnnotation : Annotation
{
    public Rect Rect { get; set; }

    public BitmapSource? Pattern { get; set; }

    public override void Draw(DrawingContext dc)
    {
        if (Pattern != null)
        {
            dc.DrawImage(Pattern, Rect);
            return;
        }

        // While the user is still dragging, show a translucent placeholder.
        dc.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(120, 30, 30, 30)),
            null,
            Rect);
    }

    public override Rect Bounds => Rect;

    public override void Translate(double dx, double dy)
    {
        Rect = new Rect(Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height);
    }
}

public sealed class WatermarkAnnotation : Annotation
{
    public Rect Rect { get; set; }

    public string Text { get; set; } = "Markshot";

    public double FontSize { get; set; } = 24;

    public Color StrokeColor { get; set; } = Color.FromRgb(0x20, 0x20, 0x20);

    public double StrokeThickness { get; set; }

    public override void Draw(DrawingContext dc)
    {
        if (Rect.Width < 4 || Rect.Height < 4)
        {
            return;
        }

        var alpha = (byte)Math.Clamp(Opacity * 255, 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, Color.R, Color.G, Color.B));
        var fontSize = Math.Clamp(FontSize, 10, 160);
        var text = new FormattedText(
            Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            fontSize,
            brush,
            1.0);

        dc.PushClip(new RectangleGeometry(Rect));
        var stepX = Math.Max(80, text.Width + 46);
        var stepY = Math.Max(46, text.Height + 34);
        var row = 0;
        for (var y = Rect.Top - stepY; y < Rect.Bottom + stepY; y += stepY, row++)
        {
            var offset = row % 2 == 0 ? 0 : stepX / 2;
            for (var x = Rect.Left - stepX; x < Rect.Right + stepX; x += stepX)
            {
                var point = new Point(x + offset, y);
                if (StrokeThickness > 0)
                {
                    var geometry = text.BuildGeometry(point);
                    dc.DrawGeometry(brush,
                        new Pen(new SolidColorBrush(Color.FromArgb(alpha, StrokeColor.R, StrokeColor.G, StrokeColor.B)), StrokeThickness),
                        geometry);
                }
                else
                {
                    dc.DrawText(text, point);
                }
            }
        }

        dc.Pop();
    }

    public override Rect Bounds => Rect;

    public override void Translate(double dx, double dy)
    {
        Rect = new Rect(Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height);
    }
}

public sealed class TextAnnotation : Annotation
{
    public Point Position { get; set; }

    public string Text { get; set; } = string.Empty;

    public double FontSize { get; set; } = 18;

    public Color StrokeColor { get; set; } = Color.FromRgb(0x20, 0x20, 0x20);

    public double StrokeThickness { get; set; }

    public override void Draw(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var formatted = new FormattedText(
            Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            FontSize,
            new SolidColorBrush(Color),
            1.0)
        {
            MaxTextWidth = 2400
        };

        if (StrokeThickness > 0)
        {
            var geometry = formatted.BuildGeometry(Position);
            dc.DrawGeometry(
                new SolidColorBrush(Color),
                new Pen(new SolidColorBrush(StrokeColor), StrokeThickness),
                geometry);
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
            if (string.IsNullOrEmpty(Text))
            {
                return Rect.Empty;
            }

            var measured = Measure();
            return new Rect(Position.X - 4, Position.Y - 4, measured.Width + 8, measured.Height + 8);
        }
    }

    public override void Translate(double dx, double dy)
    {
        Position = new Point(Position.X + dx, Position.Y + dy);
    }

    private FormattedText Measure()
    {
        return new FormattedText(
            Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            FontSize,
            new SolidColorBrush(Color),
            1.0)
        {
            MaxTextWidth = 2400
        };
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

internal static class GeometryUtil
{
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

        blockSize = Math.Clamp(blockSize, 4, 64);

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(new Int32Rect(x, y, width, height), pixels, stride, 0);

        var smallWidth = Math.Max(1, (width + blockSize - 1) / blockSize);
        var smallHeight = Math.Max(1, (height + blockSize - 1) / blockSize);
        var small = new byte[smallWidth * smallHeight * 4];

        for (var by = 0; by < smallHeight; by++)
        {
            for (var bx = 0; bx < smallWidth; bx++)
            {
                var sx = Math.Min(width - 1, bx * blockSize + blockSize / 2);
                var sy = Math.Min(height - 1, by * blockSize + blockSize / 2);
                var sourceIndex = sy * stride + sx * 4;
                var targetIndex = (by * smallWidth + bx) * 4;
                small[targetIndex] = pixels[sourceIndex];
                small[targetIndex + 1] = pixels[sourceIndex + 1];
                small[targetIndex + 2] = pixels[sourceIndex + 2];
                small[targetIndex + 3] = pixels[sourceIndex + 3];
            }
        }

        var pattern = BitmapSource.Create(
            smallWidth, smallHeight, 96, 96, PixelFormats.Bgra32, null, small, smallWidth * 4);
        pattern.Freeze();
        return pattern;
    }
}
