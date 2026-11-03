// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Markshot.Core.Recording;

namespace Markshot.UI.Recording;

/// <summary>
/// The recorder's toolbar: time · fps · size · delay · start/stop · pause · sound · keys · retake ·
/// exit; after stopping: timeline + play · speed · sound · save · copy · retake · exit. A normal
/// (opaque) window so it can be kept out of the recording; it is also the taskbar button
/// ("Markshot 录屏": clicking it hides / shows the recorder, closing it exits).
/// </summary>
public partial class RecordBarWindow : Window
{
    private const int GWL_STYLE = -16;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const double Gap = 4;

    private readonly RecorderController _controller;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private RecordPreviewWindow? _preview;
    private double _speed = 1;
    private bool _soundOn = true;
    private bool _closing;
    private bool _dragging;
    private Native.POINT _dragStart;
    private Int32Rect _dragOrigin;
    private RecordPhase _lastPhase = RecordPhase.Idle;
    private Popup? _popup;

    public RecordBarWindow(RecorderController controller)
    {
        InitializeComponent();
        _controller = controller;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Markshot;component/Assets/markshot.ico"));
        controller.Changed += Refresh;
        controller.Notice += ShowStatus;
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusText.Visibility = Visibility.Collapsed;
        };
        Trim.Seek += t => _preview?.Seek(t);
        Trim.Trim += (isIn, t) =>
        {
            if (_preview == null)
            {
                return;
            }

            _preview.In = Trim.In;
            _preview.Out = Trim.Out;
            _preview.Seek(t); // the picture follows the triangle
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // A taskbar click minimises = hides the recorder UI (the recording goes on).
            SetWindowLong(handle, GWL_STYLE, GetWindowLong(handle, GWL_STYLE) | WS_MINIMIZEBOX);
            var round = 2;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            var border = 0x00333333;
            DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        };
        // Off screen until laid out, then straight to its place (it used to show up elsewhere first).
        Left = -32000;
        Top = -32000;
        ContentRendered += (_, _) => Place();
        SizeChanged += (_, _) =>
        {
            if (_placed)
            {
                Place();
            }
        };
        StateChanged += (_, _) => _controller.SetUiHidden(WindowState == WindowState.Minimized);
        Closing += (_, e) =>
        {
            if (!_closing)
            {
                e.Cancel = true; // Alt+F4 / taskbar "close" = exit the recorder (asks first if needed)
                _controller.RequestClose();
            }
        };
        Closed += (_, _) =>
        {
            controller.Changed -= Refresh;
            controller.Notice -= ShowStatus;
        };
        PreviewKeyDown += OnKey;
        Refresh();
    }

    public void CloseNow()
    {
        _closing = true;
        _popup?.SetCurrentValue(Popup.IsOpenProperty, false);
        Close();
    }

    public void RestoreFromTaskbar()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
    }

    private bool _protectWanted;
    private bool? _protectedNow;
    private bool _placed;

    /// <summary>
    /// While recording, the toolbar is kept out of the video — but only when it actually sits on the
    /// area (a full-screen area): an excluded window also vanishes from screen sharing / remote
    /// control, where the user would lose the toolbar while recording.
    /// </summary>
    public void SetProtected(bool on)
    {
        _protectWanted = on;
        UpdateProtection();
    }

    private void UpdateProtection()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        Native.GetWindowRect(handle, out var bar);
        var r = _controller.Rect;
        var overlaps = bar.Left < r.X + r.Width && bar.Right > r.X && bar.Top < r.Y + r.Height && bar.Bottom > r.Y;
        var on = _protectWanted && overlaps;
        if (_protectedNow == on)
        {
            return;
        }

        _protectedNow = on;
        RecordNative.ExcludeFromCapture(handle, on);
    }

    /// <summary>On top of the frame and the preview (only when they are created; moving keeps the order).</summary>
    public void BringToFront()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            Native.SetWindowPos(handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    // ------------------------------------------------------------------ placement

    /// <summary>Below the area when it fits, else above, else inside along its bottom; centred, on the work area.</summary>
    public void Place()
    {
        if (Target(force: true) is not { } move)
        {
            return;
        }

        // The first time it also goes on top (above the frame); later moves keep the order.
        Native.SetWindowPos(move.Handle, _placed ? IntPtr.Zero : Native.HWND_TOPMOST, move.X, move.Y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | (_placed ? Native.SWP_NOZORDER : 0));
        _placed = true;
        Placed();
    }

    public void Placed() => UpdateProtection();

    /// <summary>Where the toolbar goes for the current area (null until it has been laid out once).</summary>
    internal WindowMove? Target(bool force = false)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || WindowState == WindowState.Minimized || (!_placed && !force))
        {
            return null;
        }

        Native.GetWindowRect(handle, out var bar);
        var r = _controller.Rect;
        var monitor = Monitors.At(r.X + r.Width / 2, Math.Min(r.Y + r.Height + 1, r.Y + r.Height));
        var below = Monitors.At(r.X + r.Width / 2, r.Y + r.Height + 2);
        var scale = monitor.Scale;
        var gap = (int)Math.Round(Gap * scale);
        var work = below.WorkArea;
        var y = r.Y + r.Height + gap;
        if (y + bar.Height > work.Y + work.Height - 4)
        {
            var above = Monitors.At(r.X + r.Width / 2, r.Y - 2);
            var ya = r.Y - gap - bar.Height;
            if (ya >= above.WorkArea.Y + 4)
            {
                work = above.WorkArea;
                y = ya;
            }
            else
            {
                work = monitor.WorkArea;
                y = Math.Clamp(r.Y + r.Height - bar.Height - (int)(10 * scale), work.Y + 4, work.Y + work.Height - bar.Height - 4);
            }
        }

        var x = Math.Clamp(r.X + r.Width / 2 - bar.Width / 2, work.X + 4, Math.Max(work.X + 4, work.X + work.Width - bar.Width - 4));
        return new WindowMove(handle, x, y, 0, 0, false);
    }

    // ------------------------------------------------------------------ state → controls

    private void Refresh()
    {
        if (!_controller.IsOpen)
        {
            return;
        }

        var phase = _controller.Phase;
        var config = _controller.Config;
        var preview = phase == RecordPhase.Preview;
        if (phase != _lastPhase)
        {
            OnPhaseChanged(phase);
        }

        RecordRow.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewRows.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;

        var setup = phase == RecordPhase.Setup;
        var live = phase is RecordPhase.Recording or RecordPhase.Paused;
        SetupGroup.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
        FpsButton.Content = $"{config.Fps} fps";
        if (!WidthBox.IsKeyboardFocusWithin && !HeightBox.IsKeyboardFocusWithin)
        {
            SetText(WidthBox, _controller.Rect.Width.ToString(CultureInfo.InvariantCulture));
            SetText(HeightBox, _controller.Rect.Height.ToString(CultureInfo.InvariantCulture));
        }

        DelayToggle.IsChecked = config.DelayOn;
        DelayButton.Content = $"{config.Delay}s";
        DelayButton.Opacity = config.DelayOn ? 1 : 0.5;
        SystemToggle.IsChecked = config.SystemAudio;
        MicToggle.IsChecked = config.Microphone;
        KeysToggle.IsChecked = _controller.KeysOn;
        PauseToggle.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        PauseToggle.IsChecked = false;
        SetPauseBlink(phase == RecordPhase.Paused);
        PauseIcon.Style = (Style)FindResource(phase == RecordPhase.Paused ? "IconPlayMask" : "IconPauseMask");
        StartButton.IsEnabled = phase != RecordPhase.Finishing;
        var stopping = phase is RecordPhase.Countdown or RecordPhase.Starting or RecordPhase.Recording or RecordPhase.Paused;
        StartText.Text = phase switch
        {
            RecordPhase.Countdown => $"取消 {_controller.Count}",
            RecordPhase.Starting => "准备中…",
            RecordPhase.Finishing => "处理中…",
            _ when stopping => "停止",
            _ => "开始"
        };
        StartGlyph.CornerRadius = new CornerRadius(stopping ? 2 : 5);
        var dot = phase switch
        {
            RecordPhase.Recording => RecordFrameWindow.Red,
            RecordPhase.Paused => RecordFrameWindow.Amber,
            _ => Color.FromRgb(0x7A, 0x7A, 0x7A)
        };
        if (RecDot.Fill is not SolidColorBrush { } current || current.Color != dot)
        {
            RecDot.Fill = new SolidColorBrush(dot);
        }
        TimeText.Text = FormatTime(_controller.Elapsed, false);
        RetakeButton.IsEnabled = phase != RecordPhase.Finishing;

        if (preview && _controller.Take is { } take)
        {
            var busy = _controller.Progress is not null;
            SaveButton.IsEnabled = !busy;
            CopyButton.IsEnabled = !busy;
            SoundToggle.IsEnabled = take.HasAudio;
            SoundToggle.IsChecked = take.HasAudio && _soundOn;
            SpeedButton.Content = FormatSpeed(_speed);
            if (_controller.Progress is { } progress)
            {
                StatusText.Text = $"正在导出… {progress * 100:0}%";
                StatusText.Visibility = Visibility.Visible;
                _statusTimer.Stop();
            }
        }
    }

    private static void SetText(TextBox box, string text)
    {
        if (box.Text != text)
        {
            box.Text = text;
        }
    }

    private void OnPhaseChanged(RecordPhase phase)
    {
        _lastPhase = phase;
        if (phase == RecordPhase.Preview && _controller.Take is { } take)
        {
            _preview = _controller.Preview;
            _speed = 1;
            _soundOn = take.HasAudio;
            Trim.Duration = take.Duration;
            Trim.In = 0;
            Trim.Out = take.Duration;
            Trim.Position = 0;
            Trim.InvalidateVisual();
            if (_preview != null)
            {
                _preview.PlaybackChanged += PreviewChanged;
                _preview.Muted = !_soundOn;
                _preview.ContextMenu = PreviewMenu();
            }
        }
        else if (_preview != null)
        {
            _preview.PlaybackChanged -= PreviewChanged;
            _preview = null;
        }

        Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded);
    }

    private void PreviewChanged()
    {
        if (_preview == null || _controller.Take is not { } take)
        {
            return;
        }

        if (Math.Abs(Trim.Duration - take.Duration) > 0.001)
        {
            // The player measured another length.
            var atEnd = Math.Abs(Trim.Out - Trim.Duration) < 0.01;
            Trim.Duration = take.Duration;
            if (atEnd)
            {
                Trim.Out = take.Duration;
            }
        }

        Trim.Position = _preview.Position;
        Trim.InvalidateVisual();
        PreviewTime.Text = $"{FormatTime(_preview.Position, true)} / {FormatTime(Trim.Out - Trim.In, true)}";
        PlayIcon.Style = (Style)FindResource(_preview.IsPlaying ? "IconPauseMask" : "IconPlayMask");
        if (_preview.PlaybackError is { } error && StatusText.Text != error)
        {
            ShowStatus(error);
        }
    }

    private bool _blinking;

    /// <summary>Paused: the pause button blinks amber.</summary>
    private void SetPauseBlink(bool on)
    {
        if (on == _blinking)
        {
            return;
        }

        _blinking = on;
        if (on)
        {
            PauseToggle.Foreground = new SolidColorBrush(RecordFrameWindow.Amber);
            PauseToggle.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(520))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            });
        }
        else
        {
            PauseToggle.BeginAnimation(OpacityProperty, null);
            PauseToggle.Opacity = 1;
            PauseToggle.ClearValue(ForegroundProperty);
        }
    }

    private static string FormatTime(double seconds, bool tenths)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var text = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
        return tenths ? text + "." + (t.Milliseconds / 100) : text;
    }

    private static string FormatSpeed(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ------------------------------------------------------------------ setup controls

    private void Fps_Click(object sender, RoutedEventArgs e) =>
        ShowMenu((FrameworkElement)sender, RecorderController.FpsChoices.Select(fps =>
            ($"{fps} fps", fps == _controller.Config.Fps, (Action)(() => _controller.SetFps(fps)))));

    private void SizePresets_Click(object sender, RoutedEventArgs e) =>
        ShowMenu((FrameworkElement)sender, RecorderController.SizePresets.Select(p =>
            ($"{p.W} × {p.H}", _controller.Rect.Width == p.W && _controller.Rect.Height == p.H, (Action)(() => _controller.SetSize(p.W, p.H)))));

    private void DelayToggle_Click(object sender, RoutedEventArgs e) =>
        _controller.SetDelay(DelayToggle.IsChecked == true, _controller.Config.Delay);

    private void Delay_Click(object sender, RoutedEventArgs e) =>
        ShowMenu((FrameworkElement)sender, Enumerable.Range(1, 10).Select(s =>
            ($"{s} 秒", _controller.Config.DelayOn && s == _controller.Config.Delay, (Action)(() => _controller.SetDelay(true, s)))));

    private void SizeBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyTypedSize();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            Refresh();
            e.Handled = true;
        }
    }

    private void SizeBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!WidthBox.IsKeyboardFocusWithin && !HeightBox.IsKeyboardFocusWithin)
        {
            ApplyTypedSize();
        }
    }

    private void ApplyTypedSize()
    {
        if (int.TryParse(WidthBox.Text.Trim(), out var w) && int.TryParse(HeightBox.Text.Trim(), out var h) &&
            (w != _controller.Rect.Width || h != _controller.Rect.Height))
        {
            _controller.SetSize(w, h);
        }

        Refresh();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Phase == RecordPhase.Setup)
        {
            _controller.Start();
        }
        else
        {
            _ = _controller.StopAsync();
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _controller.TogglePause();

    private void System_Click(object sender, RoutedEventArgs e) => _controller.SetSystemAudio(SystemToggle.IsChecked == true);

    private void Mic_Click(object sender, RoutedEventArgs e) => _controller.SetMicrophone(MicToggle.IsChecked == true);

    private void Keys_Click(object sender, RoutedEventArgs e) => _controller.SetKeys(KeysToggle.IsChecked == true);

    private void Retake_Click(object sender, RoutedEventArgs e) => _controller.Retake();

    private void Exit_Click(object sender, RoutedEventArgs e) => _controller.RequestClose();

    // ------------------------------------------------------------------ preview controls

    private void Play_Click(object sender, RoutedEventArgs e) => _preview?.TogglePlay();

    private void Speed_Click(object sender, RoutedEventArgs e) =>
        ShowMenu((FrameworkElement)sender, RecorderController.Speeds.Select(s =>
            (FormatSpeed(s), Math.Abs(s - _speed) < 1e-3, (Action)(() =>
            {
                _speed = s;
                if (_preview != null)
                {
                    _preview.Speed = s;
                }

                Refresh();
            }))));

    private void Sound_Click(object sender, RoutedEventArgs e)
    {
        _soundOn = SoundToggle.IsChecked == true;
        if (_preview != null)
        {
            _preview.Muted = !_soundOn;
        }
    }

    /// <summary>What the preview shows now becomes the export: trim, speed, sound.</summary>
    private ExportOptions Options(bool gif)
    {
        var config = _controller.Config;
        var fps = config.ExportFps > 0 ? config.ExportFps : _controller.Take?.Fps ?? 30;
        if (gif && config.ExportFps <= 0)
        {
            fps = 15;
        }

        return new ExportOptions(Trim.In, Trim.Out, _speed, !_soundOn, (RecordQuality)Math.Clamp(config.Quality, 0, 2), fps, gif);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => ShowSavePanel((FrameworkElement)sender);

    private async void SaveNow(bool gif)
    {
        if (_popup != null)
        {
            _popup.IsOpen = false;
        }

        var directory = _controller.SaveDirectory;
        Directory.CreateDirectory(directory);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存录像",
            InitialDirectory = directory,
            FileName = RecorderController.FileStamp() + (gif ? ".gif" : ".mp4"),
            Filter = gif ? "GIF 动图 (*.gif)|*.gif" : "MP4 视频 (*.mp4)|*.mp4",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (await _controller.ExportAsync(Options(gif), dialog.FileName))
        {
            _controller.MarkSaved(dialog.FileName);
            ((App)Application.Current).ShowSavedPrompt(dialog.FileName);
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e) => await _controller.CopyAsync(Options(false));

    /// <summary>Right-click on the preview: 保存 / 复制 / 重录 / 退出.</summary>
    private ContextMenu PreviewMenu()
    {
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("保存为 MP4…", () => SaveNow(false));
        Add("保存为 GIF…", () => SaveNow(true));
        Add("复制视频文件", () => _ = _controller.CopyAsync(Options(false)));
        menu.Items.Add(new Separator());
        Add("重录", _controller.Retake);
        Add("退出录屏", _controller.RequestClose);
        menu.Opened += (_, _) =>
        {
            var busy = _controller.Progress != null;
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                item.IsEnabled = !busy || (string)item.Header == "退出录屏";
            }
        };
        return menu;
    }

    /// <summary>Save panel: format · quality · frame rate, then 保存….</summary>
    private void ShowSavePanel(FrameworkElement anchor)
    {
        var config = _controller.Config;
        var panel = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };

        StackPanel Row(string label)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)), FontSize = 12, Width = 46, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(row);
            return row;
        }

        RadioButton Segment(Panel row, string text, string group, bool on, Action pick)
        {
            var button = new RadioButton { Content = text, GroupName = group + GetHashCode(), IsChecked = on, Style = (Style)FindResource("SegmentRadio") };
            button.Checked += (_, _) => pick();
            row.Children.Add(button);
            return button;
        }

        var gif = config.ExportGif;
        var formatRow = Row("格式");
        var fpsRow = new StackPanel { Orientation = Orientation.Horizontal };

        void FillFps()
        {
            fpsRow.Children.Clear();
            var stops = gif ? new[] { 10, 15, 24, 30 } : new[] { 15, 24, 30, 60 };
            Segment(fpsRow, "自适应", "fps", config.ExportFps <= 0, () => { config.ExportFps = 0; App.Config.Save(); });
            foreach (var stop in stops)
            {
                var value = stop;
                Segment(fpsRow, value.ToString(CultureInfo.InvariantCulture), "fps", config.ExportFps == value,
                    () => { config.ExportFps = value; App.Config.Save(); });
            }
        }

        Segment(formatRow, "MP4", "format", !gif, () => { gif = false; config.ExportGif = false; FillFps(); });
        Segment(formatRow, "GIF", "format", gif, () => { gif = true; config.ExportGif = true; FillFps(); });
        var qualityRow = Row("质量");
        var names = new[] { "流畅", "清晰", "高清" };
        for (var i = 0; i < names.Length; i++)
        {
            var level = i;
            Segment(qualityRow, names[i], "quality", config.Quality == i, () => { config.Quality = level; App.Config.Save(); });
        }

        var frameRow = Row("帧率");
        frameRow.Children.Add(fpsRow);
        FillFps();
        var save = new Button { Content = "保存…", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        save.Click += (_, _) => SaveNow(gif);
        panel.Children.Add(save);
        ShowPopup(anchor, panel);
    }

    // ------------------------------------------------------------------ menus, popups, confirm

    private void ShowMenu(FrameworkElement anchor, IEnumerable<(string Label, bool Checked, Action Action)> items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (label, isChecked, action) in items)
        {
            var item = new MenuItem { Header = label, IsChecked = isChecked };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void ShowPopup(FrameworkElement anchor, UIElement content)
    {
        if (_popup != null)
        {
            _popup.IsOpen = false;
        }

        _popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = (Brush)FindResource("PanelBackground"),
                BorderBrush = (Brush)FindResource("PanelBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = content
            }
        };
        _popup.IsOpen = true;
    }

    /// <summary>A small question above the toolbar (退出 / 重录 when something would be lost).</summary>
    public void Confirm(string message, string action, Action yes)
    {
        Activate();
        var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12), MaxWidth = 320 };
        panel.Children.Add(new TextBlock { Text = message, Foreground = Brushes.White, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Style = (Style)FindResource("FooterButton"), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = action, Style = (Style)FindResource("PrimaryButton") };
        cancel.Click += (_, _) => _popup!.IsOpen = false;
        ok.Click += (_, _) =>
        {
            _popup!.IsOpen = false;
            yes();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        ShowPopup(Root, panel);
        if (_popup != null)
        {
            _popup.Placement = PlacementMode.Top;
            _popup.HorizontalOffset = 0;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (WidthBox.IsKeyboardFocusWithin || HeightBox.IsKeyboardFocusWithin)
        {
            return;
        }

        if (e.Key == Key.Space && _controller.Phase == RecordPhase.Preview)
        {
            _preview?.TogglePlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _popup is { IsOpen: true })
        {
            _popup.IsOpen = false;
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ dragging the empty part moves the area / preview

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not (Border or StackPanel or TextBlock) || _controller.Phase is not (RecordPhase.Setup or RecordPhase.Preview))
        {
            return;
        }

        _dragging = true;
        _dragStart = Native.CursorPosition();
        _dragOrigin = _controller.Rect;
        Root.CaptureMouse();
        e.Handled = true;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        var c = Native.CursorPosition();
        _controller.MoveTo(_dragOrigin, c.X - _dragStart.X, c.Y - _dragStart.Y);
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_dragging)
        {
            _dragging = false;
            Root.ReleaseMouseCapture();
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
