using System.Windows.Controls;

namespace Markshot.UI;

/// <summary>
/// 更新 in Markshot's look: asks, downloads with a progress bar, then installs in the background
/// (no setup wizard) and Markshot opens again by itself.
/// </summary>
public sealed class UpdateWindow : Window
{
    private readonly UpdateService.Package _package;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _install;
    private readonly Button _later;
    private readonly CancellationTokenSource _cancel = new();
    private bool _busy;

    public UpdateWindow(UpdateService.Package package)
    {
        _package = package;
        Title = "更新 Markshot";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false; // with the settings as owner it stays in front of them, not of every other program
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var heading = new TextBlock { Text = $"Markshot {package.Version} 可以更新", FontSize = 15, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFF5F5F5));
        var current = new TextBlock { Text = $"当前版本 {AppVersion.Display}", FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        current.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF9A9A9A));
        _status = new TextBlock
        {
            Text = "更新会在后台安装，完成后 Markshot 自动重新打开；贴图、设置都会保留。",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            Margin = new Thickness(0, 14, 0, 0)
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFC8C8C8));
        _progress = new ProgressBar { Height = 4, Margin = new Thickness(0, 12, 0, 0), Maximum = 1, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(0) };
        _progress.SetResourceReference(ForegroundProperty, "Accent");
        _progress.SetResourceReference(BackgroundProperty, Theme.Key(0xFF2A2A2A));

        _install = new Button { Content = "立即更新", Style = (Style)Application.Current.FindResource("PrimaryButton"), MinWidth = 88, IsDefault = true };
        _install.Click += async (_, _) => await InstallAsync();
        _later = new Button { Content = "以后再说", Style = (Style)Application.Current.FindResource("FooterButton"), MinWidth = 88, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        _later.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(_install);
        buttons.Children.Add(_later);

        var body = new StackPanel { MinWidth = 340 };
        body.Children.Add(heading);
        body.Children.Add(current);
        body.Children.Add(_status);
        body.Children.Add(_progress);
        body.Children.Add(buttons);
        var card = new Border
        {
            LayoutTransform = UiScale.Transform,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22, 18, 22, 16),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.45 },
            Child = body
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF1E1E1E));
        card.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x33FFFFFF));
        Content = card;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not Button)
            {
                DragMove();
            }
        };
        Closing += (_, e) =>
        {
            if (_busy && !_cancel.IsCancellationRequested)
            {
                _cancel.Cancel(); // closing while downloading stops the download
            }
        };
        Loaded += (_, _) =>
        {
            card.Opacity = 0;
            Motion.PopIn(card, fromAbove: false);
            Activate();
            _install.Focus();
        };
    }

    /// <summary>For tests: what 立即更新 runs after the download (default: the silent install).</summary>
    public Action<UpdateService.Package>? InstallAction { get; init; }

    public string Status => _status.Text;

    public async Task InstallAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _install.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = !_package.Remote;
        _status.Text = _package.Remote ? "正在下载更新…" : "正在准备安装…";
        try
        {
            var progress = new Progress<double>(value =>
            {
                _progress.Value = value;
                _status.Text = $"正在下载更新… {value:P0}";
            });
            var local = await UpdateService.DownloadAsync(_package, progress, _cancel.Token);
            _progress.IsIndeterminate = true;
            _status.Text = "正在安装，Markshot 马上会重新打开…";
            _later.IsEnabled = false;
            await Task.Delay(400);
            (InstallAction ?? UpdateService.InstallQuietly)(local);
        }
        catch (OperationCanceledException)
        {
            _busy = false;
        }
        catch (Exception ex)
        {
            Log.Error("更新失败", ex);
            _busy = false;
            _progress.Visibility = Visibility.Collapsed;
            _status.Text = "更新失败：" + ex.Message;
            _install.Content = "重试";
            _install.IsEnabled = true;
            _later.IsEnabled = true;
        }
    }
}
