using Markshot.Annotations;

namespace Markshot.UI;

/// <summary>Toolbar ↔ annotation style mapping shared by the capture overlay and pin windows.</summary>
internal static class AnnotationStyler
{
    /// <summary>Applies the toolbar's current style to an existing annotation.</summary>
    public static void Apply(Annotation annotation, AnnotationToolbar toolbar, BitmapSource? mosaicSource)
    {
        var stroke = toolbar.StrokeEnabled ? toolbar.StrokeWidth : 0;
        if (annotation is not WatermarkAnnotation)
        {
            annotation.Opacity = toolbar.ElementOpacity;
        }

        switch (annotation)
        {
            case TextAnnotation text:
                text.Color = toolbar.Color;
                text.FontSize = Math.Clamp(toolbar.Thickness, 6, 400);
                text.Bold = toolbar.TextBold;
                text.Italic = toolbar.TextItalic;
                text.Fill = toolbar.TextBackground;
                text.StrokeColor = toolbar.StrokeColor;
                text.StrokeThickness = stroke;
                break;

            case WatermarkAnnotation watermark:
                watermark.Color = toolbar.Color;
                watermark.FontSize = Math.Max(10, toolbar.Thickness);
                watermark.Opacity = toolbar.WatermarkOpacity;
                watermark.Angle = toolbar.WatermarkAngle;
                break;

            case NumberAnnotation number:
                number.Color = toolbar.Color;
                number.Radius = Math.Max(8, toolbar.Thickness);
                number.Thickness = 1.5;
                break;

            case MosaicAnnotation box:
                SetLevel(box, toolbar.MosaicLevel, mosaicSource);
                break;

            case MosaicBrushAnnotation brush:
                brush.Thickness = Math.Max(2, toolbar.Thickness);
                SetLevel(brush, toolbar.MosaicLevel, mosaicSource);
                break;

            case RectAnnotation:
                annotation.Color = toolbar.Color;
                annotation.Thickness = toolbar.Thickness;
                annotation.Fill = toolbar.FillEnabled;
                annotation.CornerRadius = toolbar.CornerRadius;
                break;

            case EllipseAnnotation:
                annotation.Color = toolbar.Color;
                annotation.Thickness = toolbar.Thickness;
                annotation.Fill = toolbar.FillEnabled;
                break;

            case PenAnnotation pen when AnnotationOps.HasSoftEdge(pen):
                pen.Color = toolbar.Color;
                pen.Thickness = toolbar.Thickness;
                pen.Softness = toolbar.Softness;
                break;

            case ArrowAnnotation arrow:
                arrow.Color = toolbar.Color;
                arrow.Thickness = toolbar.Thickness;
                arrow.Style = toolbar.ArrowStyle;
                break;

            default:
                annotation.Color = toolbar.Color;
                annotation.Thickness = toolbar.Thickness;
                break;
        }
    }

    private static void SetLevel(Annotation mosaic, double level, BitmapSource? source)
    {
        var block = Math.Clamp(Math.Round(level), 3, 64);
        var current = mosaic switch
        {
            MosaicAnnotation box => box.BlockSize,
            MosaicBrushAnnotation brush => brush.BlockSize,
            _ => block
        };

        if (Math.Abs(current - block) < 0.01 && HasPattern(mosaic))
        {
            return;
        }

        switch (mosaic)
        {
            case MosaicAnnotation box:
                box.BlockSize = block;
                break;
            case MosaicBrushAnnotation brush:
                brush.BlockSize = block;
                break;
        }

        if (source != null)
        {
            MosaicBuilder.Rebuild(mosaic, source);
        }
    }

    private static bool HasPattern(Annotation mosaic) => mosaic switch
    {
        MosaicAnnotation box => box.Pattern != null,
        MosaicBrushAnnotation brush => brush.Pattern != null,
        _ => true
    };

    /// <summary>
    /// A new, empty text whose first line is vertically centred on the click,
    /// so the caret appears right under the pointer.
    /// </summary>
    public static TextAnnotation NewText(Point click, AnnotationToolbar toolbar)
    {
        var text = new TextAnnotation
        {
            Color = toolbar.Color,
            FontSize = Math.Clamp(toolbar.Thickness, 6, 400),
            Bold = toolbar.TextBold,
            Italic = toolbar.TextItalic,
            Fill = toolbar.TextBackground,
            StrokeColor = toolbar.StrokeColor,
            StrokeThickness = toolbar.StrokeEnabled ? toolbar.StrokeWidth : 0,
            Opacity = toolbar.ElementOpacity
        };
        text.Position = new Point(click.X, click.Y - text.LineHeight / 2);
        return text;
    }

    /// <summary>Size value the toolbar slider shows for an annotation.</summary>
    public static double SizeOf(Annotation annotation) => annotation switch
    {
        TextAnnotation text => text.FontSize,
        WatermarkAnnotation watermark => watermark.FontSize,
        NumberAnnotation number => number.Radius,
        _ => annotation.Thickness
    };
}
