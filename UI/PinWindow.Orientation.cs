using System.Globalization;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

// Pin window: flip / rotate and the light sweep.
public partial class PinWindow
{
    private void FlipPinImage(bool horizontal) => ApplyOrientation(horizontal ? 'H' : 'V', record: true);

    private void RotatePinImageCounterClockwise() => ApplyOrientation('R', record: true);

    /// <summary>Flips ('H', 'V') or rotates 90° counter-clockwise ('R') the image under the annotations.</summary>
    private protected void ApplyOrientation(char op, bool record)
    {
        var width = _baseImage.PixelWidth;
        var height = _baseImage.PixelHeight;
        switch (op)
        {
            case 'H':
                ApplyPinImageTransform(new ScaleTransform(-1, 1), p => new Point(width - p.X, p.Y), width, height, op);
                break;
            case 'V':
                ApplyPinImageTransform(new ScaleTransform(1, -1), p => new Point(p.X, height - p.Y), width, height, op);
                break;
            default:
            {
                // Turn about the pin's centre: move the top-left so the centre stays where it is.
                var oldWidth = width * _zoom;
                var oldHeight = height * _zoom;
                _left += (int)Math.Round((oldWidth - oldHeight) / 2);
                _top += (int)Math.Round((oldHeight - oldWidth) / 2);
                ApplyPinImageTransform(new RotateTransform(-90), p => new Point(p.Y, width - p.X), height, width, op);
                break;
            }
        }

        if (record)
        {
            _orientation.Add(op);
        }

        if (!_quietOrientation)
        {
            AnimateOrientation(op);
        }
    }

    private bool _quietOrientation; // F: the steps are undone without their own animations

