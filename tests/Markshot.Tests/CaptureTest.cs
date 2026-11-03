using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

// The capture window: UI element detection, 显示鼠标, 辅助线.
static class CaptureTest
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }

    // A real message loop: UI Automation questions to this process's own window arrive as messages.
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static T Wait<T>(Task<T> task)
    {
        var until = DateTime.Now.AddSeconds(10);
        while (!task.IsCompleted && DateTime.Now < until) Pump(20);
        return task.Result;
    }

    static BitmapSource Solid(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    static (byte B, byte G, byte R) At(BitmapSource image, int x, int y)
    {
        var px = new byte[4];
        new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2]);
    }

    static object Get(object o, string name) =>
        typeof(OverlayWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    static object Call(object o, string name, params object[] args) =>
        typeof(OverlayWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, args)!;

    public static void Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Theme.Apply(ThemeMode.Dark);

        // ---- UI elements under a point (a window of this process stands in for another app)
        var button = new Button { Content = "确定", Width = 120, Height = 40, Margin = new Thickness(30) };
        var text = new TextBlock { Text = "一段文字", Margin = new Thickness(30, 0, 0, 0), FontSize = 16 };
        var panel = new StackPanel { Children = { button, text } };
        var host = new Window { Left = 40, Top = 40, Width = 360, Height = 260, ShowActivated = false, ShowInTaskbar = false, Content = new Border { Child = panel } };
        host.Show();
        Pump(400);
        var handle = new WindowInteropHelper(host).Handle;
        var dpi = VisualTreeHelper.GetDpi(host);
        var topLeft = button.PointToScreen(new Point(0, 0));
        var center = button.PointToScreen(new Point(60, 20));
        var chain = Wait(Task.Run(() => ElementDetector.Detect(handle, (int)center.X, (int)center.Y)));
        Console.WriteLine("chain: " + string.Join(" | ", chain.Select(c => $"{c.Label} {c.Bounds}")));
        Check(chain.Count >= 2 && chain[0].Bounds.Width < chain[1].Bounds.Width && chain[0].Label.Contains("确定"),
            "the smallest element under the mouse comes first (the button's text)");
        Check(chain.Any(c => Math.Abs(c.Bounds.X - topLeft.X) <= 2 && Math.Abs(c.Bounds.Width - 120 * dpi.DpiScaleX) <= 2),
            "the button itself is the next level");
        Check(chain.Count >= 1 && chain[0].Label.Contains("确定"), "its label names it");
        Check(chain.Zip(chain.Skip(1)).All(p => p.Second.Bounds.Width * p.Second.Bounds.Height >= p.First.Bounds.Width * p.First.Bounds.Height),
            "then the elements around it, each larger");
        var onText = text.PointToScreen(new Point(5, 5));
        var textChain = Wait(Task.Run(() => ElementDetector.Detect(handle, (int)onText.X, (int)onText.Y)));
        Check(textChain.Count >= 1 && textChain[0].Label.Contains("一段文字"), "a text is found as its own element");
        Check(Wait(Task.Run(() => ElementDetector.Detect(IntPtr.Zero, 0, 0))).Count == 0, "no window, no elements");
        host.Close();

        // ---- the screenshot itself: runs of text, a picture, a paragraph (apps without UI Automation)
        var page = new Canvas { Width = 640, Height = 320, Background = Brushes.White };
        TextBlock T(string s, double x, double y) { var t = new TextBlock { Text = s, FontSize = 15, Foreground = Brushes.Black }; Canvas.SetLeft(t, x); Canvas.SetTop(t, y); page.Children.Add(t); return t; }
        var run = T("Hello world 你好", 40, 40);
        var other = T("第二段文字", 320, 40);
        var picture = new System.Windows.Shapes.Rectangle { Width = 120, Height = 80, Fill = new LinearGradientBrush(Colors.SteelBlue, Colors.Orange, 30) };
        Canvas.SetLeft(picture, 40); Canvas.SetTop(picture, 130); page.Children.Add(picture);
        var line1 = T("段落第一行，内容比较长一些", 320, 130);
        var line2 = T("段落第二行", 320, 152);
        page.Measure(new Size(640, 320)); page.Arrange(new Rect(0, 0, 640, 320)); page.UpdateLayout();
        var pageShot = new RenderTargetBitmap(640, 320, 96, 96, PixelFormats.Pbgra32);
        pageShot.Render(page);
        pageShot.Freeze();
        var (pagePixels, _, _) = ImageOps.ReadPixels(pageShot);
        var segmenter = new ScreenSegmenter(pagePixels, 640, 320, 1);
        var whole = new Int32Rect(0, 0, 640, 320);
        Rect Box(FrameworkElement e) => new(Canvas.GetLeft(e), Canvas.GetTop(e), e.ActualWidth, e.ActualHeight);
        bool Around(Int32Rect found, Rect want, double slack) =>
            found.X <= want.X + slack && found.Y <= want.Y + slack && found.X + found.Width >= want.Right - slack && found.Y + found.Height >= want.Bottom - slack &&
            found.Width <= want.Width + slack * 2 + 8 && found.Height <= want.Height + slack * 2 + 8;
        var onRun = segmenter.At(whole, 60, 50);
        Console.WriteLine("on run: " + string.Join(" | ", onRun));
        Check(onRun.Count > 0 && Around(onRun[0], Box(run), 4), "over a run of text: just that run (not the text beside it)");
        var onPicture = segmenter.At(whole, 100, 170);
        Check(onPicture.Count > 0 && Around(onPicture[0], Box(picture), 3), "over a picture: the picture");
        var onParagraph = segmenter.At(whole, 340, 140);
        var both = Rect.Union(Box(line1), Box(line2));
        Console.WriteLine("on paragraph: " + string.Join(" | ", onParagraph));
        Check(onParagraph.Count >= 2 && Around(onParagraph[0], Box(line1), 4) && onParagraph.Skip(1).Any(r => Around(r, both, 4)),
            "over a paragraph line: the line, then the paragraph");
        Check(segmenter.At(whole, 600, 300).Count == 0, "over empty background: nothing");

        // ---- the capture window, not shown: hover frames, 显示鼠标, 辅助线
        var screen = Solid(300, 200, 40, 40, 40);
        var overlay = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
        var candidateType = typeof(OverlayWindow).GetNestedType("HoverCandidate", BindingFlags.NonPublic)!;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(candidateType))!;
        list.Add(Activator.CreateInstance(candidateType, new Rect(50, 50, 40, 20), "按钮 · 确定"));
        list.Add(Activator.CreateInstance(candidateType, new Rect(20, 20, 200, 120), "窗格"));
        list.Add(Activator.CreateInstance(candidateType, new Rect(0, 0, 300, 200), "窗口"));
        Call(overlay, "SetHoverChain", list);
        var editor = (Markshot.Annotations.AnnotationEditor)overlay.FindName("Editor");
        Check(editor.ShowHover && editor.HoverBounds == new Rect(50, 50, 40, 20) && editor.HoverLabel!.StartsWith("按钮"), "hover frames the smallest element");
        Check((bool)Call(overlay, "StepHoverLevel", 120) && (Rect)Get(overlay, "_hoverLocal") == new Rect(20, 20, 200, 120), "wheel up: the element around it");
        Pump(400);
        Check(editor.HoverBounds == new Rect(20, 20, 200, 120), "the frame slides there (animated)");
        Call(overlay, "StepHoverLevel", 120);
        Call(overlay, "StepHoverLevel", 120);
        Check((Rect)Get(overlay, "_hoverLocal") == new Rect(0, 0, 300, 200), "up to the window, and no further");
        Call(overlay, "StepHoverLevel", -120);
        Check((Rect)Get(overlay, "_hoverLocal") == new Rect(20, 20, 200, 120), "wheel down: back in");
        Call(overlay, "SetHoverChain", list);
        Check((Rect)Get(overlay, "_hoverLocal") == new Rect(20, 20, 200, 120), "the chosen level stays while the mouse stays on the element");

        // the hover asks the screenshot when UI Automation has nothing (no window behind here: handle 0)
        var pageOverlay = new OverlayWindow(pageShot, new Int32Rect(0, 0, 640, 320));
        typeof(OverlayWindow).GetField("_hoverTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(pageOverlay, new WindowTarget { Handle = IntPtr.Zero, Bounds = new Rect(0, 0, 640, 320), Label = "窗口" });
        typeof(OverlayWindow).GetField("_hoverScreenPoint", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(pageOverlay, (60, 50));
        typeof(OverlayWindow).GetField("_elementWanted", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(pageOverlay, ((IntPtr, int, int)?)(IntPtr.Zero, 60, 50));
        Call(pageOverlay, "RunElementDetection");
        Pump(800);
        var pageEditor = (Markshot.Annotations.AnnotationEditor)pageOverlay.FindName("Editor");
        var framed = pageEditor.HoverBounds;
        Check(pageEditor.ShowHover && Around(new Int32Rect((int)framed.X, (int)framed.Y, (int)framed.Width, (int)framed.Height), Box(run), 4) && pageEditor.HoverLabel!.StartsWith("文字"),
            "the capture frames the run of text under the mouse");
        pageOverlay.Close();

        // a picture wall (Pinterest-like): picture -> gap -> picture -> back; the frame follows every time
        var wall = new Canvas { Width = 800, Height = 500, Background = Brushes.White };
        var cards = new List<Rect>();
        for (var c = 0; c < 4; c++)
        for (var r = 0; r < 2; r++)
        {
            var card = new Rect(30 + c * 190, 30 + r * 230, 170, 200 - c * 12);
            cards.Add(card);
            var fill = new System.Windows.Shapes.Rectangle { Width = card.Width, Height = card.Height, Fill = new LinearGradientBrush(Color.FromRgb((byte)(40 + c * 50), 90, 160), Colors.Orange, 45 + r * 30) };
            Canvas.SetLeft(fill, card.X); Canvas.SetTop(fill, card.Y); wall.Children.Add(fill);
        }
        wall.Measure(new Size(800, 500)); wall.Arrange(new Rect(0, 0, 800, 500)); wall.UpdateLayout();
        var wallShot = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32);
        wallShot.Render(wall);
        wallShot.Freeze();
        var wallOverlay = new OverlayWindow(wallShot, new Int32Rect(0, 0, 800, 500));
        typeof(OverlayWindow).GetField("_windowFinder", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(wallOverlay,
            (Func<int, int, WindowTarget?>)((_, _) => new WindowTarget { Handle = IntPtr.Zero, Bounds = new Rect(0, 0, 800, 500), Label = "网页" }));
        var wallEditor = (Markshot.Annotations.AnnotationEditor)wallOverlay.FindName("Editor");
        Rect HoverAt(double x, double y)
        {
            Call(wallOverlay, "UpdateHover", new Point(x, y));
            Pump(500);
            return (Rect)Get(wallOverlay, "_hoverLocal");
        }

        bool Card(Rect found, Rect card) => Math.Abs(found.X - card.X) <= 3 && Math.Abs(found.Y - card.Y) <= 3 && Math.Abs(found.Width - card.Width) <= 6 && Math.Abs(found.Height - card.Height) <= 6;
        var path = new (double X, double Y, int Card)[]
        {
            (100, 120, 0), (210, 120, -1), (290, 130, 2), (600, 600 - 590, -2), (480, 330, 5), (210, 120, -1), (100, 300, 1), (110, 125, 0)
        };
        var followed = true;
        foreach (var (x, y, index) in path)
        {
            var found = HoverAt(x, y);
            var ok = index >= 0 ? Card(found, cards[index]) : !Card(found, cards[0]);
            Console.WriteLine($"wall ({x},{y}) -> {found} {(ok ? "ok" : "WRONG")}");
            followed &= ok;
        }

        Check(followed, "picture wall: picture, gap, another picture and back - the frame follows every time");
        wallOverlay.Close();

        var pointer = Solid(12, 18, 0, 0, 255);
        overlay.CursorShot = new CursorShot(pointer, 100, 60);
        Check(!overlay.ShowsCursor && At((BitmapSource)Get(overlay, "_screen"), 104, 64).R == 40, "显示鼠标 off: the screenshot without the pointer");
        overlay.SetShowCursor(true);
        var shown = (BitmapSource)Get(overlay, "_screen");
        Check(overlay.ShowsCursor && At(shown, 104, 64).R == 255 && At(shown, 120, 64).R == 40, "显示鼠标 on: the pointer is in the screenshot");
        Check(ReferenceEquals(((Image)overlay.FindName("BrightImage")).Source, shown) && ReferenceEquals(editor.Source, shown), "everything shows the same picture");
        overlay.SetShowCursor(false);
        Check(At((BitmapSource)Get(overlay, "_screen"), 104, 64).R == 40, "and off again");
        var grabbed = CursorCapture.Grab(0, 0);
        Check(grabbed == null || grabbed.Image.PixelWidth == 128, "grabbing the real pointer works (or there is none)");

        Check(CaptureKey.Matches("Ctrl+M", Key.M, ModifierKeys.Control) && !CaptureKey.Matches("Ctrl+M", Key.M, ModifierKeys.None) &&
              !CaptureKey.Matches("Alt", Key.LeftAlt, ModifierKeys.Alt) && CaptureKey.IsLoneModifier("Alt") && CaptureKey.Matches("F2", Key.F2, ModifierKeys.None),
            "key settings: combinations and lone modifiers");
        Call(overlay, "CaptureKeyDown", Key.LeftAlt, false);
        Check((bool)Call(overlay, "CaptureKeyUp", Key.LeftAlt) && overlay.ShowsCrosshair, "a tap on Alt shows the crosshair");
        Check(((FrameworkElement)overlay.FindName("CrossH")).Visibility == Visibility.Visible, "the lines are there");
        Call(overlay, "CaptureKeyDown", Key.LeftAlt, false);
        Call(overlay, "CaptureKeyDown", Key.X, false);
        Check(!(bool)Call(overlay, "CaptureKeyUp", Key.LeftAlt) && overlay.ShowsCrosshair, "Alt with another key is not a tap");
        Call(overlay, "CaptureKeyDown", Key.LeftAlt, false);
        Call(overlay, "CaptureKeyUp", Key.LeftAlt);
        Check(!overlay.ShowsCrosshair && ((FrameworkElement)overlay.FindName("CrossV")).Visibility == Visibility.Collapsed, "another tap hides it");
        overlay.Close();

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
