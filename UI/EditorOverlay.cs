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
        Mesh = new Point[(MeshColumns + 1) * (MeshRows + 1)];
        for (var r = 0; r <= MeshRows; r++)
        {
            for (var c = 0; c <= MeshColumns; c++)
            {
                Mesh[r * (MeshColumns + 1) + c] = new Point(w * c / MeshColumns, h * r / MeshRows);
            }
        }

        Cursor = mode == EditorOverlayMode.Crop ? Cursors.Cross : mode == EditorOverlayMode.Path ? Cursors.Pen : Cursors.Arrow;
    }

    public EditorOverlayMode Mode { get; }

    public double Zoom { get; set; }

    /// <summary>The crop rectangle.</summary>
    public Rect Crop { get; set; }

    /// <summary>Keystone corners: top-left, top-right, bottom-right, bottom-left.</summary>
    public Point[] Quad { get; }

    public const int MeshColumns = 4;

    public const int MeshRows = 4;

    /// <summary>网格形变: the grid points (row by row), in image pixels.</summary>
    public Point[] Mesh { get; }

    /// <summary>The warped picture for the current grid (a small copy is fine: it is stretched over the image).</summary>
    public Func<IReadOnlyList<Point>, BitmapSource>? MeshPreview { get; set; }

    private BitmapSource? _meshImage;
    private bool _meshDirty = true;

    /// <summary>The cut-out path.</summary>
    public List<Point> PathPoints { get; } = new();

    /// <summary>The selection can be applied: a closed path, or a crop of at least 2 × 2 pixels.</summary>
    public bool Ready => Mode switch
    {
        EditorOverlayMode.Path => !_drawing && PathPoints.Count >= 3,
        EditorOverlayMode.Crop => Crop.Width >= 2 && Crop.Height >= 2,
        EditorOverlayMode.Mesh => Moved,
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
            {
                if (_meshDirty && MeshPreview != null)
                {
                    _meshImage = MeshPreview(Mesh);
                    _meshDirty = false;
                }

                // The warped picture covers the original (which shows through nowhere).
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), null, all);
                if (_meshImage != null)
                {
                    dc.DrawImage(_meshImage, all);
                }

                var thin = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), 1);
                var shade = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 2.6);
                for (var r = 0; r <= MeshRows; r++)
                {
                    for (var c = 0; c <= MeshColumns; c++)
                    {
                        var at = ToDip(MeshPoint(c, r));
                        if (c < MeshColumns)
                        {
                            dc.DrawLine(shade, at, ToDip(MeshPoint(c + 1, r)));
                            dc.DrawLine(thin, at, ToDip(MeshPoint(c + 1, r)));
                        }

                        if (r < MeshRows)
                        {
                            dc.DrawLine(shade, at, ToDip(MeshPoint(c, r + 1)));
                            dc.DrawLine(thin, at, ToDip(MeshPoint(c, r + 1)));
                        }
                    }
                }

                foreach (var point in Mesh)
                {
                    dc.DrawEllipse(Brushes.White, accent, ToDip(point), Grip, Grip);
                }

                break;
            }

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

    private Point MeshPoint(int column, int row) => Mesh[row * (MeshColumns + 1) + column];

    /// <summary>A grid point was moved away from where it started.</summary>
    public bool Moved { get; private set; }

    /// <summary>Moves a grid point (tests and dragging); the preview follows.</summary>
    public void MoveMeshPoint(int index, Point to)
    {
        Mesh[index] = new Point(Math.Clamp(to.X, -_imageWidth, _imageWidth * 2), Math.Clamp(to.Y, -_imageHeight, _imageHeight * 2));
        Moved = true;
        _meshDirty = true;
        InvalidateVisual();
        Changed?.Invoke();
    }

    private int MeshHit(Point dip)
    {
        for (var i = 0; i < Mesh.Length; i++)
        {
            if ((ToDip(Mesh[i]) - dip).Length <= Grip + 6)
            {
                return i;
            }
        }

        return -1;
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
                _corner = MeshHit(dip);
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
            case EditorOverlayMode.Mesh when pressed && _corner >= 0:
                // Free to leave the picture a little (the warp may pull an edge in or out).
                MoveMeshPoint(_corner, new Point(dip.X / Zoom, dip.Y / Zoom));
                e.Handled = true;
                return;
            case EditorOverlayMode.Mesh:
                Cursor = MeshHit(dip) >= 0 ? Cursors.SizeAll : Cursors.Arrow;
                break;
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
