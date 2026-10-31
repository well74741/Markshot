using System.Text.RegularExpressions;
using RapidOcrNet;
using SkiaSharp;

namespace Markshot.Services;

/// <summary>
/// Local OCR using RapidOcrNet (ONNX Runtime + PaddleOCR PP-OCRv5 models),
/// the same model family PixPin uses. Images never leave the machine.
/// </summary>
public sealed record OcrTextBlock(string Text, System.Windows.Int32Rect Box);

public static class OcrService
{
    private static readonly object Gate = new();
    private static RapidOcr? _chineseEngine;
    private static RapidOcr? _latinEngine;

    public static Task<string> RecognizeAsync(BitmapSource image, string languageTag = "")
    {
        return Task.Run(() => Recognize(image, languageTag));
    }

    public static Task<IReadOnlyList<OcrTextBlock>> RecognizeBlocksAsync(
        BitmapSource image, string languageTag = "")
    {
        return Task.Run(() => RecognizeBlocks(image, languageTag));
    }

    private static IReadOnlyList<OcrTextBlock> RecognizeBlocks(BitmapSource image, string languageTag)
    {
        if (image.CanFreeze && !image.IsFrozen)
        {
            image.Freeze();
        }

        lock (Gate)
        {
            var engine = UseLatin(languageTag)
                ? _latinEngine ??= CreateEngine(latin: true)
                : _chineseEngine ??= CreateEngine(latin: false);

            using var bitmap = ToSkBitmap(image);
            var result = engine.Detect(bitmap, RapidOcrOptions.Default with { DoAngle = false });
            var blocks = new List<OcrTextBlock>();
            foreach (var block in result.TextBlocks ?? Array.Empty<RapidOcrNet.TextBlock>())
            {
                if (string.IsNullOrWhiteSpace(block.Text) || block.BoxPoints is not { Length: > 0 } points)
                {
                    continue;
                }

                var minX = points.Min(point => point.X);
                var minY = points.Min(point => point.Y);
                var maxX = points.Max(point => point.X);
                var maxY = points.Max(point => point.Y);
                var rect = new System.Windows.Int32Rect(
                    Math.Clamp(minX, 0, image.PixelWidth - 1),
                    Math.Clamp(minY, 0, image.PixelHeight - 1),
                    Math.Clamp(maxX - minX, 1, image.PixelWidth - Math.Max(0, minX)),
                    Math.Clamp(maxY - minY, 1, image.PixelHeight - Math.Max(0, minY)));
                blocks.Add(new OcrTextBlock(Cleanup(block.Text), rect));
            }

            return blocks
                .OrderBy(block => block.Box.Y)
                .ThenBy(block => block.Box.X)
                .ToList();
        }
    }

    private static string Recognize(BitmapSource image, string languageTag)
    {
        if (image.CanFreeze && !image.IsFrozen)
        {
            image.Freeze();
        }

        lock (Gate)
        {
            var engine = UseLatin(languageTag)
                ? _latinEngine ??= CreateEngine(latin: true)
                : _chineseEngine ??= CreateEngine(latin: false);

            using var bitmap = ToSkBitmap(image);

            var result = engine.Detect(bitmap, RapidOcrOptions.Default with
            {
                // Screenshots are upright; skip the 180° classifier for speed.
                DoAngle = false
            });

            var text = result.StrRes;
            if (string.IsNullOrWhiteSpace(text))
            {
                text = string.Join(Environment.NewLine,
                    result.TextBlocks?.Select(block => block.Text) ?? Array.Empty<string>());
            }

            return Cleanup(text);
        }
    }

    private static bool UseLatin(string languageTag)
    {
        return !string.IsNullOrWhiteSpace(languageTag)
               && languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)
               && !languageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    private static RapidOcr CreateEngine(bool latin)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "models", "v5");

        var detPath = Path.Combine(directory, "ch_PP-OCRv5_mobile_det.onnx");
        var clsPath = Path.Combine(directory, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        var recPath = Path.Combine(
            directory,
            latin ? "latin_PP-OCRv5_rec_mobile_infer.onnx" : "ch_PP-OCRv5_rec_mobile.onnx");
        var keysPath = Path.Combine(
            directory,
            latin ? "ppocrv5_latin_dict.txt" : "ppocrv5_dict.txt");

        foreach (var path in new[] { detPath, clsPath, recPath, keysPath })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "OCR 模型缺失：" + path + "。请确认 models/v5 已随程序一起发布。", path);
            }
        }

        var engine = new RapidOcr();
        engine.InitModels(detPath, clsPath, recPath, keysPath);
        return engine;
    }

    private static SKBitmap ToSkBitmap(BitmapSource image)
    {
        using var memory = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(memory);
        memory.Position = 0;

        return SKBitmap.Decode(memory)
               ?? throw new InvalidOperationException("无法把截图解码为 OCR 输入图像。");
    }

    /// <summary>
    /// PP-OCRv5 is usually already space-free for CJK, but older models insert
    /// spaces between characters; remove only CJK-to-CJK spaces, keeping line breaks.
    /// </summary>
    private static string Cleanup(string text)
    {
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])[ \t\u3000]+(?=[\u4e00-\u9fff])", string.Empty);
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])[ \t\u3000]+(?=[，。；：！？、）】》”’])", string.Empty);
        text = Regex.Replace(text, @"(?<=[（【《“‘])[ \t\u3000]+(?=[\u4e00-\u9fff])", string.Empty);
        return text.Trim();
    }
}
