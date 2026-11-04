namespace Markshot.UI;

/// <summary>
/// 吸色 with the pen (capture, pin, image editor): while Alt is held with the pen / highlighter in hand a
/// cross follows the pointer with the colour under it; a click takes that colour for the pen. The
/// colour comes from what the window shows (picture and annotations), read by the owner.
/// </summary>
public sealed class PenEyedropper
{
    private readonly Annotations.AnnotationEditor _editor;
    private readonly Func<Point, Color?> _colorAt;

    /// <param name="editor">Draws the cross.</param>
    /// <param name="colorAt">The colour at a canvas point (null outside the picture).</param>
    internal PenEyedropper(Annotations.AnnotationEditor editor, Func<Point, Color?> colorAt)
    {
        _editor = editor;
        _colorAt = colorAt;
    }

    public bool Active => _editor.Eyedrop != null;

    public static bool IsPen(Annotations.AnnotationTool tool) =>
        tool is Annotations.AnnotationTool.Pen or Annotations.AnnotationTool.Highlighter;

    public static bool AltDown => (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

    /// <summary>The pointer moved (or Alt changed): shows / moves / hides the cross. True while it is on.</summary>
    public bool Update(Annotations.AnnotationTool tool, Point canvasPoint, bool drawing)
    {
        if (!IsPen(tool) || !AltDown || drawing)
        {
            Hide();
            return false;
        }

        _editor.Eyedrop = (canvasPoint, _colorAt(canvasPoint));
        _editor.InvalidateVisual();
        return true;
    }

    /// <summary>A click while the cross is on: the colour there (null = nothing to take).</summary>
    public Color? Pick(Point canvasPoint)
    {
        var color = _colorAt(canvasPoint);
        Hide();
        return color is { } c ? Color.FromRgb(c.R, c.G, c.B) : null;
    }

    public void Hide()
    {
        if (_editor.Eyedrop == null)
        {
            return;
        }

        _editor.Eyedrop = null;
        _editor.InvalidateVisual();
    }

    /// <summary>The colour of one pixel of a picture (null outside it or where it is see-through).</summary>
    public static Color? Sample(BitmapSource image, Point pixel)
    {
        var x = (int)Math.Floor(pixel.X);
        var y = (int)Math.Floor(pixel.Y);
        if (x < 0 || y < 0 || x >= image.PixelWidth || y >= image.PixelHeight)
        {
            return null;
        }

        BitmapSource bgra = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        if (px[3] == 0)
        {
            return null;
        }

        if (bgra.Format == PixelFormats.Pbgra32 && px[3] < 255)
        {
            // Premultiplied: back to the real colour.
            var a = px[3] / 255.0;
            return Color.FromRgb((byte)Math.Min(255, px[2] / a), (byte)Math.Min(255, px[1] / a), (byte)Math.Min(255, px[0] / a));
        }

        return Color.FromRgb(px[2], px[1], px[0]);
    }

    /// <summary>The tablet pen's pressure for this event (null with a mouse, or a pen that does not report it).</summary>
    public static float? Pressure(MouseEventArgs e, IInputElement relativeTo)
    {
        if (e.StylusDevice is not { } stylus)
        {
            return null;
        }

        var points = stylus.GetStylusPoints(relativeTo);
        if (points.Count == 0 || !points[^1].HasProperty(StylusPointProperties.NormalPressure))
        {
            return null;
        }

        return points[^1].PressureFactor;
    }
}
