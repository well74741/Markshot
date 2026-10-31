namespace Markshot.UI;

/// <summary>Magnifier card shown next to the cursor during capture.</summary>
public sealed class MagnifierLayer : FrameworkElement
{
    public const double CardWidth = 200;
    public const double CardHeight = 176;

    public BitmapSource? Source { get; set; }

    public Point Pixel { get; set; }

    public Color PixelColor { get; set; }

    public bool HasPixel { get; set; }

    public bool HexFormat { get; set; } = true;

    public string? Notice { get; set; }

    public MagnifierLayer()
    {
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(238, 20, 20, 20)),
            new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)), 1),
            new Rect(0.5, 0.5, CardWidth - 1, CardHeight - 1),
            8, 8);

        if (Source == null || !HasPixel)
        {
            return;
        }

        const double imageLeft = 14;
        const double imageTop = 12;
        const double imageWidth = 172;
        const double imageHeight = 112;

        var sampleWidth = Math.Min(24, Source.PixelWidth);
        var sampleHeight = Math.Min(16, Source.PixelHeight);
        var sourceX = Math.Clamp((int)Pixel.X - sampleWidth / 2, 0, Math.Max(0, Source.PixelWidth - sampleWidth));
        var sourceY = Math.Clamp((int)Pixel.Y - sampleHeight / 2, 0, Math.Max(0, Source.PixelHeight - sampleHeight));

        var crop = new CroppedBitmap(
            Source,
            new Int32Rect(sourceX, sourceY, sampleWidth, sampleHeight));
        crop.Freeze();

        dc.DrawImage(crop, new Rect(imageLeft, imageTop, imageWidth, imageHeight));

        // Crosshair at the center pixel.
        var scaleX = imageWidth / sampleWidth;
        var scaleY = imageHeight / sampleHeight;
        var centerX = imageLeft + ((Pixel.X - sourceX) + 0.5) * scaleX;
        var centerY = imageTop + ((Pixel.Y - sourceY) + 0.5) * scaleY;
        var crosshair = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1);

        dc.DrawLine(crosshair, new Point(centerX - 10, centerY), new Point(centerX + 10, centerY));
        dc.DrawLine(crosshair, new Point(centerX, centerY - 10), new Point(centerX, centerY + 10));
        dc.DrawRectangle(null,
            new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1),
            new Rect(centerX - scaleX / 2, centerY - scaleY / 2, scaleX, scaleY));

        // Color chip and values.
        var swatchRect = new Rect(14, 134, 18, 18);
        dc.DrawRectangle(new SolidColorBrush(PixelColor), new Pen(Brushes.White, 1), swatchRect);

        var valueText = HexFormat
            ? $"#{PixelColor.R:X2}{PixelColor.G:X2}{PixelColor.B:X2}"
            : $"RGB({PixelColor.R}, {PixelColor.G}, {PixelColor.B})";

        var value = new FormattedText(
            valueText,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            12.5,
            Brushes.White,
            1.0);

        dc.DrawText(value, new Point(40, 133));

        var position = new FormattedText(
            $"{(int)Pixel.X}, {(int)Pixel.Y}   ·   Shift 切换格式   C 复制",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            10.5,
            new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            1.0);

        dc.DrawText(position, new Point(14, 156));

        if (!string.IsNullOrWhiteSpace(Notice))
        {
            var notice = new FormattedText(
                Notice,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                11,
                new SolidColorBrush(Color.FromRgb(0x7C, 0xE0, 0x9A)),
                1.0);

            dc.DrawText(notice, new Point(14, 116));
        }
    }
}
