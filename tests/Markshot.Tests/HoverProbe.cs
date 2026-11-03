using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

// Hover detection across processes: another process shows a window, the capture window's hover finds its parts.
static class HoverProbe
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }

    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>The other process: a window with a button, a line of text and a picture; writes their screen rectangles.</summary>
    public static void Host(string file)
    {
        var button = new Button { Content = "确定", Width = 120, Height = 40, Margin = new Thickness(30, 30, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var text = new TextBlock { Text = "独立的一段文字", Margin = new Thickness(30, 20, 0, 0), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Left };
        var image = new Image { Width = 80, Height = 60, Margin = new Thickness(30, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8) };
        var window = new Window
        {
            Title = "HoverHost", Left = 60, Top = 60, Width = 380, Height = 300, ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            Content = new StackPanel { Children = { button, text, image } }
        };
        window.ContentRendered += (_, _) =>
        {
            string R(FrameworkElement e) { var p = e.PointToScreen(new Point(0, 0)); var q = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight)); return $"{p.X},{p.Y},{q.X - p.X},{q.Y - p.Y}"; }
            File.WriteAllText(file, string.Join("\n", R(button), R(text), R(image), R(window)));
        };
        window.Show();
        var until = DateTime.Now.AddSeconds(25);
        while (DateTime.Now < until && (!File.Exists(file) || new FileInfo(file).Length == 0 ? DateTime.Now < until : File.Exists(file)))
        {
            Pump(100);
            if (File.Exists(file) && new FileInfo(file).Length > 0) break;
        }

        while (DateTime.Now < until && File.Exists(file)) Pump(100); // the probe deletes it when done
        window.Close();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    public static void Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); // as in mouse events
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "markshot-hoverhost.txt");
        File.Delete(file);
        var host = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "hoverhost \"" + file + "\"") { UseShellExecute = false });
        var wait = DateTime.Now.AddSeconds(15);
        while (!File.Exists(file) && DateTime.Now < wait) Pump(100);
        Pump(500);
        var rects = File.ReadAllLines(file).Select(l => l.Split(',').Select(double.Parse).ToArray()).Select(a => new Rect(a[0], a[1], a[2], a[3])).ToArray();
        Console.WriteLine("host: " + string.Join(" | ", rects));

        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        var blank = BitmapSource.Create(vw, vh, 96, 96, PixelFormats.Bgra32, null, new byte[vw * vh * 4], vw * 4);
        var overlay = new OverlayWindow(blank, new Int32Rect(vx, vy, vw, vh));
        // The capture covers the screen while it looks: a topmost window of this process over the host.
        var dpiScale = VisualTreeHelper.GetDpi(new DrawingVisual()).DpiScaleX;
        var cover = new Window { WindowStyle = WindowStyle.None, Topmost = true, ShowActivated = false, ShowInTaskbar = false, Background = Brushes.Black,
            Left = (rects[3].X - 20) / dpiScale, Top = (rects[3].Y - 40) / dpiScale, Width = (rects[3].Width + 40) / dpiScale, Height = (rects[3].Height + 80) / dpiScale };
        cover.Show();
        Pump(300);
        var editor = (AnnotationEditor)overlay.FindName("Editor");
        var hover = typeof(OverlayWindow).GetMethod("UpdateHover", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Rect Hover(Rect target)
        {
            var c = new Point(target.X + target.Width / 2 - vx, target.Y + target.Height / 2 - vy);
            hover.Invoke(overlay, new object[] { c });
            Pump(900);
            var b = editor.HoverBounds;
            Console.WriteLine($"hover {target} -> {(editor.ShowHover ? b.ToString() : "none")} {editor.HoverLabel}");
            return editor.ShowHover ? new Rect(b.X + vx, b.Y + vy, b.Width, b.Height) : Rect.Empty;
        }

        bool Near(Rect a, Rect b) => !a.IsEmpty && Math.Abs(a.X - b.X) <= 3 && Math.Abs(a.Y - b.Y) <= 3 && Math.Abs(a.Width - b.Width) <= 4 && Math.Abs(a.Height - b.Height) <= 4;
        var onButton = Hover(rects[0]);
        Check(!onButton.IsEmpty && onButton.Width <= rects[0].Width + 4, "over a button: the button (or its text) is framed");
        Check(Near(Hover(rects[1]), rects[1]) , "over a line of text: the text is framed");
        Check(Near(Hover(rects[2]), rects[2]), "over a picture: the picture is framed");
        cover.Close();
        File.Delete(file);
        host?.WaitForExit(5000);
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}

