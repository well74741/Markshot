using System;
using System.Collections.Generic;
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

// 折线, arrow heads.
static class AnnotateTest
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

    static BitmapSource Blank(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 255; px[i + 3] = 255; }
        return ImageOps.FromPixels(px, w, h);
    }

    static BitmapSource Render(Annotation a, int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) a.Draw(dc);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return bmp;
    }

    static byte Alpha(BitmapSource image, int x, int y)
    {
        var (px, w, _) = ImageOps.ReadPixels(image);
        return px[(y * w + x) * 4 + 3];
    }

    static byte PixelA(BitmapSource image, int x, int y)
    {
        var px = new byte[4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return px[3];
    }

    static object Get(object o, string name) =>
        typeof(PinWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    /// <summary>The pin's own press handler (Window has a method of the same name).</summary>
    static void PinDown(PinWindow pin, MouseButtonEventArgs press) =>
        typeof(PinWindow).GetMethod("OnMouseLeftButtonDown", BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(object), typeof(MouseButtonEventArgs) })!.Invoke(pin, new object[] { pin, press });

    static void CallEd(object o, string name, params object[] args) =>
        typeof(ImageEditorWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, args);

    static void Call(object o, string name, params object[] args) =>
        typeof(PinWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, args);

    public static void Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Theme.Apply(ThemeMode.Dark);

        // ---- the draft: clicks are corners, the last point follows the mouse
        var line = new PolylineAnnotation { Color = Colors.Red, Thickness = 4 };
        var draft = new PolylineDraft(line, new Point(10, 10));
        draft.Move(new Point(100, 12), false);
        draft.Press(new Point(100, 12), false);
        draft.Release(new Point(100, 12), false, 4);
        draft.Move(new Point(100, 90), false);
        Check(line.Points.Count == 3 && line.Points[^1] == new Point(100, 90), "a click fixes a corner, the end follows the mouse");
        draft.Move(new Point(150, 95), true);
        var last = line.Points[^1];
        Check(Math.Abs(Math.Atan2(last.Y - 12, last.X - 100) * 180 / Math.PI - 45) < 0.01, "Shift keeps the segment at 45°");
        draft.Press(new Point(160, 160), false);
        draft.Release(new Point(160, 230), false, 4); // dragged from the click: another corner
        Check(line.Points.Count == 5, "a drag from a corner draws a segment");
        Check(draft.RemoveLast() && line.Points.Count == 4, "Backspace takes a corner back");
        draft.Press(new Point(200, 40), false); // double-click: first click places, second ends
        Check(draft.Finish() && line.Points.Count == 4 && line.Points[^1] == new Point(200, 40), "finishing drops the follow point and repeats");
        var single = new PolylineDraft(new PolylineAnnotation(), new Point(5, 5));
        Check(!single.Finish() && AnnotationOps_IsTooSmall(single.Target), "a click alone makes no line");

        var drawn = Render(line, 260, 260);
        Check(Alpha(drawn, 55, 11) > 200 && Alpha(drawn, 130, 86) > 200 && Alpha(drawn, 60, 60) == 0, "straight segments, nothing between");
        Check(line.HitTest(new Point(130, 86), 2) && !line.HitTest(new Point(60, 60), 2), "hit test on the segments");
        var copy = (PolylineAnnotation)line.Clone();
        copy.Points[0] = new Point(0, 0);
        Check(line.Points[0] == new Point(10, 10), "a copy has its own corners");

        // ---- arrow heads
        foreach (var style in Enum.GetValues<ArrowStyle>())
        {
            var arrow = new ArrowAnnotation { Start = new Point(20, 50), End = new Point(180, 50), Color = Colors.Blue, Thickness = 4, Style = style };
            var image = Render(arrow, 200, 100);
            bool On(int x, int y) => Alpha(image, x, y) > 100;
            var ok = style switch
            {
                ArrowStyle.Sharp => On(176, 50) && On(160, 55) && !On(26, 54),
                ArrowStyle.Open => On(176, 50) && On(172, 54) && On(22, 50) && !On(26, 56),
                ArrowStyle.Triangle => On(176, 50) && On(165, 56) && On(22, 50) && !On(26, 56),
                _ => On(176, 50) && On(165, 56) && On(35, 56) && On(22, 50)
            };
            Check(ok, "arrow style " + style);
        }

        // a picture of all heads and a polyline (tests/out)
        var sheet = new DrawingVisual();
        using (var dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 420, 260));
            var y = 30;
            foreach (var style in Enum.GetValues<ArrowStyle>())
            {
                new ArrowAnnotation { Start = new Point(20, y), End = new Point(200, y), Color = Color.FromRgb(0xFF, 0x4D, 0x4F), Thickness = 4, Style = style }.Draw(dc);
                y += 55;
            }

            var shown = (PolylineAnnotation)line.Clone();
            shown.Translate(200, 0);
            shown.Draw(dc);
        }

        var sheetBmp = new RenderTargetBitmap(420, 260, 96, 96, PixelFormats.Pbgra32);
        sheetBmp.Render(sheet);
        var sheetEnc = new PngBitmapEncoder();
        sheetEnc.Frames.Add(BitmapFrame.Create(sheetBmp));
        using (var f = System.IO.File.Create("annotate_shapes.png")) sheetEnc.Save(f);

        // ---- in an editing window (pin / image editor): the tool, the bar and undo
        var editor = new ImageEditorWindow(Blank(300, 200)) { Left = 40, Top = 40, ShowActivated = false };
        editor.Show();
        Pump(300);
        var toolbar = (AnnotationToolbar)Get(editor, "Toolbar");
        toolbar.SetTool(AnnotationTool.Polyline);
        Call(editor, "SetTool", AnnotationTool.Polyline);
        Pump(50);
        var lineVariants = (FrameworkElement)toolbar.FindName("LineVariants");
        var arrowStyles = (FrameworkElement)toolbar.FindName("ArrowStyleGroup");
        var polyRadio = (RadioButton)toolbar.FindName("VariantPolyline");
        Check(lineVariants.Visibility == Visibility.Visible && polyRadio.IsChecked == true && arrowStyles.Visibility != Visibility.Visible,
            "折线 is a style in the line row (no head choice)");
        Check(AnnotationOps_Next(AnnotationTool.Polyline) == AnnotationTool.Arrow && AnnotationOps_Next(AnnotationTool.Arrow) == AnnotationTool.Line,
            "Tab switches the main kinds only (箭头 ↔ 直线)");
        Call(editor, "StartAnnotation", new Point(20, 20));
        var placing = (PolylineDraft)Get(editor, "_polyline");
        placing.Press(new Point(120, 30), false);
        placing.Press(new Point(150, 150), false);
        Call(editor, "FinishPolyline");
        Pump(30);
        var items = ((AnnotationEditor)Get(editor, "Editor")).Items;
        Check(items.Count(i => i is not ImageAnnotation) == 1 && items[^1] is PolylineAnnotation { Points.Count: 3 }, "折线 lands as one element");
        editor.Flip(true);
        Pump(30);
        Check(items.OfType<PolylineAnnotation>().First().Points[0].X == 280, "mirroring moves the corners");
        editor.UndoStep();
        editor.UndoStep();
        Pump(30);
        Check(!items.Any(i => i is not ImageAnnotation), "undo removes it");
        Call(editor, "StartAnnotation", new Point(20, 20));
        Call(editor, "CancelGesture");
        Check(Get(editor, "_polyline") == null && !items.Any(i => i is not ImageAnnotation), "Esc drops a polyline being placed");

        toolbar.SetTool(AnnotationTool.Arrow);
        Call(editor, "SetTool", AnnotationTool.Arrow);
        Pump(50);
        Check(arrowStyles.Visibility == Visibility.Visible, "the arrow shows its head styles");
        ((RadioButton)toolbar.FindName("ArrowDouble")).IsChecked = true;
        Check(toolbar.ArrowStyle == ArrowStyle.Double, "picking a head");
        var created = (ArrowAnnotation)typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps")!
            .GetMethod("Create")!.Invoke(null, new object[] { AnnotationTool.Arrow, new Point(5, 5), toolbar, 1, Blank(10, 10) })!;
        Check(created.Style == ArrowStyle.Double, "new arrows get the chosen head");
        TestShot(editor, toolbar);
        editor.CloseWithoutAsking();

        // ---- capture → pin: the undo steps go along (in the pin's space), across edit sessions and flips
        var history = new AnnotationHistory();
        var live = new List<Annotation>();
        history.Checkpoint(live);
        live.Add(new RectAnnotation { Rect = new Rect(110, 120, 40, 30), Color = Colors.Red, Thickness = 3 });
        history.Checkpoint(live);
        live.Add(new ArrowAnnotation { Start = new Point(110, 110), End = new Point(180, 160), Color = Colors.Red, Thickness = 3 });
        var origin = new Point(100, 100);
        var pinBase = Blank(200, 150);
        var handed = history.Convert(step => step.Select(a => { var c = a.Clone(); c.Translate(-origin.X, -origin.Y); return c; }).ToList());
        var pinItems = live.Select(a => { var c = a.Clone(); c.Translate(-origin.X, -origin.Y); return c; }).ToList();
        var pin = new PinWindow(pinBase, -4000, -4000, pinBase, pinItems) { Left = -4000, Top = -4000, ShowActivated = false };
        pin.Show();
        Pump(300);
        pin.AdoptHistory(handed);
        var pinEditor = ((AnnotationEditor)Get(pin, "Editor")).Items;
        Call(pin, "EnterEditMode");
        Pump(50);
        Call(pin, "Undo");
        Check(pinEditor.Count == 1 && ((RectAnnotation)pinEditor[0]).Rect.X == 10, "Ctrl+Z in the pin undoes the capture's last step (pin coordinates)");
        Call(pin, "ExitEditMode", true);
        Pump(50);
        typeof(PinWindow).GetMethod("ApplyOrientation", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pin, new object[] { 'H', true });
        Call(pin, "EnterEditMode");
        Pump(50);
        Call(pin, "Redo");
        var redone = pinEditor.OfType<ArrowAnnotation>().FirstOrDefault();
        Check(redone != null && Math.Abs(redone.Start.X - 190) < 0.01, "steps survive leaving edit mode and follow a mirror");
        Call(pin, "Undo");
        Call(pin, "Undo");
        Check(pinEditor.Count == 0, "back to the capture's first step");
        pin.CloseImmediately();

        // ---- OCR keeps the layout: rows, columns (tab), indents, paragraph gaps
        Markshot.Services.OcrTextBlock B(string t, int x, int y, int w) => new(t, new Int32Rect(x, y, w, 20));
        var layout = Markshot.Services.OcrService.Layout(new[]
        {
            B("名称", 10, 10, 40), B("数量", 200, 12, 40), B("价格", 320, 9, 40),
            B("苹果", 10, 40, 40), B("3", 200, 41, 10), B("5.0", 320, 40, 30),
            B("备注：", 10, 100, 60), B("第二段", 50, 130, 60),
            B("Hello", 10, 160, 50), B("world", 66, 161, 50)
        });
        var lines = layout.Split(Environment.NewLine);
        Console.WriteLine(layout.Replace("	", "→"));
        Check(lines.Length == 6 && lines[0] == "名称	数量	价格" && lines[1] == "苹果	3	5.0", "a table comes out as tab-separated rows");
        Check(lines.Length == 6 && lines[2] == "" && lines[3] == "备注：", "a tall gap leaves an empty line");
        Check(lines.Length == 6 && lines[4] == "    第二段", "a line starting further right is indented");
        Check(lines.Length == 6 && lines[5] == "Hello world", "words in a row keep their space");

        // ---- pins picked together: Shift adds, Ctrl takes out; they move and close together
        PinWindow MakePin(int x) { var p = new PinWindow(Blank(120, 80), x, -4000, null, null) { ShowActivated = false }; p.Show(); return p; }
        var a = MakePin(-4000); var b = MakePin(-3800); var c = MakePin(-3600);
        Pump(300);
        bool Press(PinWindow p, ModifierKeys m) => (bool)typeof(PinWindow).GetMethod("GroupPress", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p, new object[] { m })!;
        int Left(PinWindow p) => (int)Get(p, "_left");
        Visibility Mark(PinWindow p) => ((FrameworkElement)p.FindName("GroupMark")).Visibility;
        Press(a, ModifierKeys.None);
        Check(PinWindow.SelectedPins.Count == 0, "a plain click picks nothing");
        Press(b, ModifierKeys.Shift);
        Check(PinWindow.SelectedPins.Count == 2 && a.InGroup && b.InGroup && Mark(a) != Visibility.Visible, "click A, Shift+click B: both picked (no dashed outline)");
        Press(c, ModifierKeys.Shift);
        Check(PinWindow.SelectedPins.Count == 3, "Shift+click adds more");
        Check(Press(b, ModifierKeys.Control) && !b.InGroup && Mark(b) == Visibility.Collapsed && PinWindow.SelectedPins.Count == 2, "Ctrl+click takes one out");
        var aLeft = Left(a); var cLeft = Left(c); var bLeft = Left(b);
        Press(a, ModifierKeys.None);
        Call(a, "BeginGroupDrag");
        Call(a, "MoveGroup", 25, 10);
        Check(Left(c) == cLeft + 25 && Left(b) == bLeft, "dragging a picked pin moves the others picked");
        Call(a, "EndGroupDrag", true);
        Check(PinWindow.SelectedPins.Count == 2, "the group stays after a drag");
        Call(a, "NudgeWithGroup", -5, 0);
        Check(Left(a) == aLeft - 5 && Left(c) == cLeft + 20, "arrow keys nudge all picked pins");
        Press(a, ModifierKeys.None);
        Call(a, "EndGroupDrag", false);
        Check(PinWindow.SelectedPins.Count == 0 && Mark(c) == Visibility.Collapsed, "a click without moving leaves just that pin");
        Press(a, ModifierKeys.None);
        Press(c, ModifierKeys.Shift);
        Call(c, "CloseWithGroup");
        Pump(600);
        Check(!a.IsVisible && !c.IsVisible && b.IsVisible && PinWindow.SelectedPins.Count == 0, "Esc closes the picked pins only");
        b.CloseImmediately();

        // ---- mosaic: the average colour of each block (white text on dark stays grey, no coloured fringes)
        var textPx = new byte[24 * 24 * 4];
        for (var i = 0; i < textPx.Length; i += 4)
        {
            var x = (i / 4) % 24;
            var on = x % 3 == 0; // thin white strokes with coloured anti-aliasing beside them
            textPx[i] = (byte)(on ? 255 : x % 3 == 1 ? 200 : 20);
            textPx[i + 1] = (byte)(on ? 255 : 20);
            textPx[i + 2] = (byte)(on ? 255 : x % 3 == 2 ? 200 : 20);
            textPx[i + 3] = 255;
        }

        var textImage = ImageOps.FromPixels(textPx, 24, 24);
        var pattern = MosaicBuilder.Build(textImage, new Rect(0, 0, 24, 24), 12)!;
        var (patPx, _, _) = ImageOps.ReadPixels(pattern);
        Check(Math.Abs(patPx[0] - patPx[2]) < 30 && patPx[1] > 80, $"mosaic block = average colour ({patPx[2]},{patPx[1]},{patPx[0]})");

        // ---- 抠除: a hole through the picture and every annotation, also those drawn later
        var cutEditor = new ImageEditorWindow(Blank(200, 120)) { Left = 40, Top = 40, ShowActivated = false };
        cutEditor.Show();
        Pump(300);
        var cutItems = ((AnnotationEditor)Get(cutEditor, "Editor")).Items;
        cutItems.Add(new RectAnnotation { Rect = new Rect(20, 20, 100, 60), Color = Colors.Red, Thickness = 6, Fill = true });
        var hole = new CutoutAnnotation { Thickness = 20 };
        hole.Points.Add(new Point(60, 50));
        hole.Points.Add(new Point(140, 50));
        cutItems.Add(hole);
        cutItems.Add(new RectAnnotation { Rect = new Rect(100, 30, 60, 40), Color = Colors.Blue, Thickness = 4, Fill = true }); // drawn after the hole
        var cut = cutEditor.Composite;
        byte A(int x, int y) { var (px, w, _) = ImageOps.ReadPixels(cut); return px[(y * w + x) * 4 + 3]; }
        Check(A(80, 50) == 0 && A(120, 50) == 0 && A(10, 10) == 255 && A(80, 25) == 255, "抠除: transparent hole through the picture and annotations; the rest stays");
        Check(cutItems.OfType<CutoutAnnotation>().Count() == 1 && AnnotationOps_HitNothing(cutItems, new Point(150, 50)), "the hole is never selected");
        var toolbarCut = (AnnotationToolbar)Get(cutEditor, "Toolbar");
        toolbarCut.SetTool(AnnotationTool.Eraser);
        Check(((FrameworkElement)toolbarCut.FindName("EraserChevron")).Visibility == (Markshot.App.Config.HideToolChevrons ? Visibility.Collapsed : Visibility.Visible), "the eraser button has its ▾ menu (unless the ▾ are hidden: then on hover)");
        var modes = (FrameworkElement)toolbarCut.FindName("EraserModes");
        var row = (Panel)VisualTreeHelper.GetParent(modes);
        Check(row.Children.Cast<FrameworkElement>().TakeWhile(c => !ReferenceEquals(c, modes)).All(c => c.Visibility != Visibility.Visible), "擦除 / 抠除 are the first thing shown in the settings row");
        Check(AnnotationOps_Next(AnnotationTool.Eraser) == AnnotationTool.Cutout && AnnotationOps_Next(AnnotationTool.Cutout) == AnnotationTool.Eraser, "Tab switches 擦除 ↔ 抠除");
        toolbarCut.PickVariant(AnnotationTool.Cutout);
        Check(toolbarCut.EraserCutout && ((RadioButton)toolbarCut.FindName("EraserCut")).IsChecked == true, "the eraser has the 抠除 style");
        Check(((FrameworkElement)toolbarCut.FindName("ClearPinButton")) is { } && toolbarCut.FindName("ClearPinButton") is Button, "clear button exists");
        Call(cutEditor, "UpdateUndoButtons");
        typeof(PinWindow).GetMethod("ClearAnnotations", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cutEditor, null);
        Check(cutItems.Count == 1 && cutItems[0] is ImageAnnotation, "清空批注 removes every annotation, the picture stays");
        cutEditor.UndoStep();
        Pump(30);
        Check(cutItems.Count(i => i is not ImageAnnotation) == 3, "and it can be undone");
        cutEditor.CloseWithoutAsking();

        // ---- editor: copy / paste after typing in a box, pictures pasted as elements, layers
        {
            var ed = new ImageEditorWindow(Blank(300, 200)) { Left = 40, Top = 40, ShowActivated = false };
            ed.Show();
            Pump(300);
            var edItems = ((AnnotationEditor)Get(ed, "Editor")).Items;
            var first = new RectAnnotation { Rect = new Rect(20, 20, 50, 40), Color = Colors.Red, Thickness = 3 };
            var second = new RectAnnotation { Rect = new Rect(40, 40, 50, 40), Color = Colors.Blue, Thickness = 3 };
            edItems.Add(first);
            edItems.Add(second);
            var sizeBox = (TextBox)typeof(ImageEditorWindow).GetField("_widthBox", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(ed)!;
            sizeBox.Focus();
            Check(sizeBox.IsKeyboardFocused, "the size box has the keyboard");
            Call(ed, "ReclaimKeyboard");
            Check(Keyboard.FocusedElement is not TextBox, "leaving the box gives the keyboard back to the canvas (copy / paste work again)");
            var mover = typeof(PinWindow).GetMethod("MoveLayer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Check(edItems[0] is ImageAnnotation && edItems.Count == 3, "the opened picture is an element at the bottom");
            mover.Invoke(ed, new object[] { first, 2 });
            Check(edItems[2] == first, "上移一层");
            mover.Invoke(ed, new object[] { first, 0 });
            Check(edItems[0] == first && edItems[1] is ImageAnnotation, "置于底层: an annotation can go under the picture");
            ed.UndoStep();
            Pump(30);
            Check(edItems.Count == 3 && edItems[2] is RectAnnotation { Color: var layerColor } && layerColor == Colors.Red, "layer change is undoable");

            var picture = new ImageAnnotation { Image = Blank(60, 40), Rect = new Rect(100, 100, 60, 40) };
            edItems.Add(picture);
            typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps")!.GetMethod("Resize")!.Invoke(null, new object[] { picture, new Rect(100, 100, 120, 80), 4 });
            Check(picture.Rect.Width == 120 && picture.Bounds.Height == 80, "a picture element resizes");
            picture.Translate(10, 5);
            Check(picture.Rect.X == 110, "and moves");
            Check(picture.Clone() is ImageAnnotation { Image: not null }, "and copies");
            Check(!picture.Erasable, "the eraser leaves pictures alone (抠除 cuts through them)");

            // a second picture on the canvas: the panel works on the selected one, layers mix with annotations
            var opened = edItems.OfType<ImageAnnotation>().First();
            typeof(PinWindow).GetMethod("SelectQuietly", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(ed, new object[] { picture });
            ed.RotateBy(15);
            Pump(30);
            Check(Math.Abs(picture.Rotation - 15) < 0.01 && Math.Abs(opened.Rotation) < 0.01 && ed.ImageWidth == 300, "an angle turns the selected picture only; the canvas stays");
            mover.Invoke(ed, new object[] { picture, 0 });
            Check(edItems[0] == picture, "a picture goes to the bottom like any element");
            ed.CanvasTo(400, 260);
            Pump(30);
            Check(ed.ImageWidth == 400 && ed.ImageHeight == 260 && opened.Rect.X == 50 && opened.Rect.Y == 30 && opened.Rect.Width == 300,
                $"画布尺寸: a bigger background, pictures moved to stay centred, not scaled ({opened.Rect})");
            var flat = ed.Composite;
            Check(flat.PixelWidth == 400 && PixelA(flat, 5, 5) == 0, "the added background is transparent");

            // 吸色 reads the pictures on the canvas
            var source = (BitmapSource)typeof(ImageEditorWindow).GetProperty("EyedropSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ed)!;
            Check(PenEyedropper.Sample(source, new Point(60, 40)) == Colors.White && PenEyedropper.Sample(source, new Point(5, 5)) == null,
                "吸色: the colour of the picture under the cross, nothing on the empty background");
            var bar = (AnnotationToolbar)Get(ed, "Toolbar");
            bar.PickColor(Color.FromRgb(10, 120, 200));
            Check(bar.Color == Color.FromRgb(10, 120, 200), "a picked colour becomes the pen's colour");
            var editorLayer = (AnnotationEditor)Get(ed, "Editor");
            editorLayer.Eyedrop = (new Point(60, 40), Colors.White);
            editorLayer.InvalidateVisual();
            Pump(30);
            editorLayer.Eyedrop = null;

            // the bar: 粘贴图片, 打开图片, 清空批注
            var actions = ((Panel)bar.FindName("EditorActions")).Children.OfType<FrameworkElement>().Select(x => (string)x.Tag).ToList();
            Check(actions.Take(4).SequenceEqual(new[] { "New", "Paste", "Open", "Save" }) && actions.Contains("Clear"), $"editor bar: new, paste, open, save, clear … ({string.Join(",", actions)})");
            // (the clipboard itself is left alone here: it is the user's)

            // tablet pressure: a stroke is thin where the pen pressed lightly, thick where it pressed hard
            var stroke = new PenAnnotation { Thickness = 10, Color = Colors.Black };
            stroke.Points.AddRange(new[] { new Point(10, 20), new Point(60, 20), new Point(110, 20) });
            stroke.Pressures = new List<float> { 0.05f, 0.5f, 1f };
            var pressureShot = Render(stroke, 130, 40);
            int Thick(int x) => Enumerable.Range(0, 40).Count(y => PixelA(pressureShot, x, y) > 128);
            Check(Thick(15) < Thick(60) && Thick(60) < Thick(105), $"笔压: the width follows the pressure ({Thick(15)} < {Thick(60)} < {Thick(105)})");
            var pressed = new PenAnnotation();
            pressed.Points.Add(new Point(0, 0));
            var gesture = new CreationGesture(pressed, new Point(0, 0)) { Pressure = 0.3f };
            gesture.Move(new Point(20, 0), false, false);
            Check(((PenAnnotation)gesture.Target).Pressures is { Count: 2 } p && Math.Abs(p[1] - 0.3f) < 0.001, "a pen stroke keeps the pressure of each point");
            var edBar = (AnnotationToolbar)Get(ed, "Toolbar");
            var cfg = Markshot.App.Config;
            var (oldOrder, oldHidden, oldTools) = (cfg.EditorActionOrder, cfg.EditorActionHidden, cfg.EditorToolHidden);
            cfg.EditorActionOrder = "Pin,Paste,Open,Save,Clear,Copy";
            cfg.EditorActionHidden = "Copy,Clear";
            cfg.EditorToolHidden = "Number";
            edBar.ApplyToolOrderFromConfig();
            var edActions = (Panel)edBar.FindName("EditorActions");
            var shown = edActions.Children.OfType<FrameworkElement>().Where(x => x.Visibility == Visibility.Visible).Select(x => (string)x.Tag);
            Check(string.Join(",", shown) == "New,Pin,Paste,Open,Save", $"the editor bar has its own order / hidden buttons ({string.Join(",", shown)})");
            Check(!((FrameworkElement)edBar.FindName("ToolNumber")).IsVisible, "and its own hidden tools");
            (cfg.EditorActionOrder, cfg.EditorActionHidden, cfg.EditorToolHidden) = (oldOrder, oldHidden, oldTools);
            edBar.ApplyToolOrderFromConfig();
            ed.CloseWithoutAsking();
        }

        // ---- the wheel sizes the pen one step per notch; the brush ring follows down to thin sizes
        {
            var we = new ImageEditorWindow(Blank(200, 120)) { Left = 40, Top = 40, ShowActivated = false };
            we.Show();
            Pump(200);
            typeof(PinWindow).GetMethod("SetTool", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(we, new object[] { AnnotationTool.Pen });
            typeof(PinWindow).GetField("_thickness", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(we, 3.0);
            var sizes = new List<double>();
            var wheel = typeof(PinWindow).GetMethod("AdjustActiveToolByWheel", BindingFlags.NonPublic | BindingFlags.Instance)!;
            for (var i = 0; i < 3; i++)
            {
                wheel.Invoke(we, new object[] { 120 });
                sizes.Add((double)typeof(PinWindow).GetField("_thickness", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(we)!);
            }

            var shown = ((TextBlock)((AnnotationToolbar)Get(we, "Toolbar")).FindName("WidthValue")).Text;
            Check(sizes.SequenceEqual(new[] { 4.0, 5.0, 6.0 }) && shown == "6", $"每格滚轮 = 粗细 ±1 ({string.Join(",", sizes)}, shows {shown})");
            Check(!ReferenceEquals(ToolCursors.Get(AnnotationTool.Pen, 2, Colors.Red, 1), ToolCursors.Get(AnnotationTool.Pen, 4, Colors.Red, 1)) &&
                  !ReferenceEquals(ToolCursors.Get(AnnotationTool.Pen, 4, Colors.Red, 1), ToolCursors.Get(AnnotationTool.Pen, 5, Colors.Red, 1)),
                "the brush ring keeps getting smaller below 6");
            we.CloseWithoutAsking();
        }

        // ---- editor: dropped pictures join the canvas, 画布适应内容, 吸附, 对齐
        {
            var dropDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "markshot-drop-test");
            System.IO.Directory.CreateDirectory(dropDir);
            var fileA = System.IO.Path.Combine(dropDir, "a.png");
            var fileB = System.IO.Path.Combine(dropDir, "b.png");
            ImageFile.Save(Blank(80, 60), fileA);
            ImageFile.Save(Blank(40, 40), fileB);
            var de = new ImageEditorWindow(Blank(300, 200)) { Left = 40, Top = 40, ShowActivated = false };
            de.Show();
            Pump(300);
            var deItems = ((AnnotationEditor)Get(de, "Editor")).Items;
            de.AddPictures(new[] { fileA, fileB }, new Point(150, 100));
            Pump(50);
            var pictures = deItems.OfType<ImageAnnotation>().ToList();
            Check(pictures.Count == 3 && de.ImageWidth == 300 && de.SelectedElements.Count == 2, "拖入图片: two more picture elements, the canvas stays, both selected");
            Check(pictures[1].Rect.Width == 80 && Math.Abs(pictures[1].Rect.X + 40 - 150) < 0.5, "… where they were dropped");
            de.UndoStep();
            Pump(30);
            Check(deItems.OfType<ImageAnnotation>().Count() == 1, "one undo step takes them back");

            // 吸附: a box near the canvas middle lines up with it, Alt-free otherwise
            var (shift, guides) = Snapping.Snap(new Rect(147, 20, 10, 10), new Rect(0, 0, 300, 200), Array.Empty<Rect>(), 6);
            Check(Math.Abs(shift.X - (-2)) < 0.01 && guides.Any(g => g.Vertical && g.At == 150), $"吸附: the middle lines up with the canvas middle (shift {shift.X})");
            var (far, farGuides) = Snapping.Snap(new Rect(120, 70, 10, 10), new Rect(0, 0, 300, 200), Array.Empty<Rect>(), 6);
            Check(far.Length < 0.01 && farGuides.Count == 0, "far from anything: no pull, no line");
            var (toOther, _) = Snapping.Snap(new Rect(64, 120, 30, 20), new Rect(0, 0, 300, 200), new[] { new Rect(10, 10, 50, 40) }, 6);
            Check(Math.Abs(toOther.X - (-4)) < 0.01, "… and lines up with another element's edge");
            var dragged = new RectAnnotation { Rect = new Rect(60, 60, 40, 30), Color = Colors.Red, Thickness = 2 };
            deItems.Add(dragged);
            var snapPlace = (Point)typeof(ImageEditorWindow).GetMethod("SnapMove", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(de, new object[] { dragged, new Rect(128, 60, 40, 30) })!;
            Check(Math.Abs(snapPlace.X - 130) < 0.01 && ((AnnotationEditor)Get(de, "Editor")).Guides is { Count: > 0 }, $"moving in the editor snaps to the canvas middle and shows the line ({snapPlace.X})");

            // 对齐 / 分布
            var a1 = new RectAnnotation { Rect = new Rect(10, 10, 20, 20), Color = Colors.Red, Thickness = 2 };
            var a2 = new RectAnnotation { Rect = new Rect(60, 40, 30, 20), Color = Colors.Red, Thickness = 2 };
            var a3 = new RectAnnotation { Rect = new Rect(200, 25, 10, 20), Color = Colors.Red, Thickness = 2 };
            deItems.AddRange(new Annotation[] { a1, a2, a3 });
            de.SelectElements(new Annotation[] { a1, a2, a3 });
            de.AlignGroup("Left");
            Check(a1.Rect.X == 10 && a2.Rect.X == 10 && a3.Rect.X == 10, "左对齐");
            de.UndoStep();
            Pump(30);
            a1 = deItems.OfType<RectAnnotation>().First(r => r.Rect.Width == 20);
            a2 = deItems.OfType<RectAnnotation>().First(r => r.Rect.Width == 30);
            a3 = deItems.OfType<RectAnnotation>().First(r => r.Rect.Width == 10);
            Check(a2.Rect.X == 60, "… undoable");
            de.SelectElements(new Annotation[] { a1, a2, a3 });
            de.AlignGroup("SpreadX");
            var gap1 = a2.Rect.Left - a1.Rect.Right;
            var gap2 = a3.Rect.Left - a2.Rect.Right;
            Check(Math.Abs(gap1 - gap2) < 0.01 && a1.Rect.X == 10 && a3.Rect.X == 200, $"横向等距分布: equal gaps, the outer two stay ({gap1:0.#} / {gap2:0.#})");
            de.AlignGroup("Middle");
            Check(Math.Abs(a1.Rect.Y + 10 - (a2.Rect.Y + 10)) < 0.01, "垂直居中");

            // 画布适应内容: everything, also what was dragged past the edge
            a3.Translate(150, 0); // now 350…360: past the 300 wide canvas
            de.FitCanvasToContent();
            Pump(30);
            Check(de.ImageWidth >= 350 && deItems.All(i => AnnotationEditor.RotatedBounds(i).Left >= -2), $"画布适应内容: the canvas holds everything ({de.ImageWidth}×{de.ImageHeight})");
            de.UndoStep();
            Pump(30);
            Check(de.ImageWidth == 300, "… undoable");
            de.CloseWithoutAsking();
            System.IO.Directory.Delete(dropDir, true);
        }

        // ---- editor: 元素样式 (圆角 / 阴影 / 描边), 画布背景, 适应内容 with a margin
        {
            var se2 = new ImageEditorWindow(Blank(200, 120)) { Left = 40, Top = 40, ShowActivated = false };
            se2.Show();
            Pump(300);
            var items2 = ((AnnotationEditor)Get(se2, "Editor")).Items;
            var photo = items2.OfType<ImageAnnotation>().First();
            var styleSection = (FrameworkElement)typeof(ImageEditorWindow).GetField("_styleSection", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(se2)!;
            Check(styleSection.Visibility == Visibility.Visible && !((UIElement)styleSection.Tag).IsEnabled, "元素样式 always shown, resting while no picture is selected");
            var heightBefore = styleSection.ActualHeight;
            se2.SelectElements(new Annotation[] { photo });
            Pump(100);
            Check(((UIElement)styleSection.Tag).IsEnabled && Math.Abs(styleSection.ActualHeight - heightBefore) < 40, "… usable for a selected picture, without the panel jumping");
            se2.SelectElements(Array.Empty<Annotation>());
            Pump(100);
            Check(styleSection.Visibility == Visibility.Visible && !((UIElement)styleSection.Tag).IsEnabled, "… and resting again when nothing is selected");
            se2.SelectElements(new Annotation[] { photo });
            Pump(400);
            var footer = (FrameworkElement)typeof(ImageEditorWindow).GetField("_resetAllFooter", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(se2)!;
            var insideScroll = false;
            for (DependencyObject? up = footer; up != null; up = VisualTreeHelper.GetParent(up))
            {
                insideScroll |= up is ScrollViewer;
            }

            Check(footer.IsVisible && !insideScroll, "重置所有修改 is fixed at the bottom of the panel (not in the scrolled part)");
            var invert = (System.Windows.Controls.Primitives.ToggleButton)typeof(ImageEditorWindow).GetField("_invert", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(se2)!;
            Check(invert.Style == (Style)Application.Current.FindResource("FooterToggle") && invert.ActualHeight == 30, "反色 is a button like 曲线 / 一键增强");
            se2.SetPictureStyle("Radius", 30);
            se2.SetPictureStyle("Border", 6);
            se2.SetBorderColor(Colors.Red);
            se2.SetPictureStyle("Shadow", 16);
            se2.SetBackground(Colors.White);
            se2.CanvasTo(260, 180);
            se2.SelectElements(new Annotation[] { items2.OfType<ImageAnnotation>().First() });
            Pump(500);
            var panelShot = new RenderTargetBitmap((int)((FrameworkElement)se2.Content).ActualWidth, (int)((FrameworkElement)se2.Content).ActualHeight, 96, 96, PixelFormats.Pbgra32);
            panelShot.Render((FrameworkElement)se2.Content);
            var panelEnc = new PngBitmapEncoder();
            panelEnc.Frames.Add(BitmapFrame.Create(panelShot));
            using (var panelFile = System.IO.File.Create("editor_style_panel.png")) panelEnc.Save(panelFile);
            {
                ScrollViewer? panelScroll = null;
                void FindScroll(DependencyObject node)
                {
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node) && panelScroll == null; i++)
                    {
                        var child = VisualTreeHelper.GetChild(node, i);
                        if (child is ScrollViewer sv && sv.Content is StackPanel) panelScroll = sv; else FindScroll(child);
                    }
                }

                FindScroll((DependencyObject)se2.Content);
                panelScroll?.ScrollToEnd();
                Pump(200);
                var bottomShot = new RenderTargetBitmap((int)((FrameworkElement)se2.Content).ActualWidth, (int)((FrameworkElement)se2.Content).ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bottomShot.Render((FrameworkElement)se2.Content);
                var bottomEnc = new PngBitmapEncoder();
                bottomEnc.Frames.Add(BitmapFrame.Create(bottomShot));
                using (var bottomFile = System.IO.File.Create("editor_panel_bottom.png")) bottomEnc.Save(bottomFile);
                panelScroll?.ScrollToHome();
            }
            se2.UndoStep(); se2.UndoStep(); se2.UndoStep();
            Pump(30);
            photo = items2.OfType<ImageAnnotation>().First();
            se2.SelectElements(new Annotation[] { photo });
            var styled = se2.Composite;
            Check(PixelA(styled, 1, 1) == 0 && PixelA(styled, 100, 60) == 255, "圆角: the corner is cut, the middle stays");
            var (sp, sw, _) = ImageOps.ReadPixels(styled);
            var edgePixel = (60 * sw + 2) * 4;
            Check(sp[edgePixel + 2] > 200 && sp[edgePixel + 1] < 60, "描边: a red outline along the edge");
            se2.UndoStep(); se2.UndoStep(); se2.UndoStep();
            Pump(30);
            Check(photo.Radius == 0 || items2.OfType<ImageAnnotation>().First().Radius == 0, "… each change undoable");

            // a free shape: no 圆角 row, the outline and shadow follow it (no rectangle around it)
            var discPixels = new byte[100 * 100 * 4];
            for (var dy = 0; dy < 100; dy++)
            for (var dx = 0; dx < 100; dx++)
            {
                var o = (dy * 100 + dx) * 4;
                discPixels[o] = 200;
                discPixels[o + 3] = (byte)(Math.Sqrt((dx - 49.5) * (dx - 49.5) + (dy - 49.5) * (dy - 49.5)) <= 45 ? 255 : 0);
            }

            var cutShape = new ImageAnnotation { Image = ImageOps.FromPixels(discPixels, 100, 100), Rect = new Rect(20, 10, 100, 100) };
            var radiusRow = (FrameworkElement)typeof(ImageEditorWindow).GetField("_radiusRow", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(se2)!;
            Check(radiusRow.Visibility == Visibility.Visible, "a rectangular picture has 圆角");
            items2.Add(cutShape);
            se2.SelectElements(new Annotation[] { cutShape });
            Check(cutShape.IsShaped && radiusRow.Visibility != Visibility.Visible && styleSection.Visibility == Visibility.Visible, "a free shape: no 圆角 row, 阴影 and 描边 stay");
            cutShape.Radius = 30; // set earlier, before it was cut
            cutShape.BorderWidth = 6;
            cutShape.BorderColor = Colors.Lime;
            var shapeShot = Render(cutShape, 160, 130);
            var (shapePixels, shapeWidth, _) = ImageOps.ReadPixels(shapeShot);
            byte ShapeG(int x, int y) => shapePixels[(y * shapeWidth + x) * 4 + 1];
            byte ShapeA(int x, int y) => shapePixels[(y * shapeWidth + x) * 4 + 3];
            Check(ShapeA(70, 60) == 255 && ShapeG(70, 60) < 40, "… the picture itself is uncut by the old corners");
            Check(ShapeA(20 + 49 + 48, 60) > 200 && ShapeG(20 + 49 + 48, 60) > 200, "描边 runs around the shape");
            Check(ShapeA(22, 12) == 0 && ShapeA(118, 108) == 0, "… nothing in the square's corners (it used to be a rectangle)");
            cutShape.BorderWidth = 0;
            cutShape.ShadowSize = 16;
            var shadowShot = Render(cutShape, 160, 140);
            Check(PixelA(shadowShot, 22, 14) < PixelA(shadowShot, 70, 112) && PixelA(shadowShot, 70, 112) > 0, $"阴影 follows the shape ({PixelA(shadowShot, 22, 14)} in the corner, {PixelA(shadowShot, 70, 112)} below it)");
            Check(cutShape.StyleReach > 16, "… and counts for 适应内容");
            cutShape.BorderWidth = 40;
            cutShape.ShadowSize = 0;
            Check(cutShape.StyleReach == 40, "… as does the outline outside a shape");
            cutShape.BorderWidth = 6;
            cutShape.ShadowSize = 14;
            var sampleCanvas = new DrawingVisual();
            using (var sampleDc = sampleCanvas.RenderOpen())
            {
                sampleDc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 170, 150));
                cutShape.Draw(sampleDc);
            }

            var sampleBitmap = new RenderTargetBitmap(170, 150, 96, 96, PixelFormats.Pbgra32);
            sampleBitmap.Render(sampleCanvas);
            var sampleEncoder = new PngBitmapEncoder();
            sampleEncoder.Frames.Add(BitmapFrame.Create(sampleBitmap));
            using (var sampleFile = System.IO.File.Create("element_style_shape.png")) sampleEncoder.Save(sampleFile);
            items2.Remove(cutShape);
            se2.SelectElements(new Annotation[] { items2.OfType<ImageAnnotation>().First() });
            Check(radiusRow.Visibility == Visibility.Visible, "back on a rectangle: 圆角 again");

            // the shadow reaches past a smaller picture
            var small = new ImageAnnotation { Image = Blank(60, 40), Rect = new Rect(70, 40, 60, 40) };
            items2.Add(small);
            se2.SelectElements(new Annotation[] { small });
            se2.SetPictureStyle("Shadow", 20);
            Check(small.ShadowSize == 20 && small.StyleReach > 20, "阴影 set on the selected picture");

            // 画布背景: the canvas' colour, kept through size changes and undoable
            se2.SetBackground(Colors.Black);
            Check(se2.CanvasColor == Colors.Black, "背景: black");
            se2.CanvasTo(260, 160);
            Pump(30);
            Check(se2.CanvasColor == Colors.Black && PixelA(se2.Composite, 2, 2) == 255, "… stays black when the canvas grows");
            se2.UndoStep(); se2.UndoStep();
            Pump(30);
            Check(se2.CanvasColor.A == 0, "… and undoable back to transparent");

            // 适应内容 with a margin (after the undos the elements are new copies)
            small = items2.OfType<ImageAnnotation>().Last();
            small.Translate(200, 0); // out of the canvas
            se2.FitCanvasToContent(20);
            Pump(30);
            var reach = small.StyleReach;
            Check(se2.ImageWidth >= 330 + reach + 20 - 2 && items2.All(i => AnnotationEditor.RotatedBounds(i).Left >= 20 - reach - 2), $"适应内容 with a 20 px margin ({se2.ImageWidth}×{se2.ImageHeight})");
            se2.CloseWithoutAsking();
        }

        // ---- the pin: IME, 应用 and the bar again, double-click copies and closes, zoom label, 软边, text card
        {
            BitmapSource copied = null;
            ClipboardService.CopyOverride = image => { copied = image; return true; };
            var quickBefore = Markshot.App.Config.QuickDoubleClickCopy;
            PinWindow NewPin()
            {
                var made = new PinWindow(Blank(120, 80), -4000, -4000, Blank(120, 80), null) { Left = -4000, Top = -4000, ShowActivated = false };
                made.Show();
                Pump(150);
                return made;
            }

            MouseButtonEventArgs ClickArgs(int clicks, RoutedEvent routed)
            {
                var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routed };
                typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(press, clicks, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public, null, null, null);
                return press;
            }

            var keysPin = NewPin();
            var layer = (FrameworkElement)Get(keysPin, "InputLayer");
            Check(!InputMethod.GetIsInputMethodEnabled(layer) && !InputMethod.GetIsInputMethodEnabled(keysPin), "输入法: the canvas layer that holds the keys while annotating takes no IME (letters and Space stay shortcuts)");
            Check(InputMethod.GetIsInputMethodEnabled((UIElement)Get(keysPin, "TextEditor")), "… typing a text still does");

            // 应用 on the bar, the keys come back; a Space whose release was missed does not block the next one
            var keysBar = (AnnotationToolbar)Get(keysPin, "Toolbar");
            Call(keysPin, "ToggleEditing");
            Pump(150);
            var applyButton = ((Panel)keysBar.FindName("PinActions")).Children.OfType<Button>().First(b => (string)b.Tag == "Apply");
            Check(applyButton.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue, "应用 has no colour of its own");
            ((Action)typeof(AnnotationToolbar).GetField("ApplyRequested", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(keysBar)!)();
            Pump(300);
            Check(!(bool)Get(keysPin, "_editMode") && !keysBar.IsVisible, "应用: the bar goes");
            if (keysPin.IsActive)
            {
                Check(keysPin.IsKeyboardFocusWithin, "… and the pin has the keys");
            }

            // another window took the activation while the bar was open: 应用 brings the keys back to the pin
            Call(keysPin, "ToggleEditing");
            Pump(150);
            keysPin.Activate();
            var elsewhere = new Window { Width = 80, Height = 40, Left = -3000, Top = -3000, ShowInTaskbar = false };
            elsewhere.Show();
            elsewhere.Activate();
            Pump(200);
            var activationMoved = !keysPin.IsActive;
            ((Action)typeof(AnnotationToolbar).GetField("ApplyRequested", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(keysBar)!)();
            Pump(300);
            Console.WriteLine($"   activation had moved: {activationMoved}, pin active after 应用: {keysPin.IsActive}, focus within: {keysPin.IsKeyboardFocusWithin}");
            Check(keysPin.IsActive && keysPin.IsKeyboardFocusWithin, "应用 with another window active: the pin is active again and has the keys");
            elsewhere.Close();
            Pump(100);

            typeof(PinWindow).GetField("_spaceExitGuard", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(keysPin, true);
            keysPin.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(keysPin)!, 0, Key.Space) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            Pump(300);
            Check((bool)Get(keysPin, "_editMode") && keysBar.IsVisible, "Space brings the bar back, also when the release of the Space before was missed");
            keysPin.CloseImmediately();

            // zoom label
            var zoomPin = NewPin();
            typeof(PinWindow).GetMethod("ZoomBy", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(zoomPin, new object[] { 1.5 });
            Pump(60);
            var zoomLabel = (FrameworkElement)Get(zoomPin, "PinZoomLabel");
            Check(zoomLabel.Visibility == Visibility.Visible && ((TextBlock)Get(zoomPin, "PinZoomText")).Text == "150%" && zoomLabel.VerticalAlignment == VerticalAlignment.Bottom && zoomLabel.HorizontalAlignment == HorizontalAlignment.Left,
                "zooming: the scale in the lower left corner");
            var zoomWait = System.Diagnostics.Stopwatch.StartNew(); // 0.8 s + the fade; a slow machine may take longer
            while (zoomLabel.Visibility == Visibility.Visible && zoomWait.ElapsedMilliseconds < 3000) Pump(50);
            Check(zoomLabel.Visibility != Visibility.Visible && zoomWait.ElapsedMilliseconds > 700, $"… gone when the zooming stops ({zoomWait.ElapsedMilliseconds} ms)");
            zoomPin.CloseImmediately();

            // double-click on the empty part: view mode, and annotating without a tool
            var viewPin = NewPin();
            PinDown(viewPin, ClickArgs(2, Mouse.MouseDownEvent));
            Pump(400);
            Check(copied != null && !viewPin.IsVisible, "double-click with the bar closed: copied and closed");
            viewPin.CloseImmediately();
            copied = null;
            var selectPin = NewPin();
            Call(selectPin, "ToggleEditing");
            Pump(150);
            PinDown(selectPin, ClickArgs(2, Mouse.MouseDownEvent));
            Pump(400);
            Check(copied != null && !selectPin.IsVisible, "double-click annotating without a tool: copied and closed");
            selectPin.CloseImmediately();
            copied = null;

            // with a tool in hand: only with 快速双击, and what the first click drew does not stay
            var toolPin = NewPin();
            Call(toolPin, "ToggleEditing");
            Pump(150);
            typeof(PinWindow).GetMethod("SetTool", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(toolPin, new object[] { AnnotationTool.Number });
            var toolItems = ((AnnotationEditor)Get(toolPin, "Editor")).Items;
            typeof(PinWindow).GetField("PointerOver", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, new Func<UIElement, bool>(_ => true));
            Markshot.App.Config.QuickDoubleClickCopy = false;
            Call(toolPin, "OnQuickDoubleClick", toolPin, ClickArgs(1, Mouse.PreviewMouseDownEvent));
            Call(toolPin, "OnQuickDoubleClick", toolPin, ClickArgs(2, Mouse.PreviewMouseDownEvent));
            Check(copied == null && toolPin.IsVisible, "a tool in hand, 快速双击 off: the double-click stays with the tool");
            Markshot.App.Config.QuickDoubleClickCopy = true;
            Call(toolPin, "OnQuickDoubleClick", toolPin, ClickArgs(1, Mouse.PreviewMouseDownEvent));
            var toolHistory = (AnnotationHistory)Get(toolPin, "_history");
            toolHistory.Checkpoint(toolItems);
            toolItems.Add(new RectAnnotation { Rect = new Rect(5, 5, 60, 40), Color = Colors.Red, Fill = true }); // what the first click drew
            var press2 = ClickArgs(2, Mouse.PreviewMouseDownEvent);
            Call(toolPin, "OnQuickDoubleClick", toolPin, press2);
            Pump(400);
            var (quickPixels, quickWidth, _) = ImageOps.ReadPixels(copied ?? Blank(120, 80));
            var quickPixel = (20 * quickWidth + 20) * 4;
            Check(copied != null && press2.Handled && !toolPin.IsVisible && quickPixels[quickPixel + 2] == 255 && quickPixels[quickPixel + 1] == 255,
                "快速双击 on: copied and closed at once, without what the first click drew");
            toolPin.CloseImmediately();
            typeof(PinWindow).GetField("PointerOver", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, new Func<UIElement, bool>(element => element.IsMouseOver));
            Markshot.App.Config.QuickDoubleClickCopy = quickBefore;
            ClipboardService.CopyOverride = null;

            // 软边: the edge fades, the middle stays solid; a selected stroke follows the slider
            var softStroke = new PenAnnotation { Thickness = 24, Color = Colors.Black, Softness = 0.8 };
            softStroke.Points.AddRange(new[] { new Point(10, 30), new Point(110, 30) });
            var hardStroke = (PenAnnotation)softStroke.Clone();
            hardStroke.Softness = 0;
            var softShot = Render(softStroke, 120, 60);
            var hardShot = Render(hardStroke, 120, 60);
            Check(PixelA(softShot, 60, 30) == 255 && PixelA(hardShot, 60, 20) == 255 && PixelA(softShot, 60, 20) is > 0 and < 200,
                $"软边: solid in the middle, fading to the edge (hard {PixelA(hardShot, 60, 20)}, soft {PixelA(softShot, 60, 20)})");
            Check(PixelA(softShot, 60, 24) > PixelA(softShot, 60, 20) && PixelA(softShot, 60, 20) > PixelA(softShot, 60, 18), "… a smooth ramp");
            var softPin = NewPin();
            var softBar = (AnnotationToolbar)Get(softPin, "Toolbar");
            Call(softPin, "ToggleEditing");
            Pump(150);
            typeof(PinWindow).GetMethod("SetTool", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(softPin, new object[] { AnnotationTool.Pen });
            Pump(150);
            Check(((FrameworkElement)softBar.FindName("SoftnessGroup")).Visibility == Visibility.Visible, "the pen's settings row has 软边");
            var softBefore = Markshot.App.Config.PenSoftness;
            softBar.NudgeSoftness(120);
            softBar.NudgeSoftness(120);
            Check(Math.Abs(softBar.Softness - 0.1) < 0.001 + softBefore / 100.0 && Markshot.App.Config.PenSoftness == softBefore + 10, "Shift+滚轮: 5 % per notch, remembered for the next stroke");
            var softItems = ((AnnotationEditor)Get(softPin, "Editor")).Items;
            var chosen = new PenAnnotation { Thickness = 10, Color = Colors.Red };
            chosen.Points.AddRange(new[] { new Point(5, 5), new Point(60, 40) });
            softItems.Add(chosen);
            Call(softPin, "SelectAnnotation", chosen);
            Pump(50);
            softBar.NudgeSoftness(120);
            Pump(50);
            Check(Math.Abs(chosen.Softness - 0.05) < 0.001, $"… and a selected stroke follows ({chosen.Softness})");
            softBar.UpdateLayout();
            var barShot = new RenderTargetBitmap((int)softBar.ActualWidth, (int)softBar.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            barShot.Render(softBar);
            var barEnc = new PngBitmapEncoder();
            barEnc.Frames.Add(BitmapFrame.Create(barShot));
            using (var barFile = System.IO.File.Create("toolbar_pen_softness.png")) barEnc.Save(barFile);
            softPin.CloseImmediately();
            Markshot.App.Config.PenSoftness = softBefore;

            // the text card in the theme's colours
            Theme.Apply(ThemeMode.Light);
            var lightCard = Markshot.Services.TextCardRenderer.Render("标题", "正文");
            Theme.Apply(ThemeMode.Dark);
            var darkCard = Markshot.Services.TextCardRenderer.Render("标题", "正文");
            byte CardRed(BitmapSource card)
            {
                var (px, w, h) = ImageOps.ReadPixels(card);
                return px[((h - 8) * w + w / 2) * 4 + 2];
            }

            var softSample = new PenAnnotation { Thickness = 28, Color = Colors.DeepSkyBlue, Softness = 0.9 };
            softSample.Points.AddRange(new[] { new Point(20, 30), new Point(120, 50), new Point(220, 26) });
            var hardSample = (PenAnnotation)softSample.Clone();
            hardSample.Softness = 0;
            hardSample.Translate(0, 60);
            var sampleVisual = new DrawingVisual();
            using (var dc = sampleVisual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 240, 140));
                softSample.Draw(dc);
                hardSample.Draw(dc);
            }

            var sampleShot = new RenderTargetBitmap(240, 140, 96, 96, PixelFormats.Pbgra32);
            sampleShot.Render(sampleVisual);
            foreach (var (file, bitmap) in new (string, BitmapSource)[] { ("pen_softness.png", sampleShot), ("text_card_dark.png", darkCard), ("text_card_light.png", lightCard) })
            {
                var cardEnc = new PngBitmapEncoder();
                cardEnc.Frames.Add(BitmapFrame.Create(bitmap));
                using var cardFile = System.IO.File.Create(file);
                cardEnc.Save(cardFile);
            }

            Check(CardRed(lightCard) > 200 && CardRed(darkCard) < 60, $"文字贴图 follows the theme (light {CardRed(lightCard)}, dark {CardRed(darkCard)})");
        }

        // ---- editor: 选择 by default, 框选 / Shift 加选 / Ctrl 减选, 裁切与变形 on the selection, a paste is placed
        {
            var se = new ImageEditorWindow(Blank(300, 200)) { Left = 40, Top = 40, ShowActivated = false };
            se.Show();
            Pump(300);
            var seBar = (AnnotationToolbar)Get(se, "Toolbar");
            var selectButton = (RadioButton)seBar.FindName("ToolSelect");
            Check(selectButton.Visibility == Visibility.Visible && selectButton.IsChecked == true, "the editor opens in 选择");
            var its = ((AnnotationEditor)Get(se, "Editor")).Items;
            var opened = its.OfType<ImageAnnotation>().First();
            var ra = new RectAnnotation { Rect = new Rect(20, 20, 40, 30), Color = Colors.Red, Thickness = 3 };
            var eb = new EllipseAnnotation { Rect = new Rect(100, 60, 40, 40), Color = Colors.Blue, Thickness = 3 };
            var rc = new RectAnnotation { Rect = new Rect(200, 120, 30, 30), Color = Colors.Green, Thickness = 3 };
            its.AddRange(new Annotation[] { ra, eb, rc });
            se.MarqueeSelect(new Rect(10, 10, 140, 100), 0);
            Check(se.SelectedElements.Contains(ra) && se.SelectedElements.Contains(eb) && !se.SelectedElements.Contains(rc), "框选: what the rectangle touches");
            se.SelectElements(new Annotation[] { ra, eb });
            se.MarqueeSelect(new Rect(195, 115, 10, 10), 1);
            Check(se.SelectedElements.Contains(rc) && se.SelectedElements.Contains(ra), "Shift 框选 adds");
            se.MarqueeSelect(new Rect(15, 15, 10, 10), 2);
            Check(!se.SelectedElements.Contains(ra) && !se.SelectedElements.Contains(opened) && se.SelectedElements.Count == 2, "Ctrl 框选 takes away");

            // 调节框 on the 多选: each moves with the box and stays what it is
            CallEd(se, "StartSession", EditorOverlayMode.Transform);
            var groupBox = (EditorOverlay)typeof(ImageEditorWindow).GetField("_overlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(se)!;
            var boxStart = groupBox.TransformStart;
            groupBox.TransformRect = new Rect(boxStart.X + 10, boxStart.Y + 5, boxStart.Width, boxStart.Height);
            Check(eb.Hidden && rc.Hidden, "the box shows them meanwhile");
            CallEd(se, "ApplySession");
            Pump(30);
            Check(eb.Rect.X == 110 && rc.Rect.Y == 125 && !eb.Hidden && its.Contains(eb) && se.SelectedElements.Count == 2, "调节框 on a 多选 moves each element (they stay editable and selected)");

            // 路径裁切 on the 多选: they become one picture, cut along the path
            CallEd(se, "StartSession", EditorOverlayMode.Path);
            var pathBox = (EditorOverlay)typeof(ImageEditorWindow).GetField("_overlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(se)!;
            pathBox.PathPoints.AddRange(new[] { new Point(90, 40), new Point(260, 40), new Point(260, 180), new Point(90, 180) });
            CallEd(se, "ApplySession");
            Pump(30);
            Check(!its.Contains(eb) && !its.Contains(rc) && its.OfType<ImageAnnotation>().Count() == 2 && its.Contains(ra), "路径裁切 on a 多选: merged into one picture and cut; the rest stays");

            // 网格形变 on a 多选: merged and warped, and still visible
            var picturesBefore = its.OfType<ImageAnnotation>().ToList();
            se.SelectElements(new Annotation[] { ra, opened });
            CallEd(se, "StartSession", EditorOverlayMode.Mesh);
            var warpBox = (EditorOverlay)typeof(ImageEditorWindow).GetField("_overlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(se)!;
            var corner = warpBox.Warp.Anchors[0, 0];
            warpBox.DragWarp((0, 0, 0, 0), new Point(corner.X + 20, corner.Y + 10));
            CallEd(se, "ApplySession");
            Pump(30);
            var warped = its.OfType<ImageAnnotation>().First(p => !picturesBefore.Contains(p));
            Check(!its.Contains(ra) && !its.Contains(opened) && its.All(i => !i.Hidden) && PixelA(se.Composite, 150, 100) == 255,
                "网格形变 on a 多选: one warped picture, visible (it used to stay hidden)");
            opened = warped;

            // 矩形裁切 on a selected picture: the picture is cut, the canvas stays
            se.SelectElements(new Annotation[] { opened });
            CallEd(se, "StartSession", EditorOverlayMode.Crop);
            var cropBox = (EditorOverlay)typeof(ImageEditorWindow).GetField("_overlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(se)!;
            cropBox.Crop = new Rect(50, 40, 100, 80);
            CallEd(se, "ApplySession");
            Pump(30);
            Check(opened.Rect == new Rect(50, 40, 100, 80) && se.ImageWidth == 300 && se.ImageHeight == 200, $"矩形裁切 on the selected picture: it is cut, the canvas stays ({opened.Rect})");

            // a paste is placed: Esc takes it back (no undo step left), Enter or a click elsewhere keeps it
            var clipboard = typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationClipboard")!;
            clipboard.GetMethod("Copy")!.Invoke(null, new object[] { ra });
            var seHistory = (AnnotationHistory)Get(se, "_history");
            var depth = seHistory.Depth;
            var count = its.Count;
            Call(se, "PasteAnnotation");
            Check(its.Count == count + 1 && se.PastePending, "pasted: waiting to be placed");
            Call(se, "EditorKey", Key.Escape, false);
            Check(its.Count == count && seHistory.Depth == depth && !se.PastePending, "Esc cancels the paste, no undo step left");
            Call(se, "PasteAnnotation");
            Call(se, "EditorKey", Key.Enter, false);
            Check(its.Count == count + 1 && !se.PastePending && se.SelectedElements.Count == 0, "Enter places it");
            Call(se, "PasteAnnotation");
            Call(se, "ClearSelection");
            typeof(ImageEditorWindow).GetMethod("AfterInput", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(se, null);
            Check(its.Count == count + 2 && !se.PastePending, "a click on the empty space places it too");
            se.CloseWithoutAsking();
        }

        // ---- 抠除 on a pin: the hole is see-through (no shadow plate behind it)
        var holePin = new PinWindow(Blank(120, 80), -4000, -4000, Blank(120, 80), null) { Left = -4000, Top = -4000, ShowActivated = false };
        holePin.Show();
        Pump(200);
        var pinItems2 = ((AnnotationEditor)Get(holePin, "Editor")).Items;
        var pinHole = new CutoutAnnotation { Thickness = 20 };
        pinHole.Points.Add(new Point(30, 40));
        pinHole.Points.Add(new Point(90, 40));
        pinItems2.Add(pinHole);
        Call(holePin, "EnterEditMode");
        ((AnnotationEditor)Get(holePin, "Editor")).InvalidateVisual();
        Pump(150);
        var shadowLayer = (FrameworkElement)Get(holePin, "ImageShadowLayer");
        Check(shadowLayer.Visibility != Visibility.Visible && ((Image)Get(holePin, "Img")).Clip != null, "pin with a hole: no shadow plate behind it, the picture is cut");
        holePin.CloseImmediately();

        // ---- number badge: resizing follows the handle, no flip-flopping
        var badge = new NumberAnnotation { Center = new Point(50, 50), Radius = 14, Number = 1 };
        var start = badge.Bounds;
        var radii = new List<double>();
        foreach (var x in new[] { 70.0, 72, 74, 76, 78, 80 })
        {
            var target = AnnotationOps_ResizeFromHandle(start, 0, 4, new Point(x, x - 2));
            AnnotationOps_Resize(badge, target, 4);
            radii.Add(badge.Radius);
        }

        Check(radii.Zip(radii.Skip(1)).All(p => p.Second >= p.First), "number badge grows steadily: " + string.Join(",", radii.Select(r => r.ToString("0.#"))));

        // ---- toolbar: clear / close buttons in the right places, part of the order
        var catalog = typeof(PinWindow).Assembly.GetType("Markshot.UI.ToolbarCatalog")!;
        var captureKeys = (string[])catalog.GetField("CaptureActions")!.GetValue(null)!;
        var pinKeys = (string[])catalog.GetField("PinActions")!.GetValue(null)!;
        Check(Array.IndexOf(captureKeys, "Clear") == Array.IndexOf(captureKeys, "Close") + 1, "capture bar: 清空批注 right of 关闭");
        Check(string.Join(",", pinKeys) == "Close,Save,Clear,Edit,Apply,Copy", "pin bar: 关闭 before 保存, 清空批注 before 应用, 复制 right of 应用");
        var editorKeys = (string[])catalog.GetField("EditorActions")!.GetValue(null)!;
        Check(captureKeys[^1] == "Copy" && pinKeys[^1] == "Copy" && editorKeys[^1] == "Copy", "复制 is the rightmost button of every bar by default");
        var oldConfig = new AppConfig
        {
            CaptureActionOrder = "Scroll,Record,Ocr,Close,Clear,Pin,Save,Copy,Translate,Edit",
            PinActionOrder = "Close,Save,Clear,Apply,Edit",
            EditorActionOrder = "Paste,Open,Save,Clear,Copy,Pin"
        };
        oldConfig.EnsureDefaults();
        var arranged = new AppConfig { CaptureActionOrder = "Copy,Scroll,Record,Ocr,Close,Clear,Pin,Save,Translate,Edit" };
        arranged.EnsureDefaults();
        Check(oldConfig.CaptureActionOrder.EndsWith(",Copy") && oldConfig.PinActionOrder.EndsWith(",Copy") && oldConfig.EditorActionOrder.EndsWith(",Copy") &&
              arranged.CaptureActionOrder.StartsWith("Copy,"), "an untouched old default order moves 复制 to the right; a rearranged one stays");
        var order = (List<string>)catalog.GetMethod("Order")!.Invoke(null, new object[] { "Save,Apply,Edit", pinKeys })!;
        Check(order.Contains("Clear") && order.Contains("Close"), "an old saved order gets the new buttons");
        Check(order.IndexOf("Copy") == order.IndexOf("Apply") + 1, $"… 复制 right of 应用 ({string.Join(",", order)})");

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static bool AnnotationOps_HitNothing(List<Annotation> items, Point p) =>
        typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps")!.GetMethod("HitTest")!.Invoke(null, new object[] { items, p, 4.0, false }) is not CutoutAnnotation;

    static Rect AnnotationOps_ResizeFromHandle(Rect start, double rotation, int handle, Point p) =>
        (Rect)typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps")!.GetMethod("ResizeFromHandle")!.Invoke(null, new object[] { start, rotation, handle, p, false })!;

    static void AnnotationOps_Resize(Annotation a, Rect target, int handle) =>
        typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps")!.GetMethod("Resize")!.Invoke(null, new object[] { a, target, handle });

    static void TestShot(ImageEditorWindow editor, AnnotationToolbar toolbar)
    {
        var root = (FrameworkElement)editor.Content;
        root.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = System.IO.File.Create("annotate_arrow_styles.png");
        enc.Save(f);
    }

    static bool AnnotationOps_IsTooSmall(Annotation a) =>
        (bool)typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps")!.GetMethod("IsTooSmall")!.Invoke(null, new object[] { a })!;

    static AnnotationTool? AnnotationOps_Next(AnnotationTool t) =>
        (AnnotationTool?)typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps")!.GetMethod("NextVariant")!.Invoke(null, new object[] { t });
}
