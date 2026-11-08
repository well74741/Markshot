using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markshot.Services;

/// <summary>离线翻译 by hand: MARKSHOT_MT_ROOT points at packs; MARKSHOT_MT_DOWNLOAD=zh-en downloads one there first (never part of the regular run).</summary>
static class MtProbe
{
    public static void Run()
    {
        OfflinePacks.RootOverride = Environment.GetEnvironmentVariable("MARKSHOT_MT_ROOT");
        if (Environment.GetEnvironmentVariable("MARKSHOT_MT_DOWNLOAD") is { Length: > 0 } id)
        {
            var watch = Stopwatch.StartNew();
            OfflinePacks.DownloadAsync(OfflinePacks.Find(id)!, Environment.GetEnvironmentVariable("MARKSHOT_MT_MIRROR") ?? "hf-mirror", null, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"downloaded {id}: {OfflinePacks.SizeOnDisk(OfflinePacks.Find(id)!) / 1048576} MB in {watch.Elapsed.TotalSeconds:0} s");
        }

        var provider = new OfflineProvider();
        foreach (var (text, target) in new[] { ("Hello, world!", "简体中文"), ("Click the button to save your changes.", "简体中文"), ("点击按钮保存你的修改。", "English"), ("今天天气很好，我们去公园散步吧。", "English") })
        {
            try
            {
                var watch = Stopwatch.StartNew();
                var result = provider.TranslateAsync(text, target, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"{text} => {result}  ({watch.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{text} => {ex.Message}");
            }
        }

        // 原位翻译 end to end with 离线翻译包 chosen: the API points nowhere, so any network use would fail.
        var config = Markshot.App.Config;
        config.Translation.Engine = "Offline";
        config.Translation.BaseUrl = "http://127.0.0.1:9/v1";
        config.Translation.ApiKey = "not-used";
        config.Translation.TargetLanguage = "简体中文";
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 160));
            var text = new FormattedText("Save your changes before closing the window.", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 26, Brushes.Black, 1.0);
            dc.DrawText(text, new Point(20, 60));
        }

        var bitmap = new RenderTargetBitmap(640, 160, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var last = default(ImmersiveTranslator.Update);
        var task = ImmersiveTranslator.TranslateAsync(bitmap, new Progress<ImmersiveTranslator.Update>(u => last = u), CancellationToken.None);
        var frame = new System.Windows.Threading.DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, System.Threading.Tasks.TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Console.WriteLine($"immersive offline: {(task.IsFaulted ? task.Exception!.InnerException!.Message : "ok")}, translated {last?.Translated}/{last?.Total}, status {last?.Status}");
    }
}
