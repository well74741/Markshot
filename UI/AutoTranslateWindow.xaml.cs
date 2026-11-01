using System.IO;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Markshot.Core;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

public partial class AutoTranslateWindow : Window
{
    private readonly BitmapSource _source;
    private readonly CancellationTokenSource _cts = new();
    private BitmapSource? _translated;
    private bool _closed;

    public AutoTranslateWindow(BitmapSource source)
    {
        InitializeComponent();
        _source = source;
        Preview.Source = source;
        SourceInitialized += (_, _) => Native.EnableDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log.Info("自动翻译开始");
        try
        {
            var progress = new Progress<string>(text => StatusText.Text = text);
            _translated = await AutoTranslateService.TranslateImageAsync(_source, _cts.Token, progress);
            if (_closed)
            {
                return;
            }

            Log.Info("自动翻译完成");
            Preview.Source = _translated;
            BusyPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "翻译完成";
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                BusyPanel.Visibility = Visibility.Collapsed;
                StatusText.Text = "已取消";
            }
        }
        catch (Exception ex)
        {
            Log.Error("自动翻译失败", ex);
            if (!_closed)
            {
                BusyPanel.Visibility = Visibility.Collapsed;
                StatusText.Text = "翻译失败：" + ex.Message;
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_translated == null)
        {
            return;
        }

        StatusText.Text = ClipboardService.CopyImage(_translated)
            ? "已复制翻译后的图片"
            : "复制失败：剪贴板被占用";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_translated == null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存翻译图片",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg",
            FileName = $"Markshot_translated_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        var directory = App.Config.ResolveSaveDirectory();
        if (Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            BitmapEncoder encoder = Path.GetExtension(dialog.FileName).ToLowerInvariant() is ".jpg" or ".jpeg"
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_translated));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            StatusText.Text = "已保存";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (_translated == null)
        {
            return;
        }

        ((App)Application.Current).CreatePinAtCursor(_translated);
        StatusText.Text = "已贴到屏幕";
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
