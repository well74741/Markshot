namespace Markshot.Annotations;

/// <summary>
/// Draws annotations in image-pixel coordinates on top of the frozen screenshot.
/// The element itself lives in DIPs, so everything is scaled by 1/Scale.
/// </summary>
public sealed class AnnotationEditor : FrameworkElement
{
    public BitmapSource? Source { get; set; }

    public double Scale { get; set; } = 1.0;

    public Rect Selection { get; set; } = Rect.Empty;

    public bool ShowSelectionChrome { get; set; }

    public List<Annotation> Items { get; } = new();

    public Annotation? Preview { get; set; }

    public Annotation? Selected { get; set; }

    public Rect HoverBounds { get; set; } = Rect.Empty;

    public string? HoverLabel { get; set; }

    public bool ShowHover { get; set; }

    public AnnotationEditor()
    {
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Scale <= 0)
        {
            return;
        }

        dc.PushTransform(new ScaleTransform(1 / Scale, 1 / Scale));

        var hasSelection = ShowSelectionChrome && Selection.Width > 0 && Selection.Height > 0;
        if (hasSelection)
        {
            dc.PushClip(new RectangleGeometry(Selection));
        }

        foreach (var annotation in Items)
        {
            DrawRotated(dc, annotation);
        }

        if (Preview != null)
        {
            DrawRotated(dc, Preview);
        }

        if (ShowHover && HoverBounds.Width > 0)
        {
            DrawHover(dc, HoverBounds, HoverLabel);
        }

        if (Selected != null)
        {
            DrawAnnotationSelection(dc, Selected);
        }

        if (hasSelection)
        {
            dc.Pop();
            DrawSelectionChrome(dc, Selection);
        }

