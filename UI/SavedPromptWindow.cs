using System.Windows.Controls;

namespace Markshot.UI;

/// <summary>
/// "Saved — open the folder?" after saving an image. The check box at the bottom turns the
/// prompt off (also in Settings → 保存与识别).
/// </summary>
internal sealed class SavedPromptWindow : Window
{
    private readonly string _path;
    private readonly CheckBox _ask;

    public SavedPromptWindow(string path)
    {
        _path = path;
        Title = "已保存";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var video = Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".gif";
        var title = new TextBlock { Text = video ? "录像已保存" : "图片已保存", Foreground = Theme.Brush(0xFFF5F5F5), FontSize = 14, FontWeight = FontWeights.SemiBold };
        var file = new TextBlock
        {
            Text = path,
            Foreground = Theme.Brush(0xFF9A9A9A),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 14),
            MaxWidth = 380,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = path
        };
        var open = new Button { Content = "打开所在文件夹", Style = (Style)Application.Current.FindResource("PrimaryButton"), IsDefault = true };
        open.Click += (_, _) =>
        {
            OpenFolder(_path);
            Close();
        };
        var close = new Button { Content = "关闭", Style = (Style)Application.Current.FindResource("FooterButton"), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (video)
        {
            // A recording: play it right away with the default player.
            var play = new Button { Content = "播放", Style = (Style)Application.Current.FindResource("FooterButton"), Margin = new Thickness(0, 0, 8, 0) };
            play.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log.Error("播放录像失败", ex);
                }

                Close();
            };
            buttons.Children.Add(play);
        }

        buttons.Children.Add(open);
        buttons.Children.Add(close);
        _ask = new CheckBox
        {
            Content = "保存后都询问是否打开所在文件夹",
            IsChecked = true,
            Foreground = Theme.Brush(0xFFB8B8B8),
            FontSize = 12,
            Margin = new Thickness(0, 14, 0, 0)
        };

        var body = new StackPanel { MinWidth = 300 };
        body.Children.Add(title);
        body.Children.Add(file);
        body.Children.Add(buttons);
        body.Children.Add(_ask);
        Content = new Border
        {
            Background = Theme.Brush(0xFF1E1E1E),
            BorderBrush = Theme.Brush(0x33FFFFFF),
            LayoutTransform = UiScale.Transform,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 16, 20, 14),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.45 },
            Child = body
        };
        MouseLeftButtonDown += (_, _) => DragMove();
        Loaded += (_, _) =>
        {
            if (Content is FrameworkElement root)
            {
                root.Opacity = 0;
                Motion.PopIn(root, fromAbove: false);
            }

            Activate();
            open.Focus();
        };
        Closed += (_, _) =>
        {
            if (_ask.IsChecked != true && App.Config.AskOpenFolderAfterSave)
            {
                App.Config.AskOpenFolderAfterSave = false;
                App.Config.Save();
            }
        };
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开文件夹失败", ex);
        }
    }
}
