using Markshot.Services;
using Microsoft.Win32;
using System.Windows.Interop;

namespace Markshot.UI;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();

        _config = config;
        SourceInitialized += (_, _) =>
            Native.EnableDarkTitleBar(new WindowInteropHelper(this).Handle);

        CaptureHotkeyBox.Text = config.CaptureHotkey;
        PinHotkeyBox.Text = config.PinHotkey;
        SaveDirBox.Text = config.SaveDirectory;
        OcrLanguageBox.Text = config.OcrLanguage;
        BaseUrlBox.Text = config.Translation.BaseUrl;
        ApiKeyBox.Text = config.Translation.ApiKey;
        ModelBox.Text = config.Translation.Model;
        TargetBox.Text = config.Translation.TargetLanguage;
        TimeoutBox.Text = config.Translation.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);

        try
        {
            var languages = OcrService.AvailableLanguages;
            OcrHintText.Text = languages.Count == 0
                ? "系统未安装 OCR 语言包。"
                : "系统可用：" + string.Join("、", languages);
        }
        catch (Exception ex)
        {
            OcrHintText.Text = "无法枚举 OCR 语言：" + ex.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!HotkeyGesture.TryParse(CaptureHotkeyBox.Text, out var capture, out var error))
        {
            MessageBox.Show(this, "截图快捷键无效：" + error, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!HotkeyGesture.TryParse(PinHotkeyBox.Text, out var pin, out error))
        {
            MessageBox.Show(this, "贴图快捷键无效：" + error, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.Equals(capture.Text, pin.Text, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "截图和贴图不能使用同一个快捷键。", "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.CaptureHotkey = capture.Text;
        _config.PinHotkey = pin.Text;
        _config.SaveDirectory = SaveDirBox.Text.Trim();
        _config.OcrLanguage = OcrLanguageBox.Text.Trim();

        _config.Translation.BaseUrl = BaseUrlBox.Text.Trim();
        _config.Translation.ApiKey = ApiKeyBox.Text.Trim();
        _config.Translation.Model = ModelBox.Text.Trim();
        _config.Translation.TargetLanguage = TargetBox.Text.Trim();

        _config.Translation.TimeoutSeconds =
            int.TryParse(TimeoutBox.Text.Trim(), out var timeout)
                ? Math.Clamp(timeout, 10, 300)
                : 60;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

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
        StatusText.Text = "测试中…";

        try
        {
            var testConfig = new TranslationConfig
            {
                BaseUrl = BaseUrlBox.Text.Trim(),
                ApiKey = ApiKeyBox.Text.Trim(),
                Model = ModelBox.Text.Trim(),
                TargetLanguage = TargetBox.Text.Trim()
            };

            var provider = TranslationService.Create(testConfig);
            var result = await provider.TranslateAsync(
                "Hello, Markshot!", TargetBox.Text.Trim(), CancellationToken.None);

            StatusText.Text = "连接成功：" + result;
        }
        catch (Exception ex)
        {
            StatusText.Text = "测试失败：" + ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }
}
