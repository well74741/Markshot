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

        Pump(45000); // the scrolling lines have played out
        foreach (var page in new[] { "Style", "Hotkeys", "Toolbar", "Translate", "General" })
        {
            settings.ShowPage(page);
            Pump(1500);
            var frames = 0;
            EventHandler count = (_, _) => frames++;
            System.Windows.Media.CompositionTarget.Rendering += count;
            Pump(2000);
            System.Windows.Media.CompositionTarget.Rendering -= count;
            Console.WriteLine($"idle on {page}: {frames} frames in 2 s");
        }

        void Walk(DependencyObject node, int depth)
        {
            if (node is UIElement element && element.HasAnimatedProperties)
            {
                Console.WriteLine($"animated: {node.GetType().Name} {(node as FrameworkElement)?.Name} {(node as System.Windows.Controls.TextBlock)?.Text}");
            }

            if (node is System.Windows.Controls.TextBlock block)
            {
                foreach (var effect in block.TextEffects)
                {
                    if (effect.Transform is { HasAnimatedProperties: true }) Console.WriteLine($"marquee: {block.Name} '{block.Text}'");
                }
            }

            if (node is FrameworkElement fe && fe.RenderTransform is { HasAnimatedProperties: true }) Console.WriteLine($"transform animated: {node.GetType().Name} {fe.Name}");
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i), depth + 1);
        }

        Walk(settings, 0);
        settings.Close();
    }

    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); System.Threading.Thread.Sleep(5); } }
}
