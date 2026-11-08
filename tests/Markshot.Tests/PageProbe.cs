using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

/// <summary>By hand: how long each settings page takes to show (layout + first render).</summary>
static class PageProbe
{
    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settings = new SettingsWindow(new AppConfig { Theme = "Dark" }) { Left = 100, Top = 100, ShowActivated = false };
        settings.Show();
        Pump(500);
        foreach (var round in new[] { 1, 2 })
        foreach (var page in new[] { "Hotkeys", "Toolbar", "Translate", "General", "Style" })
        {
            var watch = Stopwatch.StartNew();
            settings.ShowPage(page);
            settings.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Console.WriteLine($"round {round} {page}: {watch.ElapsedMilliseconds} ms");
            Pump(400);
        }

        settings.Close();
    }

    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); System.Threading.Thread.Sleep(5); } }
}
