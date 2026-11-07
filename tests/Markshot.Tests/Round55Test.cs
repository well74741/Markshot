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
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.55: floating button, moving pins, the editor, sizes per tool, right click, text by double-click.</summary>
static class Round55Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    static object? Call(object t, string name, params object[] args)
    {
        for (var type = t.GetType(); type != null; type = type.BaseType)
        {
            var m = type.GetMethods(Any | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length &&
                x.GetParameters().Zip(args).All(p => p.Second == null || p.First.ParameterType.IsInstanceOfType(p.Second) || p.First.ParameterType.IsEnum));
            if (m != null) return m.Invoke(t, args);
        }

        throw new MissingMethodException(name);
    }

    static object? Get(object t, string name)
    {
        for (var type = t.GetType(); type != null; type = type.BaseType)
        {
            if (type.GetField(name, Any | BindingFlags.DeclaredOnly) is { } f) return f.GetValue(t);
            if (type.GetProperty(name, Any | BindingFlags.DeclaredOnly) is { } p) return p.GetValue(t);
        }

        throw new MissingFieldException(name);
    }

    static void Set(object t, string name, object value)
    {
        for (var type = t.GetType(); type != null; type = type.BaseType)
        {
            if (type.GetField(name, Any | BindingFlags.DeclaredOnly) is { } f) { f.SetValue(t, value); return; }
        }

        throw new MissingFieldException(name);
    }

    static void Click(ToggleButton b) => b.GetType().GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(b, null);

    static BitmapSource Blank(int w, int h, byte value = 90, byte alpha = 255)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = value; px[i + 3] = alpha; }
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    static MouseButtonEventArgs Press(MouseButton button, int clicks)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button) { RoutedEvent = button == MouseButton.Right ? Mouse.PreviewMouseDownEvent : Mouse.MouseDownEvent };
        typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.GetSetMethod(true)!.Invoke(args, new object[] { clicks });
        return args;
    }

    static PinWindow EditPin()
    {
        var pin = new PinWindow(Blank(200, 120), -4000, -4000, Blank(200, 120), null) { Left = -4000, Top = -4000, ShowActivated = false };
        pin.Show();
        Pump(150);
        Call(pin, "EnterEditMode");
        Pump(80);
        return pin;
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var dir = Path.GetDirectoryName(AppConfig.ConfigPath)!;

        // ---- a selected element keeps its size when another tool is picked; tools keep their own sizes
        foreach (var how in new[] { "SelectAnnotation", "SelectQuietly" })
        {
            var pin = EditPin();
            var toolbar = (AnnotationToolbar)Get(pin, "Toolbar")!;
            var editor = (AnnotationEditor)Get(pin, "Editor")!;
            var number = new NumberAnnotation { Center = new Point(50, 50), Radius = 30, Number = 1, Color = Colors.Red };
            var rect = new RectAnnotation { Rect = new Rect(10, 10, 60, 40), Thickness = 9, Color = Colors.Red };
            editor.Items.Add(number);
            editor.Items.Add(rect);
            if (how == "SelectQuietly") { Click((ToggleButton)toolbar.FindName("ToolNumber")); Pump(30); }
            Call(pin, how, number);
            Click((ToggleButton)toolbar.FindName("ToolPen"));
            Pump(50);
            Check(Math.Abs(number.Radius - 30) < 0.01 && Math.Abs(toolbar.Thickness - 3) < 0.01, $"{how}: a selected 序号 keeps its size when the pen is picked ({number.Radius}, pen {toolbar.Thickness})");
            Call(pin, how, rect);
            Click((ToggleButton)toolbar.FindName("ToolNumber"));
            Pump(50);
            Check(Math.Abs(rect.Thickness - 9) < 0.01 && Math.Abs(toolbar.Thickness - 14) < 0.01, $"{how}: a selected rectangle keeps its width, 序号 its own size ({rect.Thickness}, {toolbar.Thickness})");
            pin.Close();
        }

        // ---- right click with an element selected: let go of it, 选择; the menu on the next one
        {
            var pin = EditPin();
            var editor = (AnnotationEditor)Get(pin, "Editor")!;
            var rect = new RectAnnotation { Rect = new Rect(10, 10, 60, 40), Thickness = 4, Color = Colors.Red };
            editor.Items.Add(rect);
            Call(pin, "SetTool", AnnotationTool.Rectangle);
            Call(pin, "SelectQuietly", rect);
            var right = Press(MouseButton.Right, 1);
            pin.RaiseEvent(right);
            Check(Get(pin, "_selected") == null && right.Handled && (AnnotationTool)Get(pin, "_tool")! is AnnotationTool.None or AnnotationTool.Select,
                "pin: a right click lets go of the selected element and puts the tool down");
            var again = Press(MouseButton.Right, 1);
            pin.RaiseEvent(again);
            Check(!again.Handled, "the next right click is the menu again");
            pin.Close();
            var overlay = File.ReadAllText(Repo.File("UI/OverlayWindow.xaml.cs"));
            Check(overlay.Contains("if (_selectedAnnotation != null)\r\n                {\r\n                    // An element is selected: the right click lets go of it") ||
                  overlay.Contains("if (_selectedAnnotation != null)\n                {\n                    // An element is selected: the right click lets go of it"), "capture: a right click lets go of the selected element first");
        }

        // ---- text: a click on the blank lets go / does nothing; a double-click makes a new text
        {
            var pin = EditPin();
            var editor = (AnnotationEditor)Get(pin, "Editor")!;
            var text = new TextAnnotation { Position = new Point(10, 10), Text = "abc", FontSize = 16, Color = Colors.Red };
            editor.Items.Add(text);
            Call(pin, "SetTool", AnnotationTool.Text);
            Call(pin, "SelectQuietly", text);
            var input = (UIElement)Get(pin, "InputLayer")!;
            Call(pin, "Input_MouseLeftButtonDown", input, Press(MouseButton.Left, 1));
            var editing = (InlineTextEditor)Get(pin, "_textEditor")!;
            Check(Get(pin, "_selected") == null && !editing.IsActive && editor.Items.Count == 1, "text tool: a click on the blank lets go of the text, no new box");
            Call(pin, "Input_MouseLeftButtonDown", input, Press(MouseButton.Left, 2));
            Check(editing.IsActive, "text tool: a double-click on the blank makes a new text");
            pin.Close();
        }

        // ---- a move measures from its own press (it jumped by the press that drew the element)
        {
            var source = File.ReadAllText(Repo.File("UI/PinWindow.Editing.cs"));
            var down = source[source.IndexOf("private void Input_MouseLeftButtonDown", StringComparison.Ordinal)..];
            Check(down.IndexOf("_pressPoint = point;", StringComparison.Ordinal) is > 0 and < 300, "every press sets the point a move is measured from");
            var frame = File.ReadAllText(Repo.File("Annotations/AnnotationEditor.cs"));
            Check(frame.Contains("new Pen(Accent, 1.6 * unit)"), "the rotation grips are the frame's blue");
        }

        // ---- a moving picture plays in its pin; not while annotating; its shadow follows its shape
        {
            var gif = Path.Combine(dir, "move.gif");
            var encoder = new GifBitmapEncoder();
            foreach (var v in new byte[] { 40, 140, 240 }) encoder.Frames.Add(BitmapFrame.Create(Blank(40, 30, v)));
            using (var f = File.Create(gif)) encoder.Save(f);
            var moving = AnimatedImage.Load(gif, 2400)!;
            var pin = new PinWindow(moving.Frames[0], -4000, -4000, null, null) { Left = -4000, Top = -4000, ShowActivated = false, Animation = moving, ShapedShadow = true };
            pin.Show();
            Pump(200);
            Check(pin.IsPlaying, "a GIF pin plays");
            Call(pin, "EnterEditMode");
            Pump(50);
            Check(!pin.IsPlaying, "annotating it: the still first frame");
            Call(pin, "ExitEditMode", false);
            Pump(50);
            Check(pin.IsPlaying, "nothing drawn: it moves again");
            var img = (Image)Get(pin, "Img")!;
            Check(img.Effect is System.Windows.Media.Effects.DropShadowEffect && ((UIElement)Get(pin, "ImageShadowLayer")!).Visibility != Visibility.Visible,
                "its shadow follows the picture (no plate behind see-through parts)");
            pin.Close();
            var app = File.ReadAllText(Repo.File("App.xaml.cs"));
            Check(app.Contains("PinFileRequested += (file, index) => PinFile(") && app.Contains("shapedShadow: ImageOps.HasTransparentCorner(image), animation: animation"),
                "dropped files are pinned in the middle of the screen (moving ones play)");
            Check(app.Contains("CreatePin(image, cursor.X + 16, cursor.Y + 16, sweep: sweep, shapedShadow: ImageOps.HasTransparentCorner(image))"),
                "a see-through picture from the clipboard gets the shaped shadow too");
        }

        // ---- the floating button: size, the history button, closed after a capture, fades
        {
            var config = new AppConfig { FloatButtonEnabled = true };
            var ran = new List<string>();
            var button = new FloatButtonWindow(config, ran.Add, () => { }, () => { }) { FullscreenOverride = () => false };
            button.Show();
            Pump(300);
            Check(button.ShownActions.Contains("History"), "the row has 贴图历史");
            Check(((double)Get(button, "Shown")!) > 0.9, "it fades in once in place");
            var main = (Border)Get(button, "_main")!;
            var small = main.Width;
            config.FloatIconSize = 2;
            button.ApplyLook();
            Check(Math.Abs(main.Width - small * 2) < 0.01 && Math.Abs(small - FloatButtonWindow.BaseSize) < 0.01, $"size 200 % = twice as big ({small} → {main.Width})");
            button.PointerOnItOverride = () => true;
            button.Suspend();
            Check(!button.IsVisible, "out of the way during a capture (after a quick fade)");
            button.Resume();
            Pump(200);
            Check((bool)Get(button, "HoverBlocked")! && button.IsVisible, "back after the capture with the pointer on it: it stays closed");
            Call(button, "Hover", false);
            button.Close();
            var app = File.ReadAllText(Repo.File("App.xaml.cs"));
            Check(app.Contains("case \"History\":\r\n                ShowPinHistory();") || app.Contains("case \"History\":\n                ShowPinHistory();"), "贴图历史 opens the pin history");
        }

        // ---- the settings: the floating button below 贴图阴影, its size slider, 工具栏大小
        {
            var settings = new SettingsWindow(new AppConfig()) { Left = -4000, Top = -4000, ShowActivated = false };
            settings.Show();
            Pump(200);
            var card = (FrameworkElement)settings.FindName("FloatCard");
            var page = (Panel)card.Parent;
            var shadow = page.Children.OfType<Border>().FirstOrDefault(b => b.Child is StackPanel s && s.Children.OfType<TextBlock>().FirstOrDefault()?.Text == "贴图阴影");
            Check(shadow != null && page.Children.IndexOf(card) == page.Children.IndexOf(shadow) + 1, "the floating button's card is right below 贴图阴影");
            Check(settings.FindName("FloatSizeSlider") is Slider { Minimum: 100, Maximum: 200 }, "a size slider from 100 % to 200 %");
            var xaml = File.ReadAllText(Repo.File("UI/SettingsWindow.xaml"));
            Check(xaml.Contains("Text=\"工具栏大小\"") && !xaml.Contains("Text=\"界面大小\""), "界面大小 is called 工具栏大小");
            settings.Close();
        }

        // ---- the editor: 清空图片, 移动吸附, no double-click 调节框, the dark logo on a light title bar
        {
            var editor = new ImageEditorWindow(Blank(120, 80)) { Left = -4000, Top = -4000, ShowActivated = false };
            editor.Show();
            Pump(300);
            Check(editor.HasImage && editor.ClearDocument(ask: false) && !editor.HasImage, "清空图片 empties the editor");
            var config = Markshot.App.Config;
            config.EditorSnap = false;
            var moving = new RectAnnotation { Rect = new Rect(3, 3, 10, 10) };
            var at = (Point)Call(editor, "Snap", new List<Annotation> { moving }, new Rect(3, 3, 10, 10))!;
            Check(at == new Point(3, 3), "移动吸附 off: an element goes where it is put");
            config.EditorSnap = true;
            Check(new AppConfig().EditorSnap, "移动吸附 is on by default");
            editor.CloseWithoutAsking();
            var source = File.ReadAllText(Repo.File("UI/ImageEditorWindow.cs"));
            Check(source.Contains("\"清空图片\"") && source.Contains("\"移动吸附\"") && !source.Contains("Double-click on the picture (no tool, not on an annotation)"),
                "the editor has 清空图片 and 移动吸附; the double-click 调节框 is gone");
            Theme.Apply(ThemeMode.Light);
            var light = Theme.Logo();
            Theme.Apply(ThemeMode.Dark);
            Check(light is BitmapFrame lf && lf.Decoder != null && !ReferenceEquals(light, Theme.Logo()), "a light title bar gets the dark logo");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