// Read-only: what the hover would find on the real desktop at a few points (class names and sizes only).
static class DeskProbe
{
    static BitmapSource? _shot;
    static ScreenSegmenter? _seg;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    [System.Runtime.InteropServices.DllImport("oleacc.dll")]
    static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.IUnknown)] out object obj);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);

    public static void Run()
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        Sample(vx, vy, vw, vh);
        var woke = 0;
        foreach (var (fx, fy) in new[] { (0.5, 0.5), (0.25, 0.3), (0.75, 0.7) })
        {
            var top = WindowDetector.Detect(vx + (int)(vw * fx), vy + (int)(vh * fy), false);
            var cls = new System.Text.StringBuilder(128);
            if (top == null) continue;
            GetClassName(top.Handle, cls, 128);
            if (!cls.ToString().StartsWith("Chrome_WidgetWin")) continue;
            var render = FindWindowEx(top.Handle, IntPtr.Zero, "Chrome_RenderWidgetHostHWND", null);
            var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            var hr = AccessibleObjectFromWindow(render != IntPtr.Zero ? render : top.Handle, 0xFFFFFFFC, ref iid, out var acc);
            var count = -1;
            try
            {
                // Screen-reader-like calls: Chromium turns its accessibility tree on when a client walks it.
                var ia = (Accessibility.IAccessible)acc;
                count = ia.accChildCount;
                var role = ia.get_accRole(0);
                Console.WriteLine($"  role {role} children {count}");
            }
            catch (Exception ex) { Console.WriteLine("  IAccessible: " + ex.GetType().Name); }
            Console.WriteLine($"chrome render hwnd {render}; wake = {hr}");
            woke++;
        }

        if (woke > 0)
        {
            Thread.Sleep(2000);
            Sample(vx, vy, vw, vh);
        }
    }

    static void Sample(int vx, int vy, int vw, int vh)
    {
        foreach (var (fx, fy) in new[] { (0.5, 0.5), (0.25, 0.3), (0.75, 0.7), (0.5, 0.1) })
        {
            int x = vx + (int)(vw * fx), y = vy + (int)(vh * fy);
            var target = WindowDetector.Detect(x, y, false);
            var cls = new System.Text.StringBuilder(128);
            if (target != null) GetClassName(target.Handle, cls, 128);
            var sw = Stopwatch.StartNew();
            var chain = target == null ? new() : ElementDetector.Detect(target.Handle, x, y);
            if (target != null)
            {
                _shot ??= ScreenCapture.CaptureVirtualScreen().Image;
                _seg ??= new ScreenSegmenter(ImageOps.ReadPixels(_shot).Pixels, _shot.PixelWidth, _shot.PixelHeight, 1);
                var b = target.Bounds;
                var watch = Stopwatch.StartNew();
                var pieces = _seg.At(new Int32Rect((int)b.X - vx, (int)b.Y - vy, (int)b.Width, (int)b.Height), x - vx, y - vy);
                Console.WriteLine($"   pieces in {watch.ElapsedMilliseconds} ms: " + string.Join(" | ", pieces.Select(r => $"{r.Width}x{r.Height}")));
            }
            Console.WriteLine($"({x},{y}) window class={cls} bounds={target?.Bounds} elements={chain.Count} in {sw.ElapsedMilliseconds} ms: " +
                string.Join(" | ", chain.Take(4).Select(c => $"{c.Bounds.Width}x{c.Bounds.Height}")));
        }
    }
}
