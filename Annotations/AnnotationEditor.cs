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

        if (hasSelection)
        {
            dc.Pop();
            DrawSelectionChrome(dc, Selection);
        }

        dc.Pop();
    }

    private void DrawSelectionChrome(DrawingContext dc, Rect selection)
    {
        var unit = 1.0 / Scale;

        dc.DrawRectangle(
            null,
            new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), unit * 2),
            new Rect(selection.X - unit, selection.Y - unit, selection.Width + unit * 2, selection.Height + unit * 2));

        dc.DrawRectangle(null, new Pen(Brushes.White, unit), selection);

        const double handleDip = 8;
        var handleSize = handleDip * unit;
        var handlePen = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), unit);

        foreach (var center in HandleCenters(selection))
        {
            dc.DrawRectangle(
                Brushes.White,
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
