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
        if (!autoTranslate)
        {
            Title = "Markshot 文字识别";
            HeaderTitle.Text = "文字识别";
            HeaderSubtitle.Text = "本地 OCR · 可编辑后复制或翻译";
        }

        SourceBox.TextChanged += (_, _) => UpdateMeta();
        ResultBox.TextChanged += (_, _) => UpdateMeta();
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
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _ = TranslateAsync();
            e.Handled = true;
        }
    }

    private enum StatusKind
    {
        Idle,
        Busy,
        Done,
        Error
    }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusDot.Fill = kind switch
        {
            StatusKind.Busy => (Brush)FindResource("Accent"),
            StatusKind.Done => new SolidColorBrush(Color.FromRgb(0x52, 0xC4, 0x1A)),
            StatusKind.Error => new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4F)),
            _ => new SolidColorBrush(Color.FromRgb(0x5E, 0x5E, 0x5E))
        };
    }

    private void UpdateMeta()
    {
        var source = SourceBox.Text.Trim();
        SourceMeta.Text = source.Length == 0 ? "识别结果，可编辑" : $"{source.Length} 字 · 可编辑";
        var result = ResultBox.Text.Trim();
        ResultMeta.Text = result.Length == 0 ? "" : $"{result.Length} 字";
        ResultPlaceholder.Visibility = result.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopySource_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourceBox.Text))
        {
            return;
        }

        if (ClipboardService.CopyText(SourceBox.Text))
        {
            SetStatus("原文已复制", StatusKind.Done);
        }
        else
        {
            SetStatus("复制失败：剪贴板被占用", StatusKind.Error);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_image == null)
        {
            SetStatus("可直接输入文本后点击“翻译”。", StatusKind.Idle);
            return;
        }

        SetStatus("正在识别文字…", StatusKind.Busy);

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
                SetStatus("未识别到文字，可手动输入后翻译。", StatusKind.Idle);
                return;
            }

            if (_autoTranslate)
            {
                await TranslateAsync();
            }
            else
            {
                SetStatus("识别完成，可编辑、复制或翻译。", StatusKind.Done);
            }
        }
        catch (Exception ex)
        {
            Log.Error("OCR 识别失败", ex);
            if (!_closed)
            {
                SetStatus("识别失败：" + ex.Message, StatusKind.Error);
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
            SetStatus("没有可翻译的文本。", StatusKind.Idle);
            return;
        }

        _busy = true;
        TranslateButton.IsEnabled = false;
        SetStatus("正在翻译…", StatusKind.Busy);

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
            SetStatus(result.Length == 0 ? "翻译结果为空。" : "翻译完成", result.Length == 0 ? StatusKind.Idle : StatusKind.Done);
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                SetStatus("已取消", StatusKind.Idle);
            }
        }
        catch (Exception ex)
        {
            Log.Error("翻译失败", ex);
            if (!_closed)
            {
                SetStatus("翻译失败：" + ex.Message, StatusKind.Error);
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

        if (ClipboardService.CopyText(ResultBox.Text))
        {
            SetStatus("译文已复制", StatusKind.Done);
        }
        else
        {
            SetStatus("复制失败：剪贴板被占用", StatusKind.Error);
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        // The translation when there is one, otherwise the recognised text.
        var translated = !string.IsNullOrWhiteSpace(ResultBox.Text);
        var text = translated ? ResultBox.Text : SourceBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("没有可贴图的文字。", StatusKind.Idle);
            return;
        }

        var target = string.IsNullOrWhiteSpace(TargetBox.Text) ? "译文" : TargetBox.Text.Trim();
        var card = TextCardRenderer.Render(translated ? $"译文 · {target}" : "识别文字", text);
        ((App)Application.Current).CreatePinAtCursor(card);
        SetStatus(translated ? "译文已贴到屏幕" : "原文已贴到屏幕", StatusKind.Done);
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
