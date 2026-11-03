namespace Markshot.UI;

/// <summary>
/// 贴图历史: the recently closed pins as thumbnails (newest first). Click = pin it again (with its
/// annotations / zoom when it was closed in this session), right-click = remove.
/// </summary>
public sealed class PinHistoryWindow : Window
{
    private readonly WrapPanel _tiles = new() { Margin = new Thickness(14, 4, 6, 14) };
    private readonly TextBlock _empty;

    private readonly Func<IReadOnlyList<PinEntry>> _entries;
    private readonly Action<PinEntry> _restore;
    private readonly Action<PinEntry> _delete;
    private readonly Action _clear;

    public PinHistoryWindow(Func<IReadOnlyList<PinEntry>> entries, Action<PinEntry> restore, Action<PinEntry> delete, Action clearAll)
    {
        _entries = entries;
        _restore = restore;
        _delete = delete;
        _clear = clearAll;
        Title = "贴图历史";
        Width = 660;
        Height = 470;
        MinWidth = 360;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Markshot;component/Assets/markshot.ico"));
        SetResourceReference(BackgroundProperty, Theme.Key(0xFF1A1A1A));

        var title = new TextBlock { Text = "最近关闭的贴图", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFF0F0F0));
        var hint = new TextBlock { Text = "单击重新贴到屏幕，右键删除", FontSize = 12, Margin = new Thickness(12, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF8A8A8A));
        var clear = new Button { Content = "清空", Style = (Style)Application.Current.FindResource("FooterButton"), MinWidth = 64, Height = 28 };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "清空全部贴图历史？", "Markshot", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                _clear();
                Refresh();
            }
        };
        var header = new DockPanel { Margin = new Thickness(16, 12, 16, 6), LastChildFill = false };
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(title);
        header.Children.Add(hint);

        _empty = new TextBlock { Text = "还没有关闭过的贴图", FontSize = 13, Margin = new Thickness(18), Visibility = Visibility.Collapsed };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF7A7A7A));
        var list = new StackPanel();
        list.Children.Add(_empty);
        list.Children.Add(_tiles);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
        var root = new DockPanel();
        root.SetResourceReference(Panel.BackgroundProperty, Theme.Key(0xFF1A1A1A));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(scroll);
        Content = root;

        Theme.TrackTitleBar(this);
        UiScale.AttachWindow(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        Refresh();
    }

    public void Refresh()
    {
        _tiles.Children.Clear();
        var entries = _entries().OrderByDescending(entry => entry.Created).ToList();
        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var entry in entries)
        {
            _tiles.Children.Add(Tile(entry));
        }
    }

    private FrameworkElement Tile(PinEntry entry)
    {
        var image = entry.LoadImage();
        var picture = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            Width = 132,
            Height = 92,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var time = new TextBlock
        {
            Text = entry.Created.Date == DateTime.Today ? entry.Created.ToString("HH:mm:ss") : entry.Created.ToString("MM-dd HH:mm"),
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        time.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF9A9A9A));
        var size = image == null ? "" : $"{image.PixelWidth} × {image.PixelHeight}";
        var stack = new StackPanel();
        stack.Children.Add(new Border { Child = picture, Background = (Brush)Application.Current.FindResource("SmallCheckerBrush"), CornerRadius = new CornerRadius(4) });
        stack.Children.Add(time);
        var tile = new Border
        {
            Width = 148,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = stack,
            ToolTip = $"{entry.Created:yyyy-MM-dd HH:mm:ss}  {size}"
        };
        tile.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF222222));
        tile.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x14FFFFFF));
        tile.MouseEnter += (_, _) => tile.BorderBrush = (Brush)Application.Current.FindResource("Accent");
        tile.MouseLeave += (_, _) => tile.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x14FFFFFF));
        tile.MouseLeftButtonUp += (_, _) =>
        {
            _restore(entry);
            Refresh();
        };
        var menu = new ContextMenu();
        var pin = new MenuItem { Header = "重新贴图" };
        pin.Click += (_, _) =>
        {
            _restore(entry);
            Refresh();
        };
        var copy = new MenuItem { Header = "复制图片" };
        copy.Click += (_, _) =>
        {
            if (entry.LoadImage() is { } picked)
            {
                ClipboardService.CopyImage(picked);
            }
        };
        var remove = new MenuItem { Header = "删除" };
        remove.Click += (_, _) =>
        {
            _delete(entry);
            Refresh();
        };
        menu.Items.Add(pin);
        menu.Items.Add(copy);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        tile.ContextMenu = menu;
        return tile;
    }
}
