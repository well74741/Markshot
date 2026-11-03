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

static class ImageEditorTest
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }

    static void Pump(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }
    }

    // Left half red, right half blue, a green square in the top-left corner.
    static BitmapSource Picture(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            if (x < 20 && y < 20) { px[i + 1] = 200; }
            else if (x < w / 2) { px[i + 2] = 200; }
            else { px[i] = 200; }
            px[i + 3] = 255;
        }
        return ImageOps.FromPixels(px, w, h);
    }

    static (byte B, byte G, byte R, byte A) At(BitmapSource image, int x, int y)
    {
        var (px, w, _) = ImageOps.ReadPixels(image);
        var i = (y * w + x) * 4;
        return (px[i], px[i + 1], px[i + 2], px[i + 3]);
    }

    static bool Same(BitmapSource a, BitmapSource b)
    {
        var (pa, wa, ha) = ImageOps.ReadPixels(a);
        var (pb, wb, hb) = ImageOps.ReadPixels(b);
        return wa == wb && ha == hb && pa.SequenceEqual(pb);
    }

    static void Shot(FrameworkElement root, string file)
    {
        root.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(file);
        enc.Save(f);
    }

    static object Field(object o, string name) =>
        typeof(PinWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o)
        ?? typeof(ImageEditorWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    static void Call(object o, string name, params object[] args) =>
        typeof(ImageEditorWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, args);

    public static void Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Theme.Apply(ThemeMode.Dark);
        var dir = Path.Combine(Path.GetTempPath(), "markshot-editor-test");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "photo.png");
        ImageFile.Save(Picture(400, 300), file);

        // ---- empty window: opens, takes a file
        var empty = new ImageEditorWindow { Left = 40, Top = 40, ShowActivated = false };
        empty.Show();
        Pump(300);
        Check(!empty.HasImage && !empty.IsDirty && empty.Title.Contains("图片编辑器"), "opens empty");
        Shot((FrameworkElement)empty.Content, "editor_empty.png");
        Check(empty.AllowDrop, "takes dropped files");
        var dropped = (string)typeof(ImageEditorWindow).GetMethod("DroppedImage", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { new DataObject(DataFormats.FileDrop, new[] { "notes.txt", file }) });
        Check(dropped == file, "a dropped image is picked out of the files");
        Check(empty.OpenFile(file) && empty.HasImage && empty.ImageWidth == 400 && empty.FilePath == file && empty.Title.StartsWith("photo.png"), "opens an image file");
        Check(!empty.IsDirty, "a just-opened file has nothing to save");
        empty.CloseWithoutAsking();

        // ---- a picture with annotations handed over (from a capture / pin)
        var rect = new RectAnnotation { Rect = new Rect(100, 60, 50, 40), Color = Colors.Yellow, Thickness = 4 };
        var editor = new ImageEditorWindow(Picture(400, 300), null, new List<Annotation> { rect }) { Left = 40, Top = 40, ShowActivated = false };
        editor.Show();
        Pump(400);
        var items = ((AnnotationEditor)typeof(PinWindow).GetField("Editor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!).Items;
        Check(editor.AnnotationCount == 1 && (bool)Field(editor, "_editMode"), "annotations stay editable; edit mode at once");
        Check(editor.IsDirty, "a capture that was never saved counts as unsaved");
        Check(((AnnotationToolbar)Field(editor, "Toolbar")).IsEditorMode, "the toolbar shows the editor's buttons");
        var clean = editor.Composite;
        Check(At(clean, 100, 60).G > 200 && At(clean, 100, 60).R > 200, "composite has the annotation drawn");

        // ---- colour: from the original every time; one undo step per change
        editor.SetAdjust(new ImageAdjust(Gray: true));
        Pump(30);
        var g = At(editor.Composite, 300, 200);
        Check(g.R == g.G && g.G == g.B, "灰度");
        editor.UndoStep();
        Pump(30);
        Check(Same(editor.Composite, clean), "undo brings the colours back exactly");
        editor.RedoStep();
        Pump(30);
        var g2 = At(editor.Composite, 300, 200);
        Check(g2.R == g2.B, "redo");
        editor.UndoStep();
        Pump(30);
        editor.SetAdjust(new ImageAdjust(Brightness: 60));
        editor.SetAdjust(new ImageAdjust(Brightness: 60, Contrast: 40));
        editor.SetAdjust(new ImageAdjust());
        Check(Same(editor.Composite, clean), "moving the sliders back really restores the picture (nothing clipped on the way)");
        editor.SetAdjust(new ImageAdjust(Invert: true));
        Check(At(editor.Composite, 300, 200).R == 255 && At(editor.Composite, 300, 200).B == 55, "反色");
        editor.SetAdjust(new ImageAdjust(Opacity: 50));
        Check(At(editor.Composite, 300, 200).A is 127 or 128, "透明度");
        editor.SetAdjust(new ImageAdjust(Saturation: -100));
        var s = At(editor.Composite, 300, 200);
        Check(s.R == s.B, "饱和度");
        editor.SetAdjust(new ImageAdjust(Brightness: 30));
        Check(At(editor.Composite, 300, 200).B > 200, "亮度");
        editor.SetAdjust(new ImageAdjust());

        // ---- mirror / turn: annotations follow and stay editable
        editor.Flip(true);
        Pump(30);
        Check(editor.ImageWidth == 400 && At(editor.Composite, 395, 5).G == 200 && ((RectAnnotation)items[0]).Rect.Left == 250, "水平镜像 (the annotation follows)");
        editor.UndoStep();
        Pump(30);
        Check(Same(editor.Composite, clean) && ((RectAnnotation)items[0]).Rect.Left == 100, "undo a mirror");
        editor.Flip(false);
        Pump(30);
        Check(At(editor.Composite, 5, 295).G == 200, "垂直镜像");
        editor.UndoStep();
        editor.Turn(true);
        Pump(30);
        Check(editor.ImageWidth == 300 && editor.ImageHeight == 400 && At(editor.Composite, 295, 5).G == 200 && editor.AnnotationCount == 1, "右转 90°");
        editor.UndoStep();
        editor.Turn(false);
        Pump(30);
        Check(editor.ImageWidth == 300 && At(editor.Composite, 5, 395).G == 200, "左转 90°");
        editor.UndoStep();
        Pump(30);
        Check(Same(editor.Composite, clean), "after all that, undo is back at the start");

        // ---- size / angle merge the annotations, undo brings them back
        editor.ResizeTo(200, 150);
        Pump(30);
        Check(editor.ImageWidth == 200 && editor.ImageHeight == 150 && editor.AnnotationCount == 0 && At(editor.Composite, 50, 30).G > 150, "尺寸 (annotations go into the pixels)");
        editor.UndoStep();
        Pump(30);
        Check(editor.ImageWidth == 400 && editor.AnnotationCount == 1 && Same(editor.Composite, clean), "undo a resize: size and annotations back");
        editor.RotateBy(30);
        Pump(30);
        Check(editor.ImageWidth > 400 && At(editor.Composite, 1, 1).A == 0, "任意角度 (the canvas grows, corners transparent)");
        editor.UndoStep();
        Pump(30);

        // ---- crop keeps annotations editable at their place on the picture
        editor.CropTo(new Int32Rect(50, 40, 300, 200));
        Pump(30);
        Check(editor.ImageWidth == 300 && editor.ImageHeight == 200 && ((RectAnnotation)items[0]).Rect.Left == 50 && ((RectAnnotation)items[0]).Rect.Top == 20, "矩形裁切");
        editor.UndoStep();
        Pump(30);
        Check(((RectAnnotation)items[0]).Rect.Left == 100 && editor.ImageWidth == 400, "undo a crop");

        // ---- the interactive sessions: path, crop, keystone
        Call(editor, "StartSession", EditorOverlayMode.Path);
        var overlay = (EditorOverlay)Field(editor, "_overlay");
        Check(overlay != null && !overlay.Ready, "路径裁切: nothing to apply before a path is drawn");
        overlay.PathPoints.AddRange(new[] { new Point(50, 50), new Point(350, 50), new Point(200, 280) });
        Pump(50);
        Shot((FrameworkElement)editor.Content, "editor_path.png");
        Check(overlay.Ready, "路径裁切: a path can be applied");
        Call(editor, "ApplySession");
        Pump(50);
        Check(editor.ImageWidth == 300 && editor.ImageHeight == 230 && At(editor.Composite, 150, 10).A == 255 && At(editor.Composite, 2, 225).A == 0, "路径裁切: bounding box, transparent outside");
        Check(ImageOps.HasTransparentCorner(editor.Composite), "the cut-out counts as a shape (a pin's shadow follows it)");
        editor.UndoStep();
        Pump(30);
        Call(editor, "StartSession", EditorOverlayMode.Keystone);
        overlay = (EditorOverlay)Field(editor, "_overlay");
        overlay.Quad[0] = new Point(60, 30); overlay.Quad[1] = new Point(340, 30); overlay.Quad[2] = new Point(390, 280); overlay.Quad[3] = new Point(10, 280);
        overlay.InvalidateVisual();
        Pump(50);
        Shot((FrameworkElement)editor.Content, "editor_keystone.png");
        Call(editor, "ApplySession");
        Pump(50);
        Check(editor.ImageWidth == 380 && editor.ImageHeight >= 250 && At(editor.Composite, 3, editor.ImageHeight / 2).A == 255, $"梯形校正 ({editor.ImageWidth}×{editor.ImageHeight})");
        editor.UndoStep();
        Pump(30);
        Call(editor, "StartSession", EditorOverlayMode.Crop);
        overlay = (EditorOverlay)Field(editor, "_overlay");
        overlay.Crop = new Rect(10, 10, 100, 80);
        Call(editor, "CancelSession");
        Check(editor.ImageWidth == 400 && Field(editor, "_overlay") == null, "cancel leaves the picture alone");

        // ---- save, and 贴到屏幕 hands over base + annotations
        foreach (var ext in new[] { "png", "jpg", "webp" })
        {
            var path = Path.Combine(dir, "out." + ext);
            ImageFile.Save(editor.Composite, path);
            Check(ImageFile.Load(path).PixelWidth == 400, "saved as " + ext);
        }

        Shot((FrameworkElement)editor.Content, "editor_dark.png");
        Theme.Apply(ThemeMode.Light);
        Pump(100);
        Shot((FrameworkElement)editor.Content, "editor_light.png");
        Theme.Apply(ThemeMode.Dark);
        editor.CloseWithoutAsking();

        PinWindow made = null;
        BitmapSource handedBase = null, handedComposed = null;
        List<Annotation> handed = null;
        var pinner = new ImageEditorWindow(Picture(300, 200), null, new List<Annotation> { new RectAnnotation { Rect = new Rect(10, 10, 30, 30), Color = Colors.Yellow, Thickness = 3 } })
        {
            Left = 40, Top = 40, ShowActivated = false,
            PinAction = (b, a, c) =>
            {
                handedBase = b; handed = a; handedComposed = c;
                return made = new PinWindow(c, 100, 100, b, a) { Left = -4000, Top = -4000, ShowActivated = false };
            }
        };
        pinner.Show();
        Pump(300);
        pinner.SetAdjust(new ImageAdjust(Invert: true));
        Call(pinner, "PinToScreen");
        Check(made != null && handed.Count == 1 && handedComposed.PixelWidth == 300 && At(handedBase, 200, 100).R == 255 && !pinner.IsDirty, "贴到屏幕: the adjusted picture + editable annotations; counts as kept");
        made.Show();
        Pump(200);
        Check(Same(made.SourceImage, handedComposed), "the pin shows the edited picture");
        made.CloseImmediately();
        pinner.CloseWithoutAsking();

        // ---- a very long picture opens (long screenshot)
        var longOne = new ImageEditorWindow(Picture(800, 12000)) { Left = 40, Top = 40, ShowActivated = false };
        longOne.Show();
        Pump(400);
        Check(longOne.ImageHeight == 12000, "a long screenshot opens");
        Shot((FrameworkElement)longOne.Content, "editor_long.png");
        longOne.CloseWithoutAsking();
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
