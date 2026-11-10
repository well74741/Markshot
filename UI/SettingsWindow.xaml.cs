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
        Theme.TrackTitleBar(this);
        UiScale.AttachWindow(this);
        // The title bar's height on the screen (the content is scaled), the dragging area.
        System.Windows.Shell.WindowChrome.GetWindowChrome(this).CaptionHeight = TitleBar.Height * UiScale.SettingsFactor;
        StateChanged += (_, _) => FitMaximized();
        Activated += (_, _) => TitleText.Opacity = 1;
        Deactivated += (_, _) => TitleText.Opacity = 0.55;

        CaptureHotkeyBox.Text = config.CaptureHotkey;
        PinHotkeyBox.Text = config.PinHotkey;
        MouseThroughHotkeyBox.Text = config.MouseThroughHotkey;
        RestorePinHotkeyBox.Text = config.RestorePinHotkey;
        CloseAllPinsHotkeyBox.Text = config.CloseAllPinsHotkey;
        HotkeysDisabledCheck.IsChecked = config.HotkeysDisabled;
        WinMouseCheck.IsChecked = config.WinMouseEnabled;
        _winMouse = new Dictionary<string, string>
        {
            ["Left"] = config.WinMouseLeft, ["Middle"] = config.WinMouseMiddle, ["Right"] = config.WinMouseRight
        };
        BuildWinMouseRows();
        RecordHotkeyBox.Text = config.RecordHotkey;
        TextBypassBox.Text = config.TextBypassModifier;
        PinFlipHBox.Text = config.PinFlipHorizontalHotkey;
        _theme = config.Theme;
        _uiSize = config.UiSize;
        (_theme switch { "Light" => ThemeLight, "System" => ThemeSystem, _ => ThemeDark }).IsChecked = true;
        (string.Equals(_uiSize, "Large", StringComparison.OrdinalIgnoreCase) ? SizeLarge : string.Equals(_uiSize, "Medium", StringComparison.OrdinalIgnoreCase) ? SizeMedium : SizeSmall).IsChecked = true;
        UpdateDrawerQr();
        Theme.Changed += UpdateDrawerQr;
        Closed += (_, _) => Theme.Changed -= UpdateDrawerQr;
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
        DetectAutoCheck.IsChecked = config.DetectAuto;
        DetectSensitivitySlider.Value = Math.Clamp(config.DetectRange, 1, 4);
        DetectElementsCheck.IsChecked = config.DetectElements;
        DetectWindowsCheck.IsChecked = config.DetectWindows;
        DetectAuto_Click(this, new RoutedEventArgs());
        CaptureCursorCheck.IsChecked = config.CaptureShowCursor;
        OpenWithCheck.IsChecked = config.RegisterOpenWith;
        CaptureCursorKeyBox.Text = config.CaptureCursorKey;
        CrosshairKeyBox.Text = config.CrosshairKey;
        AutoStartCheck.IsChecked = AutoStart.IsEnabled;
        FillToolbarRow(CaptureToolsRow, ToolbarCatalog.Tools, config.CaptureToolOrder, config.CaptureToolHidden);
        FillToolbarRow(CaptureActionsRow, ToolbarCatalog.CaptureActions, config.CaptureActionOrder, config.CaptureActionHidden);
        FillToolbarRow(PinToolsRow, ToolbarCatalog.Tools, config.PinToolOrder, config.PinToolHidden);
        FillToolbarRow(PinActionsRow, ToolbarCatalog.PinActions, config.PinActionOrder, config.PinActionHidden);
        FillToolbarRow(EditorToolsRow, ToolbarCatalog.Tools, config.EditorToolOrder, config.EditorToolHidden);
        FillToolbarRow(EditorActionsRow, ToolbarCatalog.EditorActions, config.EditorActionOrder, config.EditorActionHidden);
        InitFloat();
        AskOpenFolderCheck.IsChecked = config.AskOpenFolderAfterSave;
        EscExitsCheck.IsChecked = config.EscExitsCapture;
        QuickDoubleClickCheck.IsChecked = config.QuickDoubleClickCopy;
        PinTextSelectCheck.IsChecked = config.PinTextSelect;
        PinTextSelectCheck.Checked += (_, _) => TextBypassBox.IsEnabled = true;
        PinTextSelectCheck.Unchecked += (_, _) => TextBypassBox.IsEnabled = false;
        TextBypassBox.IsEnabled = config.PinTextSelect;
        HideChevronsCheck.IsChecked = config.HideToolChevrons;
        SaveDirBox.Text = config.SaveDirectory;
        Loaded += (_, _) => RefreshUpdate(quiet: true);
        OcrLanguageBox.Text = config.OcrLanguage;
        BaseUrlBox.Text = config.Translation.BaseUrl;
        ApiKeyPassword.Password = config.Translation.ApiKey;
        ModelBox.Text = config.Translation.Model;
        TargetBox.Text = config.Translation.TargetLanguage;
        TimeoutBox.Text = config.Translation.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        InitOffline();
        ImmersiveSwitch.IsChecked = config.ImmersiveTranslate;

        VersionText.Text = "版本 " + AppVersion.Display;
        ConfigPathText.Text = AppConfig.ConfigPath;
        ConfigPathText.ToolTip = AppConfig.ConfigPath;
        if (!config.Translation.IsConfigured && !Services.OfflinePacks.AnyInstalled)
        {
            StatusText.Text = "未设置翻译：填 API Key 或下载离线包（“翻译”页）";
        }

        PreviewKeyDown += OnPreviewKeyDownFloat;
        foreach (var name in LiveSettings)
        {
            _liveSaved[name] = typeof(AppConfig).GetProperty(name)!.GetValue(config);
        }

        // Any change made in the window (typed, ticked, slid, picked) is a change to save.
        AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new RoutedEventHandler(OnSettingEdited));
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(OnSettingEdited));
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(OnSettingEdited));
        AddHandler(System.Windows.Controls.Primitives.RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((s, e) => OnSettingEdited(s, e)));
        AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler(OnSettingEdited));
        Closing += OnClosingAsk;
        Closed += (_, _) =>
        {
            _notesClose.Stop();
            if (_notesPopup != null)
            {
                _notesPopup.IsOpen = false;
            }
        };
        PreviewKeyDown += OnEscape;
        PreviewMouseDown += PutDrawerAwayOnOutsideClick;
        _savedPrint = Fingerprint(); // what "nothing changed" looks like
        Activated += (_, _) => _activatedAt = Environment.TickCount64;
        _ready = true;
    }

    /// <summary>Settings the window applies at once for a preview (theme, size, the floating button): put back when not saved.</summary>
    private static readonly string[] LiveSettings =
    {
        "Theme", "UiSize", "FloatButtonEnabled", "FloatHideFullscreen", "FloatActionOrder", "FloatActionHidden",
        "FloatIcon", "FloatIconOpacity", "FloatIconSize"
    };

    private readonly Dictionary<string, object?> _liveSaved = new();
    private bool _dirty;
    private bool _asked;
    private string _savedPrint = "";

    /// <summary>
    /// There are changes that 保存 has not kept yet: what the window shows now differs from what it showed when opened
    /// (or last saved). Scrolling, moving between pages, changing something and back again change nothing.
    /// </summary>
    public bool HasUnsavedChanges => _dirty = _ready && Fingerprint() != _savedPrint;

    /// <summary>Every value the window can change, in one string.</summary>
    private string Fingerprint()
    {
        var text = new StringBuilder();
        void Walk(object node)
        {
            switch (node)
            {
                case System.Windows.Controls.Primitives.ScrollBar:
                    return;
                case TextBox { Name.Length: > 0 } box:
                    text.Append(box.Name).Append('=').Append(box.Text).Append('\n');
                    break;
                case PasswordBox { Name.Length: > 0 } password:
                    text.Append(password.Name).Append('=').Append(password.Password.GetHashCode()).Append('\n');
                    break;
                case System.Windows.Controls.Primitives.ToggleButton { Name.Length: > 0 } toggle when toggle is not RadioButton { GroupName: "Nav" }:
                    text.Append(toggle.Name).Append('=').Append(toggle.IsChecked).Append('\n');
                    break;
                case Slider { Name.Length: > 0 } slider when slider.Name != "DrawerQrSlider":
                    text.Append(slider.Name).Append('=').Append(Math.Round(slider.Value, 2)).Append('\n');
                    break;
            }

            if (node is DependencyObject element)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(element))
                {
                    Walk(child);
                }
            }
        }

        Walk(this);
        foreach (var row in new Panel[] { CaptureToolsRow, CaptureActionsRow, PinToolsRow, PinActionsRow, EditorToolsRow, EditorActionsRow, FloatActionsRow })
        {
            var (order, hidden) = ReadToolbarRow(row);
            text.Append(row.Name).Append('=').Append(order).Append('|').Append(hidden).Append('\n');
        }

        text.Append(_shadowColor).Append('|').Append(_theme).Append('|').Append(_uiSize).Append('|')
            .Append(string.Join(",", _winMouse.Select(pair => pair.Key + ":" + pair.Value))).Append('|').Append(_floatClick).Append('|')
            .Append(string.Join(",", LiveSettings.Select(name => typeof(AppConfig).GetProperty(name)!.GetValue(_config))));
        return text.ToString();
    }

    /// <summary>Tests: the answer the close question gets (0 保存, 1 不保存, 2 取消).</summary>
    internal Func<int>? AskOverride;

    /// <summary>Tests: the answer for every settings window (no question box that would wait for a click).</summary>
    internal static Func<int>? AskAll;

    // (Kept for the places that change something: whether it is a change is decided by comparing, see HasUnsavedChanges.)
    private void MarkDirty()
    {
    }

    private void OnSettingEdited(object sender, RoutedEventArgs e)
    {
        // Moving between pages is no change.
        if (e.OriginalSource is RadioButton { GroupName: "Nav" } || e.OriginalSource is FrameworkElement { Name: "DrawerQrSlider" })
        {
            return;
        }

        MarkDirty();
    }

    private void OnClosingAsk(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_asked || !HasUnsavedChanges)
        {
            return;
        }

        if (_closingNow)
        {
            e.Cancel = true;
            return;
        }

        _closingNow = true;
        try
        {
            AskBeforeClosing(e);
        }
        finally
        {
            _closingNow = false;
        }
    }

    private void AskBeforeClosing(System.ComponentModel.CancelEventArgs e)
    {
        var answer = AskOverride?.Invoke() ?? AskAll?.Invoke() ??
                     ThemedMessage.Show(this, "设置未保存", "修改的设置还没有保存，关闭后不会生效。要保存吗？", "保存", "不保存", "取消");
        if (answer == 0)
        {
            var saves = _saves;
            Save_Click(this, new RoutedEventArgs());
            if (_saves == saves)
            {
                e.Cancel = true; // a setting is not valid: the message said which
                return;
            }
        }
        else if (answer == 1)
        {
            DiscardLive();
        }
        else
        {
            e.Cancel = true;
            return;
        }

        _asked = true;
    }

    /// <summary>不保存: the live previews go back to what is saved (the floating button, its picture; the app restores the theme).</summary>
    private void DiscardLive()
    {
        var newIcon = FloatIcon.CustomPath(_config);
        foreach (var (name, value) in _liveSaved)
        {
            typeof(AppConfig).GetProperty(name)!.SetValue(_config, value);
        }

        if (newIcon != null && !string.Equals(newIcon, FloatIcon.CustomPath(_config), StringComparison.OrdinalIgnoreCase))
        {
            FloatIcon.Forget(newIcon); // a picture taken in here and not kept
        }

        _dirty = false;
        RunningApp?.ApplyFloatButton();
        RunningApp?.RefreshFloatLook();
    }

    private bool _ready; // the constructor has filled everything in: changes from here on are the user's
    private int _saves;

    // ---- 悬浮按钮: every change applies and is saved at once ----------------------------------

    private void InitFloat()
    {
        FloatEnabledCheck.IsChecked = _config.FloatButtonEnabled;
        FloatFullscreenCheck.IsChecked = _config.FloatHideFullscreen;
        FloatCaptureHideCheck.IsChecked = _config.FloatHideWhileCapturing;
        var reorder = FillToolbarRow(FloatActionsRow, FloatButtonWindow.Actions, _config.FloatActionOrder, _config.FloatActionHidden);
        reorder.Reordered += _ =>
        {
            ApplyFloatRow();
            Dispatcher.BeginInvoke(() => PlaceClickKnob(animate: false), DispatcherPriority.Loaded); // the knob stays on its button
        };
        FloatActionsRow.MouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(ApplyFloatRow, DispatcherPriority.Input);
        _floatClick = FloatButtonWindow.Actions.Contains(_config.FloatClickAction) ? _config.FloatClickAction : "Capture";
        BuildClickTrack();
        FloatSizeSlider.Value = Math.Round(Math.Clamp(_config.FloatIconSize, 1, 2) * 100);
        FloatSizeValue.Text = $"{FloatSizeSlider.Value:0}%";
        FloatOpacitySlider.Value = Math.Round(Math.Clamp(_config.FloatIconOpacity, 0.2, 1) * 100);
        FloatOpacityValue.Text = $"{FloatOpacitySlider.Value:0}%";
        ShowFloatIcon();
    }

    private static App? RunningApp => Application.Current as App;

    // ---- 离线翻译包 ------------------------------------------------------------------------------------------
    private readonly Dictionary<string, CancellationTokenSource> _downloads = new();

    private void InitOffline()
    {
        (_config.Translation.Engine switch { "Offline" => EngineOffline, "Api" => EngineApi, _ => EngineAuto }).IsChecked = true;
        MirrorRow.Children.Clear();
        foreach (var (id, label, _) in Services.OfflinePacks.Mirrors)
        {
            MirrorRow.Children.Add(new RadioButton
            {
                Name = "Mirror_" + id.Replace("-", "_"),
                Style = (Style)FindResource("SegmentRadio"),
                GroupName = "Mirror",
                Tag = id,
                Content = label,
                IsChecked = id == _config.Translation.OfflineMirror
            });
        }

        if (MirrorRow.Children.OfType<RadioButton>().All(radio => radio.IsChecked != true))
        {
            ((RadioButton)MirrorRow.Children[0]).IsChecked = true;
        }

        BuildPackRows();
        BuildOcrPackRows();
    }

    /// <summary>识别模型: the optional recognition packs (公式识别), each with 下载 / 删除 or 取消 and its progress.</summary>
    private void BuildOcrPackRows()
    {
        OcrPackRows.Children.Clear();
        foreach (var pack in Services.OcrPacks.All)
        {
            var key = "ocr:" + pack.Id;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), Tag = key };
            var installed = Services.OcrPacks.IsInstalled(pack);
            var busy = _downloads.ContainsKey(key);
            var action = new Button { Style = (Style)FindResource("FooterButton"), Content = busy ? "取消" : installed ? "删除" : "下载", MinWidth = 64, Tag = pack.Id };
            action.Click += OcrPackAction_Click;
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(action);
            var name = new TextBlock { Text = pack.Label, Width = 96, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, ToolTip = pack.Description };
            name.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFD8D8D8));
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            var state = new TextBlock
            {
                Text = busy ? "准备下载…" : installed ? $"已安装（{Services.OcrPacks.SizeOnDisk(pack) / 1048576.0:0} MB）" : $"未下载，约 {pack.ApproxBytes / 1000000} MB · 识别成 LaTeX",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Margin = new Thickness(0, 0, 10, 0),
                Tag = "state",
                ToolTip = pack.Description
            };
            state.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(installed ? 0xFF4CC38A : 0xFF9A9A9A));
            row.Children.Add(state);
            OcrPackRows.Children.Add(row);
        }
    }

    private async void OcrPackAction_Click(object sender, RoutedEventArgs e)
    {
        var pack = Services.OcrPacks.Find((string)((Button)sender).Tag)!;
        var key = "ocr:" + pack.Id;
        TextBlock? State() => OcrPackRows.Children.OfType<DockPanel>().FirstOrDefault(row => (string)row.Tag == key)?
            .Children.OfType<TextBlock>().FirstOrDefault(text => (string?)text.Tag == "state");
        if (_downloads.Remove(key, out var running))
        {
            running.Cancel();
            return;
        }

        if (Services.OcrPacks.IsInstalled(pack))
        {
            if (MessageBox.Show(this, $"删除识别模型「{pack.Label}」？", "Markshot", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
            {
                Services.OcrPacks.Delete(pack);
                BuildOcrPackRows();
            }

            return;
        }

        var cts = new CancellationTokenSource();
        _downloads[key] = cts;
        BuildOcrPackRows();
        var mirror = MirrorRow.Children.OfType<RadioButton>().FirstOrDefault(radio => radio.IsChecked == true)?.Tag as string ?? "hf-mirror";
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            if (State() is { } state)
            {
                state.Text = $"下载中 {p.Done / 1048576.0:0} / {p.Total / 1048576.0:0} MB";
            }
        });
        string? failure = null;
        try
        {
            await Services.OcrPacks.DownloadAsync(pack, mirror, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            failure = "已取消（下完的文件会保留，再点下载继续）";
        }
        catch (Exception ex)
        {
            Log.Error("下载识别模型失败", ex);
            failure = "下载失败：" + ex.Message + "（可在翻译页换个下载源再试）";
        }
        finally
        {
            _downloads.Remove(key);
            cts.Dispose();
        }

        if (!IsLoaded)
        {
            return;
        }

        BuildOcrPackRows();
        if (failure != null && State() is { } failed)
        {
            failed.Text = failure;
            failed.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFFF6B6B));
        }
    }

    /// <summary>One row per pack: what it is, its state, 下载 / 删除 (or 取消 with the progress while it downloads).</summary>
    private void BuildPackRows()
    {
        OfflinePackRows.Children.Clear();
        foreach (var pack in Services.OfflinePacks.All)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), Tag = pack.Id };
            var installed = Services.OfflinePacks.IsInstalled(pack);
            var busy = _downloads.ContainsKey(pack.Id);
            var action = new Button
            {
                Style = (Style)FindResource("FooterButton"),
                Content = busy ? "取消" : installed ? "删除" : "下载",
                MinWidth = 64,
                Tag = pack.Id
            };
            action.Click += PackAction_Click;
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(action);
            var name = new TextBlock { Text = pack.Label, Width = 96, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            name.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFD8D8D8));
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            var state = new TextBlock
            {
                Text = busy ? "准备下载…" : installed ? $"已安装（{Services.OfflinePacks.SizeOnDisk(pack) / 1048576.0:0} MB）" : $"未下载，约 {pack.ApproxBytes / 1000000} MB",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Margin = new Thickness(0, 0, 10, 0),
                Tag = "state"
            };
            state.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(installed ? 0xFF4CC38A : 0xFF9A9A9A));
            row.Children.Add(state);
            OfflinePackRows.Children.Add(row);
        }
    }

    private TextBlock? PackState(string id) => OfflinePackRows.Children.OfType<DockPanel>().FirstOrDefault(row => (string)row.Tag == id)?
        .Children.OfType<TextBlock>().FirstOrDefault(text => (string?)text.Tag == "state");

    private async void PackAction_Click(object sender, RoutedEventArgs e)
    {
        var id = (string)((Button)sender).Tag;
        var pack = Services.OfflinePacks.Find(id)!;
        if (_downloads.Remove(id, out var running))
        {
            running.Cancel();
            return;
        }

        if (Services.OfflinePacks.IsInstalled(pack))
        {
            if (MessageBox.Show(this, $"删除离线翻译包「{pack.Label}」？", "Markshot", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            {
                return;
            }

            Services.OfflinePacks.Delete(pack);
            BuildPackRows();
            return;
        }

        var cts = new CancellationTokenSource();
        _downloads[id] = cts;
        BuildPackRows();
        var mirror = MirrorRow.Children.OfType<RadioButton>().FirstOrDefault(radio => radio.IsChecked == true)?.Tag as string ?? "hf-mirror";
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            if (PackState(id) is { } state)
            {
                state.Text = $"下载中 {p.Done / 1048576.0:0} / {p.Total / 1048576.0:0} MB";
            }
        });
        string? failure = null;
        try
        {
            await Services.OfflinePacks.DownloadAsync(pack, mirror, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            failure = "已取消（下完的文件会保留，再点下载继续）";
        }
        catch (Exception ex)
        {
            Log.Error("下载离线翻译包失败", ex);
            failure = "下载失败：" + ex.Message + "（可换个下载源再试）";
        }
        finally
        {
            _downloads.Remove(id);
            cts.Dispose();
        }

        if (!IsLoaded)
        {
            return;
        }

        BuildPackRows();
        if (failure != null && PackState(id) is { } failed)
        {
            failed.Text = failure;
            failed.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFFF6B6B));
        }
    }

    // ---- 点击行为: a knob on a track as long as the row, snapping under a button --------------------------------
    private const double Tile = 28;
    private string _floatClick = "Capture";
    private readonly System.Windows.Shapes.Ellipse _clickKnob = new() { Width = 14, Height = 14, IsHitTestVisible = false };
    private bool _clickDragging;

    /// <summary>The action a click on the floating button runs (tests).</summary>
    internal string FloatClickChoice => _floatClick;

    private List<string> FloatKeys => FloatActionsRow.Children.OfType<FrameworkElement>().Select(tile => (string)tile.Tag).ToList();

    private void BuildClickTrack()
    {
        FloatClickTrack.Children.Clear();
        var count = FloatButtonWindow.Actions.Length;
        FloatClickTrack.Width = count * Tile;
        var line = new Border { Width = (count - 1) * Tile, Height = 3, CornerRadius = new CornerRadius(1.5), IsHitTestVisible = false };
        line.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF3A3A3A));
        Canvas.SetLeft(line, Tile / 2);
        Canvas.SetTop(line, 7.5);
        FloatClickTrack.Children.Add(line);
        _clickKnob.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
        _clickKnob.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, Theme.Key(0xFFFFFFFF));
        _clickKnob.StrokeThickness = 1.5;
        Canvas.SetTop(_clickKnob, 2);
        FloatClickTrack.Children.Add(_clickKnob);
        PlaceClickKnob(animate: false);
    }

    /// <summary>The knob under the chosen button, which gets a coloured plate.</summary>
    private void PlaceClickKnob(bool animate)
    {
        var index = Math.Max(0, FloatKeys.IndexOf(_floatClick));
        var left = index * Tile + Tile / 2 - _clickKnob.Width / 2;
        PaintClickPlate();
        if (animate)
        {
            var ease = new System.Windows.Media.Animation.DoubleAnimation(left, Motion.Bar) { EasingFunction = Motion.Ease };
            _clickKnob.BeginAnimation(Canvas.LeftProperty, ease);
            FloatClickPlate.BeginAnimation(Canvas.LeftProperty,
                new System.Windows.Media.Animation.DoubleAnimation(PlateLeft(left), Motion.Bar) { EasingFunction = Motion.Ease });
        }
        else
        {
            KnobAt(left);
        }
    }

    /// <summary>The plate's place for a knob place: centred on the knob's centre (the row and the track start alike).</summary>
    private double PlateLeft(double knobLeft) => knobLeft + _clickKnob.Width / 2 - Tile / 2;

    /// <summary>Knob and plate there at once (dragging: the plate slides along under the buttons).</summary>
    private void KnobAt(double left)
    {
        _clickKnob.BeginAnimation(Canvas.LeftProperty, null);
        FloatClickPlate.BeginAnimation(Canvas.LeftProperty, null);
        Canvas.SetLeft(_clickKnob, left);
        Canvas.SetLeft(FloatClickPlate, PlateLeft(left));
    }

    private void PaintClickPlate()
    {
        var accent = (TryFindResource("Accent") as SolidColorBrush)?.Color ?? Color.FromRgb(0x4C, 0x8D, 0xFF);
        if (FloatClickPlate.Background is not SolidColorBrush { } now || now.Color != Color.FromArgb(0x59, accent.R, accent.G, accent.B))
        {
            FloatClickPlate.Background = new SolidColorBrush(Color.FromArgb(0x59, accent.R, accent.G, accent.B));
        }
    }

    /// <summary>A tile's plate when the pointer is not on it (the chosen one's colour lies under the row, <see cref="FloatClickPlate"/>).</summary>
    private static Brush RestPlate(Panel row, string key) => Brushes.Transparent;

    /// <summary>The button the pointer is over on the track (any button: a hidden one may be chosen too).</summary>
    internal void ChooseClickAt(double x)
    {
        var keys = FloatKeys;
        var index = Math.Clamp((int)Math.Round((x - Tile / 2) / Tile), 0, keys.Count - 1);
        if (keys.Count == 0 || keys[index] == _floatClick)
        {
            PlaceClickKnob(animate: true);
            return;
        }

        _floatClick = keys[index];
        PlaceClickKnob(animate: true);
    }

    private void FloatClick_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _clickDragging = FloatClickTrack.CaptureMouse();
        KnobAt(Math.Clamp(e.GetPosition(FloatClickTrack).X - _clickKnob.Width / 2, 0, FloatClickTrack.Width - _clickKnob.Width));
        e.Handled = true;
    }

    private void FloatClick_MouseMove(object sender, MouseEventArgs e)
    {
        if (_clickDragging)
        {
            // Follows the pointer; snaps when let go.
            KnobAt(Math.Clamp(e.GetPosition(FloatClickTrack).X - _clickKnob.Width / 2, 0, FloatClickTrack.Width - _clickKnob.Width));
        }
    }

    private void FloatClick_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_clickDragging)
        {
            return;
        }

        _clickDragging = false;
        FloatClickTrack.ReleaseMouseCapture();
        ChooseClickAt(e.GetPosition(FloatClickTrack).X);
        e.Handled = true;
    }

    /// <summary>The floating button's row was reordered there: this row takes the order, its buttons sliding to their places.</summary>
    public void SyncFloatOrder()
    {
        var wasSaved = !HasUnsavedChanges;
        var order = ToolbarCatalog.Order(_config.FloatActionOrder, FloatButtonWindow.Actions);
        var tiles = FloatActionsRow.Children.OfType<FrameworkElement>().ToList();
        var before = tiles.ToDictionary(tile => (string)tile.Tag, tile => tile.TranslatePoint(new Point(0, 0), FloatActionsRow).X);
        FloatActionsRow.Children.Clear();
        foreach (var key in order)
        {
            if (tiles.FirstOrDefault(tile => (string)tile.Tag == key) is { } tile)
            {
                FloatActionsRow.Children.Add(tile);
            }
        }

        FloatActionsRow.UpdateLayout();
        foreach (var tile in FloatActionsRow.Children.OfType<FrameworkElement>())
        {
            var now = tile.TranslatePoint(new Point(0, 0), FloatActionsRow).X;
            if (before.TryGetValue((string)tile.Tag, out var was) && Math.Abs(was - now) > 0.5)
            {
                var shift = new TranslateTransform(was - now, 0);
                tile.RenderTransform = shift;
                shift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(was - now, 0, Motion.Fast) { EasingFunction = Motion.Ease });
            }
        }

        _liveSaved["FloatActionOrder"] = _config.FloatActionOrder;
        PlaceClickKnob(animate: true);
        if (wasSaved)
        {
            _savedPrint = Fingerprint(); // saved by the floating button already: nothing to save here
        }
    }

    private void ApplyFloatRow()
    {
        var (order, hidden) = ReadToolbarRow(FloatActionsRow);
        if (order == _config.FloatActionOrder && hidden == _config.FloatActionHidden)
        {
            return;
        }

        _config.FloatActionOrder = order;
        _config.FloatActionHidden = hidden;
        MarkDirty();
        RunningApp?.RefreshFloatLook();
    }

    /// <summary>The switch follows the floating button's own menu / the tray.</summary>
    public void SyncFloatSwitch()
    {
        // Switched (and saved) outside the window: that is the saved state now, not a change made here.
        _liveSaved["FloatButtonEnabled"] = _config.FloatButtonEnabled;
        var wasSaved = !HasUnsavedChanges;
        FloatEnabledCheck.IsChecked = _config.FloatButtonEnabled;
        if (wasSaved)
        {
            _savedPrint = Fingerprint(); // still nothing to save
        }
    }

    private void FloatEnabled_Click(object sender, RoutedEventArgs e)
    {
        _config.FloatButtonEnabled = FloatEnabledCheck.IsChecked == true;
        _config.FloatButtonChosen = true;
        RunningApp?.ApplyFloatButton(); // shown / hidden at once; kept only with 保存
    }

    private void FloatFullscreen_Click(object sender, RoutedEventArgs e)
    {
        _config.FloatHideFullscreen = FloatFullscreenCheck.IsChecked == true;
        RunningApp?.FloatButton?.CheckFullscreen();
    }

    private void FloatOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FloatOpacityValue == null)
        {
            return;
        }

        FloatOpacityValue.Text = $"{e.NewValue:0}%";
        FloatIconImage.Opacity = e.NewValue / 100;
        if (!_ready)
        {
            return;
        }

        _config.FloatIconOpacity = Math.Round(e.NewValue) / 100;
        RunningApp?.FloatButton?.ApplyLook();
    }


    /// <summary>悬浮按钮 尺寸: 100 % (the smallest, as designed) to 200 %.</summary>
    private void FloatSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FloatSizeValue == null)
        {
            return;
        }

        FloatSizeValue.Text = $"{e.NewValue:0}%";
        if (!_ready)
        {
            return;
        }

        _config.FloatIconSize = Math.Round(e.NewValue) / 100;
        RunningApp?.FloatButton?.ApplyLook();
    }


    private void ShowFloatIcon()
    {
        FloatIcon.Load(_config).Play(FloatIconImage);
        FloatIconImage.Opacity = FloatOpacitySlider.Value / 100;
        FloatIconReset.IsEnabled = FloatIcon.IsCustom(_config);
    }

    /// <summary>A new picture was taken (or reset): preview, the floating button and the tray icon follow.</summary>
    private void FloatIconChanged()
    {
        MarkDirty();
        ShowFloatIcon();
        RunningApp?.RefreshFloatLook();
    }

    /// <summary>Takes a picture file for the floating button (tests call it too).</summary>
    internal bool UseFloatIcon(string path)
    {
        try
        {
            if (!FloatIcon.SetFile(_config, path))
            {
                ThemedMessage.Show(this, "提示", "这个文件不是能打开的图片。", "确定");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("设置悬浮按钮图标失败", ex);
            ThemedMessage.Show(this, "提示", "无法保存这张图片：" + ex.Message, "确定");
            return false;
        }

        FloatIconChanged();
        return true;
    }

    internal void UseFloatIcon(BitmapSource image)
    {
        try
        {
            FloatIcon.SetImage(_config, image);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("设置悬浮按钮图标失败", ex);
            ThemedMessage.Show(this, "提示", "无法保存这张图片：" + ex.Message, "确定");
            return;
        }

        FloatIconChanged();
    }

    private void FloatIconPick_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择悬浮按钮图标",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.ico;*.apng|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            UseFloatIcon(dialog.FileName);
        }
    }

    private void FloatIconReset_Click(object sender, RoutedEventArgs e)
    {
        FloatIcon.Reset(_config);
        FloatIconChanged();
    }

    private void FloatIconPreview_Click(object sender, MouseButtonEventArgs e) => FloatIconPreview.Focus(); // then Ctrl+V pastes here

    private void FloatIcon_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FloatIcon_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            UseFloatIcon(files[0]);
        }
        else if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource image)
        {
            UseFloatIcon(image);
        }
    }

    /// <summary>Ctrl+V on the 样式 page (not in a text box): the copied picture (or picture file) becomes the icon.</summary>
    private void OnPreviewKeyDownFloat(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || PageStyle.Visibility != Visibility.Visible ||
            Keyboard.FocusedElement is TextBox or PasswordBox)
        {
            return;
        }

        e.Handled = PasteFloatIcon();
    }

    internal bool PasteFloatIcon()
    {
        try
        {
            if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files && files[0] is { } file)
            {
                return UseFloatIcon(file);
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Busy clipboard: try the picture below.
        }

        if (ClipboardService.TryGetImage() is { } image)
        {
            UseFloatIcon(image);
            return true;
        }

        return false;
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
        PageStyle.Visibility = page == "Style" ? Visibility.Visible : Visibility.Collapsed;
        Motion.EnterPage(page switch
        {
            "Translate" => PageTranslate,
            "Toolbar" => PageToolbar,
            "Style" => PageStyle,
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
            case "Style":
                NavStyle.IsChecked = true;
                break;
            case "Float":
                NavStyle.IsChecked = true;
                Dispatcher.BeginInvoke(() => FloatCard.BringIntoView(), DispatcherPriority.Loaded);
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

    /// <summary>
    /// True while a hotkey box takes keys: the global hotkeys rest meanwhile (pressing F1 there sets the field
    /// instead of starting a capture). The rest of the time they work with the settings open.
    /// </summary>
    public event Action<bool>? RecordingHotkey;

    /// <summary>The parts of 自动识别 only matter while it is on.</summary>
    private void DetectAuto_Click(object sender, RoutedEventArgs e)
    {
        var on = DetectAutoCheck.IsChecked == true;
        DetectElementsCheck.IsEnabled = DetectWindowsCheck.IsEnabled = DetectSensitivityRow.IsEnabled = on;
        DetectSensitivityRow.Opacity = DetectElementsCheck.Opacity = DetectWindowsCheck.Opacity = on ? 1 : 0.45;
    }

    private void DetectSensitivity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DetectSensitivityValue != null)
        {
            var level = (int)Math.Round(e.NewValue);
            DetectSensitivityValue.Text = level switch { 1 => "1 · 窗口", 2 => "2 · 大区块", 4 => "4 · 细小元素", _ => "3 · 常规元素" };
            DetectSensitivityValue.ToolTip = level switch
            {
                1 => "只框软件窗口",
                2 => "窗口里的大块区域：侧栏、主内容、大图",
                4 => "按钮、单行文字、小图标也框",
                _ => "图片、卡片、段落、表格（默认）"
            };
        }
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => RecordingHotkey?.Invoke(false);

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        RecordingHotkey?.Invoke(true);
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
        var key = AnnotationOps.RealKey(e); // an IME reports the keys as ImeProcessed
        e.Handled = HandleHotkeyKey(box, key, Keyboard.Modifiers);
    }

    /// <summary>Registers Markshot and opens Windows' chooser for .png (the user ticks "always").</summary>
    private void DefaultOpen_Click(object sender, RoutedEventArgs e)
    {
        OpenWithCheck.IsChecked = true;
        _config.RegisterOpenWith = true;
        (Application.Current as App)?.ApplyOpenWith();
        try
        {
            FileAssociation.ChooseDefault(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }
        catch (Exception ex)
        {
            Log.Error("打开默认程序选择失败", ex);
        }
    }

    private void DefaultApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开默认应用设置失败", ex);
        }
    }

    /// <summary>Capture-window keys: a combination, or a lone Alt / Ctrl / Shift (set when it is released alone).</summary>
    private void CaptureKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        var key = AnnotationOps.RealKey(e); // an IME reports the keys as ImeProcessed
        e.Handled = HandleHotkeyKey(box, key, Keyboard.Modifiers);
    }

    private void CaptureKeyBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || !box.Text.EndsWith("…", StringComparison.Ordinal))
        {
            return;
        }

        var key = AnnotationOps.RealKey(e); // an IME reports the keys as ImeProcessed
        box.Text = key switch
        {
            Key.LeftAlt or Key.RightAlt => "Alt",
            Key.LeftCtrl or Key.RightCtrl => "Ctrl",
            Key.LeftShift or Key.RightShift => "Shift",
            _ => box.Tag as string ?? ""
        };
        e.Handled = true;
    }

    private static string CaptureKeyText(TextBox box, string previous)
    {
        var text = box.Text.Trim();
        return text.EndsWith("…", StringComparison.Ordinal) ? previous : text;
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
        if (value == "~")
        {
            value = "`"; // Shift + the key above Tab
        }

        if (value.Length == 1 && (char.IsLetterOrDigit(value[0]) || value[0] == '`'))
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
            ThemedMessage.Show(this, "提示", "截图快捷键无效：" + error, "确定");
            return;
        }

        var hasPinHotkey = !string.IsNullOrWhiteSpace(PinHotkeyBox.Text);
        var pin = default(HotkeyGesture);
        if (hasPinHotkey && !HotkeyGesture.TryParse(PinHotkeyBox.Text, out pin, out error))
        {
            ThemedMessage.Show(this, "提示", "贴图快捷键无效：" + error, "确定");
            return;
        }

        if (hasPinHotkey &&
            string.Equals(capture.Text, pin.Text, StringComparison.OrdinalIgnoreCase))
        {
            ThemedMessage.Show(this, "提示", "截图和贴图不能使用同一个快捷键。", "确定");
            return;
        }

        var hasMouseThrough = !string.IsNullOrWhiteSpace(MouseThroughHotkeyBox.Text);
        var mouseThrough = default(HotkeyGesture);
        if (hasMouseThrough &&
            !HotkeyGesture.TryParse(MouseThroughHotkeyBox.Text, out mouseThrough, out error))
        {
            ThemedMessage.Show(this, "提示", "鼠标穿透快捷键无效：" + error, "确定");
            return;
        }

        var hasRestore = !string.IsNullOrWhiteSpace(RestorePinHotkeyBox.Text);
        var restore = default(HotkeyGesture);
        if (hasRestore && !HotkeyGesture.TryParse(RestorePinHotkeyBox.Text, out restore, out error))
        {
            ThemedMessage.Show(this, "提示", "返回上一贴图快捷键无效：" + error, "确定");
            return;
        }

        var hasCloseAll = !string.IsNullOrWhiteSpace(CloseAllPinsHotkeyBox.Text);
        var closeAll = default(HotkeyGesture);
        if (hasCloseAll && !HotkeyGesture.TryParse(CloseAllPinsHotkeyBox.Text, out closeAll, out error))
        {
            ThemedMessage.Show(this, "提示", "关闭所有贴图快捷键无效：" + error, "确定");
            return;
        }

        var hasRecord = !string.IsNullOrWhiteSpace(RecordHotkeyBox.Text);
        var record = default(HotkeyGesture);
        if (hasRecord && !HotkeyGesture.TryParse(RecordHotkeyBox.Text, out record, out error))
        {
            ThemedMessage.Show(this, "提示", "录屏快捷键无效：" + error, "确定");
            return;
        }

        if (hasRecord && string.Equals(capture.Text, record.Text, StringComparison.OrdinalIgnoreCase))
        {
            ThemedMessage.Show(this, "提示", "截图和录屏不能使用同一个快捷键。", "确定");
            return;
        }

        _config.CaptureHotkey = capture.Text;
        _config.RecordHotkey = hasRecord ? record.Text : "";
        _config.PinHotkey = hasPinHotkey ? pin.Text : "";
        _config.MouseThroughHotkey = hasMouseThrough ? mouseThrough.Text : "";
        _config.RestorePinHotkey = hasRestore ? restore.Text : "";
        _config.CloseAllPinsHotkey = hasCloseAll ? closeAll.Text : "";
        _config.HotkeysDisabled = HotkeysDisabledCheck.IsChecked == true;
        _config.WinMouseEnabled = WinMouseCheck.IsChecked == true;
        _config.WinMouseLeft = _winMouse["Left"];
        _config.WinMouseMiddle = _winMouse["Middle"];
        _config.WinMouseRight = _winMouse["Right"];
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
            ThemedMessage.Show(this, "提示", "标注工具快捷键不能重复。", "确定");
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
        _config.Theme = _theme;
        _config.UiSize = _uiSize;
        _config.CopyImageAsFile = CopyFileCheck.IsChecked == true;
        _config.DetectAuto = DetectAutoCheck.IsChecked == true;
        _config.DetectRange = (int)Math.Round(DetectSensitivitySlider.Value);
        _config.DetectElements = DetectElementsCheck.IsChecked == true;
        _config.DetectWindows = DetectWindowsCheck.IsChecked == true;
        _config.CaptureShowCursor = CaptureCursorCheck.IsChecked == true;
        var openWithChanged = _config.RegisterOpenWith != (OpenWithCheck.IsChecked == true);
        _config.RegisterOpenWith = OpenWithCheck.IsChecked == true;
        if (openWithChanged)
        {
            (Application.Current as App)?.ApplyOpenWith();
        }
        _config.CaptureCursorKey = CaptureKeyText(CaptureCursorKeyBox, _config.CaptureCursorKey);
        _config.CrosshairKey = CaptureKeyText(CrosshairKeyBox, _config.CrosshairKey);
        if ((AutoStartCheck.IsChecked == true) != AutoStart.IsEnabled)
        {
            AutoStart.Set(AutoStartCheck.IsChecked == true);
        }
        (_config.CaptureToolOrder, _config.CaptureToolHidden) = ReadToolbarRow(CaptureToolsRow);
        (_config.CaptureActionOrder, _config.CaptureActionHidden) = ReadToolbarRow(CaptureActionsRow);
        (_config.PinToolOrder, _config.PinToolHidden) = ReadToolbarRow(PinToolsRow);
        (_config.PinActionOrder, _config.PinActionHidden) = ReadToolbarRow(PinActionsRow);
        (_config.EditorToolOrder, _config.EditorToolHidden) = ReadToolbarRow(EditorToolsRow);
        (_config.EditorActionOrder, _config.EditorActionHidden) = ReadToolbarRow(EditorActionsRow);
        _config.AskOpenFolderAfterSave = AskOpenFolderCheck.IsChecked == true;
        _config.EscExitsCapture = EscExitsCheck.IsChecked == true;
        _config.QuickDoubleClickCopy = QuickDoubleClickCheck.IsChecked == true;
        _config.PinTextSelect = PinTextSelectCheck.IsChecked == true;
        _config.FloatHideWhileCapturing = FloatCaptureHideCheck.IsChecked != false;
        _config.HideToolChevrons = HideChevronsCheck.IsChecked == true;

        _config.SaveDirectory = SaveDirBox.Text.Trim();
        // Keep following the installer's folder unless another one was entered.
        _config.OcrLanguage = OcrLanguageBox.Text.Trim();

        _config.Translation.BaseUrl = BaseUrlBox.Text.Trim();
        _config.Translation.Engine = EngineOffline.IsChecked == true ? "Offline" : EngineApi.IsChecked == true ? "Api" : "Auto";
        _config.Translation.OfflineMirror = MirrorRow.Children.OfType<RadioButton>().FirstOrDefault(radio => radio.IsChecked == true)?.Tag as string ?? "hf-mirror";
        _config.Translation.ApiKey = ApiKey.Trim();
        _config.Translation.Model = ModelBox.Text.Trim();
        _config.Translation.TargetLanguage = TargetBox.Text.Trim();
        _config.ImmersiveTranslate = ImmersiveSwitch.IsChecked == true;

        _config.Translation.TimeoutSeconds =
            int.TryParse(TimeoutBox.Text.Trim(), out var timeout)
                ? Math.Clamp(timeout, 10, 300)
                : 60;

        _config.FloatClickAction = _floatClick;
        Saved = true;
        _saves++;
        _dirty = false;
        _savedPrint = Fingerprint();
        foreach (var name in LiveSettings)
        {
            _liveSaved[name] = typeof(AppConfig).GetProperty(name)!.GetValue(_config);
        }

        FloatIcon.CleanUp(_config); // pictures replaced in here and now not used
        SavedChanges?.Invoke(); // applied now; the window stays open
        ShowSavedNotice();
    }

    /// <summary>保存 pressed: the app applies the settings (the window stays open, no balloon / sound).</summary>
    public event Action? SavedChanges;

    private DispatcherTimer? _noticeTimer;
    private string? _statusBefore;

    /// <summary>A quiet line in the footer for a moment.</summary>
    private void ShowSavedNotice()
    {
        _noticeTimer?.Stop();
        _statusBefore ??= StatusText.Text;
        StatusText.Text = "✓ 设置已保存并生效";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            StatusText.Text = _statusBefore ?? "";
            StatusText.ClearValue(TextBlock.ForegroundProperty);
            _statusBefore = null;
        };
        _noticeTimer.Start();
    }

    /// <summary>The footer line (tests).</summary>
    public string StatusLine => StatusText.Text;

    // ---- the version: what changed lately, on hover ------------------------------------------------------------------

    private System.Windows.Controls.Primitives.Popup? _notesPopup;
    private readonly DispatcherTimer _notesClose = new() { Interval = TimeSpan.FromMilliseconds(180) };

    /// <summary>The release-notes card under the version (tests).</summary>
    internal System.Windows.Controls.Primitives.Popup? NotesPopup => _notesPopup;

    private void VersionText_MouseEnter(object sender, MouseEventArgs e)
    {
        _notesClose.Stop();
        _notesPopup ??= BuildNotesPopup();
        if (!_notesPopup.IsOpen)
        {
            _notesPopup.IsOpen = true;
            if (_notesPopup.Child is FrameworkElement card)
            {
                Motion.PopIn(card, fromAbove: true, duration: Motion.Bar);
            }
        }
    }

    private void VersionText_MouseLeave(object sender, MouseEventArgs e)
    {
        _notesClose.Tick -= CloseNotes;
        _notesClose.Tick += CloseNotes;
        _notesClose.Start(); // a moment to move onto the card itself
    }

    private void CloseNotes(object? sender, EventArgs e)
    {
        _notesClose.Stop();
        if (_notesPopup is { IsOpen: true, IsMouseOver: false } && !VersionText.IsMouseOver)
        {
            _notesPopup.IsOpen = false;
        }
    }

    private System.Windows.Controls.Primitives.Popup BuildNotesPopup()
    {
        var body = new StackPanel { Width = 330 };
        foreach (var (version, lines) in ReleaseNotes.All.Take(3))
        {
            var heading = new TextBlock { Text = version, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 10, 0, 2) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFF0F0F0));
            body.Children.Add(heading);
            foreach (var line in lines)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
                var dot = new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Margin = new Thickness(1, 6, 7, 0), VerticalAlignment = VerticalAlignment.Top };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
                DockPanel.SetDock(dot, Dock.Left);
                var text = new TextBlock { Text = line, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
                text.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFB8B8B8));
                row.Children.Add(dot);
                row.Children.Add(text);
                body.Children.Add(row);
            }
        }

        var card = new Border
        {
            Child = body,
            Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = 0.4 }
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF1E1E1E));
        card.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x33FFFFFF));
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Child = new Grid { Children = { card }, LayoutTransform = UiScale.SettingsTransform },
            PlacementTarget = VersionText,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            AllowsTransparency = true,
            StaysOpen = true,
            VerticalOffset = 2
        };
        popup.MouseLeave += (_, _) => VersionText_MouseLeave(popup, null!);
        return popup;
    }

    private void Help_Click(object sender, RoutedEventArgs e) => HelpDocument.Open();

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Maximized, a window reaches past the screen by its (invisible) resize border: that much is kept empty, so the
    /// title bar and the buttons at the bottom stay on the screen. The maximize button turns into restore.
    /// </summary>
    private void FitMaximized()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "向下还原" : "最大化";
        if (!maximized)
        {
            BorderThickness = new Thickness(0);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = (GetSystemMetrics(32 /* SM_CXSIZEFRAME */) + GetSystemMetrics(92 /* SM_CXPADDEDBORDER */)) / dpi.DpiScaleX;
        var y = (GetSystemMetrics(33 /* SM_CYSIZEFRAME */) + GetSystemMetrics(92)) / dpi.DpiScaleY;
        BorderThickness = new Thickness(x, y, x, y);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_closingNow)
        {
            return; // already closing (its question is open): a second Close threw
        }

        Close();
    }

    private bool _closingNow;
    private long _activatedAt;

    /// <summary>
    /// Esc closes the window only when it was pressed here: not held / repeated, and not right after the window came to
    /// the front — the Esc that ended a capture (or the question box) went on to the settings behind it and closed them.
    /// </summary>
    private void OnEscape(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Keyboard.FocusedElement is TextBox { Name: var name } && name.EndsWith("HotkeyBox", StringComparison.Ordinal))
        {
            return;
        }

        e.Handled = true;
        if (e.IsRepeat || _closingNow || Environment.TickCount64 - _activatedAt < 400)
        {
            return;
        }

        Close();
    }

    /// <summary>恢复默认设置: every setting back to its default (the translation key is kept); asked first.</summary>
    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var answer = ResetAnswer?.Invoke() ??
                     ThemedMessage.Show(this, "恢复默认设置", "所有设置（快捷键、工具栏、样式、悬浮按钮等）都恢复为默认值，翻译 API Key 保留。确定恢复？", "恢复默认", "取消");
        if (answer != 0)
        {
            return;
        }

        var fresh = new AppConfig();
        fresh.Translation.ApiKey = _config.Translation.ApiKey;
        fresh.Translation.BaseUrl = _config.Translation.BaseUrl;
        fresh.Translation.Model = _config.Translation.Model;
        fresh.LastVersion = _config.LastVersion;
        fresh.RecentRegions = _config.RecentRegions;
        foreach (var property in typeof(AppConfig).GetProperties())
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0 &&
                property.Name is not ("LegacySensitivity" or "DetectRangeStored"))
            {
                property.SetValue(_config, property.GetValue(fresh));
            }
        }

        _config.UiSizeVersion = 2;
        _config.Save(); // kept whatever happens next
        FloatIcon.CleanUp(_config);
        _dirty = false;
        _asked = true;
        Saved = true;
        SavedChanges?.Invoke(); // saved and applied (hotkeys, the floating button, pins' shadows …)
        RunningApp?.ApplyFloatButton();
        RunningApp?.RefreshFloatLook();
        RunningApp?.ApplyOpenWith();
        ResetDone?.Invoke(); // the app opens the settings again with the defaults shown
        Close();
    }

    /// <summary>Tests: the answer to 恢复默认设置's question.</summary>
    internal Func<int>? ResetAnswer;

    /// <summary>恢复默认设置 is done (the app reopens the window).</summary>
    public event Action? ResetDone;

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
        Theme.Bind(DrawerBar, Border.BackgroundProperty, 0x99FFFFFF);
        if (!_drawerOpen)
        {
            ShowDrawerHint(true);
        }
    }

    /// <summary>The hint text on hover, the coffee cup otherwise (neither while the QR code is out).</summary>
    private bool _drawerOpen;

    private void ShowDrawerHint(bool text, double? height = null)
    {
        if (height is { } target)
        {
            _drawerOpen = target >= 1;
            if (!_drawerOpen)
            {
                ResetQr();
            }
        }

        // The state the drawer is going to (not its height mid-animation): put away = the cup again.
        Motion.Fade(DrawerHint, text && !_drawerOpen ? 1 : 0);
        Motion.Fade(DrawerCoffee, text || _drawerOpen ? 0 : 1);
    }

    private void DrawerHandle_MouseLeave(object sender, MouseEventArgs e)
    {
        // From the cup to the bar (or back) is still over the drawer.
        if (sender is FrameworkElement left && Mouse.DirectlyOver is DependencyObject over && over != left &&
            (DrawerHandle.IsMouseOver || DrawerCoffee.Parent is UIElement { IsMouseOver: true }))
        {
            return;
        }

        Theme.Bind(DrawerBar, Border.BackgroundProperty, 0x4DFFFFFF);
        if (!_drawerDragging)
        {
            ShowDrawerHint(false);
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
        ((UIElement)sender).CaptureMouse();
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
        DrawerCoffee.Opacity = 0;
    }

    private void DrawerHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawerDragging)
        {
            return;
        }

        _drawerDragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
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
        ShowDrawerHint(target < 1 && ((UIElement)sender).IsMouseOver, target);
        e.Handled = true;
    }

    /// <summary>The QR code is out and the click is elsewhere: it goes back in.</summary>
    private void PutDrawerAwayOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (DrawerHeight < 1 || _drawerDragging || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        for (var node = source; node != null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, DrawerPanel) || ReferenceEquals(node, DrawerHandle) || ReferenceEquals(node, DrawerBar))
            {
                return; // on the code or its bar: they handle it
            }
        }

        PutDrawerAway();
    }

    /// <summary>The QR code slides back in.</summary>
    internal void PutDrawerAway()
    {
        var height = DrawerHeight;
        if (height < 1)
        {
            return;
        }

        DrawerPanel.BeginAnimation(HeightProperty, new System.Windows.Media.Animation.DoubleAnimation(height, 0, Motion.Base) { EasingFunction = Motion.Ease });
        ShowDrawerHint(false, 0);
    }

    // ---- annotation bar buttons: click = show / hide, long-press drag = order ----------------
    private readonly HashSet<string> _hiddenButtons = new();

    private ReorderBehavior FillToolbarRow(Panel row, string[] defaults, string order, string hidden)
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
                Theme.Bind(mask, Border.BackgroundProperty, 0xFFFFFFFF);
            }
            else if (icon is System.Windows.Shapes.Shape shape)
            {
                shape.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, Theme.Key(0xFFFFFFFF));
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

            button.MouseEnter += (_, _) => Theme.Bind(button, Border.BackgroundProperty, 0x26FFFFFF);
            button.MouseLeave += (_, _) => button.Background = RestPlate(row, key);
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

                MarkDirty();

                var off = _hiddenButtons.Add(id) || !_hiddenButtons.Remove(id);
                Motion.Fade(icon, off ? 0.28 : 1);
            };
            row.Children.Add(button);
        }

        reorder.Reordered += _ => MarkDirty();
        return reorder;
    }

    private (string Order, string Hidden) ReadToolbarRow(Panel row)
    {
        var keys = row.Children.OfType<FrameworkElement>().Select(child => child.Tag as string ?? "").ToList();
        return (string.Join(",", keys), string.Join(",", keys.Where(key => _hiddenButtons.Contains(RowId(row, key)))));
    }

    private static string RowId(Panel row, string key) => row.Name + ":" + key;

    // ---- 样式: theme and size preview at once; App re-applies the saved ones when not saved ----
    private string _theme = "Dark";
    private string _uiSize = "Medium";

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mode })
        {
            _theme = mode;
            Theme.Apply(mode);

        }
    }

    private Dictionary<string, string> _winMouse = new();

    /// <summary>Win + 左键 / 中键 / 右键: each with its own choice of 识别文字 / 贴图 / 复制 / 不使用.</summary>
    private void BuildWinMouseRows()
    {
        WinMouseRows.Children.Clear();
        foreach (var (button, label) in new[] { ("Left", "Win + 左键"), ("Middle", "Win + 中键"), ("Right", "Win + 右键") })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var text = new TextBlock { Text = label, Width = 84, Style = (Style)FindResource("RowLabel") };
            row.Children.Add(text);
            var plate = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(2) };
            plate.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF2A2A2A));
            var choices = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (action, name) in new[] { ("Ocr", "识别文字"), ("Pin", "贴图"), ("Copy", "复制"), ("None", "不使用") })
            {
                var choice = new RadioButton
                {
                    Content = name,
                    Tag = action,
                    GroupName = "WinMouse" + button + GetHashCode(),
                    Style = (Style)FindResource("SegmentRadio"),
                    IsChecked = string.Equals(_winMouse[button], action, StringComparison.OrdinalIgnoreCase)
                };
                var key = button;
                choice.Checked += (_, _) => _winMouse[key] = action;
                choices.Children.Add(choice);
            }

            plate.Child = choices;
            row.Children.Add(plate);
            WinMouseRows.Children.Add(row);
        }
    }

    private void UiSize_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string size })
        {
            _uiSize = size;
            UiScale.Apply(size);
            RunningApp?.FloatButton?.ApplyLook();

        }
    }

    /// <summary>The QR code: grey dots on the dark theme, dark dots on the light one (the first or, turned over, the WeChat one).</summary>
    private void UpdateDrawerQr() =>
        DrawerQr.Source = new BitmapImage(new Uri("pack://application:,,,/Markshot;component/Assets/donate_qr" +
            (_qrSecond ? "_wechat" : "") + (Theme.IsLight ? "_light" : "") + ".png"));

    private bool _qrSecond; // the WeChat code is showing
    private bool _qrWanted;
    private bool _qrTurning;

    /// <summary>The code being shown (tests): false the first, true the WeChat one.</summary>
    public bool ShowsSecondQr => _qrSecond;

    private void DrawerQr_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_drawerOpen && !_drawerDragging)
        {
            TurnQr(true);
        }
    }

    private void DrawerQr_MouseLeave(object sender, MouseEventArgs e) => TurnQr(false);

    /// <summary>
    /// Turns the card over horizontally: it narrows to its middle line, the other code comes in, it widens
    /// again (the shared curve, half of Motion.Base each way). A change of mind meanwhile turns it once more.
    /// </summary>
    private void TurnQr(bool second)
    {
        _qrWanted = second;
        if (_qrTurning || _qrSecond == second)
        {
            return;
        }

        _qrTurning = true;
        var half = TimeSpan.FromMilliseconds(Motion.Base.TimeSpan.TotalMilliseconds / 2);
        var close = new System.Windows.Media.Animation.DoubleAnimation(1, 0, half) { EasingFunction = Motion.Ease };
        close.Completed += (_, _) =>
        {
            _qrSecond = !_qrSecond;
            UpdateDrawerQr();
            var open = new System.Windows.Media.Animation.DoubleAnimation(0, 1, half) { EasingFunction = Motion.Ease };
            open.Completed += (_, _) =>
            {
                _qrTurning = false;
                if (_qrWanted != _qrSecond)
                {
                    TurnQr(_qrWanted);
                }
            };
            DrawerQrScale.BeginAnimation(ScaleTransform.ScaleXProperty, open);
        };
        DrawerQrScale.BeginAnimation(ScaleTransform.ScaleXProperty, close);
    }

    /// <summary>Put away: the first code again, at once.</summary>
    private void ResetQr()
    {
        _qrWanted = false;
        if (!_qrSecond && !_qrTurning)
        {
            return;
        }

        DrawerQrScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DrawerQrScale.ScaleX = 1;
        _qrTurning = false;
        _qrSecond = false;
        UpdateDrawerQr();
    }

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
            MarkDirty();
            ShadowOpacitySlider.Value = Math.Round(chosen.A / 2.55); // the picker's alpha is the shadow opacity
            UpdateShadowPreview();
        }
    }

    private UpdateService.Package? _update;

    /// <summary>Looks for a newer version in the online releases.</summary>
    private async void RefreshUpdate(bool quiet)
    {
        if (!quiet)
        {
            StatusText.Text = "正在检查更新…";
        }

        _update = await UpdateService.FindNewestAsync(null);
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
            StatusText.Text = UpdateService.LastFailure is { } failure
                ? $"检查更新失败（{failure}），请稍后再试；也可以到 {UpdateService.DefaultRepository}/releases 手动下载"
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

        // What was changed here and not saved yet is saved first: the update restarts Markshot.
        var saves = _saves;
        Save_Click(this, e);
        if (_saves == saves)
        {
            return; // a setting is not valid (the message said which)
        }

        await Task.Yield();
        new UpdateWindow(_update) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
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
                TargetLanguage = TargetBox.Text.Trim(),
                Engine = "Api" // this button tests the API card
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
