using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markshot.Core;
using Markshot.UI;

static class SettingsShot
{
    public static void Run()
    {
        var config = new AppConfig();
        config.Translation.ApiKey = "sk-demo-not-real-0000000000";
        var w = new SettingsWindow(config)
        {
            Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false, Topmost = false
        };
        w.Show();
        foreach (var page in new[] { "Hotkeys", "Translate", "General" })
        {
            w.ShowPage(page);
            w.UpdateLayout();
            var root = (FrameworkElement)w.Content;
            var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var f = File.Create($"settings_{page}.png");
            enc.Save(f);
            Console.WriteLine($"settings_{page}.png {root.ActualWidth}x{root.ActualHeight}");
        }

        w.Close();
    }
}
