using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

/// <summary>By hand: how long the steps of opening a capture take.</summary>
static class CaptureProbe
{
    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        for (var i = 0; i < 3; i++)
        {
            var w = Stopwatch.StartNew();
            var bar = new AnnotationToolbar();
            var built = w.ElapsedMilliseconds;
            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bar.Arrange(new Rect(bar.DesiredSize));
            Console.WriteLine($"toolbar alone: built {built} ms, laid out {w.ElapsedMilliseconds - built} ms");
        }

        for (var i = 0; i < 3; i++)
        {
            var (img, b) = ScreenCapture.CaptureVirtualScreen();
            var w = Stopwatch.StartNew();
            var plain = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Left = -12000, Top = -12000,
                Width = b.Width / 1.5, Height = b.Height / 1.5, Content = i == 2 ? new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.Black } : new System.Windows.Controls.Image { Source = img, Stretch = System.Windows.Media.Stretch.Fill } };
            if (i == 2) Console.Write("(no picture) ");
            if (i == 1)
            {
                var e0 = w.ElapsedMilliseconds;
                new System.Windows.Interop.WindowInteropHelper(plain).EnsureHandle();
                Console.Write($"(handle made beforehand: {w.ElapsedMilliseconds - e0} ms; then from Show) ");
                w.Restart();
            }

            long layoutAt = 0;
            plain.LayoutUpdated += (_, _) => { if (layoutAt == 0) layoutAt = w.ElapsedMilliseconds; };
            plain.ContentRendered += (_, _) => { Console.WriteLine($"plain window: layout {layoutAt} ms, rendered {w.ElapsedMilliseconds} ms"); plain.Dispatcher.BeginInvoke(plain.Close); };
            plain.ShowDialog();
        }

        for (var round = 1; round <= 3; round++)
        {
            var watch = Stopwatch.StartNew();
            var (image, bounds) = ScreenCapture.CaptureVirtualScreen();
            var shot = watch.ElapsedMilliseconds;
            var cursor = CursorCapture.Grab(bounds.X, bounds.Y);
            var active = WindowDetector.ActiveProgram();
            var grab = watch.ElapsedMilliseconds;
            // Off the screen (same size picture): nothing shows on the desktop. Rounds 2 and 3: a spare made beforehand.
            OverlayWindow overlay;
            if (round == 1)
            {
                overlay = new OverlayWindow(image, new Int32Rect(-12000, -12000, bounds.Width, bounds.Height));
            }
            else
            {
                var s0 = Stopwatch.StartNew();
                overlay = OverlayWindow.CreateSpare();
                Console.Write($"(spare made beforehand in {s0.ElapsedMilliseconds} ms) ");
                watch.Restart();
                grab = 0; shot = 0;
                overlay.Load(image, new Int32Rect(-12000, -12000, bounds.Width, bounds.Height));
            }
            var root = (FrameworkElement)overlay.Content;
            var m0 = watch.ElapsedMilliseconds;
            foreach (UIElement child in ((System.Windows.Controls.Panel)root).Children)
            {
                var c0 = watch.ElapsedMilliseconds;
                child.Measure(new Size(2426, 1280));
                var c1 = watch.ElapsedMilliseconds - c0;
                if (c1 > 3) Console.WriteLine($"   measure {(child as FrameworkElement)?.Name ?? child.GetType().Name}: {c1} ms");
            }
            Console.WriteLine($"   root children measured: {watch.ElapsedMilliseconds - m0} ms");
            var made = watch.ElapsedMilliseconds;
            long rendered = 0, loaded = 0, firstFrame = 0;
            overlay.Loaded += (_, _) => loaded = watch.ElapsedMilliseconds;
            long source = 0, layout = 0;
            overlay.SourceInitialized += (_, _) => source = watch.ElapsedMilliseconds;
            overlay.LayoutUpdated += (_, _) => { if (layout == 0) layout = watch.ElapsedMilliseconds; };
            EventHandler? tick = null;
            tick = (_, _) => { if (firstFrame == 0 && loaded > 0) { firstFrame = watch.ElapsedMilliseconds; System.Windows.Media.CompositionTarget.Rendering -= tick; } };
            System.Windows.Media.CompositionTarget.Rendering += tick;
            overlay.ContentRendered += (_, _) =>
            {
                rendered = watch.ElapsedMilliseconds;
                overlay.Dispatcher.BeginInvoke(overlay.Close, DispatcherPriority.Background);
            };
            overlay.ShowDialog();
            Console.WriteLine($"round {round}: screenshot {shot} ms, cursor+active {grab - shot} ms, window built {made - grab} ms, source +{source - made}, layout +{layout - source}, loaded +{loaded - layout} ms, first frame +{firstFrame - loaded} ms, content rendered +{rendered - firstFrame} ms, total {rendered} ms ({bounds.Width}x{bounds.Height})");
        }
    }
}
