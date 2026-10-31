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
            annotation.Draw(dc);
        }

        Preview?.Draw(dc);

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
        var bounds = annotation.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var unit = 1.0 / Scale;
        bounds.Inflate(4 * unit, 4 * unit);

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)), 1.4 * unit)
        {
            DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0)
        };

        dc.DrawRectangle(null, pen, bounds);

        var handle = 6 * unit;
        var handlePen = new Pen(Brushes.White, unit);
        foreach (var point in new[]
                 {
                     bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight
                 })
        {
            dc.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)),
                handlePen,
                new Rect(point.X - handle / 2, point.Y - handle / 2, handle, handle));
        }
    }

    private void DrawSelectionChrome(DrawingContext dc, Rect selection)
    {
        var unit = 1.0 / Scale;

        dc.DrawRectangle(
            null,
            new Pen(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), unit * 4),
            new Rect(selection.X - unit * 2, selection.Y - unit * 2, selection.Width + unit * 4, selection.Height + unit * 4));

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
