using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markshot.Core;

namespace Markshot.Services;

/// <summary>
/// OCRs the screenshot, translates each text line and paints the translated
/// line back into the same box. The background is sampled from the pixels
/// around the original text so the replacement does not leave a flat block.
/// </summary>
public static class AutoTranslateService
{
    public static async Task<BitmapSource> TranslateImageAsync(
        BitmapSource image,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        progress?.Report("正在识别文字…");
        var blocks = await OcrService.RecognizeBlocksAsync(image, App.Config.OcrLanguage)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (blocks.Count == 0)
        {
            progress?.Report("未识别到文字");
            return image;
        }

        var target = string.IsNullOrWhiteSpace(App.Config.Translation.TargetLanguage)
            ? "简体中文"
            : App.Config.Translation.TargetLanguage;
        var provider = TranslationService.Create(App.Config.Translation);
        var translated = new string[blocks.Count];
        var unique = blocks.Select(block => block.Text).Distinct(StringComparer.Ordinal).ToList();
        var chunks = unique.Chunk(30).ToList();
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        var done = 0;
        progress?.Report($"正在翻译 0/{chunks.Count} 批…");

        for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[chunkIndex];
            const string separator = "\n[[MarkshotSeg]]\n";
            try
            {
                var joined = string.Join(separator, chunk);
                var result = await provider.TranslateAsync(joined, target, cancellationToken)
                    .ConfigureAwait(false);
                var parts = result.Split("[[MarkshotSeg]]", StringSplitOptions.None);
                if (parts.Length == chunk.Length)
                {
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        lookup[chunk[i]] = parts[i].Trim();
                    }
                }
                else
                {
                    // Some models collapse delimiters. Translate this small
                    // chunk line by line rather than silently keeping originals.
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        lookup[chunk[i]] = await provider
                            .TranslateAsync(chunk[i], target, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("自动翻译批次失败", ex);
                if (ex is InvalidOperationException)
                {
                    throw;
                }

                foreach (var text in chunk)
                {
                    lookup.TryAdd(text, text);
                }
            }

            done++;
            progress?.Report($"正在翻译 {done}/{chunks.Count} 批…");
        }

        cancellationToken.ThrowIfCancellationRequested();
        for (var i = 0; i < blocks.Count; i++)
        {
            translated[i] = lookup.TryGetValue(blocks[i].Text, out var value)
                ? value
                : blocks[i].Text;
        }

        return Render(image, blocks, translated);
    }

    private static BitmapSource Render(
        BitmapSource image,
        IReadOnlyList<OcrTextBlock> blocks,
        IReadOnlyList<string> translated)
    {
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var stride = width * 4;
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        source.Freeze();
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, width, height));
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                var text = translated[i];
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var box = Expand(block.Box, 3, width, height);
                DrawSampledBackground(dc, pixels, stride, width, height, box);
                DrawTranslatedText(dc, pixels, stride, width, height, box, text);
            }
        }

        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }

    private static void DrawSampledBackground(
        DrawingContext dc,
        byte[] pixels,
        int stride,
        int width,
        int height,
        Int32Rect box)
    {
        // Interpolate the pixels immediately outside the text box, row by row.
        // This works for flat colours and simple gradients while covering the
        // original anti-aliased glyphs.
        for (var y = box.Y; y < box.Y + box.Height; y++)
        {
            var left = AverageRow(pixels, stride, width, height, Math.Max(0, box.X - 4), y, 4);
            var right = AverageRow(pixels, stride, width, height,
                Math.Min(width - 1, box.X + box.Width + 1), y, 4);
            var brush = new LinearGradientBrush(left, right, 0);
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(box.X, y, box.Width, 1));
        }
    }

    private static Color AverageRow(
        byte[] pixels,
        int stride,
        int width,
        int height,
        int x,
        int y,
        int samples)
    {
        if (y < 0 || y >= height)
        {
            return Colors.Transparent;
        }

        long b = 0, g = 0, r = 0, a = 0, count = 0;
        for (var i = 0; i < samples; i++)
        {
            var sx = Math.Clamp(x + i, 0, width - 1);
            var offset = y * stride + sx * 4;
            b += pixels[offset];
            g += pixels[offset + 1];
            r += pixels[offset + 2];
            a += pixels[offset + 3];
            count++;
        }

        return Color.FromArgb(
            (byte)(a / Math.Max(1, count)),
            (byte)(r / Math.Max(1, count)),
            (byte)(g / Math.Max(1, count)),
            (byte)(b / Math.Max(1, count)));
    }

    private static void DrawTranslatedText(
        DrawingContext dc,
        byte[] pixels,
        int stride,
        int width,
        int height,
        Int32Rect box,
        string text)
    {
        var foreground = FindForeground(pixels, stride, width, height, box);
        var brush = new SolidColorBrush(foreground);
        brush.Freeze();

        var availableWidth = Math.Max(8, box.Width);
        var availableHeight = Math.Max(8, box.Height);
        var fontSize = Math.Clamp(availableHeight * 0.72, 8, 72);
        FormattedText formatted;
        do
        {
            formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
                    FontWeights.Normal, FontStretches.Normal),
                fontSize,
                brush,
                1.0)
            {
                MaxTextWidth = availableWidth,
                Trimming = TextTrimming.CharacterEllipsis
            };

            if (formatted.Width <= availableWidth && formatted.Height <= availableHeight)
            {
                break;
            }

            fontSize -= 1;
        }
        while (fontSize >= 7);

        var origin = new Point(
            box.X,
            box.Y + Math.Max(0, (availableHeight - formatted.Height) / 2));
        dc.DrawText(formatted, origin);
    }

    private static Color FindForeground(
        byte[] pixels,
        int stride,
        int width,
        int height,
        Int32Rect box)
    {
        var best = Color.FromRgb(0, 0, 0);
        var bestDistance = -1;
        for (var y = box.Y; y < box.Y + box.Height; y += 2)
        {
            for (var x = box.X; x < box.X + box.Width; x += 2)
            {
                if (x < 0 || x >= width || y < 0 || y >= height)
                {
                    continue;
                }

                var offset = y * stride + x * 4;
                var color = Color.FromRgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                var distance = Math.Abs(color.R - 128) + Math.Abs(color.G - 128) + Math.Abs(color.B - 128);
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    best = color;
                }
            }
        }

        // A screenshot commonly has white/black text on a mid-tone logo. Pick
        // the nearest high-contrast monochrome when the sampled colour is weak.
        if (bestDistance < 110)
        {
            return Colors.White;
        }

        return best;
    }

    private static Int32Rect Expand(Int32Rect box, int padding, int width, int height)
    {
        var x = Math.Clamp(box.X - padding, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(box.Y - padding, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(box.X + box.Width + padding, x + 1, width);
        var bottom = Math.Clamp(box.Y + box.Height + padding, y + 1, height);
        return new Int32Rect(x, y, right - x, bottom - y);
    }
}
