using System;
using System.Collections.Generic;
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
using Markshot.UI;

/// <summary>0.8.61: settings changed only when they are, one darkness for 聚焦, mosaic blur, 抠除 layers, the editor, the floating button.</summary>
static class Round61Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file));
    static object? Get(object t, string name)
    {
        for (var type = t.GetType(); type != null; type = type.BaseType)
        {
            if (type.GetField(name, Any | BindingFlags.DeclaredOnly) is { } f) return f.GetValue(t);
            if (type.GetProperty(name, Any | BindingFlags.DeclaredOnly) is { } p) return p.GetValue(t);
        }

        throw new MissingFieldException(name);
    }

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

    static BitmapSource Grey(int w, int h)
    {
        var px = Enumerable.Repeat((byte)200, w * h * 4).ToArray();
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- settings: only a real change counts
        {
            var settings = new SettingsWindow(new AppConfig()) { Left = -4000, Top = -4000, ShowActivated = false };
            settings.Show();
            Pump(300);
            foreach (var bar in FindAll<ScrollBar>(settings))
            {
                bar.Value = bar.Maximum / 2; // scrolling through the pages
            }

            Check(!settings.HasUnsavedChanges, "scrolling through the settings is no change");
            var box = (CheckBox)settings.FindName("EscExitsCheck");
            box.IsChecked = !box.IsChecked;
            Check(settings.HasUnsavedChanges, "ticking a box is");
            box.IsChecked = !box.IsChecked;
            Check(!settings.HasUnsavedChanges, "and ticking it back is none again");
            Check(((FrameworkElement)settings.FindName("DrawerCoffee")).Width == 24, "the coffee cup is smaller");
            var drawer = (FrameworkElement)settings.FindName("DrawerPanel");
            drawer.Height = 144;
            settings.GetType().GetMethod("PutDrawerAway", Any)!.Invoke(settings, null);
            Pump(400);
            Check(drawer.ActualHeight < 1, "the QR code goes back in on a click elsewhere");
            Check(Source("UI/SettingsWindow.xaml.cs").Contains("PreviewMouseDown += PutDrawerAwayOnOutsideClick;"), "… wired to every click outside it");
            settings.Close();
            var update = Source("UI/UpdateWindow.cs");
            Check(update.Contains("new StackPanel { Width = 360 }") && update.Contains("_install.Opacity = 0.45;"), "the update window keeps its width; 立即更新 greys while it works");
        }

        // ---- 聚焦: one darkness for the picture, the slider works on all of them at once
        {
            var pin = new PinWindow(Grey(200, 120), -4000, -4000, Grey(200, 120), null) { Left = -4000, Top = -4000, ShowActivated = false };
            pin.Show();
            Pump(150);
            pin.GetType().GetMethod("EnterEditMode", Any)!.Invoke(pin, null);
            var editor = (AnnotationEditor)Get(pin, "Editor")!;
            var a = new SpotlightAnnotation { Rect = new Rect(10, 10, 40, 40), Darkness = 0.55 };
            var b = new SpotlightAnnotation { Rect = new Rect(100, 10, 40, 40), Darkness = 0.8 };
            editor.Items.Add(a);
            editor.Items.Add(b);
            var toolbar = (AnnotationToolbar)Get(pin, "Toolbar")!;
            toolbar.SetTool(AnnotationTool.Spotlight);
            ((Slider)toolbar.FindName("DarknessSlider")).Value = 20;
            Check(Math.Abs(a.Darkness - 0.2) < 0.001 && Math.Abs(b.Darkness - 0.2) < 0.001, $"the darkness slider sets every spotlight, none selected ({a.Darkness:0.00}, {b.Darkness:0.00})");
            pin.Close();
        }

        // ---- mosaic blur
        {
            var source = Stripes(64, 64);
            var (hard, _) = MosaicBuilder.BuildCanvas(source, 4);
            var (soft, area) = MosaicBuilder.BuildCanvas(source, 4, 6);
            double Spread(BitmapSource bmp)
            {
                var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
                bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
                var values = Enumerable.Range(0, px.Length / 4).Select(i => (double)px[i * 4]).ToList();
                var mean = values.Average();
                return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
            }

            Check(soft != null && area.Width >= 64 && Spread(soft) < Spread(Stripes(64, 64)) * 0.5, "the blur softens the mosaic");
            var bar = new AnnotationToolbar();
            bar.SetTool(AnnotationTool.Mosaic);
            // (0.8.63: 高斯模糊 is a mosaic type of its own; its 程度 shows once it is chosen)
            bar.GetType().GetMethod("MosaicKind_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(bar, new object[] { bar.FindName("KindGaussianToggle"), new RoutedEventArgs() });
            Check(((FrameworkElement)bar.FindName("BlurGroup")).Visibility == Visibility.Visible, "the mosaic row has 模糊");
            ((Slider)bar.FindName("BlurSlider")).Value = 8;
            Check(Math.Abs(bar.MosaicBlur - 8) < 0.01, "… and it sets the blur");
        }

        // ---- 抠除 cuts what lies under it, not what is drawn later
        {
            var below = new RectAnnotation { Rect = new Rect(10, 10, 80, 80), Fill = true, Color = Colors.Red };
            var cut = new CutoutAnnotation { Thickness = 20 };
            cut.Points.Add(new Point(50, 0));
            cut.Points.Add(new Point(50, 100));
            var above = new RectAnnotation { Rect = new Rect(40, 40, 20, 20), Fill = true, Color = Colors.Blue };
            var layers = AnnotationEditor.Layered(new List<Annotation> { below, cut, above }).ToList();
            Check(layers[0].Above != null && !layers[0].Above!.FillContains(new Point(50, 50)) && layers[2].Above == null,
                "the element below the hole is cut there; the one drawn after it is not");
        }

        // ---- the editor: its title shows the version; 元素样式 eases; the floating button's 显示贴图
        {
            var editor = new ImageEditorWindow(Grey(200, 120)) { Left = -4000, Top = -4000, ShowActivated = false };
            editor.Show();
            Pump(300);
            Check(editor.Title.Contains(AppVersion.Display.Split('+')[0]), $"the editor's title shows the version ({editor.Title})");
            var rect = new RectAnnotation { Rect = new Rect(10, 10, 40, 30) };
            ((AnnotationEditor)Get(editor, "Editor")!).Items.Add(rect);
            var section = (FrameworkElement)Get(editor, "_styleSection")!;
            editor.SelectElements(new Annotation[] { rect });
            Pump(30);
            var easing = section.HasAnimatedProperties;
            Pump(400);
            Check(easing && !section.HasAnimatedProperties, "元素样式 slides to its new size when another kind is selected");
            editor.CloseWithoutAsking();

            var config = new AppConfig();
            Check(ToolbarCatalog_Set(config.FloatActionHidden).Contains("TogglePins") && config.FloatActionOrder.Contains("TogglePins"), "显示贴图 is in the row, off until turned on");
            var shown = true;
            var button = new FloatButtonWindow(new AppConfig { FloatActionHidden = "" }, _ => { }, () => { }, () => { }) { FullscreenOverride = () => false, PinsShown = () => shown };
            button.Show();
            Pump(200);
            shown = false;
            button.RefreshPinsButton();
            var item = ((StackPanel)Get(button, "_items")!).Children.OfType<FrameworkElement>().First(e => (string)e.Tag == "TogglePins");
            Check(item.Opacity < 0.5, "pins all hidden: 显示贴图 dims");
            button.Close();
            Check(Source("App.xaml.cs").Contains("public void TogglePinsShown()"), "显示贴图 hides / shows every pin");
            Check(Source("UI/OverlayWindow.Finish.cs").Contains("FinishWithTransition(() => CloseWith(CaptureAction.Scroll, _screen), area"), "长截图: the screen clears from the area outwards");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static HashSet<string> ToolbarCatalog_Set(string text) => new(text.Split(',', StringSplitOptions.RemoveEmptyEntries));

    static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var deeper in FindAll<T>(child)) yield return deeper;
        }
    }
}
