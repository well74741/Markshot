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
    public static void Run(string file)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var image = ImageFile.Load(file);
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
                var texts = paragraphs.Select(p => (string?)string.Concat(Enumerable.Repeat(sample, 1 + p.Text.Length / 60)).Substring(0, Math.Max(4, Math.Min(sample.Length * (1 + p.Text.Length / 60), (int)(p.Text.Length * 0.55))))).ToList();
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
}
