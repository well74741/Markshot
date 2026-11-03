using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Services;
using Markshot.UI;

static class Batch2Test
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

    public static void Run()
    {
        // ---- adjustment frame: handles / edges resize, outside corners rotate, inside moves
        var box = new RectAnnotation { Rect = new Rect(100, 100, 200, 100) };
        var frame = AnnotationEditor.SelectionFrame(box.Bounds, 1);
        Check(AnnotationEditor.HitFrame(box, frame.TopLeft, 1).Handle == 0, "corner handle resizes");
        Check(AnnotationEditor.HitFrame(box, new Point(frame.Left + 50, frame.Top), 1).Handle == 1, "top edge (not just its handle) resizes");
        Check(AnnotationEditor.HitFrame(box, new Point(frame.Right, frame.Top + 30), 1).Handle == 3, "right edge resizes");
        Check(AnnotationEditor.HitFrame(box, frame.TopLeft + new Vector(-14, -14), 1).Rotate, "just outside a corner rotates");
        Check(AnnotationEditor.HitFrame(box, new Point(200, 150), 1).Inside, "inside moves");
        Check(!AnnotationEditor.HitFrame(box, new Point(600, 600), 1).Any, "far away hits nothing");

        // ---- cursors follow rotation
        Check(AnnotationEditor.ResizeCursor(3, 0) == Cursors.SizeWE, "edge cursor ↔ at 0°");
        Check(AnnotationEditor.ResizeCursor(3, 90) == Cursors.SizeNS, "same edge cursor ↕ at 90°");
        Check(AnnotationEditor.ResizeCursor(0, 45) == Cursors.SizeNS, "corner cursor follows 45° rotation");

        // ---- resizing a rotated box keeps the opposite corner on screen
        var start = new Rect(100, 100, 200, 100);
        const double rot = 30;
        var center = new Point(200, 150);
        var fixedCorner = AnnotationEditor.RotatePoint(start.TopLeft, center, rot); // handle 4 = bottom-right, fixed = top-left
        var target = (Rect)Ops("ResizeFromHandle", start, rot, 4, AnnotationEditor.RotatePoint(new Point(360, 260), center, rot), false)!;
        var newCenter = new Point(target.X + target.Width / 2, target.Y + target.Height / 2);
        var newFixed = AnnotationEditor.RotatePoint(target.TopLeft, newCenter, rot);
        Console.WriteLine($"   fixed corner before {fixedCorner} after {newFixed}; size {target.Width:F1}x{target.Height:F1}");
        Check((newFixed - fixedCorner).Length < 0.01 && Math.Abs(target.Width - 260) < 0.01 && Math.Abs(target.Height - 160) < 0.01,
            "rotated resize keeps the opposite corner fixed and follows the pointer");

        // ---- text resize from an edge scales the font, the opposite side stays
        var text = new TextAnnotation { Position = new Point(50, 50), Text = "Hello", FontSize = 20 };
        var tb = text.Bounds;
        Ops("Resize", text, new Rect(tb.X, tb.Y, tb.Width * 2, tb.Height), 3);
        Check(Math.Abs(text.FontSize - 40) < 0.5 && Math.Abs(text.Bounds.Left - tb.Left) < 0.5, "text edge drag doubles the size, left edge fixed");

        // ---- slider: a click on the track hands the press to the thumb (keeps dragging)
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 10, Width = 200, Style = (Style)Application.Current.Resources[typeof(Slider)] };
        var host = new Window { Content = slider, Width = 260, Height = 80, Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show();
        host.Activate();
        slider.UpdateLayout();
        var track = (Track)slider.Template.FindName("PART_Track", slider);
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent, Source = track
        };
        Console.WriteLine($"   behaviour enabled={Markshot.Core.SliderDragBehavior.GetEnabled(slider)} style={slider.Style != null} mouse over track at {Mouse.GetPosition(track)}");
        slider.PreviewMouseLeftButtonDown += (_, e) => Console.WriteLine($"   slider preview saw the press (handled={e.Handled})");
        track.RaiseEvent(args);
        Console.WriteLine($"   slider value after track click {slider.Value:F1}; thumb dragging={track.Thumb.IsDragging}");
        Check(args.Handled && track.Thumb.IsDragging, "track click jumps and the thumb keeps dragging in the same press");
        track.Thumb.CancelDrag();
        host.Close();

        // ---- pin text layer: drag selects across words / lines, double-click selects a line
        var layer = new PinTextLayer();
        layer.SetLines(new List<OcrTextBlock>
        {
            new("Hello world", new Int32Rect(10, 10, 110, 20)),
            new("第二行文字", new Int32Rect(10, 40, 100, 20)),
        });
        layer.BeginSelection(new Point(12, 20));
        layer.ExtendSelection(new Point(60, 20));
        Check(layer.SelectedText == "Hello", $"drag selects the first word (got '{layer.SelectedText}')");
        layer.ExtendSelection(new Point(75, 50));
        Check(layer.SelectedText.Replace("\r", "") == "Hello world\n第二行", $"selection continues onto the next line (got '{layer.SelectedText.Replace("\r\n", "\\n")}')");
        layer.SelectLineAt(new Point(30, 45));
        Check(layer.SelectedText == "第二行文字", "double-click selects the whole line");
        Check(layer.IsOverText(new Point(50, 20)) && !layer.IsOverText(new Point(300, 300)), "text vs blank detection");

        // ---- language detection and the 中文 → English auto switch
        Check(ImmersiveTranslator.DetectLanguage("这是一段中文 with a few words") == "中文", "detects Chinese");
        Check(ImmersiveTranslator.DetectLanguage("The quick brown fox jumps") == "English", "detects English");
        Check(ImmersiveTranslator.DetectLanguage("これは日本語のテキストです") == "日本語", "detects Japanese");

        Shots();
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static void Shots()
    {
        // Cursors: the .cur payload is a PNG after a 22-byte header.
        var dir = Path.Combine(AppContext.BaseDirectory, "cursors");
        Markshot.Core.ToolCursors.GetRotationCursor(true);
        Markshot.Core.ToolCursors.Get(AnnotationTool.Pen, 12, Colors.Red);
        Markshot.Core.ToolCursors.Get(AnnotationTool.Eraser, 10);
        var sheet = new DrawingVisual();
        using (var dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Colors.White, Color.FromRgb(30, 30, 30), 0), null, new Rect(0, 0, 330, 80));
            var x = 10.0;
            foreach (var name in new[] { "rotate-light.cur", "cursor-Pen-12-FF0000.cur", "cursor-Eraser-20-FFFFFF.cur" })
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) { Console.WriteLine("   missing " + name); continue; }
                var bytes = File.ReadAllBytes(path);
                using var ms = new MemoryStream(bytes, 22, bytes.Length - 22);
                var png = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                dc.DrawImage(png, new Rect(x, 8, png.PixelWidth, png.PixelHeight));
                dc.DrawImage(png, new Rect(x + 170, 8, png.PixelWidth, png.PixelHeight));
                x += 70;
            }
        }
        Save(sheet, 330, 80, "cursors.png");

        // Colour picker layout (height follows content now).
        var picker = new ColorPickerWindow(Color.FromRgb(0xD9, 0x4B, 0x38)) { Left = -4000, Top = -4000, ShowInTaskbar = false };
        picker.Show();
        var root = (FrameworkElement)picker.Content;
        root.Measure(new Size(286, double.PositiveInfinity));
        root.Arrange(new Rect(0, 0, 286, root.DesiredSize.Height));
        root.UpdateLayout();
        Console.WriteLine($"   colour picker content size {root.ActualWidth}x{root.ActualHeight}");
        Save(root, (int)root.ActualWidth, (int)root.ActualHeight, "picker.png");
        picker.Close();
    }

    static void Save(Visual visual, int w, int h, string path)
    {
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }
}
