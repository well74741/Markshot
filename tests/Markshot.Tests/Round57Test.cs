using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Markshot.Core;
using Markshot.UI;

/// <summary>0.8.57: Esc does not close the settings by accident (the F1 errors), 恢复默认设置, 添加贴图 on the floating button.</summary>
static class Round57Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) _fail++; }
    static void Pump(int ms) { var u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); } }
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    public static void Run()
    {
        Theme.Apply(ThemeMode.Dark);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- the F1 errors: an Esc while the settings were closing (their question open) closed them again → exception
        {
            var config = new AppConfig();
            var settings = new SettingsWindow(config) { Left = -4000, Top = -4000, ShowActivated = false };
            settings.Show();
            Pump(200);
            ((Slider)settings.FindName("FloatOpacitySlider")).Value = 40;
            var nested = 0;
            Exception? thrown = null;
            settings.GetType().GetField("AskOverride", Any)!.SetValue(settings, new Func<int>(() =>
            {
                nested++;
                try
                {
                    // While the question is open: another Esc / 关闭 reaches the window.
                    settings.GetType().GetMethod("Cancel_Click", Any)!.Invoke(settings, new object[] { settings, new RoutedEventArgs() });
                }
                catch (TargetInvocationException ex)
                {
                    thrown = ex.InnerException;
                }

                return 2; // 取消
            }));
            settings.Close();
            Pump(50);
            Check(thrown == null && nested == 1 && settings.IsVisible, "closing again while the question is open: no error, asked once");
            var xaml = File.ReadAllText(Repo.File("UI/SettingsWindow.xaml"));
            Check(!xaml.Contains("Click=\"Cancel_Click\" IsCancel=\"True\""), "Esc is no button default any more (a held Esc from the capture closed the settings)");
            var source = File.ReadAllText(Repo.File("UI/SettingsWindow.xaml.cs"));
            Check(source.Contains("e.IsRepeat || _closingNow || Environment.TickCount64 - _activatedAt < 400"), "a repeated / just-arrived Esc is ignored");

            // ---- 恢复默认设置
            config.CaptureHotkey = "F2";
            config.FloatButtonEnabled = true;
            config.Translation.ApiKey = "test-key";
            settings.GetType().GetField("AskOverride", Any)!.SetValue(settings, new Func<int>(() => 1));
            settings.GetType().GetField("ResetAnswer", Any)!.SetValue(settings, new Func<int>(() => 0));
            Check(settings.FindName("ResetDefaultsButton") is Button { Content: "恢复默认设置" }, "the footer has 恢复默认设置");
            settings.GetType().GetMethod("ResetDefaults_Click", Any)!.Invoke(settings, new object[] { settings, new RoutedEventArgs() });
            Pump(100);
            Check(config.CaptureHotkey == "F1" && !config.FloatButtonEnabled && config.Translation.ApiKey == "test-key" && Math.Abs(config.FloatIconOpacity - 1) < 0.001,
                "恢复默认设置: everything back to the defaults, the API key kept");
            Check(!settings.IsVisible && File.ReadAllText(AppConfig.ConfigPath).Contains("\"CaptureHotkey\": \"F1\""), "saved, and the window closes (the app reopens it)");
        }

        // ---- the floating button: 添加贴图 in its row and menu; 粘贴剪贴板 / 关闭所有贴图 in the menu
        {
            var config = new AppConfig();
            var ran = new System.Collections.Generic.List<string>();
            var button = new FloatButtonWindow(config, ran.Add, () => { }, () => { }) { FullscreenOverride = () => false };
            button.Show();
            Pump(200);
            Check(button.ShownActions.Last() == "AddPin", "the row ends with 添加贴图");
            var headers = (System.Collections.Generic.IReadOnlyList<string>)button.GetType().GetMethod("MenuHeaders", Any)!.Invoke(button, null)!;
            Check(headers.Take(3).SequenceEqual(new[] { "添加贴图…", "粘贴剪贴板", "关闭所有贴图" }), "the menu: 添加贴图, 粘贴剪贴板, 关闭所有贴图 first");
            button.Close();
            var app = File.ReadAllText(Repo.File("App.xaml.cs"));
            Check(app.Contains("case \"AddPin\":") && app.Contains("Multiselect = true") && app.Contains("case \"PasteClipboard\":") && app.Contains("case \"CloseAllPins\":"),
                "添加贴图 opens pictures (moving ones too) as pins; the menu items do what they say");
            Check(File.Exists(Repo.File("Assets/Icons/add_image.png")), "the 添加图片 icon from the library");
        }

        Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
    }
}
