using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.58: version notes on hover, the settings footer, the ` key, the floating row, the recorder bar.</summary>
static class Round58Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static string Source(string file) => File.ReadAllText(Repo.File(file));

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- settings: the version shows the latest notes on hover; 恢复默认设置 on the far left; a bigger cup
        {
            var settings = new SettingsWindow(new AppConfig()) { Left = -4000, Top = -4000, ShowActivated = false };
            settings.Show();
            Pump(200);
            settings.GetType().GetMethod("VersionText_MouseEnter", Any)!.Invoke(settings, new object?[] { settings, null });
            Pump(100);
            var popup = (Popup?)settings.GetType().GetProperty("NotesPopup", Any)!.GetValue(settings);
            Check(popup is { IsOpen: true }, "the version shows the latest release notes on hover");
            var reset = (Button)settings.FindName("ResetDefaultsButton");
            Check(DockPanel.GetDock(reset) == Dock.Left && reset.Parent is DockPanel, "恢复默认设置 sits on the far left of the footer");
            Check(((FrameworkElement)settings.FindName("DrawerCoffee")).Width == 32, "the coffee cup is twice as big");
            settings.Close();
        }

        // ---- the key above Tab
        {
            Check(HotkeyGesture.TryFormat(Key.Oem3, ModifierKeys.Control, out var text) && text == "Ctrl+`", "` can be a hotkey (Ctrl+`)");
            Check(HotkeyGesture.TryParse("Ctrl+`", out var gesture, out _) && gesture.VirtualKey == 0xC0, "Ctrl+` registers as the key above Tab");
            Check(ToolHotkeys.Name(Key.Oem3) == "`" && Source("UI/SettingsWindow.xaml.cs").Contains("value[0] == '`'"), "` as a tool / pin key");
        }

        // ---- the floating row: the annotation bar's buttons and plate; a fixed room (no flicker when the row changes)
        {
            var config = new AppConfig();
            var button = new FloatButtonWindow(config, _ => { }, () => { }, () => { }) { FullscreenOverride = () => false };
            button.Show();
            Pump(250);
            var items = (StackPanel)button.GetType().GetField("_items", Any)!.GetValue(button)!;
            Check(items.Children.OfType<Button>().All(b => b.Style == (Style)Application.Current.FindResource("IconToolButton")), "the row's buttons are the annotation bar's");
            var handle = new System.Windows.Interop.WindowInteropHelper(button).Handle;
            var before = (button.ActualWidth, button.Left);
            config.FloatActionHidden = "Record,Ocr,Editor";
            button.BuildItems();
            button.ApplyLook();
            Pump(100);
            Check(Math.Abs(button.ActualWidth - before.ActualWidth) < 0.5 && Math.Abs(button.Left - before.Left) < 0.5, "hiding row buttons does not move or resize the window");
            button.Close();
        }

        // ---- the recorder bar
        {
            var xaml = Source("UI/Recording/RecordBarWindow.xaml");
            Check(xaml.Contains("x:Name=\"BarShadeClip\"") && xaml.Contains("CornerRadius=\"9\"") && !xaml.Contains("Width=\"17\" Height=\"17\""),
                "the recorder bar has the annotation bar's plate, inner shade and icon size");
            var code = Source("UI/Recording/RecordBarWindow.xaml.cs");
            Check(code.Contains("Reveal(PreviewRows, preview)") && code.Contains("Motion.PopOut(Rows"), "its parts come and go with quick transitions");
            Check(code.Contains("(\"全屏\", _controller.IsFullScreen"), "the size menu ends with 全屏");
            Check(code.Contains("if (!_sizeTyped)"), "after a preset, dragging the frame keeps the new size (no old typed size put back)");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
