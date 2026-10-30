using System.Text.RegularExpressions;
using Windows.Media.Ocr;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace Markshot.Services;

/// <summary>Uses the OCR engine built into Windows (Windows.Media.Ocr). Nothing leaves the machine.</summary>
public static class OcrService
{
    public static async Task<string> RecognizeAsync(BitmapSource image, string languageTag = "")
    {
        var engine = CreateEngine(languageTag)
                     ?? throw new InvalidOperationException(
                         "系统没有可用的 OCR 语言包。请在 Windows 设置 → 时间和语言 → 语言中安装“光学字符识别”组件。");

        var input = Downscale(image);

        using var memory = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(WpfBitmapFrame.Create(input));
        encoder.Save(memory);
        memory.Position = 0;

        using var randomAccess = memory.AsRandomAccessStream();
        var decoder = await WinBitmapDecoder.CreateAsync(randomAccess);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        var result = await engine.RecognizeAsync(softwareBitmap);

        var builder = new StringBuilder();
        foreach (var line in result.Lines)
        {
            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                builder.AppendLine(line.Text.Trim());
            }
        }

        return Cleanup(builder.ToString());
    }

    /// <summary>
    /// Windows OCR puts spaces between Chinese characters. Remove spaces between CJK
    /// characters only, keeping spaces around Latin words and across line breaks.
    /// </summary>
    private static string Cleanup(string text)
    {
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])[ \t\u3000]+(?=[\u4e00-\u9fff])", string.Empty);
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])[ \t\u3000]+(?=[，。；：！？、）】》”’])", string.Empty);
        text = Regex.Replace(text, @"(?<=[（【《“‘])[ \t\u3000]+(?=[\u4e00-\u9fff])", string.Empty);
        return text.Trim();
    }

    public static IReadOnlyList<string> AvailableLanguages =>
        OcrEngine.AvailableRecognizerLanguages
            .Select(language => $"{language.DisplayName} ({language.LanguageTag})")
            .ToList();

    private static BitmapSource Downscale(BitmapSource image)
    {
        var max = OcrEngine.MaxImageDimension;
        if (image.PixelWidth <= max && image.PixelHeight <= max)
        {
            return image;
        }

        var scale = Math.Min((double)max / image.PixelWidth, (double)max / image.PixelHeight);
        var transformed = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private static OcrEngine? CreateEngine(string languageTag)
    {
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            try
            {
                var language = new Windows.Globalization.Language(languageTag);
                if (OcrEngine.IsLanguageSupported(language))
                {
                    var engine = OcrEngine.TryCreateFromLanguage(language);
                    if (engine != null)
                    {
                        return engine;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"创建 OCR 引擎失败：{languageTag}", ex);
            }
        }

        var fallback = OcrEngine.TryCreateFromUserProfileLanguages();
        if (fallback != null)
        {
            return fallback;
        }

        foreach (var language in OcrEngine.AvailableRecognizerLanguages)
        {
            var engine = OcrEngine.TryCreateFromLanguage(language);
            if (engine != null)
            {
                return engine;
            }
        }

        return null;
    }
}
