namespace Markshot.UI;

/// <summary>What the editor's overlay is doing: picking a crop, a cut-out path or the four corners of a keystone fix.</summary>
public enum EditorOverlayMode
{
    Crop,
    Path,
    Keystone,
    Mesh,
    Transform
}

/// <summary>
/// Drawn over the image in the editor while cropping, cutting along a path, correcting a keystone or
/// warping with a mesh (网格形变: the picture follows the grid points as they are dragged).
/// Coordinates are image pixels; <see cref="Zoom"/> converts them to this element's DIPs.
/// </summary>
public sealed class EditorOverlay : FrameworkElement
{
    private const double Grip = 5;
    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)));
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)));

    private readonly int _imageWidth;
    private readonly int _imageHeight;
    private string? _drag;
    private Point _dragStart;
    private Rect _dragOrigin;
    private int _corner = -1;
    private bool _drawing;
    private Point? _straight;

    public EditorOverlay(EditorOverlayMode mode, int imageWidth, int imageHeight, double zoom)
    {
        Mode = mode;
        _imageWidth = imageWidth;
        _imageHeight = imageHeight;
        Zoom = zoom;
        Crop = new Rect(0, 0, imageWidth, imageHeight);
        double w = imageWidth, h = imageHeight;
        // A keystone starts a little inside the image, so the corners are easy to grab.
        var inset = Math.Min(w, h) * 0.08;
        Quad = new[] { new Point(inset, inset), new Point(w - inset, inset), new Point(w - inset, h - inset), new Point(inset, h - inset) };
        Warp = new WarpMesh(w, h);

        Cursor = mode is EditorOverlayMode.Crop or EditorOverlayMode.Mesh ? Cursors.Cross : mode == EditorOverlayMode.Path ? Cursors.Pen : Cursors.Arrow;
    }

    public EditorOverlayMode Mode { get; }

    public double Zoom { get; set; }

    /// <summary>The crop rectangle.</summary>
    public Rect Crop { get; set; }

    /// <summary>Keystone corners: top-left, top-right, bottom-right, bottom-left.</summary>
    public Point[] Quad { get; }

    /// <summary>网格形变: the warp being edited (image pixels).</summary>
    public WarpMesh Warp { get; }

    /// <summary>The warped picture and where it goes (image pixels); a small copy is fine.</summary>
    public Func<WarpMesh, (BitmapSource Image, Rect Place)>? WarpPreview { get; set; }

    private (BitmapSource Image, Rect Place)? _warpImage;
    private bool _warpDirty = true;
    private (int Kind, int R, int C, int End) _warpGrab = (-1, 0, 0, 0); // kind 0 crossing, 1 across handle, 2 down handle
    private Point? _hover;
    private Vector _warpOffset; // grabbed point minus the mouse: the point does not jump to the pointer

    /// <summary>The one 交叉拆分 of this session is done: from now on the mouse only drags.</summary>
    public bool WarpSplit { get; private set; }

    /// <summary>Draws its own size label on the crop (the editor shows an editable one instead).</summary>
    public bool ShowSizeLabel { get; set; } = true;

    /// <summary>调节框: where the picture goes (image pixels, before turning) and its angle (degrees).</summary>
    public Rect TransformRect { get; set; }

    public double TransformAngle { get; set; }

    /// <summary>The picture drawn in the box (a small copy is fine).</summary>
    public BitmapSource? TransformPreview { get; set; }

    private string? _transformDrag;
    private Point _transformStart;
    private Rect _transformOrigin;
    private double _angleOrigin;
    private double _angleStart;

    /// <summary>The cut-out path.</summary>
    public List<Point> PathPoints { get; } = new();

    /// <summary>The selection can be applied: a closed path, or a crop of at least 2 × 2 pixels.</summary>
    public bool Ready => Mode switch
    {
        EditorOverlayMode.Path => !_drawing && PathPoints.Count >= 3,
        EditorOverlayMode.Crop => Crop.Width >= 2 && Crop.Height >= 2,
        EditorOverlayMode.Mesh => Warp.Changed,
        EditorOverlayMode.Transform => Math.Abs(TransformAngle) > 0.05 ||
                                       Math.Abs(TransformRect.Width - _imageWidth) >= 1 || Math.Abs(TransformRect.Height - _imageHeight) >= 1,
        _ => true
    };

    public event Action? Changed;

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private Point ToImage(Point dip) => new(Math.Clamp(dip.X / Zoom, 0, _imageWidth), Math.Clamp(dip.Y / Zoom, 0, _imageHeight));

    private Point ToDip(Point pixel) => new(pixel.X * Zoom, pixel.Y * Zoom);

    private Rect ToDip(Rect pixel) => new(ToDip(pixel.TopLeft), ToDip(pixel.BottomRight));

    protected override void OnRender(DrawingContext dc)
    {
        var all = new Rect(0, 0, _imageWidth * Zoom, _imageHeight * Zoom);
        dc.DrawRectangle(Brushes.Transparent, null, all); // takes the mouse everywhere over the image
        if (Mode == EditorOverlayMode.Transform)
        {
            DrawTransform(dc, all);
            return;
        }

        var white = new Pen(Brushes.White, 1.5);
        var accent = new Pen(Accent, 1.5);
        switch (Mode)
        {
            case EditorOverlayMode.Crop:
            {
                var crop = ToDip(Crop);
                dc.DrawGeometry(Dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(all), new RectangleGeometry(crop)));
                dc.DrawRectangle(null, accent, crop);
                var thin = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), 1);
                for (var i = 1; i < 3; i++)
                {
                    dc.DrawLine(thin, new Point(crop.X + crop.Width * i / 3, crop.Y), new Point(crop.X + crop.Width * i / 3, crop.Bottom));
                    dc.DrawLine(thin, new Point(crop.X, crop.Y + crop.Height * i / 3), new Point(crop.Right, crop.Y + crop.Height * i / 3));
                }

                foreach (var (_, grip) in CropGrips())
                {
                    DrawGrip(dc, ToDip(grip), accent);
                }

                if (ShowSizeLabel)
                {
                    DrawLabel(dc, $"{(int)Math.Round(Crop.Width)} × {(int)Math.Round(Crop.Height)}", crop.TopLeft);
                }
                break;
            }

            case EditorOverlayMode.Path:
            {
                if (PathPoints.Count == 0)
                {
                    break;
                }

                var figure = new StreamGeometry();
                using (var context = figure.Open())
                {
                    context.BeginFigure(ToDip(PathPoints[0]), !_drawing, !_drawing);
                    context.PolyLineTo(PathPoints.Skip(1).Select(ToDip).ToList(), true, true);
                }

                if (!_drawing)
                {
                    dc.DrawGeometry(Dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(all), figure));
                }

                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x96, 0, 0, 0)), 3.2), figure);
                dc.DrawGeometry(null, white, figure);
                if (_straight is { } preview)
                {
                    dc.DrawLine(accent, ToDip(PathPoints[^1]), ToDip(preview));
                }

                break;
            }

            case EditorOverlayMode.Mesh:
                DrawWarp(dc, all, accent);
                break;

            case EditorOverlayMode.Keystone:
            {
                var outline = new StreamGeometry();
                using (var context = outline.Open())
                {
                    context.BeginFigure(ToDip(Quad[0]), true, true);
                    context.PolyLineTo(Quad.Skip(1).Select(ToDip).ToList(), true, true);
                }

                dc.DrawGeometry(Dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(all), outline));
                var thin = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), 1);
                for (var i = 1; i < 3; i++)
                {
                    var t = i / 3.0;
                    dc.DrawLine(thin, ToDip(Lerp(Quad[0], Quad[1], t)), ToDip(Lerp(Quad[3], Quad[2], t)));
                    dc.DrawLine(thin, ToDip(Lerp(Quad[0], Quad[3], t)), ToDip(Lerp(Quad[1], Quad[2], t)));
                }

                dc.DrawGeometry(null, accent, outline);
                foreach (var corner in Quad)
                {
                    DrawGrip(dc, ToDip(corner), accent);
                }

                break;
            }
        }
    }

    /// <summary>调节框: the picture in its box (turned), eight handles; outside the box = turning.</summary>
    private void DrawTransform(DrawingContext dc, Rect all)
    {
        // The whole area around takes the mouse too (turning happens outside the box).
        var reach = all;
        reach.Inflate(4000, 4000);
        dc.DrawRectangle(Brushes.Transparent, null, reach);
        // The picture itself is hidden meanwhile (the editor sets it see-through): what the box does not
        // cover shows the checkerboard, as it will be transparent.
        var box = ToDip(TransformRect);
        var center = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        dc.PushTransform(new RotateTransform(TransformAngle, center.X, center.Y));
        if (TransformPreview != null)
        {
            dc.DrawImage(TransformPreview, box);
        }

        var accent = new Pen(Accent, 1.5);
        dc.DrawRectangle(null, accent, box);
        foreach (var (_, grip) in Grips(TransformRect))
        {
            DrawGrip(dc, ToDip(grip), accent);
        }

        dc.Pop();
        if (ShowSizeLabel)
        {
            DrawLabel(dc, $"{(int)Math.Round(TransformRect.Width)} × {(int)Math.Round(TransformRect.Height)}   {TransformAngle:0.#}°", new Point(box.X, box.Y));
        }
    }

    /// <summary>The box turned: its outline on the screen (image pixels), for placing the size label above it.</summary>
    public Rect TransformBounds
    {
        get
        {
            var r = TransformRect;
            var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            var corners = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft }
                .Select(p => Annotations.AnnotationEditor.RotatePoint(p, center, TransformAngle)).ToList();
            return new Rect(new Point(corners.Min(p => p.X), corners.Min(p => p.Y)), new Point(corners.Max(p => p.X), corners.Max(p => p.Y)));
        }
    }

    private static IEnumerable<(int Handle, Point At)> Grips(Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        yield return (0, r.TopLeft);
        yield return (1, new Point(cx, r.Top));
        yield return (2, r.TopRight);
        yield return (3, new Point(r.Right, cy));
        yield return (4, r.BottomRight);
        yield return (5, new Point(cx, r.Bottom));
        yield return (6, r.BottomLeft);
        yield return (7, new Point(r.Left, cy));
    }

    /// <summary>What a press at <paramref name="pixel"/> does: a handle ("0".."7"), "move" inside, "rotate" outside.</summary>
    public string TransformHit(Point pixel)
    {
        var center = new Point(TransformRect.X + TransformRect.Width / 2, TransformRect.Y + TransformRect.Height / 2);
        var local = Annotations.AnnotationEditor.RotatePoint(pixel, center, -TransformAngle);
        foreach (var (handle, at) in Grips(TransformRect))
        {
            if ((ToDip(at) - ToDip(local)).Length <= Grip + 4)
            {
                return handle.ToString(CultureInfo.InvariantCulture);
            }
        }

        return TransformRect.Contains(local) ? "move" : "rotate";
    }

    /// <summary>A press of the box (tests and the mouse).</summary>
    public void PressTransform(Point pixel)
    {
        _transformDrag = TransformHit(pixel);
        _transformStart = pixel;
        _transformOrigin = TransformRect;
        _angleOrigin = TransformAngle;
        var center = new Point(TransformRect.X + TransformRect.Width / 2, TransformRect.Y + TransformRect.Height / 2);
        _angleStart = Math.Atan2(pixel.Y - center.Y, pixel.X - center.X) * 180 / Math.PI;
    }

    public void DragTransform(Point pixel)
    {
        switch (_transformDrag)
        {
            case null:
                return;
            case "move":
                TransformRect = new Rect(_transformOrigin.X + pixel.X - _transformStart.X, _transformOrigin.Y + pixel.Y - _transformStart.Y,
                    _transformOrigin.Width, _transformOrigin.Height);
                break;
            case "rotate":
            {
                var center = new Point(_transformOrigin.X + _transformOrigin.Width / 2, _transformOrigin.Y + _transformOrigin.Height / 2);
                var angle = _angleOrigin + Math.Atan2(pixel.Y - center.Y, pixel.X - center.X) * 180 / Math.PI - _angleStart;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    angle = Math.Round(angle / 15) * 15;
                }

                TransformAngle = Math.IEEERemainder(angle, 360);
                break;
            }

            default:
                TransformRect = UI.AnnotationOps.ResizeFromHandle(_transformOrigin, TransformAngle, int.Parse(_transformDrag, CultureInfo.InvariantCulture),
                    pixel, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                break;
        }

        InvalidateVisual();
        Changed?.Invoke();
    }

    private void DrawWarp(DrawingContext dc, Rect all, Pen accent)
    {
        if (_warpDirty && WarpPreview != null)
        {
            _warpImage = WarpPreview(Warp);
            _warpDirty = false;
        }

        // The warped picture covers the original (which shows through nowhere).
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), null, all);
        if (_warpImage is { } warped)
        {
            dc.DrawImage(warped.Image, ToDip(warped.Place));
        }

        var edges = new StreamGeometry();
        using (var context = edges.Open())
        {
            for (var r = 0; r <= Warp.Rows; r++)
            {
                for (var c = 0; c < Warp.Columns; c++)
                {
                    context.BeginFigure(ToDip(Warp.Anchors[r, c]), false, false);
                    context.BezierTo(ToDip(Warp.Across[r, c, 0]), ToDip(Warp.Across[r, c, 1]), ToDip(Warp.Anchors[r, c + 1]), true, false);
                }
            }

            for (var r = 0; r < Warp.Rows; r++)
            {
                for (var c = 0; c <= Warp.Columns; c++)
                {
                    context.BeginFigure(ToDip(Warp.Anchors[r, c]), false, false);
                    context.BezierTo(ToDip(Warp.Down[r, c, 0]), ToDip(Warp.Down[r, c, 1]), ToDip(Warp.Anchors[r + 1, c]), true, false);
                }
            }
        }

        edges.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 3), edges);
        dc.DrawGeometry(null, accent, edges);

        // Before a split: the cross that a click would add, along the warped grid.
        if (!WarpSplit && _hover is { } hover && _warpGrab.Kind < 0 && WarpHit(ToDip(hover)).Kind < 0 && Warp.Locate(hover, 60) is { } at)
        {
            var guide = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
            var cross = new StreamGeometry();
            using (var context = cross.Open())
            {
                context.BeginFigure(ToDip(Warp.Evaluate(at.U, 0)), false, false);
                context.PolyLineTo(Enumerable.Range(1, 48).Select(i => ToDip(Warp.Evaluate(at.U, i / 48.0))).ToList(), true, false);
                context.BeginFigure(ToDip(Warp.Evaluate(0, at.V)), false, false);
                context.PolyLineTo(Enumerable.Range(1, 48).Select(i => ToDip(Warp.Evaluate(i / 48.0, at.V))).ToList(), true, false);
            }

            cross.Freeze();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 2.4), cross);
            dc.DrawGeometry(null, guide, cross);
        }

        var handleLine = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0x4C, 0x8D, 0xFF)), 1);
        var handleFill = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
        foreach (var (anchor, handle) in Handles())
        {
            dc.DrawLine(handleLine, ToDip(anchor), ToDip(handle));
            dc.DrawEllipse(handleFill, null, ToDip(handle), 3.5, 3.5);
        }

        for (var r = 0; r <= Warp.Rows; r++)
        {
            for (var c = 0; c <= Warp.Columns; c++)
            {
                dc.DrawEllipse(Brushes.White, accent, ToDip(Warp.Anchors[r, c]), Grip - 1, Grip - 1);
            }
        }
    }

    /// <summary>Every handle with the crossing it belongs to.</summary>
    private IEnumerable<(Point Anchor, Point Handle)> Handles()
    {
        for (var r = 0; r <= Warp.Rows; r++)
        {
            for (var c = 0; c < Warp.Columns; c++)
            {
                yield return (Warp.Anchors[r, c], Warp.Across[r, c, 0]);
                yield return (Warp.Anchors[r, c + 1], Warp.Across[r, c, 1]);
            }
        }

        for (var r = 0; r < Warp.Rows; r++)
        {
            for (var c = 0; c <= Warp.Columns; c++)
            {
                yield return (Warp.Anchors[r, c], Warp.Down[r, c, 0]);
                yield return (Warp.Anchors[r + 1, c], Warp.Down[r, c, 1]);
            }
        }
    }

    /// <summary>What is under the mouse: a crossing first, then a handle.</summary>
    private (int Kind, int R, int C, int End) WarpHit(Point dip)
    {
        for (var r = 0; r <= Warp.Rows; r++)
        {
            for (var c = 0; c <= Warp.Columns; c++)
            {
                if ((ToDip(Warp.Anchors[r, c]) - dip).Length <= Grip + 4)
                {
                    return (0, r, c, 0);
                }
            }
        }

        for (var r = 0; r <= Warp.Rows; r++)
        {
            for (var c = 0; c < Warp.Columns; c++)
            {
                for (var k = 0; k < 2; k++)
                {
                    if ((ToDip(Warp.Across[r, c, k]) - dip).Length <= 7)
                    {
                        return (1, r, c, k);
                    }
                }
            }
        }

        for (var r = 0; r < Warp.Rows; r++)
        {
            for (var c = 0; c <= Warp.Columns; c++)
            {
                for (var k = 0; k < 2; k++)
                {
                    if ((ToDip(Warp.Down[r, c, k]) - dip).Length <= 7)
                    {
                        return (2, r, c, k);
                    }
                }
            }
        }

        return (-1, 0, 0, 0);
    }

    /// <summary>
    /// The first click on the picture: 交叉拆分 there (once per session); the new crossing is then dragged.
    /// Null = off the picture, or already split.
    /// </summary>
    public (int Row, int Column)? SplitWarpAt(Point pixel)
    {
        if (WarpSplit || Warp.Locate(pixel) is not { } at)
        {
            return null;
        }

        var crossing = Warp.SplitAt(at.U, at.V);
        WarpSplit = true;
        WarpChanged();
        return crossing;
    }

    /// <summary>After the split, a press away from the points takes the nearest crossing (dragged from where it is).</summary>
    private (int Kind, int R, int C, int End) NearestCrossing(Point pixel)
    {
        var best = (Kind: -1, R: 0, C: 0, End: 0);
        var distance = double.MaxValue;
        for (var r = 0; r <= Warp.Rows; r++)
        {
            for (var c = 0; c <= Warp.Columns; c++)
            {
                var d = (Warp.Anchors[r, c] - pixel).LengthSquared;
                if (d < distance)
                {
                    distance = d;
                    best = (0, r, c, 0);
                }
            }
        }

        return best;
    }

    private Point GrabbedPoint((int Kind, int R, int C, int End) grab) => grab.Kind switch
    {
        0 => Warp.Anchors[grab.R, grab.C],
        1 => Warp.Across[grab.R, grab.C, grab.End],
        2 => Warp.Down[grab.R, grab.C, grab.End],
        _ => default
    };

    /// <summary>A press at <paramref name="pixel"/> (tests and the mouse): split first, then grab; returns what is dragged.</summary>
    public (int Kind, int R, int C, int End) PressWarp(Point pixel)
    {
        var grab = WarpHit(ToDip(pixel));
        if (grab.Kind < 0)
        {
            if (!WarpSplit)
            {
                if (SplitWarpAt(pixel) is { } crossing)
                {
                    grab = (0, crossing.Row, crossing.Column, 0); // the new crossing follows the drag
                }
            }
            else
            {
                grab = NearestCrossing(pixel); // drag mode: the nearest crossing comes along
            }
        }

        _warpGrab = grab;
        _warpOffset = grab.Kind >= 0 ? GrabbedPoint(grab) - pixel : default;
        return grab;
    }

    /// <summary>The mouse moved to <paramref name="pixel"/> with the button down.</summary>
    public void DragWarpTo(Point pixel)
    {
        if (_warpGrab.Kind >= 0)
        {
            DragWarp(_warpGrab, pixel + _warpOffset);
        }
    }

    /// <summary>A crossing or handle dragged to <paramref name="pixel"/> (tests and the mouse).</summary>
    public void DragWarp((int Kind, int R, int C, int End) grab, Point pixel)
    {
        var to = new Point(Math.Clamp(pixel.X, -_imageWidth, _imageWidth * 2.0), Math.Clamp(pixel.Y, -_imageHeight, _imageHeight * 2.0));
        switch (grab.Kind)
        {
            case 0:
                Warp.MoveAnchor(grab.R, grab.C, to);
                break;
            case 1:
                Warp.MoveHandle(false, grab.R, grab.C, grab.End, to);
                break;
            case 2:
                Warp.MoveHandle(true, grab.R, grab.C, grab.End, to);
                break;
            default:
                return;
        }

        WarpChanged();
    }

    private void WarpChanged()
    {
        _warpDirty = true;
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover != null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    private static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static void DrawGrip(DrawingContext dc, Point center, Pen pen) =>
        dc.DrawRectangle(Brushes.White, pen, new Rect(center.X - Grip, center.Y - Grip, Grip * 2, Grip * 2));

    private void DrawLabel(DrawingContext dc, string text, Point at)
    {
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var box = new Rect(at.X, Math.Max(0, at.Y - label.Height - 6), label.Width + 8, label.Height + 2);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xCD, 0x14, 0x14, 0x14)), null, box, 3, 3);
        dc.DrawText(label, new Point(box.X + 4, box.Y + 1));
    }

    private IEnumerable<(string Kind, Point At)> CropGrips()
    {
        var r = Crop;
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        yield return ("nw", r.TopLeft);
        yield return ("n", new Point(cx, r.Top));
        yield return ("ne", r.TopRight);
        yield return ("e", new Point(r.Right, cy));
        yield return ("se", r.BottomRight);
        yield return ("s", new Point(cx, r.Bottom));
        yield return ("sw", r.BottomLeft);
        yield return ("w", new Point(r.Left, cy));
    }

    private string? CropHit(Point dip)
    {
        foreach (var (kind, at) in CropGrips())
        {
            if ((ToDip(at) - dip).Length <= Grip + 4)
            {
                return kind;
            }
        }

        return ToDip(Crop).Contains(dip) ? "move" : null;
    }

    private static Cursor CursorFor(string? kind) => kind switch
    {
        "nw" or "se" => Cursors.SizeNWSE,
        "ne" or "sw" => Cursors.SizeNESW,
        "n" or "s" => Cursors.SizeNS,
        "e" or "w" => Cursors.SizeWE,
        "move" => Cursors.SizeAll,
        _ => Cursors.Cross
    };

    private int CornerHit(Point dip)
    {
        for (var i = 0; i < Quad.Length; i++)
        {
            if ((ToDip(Quad[i]) - dip).Length <= Grip + 6)
            {
                return i;
            }
        }

        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var dip = e.GetPosition(this);
        var pixel = ToImage(dip);
        switch (Mode)
        {
            case EditorOverlayMode.Crop:
                _drag = CropHit(dip) ?? "new";
                _dragStart = pixel;
                _dragOrigin = Crop;
                break;
            case EditorOverlayMode.Path:
                PathPoints.Clear();
                PathPoints.Add(pixel);
                _drawing = true;
                break;
            case EditorOverlayMode.Keystone:
                _corner = CornerHit(dip);
                break;
            case EditorOverlayMode.Mesh:
                PressWarp(new Point(dip.X / Zoom, dip.Y / Zoom));
                break;
            case EditorOverlayMode.Transform:
                PressTransform(new Point(dip.X / Zoom, dip.Y / Zoom));
                break;
        }

        CaptureMouse();
        InvalidateVisual();
        Changed?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var dip = e.GetPosition(this);
        var pixel = ToImage(dip);
        var pressed = e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured;
        switch (Mode)
        {
            case EditorOverlayMode.Crop when pressed:
                DragCrop(pixel);
                break;
            case EditorOverlayMode.Crop:
                Cursor = CursorFor(CropHit(dip));
                break;
            case EditorOverlayMode.Path when pressed && _drawing:
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    _straight = pixel; // straight segment: placed when Shift is released
                }
                else
                {
                    if (_straight != null || (pixel - PathPoints[^1]).Length * Zoom >= 1.5)
                    {
                        PathPoints.Add(pixel);
                    }

                    _straight = null;
                }

                break;
            case EditorOverlayMode.Keystone when pressed && _corner >= 0:
                Quad[_corner] = pixel;
                break;
            case EditorOverlayMode.Keystone:
                Cursor = CornerHit(dip) >= 0 ? Cursors.SizeAll : Cursors.Arrow;
                break;
            case EditorOverlayMode.Transform when pressed:
                DragTransform(new Point(dip.X / Zoom, dip.Y / Zoom));
                e.Handled = true;
                return;
            case EditorOverlayMode.Transform:
                Cursor = TransformHit(new Point(dip.X / Zoom, dip.Y / Zoom)) switch
                {
                    "move" => Cursors.SizeAll,
                    "rotate" => ToolCursors.GetRotationCursor(true),
                    "0" or "4" => Cursors.SizeNWSE,
                    "2" or "6" => Cursors.SizeNESW,
                    "1" or "5" => Cursors.SizeNS,
                    _ => Cursors.SizeWE
                };
                e.Handled = true;
                return;
            case EditorOverlayMode.Mesh when pressed && _warpGrab.Kind >= 0:
                // Free to leave the picture (the canvas grows to hold the result).
                DragWarpTo(new Point(dip.X / Zoom, dip.Y / Zoom));
                e.Handled = true;
                return;
            case EditorOverlayMode.Mesh:
                // Before the split: a cross to place it; after it: dragging only.
                Cursor = WarpHit(dip).Kind >= 0 ? Cursors.SizeAll : WarpSplit ? Cursors.Hand : Cursors.Cross;
                _hover = new Point(dip.X / Zoom, dip.Y / Zoom);
                InvalidateVisual();
                e.Handled = true;
                return;
            default:
                return;
        }

        if (pressed)
        {
            InvalidateVisual();
            Changed?.Invoke();
        }

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (Mode == EditorOverlayMode.Path && _drawing)
        {
            if (_straight is { } end)
            {
                PathPoints.Add(end);
            }

            _straight = null;
            _drawing = false;
        }

        _transformDrag = null;
        _warpGrab = (-1, 0, 0, 0);
        _drag = null;
        _corner = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
        Changed?.Invoke();
        e.Handled = true;
    }

    private void DragCrop(Point pixel)
    {
        var dx = pixel.X - _dragStart.X;
        var dy = pixel.Y - _dragStart.Y;
        var r = _dragOrigin;
        switch (_drag)
        {
            case "new":
                Crop = new Rect(_dragStart, pixel);
                return;
            case "move":
                Crop = new Rect(Math.Clamp(r.X + dx, 0, _imageWidth - r.Width), Math.Clamp(r.Y + dy, 0, _imageHeight - r.Height), r.Width, r.Height);
                return;
        }

        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        if (_drag!.Contains('w'))
        {
            left = Math.Min(pixel.X, right - 2);
        }

        if (_drag.Contains('e'))
        {
            right = Math.Max(pixel.X, left + 2);
        }

        if (_drag.Contains('n'))
        {
            top = Math.Min(pixel.Y, bottom - 2);
        }

        if (_drag.Contains('s'))
        {
            bottom = Math.Max(pixel.Y, top + 2);
        }

        Crop = new Rect(new Point(left, top), new Point(right, bottom));
    }
}
