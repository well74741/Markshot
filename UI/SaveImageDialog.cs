using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace Markshot.UI;

/// <summary>
/// Saving a picture in Markshot's own look (the Windows dialog does not follow the theme): file name,
/// format, folder (with the Windows folder picker behind 浏览…). Used by the capture, pins, the image
/// editor and the long screenshot.
/// </summary>
public sealed class SaveImageDialog : Window
{
    private static readonly (string Label, string Extension)[] Formats =
        { ("PNG", ".png"), ("JPEG", ".jpg"), ("WebP", ".webp"), ("BMP", ".bmp"), ("TIFF", ".tif") };

    /// <summary>The folder used last in this run (the next dialog starts there).</summary>
    private static string? _lastDirectory;

    private readonly TextBox _name;
    private readonly TextBox _folder;
    private readonly TextBlock _warning;
    private readonly Dictionary<string, RadioButton> _formatButtons = new();
    private bool _syncing;
    private readonly BitmapSource? _picture;
    private readonly Slider _quality = new() { Minimum = 10, Maximum = 100, Width = 170, IsSnapToTickEnabled = true, TickFrequency = 1, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _qualityValue = new() { FontSize = 12, Width = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _estimate = new() { FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly FrameworkElement _qualityRow;
    private readonly TextBlock _qualityLabel = new() { Text = "质量", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _notice = new() { FontSize = 12, Margin = new Thickness(52, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
    private readonly DispatcherTimer _estimateTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly bool _transparent;

    /// <param name="picture">What will be saved: the size estimate and the transparency notice use it.</param>
    public SaveImageDialog(string title, string fileName, string? directory, BitmapSource? picture = null)
    {
        _picture = picture;
        _transparent = picture != null && HasTransparency(picture);
        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var heading = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFF5F5F5));

        _name = new TextBox { Style = (Style)Application.Current.FindResource("InputBox"), Width = 330, Text = fileName };
        _name.TextChanged += (_, _) => NameChanged();
        _folder = new TextBox { Style = (Style)Application.Current.FindResource("InputBox"), Width = 260 };
        _folder.Text = directory is { Length: > 0 } && Directory.Exists(directory) ? directory : _lastDirectory ?? App.Config.ResolveSaveDirectory();
        _folder.TextChanged += (_, _) => UpdateWarning();
        var browse = new Button { Content = "浏览…", Style = (Style)Application.Current.FindResource("FooterButton"), Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) => Browse();

        var formats = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, extension) in Formats)
        {
            var button = new RadioButton
            {
                Content = label,
                GroupName = "SaveFormat" + GetHashCode(),
                Style = (Style)Application.Current.FindResource("SegmentRadio"),
                Tag = extension
            };
            button.Checked += (_, _) => FormatChosen(extension);
            _formatButtons[extension] = button;
            formats.Children.Add(button);
        }

        var formatPlate = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(2), Child = formats, HorizontalAlignment = HorizontalAlignment.Left };
        formatPlate.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF2A2A2A));

