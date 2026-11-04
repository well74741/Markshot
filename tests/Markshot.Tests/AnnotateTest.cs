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
        Check(PinWindow.SelectedPins.Count == 2 && a.InGroup && b.InGroup && Mark(a) == Visibility.Visible, "click A, Shift+click B: both picked (dashed outline)");
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
        Check(((FrameworkElement)toolbarCut.FindName("EraserChevron")).Visibility == Visibility.Visible, "the eraser button has its ▾ menu");
        var modes = (FrameworkElement)toolbarCut.FindName("EraserModes");
        var row = (Panel)VisualTreeHelper.GetParent(modes);
        Check(row.Children.IndexOf(modes) == 0, "擦除 / 抠除 are the first thing in the settings row");
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
            Check(actions.Take(3).SequenceEqual(new[] { "Paste", "Open", "Save" }) && actions.Contains("Clear"), $"editor bar: paste, open, save, clear … ({string.Join(",", actions)})");
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
            Check(string.Join(",", shown) == "Pin,Paste,Open,Save", $"the editor bar has its own order / hidden buttons ({string.Join(",", shown)})");
            Check(!((FrameworkElement)edBar.FindName("ToolNumber")).IsVisible, "and its own hidden tools");
            (cfg.EditorActionOrder, cfg.EditorActionHidden, cfg.EditorToolHidden) = (oldOrder, oldHidden, oldTools);
            edBar.ApplyToolOrderFromConfig();
            ed.CloseWithoutAsking();
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
        Check(string.Join(",", pinKeys) == "Close,Save,Clear,Apply,Edit", "pin bar: 关闭 before 保存, 清空批注 before 应用");
        var order = (List<string>)catalog.GetMethod("Order")!.Invoke(null, new object[] { "Save,Apply,Edit", pinKeys })!;
        Check(order.Contains("Clear") && order.Contains("Close"), "an old saved order gets the new buttons");

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static bool AnnotationOps_HitNothing(List<Annotation> items, Point p) =>
        typeof(PinWindow).Assembly.GetType("Markshot.UI.AnnotationOps")!.GetMethod("HitTest")!.Invoke(null, new object[] { items, p, 4.0 }) is not CutoutAnnotation;

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
