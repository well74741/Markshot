using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markshot.Annotations;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // A plain Application: constructing Markshot.App would queue its real
        // OnStartup (tray icon, global hotkeys, user config) on the dispatcher.
        // Never the user's settings: a config file of the test run's own.
        var configDir = Path.Combine(Path.GetTempPath(), "markshot-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(configDir);
        Markshot.Core.AppConfig.PathOverride = Path.Combine(configDir, "config.json");
        // A settings window closed with changes would wait for an answer: tests answer 不保存 unless they set their own.
        typeof(Markshot.UI.SettingsWindow).GetField("AskAll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, new Func<int>(() => 1));
        var app = new Application();
        var xaml = File.ReadAllText(Repo.File("App.xaml"));
        var start = xaml.IndexOf("<Application.Resources>") + "<Application.Resources>".Length;
        var end = xaml.IndexOf("</Application.Resources>");
        var dict = "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                   "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                   "xmlns:core=\"clr-namespace:Markshot.Core;assembly=Markshot\">" + xaml[start..end] + "</ResourceDictionary>";
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dict);
        var mode = args.Length > 0 ? args[0] : "text";
        if (mode == "text") TextAlign();
        if (mode == "editor") EditorTest.Run();
        if (mode == "translate") TranslateTest.Run();
        if (mode == "settings") SettingsShot.Run();
        if (mode == "batch1") Batch1Test.Run();
        if (mode == "toolbar") ToolbarShot.Run();
        if (mode == "batch2") Batch2Test.Run();
        if (mode == "translate-real") TranslateTest.RunReal();
        if (mode == "batch3") Batch3Test.Run();
        if (mode == "batch4") Batch4Test.Run();
        if (mode == "batch5") Batch5Test.Run();
        if (mode == "batch6") Batch6Test.Run();
        if (mode == "batch7") Batch7Test.Run();
        if (mode == "batch8") Batch8Test.Run();
        if (mode == "batch9") Batch9Test.Run();
        if (mode == "reccore") RecordCoreTest.Run();
        if (mode == "recflow") RecorderFlowTest.Run();
        if (mode == "picker") PickerProbe.Run();
        if (mode == "theme") ThemeShot.Run();
        if (mode == "round48") Round48Test.Run();
        if (mode == "round53") Round53Test.Run();
        if (mode == "round55") Round55Test.Run();
        if (mode == "round56") Round56Test.Run();
        if (mode == "round57") Round57Test.Run();
        if (mode == "round58") Round58Test.Run();
        if (mode == "round59") Round59Test.Run();
        if (mode == "menu") MenuProbe.Run();
        if (mode == "support") SupportTest.Run();
        if (mode == "history") HistoryTest.Run();
        if (mode == "stitch") StitchTest.Run();
        if (mode == "imageops") ImageOpsTest.Run();
        if (mode == "imageeditor") ImageEditorTest.Run();
        if (mode == "annotate") AnnotateTest.Run();
        if (mode == "capture") CaptureTest.Run();
        if (mode == "hover") HoverProbe.Run();
        if (mode == "copyprobe") CopyProbe.Run();
        if (mode == "fontprobe") FontProbe.Run(args[1], args.Length > 2 && args[2] == "en");
        if (mode == "files") FilesTest.Run();
        if (mode == "deskprobe") DeskProbe.Run();
        if (mode == "hoverhost") HoverProbe.Host(args[1]);
        if (mode == "firstopen") FirstOpenProbe.Run();
        if (mode == "cornerprobe") CornerProbe.Run();
    }

    public static (int l, int t, int r, int b) Bbox(BitmapSource bmp)
    {
        var w = bmp.PixelWidth; var h = bmp.PixelHeight;
        var px = new byte[w * h * 4]; bmp.CopyPixels(px, w * 4, 0);
        int l = w, t = h, r = -1, b = -1;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            if (px[(y * w + x) * 4 + 3] > 60) { l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y); }
        return (l, t, r, b);
    }

    static void TextAlign()
    {
        foreach (var size in new[] { 14.0, 20.0, 36.0, 60.0 })
        foreach (var bold in new[] { false, true })
        {
            var text = "Hg中文 Test\n第二行 jy";
            var ann = new TextAnnotation { Position = new Point(40, 30), Text = text, FontSize = size, Bold = bold, Color = Colors.Blue };
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) ann.Draw(dc);
            var a = new RenderTargetBitmap(800, 400, 96, 96, PixelFormats.Pbgra32); a.Render(dv);

            var canvas = new Canvas { Width = 800, Height = 400 };
            var box = new TextBox { Style = (Style)Application.Current.Resources["InlineTextBox"], Text = text.Replace("\n", "\r\n") };
            canvas.Children.Add(box);
            box.FontFamily = new FontFamily(TextAnnotation.FontFamilyName);
            box.FontSize = size; box.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
            box.Foreground = Brushes.Red;
            TextBlock.SetLineHeight(box, ann.LineHeight);
            TextBlock.SetLineStackingStrategy(box, LineStackingStrategy.BlockLineHeight);
            var m = ann.MeasureText();
            box.Width = m.Width + size + 4; box.Height = m.Height + 2;
            Canvas.SetLeft(box, 40); Canvas.SetTop(box, 30);
            canvas.Measure(new Size(800, 400)); canvas.Arrange(new Rect(0, 0, 800, 400)); canvas.UpdateLayout();
            var bb = new RenderTargetBitmap(800, 400, 96, 96, PixelFormats.Pbgra32); bb.Render(canvas);
            var ra = Bbox(a); var rb = Bbox(bb);
            Console.WriteLine($"size={size} bold={bold} ann={ra} box={rb} dL={rb.l - ra.l} dT={rb.t - ra.t} dR={rb.r - ra.r} dB={rb.b - ra.b} measure={m}");
            if (size == 36 && !bold)
            {
                var dv2 = new DrawingVisual();
                using (var dc = dv2.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0,0,800,400)); dc.DrawImage(a, new Rect(0,0,800,400)); dc.PushOpacity(0.5); dc.DrawImage(bb, new Rect(0,0,800,400)); dc.Pop(); }
                var o = new RenderTargetBitmap(800, 400, 96, 96, PixelFormats.Pbgra32); o.Render(dv2);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(o));
                using var f = File.Create("text_overlay.png"); enc.Save(f);
            }
        }
    }
}
