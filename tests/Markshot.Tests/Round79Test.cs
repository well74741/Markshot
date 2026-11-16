using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

/// <summary>Dragging samples the newest cursor once per frame, without layout or a menu-fold wait.</summary>
static class Round79Test
{
    static int _fail;
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) _fail++; }
    static string Source(string name) => File.ReadAllText(Repo.File(name)).Replace("\r\n", "\n");
    static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle); Thread.Sleep(2); }
    }
    static FieldInfo Field(object o, string name) => o.GetType().GetField(name, Any)!;
    static void Set(object o, string name, object value) => Field(o, name).SetValue(o, value);
    static T Get<T>(object o, string name) => (T)Field(o, name).GetValue(o)!;
    static object? Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Any)!.Invoke(o, args);
    static void SetNativePoint(object o, string name, int x, int y)
    {
        var f = Field(o, name); var point = Activator.CreateInstance(f.FieldType)!;
        f.FieldType.GetField("X")!.SetValue(point, x); f.FieldType.GetField("Y")!.SetValue(point, y); f.SetValue(o, point);
    }
    static BitmapSource Image()
    {
        var image = BitmapSource.Create(180, 100, 96, 96, PixelFormats.Bgra32, null, new byte[180 * 100 * 4], 180 * 4);
        image.Freeze(); return image;
    }

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var startX = screen.Left + 120; var startY = screen.Top + 140;
        // All cursor values are supplied by test delegates: no mouse movement or keyboard injection.
        var pin = new PinWindow(Image(), startX, startY) { ShowActivated = false, Opacity = 0 };
        pin.Show(); pin.Hide(); Pump(120);
        var sourceX = Get<int>(pin, "_left"); var sourceY = Get<int>(pin, "_top");
        int moves = 0, sizes = 0;
        var hwnd = HwndSource.FromHwnd(new WindowInteropHelper(pin).Handle)!;
        HwndSourceHook hook = (IntPtr window, int message, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (message == 0x0047) moves++; // WINDOWPOSCHANGED
            if (message == 0x0005) sizes++;
            return IntPtr.Zero;
        };
        hwnd.AddHook(hook);
        try
        {
            Set(pin, "_dragStartLeft", sourceX); Set(pin, "_dragStartTop", sourceY);
            Set(pin, "_dragStartCursorX", 100); Set(pin, "_dragStartCursorY", 100);
            Set(pin, "_dragging", true); Set(pin, "_pinMoved", false);
            var cursor = new Point(250, 190);
            Set(pin, "DragPointerOverride", (Func<Point>)(() => cursor));
            for (var i = 0; i < 200; i++) Call(pin, "QueuePinDrag", 101 + i, 120, ModifierKeys.None, false);
            Check(moves == 0 && Get<int>(pin, "_left") == sourceX && Get<bool>(pin, "_pinDragQueued"),
                "200 mouse messages wait for one frame, not 200 HWND moves");
            Call(pin, "FlushPinDrag", null!, EventArgs.Empty);
            Check(Get<int>(pin, "_left") == sourceX + 150 && Get<int>(pin, "_top") == sourceY + 90,
                "frame reads the newest cursor, not the queued message's older position");
            Check(moves <= 2 && sizes == 0 && !Get<bool>(pin, "_pinDragQueued"), "one position pair, no resize, no leftover frame subscription");
            for (var i = 0; i < 15; i++)
            {
                cursor = new Point(251 + i, 191 + i);
                Call(pin, "QueuePinDrag", 101, 100, ModifierKeys.None, false);
                Call(pin, "FlushPinDrag", null!, EventArgs.Empty);
                Check(Get<int>(pin, "_left") == sourceX + 151 + i && Get<int>(pin, "_top") == sourceY + 91 + i,
                    "ordinary drag follows each single-pixel step, no snapping");
            }
            Call(pin, "QueuePinDrag", 300, 220, ModifierKeys.None, false);
            Call(pin, "CancelPinDrag");
            Call(pin, "FlushPinDrag", null!, EventArgs.Empty);
            Check(Get<int>(pin, "_left") == sourceX && Get<int>(pin, "_top") == sourceY && !Get<bool>(pin, "_pinDragQueued"),
                "right-click cancellation clears the queued move before restoring the origin");
            Set(pin, "_middleDragging", true); Set(pin, "_middleMoved", false);
            cursor = new Point(175, 150);
            Call(pin, "QueuePinDrag", 120, 110, ModifierKeys.Shift, true);
            Call(pin, "FlushPinDrag", null!, EventArgs.Empty);
            Check(Get<int>(pin, "_left") == sourceX + 75 && Get<int>(pin, "_top") == sourceY,
                "middle-button queued drag still supports Shift axis constraint");
            Set(pin, "_middleDragging", false);
            Set(pin, "_dragging", true);
            Call(pin, "QueuePinDrag", 110, 110, ModifierKeys.None, false);
            pin.CloseImmediately();
            Check(!Get<bool>(pin, "_pinDragQueued"), "closing a pin removes the pending Rendering handler");
        }
        finally
        {
            hwnd.RemoveHook(hook);
            if (pin.IsVisible) pin.CloseImmediately();
        }

        var config = new AppConfig { FloatActionHidden = "", FloatButtonX = screen.Left + screen.Width / 4, FloatButtonY = screen.Top + 250 };
        var button = new FloatButtonWindow(config, _ => { }, () => { }, () => { }) { FullscreenOverride = () => false, Opacity = 0 };
        var pointerField = typeof(FloatButtonWindow).GetField("DragPointerOverride", Any)!;
        var oldPointer = pointerField.GetValue(null);
        try
        {
            button.Show(); Pump(160);
            Call(button, "Expand"); Pump(120);
            var x = Get<double>(button, "_centerX"); var y = Get<double>(button, "_centerY");
            Set(button, "_pressCenter", (x, y)); SetNativePoint(button, "_pressPoint", 100, 100);
            Set(button, "_pressed", true); Set(button, "_moving", false);
            var cursor = new Point(115, 110);
            pointerField.SetValue(null, (Func<Point>)(() => cursor));
            var mouse = new MouseEventArgs(Mouse.PrimaryDevice, 0);
            Call(button, "OnMainMove", button, mouse);
            Check(Get<bool>(button, "_moveQueued") && !button.IsExpanded, "first drag move queues immediately: no 45 ms menu-fold wait");
            var openingSide = Get<bool>(button, "_panelLeft");
            var root = (FrameworkElement)button.Content;
            Check(root.CacheMode is BitmapCache, "only the small floating icon uses a temporary bitmap cache during dragging");
            cursor = new Point(100 + screen.Width / 2, 140);
            for (var i = 0; i < 180; i++) Call(button, "OnMainMove", button, mouse);
            Call(button, "ApplyPendingMove", null!, EventArgs.Empty);
            var center = ((FrameworkElement)Get<object>(button, "_main")).PointToScreen(new Point(config.FloatIconSize * FloatButtonWindow.BaseSize / 2, config.FloatIconSize * FloatButtonWindow.BaseSize / 2));
            var wantX = x + screen.Width / 2;
            Check(Math.Abs(center.X - wantX) <= 2 && Math.Abs(center.Y - (y + 40)) <= 2,
                $"float frame follows newest pointer and keeps the grab offset ({center.X:0},{center.Y:0})");
            Check(Get<bool>(button, "_panelLeft") == openingSide && root.IsMeasureValid && root.IsArrangeValid,
                "crossing the screen centre does not rearrange the menu mid-drag");
            cursor = new Point(100 + screen.Width / 2 - 10, 155);
            Call(button, "EndPress", false);
            Check(Math.Abs(Get<double>(button, "_centerX") - (wantX - 10)) <= 1 && root.CacheMode == null && !Get<bool>(button, "_moveQueued"),
                "release flushes the final position and clears the temporary cache");
            var code = Source("UI/FloatButtonWindow.cs");
            var drag = code[code.IndexOf("private void PlaceDragPosition()", StringComparison.Ordinal)..];
            drag = drag[..drag.IndexOf("internal bool Dragging", StringComparison.Ordinal)];
            Check(!drag.Contains("UpdateLayout") && !drag.Contains("Arrange("), "floating-button movement does not force layout");
            Set(button, "_pressed", true); Set(button, "_moving", true);
            Call(button, "OnMainMove", button, mouse);
            button.Close();
            Check(!Get<bool>(button, "_moveQueued") && root.CacheMode == null, "closing the float window removes queued Rendering work and its cache");
        }
        finally
        {
            pointerField.SetValue(null, oldPointer);
            button.Close();
        }

        var app = Source("App.xaml.cs");
        Check(app.Contains("Mouse.Captured != null") && app.Contains("_float?.Dragging == true") && app.Contains("_trimTimer?.Start(); // a timer"),
            "idle cleanup is deferred while dragging / capturing input");
        Check(app.Contains("_settings?.IsVisible == true") && app.Contains("Do not evict visible pictures from RAM"),
            "visible content is not force-trimmed out of RAM before the next drag");
        var project = Source("Markshot.csproj");
        Check(project.Contains("<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>") && project.Contains("System.GC.ConserveMemory\" Value=\"3\""),
            "background GC and moderate memory conservation preserve responsiveness");
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
