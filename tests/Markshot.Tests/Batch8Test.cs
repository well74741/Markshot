using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Annotations;
using Markshot.Core;
using Markshot.UI;

static class Batch8Test
{
    static int _fail;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) _fail++;
    }

    static void Pump(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }
    }

    /// <summary>The last update check failed on the network or GitHub's side (5xx, no connection), not on what the page says.</summary>
    static bool NetworkTrouble()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Markshot-test");
            using var answer = http.GetAsync("https://github.com/well74741/Markshot/releases/latest").GetAwaiter().GetResult();
            return (int)answer.StatusCode >= 500;
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static void Run()
    {
        var catalog = typeof(SettingsWindow).Assembly.GetType("Markshot.UI.ToolbarCatalog")!;
        var order = catalog.GetMethod("Order")!;
        var tools = (string[])catalog.GetField("Tools")!.GetValue(null)!;
        var o = (System.Collections.Generic.List<string>)order.Invoke(null, new object?[] { "Eraser,Shape,Bogus", tools })!;
        Check(o.First() == "Eraser" && o.Count == tools.Length && !o.Contains("Bogus"), "saved order kept, unknown dropped, missing added: " + string.Join(",", o));

        // ---- the toolbar applies order / hidden to tools and actions, each tool as one block
        var bar = new AnnotationToolbar();
        var win = new Window { Content = bar, SizeToContent = SizeToContent.WidthAndHeight, Left = -4000, Top = -4000, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        win.Show();
        bar.ApplyToolOrder("Text,Shape,Pen,Arrow,Number,Mosaic,Eraser", "Number");
        var items = (Panel)bar.FindName("ToolItems");
        var keys = items.Children.OfType<FrameworkElement>().Select(c => c.Tag as string).ToList();
        var numberHidden = items.Children.OfType<FrameworkElement>().First(c => (string?)c.Tag == "Number").Visibility == Visibility.Collapsed;
        var shapeBlock = items.Children.OfType<Panel>().First(c => (string?)c.Tag == "Shape");
        Check(keys.First() == "Text" && numberHidden && shapeBlock.Children.Count == 2,
            "tool order applied; hidden tool collapsed; button + menu arrow move as one block");
        var apply = typeof(AnnotationToolbar).GetMethod("ApplyActionOrder", BindingFlags.NonPublic | BindingFlags.Static)!;
        var actions = (Panel)bar.FindName("CaptureActions");
        apply.Invoke(null, new object?[] { actions, "Copy,Save,Pin,Close,Ocr,Translate", "Ocr", (string[])catalog.GetField("CaptureActions")!.GetValue(null)! });
        var actionKeys = actions.Children.OfType<FrameworkElement>().Select(c => c.Tag as string).ToList();
        Check(actionKeys[0] == "Scroll" && actionKeys[1] == "Record" && actionKeys[2] == "Copy" && actions.Children.OfType<FrameworkElement>().First(c => (string?)c.Tag == "Ocr").Visibility == Visibility.Collapsed,
            "action order applied, hidden action collapsed: " + string.Join(",", actionKeys));
        win.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)bar.ActualWidth, (int)bar.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(bar);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create("toolbar_reordered.png")) enc.Save(f);
        win.Close();

        // ---- settings rows: icons, click toggles, read back
        var config = new AppConfig { CaptureToolOrder = "Pen,Shape,Arrow,Text,Number,Mosaic,Eraser", CaptureToolHidden = "Eraser", PinActionHidden = "" };
        var settings = new SettingsWindow(config) { Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false };
        settings.Show();
        settings.ShowPage("General");
        Pump(300);
        var row = (Panel)settings.FindName("CaptureToolsRow");
        Check(row.Children.Count == 7 && (string?)((FrameworkElement)row.Children[0]).Tag == "Pen", "settings row shows the capture tools in saved order");
        var shape = row.Children.OfType<Border>().First(b => (string?)b.Tag == "Shape");
        shape.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        var read = typeof(SettingsWindow).GetMethod("ReadToolbarRow", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var (o2, h2) = ((string, string))read.Invoke(settings, new object[] { row })!;
        Check(h2.Split(',').OrderBy(x => x).SequenceEqual(new[] { "Eraser", "Shape" }) && o2.StartsWith("Pen,Shape"), $"click hides a button (hidden: {h2})");
        shape.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        (_, h2) = ((string, string))read.Invoke(settings, new object[] { row })!;
        Check(h2 == "Eraser", "click again shows it");
        settings.Close();

        // ---- update: the GitHub source is built in; the custom folder is optional
        Check(UpdateService.EffectivePath(new AppConfig()) == "", "no update path to fill in by default");
        var dir = Path.Combine(Path.GetTempPath(), "markshot-update-test2");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        File.WriteAllText(Path.Combine(dir, "Markshot-Setup-9.9.99.exe"), "");
        var found = UpdateService.FindNewestAsync(dir).GetAwaiter().GetResult();
        Check(found?.Version == "9.9.99", $"custom folder is checked as well ({found?.Version})");
        // the release page (no API quota): finds the published installer when the network is there
        var page = (System.Threading.Tasks.Task)typeof(UpdateService).GetMethod("FindNewerOnPageAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { "well74741", "Markshot", CancellationToken.None })!;
        ((System.Threading.Tasks.Task)page).Wait();
        var result = page.GetType().GetProperty("Result")!.GetValue(page)!;
        var checkedOk = (bool)result.GetType().GetField("Item1")!.GetValue(result)!;
        Console.WriteLine("   release page checked: " + checkedOk);
        // Offline or GitHub in trouble: the page cannot be read, which the update check handles by trying the API.
        if (!checkedOk) Console.WriteLine("   the release page could not be read this time (network / GitHub)");
        Check(checkedOk || NetworkTrouble(), "the release page names the latest installer (no API quota needed)");
        var latest = UpdateService.LatestOnPageAsync("well74741", "Markshot", CancellationToken.None).GetAwaiter().GetResult();
        Check(latest.Package != null || NetworkTrouble(), "the release page has an installer");
        if (latest.Package is { } found2)
        {
            // The link must really download (it once lacked the owner / repository: 404).
            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Markshot-test");
            using var head = http.Send(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, found2.Path));
            Console.WriteLine("   " + found2.Path + " -> " + (int)head.StatusCode);
            // A wrong link answers 404; a 5xx is GitHub's own trouble at the moment and says nothing about the link.
            var status = (int)head.StatusCode;
            if (status >= 500) Console.WriteLine("   GitHub answered " + status + ": the link could not be checked this time");
            Check(found2.Path.StartsWith("https://github.com/well74741/Markshot/releases/download/") && status is < 400 or >= 500, $"the installer link from the release page is not a dead link ({status})");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
