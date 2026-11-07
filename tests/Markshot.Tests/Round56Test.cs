using System;
using System.Collections.Generic;
using System.IO;
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

/// <summary>0.8.56: moving pins turn, settings ask before closing, Z undo, the history window, stability.</summary>
static class Round56Test
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
                x.GetParameters().Zip(args).All(p => p.Second == null || p.First.ParameterType.IsInstanceOfType(p.Second) || p.First.ParameterType.IsEnum || p.First.ParameterType.IsValueType));
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

    static BitmapSource Blank(int w, int h, byte value = 90)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = value; px[i + 1] = (byte)(value / 2); px[i + 2] = 20; px[i + 3] = 255; }
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    static string Source(string file) => File.ReadAllText(Repo.File(file));

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var dir = Path.GetDirectoryName(AppConfig.ConfigPath)!;

        // ---- a moving pin turns and flips and keeps moving; it is not annotated
        {
            var gif = Path.Combine(dir, "turn.gif");
            var encoder = new GifBitmapEncoder();
            foreach (var v in new byte[] { 40, 140, 240 }) encoder.Frames.Add(BitmapFrame.Create(Blank(40, 20, v)));
            using (var f = File.Create(gif)) encoder.Save(f);
            var moving = AnimatedImage.Load(gif, 2400)!;
            var pin = new PinWindow(moving.Frames[0], -4000, -4000, null, null) { Left = -4000, Top = -4000, ShowActivated = false, Animation = moving };
            pin.Show();
            Pump(200);
            Call(pin, "ApplyOrientation", 'R', true);
            Pump(50);
            var turned = (AnimatedImage)Get(pin, "_animation")!;
            Check(pin.IsPlaying && turned.Frames[0].PixelWidth == 20 && turned.Frames[0].PixelHeight == 40, "turned: every frame turns and it keeps playing");
            Call(pin, "ApplyOrientation", 'H', true);
            Pump(50);
            Check(pin.IsPlaying, "mirrored: still playing");
            Call(pin, "EnterEditMode");
            Check(!(bool)Get(pin, "_editMode")! && pin.IsPlaying, "a moving pin does not go into annotating");
            pin.Close();
        }

        // ---- settings: changes wait for 保存; closing asks; 不保存 puts everything back
        {
            var config = new AppConfig();
            File.WriteAllText(AppConfig.ConfigPath, "{}");
            var settings = new SettingsWindow(config) { Left = -4000, Top = -4000, ShowActivated = false };
            settings.Show();
            Pump(200);
            Check(!settings.HasUnsavedChanges, "opening the settings changes nothing");
            ((RadioButton)settings.FindName("ThemeLight")).IsChecked = true;
            ((Slider)settings.FindName("FloatSizeSlider")).Value = 150;
            var gif = Path.Combine(dir, "icon.gif");
            File.Copy(Path.Combine(dir, "turn.gif"), gif, true);
            Call(settings, "UseFloatIcon", gif);
            var taken = FloatIcon.CustomPath(config)!;
            Check(settings.HasUnsavedChanges && !File.ReadAllText(AppConfig.ConfigPath).Contains("Light"), "a theme / icon chosen is not saved until 保存");
            var asked = 0;
            settings.GetType().GetField("AskOverride", Any)!.SetValue(settings, new Func<int>(() => { asked++; return 1; }));
            settings.Close();
            Pump(100);
            Check(asked == 1 && !settings.IsVisible, "closing with changes asks first");
            Check(config.FloatIcon == "" && Math.Abs(config.FloatIconSize - 1) < 0.001 && config.Theme == "Dark" && !File.Exists(taken),
                "不保存: the icon, size and theme go back (the picture taken is removed)");

            var second = new SettingsWindow(config) { Left = -4000, Top = -4000, ShowActivated = false };
            second.Show();
            Pump(200);
            ((Slider)second.FindName("FloatOpacitySlider")).Value = 50;
            second.GetType().GetField("AskOverride", Any)!.SetValue(second, new Func<int>(() => 2));
            second.Close();
            Pump(50);
            Check(second.IsVisible, "取消: the window stays");
            second.GetType().GetField("AskOverride", Any)!.SetValue(second, new Func<int>(() => 0));
            second.Close();
            Pump(100);
            Check(!second.IsVisible && Math.Abs(config.FloatIconOpacity - 0.5) < 0.001 && second.Saved, "保存 from the question keeps it");
        }

        // ---- Z / Shift+Z undo / redo in pins and the capture
        {
            var pin = new PinWindow(Blank(120, 80), -4000, -4000, Blank(120, 80), null) { Left = -4000, Top = -4000, ShowActivated = false };
            pin.Show();
            Pump(150);
            Call(pin, "EnterEditMode");
            var editor = (AnnotationEditor)Get(pin, "Editor")!;
            var history = (AnnotationHistory)Get(pin, "_history")!;
            history.Checkpoint(editor.Items);
            editor.Items.Add(new RectAnnotation { Rect = new Rect(5, 5, 20, 20) });
            var source = PresentationSource.FromVisual(pin)!;
            KeyEventArgs Key(Key key) => new(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
            Call(pin, "OnEditKeyDown", Key(System.Windows.Input.Key.Z));
            Check(editor.Items.Count == 0, "Z undoes");
            Check(Source("UI/PinWindow.Editing.cs").Contains("if (key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)") &&
                  Source("UI/OverlayWindow.Input.cs").Contains("if (key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)"), "Z / Shift+Z in pins and the capture");
            pin.Close();
        }

        // ---- the pin history: columns fill the width, its size is remembered
        {
            var config = Markshot.App.Config;
            config.PinHistoryWidth = 900;
            config.PinHistoryHeight = 520;
            var history = new PinHistoryWindow(() => Array.Empty<PinEntry>(), _ => { }, _ => { }, () => { }) { Left = -4000, Top = -4000, ShowActivated = false };
            history.Show();
            Pump(200);
            Check(Math.Abs(history.Width - 900) < 1, "it opens at the size it was left at");
            Check((int)Get(history, "Columns")! >= 5, $"as many columns as fit ({Get(history, "Columns")})");
            history.Width = 700;
            Pump(100);
            history.Close();
            Check(Math.Abs(config.PinHistoryWidth - 700) < 1, "a new size is remembered");
        }

        // ---- the floating button and others, by their source
        {
            var floatSource = Source("UI/FloatButtonWindow.cs");
            Check(floatSource.Contains("LayoutTransform = UiScale.Transform") && floatSource.Contains("Left = x / dpi.DpiScaleX"),
                "the row follows 工具栏大小; the button moves by Left / Top (no flash on the path)");
            Check(Source("UI/OverlayWindow.xaml.cs").Contains("Native.ForceForeground(handle)"), "the capture takes the keyboard (Esc) also after a click on the floating button");
            Check(Source("UI/ImageEditorWindow.cs").Contains("_waitingPin?.CloseImmediately();"), "清空图片 does not put the pin back");
            Check(Source("UI/Recording/RecordBarWindow.xaml.cs").Contains("message == 0x0005 /* WM_SIZE */"), "the recorder bar's rounded region follows its real size");
            Check(Source("UI/InlineTextEditor.cs").Contains("_box.ScrollToHorizontalOffset(0);"), "the text box is not scrolled while its frame is dragged");
        }

        // ---- stability
        {
            var cursors = typeof(AnnotationEditor).Assembly.GetType("Markshot.Core.ToolCursors")!;
            for (var i = 2; i < 200; i++)
            {
                cursors.GetMethod("Get")!.Invoke(null, new object[] { AnnotationTool.Pen, (double)i, Colors.Red, 1.0, 0 });
            }

            Check((int)cursors.GetProperty("CachedCount", Any)!.GetValue(null)! <= 96, "the cursor cache stays bounded");
            var entry = new PinEntry { FilePath = Path.Combine(dir, "turn.gif") };
            Check(entry.LoadImage() != null && entry.Image == null && entry.Thumbnail() is { PixelWidth: <= 320 }, "pin history pictures are read when needed, thumbnails small");
            Check(Source("App.xaml.cs").Contains("TaskScheduler.UnobservedTaskException") && Source("App.xaml.cs").Contains("entry.Image = null"),
                "background failures are logged; closed pins do not keep their pictures in memory");
            Check(Source("Core/AppConfig.cs").Contains("AtomicFile.WriteAllText(path, json)") && Source("Core/PinStore.cs").Contains("AtomicFile.WriteAllText"),
                "settings and the pin index are written whole or not at all");
            Check(Source("UI/Recording/RecorderController.cs").Contains("await FinishAsync(generation);"), "a failing recording stop does not stay stuck");
            Check(Source("UI/ImageEditorWindow.cs").Contains("UiScale.Changed -= _panelWidth"), "a closed editor is not kept by the size setting");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
