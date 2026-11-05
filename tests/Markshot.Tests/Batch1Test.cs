using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markshot.Annotations;

static class Batch1Test
{
    static int _fail;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) _fail++;
    }

    public static void Run()
    {
        // 1) Rotate (E) mid-drag, then keep stretching: the start corner must stay put.
        var rect = new RectAnnotation();
        var g = new CreationGesture(rect, new Point(100, 100));
        g.Move(new Point(200, 160), false, false);
        g.BeginRotate();
        g.Move(new Point(180, 200), false, false); // swing around the start point
        g.EndRotate();
        var startCorner1 = StartCorner(rect);
        g.Move(new Point(230, 260), false, false); // keep stretching
        g.Move(new Point(260, 300), false, false);
        var startCorner2 = StartCorner(rect);
        Console.WriteLine($"   rotation={rect.Rotation:F1} start corner before={startCorner1} after={startCorner2}");
        Check(Math.Abs(rect.Rotation) > 5, "E rotates the shape while drawing");
        Check((startCorner1 - new Point(100, 100)).Length < 0.5 && (startCorner2 - new Point(100, 100)).Length < 0.5,
            "pivot (drag start) stays exactly where the drag began after rotating and stretching");

        // 1b) releasing E does not make the shape jump
        var w0 = rect.Rect.Width;
        var rect2 = new RectAnnotation();
        var g2 = new CreationGesture(rect2, new Point(0, 0));
        g2.Move(new Point(100, 50), false, false);
        g2.BeginRotate();
        g2.Move(new Point(60, 90), false, false);
        var before = rect2.Rect;
        g2.EndRotate();
        g2.Move(new Point(60, 90), false, false); // same pointer after release
        Check(rect2.Rect == before, "releasing E recomputes nothing (no jump)");

        // 1c) Space moves, Shift squares
        var ell = new EllipseAnnotation();
        var g3 = new CreationGesture(ell, new Point(10, 10));
        g3.Move(new Point(60, 30), true, false);
        Check(Math.Abs(ell.Rect.Width - ell.Rect.Height) < 0.01, "Shift makes a circle");
        g3.BeginMove();
        g3.Move(new Point(160, 130), true, false);
        g3.EndMove();
        Check(Math.Abs(ell.Rect.X - 110) < 0.01 && Math.Abs(ell.Rect.Y - 110) < 0.01, "Space moves the shape by the pointer delta");

        // 2) Pen: Shift straight segment, then free again; Space moves the whole stroke
        var pen = new PenAnnotation();
        pen.Points.Add(new Point(0, 0));
        var gp = new CreationGesture(pen, new Point(0, 0));
        gp.Move(new Point(5, 5), false, false);
        gp.Move(new Point(10, 3), false, false);
        gp.Move(new Point(50, 40), true, false);
        gp.Move(new Point(80, 90), true, false);
        var straight = pen.Points.Count;
        Check(pen.Points[^1] == new Point(80, 90) && pen.Points[^2] == new Point(10, 3) && pen.Corners.Contains(straight - 1),
            "Shift turns the stroke into a straight segment from the last point");
        gp.Move(new Point(90, 95), false, false);
        Check(pen.Points.Count == straight + 1, "releasing Shift continues free drawing");
        var p0 = pen.Points[0];
        gp.BeginMove();
        gp.Move(new Point(100, 105), false, false);
        gp.EndMove();
        Check(pen.Points[0] == p0 + new Vector(10, 10), "Space moves the whole stroke");

        // 3) Eraser bound to the element: moves with it, nothing left on the canvas
        var box = new RectAnnotation { Rect = new Rect(100, 100, 200, 100), Fill = true, Color = Colors.Red };
        var items = new System.Collections.Generic.List<Annotation> { box };
        var stroke = new[] { new Point(150, 80), new Point(150, 220) };
        Check(BindEraser(items, stroke, 20), "eraser stroke binds to the rectangle it crosses");
        Check(box.IsErasedAt(new Point(150, 150)) && !box.IsErasedAt(new Point(250, 150)), "erased point no longer hits, the rest does");
        box.Translate(300, 0);
        Check(box.IsErasedAt(new Point(450, 150)) && !box.IsErasedAt(new Point(150, 150)), "the hole moves with the element");
        box.Rect = new Rect(400, 100, 400, 100); // stretch x2 horizontally
        Check(box.IsErasedAt(new Point(500, 150)), "the hole scales with the element");
        var clone = (RectAnnotation)box.Clone();
        box.EraseMasks!.Clear();
        Check(clone.EraseMasks!.Count == 1, "undo snapshots deep-copy erase masks");

        // 4) Watermark: hits on glyphs only, not in the gaps; sits at the bottom
        var wm = new WatermarkAnnotation { Rect = new Rect(0, 0, 800, 600), FontSize = 30, Text = "CONFIDENTIAL" };
        int hits = 0, total = 0;
        for (var x = 0; x < 800; x += 7)
        for (var y = 0; y < 600; y += 7)
        {
            total++;
            if (wm.HitTest(new Point(x, y), 0)) hits++;
        }
        Console.WriteLine($"   watermark glyph-area coverage {100.0 * hits / total:F0}%");
        Check(hits > 0 && hits < total * 0.6, "watermark hit-test covers the texts, not the whole canvas");

        // 5) Composition draws rotation (was dropped before) and erase masks
        var rotated = new RectAnnotation { Rect = new Rect(60, 90, 200, 20), Fill = true, Color = Colors.Blue, Rotation = 90 };
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 320, 200));
            AnnotationEditor.DrawItem(dc, rotated);
        }
        var bmp = new RenderTargetBitmap(320, 200, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var vertical = IsBlue(bmp, 160, 20) && IsBlue(bmp, 160, 180) && !IsBlue(bmp, 70, 100);
        Check(vertical, "composition renders the element rotated (a 90° bar is vertical)");

        // 6) Mosaic lens: moving the box pixelates what is underneath
        var src = Gradient(400, 200);
        var mosaic = new MosaicAnnotation { Rect = new Rect(0, 0, 100, 100), BlockSize = 20 };
        MosaicBuilder.Rebuild(mosaic, src);
        mosaic.Translate(200, 0);
        var dv2 = new DrawingVisual();
        using (var dc = dv2.RenderOpen())
        {
            dc.DrawImage(src, new Rect(0, 0, 400, 200));
            AnnotationEditor.DrawItem(dc, mosaic);
        }
        var out2 = new RenderTargetBitmap(400, 200, 96, 96, PixelFormats.Pbgra32);
        out2.Render(dv2);
        var a = Pixel(out2, 201, 50);
        var b = Pixel(out2, 218, 50);
        var c = Pixel(src, 218, 50);
        Check(a == b && b != c, "moved mosaic box pixelates the content under its new position (block-uniform)");
        Save(out2, "batch1_mosaic.png");

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static bool BindEraser(System.Collections.Generic.List<Annotation> items, Point[] points, double width)
    {
        var type = typeof(AnnotationEditor).Assembly.GetType("Markshot.UI.AnnotationOps")!;
        var method = type.GetMethod("BindEraser", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!;
        return (bool)method.Invoke(null, new object[] { items, points, width, 1.0, 0.0 })!;
    }

    static Point StartCorner(RectAnnotation r)
    {
        // The corner that was the drag start: the top-left of the unrotated box, rotated about the centre.
        var c = new Point(r.Rect.X + r.Rect.Width / 2, r.Rect.Y + r.Rect.Height / 2);
        return AnnotationEditor.RotatePoint(r.Rect.TopLeft, c, r.Rotation);
    }

    static bool IsBlue(BitmapSource b, int x, int y)
    {
        var p = Pixel(b, x, y);
        return p.B > 200 && p.R < 60;
    }

    static Color Pixel(BitmapSource b, int x, int y)
    {
        var px = new byte[4];
        new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }

    static BitmapSource Gradient(int w, int h)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Colors.Black, Colors.Orange, 0), null, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    static void Save(BitmapSource b, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var f = File.Create(path);
        enc.Save(f);
    }
}
