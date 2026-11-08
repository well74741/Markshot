using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.67: the bent 尖头 arrow keeps its shape, erased holes go with bent elements, the floating button moves once a frame, 文字识别 pins either side.</summary>
static class Round67Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file)).Replace("\r\n", "\n");

    static byte[] Render(Annotation item, int w, int h, string? save = null)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (save != null) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            AnnotationEditor.DrawItem(dc, item);
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        if (save != null)
        {
            Directory.CreateDirectory("out");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, save));
            encoder.Save(file);
        }

        var px = new byte[w * h * 4];
        bitmap.CopyPixels(px, w * 4, 0);
        return px;
    }

    static byte A(byte[] px, int w, int x, int y) => px[(y * w + x) * 4 + 3];

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- the bent 尖头 arrow: a tail narrowing to a point, swept-back barbs
        {
            var arrow = new ArrowAnnotation { Start = new Point(20, 150), End = new Point(260, 150), Thickness = 4, Color = Colors.Red, Style = ArrowStyle.Sharp };
            arrow.MovePointHandle(1, new Point(140, 80));
            Render(arrow, 300, 200, "round67-sharp-bent.png");
            var px = Render(arrow, 300, 200);
            int Width(int x)
            {
                var column = Enumerable.Range(0, 200).Where(y => A(px, 300, x, y) > 128).ToList();
                return column.Count == 0 ? 0 : column.Max() - column.Min() + 1;
            }

            Check(Width(26) < Width(120) && Width(120) <= Width(200), $"the tail narrows to the start ({Width(26)} → {Width(120)} → {Width(200)})");
            var head = Enumerable.Range(0, 300).Where(x => Enumerable.Range(0, 200).Any(y => A(px, 300, x, y) > 128)).Max();
            Check(head >= 255, "the head reaches the end");
            Check(Source("Annotations/Annotation.cs").Contains("if (Style == ArrowStyle.Sharp)\n        {\n            DrawCurvedSharp(dc);"), "尖头 keeps its own (燕尾) shape when bent");
        }

        // ---- erased holes go with a bent element (moved, bent, an end dragged)
        {
            var arrow = new ArrowAnnotation { Start = new Point(20, 100), End = new Point(220, 100), Thickness = 8, Color = Colors.Blue, Style = ArrowStyle.Triangle };
            AnnotationOps_BindEraser(new List<Annotation> { arrow }, new[] { new Point(80, 80), new Point(80, 120) }, 12);
            var before = Render(arrow, 300, 250);
            Check(A(before, 300, 80, 100) < 10 && A(before, 300, 60, 100) > 200, "the eraser cut a gap in the arrow");
            arrow.Translate(0, 60);
            var moved = Render(arrow, 300, 250);
            Check(A(moved, 300, 80, 160) < 10 && A(moved, 300, 80, 100) < 10 && A(moved, 300, 60, 160) > 200, "moved: the gap goes along");
            arrow.MovePointHandle(1, new Point(120, 200));
            Render(arrow, 300, 250, "round67-erased-bent.png");
            var bent = Render(arrow, 300, 250);
            var curve = Curves.Sample(arrow.Start, Curves.Control(arrow.Start, arrow.End, arrow.Bend), arrow.End, 200);
            var near = curve.OrderBy(p => Math.Abs(p.X - 80)).First();
            Check(A(bent, 300, (int)near.X, (int)Math.Round(near.Y)) < 10, $"bent: the gap stays on the arrow, at the same place along it ({near.X:0},{near.Y:0})");
            var solid = curve.OrderBy(p => Math.Abs(p.X - 150)).First();
            Check(A(bent, 300, (int)solid.X, (int)Math.Round(solid.Y)) > 200, "… the rest of it is drawn");
        }

        // ---- the floating button moves once per frame while dragged
        {
            var code = Source("UI/FloatButtonWindow.cs");
            Check(code.Contains("CompositionTarget.Rendering += ApplyPendingMove;") && code.Contains("_pendingMove = (_pressCenter.X + dx, _pressCenter.Y + dy);"),
                "dragged: one move a frame, just before it is drawn (no frame shown on the path)");
        }

        // ---- 文字识别: 贴到屏幕 under each side, on the right
        {
            var xaml = Source("UI/TranslationWindow.xaml");
            Check(xaml.Contains("x:Name=\"PinSourceButton\" Content=\"贴到屏幕\"") && xaml.Contains("Click=\"PinSource_Click\"\n                    HorizontalAlignment=\"Right\""),
                "贴到屏幕 under 原文 (right-aligned) pins the source text");
            Check(xaml.Contains("<StackPanel Grid.Column=\"2\" Orientation=\"Horizontal\" HorizontalAlignment=\"Right\">\n                <Button x:Name=\"PinResultButton\""), "贴到屏幕 under 译文 pins the translation");
            var code = Source("UI/TranslationWindow.xaml.cs");
            Check(code.Contains("TextCardRenderer.Render(\"识别文字\", SourceBox.Text)") && code.Contains("TextCardRenderer.Render($\"译文 · {ResolveTarget()}\", ResultBox.Text)"), "each pins its own text");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static void AnnotationOps_BindEraser(List<Annotation> items, Point[] points, double width) =>
        typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps")!.GetMethod("BindEraser", Any)!.Invoke(null, new object[] { items, points.ToList(), width, 1.0, 0.0 });
}
