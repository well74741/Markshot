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

        var title = new TextBlock { Text = "图片已保存", Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)), FontSize = 14, FontWeight = FontWeights.SemiBold };
        var file = new TextBlock
        {
            Text = path,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
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
        buttons.Children.Add(open);
        buttons.Children.Add(close);
        _ask = new CheckBox
        {
            Content = "保存后都询问是否打开所在文件夹",
            IsChecked = true,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8)),
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
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
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
