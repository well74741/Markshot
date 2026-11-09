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
    private const double Gap = 8;

    public static readonly string[] Actions = { "Capture", "Record", "Ocr", "Editor", "History", "AddPin", "TogglePins" };

    /// <summary>The row was reordered here (long-press drag).</summary>
    public event Action? OrderChanged;

    /// <summary>Whether the pins show (显示贴图 dims while they are all hidden).</summary>
    public Func<bool>? PinsShown { get; set; }

    /// <summary>关闭所有贴图 was used and none is open now: the menu offers 打开所有贴图.</summary>
    public Func<bool>? PinsClosedAll { get; set; }

    /// <summary>Whether the open pins let the mouse through (null: no pin is open, the menu leaves 鼠标穿透 out).</summary>
    public Func<bool?>? PinsClickThrough { get; set; }

    /// <summary>
    /// A screenshot was taken: the button shows it at its own size for half a second, then it slides away to the
    /// right and the button's picture is there again. Hidden for the capture, it does this as it comes back.
    /// </summary>
    public void ShowThumbnail(BitmapSource shot)
    {
        if (_suspended || _fullscreenHidden || !IsVisible)
        {
            _pendingThumb = shot; // hidden now: shown as the button comes back
            return;
        }

        _pendingThumb = null;
        _thumbTimer?.Stop();
        _thumbSlide.BeginAnimation(TranslateTransform.XProperty, null);
        _thumbSlide.X = 0;
        _thumbImage.Source = shot;
        // The whole button (as big as its picture), rounded like it.
        _thumb.Width = _thumb.Height = MainSize;
        _thumb.CornerRadius = new CornerRadius(MainSize * 0.24);
        _thumb.BorderThickness = new Thickness(0);
        _thumb.HorizontalAlignment = HorizontalAlignment.Center;
        _thumb.VerticalAlignment = VerticalAlignment.Center;
        _thumb.Visibility = Visibility.Visible;
        _icon.Opacity = 0;
        _thumbTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _thumbTimer.Tick -= SlideThumbnailAway;
        _thumbTimer.Tick += SlideThumbnailAway;
        _thumbTimer.Start();
    }

    /// <summary>
    /// The tip above the row (below it when the button is near the top of the screen), clear of the buttons — at the
    /// pointer it lay over the next ones.
    /// </summary>
    private void PlaceTip(FrameworkElement element)
    {
        ToolTipService.SetPlacement(element, System.Windows.Controls.Primitives.PlacementMode.Top);
        ToolTipService.SetVerticalOffset(element, -6);
        element.ToolTipOpening += (_, _) =>
        {
            var work = Forms.Screen.FromPoint(new System.Drawing.Point((int)_centerX, (int)_centerY)).WorkingArea;
            var high = _centerY - work.Top < work.Height * 0.25;
            ToolTipService.SetPlacement(element, high ? System.Windows.Controls.Primitives.PlacementMode.Bottom : System.Windows.Controls.Primitives.PlacementMode.Top);
            ToolTipService.SetVerticalOffset(element, high ? 6 : -6);
        };
    }

    /// <summary>
    /// Back on top now and then: a program's message popup (bottom right) that came later was drawn over the button.
    /// </summary>
    private void KeepOnTop()
    {
        if (!IsVisible || _pressed || _moving || _reorder.IsDragging || _handle == IntPtr.Zero || ContextMenu is { IsOpen: true })
        {
            return;
        }

        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, 0, 0, 0, 0, 0x0001 /* NOSIZE */ | 0x0002 /* NOMOVE */ | Native.SWP_NOACTIVATE);
    }

    private readonly DispatcherTimer _topTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    /// <summary>Tests: the screenshot is on the button now.</summary>
    internal bool ShowingThumbnail => _thumb.Visibility == Visibility.Visible;

    private void SlideThumbnailAway(object? sender, EventArgs e)
    {
        _thumbTimer?.Stop();
        _icon.Opacity = _config.FloatIconOpacity;
        var slide = new DoubleAnimation(0, MainSize + 4, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        slide.Completed += (_, _) =>
        {
            if (_thumbTimer?.IsEnabled != true)
            {
                _thumb.Visibility = Visibility.Collapsed;
                _thumbImage.Source = null;
                _thumbSlide.BeginAnimation(TranslateTransform.XProperty, null);
                _thumbSlide.X = 0;
            }
        };
        _thumbSlide.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private readonly AppConfig _config;
    private readonly Action<string> _run;
    private readonly Action _openSettings;
    private readonly Action _close;
    private readonly Grid _root;
    private readonly StackPanel _row;
    private readonly Border _main;
    private readonly System.Windows.Controls.Image _icon;
    private readonly Border _plate;
    private readonly Grid _plateHost;
    private readonly StackPanel _items;
    private readonly ScaleTransform _plateScale = new(1, 1);
    private readonly ScaleTransform _pressScale = new(1, 1); // pressed: the button gives a little
    private readonly Border _thumb;
    private readonly System.Windows.Controls.Image _thumbImage;
    private readonly TranslateTransform _thumbSlide = new();
    private DispatcherTimer? _thumbTimer;
    private BitmapSource? _pendingThumb; // a screenshot taken while it was hidden
    private long _holdMoveUntil; // the row folding at the start of a drag: the window waits (no drawing while it moves)
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
        // A screenshot just taken shows here for a moment (same size), then slides away to the right.
        _thumbImage = new System.Windows.Controls.Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(_thumbImage, BitmapScalingMode.HighQuality);
        _thumb = new Border
        {
            Child = _thumbImage,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            Visibility = Visibility.Collapsed,
            RenderTransform = _thumbSlide,
            IsHitTestVisible = false
        };
        _main = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), // the whole round area takes the mouse
            Child = new Grid { Children = { _icon, _thumb }, ClipToBounds = true },
            Cursor = Cursors.Hand,
            ToolTip = "单击截图 · 拖动移动 · 右键更多", // (the click's action: see ApplyLook)
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _pressScale
        };
        ToolTipService.SetInitialShowDelay(_main, 900);
        PlaceTip(_main);
        _topTimer.Tick += (_, _) => KeepOnTop();
        _topTimer.Start();
        Closed += (_, _) => _topTimer.Stop();

        _items = new StackPanel { Orientation = Orientation.Horizontal };
        // The annotation bar's plate: same colour, corners, padding and the narrow inner shade; its buttons are the
        // bar's IconToolButton (30 DIP, 18 DIP icons, the same hover).
        var shade = new Border
        {
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(3),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0, 0, 0)),
            Effect = new BlurEffect { Radius = 4, KernelType = KernelType.Gaussian },
            IsHitTestVisible = false
        };
        var shadeClip = new Grid { IsHitTestVisible = false, Children = { shade } };
        shadeClip.SizeChanged += (_, _) => shadeClip.Clip = new RectangleGeometry(new Rect(0, 0, shadeClip.ActualWidth, shadeClip.ActualHeight), 9, 9);
        var face = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(5, 3, 5, 3), Child = _items };
        face.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xF21E1E1E));
        _plate = new Border
        {
            Child = new Grid { Children = { face, shadeClip } },
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Hidden, // keeps its room: the window does not change size when it opens
            RenderTransform = _plateScale
        };
        // Room for every button, whatever is shown: changing the row in the settings never resizes the window
        // (a resize moved the button for a frame — it flickered left and right).
        _plateHost = new Grid
        {
            Width = Actions.Length * 30 + 10 + Gap,
            LayoutTransform = UiScale.Transform, // as big as the annotation bar (工具栏大小)
            Children = { _plate }
        };
        UiScale.Changed += OnUiScaleChanged;
        _row = new StackPanel { Orientation = Orientation.Horizontal };
        _root = new Grid { Children = { _row }, Opacity = 0 }; // fades in once it is in place (no jump on the first frame)
        Content = _root;
        _reorder = new ReorderBehavior(_items);
        _reorder.Reordered += keys =>
        {
            _config.FloatActionOrder = string.Join(",", keys);
            _config.Save();
            OrderChanged?.Invoke(); // the settings' row follows
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
                Collapse(animate: true);
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
            UiScale.Changed -= OnUiScaleChanged;
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
        "AddPin" => "添加贴图",
        "TogglePins" => "显示 / 隐藏贴图",
        _ => key
    };

    public static string IconStyle(string key) => key switch
    {
        "Capture" => "IconCaptureMask",
        "Record" => "IconRecordMask",
        "Ocr" => "IconOcrMask",
        "History" => "IconHistoryMask",
        "AddPin" => "IconAddImageMask",
        "TogglePins" => "IconShowPinsMask",
        _ => "IconEditMask"
    };

    /// <summary>The row's buttons from the settings (order, the hidden ones out).</summary>
    public void BuildItems()
    {
        _items.Children.Clear();
        var hidden = ToolbarCatalog.Set(_config.FloatActionHidden);
        foreach (var key in ToolbarCatalog.Order(_config.FloatActionOrder, Actions))
        {
            var item = new Button
            {
                Style = (Style)Application.Current.FindResource("IconToolButton"),
                Content = new Border { Style = (Style)Application.Current.FindResource(IconStyle(key)) },
                Tag = key,
                ToolTip = Label(key) + "（长按拖动排序）",
                Visibility = hidden.Contains(key) ? Visibility.Collapsed : Visibility.Visible
            };
            PlaceTip(item);
            item.Click += (_, _) =>
            {
                if (_reorder.JustDragged || _reorder.IsDragging)
                {
                    return;
                }

                Run(key);
            };
            _items.Children.Add(item);
        }

        _plate.Visibility = _expanded && ShownActions.Count > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Picture, opacity and shadow from the settings (the shadow is the pins' one).</summary>
    public void ApplyLook()
    {
        var click = Actions.Contains(_config.FloatClickAction) ? _config.FloatClickAction : "Capture";
        _main.ToolTip = $"单击：{Label(click)} · 拖动移动 · 右键更多";
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
        if (_row.Children.Count == 0)
        {
            Arrange(_panelLeft);
        }

        if (IsLoaded)
        {
            Place(); // now: the size and the place change together (no frame at the old place)
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
        _plate.Padding = panelLeft ? new Thickness(0, 0, Gap, 0) : new Thickness(Gap, 0, 0, 0); // inside: the gap takes the mouse when open
        _plate.HorizontalAlignment = panelLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left; // next to the button
        _plate.RenderTransformOrigin = new Point(panelLeft ? 1 : 0, 0.5);
        if (panelLeft)
        {
            _row.Children.Add(_plateHost);
            _row.Children.Add(_main);
        }
        else
        {
            _row.Children.Add(_main);
            _row.Children.Add(_plateHost);
        }
    }

    private void Expand()
    {
        if (_expanded || ShownActions.Count == 0)
        {
            return;
        }

        _expanded = true;
        RefreshPinsButton();
        _plate.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // the gap to the button keeps the mouse too
        _plate.Visibility = Visibility.Visible;
        var duration = new Duration(TimeSpan.FromMilliseconds(90));
        _plateScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.4, 1, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _plate.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
    }

    private void Collapse() => Collapse(animate: false);

    /// <summary>The row folds away: at once (a capture is coming), or quickly drawn in (dragged, pointer gone).</summary>
    private void Collapse(bool animate) => Collapse(animate, FoldMs);

    private void Collapse(bool animate, int milliseconds)
    {
        if (!_expanded)
        {
            return;
        }

        _expanded = false;
        _plate.Background = null;
        if (!animate || _plate.Visibility != Visibility.Visible)
        {
            _plateScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _plate.BeginAnimation(OpacityProperty, null);
            _plate.Visibility = Visibility.Hidden; // at once
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var fold = new DoubleAnimation(0.4, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        var fade = new DoubleAnimation(0, duration);
        fade.Completed += (_, _) =>
        {
            if (!_expanded)
            {
                _plate.Visibility = Visibility.Hidden;
            }
        };
        _plateScale.BeginAnimation(ScaleTransform.ScaleXProperty, fold);
        _plate.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Tests: the row is folding away right now.</summary>
    internal bool Folding => _plate.Visibility == Visibility.Visible && !_expanded;

    private const int FoldMs = 100;
    private const int DragFoldMs = 45; // at the start of a drag: quick, the window waits only this long
    private const double PressedScale = 0.86;

    /// <summary>Pressed in a little (and back).</summary>
    private void Press(bool down)
    {
        var to = down ? PressedScale : 1.0;
        var motion = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(down ? 70 : 120)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _pressScale.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
        _pressScale.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
    }

    /// <summary>Tests: how far the button is pressed in.</summary>
    internal double PressScale => _pressScale.ScaleX;

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

    /// <summary>显示贴图 is dimmed while every pin is hidden.</summary>
    public void RefreshPinsButton()
    {
        foreach (var item in _items.Children.OfType<FrameworkElement>().Where(item => (string)item.Tag == "TogglePins"))
        {
            item.Opacity = PinsShown?.Invoke() == false ? 0.4 : 1;
        }
    }

    private void Run(string key)
    {
        if (key != "TogglePins")
        {
            Collapse(); // (显示贴图 keeps the row open: the pointer is still on it)
        }

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
        Press(down: true);
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
            // The row draws in quickly (the button stays pressed in until let go); nothing animates while the
            // window then moves: a see-through window drawn and moved at once showed a frame on its path.
            if (_expanded)
            {
                Collapse(animate: true, DragFoldMs);
                _holdMoveUntil = Environment.TickCount64 + DragFoldMs;
            }

            _pressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _pressScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _pressScale.ScaleX = _pressScale.ScaleY = PressedScale;
        }

        if (Environment.TickCount64 < _holdMoveUntil)
        {
            return; // the next move after the fold catches up
        }

        // At most one move per drawn frame, made just before WPF draws it: a see-through window moved between its
        // frames was now and then shown once more at a place on its path.
        _pendingMove = (_pressCenter.X + dx, _pressCenter.Y + dy);
        if (!_moveQueued)
        {
            _moveQueued = true;
            CompositionTarget.Rendering += ApplyPendingMove;
        }
    }

    private (double X, double Y)? _pendingMove;
    private bool _moveQueued;

    private void ApplyPendingMove(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= ApplyPendingMove;
        _moveQueued = false;
        if (_pendingMove is { } target)
        {
            _pendingMove = null;
            MoveTo(target.X, target.Y, keepInside: false);
        }
    }

    /// <summary>Tests: a drag move waits for the next frame.</summary>
    internal bool MoveWaiting => _pendingMove != null;

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
        Press(down: false);
        if (_moveQueued)
        {
            CompositionTarget.Rendering -= ApplyPendingMove;
            _moveQueued = false;
        }

        if (_pendingMove is { } last)
        {
            _pendingMove = null;
            _centerX = last.X; // where it was let go (placed inside the screen just below)
            _centerY = last.Y;
        }
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
            Run(Actions.Contains(_config.FloatClickAction) ? _config.FloatClickAction : "Capture"); // the click's action (设置 → 悬浮按钮)
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

    private void OnUiScaleChanged() => Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded);

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
        // Through Left / Top, not SetWindowPos: WPF draws a see-through window together with its own idea of the
        // position, and after a SetWindowPos the next frame showed it once more where it had been (a flash on the drag path).
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = x / dpi.DpiScaleX;
        Top = y / dpi.DpiScaleY;
        if (!_moving)
        {
            Native.SetWindowPos(_handle, Native.HWND_TOPMOST, 0, 0, 0, 0, 0x0001 /* NOSIZE */ | 0x0002 /* NOMOVE */ | Native.SWP_NOACTIVATE);
        }
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
            // Gone at once (no fade to wait for: the capture opens sooner), then off the screen before it is taken.
            _fadingOut = false;
            _root.BeginAnimation(OpacityProperty, null);
            _root.Opacity = 0;
        }

        UpdateShown();
        if (wasShown)
        {
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
        if (_pendingThumb is { } shot)
        {
            ShowThumbnail(shot); // coming back with the screenshot taken meanwhile
        }
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
        void Add(string header, string key)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => Run(key);
            menu.Items.Add(item);
        }

        Add("添加贴图…", "AddPin");
        Add("粘贴剪贴板", "PasteClipboard");
        if (PinsClosedAll?.Invoke() == true)
        {
            Add("打开所有贴图", "OpenAllPins"); // the ones 关闭所有贴图 closed
        }
        else
        {
            Add("关闭所有贴图", "CloseAllPins");
        }
        if (PinsClickThrough?.Invoke() is { } through)
        {
            var clickThrough = new MenuItem { Header = "鼠标穿透", IsChecked = through, ToolTip = "所有贴图都让鼠标点击穿过（再点一次恢复）" };
            clickThrough.Click += (_, _) => Run("ToggleClickThrough");
            menu.Items.Add(clickThrough);
        }

        menu.Items.Add(new Separator());
        var hotkeys = new MenuItem { Header = "禁用所有快捷键", IsChecked = _config.HotkeysDisabled };
        hotkeys.Click += (_, _) => Run("ToggleHotkeys");
        menu.Items.Add(hotkeys);
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
