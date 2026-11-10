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
    private static long _lastUse;
    private static Timer? _idle;

    /// <summary>
    /// The recognisers (a few hundred MB of model and working memory) are let go after a few minutes unused and made
    /// again on the next use (about a second): Markshot sits small in the background. Pins' text layers use them too.
    /// </summary>
    public static TimeSpan UnloadAfter { get; set; } = TimeSpan.FromMinutes(3);

    private static RapidOcr Engine(string languageTag)
    {
        _lastUse = Environment.TickCount64;
        _idle ??= new Timer(_ => UnloadIfIdle(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        return UseLatin(languageTag)
            ? _latinEngine ??= CreateEngine(latin: true)
            : _chineseEngine ??= CreateEngine(latin: false);
    }

    /// <summary>Tests / idle: the engines go (made again when needed).</summary>
    public static bool UnloadIfIdle(bool force = false)
    {
        if (!force && Environment.TickCount64 - _lastUse < UnloadAfter.TotalMilliseconds)
        {
            return false;
        }

        if (!Monitor.TryEnter(Gate))
        {
            return false; // recognising right now
        }

        try
        {
            if (_chineseEngine == null && _latinEngine == null)
            {
                return false;
            }

            _chineseEngine?.Dispose();
            _latinEngine?.Dispose();
            _chineseEngine = null;
            _latinEngine = null;
        }
        finally
        {
            Monitor.Exit(Gate);
        }

        Log.Info("文字识别模型闲置，已释放");
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
        return true;
    }

    public static bool Loaded => _chineseEngine != null || _latinEngine != null;

    public static Task<string> RecognizeAsync(BitmapSource image, string languageTag = "")
    {
        image = Shareable(image);
        return Task.Run(() => Recognize(image, languageTag));
    }

    /// <summary>A picture the worker thread may read: frozen here, on the thread that owns it (or a frozen copy).</summary>
    private static BitmapSource Shareable(BitmapSource image)
    {
        if (image.IsFrozen)
        {
            return image;
        }

        if (image.CanFreeze)
        {
            image.Freeze();
            return image;
        }

        var copy = new WriteableBitmap(image);
        copy.Freeze();
        return copy;
    }

    public static Task<IReadOnlyList<OcrTextBlock>> RecognizeBlocksAsync(
        BitmapSource image, string languageTag = "")
    {
        image = Shareable(image);
        return Task.Run(() => RecognizeBlocks(image, languageTag));
    }

    private static IReadOnlyList<OcrTextBlock> RecognizeBlocks(BitmapSource image, string languageTag)
    {
        if (image.CanFreeze && !image.IsFrozen)
        {
            image.Freeze();
        }

        // A long picture (a long screenshot) in pieces: the recogniser scales a picture down to about 1000 pixels to
        // find the text, which made the lines of a tall one too small to find. Pieces overlap; a line is kept by the
        // piece its middle is in.
        var horizontal = image.PixelWidth > image.PixelHeight;
        var length = horizontal ? image.PixelWidth : image.PixelHeight;
        var across = horizontal ? image.PixelHeight : image.PixelWidth;
        if (length > 2200 && length > across * 1.6)
        {
            const int Overlap = 160;
            var piece = Math.Max(1400, across);
            var found = new List<OcrTextBlock>();
            for (var start = 0; start < length; start += piece - Overlap)
            {
                var size = Math.Min(piece, length - start);
                var area = horizontal ? new System.Windows.Int32Rect(start, 0, size, across) : new System.Windows.Int32Rect(0, start, across, size);
                var part = new CroppedBitmap(image, area);
                part.Freeze();
                var keepFrom = start == 0 ? 0 : start + Overlap / 2;
                var keepTo = start + size >= length ? length : start + size - Overlap / 2;
                foreach (var block in RecognizePiece(part, languageTag))
                {
                    var box = horizontal
                        ? new System.Windows.Int32Rect(block.Box.X + start, block.Box.Y, block.Box.Width, block.Box.Height)
                        : new System.Windows.Int32Rect(block.Box.X, block.Box.Y + start, block.Box.Width, block.Box.Height);
                    var middle = horizontal ? box.X + box.Width / 2 : box.Y + box.Height / 2;
                    if (middle >= keepFrom && middle < keepTo)
                    {
                        found.Add(block with { Box = box });
                    }
                }

                if (start + size >= length)
                {
                    break;
                }
            }

            return found.OrderBy(block => block.Box.Y).ThenBy(block => block.Box.X).ToList();
        }

        return RecognizePiece(image, languageTag);
    }

    private static IReadOnlyList<OcrTextBlock> RecognizePiece(BitmapSource image, string languageTag)
    {
        lock (Gate)
        {
            var engine = Engine(languageTag);

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

    /// <summary>The text with its layout: lines as on the image, columns (tables) tab-separated, paragraphs apart.</summary>
    private static string Recognize(BitmapSource image, string languageTag)
    {
        var blocks = RecognizeBlocks(image, languageTag);
        return blocks.Count > 0 ? Layout(blocks) : RecognizeFlat(image, languageTag);
    }

    /// <summary>The text with its layout (rows, columns, tables, fractions): see <see cref="OcrLayout"/>.</summary>
    public static string Layout(IReadOnlyList<OcrTextBlock> blocks) => OcrLayout.Analyze(blocks).Text;

    /// <summary>The text with its layout and the tables found in it.</summary>
    public static Task<OcrDocument> RecognizeDocumentAsync(BitmapSource image, string languageTag = "")
    {
        image = Shareable(image);
        return Task.Run(() =>
        {
            var blocks = RecognizeBlocks(image, languageTag);
            return blocks.Count > 0 ? OcrLayout.Analyze(blocks) : new OcrDocument(RecognizeFlat(image, languageTag), Array.Empty<OcrTable>());
        });
    }

    private static bool IsCjk(char c) => c is >= '\u4e00' and <= '\u9fff' or >= '\u3000' and <= '\u303f' or >= '\uff00' and <= '\uffef';

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToList();
        return sorted.Count == 0 ? 16 : sorted[sorted.Count / 2];
    }

    private static string RecognizeFlat(BitmapSource image, string languageTag)
    {
        if (image.CanFreeze && !image.IsFrozen)
        {
            image.Freeze();
        }

        lock (Gate)
        {
            var engine = Engine(languageTag);

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
