using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.53: the notice bubble, settings kept over updates, what is new, tools, icons, the floating button.</summary>
static class Round53Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static object? Call(object target, string name, params object[] args)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var method = type.GetMethods(Any | BindingFlags.DeclaredOnly).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length &&
                m.GetParameters().Zip(args).All(pair => pair.Second == null || pair.First.ParameterType.IsInstanceOfType(pair.Second) || pair.First.ParameterType.IsEnum));
            if (method != null) return method.Invoke(target, args);
        }

        throw new MissingMethodException(name);
    }
    static T Field<T>(object target, string name) => (T)FindField(target.GetType(), name).GetValue(target)!;
    static void SetField(object target, string name, object value) => FindField(target.GetType(), name).SetValue(target, value);
    static FieldInfo FindField(Type? type, string name)
    {
        for (; type != null; type = type.BaseType)
        {
            if (type.GetField(name, Any) is { } field) return field;
        }

        throw new MissingFieldException(name);
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    static BitmapSource Blank(int w, int h, byte value = 90)
    {
        var px = Enumerable.Repeat(value, w * h * 4).ToArray();
        for (var i = 3; i < px.Length; i += 4) px[i] = 255;
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; // as in the app (it lives in the tray)
        var dir = Path.GetDirectoryName(AppConfig.ConfigPath)!;
        Check(AppConfig.PathOverride != null && !AppConfig.ConfigPath.Contains("Roaming"), "tests use a config file of their own");

        // ---- the notice bubble instead of the Windows notification
        {
            Toast.Notify("已复制到剪贴板", "120 × 80");
            Pump(300);
            var toast = (Window?)typeof(Toast).GetProperty("Current", Any)!.GetValue(null);
            Check(toast != null && toast.IsVisible && (string)toast.GetType().GetProperty("Heading", Any)!.GetValue(toast)! == "已复制到剪贴板", "a notice shows Markshot's own bubble");
            if (toast != null)
            {
                var handle = new WindowInteropHelper(toast).Handle;
                var style = GetWindowLongPtr(handle, -20).ToInt64();
                Check((style & 0x08000000) != 0 && !toast.IsActive, "the bubble never takes the focus");
                var work = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
                GetWindowRect(handle, out var rect);
                Check(rect.Right <= work.Right && rect.Right > work.Right - 60 && rect.Bottom <= work.Bottom && rect.Bottom > work.Bottom - 60,
                    $"the bubble sits in the corner above the taskbar ({rect.Left},{rect.Top},{rect.Right},{rect.Bottom} in {work})");
                Toast.Notify("第二条");
                Pump(200);
                Check(!toast.IsVisible, "a new notice replaces the shown one");
            }

            var tray = File.ReadAllText(Repo.File("Core/TrayService.cs"));
            Check(!tray.Contains("ShowBalloonTip(") && tray.Contains("Toast.Notify"), "no Windows notification (no sound) for tray notices");
            for (var i = 0; i < 80 && typeof(Toast).GetProperty("Current", Any)!.GetValue(null) != null; i++) Pump(50);
            Check(typeof(Toast).GetProperty("Current", Any)!.GetValue(null) == null, "the bubble goes by itself");
        }

        // ---- settings survive: a value that does not read keeps only itself at the default
        {
            var json = "{ \"Theme\": \"Light\", \"PinShadowSize\": \"wide\", \"CaptureHotkey\": \"F2\", \"Recorder\": { \"Fps\": 60 } }";
            File.WriteAllText(AppConfig.ConfigPath, json);
            var loaded = AppConfig.Load();
            Check(loaded.Theme == "Light" && loaded.CaptureHotkey == "F2" && loaded.Recorder.Fps == 60 && Math.Abs(loaded.PinShadowSize - 12) < 0.01,
                $"a settings file with one bad value keeps all the others ({loaded.Theme}, {loaded.CaptureHotkey}, {loaded.Recorder.Fps})");
            Check(Directory.GetFiles(dir, "config.broken-*.json").Length > 0, "the file that did not read is kept as a copy");
            File.Delete(AppConfig.ConfigPath);
        }

        // ---- what is new after an update
        {
            Check(ReleaseNotes.Since("0.8.52", "0.8.53").Select(n => n.Version).SequenceEqual(new[] { "0.8.53" }) && ReleaseNotes.Since("0.8.53", "0.8.54").Count == 1, "from the previous version: only its notes");
            Check(ReleaseNotes.Since("", "0.8.53+abc").Select(n => n.Version).SequenceEqual(new[] { "0.8.53" }), "from an unknown older version: the current notes");
            Check(ReleaseNotes.Since("0.8.46", "0.8.53").Count >= 5 && ReleaseNotes.Since("0.8.53", "0.8.53").Count == 0, "from further back: every version since");
            Check(ReleaseNotes.All[0].Version == typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0],
                "the notes have an entry for this version");
            var whatsNew = new WhatsNewWindow("0.8.53", ReleaseNotes.Since("0.8.52", "0.8.53")) { Left = -4000, Top = -4000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
            whatsNew.Show();
            Pump(250);
            Call(whatsNew, "ClickOutside");
            Pump(400);
            Check(!whatsNew.IsVisible, "what's new closes on a click outside");
            var app = File.ReadAllText(Repo.File("App.xaml.cs"));
            Check(app.Contains("WhatsNewWindow") && app.Contains("config.before-update.json"), "an update shows what's new and keeps a copy of the old settings");
        }

        // ---- the settings: 样式 first; theme and size kept as soon as chosen; 更新 saves first
        {
            var config = new AppConfig();
            var settings = new SettingsWindow(config) { Left = -4000, Top = -4000, ShowActivated = false };
            settings.Show();
            Pump(300);
            var nav = (Panel)((FrameworkElement)settings.FindName("NavStyle")).Parent;
            var items = nav.Children.OfType<RadioButton>().ToList();
            Check(items.FirstOrDefault()?.Name == "NavStyle" && items[0].IsChecked == true &&
                  ((FrameworkElement)settings.FindName("PageStyle")).Visibility == Visibility.Visible, "样式 is the first page and opens first");
            ((RadioButton)settings.FindName("ThemeLight")).IsChecked = true;
            Pump(50);
            Check(config.Theme == "Dark" && settings.HasUnsavedChanges, "a chosen theme is previewed and waits for 保存 (0.8.56)");
            ((RadioButton)settings.FindName("ThemeDark")).IsChecked = true;
            Pump(50);
            var source = File.ReadAllText(Repo.File("UI/SettingsWindow.xaml.cs"));
            var update = source[source.IndexOf("private async void Update_Click", StringComparison.Ordinal)..];
            Check(update.IndexOf("Save_Click(this, e)", StringComparison.Ordinal) is > 0 and < 600, "更新 saves what was changed first");
            var check = (Button)settings.FindName("CheckUpdateButton");
            Check(check.Content is Border { Style: { } style } && style == (Style)Application.Current.FindResource("IconRefreshMask"), "the version refresh button has the library icon");

            // the floating button's card
            Check(settings.FindName("FloatCard") is Border && settings.FindName("FloatEnabledCheck") is CheckBox && settings.FindName("FloatOpacitySlider") is Slider,
                "样式 has the floating button's settings");
            var row = (Panel)settings.FindName("FloatActionsRow");
            Check(row.Children.Count == 6, "the floating button's row: 截屏, 录屏, 文字识别, 图片编辑器, 贴图历史");
            var gif = MakeGif(Path.Combine(dir, "spin.gif"));
            Check((bool)Call(settings, "UseFloatIcon", gif)! && FloatIcon.IsCustom(config) && File.Exists(FloatIcon.CustomPath(config)!), "a picture file becomes the icon (copied beside the settings)");
            var preview = (Image)settings.FindName("FloatIconImage");
            Check(preview.HasAnimatedProperties, "a moving picture plays in the preview");
            var old = FloatIcon.CustomPath(config)!;
            Call(settings, "FloatIconReset_Click", settings, new RoutedEventArgs());
            Check(!FloatIcon.IsCustom(config) && !preview.HasAnimatedProperties, "重置 brings back the Markshot logo");
            ((Slider)settings.FindName("FloatOpacitySlider")).Value = 60;
            Check(Math.Abs(config.FloatIconOpacity - 0.6) < 0.001 && Math.Abs(preview.Opacity - 0.6) < 0.01, "the opacity slider sets the icon's opacity");
            settings.ShowPage("Float");
            Pump(100);
            Check(((RadioButton)settings.FindName("NavStyle")).IsChecked == true, "the floating button's 设置 opens on its card");
            settings.Close();
        }

        // ---- animated pictures: frames and their speed
        {
            var path = MakeGif(Path.Combine(dir, "frames.gif"));
            var picture = AnimatedImage.Load(path)!;
            Check(picture.Frames.Count == 3 && picture.Durations.All(d => d.TotalMilliseconds >= 20), $"a GIF reads as its frames ({picture.Frames.Count})");
            using var icon = picture.ToIcon(16);
            Check(icon.Width == 16, "the first frame makes the tray icon");
            Check(FloatIcon.Logo().Frames[0].PixelWidth >= 32, "the default picture is the Markshot logo");
        }

        // ---- the floating button
        {
            var config = new AppConfig { FloatButtonEnabled = true };
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            config.FloatButtonX = work.Right - 30;
            config.FloatButtonY = work.Bottom - 30;
            var ran = new List<string>();
            var settingsOpened = 0;
            var closed = 0;
            var button = new FloatButtonWindow(config, ran.Add, () => settingsOpened++, () => closed++);
            button.FullscreenOverride = () => false;
            button.Show();
            Pump(300);
            var handle = new WindowInteropHelper(button).Handle;
            var floatSource = File.ReadAllText(Repo.File("UI/FloatButtonWindow.cs"));
            Check(floatSource.Contains("DwmFlush();"), "the floating button is off the screen before a screenshot is taken");
            Check(button.Center.X <= work.Right && button.Center.Y <= work.Bottom && button.PanelOnLeft, "on the right of the screen, the row opens to the left");
            GetWindowRect(handle, out var before);
            Check(button.ShownActions.SequenceEqual(new[] { "Capture", "Record", "Ocr", "Editor", "History", "AddPin" }), "the row: 截屏, 录屏, 文字识别, 图片编辑器");
            Call(button, "Hover", true);
            Pump(150);
            Check(button.IsExpanded, "the pointer on it opens the row at once");
            GetWindowRect(handle, out var open);
            Check(open.Left == before.Left && open.Right == before.Right, "opening does not move the window (the button stays put)");
            Call(button, "Hover", false);
            Check(!button.IsExpanded, "the pointer away closes it at once");
            Call(button, "Run", "Ocr");
            Pump(100);
            Check(ran.SequenceEqual(new[] { "Ocr" }), "a row button runs its action");
            config.FloatActionHidden = "Record";
            config.FloatActionOrder = "Editor,Capture,Ocr,Record";
            button.BuildItems();
            Check(button.ShownActions.SequenceEqual(new[] { "Editor", "History", "AddPin", "Capture", "Ocr" }), "the row follows the settings (order, hidden ones out)");
            Call(button, "DragBy", 100000.0, 100000.0);
            Pump(100);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)button.Center.X, (int)button.Center.Y)).WorkingArea;
            Check(button.Center.X < screen.Right && button.Center.Y < screen.Bottom && config.FloatButtonX == (int)Math.Round(button.Center.X),
                "dropped beyond the edge it stays on a screen (and the place is remembered)");
            var headers = (IReadOnlyList<string>)Call(button, "MenuHeaders")!;
            Check(headers.SequenceEqual(new[] { "添加贴图…", "粘贴剪贴板", "关闭所有贴图", "设置…", "全屏时自动隐藏", "关闭悬浮按钮" }), "the right-click menu: 设置, 全屏时自动隐藏, 关闭");
            button.FullscreenOverride = () => true;
            button.CheckFullscreen();
            Pump(300); // after its fade
            Check(!button.IsVisible && button.IsHiddenForFullscreen, "a full-screen program in front hides it");
            button.FullscreenOverride = () => false;
            button.CheckFullscreen();
            Check(button.IsVisible, "and it comes back afterwards");
            button.Suspend();
            Check(!button.IsVisible, "out of the way during a capture");
            button.Resume();
            Check(button.IsVisible, "back after the capture");
            config.PinShadowSize = 20;
            button.ApplyLook();
            Check(button.IconImage.Effect is System.Windows.Media.Effects.DropShadowEffect { BlurRadius: 20 }, "the shadow is the pins' shadow");
            var pinned = new List<BitmapSource>();
            button.PinRequested += pinned.Add;
            var drop = new DataObject(DataFormats.FileDrop, new[] { MakeGif(Path.Combine(dir, "drop.gif")), Path.Combine(dir, "config.json.missing") });
            Check(FloatButtonWindow.DroppedImages(drop).Count == 1, "a picture dropped on the button opens (other files are skipped)");
            Check(File.ReadAllText(Repo.File("App.xaml.cs")).Contains("PinRequested += image => PinCentered(image)"), "a picture dropped on the button is pinned");
            button.Close();
            var app = File.ReadAllText(Repo.File("App.xaml.cs"));
            Check(app.Contains("\"悬浮按钮回中\"") && app.Contains("_float?.Suspend()") && app.Contains("OcrOnSelect = ocr"),
                "tray: 悬浮按钮 and 悬浮按钮回中; capture hides it; 文字识别 recognises the chosen area");
        }

        // ---- tools: 序号 from 1 after 清空, sizes per tool, right click puts the tool down
        {
            var pin = new PinWindow(Blank(120, 80), -4000, -4000, Blank(120, 80), null) { Left = -4000, Top = -4000, ShowActivated = false };
            pin.Show();
            Pump(200);
            var editor = Field<AnnotationEditor>(pin, "Editor");
            editor.Items.Add(new NumberAnnotation { Center = new Point(20, 20), Number = 4, Radius = 12 });
            SetField(pin, "_number", 5);
            Call(pin, "ClearAnnotations");
            Check(Field<int>(pin, "_number") == 1, "清空批注 starts the numbers at 1 again");

            var toolbar = new AnnotationToolbar();
            toolbar.SetTool(AnnotationTool.Select);
            toolbar.SetSelection(new NumberAnnotation { Center = new Point(10, 10), Radius = 30, Number = 1 });
            toolbar.SetTool(AnnotationTool.Pen, notify: false);
            Check(Math.Abs(toolbar.Thickness - 3) < 0.01, $"the pen keeps its own size when a 序号 was selected ({toolbar.Thickness})");
            toolbar.SetSelection(null);
            toolbar.SetTool(AnnotationTool.Number);
            Check(Math.Abs(toolbar.Thickness - 14) < 0.01, $"and the 序号 tool its own ({toolbar.Thickness})");

            var pinTool = new PinWindow(Blank(120, 80), -4000, -4000, Blank(120, 80), null) { Left = -4000, Top = -4000, ShowActivated = false };
            pinTool.Show();
            Pump(200);
            Call(pinTool, "EnterEditMode");
            Pump(100);
            Call(pinTool, "SetTool", AnnotationTool.Pen);
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = Mouse.MouseDownEvent };
            Call(pinTool, "Input_MouseRightButtonDown", pinTool, args);
            Check(Field<AnnotationTool>(pinTool, "_tool") is AnnotationTool.None or AnnotationTool.Select && args.Handled, "a right click with a tool in hand puts it down (no menu yet)");
            var overlay = File.ReadAllText(Repo.File("UI/OverlayWindow.xaml.cs"));
            Check(overlay.Contains("SetTool(AnnotationTool.Select);\n                    UpdateCursorToPointer();\n                    return;") || overlay.Contains("SetTool(AnnotationTool.Select);\r\n                    UpdateCursorToPointer();\r\n                    return;"),
                "capture: the first right click puts the tool down, the next one is Esc");
            pin.Close();
            pinTool.Close();
        }

        // ---- icons from the library, sized like the others
        {
            var appXaml = File.ReadAllText(Repo.File("App.xaml"));
            Check(appXaml.Contains("Assets/Icons/screen_record.png") && appXaml.Contains("Assets/Icons/ocr.png") && appXaml.Contains("Assets/Icons/refresh.png"),
                "录屏 / 文字识别 / 刷新 use the library icons");
            foreach (var name in new[] { "screen_record", "ocr", "capture" })
            {
                var frame = BitmapFrame.Create(new Uri(Repo.File($"Assets/Icons/{name}.png")));
                var (l, t, r, b) = Program.Bbox(frame);
                var side = Math.Max(r - l + 1, b - t + 1) / (double)frame.PixelWidth;
                Check(side is > 0.76 and < 0.86, $"{name}.png fills the tile like the other icons ({side:P0})");
            }
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static string MakeGif(string path)
    {
        var encoder = new GifBitmapEncoder();
        foreach (var value in new byte[] { 40, 140, 240 })
        {
            encoder.Frames.Add(BitmapFrame.Create(Blank(24, 24, value)));
        }

        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        return path;
    }
}