        var folderRow = new StackPanel { Orientation = Orientation.Horizontal };
        folderRow.Children.Add(_folder);
        folderRow.Children.Add(browse);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string label, UIElement control, double top)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, top, 0, 0) };
            text.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFA8A8A8));
            Grid.SetRow(text, grid.RowDefinitions.Count - 1);
            grid.Children.Add(text);
            if (control is FrameworkElement element)
            {
                element.Margin = new Thickness(0, top, 0, 0);
            }

            Grid.SetRow(control, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }

        Row("文件名", _name, 0);
        Row("格式", formatPlate, 10);
        Row("位置", folderRow, 10);

        // 质量 (JPEG / WebP): a slider with the file size it gives.
        _quality.Value = Math.Clamp(App.Config.SaveQuality, 10, 100);
        _quality.ValueChanged += (_, _) => QualityChanged();
        _qualityValue.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFC8C8C8));
        _estimate.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF8A8A8A));
        _qualityLabel.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFA8A8A8));
        var qualityPanel = new StackPanel { Orientation = Orientation.Horizontal };
        qualityPanel.Children.Add(_quality);
        qualityPanel.Children.Add(_qualityValue);
        qualityPanel.Children.Add(_estimate);
        _qualityRow = qualityPanel;
        Row("质量", qualityPanel, 10);
        _estimateTimer.Tick += (_, _) =>
        {
            _estimateTimer.Stop();
            UpdateEstimate();
        };
        _notice.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF9A9A9A));

        _warning = new TextBlock { FontSize = 12, Margin = new Thickness(52, 8, 0, 0), Visibility = Visibility.Collapsed, Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)) };

        var save = new Button { Content = "保存", Style = (Style)Application.Current.FindResource("PrimaryButton"), IsDefault = true, MinWidth = 72 };
        save.Click += (_, _) => Confirm();
        var cancel = new Button { Content = "取消", Style = (Style)Application.Current.FindResource("FooterButton"), IsCancel = true, MinWidth = 72, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var body = new StackPanel();
        body.Children.Add(heading);
        body.Children.Add(grid);
        body.Children.Add(_notice);
        body.Children.Add(_warning);
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
        UpdateFormatRows();
        Loaded += (_, _) =>
        {
            card.Opacity = 0;
            Motion.PopIn(card, fromAbove: false);
            Activate();
            _name.Focus();
            // The name is selected without its extension, ready to type over.
            var dot = _name.Text.LastIndexOf('.');
            _name.Select(0, dot > 0 ? dot : _name.Text.Length);
        };
        NameChanged();
    }

    /// <summary>The full path to save to (after 保存), or null.</summary>
    public string? ResultPath { get; private set; }

    /// <summary>Asks where to save; null when cancelled. A topmost owner (the capture) keeps the dialog above it.</summary>
    public static string? Ask(Window? owner, string title, string fileName, string? directory = null, BitmapSource? picture = null)
    {
        var dialog = new SaveImageDialog(title, fileName, directory, picture);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.Topmost = owner.Topmost;
        }
        else
        {
            dialog.Topmost = true;
        }

        dialog.ShowDialog();
        return dialog.ResultPath;
    }

    /// <summary>The path the fields describe now (the format's extension added when the name has none).</summary>
    public string CurrentPath
    {
        get
        {
            var name = _name.Text.Trim();
            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (!ImageFile.IsImage(name) || extension is ".gif" or ".ico")
            {
                name += SelectedExtension;
            }

            return Path.Combine(Environment.ExpandEnvironmentVariables(_folder.Text.Trim()), name);
        }
    }

    private string SelectedExtension => _formatButtons.FirstOrDefault(pair => pair.Value.IsChecked == true).Key ?? ".png";

    public void SetFormat(string extension)
    {
        if (_formatButtons.TryGetValue(Normalize(extension), out var button))
        {
            button.IsChecked = true;
        }
    }

    public void SetFolder(string folder) => _folder.Text = folder;

    public string FileName => _name.Text;

    public bool WarningShown => _warning.Visibility == Visibility.Visible;

    /// <summary>Tests: the quality row, the size estimate and the transparency notice.</summary>
    public bool QualityShown => _qualityRow.Visibility == Visibility.Visible;

    public string Estimate => _estimate.Text;

    public string Notice => _notice.Visibility == Visibility.Visible ? _notice.Text : "";

    public void SetQuality(int quality) => _quality.Value = quality;

    public void EstimateNow() => UpdateEstimate();

    private static bool HasTransparency(BitmapSource picture)
    {
        var (pixels, _, _) = ImageOps.ReadPixels(picture);
        for (var i = 3; i < pixels.Length; i += 4 * 7) // every 7th pixel is enough to tell
        {
            if (pixels[i] < 250)
            {
                return true;
            }
        }

        return false;
    }

    private void QualityChanged()
    {
        _qualityValue.Text = ((int)Math.Round(_quality.Value)).ToString(CultureInfo.InvariantCulture);
        ImageFile.Quality = (int)Math.Round(_quality.Value);
        _estimate.Text = _picture == null ? "" : "…";
        _estimateTimer.Stop();
        _estimateTimer.Start();
    }

    /// <summary>The format's own rows: 质量 for JPEG / WebP, the notice when the picture's transparency would be lost.</summary>
    private void UpdateFormatRows()
    {
        if (_qualityRow == null)
        {
            return;
        }

        var extension = SelectedExtension;
        var lossy = ImageFile.Lossy(extension);
        _qualityRow.Visibility = lossy ? Visibility.Visible : Visibility.Collapsed;
        _qualityLabel.Visibility = _qualityRow.Visibility;
        foreach (var child in ((Grid)((FrameworkElement)_qualityRow).Parent).Children.OfType<TextBlock>())
        {
            if (child.Text == "质量")
            {
                child.Visibility = _qualityRow.Visibility;
            }
        }

        _notice.Text = _transparent && ImageFile.Opaque(extension)
            ? "这张图有透明部分，" + (extension == ".bmp" ? "BMP" : "JPEG") + " 不支持透明，透明处会变成白色；要保留透明请选 PNG 或 WebP。"
            : "";
        _notice.Visibility = _notice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (lossy)
        {
            QualityChanged();
        }
    }

    private void UpdateEstimate()
    {
        if (_picture == null || !ImageFile.Lossy(SelectedExtension))
        {
            _estimate.Text = "";
            return;
        }

        try
        {
            var bytes = ImageFile.Encode(_picture, SelectedExtension, (int)Math.Round(_quality.Value)).Length;
            _estimate.Text = "约 " + (bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB");
        }
        catch (Exception ex)
        {
            Log.Info("估算文件大小失败：" + ex.Message);
            _estimate.Text = "";
        }
    }

    private static string Normalize(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpeg" or ".jfif" => ".jpg",
        ".tiff" => ".tif",
        var other => other
    };

    /// <summary>A typed extension picks the format.</summary>
    private void NameChanged()
    {
        if (_syncing)
        {
            return;
        }

        var extension = Normalize(Path.GetExtension(_name.Text.Trim()));
        _syncing = true;
        if (_formatButtons.TryGetValue(extension, out var button))
        {
            button.IsChecked = true;
        }
        else if (_formatButtons.Values.All(b => b.IsChecked != true))
        {
            _formatButtons[".png"].IsChecked = true;
        }

        _syncing = false;
        UpdateWarning();
        UpdateFormatRows();
    }

    /// <summary>A format picked: the name's extension follows.</summary>
    private void FormatChosen(string extension)
    {
        if (!_syncing)
        {
            var name = _name.Text.Trim();
            var current = Path.GetExtension(name);
            var stem = current.Length > 0 && ImageFile.IsImage(name) ? name[..^current.Length] : name;
            _syncing = true;
            _name.Text = stem + extension;
            _syncing = false;
        }

        UpdateWarning();
        UpdateFormatRows();
    }

    private void UpdateWarning()
    {
        if (_warning == null)
        {
            return;
        }

        string? text = null;
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            text = "请输入文件名";
        }
        else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            text = "文件名里有不能用的字符";
        }
        else
        {
            try
            {
                if (File.Exists(CurrentPath))
                {
                    text = "已有同名文件，保存会覆盖它";
                }
            }
            catch (ArgumentException)
            {
                text = "位置无效";
            }
        }

        _warning.Text = text ?? "";
        _warning.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "选择保存位置" };
        if (Directory.Exists(_folder.Text.Trim()))
        {
            dialog.InitialDirectory = _folder.Text.Trim();
        }

        if (dialog.ShowDialog(this) == true)
        {
            _folder.Text = dialog.FolderName;
        }
    }

    /// <summary>保存: checks the name and folder (creating the folder), then closes with the path.</summary>
    public bool Confirm()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            UpdateWarning();
            _name.Focus();
            return false;
        }

        string path;
        try
        {
            path = CurrentPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _warning.Text = "无法使用这个位置：" + ex.Message;
            _warning.Visibility = Visibility.Visible;
            return false;
        }

        ResultPath = path;
        _lastDirectory = Path.GetDirectoryName(path);
        ImageFile.Quality = (int)Math.Round(_quality.Value); // what this save writes with
        if (ImageFile.Lossy(Path.GetExtension(path)) && App.Config.SaveQuality != ImageFile.Quality)
        {
            App.Config.SaveQuality = ImageFile.Quality;
            App.Config.Save();
        }
        if (IsVisible)
        {
            DialogResult = true;
        }

        return true;
    }
}

