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

    // The picture with its annotations merged (what an angle turns).
    static BitmapSource Flat(BitmapSource composed) => ImageOps.Normalize(composed);

    static object Field(object o, string name) =>
        typeof(PinWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o)
        ?? typeof(ImageEditorWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    static void Call(object o, string name, params object[] args) =>
        typeof(ImageEditorWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, args);

    static string Shot2Text(FrameworkElement root)
    {
        var texts = new List<string>();
        void Walk(DependencyObject d)
        {
            if (d is ContentControl { Content: string c }) texts.Add(c);
            if (d is TextBlock t) texts.Add(t.Text);
            foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(child);
        }
        Walk(root);
        return string.Join("|", texts);
    }

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
        editor.SetAdjust(new ImageAdjust(Saturation: -100));
        Pump(30);
        var g = At(editor.Composite, 300, 200);
        Check(g.R == g.G && g.G == g.B, "饱和度 -100");
        Check(!Shot2Text(editor).Contains("灰度"), "no 灰度 button any more");
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
        var rectLeft = ((RectAnnotation)items[0]).Rect.Left;
        editor.CanvasTo(500, 400);
        Pump(30);
        Check(editor.ImageWidth == 500 && editor.ImageHeight == 400 && At(editor.Composite, 10, 10).A == 0 && At(editor.Composite, 55, 55).G == 200,
            "画布尺寸: a bigger canvas, the picture centred and not scaled, the rest transparent");
        Check(editor.AnnotationCount == 1 && ((RectAnnotation)items[0]).Rect.Left == rectLeft + 50, "画布尺寸: the annotations move with the picture and stay editable");
        editor.CanvasTo(300, 200);
        Pump(30);
        Check(editor.ImageWidth == 300 && editor.ImageHeight == 200, "画布尺寸: a smaller canvas cuts round the centre");
        editor.UndoStep(); editor.UndoStep();
        Pump(30);
        Check(editor.ImageWidth == 400 && Same(editor.Composite, clean), "undo the canvas sizes");
        editor.RotateBy(30);
        Pump(30);
        Check(editor.ImageWidth > 400 && At(editor.Composite, 1, 1).A == 0, "任意角度 (the canvas grows, corners transparent)");
        editor.RotateBy(30);
        editor.RotateBy(30);
        Pump(30);
        Check(editor.ImageWidth == 300 && editor.ImageHeight == 400, $"three times 30° = 90°: no growing border ({editor.ImageWidth}×{editor.ImageHeight})");
        editor.RotateBy(-90);
        Pump(30);
        Check(editor.ImageWidth == 400 && editor.ImageHeight == 300 && Same(editor.Composite, Flat(clean)), "turned back: exactly the picture again");
        editor.UndoStep(); editor.UndoStep(); editor.UndoStep(); editor.UndoStep();
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

        // ---- sections' 重置: back to before that group's changes, while it was the last kind of change
        Check(!editor.CanReset("Size") && !editor.CanReset("Turn") && !editor.CanReset("Cut"), "nothing to reset at first");
        editor.ResizeTo(200, 150);
        editor.ResizeTo(100, 75);
        Pump(30);
        Check(editor.CanReset("Size") && editor.ImageWidth == 100, "after two resizes the size can be reset");
        editor.Reset("Size");
        Pump(30);
        Check(editor.ImageWidth == 400 && editor.AnnotationCount == 1 && Same(editor.Composite, clean) && !editor.CanReset("Size"), "尺寸 重置: original size and annotations back");
        editor.UndoStep();
        Pump(30);
        Check(editor.ImageWidth == 100, "the reset is one undo step");
        editor.RedoStep();
        Pump(30);
        editor.Turn(true);
        editor.Flip(true);
        Pump(30);
        Check(editor.CanReset("Turn") && !editor.CanReset("Size"), "rotation group: its own reset");
        editor.CropTo(new Int32Rect(10, 10, 200, 100));
        Pump(30);
        Check(!editor.CanReset("Turn") && editor.CanReset("Cut"), "another kind of change: the earlier group cannot be reset alone");
        editor.Reset("Cut");
        Pump(30);
        Check(editor.ImageWidth == 300 && editor.ImageHeight == 400, "裁切 重置: back to the turned picture");
        editor.UndoStep(); editor.UndoStep(); editor.UndoStep(); editor.UndoStep();
        Pump(30);
        Check(Same(editor.Composite, clean), "undo back to the start");

        // ---- the view: wheel zooms at the mouse, the middle button drags anywhere, F fits and centres
        editor.Fit();
        Pump(50);
        var fitted = editor.PanOffset;
        var zoomBefore = (double)Field(editor, "_zoom");
        var at = new Point(fitted.X + 100 * zoomBefore, fitted.Y + 80 * zoomBefore); // picture pixel (100, 80)
        editor.ZoomAt(120, at);
        editor.ZoomAt(120, at);
        var zoomAfter = (double)Field(editor, "_zoom");
        var under = new Point((at.X - editor.PanOffset.X) / zoomAfter, (at.Y - editor.PanOffset.Y) / zoomAfter);
        Check(zoomAfter > zoomBefore * 1.15 && Math.Abs(under.X - 100) < 1 && Math.Abs(under.Y - 80) < 1, "wheel zooms in, the point under the mouse stays put");
        editor.PanBy(new Vector(-500, 300));
        Check(editor.PanOffset.X < fitted.X - 300, "the picture moves freely (past the edges)");
        editor.PanBy(new Vector(-100000, 0));
        Check(editor.PanOffset.X + 400 * zoomAfter >= 47, "a part of it always stays in view");
        Call(editor, "EditorKey", System.Windows.Input.Key.F, false);
        Pump(30);
        Check(Math.Abs(editor.PanOffset.X - fitted.X) < 2 && Math.Abs(editor.PanOffset.Y - fitted.Y) < 2 && (double)Field(editor, "_zoom") == zoomBefore, "F: fitted and back in the middle");

        // ---- zoom far in: up to 3200 %, the checkerboard only where the picture is visible, pixels stay sharp
        for (var i = 0; i < 80; i++) editor.ZoomAt(120, new Point(300, 200));
        var deep = (double)Field(editor, "_zoom");
        var checker = (System.Windows.Shapes.Rectangle)Field(editor, "_checker");
        var stage = (Canvas)Field(editor, "_stage");
        Check(deep > 30 && deep <= 32, $"zooms in to 3200 % ({deep * 100:0} %)");
        Check(checker.Width <= stage.ActualWidth + 1 && checker.Height <= stage.ActualHeight + 1, "the checkerboard covers only the view, not the 12 800 px picture");
        Check(RenderOptions.GetBitmapScalingMode((Image)Field(editor, "Img")) == BitmapScalingMode.NearestNeighbor, "close up the pixels stay sharp");
        var before = editor.PanOffset;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 60; i++) { editor.PanBy(new Vector(7, 3)); ((FrameworkElement)editor.Content).UpdateLayout(); }
        Check(watch.ElapsedMilliseconds < 1500 && editor.PanOffset != before, $"dragging close up stays quick ({watch.ElapsedMilliseconds} ms for 60 moves)");
        editor.Fit();
        Pump(30);

        // ---- the floating bar at the bottom, the status bar says what works now
        var bar = editor.ToolbarBounds;
        var area = editor.ToolbarArea;
        Check(Math.Abs(bar.X + bar.Width / 2 - area.Width / 2) < 3 && Math.Abs(area.Height - bar.Bottom - 14) < 3, $"the annotation bar floats at the bottom middle ({bar})");
        Check(((AnnotationToolbar)Field(editor, "Toolbar")).FindName("DragHandle") is FrameworkElement { Visibility: Visibility.Visible }, "it has its grip to move it");
        Canvas.SetLeft((AnnotationToolbar)Field(editor, "Toolbar"), 20);
        Canvas.SetTop((AnnotationToolbar)Field(editor, "Toolbar"), 30);
        Call(editor, "RememberToolbarPlace");
        editor.Width += 60;
        Pump(100);
        Check(Math.Abs(editor.ToolbarBounds.Y - 30) < 3, "moved by its grip: it stays there");
        Check(editor.Hint.Contains("滚轮缩放") && editor.Hint.Contains("单击批注选中"), "status bar: no tool");
        ((AnnotationToolbar)Field(editor, "Toolbar")).SetTool(AnnotationTool.Pen);
        typeof(PinWindow).GetMethod("SetTool", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, new object[] { AnnotationTool.Pen });
        Call(editor, "UpdateHint");
        Check(editor.Hint.Contains("Alt+滚轮 笔刷大小"), "status bar: the pen's keys");
        Call(editor, "StartSession", EditorOverlayMode.Mesh);
        Check(editor.Hint.Contains("交叉拆分") && editor.Hint.Contains("Enter 应用"), "status bar: the warp session");
        Call(editor, "CancelSession");
        typeof(PinWindow).GetMethod("SetTool", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, new object[] { AnnotationTool.None });

        // ---- 网格形变 in the editor, 色相 and 曲线
        Call(editor, "StartSession", EditorOverlayMode.Mesh);
        var mesh = (EditorOverlay)Field(editor, "_overlay");
        Check(!mesh.Ready, "warp: nothing to apply before a split or a drag");
        var crossing = mesh.SplitWarpAt(new Point(200, 150));
        Check(crossing == (1, 1) && mesh.Warp.Rows == 2 && mesh.Warp.Columns == 2, "warp: a click splits the grid crosswise there");
        Check(mesh.WarpSplit && mesh.SplitWarpAt(new Point(100, 80)) == null && mesh.Warp.Rows == 2, "warp: split only once, further clicks do not split");
        Check(mesh.PressWarp(new Point(180, 130)) == (0, 1, 1, 0), "warp: then a press away from the points takes the nearest crossing");
        mesh.DragWarpTo(new Point(200, 130));
        Check((mesh.Warp.Anchors[1, 1] - new Point(220, 150)).Length < 0.01, "it moves with the drag, without jumping to the pointer");
        mesh.DragWarp((0, 1, 1, 0), new Point(260, 150));
        mesh.DragWarp((1, 0, 0, 1), new Point(140, -30)); // the top edge's handle bends it up
        Pump(50);
        Shot((FrameworkElement)editor.Content, "editor_mesh.png");
        Call(editor, "ApplySession");
        Pump(50);
        Check(At(editor.Composite, 230 - 0, 150 + editor.ImageHeight - 300).R == 200, "warp applied: the red half reaches the dragged crossing");
        Check(editor.ImageHeight > 300 && editor.ImageWidth == 400, $"the canvas grew to hold the bent top edge ({editor.ImageWidth}×{editor.ImageHeight})");
        editor.UndoStep();
        Pump(30);
        Check(Same(editor.Composite, clean), "undo the mesh");
        editor.SetAdjust(new ImageAdjust(Hue: 120));
        Pump(30);
        var turnedHue = At(editor.Composite, 100, 200);
        Check(turnedHue.G > turnedHue.R, "色相 slider value turns the colours");
        editor.SetAdjust(new ImageAdjust());
        var curves = new CurvesWindow(null, editor.Composite) { Left = -3000, Top = -3000, ShowActivated = false };
        double[]? lastCurve = null;
        curves.CurveChanged += curve => lastCurve = curve;
        curves.Show();
        Pump(50);
        var added = curves.AddPoint(128, 200);
        curves.MovePoint(added, new Point(128, 220));
        Check(lastCurve != null && ToneCurve.Table(lastCurve)[128] == 220, "曲线: a point added and dragged");
        Shot((FrameworkElement)curves.Content, "curves_dark.png");
        curves.Close();
        Check(lastCurve!.Length == 4 && ToneCurve.IsStraight(lastCurve), "曲线: 取消 puts the curve back");
        editor.SetAdjust(new ImageAdjust());
        Pump(30);

        // ---- 调节框 (double-click), crop size typed, 重置所有修改, angle range
        Check(((Slider)Field(editor, "_angle")).Minimum == -180 && ((Slider)Field(editor, "_angle")).Maximum == 180, "angle slider covers 360°");
        Call(editor, "StartSession", EditorOverlayMode.Transform);
        var box = (EditorOverlay)Field(editor, "_overlay");
        Check(box.TransformRect == new Rect(0, 0, 400, 300) && !box.Ready, "调节框 starts on the picture");
        box.PressTransform(new Point(400, 300));
        box.DragTransform(new Point(300, 260));
        Check(Math.Abs(box.TransformRect.Width / box.TransformRect.Height - 4.0 / 3) < 0.02, $"调节框 corners keep the ratio ({box.TransformRect.Width:0}x{box.TransformRect.Height:0})");
        box.TransformRect = new Rect(0, 0, 300, 200);
        var reachPx = 20 / box.Zoom;
        box.PressTransform(new Point(300 + reachPx, 100)); // just outside: turning
        box.DragTransform(new Point(150, 450));
        Check(box.Ready && Math.Abs(box.TransformAngle) > 10 && Field(editor, "_overlay") != null, $"just outside the box turns it ({box.TransformAngle:0}°)");
        box.TransformAngle = 0;
        Check(((Image)Field(editor, "Img")).Opacity == 0, "调节框: the picture is not darkened but hidden (the box draws it, the rest shows the checkerboard)");
        var sizeLabel = (Border)Field(editor, "_cropSize");
        Pump(50);
        Check(sizeLabel.Visibility == Visibility.Visible, "调节框: the editable size label like the capture's");
        Shot((FrameworkElement)editor.Content, "editor_transform.png");
        var keyDown = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, 0, System.Windows.Input.Key.Enter)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent
        };
        editor.RaiseEvent(keyDown);
        Pump(50);
        Check(editor.ImageWidth == 300 && editor.ImageHeight == 200, $"调节框 applied with Enter: new size ({editor.ImageWidth}×{editor.ImageHeight})");
        Check(((Image)Field(editor, "Img")).Opacity == 1, "the picture shows again");
        editor.UndoStep();
        Pump(30);
        // a click well away from the box applies it
        Call(editor, "StartSession", EditorOverlayMode.Transform);
        box = (EditorOverlay)Field(editor, "_overlay");
        box.TransformRect = new Rect(0, 0, 200, 150);
        Check(box.TransformHit(new Point(380, 290)) == "outside", "far from the box counts as outside");
        box.PressTransform(new Point(380, 290));
        Pump(50);
        Check(Field(editor, "_overlay") == null && editor.ImageWidth == 200, $"a click outside the 调节框 applies it and leaves ({editor.ImageWidth})");
        editor.UndoStep();
        Pump(30);
        // a previewed angle is kept when a tool starts (applied first)
        ((Slider)Field(editor, "_angle")).Value = 30;
        Call(editor, "StartSession", EditorOverlayMode.Mesh);
        Check(editor.ImageWidth > 400 && ((Slider)Field(editor, "_angle")).Value == 0, $"网格变形 starts on the turned picture ({editor.ImageWidth})");
        Call(editor, "CancelSession");
        editor.UndoStep();
        Pump(30);
        Check(Same(editor.Composite, clean), "undo the angle");
        // typed size: around the box's centre; shrinking + turning leaves see-through corners
        Call(editor, "StartSession", EditorOverlayMode.Transform);
        box = (EditorOverlay)Field(editor, "_overlay");
        typeof(ImageEditorWindow).GetField("_cropWidth", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!.GetType();
        ((TextBox)Field(editor, "_cropWidth")).Text = "200";
        ((TextBox)Field(editor, "_cropHeight")).Text = "150";
        editor.ApplyCropSize();
        Check(box.TransformRect == new Rect(100, 75, 200, 150), $"typed size goes round the centre ({box.TransformRect})");
        box.TransformAngle = 30;
        Call(editor, "ApplySession");
        Pump(50);
        Check(At(editor.Composite, 1, 1).A == 0 && editor.ImageWidth > 200, "smaller and turned: the uncovered corners are transparent");
        editor.UndoStep();
        Pump(30);

        // a rectangle crop after turning keeps the turned picture (cut straight, no turning back)
        editor.RotateBy(30);
        Pump(30);
        var turnedWidth = editor.ImageWidth;
        editor.CropTo(new Int32Rect(60, 60, 200, 150));
        Pump(30);
        Check(editor.ImageWidth == 200 && editor.ImageHeight == 150, "crop after an angle: straight 200×150");
        editor.RotateBy(10);
        Pump(30);
        Check(editor.ImageWidth < turnedWidth, $"the next angle turns the cropped picture, not the old one ({editor.ImageWidth} < {turnedWidth})");
        editor.UndoStep(); editor.UndoStep(); editor.UndoStep();
        Pump(30);
        Check(Same(editor.Composite, clean), "undo back");
        ((Slider)Field(editor, "_angle")).Value = 30; // previewed, not applied
        Call(editor, "StartSession", EditorOverlayMode.Crop);
        Check(((FrameworkElement)Field(editor, "ImageHost")).RenderTransform.Value.IsIdentity, "a crop is never tilted by a previewed angle");
        editor.TypeCropSize(120, 80);
        var cropBox = ((EditorOverlay)Field(editor, "_overlay")).Crop;
        Check(cropBox.Width == 120 && cropBox.Height == 80, "the crop size label takes typed numbers");
        Check(((Border)Field(editor, "_cropSize")).Visibility == Visibility.Visible, "the crop shows the editable size label");
        Call(editor, "CancelSession");
        editor.Flip(true);
        editor.ResizeTo(200, 150);
        Pump(30);
        editor.ResetAll();
        Pump(30);
        Check(editor.ImageWidth == 400 && Same(editor.Composite, clean) && editor.AnnotationCount == 1, "重置所有修改: as opened");
        editor.UndoStep();
        Pump(30);
        Check(editor.ImageWidth == 200, "and undoable");
        editor.ResetAll();
        Pump(30);

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
        Pump(50);
        Check(!pinner.IsVisible, "贴到屏幕 closes the editor");
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
