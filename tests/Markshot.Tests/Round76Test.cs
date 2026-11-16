using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.77: 文字识别 after 原位翻译 — the original's text, the translation already made as the 译文.</summary>
static class Round76Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file)).Replace("\r\n", "\n");

    static BitmapSource Text(string text, int w = 360, int h = 80)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 22, Brushes.Black, 1), new Point(16, 22));
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- the window: the 译文 given, shown at once; the 原文 read from the (original) picture
        var models = File.Exists(Path.Combine(AppContext.BaseDirectory, "models", "v5", "ch_PP-OCRv5_mobile_det.onnx"));
        {
            var window = new TranslationWindow(Text("Export settings"), autoTranslate: false) { GivenTranslation = "导出设置", GivenTarget = "中文", Left = -4000, Top = -4000, ShowActivated = false };
            window.Show();
            for (var waited = 0; waited < 8000 && ((TextBox)window.FindName("SourceBox")).Text.Length == 0 && models; waited += 100) Pump(100);
            Pump(200);
            var source = ((TextBox)window.FindName("SourceBox")).Text;
            var result = ((TextBox)window.FindName("ResultBox")).Text;
            Check(result == "导出设置", $"the translation already made is the 译文, at once ({result})");
            if (models)
            {
                Check(source.Contains("Export", StringComparison.OrdinalIgnoreCase), $"the 原文 is read from the original picture ({source})");
            }

            Check(((ComboBox)window.FindName("TargetBox")).SelectedItem as string == "简体中文", "its language picked in the list");
            window.Close();
        }

        // ---- the capture and the pin hand over the original picture and the translation's text
        {
            var finish = Source("UI/OverlayWindow.Finish.cs");
            Check(finish.Contains("var image = translated != null ? Compose(includeAnnotations: true, withTranslation: false) : Compose();") &&
                  finish.Contains("ShowOcr(image, translated, _targetLabel)"), "capture → 文字识别: the picture without the translation drawn on it, and the translation");
            Check(Source("Services/ImmersiveTranslator.cs").Contains("{ Text = text }"), "the in-place translation reports its text when done");

            var pin = new PinWindow(Text("Export settings"), -4000, -4000, null, null) { ShowActivated = false };
            pin.Show();
            Pump(200);
            var translatedPicture = Text("导出设置");
            var original = Text("Export settings");
            typeof(PinWindow).GetField("_baseImage", Any)!.SetValue(pin, translatedPicture);
            pin.AdoptTranslation(original, "English", "中文", "导出设置");
            var (image, translation, target) = ((BitmapSource, string?, string?))typeof(PinWindow).GetMethod("ForRecognition", Any)!.Invoke(pin, null)!;
            Check(ReferenceEquals(image, original) && translation == "导出设置" && target == "中文", "a pin translated in place: 文字识别 gets the original and the translation");
            pin.CloseImmediately();
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