        dc.Pop();
    }

    private static void DrawRotated(DrawingContext dc, Annotation annotation)
    {
        if (Math.Abs(annotation.Rotation) < 0.01)
        {
            annotation.Draw(dc);
            return;
        }

        var bounds = annotation.Bounds;
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        dc.PushTransform(new RotateTransform(annotation.Rotation, center.X, center.Y));
        annotation.Draw(dc);
        dc.Pop();
    }

    private void DrawHover(DrawingContext dc, Rect bounds, string? label)
    {
        var unit = 1.0 / Scale;
        var accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));

        dc.DrawRectangle(
            null,
            new Pen(accent, 1.5 * unit)
            {
                DashStyle = new DashStyle(new[] { 5.0, 3.0 }, 0)
            },
            bounds);

        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        var text = new FormattedText(
            label!,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            12 * unit,
            Brushes.White,
            1.0);

        var labelRect = new Rect(
            bounds.X,
            bounds.Y - text.Height - 8 * unit,
            text.Width + 12 * unit,
            text.Height + 6 * unit);

        if (labelRect.Top < 0)
        {
            labelRect = new Rect(bounds.X, bounds.Y + 4 * unit, labelRect.Width, labelRect.Height);
        }

        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(210, 20, 20, 20)),
            null,
            labelRect,
            3 * unit,
            3 * unit);

        dc.DrawText(text, new Point(labelRect.X + 6 * unit, labelRect.Y + 3 * unit));
    }

    private void DrawAnnotationSelection(DrawingContext dc, Annotation annotation)
    {
        var unit = 1.0 / Scale;
        var bounds = SelectionFrame(annotation.Bounds, unit);
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

        dc.PushTransform(new RotateTransform(annotation.Rotation, center.X, center.Y));

        var accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
        var pen = new Pen(accent, 1.4 * unit)
        {
            DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0)
        };

        dc.DrawRectangle(null, pen, bounds);

        var handle = 7 * unit;
        var handlePen = new Pen(Brushes.White, unit);
        foreach (var point in HandleCenters(bounds))
        {
            dc.DrawRectangle(
                accent,
                handlePen,
                new Rect(point.X - handle / 2, point.Y - handle / 2, handle, handle));
        }

        var rotateCenter = RotationHandleCenter(bounds);
        var radius = 9 * unit;
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(220, 25, 25, 25)), new Pen(accent, 1.4 * unit),
            rotateCenter, radius, radius);

        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            var start = new Point(rotateCenter.X - radius * 0.62, rotateCenter.Y + radius * 0.35);
            var end = new Point(rotateCenter.X + radius * 0.62, rotateCenter.Y + radius * 0.35);
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius * 0.72, radius * 0.72), 0, false,
                SweepDirection.Counterclockwise, true, false);
        }

        arc.Freeze();
        dc.DrawGeometry(null, new Pen(accent, 1.5 * unit), arc);
        dc.DrawLine(new Pen(accent, 1.5 * unit),
            new Point(rotateCenter.X - radius * 0.62, rotateCenter.Y + radius * 0.35),
            new Point(rotateCenter.X - radius * 0.62, rotateCenter.Y - radius * 0.15));
        dc.DrawLine(new Pen(accent, 1.5 * unit),
            new Point(rotateCenter.X + radius * 0.62, rotateCenter.Y + radius * 0.35),
            new Point(rotateCenter.X + radius * 0.62, rotateCenter.Y + radius * 0.85));

        dc.Pop();
    }

    public static Point RotationHandleCenter(Rect rect) =>
        new(rect.Left + rect.Width / 2, rect.Top - 24);

    public static Rect SelectionFrame(Rect bounds, double unit)
    {
        var width = Math.Max(bounds.Width, 20 * unit);
        var height = Math.Max(bounds.Height, 20 * unit);
        var centerX = bounds.X + bounds.Width / 2;
        var centerY = bounds.Y + bounds.Height / 2;
        var frame = new Rect(centerX - width / 2, centerY - height / 2, width, height);
        frame.Inflate(4 * unit, 4 * unit);
        return frame;
    }

    public static Point RotatePoint(Point point, Point center, double degrees)
    {
        if (Math.Abs(degrees) < 0.01)
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

    private void DrawSelectionChrome(DrawingContext dc, Rect selection)
    {
        var unit = 1.0 / Scale;

        var blue = new SolidColorBrush(Color.FromRgb(0x2F, 0x7B, 0xFF));
        dc.DrawRectangle(null, new Pen(blue, unit * 2), selection);

        const double handleDip = 9;
        var handleSize = handleDip * unit;
        var handlePen = new Pen(Brushes.White, unit * 1.2);

        foreach (var center in HandleCenters(selection))
        {
            dc.DrawRectangle(
                blue,
                handlePen,
                new Rect(center.X - handleSize / 2, center.Y - handleSize / 2, handleSize, handleSize));
        }

        var label = new FormattedText(
            $"{Math.Round(selection.Width)} × {Math.Round(selection.Height)}",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            12 * unit,
            Brushes.White,
            1.0);

        var paddingX = 5 * unit;
        var paddingY = 3 * unit;
        var labelRect = new Rect(
            selection.X,
            selection.Y - label.Height - paddingY * 2 - 4 * unit,
            label.Width + paddingX * 2,
            label.Height + paddingY * 2);

        if (labelRect.Top < 0)
        {
            labelRect = new Rect(
                selection.X,
                selection.Y + 4 * unit,
                label.Width + paddingX * 2,
                label.Height + paddingY * 2);
        }

        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(205, 20, 20, 20)),
            null,
            labelRect,
            3 * unit,
            3 * unit);

        dc.DrawText(label, new Point(labelRect.X + paddingX, labelRect.Y + paddingY));
    }

    public static Point[] HandleCenters(Rect rect) => new[]
    {
        new Point(rect.Left, rect.Top),
        new Point(rect.Left + rect.Width / 2, rect.Top),
        new Point(rect.Right, rect.Top),
        new Point(rect.Right, rect.Top + rect.Height / 2),
        new Point(rect.Right, rect.Bottom),
        new Point(rect.Left + rect.Width / 2, rect.Bottom),
        new Point(rect.Left, rect.Bottom),
        new Point(rect.Left, rect.Top + rect.Height / 2)
    };
}
