using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.74: the style plate stays under its button wherever the bar is (Windows keeps popups on the screen).</summary>
static class Round74Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var config = Markshot.App.Config;
        var before = config.HideToolChevrons;
        config.HideToolChevrons = true;
        // Off every screen (Windows moves the popup onto one) — the plate must still be centred under its button.
        foreach (var (left, top) in new[] { (-4000.0, -4000.0), (-2600.0, 200.0) })
        {
            var bar = new AnnotationToolbar();
            var window = new Window { Content = bar, Left = left, Top = top, Width = 900, Height = 200, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();
            bar.SetMode(AnnotationToolbarMode.Capture);
            bar.ApplyToolOrderFromConfig();
            Pump(150);
            foreach (var name in new[] { "ToolShape", "ToolEraser" })
            {
                var tool = (FrameworkElement)bar.FindName(name);
                typeof(AnnotationToolbar).GetMethod("OpenHoverMenu", Any)!.Invoke(bar, new object[] { tool });
                for (var waited = 0; waited < 1500; waited += 50) Pump(50);
                var card = (FrameworkElement)typeof(AnnotationToolbar).GetField("_hoverCard", Any)!.GetValue(bar)!;
                var plate = card.PointToScreen(new Point(card.ActualWidth / 2, 0)).X;
                var button = tool.PointToScreen(new Point(tool.ActualWidth / 2, 0)).X;
                var shift = (double)typeof(AnnotationToolbar).GetProperty("HoverShift", Any)!.GetValue(bar)!;
                Check(Math.Abs(plate - button) < 3, $"bar at ({left}, {top}), {name}: the plate's middle {plate:0} under the button's {button:0} (layer shifted {shift:0})");
                bar.CloseHoverMenu();
                Pump(150);
            }

            window.Close();
        }

        config.HideToolChevrons = before;
        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
