using System;
using System.Collections.Generic;
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

/// <summary>0.8.72: closed capture windows and pins are freed (memory), the hover list, the OCR window, small items.</summary>
static class Round72Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file)).Replace("\r\n", "\n");

    static BitmapSource Screen(int w, int h)
    {
        var px = new byte[w * h * 4];
        new Random(1).NextBytes(px);
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    static void Collect()
    {
        for (var i = 0; i < 4; i++)
        {
            Pump(60);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference OpenAndCloseOverlay()
    {
        var overlay = new OverlayWindow(Screen(320, 200), new Int32Rect(-6000, -6000, 320, 200));
        overlay.Show();
        Pump(150);
        // the line that scrolls when long (a Marquee) is what held them before
        overlay.GetType().GetMethod("ShowNotice", Any)?.Invoke(overlay, new object[] { "一行很长的提示文字，长到需要滚动播放才能看完整的内容，测试用" });
        Pump(80);
        var weak = new WeakReference(overlay);
        overlay.Close();
        return weak;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference OpenAndClosePin()
    {
        var pin = new PinWindow(Screen(160, 100), -6000, -6000, null, null) { ShowActivated = false };
        pin.Show();
        Pump(150);
        var weak = new WeakReference(pin);
        pin.CloseImmediately();
        return weak;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference OpenAndCloseEditor()
    {
        var editor = new ImageEditorWindow(Screen(300, 200)) { Left = 40, Top = 40, ShowActivated = false };
        editor.Show();
        Pump(250);
        var weak = new WeakReference(editor);
        editor.CloseWithoutAsking();
        return weak;
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- memory: a closed capture window / pin is gone (with its screenshots)
        {
            var overlays = Enumerable.Range(0, 4).Select(_ => OpenAndCloseOverlay()).ToList();
            var pins = Enumerable.Range(0, 3).Select(_ => OpenAndClosePin()).ToList();
            var editors = Enumerable.Range(0, 2).Select(_ => OpenAndCloseEditor()).ToList();
            Collect();
            var overlaysAlive = overlays.Count(w => w.IsAlive);
            var pinsAlive = pins.Count(w => w.IsAlive);
            if (Environment.GetEnvironmentVariable("R72WAIT") == "1")
            {
                Console.WriteLine("PID " + Environment.ProcessId);
                Pump(45000);
            }

            var editorsAlive = editors.Count(w => w.IsAlive);
            Check(overlaysAlive == 0, $"closed capture windows are freed ({overlaysAlive} of {overlays.Count} still held)");
            Check(pinsAlive == 0, $"closed pins are freed ({pinsAlive} of {pins.Count} still held)");
            Check(editorsAlive == 0, $"closed image editors are freed ({editorsAlive} of {editors.Count} still held)");
            Check(Source("Core/Marquee.cs").Contains("descriptor.RemoveValueChanged(block, changed);"), "the scrolling line lets go of its text when unloaded (WPF held it, and its window, for good)");
        }

        // ---- models let go when unused
        {
            Check(Markshot.Services.OcrService.UnloadAfter <= TimeSpan.FromMinutes(5) && Source("Services/OfflineTranslation.cs").Contains("Monitor.TryEnter(idle._run)") &&
                  Source("Services/FormulaRecognizer.cs").Contains("OcrService.UnloadAfter"), "text recognition, translation and formula models are let go after a few idle minutes");
        }

        // ---- the hover list: slides to the next tool, its width easing; above the bar's other windows; bars opaque
        {
            var config = Markshot.App.Config;
            var before = config.HideToolChevrons;
            config.HideToolChevrons = true;
            var bar = new AnnotationToolbar();
            var window = new Window { Content = bar, Left = -4000, Top = -4000, Width = 900, Height = 200, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();
            bar.SetMode(AnnotationToolbarMode.Capture);
            bar.ApplyToolOrderFromConfig();
            Pump(150);
            var open = typeof(AnnotationToolbar).GetMethod("OpenHoverMenu", Any)!;
            open.Invoke(bar, new object[] { (System.Windows.Controls.Primitives.ButtonBase)bar.FindName("ToolShape") });
            Pump(200);
            open.Invoke(bar, new object[] { (System.Windows.Controls.Primitives.ButtonBase)bar.FindName("ScrollButton") });
            var plate = typeof(AnnotationToolbar).GetProperty("PlateX", Any)!;
            var scroll = (FrameworkElement)bar.FindName("ScrollButton");
            var target = scroll.TranslatePoint(new Point(scroll.ActualWidth / 2, 0), (UIElement)bar.FindName("MainRow")).X - 42;
            var midway = (double)plate.GetValue(bar)!;
            Check(Math.Abs(midway - target) > 20, $"the plate slides from the last tool ({Math.Abs(midway - target):0} DIP away at first), one fixed width");
            // (slow machines: up to two seconds for the 140 ms glide to be drawn through)
            for (var waited = 0; waited < 2000 && Math.Abs((double)plate.GetValue(bar)! - target) >= 0.5; waited += 100) Pump(100);
            Check(Math.Abs((double)plate.GetValue(bar)! - target) < 0.5, "… and ends under the new one");
            window.Close();
            config.HideToolChevrons = before;
            Check(Source("UI/AnnotationToolbar.xaml.cs").Contains("RaiseHoverPopup(); // above the bar's other windows"), "the list's window comes to the top when shown (not under the settings row)");
            Check(Source("UI/AnnotationToolbar.xaml").Contains("Background=\"{DynamicResource T_FF1E1E1E}\"") && !Source("App.xaml").Contains("#F21C1C1C") &&
                  Source("UI/FloatButtonWindow.cs").Contains("Theme.Key(0xFF1E1E1E)"), "tool bars are opaque");
        }

        // ---- the text recognition window: plated actions, outlined switches; 公式 only for mathematics
        {
            Check(TranslationWindow.LooksLikeFormula("x = (-b ± √(b²-4ac))/(2a)") && TranslationWindow.LooksLikeFormula("E = mc² + ∑ aᵢ"),
                "a formula is recognised as one");
            Check(!TranslationWindow.LooksLikeFormula("今天下午三点在会议室开会，请大家准时参加。") && !TranslationWindow.LooksLikeFormula("Hello world, this is a sentence.") &&
                  !TranslationWindow.LooksLikeFormula("产品\t单价\n键盘\t199"), "ordinary text and tables are not (no 公式 button)");
            var xaml = Source("UI/TranslationWindow.xaml");
            Check(xaml.Contains("<ToggleButton x:Name=\"FormulaButton\"") && xaml.Contains("<Setter Property=\"Background\" Value=\"{DynamicResource T_22FFFFFF}\" />") &&
                  xaml.Contains("x:Name=\"Dot\""), "actions have a plate, switches an outline and a dot");
        }

        // ---- small items
        {
            Check(new AppConfig().DetectRangeRemember && Source("App.xaml.cs").Contains("Config.DetectRange = overlay.RangeAtStart; // only for that capture") &&
                  Source("UI/SettingsWindow.xaml").Contains("x:Name=\"DetectRememberCheck\""), "识别范围: a switch for remembering the wheel's choice");
            Check(Source("UI/OverlayWindow.Selection.cs").Contains("WheelTip.SizeChanged += (_, e) => PlaceWheelTip(e.NewSize);"), "the wheel's tip is centred on its real size");
            Check(Source("UI/SettingsWindow.xaml").Contains("Content=\"识别与保存\"") && !Source("UI/SettingsWindow.xaml").Contains("保存与识别"), "settings: 识别与保存");
            Check(Source("UI/FloatButtonWindow.cs").Contains("AfterFrames(2, () =>"), "the floating button comes back without its old picture flashing");
            Check(Source("App.xaml.cs").Contains("SHChangeNotify(0x08000000") && Source("installer/Markshot.iss").Contains("ie4uinit.exe"), "a new version refreshes Windows' icon cache (Task Manager's logo)");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
