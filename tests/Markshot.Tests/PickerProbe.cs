using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Markshot.UI;

static class PickerProbe
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }

    static void Key(Window w, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        w.RaiseEvent(args);
    }

    public static void Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var picker = new ColorPickerWindow(Colors.Red);
        picker.Show();
        Pump(300);
        var shieldField = typeof(ColorPickerWindow).GetField("_shield", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var button = (ButtonBase)picker.FindName("EyedropperButton");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(500);
        Check(shieldField.GetValue(picker) != null, "eyedropper stays active after it opens (focus goes back to the panel)");
        Key(picker, System.Windows.Input.Key.Escape);
        Pump(100);
        Check(shieldField.GetValue(picker) == null && picker.IsVisible, "Esc ends picking, the panel stays");
        Key(picker, System.Windows.Input.Key.Escape);
        Pump(300);
        Check(!picker.IsVisible && !picker.Accepted, "Esc again closes the panel (cancel)");
        Mouse.OverrideCursor = null;
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
