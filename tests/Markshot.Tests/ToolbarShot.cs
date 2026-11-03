using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markshot.Annotations;
using Markshot.UI;

static class ToolbarShot
{
    public static void Run()
    {
        var stack = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(90, 100, 110)) };
        foreach (var tool in new[] { AnnotationTool.Text, AnnotationTool.Mosaic, AnnotationTool.MosaicBrush, AnnotationTool.Watermark })
        {
            var bar = new AnnotationToolbar { Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left };
            bar.SetTool(tool);
            bar.SetTranslateEnabled(tool == AnnotationTool.Text);
            stack.Children.Add(bar);
        }

        var win = new Window
        {
            Content = stack, SizeToContent = SizeToContent.WidthAndHeight, Left = -4000, Top = -4000,
            ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false
        };
        win.Show();
        stack.UpdateLayout();
        // Let the 160 ms pop-in / cross-fade animations finish before the shot.
        var until = DateTime.Now.AddMilliseconds(500);
        while (DateTime.Now < until)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            System.Threading.Thread.Sleep(15);
        }
        foreach (AnnotationToolbar b in stack.Children)
            foreach (var n in new[] { "VariantMosaic", "VariantMosaicBrush" })
                Console.Write($"{n}={((RadioButton)b.FindName(n)).IsChecked} ");
        Console.WriteLine();
        var bmp = new RenderTargetBitmap((int)stack.ActualWidth, (int)stack.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(stack);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create("toolbar_modes.png")) enc.Save(f);
        var first = (AnnotationToolbar)stack.Children[0];
        var toggle = (System.Windows.Controls.Primitives.ToggleButton)first.FindName("TranslateToggle");
        Console.WriteLine($"translate IsChecked={toggle.IsChecked} Foreground={toggle.Foreground} local={toggle.ReadLocalValue(Control.ForegroundProperty)}");
        win.Close();
        Console.WriteLine("wrote toolbar_modes.png");
    }
}
