using Markshot.Annotations;

namespace Markshot.UI;

/// <summary>Toolbar ↔ annotation style mapping shared by the capture overlay and pin windows.</summary>
internal static class AnnotationStyler
{
    /// <summary>Applies the toolbar's current style to an existing annotation.</summary>
    public static void Apply(Annotation annotation, AnnotationToolbar toolbar, BitmapSource? mosaicSource)
    {
        annotation.Color = toolbar.Color;
        var stroke = toolbar.StrokeEnabled ? toolbar.StrokeWidth : 0;
        switch (annotation)
        {
            case TextAnnotation text:
                text.FontSize = Math.Clamp(toolbar.Thickness, 6, 400);
                text.Bold = toolbar.TextBold;
                text.Fill = toolbar.TextBackground;
                text.StrokeColor = toolbar.StrokeColor;
                text.StrokeThickness = stroke;
                break;

            case WatermarkAnnotation watermark:
                watermark.FontSize = Math.Max(10, toolbar.Thickness);
                watermark.StrokeColor = toolbar.StrokeColor;
                watermark.StrokeThickness = stroke;
                break;

            case NumberAnnotation number:
                number.Radius = Math.Max(8, toolbar.Thickness);
                number.Thickness = 1.5;
                break;

            case MosaicAnnotation mosaic:
                var block = Math.Clamp(toolbar.Thickness, 4, 40);
                if (Math.Abs(block - mosaic.Thickness) > 0.01 && mosaicSource != null)
                {
                    mosaic.Thickness = block;
                    mosaic.Pattern = MosaicBuilder.Build(mosaicSource, mosaic.Rect, (int)Math.Round(block));
                }

                break;

            case RectAnnotation:
                annotation.Thickness = toolbar.Thickness;
                annotation.Fill = toolbar.FillEnabled;
                annotation.CornerRadius = toolbar.CornerRadius;
                break;

            case EllipseAnnotation:
                annotation.Thickness = toolbar.Thickness;
                annotation.Fill = toolbar.FillEnabled;
                break;

            default:
                annotation.Thickness = toolbar.Thickness;
                break;
        }
    }

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
            Fill = toolbar.TextBackground,
            StrokeColor = toolbar.StrokeColor,
            StrokeThickness = toolbar.StrokeEnabled ? toolbar.StrokeWidth : 0
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
