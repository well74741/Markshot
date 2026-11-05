using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Markshot.Services;

/// <summary>
/// Chrome-style in-place translation of a screenshot: the text inside the
/// image is replaced by its translation, in the same place, on the same
/// background, in a matching colour.
///
/// Pipeline: OCR lines → paragraphs (wrapped lines are merged so the model sees
/// whole sentences) → skip text already in the target language → translate in
/// parallel batches (results cached) → repaint after every batch so the image
/// fills in progressively → each paragraph is reflowed into its own area.
/// </summary>
public static class ImmersiveTranslator
{
    private const int BatchItems = 24;
    private const int BatchChars = 2400;
    private const int Parallel = 3;

    private static readonly Dictionary<(string Target, string Text), string> Cache = new();
    private static readonly object CacheGate = new();

    /// <summary>
    /// One OCR line plus what was measured from its pixels: the background
    /// around it, the ink colour, the vertical ink extent and the em size the
    /// glyphs imply.
    /// </summary>
    public sealed record Line(string Text, Int32Rect Box, Color Background, bool FlatBackground,
        Color Ink, double InkTop, double InkBottom, double EmSize, bool Cjk);

    public sealed record Paragraph(string Text, IReadOnlyList<Line> Lines, Int32Rect Box)
    {
        public double EmSize => Lines.Select(l => l.EmSize).OrderBy(v => v).ElementAt(Lines.Count / 2);

        /// <summary>Distance between consecutive baselines; 0 for a single line.</summary>
        public double Pitch => Lines.Count < 2
            ? 0
            : (Lines[^1].InkBottom - Lines[0].InkBottom) / (Lines.Count - 1);
    }

    /// <param name="Source">Detected language of the screenshot text ("中文", "English", …), null before OCR.</param>
    /// <param name="Target">Language actually translated into.</param>
    public sealed record Update(BitmapSource Image, string Status, bool Done, int Translated, int Total,
        string? Source = null, string? Target = null);

    /// <summary>Short display name of a language ("中文", "English", "日本語", "한국어").</summary>
    public static string LanguageLabel(string language)
    {
        var t = language.ToLowerInvariant();
        if (t.Contains("中") || t.Contains("汉") || t.Contains("chinese") || t.StartsWith("zh")) return "中文";
        if (t.Contains("英") || t.Contains("english") || t.StartsWith("en")) return "English";
        if (t.Contains("日") || t.Contains("japan") || t.StartsWith("ja")) return "日本語";
        if (t.Contains("韩") || t.Contains("한") || t.Contains("korean") || t.StartsWith("ko")) return "한국어";
        return language;
    }

    /// <summary>Majority script of the recognised text.</summary>
    public static string DetectLanguage(string text)
    {
        var kana = text.Count(c => c is >= '぀' and <= 'ヿ');
        var hangul = text.Count(c => c is >= '가' and <= '힯');
        var han = text.Count(c => c is >= '一' and <= '鿿');
        var latin = text.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (kana > 0 && kana * 4 >= han) return "日本語";
        if (hangul > Math.Max(han, latin / 2)) return "한국어";
        // One Chinese character carries about as much as a word of 4–5 Latin letters.
        return han * 3 >= latin ? "中文" : "English";
    }