/// <summary>A question / notice in Markshot's look (instead of the Windows message box).</summary>
public static class ThemedMessage
{
    /// <summary>Shows <paramref name="message"/> with buttons; returns the index of the one pressed (-1 = closed / Esc).
    /// The first button is the main one (Enter), the last one is Esc.</summary>
    public static int Show(Window? owner, string title, string message, params string[] buttons)
    {
        var window = new Window
        {
            Title = title,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Topmost = owner?.Topmost ?? true,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        var result = -1;
        var heading = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFF5F5F5));
        var text = new TextBlock { Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 8, 0, 16) };
        text.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFC8C8C8));
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = buttons[i],
                Style = (Style)Application.Current.FindResource(i == 0 ? "PrimaryButton" : "FooterButton"),
                MinWidth = 72,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = i == 0,
                IsCancel = i == buttons.Length - 1 && buttons.Length > 1
            };
            button.Click += (_, _) =>
            {
                result = index;
                window.Close();
            };
            row.Children.Add(button);
        }

        var body = new StackPanel { MinWidth = 300 };
        body.Children.Add(heading);
        body.Children.Add(text);
        body.Children.Add(row);
        var card = new Border
        {
            LayoutTransform = UiScale.Transform,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 16, 20, 14),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.45 },
            Child = body
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF1E1E1E));
        card.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x33FFFFFF));
        window.Content = card;
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                window.Close();
            }
        };
        window.MouseLeftButtonDown += (_, _) => window.DragMove();
        window.Loaded += (_, _) =>
        {
            card.Opacity = 0;
            Motion.PopIn(card, fromAbove: false);
            window.Activate();
            (row.Children[0] as Button)?.Focus();
        };
        window.ShowDialog();
        return result;
    }
}
