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

        var hintOverlay = new OverlayWindow(Solid(300, 200, 40, 40, 40), new Int32Rect(-4000, -4000, 300, 200));
        var hintBlock = (TextBlock)hintOverlay.FindName("HintText");
        var hintWords = string.Concat(hintBlock.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
        Check(hintWords.Contains("滚轮 调整识别范围"), "the capture's hint tells what the wheel does");
        Check(hintBlock.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Count() >= 4, "… with a mouse icon next to the mouse actions (left, right, wheel)");
        var hintPop = false;
        ((FrameworkElement)hintOverlay.FindName("Hint")).IsVisibleChanged += (_, _) => hintPop = true;
        var hintBorder = (FrameworkElement)hintOverlay.FindName("Hint");
        var tipBorder = (FrameworkElement)hintOverlay.FindName("WheelTip");
        ((TextBlock)hintOverlay.FindName("WheelTipText")).Text = "识别范围 2 / 4：卡片（滚轮：上 变大，下 变小）";
        tipBorder.Visibility = Visibility.Visible;
        var uiParts = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(70, 70, 70)) };
        foreach (var part in new[] { hintBorder, tipBorder })
        {
            ((Panel)LogicalTreeHelper.GetParent(part)).Children.Remove(part);
            part.HorizontalAlignment = HorizontalAlignment.Center;
            part.Margin = new Thickness(8);
            uiParts.Children.Add(part);
        }

        uiParts.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        uiParts.Arrange(new Rect(uiParts.DesiredSize));
        uiParts.UpdateLayout();
        Console.WriteLine($"   hint width: {hintBorder.ActualWidth:0} DIP");
        var hintShot = new RenderTargetBitmap((int)uiParts.ActualWidth, (int)uiParts.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        hintShot.Render(uiParts);
        var hintEncoder = new PngBitmapEncoder();
        hintEncoder.Frames.Add(BitmapFrame.Create(hintShot));
        using (var hintFile = System.IO.File.Create("capture_hint_wheel.png")) hintEncoder.Save(hintFile);
        hintOverlay.Close();

        // 识别范围 4 (细小元素) also counts a faint box (contrast 20) that the other ranges leave alone
        var faint = new byte[300 * 200 * 4];
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 300; x++)
        {
            var inBox = x >= 100 && x < 180 && y >= 60 && y < 120;
            var v = (byte)(inBox ? 220 : 240);
            var i = (y * 300 + x) * 4;
            faint[i] = faint[i + 1] = faint[i + 2] = v;
            faint[i + 3] = 255;
        }

        var faintRegion = new Int32Rect(0, 0, 300, 200);
        Check(new ScreenSegmenter(faint, 300, 200, 1, 3).At(faintRegion, 140, 60).Count == 0 &&
              new ScreenSegmenter(faint, 300, 200, 1, 4).At(faintRegion, 140, 60).Count > 0,
            "识别范围 细小元素 finds a faint box that 常规元素 leaves alone");

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
        var bright = (RectangleGeometry)overlay.FindName("BrightClip");
        var scale = (double)Get(overlay, "_scale");
        Check(bright.Rect.Width > 0 && Math.Abs(bright.Rect.X - 50 / scale) < 0.5 && Math.Abs(bright.Rect.Width - 40 / scale) < 0.5,
            $"the framed element is shown bright, the rest dim ({bright.Rect})");
        {
            var fader = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
            var fadeList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(candidateType))!;
            fadeList.Add(Activator.CreateInstance(candidateType, new Rect(50, 50, 40, 20), "按钮"));
            Call(fader, "SetHoverChain", fadeList);
            Call(fader, "ClearHover"); // pressed on it: the frame goes
            var fade = (Image)fader.FindName("BrightFade");
            var fadeClip = (RectangleGeometry)fader.FindName("BrightFadeClip");
            Check(fade.Visibility == Visibility.Visible && fadeClip.Rect.Width > 0 && ((RectangleGeometry)fader.FindName("BrightClip")).Rect.Width == 0,
                "pressing in the bright element: it fades to the dim instead of going dark in one frame");
            Pump(400);
            Check(fade.Visibility != Visibility.Visible, "… and the fade is gone after it played");
            fader.Close();
        }
        var rangeAtStart = Markshot.App.Config.DetectRange;
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
        Check(Markshot.App.Config.DetectRange == rangeAtStart && !(bool)typeof(OverlayWindow).GetProperty("RangeChanged")!.GetValue(overlay)!, "no window under the pointer: the wheel keeps no range");

        // 识别范围: what is framed first (the wheel still goes out and in)
        var rangeConfig = Markshot.App.Config;
        var rangeBefore = rangeConfig.DetectRange;
        var ranged = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
        var windowTargetType = typeof(OverlayWindow).Assembly.GetType("Markshot.Core.WindowTarget")!;
        var target = Activator.CreateInstance(windowTargetType)!;
        windowTargetType.GetProperty("Bounds")!.SetValue(target, new Rect(-4000, -4000, 300, 200));
        windowTargetType.GetProperty("Label")!.SetValue(target, "窗口");
        windowTargetType.GetProperty("Handle")!.SetValue(target, new IntPtr(0x12340));
        typeof(OverlayWindow).GetField("_hoverTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ranged, target);
        var rangedEditor = (Markshot.Annotations.AnnotationEditor)ranged.FindName("Editor");
        var rangeChain = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(candidateType))!;
        rangeChain.Add(Activator.CreateInstance(candidateType, new Rect(50, 50, 24, 20), "按钮"));
        rangeChain.Add(Activator.CreateInstance(candidateType, new Rect(40, 40, 120, 60), "卡片"));
        rangeChain.Add(Activator.CreateInstance(candidateType, new Rect(20, 20, 200, 150), "面板"));
        rangeChain.Add(Activator.CreateInstance(candidateType, new Rect(0, 0, 300, 200), "窗口"));
        Rect FirstAt(int range)
        {
            rangeConfig.DetectRange = range;
            Call(ranged, "ClearHover");
            typeof(OverlayWindow).GetField("_hoverTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ranged, target); // the pointer is over the window
            Call(ranged, "SetHoverChain", rangeChain);
            return rangedEditor.HoverBounds;
        }

        Check(FirstAt(4) == new Rect(50, 50, 24, 20), "4 细小元素: the button first");
        Check(FirstAt(3) == new Rect(40, 40, 120, 60), "3 常规元素: the card, not the lone button");
        Check(FirstAt(2) == new Rect(20, 20, 200, 150), "2 大区块: the panel");
        Check(FirstAt(1) == new Rect(0, 0, 300, 200), "1 窗口: the window");
        FirstAt(3);
        var movedFrom = rangedEditor.HoverBounds;
        var nextChain = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(candidateType))!;
        nextChain.Add(Activator.CreateInstance(candidateType, new Rect(200, 120, 90, 70), "图片"));
        nextChain.Add(Activator.CreateInstance(candidateType, new Rect(0, 0, 300, 200), "窗口"));
        Call(ranged, "SetHoverChain", nextChain);
        Check((Rect)Get(ranged, "_hoverLocal") == new Rect(200, 120, 90, 70), "another element: the frame heads there at once, nothing waits for the pointer to rest");
        Pump(AppConfig.HoverSlide + 80);
        Check(rangedEditor.HoverBounds == new Rect(200, 120, 90, 70) && rangedEditor.HoverBounds != movedFrom, $"… and is there after the short slide of {AppConfig.HoverSlide} ms");
        Call(ranged, "ClearHover");
        FirstAt(3);
        Check((bool)Call(ranged, "StepHoverLevel", -120) && (Rect)Get(ranged, "_hoverLocal") == new Rect(50, 50, 24, 20), "the wheel still goes in to the button");
        Call(ranged, "SetHoverChain", rangeChain);
        Check((Rect)Get(ranged, "_hoverLocal") == new Rect(50, 50, 24, 20), "… and that choice stays while the pointer stays");

        // the wheel says what it did, and the range it went to is kept
        var tip = (FrameworkElement)ranged.FindName("WheelTip");
        var tipText = (TextBlock)ranged.FindName("WheelTipText");
        Check(tip.Visibility == Visibility.Visible && tipText.Text.Contains("细小元素") && tipText.Text.Contains("已记住") && tipText.Text.Contains("按钮"),
            $"滚轮: a tip with the range and what is framed ({tipText.Text})");
        Check(rangeConfig.DetectRange == 4 && (bool)typeof(OverlayWindow).GetProperty("RangeChanged")!.GetValue(ranged)!, "… the range the wheel went to is kept (细小元素)");
        Call(ranged, "ClearHover");
        typeof(OverlayWindow).GetField("_hoverTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ranged, target);
        Call(ranged, "SetHoverChain", rangeChain);
        Check((Rect)Get(ranged, "_hoverLocal") == new Rect(50, 50, 24, 20), "… the next element starts at that range (no going back)");
        Call(ranged, "StepHoverLevel", -120);
        Check(tipText.Text.Contains("已是最小"), $"… at the end: it says so ({tipText.Text})");
        Call(ranged, "StepHoverLevel", 120);
        Check(tipText.Text.Contains("常规元素") && tipText.Text.Contains("卡片") && rangeConfig.DetectRange == 3, $"… going out: 常规元素, kept ({tipText.Text})");
        Pump(1900);
        Check(tip.Visibility != Visibility.Visible, "… and goes by itself");

        // the wheel tip sits at the bottom of the screen, wherever the pointer is
        typeof(OverlayWindow).GetField("_hoverScreenPoint", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ranged, (-3990, -3990));
        Call(ranged, "ShowWheelTip", "识别范围：常规元素");
        var tipAt = (Canvas.GetLeft(tip), Canvas.GetTop(tip));
        typeof(OverlayWindow).GetField("_hoverScreenPoint", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ranged, (-3760, -3820));
        Call(ranged, "ShowWheelTip", "识别范围：常规元素");
        Check((Canvas.GetLeft(tip), Canvas.GetTop(tip)) == tipAt, $"the wheel tip stays put at the bottom, it does not follow the pointer ({tipAt})");

        // windows everywhere: the wheel goes on past the window to the whole screen, for this capture only
        var smallWindow = Activator.CreateInstance(windowTargetType)!;
        windowTargetType.GetProperty("Bounds")!.SetValue(smallWindow, new Rect(-3980, -3980, 200, 150));
        windowTargetType.GetProperty("Label")!.SetValue(smallWindow, "窗口");
        windowTargetType.GetProperty("Handle")!.SetValue(smallWindow, new IntPtr(0x12350));
        rangeConfig.DetectRange = 3;
        Call(ranged, "ClearHover");
        typeof(OverlayWindow).GetField("_hoverTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ranged, smallWindow);
        typeof(OverlayWindow).GetField("_hoverScreenPoint", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ranged, (-3900, -3900));
        var inSmall = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(candidateType))!;
        inSmall.Add(Activator.CreateInstance(candidateType, new Rect(40, 40, 120, 60), "卡片"));
        inSmall.Add(Activator.CreateInstance(candidateType, new Rect(20, 20, 200, 150), "窗口"));
        Call(ranged, "SetHoverChain", inSmall);
        Check((Rect)Get(ranged, "_hoverLocal") == new Rect(40, 40, 120, 60), "the whole screen is never the first frame");
        Call(ranged, "StepHoverLevel", 120);
        Check((Rect)Get(ranged, "_hoverLocal") == new Rect(20, 20, 200, 150) && rangeConfig.DetectRange == 1, "wheel up: the window (窗口, kept)");
        var savedRange = rangeConfig.DetectRange;
        Check((bool)Call(ranged, "StepHoverLevel", 120) && (Rect)Get(ranged, "_hoverLocal") == new Rect(0, 0, 300, 200) && tipText.Text.Contains("屏幕") && tipText.Text.Contains("临时"),
            $"wheel up past the window: the whole screen ({tipText.Text})");
        Check(rangeConfig.DetectRange == savedRange, "… not kept as a 识别范围");
        Call(ranged, "StepHoverLevel", 120);
        Check(tipText.Text.Contains("已是最大"), "… and no further");
        Call(ranged, "StepHoverLevel", -120);
        Check((Rect)Get(ranged, "_hoverLocal") == new Rect(20, 20, 200, 150) && rangeConfig.DetectRange == savedRange, "wheel down: back to the window, the range as it was");
        var onlySmall = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(candidateType))!;
        onlySmall.Add(Activator.CreateInstance(candidateType, new Rect(20, 20, 200, 150), "窗口"));
        Call(ranged, "ClearHover");
        typeof(OverlayWindow).GetField("_hoverTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ranged, smallWindow);
        Call(ranged, "SetHoverChain", onlySmall);
        Check((Rect)Get(ranged, "_hoverLocal") == new Rect(20, 20, 200, 150) && !(bool)Call(ranged, "StepHoverLevel", -120), "only the window framed: wheel down is left to going into the window");
        Check((bool)Call(ranged, "StepHoverLevel", 120) && (Rect)Get(ranged, "_hoverLocal") == new Rect(0, 0, 300, 200), "… wheel up: the whole screen");

        // 窗口 range (or another program's window): the wheel still goes in
        rangeConfig.DetectRange = 1;
        Call(ranged, "ClearHover");
        typeof(OverlayWindow).GetField("_hoverTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ranged, target);
        var onlyWindow = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(candidateType))!;
        onlyWindow.Add(Activator.CreateInstance(candidateType, new Rect(0, 0, 300, 200), "窗口"));
        Call(ranged, "SetHoverChain", onlyWindow);
        Check((bool)Call(ranged, "WheelInWindow", 120) && tipText.Text.Contains("已是最大"), $"窗口 range, wheel up: already the largest ({tipText.Text})");
        Check((bool)Call(ranged, "WheelInWindow", -120) && tipText.Text.Contains("正在识别"), "… wheel down: it looks inside");
        Call(ranged, "SetHoverChain", rangeChain);
        Check((Rect)Get(ranged, "_hoverLocal") == new Rect(20, 20, 200, 150), "… and takes one step in from the window");
        ranged.Close();
        rangeConfig.DetectRange = rangeBefore;

        // the old 灵敏度 setting carries over once, then the 识别范围 is what is kept
        var migrated = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"DetectSensitivity\":5}")!;
        var kept = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"DetectSensitivity\":1,\"DetectRange\":4}")!;
        Check(migrated.DetectRange == 4 && kept.DetectRange == 4 && System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{}")!.DetectRange == 3 &&
              !System.Text.Json.JsonSerializer.Serialize(migrated).Contains("DetectSensitivity"), "设置: the old 灵敏度 maps to a range once, the default is 常规元素");

        // the rounded corners last adjusted come back on the next capture, as far as the area allows
        var cornerConfig = Markshot.App.Config;
        var cornerBefore = cornerConfig.CaptureCornerRadius;
        var rounded = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
        typeof(OverlayWindow).GetField("_cornerRadius", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(rounded, 24.0);
        Call(rounded, "RememberCorner");
        rounded.Close();
        Check(cornerConfig.CaptureCornerRadius == 24, "the corners are remembered when the dot is let go");
        var nextCapture = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
        var nextField = typeof(OverlayWindow).GetField("_selection", BindingFlags.NonPublic | BindingFlags.Instance)!;
        typeof(OverlayWindow).GetField("_hasSelection", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(nextCapture, true);
        nextField.SetValue(nextCapture, new Rect(10, 10, 200, 120));
        Call(nextCapture, "ApplyRememberedCorner");
        Check((double)Get(nextCapture, "_cornerRadius") == 24, "the next capture starts with them");
        nextField.SetValue(nextCapture, new Rect(10, 10, 30, 20));
        Call(nextCapture, "ApplyRememberedCorner");
        Check((double)Get(nextCapture, "_cornerRadius") == 10, "… no more than half of a small area");
        nextCapture.Close();
        cornerConfig.CaptureCornerRadius = 0;
        var plainCapture = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
        typeof(OverlayWindow).GetField("_hasSelection", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plainCapture, true);
        typeof(OverlayWindow).GetField("_selection", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plainCapture, new Rect(10, 10, 200, 120));
        Call(plainCapture, "ApplyRememberedCorner");
        Check((double)Get(plainCapture, "_cornerRadius") == 0, "dragged back to square: square from then on");

        // the automatically framed area shows the remembered corners, and a click on it keeps them
        cornerConfig.CaptureCornerRadius = 24;
        var hoverRound = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
        var hoverEditor = (Markshot.Annotations.AnnotationEditor)hoverRound.FindName("Editor");
        var hoverClip = (RectangleGeometry)hoverRound.FindName("BrightClip");
        var hoverScale = (double)Get(hoverRound, "_scale");
        Call(hoverRound, "SetHoverChain", list);
        Check(hoverEditor.HoverCornerRadius == 10 && Math.Abs(hoverClip.RadiusX - 10 / hoverScale) < 0.01, $"识别框 and its bright area are rounded, no more than half of a small element ({hoverEditor.HoverCornerRadius})");
        var hoverArea = new Rect(20, 20, 200, 120);
        typeof(OverlayWindow).GetField("_pendingWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(hoverRound, hoverArea);
        typeof(OverlayWindow).GetField("_dragging", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(hoverRound, true);
        Call(hoverRound, "Input_MouseLeftButtonUp", hoverRound, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Check((bool)Get(hoverRound, "_hasSelection") && (Rect)Get(hoverRound, "_selection") == hoverArea && (double)Get(hoverRound, "_cornerRadius") == 24, "a click on the framed area: the capture has those corners");
        Check(hoverEditor.HoverCornerRadius == 0, "the hover frame is square again for annotations once an area is chosen");
        hoverRound.Close();
        cornerConfig.CaptureCornerRadius = 0;
        var squareHover = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
        Call(squareHover, "SetHoverChain", list);
        Check(((Markshot.Annotations.AnnotationEditor)squareHover.FindName("Editor")).HoverCornerRadius == 0 && ((RectangleGeometry)squareHover.FindName("BrightClip")).RadiusX == 0, "no corners remembered: a square frame");
        squareHover.Close();

        // what the rounded frame looks like
        var frameEditor = new Markshot.Annotations.AnnotationEditor { Width = 220, Height = 140, Scale = 1, ShowHover = true, HoverBounds = new Rect(20, 20, 180, 100), HoverCornerRadius = 30 };
        frameEditor.Measure(new Size(220, 140));
        frameEditor.Arrange(new Rect(0, 0, 220, 140));
        var frameShot = new RenderTargetBitmap(220, 140, 96, 96, PixelFormats.Pbgra32);
        frameShot.Render(frameEditor);
        var framePixels = new byte[4];
        frameShot.CopyPixels(new Int32Rect(21, 21, 1, 1), framePixels, 4, 0);
        var cornerEmpty = framePixels[3] == 0;
        frameShot.CopyPixels(new Int32Rect(110, 21, 1, 1), framePixels, 4, 0);
        Check(cornerEmpty && framePixels[3] > 200, "drawn with rounded corners: the square corner stays empty, the edge is solid");
        var frameEncoder = new PngBitmapEncoder();
        frameEncoder.Frames.Add(BitmapFrame.Create(frameShot));
        using (var frameFile = System.IO.File.Create("hover_frame_rounded.png")) frameEncoder.Save(frameFile);

        // 快速双击 in the capture: copies without what the first click drew, and closes
        BitmapSource quickCopied = null;
        ClipboardService.CopyOverride = image => { quickCopied = image; return true; };
        var quickItems = ((Markshot.Annotations.AnnotationEditor)plainCapture.FindName("Editor")).Items;
        var quickHistory = (Markshot.Annotations.AnnotationHistory)Get(plainCapture, "_history");
        typeof(OverlayWindow).GetField("_quickDepth", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plainCapture, quickHistory.Depth);
        quickHistory.Checkpoint(quickItems);
        quickItems.Add(new Markshot.Annotations.RectAnnotation { Rect = new Rect(20, 20, 100, 60), Color = Colors.Red, Fill = true });
        Call(plainCapture, "QuickCopyAndClose");
        Pump(300);
        Check(quickCopied != null && quickItems.Count == 0 && ((bool)Get(plainCapture, "_finishing") || (bool)Get(plainCapture, "_closed")), "快速双击: copied without the first click's element, the capture closes");
        ClipboardService.CopyOverride = null;
        cornerConfig.CaptureCornerRadius = cornerBefore;

        // Esc: one press leaves the capture (default); with the setting off it steps back first
        void SetField(object o, string name, object value) => typeof(OverlayWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);
        var config = Markshot.App.Config;
        var escBefore = config.EscExitsCapture;
        config.EscExitsCapture = false;
        var stepper = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
        SetField(stepper, "_hasSelection", true);
        SetField(stepper, "_selection", new Rect(10, 10, 100, 80));
        Call(stepper, "HandleEscape");
        Check(!(bool)Get(stepper, "_hasSelection") && !(bool)Get(stepper, "_finishing"), "Esc with 逐步退出: the area goes first, the capture stays");
        stepper.Close();
        config.EscExitsCapture = true;
        var leaver = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
        SetField(leaver, "_hasSelection", true);
        SetField(leaver, "_selection", new Rect(10, 10, 100, 80));
        Call(leaver, "HandleEscape");
        Pump(300);
        Check((bool)Get(leaver, "_finishing") || (bool)Get(leaver, "_closed"), "Esc (default): one press leaves the capture with an area chosen");
        void RightClick(OverlayWindow o)
        {
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent };
            o.RaiseEvent(down);
            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent };
            o.RaiseEvent(up);
        }

        var rightLeaver = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
        SetField(rightLeaver, "_hasSelection", true);
        SetField(rightLeaver, "_selection", new Rect(10, 10, 100, 80));
        ((Markshot.Annotations.AnnotationEditor)rightLeaver.FindName("Editor")).Items.Add(new Markshot.Annotations.RectAnnotation { Rect = new Rect(20, 20, 30, 30) });
        RightClick(rightLeaver);
        Pump(300);
        Check((bool)Get(rightLeaver, "_finishing") || (bool)Get(rightLeaver, "_closed"), "right click = Esc: one click leaves the capture (also with something drawn)");
        config.EscExitsCapture = false;
        var rightStepper = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
        SetField(rightStepper, "_hasSelection", true);
        SetField(rightStepper, "_selection", new Rect(10, 10, 100, 80));
        RightClick(rightStepper);
        Check(!(bool)Get(rightStepper, "_hasSelection") && !(bool)Get(rightStepper, "_finishing"), "right click with 逐步退出: steps back like Esc (the area goes, the capture stays)");
        rightStepper.Close();

        // no tool: the wheel rounds the capture's corners (remembered); moving the area shows no loupe
        {
            var wheelCornerBefore = config.CaptureCornerRadius;
            var rounder = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
            SetField(rounder, "_hasSelection", true);
            SetField(rounder, "_selection", new Rect(50, 50, 150, 100));
            var roundScale = (double)Get(rounder, "_scale");
            Call(rounder, "WheelCorner", 120);
            Call(rounder, "WheelCorner", 120);
            Check(Math.Abs((double)Get(rounder, "_cornerRadius") - 4 * roundScale) < 0.01 && Math.Abs(config.CaptureCornerRadius - 4 * roundScale) < 0.01,
                $"wheel up twice without a tool: rounder corners, remembered ({Get(rounder, "_cornerRadius")})");
            for (var i = 0; i < 5; i++) Call(rounder, "WheelCorner", -120);
            Check((double)Get(rounder, "_cornerRadius") == 0, "… wheel down: back to square, not below");
            for (var i = 0; i < 80; i++) Call(rounder, "WheelCorner", 120);
            Check((double)Get(rounder, "_cornerRadius") <= 50, "… at most half the short side");
            SetField(rounder, "_dragging", true);
            SetField(rounder, "_mode", Enum.Parse(typeof(OverlayWindow).GetNestedType("DragMode", BindingFlags.NonPublic)!, "Moving"));
            Check(!(bool)Call(rounder, "ShouldShowMagnifier", new Point(100, 100))!, "moving the area: no loupe");
            SetField(rounder, "_mode", Enum.Parse(typeof(OverlayWindow).GetNestedType("DragMode", BindingFlags.NonPublic)!, "Resizing"));
            Check((bool)Call(rounder, "ShouldShowMagnifier", new Point(100, 100))!, "… resizing it still has the loupe");
            SetField(rounder, "_dragging", false);
            rounder.Close();
            config.CaptureCornerRadius = wheelCornerBefore;
        }

        // a pen in hand: the capture's edges still resize it (a narrow band inside, the full band outside)
        var penFrame = new OverlayWindow(screen, new Int32Rect(-4000, -4000, 300, 200));
        SetField(penFrame, "_hasSelection", true);
        SetField(penFrame, "_selection", new Rect(50, 50, 150, 100));
        var frameScale = (double)Get(penFrame, "_scale");
        Call(penFrame, "SetTool", Markshot.Annotations.AnnotationTool.Pen);
        int EdgeAt(double x, double y) => (int)Call(penFrame, "HitHandle", new Point(x, y))!;
        Check(EdgeAt(50 + 2 / frameScale, 100) == 7 && EdgeAt(50 - 8 / frameScale, 100) == 7 && EdgeAt(200, 150 + 2 / frameScale) == 4,
            "pen: the edge and the corner of the capture are still grabbed");
        Check(EdgeAt(50 + 9 / frameScale, 100) == -1 && EdgeAt(120, 100) == -1, "… a little inside, the pen draws (close to the edge too)");
        var allTools = Enum.GetValues<Markshot.Annotations.AnnotationTool>()
            .Where(t => t is not (Markshot.Annotations.AnnotationTool.None or Markshot.Annotations.AnnotationTool.Select)).ToList();
        var missed = new List<string>();
        foreach (var tool in allTools)
        {
            Call(penFrame, "SetTool", tool);
            if (EdgeAt(50 + 2 / frameScale, 100) != 7 || EdgeAt(200 + 6 / frameScale, 150 + 6 / frameScale) != 4)
            {
                missed.Add(tool.ToString());
            }

            Call(penFrame, "UpdateAnnotationHover", new Point(50 + 2 / frameScale, 100));
            if (((FrameworkElement)penFrame.FindName("InputLayer")).Cursor != Cursors.SizeWE)
            {
                missed.Add(tool + " (cursor)");
            }
        }

        Check(missed.Count == 0, $"every annotation tool: the capture's edge is grabbed, with the resize cursor ({string.Join(", ", missed)})");
        Call(penFrame, "SetTool", Markshot.Annotations.AnnotationTool.Select);
        Check(EdgeAt(50 + 9 / frameScale, 100) == 7, "… without a tool the band inside is as wide as before");
        penFrame.Close();
        config.EscExitsCapture = escBefore;

        // the hover asks the screenshot when UI Automation has nothing (no window behind here: handle 0)
        var pageOverlay = new OverlayWindow(pageShot, new Int32Rect(0, 0, 640, 320));
        typeof(OverlayWindow).GetField("_hoverTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(pageOverlay, new WindowTarget { Handle = IntPtr.Zero, Bounds = new Rect(0, 0, 640, 320), Label = "窗口" });
        typeof(OverlayWindow).GetField("_hoverScreenPoint", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(pageOverlay, (60, 50));
        typeof(OverlayWindow).GetField("_elementWanted", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(pageOverlay, ((IntPtr, int, int)?)(IntPtr.Zero, 60, 50));
        var pageRangeBefore = Markshot.App.Config.DetectRange;
        Markshot.App.Config.DetectRange = 4;
        Call(pageOverlay, "RunElementDetection");
        Pump(800);
        var pageEditor = (Markshot.Annotations.AnnotationEditor)pageOverlay.FindName("Editor");
        var framed = pageEditor.HoverBounds;
        Check(pageEditor.ShowHover && Around(new Int32Rect((int)framed.X, (int)framed.Y, (int)framed.Width, (int)framed.Height), Box(run), 4) && pageEditor.HoverLabel!.StartsWith("文字"),
            "细小元素: the capture frames the run of text under the mouse");
        Markshot.App.Config.DetectRange = 3;
        Call(pageOverlay, "ClearHover");
        typeof(OverlayWindow).GetField("_hoverTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(pageOverlay, new WindowTarget { Handle = IntPtr.Zero, Bounds = new Rect(0, 0, 640, 320), Label = "窗口" });
        Call(pageOverlay, "RefreshHoverChain");
        Pump(300);
        var framed3 = pageEditor.HoverBounds;
        Check(pageEditor.ShowHover && framed3.Width * framed3.Height > Box(run).Width * Box(run).Height, $"常规元素: something bigger than the lone run of text ({framed3})");
        Markshot.App.Config.DetectRange = pageRangeBefore;
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
