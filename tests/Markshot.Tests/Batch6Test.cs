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

static class Batch6Test
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

    static object? Clip(string name, params object[] args)
    {
        var type = typeof(AnnotationEditor).Assembly.GetType("Markshot.UI.AnnotationClipboard")!;
        var member = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public);
        if (member != null) return member.Invoke(null, args);
        return type.GetProperty(name, BindingFlags.Static | BindingFlags.Public)!.GetValue(null);
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

    public static void Run()
    {
        // ---- Tab variants and style conversion rules
        Check((AnnotationTool?)Ops("NextVariant", AnnotationTool.Rectangle) == AnnotationTool.Ellipse &&
              (AnnotationTool?)Ops("NextVariant", AnnotationTool.Watermark) == AnnotationTool.Text &&
              (AnnotationTool?)Ops("NextVariant", AnnotationTool.Number) == null, "Tab cycles the styles of one button");
        var rect = new RectAnnotation { Rect = new Rect(10, 10, 50, 30) };
        Check((bool)Ops("CanConvert", rect, AnnotationTool.Ellipse)! && !(bool)Ops("CanConvert", rect, AnnotationTool.Rectangle)! &&
              !(bool)Ops("CanConvert", rect, AnnotationTool.Arrow)!, "a selected rectangle converts to an ellipse only");

        // ---- element copy / paste
        Check(!(bool)Clip("CanCopy", new PenAnnotation())! && !(bool)Clip("CanCopy", new WatermarkAnnotation())! && (bool)Clip("CanCopy", rect)!,
            "pen strokes and the watermark are not copied");
        Clip("Copy", rect);
        var first = (Annotation)Clip("Paste", new Rect(0, 0, 400, 300))!;
        var second = (Annotation)Clip("Paste", new Rect(0, 0, 400, 300))!;
        Check(!ReferenceEquals(first, rect) && first.Bounds.X == 26 && second.Bounds.X == 42, $"pastes are offset copies ({first.Bounds.X}, {second.Bounds.X})");
        var far = (Annotation)Clip("Paste", new Rect(1000, 1000, 200, 200))!;
        Check(new Rect(1000, 1000, 200, 200).Contains(far.Bounds.TopLeft), "pasting into another canvas lands inside it");

        // ---- hotkey M = shape button
        Check(ToolHotkeys.TryMap(Key.M, new AppConfig(), out var shapeTool) && shapeTool == AnnotationTool.Rectangle, "M activates the shape tool");

        // ---- toolbar: picking a variant raises VariantChanged; hotkeys use the last style; same row height
        var bar = new AnnotationToolbar();
        var win = new Window { Content = bar, SizeToContent = SizeToContent.WidthAndHeight, Left = -4000, Top = -4000, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        win.Show();
        Pump(50);
        AnnotationTool? raised = null;
        bar.VariantChanged += tool => raised = tool;
        bar.SetTool(AnnotationTool.Rectangle);
        bar.PickVariant(AnnotationTool.Ellipse);
        Check(raised == AnnotationTool.Ellipse && bar.ChoiceFor(AnnotationTool.Rectangle) == AnnotationTool.Ellipse,
            "menu / Tab pick reaches the window as a style change; M then means ellipse");

        var heights = new List<string>();
        var values = new HashSet<double>();
        foreach (var tool in new[] { AnnotationTool.Rectangle, AnnotationTool.Pen, AnnotationTool.Text, AnnotationTool.Mosaic, AnnotationTool.Watermark, AnnotationTool.Eraser, AnnotationTool.Number })
        {
            bar.SetTool(tool);
            Pump(350);
            bar.UpdateLayout();
            var row = ((Border)bar.FindName("SettingsBar")).Child as FrameworkElement;
            heights.Add($"{tool}={row!.ActualHeight:F0}");
            values.Add(Math.Round(row.ActualHeight));
        }

        Check(values.Count == 1, "every tool's settings row has the same height: " + string.Join(" ", heights));

        // ---- toolbar hide is animated and can be reversed mid-way
        Motion.PopOut(bar);
        Pump(40);
        var hiding = Motion.IsHiding(bar) && bar.Visibility == Visibility.Visible;
        Motion.PopIn(bar);
        Pump(300);
        Check(hiding && !Motion.IsHiding(bar) && bar.Visibility == Visibility.Visible && bar.Opacity > 0.99,
            "toolbar hide plays and turns around when shown again");
        Motion.PopOut(bar);
        Pump(300);
        Check(bar.Visibility == Visibility.Collapsed, "toolbar collapses after the hide transition");
        win.Close();

        // ---- copy as PNG file
        var save = typeof(ClipboardService).GetMethod("SaveClipboardFile", BindingFlags.NonPublic | BindingFlags.Static)!;
        var image = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16);
        var path = (string?)save.Invoke(null, new object[] { image });
        Check(path != null && File.Exists(path) && path.EndsWith(".png"), "copying also writes a PNG for Explorer paste");

        // ---- logo: no dark outline left (edge pixels are white or soft shadow, never near-black opaque)
        var logo = new BitmapImage(new Uri(Repo.File(@"Assets\markshot.png")));
        var conv = new FormatConvertedBitmap(logo, PixelFormats.Bgra32, null, 0);
        var px = new byte[conv.PixelWidth * conv.PixelHeight * 4];
        conv.CopyPixels(px, conv.PixelWidth * 4, 0);
        var hardDark = 0;
        for (var i = 0; i < px.Length; i += 4)
            if (px[i + 3] > 200 && px[i] < 90 && px[i + 1] < 90 && px[i + 2] < 90) hardDark++;
        Check(hardDark == 0, $"logo has no opaque dark edge pixels ({hardDark})");

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
