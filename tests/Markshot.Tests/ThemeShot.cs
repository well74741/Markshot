using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

static class ThemeShot
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }

    static void Shot(FrameworkElement root, string file)
    {
        root.UpdateLayout();
        var bmp = new RenderTargetBitmap(Math.Max(1, (int)root.ActualWidth), Math.Max(1, (int)root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(file);
        enc.Save(f);
    }

    static Color Pixel(FrameworkElement root, int x, int y)
    {
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    public static void Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Theme.Apply(ThemeMode.Dark);
        var settings = new SettingsWindow(new AppConfig()) { Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false };
        settings.Show();
        settings.ShowPage("Style");
        Pump(400);
        var root = (FrameworkElement)settings.Content;
        var dark = Pixel(root, 400, 300);
        Shot(root, "theme_dark_style.png");
        Theme.Apply(ThemeMode.Light);
        Pump(200);
        var light = Pixel(root, 400, 300);
        Check(dark.R < 60 && light.R > 200, $"settings follow the theme live: {dark} -> {light}");
        Shot(root, "theme_light_style.png");
        settings.ShowPage("Hotkeys"); Pump(300); Shot(root, "theme_light_hotkeys.png");
        var w0 = settings.Width;
        UiScale.Apply("Large");
        Pump(300);
        Check(Math.Abs(settings.Width - w0 * UiScale.Large) < 1, $"large size grows the window {w0} -> {settings.Width}");
        Shot((FrameworkElement)settings.Content, "theme_light_large.png");
        UiScale.Apply("Medium");
        settings.Close();

        var bar = new AnnotationToolbar();
        var win = new Window { Content = bar, SizeToContent = SizeToContent.WidthAndHeight, Left = -4000, Top = -4000, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Background = Brushes.Gray };
        win.Show();
        bar.SetTool(Markshot.Annotations.AnnotationTool.Text, notify: false);
        Pump(300);
        Shot(bar, "theme_light_toolbar.png");
        var mediumWidth = bar.ActualWidth;
        UiScale.Apply("Large");
        Pump(300);
        Check(bar.ActualWidth > mediumWidth * 1.1, $"toolbar is larger: {mediumWidth:0} -> {bar.ActualWidth:0}");
        UiScale.Apply("Medium");
        Theme.Apply(ThemeMode.Dark);
        Pump(200);
        Shot(bar, "theme_dark_toolbar.png");
        win.Close();
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