    /// <summary>
    /// Translates <paramref name="image"/> in place. Every intermediate image is
    /// reported through <paramref name="progress"/>; the final one is returned.
    /// Call from the UI thread: OCR and HTTP run in the background, but the
    /// repaint uses WPF visuals and therefore resumes on the caller's thread.
    /// </summary>
    /// <param name="target">Language to translate into; null = the configured target.</param>
    /// <param name="switchWhenSame">
    /// When the text already is in the target language, translate it into the
    /// other common language instead (中文 ↔ English), so the toggle always does something.
    /// </param>
    public static async Task<BitmapSource> TranslateAsync(
        BitmapSource image,
        IProgress<Update>? progress,
        CancellationToken cancellationToken,
        string? target = null,
        bool switchWhenSame = true)
    {
        if (image.CanFreeze && !image.IsFrozen)
        {
            image.Freeze();
        }

        progress?.Report(new Update(image, "正在识别文字…", false, 0, 0));
        var blocks = await OcrService.RecognizeBlocksAsync(image, App.Config.OcrLanguage);
        cancellationToken.ThrowIfCancellationRequested();

        target ??= string.IsNullOrWhiteSpace(App.Config.Translation.TargetLanguage)
            ? "简体中文"
            : App.Config.Translation.TargetLanguage.Trim();
        var source = DetectLanguage(string.Concat(blocks.Select(b => b.Text)));
        if (switchWhenSame && LanguageLabel(target) == source)
        {
            target = source == "中文" ? "English" : "简体中文";
        }

        var targetLabel = LanguageLabel(target);

        var pixels = ReadPixels(image);
        var paragraphs = GroupParagraphs(blocks.Select(b => Measure(pixels, b)).ToList())
            .Where(p => NeedsTranslation(p.Text, target))
            .ToList();
        if (paragraphs.Count == 0)
        {
            var status = blocks.Count == 0 ? "未识别到文字" : $"内容已是{targetLabel}";
            progress?.Report(new Update(image, status, true, 0, 0, blocks.Count == 0 ? null : source, targetLabel));
            return image;
        }

        var translations = new string?[paragraphs.Count];
        var pending = new List<int>();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            lock (CacheGate)
            {
                if (Cache.TryGetValue((target, paragraphs[i].Text), out var cached))
                {
                    translations[i] = cached;
                    continue;
                }
            }

            pending.Add(i);
        }

        var total = paragraphs.Count;
        var done = total - pending.Count;
        var current = done > 0 ? Render(image, pixels, paragraphs, translations) : image;
        progress?.Report(new Update(current, $"正在翻译 {done}/{total}…", false, done, total, source, targetLabel));

        var provider = TranslationService.Create(App.Config.Translation);
        var gate = new SemaphoreSlim(Parallel);
        var renderGate = new object();
        Exception? firstError = null;

        var tasks = Batches(pending, paragraphs).Select(async batch =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var results = await provider
                    .TranslateBatchAsync(batch.Select(i => paragraphs[i].Text).ToList(), target, cancellationToken)
                    ;
                lock (CacheGate)
                {
                    for (var k = 0; k < batch.Count; k++)
                    {
                        var text = results[k].Trim();
                        translations[batch[k]] = text;
                        Cache[(target, paragraphs[batch[k]].Text)] = text;
                    }
                }

                BitmapSource snapshot;
                int count;
                lock (renderGate)
                {
                    done += batch.Count;
                    count = done;
                    snapshot = Render(image, pixels, paragraphs, translations);
                }

                progress?.Report(new Update(snapshot, $"正在翻译 {count}/{total}…", false, count, total, source, targetLabel));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("原位翻译批次失败", ex);
                Interlocked.CompareExchange(ref firstError, ex, null);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();

        var translatedCount = translations.Count(t => t != null);
        if (translatedCount == 0 && firstError != null)
        {
            throw firstError;
        }

        var final = Render(image, pixels, paragraphs, translations);
        var finalStatus = firstError == null
            ? $"已翻译为{targetLabel}"
            : $"部分翻译失败（{translatedCount}/{total}）：{firstError.Message}";
        progress?.Report(new Update(final, finalStatus, true, translatedCount, total, source, targetLabel));
        return final;
    }

    // ------------------------------------------------------------------ layout

    /// <summary>
    /// Merges OCR lines that are wrapped parts of one paragraph: stacked
    /// closely, same left edge, same size, same ink colour, same script, and the
    /// line above is long (it wrapped). Short stacked lines — menus, lists,
    /// table cells — stay separate so each is translated on its own.
    /// </summary>
    public static List<Paragraph> GroupParagraphs(IReadOnlyList<Line> lines)
    {
        var open = new List<(List<Line> Lines, double MaxWidth)>();
        foreach (var line in lines.OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X))
        {
            var merged = false;
            for (var i = 0; i < open.Count && !merged; i++)
            {
                var (group, maxWidth) = open[i];
                var last = group[^1];
                var em = Math.Max(last.EmSize, line.EmSize);
                var gap = line.InkTop - last.InkBottom;
                var sameSize = Math.Min(last.EmSize, line.EmSize) / Math.Max(1, em) >= 0.8;
                var sameLeft = Math.Abs(line.Box.X - last.Box.X) <= em * 1.2;
                var sameInk = Distance(last.Ink, line.Ink) < 70;
                var lastWrapped = last.Box.Width >= maxWidth * 0.72 && maxWidth >= em * 10;
                if (gap >= -em * 0.2 && gap <= em * 1.1 && sameSize && sameLeft && sameInk &&
                    last.Cjk == line.Cjk && lastWrapped)
                {
                    group.Add(line);
                    open[i] = (group, Math.Max(maxWidth, line.Box.Width));
                    merged = true;
                }
            }

            if (!merged)
            {
                open.Add((new List<Line> { line }, line.Box.Width));
            }
        }

