using System;
using System.Diagnostics;
using System.Threading;
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
    }
}
