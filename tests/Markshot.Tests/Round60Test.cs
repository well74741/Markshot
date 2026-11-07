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

/// <summary>0.8.60: 聚焦, the toolbar rows, the editor's 元素样式 for every element, reset buttons, flat pins.</summary>
static class Round60Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file));

    static BitmapSource Grey(int w, int h, byte v = 200)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    static RenderTargetBitmap Render(int w, int h, IReadOnlyList<Annotation> items)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(Grey(w, h), new Rect(0, 0, w, h));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            foreach (var item in AnnotationEditor.DrawOrder(items))
            {
                AnnotationEditor.DrawItem(dc, item);
            }
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return bmp;
    }

    static byte At(BitmapSource b, int x, int y)
    {
        var px = new byte[4];
        b.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return px[2];
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- 聚焦: clear inside, dark outside, the edge soft only at the edge; several together darken once
        {
            var spot = new SpotlightAnnotation { Rect = new Rect(50, 50, 100, 60), Darkness = 0.5, Feather = 0 };
            var shot = Render(200, 160, new List<Annotation> { spot });
            Check(At(shot, 100, 80) == 200 && Math.Abs(At(shot, 10, 10) - 100) <= 2, $"inside clear ({At(shot, 100, 80)}), outside darkened by half ({At(shot, 10, 10)})");
            spot.Feather = 20;
            shot = Render(200, 160, new List<Annotation> { spot });
            var edgeIn = At(shot, 55, 80);
            var edgeOut = At(shot, 45, 80);
            Check(At(shot, 100, 80) == 200 && Math.Abs(At(shot, 10, 10) - 100) <= 2 && edgeIn < 200 && edgeIn > edgeOut && edgeOut > 100,
                $"feathered: only around the edge ({edgeIn} → {edgeOut}), the middle clear, far outside fully dark");
            var other = new SpotlightAnnotation { Rect = new Rect(160, 10, 30, 30), Darkness = 0.5, Feather = 0, Ellipse = true };
            spot.Feather = 0;
            shot = Render(200, 160, new List<Annotation> { spot, other });
            Check(At(shot, 100, 80) == 200 && At(shot, 175, 25) == 200 && Math.Abs(At(shot, 10, 150) - 100) <= 2,
                "two spotlights: both clear, the rest darkened once (not twice)");
            Check(AnnotationOps_Create(AnnotationTool.Spotlight) is SpotlightAnnotation, "the 聚焦 tool makes a spotlight");
            var gesture = new CreationGesture(new SpotlightAnnotation(), new Point(10, 10));
            gesture.Move(new Point(40, 30), true, false);
            Check(((SpotlightAnnotation)gesture.Target).Rect == new Rect(10, 10, 30, 30), "drawn like a shape (Shift square)");
        }

        // ---- the toolbar rows
        {
            var bar = new AnnotationToolbar();
            var host = new Window { Content = bar, Left = -4000, Top = -4000, Width = 900, Height = 200, ShowActivated = false };
            host.Show();
            Pump(100);
            bar.SetTool(AnnotationTool.Spotlight);
            Pump(50);
            bool Shown(string name) => ((FrameworkElement)bar.FindName(name)).Visibility == Visibility.Visible;
            Check(Shown("NumberVariants") && Shown("SpotlightGroup") && !Shown("WidthGroup") && !Shown("ColorSection"), "聚焦: [序号|聚焦] icons, shape, 暗度, 羽化; no size or colour");
            ((Slider)bar.FindName("DarknessSlider")).Value = 70;
            ((Slider)bar.FindName("FeatherSlider")).Value = 30;
            Check(Math.Abs(bar.SpotlightDarkness - 0.7) < 0.001 && Math.Abs(bar.SpotlightFeather - 30) < 0.001, "the sliders set darkness and the soft edge");
            bar.SetTool(AnnotationTool.Number);
            Pump(50);
            var row = (Panel)((FrameworkElement)bar.FindName("WidthGroup")).Parent;
            var order = row.Children.Cast<FrameworkElement>().Select(c => c.Name).ToList();
            Check(Shown("NumberVariants") && order.IndexOf("NumberStyleGroup") < order.IndexOf("WidthGroup") && order.IndexOf("NumberVariants") == 0,
                "序号: the style icons on the far left, 空心 / 描边 left of 大小");
            bar.SetTool(AnnotationTool.MosaicBrush);
            Pump(50);
            var mosaic = (Panel)bar.FindName("MosaicVariants");
            Check(mosaic.Children.OfType<RadioButton>().Count() == 2 && !mosaic.Children.OfType<TextBlock>().Any() &&
                  order.IndexOf("WidthGroup") < order.IndexOf("LevelGroup") && Shown("WidthGroup") && Shown("LevelGroup"), "马赛克: two icons (no 类型), 粗细 then 程度");
            host.Close();
            var cut = BitmapFrame.Create(new Uri(Repo.File("Assets/Icons/cutout.png")));
            Check(cut.PixelWidth == 200, "抠除 has the library's 蒙版 icon");
            Check(Source("UI/OverlayWindow.Annotations.cs").Contains("InputLayer.Cursor = _hasSelection && !_selection.Contains(point)"), "outside the area the cursor no longer flickers");
        }

        // ---- the editor: 元素样式 for shapes (opacity, corners), greyed rows, resets resting
        {
            var editor = new ImageEditorWindow(Grey(200, 120)) { Left = -4000, Top = -4000, ShowActivated = false };
            editor.Show();
            Pump(300);
            var rect = new RectAnnotation { Rect = new Rect(10, 10, 60, 40), Thickness = 3 };
            ((AnnotationEditor)typeof(PinWindow).GetField("Editor", Any)!.GetValue(editor)!).Items.Add(rect);
            editor.SelectElements(new Annotation[] { rect });
            Pump(100);
            editor.SetPictureStyle("Opacity", 40);
            editor.SetPictureStyle("Radius", 8);
            editor.SetPictureStyle("Shadow", 20);
            Check(Math.Abs(rect.Opacity - 0.4) < 0.001 && Math.Abs(rect.CornerRadius - 8) < 0.001, "a shape: opacity and corners from 元素样式");
            var shadowRow = (FrameworkElement)typeof(ImageEditorWindow).GetField("_shadowRow", Any)!.GetValue(editor)!;
            Check(!shadowRow.IsEnabled || shadowRow.Visibility != Visibility.Visible, "a shape: the picture-only rows rest");
            var adjustReset = (Button)typeof(ImageEditorWindow).GetField("_adjustReset", Any)!.GetValue(editor)!;
            Check(!adjustReset.IsEnabled, "调色's 重置 rests while nothing was changed");
            var source = Source("UI/ImageEditorWindow.cs");
            Check(source.Contains("AnimateSectionHeight(_styleSection"), "the section eases to its new height when rows come and go");
            Check(source.Contains("var flat = composed;") && source.Contains("var annotations = new List<Annotation>();"), "贴到屏幕: everything goes into the picture, no annotations kept");
            editor.CloseWithoutAsking();
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static Annotation? AnnotationOps_Create(AnnotationTool tool)
    {
        var ops = typeof(AnnotationEditor).Assembly.GetType("Markshot.UI.AnnotationOps")!;
        return (Annotation?)ops.GetMethod("Create")!.Invoke(null, new object[] { tool, new Point(5, 5), new AnnotationToolbar(), 1, Grey(20, 20) });
    }
}
