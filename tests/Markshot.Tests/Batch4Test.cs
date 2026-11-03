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
using Markshot.UI;

static class Batch4Test
{
    static int _fail;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) _fail++;
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

    static void Save(Visual visual, int w, int h, string name)
    {
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(name);
        enc.Save(f);
        Console.WriteLine("   wrote " + name);
    }

    public static void Run()
    {
        // ---- toolbar: watermark is its own tool; mosaic types are 区域 / 画笔 only
        var stack = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(90, 100, 110)) };
        var bars = new List<AnnotationToolbar>();
        foreach (var tool in new[] { AnnotationTool.Mosaic, AnnotationTool.Watermark, AnnotationTool.Pen })
        {
            var bar = new AnnotationToolbar { Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left };
            stack.Children.Add(bar);
            bars.Add(bar);
        }

        var win = new Window { Content = stack, SizeToContent = SizeToContent.WidthAndHeight, Left = -4000, Top = -4000, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        win.Show();
        Pump(50);
        bars[0].SetTool(AnnotationTool.Mosaic);
        bars[1].SetTool(AnnotationTool.Watermark);
        // (all three bars share the "Tool" radio group in this one window, so read the state right away)
        // The watermark now lives under the text button, which then shows the watermark icon.
        var watermarkChecked = ((RadioButton)bars[1].FindName("ToolText")).IsChecked == true && (string)((RadioButton)bars[1].FindName("ToolText")).Tag == "Watermark";
        bars[2].SetTool(AnnotationTool.Pen);
        Pump(450); // let the slide-open finish
        stack.UpdateLayout();

        var mosaicTypes = ((Panel)((Border)((Panel)bars[0].FindName("MosaicVariants")).Children.OfType<Border>().First()).Child).Children.OfType<RadioButton>().Select(r => (string)r.Content).ToList();
        Check(string.Join("|", mosaicTypes) == "区域|画笔", $"mosaic 类型 = [{string.Join("|", mosaicTypes)}]");
        Check(watermarkChecked,
            "watermark sits on the text button (checked, button switched to 水印)");
        var wmVisible = new[] { "WidthGroup", "AngleGroup", "OpacityGroup", "ColorSection" }
            .Select(n => (n, ((FrameworkElement)bars[1].FindName(n)).IsVisible)).ToList();
        Check(wmVisible.All(v => v.IsVisible) && !((FrameworkElement)bars[1].FindName("MosaicVariants")).IsVisible,
            "watermark row shows 大小/倾斜/透明度/颜色 (no mosaic type): " + string.Join(",", wmVisible.Select(v => $"{v.n}={v.IsVisible}")));
        var host = (FrameworkElement)bars[1].FindName("SettingsHost");
        Check(host.ActualHeight > 20 && host.ActualWidth > 300, $"settings row is open ({host.ActualWidth:F0}x{host.ActualHeight:F0})");
        Save(stack, (int)stack.ActualWidth, (int)stack.ActualHeight, "toolbar_b4.png");

        // collapse animates: right after deactivation it is still partly visible, then 0
        var penHost = (FrameworkElement)bars[2].FindName("SettingsHost");
        Console.WriteLine($"   pen host before {penHost.ActualHeight:F1}");
        bars[2].DeactivateTools();
        stack.UpdateLayout();
        var immediate = penHost.ActualHeight;
        var t0 = DateTime.Now;
        var samples = new List<string>();
        double mid = 0;
        for (var i = 0; i < 12; i++)
        {
            Pump(25);
            stack.UpdateLayout();
            samples.Add($"{(DateTime.Now - t0).TotalMilliseconds:F0}ms:{penHost.ActualHeight:F1}");
            if (i == 0) mid = penHost.ActualHeight;
        }
        Console.WriteLine("   " + string.Join(" ", samples));
        Pump(400);
        stack.UpdateLayout();
        Check(immediate > 30 && penHost.ActualHeight < 0.5, $"settings row slides closed, not instantly ({immediate:F1} → {penHost.ActualHeight:F1})");

        // width eases when switching to a tool with a wider row
        var host0 = (FrameworkElement)bars[0].FindName("SettingsHost");
        // A short bar (pin mode, narrow row) so the text row is wider than the buttons above it.
        bars[0].SetMode(AnnotationToolbarMode.Pin);
        bars[0].SetTool(AnnotationTool.Number);
        Pump(450);
        stack.UpdateLayout();
        var startWidth = host0.ActualWidth;
        bars[0].SetTool(AnnotationTool.Text);
        var widths = new System.Collections.Generic.List<double>();
        for (var i = 0; i < 25; i++)
        {
            Pump(20);
            stack.UpdateLayout();
            widths.Add(host0.ActualWidth);
        }

        var endWidth = widths[^1];
        var midWidth = widths.FirstOrDefault(w => w > startWidth + 1 && w < endWidth - 1);
        Check(endWidth > startWidth + 5 && midWidth > 0, $"settings row width eases ({midWidth:F0} → {host0.ActualWidth:F0})");
        win.Close();

        // ---- rotation grips hug the corners and rotate; text edit frame moves in the band only
        var box = new RectAnnotation { Rect = new Rect(100, 100, 200, 100) };
        var frame = AnnotationEditor.SelectionFrame(box.Bounds, 1);
        var grip = AnnotationEditor.RotateGripCenters(frame, 1)[0];
        Check(AnnotationEditor.HitFrame(box, grip, 1).Rotate, "curved grip outside the top-left handle rotates");
        Check(AnnotationEditor.HitFrame(box, grip, 1, handlesOnly: true).Rotate, "grips work on a freshly drawn element too");
        var text = new TextAnnotation { Position = new Point(50, 50), Text = "Hello", FontSize = 30 };
        var tb = text.Bounds;
        Check(!AnnotationEditor.HitEditFrame(text, new Point(tb.X + tb.Width / 2, tb.Y + tb.Height / 2), 1).Any, "inside the text stays with the caret");
        Check(AnnotationEditor.HitEditFrame(text, new Point(tb.X + tb.Width * 0.3, tb.Top - 6), 1).Inside, "band just around the text moves it");
        Check(AnnotationEditor.HitEditFrame(text, AnnotationEditor.SelectionFrame(tb, 1).BottomRight, 1).IsHandle, "text edit frame handles resize");

        // frame rendering with grips
        var editor = new AnnotationEditor { Width = 420, Height = 260 };
        editor.Items.Add(box);
        editor.Selected = box;
        box.Rotation = 12;
        editor.Measure(new Size(420, 260)); editor.Arrange(new Rect(0, 0, 420, 260));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(200, 205, 210)), null, new Rect(0, 0, 420, 260));
            dc.DrawRectangle(new VisualBrush(editor), null, new Rect(0, 0, 420, 260));
        }
        Save(dv, 420, 260, "grips.png");

        // ---- freehand strokes are not auto-selected; the restore stack exists
        var overlaySrc = string.Concat(Directory.GetFiles(Repo.File("UI"), "OverlayWindow*.cs").Select(File.ReadAllText));
        Check(overlaySrc.Contains("annotation is not PenAnnotation and not MosaicBrushAnnotation"), "pen / brush mosaic strokes are not selected after drawing");
        Check(typeof(PinWindow).GetMethod("Snapshot") != null && typeof(PinWindow).GetMethod("RestoreView") != null, "pins snapshot their full state for restore");

        // ---- OCR window
        var ocr = new TranslationWindow(null, autoTranslate: false) { Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false, Width = 900, Height = 520 };
        ocr.Show();
        ((TextBox)ocr.FindName("SourceBox")).Text = "Markshot 文字识别示例\n第二行 second line";
        Pump(200);
        ocr.UpdateLayout();
        Check(ocr.Title == "Markshot 文字识别" && ((TextBlock)ocr.FindName("HeaderTitle")).Text == "文字识别", "OCR mode titles");
        var content = (Grid)ocr.Content;
        content.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        content.Measure(new Size(900, 480));
        content.Arrange(new Rect(0, 0, 900, 480));
        content.UpdateLayout();
        Save(content, 900, 480, "ocr_window.png");
        ocr.Close();

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
