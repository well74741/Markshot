using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Markshot.UI;

static class MenuProbe
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }

    public static void Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var bar = new AnnotationToolbar();
        var win = new Window { Content = bar, SizeToContent = SizeToContent.WidthAndHeight, Left = 200, Top = 200, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        win.Show();
        Pump(300);
        var chevron = (Button)bar.FindName("ShapeChevron");
        void Press() => chevron.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        void Click() => chevron.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        bool Open() => chevron.ContextMenu?.IsOpen == true;

        Press(); Click(); Pump(200);
        Check(Open(), "first click opens the menu");
        // order 1: the press arrives while the menu is still open, the click before Closed
        Press(); Click(); Pump(200);
        Check(!Open(), "click on the arrow while open closes it (press before close)");
        Pump(400);
        Press(); Click(); Pump(200);
        Check(Open(), "opens again later");
        // order 2: the menu closes first (click outside it), then press + click on the arrow
        chevron.ContextMenu!.IsOpen = false; Pump(20);
        Press(); Click(); Pump(200);
        Check(!Open(), "click on the arrow right after the menu closed does not reopen it");
        Pump(400);
        Press(); Click(); Pump(200);
        Check(Open(), "and later it opens normally");
        chevron.ContextMenu!.IsOpen = false;
        win.Close();
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
