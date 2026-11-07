using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markshot.Annotations;
using Markshot.UI;

static class Batch3Test
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

    static byte[] Pixels(Action<DrawingContext> draw, int w, int h)
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
        var screen = BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgra32, null, new byte[400 * 300 * 4], 400 * 4);

        // ---- toolbar: opacity per tool, preview, mosaic type segments, watermark tilt
        var toolbar = new AnnotationToolbar();
        var host = new Window { Content = toolbar, Width = 900, Height = 160, Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show();
        toolbar.SetTool(AnnotationTool.Rectangle);
        toolbar.SetOpacity(0.5);
        toolbar.SetTool(AnnotationTool.Pen);
        Check(Math.Abs(toolbar.ElementOpacity - 1) < 0.001, "another tool keeps its own opacity (pen 100%)");
        toolbar.SetTool(AnnotationTool.Rectangle);
        Check(Math.Abs(toolbar.ElementOpacity - 0.5) < 0.001, "rectangle remembers its preset opacity (50%)");
        host.UpdateLayout();
        var hint = (TextBlock)toolbar.FindName("OpacityHint");
        var inner = (System.Windows.Shapes.Ellipse)toolbar.FindName("CustomColorInner");
        Check(hint.Visibility == Visibility.Visible && hint.Text == "50%", $"opacity shows next to the colours ({hint.Text})");
        Check(((SolidColorBrush)inner.Fill).Color.A is >= 126 and <= 129, "colour preview carries the alpha");

        var rect = (Annotation)Ops("Create", AnnotationTool.Rectangle, new Point(10, 10), toolbar, 1, screen)!;
        Check(Math.Abs(rect.Opacity - 0.5) < 0.001, "new element takes the tool opacity");

        toolbar.SetTool(AnnotationTool.Mosaic);
        host.UpdateLayout();
        var variants = (Panel)toolbar.FindName("MosaicVariants");
        var labels = string.Join("|", variants.Children.OfType<RadioButton>().Select(r => r.Tag));
        Check(variants.Visibility == Visibility.Visible && labels == "Mosaic|MosaicBrush", $"mosaic tool shows its two style icons [{labels}]");
        Check(((FrameworkElement)toolbar.FindName("LevelGroup")).Visibility == Visibility.Visible, "mosaic tool shows 程度");
        toolbar.SetTool(AnnotationTool.Watermark);
        host.UpdateLayout();
        var visible = new[] { "WidthGroup", "AngleGroup", "ColorSection" }.All(n => ((FrameworkElement)toolbar.FindName(n)).Visibility == Visibility.Visible);
        Check(visible && ((FrameworkElement)toolbar.FindName("OpacityGroup")).Visibility != Visibility.Visible && ((TextBlock)toolbar.FindName("WidthLabel")).Text == "大小", "watermark shows 大小 / 倾斜 / 颜色, no opacity slider (0.8.59)");
        ((Slider)toolbar.FindName("AngleSlider")).Value = 30;
        Check(Math.Abs(toolbar.WatermarkAngle - 30) < 0.001, "tilt slider sets the watermark angle");
        var mark = new WatermarkAnnotation { Rect = new Rect(0, 0, 400, 300) };
        typeof(AnnotationEditor).Assembly.GetType("Markshot.UI.AnnotationStyler")!.GetMethod("Apply")!.Invoke(null, new object[] { mark, toolbar, screen });
        Check(Math.Abs(mark.Angle - 30) < 0.001, "styler applies the tilt");
        host.Close();

        // ---- every element renders with its opacity (strokes included)
        var pen = new PenAnnotation { Color = Colors.Red, Thickness = 10, Opacity = 0.5 };
        pen.Points.AddRange(new[] { new Point(10, 50), new Point(190, 50) });
        var px = Pixels(dc => AnnotationEditor.DrawItem(dc, pen), 200, 100);
        var a = px[(50 * 200 + 100) * 4 + 3];
        Check(a is >= 120 and <= 135, $"pen stroke at 50% renders half transparent (alpha {a})");

        // ---- wheel: thickness / size of the selection; Ctrl step for opacity
        var line = new LineAnnotation { Thickness = 3 };
        var size = (double?)Ops("WheelSize", line, 120, screen);
        Check(size == 4 && line.Thickness == 4, "wheel up thickens a line by one per notch");
        var brush = new MosaicBrushAnnotation { Thickness = 24 };
        Ops("WheelSize", brush, -120, screen);
        Check(brush.Thickness == 22, "wheel down thins the brush mosaic");
        Check((double)Ops("WheelOpacity", 1.0, -120)! == 0.95 && (double)Ops("WheelOpacity", 0.05, -120)! == 0.05, "Ctrl+wheel opacity steps 5% and never vanishes");

        // ---- number badge resizes around its centre (no sliding)
        var number = new NumberAnnotation { Center = new Point(100, 100), Radius = 14 };
        var nb = number.Bounds;
        Ops("Resize", number, new Rect(nb.X, nb.Y, nb.Width + 20, nb.Height + 20), 4);
        Check(number.Center == new Point(100, 100) && number.Radius > 20, $"number grows around its centre (r={number.Radius:F1})");

        // ---- pressing inside a fresh pen stroke's frame keeps drawing
        var stroke = new PenAnnotation { Thickness = 4 };
        stroke.Points.AddRange(new[] { new Point(0, 0), new Point(100, 100) });
        Check(!(bool)Ops("InsideMoves", stroke, AnnotationTool.Pen, new Point(80, 10), 7.0)!, "pen tool: empty area inside a stroke's frame draws");
        Check((bool)Ops("InsideMoves", stroke, AnnotationTool.Pen, new Point(50, 50), 7.0)!, "pen tool: on the stroke itself moves it");
        Check((bool)Ops("InsideMoves", new RectAnnotation { Rect = new Rect(0, 0, 100, 100) }, AnnotationTool.Rectangle, new Point(50, 50), 7.0)!, "rectangle inside still moves");

        // ---- a just-created element (tool still active) only answers to its handle squares
        var fresh = new RectAnnotation { Rect = new Rect(100, 100, 200, 100) };
        var fr = AnnotationEditor.SelectionFrame(fresh.Bounds, 1);
        object Hit(bool isFresh, AnnotationTool tool, Point p) => Ops("SelectionHit", fresh, isFresh, tool, p, 1.0)!;
        Check(((FrameHit)Hit(true, AnnotationTool.Rectangle, fr.TopLeft)).IsHandle, "fresh element: handle square still resizes");
        Check(!((FrameHit)Hit(true, AnnotationTool.Rectangle, new Point(fr.Left + 50, fr.Top))).Any, "fresh element: edge band draws instead of resizing");
        Check(!((FrameHit)Hit(true, AnnotationTool.Rectangle, fr.TopLeft + new Vector(-30, 4))).Any, "fresh element: just outside the frame draws");
        Check(((FrameHit)Hit(true, AnnotationTool.Rectangle, fr.TopLeft + new Vector(-9, -9))).Rotate, "fresh element: the corner grip still rotates");
        Check(!((FrameHit)Hit(true, AnnotationTool.Number, fr.TopLeft)).Any, "fresh element with the number tool: next badge can go right next to it");
        Check(((FrameHit)Hit(false, AnnotationTool.Rectangle, new Point(fr.Left + 50, fr.Top))).IsHandle, "explicit selection keeps the full frame");
        Check(typeof(PinWindow).GetMethod("CloseImmediately") != null, "pins can close without the fade (pin limit / exit)");

        // ---- slider with IsMoveToPointEnabled: press on the track keeps dragging
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 10, Width = 200, IsMoveToPointEnabled = true, Style = (Style)Application.Current.Resources[typeof(Slider)] };
        var sliderHost = new Window { Content = slider, Width = 260, Height = 80, Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        sliderHost.Show();
        sliderHost.Activate();
        slider.UpdateLayout();
        slider.ApplyTemplate(); slider.UpdateLayout();
        Console.WriteLine($"   slider template={slider.Template != null} loaded={slider.IsLoaded}");
        var track = (Track)slider.Template!.FindName("PART_Track", slider);
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = track };
        track.RaiseEvent(args);
        var dragging = track.Thumb.IsDragging || slider.IsMouseCaptured;
        Console.WriteLine($"   thumb dragging={track.Thumb.IsDragging} slider captured={slider.IsMouseCaptured}");
        Check(dragging, "move-to-point slider: track press keeps dragging (thumb or fallback capture)");
        track.Thumb.CancelDrag();
        slider.ReleaseMouseCapture();
        sliderHost.Close();

        // ---- motion: the shared curve
        var y0 = Markshot.Core.Motion.Bezier(0); var y1 = Markshot.Core.Motion.Bezier(1); var yh = Markshot.Core.Motion.Bezier(0.5);
        var monotonic = Enumerable.Range(0, 100).All(i => Markshot.Core.Motion.Bezier((i + 1) / 100.0) >= Markshot.Core.Motion.Bezier(i / 100.0));
        Check(y0 == 0 && y1 == 1 && Math.Abs(yh - 0.774) < 0.01 && monotonic, $"cubic-bezier(0.4,0,0.2,1): f(0.5)={yh:F3}, monotonic");

        // ---- compact menu items
        var menu = new ContextMenu { Placement = PlacementMode.Absolute, HorizontalOffset = -4000, VerticalOffset = -4000 };
        foreach (var label in new[] { "矩形", "椭圆" }) menu.Items.Add(new MenuItem { Header = label, IsChecked = label == "矩形" });
        menu.IsOpen = true;
        menu.UpdateLayout();
        Console.WriteLine($"   menu width {menu.ActualWidth:F0}");
        Check(menu.ActualWidth is > 40 and < 100, $"chevron menu is compact ({menu.ActualWidth:F0} DIP wide)");
        menu.IsOpen = false;

        // ---- version
        Check(Markshot.Core.AppVersion.Display.StartsWith("0.8."), $"display version {Markshot.Core.AppVersion.Display}");

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
