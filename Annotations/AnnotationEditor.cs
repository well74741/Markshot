namespace Markshot.Annotations;

/// <summary>What the pointer is over on a selected element's adjustment frame.</summary>
public readonly record struct FrameHit(int Handle, bool Rotate, bool Inside)
{
    public static readonly FrameHit None = new(-1, false, false);

    public bool IsHandle => Handle >= 0;

    public bool Any => Handle >= 0 || Rotate || Inside;
}

/// <summary>
/// Draws annotations in image-pixel coordinates on top of the frozen screenshot.
/// The element itself lives in DIPs, so everything is scaled by 1/Scale.
/// <see cref="DrawItem"/> is the single drawing path for the canvas, the
/// capture output and the pin composition, so what you see is what you get.
/// </summary>
public sealed class AnnotationEditor : FrameworkElement
{
    private static readonly SolidColorBrush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)));

    public BitmapSource? Source { get; set; }

    public double Scale { get; set; } = 1.0;

    public Rect Selection { get; set; } = Rect.Empty;

    public bool ShowSelectionChrome { get; set; }

    public List<Annotation> Items { get; } = new();

    public Annotation? Preview { get; set; }

    public Annotation? Selected { get; set; }

    /// <summary>Text currently open in the inline editor; drawn with its adjustment frame.</summary>
    public TextAnnotation? EditingText { get; set; }

    /// <summary>Watermark whose glyphs are under the pointer (outlined).</summary>
    public WatermarkAnnotation? HoverWatermark { get; set; }

    /// <summary>Eraser stroke being dragged (canvas coordinates); erases live before it is bound.</summary>
    public List<Point>? LiveEraser { get; set; }

    public double LiveEraserWidth { get; set; }

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

        var live = LiveEraser is { Count: > 0 } ? EraseStroke.Widen(LiveEraser, LiveEraserWidth) : null;
        foreach (var annotation in Items)
        {
            DrawItem(dc, annotation, live, ReferenceEquals(annotation, HoverWatermark) ? Accent : null);
        }

        if (Preview != null)
        {
            DrawItem(dc, Preview);
        }

        if (ShowHover && HoverBounds.Width > 0)
        {
            DrawHover(dc, HoverBounds, HoverLabel);
        }

        if (EditingText != null)
        {
            DrawFrame(dc, EditingText, editing: true);
        }
        else if (Selected != null)
        {
            DrawFrame(dc, Selected, editing: false);
        }

        if (hasSelection)
        {
            dc.Pop();
            DrawSelectionChrome(dc, Selection);
        }

        dc.Pop();
    }

    /// <summary>
    /// Draws one element exactly as it appears everywhere: rotated about its
    /// bounds centre, with its bound eraser strokes (and an optional live eraser)
    /// cut out. Used by the canvas, the capture output and the pin composition.
    /// </summary>
    public static void DrawItem(DrawingContext dc, Annotation annotation, Geometry? liveEraser = null, Brush? highlight = null)
    {
        var bounds = annotation.Bounds;
        var pops = 0;
        if (liveEraser != null && annotation.Erasable && !bounds.IsEmpty)
        {
            var area = RotatedBounds(annotation);
            area.Inflate(annotation.Thickness * 2 + 4, annotation.Thickness * 2 + 4);
            if (area.IntersectsWith(liveEraser.Bounds))
            {
                dc.PushClip(Geometry.Combine(new RectangleGeometry(Room(area)), liveEraser, GeometryCombineMode.Exclude, null));
                pops++;
            }
        }

        if (Math.Abs(annotation.Rotation) >= 0.01 && !bounds.IsEmpty)
        {
            var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            dc.PushTransform(new RotateTransform(annotation.Rotation, center.X, center.Y));
            pops++;
        }

        var mask = annotation.MaskGeometry();
        if (mask != null)
        {
            dc.PushClip(Geometry.Combine(new RectangleGeometry(Room(bounds)), mask, GeometryCombineMode.Exclude, null));
            pops++;
        }

        // Every element has its own opacity (the watermark applies it to its glyphs itself).
        if (annotation is not WatermarkAnnotation && annotation.Opacity < 0.999)
        {
            dc.PushOpacity(Math.Clamp(annotation.Opacity, 0.05, 1));
            pops++;
        }

        if (annotation is WatermarkAnnotation watermark)
        {
            watermark.Draw(dc, highlight);
        }
        else
        {
            annotation.Draw(dc);
        }

        for (var i = 0; i < pops; i++)
        {
            dc.Pop();
        }
    }

    /// <summary>Axis-aligned box around the element after rotation.</summary>
    public static Rect RotatedBounds(Annotation annotation)
    {
        var bounds = annotation.Bounds;
        if (bounds.IsEmpty || Math.Abs(annotation.Rotation) < 0.01)
        {
            return bounds;
        }

        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var corners = new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft }
            .Select(p => RotatePoint(p, center, annotation.Rotation))
            .ToList();
        var minX = corners.Min(p => p.X);
        var minY = corners.Min(p => p.Y);
        return new Rect(minX, minY, corners.Max(p => p.X) - minX, corners.Max(p => p.Y) - minY);
    }

    // Generous clip area so strokes, outlines and shadows are never cut.
    private static Rect Room(Rect bounds)
    {
        var room = bounds;
        room.Inflate(Math.Max(400, bounds.Width), Math.Max(400, bounds.Height));
        return room;
    }

    private void DrawHover(DrawingContext dc, Rect bounds, string? label)
    {
        var unit = 1.0 / Scale;

        dc.DrawRectangle(
            null,
            new Pen(Accent, 1.5 * unit)
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

    private void DrawFrame(DrawingContext dc, Annotation annotation, bool editing)
    {
        if (annotation is WatermarkAnnotation)
        {
            return; // the watermark covers the canvas and has no frame
        }

        var unit = 1.0 / Scale;
        var frame = SelectionFrame(annotation.Bounds, unit);
        var center = new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);

        dc.PushTransform(new RotateTransform(annotation.Rotation, center.X, center.Y));

        // Two-tone dashes stay visible on light and dark screenshots.
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 1.4 * unit), frame);
        dc.DrawRectangle(null, new Pen(Accent, 1.4 * unit)
        {
            DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0)
        }, frame);

        var handle = (editing ? 6 : 7) * unit;
        var handlePen = new Pen(Brushes.White, unit);
        foreach (var point in HandleCenters(frame))
        {
            dc.DrawRectangle(
                Accent,
                handlePen,
                new Rect(point.X - handle / 2, point.Y - handle / 2, handle, handle));
        }

        dc.Pop();
    }

    /// <summary>
    /// Hit-tests the adjustment frame of <paramref name="annotation"/>: the 8
    /// handles and the frame edges resize, a band just outside the corners
    /// rotates, the inside moves. <paramref name="unit"/> = image px per DIP.
    /// </summary>
    public static FrameHit HitFrame(Annotation annotation, Point point, double unit, bool handlesOnly = false)
    {
        if (annotation is WatermarkAnnotation)
        {
            return FrameHit.None;
        }

        var frame = SelectionFrame(annotation.Bounds, unit);
        var center = new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
        var local = RotatePoint(point, center, -annotation.Rotation);

        var handles = HandleCenters(frame);
        for (var i = 0; i < handles.Length; i++)
        {
            if ((local - handles[i]).Length <= 8 * unit)
            {
                return new FrameHit(i, false, false);
            }
        }

        if (handlesOnly)
        {
            return FrameHit.None;
        }

        var edge = 5 * unit;
        var withinX = local.X >= frame.Left - edge && local.X <= frame.Right + edge;
        var withinY = local.Y >= frame.Top - edge && local.Y <= frame.Bottom + edge;
        if (withinY && Math.Abs(local.X - frame.Left) <= edge) return new FrameHit(7, false, false);
        if (withinY && Math.Abs(local.X - frame.Right) <= edge) return new FrameHit(3, false, false);
        if (withinX && Math.Abs(local.Y - frame.Top) <= edge) return new FrameHit(1, false, false);
        if (withinX && Math.Abs(local.Y - frame.Bottom) <= edge) return new FrameHit(5, false, false);

        if (frame.Contains(local))
        {
            return new FrameHit(-1, false, true);
        }

        // Rotation: a band just outside each corner.
        foreach (var corner in new[] { frame.TopLeft, frame.TopRight, frame.BottomRight, frame.BottomLeft })
        {
            if ((local - corner).Length <= 24 * unit)
            {
                return new FrameHit(-1, true, false);
            }
        }

        return FrameHit.None;
    }

    /// <summary>Double-arrow cursor for a resize handle, following the element's rotation.</summary>
    public static Cursor ResizeCursor(int handle, double rotation)
    {
        // Direction the handle pulls in the element's own frame (y down).
        var baseAngle = handle switch
        {
            0 or 4 => 45.0,
            2 or 6 => 135.0,
            1 or 5 => 90.0,
            _ => 0.0
        };
        var angle = ((baseAngle + rotation) % 180 + 180) % 180;
        var step = (int)Math.Round(angle / 45) % 4;
        return step switch
        {
            0 => Cursors.SizeWE,
            1 => Cursors.SizeNWSE,
            2 => Cursors.SizeNS,
            _ => Cursors.SizeNESW
        };
    }

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

    public static Point RotatePoint(Point point, Point center, double degrees) =>
        GeometryUtil.Rotate(point, center, degrees);

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

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
