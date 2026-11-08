using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.Services;
using Markshot.UI;

/// <summary>0.8.64: 色块 sharp again, smooth soft edges, quicker drawing, the pin's language switch outside, the floating button held in, 离线翻译包.</summary>
static class Round64Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file));

    static BitmapSource Stripes(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var v = (byte)(((x / 4 + y / 4) % 2) * 255);
            px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255;
        }

        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    static byte[] Render(Action<DrawingContext> draw, int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var px = new byte[w * h * 4];
        bitmap.CopyPixels(px, w * 4, 0);
        return px;
    }

    static byte Alpha(byte[] px, int w, int x, int y) => px[(y * w + x) * 4 + 3];

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- 色块: blocks with hard edges even when a blur value is left from 模糊
        {
            var source = Stripes(64, 64);
            var box = new MosaicAnnotation { Rect = new Rect(0, 0, 64, 64), BlockSize = 8, Blur = 12, Gaussian = false };
            MosaicBuilder.Rebuild(box, source);
            Check(box.Pattern != null && box.Pattern.PixelWidth == 8, "色块 stays one pixel per block (no blur over it)");
            Check(Source("UI/PinWindow.Orientation.cs").Contains("PatternFor(block, box.Gaussian ? box.Blur : 0, box.Gaussian)"), "… also after the pin is turned / flipped");
        }

        // ---- smooth soft edges (no steps)
        {
            var pattern = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 90, 90, 90, 255 }, 4);
            var soft = new MosaicAnnotation { Rect = new Rect(40, 20, 100, 60), Pattern = pattern, PatternRect = new Rect(0, 0, 200, 100), Gaussian = true, Blur = 10, Feather = 30 };
            var px = Render(soft.Draw, 200, 100);
            var profile = Enumerable.Range(140, 30).Select(x => (int)Alpha(px, 200, x, 50)).ToList();
            var steps = profile.Zip(profile.Skip(1), (a, b) => a - b).ToList();
            Check(Alpha(px, 200, 139, 50) > 240, $"模糊 is whole inside its box ({Alpha(px, 200, 139, 50)})");
            Check(profile.First() > profile.Last() && steps.All(d => d >= -1) && steps.Max() < 30,
                $"… and fades out beyond it smoothly, without steps ({string.Join(",", profile.Where((_, i) => i % 3 == 0))})");
            var again = soft.GetType().GetField("_mask", Any)!.GetValue(soft);
            soft.Draw(new DrawingVisual().RenderOpen());
            Check(again != null && ReferenceEquals(again, soft.GetType().GetField("_mask", Any)!.GetValue(soft)), "the soft edge is made once (not at every frame)");

            var spot = new SpotlightAnnotation { Rect = new Rect(60, 30, 80, 40), Darkness = 0.6, Feather = 30 };
            var items = new List<Annotation> { spot };
            var sp = Render(dc => AnnotationEditor.DrawLayers(dc, items), 200, 100);
            var ramp = Enumerable.Range(40, 40).Select(x => (int)Alpha(sp, 200, x, 50)).ToList();
            var rampSteps = ramp.Zip(ramp.Skip(1), (a, b) => b - a).ToList();
            Check(ramp.First() > ramp.Last() && rampSteps.All(d => d <= 1) && rampSteps.Min() > -14,
                $"聚焦's soft edge is smooth too ({string.Join(",", ramp.Where((_, i) => i % 4 == 0))})");
            Check(Alpha(sp, 200, 100, 50) < 10 && Math.Abs(Alpha(sp, 200, 5, 5) - 153) < 4, "clear inside, the darkness outside");
        }

        // ---- quicker: a brush's outline and an element's holes are not worked out again at every frame / move
        {
            var brush = new MosaicBrushAnnotation { Thickness = 20 };
            for (var i = 0; i < 400; i++) brush.Points.Add(new Point(10 + i, 50 + 20 * Math.Sin(i / 20.0)));
            brush.Draw(new DrawingVisual().RenderOpen());
            var area = brush.GetType().GetField("_area", Any)!.GetValue(brush);
            brush.Draw(new DrawingVisual().RenderOpen());
            Check(area != null && ReferenceEquals(area, brush.GetType().GetField("_area", Any)!.GetValue(brush)), "a mosaic stroke's outline is kept");

            var rect = new RectAnnotation { Rect = new Rect(0, 0, 100, 100), Fill = true, Color = Colors.Red };
            rect.AddEraseStroke(new[] { new Point(20, 50), new Point(80, 50) }, 10);
            var before = rect.EraseClip()!.Bounds.Left;
            var clipField = typeof(Annotation).GetField("ClipCache", Any)!;
            var kept = clipField.GetValue(rect);
            rect.Translate(30, 0);
            var moved = rect.EraseClip();
            Check(kept != null && ReferenceEquals(kept, clipField.GetValue(rect)) && Math.Abs(moved!.Bounds.Left - before - 30) < 0.5, "moved: its holes go along without being recomputed");
            var watch = Stopwatch.StartNew();
            var many = new List<Annotation> { rect };
            for (var i = 0; i < 40; i++)
            {
                var cut = new CutoutAnnotation { Thickness = 8, Baked = true };
                cut.Points.Add(new Point(i * 2, 10));
                cut.Points.Add(new Point(i * 2 + 5, 90));
                many.Add(cut);
            }

            for (var i = 0; i < 30; i++) AnnotationEditor.DrawLayers(new DrawingVisual().RenderOpen(), many);
            Check(watch.ElapsedMilliseconds < 1500, $"many finished holes: drawing stays quick ({watch.ElapsedMilliseconds} ms for 30 frames)");
            Check(Source("UI/ImageEditorWindow.cs").Contains("FollowMosaics(); // a picture dragged under a mosaic"), "editor: mosaics follow a picture while it is dragged");
        }

        // ---- the floating button stays pressed in while dragged
        {
            var code = Source("UI/FloatButtonWindow.cs");
            var move = code[code.IndexOf("private void OnMainMove", StringComparison.Ordinal)..code.IndexOf("private void OnMainUp", StringComparison.Ordinal)];
            Check(!move.Contains("Press(down: false)"), "dragging keeps the button small; it grows back when let go");
            Check(Source("UI/SettingsWindow.xaml").Contains("<Image x:Name=\"FloatIconImage\" Width=\"62\" Height=\"62\""), "the icon preview shows it bigger");
        }

        // ---- the pin's [原文 | 译文]: outside the picture, above its top right corner
        {
            var pin = new PinWindow(Stripes(160, 100), 600, 400, Stripes(160, 100), null) { Left = 600, Top = 400, ShowActivated = false };
            pin.Show();
            Pump(300);
            pin.AdoptTranslation(Stripes(160, 100), "English", "中文");
            Pump(200);
            var host = (Window?)pin.GetType().GetProperty("LanguageHost", Any)!.GetValue(pin);
            Check(host is { IsVisible: true }, "a window of its own");
            if (host != null)
            {
                var inset = (int)pin.GetType().GetProperty("Pad", Any)!.GetValue(pin)! / (VisualTreeHelper.GetDpi(pin).DpiScaleX) * VisualTreeHelper.GetDpi(pin).DpiScaleX;
                var imageTop = pin.Top + inset;
                var imageRight = pin.Left + pin.ActualWidth - inset;
                Check(host.Top + host.ActualHeight <= imageTop + 1 && Math.Abs(host.Left + host.ActualWidth - imageRight) < 3,
                    $"above the picture, right-aligned with it (bar {host.Left:0},{host.Top:0} {host.ActualWidth:0}×{host.ActualHeight:0}; picture right {imageRight:0}, top {imageTop:0})");
                pin.Left += 50;
                Pump(100);
                Check(Math.Abs(host.Left + host.ActualWidth - (imageRight + 50)) < 3, "it follows the pin");
                pin.Hide();
                Pump(50);
                Check(!host.IsVisible, "hidden with the pin");
            }

            pin.Close();
        }

        // ---- 离线翻译包
        {
            Check(OfflinePacks.All.Select(p => p.Id).SequenceEqual(new[] { "en-zh", "zh-en" }) &&
                  OfflinePacks.Root.Contains("Markshot") && !Directory.Exists(Repo.File("Assets/translate")), "packs are downloaded into the user's folder, not shipped");
            Check(OfflinePacks.For("hello", "简体中文")?.Id == "en-zh" && OfflinePacks.For("你好", "English")?.Id == "zh-en" && OfflinePacks.For("x", "日本語") == null,
                "the pack follows the target language (中 ↔ 英)");
            var old = OfflinePacks.RootOverride;
            var root = Path.Combine(Path.GetTempPath(), "markshot-mt-test-" + Guid.NewGuid().ToString("N"));
            OfflinePacks.RootOverride = root;
            try
            {
                var api = new TranslationConfig { ApiKey = "", BaseUrl = "https://api.deepseek.com/v1" };
                Check(!TranslationService.UsesOffline(api), "自动 without a pack: the API (it says what is missing)");
                Directory.CreateDirectory(Path.Combine(root, "en-zh"));
                foreach (var (_, local) in OfflinePacks.Files) File.WriteAllText(Path.Combine(root, "en-zh", local), "x");
                Check(TranslationService.UsesOffline(api), "自动, no API key, a pack there: the pack");
                Check(!TranslationService.UsesOffline(new TranslationConfig { ApiKey = "k", BaseUrl = "https://api.deepseek.com/v1" }), "自动 with a key: the API");
                Check(!TranslationService.UsesOffline(new TranslationConfig { ApiKey = "", BaseUrl = "http://localhost:11434/v1" }), "自动 with a local server: the API (needs no key)");
                Check(TranslationService.UsesOffline(new TranslationConfig { Engine = "Offline", ApiKey = "k" }) &&
                      !TranslationService.UsesOffline(new TranslationConfig { Engine = "Api", ApiKey = "" }), "离线翻译包 / 大模型 API chosen: as chosen");

                // The tokenizer: the likeliest pieces (unigram), language tags kept whole.
                var tokenizer = Path.Combine(root, "tok.json");
                File.WriteAllText(tokenizer, "{\"model\":{\"type\":\"Unigram\",\"unk_id\":1,\"vocab\":[[\"</s>\",0],[\"<unk>\",0],[\"▁he\",-2],[\"llo\",-2],[\"▁hello\",-1],[\"▁\",-5],[\"h\",-6],[\"e\",-6],[\"l\",-6],[\"o\",-6],[\">>cmn_Hans<<\",-100],[\"▁world\",-1]]},\"added_tokens\":[{\"id\":0,\"special\":true},{\"id\":1,\"special\":true}]}");
                var type = typeof(OfflineTranslator).GetNestedType("Unigram", Any)!;
                var unigram = type.GetMethod("Load", Any)!.Invoke(null, new object[] { tokenizer })!;
                var ids = (List<int>)type.GetMethod("Encode", Any)!.Invoke(unigram, new object[] { ">>cmn_Hans<< hello world" })!;
                Check(ids.SequenceEqual(new[] { 10, 4, 11 }), $"pieces: a language tag, ▁hello, ▁world ({string.Join(",", ids)})");
                var text = (string)type.GetMethod("Decode", Any)!.Invoke(unigram, new object[] { new[] { 10, 4, 11 } })!;
                Check(text == "hello world", $"back to text, the tag left out ({text})");
            }
            finally
            {
                OfflinePacks.RootOverride = old;
                try { Directory.Delete(root, true); } catch { }
            }

            var settings = new SettingsWindow(new AppConfig { Theme = "Dark" }) { Left = -4000, Top = -4000, ShowActivated = false };
            settings.Show();
            Pump(250);
            var rows = (Panel)settings.FindName("OfflinePackRows");
            Check(((RadioButton)settings.FindName("EngineAuto")).IsChecked == true && rows.Children.Count == 2 &&
                  ((Panel)settings.FindName("MirrorRow")).Children.Count == 2, "设置 → 翻译: 翻译方式, 下载源 and one row per pack");
            Check(rows.Children.OfType<DockPanel>().All(row => row.Children.OfType<Button>().Any(b => (string)b.Content is "下载" or "删除")), "each pack can be downloaded (or removed)");
            ((RadioButton)settings.FindName("EngineOffline")).IsChecked = true;
            Check(settings.HasUnsavedChanges, "a new 翻译方式 waits for 保存");
            settings.Close();

            // A real pack when one is at hand (MARKSHOT_MT_ROOT); never downloaded by the tests.
            if (Environment.GetEnvironmentVariable("MARKSHOT_MT_ROOT") is { Length: > 0 } real)
            {
                OfflinePacks.RootOverride = real;
                var result = new OfflineProvider().TranslateAsync("Click the button to save your changes.", "简体中文", CancellationToken.None).GetAwaiter().GetResult();
                Check(result.Contains("保存") && !result.Contains(' '), $"a real pack translates ({result})");
                OfflinePacks.RootOverride = old;
            }
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
