using Markshot.Services;
using Microsoft.Win32;
using System.Windows.Interop;

namespace Markshot.UI;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private bool _syncingKey;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();

        _config = config;
        SourceInitialized += (_, _) =>
            Native.EnableDarkTitleBar(new WindowInteropHelper(this).Handle);

        CaptureHotkeyBox.Text = config.CaptureHotkey;
        PinHotkeyBox.Text = config.PinHotkey;
        MouseThroughHotkeyBox.Text = config.MouseThroughHotkey;
        RestorePinHotkeyBox.Text = config.RestorePinHotkey;
        RecordHotkeyBox.Text = config.RecordHotkey;
        TextBypassBox.Text = config.TextBypassModifier;
        PinFlipHBox.Text = config.PinFlipHorizontalHotkey;
        _shadowColor = config.PinShadowColor;
        ShadowSizeSlider.Value = config.PinShadowSize;
        ShadowOpacitySlider.Value = Math.Round(config.PinShadowOpacity * 100);
        UpdateShadowPreview();
        PinFlipVBox.Text = config.PinFlipVerticalHotkey;
        PinRotateBox.Text = config.PinRotateHotkey;
        PinResetBox.Text = config.PinResetHotkey;
        PenKeyBox.Text = config.ToolPenHotkey;
        SelectKeyBox.Text = config.ToolSelectHotkey;
        EraserKeyBox.Text = config.ToolEraserHotkey;
        TextKeyBox.Text = config.ToolTextHotkey;
        NumberKeyBox.Text = config.ToolNumberHotkey;
        LineKeyBox.Text = config.ToolLineHotkey;
        ShapeKeyBox.Text = config.ToolShapeHotkey;
        CopyFileCheck.IsChecked = config.CopyImageAsFile;
        FillToolbarRow(CaptureToolsRow, ToolbarCatalog.Tools, config.CaptureToolOrder, config.CaptureToolHidden);
        FillToolbarRow(CaptureActionsRow, ToolbarCatalog.CaptureActions, config.CaptureActionOrder, config.CaptureActionHidden);
        FillToolbarRow(PinToolsRow, ToolbarCatalog.Tools, config.PinToolOrder, config.PinToolHidden);
        FillToolbarRow(PinActionsRow, ToolbarCatalog.PinActions, config.PinActionOrder, config.PinActionHidden);
        AskOpenFolderCheck.IsChecked = config.AskOpenFolderAfterSave;
        SaveDirBox.Text = config.SaveDirectory;
        UpdatePathBox.Text = UpdateService.EffectivePath(config);
        Loaded += (_, _) => RefreshUpdate(quiet: true);
        OcrLanguageBox.Text = config.OcrLanguage;
        BaseUrlBox.Text = config.Translation.BaseUrl;
        ApiKeyPassword.Password = config.Translation.ApiKey;
        ModelBox.Text = config.Translation.Model;
        TargetBox.Text = config.Translation.TargetLanguage;
        TimeoutBox.Text = config.Translation.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ImmersiveSwitch.IsChecked = config.ImmersiveTranslate;

        VersionText.Text = "版本 " + AppVersion.Display;
        ConfigPathText.Text = AppConfig.ConfigPath;
        ConfigPathText.ToolTip = AppConfig.ConfigPath;
        if (!config.Translation.IsConfigured)
        {
            StatusText.Text = "提示：还没有填写翻译 API Key，原位翻译和文字翻译需要它（见“翻译”页）。";
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHotkeys == null || sender is not RadioButton { Tag: string page })
        {
            return;
        }

        PageHotkeys.Visibility = page == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PageTranslate.Visibility = page == "Translate" ? Visibility.Visible : Visibility.Collapsed;
        PageGeneral.Visibility = page == "General" ? Visibility.Visible : Visibility.Collapsed;
        PageToolbar.Visibility = page == "Toolbar" ? Visibility.Visible : Visibility.Collapsed;
        Motion.EnterPage(page switch
        {
            "Translate" => PageTranslate,
            "Toolbar" => PageToolbar,
            "General" => PageGeneral,
            _ => PageHotkeys
        });
    }

    /// <summary>Opens the window on a given page ("Hotkeys", "Translate", "Toolbar", "General").</summary>
    public void ShowPage(string page)
    {
        switch (page)
        {
            case "Translate":
                NavTranslate.IsChecked = true;
                break;
            case "General":
                NavGeneral.IsChecked = true;
                break;
            case "Toolbar":
                NavToolbar.IsChecked = true;
                break;
            default:
                NavHotkeys.IsChecked = true;
                break;
        }
    }

    private string ApiKey => ApiKeyBox.Visibility == Visibility.Visible ? ApiKeyBox.Text : ApiKeyPassword.Password;

    private void ApiKeyReveal_Click(object sender, RoutedEventArgs e)
    {
        var reveal = ApiKeyBox.Visibility != Visibility.Visible;
        _syncingKey = true;
        if (reveal)
        {
            ApiKeyBox.Text = ApiKeyPassword.Password;
        }
        else
        {
            ApiKeyPassword.Password = ApiKeyBox.Text;
        }

        _syncingKey = false;
        ApiKeyBox.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyPassword.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        ApiKeyReveal.Content = reveal ? "隐藏" : "显示";
    }

    private void ApiKeyPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingKey && TestStatusText != null)
        {
            TestStatusText.Text = string.Empty;
        }
    }

    private void ApiKeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingKey && TestStatusText != null)
        {
            TestStatusText.Text = string.Empty;
        }
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string language })
        {
            TargetBox.Text = language;
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = AppConfig.ConfigPath;
            if (File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                var directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                Process.Start("explorer.exe", $"\"{directory}\"");
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法打开文件夹：" + ex.Message;
        }
    }

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.Tag = box.Text;
            box.SelectAll();
        }
    }

    /// <summary>A focused hotkey box takes the middle / side mouse buttons as the hotkey.</summary>
    private void HotkeyBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || !box.IsKeyboardFocused ||
            e.ChangedButton is not (MouseButton.Middle or MouseButton.XButton1 or MouseButton.XButton2))
        {
            return;
        }

        box.Text = HotkeyGesture.FormatMouse(e.ChangedButton, Keyboard.Modifiers);
        box.CaretIndex = box.Text.Length;
        e.Handled = true;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        // Alt combinations arrive as Key.System; the real key is in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = HandleHotkeyKey(box, key, Keyboard.Modifiers);
    }

    private bool HandleHotkeyKey(TextBox box, Key key, ModifierKeys modifiers)
    {
        if (IsModifierKey(key))
        {
            box.Text = FormatModifierPreview(modifiers);
            return true;
        }

        if (key == Key.Escape)
        {
            box.Text = box.Tag as string ?? box.Text;
            return true;
        }

        if (key is Key.Back or Key.Delete)
        {
            box.Text = string.Empty;
            return true;
        }

        // Let Tab navigate and let a bare Enter trigger the default Save button.
        if (key == Key.Tab && modifiers == ModifierKeys.None)
        {
            return false;
        }

        if (key == Key.Enter && modifiers == ModifierKeys.None)
        {
            return false;
        }

        if (HotkeyGesture.TryFormat(key, modifiers, out var text))
        {
            box.Text = text;
            box.CaretIndex = text.Length;
            return true;
        }

        return false;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    private static string NormalizeToolKey(string text, string fallback)
    {
        var value = (text ?? string.Empty).Trim().ToUpperInvariant();
        if (value.Length == 1 && char.IsLetterOrDigit(value[0]))
        {
            return value;
        }

        return fallback;
    }

    private static string FormatModifierPreview(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        return parts.Count == 0 ? "…" : string.Join("+", parts) + "+…";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!HotkeyGesture.TryParse(CaptureHotkeyBox.Text, out var capture, out var error))
        {
            MessageBox.Show(this, "截图快捷键无效：" + error, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hasPinHotkey = !string.IsNullOrWhiteSpace(PinHotkeyBox.Text);
        var pin = default(HotkeyGesture);
        if (hasPinHotkey && !HotkeyGesture.TryParse(PinHotkeyBox.Text, out pin, out error))
        {
            MessageBox.Show(this, "贴图快捷键无效：" + error, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (hasPinHotkey &&
            string.Equals(capture.Text, pin.Text, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "截图和贴图不能使用同一个快捷键。", "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hasMouseThrough = !string.IsNullOrWhiteSpace(MouseThroughHotkeyBox.Text);
        var mouseThrough = default(HotkeyGesture);
        if (hasMouseThrough &&
            !HotkeyGesture.TryParse(MouseThroughHotkeyBox.Text, out mouseThrough, out error))
        {
            MessageBox.Show(this, "鼠标穿透快捷键无效：" + error, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hasRestore = !string.IsNullOrWhiteSpace(RestorePinHotkeyBox.Text);
        var restore = default(HotkeyGesture);
        if (hasRestore && !HotkeyGesture.TryParse(RestorePinHotkeyBox.Text, out restore, out error))
        {
            MessageBox.Show(this, "返回上一贴图快捷键无效：" + error, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hasRecord = !string.IsNullOrWhiteSpace(RecordHotkeyBox.Text);
        var record = default(HotkeyGesture);
        if (hasRecord && !HotkeyGesture.TryParse(RecordHotkeyBox.Text, out record, out error))
        {
            MessageBox.Show(this, "录屏快捷键无效：" + error, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (hasRecord && string.Equals(capture.Text, record.Text, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "截图和录屏不能使用同一个快捷键。", "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.CaptureHotkey = capture.Text;
        _config.RecordHotkey = hasRecord ? record.Text : "";
        _config.PinHotkey = hasPinHotkey ? pin.Text : "";
        _config.MouseThroughHotkey = hasMouseThrough ? mouseThrough.Text : "";
        _config.RestorePinHotkey = hasRestore ? restore.Text : "";
        _config.TextBypassModifier = string.IsNullOrWhiteSpace(TextBypassBox.Text)
            ? "Alt"
            : TextBypassBox.Text.Trim();
        _config.PinFlipHorizontalHotkey = string.IsNullOrWhiteSpace(PinFlipHBox.Text) ? "Q" : PinFlipHBox.Text.Trim();
        _config.PinFlipVerticalHotkey = string.IsNullOrWhiteSpace(PinFlipVBox.Text) ? "W" : PinFlipVBox.Text.Trim();
        _config.PinRotateHotkey = string.IsNullOrWhiteSpace(PinRotateBox.Text) ? "E" : PinRotateBox.Text.Trim();
        _config.PinResetHotkey = string.IsNullOrWhiteSpace(PinResetBox.Text) ? "F" : PinResetBox.Text.Trim();

        var penKey = NormalizeToolKey(PenKeyBox.Text, "B");
        var selectKey = NormalizeToolKey(SelectKeyBox.Text, "V");
        var eraserKey = NormalizeToolKey(EraserKeyBox.Text, "E");
        var textKey = NormalizeToolKey(TextKeyBox.Text, "T");
        var numberKey = NormalizeToolKey(NumberKeyBox.Text, "S");
        var lineKey = NormalizeToolKey(LineKeyBox.Text, "L");
        var shapeKey = NormalizeToolKey(ShapeKeyBox.Text, "M");
        var toolKeys = new[] { penKey, selectKey, eraserKey, textKey, numberKey, lineKey, shapeKey };
        if (toolKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != toolKeys.Length)
        {
            MessageBox.Show(this, "标注工具快捷键不能重复。", "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.ToolPenHotkey = penKey;
        _config.ToolSelectHotkey = selectKey;
        _config.ToolEraserHotkey = eraserKey;
        _config.ToolTextHotkey = textKey;
        _config.ToolNumberHotkey = numberKey;
        _config.ToolLineHotkey = lineKey;
        _config.ToolShapeHotkey = shapeKey;
        _config.PinShadowSize = Math.Round(ShadowSizeSlider.Value);
        _config.PinShadowOpacity = Math.Round(ShadowOpacitySlider.Value) / 100;
        _config.PinShadowColor = _shadowColor;
        _config.CopyImageAsFile = CopyFileCheck.IsChecked == true;
        (_config.CaptureToolOrder, _config.CaptureToolHidden) = ReadToolbarRow(CaptureToolsRow);
        (_config.CaptureActionOrder, _config.CaptureActionHidden) = ReadToolbarRow(CaptureActionsRow);
        (_config.PinToolOrder, _config.PinToolHidden) = ReadToolbarRow(PinToolsRow);
        (_config.PinActionOrder, _config.PinActionHidden) = ReadToolbarRow(PinActionsRow);
        _config.AskOpenFolderAfterSave = AskOpenFolderCheck.IsChecked == true;

        _config.SaveDirectory = SaveDirBox.Text.Trim();
        // Keep following the installer's folder unless another one was entered.
        var updatePath = UpdatePathBox.Text.Trim();
        _config.UpdatePath = updatePath; // optional; the built-in GitHub source is always used as well
        _config.OcrLanguage = OcrLanguageBox.Text.Trim();

        _config.Translation.BaseUrl = BaseUrlBox.Text.Trim();
        _config.Translation.ApiKey = ApiKey.Trim();
        _config.Translation.Model = ModelBox.Text.Trim();
        _config.Translation.TargetLanguage = TargetBox.Text.Trim();
        _config.ImmersiveTranslate = ImmersiveSwitch.IsChecked == true;

        _config.Translation.TimeoutSeconds =
            int.TryParse(TimeoutBox.Text.Trim(), out var timeout)
                ? Math.Clamp(timeout, 10, 300)
                : 60;

        Saved = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>True when the window was closed with “保存”.</summary>
    public bool Saved { get; private set; }

    // ---- coffee drawer ------------------------------------------------------------------
    private const double DrawerFull = 144; // QR (136) + its top margin
    private bool _drawerDragging;
    private bool _drawerMoved;
    private double _drawerStartY;
    private double _drawerStartHeight;

    private double DrawerHeight => double.IsNaN(DrawerPanel.Height) ? 0 : DrawerPanel.Height;

    private void DrawerHandle_MouseEnter(object sender, MouseEventArgs e)
    {
        DrawerBar.Background = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        if (DrawerHeight < 1)
        {
            Motion.Fade(DrawerHint, 1);
        }
    }

    private void DrawerHandle_MouseLeave(object sender, MouseEventArgs e)
    {
        DrawerBar.Background = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
        if (!_drawerDragging)
        {
            Motion.Fade(DrawerHint, 0);
        }
    }

    private void DrawerHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var height = DrawerHeight;
        DrawerPanel.BeginAnimation(HeightProperty, null);
        DrawerPanel.Height = height;
        _drawerDragging = true;
        _drawerMoved = false;
        _drawerStartY = e.GetPosition(this).Y;
        _drawerStartHeight = height;
        DrawerHandle.CaptureMouse();
        e.Handled = true;
    }

    private void DrawerHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawerDragging)
        {
            return;
        }

        var delta = _drawerStartY - e.GetPosition(this).Y;
        if (Math.Abs(delta) > 3)
        {
            _drawerMoved = true;
        }

        // Follows the pointer upwards and stops once the QR code is fully out.
        DrawerPanel.Height = Math.Clamp(_drawerStartHeight + delta, 0, DrawerFull);
        DrawerHint.Opacity = DrawerPanel.Height < 1 ? 1 : 0;
    }

    private void DrawerHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawerDragging)
        {
            return;
        }

        _drawerDragging = false;
        DrawerHandle.ReleaseMouseCapture();
        var height = DrawerHeight;
        double target;
        if (!_drawerMoved)
        {
            target = height > 1 ? 0 : DrawerFull; // a click puts it away (or pulls it out)
        }
        else
        {
            target = height > DrawerFull * 0.4 ? DrawerFull : 0;
        }

        DrawerPanel.BeginAnimation(HeightProperty, new System.Windows.Media.Animation.DoubleAnimation(height, target, Motion.Base)
        {
            EasingFunction = Motion.Ease
        });
        Motion.Fade(DrawerHint, target < 1 && DrawerHandle.IsMouseOver ? 1 : 0);
        e.Handled = true;
    }

    // ---- annotation bar buttons: click = show / hide, long-press drag = order ----------------
    private readonly HashSet<string> _hiddenButtons = new();

    private void FillToolbarRow(Panel row, string[] defaults, string order, string hidden)
    {
        var hiddenSet = ToolbarCatalog.Set(hidden);
        var reorder = new ReorderBehavior(row);
        row.Children.Clear();
        foreach (var key in ToolbarCatalog.Order(order, defaults))
        {
            // A plain tile (no control template): the icon in white, a hover plate, click = on / off.
            var icon = ToolbarCatalog.Icon(key);
            if (icon is Border mask)
            {
                mask.Background = Brushes.White;
            }
            else if (icon is System.Windows.Shapes.Shape shape)
            {
                shape.Stroke = Brushes.White;
            }

            var button = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = key,
                Child = icon,
                ToolTip = ToolbarCatalog.Label(key) + "：单击开关，长按拖动排序"
            };
            if (icon is FrameworkElement iconElement)
            {
                iconElement.HorizontalAlignment = HorizontalAlignment.Center;
                iconElement.VerticalAlignment = VerticalAlignment.Center;
            }

            button.MouseEnter += (_, _) => button.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            var id = RowId(row, key);
            if (hiddenSet.Contains(key))
            {
                _hiddenButtons.Add(id);
                icon.Opacity = 0.28; // dimmed = not shown on the bar
            }

            button.MouseLeftButtonUp += (_, _) =>
            {
                if (reorder.JustDragged || reorder.IsDragging)
                {
                    return;
                }

                var off = _hiddenButtons.Add(id) || !_hiddenButtons.Remove(id);
                Motion.Fade(icon, off ? 0.28 : 1);
            };
            row.Children.Add(button);
        }
    }

    private (string Order, string Hidden) ReadToolbarRow(Panel row)
    {
        var keys = row.Children.OfType<FrameworkElement>().Select(child => child.Tag as string ?? "").ToList();
        return (string.Join(",", keys), string.Join(",", keys.Where(key => _hiddenButtons.Contains(RowId(row, key)))));
    }

    private static string RowId(Panel row, string key) => row.Name + ":" + key;

    private string _shadowColor = "#6A6A6A";

    private void Shadow_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateShadowPreview();

    private void UpdateShadowPreview()
    {
        if (ShadowSizeValue == null || ShadowOpacityValue == null)
        {
            return;
        }

        ShadowSizeValue.Text = $"{Math.Round(ShadowSizeSlider.Value)}";
        ShadowOpacityValue.Text = $"{Math.Round(ShadowOpacitySlider.Value)}%";
        if (IsLoaded && Application.Current is App app)
        {
            // Live on the open pins while dragging; saved or dropped when the window closes.
            app.PreviewPinShadow((Math.Round(ShadowSizeSlider.Value), _shadowColor, Math.Round(ShadowOpacitySlider.Value) / 100));
        }
        try
        {
            ShadowColorSwatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_shadowColor));
        }
        catch (FormatException)
        {
            ShadowColorSwatch.Background = Brushes.Gray;
        }
    }

    private void ShadowColor_Click(object sender, RoutedEventArgs e)
    {
        Color current;
        try
        {
            current = (Color)ColorConverter.ConvertFromString(_shadowColor);
        }
        catch (FormatException)
        {
            current = Color.FromRgb(0x6A, 0x6A, 0x6A);
        }

        var picker = new ColorPickerWindow(Color.FromArgb((byte)Math.Round(ShadowOpacitySlider.Value * 2.55), current.R, current.G, current.B))
        {
            Owner = this
        };
        picker.ShowDialog();
        if (picker.Accepted)
        {
            var chosen = picker.SelectedColor;
            _shadowColor = $"#{chosen.R:X2}{chosen.G:X2}{chosen.B:X2}";
            ShadowOpacitySlider.Value = Math.Round(chosen.A / 2.55); // the picker's alpha is the shadow opacity
            UpdateShadowPreview();
        }
    }

    private UpdateService.Package? _update;

    /// <summary>Looks for a newer installer at the update source shown in the box (GitHub or a folder).</summary>
    private async void RefreshUpdate(bool quiet)
    {
        var folder = Environment.ExpandEnvironmentVariables(UpdatePathBox.Text.Trim());
        if (!quiet)
        {
            StatusText.Text = "正在检查更新…";
        }

        _update = await UpdateService.FindNewestAsync(folder);
        if (!IsLoaded)
        {
            return;
        }

        if (_update != null)
        {
            UpdateButton.Content = $"更新 {_update.Version}";
            UpdateButton.ToolTip = _update.Path;
            UpdateButton.Visibility = Visibility.Visible;
            Motion.PopIn(UpdateButton, fromAbove: false);
            return;
        }

        UpdateButton.Visibility = Visibility.Collapsed;
        if (!quiet)
        {
            StatusText.Text = !string.IsNullOrWhiteSpace(folder) && !UpdateService.IsGitHub(folder) && !Directory.Exists(folder)
                ? $"已是最新版本（{AppVersion.Display}）。自定义更新路径不存在：{folder}"
                : $"已是最新版本（{AppVersion.Display}）";
        }
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e) => RefreshUpdate(quiet: false);

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null)
        {
            return;
        }

        var result = MessageBox.Show(this, $"关闭 Markshot 并安装 {_update.Version}？", "更新 Markshot",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            UpdateButton.IsEnabled = false;
            var progress = new Progress<double>(value => StatusText.Text = $"正在下载更新… {value:P0}");
            var local = await UpdateService.DownloadAsync(_update, progress);
            UpdateService.Install(local);
        }
        catch (Exception ex)
        {
            Log.Error("下载更新失败", ex);
            StatusText.Text = "下载更新失败：" + ex.Message;
            UpdateButton.IsEnabled = true;
        }
    }

    private void BrowseUpdate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择放安装包的文件夹",
            InitialDirectory = UpdatePathBox.Text.Trim()
        };

        if (dialog.ShowDialog(this) == true)
        {
            UpdatePathBox.Text = dialog.FolderName;
            RefreshUpdate(quiet: false);
        }
    }

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
        TestStatusText.Foreground = (Brush)FindResource("TextSecondary");
        TestStatusText.Text = "测试中…";

        try
        {
            var testConfig = new TranslationConfig
            {
                BaseUrl = BaseUrlBox.Text.Trim(),
                ApiKey = ApiKey.Trim(),
                TimeoutSeconds = int.TryParse(TimeoutBox.Text.Trim(), out var seconds) ? seconds : 30,
                Model = ModelBox.Text.Trim(),
                TargetLanguage = TargetBox.Text.Trim()
            };

            var provider = TranslationService.Create(testConfig);
            var result = await provider.TranslateAsync(
                "Hello, Markshot!", TargetBox.Text.Trim(), CancellationToken.None);

            TestStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8A));
            TestStatusText.Text = "连接成功：" + result;
            TestStatusText.ToolTip = result;
        }
        catch (Exception ex)
        {
            TestStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A));
            TestStatusText.Text = "测试失败：" + ex.Message;
            TestStatusText.ToolTip = ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }
}
