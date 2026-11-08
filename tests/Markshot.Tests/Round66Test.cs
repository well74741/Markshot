using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.66: point handles and bends for 箭头 / 直线 / 折线, the 序号 arrow head, the floating button, F, copies, translation bar, right-click cancel.</summary>
static class Round66Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file)).Replace("\r\n", "\n");
    static object? Prop(object target, string name) => target.GetType().GetProperty(name, Any)!.GetValue(target);

    static bool WaitFor(Func<bool> condition, int ms = 4000)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            if (condition()) return true;
            Pump(20);
        }

        return condition();
    }

    static byte[] Render(Annotation item, int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) AnnotationEditor.DrawItem(dc, item);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var px = new byte[w * h * 4];
        bitmap.CopyPixels(px, w * 4, 0);
        return px;
    }

    static byte A(byte[] px, int w, int x, int y) => px[(y * w + x) * 4 + 3];

    static BitmapSource Grey(int w, int h, byte v = 200)
    {
        var px = Enumerable.Repeat(v, w * h * 4).ToArray();
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- 箭头 / 直线: two ends and a bend handle, no frame; dragging the bend bends it
        {
            var arrow = new ArrowAnnotation { Start = new Point(20, 100), End = new Point(180, 100), Thickness = 4, Color = Colors.Red };
            Check(arrow.Frameless && arrow.PointHandles.Count == 3 && arrow.PointHandles[1].Bend && arrow.PointHandles[1].At == new Point(100, 100),
                "an arrow has its two ends and a bend point in the middle (no frame)");
            var hit = AnnotationEditor.HitFrame(arrow, new Point(101, 99), 1);
            Check(hit.Handle == AnnotationEditor.PointHandleBase + 1, "the bend point is a handle");
            Check(AnnotationEditor.HitFrame(arrow, new Point(60, 101), 1).Inside && !AnnotationEditor.HitFrame(arrow, new Point(60, 130), 1).Any,
                "a press on the arrow itself moves it; next to it there is no frame to hit");
            arrow.MovePointHandle(1, new Point(100, 40));
            var px = Render(arrow, 200, 160);
            Check(arrow.Curved && A(px, 200, 100, 40) > 200 && A(px, 200, 100, 100) < 10, "dragging the bend point bends the arrow through it");
            Check(arrow.HitTest(new Point(100, 41), 2) && !arrow.HitTest(new Point(100, 100), 2), "… and it is hit along the curve");
            arrow.MovePointHandle(2, new Point(180, 150));
            Check(arrow.End == new Point(180, 150) && arrow.Curved, "an end handle turns / stretches it, the bend stays");
            var before = arrow.PointHandles.Select(h => h.At).ToList();
            arrow.Translate(10, 5);
            Check(arrow.PointHandles.Select(h => h.At).Zip(before, (a, b) => (a - b) == new Vector(10, 5)).All(x => x), "moved as a whole, all points go along");

            var line = new LineAnnotation { Start = new Point(20, 50), End = new Point(180, 50), Thickness = 4, Color = Colors.Blue };
            line.MovePointHandle(1, new Point(100, 10));
            var lp = Render(line, 200, 100);
            Check(line.Curved && A(lp, 200, 100, 10) > 200 && A(lp, 200, 100, 50) < 10, "a line bends the same way");
        }

        // ---- 折线: every corner and every segment's middle
        {
            var poly = new PolylineAnnotation { Thickness = 4, Color = Colors.Green };
            poly.Points.AddRange(new[] { new Point(20, 20), new Point(120, 20), new Point(120, 120) });
            var handles = poly.PointHandles;
            Check(poly.Frameless && handles.Count == 5 && handles.Count(h => h.Bend) == 2 && handles[3].At == new Point(70, 20), "a polyline: its corners and a bend point per segment");
            poly.MovePointHandle(1, new Point(140, 20));
            Check(poly.Points[1] == new Point(140, 20), "a corner moves");
            poly.MovePointHandle(3, new Point(80, -10));
            var pp = Render(poly, 200, 200);
            Check(poly.Bends.ContainsKey(0) && poly.HitTest(new Point(80, -9), 2) && !poly.HitTest(new Point(80, 20), 1), "a segment bends by its middle point");
            var copy = (PolylineAnnotation)poly.Clone();
            copy.Bends[0] = default;
            Check(poly.Bends[0].Length > 1, "an undo copy has its own bends");
        }

        // ---- 序号: its arrow head is a handle
        {
            var number = new NumberAnnotation { Center = new Point(40, 40), Radius = 12, Tail = new Point(120, 40), Color = Colors.Red };
            Check(number.PointHandles.Count == 1 && !number.Frameless && AnnotationEditor.HitFrame(number, new Point(119, 41), 1).Handle == AnnotationEditor.PointHandleBase,
                "a number's arrow head is a handle (and it keeps its frame)");
            number.MovePointHandle(0, new Point(40, 120));
            Check(number.Tail == new Point(40, 120), "moving it points the arrow somewhere else");
        }

        // ---- the floating button
        {
            var config = new AppConfig { FloatActionHidden = "" };
            var button = new FloatButtonWindow(config, _ => { }, () => { }, () => { }) { FullscreenOverride = () => false };
            button.Show();
            Pump(250);
            button.ShowThumbnail(Grey(300, 200, 90));
            Pump(30);
            var thumb = (Border)button.GetType().GetField("_thumb", Any)!.GetValue(button)!;
            var main = (double)Prop(button, "MainSize")!;
            Check(Math.Abs(thumb.Width - main) < 0.5, $"the screenshot fills the button ({thumb.Width:0} of {main:0})");
            button.Suspend();
            Pump(100);
            button.ShowThumbnail(Grey(100, 100, 30));
            Check(button.GetType().GetField("_pendingThumb", Any)!.GetValue(button) != null, "hidden (a capture): the screenshot waits …");
            button.Resume();
            Check(WaitFor(() => button.GetType().GetField("_pendingThumb", Any)!.GetValue(button) == null && (bool)Prop(button, "ShowingThumbnail")!, 1500),
                "… and shows as the button comes back");
            bool? through = false;
            button.PinsClickThrough = () => through;
            var headers = (IReadOnlyList<string>)button.GetType().GetMethod("MenuHeaders", Any)!.Invoke(button, null)!;
            Check(headers.Contains("鼠标穿透"), "the menu has 鼠标穿透 (for the pins)");
            through = null;
            headers = (IReadOnlyList<string>)button.GetType().GetMethod("MenuHeaders", Any)!.Invoke(button, null)!;
            Check(!headers.Contains("鼠标穿透"), "… only while pins are open");
            button.Close();

            var code = Source("UI/FloatButtonWindow.cs");
            Check(code.Contains("Collapse(animate: true, DragFoldMs);") && code.Contains("private const int DragFoldMs = 45;"), "a drag waits only briefly for the row to fold");
        }

        // ---- every copy of a picture shows on the button; no bubble for copies
        {
            BitmapSource? seen = null;
            Action<BitmapSource> listener = image => seen = image;
            ClipboardService.ImageCopied += listener;
            var old = ClipboardService.CopyOverride;
            ClipboardService.CopyOverride = _ => true;
            var picture = Grey(10, 10);
            ClipboardService.CopyImage(picture);
            ClipboardService.CopyOverride = old;
            ClipboardService.ImageCopied -= listener;
            Check(ReferenceEquals(seen, picture), "a copied picture is announced (the floating button shows it)");
            var app = Source("App.xaml.cs");
            Check(app.Contains("ClipboardService.ImageCopied += image =>") && !app.Contains("ShowBalloon(\"已复制到剪贴板\"") && !app.Contains("ShowBalloon(\"长截图已复制\"") &&
                  !Source("UI/Recording/RecorderController.cs").Contains("Toast.Notify(\"已复制录屏区域截图\""), "no info bubble at the screen corner for copies");
        }

        // ---- F: the fewest steps back, one move
        {
            var pin = new PinWindow(Grey(160, 100), 500, 400, Grey(160, 100), null) { Left = 500, Top = 400, ShowActivated = false };
            pin.Show();
            Pump(300);
            var rotate = pin.GetType().GetMethod("RotatePinImageCounterClockwise", Any)!;
            var flip = pin.GetType().GetMethod("FlipPinImage", Any)!;
            for (var i = 0; i < 5; i++) rotate.Invoke(pin, null);
            flip.Invoke(pin, new object[] { true });
            flip.Invoke(pin, new object[] { false });
            flip.Invoke(pin, new object[] { true });
            Pump(400);
            var way = (List<char>)pin.GetType().GetMethod("ShortestWayBack", Any)!.Invoke(null, new object[] { 0, 1, 1, 0 })!;
            Check(way.Count <= 4, $"the way back is at most a flip and three turns ({string.Join("", way)})");
            pin.GetType().GetMethod("ResetPinImageTransform", Any)!.Invoke(pin, null);
            WaitFor(() => pin.ActualWidth > pin.ActualHeight, 2000);
            Check(((List<char>)pin.GetType().GetField("_orientation", Any)!.GetValue(pin)!).Count == 0 && pin.ActualWidth > pin.ActualHeight, "eight steps undone in one go");
            Check(Source("UI/PinWindow.Orientation.cs").Contains("return; // F: undone in one go"), "the steps on the way are not laid out or shown");
            pin.Close();
        }

        // ---- the capture's translation bar keeps its size; progress inside the area
        {
            var xaml = Source("UI/OverlayWindow.xaml");
            var bar = xaml[xaml.IndexOf("x:Name=\"TranslateStatus\"", StringComparison.Ordinal)..xaml.IndexOf("x:Name=\"TranslateProgress\"", StringComparison.Ordinal)];
            Check(!bar.Contains("TranslateStatusText") && xaml.Contains("x:Name=\"TranslateProgress\""), "the progress / errors are in their own pill, not in the bar");
            Check(Source("UI/OverlayWindow.Translation.cs").Contains("LanguageSwitch.Visibility = Visibility.Visible;\n        SourceLanguageButton.Content = known ? _sourceLabel : \"原文\";"),
                "the bar always has [原文 | 译文] (its size does not change)");
        }

        // ---- right click while the area is drawn drops it
        {
            Check(Source("UI/OverlayWindow.Input.cs").Contains("if (_dragging && _mode == DragMode.Creating)"), "capture: a right click drops the area being drawn");
            var select = new WinMouseSelect((_, _) => { });
            var winField = typeof(WinMouseSelect).GetField("WinDown", Any)!;
            var old = winField.GetValue(null);
            winField.SetValue(null, new Func<bool>(() => true));
            try
            {
                var handle = select.GetType().GetMethod("Handle", Any)!;
                bool Send(int message, double x, double y) => (bool)handle.Invoke(select, new object[] { message, new Point(x, y) })!;
                Send(0x0201, 10, 10);
                Send(0x0200, 200, 150);
                Check((bool)Prop(select, "Drawing")!, "Win + left: drawing");
                var swallowed = Send(0x0204, 200, 150);
                Check(swallowed && !(bool)Prop(select, "Drawing")!, "a right click drops it");
                Check(Send(0x0205, 200, 150) && Send(0x0202, 200, 150), "its release (and the left one) go nowhere: no context menu");
                Check(!Send(0x0201, 5, 5) || (bool)Prop(select, "Drawing")!, "the next Win + press draws again");
            }
            finally
            {
                winField.SetValue(null, old);
                select.GetType().GetMethod("Cancel", Any)!.Invoke(select, null);
            }
        }

        // ---- settings: a page comes in drawn once (not at every frame)
        {
            var page = new Grid();
            Motion.EnterPage(page);
            Check(page.CacheMode is BitmapCache, "a page slides in as one picture (批注栏 was slow: every icon drawn again at each frame)");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
