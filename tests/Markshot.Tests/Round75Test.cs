using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.75: tool sizes kept between captures; an element framed where the picture shows it.</summary>
static class Round75Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- a tool's size is kept for the next capture (a new toolbar)
        {
            var config = Markshot.App.Config;
            var saved = new Dictionary<string, double>(config.ToolSizes);
            var bar = new AnnotationToolbar();
            bar.SetTool(Markshot.Annotations.AnnotationTool.Pen);
            ((System.Windows.Controls.Slider)bar.FindName("WidthSlider")).Value = 11;
            bar.SetTool(Markshot.Annotations.AnnotationTool.Arrow);
            bar.SetTool(Markshot.Annotations.AnnotationTool.Pen);
            Check(Math.Abs(bar.Thickness - 11) < 0.01, $"switching away and back keeps the pen's size ({bar.Thickness})");
            var next = new AnnotationToolbar();
            next.SetTool(Markshot.Annotations.AnnotationTool.Pen);
            Check(Math.Abs(next.Thickness - 11) < 0.01 && Math.Abs(config.ToolSizes["Pen"] - 11) < 0.01, $"the next capture starts with it too ({next.Thickness}), saved in the settings");
            next.SetTool(Markshot.Annotations.AnnotationTool.Arrow);
            Check(Math.Abs(next.Thickness - 11) > 0.01, "other tools keep their own sizes");
            config.ToolSizes = saved;
        }

        // ---- an element reported off its place is framed where the picture shows it
        {
            var snapped = (Rect)typeof(OverlayWindow).GetMethod("Snapped", Any)!.Invoke(null, new object[]
            {
                new Rect(57, 45, 1280, 720),
                new List<Int32Rect> { new(110, 115, 1294, 728), new(0, 0, 1516, 983) }
            })!;
            Check(snapped == new Rect(110, 115, 1294, 728), $"a player's video reported at the window's corner: framed where it is shown ({snapped})");
            var kept = (Rect)typeof(OverlayWindow).GetMethod("Snapped", Any)!.Invoke(null, new object[]
            {
                new Rect(100, 100, 300, 200),
                new List<Int32Rect> { new(102, 101, 298, 199), new(400, 100, 300, 200) }
            })!;
            Check(kept == new Rect(100, 100, 300, 200), "an element where the picture shows it stays (a block of its size elsewhere does not move it)");
            var misplaced = typeof(OverlayWindow).GetMethod("Misplaced", Any)!;
            Check((bool)misplaced.Invoke(null, new object[] { new Rect(57, 45, 1280, 720), new Rect(57, 45, 1440, 900) })!,
                "an element reported at the window's very corner (its title bar), smaller than the window: not offered");
            Check(!(bool)misplaced.Invoke(null, new object[] { new Rect(57, 45, 1440, 30), new Rect(57, 45, 1440, 900) })! &&
                  !(bool)misplaced.Invoke(null, new object[] { new Rect(110, 115, 1294, 728), new Rect(57, 45, 1440, 900) })!,
                "… a title bar across the window, or an element inside it, still is");
            var alone = (Rect)typeof(OverlayWindow).GetMethod("Snapped", Any)!.Invoke(null, new object[] { new Rect(10, 10, 500, 400), new List<Int32Rect> { new(50, 50, 200, 100) } })!;
            Check(alone == new Rect(10, 10, 500, 400), "no block of its size: as reported");

            if (Environment.GetEnvironmentVariable("R75IMAGE") is { } path && File.Exists(path))
            {
                var image = new FormatConvertedBitmap(new BitmapImage(new Uri(path)), PixelFormats.Bgra32, null, 0);
                var px = new byte[image.PixelWidth * image.PixelHeight * 4];
                image.CopyPixels(px, image.PixelWidth * 4, 0);
                var segmenter = new ScreenSegmenter(px, image.PixelWidth, image.PixelHeight, 1, 3);
                var region = new Int32Rect(57, 45, image.PixelWidth - 80, image.PixelHeight - 60);
                var pieces = segmenter.At(region, 693, 418);
                Console.WriteLine("  pieces: " + string.Join(" | ", pieces.Select(p => $"{p.X},{p.Y} {p.Width}x{p.Height}")));
            }
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