    /// <summary>
    /// A quick turn after a flip / rotation: the new image starts where the old one was
    /// (mirrored, or turned back 90°) and swings into place (240 ms, shared curve).
    /// </summary>
    private protected void AnimateOrientation(char op, bool clockwise = false)
    {
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        ImageHost.RenderTransformOrigin = new Point(0.5, 0.5);
        ImageHost.RenderTransform = new TransformGroup { Children = { scale, rotate } };
        var duration = new Duration(TimeSpan.FromMilliseconds(150));
        switch (op)
        {
            case 'H':
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(-1, 1, duration) { EasingFunction = Motion.Ease });
                break;
            case 'V':
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(-1, 1, duration) { EasingFunction = Motion.Ease });
                break;
            default:
                rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(clockwise ? -90 : 90, 0, duration) { EasingFunction = Motion.Ease });
                break;
        }
    }

    /// <summary>
    /// F: back to 100%, opaque and the original orientation, in one move: the steps are undone at once (unseen) and
    /// the picture swings / scales from how it looked straight to how it began. Annotations are kept.
    /// </summary>
    private void ResetPinImageTransform()
    {
        // What the flips / turns add up to (column vectors, y down): one turn, perhaps mirrored.
        int a = 1, b = 0, c = 0, d = 1;
        foreach (var op in _orientation)
        {
            var (oa, ob, oc, od) = op switch { 'H' => (-1, 0, 0, 1), 'V' => (1, 0, 0, -1), _ => (0, 1, -1, 0) };
            (a, b, c, d) = (oa * a + ob * c, oa * b + ob * d, oc * a + od * c, oc * b + od * d);
        }

        var zoom = _zoom;
        // The fewest steps back (at most a flip and three turns), whatever the history was.
        var back = ShortestWayBack(a, b, c, d);
        _quietOrientation = true;
        try
        {
            foreach (var op in back)
            {
                ApplyOrientation(op, record: false);
            }
        }
        finally
        {
            _quietOrientation = false;
        }

        _orientation.Clear();
        if (back.Count > 0)
        {
            // Composed and shown once, at the end.
            SourceImage = ComposeEditedImage();
            Img.Source = _editMode ? _baseImage : SourceImage;
            if (!_editMode)
            {
                RefreshTextLayer();
            }

            _selected = null;
            Editor.Selected = null;
            Toolbar.SetSelection(null);
            Editor.InvalidateVisual();
            UpdateAnimation();
        }

        ResetView();

        // From the old look to the original: a mirror (scale -1), the turn, the size.
        var mirror = a * d - b * c < 0 ? -1 : 1;
        var angle = Math.Atan2(c * mirror, a * mirror) * 180 / Math.PI;
        if (Math.Abs(angle) < 0.5 && mirror == 1 && Math.Abs(zoom - 1) < 0.001)
        {
            return;
        }

        var scale = new ScaleTransform(mirror * zoom, zoom);
        var rotate = new RotateTransform(angle);
        ImageHost.RenderTransformOrigin = new Point(0.5, 0.5);
        ImageHost.RenderTransform = new TransformGroup { Children = { scale, rotate } };
        var duration = new Duration(TimeSpan.FromMilliseconds(240));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(mirror * zoom, 1, duration) { EasingFunction = Motion.Ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(zoom, 1, duration) { EasingFunction = Motion.Ease });
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, 0, duration) { EasingFunction = Motion.Ease });
        ResetMotion = (mirror, angle, zoom);
    }

    /// <summary>The shortest list of steps whose sum undoes (a b; c d): [], R, R R, R R R, or a flip before them.</summary>
    private static List<char> ShortestWayBack(int a, int b, int c, int d)
    {
        (int, int, int, int) Of(IEnumerable<char> ops)
        {
            int p = 1, q = 0, r = 0, s = 1;
            foreach (var op in ops)
            {
                var (oa, ob, oc, od) = op switch { 'H' => (-1, 0, 0, 1), 'V' => (1, 0, 0, -1), _ => (0, 1, -1, 0) };
                (p, q, r, s) = (oa * p + ob * r, oa * q + ob * s, oc * p + od * r, oc * q + od * s);
            }

            return (p, q, r, s);
        }

        foreach (var flip in new[] { "", "H" })
        {
            for (var turns = 0; turns < 4; turns++)
            {
                var ops = (flip + new string('R', turns)).ToList();
                var (p, q, r, s) = Of(ops);
                // ops after the history = nothing at all
                if ((p * a + q * c, p * b + q * d, r * a + s * c, r * b + s * d) == (1, 0, 0, 1))
                {
                    return ops;
                }
            }
        }

        return new List<char>();
    }

    /// <summary>Tests: where the last F started its one move from (mirror, angle, size).</summary>
    internal (int Mirror, double Angle, double Zoom)? ResetMotion { get; private set; }

    private protected void ApplyPinImageTransform(Transform transform, Func<Point, Point> map, int newWidth, int newHeight, char op)
    {
        CancelInPlaceTranslation();
        _textEditor.Commit();
        // Transform the image under the annotations (not the composed one) and map
        // every element, so re-editing never shows the annotations twice.
        var transformed = new TransformedBitmap(_baseImage, transform);
        transformed.Freeze();
        if (IsMoving)
        {
            _animation = _animation!.Transformed(transform); // every frame turns too: it keeps moving
        }

        // One pattern per mosaic strength: the stored undo steps share it with the live elements.
        var patterns = new Dictionary<(double, double, bool), (BitmapSource? Pattern, Rect Area)>();
        (BitmapSource? Pattern, Rect Area) PatternFor(double block, double blur, bool gaussian)
        {
            if (!patterns.TryGetValue((block, blur, gaussian), out var built))
            {
                patterns[(block, blur, gaussian)] = built = MosaicBuilder.BuildCanvas(transformed, block, blur, gaussian);
            }

            return built;
        }

        void SetPattern(Annotation mosaic, double block)
        {
            switch (mosaic)
            {
                case MosaicAnnotation box:
                    (box.Pattern, box.PatternRect) = PatternFor(block, box.Gaussian ? box.Blur : 0, box.Gaussian);
                    break;
                case MosaicBrushAnnotation brush:
                    (brush.Pattern, brush.PatternRect) = PatternFor(block, brush.Gaussian ? brush.Blur : 0, brush.Gaussian);
                    break;
            }
        }

        void Map(Annotation annotation)
        {
        AnnotationOps.ReorientMasks(annotation, op);
        if (op is 'H' or 'V')
        {
            annotation.Rotation = -annotation.Rotation; // a mirrored rotated box turns the other way
        }

        switch (annotation)
        {
            case RectAnnotation rect:
                rect.Rect = MapRect(rect.Rect, map);
                break;
            case EllipseAnnotation ellipse:
                ellipse.Rect = MapRect(ellipse.Rect, map);
                break;
            case LineAnnotation line:
                line.Start = map(line.Start);
                line.End = map(line.End);
                break;
            case ArrowAnnotation arrow:
                arrow.Start = map(arrow.Start);
                arrow.End = map(arrow.End);
                break;
            case PenAnnotation pen:
                for (var i = 0; i < pen.Points.Count; i++)
                {
                    pen.Points[i] = map(pen.Points[i]);
                }
                break;
            case ImageAnnotation picture:
                picture.Rect = MapRect(picture.Rect, map);
                picture.Image = picture.Image == null ? null : ImageOps.Normalize(new TransformedBitmap(picture.Image, transform));
                picture.Origin = picture.Origin == null ? null : ImageOps.Normalize(new TransformedBitmap(picture.Origin, transform));
                break;
            case TextAnnotation text:
            {
                // Text stays readable: move its centre, do not mirror the glyphs.
                var bounds = text.Bounds;
                var center = map(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
                text.Translate(center.X - (bounds.X + bounds.Width / 2), center.Y - (bounds.Y + bounds.Height / 2));
                break;
            }
            case NumberAnnotation number:
                number.Center = map(number.Center);
                break;
            case WatermarkAnnotation watermark:
                watermark.Rect = new Rect(0, 0, newWidth, newHeight);
                break;
            case MosaicAnnotation mosaic:
                mosaic.Rect = MapRect(mosaic.Rect, map);
                SetPattern(mosaic, mosaic.BlockSize);
                break;
            case MosaicBrushAnnotation brush:
                for (var i = 0; i < brush.Points.Count; i++)
                {
                    brush.Points[i] = map(brush.Points[i]);
                }

                SetPattern(brush, brush.BlockSize);
                break;
        }
        }

        foreach (var annotation in Editor.Items)
        {
            Map(annotation);
        }

        if (!IsEditor)
        {
            // A pin's flip / turn is no undo step: undo after it lands in the new orientation.
            // (In the editor it is a step of its own, stored with the picture it belongs to.)
            _history.ForEachStored(Map);
        }

        _baseImage = transformed;
        _pixelWidth = newWidth;
        _pixelHeight = newHeight;
        Editor.Source = transformed;
        if (_quietOrientation)
        {
            return; // F: undone in one go; composed, laid out and shown once at the end
        }

        SourceImage = ComposeEditedImage();
        Img.Source = _editMode ? transformed : SourceImage;
        if (!_editMode)
        {
            RefreshTextLayer();
        }
        _selected = null;
        Editor.Selected = null;
        Toolbar.SetSelection(null);
        Editor.InvalidateVisual();
        ApplyBounds();
        UpdateAnimation(); // turned / flipped: the still picture from here on
    }

    private static Rect MapRect(Rect rect, Func<Point, Point> map)
    {
        var a = map(rect.TopLeft);
        var b = map(rect.BottomRight);
        return new Rect(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>
    /// A slanted band of light crosses the pin once, left to right (fast), clipped to its outline.
    /// </summary>
    private void PlayLightSweep()
    {
        var width = ImageHost.ActualWidth;
        var height = ImageHost.ActualHeight;
        if (width < 8 || height < 8)
        {
            return;
        }

        const double tilt = 22; // degrees from vertical
        var bandWidth = Math.Clamp(width * 0.18, 26, 120);
        var bandHeight = height / Math.Cos(tilt * Math.PI / 180) + bandWidth * 2;
        var band = new System.Windows.Shapes.Rectangle
        {
            Width = bandWidth,
            Height = bandHeight,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0, 255, 255, 255), 0),
                    new(Color.FromArgb(120, 255, 255, 255), 0.5),
                    new(Color.FromArgb(0, 255, 255, 255), 1)
                },
                new Point(0, 0.5), new Point(1, 0.5))
        };
        var move = new TranslateTransform();
        band.RenderTransform = new TransformGroup
        {
            Children = { new RotateTransform(tilt, bandWidth / 2, bandHeight / 2), move }
        };
        var layer = new Canvas
        {
            IsHitTestVisible = false,
            Width = width,
            Height = height,
            // Never outside the pin (rounded corners included).
            Clip = new RectangleGeometry(new Rect(0, 0, width, height), CornerRadius * _zoom, CornerRadius * _zoom)
        };
        if (ShapedShadow && Img.Source is { } picture)
        {
            // A freehand (lasso) pin: the light only crosses the cut-out shape, not its bounding box.
            layer.OpacityMask = new ImageBrush(picture) { Stretch = Stretch.Fill };
        }
        Canvas.SetTop(band, (height - bandHeight) / 2);
        layer.Children.Add(band);
        ImageHost.Children.Add(layer);

        var reach = bandHeight * Math.Sin(tilt * Math.PI / 180) / 2 + bandWidth;
        var sweep = new DoubleAnimation(-reach, width + reach, TimeSpan.FromMilliseconds(Math.Clamp(200 + width * 0.12, 220, 340)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        sweep.Completed += (_, _) => ImageHost.Children.Remove(layer);
        move.BeginAnimation(TranslateTransform.XProperty, sweep);
    }
}
