using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.Services;

/// <summary>
/// Diagnosis, not in the default run: the font size measured for each OCR line of a picture, and the picture
/// redrawn with fixed sample translations (no translation request is made).
/// </summary>
static class FontProbe
{
    /// <param name="file">A picture, or "page" for a drawn page of Chinese text (headings and wrapped bullets).</param>
    /// <param name="english">Redraw with English samples (as if Chinese were translated to English).</param>
    public static void Run(string file, bool english = false)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var image = file == "page" ? Page() : ImageFile.Load(file);
        if (file == "page")
        {
            var page = new PngBitmapEncoder();
            page.Frames.Add(BitmapFrame.Create(image));
            using var pf = System.IO.File.Create("fontprobe_page.png");
            page.Save(pf);
        }
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                var blocks = await OcrService.RecognizeBlocksAsync(image);
                var paragraphs = ImmersiveTranslator.Analyze(image, blocks);
                foreach (var p in paragraphs)
                {
                    foreach (var l in p.Lines)
                    {
                        Console.WriteLine($"box h={l.Box.Height,3} ink={l.InkBottom - l.InkTop,5:0.0} em={l.EmSize,5:0.0} para(lines={p.Lines.Count}, em={p.EmSize:0.0}) :: {(l.Text.Length > 34 ? l.Text[..34] : l.Text)}");
                    }
                }

                // Every paragraph gets a Chinese sample of about the same length as its text.
                const string sample = "这是一段用于检查字号的中文示例文字，长度大致与原文相当，用来观察译文在原位置的大小和换行。";
                const string englishSample = "This is an English sample sentence used to check the size and the wrapping of a translation in the place of the original text. ";
                var texts = english
                    ? paragraphs.Select(p => (string?)string.Concat(Enumerable.Repeat(englishSample, 2 + p.Text.Length / 40)).Substring(0, Math.Max(6, p.Text.Length * (Environment.GetEnvironmentVariable("FONTPROBE_LONG") == "1" ? 4 : 2))).Trim()).ToList()
                    : paragraphs.Select(p => (string?)string.Concat(Enumerable.Repeat(sample, 1 + p.Text.Length / 60)).Substring(0, Math.Max(4, Math.Min(sample.Length * (1 + p.Text.Length / 60), (int)(p.Text.Length * 0.55))))).ToList();
                var flags = BindingFlags.NonPublic | BindingFlags.Static;
                var pixels = typeof(ImmersiveTranslator).GetMethod("ReadPixels", flags)!.Invoke(null, new object[] { image });
                var result = (BitmapSource)typeof(ImmersiveTranslator).GetMethod("Render", flags)!.Invoke(null, new object?[] { image, pixels, paragraphs, texts })!;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(result));
                using (var f = System.IO.File.Create("fontprobe_render.png"))
                {
                    encoder.Save(f);
                }

                Console.WriteLine("wrote fontprobe_render.png");
            }
            catch (Exception ex)
            {
                Console.WriteLine("error " + ex);
            }
            finally
            {
                frame.Continue = false;
            }
        }));
        Dispatcher.PushFrame(frame);
    }

    private static BitmapSource Page()
    {
        var panel = new System.Windows.Controls.StackPanel { Width = 800, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E)) };
        void Text(string text, double size, bool bold, double left, double top)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = text, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
                FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"), LineHeight = size * 1.7,
                Foreground = System.Windows.Media.Brushes.Gainsboro, Margin = new Thickness(left, top, 24, 0)
            });
        }

        Text("1. 原位翻译字号偏小", 15, true, 24, 16);
        Text("· 原因：以前多行段落的规则是“一直缩小，直到塞进原文的高度”，最多能缩到 60%。英文译成中文后行数会变，几乎每段都被缩到很小。", 14, false, 40, 6);
        Text("· 现在：译文字号就是原文字号，中英文之间只做 ±8% 的微调，多行段落不再缩小。译文放不下时会往下延续。", 14, false, 40, 6);
        Text("2. 设置窗口遮住其他窗口", 15, true, 24, 16);
        Text("· 设置窗口去掉了“总在最前”，从托盘打开时只在打开那一下放到前面。", 14, false, 40, 6);
        Text("· 下载更新的窗口也去掉了置顶，现在只浮在设置窗口上面。", 14, false, 40, 6);
        Text("3. 滚轮调的识别档位会被记住", 15, true, 24, 16);
        Text("· 滚轮换层后，会按当前框住的元素换算成 4 档之一，下一个元素和下次截图都从这一档开始。", 14, false, 40, 6);
        Text("· 例外：鼠标下面没有窗口时不会记住。", 14, false, 40, 6);
        panel.Children.Add(new System.Windows.Controls.Border { Height = 20 });
        panel.Measure(new Size(800, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(800, (int)Math.Ceiling(panel.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(panel);
        bitmap.Freeze();
        return bitmap;
    }
}
