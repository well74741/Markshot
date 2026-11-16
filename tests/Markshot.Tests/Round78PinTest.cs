using System;
using System.Diagnostics;
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
using Markshot.Services;
using Markshot.UI;

/// <summary>Pin movement and toolbar copy, without the system clipboard, input injection or user settings.</summary>
static class Round78PinTest
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static object Get(object target, string field) => typeof(PinWindow).GetField(field, Any)!.GetValue(target)!;
    static void Set(object target, string field, object value) => typeof(PinWindow).GetField(field, Any)!.SetValue(target, value);
    static object Call(object target, string method, params object[] args) => typeof(PinWindow).GetMethod(method, Any)!.Invoke(target, args)!;
    static string Source(string file) => File.ReadAllText(Repo.File(file)).Replace("\r\n", "\n");
    static void Pump(int ms)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ms)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
            Thread.Sleep(2);
        }
    }

    static BitmapSource Picture()
    {
        var pixels = Enumerable.Repeat((byte)255, 160 * 100 * 4).ToArray();
        var image = BitmapSource.Create(160, 100, 96, 96, PixelFormats.Bgra32, null, pixels, 160 * 4);
        image.Freeze();
        return image;
    }

    static PinWindow Pin(int x, int y)
    {
        var pin = new PinWindow(Picture(), x, y) { ShowActivated = false };
        // Loaded is needed by group movement. No frame is shown and no window is activated:
        // its whole visual is hidden before Show, then the HWND is immediately hidden too.
        ((FrameworkElement)pin.FindName("RootGrid")).Visibility = Visibility.Hidden;
        pin.Show();
        pin.Hide();
        Pump(30);
        return pin;
    }

    static Native.RECT Bounds(PinWindow pin)
    {
        Native.GetWindowRect(new WindowInteropHelper(pin).Handle, out var bounds);
        return bounds;
    }

    static Native.RECT ImageBounds(PinWindow pin)
    {
        var raw = Call(pin, "ImageScreenRect");
        var type = raw.GetType();
        return new Native.RECT
        {
            Left = (int)type.GetField("Left")!.GetValue(raw)!, Top = (int)type.GetField("Top")!.GetValue(raw)!,
            Right = (int)type.GetField("Right")!.GetValue(raw)!, Bottom = (int)type.GetField("Bottom")!.GetValue(raw)!
        };
    }

    // Read-only HWND / screen queries; no input injection or system state changes.
    static class Native
    {
        public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
        [StructLayout(LayoutKind.Sequential)] public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    }

    static void StartDrag(PinWindow pin)
    {
        Set(pin, "_dragStartLeft", Get(pin, "_left"));
        Set(pin, "_dragStartTop", Get(pin, "_top"));
        Set(pin, "_dragStartCursorX", 1000);
        Set(pin, "_dragStartCursorY", 1000);
        Set(pin, "_dragging", true);
        Set(pin, "_pinMoved", false);
        Call(pin, "BeginGroupDrag");
    }

    static void CopyButton(PinWindow pin)
    {
        var toolbar = (AnnotationToolbar)Get(pin, "Toolbar");
        var callback = (Action)typeof(AnnotationToolbar).GetField("CopyRequested", Any)!.GetValue(toolbar)!;
        callback(); // the actual registered toolbar action, not a mouse / keyboard event
    }

    public static void Run()
    {
        _fail = 0;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var configProperty = typeof(Markshot.App).GetProperty("Config", Any)!;
        var oldConfig = configProperty.GetValue(null);
        var oldPath = AppConfig.PathOverride;
        var copyField = typeof(PinWindow).GetField("CopyPinImage", Any)!;
        var oldCopy = copyField.GetValue(null);
        var configDir = Path.Combine(Path.GetTempPath(), "markshot-round78-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);
        AppConfig.PathOverride = Path.Combine(configDir, "config.json");
        configProperty.SetValue(null, new AppConfig { PinTextSelect = false }); // no OCR or user's configuration
        var pins = new System.Collections.Generic.List<PinWindow>();
        try
        {
            Theme.Apply(ThemeMode.Dark);
            PinWindow.ClearPinGroup();
            var x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN) + 200;
            var y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN) + 200;
            var a = Pin(x, y); pins.Add(a);
            var b = Pin(x + 250, y + 150); pins.Add(b);
            var locked = Pin(x + 450, y); pins.Add(locked);
            Check(a.IsLoaded && b.IsLoaded && !a.IsVisible && !b.IsVisible, "test pins have loaded hidden HWNDs, never input or visible captures");

            // Hidden pins intentionally cannot be Shift-picked through GroupPress's visibility guard.
            // Supply the selection directly, then exercise the real movement / cancellation paths.
            var group = (System.Collections.IList)typeof(PinWindow).GetField("Group", Any)!.GetValue(null)!;
            group.Add(a); group.Add(b); group.Add(locked);
            Set(locked, "_locked", true);
            var beforeA = Bounds(a); var beforeB = Bounds(b); var beforeLocked = Bounds(locked);
            var imageHost = (FrameworkElement)a.FindName("ImageHost");
            var text = (PinTextLayer)a.FindName("TextLayer");
            text.SetLines(new[] { new OcrTextBlock("test", new Int32Rect(10, 10, 60, 20)) });
            text.SelectAll();
            imageHost.Measure(new Size(imageHost.Width, imageHost.Height));
            imageHost.Arrange(new Rect(0, 0, imageHost.Width, imageHost.Height));
            imageHost.UpdateLayout();
            var width = imageHost.Width; var height = imageHost.Height; var scale = text.Scale;
            var selected = text.SelectedText;
            var dpi = VisualTreeHelper.GetDpi(a);
            Check(Math.Abs(beforeA.Width - a.Width * dpi.DpiScaleX) <= 1 && Math.Abs(beforeA.Height - a.Height * dpi.DpiScaleY) <= 1,
                $"HWND size and WPF DIP size agree at {dpi.DpiScaleX * 100:0}% display scale");
            Check(text.IsMeasureValid && text.IsArrangeValid, "OCR layer layout is valid before movement");

            int sizeMessages = 0;
            HwndSourceHook hook = (IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            { if (msg == 0x0005) sizeMessages++; return IntPtr.Zero; };
            var source = HwndSource.FromHwnd(new WindowInteropHelper(a).Handle)!;
            source.AddHook(hook);
            StartDrag(a);
            Call(a, "ContinuePinDrag", 1001, 1001, ModifierKeys.None);
            Check(Bounds(a).Left == beforeA.Left && Bounds(a).Top == beforeA.Top, "click-sized movement stays below the four-pixel drag threshold");
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 200; i++) Call(a, "ContinuePinDrag", 1000 + i % 40, 1000 + i % 20, ModifierKeys.None);
            Call(a, "ContinuePinDrag", 1037, 1019, ModifierKeys.None);
            watch.Stop();
            var afterA = Bounds(a); var afterB = Bounds(b);
            Check(afterA.Left == beforeA.Left + 37 && afterA.Top == beforeA.Top + 19, "pin follows physical cursor deltas exactly");
            Check(afterB.Left == beforeB.Left + 37 && afterB.Top == beforeB.Top + 19 && Bounds(locked).Left == beforeLocked.Left,
                "multi-selected pins follow; locked pins stay put");
            Check(imageHost.Width == width && imageHost.Height == height && text.Scale == scale && text.SelectedText == selected &&
                text.IsMeasureValid && text.IsArrangeValid && sizeMessages == 0,
                $"201 moves preserve canvas / OCR layout and selection, without WM_SIZE ({watch.ElapsedMilliseconds} ms, diagnostic only)");
            Check(Math.Abs(a.Left * dpi.DpiScaleX - afterA.Left) <= 1 && Math.Abs(a.Top * dpi.DpiScaleY - afterA.Top) <= 1,
                "WPF Left / Top stay synchronized with the moved HWND");
            var image = ImageBounds(a);
            Check(image.Left == (int)Get(a, "_left") && image.Top == (int)Get(a, "_top"), "toolbar / language image anchor agrees with the displayed image position");
            Call(a, "ApplyBounds");
            Pump(30);
            Check(Bounds(a).Left == afterA.Left && Bounds(a).Top == afterA.Top, "a later complete WPF update does not snap a moved pin back");
            Call(a, "CancelPinDrag");
            Check(Bounds(a).Left == beforeA.Left && Bounds(a).Top == beforeA.Top && Bounds(b).Left == beforeB.Left && Bounds(b).Top == beforeB.Top &&
                !(bool)Get(a, "_dragging"), "right-click cancellation restores the entire group and ends dragging");
            source.RemoveHook(hook);

            StartDrag(a);
            Call(a, "ContinuePinDrag", 1030, 1012, ModifierKeys.Shift);
            Check(Bounds(a).Left == beforeA.Left + 30 && Bounds(a).Top == beforeA.Top, "Shift drag stays on the dominant horizontal axis");
            Call(a, "CancelPinDrag");
            StartDrag(a);
            Call(a, "ContinuePinDrag", 1008, 1030, ModifierKeys.Shift);
            Check(Bounds(a).Left == beforeA.Left && Bounds(a).Top == beforeA.Top + 30, "Shift drag stays on the dominant vertical axis");
            Call(a, "EndStaleDrag");
            Check(!(bool)Get(a, "_dragging") && ((System.Collections.IDictionary)Get(a, "_groupDragStart")).Count == 0,
                "a lost release ends the drag and discards old group anchors");
            PinWindow.ClearPinGroup();
            var beforeZoom = ImageBounds(a);
            Call(a, "ZoomBy", 1.25);
            Pump(30);
            var afterZoom = ImageBounds(a);
            Check(Math.Abs((beforeZoom.Left + beforeZoom.Right) - (afterZoom.Left + afterZoom.Right)) <= 2 &&
                Math.Abs((beforeZoom.Top + beforeZoom.Bottom) - (afterZoom.Top + afterZoom.Bottom)) <= 2,
                "zoom preserves the physical image center using WPF's display scale");
            Set(a, "_left", Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN) - 5000);
            Set(a, "_top", Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN) - 5000);
            Call(a, "ApplyPosition");
            var edge = Bounds(a);
            Check(edge.Left == Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN) && edge.Top == Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
                "position-only movement retains virtual-screen boundary limits");
            var edgeImage = ImageBounds(a);
            Check(edgeImage.Left == (int)Get(a, "_left") && edgeImage.Top == (int)Get(a, "_top"), "stored image position is the actual clamped position");

            // Image composition and the toolbar HWND are real; the clipboard is a fake.
            // The host stays wholly transparent, and the owning pin stays hidden.
            var editor = (AnnotationEditor)a.FindName("Editor");
            var toolbar = (AnnotationToolbar)Get(a, "Toolbar");
            var hostType = typeof(PinWindow).Assembly.GetType("Markshot.UI.ToolbarHostWindow")!;
            var toolbarHost = (Window)Activator.CreateInstance(hostType, new object[] { toolbar })!;
            toolbarHost.Owner = a;
            toolbarHost.ShowActivated = false;
            toolbarHost.Opacity = 0;
            Set(a, "_toolbarWindow", toolbarHost);
            var history = (AnnotationHistory)Get(a, "_history");
            history.Checkpoint(editor.Items);
            editor.Items.Add(new RectAnnotation { Rect = new Rect(10, 10, 30, 25), Color = Colors.Red, Thickness = 3 });
            Set(a, "_editMode", true);
            toolbar.Visibility = Visibility.Visible;
            editor.Visibility = Visibility.Visible;
            toolbarHost.Show();
            Pump(30);
            BitmapSource copied = null;
            copyField.SetValue(null, new Func<BitmapSource, bool>(bitmap => { copied = bitmap; return false; }));
            CopyButton(a);
            Check((bool)Get(a, "_editMode") && toolbar.Visibility == Visibility.Visible && toolbarHost.IsVisible && copied != null &&
                ((TextBlock)a.FindName("PinStatusText")).Text.Contains("复制失败"), "failed toolbar copy keeps editing and the toolbar open, with a retry hint");
            copyField.SetValue(null, new Func<BitmapSource, bool>(bitmap => { copied = bitmap; return true; }));
            CopyButton(a);
            Pump(250);
            Check(!(bool)Get(a, "_editMode") && toolbar.Visibility == Visibility.Collapsed && !toolbarHost.IsVisible && !a.IsVisible && new WindowInteropHelper(a).Handle != IntPtr.Zero,
                "successful toolbar copy folds the toolbar, without closing the pin");
            Check(history.CanUndo && editor.Items.Count == 1, "toolbar copy keeps editable annotations and their undo history");
            var pixels = new byte[copied.PixelWidth * copied.PixelHeight * 4];
            copied.CopyPixels(pixels, copied.PixelWidth * 4, 0);
            var index = (10 * copied.PixelWidth + 20) * 4;
            Check(pixels[index + 2] > 200 && pixels[index + 1] < 80, "toolbar copy includes the composed red annotation, not just the base image");
            Call(a, "EnterEditMode");
            Check((bool)Get(a, "_editMode") && toolbarHost.IsVisible && history.CanUndo,
                "the folded toolbar reopens and its undo history is still available");
            var editorCopy = typeof(ImageEditorWindow).GetMethod("Copy", Any)!;
            Check(editorCopy.GetBaseDefinition().DeclaringType == typeof(PinWindow) &&
                Source("UI/PinWindow.Editing.cs").Contains("Copy(); // the image editor keeps its toolbar and its own virtual copy behaviour"),
                "image editor toolbar still delegates to its virtual Copy method");

            var pinSource = Source("UI/PinWindow.xaml.cs");
            var editing = Source("UI/PinWindow.Editing.cs");
            Check(pinSource.Contains("!picking && !IsTextBypassHeld() && TextLayer.IsOverText(pixel)") &&
                editing.Contains("setting.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)") &&
                editing.Contains("\"SPACE\" => Keyboard.IsKeyDown(Key.Space)"), "text selection bypass and modifier combinations remain intact");
            Check(Source("UI/PinWindow.Translation.cs").Contains("if (!_placingPin) PlaceLanguageSwitch();") && Source("UI/PinWindow.xaml.cs").Contains("PlaceLanguageSwitch();") &&
                Source("UI/PinWindow.Translation.cs").Contains("var image = ImageScreenRect();"), "language label still follows the image's screen anchor");
        }
        finally
        {
            copyField.SetValue(null, oldCopy);
            PinWindow.ClearPinGroup();
            foreach (var pin in pins) pin.CloseImmediately();
            configProperty.SetValue(null, oldConfig);
            AppConfig.PathOverride = oldPath;
            Directory.Delete(configDir, true);
        }
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
        if (_fail != 0) Environment.ExitCode = 1;
    }
}
