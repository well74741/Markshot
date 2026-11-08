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
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.62: a blurred mosaic's soft edge, the pin shadow after 抠除, the floating button (on by default, click action, order sync, pin fades).</summary>
static class Round62Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file));

    static byte[] Render(Annotation item, int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) item.Draw(dc);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var px = new byte[w * h * 4];
        bitmap.CopyPixels(px, w * 4, 0);
        return px;
    }

    static BitmapSource Grey(int w, int h)
    {
        var px = Enumerable.Repeat((byte)200, w * h * 4).ToArray();
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    static byte Alpha(byte[] px, int w, int x, int y) => px[(y * w + x) * 4 + 3];

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- mosaic: with blur the edge fades out (not cut by the box or the brush)
        {
            var pattern = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 90, 90, 90, 255 }, 4);
            pattern.Freeze();
            var hard = new MosaicAnnotation { Rect = new Rect(20, 20, 60, 60), Pattern = pattern, PatternRect = new Rect(0, 0, 100, 100) };
            var soft = new MosaicAnnotation { Rect = new Rect(20, 20, 60, 60), Pattern = pattern, PatternRect = new Rect(0, 0, 100, 100), Blur = 12, Gaussian = true, Feather = 18 }; // (0.8.63: the soft edge is 高斯模糊's 柔边)
            var h = Render(hard, 100, 100);
            var s = Render(soft, 100, 100);
            Check(Alpha(h, 100, 84, 50) == 0 && Alpha(h, 100, 78, 50) == 255, "without blur the mosaic box keeps its hard edge");
            Check(Alpha(s, 100, 50, 50) > 245, "with blur its middle is solid");
            var inside = Alpha(s, 100, 78, 50);
            var outside = Alpha(s, 100, 84, 50);
            // (0.8.64: whole up to the box line, fading out beyond it)
            Check(inside > 245 && outside > 5 && outside < inside, $"… and its edge fades out past the box line (in {inside}, out {outside})");

            var brush = new MosaicBrushAnnotation { Thickness = 20, Pattern = pattern, PatternRect = new Rect(0, 0, 100, 100), Blur = 10, Gaussian = true, Feather = 15 };
            brush.Points.Add(new Point(10, 50));
            brush.Points.Add(new Point(90, 50));
            var b = Render(brush, 100, 100);
            Check(Alpha(b, 100, 50, 50) > 245 && Alpha(b, 100, 50, 62) > 5 && Alpha(b, 100, 50, 62) < Alpha(b, 100, 50, 57), "a blurred mosaic stroke fades out past its edge too");
        }

        // ---- pin: after 抠除 the picture is not clipped any more (that clip cut the shadow away)
        {
            Check(typeof(AnnotationEditor).GetMethod("ReleaseCutoutTarget") != null, "the editor lets go of the cutout clip");
            var exit = Source("UI/PinWindow.Editing.cs");
            Check(exit.Contains("Editor.ReleaseCutoutTarget();") && exit.Contains("UpdateFrame();"), "leaving edit mode drops the clip and redraws the shadow");
        }

        // ---- the floating button is on until the user chooses
        {
            var fresh = new AppConfig();
            fresh.EnsureDefaults();
            Check(fresh.FloatButtonEnabled && !fresh.FloatButtonChosen, "a fresh install has the floating button on");
            var old = new AppConfig { FloatButtonEnabled = false };
            old.EnsureDefaults();
            Check(old.FloatButtonEnabled, "an old config (never chosen) gets it on too");
            var off = new AppConfig { FloatButtonEnabled = false, FloatButtonChosen = true };
            off.EnsureDefaults();
            Check(!off.FloatButtonEnabled, "once turned off by the user it stays off");
            Check(fresh.FloatClickAction == "Capture", "the click does 截屏 by default");
        }

        // ---- the floating button: 显示贴图 keeps the row open; the click runs the chosen action
        {
            var ran = "";
            var config = new AppConfig { FloatActionHidden = "", FloatClickAction = "Ocr" };
            var button = new FloatButtonWindow(config, key => ran = key, () => { }, () => { }) { FullscreenOverride = () => false, PointerOnItOverride = () => true };
            button.Show();
            Pump(200);
            button.GetType().GetMethod("Expand", Any)!.Invoke(button, null);
            Pump(250);
            button.GetType().GetMethod("Run", Any)!.Invoke(button, new object[] { "TogglePins" });
            Pump(150);
            Check(ran == "TogglePins" && button.IsExpanded, "显示贴图 runs and the row stays open");
            button.GetType().GetMethod("Run", Any)!.Invoke(button, new object[] { "Record" });
            Pump(150);
            Check(!button.IsExpanded, "other buttons still fold the row");
            var source = Source("UI/FloatButtonWindow.cs");
            Check(source.Contains("Run(Actions.Contains(_config.FloatClickAction)"), "a click runs the action set in 设置");
            button.Close();
        }

        // ---- settings: the 点击行为 track; a hidden button may be chosen; the choice follows its button
        {
            var config = new AppConfig { FloatActionOrder = "Capture,Record,Ocr,Editor,History,AddPin,TogglePins", FloatActionHidden = "TogglePins" };
            var settings = new SettingsWindow(config) { Left = -4000, Top = -4000, ShowActivated = false };
            settings.Show();
            Pump(250);
            var track = (Canvas)settings.FindName("FloatClickTrack");
            var row = (StackPanel)settings.FindName("FloatActionsRow");
            Check(Math.Abs(track.Width - row.Children.Count * 28) < 0.5, "the track is as long as the row");
            Check((string)settings.GetType().GetProperty("FloatClickChoice", Any)!.GetValue(settings)! == "Capture", "it starts on 截屏");
            var plate = (Border)settings.FindName("FloatClickPlate");
            Check(plate.Background is SolidColorBrush { Color.A: > 0 } && Math.Abs(Canvas.GetLeft(plate)) < 0.5, "the chosen button has a coloured plate (0.8.63: one plate under the row)");
            settings.GetType().GetMethod("ChooseClickAt", Any)!.Invoke(settings, new object[] { 6 * 28 + 10.0 });
            Pump(50);
            Check((string)settings.GetType().GetProperty("FloatClickChoice", Any)!.GetValue(settings)! == "TogglePins", "a hidden (grey) button can be chosen");
            Check(settings.HasUnsavedChanges, "a new click action is a change to save");

            // the floating button reordered: the row follows, sliding, the choice stays on its button
            config.FloatActionOrder = "TogglePins,Capture,Record,Ocr,Editor,History,AddPin";
            settings.SyncFloatOrder();
            Pump(30);
            var keys = row.Children.OfType<FrameworkElement>().Select(t => (string)t.Tag).ToList();
            Check(keys[0] == "TogglePins" && keys[1] == "Capture", "the settings row takes the floating button's order");
            Check(row.Children.OfType<FrameworkElement>().Any(t => t.RenderTransform is TranslateTransform { HasAnimatedProperties: true }), "… with the swap slide");
            Pump(400);
            Check(Math.Abs(Canvas.GetLeft((Border)settings.FindName("FloatClickPlate"))) < 0.5, "the plate moved with the chosen button");
            settings.GetType().GetMethod("Save_Click", Any)?.Invoke(settings, new object?[] { settings, new RoutedEventArgs() });
            Pump(100);
            Check(config.FloatClickAction == "TogglePins", "保存 keeps the click action");
            if (settings.IsVisible) settings.Close();
        }

        // ---- pins hide and show with a fade (not in one frame)
        {
            var app = Source("App.xaml.cs");
            Check(app.Contains("pin.FadeHide();") && app.Contains("pin.FadeShow();") && !app.Contains("_hiddenPins.Add(pin);\n                pin.Hide();"), "显示贴图 fades the pins out / in");
            var pin = new PinWindow(Grey(160, 100), -3000, -3000, Grey(160, 100), null) { Left = -3000, Top = -3000, ShowActivated = false };
            pin.Show();
            Pump(400);
            pin.FadeHide();
            Check(pin.IsVisible && pin.HasAnimatedProperties, "it fades (still shown while the fade runs)");
            Pump(400);
            Check(!pin.IsVisible, "then gone");
            pin.FadeShow();
            Pump(400);
            Check(pin.IsVisible && pin.Opacity > 0.99, "and back in");
            pin.Close();
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
