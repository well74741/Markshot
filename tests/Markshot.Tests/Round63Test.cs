using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.63: 高斯模糊 as a mosaic type, toolbar placement, pulled-through mirroring, 抠除 through the layers, settings defaults, the floating button, the translation switch.</summary>
static class Round63Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file));

    static BitmapSource Stripes(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var v = (byte)(((x / 4 + y / 4) % 2) * 255);
            px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255;
        }

        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    static BitmapSource LeftRed(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            if (x < w / 2) { px[i + 2] = 255; } else { px[i] = 255; }
            px[i + 3] = 255;
        }

        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    static (byte B, byte G, byte R) Pixel(BitmapSource image, int x, int y)
    {
        var px = new byte[4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2]);
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

    static readonly Type Ops = typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps")!;

    /// <summary>AnnotationOps.ResizeThrough (internal): the flip state goes in and comes back.</summary>
    static void Through(Annotation item, Rect start, int handle, Point pointer, ref (bool, bool) flipped)
    {
        var args = new object[] { item, start, handle, pointer, false, flipped };
        Ops.GetMethod("ResizeThrough", Any)!.Invoke(null, args);
        flipped = ((bool, bool))args[5];
    }

    static object? Prop(object target, string name) => target.GetType().GetProperty(name, Any)!.GetValue(target);

    static byte Alpha(byte[] px, int w, int x, int y) => px[(y * w + x) * 4 + 3];

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- 高斯模糊: a mosaic type of its own (strength + soft edge); 色块 stays hard
        {
            var source = Stripes(64, 64);
            var (blocks, _) = MosaicBuilder.BuildCanvas(source, 8);
            var (gauss, area) = MosaicBuilder.BuildCanvas(source, 8, 6, gaussian: true);
            Check(gauss != null && area.Width == 64 && gauss.PixelWidth > 8, "高斯模糊 blurs the picture itself, not blocks (0.8.64: a strong blur on a smaller copy)");
            Check(blocks != null && blocks.PixelWidth == 8, "色块 stays blocks");

            var bar = new AnnotationToolbar();
            bar.SetTool(AnnotationTool.Mosaic);
            var level = (FrameworkElement)bar.FindName("LevelGroup");
            var blur = (FrameworkElement)bar.FindName("BlurGroup");
            Check(!bar.MosaicGaussian && level.Visibility == Visibility.Visible && blur.Visibility == Visibility.Collapsed, "色块: only its 程度");
            bar.GetType().GetMethod("MosaicKind_Click", Any)!.Invoke(bar, new object[] { bar.FindName("KindGaussianToggle"), new RoutedEventArgs() });
            Check(bar.MosaicGaussian && level.Visibility == Visibility.Collapsed && blur.Visibility == Visibility.Visible &&
                  bar.FindName("MosaicFeatherSlider") is Slider, "模糊: its 程度 and 柔边");
            ((Slider)bar.FindName("MosaicFeatherSlider")).Value = 20;
            Check(Math.Abs(bar.MosaicFeather - 20) < 0.01, "柔边 sets the soft edge");
            Check(((ToggleButton)bar.FindName("KindGaussianToggle")).IsChecked == true && ((ToggleButton)bar.FindName("KindPixelToggle")).IsChecked == false, "the type toggles show the choice");

            var pattern = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 90, 90, 90, 255 }, 4);
            var pixel = new MosaicAnnotation { Rect = new Rect(20, 20, 60, 60), Pattern = pattern, PatternRect = new Rect(0, 0, 100, 100), Blur = 12 };
            var p = Render(pixel.Draw, 100, 100);
            Check(Alpha(p, 100, 84, 50) == 0 && Alpha(p, 100, 78, 50) == 255, "色块 (even with an old blur value) keeps its hard edge");
            var soft = new MosaicAnnotation { Rect = new Rect(20, 20, 60, 60), Pattern = pattern, PatternRect = new Rect(0, 0, 100, 100), Blur = 12, Gaussian = true, Feather = 20 };
            var s = Render(soft.Draw, 100, 100);
            Check(Alpha(s, 100, 50, 50) > 245 && Alpha(s, 100, 85, 50) > 5 && Alpha(s, 100, 85, 50) < Alpha(s, 100, 77, 50), "模糊 fades out across its edge (柔边)");
        }

        // ---- capture toolbar: under the area's left edge, growing right
        {
            var code = Source("UI/OverlayWindow.Selection.cs");
            var place = code[code.IndexOf("private void PositionToolbar()", StringComparison.Ordinal)..];
            place = place[..place.IndexOf("PositionTranslateStatus();", StringComparison.Ordinal)];
            Check(place.Contains("var left = selection.Left;") && !place.Contains("selection.Right - size.Width"), "the capture toolbar starts at the area's left edge (a wider row grows to the right)");
            Check(place.Contains("ActualWidth - size.Width - 8"), "… and moves left only at the screen's end");
            var pin = Source("UI/PinWindow.Toolbar.cs");
            Check(pin.Contains("screen.Right - width") && pin.Contains("_toolbarWindow.SizeChanged += (_, _) => PlaceToolbar();"), "a pin's bar stays whole on the screen when it grows");
        }

        // ---- a side pulled past the other turns the element over
        {
            var picture = new ImageAnnotation { Rect = new Rect(100, 100, 40, 20), Image = LeftRed(40, 20) };
            var start = picture.Rect;
            var flipped = (false, false);
            Through(picture, start, 3, new Point(120, 110), ref flipped); // right edge pulled in
            Check(!flipped.Item1 && Pixel(picture.Image!, 2, 10).R == 255, "pulled in, not across: as it was");
            Through(picture, start, 3, new Point(70, 110), ref flipped); // right edge past the left one
            Check(flipped.Item1 && Math.Abs(picture.Rect.Right - 100) < 0.5 && Math.Abs(picture.Rect.Left - 70) < 0.5, $"past the left edge the box lies on the other side ({picture.Rect})");
            Check(Pixel(picture.Image!, 2, 10).B == 255 && Pixel(picture.Image!, 37, 10).R == 255, "… and the picture is mirrored");
            Through(picture, start, 3, new Point(130, 110), ref flipped); // back again
            Check(!flipped.Item1 && Pixel(picture.Image!, 2, 10).R == 255, "back across: turned round again");

            var line = new ArrowAnnotation { Start = new Point(10, 10), End = new Point(50, 30) };
            var lineFlip = (false, false);
            Through(line, line.Bounds, 5, new Point(30, 0), ref lineFlip); // bottom past the top
            Check(lineFlip.Item2 && line.Start.Y > line.End.Y, "an arrow pulled through turns upside down");

            var overlay = Source("UI/EditorOverlay.cs");
            Check(overlay.Contains("TransformMirrorX = _mirrorOrigin.X != crossed.X") && Source("UI/ImageEditorWindow.cs").Contains("AnnotationOps.Mirror(picture, horizontal: true)"),
                "the editor's 调节框 turns over the same way");
        }

        // ---- 抠除 cuts through every layer below: the hole moves with the element
        {
            var below = new RectAnnotation { Rect = new Rect(10, 10, 80, 80), Fill = true, Color = Colors.Red };
            var cut = new CutoutAnnotation { Thickness = 20 };
            cut.Points.Add(new Point(30, 50));
            cut.Points.Add(new Point(70, 50));
            var above = new RectAnnotation { Rect = new Rect(40, 40, 20, 20), Fill = true, Color = Colors.Blue };
            var items = new List<Annotation> { below, cut, above };
            Cutouts.Bake(items, cut);
            Check(cut.Baked && below.EraseMasks is { Count: 1 } && above.EraseMasks == null, "a finished hole goes into the elements below it, not above");
            below.Translate(100, 0);
            var px = Render(dc => AnnotationEditor.DrawLayers(dc, items), 220, 100);
            Check(Alpha(px, 220, 150, 50) == 0, "moved: the hole goes with it");
            Check(Alpha(px, 220, 120, 20) > 250, "the rest of it is there");
            Check(Alpha(px, 220, 30, 50) == 0, "nothing left where it was (no hole floating in place)");
            Check(Alpha(px, 220, 50, 50) > 250, "what was drawn after is not cut");
        }

        // ---- settings: light, 中 on a first start; 快速双击 under 快捷键 / 贴图窗口; the plate slides with the knob
        {
            var fresh = new AppConfig();
            Check(fresh.Theme == "Light" && fresh.UiSize == "Medium", "a first start is light, toolbar size 中");
            fresh.EnsureDefaults();
            Check(fresh.UiSize == "Medium", "… and stays so (no old-size migration on a new config)");
            var xaml = Source("UI/SettingsWindow.xaml");
            var pinCard = xaml.IndexOf("Text=\"贴图窗口\"", StringComparison.Ordinal);
            var quick = xaml.IndexOf("x:Name=\"QuickDoubleClickCheck\"", StringComparison.Ordinal);
            var translatePage = xaml.IndexOf("x:Name=\"PageTranslate\"", StringComparison.Ordinal);
            Check(pinCard > 0 && quick > pinCard && quick < translatePage, "快速双击复制 sits in 快捷键 → 贴图窗口");

            var config = new AppConfig { Theme = "Dark", FloatActionOrder = "Capture,Record,Ocr,Editor,History,AddPin,TogglePins" };
            var settings = new SettingsWindow(config) { Left = -4000, Top = -4000, ShowActivated = false };
            settings.Show();
            Pump(250);
            var plate = (Border)settings.FindName("FloatClickPlate");
            settings.GetType().GetMethod("KnobAt", Any)!.Invoke(settings, new object[] { 3 * 28 + 7.0 }); // dragging over the 4th button
            Check(Math.Abs(Canvas.GetLeft(plate) - 3 * 28) < 1, $"dragging the knob slides the plate along ({Canvas.GetLeft(plate):0})");
            settings.GetType().GetMethod("KnobAt", Any)!.Invoke(settings, new object[] { 40.0 }); // between two buttons
            Check(Math.Abs(Canvas.GetLeft(plate) - (40 + 7 - 14)) < 1, "… smoothly, between the buttons too");
            settings.Close();
        }

        // ---- the floating button: pressed in; dragged, the row folds with a quick transition
        {
            var config = new AppConfig { FloatActionHidden = "" };
            var button = new FloatButtonWindow(config, _ => { }, () => { }, () => { }) { FullscreenOverride = () => false, PointerOnItOverride = () => true };
            button.Show();
            Pump(200);
            button.GetType().GetMethod("Press", Any)!.Invoke(button, new object[] { true });
            Pump(150);
            Check((double)Prop(button, "PressScale")! < 0.95, $"pressed: the button gives a little ({(double)Prop(button, "PressScale")!:0.00})");
            button.GetType().GetMethod("Press", Any)!.Invoke(button, new object[] { false });
            Pump(200);
            Check((double)Prop(button, "PressScale")! > 0.99, "let go: back to its size");
            button.GetType().GetMethod("Expand", Any)!.Invoke(button, null);
            Pump(200);
            button.GetType().GetMethod("Collapse", Any, new[] { typeof(bool) })!.Invoke(button, new object[] { true });
            Pump(20);
            Check((bool)Prop(button, "Folding")!, "dragged / left: the row draws in (not gone in one frame)");
            Pump(300);
            Check(!(bool)Prop(button, "Folding")! && !button.IsExpanded, "… then it is gone");
            Check(Source("UI/FloatButtonWindow.cs").Contains("_holdMoveUntil = Environment.TickCount64 + FoldMs") /* (0.8.65: folds first, then moves) */, "a drag folds it with the transition");
            button.Close();
        }

        // ---- 原位翻译: the [原文 | 译文] switch comes back at once; pins have it too
        {
            var overlay = Source("UI/OverlayWindow.Translation.cs");
            Check(overlay.Contains("if (TranslateStatus.Visibility != Visibility.Visible && _translated != null && _translateCts == null)"), "switched off and on: the bar and its switch show again at once");
            var pin = new PinWindow(Stripes(120, 80), -4000, -4000, Stripes(120, 80), null) { Left = -4000, Top = -4000, ShowActivated = false };
            pin.Show();
            Pump(300);
            var original = LeftRed(120, 80);
            pin.AdoptTranslation(original, "English", "中文");
            Pump(50);
            Check((bool)pin.GetType().GetProperty("LanguageSwitchShown", Any)!.GetValue(pin)!,
                "a translated pin shows [原文 | 译文] (0.8.64: in its own window above the top right corner)");
            pin.GetType().GetMethod("PinSourceLanguage_Click", Any)!.Invoke(pin, new object?[] { pin, new RoutedEventArgs() });
            Pump(50);
            Check(ReferenceEquals(pin.GetType().GetField("_baseImage", Any)!.GetValue(pin), original) &&
                  ((ToggleButton)pin.FindName("PinSourceButton")).IsChecked == true, "原文: the original shows");
            pin.GetType().GetMethod("PinTargetLanguage_Click", Any)!.Invoke(pin, new object?[] { pin, new RoutedEventArgs() });
            Pump(50);
            Check(!ReferenceEquals(pin.GetType().GetField("_baseImage", Any)!.GetValue(pin), original) &&
                  ((ToggleButton)pin.FindName("PinTargetButton")).IsChecked == true, "译文: back to the translation");
            Check(Source("App.xaml.cs").Contains("pin.AdoptTranslation(original"), "pinned from a translated capture, the pin keeps both");
            pin.Close();
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
