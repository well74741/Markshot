using Markshot.Services;
using System.Windows.Interop;

namespace Markshot.UI;

public partial class TranslationWindow : Window
{
    private readonly BitmapSource? _image;
    private readonly bool _autoTranslate;
    private readonly CancellationTokenSource _cts = new();
    private bool _busy;
    private bool _closed;

    public TranslationWindow(BitmapSource? image, bool autoTranslate = true)
    {
        InitializeComponent();
        _image = image;
        _autoTranslate = autoTranslate;
        TargetBox.Text = App.Config.Translation.TargetLanguage;
        Loaded += OnLoaded;
        SourceInitialized += (_, _) =>
            Native.EnableDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_image == null)
        {
            StatusText.Text = "可直接输入文本后点击“翻译”。";
            return;
        }

        StatusText.Text = "正在识别文字…";

        try
        {
            var text = await OcrService.RecognizeAsync(_image, App.Config.OcrLanguage);
            if (_closed)
            {
                return;
            }

            SourceBox.Text = text;

            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText.Text = "未识别到文字，可手动输入后翻译。";
                return;
            }

            if (_autoTranslate)
            {
                await TranslateAsync();
            }
            else
            {
                StatusText.Text = "识别完成，可点击“翻译”。";
            }
        }
        catch (Exception ex)
        {
            Log.Error("OCR 识别失败", ex);
            if (!_closed)
            {
                StatusText.Text = "识别失败：" + ex.Message;
            }
        }
    }

    private async Task TranslateAsync()
    {
        if (_busy || _closed)
        {
            return;
        }

        var text = SourceBox.Text.Trim();
        if (text.Length == 0)
        {
            StatusText.Text = "没有可翻译的文本。";
            return;
        }

        _busy = true;
        TranslateButton.IsEnabled = false;
        StatusText.Text = "正在翻译…";

        try
        {
            var target = string.IsNullOrWhiteSpace(TargetBox.Text)
                ? App.Config.Translation.TargetLanguage
                : TargetBox.Text.Trim();

            var provider = TranslationService.Create(App.Config.Translation);
            var result = await provider.TranslateAsync(text, target, _cts.Token);

            if (_closed)
            {
                return;
            }

            ResultBox.Text = result;
            StatusText.Text = result.Length == 0 ? "翻译结果为空。" : "翻译完成";
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                StatusText.Text = "已取消";
            }
        }
        catch (Exception ex)
        {
            Log.Error("翻译失败", ex);
            if (!_closed)
            {
                StatusText.Text = "翻译失败：" + ex.Message;
            }
        }
        finally
        {
            _busy = false;
            TranslateButton.IsEnabled = true;
        }
    }

    private async void Translate_Click(object sender, RoutedEventArgs e) => await TranslateAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultBox.Text))
        {
            return;
        }

        StatusText.Text = ClipboardService.CopyText(ResultBox.Text) ? "译文已复制" : "复制失败：剪贴板被占用";
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultBox.Text))
        {
            StatusText.Text = "没有可贴图的译文。";
            return;
        }

        var target = string.IsNullOrWhiteSpace(TargetBox.Text) ? "译文" : TargetBox.Text.Trim();
        var card = TextCardRenderer.Render($"译文 · {target}", ResultBox.Text);
        ((App)Application.Current).CreatePinAtCursor(card);
        StatusText.Text = "译文已贴到屏幕";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        _cts.Dispose();
        base.OnClosed(e);
    }
}
