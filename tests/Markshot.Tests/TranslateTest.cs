using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Services;

static class TranslateTest
{
    public static void RunReal()
    {
        // The user's real translation settings (key never printed).
        Markshot.App.Config.Translation = Markshot.Core.AppConfig.Load().Translation;
        Console.WriteLine("model=" + Markshot.App.Config.Translation.Model + " key set=" + (Markshot.App.Config.Translation.ApiKey.Length > 0));
        Execute("real");
    }

    public static void Run()
    {
        Markshot.App.Config.Translation.BaseUrl = "http://127.0.0.1:8123/v1";
        Markshot.App.Config.Translation.ApiKey = "";
        Markshot.App.Config.Translation.TargetLanguage = "简体中文";
        Execute("mock");
    }

    static void Execute(string tag)
    {

        Console.WriteLine("cfg url=" + Markshot.App.Config.Translation.BaseUrl + " same=" + ReferenceEquals(Markshot.App.Config, Markshot.App.Config));
        var image = Scene();
        Save(image, "tr_before.png");

        var frame = new DispatcherFrame();
        var updates = 0;
        BitmapSource? result = null;
        Exception? error = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                var blocks = await OcrService.RecognizeBlocksAsync(image);
                Console.WriteLine($"OCR lines: {blocks.Count}");
                foreach (var p in ImmersiveTranslator.Analyze(image, blocks))
                {
                    Console.WriteLine($"  para lines={p.Lines.Count} em={p.EmSize:F1} ink={p.Lines[0].Ink} box={p.Box} needs={ImmersiveTranslator.NeedsTranslation(p.Text, "简体中文")} :: {p.Text}");
                }

                var progress = new Progress<ImmersiveTranslator.Update>(u =>
                {
                    updates++;
                    Console.WriteLine($"  update: {u.Status} done={u.Done} {u.Translated}/{u.Total}");
                });
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Console.WriteLine("cfg url before call=" + Markshot.App.Config.Translation.BaseUrl);
                result = await ImmersiveTranslator.TranslateAsync(image, progress, CancellationToken.None);
                Console.WriteLine($"translate took {sw.ElapsedMilliseconds} ms");
                sw.Restart();
                await ImmersiveTranslator.TranslateAsync(image, null, CancellationToken.None);
                Console.WriteLine($"second run (cache) took {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                frame.Continue = false;
            }
        }));
        Dispatcher.PushFrame(frame);
        // Let queued Progress callbacks run.
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        if (error != null)
        {
            Console.WriteLine("ERROR " + error);
            return;
        }

        Save(result!, tag == "real" ? "tr_real.png" : "tr_after.png");
        Console.WriteLine($"updates reported: {updates}; wrote tr_before.png / tr_after.png");
    }

    static BitmapSource Scene()
    {
        const int w = 760, h = 420;
        var dv = new DrawingVisual();
        TextOptions.SetTextFormattingMode(dv, TextFormattingMode.Display);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(250, 250, 250)), null, new Rect(0, 0, w, h));
            // banner with gradient
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(36, 99, 235), Color.FromRgb(124, 58, 237), 0), null, new Rect(0, 0, w, 64));
            Text(dc, "Welcome to Markshot", 24, 18, 26, Colors.White, true);
            // menu column
            var menu = new[] { "File", "Edit", "View", "Settings", "Help" };
            for (var i = 0; i < menu.Length; i++) Text(dc, menu[i], 24, 92 + i * 34, 16, Color.FromRgb(40, 40, 40), false);
            // paragraph that wraps
            var para = "The quick brown fox jumps over the lazy dog. This screenshot translation keeps the layout of the original paragraph and wraps the text inside its own column.";
            var ft = new FormattedText(para, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 17, new SolidColorBrush(Color.FromRgb(55, 65, 81)), 1.0) { MaxTextWidth = 520, LineHeight = 26 };
            dc.DrawText(ft, new Point(200, 92));
            // dark button
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(17, 24, 39)), null, new Rect(200, 230, 150, 44), 8, 8);
            Text(dc, "Download", 238, 240, 18, Colors.White, true);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(229, 231, 235)), null, new Rect(370, 230, 130, 44), 8, 8);
            Text(dc, "Cancel", 405, 240, 18, Color.FromRgb(17, 24, 39), false);
            // release notes + chinese (should be skipped)
            Text(dc, "Release notes: new features and improvements", 200, 300, 16, Color.FromRgb(220, 38, 38), false);
            Text(dc, "这一行已经是中文，不应被替换", 200, 340, 16, Color.FromRgb(22, 101, 52), false);
            Text(dc, "Version 2.4.1", 200, 378, 14, Color.FromRgb(107, 114, 128), false);
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    static void Text(DrawingContext dc, string s, double x, double y, double size, Color c, bool bold)
    {
        var family = s.Any(ch => ch > 0x2E80) ? "Microsoft YaHei UI" : "Segoe UI";
        dc.DrawText(new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(family), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, new SolidColorBrush(c), 1.0), new Point(x, y));
    }

    static void Save(BitmapSource b, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var f = File.Create(path);
        enc.Save(f);
    }
}
