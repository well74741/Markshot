using Markshot.Annotations;

namespace Markshot.UI;

/// <summary>Annotation operations shared by the capture overlay and the pin window.</summary>
internal static class AnnotationOps
{
    /// <summary>
    /// The key actually pressed. With a Chinese IME active WPF reports letters and
    /// Space as <see cref="Key.ImeProcessed"/>, and Alt combinations as <see cref="Key.System"/>.
    /// </summary>
    public static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.System => e.SystemKey,
        _ => e.Key
    };

    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    /// <summary>Creates the element a drag with <paramref name="tool"/> starts, or null.</summary>
    public static Annotation? Create(AnnotationTool tool, Point point, AnnotationToolbar toolbar, int number, BitmapSource source)
    {
        var color = toolbar.Color;
        var thickness = toolbar.Thickness;
        Annotation? created = tool switch
        {
            AnnotationTool.Rectangle => new RectAnnotation
            {
                Rect = new Rect(point, point), Color = color, Thickness = thickness,
                Fill = toolbar.FillEnabled, CornerRadius = toolbar.CornerRadius
            },
            AnnotationTool.Ellipse => new EllipseAnnotation
            {
                Rect = new Rect(point, point), Color = color, Thickness = thickness, Fill = toolbar.FillEnabled
            },
            AnnotationTool.Arrow => new ArrowAnnotation { Start = point, End = point, Color = color, Thickness = thickness },
            AnnotationTool.Line => new LineAnnotation { Start = point, End = point, Color = color, Thickness = thickness },
            AnnotationTool.Pen => new PenAnnotation { Color = color, Thickness = thickness },
            AnnotationTool.Highlighter => new PenAnnotation { Color = color, Thickness = thickness, Highlight = true },
            AnnotationTool.Mosaic => new MosaicAnnotation { Rect = new Rect(point, point), BlockSize = toolbar.MosaicLevel },
            AnnotationTool.MosaicBrush => new MosaicBrushAnnotation
            {
                Thickness = Math.Max(2, thickness), BlockSize = toolbar.MosaicLevel
            },
            AnnotationTool.Number => new NumberAnnotation
            {
                Center = point, Number = number, Color = color, Thickness = 1.5, Radius = Math.Max(10, thickness)
            },
            _ => null
        };

        if (created != null)
        {
            created.Opacity = toolbar.ElementOpacity;
        }

        switch (created)
        {
            case PenAnnotation pen:
                pen.Points.Add(point);
                break;
            case MosaicBrushAnnotation brush:
                brush.Points.Add(point);
                MosaicBuilder.Rebuild(brush, source);
                break;
            case MosaicAnnotation box:
                MosaicBuilder.Rebuild(box, source);
                break;
        }

        return created;
    }

    /// <summary>True for an accidental click that produced no usable element.</summary>
    /// <summary>
    /// Plain wheel on a selected element: its thickness / size (a mosaic area has no stroke, so its strength).
    /// Returns the new size for the toolbar, or null when the toolbar size is unaffected.
    /// </summary>
    public static double? WheelSize(Annotation annotation, int delta, BitmapSource source)
    {
        var step = Math.Sign(delta);
        switch (annotation)
        {
            case TextAnnotation text:
                text.FontSize = Math.Clamp(text.FontSize + step * 2, 8, 120);
                return text.FontSize;
            case NumberAnnotation number:
                number.Radius = Math.Clamp(number.Radius + step * 2, 8, 80);
                return number.Radius;
            case WatermarkAnnotation watermark:
                watermark.FontSize = Math.Clamp(watermark.FontSize + step * 2, 10, 120);
                return watermark.FontSize;
            case MosaicAnnotation mosaic:
                mosaic.BlockSize = Math.Clamp(mosaic.BlockSize + step * 2, 4, 40);
                MosaicBuilder.Rebuild(mosaic, source);
                return null;
            case MosaicBrushAnnotation brush:
                brush.Thickness = Math.Clamp(brush.Thickness + step * 2, 4, 80);
                return brush.Thickness;
            default:
                annotation.Thickness = Math.Clamp(annotation.Thickness + step * 0.5, 1, 24);
                return annotation.Thickness;
        }
    }

    /// <summary>
    /// Whether a press inside the selection frame moves the element. With a freehand tool (or a
    /// drawing tool on a line-like element) only a press on the element itself does, so drawing
    /// over a freshly created (still selected) stroke keeps drawing.
    /// </summary>
    public static bool InsideMoves(Annotation selected, AnnotationTool tool, Point point, double tolerance)
    {
        var freehand = tool is AnnotationTool.Pen or AnnotationTool.Highlighter or AnnotationTool.MosaicBrush or AnnotationTool.Eraser;
        var drawing = tool is not AnnotationTool.None and not AnnotationTool.Select;
        var open = selected is PenAnnotation or LineAnnotation or ArrowAnnotation or MosaicBrushAnnotation;
        return !(freehand || (drawing && open)) || selected.HitTest(point, tolerance);
    }

    /// <summary>
    /// Frame hit for the selection. An element that is selected only because it was just created
    /// (with a drawing tool still active) answers to its handle squares alone — the edges, the
    /// rotation band and the inside keep drawing — and not at all for click / freehand tools, so
    /// the next element can be drawn right next to it.
    /// </summary>
    public static FrameHit SelectionHit(Annotation selected, bool fresh, AnnotationTool tool, Point point, double unit)
    {
        var drawing = tool is not AnnotationTool.None and not AnnotationTool.Select;
        if (!fresh || !drawing)
        {
            return AnnotationEditor.HitFrame(selected, point, unit);
        }

        if (tool is AnnotationTool.Number or AnnotationTool.Pen or AnnotationTool.Highlighter
            or AnnotationTool.MosaicBrush or AnnotationTool.Eraser)
        {
            return FrameHit.None;
        }

        return AnnotationEditor.HitFrame(selected, point, unit, handlesOnly: true);
    }

    /// <summary>
    /// Offset from the pressed point to the grabbed handle of the element itself (the frame
    /// is drawn a few pixels outside the element). Adding it to the pointer keeps a click on a
    /// handle from making the element jump to the pointer.
    /// </summary>
    public static Vector ResizeGrab(Rect bounds, double rotation, int handle, Point press)
    {
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var grabbed = AnnotationEditor.RotatePoint(AnnotationEditor.HandleCenters(bounds)[handle], center, rotation);
        return grabbed - press;
    }

    /// <summary>Ctrl+wheel opacity step: 5 % per notch, never fully invisible.</summary>
    public static double WheelOpacity(double opacity, int delta) =>
        Math.Clamp(Math.Round(opacity + Math.Sign(delta) * 0.05, 2), 0.05, 1);

    public static bool IsTooSmall(Annotation annotation) => annotation switch
    {
        RectAnnotation rect => rect.Rect.Width < 2 && rect.Rect.Height < 2,
        EllipseAnnotation ellipse => ellipse.Rect.Width < 2 && ellipse.Rect.Height < 2,
        MosaicAnnotation box => box.Rect.Width < 2 || box.Rect.Height < 2,
        ArrowAnnotation arrow => (arrow.End - arrow.Start).Length < 2,
        LineAnnotation line => (line.End - line.Start).Length < 2,
        PenAnnotation pen => pen.Points.Count < 1,
        MosaicBrushAnnotation brush => brush.Points.Count < 1,
        _ => false
    };

    /// <summary>
    /// Binds a finished eraser stroke to every element it touches (SketchPlayer
    /// notes §4.2). Returns false when it touched nothing, so no undo step is needed.
    /// </summary>
    public static bool BindEraser(IReadOnlyList<Annotation> items, IReadOnlyList<Point> points, double width, double strength = 1)
    {
        if (points.Count == 0)
        {
            return false;
        }

        var stroke = EraseStroke.Widen(points, width);
        var reach = stroke.Bounds;
        var touched = false;
        foreach (var item in items)
        {
            if (!item.Erasable)
            {
                continue;
            }

            var area = AnnotationEditor.RotatedBounds(item);
            area.Inflate(item.Thickness * 2 + 2, item.Thickness * 2 + 2);
            if (!area.IntersectsWith(reach))
            {
                continue;
            }

            item.AddEraseStroke(points, width, strength);
            touched = true;
        }

        return touched;
    }

    /// <summary>
    /// Topmost element under the point. Freehand strokes and the watermark are
    /// not selectable this way; erased parts of an element do not hit.
    /// </summary>
    public static Annotation? HitTest(IReadOnlyList<Annotation> items, Point point, double tolerance)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var annotation = items[i];
            if (annotation is PenAnnotation or MosaicBrushAnnotation or WatermarkAnnotation)
            {
                continue;
            }

            var testPoint = point;
            if (Math.Abs(annotation.Rotation) > 0.01)
            {
                var bounds = annotation.Bounds;
                var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                testPoint = AnnotationEditor.RotatePoint(point, center, -annotation.Rotation);
            }

            if (annotation.HitTest(testPoint, tolerance) && !annotation.IsErasedAt(point))
            {
                return annotation;
            }
        }

        return null;
    }

    public static WatermarkAnnotation? FindWatermark(IReadOnlyList<Annotation> items) =>
        items.OfType<WatermarkAnnotation>().FirstOrDefault();

    /// <summary>The canvas has one watermark at the very bottom; creates it on first use.</summary>
    public static WatermarkAnnotation EnsureWatermark(List<Annotation> items, Rect canvas, AnnotationToolbar toolbar, AnnotationHistory history)
    {
        if (FindWatermark(items) is { } existing)
        {
            existing.Rect = canvas;
            return existing;
        }

        history.Checkpoint(items);
        var watermark = new WatermarkAnnotation
        {
            Rect = canvas,
            Color = toolbar.Color,
            FontSize = Math.Max(10, toolbar.Thickness),
            Opacity = toolbar.WatermarkOpacity,
            Angle = toolbar.WatermarkAngle
        };
        items.Insert(0, watermark);
        return watermark;
    }

    /// <summary>Fits an element into <paramref name="target"/> (its unrotated box).</summary>
    public static void Resize(Annotation annotation, Rect target, int handle)
    {
        var source = annotation.Bounds;
        switch (annotation)
        {
            case RectAnnotation rect:
                rect.Rect = target;
                break;
            case EllipseAnnotation ellipse:
                ellipse.Rect = target;
                break;
            case MosaicAnnotation mosaic:
                mosaic.Rect = target; // the pattern is canvas-wide, nothing to rebuild
                break;
            case LineAnnotation line:
                line.Start = MapShapePoint(line.Start, source, target);
                line.End = MapShapePoint(line.End, source, target);
                break;
            case ArrowAnnotation arrow:
                arrow.Start = MapShapePoint(arrow.Start, source, target);
                arrow.End = MapShapePoint(arrow.End, source, target);
                break;
            case TextAnnotation text:
            {
                // Text scales uniformly: edges use their own axis, corners the smaller ratio.
                var sx = target.Width / Math.Max(1, source.Width);
                var sy = target.Height / Math.Max(1, source.Height);
                var factor = handle is 3 or 7 ? sx : handle is 1 or 5 ? sy : Math.Min(sx, sy);
                text.FontSize = Math.Clamp(text.FontSize * factor, 6, 400);
                var size = text.Bounds.Size;
                // Keep the side opposite the handle in place.
                var x = handle is 0 or 6 or 7 ? target.Right - size.Width : target.X;
                var y = handle is 0 or 1 or 2 ? target.Bottom - size.Height : target.Y;
                text.Position = new Point(x + text.Padding, y + text.Padding);
                break;
            }
            case NumberAnnotation number:
                // A number badge grows / shrinks around its own centre (it used to slide with the handle).
                var dw = Math.Abs(target.Width - source.Width);
                var dh = Math.Abs(target.Height - source.Height);
                number.Radius = Math.Clamp((dw >= dh ? target.Width : target.Height) / 2, 8, 80);
                break;
            case PenAnnotation pen:
                ScalePoints(pen.Points, source, target);
                break;
            case MosaicBrushAnnotation brush:
                ScalePoints(brush.Points, source, target);
                break;
        }
    }

    private static void ScalePoints(List<Point> points, Rect source, Rect target)
    {
        var sx = source.Width > 1 ? target.Width / source.Width : 1;
        var sy = source.Height > 1 ? target.Height / source.Height : 1;
        for (var i = 0; i < points.Count; i++)
        {
            points[i] = new Point(target.X + (points[i].X - source.X) * sx, target.Y + (points[i].Y - source.Y) * sy);
        }
    }

    private static Point MapShapePoint(Point point, Rect source, Rect target)
    {
        var sourceCenter = new Point(source.X + source.Width / 2, source.Y + source.Height / 2);
        var targetCenter = new Point(target.X + target.Width / 2, target.Y + target.Height / 2);
        var sx = source.Width > 1 ? target.Width / source.Width : 1;
        var sy = source.Height > 1 ? target.Height / source.Height : 1;
        return new Point(
            targetCenter.X + (point.X - sourceCenter.X) * sx,
            targetCenter.Y + (point.Y - sourceCenter.Y) * sy);
    }

    /// <summary>
    /// Resizes from a handle in the element's own (rotated) frame: the opposite
    /// side/corner stays fixed on screen even when the element is rotated.
    /// Shift on a corner keeps the aspect ratio.
    /// </summary>
    public static Rect ResizeFromHandle(Rect start, double rotation, int handle, Point pointer, bool keepAspect)
    {
        var c0 = new Point(start.X + start.Width / 2, start.Y + start.Height / 2);
        var local = AnnotationEditor.RotatePoint(pointer, c0, -rotation);

        double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        switch (handle)
        {
            case 0: left = local.X; top = local.Y; break;
            case 1: top = local.Y; break;
            case 2: right = local.X; top = local.Y; break;
            case 3: right = local.X; break;
            case 4: right = local.X; bottom = local.Y; break;
            case 5: bottom = local.Y; break;
            case 6: left = local.X; bottom = local.Y; break;
            case 7: left = local.X; break;
        }

        var width = Math.Max(4, Math.Abs(right - left));
        var height = Math.Max(4, Math.Abs(bottom - top));
        if (keepAspect && handle is 0 or 2 or 4 or 6 && start.Width > 0 && start.Height > 0)
        {
            var k = Math.Max(width / start.Width, height / start.Height);
            width = start.Width * k;
            height = start.Height * k;
            // Re-anchor at the fixed corner.
            if (handle is 0 or 6) left = start.Right - width; else right = start.Left + width;
            if (handle is 0 or 2) top = start.Bottom - height; else bottom = start.Top + height;
        }

        var localBox = new Rect(Math.Min(left, right), Math.Min(top, bottom), width, height);
        // The box centre in local space → screen space; the unrotated box sits around it.
        var localCenter = new Point(localBox.X + localBox.Width / 2, localBox.Y + localBox.Height / 2);
        var center = AnnotationEditor.RotatePoint(localCenter, c0, rotation);
        return new Rect(center.X - width / 2, center.Y - height / 2, width, height);
    }

    /// <summary>Keeps bound eraser holes in place when the pin image is flipped ('H', 'V') or turned ('R').</summary>
    public static void ReorientMasks(Annotation annotation, char op)
    {
        if (annotation.EraseMasks is not { Count: > 0 } masks || annotation is TextAnnotation)
        {
            return;
        }

        for (var i = 0; i < masks.Count; i++)
        {
            var m = masks[i];
            var w = m.RecordedSize.Width;
            var h = m.RecordedSize.Height;
            masks[i] = op switch
            {
                'H' => new EraseStroke(m.Points.Select(p => new Point(w - p.X, p.Y)).ToList(), m.Width, m.RecordedSize),
                'V' => new EraseStroke(m.Points.Select(p => new Point(p.X, h - p.Y)).ToList(), m.Width, m.RecordedSize),
                _ => new EraseStroke(m.Points.Select(p => new Point(p.Y, w - p.X)).ToList(), m.Width, new Size(h, w))
            };
        }

        annotation.MaskCache = null;
    }

    public static Rect ScaleAround(Rect rect, Point center, double factor)
    {
        var width = Math.Max(4, rect.Width * factor);
        var height = Math.Max(4, rect.Height * factor);
        return new Rect(center.X - width / 2, center.Y - height / 2, width, height);
    }

    /// <summary>
    /// Copies a capture's annotations into pin coordinates (origin at the
    /// selection's top-left) so they stay editable in the pin.
    /// </summary>
    public static List<Annotation> ToPinSpace(IEnumerable<Annotation> items, Point origin, BitmapSource pinBase)
    {
        var result = new List<Annotation>();
        foreach (var item in items)
        {
            var copy = item.Clone();
            copy.Translate(-origin.X, -origin.Y);
            switch (copy)
            {
                case WatermarkAnnotation watermark:
                    watermark.Rect = new Rect(0, 0, pinBase.PixelWidth, pinBase.PixelHeight);
                    break;
                case MosaicAnnotation or MosaicBrushAnnotation:
                    MosaicBuilder.Rebuild(copy, pinBase);
                    break;
            }

            result.Add(copy);
        }

        return result;
    }
}
