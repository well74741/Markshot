using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.68: faster capture start, window detection, floating button tips / on top / any copy, settings, the scroll bar, 圆角 remembered.</summary>
static class Round68Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file)).Replace("\r\n", "\n");

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- the capture opens sooner: a window made in advance; the floating button out of the way at once
        {
            var spare = OverlayWindow.CreateSpare();
            Check(((bool)spare.GetType().GetProperty("IsSpare", Any)!.GetValue(spare)!) && !spare.IsVisible && new System.Windows.Interop.WindowInteropHelper(spare).Handle != IntPtr.Zero,
                "a capture window is made in advance, hidden, its handle ready");
            var screen = System.Windows.Media.Imaging.BitmapSource.Create(4, 4, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, new byte[64], 16);
            spare.Load(screen, new Int32Rect(-4000, -4000, 4, 4));
            Check(!((bool)spare.GetType().GetProperty("IsSpare", Any)!.GetValue(spare)!) && ReferenceEquals(spare.FindName("ScreenImage") is Image { } img ? img.Source : null, screen), "a capture loads its screenshot into it");
            spare.Close();
            var app = Source("App.xaml.cs");
            Check(app.Contains("var overlay = TakeSpareOverlay(image, bounds, demo) ?? new OverlayWindow(image, bounds, demo);") &&
                  app.Contains("Dispatcher.BeginInvoke(PrepareSpareOverlay, DispatcherPriority.ApplicationIdle)") && app.Contains("AppConfig.Saved +="),
                "captures use it; the next one is made while idle, again after settings change");
            var suspend = Source("UI/FloatButtonWindow.cs");
            suspend = suspend[suspend.IndexOf("public void Suspend()", StringComparison.Ordinal)..suspend.IndexOf("public void Resume()", StringComparison.Ordinal)];
            Check(!suspend.Contains("FadeOutAndWait();") && suspend.Contains("_root.Opacity = 0;"), "the floating button is gone at once (no fade waited for)");
        }

        // ---- window detection: the window as drawn, only windows really on the screen
        {
            var code = Source("Core/WindowDetector.cs");
            Check(code.Contains("var bounds = GetBounds(handle);") && !code.Contains("if (!Native.GetWindowRect(handle, out var rect) ||\n                rect.Width < 8"),
                "hit by the drawn frame (not the invisible resize borders)");
            Check(code.Contains("DWMWA_CLOAKED") && code.Contains("WS_EX_TRANSPARENT") && code.Contains("alpha == 0"), "hidden (cloaked), click-through and see-through windows are skipped");
        }

        // ---- the floating button: tips above the row, the click's action in its tip, on top again and again
        {
            var config = new AppConfig { FloatActionHidden = "", FloatClickAction = "Ocr" };
            var button = new FloatButtonWindow(config, _ => { }, () => { }, () => { }) { FullscreenOverride = () => false };
            button.Show();
            Pump(200);
            var items = (StackPanel)button.GetType().GetField("_items", Any)!.GetValue(button)!;
            Check(items.Children.OfType<Button>().All(b => ToolTipService.GetPlacement(b) is PlacementMode.Top or PlacementMode.Bottom),
                "the row's tips sit above / below the row, not over the next buttons");
            var main = (FrameworkElement)button.GetType().GetField("_main", Any)!.GetValue(button)!;
            Check(main.ToolTip is string tip && tip.StartsWith("单击：文字识别"), $"the button's tip says what a click does ({main.ToolTip})");
            Check(((DispatcherTimer)button.GetType().GetField("_topTimer", Any)!.GetValue(button)!).IsEnabled, "it is put back on top now and then (popups of other programs)");
            button.Close();
            Check(Source("App.xaml.cs").Contains("new ClipboardWatcher(") && File.Exists(Repo.File("Core/ClipboardWatcher.cs")) &&
                  Source("Core/ClipboardWatcher.cs").Contains("AddClipboardFormatListener"), "a picture copied by any program shows on the button");
            Check(Source("UI/SettingsWindow.xaml").Contains("显示悬浮按钮（点击行为可自定义，悬停展开更多）"), "settings: 点击行为可自定义");
        }

        // ---- 圆角 of rectangles is remembered
        {
            var config = Markshot.App.Config;
            var bar = new AnnotationToolbar();
            bar.SetTool(AnnotationTool.Rectangle);
            ((Slider)bar.FindName("CornerSlider")).Value = 14;
            Check(Math.Abs(config.RectCornerRadius - 14) < 0.01, "a 圆角 set goes into the settings");
            var next = new AnnotationToolbar();
            next.SetTool(AnnotationTool.Rectangle);
            Check(Math.Abs(next.CornerRadius - 14) < 0.01 && Math.Abs(((Slider)next.FindName("CornerSlider")).Value - 14) < 0.01, "the next rectangle starts with it");
            config.RectCornerRadius = 0;
        }

        // ---- the long screenshot bar looks like the others; the settings window rests when idle; shorter texts
        {
            var scroll = Source("UI/ScrollCaptureSession.cs");
            Check(scroll.Contains("CornerRadius = new CornerRadius(9)") && scroll.Contains("Theme.Key(0xFF1E1E1E)"),
                "the long screenshot bar: rounded, the annotation bar's colour");
            Check(Source("Core/Marquee.cs").Contains("RepeatBehavior = new RepeatBehavior(2)"), "a long line scrolls twice, then rests (no endless redrawing)");
            var xaml = Source("UI/SettingsWindow.xaml");
            var longest = System.Text.RegularExpressions.Regex.Matches(xaml, "Style=\"\\{StaticResource Hint\\}\"[^>]*Text=\"([^\"]+)\"").Select(m => m.Groups[1].Value.Length).DefaultIfEmpty(0).Max();
            Check(longest <= 45, $"settings hints are short ({longest} characters at most)");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
