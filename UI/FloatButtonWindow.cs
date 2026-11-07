using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Forms = System.Windows.Forms;

namespace Markshot.UI;

/// <summary>
/// 悬浮按钮: a round button that floats on the desktop. A click captures; with the pointer on it a row of actions
/// (截屏 / 录屏 / 文字识别 / 图片编辑器) slides out sideways at once and goes as soon as the pointer leaves. Dragging
/// moves it (it stays on a screen: dropped beyond an edge it comes back inside); right click has its menu. The window
/// never takes the focus and hides itself before a screenshot is taken.
/// </summary>
public sealed class FloatButtonWindow : Window
{
    /// <summary>The main button at size 1 (the setting goes up to twice that).</summary>
    public const double BaseSize = 46;

    private double MainSize => BaseSize * Math.Clamp(_config.FloatIconSize, 1, 2);
    private const double ItemSize = 34;
    private const double Gap = 8;

    public static readonly string[] Actions = { "Capture", "Record", "Ocr", "Editor", "History" };

    private readonly AppConfig _config;
    private readonly Action<string> _run;
    private readonly Action _openSettings;
    private readonly Action _close;
    private readonly Grid _root;
    private readonly StackPanel _row;
    private readonly Border _main;
    private readonly System.Windows.Controls.Image _icon;
    private readonly Border _plate;
    private readonly StackPanel _items;
    private readonly ScaleTransform _plateScale = new(1, 1);
    private readonly ReorderBehavior _reorder;
    private readonly DispatcherTimer _fullscreenWatch = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private IntPtr _handle;
    private double _centerX;
    private double _centerY;
    private bool _panelLeft;
    private bool _expanded;
    private bool _suspended;
    private bool _fullscreenHidden;
    private bool _pressed;
    private bool _moving;
    private Native.POINT _pressPoint;
    private (double X, double Y) _pressCenter;
    private bool _hoverBlocked; // back after a capture with the pointer on it: stays closed until the pointer comes again
    private bool _fadingOut;

    /// <summary>Where the pointer is (screen pixels); replaceable in tests.</summary>
    internal static Func<Native.POINT> Pointer = Native.CursorPosition;

    /// <summary>Whether a full-screen program is in front on the button's monitor; replaceable in tests.</summary>
    public Func<bool>? FullscreenOverride;

