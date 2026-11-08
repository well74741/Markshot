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

    /// <summary>How pictures on the canvas are scaled on the screen (the editor switches it with the zoom).</summary>
    public BitmapScalingMode PictureScaling { get; set; } = BitmapScalingMode.HighQuality;

    /// <summary>Elements selected together (the image editor's 多选); each gets a dashed frame.</summary>
    public IReadOnlyList<Annotation>? Group { get; set; }

    /// <summary>Rounded corners of the hover frame (canvas pixels): the capture's remembered ones.</summary>
    public double HoverCornerRadius { get; set; }

    /// <summary>吸附 lines while an element is moved (canvas pixels).</summary>
    public IReadOnlyList<Snapping.Guide>? Guides { get; set; }

    /// <summary>框选: the rectangle being dragged (canvas pixels).</summary>
    public Rect? Marquee { get; set; }

    /// <summary>吸色 (Alt with the pen): the cross at this canvas point, with the colour under it; null = off.</summary>
    public (Point At, Color? Color)? Eyedrop { get; set; }

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

    /// <summary>Opacity of the live eraser (1 = erases fully).</summary>
    public double LiveEraserStrength { get; set; } = 1;

    /// <summary>软边 of the eraser stroke being drawn (0…1).</summary>
    public double LiveEraserSoftness { get; set; }

    /// <summary>Freehand (lasso) capture being drawn: the path so far and the straight-segment preview end.</summary>
    public List<Point>? LassoDraft { get; set; }

    public Point? LassoPreview { get; set; }

    /// <summary>Shape of a lasso capture (canvas pixels); null = the rectangle.</summary>
    public Geometry? SelectionPath { get; set; }

    /// <summary>Corner radius of the capture area (canvas pixels); 0 = square.</summary>
    public double SelectionCornerRadius { get; set; }

    /// <summary>Corner radius of the image (a pin made from a rounded capture); elements are clipped to it.</summary>
    public double SourceCornerRadius { get; set; }

    /// <summary>
    /// The rounding dots inside the capture area's corners (canvas pixels): just inside the
    /// arc, so dragging one towards the centre rounds the corners.
    /// </summary>
    public static Point[] CornerDots(Rect selection, double radius, double unit)
    {
        var inset = 12 * unit + radius * (1 - Math.Sqrt(0.5));
        inset = Math.Min(inset, Math.Min(selection.Width, selection.Height) / 2);
        return new[]
        {
            new Point(selection.Left + inset, selection.Top + inset),
            new Point(selection.Right - inset, selection.Top + inset),
            new Point(selection.Right - inset, selection.Bottom - inset),
            new Point(selection.Left + inset, selection.Bottom - inset)
        };
    }

    /// <summary>Corner radius for a rounding dot dragged to <paramref name="pointer"/> (inverse of CornerDots).</summary>
    public static double CornerRadiusFor(Rect selection, int corner, Point pointer, double unit)
    {
        var (x, y, sx, sy) = corner switch
        {
            0 => (selection.Left, selection.Top, 1, 1),
            1 => (selection.Right, selection.Top, -1, 1),
            2 => (selection.Right, selection.Bottom, -1, -1),
            _ => (selection.Left, selection.Bottom, 1, -1)
        };
        var inward = ((pointer.X - x) * sx + (pointer.Y - y) * sy) / 2;
        var radius = (inward - 12 * unit) / (1 - Math.Sqrt(0.5));
        return Math.Clamp(radius, 0, Math.Min(selection.Width, selection.Height) / 2);
    }

    /// <summary>False while the editable size box is shown in place of the size label.</summary>
    public bool SizeLabelVisible { get; set; } = true;

    /// <summary>False while the capture is finishing into a pin: the area frame is no longer drawn.</summary>
    public bool ChromeVisible { get; set; } = true;

    /// <summary>The capture area is being dragged: a bold outline without handles.</summary>
    public bool SelectionDragging { get; set; }

    public Rect HoverBounds { get; set; } = Rect.Empty;

    public string? HoverLabel { get; set; }

    public bool ShowHover { get; set; }

    /// <summary>The element showing the picture under the annotations: 抠除 holes are cut out of it too.</summary>
    public UIElement? CutoutTarget { get; set; }

    /// <summary>Holes appeared or all went (the owner's shadow follows the shape then).</summary>
    public event Action? CutoutsChanged;

    private bool _hadCutouts;

    private Geometry? _ownTargetClip;
    private Geometry? _savedTargetClip;

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
        Geometry? area = null;
        if (hasSelection)
        {
            area = SelectionPath ?? new RectangleGeometry(Selection, SelectionCornerRadius, SelectionCornerRadius);
        }
        else if (Source != null)
        {
            // Elements never paint outside the image (a pin); frames and grips still may.
            area = new RectangleGeometry(new Rect(0, 0, Source.PixelWidth, Source.PixelHeight), SourceCornerRadius, SourceCornerRadius);
        }

        var visible = area == null ? null : Cutouts.Exclude(Items, area);
        if (area != null)
        {
            // 抠除 cuts the picture under everything (CutoutTarget) and the elements below the hole only; what is
            // drawn after it lies on top, uncut.
            dc.PushClip(area);
        }

        UpdateCutoutTarget(visible != null && !ReferenceEquals(visible, area) ? visible : null);
        var hasCutouts = Cutouts.Any(Items);
        if (hasCutouts != _hadCutouts)
        {
            _hadCutouts = hasCutouts;
            Dispatcher.BeginInvoke(new Action(() => CutoutsChanged?.Invoke()));
        }

        var live = LiveEraser is { Count: > 0 } ? EraseStroke.Widen(LiveEraser, LiveEraserWidth) : null;
        ImageAnnotation.Scaling = PictureScaling;
        foreach (var (annotation, above) in Layered(Items, Preview as SpotlightAnnotation))
        {
            if (above != null)
            {
                dc.PushClip(above);
            }

            DrawItem(dc, annotation, live, ReferenceEquals(annotation, HoverWatermark) ? Accent : null, LiveEraserStrength,
                LiveEraser, LiveEraserWidth, LiveEraserSoftness);
            if (above != null)
            {
                dc.Pop();
            }
        }

        ImageAnnotation.Scaling = BitmapScalingMode.HighQuality; // saved / copied pictures always are

        if (Preview != null)
        {
            DrawItem(dc, Preview); // a spotlight being drawn joined the others' group above (drawn by its first one)
        }

        if (!hasSelection && Source != null)
        {
            dc.Pop();
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
            if (ChromeVisible)
            {
                DrawSelectionChrome(dc, Selection);
            }
        }

        if (LassoDraft is { Count: > 0 })
        {
            DrawLassoDraft(dc);
        }

        if (Group is { Count: > 0 } group)
        {
            foreach (var member in group)
            {
                DrawGroupFrame(dc, member);
            }
        }

        if (Guides is { Count: > 0 } guides)
        {
            var guidePen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x8E)), Scale);
            foreach (var guide in guides)
            {
                dc.DrawLine(guidePen, guide.Vertical ? new Point(guide.At, guide.From) : new Point(guide.From, guide.At),
                    guide.Vertical ? new Point(guide.At, guide.To) : new Point(guide.To, guide.At));
            }
        }

        if (Marquee is { } marquee)
        {
            var unit = Scale;
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x26, 0x3B, 0x82, 0xF6)), new Pen(Accent, 1.2 * unit), marquee);
        }

        if (Eyedrop is { } eyedrop)
        {
            DrawEyedrop(dc, eyedrop.At, eyedrop.Color);
        }

        dc.Pop();
    }

    /// <summary>A member of a 多选: its dashed outline (no handles), turned with it.</summary>
    private void DrawGroupFrame(DrawingContext dc, Annotation annotation)
    {
        var unit = Scale;
        var frame = SelectionFrame(annotation.Bounds, unit);
        var center = new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
        dc.PushTransform(new RotateTransform(annotation.Rotation, center.X, center.Y));
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 1.4 * unit), frame);
        dc.DrawRectangle(null, new Pen(Accent, 1.4 * unit) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) }, frame);
        dc.Pop();
    }

    /// <summary>The picking cross: open in the middle (the picked pixel stays visible), a ring in the colour.</summary>
    private void DrawEyedrop(DrawingContext dc, Point at, Color? color)
    {
        var unit = Scale; // canvas pixels per DIP
        foreach (var (brush, width) in new[] { (Brushes.Black, 3.0), (Brushes.White, 1.0) })
        {
            var pen = new Pen(brush, width * unit);
            foreach (var (x, y) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                dc.DrawLine(pen, new Point(at.X + x * 4 * unit, at.Y + y * 4 * unit), new Point(at.X + x * 14 * unit, at.Y + y * 14 * unit));
            }
        }

        if (color is { } picked)
        {
            var centre = new Point(at.X + 22 * unit, at.Y + 22 * unit);
            dc.DrawEllipse(new SolidColorBrush(picked), new Pen(Brushes.White, 2 * unit), centre, 9 * unit, 9 * unit);
            dc.DrawEllipse(null, new Pen(Brushes.Black, 1 * unit), centre, 10.5 * unit, 10.5 * unit);
        }
    }

    /// <summary>
    /// Draws one element exactly as it appears everywhere: rotated about its
    /// bounds centre, with its bound eraser strokes (and an optional live eraser)
    /// cut out. Used by the canvas, the capture output and the pin composition.
    /// </summary>
    public static void DrawItem(DrawingContext dc, Annotation annotation, Geometry? liveEraser = null, Brush? highlight = null,
        double liveStrength = 1, IReadOnlyList<Point>? livePoints = null, double liveWidth = 0, double liveSoftness = 0)
    {
        if (annotation.Hidden)
        {
            return;
        }

        if (annotation is SpotlightAnnotation spotlight)
        {
            // Its own geometry (each spotlight of the group turned its own way), and its own opacity.
            if (spotlight.Opacity < 0.999)
            {
                dc.PushOpacity(Math.Clamp(spotlight.Opacity, 0, 1));
                spotlight.Draw(dc);
                dc.Pop();
            }
            else
            {
                spotlight.Draw(dc);
            }

            if (highlight != null)
            {
                dc.DrawGeometry(null, new Pen(highlight, 2), spotlight.Shape(0));
            }

            return;
        }

        var bounds = annotation.Bounds;
        var pops = 0;
        if (liveEraser != null && annotation.Erasable && !bounds.IsEmpty)
        {
            var area = RotatedBounds(annotation);
            area.Inflate(annotation.Thickness * 2 + 4, annotation.Thickness * 2 + 4);
            if (area.IntersectsWith(liveEraser.Bounds))
            {
                PushErase(dc, liveEraser, liveStrength, Room(area), livePoints, liveWidth, liveSoftness);
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

        // Strokes of a semi-transparent eraser only fade what they cover.
        foreach (var (geometry, strength, softness, points, width) in annotation.PartialMasks())
        {
            PushErase(dc, geometry, strength, Room(bounds), points, width, softness);
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

    private void DrawLassoDraft(DrawingContext dc)
    {
        var unit = Scale;
        var points = LassoDraft!;
        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToList(), true, true);
        }

        line.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), unit * 3.4) { LineJoin = PenLineJoin.Round }, line);
        dc.DrawGeometry(null, new Pen(Brushes.White, unit * 1.6) { LineJoin = PenLineJoin.Round }, line);
        var last = points[^1];
        if (LassoPreview is { } preview)
        {
            // Shift: the straight segment that will be added.
            dc.DrawLine(new Pen(Accent, unit * 1.6) { DashStyle = new DashStyle(new[] { 3.0, 2.0 }, 0) }, last, preview);
        }

        // Closing hint back to the start.
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), unit) { DashStyle = new DashStyle(new[] { 2.0, 3.0 }, 0) },
            LassoPreview ?? last, points[0]);
    }

    /// <summary>
    /// Paint order: as listed, except that the watermark is painted right after the mosaics
    /// (so a mosaic never pixelates it) instead of under everything.
    /// </summary>
    /// <summary>The picture element gets the holes too (in its own DIPs); put back as it was when there are none.</summary>
    /// <summary>Editing is over: the picture is the composed one (holes and all); the clip on it goes.</summary>
    public void ReleaseCutoutTarget() => UpdateCutoutTarget(null);

    private void UpdateCutoutTarget(Geometry? visible)
    {
        if (CutoutTarget == null)
        {
            return;
        }

        if (!ReferenceEquals(CutoutTarget.Clip, _ownTargetClip))
        {
            _savedTargetClip = CutoutTarget.Clip; // set by the owner meanwhile
        }

        if (visible == null)
        {
            if (_ownTargetClip != null && ReferenceEquals(CutoutTarget.Clip, _ownTargetClip))
            {
                CutoutTarget.Clip = _savedTargetClip;
            }

            _ownTargetClip = null;
            return;
        }

        var clip = visible.Clone();
        clip.Transform = new ScaleTransform(1 / Scale, 1 / Scale);
        clip.Freeze();
        _ownTargetClip = clip;
        CutoutTarget.Clip = clip;
    }

    /// <summary>
    /// The elements in paint order, each with the clip of the 抠除 holes made after it (above it): a hole cuts what lay
    /// there, not what is drawn later. Null when nothing above it cuts.
    /// </summary>
    public static IEnumerable<(Annotation Item, Geometry? Above)> Layered(IReadOnlyList<Annotation> items, SpotlightAnnotation? drawing = null)
    {
        var ordered = DrawOrder(items).ToList();
        if (drawing != null)
        {
            Spotlights.Prepare(items.Append(drawing)); // the spotlight being drawn darkens together with the others
        }

        var clips = new Geometry?[ordered.Count];
        var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
        Geometry? current = null;
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            clips[i] = current;
            if (ordered[i] is CutoutAnnotation cutout)
            {
                holes.Children.Add(cutout.Shape());
                var copy = holes.Clone();
                copy.Freeze();
                var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(-100000, -100000, 200000, 200000)), copy);
                outside.Freeze();
                current = outside;
            }
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            yield return (ordered[i], clips[i]);
        }
    }

    /// <summary>Draws the elements (in paint order, cut by the holes above each).</summary>
    public static void DrawLayers(DrawingContext dc, IReadOnlyList<Annotation> items)
    {
        foreach (var (annotation, above) in Layered(items))
        {
            if (above != null)
            {
                dc.PushClip(above);
            }

            DrawItem(dc, annotation);
            if (above != null)
            {
                dc.Pop();
            }
        }
    }

    public static IEnumerable<Annotation> DrawOrder(IReadOnlyList<Annotation> items)
    {
        Spotlights.Prepare(items); // drawn together by the first one
        var watermark = items.OfType<WatermarkAnnotation>().FirstOrDefault();
        if (watermark == null)
        {
            return items;
        }

        var rest = items.Where(item => !ReferenceEquals(item, watermark)).ToList();
        // Above the picture(s) and mosaics, below the other elements.
        var lastMosaic = rest.FindLastIndex(item => item is MosaicAnnotation or MosaicBrushAnnotation or ImageAnnotation);
        rest.Insert(lastMosaic + 1, watermark);
        return rest;
    }

    /// <summary>Erases <paramref name="area"/> fully (clip) or partly (opacity mask) for what is drawn next.</summary>
    private static void PushErase(DrawingContext dc, Geometry area, double strength, Rect room,
        IReadOnlyList<Point>? points = null, double width = 0, double softness = 0)
    {
        var soft = softness >= 0.01 && points is { Count: > 0 } && width > 0;
        if (strength >= 0.999 && !soft)
        {
            dc.PushClip(Geometry.Combine(new RectangleGeometry(room), area, GeometryCombineMode.Exclude, null));
            return;
        }

        var keep = new DrawingGroup();
        if (soft)
        {
            // 软边: nested widths of the stroke, widest first; each ring erases a little more than the one outside it,
            // a smooth ramp from the edge to the full strength in the middle.
            const int layers = 5;
            var areas = new Geometry[layers];
            for (var k = 0; k < layers; k++)
            {
                areas[k] = EraseStroke.Widen(points!, width * Math.Max(0.04, 1 - softness * k / (layers - 1)));
            }

            keep.Children.Add(new GeometryDrawing(Brushes.White, null,
                Geometry.Combine(new RectangleGeometry(room), areas[0], GeometryCombineMode.Exclude, null)));
            for (var k = 0; k < layers; k++)
            {
                var t = (k + 1) / (double)layers;
                var erased = strength * t * t * (3 - 2 * t);
                var ring = k == layers - 1 ? areas[k] : Geometry.Combine(areas[k], areas[k + 1], GeometryCombineMode.Exclude, null);
                keep.Children.Add(new GeometryDrawing(
                    new SolidColorBrush(Color.FromArgb((byte)Math.Round((1 - erased) * 255), 255, 255, 255)), null, ring));
            }
        }
        else
        {
            keep.Children.Add(new GeometryDrawing(Brushes.White, null,
                Geometry.Combine(new RectangleGeometry(room), area, GeometryCombineMode.Exclude, null)));
            keep.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromArgb((byte)Math.Round((1 - strength) * 255), 255, 255, 255)), null, area));
        }
        var brush = new DrawingBrush(keep)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = room,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = room
        };
        brush.Freeze();
        dc.PushOpacityMask(brush);
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
        var unit = Scale; // canvas pixels per DIP

        // A solid, bold blue frame drawn just inside the area (the area itself is what gets captured).
        var thickness = 3 * unit;
        var inner = bounds;
        inner.Inflate(-thickness / 2, -thickness / 2);
        if (inner.Width <= 0 || inner.Height <= 0)
        {
            inner = bounds;
        }

        // The frame's middle line runs half its width inside the area: its outer edge follows the area's corners.
        var corner = Math.Max(0, Math.Min(HoverCornerRadius, Math.Min(bounds.Width, bounds.Height) / 2) - thickness / 2);
        if (corner > 0.25)
        {
            dc.DrawRoundedRectangle(null, new Pen(Accent, thickness), inner, corner, corner);
        }
        else
        {
            dc.DrawRectangle(null, new Pen(Accent, thickness) { LineJoin = PenLineJoin.Miter }, inner);
        }

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

        var unit = Scale; // canvas pixels per DIP
        var frame = SelectionFrame(annotation.Bounds, unit);
        var center = new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);

        dc.PushTransform(new RotateTransform(annotation.Rotation, center.X, center.Y));

        // The same line as the hover preview (solid, 3 DIP); the selection only adds the handles and grips.
        dc.DrawRectangle(null, new Pen(Accent, 3 * unit) { LineJoin = PenLineJoin.Miter }, frame);

        var handle = (editing ? 6 : 7) * unit;
        var handlePen = new Pen(Brushes.White, unit);
        foreach (var point in HandleCenters(frame))
        {
            dc.DrawRectangle(
                Accent,
                handlePen,
                new Rect(point.X - handle / 2, point.Y - handle / 2, handle, handle));
        }

        // Rotation grips: a curved double arrow hugging each corner handle from outside.
        var grip = RotateGripGeometry(frame, unit);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 3.4 * unit)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
        }, grip);
        dc.DrawGeometry(null, new Pen(Accent, 1.6 * unit) // the frame's blue
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
        }, grip);

        dc.Pop();
    }

    private const double GripRadius = 13;     // DIP, arc radius around the corner
    private const double GripSweep = 34;      // degrees either side of the diagonal
    private const double GripHitRadius = 9;   // DIP around the arc's middle

    private static readonly (double X, double Y)[] CornerDirections = { (-1, -1), (1, -1), (1, 1), (-1, 1) };

    /// <summary>
    /// Frame of a text being typed: handles resize, grips rotate and the band around the
    /// text (inside and just outside the dashed line) moves it; the text itself is left to
    /// the caret.
    /// </summary>
    public static FrameHit HitEditFrame(Annotation annotation, Point point, double unit)
    {
        var hit = HitFrame(annotation, point, unit, handlesOnly: true);
        if (hit.Any)
        {
            return hit;
        }

        var bounds = annotation.Bounds;
        var frame = SelectionFrame(bounds, unit);
        var center = new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
        var local = RotatePoint(point, center, -annotation.Rotation);
        var outer = frame;
        outer.Inflate(6 * unit, 6 * unit);
        return outer.Contains(local) && !bounds.Contains(local) ? new FrameHit(-1, false, true) : FrameHit.None;
    }

    /// <summary>Middle points of the four rotation grips (frame-local coordinates).</summary>
    public static Point[] RotateGripCenters(Rect frame, double unit)
    {
        var corners = new[] { frame.TopLeft, frame.TopRight, frame.BottomRight, frame.BottomLeft };
        var distance = GripRadius * unit / Math.Sqrt(2);
        return corners.Select((c, i) => new Point(c.X + CornerDirections[i].X * distance, c.Y + CornerDirections[i].Y * distance)).ToArray();
    }

    private static Geometry RotateGripGeometry(Rect frame, double unit)
    {
        var corners = new[] { frame.TopLeft, frame.TopRight, frame.BottomRight, frame.BottomLeft };
        var radius = GripRadius * unit;
        var head = 3.6 * unit;
        var group = new GeometryGroup();
        for (var i = 0; i < 4; i++)
        {
            var c = corners[i];
            var middle = Math.Atan2(CornerDirections[i].Y, CornerDirections[i].X);
            var a1 = middle - GripSweep * Math.PI / 180;
            var a2 = middle + GripSweep * Math.PI / 180;
            Point At(double a) => new(c.X + radius * Math.Cos(a), c.Y + radius * Math.Sin(a));
            var start = At(a1);
            var end = At(a2);
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
            group.Children.Add(new PathGeometry(new[] { figure }));

            // Arrowheads point along the arc, away from its middle.
            foreach (var (tip, angle, sign) in new[] { (start, a1, -1.0), (end, a2, 1.0) })
            {
                var tangent = new Vector(-Math.Sin(angle) * sign, Math.Cos(angle) * sign);
                var normal = new Vector(Math.Cos(angle), Math.Sin(angle));
                var back = tip - tangent * head;
                var heads = new PathFigure { StartPoint = back + normal * head * 0.75, IsClosed = false };
                heads.Segments.Add(new LineSegment(tip, true));
                heads.Segments.Add(new LineSegment(back - normal * head * 0.75, true));
                group.Children.Add(new PathGeometry(new[] { heads }));
            }
        }

        group.Freeze();
        return group;
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

        // The rotation grips outside the corner handles.
        foreach (var grip in RotateGripCenters(frame, unit))
        {
            if ((local - grip).Length <= GripHitRadius * unit)
            {
                return new FrameHit(-1, true, false);
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
        var unit = Scale; // canvas pixels per DIP

        var blue = new SolidColorBrush(Color.FromRgb(0x2F, 0x7B, 0xFF));
        if (SelectionPath != null)
        {
            // Lasso capture: its outline only (no handles / rounding dots; it can be moved).
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), unit * 3.6), SelectionPath);
            dc.DrawGeometry(null, new Pen(blue, unit * 2), SelectionPath);
            return;
        }

        var corner = SelectionCornerRadius;
        if (SelectionDragging)
        {
            // While dragging: just a bold line.
            dc.DrawRoundedRectangle(null, new Pen(blue, unit * 3.5), selection, corner, corner);
        }
        else
        {
            dc.DrawRoundedRectangle(null, new Pen(blue, unit * 3.5), selection, corner, corner);

            // Rounding dots inside the corners.
            foreach (var dot in CornerDots(selection, corner, unit))
            {
                dc.DrawEllipse(Brushes.White, new Pen(blue, unit * 1.5), dot, 3.6 * unit, 3.6 * unit);
            }

            const double handleDip = 10;
            var handleSize = handleDip * unit;
            var handlePen = new Pen(Brushes.White, unit * 1.5);

            foreach (var center in HandleCenters(selection))
            {
                dc.DrawRectangle(
                    blue,
                    handlePen,
                    new Rect(center.X - handleSize / 2, center.Y - handleSize / 2, handleSize, handleSize));
            }
        }

        if (!SizeLabelVisible)
        {
            return;
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
