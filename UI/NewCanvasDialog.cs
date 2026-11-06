using System.Windows.Controls;

namespace Markshot.UI;

/// <summary>新建画布: width, height and the background (transparent / white / black / custom), in Markshot's look.</summary>
public sealed class NewCanvasDialog : Window
{
    private static readonly (string Label, int Width, int Height)[] Presets =
    {
        ("1920 × 1080", 1920, 1080), ("1280 × 720", 1280, 720), ("1080 × 1080", 1080, 1080), ("A4 竖", 1240, 1754)
    };

    private readonly TextBox _width;
    private readonly TextBox _height;
    private readonly Dictionary<string, RadioButton> _backgrounds = new();
    private Color _custom = Colors.White;
    private readonly Border _customSwatch = new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };

    public NewCanvasDialog(int width, int height, Color background)
    {
        Title = "新建画布";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var heading = new TextBlock { Text = "新建画布", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFF5F5F5));

        TextBox Number(int value)
        {
            var box = new TextBox { Style = (Style)Application.Current.FindResource("InputBox"), Width = 72, Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture), TextAlignment = TextAlignment.Center };
            InputMethod.SetIsInputMethodEnabled(box, false);
            return box;
        }

        _width = Number(width);
        _height = Number(height);
        var times = new TextBlock { Text = "×", FontSize = 12, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        times.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF8A8A8A));
        var px = new TextBlock { Text = "像素", FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        px.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF8A8A8A));
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
        sizeRow.Children.Add(_width);
        sizeRow.Children.Add(times);
        sizeRow.Children.Add(_height);
        sizeRow.Children.Add(px);

        var presets = new WrapPanel { MaxWidth = 330 };
        foreach (var (label, w, h) in Presets)
        {
            var preset = new Button { Content = label, Style = (Style)Application.Current.FindResource("FooterButton"), MinWidth = 0, Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 6) };
            preset.Click += (_, _) =>
            {
                _width.Text = w.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _height.Text = h.ToString(System.Globalization.CultureInfo.InvariantCulture);
            };
            presets.Children.Add(preset);
        }

        var backgrounds = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, label) in new[] { ("Transparent", "透明"), ("White", "白"), ("Black", "黑"), ("Custom", "自选") })
        {
            var button = new RadioButton { GroupName = "NewCanvasBackground" + GetHashCode(), Style = (Style)Application.Current.FindResource("SegmentRadio") };
            if (key == "Custom")
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(_customSwatch);
                content.Children.Add(new TextBlock { Text = label });
                button.Content = content;
                button.Click += (_, _) => PickCustom();
            }
            else
            {
                button.Content = label;
            }

            _backgrounds[key] = button;
            backgrounds.Children.Add(button);
        }

        var plate = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(2), Child = backgrounds, HorizontalAlignment = HorizontalAlignment.Left };
        plate.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF2A2A2A));
        _custom = background.A == 0 || background == Colors.White || background == Colors.Black ? Colors.White : background;
        _customSwatch.Background = new SolidColorBrush(_custom);
        _backgrounds[background.A == 0 ? "Transparent" : background == Colors.White ? "White" : background == Colors.Black ? "Black" : "Custom"].IsChecked = true;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string label, FrameworkElement control, double top)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, top, 0, 0) };
            text.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFA8A8A8));
            Grid.SetRow(text, grid.RowDefinitions.Count - 1);
            grid.Children.Add(text);
            control.Margin = new Thickness(0, top, 0, 0);
            Grid.SetRow(control, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }

        Row("尺寸", sizeRow, 0);
        Row("常用", presets, 8);
        Row("背景", plate, 4);

        var create = new Button { Content = "新建", Style = (Style)Application.Current.FindResource("PrimaryButton"), IsDefault = true, MinWidth = 72 };
        create.Click += (_, _) => Create();
        var cancel = new Button { Content = "取消", Style = (Style)Application.Current.FindResource("FooterButton"), IsCancel = true, MinWidth = 72, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);

        var body = new StackPanel();
        body.Children.Add(heading);
        body.Children.Add(grid);
        body.Children.Add(buttons);
        var card = new Border
        {
            LayoutTransform = UiScale.Transform,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.45 },
            Child = body
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF1E1E1E));
        card.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x33FFFFFF));
        Content = card;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not TextBox)
            {
                DragMove();
            }
        };
        Loaded += (_, _) =>
        {
            card.Opacity = 0;
            Motion.PopIn(card, fromAbove: false);
            Activate();
            _width.Focus();
            _width.SelectAll();
        };
    }

    /// <summary>The size and background chosen (after 新建), or null.</summary>
    public (int Width, int Height, Color Background)? Result { get; private set; }

    /// <summary>For tests: the fields' current choice.</summary>
    internal (int Width, int Height, Color Background)? Current()
    {
        if (!int.TryParse(_width.Text.Trim(), out var w) || !int.TryParse(_height.Text.Trim(), out var h) || w < 1 || h < 1 || w > 20000 || h > 20000)
        {
            return null;
        }

        var color = _backgrounds["Transparent"].IsChecked == true ? Colors.Transparent
            : _backgrounds["White"].IsChecked == true ? Colors.White
            : _backgrounds["Black"].IsChecked == true ? Colors.Black
            : _custom;
        return (w, h, color);
    }

    private void Create()
    {
        if (Current() is not { } chosen)
        {
            ThemedMessage.Show(this, "提示", "宽和高要是 1 到 20000 之间的整数。", "确定");
            return;
        }

        Result = chosen;
        Close();
    }

    private void PickCustom()
    {
        var picker = new ColorPickerWindow(_custom) { Owner = this };
        picker.ShowDialog();
        if (picker.Accepted)
        {
            _custom = Color.FromRgb(picker.SelectedColor.R, picker.SelectedColor.G, picker.SelectedColor.B);
            _customSwatch.Background = new SolidColorBrush(_custom);
        }
    }

    /// <summary>Asks for a new canvas; null when cancelled.</summary>
    public static (int Width, int Height, Color Background)? Ask(Window owner, int width, int height, Color background)
    {
        var dialog = new NewCanvasDialog(width, height, background) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = owner.Topmost };
        dialog.ShowDialog();
        return dialog.Result;
    }
}
