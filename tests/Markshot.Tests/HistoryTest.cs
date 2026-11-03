using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

static class HistoryTest
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }

    static BitmapSource Picture(Color c, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    public static void Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var history = new PinHistory();
        for (var i = 0; i < 35; i++) history.Add(new PinEntry { Image = Picture(Color.FromRgb((byte)(i * 7), 120, 200), 200 + i, 120), Created = DateTime.Now.AddMinutes(-i) });
        Check(history.Items.Count == 30, "keeps the last 30 pins");
        PinEntry restored = null, deleted = null;
        var window = new PinHistoryWindow(() => history.Items, e => { restored = e; history.Remove(e); }, e => { deleted = e; history.Remove(e); }, history.Clear)
        { Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        Pump(300);
        var tiles = FindTiles(window);
        Check(tiles.Count == 30, $"30 thumbnails ({tiles.Count})");
        Check(ReferenceEquals(tiles[0].Tag ?? FirstEntry(tiles[0], history), FirstEntry(tiles[0], history)), "newest first");
        var newest = history.Items.OrderByDescending(e => e.Created).First();
        tiles[0].RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Pump(100);
        Check(ReferenceEquals(restored, newest) && FindTiles(window).Count == 29, "click pins the newest again and it leaves the list");
        var root = (FrameworkElement)window.Content;
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create("pin_history.png")) enc.Save(f);
        window.Close();
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static object FirstEntry(Border tile, PinHistory history) => tile.Tag;

    static List<Border> FindTiles(DependencyObject root)
    {
        var found = new List<Border>();
        void Walk(DependencyObject d)
        {
            if (d is WrapPanel wrap) { found.AddRange(wrap.Children.OfType<Border>()); return; }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return found;
    }
}
