using Markshot.Services;
using Microsoft.Win32;
using System.Windows.Interop;

namespace Markshot.UI;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private bool _syncingKey;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();

        _config = config;
        SourceInitialized += (_, _) =>
            Native.EnableDarkTitleBar(new WindowInteropHelper(this).Handle);

        CaptureHotkeyBox.Text = config.CaptureHotkey;
        PinHotkeyBox.Text = config.PinHotkey;
        MouseThroughHotkeyBox.Text = config.MouseThroughHotkey;
        RestorePinHotkeyBox.Text = config.RestorePinHotkey;
        TextBypassBox.Text = config.TextBypassModifier;
        PinFlipHBox.Text = config.PinFlipHorizontalHotkey;
        PinFlipVBox.Text = config.PinFlipVerticalHotkey;
        PinRotateBox.Text = config.PinRotateHotkey;
        PinResetBox.Text = config.PinResetHotkey;
        PenKeyBox.Text = config.ToolPenHotkey;
        SelectKeyBox.Text = config.ToolSelectHotkey;
        EraserKeyBox.Text = config.ToolEraserHotkey;
        TextKeyBox.Text = config.ToolTextHotkey;
        NumberKeyBox.Text = config.ToolNumberHotkey;
        LineKeyBox.Text = config.ToolLineHotkey;
        SaveDirBox.Text = config.SaveDirectory;
        OcrLanguageBox.Text = config.OcrLanguage;
        BaseUrlBox.Text = config.Translation.BaseUrl;
        ApiKeyPassword.Password = config.Translation.ApiKey;
        ModelBox.Text = config.Translation.Model;
        TargetBox.Text = config.Translation.TargetLanguage;
        TimeoutBox.Text = config.Translation.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ImmersiveSwitch.IsChecked = config.ImmersiveTranslate;

        VersionText.Text = "版本 " + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "");
        ConfigPathText.Text = AppConfig.ConfigPath;
        ConfigPathText.ToolTip = AppConfig.ConfigPath;
        if (!config.Translation.IsConfigured)
        {
            StatusText.Text = "提示：还没有填写翻译 API Key，原位翻译和文字翻译需要它（见“翻译”页）。";
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHotkeys == null || sender is not RadioButton { Tag: string page })
        {
            return;
        }

        PageHotkeys.Visibility = page == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PageTranslate.Visibility = page == "Translate" ? Visibility.Visible : Visibility.Collapsed;
        PageGeneral.Visibility = page == "General" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Opens the window on a given page ("Hotkeys", "Translate", "General").</summary>
    public void ShowPage(string page)
    {
        switch (page)
        {
            case "Translate":
                NavTranslate.IsChecked = true;
                break;
            case "General":
                NavGeneral.IsChecked = true;
                break;
            default:
                NavHotkeys.IsChecked = true;
                break;
        }
    }

    private string ApiKey => ApiKeyBox.Visibility == Visibility.Visible ? ApiKeyBox.Text : ApiKeyPassword.Password;

    private void ApiKeyReveal_Click(object sender, RoutedEventArgs e)
    {
        var reveal = ApiKeyBox.Visibility != Visibility.Visible;
        _syncingKey = true;
        if (reveal)
        {
            ApiKeyBox.Text = ApiKeyPassword.Password;
        }
        else
        {
            ApiKeyPassword.Password = ApiKeyBox.Text;
        }

        _syncingKey = false;
        ApiKeyBox.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyPassword.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        ApiKeyReveal.Content = reveal ? "隐藏" : "显示";
    }

    private void ApiKeyPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingKey && TestStatusText != null)
        {
            TestStatusText.Text = string.Empty;
        }
    }

    private void ApiKeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingKey && TestStatusText != null)
        {
            TestStatusText.Text = string.Empty;
        }
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string language })
        {
            TargetBox.Text = language;
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = AppConfig.ConfigPath;
            if (File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                var directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                Process.Start("explorer.exe", $"\"{directory}\"");
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法打开文件夹：" + ex.Message;
        }
    }

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.Tag = box.Text;
            box.SelectAll();
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        // Alt combinations arrive as Key.System; the real key is in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = HandleHotkeyKey(box, key, Keyboard.Modifiers);
    }

    private bool HandleHotkeyKey(TextBox box, Key key, ModifierKeys modifiers)
    {
        if (IsModifierKey(key))
        {
            box.Text = FormatModifierPreview(modifiers);
            return true;
        }

        if (key == Key.Escape)
        {
            box.Text = box.Tag as string ?? box.Text;
            return true;
        }

        if (key is Key.Back or Key.Delete)
        {
            box.Text = string.Empty;
            return true;
        }

        // Let Tab navigate and let a bare Enter trigger the default Save button.
        if (key == Key.Tab && modifiers == ModifierKeys.None)
        {
            return false;
        }

        if (key == Key.Enter && modifiers == ModifierKeys.None)
        {
            return false;
        }

        if (HotkeyGesture.TryFormat(key, modifiers, out var text))
        {
            box.Text = text;
            box.CaretIndex = text.Length;
            return true;
        }

        return false;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    private static string NormalizeToolKey(string text, string fallback)
    {
        var value = (text ?? string.Empty).Trim().ToUpperInvariant();
        if (value.Length == 1 && char.IsLetterOrDigit(value[0]))
        {
            return value;
        }

        return fallback;
    }

    private static string FormatModifierPreview(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        return parts.Count == 0 ? "…" : string.Join("+", parts) + "+…";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!HotkeyGesture.TryParse(CaptureHotkeyBox.Text, out var capture, out var error))
        {
            MessageBox.Show(this, "截图快捷键无效：" + error, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hasPinHotkey = !string.IsNullOrWhiteSpace(PinHotkeyBox.Text);
        var pin = default(HotkeyGesture);
        if (hasPinHotkey && !HotkeyGesture.TryParse(PinHotkeyBox.Text, out pin, out error))
        {
            MessageBox.Show(this, "贴图快捷键无效：" + error, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (hasPinHotkey &&
            string.Equals(capture.Text, pin.Text, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "截图和贴图不能使用同一个快捷键。", "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hasMouseThrough = !string.IsNullOrWhiteSpace(MouseThroughHotkeyBox.Text);
        var mouseThrough = default(HotkeyGesture);
        if (hasMouseThrough &&
            !HotkeyGesture.TryParse(MouseThroughHotkeyBox.Text, out mouseThrough, out error))
        {
            MessageBox.Show(this, "鼠标穿透快捷键无效：" + error, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hasRestore = !string.IsNullOrWhiteSpace(RestorePinHotkeyBox.Text);
        var restore = default(HotkeyGesture);
        if (hasRestore && !HotkeyGesture.TryParse(RestorePinHotkeyBox.Text, out restore, out error))
        {
            MessageBox.Show(this, "返回上一贴图快捷键无效：" + error, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.CaptureHotkey = capture.Text;
        _config.PinHotkey = hasPinHotkey ? pin.Text : "";
        _config.MouseThroughHotkey = hasMouseThrough ? mouseThrough.Text : "";
        _config.RestorePinHotkey = hasRestore ? restore.Text : "";
        _config.TextBypassModifier = string.IsNullOrWhiteSpace(TextBypassBox.Text)
            ? "Alt"
            : TextBypassBox.Text.Trim();
        _config.PinFlipHorizontalHotkey = string.IsNullOrWhiteSpace(PinFlipHBox.Text) ? "Q" : PinFlipHBox.Text.Trim();
        _config.PinFlipVerticalHotkey = string.IsNullOrWhiteSpace(PinFlipVBox.Text) ? "W" : PinFlipVBox.Text.Trim();
        _config.PinRotateHotkey = string.IsNullOrWhiteSpace(PinRotateBox.Text) ? "E" : PinRotateBox.Text.Trim();
        _config.PinResetHotkey = string.IsNullOrWhiteSpace(PinResetBox.Text) ? "F" : PinResetBox.Text.Trim();

        var penKey = NormalizeToolKey(PenKeyBox.Text, "B");
        var selectKey = NormalizeToolKey(SelectKeyBox.Text, "V");
        var eraserKey = NormalizeToolKey(EraserKeyBox.Text, "E");
        var textKey = NormalizeToolKey(TextKeyBox.Text, "T");
        var numberKey = NormalizeToolKey(NumberKeyBox.Text, "S");
        var lineKey = NormalizeToolKey(LineKeyBox.Text, "L");
        var toolKeys = new[] { penKey, selectKey, eraserKey, textKey, numberKey, lineKey };
        if (toolKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != toolKeys.Length)
        {
            MessageBox.Show(this, "标注工具快捷键不能重复。", "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.ToolPenHotkey = penKey;
        _config.ToolSelectHotkey = selectKey;
        _config.ToolEraserHotkey = eraserKey;
        _config.ToolTextHotkey = textKey;
        _config.ToolNumberHotkey = numberKey;
        _config.ToolLineHotkey = lineKey;

        _config.SaveDirectory = SaveDirBox.Text.Trim();
        _config.OcrLanguage = OcrLanguageBox.Text.Trim();

        _config.Translation.BaseUrl = BaseUrlBox.Text.Trim();
        _config.Translation.ApiKey = ApiKey.Trim();
        _config.Translation.Model = ModelBox.Text.Trim();
        _config.Translation.TargetLanguage = TargetBox.Text.Trim();
        _config.ImmersiveTranslate = ImmersiveSwitch.IsChecked == true;

        _config.Translation.TimeoutSeconds =
            int.TryParse(TimeoutBox.Text.Trim(), out var timeout)
                ? Math.Clamp(timeout, 10, 300)
                : 60;

        Saved = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>True when the window was closed with “保存”.</summary>
    public bool Saved { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择截图保存目录",
            InitialDirectory = SaveDirBox.Text.Trim()
        };

        if (dialog.ShowDialog(this) == true)
        {
            SaveDirBox.Text = dialog.FolderName;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestStatusText.Foreground = (Brush)FindResource("TextSecondary");
        TestStatusText.Text = "测试中…";

        try
        {
            var testConfig = new TranslationConfig
            {
                BaseUrl = BaseUrlBox.Text.Trim(),
                ApiKey = ApiKey.Trim(),
                TimeoutSeconds = int.TryParse(TimeoutBox.Text.Trim(), out var seconds) ? seconds : 30,
                Model = ModelBox.Text.Trim(),
                TargetLanguage = TargetBox.Text.Trim()
            };

            var provider = TranslationService.Create(testConfig);
            var result = await provider.TranslateAsync(
                "Hello, Markshot!", TargetBox.Text.Trim(), CancellationToken.None);

            TestStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8A));
            TestStatusText.Text = "连接成功：" + result;
            TestStatusText.ToolTip = result;
        }
        catch (Exception ex)
        {
            TestStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A));
            TestStatusText.Text = "测试失败：" + ex.Message;
            TestStatusText.ToolTip = ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }
}