    public FloatButtonWindow(AppConfig config, Action<string> run, Action openSettings, Action close)
    {
        _config = config;
        _run = run;
        _openSettings = openSettings;
        _close = close;
        Title = "Markshot 悬浮按钮";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Left = -32000;
        Top = -32000;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        _icon = new System.Windows.Controls.Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        _main = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), // the whole round area takes the mouse
            Child = _icon,
            Cursor = Cursors.Hand,
            ToolTip = "单击截图 · 拖动移动 · 右键更多"
        };
        ToolTipService.SetInitialShowDelay(_main, 900);

        _items = new StackPanel { Orientation = Orientation.Horizontal };
        _plate = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(4),
            Child = _items,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Hidden, // keeps its room: the window does not change size when it opens
            RenderTransform = _plateScale
        };
        _plate.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xF21E1E1E));
        _row = new StackPanel { Orientation = Orientation.Horizontal };
        _root = new Grid { Children = { _row }, Opacity = 0 }; // fades in once it is in place (no jump on the first frame)
        Content = _root;
        _reorder = new ReorderBehavior(_items);
        _reorder.Reordered += keys =>
        {
            _config.FloatActionOrder = string.Join(",", keys);
            _config.Save();
        };

        BuildItems();
        ApplyLook();

        _main.MouseEnter += (_, _) =>
        {
            if (!_pressed && !_hoverBlocked)
            {
                Expand();
            }
        };
        _main.MouseLeave += (_, _) =>
        {
            if (!_expanded)
            {
                _hoverBlocked = false;
            }
        };
        _row.MouseLeave += (_, _) =>
        {
            _hoverBlocked = false;
            if (!_reorder.IsDragging && !_pressed)
            {
                Collapse();
            }
        };
        _main.MouseLeftButtonDown += OnMainDown;
        _main.MouseMove += OnMainMove;
        _main.MouseLeftButtonUp += OnMainUp;
        _main.LostMouseCapture += (_, _) =>
        {
            if (_pressed)
            {
                EndPress(click: false);
            }
        };
        // A picture dragged onto the button is pinned on the screen.
        _main.AllowDrop = true;
        _main.DragEnter += OnDragOver;
        _main.DragOver += OnDragOver;
        _main.Drop += (_, e) =>
        {
            e.Handled = true;
            var index = 0;
            foreach (var file in DroppedFiles(e.Data))
            {
                PinFileRequested?.Invoke(file, index++);
            }

            if (!e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                PinRequested?.Invoke(bitmap);
            }
        };
        _main.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowMenu();
        };

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(_handle, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_handle, Native.GWL_EXSTYLE, new IntPtr(style | Native.WS_EX_TOOLWINDOW | 0x08000000 /* WS_EX_NOACTIVATE */));
        };
        Loaded += (_, _) =>
        {
            (_centerX, _centerY) = StartCenter();
            Place();
            // Shown only once it is where it belongs, side and all (it was drawn once on the wrong side: a jump).
            Dispatcher.BeginInvoke(() => FadeIn(), DispatcherPriority.Render);
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            _fullscreenWatch.Stop();
        };
        _fullscreenWatch.Tick += (_, _) => CheckFullscreen();
        _fullscreenWatch.Start();
    }

    public bool IsExpanded => _expanded;

    /// <summary>A picture dropped on the button (not a file): the app pins it in the middle of the screen.</summary>
    public event Action<BitmapSource>? PinRequested;

    /// <summary>A picture file dropped on the button: the app pins it (a moving one plays) in the middle of the screen.</summary>
    public event Action<string, int>? PinFileRequested;

    /// <summary>The files of a drop (at most 20).</summary>
    public static List<string> DroppedFiles(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files ? files.Where(File.Exists).Take(20).ToList() : new List<string>();

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>The pictures in a drop (files that do not open as pictures are skipped; at most 20).</summary>
    public static List<BitmapSource> DroppedImages(IDataObject data)
    {
        var images = new List<BitmapSource>();
        if (data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files.Take(20))
            {
                try
                {
                    images.Add(ImageFile.Load(file));
                }
                catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or FileFormatException or ArgumentException or InvalidOperationException)
                {
                    Log.Info("拖到悬浮按钮的文件不是图片：" + file);
                }
            }
        }
        else if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            images.Add(bitmap);
        }

        return images;
    }

    /// <summary>The panel opens to the left (the button is on the right half of its screen).</summary>
    public bool PanelOnLeft => _panelLeft;

    /// <summary>The main button's centre (screen pixels).</summary>
    public Point Center => new(_centerX, _centerY);

    /// <summary>The action keys shown in the row, in order.</summary>
    public IReadOnlyList<string> ShownActions => _items.Children.OfType<FrameworkElement>()
        .Where(item => item.Visibility == Visibility.Visible).Select(item => (string)item.Tag).ToList();

    public System.Windows.Controls.Image IconImage => _icon;

    public static string Label(string key) => key switch
    {
        "Capture" => "截屏",
        "Record" => "录屏",
        "Ocr" => "文字识别",
        "Editor" => "图片编辑器",
        "History" => "贴图历史",
        _ => key
    };

    public static string IconStyle(string key) => key switch
    {
        "Capture" => "IconCaptureMask",
        "Record" => "IconRecordMask",
        "Ocr" => "IconOcrMask",
        "History" => "IconHistoryMask",
        _ => "IconEditMask"
    };

    /// <summary>The row's buttons from the settings (order, the hidden ones out).</summary>
    public void BuildItems()
    {
        _items.Children.Clear();
        var hidden = ToolbarCatalog.Set(_config.FloatActionHidden);
        foreach (var key in ToolbarCatalog.Order(_config.FloatActionOrder, Actions))
        {
            var mask = new Border { Style = (Style)Application.Current.FindResource(IconStyle(key)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Theme.Bind(mask, Border.BackgroundProperty, 0xFFFFFFFF);
            var item = new Border
            {
                Width = ItemSize,
                Height = ItemSize,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Child = mask,
                Tag = key,
                Cursor = Cursors.Hand,
                ToolTip = Label(key) + "（长按拖动排序）",
                Visibility = hidden.Contains(key) ? Visibility.Collapsed : Visibility.Visible
            };
            item.MouseEnter += (_, _) => Theme.Bind(item, Border.BackgroundProperty, 0x26FFFFFF);
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, e) =>
            {
                if (_reorder.JustDragged || _reorder.IsDragging)
                {
                    return;
                }

                e.Handled = true;
                Run(key);
            };
            _items.Children.Add(item);
        }

        _plate.Visibility = _expanded && ShownActions.Count > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Picture, opacity and shadow from the settings (the shadow is the pins' one).</summary>
    public void ApplyLook()
    {
        FloatIcon.Load(_config).Play(_icon);
        _main.Width = MainSize;
        _main.Height = MainSize;
        _main.CornerRadius = new CornerRadius(MainSize / 2);
        var opacity = Math.Clamp(_config.FloatIconOpacity, 0.2, 1);
        _icon.Opacity = opacity;
        var size = Math.Clamp(_config.PinShadowSize, 0, 40);
        var shadowOpacity = Math.Clamp(_config.PinShadowOpacity, 0, 1) * opacity;
        var color = ParseColor(_config.PinShadowColor);
        Effect? Shadow() => size >= 0.5 && shadowOpacity >= 0.01
            ? new DropShadowEffect { BlurRadius = size, ShadowDepth = 0, Color = color, Opacity = shadowOpacity, RenderingBias = RenderingBias.Quality }
            : null;
        _icon.Effect = Shadow();
        _plate.Effect = Shadow();
        _root.Margin = new Thickness(Math.Max(8, size + 4));
        Arrange(_panelLeft);
        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded);
        }
    }

    private static Color ParseColor(string? text)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text) is Color color)
            {
                return color;
            }
        }
        catch (FormatException)
        {
        }

        return Color.FromRgb(0x6A, 0x6A, 0x6A);
    }

    /// <summary>The row in order for the side the panel opens to.</summary>
    private void Arrange(bool panelLeft)
    {
        _panelLeft = panelLeft;
        _row.Children.Clear();
        _plate.Margin = panelLeft ? new Thickness(0, 0, Gap, 0) : new Thickness(Gap, 0, 0, 0);
        _plate.RenderTransformOrigin = new Point(panelLeft ? 1 : 0, 0.5);
        if (panelLeft)
        {
            _row.Children.Add(_plate);
            _row.Children.Add(_main);
        }
        else
        {
            _row.Children.Add(_main);
            _row.Children.Add(_plate);
        }
    }

    private void Expand()
    {
        if (_expanded || ShownActions.Count == 0)
        {
            return;
        }

        _expanded = true;
        _row.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // the gap keeps the mouse too
        _plate.Visibility = Visibility.Visible;
        var duration = new Duration(TimeSpan.FromMilliseconds(90));
        _plateScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.4, 1, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _plate.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
    }

    private void Collapse()
    {
        if (!_expanded)
        {
            return;
        }

        _expanded = false;
        _row.Background = null;
        _plateScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _plate.BeginAnimation(OpacityProperty, null);
        _plate.Visibility = Visibility.Hidden; // at once
    }

    /// <summary>Tests: the pointer on / off the button.</summary>
    internal void Hover(bool on)
    {
        if (on)
        {
            Expand();
        }
        else
        {
            Collapse();
        }
    }

    private void Run(string key)
    {
        Collapse();
        Dispatcher.BeginInvoke(() => _run(key), DispatcherPriority.Background);
    }

    // ---- dragging -----------------------------------------------------------------------------

    private void OnMainDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _moving = false;
        _pressPoint = Pointer();
        _pressCenter = (_centerX, _centerY);
        _main.CaptureMouse();
        e.Handled = true;
    }

    private void OnMainMove(object sender, MouseEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        var now = Pointer();
        var dx = now.X - _pressPoint.X;
        var dy = now.Y - _pressPoint.Y;
        if (!_moving && Math.Abs(dx) + Math.Abs(dy) < 5)
        {
            return;
        }

        if (!_moving)
        {
            _moving = true;
            Collapse();
        }

        MoveTo(_pressCenter.X + dx, _pressCenter.Y + dy, keepInside: false);
    }

    private void OnMainUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressed)
        {
            e.Handled = true;
            EndPress(click: !_moving);
        }
    }

    private void EndPress(bool click)
    {
        var moved = _moving;
        _pressed = false;
        _moving = false;
        if (_main.IsMouseCaptured)
        {
            _main.ReleaseMouseCapture();
        }

        if (moved)
        {
            MoveTo(_centerX, _centerY, keepInside: true);
            Remember();
        }
        else if (click)
        {
            Run("Capture");
        }
    }

    /// <summary>Tests: a drag of the button by (dx, dy) screen pixels.</summary>
    internal void DragBy(double dx, double dy)
    {
        MoveTo(_centerX + dx, _centerY + dy, keepInside: true);
        Remember();
    }

    private void Remember()
    {
        _config.FloatButtonX = (int)Math.Round(_centerX);
        _config.FloatButtonY = (int)Math.Round(_centerY);
        _config.Save();
    }

    /// <summary>悬浮按钮回中: to the middle of the screen with the mouse.</summary>
    public void Recenter()
    {
        var cursor = Pointer();
        var bounds = Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea;
        MoveTo(bounds.Left + bounds.Width / 2.0, bounds.Top + bounds.Height / 2.0, keepInside: true);
        Remember();
        Resume();
    }

    private void MoveTo(double x, double y, bool keepInside)
    {
        if (keepInside)
        {
            (x, y) = Inside(x, y);
        }

        _centerX = x;
        _centerY = y;
        Place();
    }

    /// <summary>The button whole on the work area of the screen it is (mostly) on — never lost beyond an edge.</summary>
    private (double X, double Y) Inside(double x, double y)
    {
        var work = Forms.Screen.FromPoint(new System.Drawing.Point((int)x, (int)y)).WorkingArea;
        var half = MainSize / 2 * Scale + 2;
        x = Math.Clamp(x, work.Left + half, Math.Max(work.Left + half, work.Right - half));
        y = Math.Clamp(y, work.Top + half, Math.Max(work.Top + half, work.Bottom - half));
        return (x, y);
    }

    private double Scale => _handle != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleX : 1;

    private (double X, double Y) StartCenter()
    {
        if (_config.FloatButtonX != int.MinValue && _config.FloatButtonY != int.MinValue)
        {
            return Inside(_config.FloatButtonX, _config.FloatButtonY); // a monitor that is gone: back on one that is there
        }

        var work = Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 720);
        var offset = (MainSize / 2 + 28) * Scale;
        return (work.Right - offset, work.Bottom - offset);
    }

    private void OnDisplayChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => MoveTo(_centerX, _centerY, keepInside: true), DispatcherPriority.Background);

    /// <summary>The window around the main button's centre; the row opens towards the middle of the screen.</summary>
    private void Place()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)_centerX, (int)_centerY)).Bounds;
        var left = _centerX > screen.Left + screen.Width / 2.0;
        if (left != _panelLeft || _row.Children.Count == 0)
        {
            Arrange(left);
        }

        UpdateLayout();
        var scale = Scale;
        var margin = _root.Margin.Left;
        var mainX = _panelLeft ? ActualWidth - margin - MainSize / 2 : margin + MainSize / 2;
        var x = (int)Math.Round(_centerX - mainX * scale);
        var y = (int)Math.Round(_centerY - ActualHeight / 2 * scale);
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, x, y, 0, 0, 0x0001 /* NOSIZE */ | Native.SWP_NOACTIVATE);
    }

    // ---- hidden during a capture / behind full-screen programs ------------------------------------

    /// <summary>
    /// Out of the way while a capture is open — and off the screen before the screenshot is taken: Windows cannot leave
    /// a see-through (layered) window out of captures, so it is hidden and the screen composed once more without it.
    /// </summary>
    public void Suspend()
    {
        var wasShown = IsVisible;
        _suspended = true;
        Collapse();
        if (wasShown)
        {
            // A quick fade, waited for here (the screenshot comes right after), then gone before it is taken.
            FadeOutAndWait();
        }

        UpdateShown();
        if (wasShown)
        {
            DwmFlush();
            DwmFlush();
        }
    }

    public void Resume()
    {
        _suspended = false;
        // Back with the pointer still on it (it was clicked): closed until the pointer leaves and comes again.
        var cursor = Pointer();
        _hoverBlocked = PointerOnItOverride?.Invoke() ?? (_handle != IntPtr.Zero && Native.GetWindowRect(_handle, out var rect) &&
                        cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom);
        UpdateShown();
    }

    /// <summary>Tests: whether the pointer is on the button.</summary>
    public Func<bool>? PointerOnItOverride;

    /// <summary>Closed after a capture until the pointer comes again (tests).</summary>
    internal bool HoverBlocked { get => _hoverBlocked; set => _hoverBlocked = value; }

    internal double Shown => _root.Opacity;

    private static readonly Duration FadeTime = new(TimeSpan.FromMilliseconds(110));

    private void FadeIn()
    {
        _fadingOut = false;
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeTime) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private void FadeOut(Action completed)
    {
        _fadingOut = true;
        var animation = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(80)));
        animation.Completed += (_, _) =>
        {
            if (_fadingOut)
            {
                completed();
            }
        };
        _root.BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>Fades out and returns when it is done (at most ~150 ms).</summary>
    private void FadeOutAndWait()
    {
        var frame = new DispatcherFrame();
        FadeOut(() => frame.Continue = false);
        var guard = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Send, (_, _) => frame.Continue = false, Dispatcher);
        guard.Start();
        Dispatcher.PushFrame(frame);
        guard.Stop();
        _fadingOut = false;
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = 0;
    }

    public bool IsHiddenForFullscreen => _fullscreenHidden;

    public void CheckFullscreen()
    {
        var hide = _config.FloatHideFullscreen && (FullscreenOverride?.Invoke() ?? FullscreenInFront());
        if (hide != _fullscreenHidden)
        {
            _fullscreenHidden = hide;
            UpdateShown();
        }
    }

    private void UpdateShown()
    {
        var show = !_suspended && !_fullscreenHidden;
        if (show && (!IsVisible || _fadingOut))
        {
            if (!IsVisible)
            {
                _root.BeginAnimation(OpacityProperty, null);
                _root.Opacity = 0;
                Show();
                Place();
            }

            FadeIn();
        }
        else if (!show && IsVisible)
        {
            Collapse();
            if (_root.Opacity <= 0.01 && !_fadingOut)
            {
                Hide();
            }
            else if (!_fadingOut)
            {
                FadeOut(() =>
                {
                    _fadingOut = false;
                    if (_suspended || _fullscreenHidden)
                    {
                        Hide();
                    }
                });
            }
        }
    }

    /// <summary>The window in front covers its whole monitor (a video, a game, a presentation) — the button's monitor.</summary>
    private bool FullscreenInFront()
    {
        var front = Native.GetForegroundWindow();
        if (front == IntPtr.Zero || front == _handle)
        {
            return false;
        }

        Native.GetWindowThreadProcessId(front, out var process);
        if (process == Environment.ProcessId)
        {
            return false; // Markshot's own (the capture is full-screen too)
        }

        var name = new StringBuilder(64);
        Native.GetClassName(front, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false; // the desktop
        }

        if (!Native.GetWindowRect(front, out var rect))
        {
            return false;
        }

        var monitor = Forms.Screen.FromHandle(front).Bounds;
        var mine = Forms.Screen.FromPoint(new System.Drawing.Point((int)_centerX, (int)_centerY)).Bounds;
        return monitor == mine && rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom;
    }

    // ---- menu -----------------------------------------------------------------------------------

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var settings = new MenuItem { Header = "设置…" };
        settings.Click += (_, _) => _openSettings();
        menu.Items.Add(settings);
        var fullscreen = new MenuItem { Header = "全屏时自动隐藏", IsCheckable = true, IsChecked = _config.FloatHideFullscreen };
        fullscreen.Click += (_, _) =>
        {
            _config.FloatHideFullscreen = fullscreen.IsChecked;
            _config.Save();
            CheckFullscreen();
        };
        menu.Items.Add(fullscreen);
        menu.Items.Add(new Separator());
        var close = new MenuItem { Header = "关闭悬浮按钮" };
        close.Click += (_, _) => _close();
        menu.Items.Add(close);
        return menu;
    }

    /// <summary>The right-click menu's entries (tests).</summary>
    internal IReadOnlyList<string> MenuHeaders() => BuildMenu().Items.OfType<MenuItem>().Select(item => (string)item.Header).ToList();

    private void ShowMenu()
    {
        Collapse();
        var menu = BuildMenu();
        menu.PlacementTarget = _main;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        Native.SetForegroundWindow(_handle); // so a click elsewhere closes the menu
        menu.IsOpen = true;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
