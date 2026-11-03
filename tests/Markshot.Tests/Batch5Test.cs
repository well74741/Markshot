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

static class Batch5Test
{
    static int _fail;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) _fail++;
    }

    static object? Ops(string name, params object[] args)
    {
        var type = typeof(AnnotationEditor).Assembly.GetType("Markshot.UI.AnnotationOps")!;
        return type.GetMethod(name, BindingFlags.Static | BindingFlags.Public)!.Invoke(null, args);
    }

    static void Pump(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }
    }

    static byte[] Render(Action<DrawingContext> draw, int w, int h)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) draw(dc);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        return px;
    }

    public static void Run()
    {
        // ---- the watermark is painted after mosaics, the rest keeps its order
        var mark = new WatermarkAnnotation { Rect = new Rect(0, 0, 200, 100) };
        var rect = new RectAnnotation { Rect = new Rect(10, 10, 20, 20) };
        var mosaic = new MosaicAnnotation { Rect = new Rect(0, 0, 100, 100) };
        var arrow = new ArrowAnnotation { Start = new Point(1, 1), End = new Point(50, 50) };
        var order = AnnotationEditor.DrawOrder(new List<Annotation> { mark, rect, mosaic, arrow }).ToList();
        Check(order.SequenceEqual(new Annotation[] { rect, mosaic, mark, arrow }), "watermark painted right after the mosaic (not pixelated)");

        // ---- dragging the watermark shifts the pattern
        mark.Translate(15, -7);
        Check(mark.Shift == new Vector(15, -7), "watermark drag shifts its tiles");

        // ---- semi-transparent eraser fades instead of cutting
        var pen = new PenAnnotation { Color = Colors.Red, Thickness = 20 };
        pen.Points.AddRange(new[] { new Point(10, 50), new Point(190, 50) });
        pen.AddEraseStroke(new[] { new Point(100, 20), new Point(100, 80) }, 30, 0.5);
        var px = Render(dc => AnnotationEditor.DrawItem(dc, pen), 200, 100);
        int A(int x, int y) => px[(y * 200 + x) * 4 + 3];
        Console.WriteLine($"   alpha erased-half {A(100, 50)} untouched {A(40, 50)}");
        Check(A(40, 50) > 240 && A(100, 50) is > 100 and < 160, "50% eraser leaves the stroke half visible");
        Check(pen.MaskGeometry() == null && !pen.IsErasedAt(new Point(100, 50)), "partial erase does not make the element click-through");
        var full = pen.Clone();
        full.AddEraseStroke(new[] { new Point(60, 20), new Point(60, 80) }, 20, 1);
        Check(full.IsErasedAt(new Point(60, 50)) && full.EraseMasks!.Count == 2, "full eraser still cuts");

        // ---- a click on a handle does not resize (grab offset)
        var box = new RectAnnotation { Rect = new Rect(100, 100, 80, 40) };
        var frame = AnnotationEditor.SelectionFrame(box.Bounds, 1);
        var press = frame.BottomRight; // handle 4, drawn 4 px outside the element
        var grab = (Vector)Ops("ResizeGrab", box.Bounds, 0.0, 4, press)!;
        var target = (Rect)Ops("ResizeFromHandle", box.Bounds, 0.0, 4, press + grab, false)!;
        Check(target == box.Bounds, $"pressing a handle keeps the size ({target})");
        var rotated = (Vector)Ops("ResizeGrab", box.Bounds, 30.0, 4, press)!;
        Check(rotated.Length < 30, "grab offset is small for rotated elements too");

        // ---- right-click cancel: history restores the state of the gesture's start
        var history = new AnnotationHistory();
        var items = new List<Annotation> { new RectAnnotation { Rect = new Rect(0, 0, 10, 10) } };
        history.Arm(items);
        history.CommitArmed();
        items[0].Translate(50, 50);
        Check(history.CancelGesture(items) && ((RectAnnotation)items[0]).Rect.X == 0 && !history.CanUndo, "right-click during a drag puts it back (no undo step left)");
        history.Arm(items);
        Check(!history.CancelGesture(items), "right-click before anything moved changes nothing");

        // ---- update packages
        Check(UpdateService.Compare("0.8.10", "0.8.04") > 0 && UpdateService.Compare("0.8.04", "0.8.4") == 0 && UpdateService.Compare("0.9", "0.8.99") > 0, "version compare");
        var dir = Path.Combine(Path.GetTempPath(), "markshot-update-test");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        File.WriteAllText(Path.Combine(dir, "Markshot-Setup-0.8.02.exe"), "");
        Check(UpdateService.FindNewer(dir) == null, "older package is ignored");
        File.WriteAllText(Path.Combine(dir, "Markshot-Setup-0.8.99.exe"), "");
        File.WriteAllText(Path.Combine(dir, "Markshot-Setup-0.8.98.exe"), "");
        Check(UpdateService.FindNewer(dir)?.Version == "0.8.99", $"newest package found ({UpdateService.FindNewer(dir)?.Version})");

        // ---- toolbar: chosen variant replaces the button; text button holds the watermark; eraser row
        var bar = new AnnotationToolbar();
        var win = new Window { Content = bar, SizeToContent = SizeToContent.WidthAndHeight, Left = -4000, Top = -4000, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        win.Show();
        Pump(50);
        bar.SetTool(AnnotationTool.Ellipse);
        var shape = (RadioButton)bar.FindName("ToolShape");
        Check((string)shape.Tag == "Ellipse" && ((Border)bar.FindName("ToolShapeIcon")).Style == (Style)Application.Current.Resources["IconEllipseMask"],
            "shape button turns into the ellipse after choosing it");
        bar.SetTool(AnnotationTool.MosaicBrush);
        Check(((Border)bar.FindName("ToolMosaicIcon")).Style == (Style)Application.Current.Resources["IconMosaicBrushMask"], "brush mosaic shows the round mosaic icon");
        Pump(400);
        Check(((FrameworkElement)bar.FindName("WidthGroup")).IsVisible && ((TextBlock)bar.FindName("WidthLabel")).Text == "粗细", "brush mosaic has a size slider");
        bar.SetTool(AnnotationTool.Eraser);
        Pump(400);
        Check(((FrameworkElement)bar.FindName("WidthGroup")).IsVisible && ((FrameworkElement)bar.FindName("OpacityGroup")).IsVisible, "eraser row: 粗细 + 透明度");
        ((Slider)bar.FindName("OpacitySlider")).Value = 40;
        Check(Math.Abs(bar.EraserOpacity - 0.4) < 0.001, "eraser opacity slider");
        Check(bar.FindName("ToolWatermark") == null && bar.FindName("StrokeToggle") == null, "no separate watermark button / stroke toggle");
        bar.SetTool(AnnotationTool.Text);
        ((Slider)bar.FindName("StrokeSlider")).Value = 2;
        Check(bar.StrokeEnabled, "stroke slider > 0 turns the outline on");
        ((Slider)bar.FindName("StrokeSlider")).Value = 0;
        Check(!bar.StrokeEnabled, "stroke 0 = off");

        // hidden toolbar: a change while hidden does not animate when shown again
        bar.Visibility = Visibility.Collapsed;
        bar.DeactivateTools();
        Pump(30);
        bar.Visibility = Visibility.Visible;
        bar.UpdateLayout();
        var host = (FrameworkElement)bar.FindName("SettingsHost");
        Check(host.ActualHeight < 0.5, $"settings row closed while hidden shows closed at once ({host.ActualHeight:F1})");
        win.Close();

        // ---- the pin canvas clips elements to the image
        var editor = new AnnotationEditor { Width = 100, Height = 100, Source = BitmapSource.Create(50, 50, 96, 96, PixelFormats.Bgra32, null, new byte[50 * 50 * 4], 200) };
        var big = new RectAnnotation { Rect = new Rect(10, 10, 80, 80), Fill = true, Color = Colors.Blue };
        editor.Items.Add(big);
        editor.Measure(new Size(100, 100)); editor.Arrange(new Rect(0, 0, 100, 100));
        var rtb = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(editor);
        var e = new byte[100 * 100 * 4];
        rtb.CopyPixels(e, 400, 0);
        Check(e[(30 * 100 + 30) * 4 + 3] > 200 && e[(70 * 100 + 70) * 4 + 3] == 0, "elements are not drawn outside the pin image");

        // ---- OCR window target language: auto 中↔英 + dropdown
        var ocr = new TranslationWindow(null, autoTranslate: false) { Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false };
        ocr.Show();
        var combo = (ComboBox)ocr.FindName("TargetBox");
        var resolve = typeof(TranslationWindow).GetMethod("ResolveTarget", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((TextBox)ocr.FindName("SourceBox")).Text = "这是一段中文说明文字";
        var zh = (string)resolve.Invoke(ocr, null)!;
        ((TextBox)ocr.FindName("SourceBox")).Text = "This is an English sentence";
        var en = (string)resolve.Invoke(ocr, null)!;
        Check(zh == "English" && en == "简体中文" && combo.Items.Count >= 8, $"auto target: 中文→{zh}, English→{en}; {combo.Items.Count} languages");
        combo.SelectedItem = "日本語";
        Check((string)resolve.Invoke(ocr, null)! == "日本語", "picked language wins over auto");
        ocr.Close();

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
