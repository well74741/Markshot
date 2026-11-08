using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.65: the floating button (no flash, thumbnail, open all pins, hide while capturing), pin translation and menu, F in one move, editor hover, number arrow, recorder, the logo.</summary>
static class Round65Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file)).Replace("\r\n", "\n"); // (CI checks out with CRLF)
    static IReadOnlyList<string> Headers(FloatButtonWindow button) => (IReadOnlyList<string>)button.GetType().GetMethod("MenuHeaders", Any)!.Invoke(button, null)!;

    /// <summary>Waits (pumping) until the condition holds, at most a few seconds (CI machines are slow).</summary>
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

    static object? Prop(object target, string name) => target.GetType().GetProperty(name, Any)!.GetValue(target);

    static BitmapSource Grey(int w, int h, byte v = 200)
    {
        var px = Enumerable.Repeat(v, w * h * 4).ToArray();
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    static byte[] Render(Action<DrawingContext> draw, int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var px = new byte[w * h * 4];
        bitmap.CopyPixels(px, w * 4, 0);
        return px;
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- the floating button
        {
            var code = Source("UI/FloatButtonWindow.cs");
            var move = code[code.IndexOf("private void OnMainMove", StringComparison.Ordinal)..code.IndexOf("private void OnMainUp", StringComparison.Ordinal)];
            Check(move.Contains("_holdMoveUntil = Environment.TickCount64 + FoldMs") && move.Contains("_pressScale.ScaleX = _pressScale.ScaleY = PressedScale"),
                "dragged: the row folds first and nothing animates while the window moves (no frame on its path)");

            var config = new AppConfig { FloatActionHidden = "" };
            var button = new FloatButtonWindow(config, _ => { }, () => { }, () => { }) { FullscreenOverride = () => false };
            button.Show();
            Pump(250);
            button.ShowThumbnail(Grey(300, 200, 90));
            Pump(30);
            Check((bool)Prop(button, "ShowingThumbnail")! && button.IconImage.Opacity < 0.01, "a screenshot shows on the button at once (its picture under it)");
            WaitFor(() => !(bool)Prop(button, "ShowingThumbnail")!);
            Check(!(bool)Prop(button, "ShowingThumbnail")! && button.IconImage.Opacity > 0.5, "half a second later it slides away and the picture is back");
            var closedAll = false;
            button.PinsClosedAll = () => closedAll;
            Check(Headers(button).Contains("关闭所有贴图") && !Headers(button).Contains("打开所有贴图"), "the menu: 关闭所有贴图");
            closedAll = true;
            Check(Headers(button).Contains("打开所有贴图") && !Headers(button).Contains("关闭所有贴图"), "… which turns into 打开所有贴图 once used");
            button.Close();

            var app = Source("App.xaml.cs");
            Check(app.Contains("NoteScreenshot(shot); // as it comes back") && app.Contains("NoteScreenshot(image);"), "every screenshot (capture, Win + mouse, long screenshot) goes to the button");
            Check(app.Contains("if (!Config.FloatHideWhileCapturing)") && new AppConfig().FloatHideWhileCapturing, "截图时隐藏: on by default, the button can stay for the capture");
            Check(Source("UI/SettingsWindow.xaml").Contains("x:Name=\"FloatCaptureHideCheck\""), "… a switch in its settings");
        }

        // ---- pins: 关闭原位翻译, the ✕, 清除批注内容 only after annotating, F in one move
        {
            var pin = new PinWindow(Grey(160, 100), 500, 400, Grey(160, 100), null) { Left = 500, Top = 400, ShowActivated = false };
            pin.Show();
            Pump(300);
            List<string> Menu()
            {
                pin.GetType().GetMethod("BuildContextMenu", Any)?.Invoke(pin, null);
                return pin.ContextMenu?.Items.OfType<MenuItem>().Select(item => item.Header as string ?? "").ToList() ?? new List<string>();
            }

            var items = Menu();
            Check(items.Count > 0 && !items.Contains("清除批注内容"), "no 清除批注内容 before anything is annotated");
            var original = Grey(160, 100, 30);
            pin.AdoptTranslation(original, "English", "中文");
            WaitFor(() => (bool)Prop(pin, "LanguageSwitchShown")!, 2000);
            Check(Menu().Contains("关闭原位翻译"), "translated: the menu has 关闭原位翻译");
            Check(pin.FindName("PinTranslateClose") is Button, "the switch has a ✕");
            pin.GetType().GetMethod("PinCloseTranslation_Click", Any)!.Invoke(pin, new object?[] { pin, new RoutedEventArgs() });
            Pump(100);
            Check(ReferenceEquals(pin.GetType().GetField("_baseImage", Any)!.GetValue(pin), original) && !(bool)Prop(pin, "LanguageSwitchShown")! && !Menu().Contains("关闭原位翻译"),
                "✕: back to the original, the switch gone");

            // F: three steps back in one move
            var rotate = pin.GetType().GetMethod("RotatePinImageCounterClockwise", Any)!;
            var flip = pin.GetType().GetMethod("FlipPinImage", Any)!;
            rotate.Invoke(pin, null);
            flip.Invoke(pin, new object[] { true });
            rotate.Invoke(pin, null);
            Pump(400);
            pin.GetType().GetMethod("ResetPinImageTransform", Any)!.Invoke(pin, null);
            WaitFor(() => pin.ActualWidth > pin.ActualHeight, 2000);
            var motion = ((int Mirror, double Angle, double Zoom)?)Prop(pin, "ResetMotion");
            var host = (FrameworkElement)pin.FindName("ImageHost");
            var group = host.RenderTransform as TransformGroup;
            Check(motion is { Mirror: -1 } && group?.Children.Count == 2, $"F: one transition from how it looked (mirrored, {motion?.Angle:0}°)");
            Check(((List<char>)pin.GetType().GetField("_orientation", Any)!.GetValue(pin)!).Count == 0 && pin.ActualWidth > pin.ActualHeight, "… back to the first orientation");
            pin.Close();
            Check(Source("UI/PinWindow.Menu.cs").Contains("Editor.Items.Any(item => item is not ImageAnnotation)"), "清除批注内容 counts annotations, not pictures");
        }

        // ---- the capture's language bar: always above the area's top right
        {
            var code = Source("UI/OverlayWindow.Translation.cs");
            var place = code[code.IndexOf("private void PositionTranslateStatus", StringComparison.Ordinal)..];
            place = place[..place.IndexOf("Canvas.SetLeft(TranslateStatus", StringComparison.Ordinal)];
            Check(place.Contains("selection.Right - size.Width") && !place.Contains("candidates"), "the capture's bar stays at the area's top right (no moving round the toolbar)");
            Check(Source("UI/OverlayWindow.xaml").Contains("x:Name=\"TranslateClose\"") && code.Contains("Toolbar.SetTranslateEnabled(false)"), "its ✕ turns 原位翻译 off");
        }

        // ---- editor: the hover outline is the selection frame; a turned number keeps its arrow
        {
            var editor = Source("Annotations/AnnotationEditor.cs");
            Check(editor.Contains("DrawHoverFrame(dc, HoverElement, HoverLabel)") && editor.Contains("var frame = SelectionFrame(annotation.Bounds, unit);\n        var center = new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);\n        dc.PushTransform(new RotateTransform(annotation.Rotation"),
                "hovering draws the frame the selection will have (same size, same turn)");
            Check(editor.Contains("!ReferenceEquals(HoverElement, Selected)"), "… not over the selected one's frame");

            var number = new NumberAnnotation { Center = new Point(40, 40), Radius = 12, Tail = new Point(120, 40), Color = Colors.Red };
            var flat = Render(dc => AnnotationEditor.DrawItem(dc, number), 160, 160);
            number.Rotation = 90;
            var turned = Render(dc => AnnotationEditor.DrawItem(dc, number), 160, 160);
            byte A(byte[] px, int x, int y) => px[(y * 160 + x) * 4 + 3];
            Check(A(flat, 100, 40) > 200 && A(turned, 100, 40) > 200 && A(turned, 40, 100) < 10, "turning a number leaves its arrow where it points");
        }

        // ---- recorder: one fade in; 截屏 by 开始, the frame flashing white
        {
            var controller = Source("UI/Recording/RecorderController.cs");
            Check(controller.Contains("_frame.PrepareOpen();") && controller.IndexOf("_frame.PrepareOpen();", StringComparison.Ordinal) < controller.IndexOf("_frame.Show();", StringComparison.Ordinal),
                "the frame is see-through before it shows (it fades in once)");
            var bar = Source("UI/Recording/RecordBarWindow.xaml");
            Check(bar.IndexOf("x:Name=\"SnapButton\"", StringComparison.Ordinal) is > 0 and var snap && snap < bar.IndexOf("x:Name=\"StartButton\"", StringComparison.Ordinal) &&
                  bar.Contains("Style=\"{StaticResource IconCaptureMask}\"") && bar.Contains("<Setter Property=\"Height\" Value=\"30\" />\n            <Setter Property=\"Padding\" Value=\"10,0\" />"),
                "截屏 left of 开始, as big, with the 截屏 icon");
            Check(controller.Contains("ClipboardService.CopyImage(shot);") && controller.Contains("_frame?.Flash();") &&
                  Source("UI/Recording/RecordFrameWindow.cs").Contains("var color = _window.Flashing ? Colors.White"), "it copies the area and the frame flashes white");
        }

        // ---- the logo: coloured, no background, everywhere
        {
            var png = new PngBitmapDecoder(new Uri(Repo.File("Assets/markshot.png")), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var px = new byte[png.PixelWidth * png.PixelHeight * 4];
            new FormatConvertedBitmap(png, PixelFormats.Bgra32, null, 0).CopyPixels(px, png.PixelWidth * 4, 0);
            var opaque = Enumerable.Range(0, px.Length / 4).Where(i => px[i * 4 + 3] > 200).ToList();
            var coloured = opaque.Count(i => Math.Max(px[i * 4], Math.Max(px[i * 4 + 1], px[i * 4 + 2])) - Math.Min(px[i * 4], Math.Min(px[i * 4 + 1], px[i * 4 + 2])) > 60);
            Check(opaque.Count > 1000 && coloured > opaque.Count * 0.8, $"the logo is in colour ({coloured} of {opaque.Count})");
            Check(px[3] == 0 && px[(png.PixelWidth * png.PixelHeight / 2 + png.PixelWidth / 2) * 4 + 3] == 0, "… with no background");
            int Hue(int i) => px[i * 4 + 2] - px[i * 4]; // red - blue
            var topLeft = opaque.Where(i => i % png.PixelWidth < 60 && i / png.PixelWidth < 60).Average(Hue);
            var bottomRight = opaque.Where(i => i % png.PixelWidth > 196 && i / png.PixelWidth > 196).Average(Hue);
            Check(topLeft < bottomRight - 100, "a diagonal gradient (blue at the top left, warm at the bottom right)");
            Check(File.ReadAllBytes(Repo.File("Assets/markshot.ico")).SequenceEqual(File.ReadAllBytes(Repo.File("Assets/markshot_dark.ico"))),
                "the same logo on light and dark title bars (editor, settings, taskbar, tray)");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
