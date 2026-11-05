namespace Markshot.Services;

/// <summary>Renders plain text into a card bitmap in the current theme's colours, used for text pins and translation pins.</summary>
public static class TextCardRenderer
{
    private static readonly Typeface BodyTypeface =
        new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Typeface TitleTypeface =
        new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static BitmapSource Render(string? title, string text, double width = 560)
    {
        width = Math.Clamp(width, 240, 1200);
        text ??= string.Empty;

        var light = Theme.IsLight;
        Brush TextBrush = new SolidColorBrush(light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF0, 0xF0, 0xF0));
        Brush AccentBrush = new SolidColorBrush(light ? Color.FromRgb(0x2F, 0x6F, 0xE0) : Color.FromRgb(0x6A, 0xA1, 0xFF));
        Brush CardBrush = new SolidColorBrush(light ? Colors.White : Color.FromRgb(0x1E, 0x1E, 0x1E));
        Brush BorderBrush = new SolidColorBrush(light ? Color.FromRgb(0xE0, 0xE0, 0xE0) : Color.FromRgb(0x3A, 0x3A, 0x3A));
        Brush DividerBrush = new SolidColorBrush(light ? Color.FromRgb(0xEE, 0xEE, 0xEE) : Color.FromRgb(0x33, 0x33, 0x33));

        var body = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            BodyTypeface,
            15,
            TextBrush,
            1.0)
        {
            MaxTextWidth = width - 36,
            MaxTextHeight = 6000,
            Trimming = TextTrimming.None
        };

        var hasTitle = !string.IsNullOrWhiteSpace(title);
        var titleHeight = hasTitle ? 34.0 : 0.0;
        var height = Math.Max(64, body.Height + 28 + titleHeight);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(
                CardBrush,
                new Pen(BorderBrush, 1),
                new Rect(0.5, 0.5, width - 1, height - 1),
                10, 10);

            var y = 16.0;
            if (hasTitle)
            {
                var titleText = new FormattedText(
                    title!,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    TitleTypeface,
                    12.5,
                    AccentBrush,
                    1.0)
                {
                    MaxTextWidth = width - 36,
                    MaxTextHeight = 40
                };

                dc.DrawText(titleText, new Point(18, y));
                y += 25;
                dc.DrawLine(new Pen(DividerBrush, 1), new Point(18, y), new Point(width - 18, y));
                y += 10;
            }

            dc.DrawText(body, new Point(18, y));
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
