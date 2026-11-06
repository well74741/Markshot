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
        settings.ShowPage("General");
        settings.UpdateLayout();
        var shot2 = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        shot2.Render(root);
        var enc2 = new PngBitmapEncoder();
        enc2.Frames.Add(BitmapFrame.Create(shot2));
        using (var f2 = File.Create("settings_general_page.png")) enc2.Save(f2);
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

        // ---- pins: restored / from the editor the light sweeps across; closing shrinks it towards its middle
        {
            var picture = BitmapSource.Create(160, 100, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)200, 160 * 100 * 4).ToArray(), 160 * 4);
            picture.Freeze();
            var swept = new PinWindow(picture, -4000, -4000) { Sweep = true, Left = -4000, Top = -4000, ShowActivated = false };
            var host = (Grid)swept.FindName("ImageHost");
            var before = host.Children.Count;
            swept.Show();
            Pump(200);
            Check(host.Children.Count == before + 1 && host.Children[^1] is Canvas { Children.Count: 1 }, "a restored pin: the light sweeps across it");
            Pump(400);
            Check(host.Children.Count == before, "… once");
            swept.Close();
            Pump(80);
            var pinRoot = (FrameworkElement)swept.FindName("RootGrid");
            var shrink = pinRoot.RenderTransform as ScaleTransform;
            Check(shrink != null && shrink.ScaleX < 0.98 && shrink.ScaleX > 0.5 && pinRoot.RenderTransformOrigin.X == 0.5 && pinRoot.RenderTransformOrigin.Y > 0.6,
                $"closing a pin: it shrinks towards a point a little below its middle while it fades ({shrink?.ScaleX:0.00}, {pinRoot.RenderTransformOrigin})");
            Pump(250);
            Check(!swept.IsVisible, "… and is gone");
            var zoomed = new PinWindow(picture, -4000, -4000) { Sweep = true, Left = -4000, Top = -4000, ShowActivated = false };
            zoomed.RestoreView(0.5, 1);
            double firstOpacity = -1;
            zoomed.SourceInitialized += (_, _) => firstOpacity = zoomed.Opacity;
            zoomed.Show();
            Check(firstOpacity == 0, "a restored pin starts invisible before its first frame (it fades in, no flash)");
            Pump(60);
            Check(Math.Abs(((FrameworkElement)zoomed.FindName("ImageHost")).ActualWidth - 80) < 1.5, "… and opens at its size (zoom set before it shows, no jump)");
            zoomed.CloseImmediately();
            var plain = new PinWindow(picture, -4000, -4000) { Left = -4000, Top = -4000, ShowActivated = false };
            var plainHost = (Grid)plain.FindName("ImageHost");
            var plainBefore = plainHost.Children.Count;
            plain.Show();
            Pump(200);
            Check(plainHost.Children.Count == plainBefore, "a new pin from the clipboard: no sweep");
            Check(typeof(PinWindow).GetField("_crosshair", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(plain)?.GetType().Name == "PinCrosshair" &&
                  typeof(PinWindow).Assembly.GetType("Markshot.UI.PinMagnifier") == null, "Alt over a pin: 辅助线 (no magnifier card any more)");
            plain.Close();
            Pump(250);
        }

        // ---- the ▾ beside the tools hidden (default): resting on a tool shows its styles
        {
            var config = Markshot.App.Config;
            var chevronsBefore = config.HideToolChevrons;
            Check(new AppConfig().HideToolChevrons, "setting: 隐藏收纳按钮, on by default");
            config.HideToolChevrons = true;
            var hoverBar = new AnnotationToolbar();
            var hoverWin = new Window { Content = hoverBar, SizeToContent = SizeToContent.WidthAndHeight, Left = -4000, Top = -4000, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
            hoverWin.Show();
            hoverBar.SetMode(AnnotationToolbarMode.Capture);
            Pump(100);
            var penChevron = (FrameworkElement)hoverBar.FindName("PenChevron");
            Check(penChevron.Visibility == Visibility.Collapsed && ((FrameworkElement)hoverBar.FindName("EraserChevron")).Visibility == Visibility.Collapsed, "… the ▾ buttons are gone");
            var penTool = (ButtonBase)hoverBar.FindName("ToolPen");
            typeof(AnnotationToolbar).GetMethod("OpenHoverMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(hoverBar, new object[] { penTool });
            Pump(100);
            var popup = (Popup)typeof(AnnotationToolbar).GetField("_hoverPopup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(hoverBar)!;
            var card = (Border)((Grid)popup.Child).Children[0];
            var hoverItems = ((StackPanel)card.Child).Children.OfType<Button>().ToList();
            Check(hoverBar.IsHoverMenuOpen && popup.StaysOpen && popup.Placement == PlacementMode.Custom &&
                  hoverItems.Select(i => ((TextBlock)i.Content).Text).SequenceEqual(new[] { "画笔", "荧光笔" }),
                "… resting on the pen shows 画笔 / 荧光笔 under it (without taking the mouse)");
            var placed = popup.CustomPopupPlacementCallback(new Size(80, 70), new Size(30, 30), new Point())[0].Point;
            Check(Math.Abs(placed.X - (30 - 80) / 2.0) < 0.01, "… centred under the button");
            Check(card.ActualWidth < 110 && hoverItems.All(i => i.HorizontalContentAlignment == HorizontalAlignment.Center && ((TextBlock)i.Content).TextAlignment == TextAlignment.Center),
                $"… a narrow list ({card.ActualWidth:0} DIP) with its words centred");
            hoverItems[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(50);
            Check(hoverBar.Tool == Markshot.Annotations.AnnotationTool.Highlighter && !hoverBar.IsHoverMenuOpen, "… picking one takes that tool and closes the list");
            Check(((FrameworkElement)hoverBar.FindName("ScrollChevron")).Visibility == Visibility.Collapsed, "… the 长截图 ▾ is hidden too (its directions on hover)");
            typeof(AnnotationToolbar).GetMethod("OpenHoverMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(hoverBar, new object[] { (ButtonBase)hoverBar.FindName("ScrollButton") });
            var scrollPopup = (Popup)typeof(AnnotationToolbar).GetField("_hoverPopup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(hoverBar)!;
            var scrollItems = ((StackPanel)((Border)((Grid)scrollPopup.Child).Children[0]).Child).Children.OfType<Button>().Select(i => ((TextBlock)i.Content).Text).ToList();
            Check(scrollItems.SequenceEqual(new[] { "竖向长截图", "横向长截图" }), "… resting on 长截图 shows 竖向 / 横向");
            hoverBar.CloseHoverMenu();
            config.HideToolChevrons = false;
            hoverBar.ApplyToolOrderFromConfig();
            Check(penChevron.Visibility == Visibility.Visible, "setting off: the ▾ buttons are back");
            config.HideToolChevrons = chevronsBefore;
            hoverWin.Close();

            var ocrIcon = typeof(PinWindow).Assembly.GetType("Markshot.UI.ToolbarCatalog")!.GetMethod("Icon")!.Invoke(null, new object[] { "Ocr" });
            Check(ocrIcon is Border { OpacityMask: DrawingBrush }, "文字识别's icon is a mask like the others (it shook as a stretched path while the bar popped in)");
            var palette = (Color[])typeof(AnnotationToolbar).GetField("Palette", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var red = palette[0];
            var saturation = (Math.Max(red.R, Math.Max(red.G, red.B)) - Math.Min(red.R, Math.Min(red.G, red.B))) / (double)Math.Max(red.R, Math.Max(red.G, red.B));
            Check(red.R == 255 && saturation > 0.85, $"the red preset is a saturated red (#{red.R:X2}{red.G:X2}{red.B:X2})");
        }

        // ---- the eraser's 软边: full in the middle of the stroke, fading towards its edge
        {
            var block = new Markshot.Annotations.RectAnnotation { Rect = new Rect(10, 10, 100, 100), Fill = true, Color = Colors.Black, Thickness = 2 };
            block.AddEraseStroke(new[] { new Point(0, 60), new Point(120, 60) }, 40, 1, 1);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                Markshot.Annotations.AnnotationEditor.DrawItem(dc, block);
            }

            var softShot = new RenderTargetBitmap(120, 120, 96, 96, PixelFormats.Pbgra32);
            softShot.Render(visual);
            byte Alpha(int x, int y) { var px = new byte[4]; softShot.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0); return px[3]; }
            var middle = Alpha(60, 60);
            var halfway = Alpha(60, 72);
            var outside = Alpha(60, 30);
            Check(middle < 30 && halfway > middle + 40 && halfway < outside - 40 && outside > 240,
                $"soft eraser: erased in the middle, partly towards the edge, untouched outside (alpha {middle} / {halfway} / {outside})");
            var eraserBar = new AnnotationToolbar();
            eraserBar.SetTool(Markshot.Annotations.AnnotationTool.Eraser);
            Check(((FrameworkElement)eraserBar.FindName("SoftnessGroup")).Visibility == Visibility.Visible, "the eraser has a 软边 slider");
        }

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
