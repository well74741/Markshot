using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

static class Batch9Test
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

    public static void Run()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing a test window must not stop the dispatcher
        // ---- settings: the bar buttons have their own page
        var settings = new SettingsWindow(new AppConfig()) { Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false };
        settings.Show();
        settings.ShowPage("Toolbar");
        settings.UpdateLayout();
        var page = (FrameworkElement)settings.FindName("PageToolbar");
        var general = (FrameworkElement)settings.FindName("PageGeneral");
        var row = (FrameworkElement)settings.FindName("CaptureToolsRow");
        Check(page.IsVisible && !general.IsVisible && IsDescendant(row, page), "批注栏 page holds the button rows");
        var root = (FrameworkElement)settings.Content;
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create("settings_toolbar_page.png")) enc.Save(f);
        settings.Close();

        // ---- long-press on a real toolbar button (which captures the mouse itself) picks it up and drops it on release
        var reorderType = typeof(ReorderBehavior);
        var leftDown = reorderType.GetField("LeftButtonDown", BindingFlags.NonPublic | BindingFlags.Static)!;
        var down = true;
        leftDown.SetValue(null, new Func<bool>(() => down));

        var bar = new AnnotationToolbar();
        var win = new Window { Content = bar, SizeToContent = SizeToContent.WidthAndHeight, Left = -4000, Top = -4000, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        win.Show();
        bar.ApplyToolOrder("Shape,Pen,Arrow,Text,Number,Mosaic,Eraser", "");
        win.UpdateLayout();
        var items = (Panel)bar.FindName("ToolItems");
        var behavior = (ReorderBehavior)typeof(AnnotationToolbar).GetField("_toolsReorder", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bar)!;
        var group = items.Children.OfType<FrameworkElement>().First(c => (string?)c.Tag == "Pen");
        var button = (ButtonBase)bar.FindName("ToolPen");
        button.CaptureMouse(); // what a pressed button does
        reorderType.GetField("_pressed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(behavior, group);
        reorderType.GetField("_pressPoint", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(behavior, Mouse.GetPosition(items));
        reorderType.GetMethod("BeginDrag", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(behavior, null);
        Console.WriteLine($"debug: win {win.ActualWidth} group {group.ActualWidth} vis {group.IsVisible} children {items.Children.Count} captured {Mouse.Captured}");
        Check(behavior.IsDragging && items.IsMouseCaptured, "picked up although the button had the mouse captured");
        var rotate = (RotateTransform)((TransformGroup)group.RenderTransform).Children[0];
        Check(rotate.HasAnimatedProperties, "picked-up item wiggles");
        down = false;
        items.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        Check(!behavior.IsDragging && !rotate.HasAnimatedProperties && rotate.Angle == 0 && group.Opacity == 1, "release drops it and stops the wiggle");
        // ---- text tool: 文字 / 水印 switch in its settings row; watermark: the on / off switch
        bar.SetTool(Markshot.Annotations.AnnotationTool.Text, notify: false);
        var textVariants = (FrameworkElement)bar.FindName("TextVariants");
        var watermarkGroup = (FrameworkElement)bar.FindName("WatermarkGroup");
        Check(textVariants.Visibility == Visibility.Visible && watermarkGroup.Visibility != Visibility.Visible, "text tool shows 文字 / 水印");
        var toggled = (bool?)null;
        bar.WatermarkToggled += on => toggled = on;
        ((System.Windows.Controls.RadioButton)bar.FindName("VariantWatermark")).IsChecked = true;
        Check(watermarkGroup.Visibility == Visibility.Visible && textVariants.Visibility == Visibility.Visible, "watermark shows its on / off switch");
        var toggle = (System.Windows.Controls.Primitives.ToggleButton)bar.FindName("WatermarkToggle");
        bar.SetWatermarkOn(true);
        toggle.IsChecked = false;
        toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(toggled == false && (string)toggle.Content == "关闭", "switching it off asks to remove the watermark");
        win.Close();
        leftDown.SetValue(null, new Func<bool>(() => Mouse.LeftButton == MouseButtonState.Pressed));

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }

    static bool IsDescendant(DependencyObject child, DependencyObject parent)
    {
        for (var e = child; e != null; e = VisualTreeHelper.GetParent(e) ?? LogicalTreeHelper.GetParent(e))
        {
            if (ReferenceEquals(e, parent)) return true;
        }

        return false;
    }
}
