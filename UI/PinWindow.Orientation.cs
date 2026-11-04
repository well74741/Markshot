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

        AnimateOrientation(op);
    }

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

    /// <summary>F: back to 100%, opaque and the original orientation. Annotations are kept.</summary>
    private void ResetPinImageTransform()
    {
        for (var i = _orientation.Count - 1; i >= 0; i--)
        {
            switch (_orientation[i])
            {
                case 'R':
                    // Undo a 90° counter-clockwise turn with three more.
                    ApplyOrientation('R', record: false);
                    ApplyOrientation('R', record: false);
                    ApplyOrientation('R', record: false);
                    break;
                default:
                    ApplyOrientation(_orientation[i], record: false); // flips undo themselves
                    break;
            }
        }

        _orientation.Clear();
        ResetView();
    }

    private protected void ApplyPinImageTransform(Transform transform, Func<Point, Point> map, int newWidth, int newHeight, char op)
    {
        CancelInPlaceTranslation();
        _textEditor.Commit();
        // Transform the image under the annotations (not the composed one) and map
        // every element, so re-editing never shows the annotations twice.
        var transformed = new TransformedBitmap(_baseImage, transform);
        transformed.Freeze();

        // One pattern per mosaic strength: the stored undo steps share it with the live elements.
        var patterns = new Dictionary<double, (BitmapSource? Pattern, Rect Area)>();
        (BitmapSource? Pattern, Rect Area) PatternFor(double block)
        {
            if (!patterns.TryGetValue(block, out var built))
            {
                patterns[block] = built = MosaicBuilder.BuildCanvas(transformed, block);
            }

            return built;
        }

        void SetPattern(Annotation mosaic, double block)
        {
            switch (mosaic)
            {
                case MosaicAnnotation box:
                    (box.Pattern, box.PatternRect) = PatternFor(block);
                    break;
                case MosaicBrushAnnotation brush:
                    (brush.Pattern, brush.PatternRect) = PatternFor(block);
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