        return open.Select(p =>
        {
            var text = new StringBuilder();
            foreach (var line in p.Lines)
            {
                var part = line.Text.Trim();
                if (text.Length > 0)
                {
                    var prev = text[^1];
                    if (prev == '-' && char.IsLetter(part.FirstOrDefault()))
                    {
                        text.Length--; // re-join a hyphenated word
                    }
                    else if (!IsCjk(prev) && !IsCjk(part.FirstOrDefault()))
                    {
                        text.Append(' ');
                    }
                }

                text.Append(part);
            }

            var left = p.Lines.Min(l => l.Box.X);
            var top = p.Lines.Min(l => l.Box.Y);
            var right = p.Lines.Max(l => l.Box.X + l.Box.Width);
            var bottom = p.Lines.Max(l => l.Box.Y + l.Box.Height);
            return new Paragraph(text.ToString(), p.Lines, new Int32Rect(left, top, right - left, bottom - top));
        }).ToList();
    }

    /// <summary>Paragraphs as the translator sees them (for diagnostics and tests).</summary>
    public static IReadOnlyList<Paragraph> Analyze(BitmapSource image, IReadOnlyList<OcrTextBlock> blocks)
    {
        var pixels = ReadPixels(image);
        return GroupParagraphs(blocks.Select(b => Measure(pixels, b)).ToList());
    }

    /// <summary>Measures background, ink colour, ink extent and implied font size of one OCR line.</summary>
    private static Line Measure(Pixels px, OcrTextBlock block)
    {
        var box = block.Box;
        var background = SampleBackground(px, box, out var flat);

        // Rows that contain glyph pixels give the true vertical ink extent; OCR
        // boxes are padded to roughly twice the glyph height and often overlap
        // the descenders/ascenders of neighbouring lines, so only the band of
        // ink rows connected to the box centre counts.
        var rowHasInk = new bool[box.Height];
        var inks = new List<(Color Color, double Distance)>();
        var step = Math.Max(1, box.Height / 40);
        for (var y = box.Y; y < box.Y + box.Height; y++)
        {
            var hits = 0;
            for (var x = box.X; x < box.X + box.Width; x += step)
            {
                var c = At(px, x, y);
                var d = Distance(c, background);
                if (d > 70)
                {
                    hits++;
                    inks.Add((c, d));
                }
            }

            rowHasInk[y - box.Y] = hits >= Math.Max(1, box.Width / step / 200);
        }

        var (bandTop, bandBottom) = InkBand(rowHasInk);

        var cjk = block.Text.Count(IsCjk) >= Math.Max(1, block.Text.Count(char.IsLetter)) * 0.5;
        double inkTop, inkBottom;
        if (bandBottom - bandTop >= 3)
        {
            inkTop = box.Y + bandTop;
            inkBottom = box.Y + bandBottom;
        }
        else
        {
            inkTop = box.Y + box.Height * 0.25;
            inkBottom = box.Y + box.Height * 0.75;
        }

        // Fraction of the em box the measured ink covers, by script and letter shapes.
        double coverage;
        if (cjk)
        {
            coverage = 0.88;
        }
        else
        {
            var tall = block.Text.Any(c => char.IsUpper(c) || char.IsDigit(c) || "bdfhiklt".Contains(c));
            var deep = block.Text.Any(c => "gjpqy".Contains(c));
            coverage = (tall ? 0.72 : 0.53) + (deep ? 0.22 : 0);
        }

        var em = Math.Clamp((inkBottom - inkTop) / coverage, 6, 200);
        var ink = inks.Count < 4
            ? (Luminance(background) > 0.55 ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Colors.White)
            : Average(inks.OrderByDescending(i => i.Distance).Take(Math.Max(3, inks.Count / 3)).Select(i => i.Color));

        return new Line(block.Text, box, background, flat, ink, inkTop, inkBottom, em, cjk);
    }

    /// <summary>
    /// The run of ink rows around the centre row, bridging gaps of a few rows
    /// (the dot of an "i", thin horizontal strokes) but not the inter-line gap.
    /// Returns [top, bottom) in box-relative rows.
    /// </summary>
    private static (int Top, int Bottom) InkBand(bool[] rows)
    {
        var n = rows.Length;
        var center = n / 2;
        var seed = -1;
        for (var d = 0; d < n && seed < 0; d++)
        {
            if (center - d >= 0 && rows[center - d]) seed = center - d;
            else if (center + d < n && rows[center + d]) seed = center + d;
        }

        if (seed < 0)
        {
            return (0, 0);
        }

        var tolerance = Math.Max(1, (int)Math.Round(n * 0.1));
        int top = seed, bottom = seed;
        for (int y = seed - 1, gap = 0; y >= 0; y--)
        {
            if (rows[y]) { top = y; gap = 0; }
            else if (++gap > tolerance) break;
        }

        for (int y = seed + 1, gap = 0; y < n; y++)
        {
            if (rows[y]) { bottom = y; gap = 0; }
            else if (++gap > tolerance) break;
        }

        return (top, bottom + 1);
    }

    /// <summary>Skips numbers/symbols and text that is already in the target language (like Chrome does).</summary>
    public static bool NeedsTranslation(string text, string target)
    {
        var letters = text.Count(c => char.IsLetter(c));
        if (letters == 0)
        {
            return false;
        }

        var cjk = text.Count(IsCjk);
        var kana = text.Count(c => c is >= '぀' and <= 'ヿ');
        var latin = text.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        var t = target.ToLowerInvariant();

        if (t.Contains("中文") || t.Contains("汉") || t.Contains("chinese") || t.StartsWith("zh"))
        {
            return kana > 0 || cjk < letters * 0.5;
        }

        if (t.Contains("英") || t.Contains("english") || t.StartsWith("en"))
        {
            return latin < letters * 0.8;
        }

        if (t.Contains("日") || t.Contains("japan") || t.StartsWith("ja"))
        {
            return kana == 0;
        }

        return true;
    }

    private static IEnumerable<List<int>> Batches(List<int> pending, IReadOnlyList<Paragraph> paragraphs)
    {
        var batch = new List<int>();
        var chars = 0;
        foreach (var index in pending)
        {
            var length = paragraphs[index].Text.Length;
            if (batch.Count > 0 && (batch.Count >= BatchItems || chars + length > BatchChars))
            {
                yield return batch;
                batch = new List<int>();
                chars = 0;
            }

            batch.Add(index);
            chars += length;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    // --------------------------------------------------------------- rendering

    private sealed record Pixels(byte[] Data, int Width, int Height, int Stride);

    private static Pixels ReadPixels(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = image.PixelWidth * 4;
        var data = new byte[stride * image.PixelHeight];
        source.CopyPixels(data, stride, 0);
        return new Pixels(data, image.PixelWidth, image.PixelHeight, stride);
    }

    private static BitmapSource Render(
        BitmapSource image, Pixels pixels, IReadOnlyList<Paragraph> paragraphs, IReadOnlyList<string?> translations)
    {
        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Ideal);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, pixels.Width, pixels.Height));
            for (var i = 0; i < paragraphs.Count; i++)
            {
                if (translations[i] is { Length: > 0 } text &&
                    !string.Equals(text, paragraphs[i].Text, StringComparison.Ordinal))
                {
                    DrawParagraph(dc, pixels, paragraphs[i], text);
                }
            }
        }

        var bitmap = new RenderTargetBitmap(pixels.Width, pixels.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawParagraph(DrawingContext dc, Pixels px, Paragraph paragraph, string text)
    {
        var em = paragraph.EmSize;
        var pad = Math.Max(2, (int)Math.Round(em * 0.15));
        var flat = paragraph.Lines.All(l => l.FlatBackground);
        var background = paragraph.Lines[0].Background;

        // Cover the original glyphs. A flat background is painted as one block;
        // a gradient/photo is rebuilt row by row from the pixels left and right
        // of each line so the patch blends in.
        var cover = InkArea(paragraph, pad, px);
        if (flat)
        {
            var brush = new SolidColorBrush(background);
            brush.Freeze();
            dc.DrawRectangle(brush, null, ToRect(cover));
        }
        else
        {
            foreach (var line in paragraph.Lines)
            {
                PaintInterpolated(dc, px, InkArea(line, pad, px));
            }
        }

        var foreground = new SolidColorBrush(paragraph.Lines[0].Ink);
        foreground.Freeze();
        var typeface = new Typeface(
            new FontFamily(HasCjk(text) ? "Microsoft YaHei UI" : "Segoe UI"),
            FontStyles.Normal,
            LooksBold(px, paragraph) ? FontWeights.SemiBold : FontWeights.Normal,
            FontStretches.Normal);

        var multiLine = paragraph.Lines.Count > 1;
        var lineGap = multiLine ? paragraph.Pitch / em : 1.3;
        var areaHeight = cover.Height + (multiLine ? 0 : em * 0.3);
        // A single label may grow to the right while the background stays the
        // same; a paragraph wraps inside its own column.
        var maxWidth = multiLine
            ? cover.Width
            : Math.Max(cover.Width, Math.Min(cover.Width * 1.3, px.Width - cover.X - 2));

        // The size of the original: a CJK em looks about as big as a Latin em with the same ink height.
        var size = OutputSize(em, paragraph.Lines.All(l => l.Cjk), HasCjk(text));
        var minSize = multiLine ? size : Math.Max(7, size * 0.85);
        FormattedText formatted;
        while (true)
        {
            formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, size, foreground, 1.0)
            {
                MaxTextWidth = Math.Max(4, maxWidth),
                LineHeight = size * Math.Clamp(lineGap, 1.1, 2.2)
            };

            var fits = multiLine || (formatted.Height <= areaHeight + 0.5 && formatted.Width <= maxWidth + 0.5);
            if (fits || size <= minSize)
            {
                break;
            }

            size = Math.Max(minSize, size - Math.Max(0.5, size * 0.05));
        }

        if (multiLine && formatted.Height > cover.Height)
        {
            // More lines than the original: the text flows on below; cover what it runs over.
            var below = new Int32Rect(cover.X, cover.Y + cover.Height, cover.Width,
                Math.Min(px.Height - cover.Y - cover.Height, (int)Math.Ceiling(formatted.Height - cover.Height) + pad));
            if (below.Height > 0)
            {
                if (flat)
                {
                    var brush = new SolidColorBrush(background);
                    brush.Freeze();
                    dc.DrawRectangle(brush, null, ToRect(below));
                }
                else
                {
                    PaintInterpolated(dc, px, below);
                }
            }
        }

        if (!multiLine && formatted.Width > cover.Width && cover.X + formatted.Width > cover.X + cover.Width)
        {
            // Grew past the original box: extend the cover under the longer text.
            var extra = new Int32Rect(cover.X + cover.Width, cover.Y,
                Math.Min(px.Width - cover.X - cover.Width, (int)Math.Ceiling(formatted.Width - cover.Width) + pad),
                cover.Height);
            if (extra.Width > 0)
            {
                if (flat)
                {
                    var brush = new SolidColorBrush(background);
                    brush.Freeze();
                    dc.DrawRectangle(brush, null, ToRect(extra));
                }
                else
                {
                    PaintInterpolated(dc, px, extra);
                }
            }
        }

        // Labels are centred on their original ink; paragraphs keep their first
        // line where it was and flow downwards (like a browser reflow).
        var first = paragraph.Lines[0];
        var y = multiLine
            ? (first.InkTop + first.InkBottom) / 2 - formatted.LineHeight / 2
            : (first.InkTop + first.InkBottom) / 2 - formatted.Height / 2;
        dc.DrawText(formatted, new Point(paragraph.Lines.Min(l => l.Box.X) + pad * 0.5, y));
    }

    /// <summary>
    /// The size to write the translation in: the original's em, corrected between scripts (a CJK line's ink
    /// fills more of its em than Latin ink does, so the same em looks bigger in Chinese than in English).
    /// </summary>
    public static double OutputSize(double em, bool sourceCjk, bool targetCjk) =>
        sourceCjk == targetCjk ? em : targetCjk ? em * 0.92 : em * 1.08;

    private static Int32Rect InkArea(Paragraph paragraph, int pad, Pixels px)
    {
        var left = paragraph.Lines.Min(l => l.Box.X);
        var right = paragraph.Lines.Max(l => l.Box.X + l.Box.Width);
        var top = (int)Math.Floor(paragraph.Lines[0].InkTop);
        var bottom = (int)Math.Ceiling(paragraph.Lines[^1].InkBottom);
        return Inflate(new Int32Rect(left, top, right - left, Math.Max(1, bottom - top)), pad, px);
    }

    private static Int32Rect InkArea(Line line, int pad, Pixels px)
    {
        var top = (int)Math.Floor(line.InkTop);
        var bottom = (int)Math.Ceiling(line.InkBottom);
        return Inflate(new Int32Rect(line.Box.X, top, line.Box.Width, Math.Max(1, bottom - top)), pad, px);
    }

    private static Color SampleBackground(Pixels px, Int32Rect box, out bool uniform)
    {
        // The OCR box border is background by construction (boxes are padded).
        var samples = new List<Color>();
        var step = Math.Max(1, (box.Width + box.Height) / 80);
        for (var x = box.X; x < box.X + box.Width; x += step)
        {
            samples.Add(At(px, x, box.Y));
            samples.Add(At(px, x, box.Y + box.Height - 1));
        }

        for (var y = box.Y; y < box.Y + box.Height; y += step)
        {
            samples.Add(At(px, box.X, y));
            samples.Add(At(px, box.X + box.Width - 1, y));
        }

        var median = Color.FromRgb(
            Median(samples.Select(c => c.R)),
            Median(samples.Select(c => c.G)),
            Median(samples.Select(c => c.B)));
        // Strict: even a gentle gradient shows as a visible patch when filled flat.
        uniform = samples.Count(c => Distance(c, median) < 10) >= samples.Count * 0.85;
        return median;
    }

    private static bool LooksBold(Pixels px, Paragraph paragraph)
    {
        // Ink coverage inside the ink band: regular UI text is ~20–30%, bold is denser.
        long ink = 0, total = 0;
        foreach (var line in paragraph.Lines)
        {
            for (var y = (int)line.InkTop; y < (int)line.InkBottom; y++)
            {
                for (var x = line.Box.X; x < line.Box.X + line.Box.Width; x += 2)
                {
                    total++;
                    if (Distance(At(px, x, y), line.Background) > 90)
                    {
                        ink++;
                    }
                }
            }
        }

        return total > 0 && ink > total * (paragraph.Lines[0].Cjk ? 0.42 : 0.36);
    }

    private static void PaintInterpolated(DrawingContext dc, Pixels px, Int32Rect box)
    {
        for (var y = box.Y; y < box.Y + box.Height; y++)
        {
            var left = AverageRun(px, Math.Max(0, box.X - 4), y);
            var right = AverageRun(px, Math.Min(px.Width - 4, box.X + box.Width), y);
            var brush = new LinearGradientBrush(left, right, 0);
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(box.X, y, box.Width, 1));
        }
    }

    private static Color AverageRun(Pixels px, int x, int y)
    {
        int r = 0, g = 0, b = 0;
        for (var i = 0; i < 4; i++)
        {
            var c = At(px, x + i, y);
            r += c.R;
            g += c.G;
            b += c.B;
        }

        return Color.FromRgb((byte)(r / 4), (byte)(g / 4), (byte)(b / 4));
    }

    // ----------------------------------------------------------------- helpers

    private static Color At(Pixels px, int x, int y)
    {
        x = Math.Clamp(x, 0, px.Width - 1);
        y = Math.Clamp(y, 0, px.Height - 1);
        var o = y * px.Stride + x * 4;
        return Color.FromRgb(px.Data[o + 2], px.Data[o + 1], px.Data[o]);
    }

    private static Int32Rect Inflate(Int32Rect r, int pad, Pixels px)
    {
        var x = Math.Max(0, r.X - pad);
        var y = Math.Max(0, r.Y - pad);
        var right = Math.Min(px.Width, r.X + r.Width + pad);
        var bottom = Math.Min(px.Height, r.Y + r.Height + pad);
        return new Int32Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    private static Rect ToRect(Int32Rect r) => new(r.X, r.Y, r.Width, r.Height);

    private static Color Average(IEnumerable<Color> colors)
    {
        var list = colors.ToList();
        return Color.FromRgb(
            (byte)list.Average(c => c.R),
            (byte)list.Average(c => c.G),
            (byte)list.Average(c => c.B));
    }

    private static byte Median(IEnumerable<byte> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static double Distance(Color a, Color b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

    private static bool IsCjk(char c) => c is >= '一' and <= '鿿' or >= '぀' and <= 'ヿ'
        or >= '가' and <= '힯' or >= '　' and <= '〿' or >= '＀' and <= '￯';

    private static bool HasCjk(string text) => text.Any(IsCjk);
}
