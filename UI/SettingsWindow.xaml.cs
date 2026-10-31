using Markshot.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Interop;

namespace Markshot.UI;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    private Point _orderDragStart;
    private ToolOrderItem? _orderDragItem;

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
        TextBypassBox.Text = config.TextBypassModifier;
        PinFlipHBox.Text = config.PinFlipHorizontalHotkey;
        PinFlipVBox.Text = config.PinFlipVerticalHotkey;
        PinRotateBox.Text = config.PinRotateHotkey;
        PinResetBox.Text = config.PinResetHotkey;
        PenKeyBox.Text = config.ToolPenHotkey;
        SelectKeyBox.Text = config.ToolSelectHotkey;
        EraserKeyBox.Text = config.ToolEraserHotkey;
        TextKeyBox.Text = config.ToolTextHotkey;
        NumberKeyBox.Text = config.ToolNumberHotkey;
        LineKeyBox.Text = config.ToolLineHotkey;
        SaveDirBox.Text = config.SaveDirectory;
        OcrLanguageBox.Text = config.OcrLanguage;
        BaseUrlBox.Text = config.Translation.BaseUrl;
        ApiKeyBox.Text = config.Translation.ApiKey;
        ModelBox.Text = config.Translation.Model;
        TargetBox.Text = config.Translation.TargetLanguage;
        TimeoutBox.Text = config.Translation.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        LoadToolOrder(CaptureOrderList, config.CaptureToolOrder, config.CaptureToolHidden);
        LoadToolOrder(PinOrderList, config.PinToolOrder, config.PinToolHidden);

        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            CaptureHotkeyBox.Focus();
            CaptureHotkeyBox.SelectAll();
        }), DispatcherPriority.Input);

        OcrHintText.Text =
            "内置 PP-OCRv5 模型，本地识别中英混排。留空使用中文模型；填 en 切换到英文专用模型。";
    }

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && ManualHotkeyCheck.IsChecked != true)
        {
            box.Tag = box.Text;
            box.SelectAll();
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ManualHotkeyCheck.IsChecked == true)
        {
            return;
        }

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

    private void ManualHotkey_Changed(object sender, RoutedEventArgs e)
    {
        var manual = ManualHotkeyCheck.IsChecked == true;
        CaptureHotkeyBox.IsReadOnly = !manual;
        PinHotkeyBox.IsReadOnly = !manual;
        MouseThroughHotkeyBox.IsReadOnly = !manual;
        RestorePinHotkeyBox.IsReadOnly = !manual;
        CaptureHotkeyBox.Cursor = manual ? Cursors.IBeam : Cursors.Hand;
        PinHotkeyBox.Cursor = manual ? Cursors.IBeam : Cursors.Hand;
        MouseThroughHotkeyBox.Cursor = manual ? Cursors.IBeam : Cursors.Hand;
        RestorePinHotkeyBox.Cursor = manual ? Cursors.IBeam : Cursors.Hand;

        if (manual)
        {
            CaptureHotkeyBox.Focus();
            CaptureHotkeyBox.SelectAll();
        }
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

        _config.CaptureHotkey = capture.Text;
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
        _config.CaptureToolOrder = OrderString(CaptureOrderList);
        _config.CaptureToolHidden = HiddenString(CaptureOrderList);
        _config.PinToolOrder = OrderString(PinOrderList);
        _config.PinToolHidden = HiddenString(PinOrderList);

        var penKey = NormalizeToolKey(PenKeyBox.Text, "B");
        var selectKey = NormalizeToolKey(SelectKeyBox.Text, "V");
        var eraserKey = NormalizeToolKey(EraserKeyBox.Text, "E");
        var textKey = NormalizeToolKey(TextKeyBox.Text, "T");
        var numberKey = NormalizeToolKey(NumberKeyBox.Text, "S");
        var lineKey = NormalizeToolKey(LineKeyBox.Text, "L");
        var toolKeys = new[] { penKey, selectKey, eraserKey, textKey, numberKey, lineKey };
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

        _config.SaveDirectory = SaveDirBox.Text.Trim();
        _config.OcrLanguage = OcrLanguageBox.Text.Trim();

        _config.Translation.BaseUrl = BaseUrlBox.Text.Trim();
        _config.Translation.ApiKey = ApiKeyBox.Text.Trim();
        _config.Translation.Model = ModelBox.Text.Trim();
        _config.Translation.TargetLanguage = TargetBox.Text.Trim();

        _config.Translation.TimeoutSeconds =
            int.TryParse(TimeoutBox.Text.Trim(), out var timeout)
                ? Math.Clamp(timeout, 10, 300)
                : 60;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

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
        StatusText.Text = "测试中…";

        try
        {
            var testConfig = new TranslationConfig
            {
                BaseUrl = BaseUrlBox.Text.Trim(),
                ApiKey = ApiKeyBox.Text.Trim(),
                Model = ModelBox.Text.Trim(),
                TargetLanguage = TargetBox.Text.Trim()
            };

            var provider = TranslationService.Create(testConfig);
            var result = await provider.TranslateAsync(
                "Hello, Markshot!", TargetBox.Text.Trim(), CancellationToken.None);

            StatusText.Text = "连接成功：" + result;
        }
        catch (Exception ex)
        {
            StatusText.Text = "测试失败：" + ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private static readonly (string Key, string Name)[] ToolDefinitions =
    {
        ("Shape", "图形"),
        ("Pen", "画笔/荧光笔"),
        ("Arrow", "箭头/直线"),
        ("Text", "文字"),
        ("Number", "序号"),
        ("Mosaic", "马赛克/水印"),
        ("Eraser", "橡皮擦")
    };

    private static void LoadToolOrder(ListBox list, string? order, string? hidden)
    {
        var hiddenSet = new HashSet<string>(
            (hidden ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        var keys = (order ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        foreach (var (key, _) in ToolDefinitions)
        {
            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                keys.Add(key);
            }
        }

        var items = new ObservableCollection<ToolOrderItem>();
        foreach (var key in keys)
        {
            var definition = ToolDefinitions.FirstOrDefault(t =>
                string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
            if (definition.Key == null)
            {
                continue;
            }

            items.Add(new ToolOrderItem
            {
                Key = definition.Key,
                Name = definition.Name,
                Visible = !hiddenSet.Contains(definition.Key)
            });
        }

        list.ItemsSource = items;
    }

    private static string OrderString(ListBox list) =>
        string.Join(",", (list.ItemsSource as ObservableCollection<ToolOrderItem> ?? new())
            .Select(item => item.Key));

    private static string HiddenString(ListBox list) =>
        string.Join(",", (list.ItemsSource as ObservableCollection<ToolOrderItem> ?? new())
            .Where(item => !item.Visible)
            .Select(item => item.Key));

    private void OrderList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.OriginalSource is CheckBox)
        {
            return;
        }

        _orderDragStart = e.GetPosition(list);
        _orderDragItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?
            .DataContext as ToolOrderItem;
    }

    private void OrderList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _orderDragItem == null || sender is not ListBox list)
        {
            return;
        }

        var current = e.GetPosition(list);
        if (Math.Abs(current.X - _orderDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _orderDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(list, _orderDragItem, DragDropEffects.Move);
        _orderDragItem = null;
    }

    private void OrderList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ToolOrderItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OrderList_Drop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list ||
            !e.Data.GetDataPresent(typeof(ToolOrderItem)) ||
            list.ItemsSource is not ObservableCollection<ToolOrderItem> items)
        {
            return;
        }

        var dragged = (ToolOrderItem)e.Data.GetData(typeof(ToolOrderItem))!;
        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ToolOrderItem;
        var oldIndex = items.IndexOf(dragged);
        if (oldIndex < 0)
        {
            return;
        }

        items.RemoveAt(oldIndex);
        var newIndex = target == null ? items.Count : items.IndexOf(target);
        if (newIndex < 0)
        {
            newIndex = items.Count;
        }

        items.Insert(newIndex, dragged);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    public sealed class ToolOrderItem : INotifyPropertyChanged
    {
        public string Key { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        private bool _visible = true;

        public bool Visible
        {
            get => _visible;
            set
            {
                if (_visible == value)
                {
                    return;
                }

                _visible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visible)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
