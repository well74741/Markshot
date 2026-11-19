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
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

/// <summary>Object duplicate drag/flip, Space-left pan, original-image protection and ordinary OCR z-order.</summary>
static class Round80Test
{
    static int _fail;
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    static void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) _fail++; }
    static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle); System.Threading.Thread.Sleep(2); }
    }
    static FieldInfo Field(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType) if (t.GetField(name, Any) is { } f) return f;
        throw new Exception("Missing field " + name);
    }
    static T Get<T>(object value, string name) => (T)Field(value.GetType(), name).GetValue(value)!;
    static void Set(object value, string name, object data) => Field(value.GetType(), name).SetValue(value, data);
    static object? Call(object value, string name, params object[] args)
    {
        for (var t = value.GetType(); t != null; t = t.BaseType)
            if (t.GetMethod(name, Any) is { } method) return method.Invoke(value, args);
        throw new Exception("Missing method " + name);
    }
    static string Source(string name) => File.ReadAllText(Repo.File(name)).Replace("\r\n", "\n");
    static BitmapSource Image(byte red = 180, byte blue = 30, int w = 140, int h = 100)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = blue; pixels[i + 1] = 20; pixels[i + 2] = red; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4); image.Freeze(); return image;
    }
    static BitmapSource DrawLayers(IReadOnlyList<Annotation> items)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) AnnotationEditor.DrawLayers(dc, items);
        var image = new RenderTargetBitmap(140, 100, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel;
    }
    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var previousLock = Markshot.App.Config.LockOriginalImage;
        var previousSnap = Markshot.App.Config.EditorSnap;
        Markshot.App.Config.EditorSnap = false;
        Markshot.App.Config.LockOriginalImage = false;
        var editor = new ImageEditorWindow(Image()) { ShowActivated = false, Opacity = 0 };
        editor.Show(); Pump(300);
        var canvas = Get<AnnotationEditor>(editor, "Editor");
        var history = Get<AnnotationHistory>(editor, "_history");
        try
        {
            var original = canvas.Items.OfType<ImageAnnotation>().Single();
            Check(original.IsInitialImage, "opened/captured source image has a stable original identity");
            var shape = new RectAnnotation { Rect = new Rect(20, 20, 40, 30), Fill = true, Color = Colors.Red };
            shape.AddEraseStroke(new[] { new Point(23, 23) }, 3);
            canvas.Items.Add(shape); history.Clear();
            var hit = Call(editor, "HitAnnotation", new Point(40, 35));
            var began = (bool)Call(editor, "BeginDuplicateDrag", new Point(40, 35), false)!;
            Check(began && canvas.Items.Count == 2, $"Alt drag arms a copy, but an Alt click alone creates nothing ({began}, {canvas.Items.Count} items, hit {hit?.GetType().Name})");
            Call(editor, "CancelGesture");
            shape = canvas.Items.OfType<RectAnnotation>().Single();
            Check(canvas.Items.Count == 2 && !history.CanUndo, "cancelling before moving creates no copy/undo step");
            Check((bool)Call(editor, "BeginDuplicateDrag", new Point(40, 35), false)!, "begin duplicate drag again");
            Call(editor, "CommitDuplicateDrag");
            var copy = canvas.Items.OfType<RectAnnotation>().Last();
            Call(editor, "BeginMoveSnap");
            Get<CheckBox>(editor, "_snapMoves").IsChecked = false;
            Call(editor, "MoveSelectedWithSnap", new Point(88, 71));
            Check(canvas.Items.Count == 3 && shape.Rect == new Rect(20, 20, 40, 30) && copy.Rect == new Rect(68, 56, 40, 30),
                $"copy follows the drag offset; original is unchanged ({shape.Rect} / {copy.Rect})");
            Check(!ReferenceEquals(copy.EraseMasks, shape.EraseMasks) && copy.EraseMasks?.Count == 1, "copied eraser gaps are independent and move with the copy");
            Set(editor, "_movingSelected", false); Mouse.Captured?.ReleaseMouseCapture();
            Call(editor, "Undo");
            Check(canvas.Items.Count == 2, "one undo removes the whole duplicate drag");
            Call(editor, "Redo");
            Check(canvas.Items.Count == 3, "redo restores the copy at its drop position");

            editor.SelectElements(canvas.Items.OfType<RectAnnotation>().ToArray());
            var count = canvas.Items.Count;
            Call(editor, "BeginGroupMove", new Point(40, 35), true, false);
            history.CommitArmed();
            Call(editor, "CommitGroupDuplicateDrag"); Call(editor, "CommitGroupDuplicateDrag");
            Check(canvas.Items.Count == count + 2 && editor.SelectedElements.Count == 2, "multi-selection copies exactly once and selects only its copies");
            Check((bool)Call(editor, "CancelGesture")! && canvas.Items.Count == count && editor.SelectedElements.Count == 2 && !Get<bool>(editor, "_groupMoving"),
                "right-cancel removes the whole group copy and restores its original selection");
            var depth = history.Depth;
            Call(editor, "BeginGroupMove", new Point(40, 35), true, false);
            Call(editor, "CancelGesture");
            Check(canvas.Items.Count == count && history.Depth == depth && !Get<bool>(editor, "_groupCopyDrag"), "group Alt click/cancel creates no copy and leaves no stale drag");
            editor.SelectElements(Array.Empty<Annotation>());

            var pan = editor.PanOffset; var imagePlace = original.Rect;
            Call(editor, "BeginCanvasPan", new Point(50, 50), false);
            Call(editor, "ContinueCanvasPan", new Point(70, 80));
            Check((editor.PanOffset - pan - new Vector(20, 30)).Length < 1 && original.Rect == imagePlace, "middle-drag pans the canvas, not any element");
            Call(editor, "EndPan");
            Check(!(bool)Get<object>(editor, "_panning"), "pan ends cleanly");

            // Use a fresh scene to ensure all target fallbacks are blocked, not redirected to a new image.
            canvas.Items.Clear(); canvas.Items.Add(original); history.Clear();
            editor.OriginalImageLocked = true;
            Check(original.Protected && editor.SelectedElements.Count == 0, "locking the source clears its selection and protects it");
            var eraser = EraseStroke.Widen(new[] { new Point(45, 40) }, 12);
            var liveVisual = new DrawingVisual();
            using (var dc = liveVisual.RenderOpen()) AnnotationEditor.DrawItem(dc, original, eraser);
            var liveImage = new RenderTargetBitmap(140, 100, 96, 96, PixelFormats.Pbgra32); liveImage.Render(liveVisual);
            Check(Pixel(liveImage, 45, 40)[2] > 150 && Pixel(liveImage, 45, 40)[3] > 200, "ordinary live eraser also leaves the protected original visible");
            var eraserOps = typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps")!;
            var touched = (bool)eraserOps.GetMethod("BindEraser", BindingFlags.Public | BindingFlags.Static)!.Invoke(null,
                new object[] { new[] { original }, new[] { new Point(45, 40) }, 12d, 1d, 0d })!;
            Check(!touched && original.EraseMasks == null, "protected-only erasing reports no change and cannot create an empty undo");
            var lockSwitch = Get<ToggleButton>(editor, "_lockOriginalSwitch");
            Check(lockSwitch.IsEnabled && lockSwitch.IsChecked == true, "the original lock stays usable even with no selection");
            var before = original.Rect; var bitmap = original.Image;
            editor.ResizeTo(80, 60); editor.TransformTo(new Rect(50, 50, 80, 70), 25); editor.RotateBy(90);
            editor.CropPicture(original, new Rect(20, 20, 60, 50));
            editor.SetAdjust(new ImageAdjust(Brightness: 0.2));
            editor.TransformItems(new[] { original }, before, new Rect(10, 10, 70, 50), 45, true, true);
            editor.Flip(true); editor.Turn(true);
            Check(original.Rect == before && original.Rotation == 0 && ReferenceEquals(original.Image, bitmap) && !history.CanUndo,
                "locked original cannot resize, transform, crop, recolour, flip or rotate through fallback/explicit APIs");
            editor.SelectElements(new[] { original });
            Check(editor.SelectedElements.Count == 0 && !(bool)Call(editor, "BeginDuplicateDrag", new Point(70, 60), false)!, "locked source cannot be picked or dragged accidentally");

            var layer = new ImageAnnotation { Image = Image(20, 230, 60, 45), Rect = new Rect(20, 20, 60, 45) };
            canvas.Items.Add(layer);
            editor.SelectElements(new[] { layer });
            Check(editor.SelectedElements.Single() == layer && !layer.Protected && !layer.IsInitialImage, "new image layers stay editable while the original is locked");
            editor.TransformTo(new Rect(25, 24, 65, 46), 0);
            Check(layer.Rect == new Rect(25, 24, 65, 46) && original.Rect == before, "transforms apply to a new layer without changing the original");
            var cut = new CutoutAnnotation { Thickness = 12 }; cut.Points.Add(new Point(45, 40)); canvas.Items.Add(cut);
            var preview = DrawLayers(canvas.Items);
            Check(Pixel(preview, 45, 40)[2] > 150 && Pixel(preview, 45, 40)[0] < 60, "live cutout removes new layer but not the protected original underneath");
            Cutouts.Bake(canvas.Items, cut);
            Check(original.EraseMasks == null && layer.EraseMasks?.Count == 1, "baked cutout skips protected source but still erases new layers");
            var output = DrawLayers(canvas.Items);
            Check(Pixel(output, 45, 40)[2] > 150 && Pixel(output, 45, 40)[0] < 60, "export/render matches protected live preview");
            Call(editor, "Undo"); // source protection must survive snapshot replacement
            var restoredOriginal = canvas.Items.OfType<ImageAnnotation>().First(p => p.IsInitialImage);
            Check(restoredOriginal.Protected && editor.OriginalImageLocked, "undo creates new objects but retains original identity/protection");
            editor.OriginalImageLocked = false;
            editor.SelectElements(new[] { restoredOriginal });
            Check(!restoredOriginal.Protected && editor.SelectedElements.Count == 1, "turning the lock off restores ordinary editing");
        }
        finally { Mouse.Captured?.ReleaseMouseCapture(); editor.CloseWithoutAsking(); Markshot.App.Config.LockOriginalImage = previousLock; Markshot.App.Config.EditorSnap = previousSnap; }

        // Pending transforms must never mutate an Alt-copy's source; previews are a separate preceding edit.
        var transforms = new ImageEditorWindow(Image()) { ShowActivated = false, Opacity = 0 };
        transforms.Show(); Pump(100);
        try
        {
            var scene = Get<AnnotationEditor>(transforms, "Editor");
            var rect = new RectAnnotation { Rect = new Rect(20, 20, 40, 30), Fill = true };
            scene.Items.Add(rect);
            Call(transforms, "BeginDuplicateDrag", new Point(40, 35), false);
            Set(transforms, "_dragScale", true); Set(transforms, "_dragScaleBounds", rect.Bounds); Set(transforms, "_dragScalePointer", new Point(40, 35));
            Call(transforms, "TransformSelected", new Point(80, 70));
            Check(scene.Items.Count == 3 && rect.Rect == new Rect(20, 20, 40, 30) && scene.Items.OfType<RectAnnotation>().Last().Rect != rect.Rect,
                "Alt copy with R scaling transforms only the newly created copy");
            Call(transforms, "CancelGesture");
            var source = scene.Items.OfType<ImageAnnotation>().Single();
            transforms.SelectElements(new[] { source });
            Get<Slider>(transforms, "_angle").Value = 30;
            Call(transforms, "BeginDuplicateDrag", new Point(70, 50), false);
            Call(transforms, "CommitDuplicateDrag");
            Check(source.Rotation == 30 && scene.Items.OfType<ImageAnnotation>().Last().Rotation == 30, "copying after angle preview does not silently roll back the original");
            Call(transforms, "Undo");
            Check(scene.Items.OfType<ImageAnnotation>().Count() == 1 && scene.Items.OfType<ImageAnnotation>().Single().Rotation == 30,
                "undoing the copy retains the separately committed preceding angle");
        }
        finally { transforms.CloseWithoutAsking(); }

        var capture = new OverlayWindow(Image(), new Int32Rect(-4000, -4000, 140, 100)) { ShowActivated = false };
        try
        {
            var scene = Get<AnnotationEditor>(capture, "Editor");
            var undo = Get<AnnotationHistory>(capture, "_history");
            var rect = new RectAnnotation { Rect = new Rect(20, 20, 40, 30), Fill = true };
            scene.Items.Add(rect); undo.Checkpoint(scene.Items); rect.Translate(5, 0); undo.Undo(scene.Items);
            Call(capture, "BeginDuplicateElementDrag", new Point(40, 35), false);
            Check(!(bool)Call(capture, "PrepareElementDrag", new Point(41, 35))! && !undo.CanUndo && undo.CanRedo && scene.Items.Count == 1,
                "capture Alt click with sub-threshold jitter retains redo and creates no empty undo/copy");
            Set(capture, "_dragScale", true);
            Check((bool)Call(capture, "PrepareElementDrag", new Point(50, 35))! && scene.Items.Count == 2 && Get<Annotation>(capture, "_selectedAnnotation") != scene.Items[0],
                "capture commits the copy before E/R transform branches");
            Call(capture, "CancelGesture");
            Check(scene.Items.Count == 1 && !undo.CanUndo, "capture copy cancellation removes its entire gesture");
        }
        finally { capture.Close(); }

        // Actual element flip + archive persistence, including a v1 file with real annotations and lazy undo.
        var folder = Path.Combine(Path.GetTempPath(), "markshot-r80-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var picture = new ImageAnnotation { Image = Image(), Rect = new Rect(0, 0, 140, 100), IsInitialImage = true };
            var label = new TextAnnotation { Text = "ABC", Position = new Point(12, 14), FontSize = 18 };
            var items = new List<Annotation> { picture, label };
            var undo = new AnnotationHistory(); undo.Checkpoint(items);
            var ops = typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps")!;
            var asymmetric = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, 8);
            asymmetric.Freeze();
            var photo = new ImageAnnotation { Image = asymmetric, Rect = new Rect(0, 0, 2, 1) };
            ops.GetMethod("FlipElements", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { new[] { photo }, true });
            var flipped = new byte[8]; photo.Image!.CopyPixels(flipped, 8, 0);
            Check(flipped[0] == 255 && flipped[2] == 0 && flipped[6] == 255, "horizontal flip mirrors actual image pixels within the element");
            var line = new LineAnnotation { Start = new Point(30, 30), End = new Point(70, 45), Rotation = 35 };
            line.AddEraseStroke(new[] { new Point(40, 35) }, 4, 0.4, 0.7);
            var lineBounds = line.Bounds;
            var center = new Point(lineBounds.Left + lineBounds.Width / 2, lineBounds.Top + lineBounds.Height / 2);
            var worldStart = AnnotationEditor.RotatePoint(line.Start, center, line.Rotation);
            ops.GetMethod("FlipElements", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { new[] { line }, true });
            var mirrorStart = AnnotationEditor.RotatePoint(line.Start, center, line.Rotation);
            Check((mirrorStart - new Point(2 * center.X - worldStart.X, worldStart.Y)).Length < 0.001 && line.Rotation == -35,
                "horizontal flip of a rotated element reflects in canvas axes");
            Check(line.EraseMasks![0].Strength == 0.4 && line.EraseMasks[0].Softness == 0.7, "flip retains eraser transparency and soft edges");
            ops.GetMethod("FlipElements", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { new[] { label }, true });
            Check(label.FlipX && label.Text == "ABC", "text flip retains editable text (not a raster replacement)");
            label.AddEraseStroke(new[] { new Point(label.Bounds.Left + 2, label.Bounds.Top + 4) }, 4);
            Check(label.IsErasedAt(new Point(label.Bounds.Left + 2, label.Bounds.Top + 4)), "eraser coordinates account for mirrored text");
            var state = new PinArchiveState(new PinSnapshot(Image(), Image(), items, 0, 0, 1, 1), undo);
            var archive = PinArchive.Create(state, Path.Combine(folder, "v2.markpin"));
            var loaded = archive.Restore();
            Check(loaded.Snapshot.Annotations.OfType<ImageAnnotation>().Single().IsInitialImage && loaded.Snapshot.Annotations.OfType<TextAnnotation>().Single().FlipX,
                "original identity and glyph mirrors persist in new pin archives");
            var legacy = (PinArchive)typeof(PinArchive).GetMethod("CreateLegacyForTests", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                new object[] { state, Path.Combine(folder, "v1.markpin") })!;
            var old = legacy.Restore();
            Check(!old.Snapshot.Annotations.OfType<ImageAnnotation>().Single().IsInitialImage && !old.Snapshot.Annotations.OfType<TextAnnotation>().Single().FlipX,
                "v1 archives still read with their original layout and absent new fields");
            var oldItems = old.Snapshot.Annotations; old.History.Undo(oldItems);
            Check(oldItems.Count == 2 && oldItems.OfType<TextAnnotation>().Single().Text == "ABC", "v1 lazy undo annotations use the old schema too");
            var imported = legacy.Restore();
            var legacyEditor = new ImageEditorWindow(ImageOps.Blank(140, 100), annotations: imported.Snapshot.Annotations) { ShowActivated = false };
            try
            {
                legacyEditor.AdoptHistory(imported.History);
                legacyEditor.OriginalImageLocked = true;
                Call(legacyEditor, "Undo");
                var legacyScene = Get<AnnotationEditor>(legacyEditor, "Editor");
                Check(legacyScene.Items.OfType<ImageAnnotation>().Any(p => p.IsInitialImage && p.Protected) && Get<ToggleButton>(legacyEditor, "_lockOriginalSwitch").IsEnabled,
                    "v1 editor history migrates original identity and retains lock after lazy undo");
            }
            finally { legacyEditor.CloseWithoutAsking(); Markshot.App.Config.LockOriginalImage = false; }
            var pastedImage = new ImageAnnotation { Image = Image(40, 80, 40, 30), Rect = new Rect(25, 25, 40, 30) };
            var captureHistory = new AnnotationHistory(); captureHistory.Checkpoint(new[] { pastedImage });
            var capturedEditor = new ImageEditorWindow(Image(), annotations: new List<Annotation> { pastedImage }) { ShowActivated = false };
            try
            {
                capturedEditor.AdoptHistory(captureHistory);
                capturedEditor.OriginalImageLocked = true;
                Call(capturedEditor, "Undo");
                var capturedScene = Get<AnnotationEditor>(capturedEditor, "Editor");
                Check(capturedScene.Items.OfType<ImageAnnotation>().Count() == 2 && capturedScene.Items.OfType<ImageAnnotation>().Count(p => p.IsInitialImage && p.Protected) == 1,
                    "capture undo containing a pasted image still retains its protected source picture");
            }
            finally { capturedEditor.CloseWithoutAsking(); Markshot.App.Config.LockOriginalImage = previousLock; }
            var annotationClipboard = typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationClipboard")!;
            var duplicate = (ImageAnnotation)annotationClipboard.GetMethod("CloneForDuplication", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { picture })!;
            Check(!duplicate.IsInitialImage && !duplicate.Protected, "a source image's copy does not inherit original-image lock identity");
        }
        finally { Directory.Delete(folder, true); }

        var ocr = new TranslationWindow(null, autoTranslate: false) { ShowActivated = false };
        Check(!ocr.Topmost, "text recognition is an ordinary, non-topmost window"); ocr.Close();
        Check(Source("UI/ImageEditorWindow.cs").Contains("左右翻转") && Source("UI/PinWindow.Menu.cs").Contains("上下翻转元素") &&
              Source("UI/OverlayWindow.ElementActions.cs").Contains("ShowSelectedElementMenu"), "flip actions are wired to editor/pin/capture element menus");
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
