using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

static class Batch7Test
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
        // ---- mouse buttons as hotkeys
        Check(HotkeyGesture.TryParse("鼠标中键", out var middle, out _) && middle.IsMouse && middle.VirtualKey == HotkeyGesture.VkMiddle,
            "middle mouse button parses as a hotkey");
        Check(HotkeyGesture.TryParse("Ctrl+鼠标侧键1", out var side, out _) && side.IsMouse && side.VirtualKey == HotkeyGesture.VkX1 &&
              side.Modifiers == 0x0002, "Ctrl + side button parses");
        Check(HotkeyGesture.TryParse("XButton2", out var x2, out _) && x2.VirtualKey == HotkeyGesture.VkX2, "English names work too");
        Check(!HotkeyGesture.TryParse("F1+鼠标中键", out _, out _), "a key and a mouse button together are rejected");
        Check(HotkeyGesture.FormatMouse(MouseButton.XButton2, ModifierKeys.Alt) == "Alt+鼠标侧键2", "settings box text for a side button");
        Check(HotkeyGesture.TryParse("Ctrl+Alt+Z", out var key, out _) && !key.IsMouse, "keyboard hotkeys unchanged");

        // ---- settings: update button on its own line, coffee drawer, save prompt option
        var settings = new SettingsWindow(new AppConfig()) { Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false };
        settings.Show();
        Pump(200);
        var update = (Button)settings.FindName("UpdateButton");
        update.Content = "更新 0.8.99";
        update.Visibility = Visibility.Visible;
        settings.UpdateLayout();
        var nav = update.Parent as FrameworkElement;
        Check(update.ActualWidth > 0 && update.TranslatePoint(new Point(update.ActualWidth, 0), nav).X <= nav!.ActualWidth,
            $"update button fits the column ({update.ActualWidth:F0} of {nav.ActualWidth:F0})");
        Check(settings.FindName("AskOpenFolderCheck") is CheckBox, "setting: ask to open the folder after saving");
        var drawer = (Border)settings.FindName("DrawerPanel");
        var qr = (Image)settings.FindName("DrawerQr");
        Check(drawer.ActualHeight < 1 && qr.Source != null, "coffee drawer starts closed, QR image loads");
        drawer.Height = 144;
        ((TextBlock)settings.FindName("DrawerHint")).Opacity = 1;
        settings.UpdateLayout();
        var content = (FrameworkElement)settings.Content;
        var bmp = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create("settings_drawer.png")) enc.Save(f);
        Console.WriteLine("   wrote settings_drawer.png");
        var hint = typeof(SettingsWindow).GetMethod("ShowDrawerHint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var cup = (FrameworkElement)settings.FindName("DrawerCoffee");
        hint.Invoke(settings, new object?[] { false, 144.0 });
        Pump(400);
        Check(cup.Opacity < 0.05, "QR code out: no cup");
        hint.Invoke(settings, new object?[] { false, 0.0 }); // put away (height still animating)
        Pump(400);
        hint.Invoke(settings, new object?[] { false, null }); // the mouse leaves afterwards
        Pump(400);
        Check(cup.Opacity > 0.95, "QR code put away: the coffee cup is back");
        // the QR code turns over to the WeChat one while hovered (drawer out), back on leaving
        hint.Invoke(settings, new object?[] { false, 144.0 });
        var turn = typeof(SettingsWindow).GetMethod("TurnQr", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        turn.Invoke(settings, new object[] { true });
        var qrImage = (Image)settings.FindName("DrawerQr");
        // The turn takes ~240 ms of animation: wait for its end, not a fixed time (a busy machine renders later).
        for (var i = 0; i < 60 && !(settings.ShowsSecondQr && ((ScaleTransform)settings.FindName("DrawerQrScale")).ScaleX == 1); i++) Pump(50);
        Check(settings.ShowsSecondQr && ((BitmapImage)qrImage.Source).UriSource.ToString().Contains("wechat") && ((ScaleTransform)settings.FindName("DrawerQrScale")).ScaleX == 1,
            "hovered: turned over to the WeChat code (upright again)");
        turn.Invoke(settings, new object[] { false });
        turn.Invoke(settings, new object[] { true });
        turn.Invoke(settings, new object[] { false }); // in and out quickly: ends on the first code
        Pump(700);
        Check(!settings.ShowsSecondQr && !((BitmapImage)qrImage.Source).UriSource.ToString().Contains("wechat"), "left: back to the first code");
        // the global hotkeys rest only while a hotkey box takes keys
        var recording = new List<bool>();
        settings.RecordingHotkey += r => recording.Add(r);
        settings.Activate();
        settings.ShowPage("Hotkeys"); // 样式 opens first since 0.8.53
        Pump(50);
        ((TextBox)settings.FindName("CaptureHotkeyBox")).Focus();
        Pump(50);
        Keyboard.ClearFocus();
        Pump(50);
        Check(recording.Count >= 2 && recording[0] && !recording[^1], $"hotkeys rest while a hotkey box has the keyboard, come back after ({string.Join(",", recording)})");
        Check(settings.FindName("QuickDoubleClickCheck") is CheckBox { IsChecked: false }, "setting: 快速双击复制并关闭, off by default");
        var escCheck = (CheckBox)settings.FindName("EscExitsCheck");
        escCheck.ApplyTemplate();
        Check(escCheck.Template.FindName("Track", escCheck) is Border { Width: 28 }, "settings check boxes are small pill switches");
        var savedEvents = 0;
        settings.SavedChanges += () => savedEvents++;
        typeof(SettingsWindow).GetMethod("Save_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(settings, new object[] { settings, new RoutedEventArgs() });
        Pump(100);
        Check(settings.IsVisible && settings.Saved && savedEvents == 1 && settings.StatusLine.Contains("已保存"), "保存: applied, the window stays open, a quiet notice in the footer");
        settings.Close();

        // ---- the saved prompt
        var promptType = typeof(SettingsWindow).Assembly.GetType("Markshot.UI.SavedPromptWindow")!;
        var prompt = (Window)Activator.CreateInstance(promptType, @"C:\Users\someone\Pictures\Markshot\Markshot_20261102_120000.png")!;
        prompt.Left = -4000;
        prompt.Top = -4000;
        prompt.ShowActivated = false;
        prompt.Show();
        Pump(250);
        prompt.UpdateLayout();
        var promptContent = (FrameworkElement)prompt.Content;
        promptContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        promptContent.Arrange(new Rect(promptContent.DesiredSize));
        promptContent.UpdateLayout();
        var pbmp = new RenderTargetBitmap((int)promptContent.ActualWidth + 24, (int)promptContent.ActualHeight + 24, 96, 96, PixelFormats.Pbgra32);
        pbmp.Render(promptContent);
        var penc = new PngBitmapEncoder();
        penc.Frames.Add(BitmapFrame.Create(pbmp));
        using (var f = File.Create("saved_prompt.png")) penc.Save(f);
        Check(promptContent.ActualWidth > 200, "saved prompt shows");
        prompt.Close();

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
