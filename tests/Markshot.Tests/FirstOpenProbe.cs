using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.UI;

static class FirstOpenProbe
{
    static void Pump(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }
    }

    static string F(object o, string name) => o.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(o)!.ToString()!;
    static DependencyProperty PP(object o) => (DependencyProperty)o.GetType().GetField("ProgressProperty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;

    public static void Run()
    {
        // Like the capture window: toolbar on a canvas, collapsed until a region exists.
        var canvas = new Canvas { Width = 900, Height = 300 };
        var bar = new AnnotationToolbar { Visibility = Visibility.Collapsed };
        bar.SetTool(AnnotationTool.Select);
        bar.SetSelection(null);
        canvas.Children.Add(bar);
        var win = new Window { Content = canvas, Width = 900, Height = 300, Left = -4000, Top = -4000, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        win.Show();
        Pump(100);
        bar.Visibility = Visibility.Visible;
        Pump(400);
        var host = (FrameworkElement)bar.FindName("SettingsHost");
        Console.WriteLine($"before tool: host {host.ActualHeight:F1} from={F(host, "_from")} to={F(host, "_to")} content={F(host, "_content")} open={((Markshot.Core.SizeTransitionHost)host).IsOpen} snap={F(host, "_snapNext")}");
        bar.SetTool(AnnotationTool.Pen, notify: true);
        var samples = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            bar.UpdateLayout();
            samples.Add($"{host.ActualHeight:F1}[{F(host, "_from")}>{F(host, "_to")} snap={F(host, "_snapNext")}]");
            Pump(30);
        }

        Console.WriteLine("first open: " + string.Join(" ", samples));
        bar.SetTool(AnnotationTool.Text, notify: true);
        samples.Clear();
        for (var i = 0; i < 10; i++)
        {
            bar.UpdateLayout();
            samples.Add($"{host.ActualWidth:F0}");
            Pump(30);
        }

        Console.WriteLine("switch width: " + string.Join(" ", samples));
        win.Close();
    }
}
