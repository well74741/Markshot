using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.69: long screenshots of smoothly scrolled pages, the capture window's size, the floating button over popups, the long screenshot bar, window detection.</summary>
static class Round69Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file)).Replace("\r\n", "\n");

    /// <summary>A page like text: dark word blocks on light lines, on a slight gradient.</summary>
    static double[,] Page(int width, int height, int seed)
    {
        var random = new Random(seed);
        var page = new double[height, width];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            page[y, x] = 235 - y % 300 * 0.05;
        for (var line = 4; line + 12 < height; line += 18)
        {
            var x = 4 + random.Next(10);
            while (x < width - 10)
            {
                var word = 8 + random.Next(30);
                var shade = 30 + random.Next(120);
                var tall = 10 + random.Next(3);
                for (var y = line; y < line + tall; y++)
                for (var i = x; i < Math.Min(width - 4, x + word); i++)
                    page[y, i] = shade;
                x += word + 5 + random.Next(8);
            }
        }

        return page;
    }

    /// <summary>
    /// A frame of the window: a fixed side panel on the left, the page on the right scrolled by a fractional number of
    /// pixels (rows blended, as smooth scrolling draws them).
    /// </summary>
    static byte[] Frame(double[,] page, double[,] side, int width, int height, int sideWidth, double offset)
    {
        var px = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var at = y + offset;
            var low = (int)Math.Floor(at);
            var t = at - low;
            for (var x = 0; x < width; x++)
            {
                var value = x < sideWidth ? side[y, x] : page[low, x - sideWidth] * (1 - t) + page[low + 1, x - sideWidth] * t;
                var b = (byte)Math.Round(value);
                var i = (y * width + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = b;
                px[i + 3] = 255;
            }
        }

        return px;
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- a page scrolled smoothly (fractional pixels) next to a fixed side panel
        {
            const int width = 320, height = 260, sideWidth = 90;
            var page = Page(width - sideWidth, 2000, 7);
            var side = Page(sideWidth, height, 11);
            foreach (var offsets in new[] { new[] { 0, 37.5, 81.25, 118.75, 160.5, 203.25 }, new[] { 0, 7.5, 15.25, 22.75, 30.5, 38.25, 46, 53.75 } })
            {
                var stitcher = new ScrollStitcher(width, height);
                foreach (var offset in offsets)
                {
                    stitcher.Add(Frame(page, side, width, height, sideWidth, offset));
                }

                var steps = string.Join(", ", offsets.Skip(1).Select(o => o.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Check(stitcher.PlacedFrames == offsets.Length - 1 && stitcher.LostFrames == 0, $"scrolled by fractions of a pixel ({steps}): every frame stitched ({stitcher.PlacedFrames} placed, {stitcher.LostFrames} lost)");
                Check(Math.Abs(stitcher.Height - (height + offsets[^1])) <= 1, $"… in its place, no drift ({stitcher.Height} rows for {height + offsets[^1]})");
                var (left, kept) = stitcher.KeptColumns();
                Check(left >= sideWidth - 8 && kept <= width - sideWidth + 8, $"… the fixed side panel left out ({left}, {kept})");
            }

            Check(Source("Core/ScrollStitcher.cs").Contains("var shift = FindShift(_last, cells, out var matched);") && Source("Core/ScrollStitcher.cs").Contains("shift ??= FindShiftLoosely(_last, cells, out matched);"), "an exact match first, then a tolerant one");
        }

        // ---- the capture window made in advance takes the screen's size before it shows (no black corner)
        {
            var spare = OverlayWindow.CreateSpare();
            var screen = System.Windows.Media.Imaging.BitmapSource.Create(64, 48, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, new byte[64 * 48 * 4], 64 * 4);
            spare.Load(screen, new Int32Rect(-6000, -6000, 64, 48));
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(spare);
            Check(Math.Abs(spare.Width - 64 / dpi.DpiScaleX) < 0.5 && Math.Abs(spare.Height - 48 / dpi.DpiScaleY) < 0.5 && !spare.IsVisible,
                $"loaded: the window has the screen's size already ({spare.Width}x{spare.Height}), still hidden");
            spare.Close();
        }

        // ---- the floating button: back on top as soon as another program's popup shows
        {
            var code = Source("UI/FloatButtonWindow.cs");
            Check(code.Contains("SetWinEventHook(EventObjectShow, EventObjectShow") && code.Contains("UnhookWinEvent("), "it hears windows being shown (and lets go when closed)");
            var show = code[code.IndexOf("private void WinEventShow", StringComparison.Ordinal)..];
            show = show[..show.IndexOf("\n    }", StringComparison.Ordinal)];
            Check(show.Contains("KeepOnTop") && show.Contains("DispatcherPriority.Input"), "… and comes back on top right away");
        }

        // ---- the long screenshot bar: the annotation bar's look, outside the area
        {
            var scroll = Source("UI/ScrollCaptureSession.cs");
            var bar = scroll[scroll.IndexOf("private sealed class ScrollBarWindow", StringComparison.Ordinal)..];
            Check(bar.Contains("AllowsTransparency = true") && bar.Contains("Theme.Key(0xFF1E1E1E)") && bar.Contains("CornerRadius = new CornerRadius(9)") &&
                  bar.Contains("BlurEffect") && !bar.Contains("RoundedWindow.Apply"), "the same plate, corners and inner shade as the annotation bar");
            Check(bar.Contains("Px(rect, 10)"), "placed outside the area, a little away (not in the picture)");
        }

        // ---- window detection: a shadow / border window around the real one is looked through
        {
            var code = Source("Core/WindowDetector.cs");
            Check(code.Contains("private static bool IsWrapper(IntPtr handle)") && code.Contains("wrapper = IsWrapper(handle);"), "frame windows without a title (shadows) are recognised");
        }

        // ---- lighter in the background
        {
            var app = Source("App.xaml.cs");
            Check(app.Contains("300L * 1024 * 1024") && app.Contains("TrimMemorySoon();") && app.Contains("GCLargeObjectHeapCompactionMode.CompactOnce"),
                "closed pins kept within 300 MB; memory given back after a capture");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
