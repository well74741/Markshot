namespace Markshot.UI;

/// <summary>What the editor's overlay is doing: picking a crop, a cut-out path or the four corners of a keystone fix.</summary>
public enum EditorOverlayMode
{
    Crop,
    Path,
    Keystone,
    Mesh
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

    /// <summary>The cut-out path.</summary>
    public List<Point> PathPoints { get; } = new();

    /// <summary>The selection can be applied: a closed path, or a crop of at least 2 × 2 pixels.</summary>
    public bool Ready => Mode switch
    {
        EditorOverlayMode.Path => !_drawing && PathPoints.Count >= 3,
        EditorOverlayMode.Crop => Crop.Width >= 2 && Crop.Height >= 2,
        EditorOverlayMode.Mesh => Warp.Changed,
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

                DrawLabel(dc, $"{(int)Math.Round(Crop.Width)} × {(int)Math.Round(Crop.Height)}", crop.TopLeft);
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
        if (_hover is { } hover && _warpGrab.Kind < 0 && WarpHit(ToDip(hover)).Kind < 0 && Warp.Locate(hover, 60) is { } at)
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

    /// <summary>A click on the picture (not on a point): 交叉拆分 there; the new crossing is then dragged. Null = off the picture.</summary>
    public (int Row, int Column)? SplitWarpAt(Point pixel)
    {
        if (Warp.Locate(pixel) is not { } at)
        {
            return null;
        }

        var crossing = Warp.SplitAt(at.U, at.V);
        WarpChanged();
        return crossing;
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
                _warpGrab = WarpHit(dip);
                if (_warpGrab.Kind < 0 && SplitWarpAt(new Point(dip.X / Zoom, dip.Y / Zoom)) is { } crossing)
                {
                    _warpGrab = (0, crossing.Row, crossing.Column, 0); // the new crossing follows the drag
                }

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
            case EditorOverlayMode.Mesh when pressed && _warpGrab.Kind >= 0:
                // Free to leave the picture (the canvas grows to hold the result).
                DragWarp(_warpGrab, new Point(dip.X / Zoom, dip.Y / Zoom));
                e.Handled = true;
                return;
            case EditorOverlayMode.Mesh:
                Cursor = WarpHit(dip).Kind >= 0 ? Cursors.SizeAll : Cursors.Cross;
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
