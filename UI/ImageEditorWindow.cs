using System.Windows.Controls.Primitives;
using Markshot.Annotations;
using Markshot.Core.Recording;
using Microsoft.Win32;

namespace Markshot.UI;

/// <summary>
/// 图片编辑器: a normal window around the pin's annotation canvas — all annotation tools work the
/// same as on a pin — with the image operations on the right: size, rotation, mirroring, crop,
/// path cut-out, keystone correction and colour adjustments. Every operation is one undo step
/// (the history also carries the image). Opens files, takes dropped files, saves in the common
/// formats and pins the result back onto the screen.
/// </summary>
public sealed class ImageEditorWindow : PinWindow
{
    private const int PreviewSide = 1400;

    /// <summary>The image state undone together with the annotations.</summary>
    private sealed record State(BitmapSource Origin, BitmapSource Base, ImageAdjust Adjust);

    private readonly DockPanel _layout = new();
    private FrameworkElement? _panel;
    private readonly Canvas _stage; // the picture sits on it anywhere: dragged with the middle button, zoomed at the mouse
    private Point _pan; // where the picture's top-left corner is on the stage (DIP)
    private readonly Border _dropHint;
    private readonly Border _emptyHint;
    private readonly Border _sessionBar;
    private readonly TextBlock _sessionText = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _sessionApply;
    private readonly TextBlock _statusSize = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusHint = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16, 0, 12, 0) };
    private readonly Canvas _toolbarLayer = new() { ClipToBounds = true };
    private readonly System.Windows.Shapes.Rectangle _checker = new() { IsHitTestVisible = false };
    private Point _toolbarAnchor = new(0.5, 14); // the floating bar: centre (fraction of the width), gap to the bottom
    private readonly TextBlock _statusZoom = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Width = 48, TextAlignment = TextAlignment.Center };
    private readonly TextBox _widthBox = new();
    private readonly TextBox _heightBox = new();
    private readonly CheckBox _keepRatio = new() { Content = "锁定比例", IsChecked = true, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Slider _angle = new() { Minimum = -45, Maximum = 45, IsMoveToPointEnabled = true };
    private readonly TextBlock _angleValue = new() { FontSize = 12, Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, (Slider Slider, TextBlock Value)> _sliders = new();
    private readonly ToggleButton _invert;
    private readonly Dictionary<EditGroup, Button> _resetButtons = new();
    // The picture before the first change of each group, while only that group has changed it since.
    private readonly Dictionary<EditGroup, (State State, List<Annotation> Items)> _beforeGroup = new();
    private EditGroup? _lastGroup;
    private readonly DispatcherTimer _adjustTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private BitmapSource _origin;
    private BitmapSource? _proxy;
    private ImageAdjust _adjust = new();
    private bool _adjustPending;
    private bool _syncingControls;
    private EditorOverlay? _overlay;
    private string? _path;
    private PinWindow? _sourcePin;
    private PinWindow? _waitingPin; // hidden while this editor is open: it is topmost and would cover the window
    private bool _hasImage;
    private bool _unsavedNew;
    private long _savedChange;
    private bool _forceClose;
    private bool _firstFit = true;
    private Point _panStart;
    private Point _panOrigin;
    private bool _panning;

    /// <summary>The panel's groups of picture changes (each has its own 重置).</summary>
    private enum EditGroup
    {
        Size,
        Turn,
        Cut
    }

    public ImageEditorWindow(BitmapSource? image = null, string? path = null, List<Annotation>? annotations = null, PinWindow? sourcePin = null)
        : this(image == null ? ImageOps.FromPixels(new byte[4], 1, 1) : ImageOps.Normalize(image), image != null, path, annotations, sourcePin)
    {
    }

    private ImageEditorWindow(BitmapSource image, bool hasImage, string? path, List<Annotation>? annotations, PinWindow? sourcePin)
        : base(image, 0, 0, image, annotations)
    {
        _origin = image;
        _path = path;
        _sourcePin = sourcePin;
        _waitingPin = sourcePin;
        _hasImage = hasImage;
        // Not saved anywhere yet (a capture, a long screenshot): closing asks first.
        _unsavedNew = hasImage && path == null && sourcePin == null;

        AllowsTransparency = false;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        ShowActivated = true;
        Topmost = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1240, work.Width - 40);
        Height = Math.Min(820, work.Height - 40);
        MinWidth = 900;
        MinHeight = 520;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Markshot;component/Assets/markshot.ico"));
        SetResourceReference(BackgroundProperty, Theme.Key(0xFF1A1A1A));
        Theme.TrackTitleBar(this);
        UpdateTitle();

        // The pin's image area moves onto a free stage; the toolbar sits above it.
        var canvas = RootGrid;
        Content = null;
        // The checkerboard behind see-through parts covers only the visible part of the picture: on the
        // picture itself it would be tiled over the whole (zoomed) size, which made zooming in slow.
        ImageHost.Background = Brushes.Transparent;
        _checker.Fill = (Brush)Application.Current.FindResource("SmallCheckerBrush");
        _stage = new Canvas { ClipToBounds = true, Background = Brushes.Transparent, Focusable = false };
        _stage.Children.Add(_checker);
        _stage.Children.Add(canvas);
        _stage.PreviewMouseWheel += Stage_PreviewMouseWheel;
        _stage.PreviewMouseDown += Stage_PreviewMouseDown;
        _stage.PreviewMouseMove += Stage_PreviewMouseMove;
        _stage.PreviewMouseUp += Stage_PreviewMouseUp;
        _stage.MouseLeftButtonDown += (_, _) =>
        {
            // A click on the empty space around the image: deselect.
            if (!ImageHost.IsMouseOver && _overlay == null)
            {
                _textEditor.Commit();
                ClearSelection();
            }
        };
        _stage.SizeChanged += (_, e) =>
        {
            if (_firstFit && _hasImage && _stage.ActualWidth > 0)
            {
                Fit();
                return;
            }

            // The picture keeps its place relative to the middle of the view.
            MoveTo(new Point(_pan.X + (e.NewSize.Width - e.PreviousSize.Width) / 2, _pan.Y + (e.NewSize.Height - e.PreviousSize.Height) / 2));
        };

        _emptyHint = EmptyHint();
        _dropHint = new Border
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            BorderThickness = new Thickness(2),
            Margin = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Application.Current.FindResource("Accent"),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0x8D, 0xFF)),
            Child = new TextBlock { Text = "松开以打开图片", Foreground = Brushes.White, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };

        _sessionApply = new Button { Content = "应用", Style = (Style)Application.Current.FindResource("PrimaryButton"), MinWidth = 64, Height = 28, Margin = new Thickness(14, 0, 0, 0) };
        _sessionApply.Click += (_, _) => ApplySession();
        var sessionCancel = new Button { Content = "取消", Style = (Style)Application.Current.FindResource("FooterButton"), MinWidth = 64, Height = 28, Margin = new Thickness(8, 0, 0, 0) };
        sessionCancel.Click += (_, _) => CancelSession();
        var sessionRow = new StackPanel { Orientation = Orientation.Horizontal };
        _sessionText.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFF0F0F0));
        sessionRow.Children.Add(_sessionText);
        sessionRow.Children.Add(_sessionApply);
        sessionRow.Children.Add(sessionCancel);
        _sessionBar = new Border
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(14, 7, 8, 7),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = sessionRow
        };
        _sessionBar.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        _sessionBar.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        UiScale.Attach(_sessionBar);

        var middle = new Grid();
        middle.Children.Add(_stage);
        middle.Children.Add(_emptyHint);
        middle.Children.Add(_sessionBar);
        middle.Children.Add(_toolbarLayer);
        middle.Children.Add(_dropHint);

        _invert = TextToggle("反色", "颜色取反");
        var panel = BuildPanel();
        var status = BuildStatusBar();
        // The annotation bar floats at the bottom of the picture area; its grip moves it anywhere.
        _toolbarLayer.Children.Add(Toolbar);
        Toolbar.SizeChanged += (_, _) => PlaceToolbar();
        _toolbarLayer.SizeChanged += (_, _) => PlaceToolbar();
        Toolbar.DragFinished += RememberToolbarPlace;
        DockPanel.SetDock(status, Dock.Bottom);
        DockPanel.SetDock(panel, Dock.Right);
        _layout.Children.Add(status);
        _layout.Children.Add(panel);
        _layout.Children.Add(middle);
        _layout.SetResourceReference(Panel.BackgroundProperty, Theme.Key(0xFF1A1A1A));
        Content = _layout;

        Toolbar.Visibility = Visibility.Visible;
        Toolbar.OpenRequested += OpenDialog;
        Toolbar.PinRequested += PinToScreen;
        Toolbar.ToolChanged += _ => CancelSession();
        Toolbar.ToolChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateHint), DispatcherPriority.Input);
        Toolbar.VariantChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateHint), DispatcherPriority.Input);
        PreviewMouseUp += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateHint), DispatcherPriority.Input);
        PreviewKeyUp += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateHint), DispatcherPriority.Input);
        SourceInitialized += (_, _) =>
        {
            // A lone Alt (Alt + wheel sizes the brush) must not put the window into menu mode.
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(
                (IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool handled) =>
                {
                    if (message == 0x0112 && (wParam.ToInt64() & 0xFFF0) == 0xF100)
                    {
                        handled = true;
                    }

                    return IntPtr.Zero;
                });
        };
        Toolbar.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(new Action(ReclaimKeyboard), DispatcherPriority.Input);

        _history.CaptureExtra = () =>
        {
            BakeAdjust(); // a step always holds finished pixels
            return new State(_origin, _baseImage, _adjust);
        };
        _history.RestoreExtra = extra =>
        {
            if (extra is State state)
            {
                RestoreState(state);
            }

            // Undo / redo: which group changed the picture last is not known any more.
            _beforeGroup.Clear();
            _lastGroup = null;
            UpdateResetButtons();
        };
        _history.ExtraSize = extra => extra is State s
            ? new (object, long)[] { (s.Origin, s.Origin.PixelWidth * 4L * s.Origin.PixelHeight), (s.Base, s.Base.PixelWidth * 4L * s.Base.PixelHeight) }
            : Array.Empty<(object, long)>();
        _adjustTimer.Tick += (_, _) =>
        {
            // Still holding a slider: the same step goes on.
            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                BakeAdjust();
            }
        };

        AllowDrop = true;
        DragEnter += OnDrag;
        DragOver += OnDrag;
        DragLeave += (_, _) => _dropHint.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        ContextMenuOpening += (_, e) =>
        {
            if (!ImageHost.IsMouseOver)
            {
                e.Handled = true;
            }
        };
        Closing += OnClosingEditor;
        Closed += (_, _) =>
        {
            _adjustTimer.Stop();
            _waitingPin?.ShowAgain(); // not re-pinned: the pin it came from comes back as it was
        };
        Loaded += (_, _) =>
        {
            EnterEditMode();
            RefreshPanel();
            Fit();
        };
    }

    private protected override bool IsEditor => true;

    /// <summary>For tests: where 贴到屏幕 goes instead of the app (base image, annotations, composed picture).</summary>
    public Func<BitmapSource, List<Annotation>, BitmapSource, PinWindow>? PinAction { get; init; }

    private protected override double MinZoom => 0.02;

    /// <summary>The picture as it is now (annotations and adjustments included).</summary>
    public BitmapSource Composite => ComposeEditedImage();

    public int ImageWidth => _baseImage.PixelWidth;

    public int ImageHeight => _baseImage.PixelHeight;

    public int AnnotationCount => Editor.Items.Count;

    public bool HasImage => _hasImage;

    public string? FilePath => _path;

    /// <summary>Edited since the last save / copy / pin.</summary>
    public bool IsDirty => _hasImage && (_unsavedNew || _history.ChangeCount != _savedChange || _adjustPending);

    private protected override bool KeysBelongToUi =>
        Keyboard.FocusedElement is TextBox box && !ReferenceEquals(box, TextEditor) && !ReferenceEquals(box, WatermarkEditor);

    // ------------------------------------------------------------------ panel

    private static TextBlock Label(string text, double size = 12)
    {
        var label = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFA8A8A8));
        return label;
    }

    private static ToggleButton TextToggle(string text, string tip) => new()
    {
        Content = text,
        ToolTip = tip,
        Style = (Style)Application.Current.FindResource("ToolToggle"),
        Height = 30,
        Margin = new Thickness(0, 0, 6, 0)
    };

    private static Button TextButton(string text, string tip, Action click)
    {
        var button = new Button
        {
            Content = text,
            ToolTip = tip,
            Style = (Style)Application.Current.FindResource("FooterButton"),
            MinWidth = 0,
            Height = 30,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, 6, 6)
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static Border Section(string title, Button? reset, params UIElement[] children)
    {
        var stack = new StackPanel();
        var heading = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        heading.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFE6E6E6));
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        if (reset != null)
        {
            reset.Height = 24;
            reset.FontSize = 11.5;
            reset.Margin = new Thickness(0);
            DockPanel.SetDock(reset, Dock.Right);
            header.Children.Add(reset);
        }

        header.Children.Add(heading);
        stack.Children.Add(header);
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        var card = new Border { Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF222222));
        card.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x14FFFFFF));
        return card;
    }

    private FrameworkElement BuildPanel()
    {
        foreach (var box in new[] { _widthBox, _heightBox })
        {
            box.Style = (Style)Application.Current.FindResource("InputBox");
            box.Width = 66;
            box.Padding = new Thickness(6, 4, 6, 4);
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    ApplyTypedSize();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    RefreshPanel();
                    ReclaimKeyboard();
                    e.Handled = true;
                }
            };
        }

        _widthBox.TextChanged += (_, _) => KeepRatio(_widthBox);
        _heightBox.TextChanged += (_, _) => KeepRatio(_heightBox);
        _keepRatio.SetResourceReference(ForegroundProperty, Theme.Key(0xFFD8D8D8));
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
        sizeRow.Children.Add(_widthBox);
        sizeRow.Children.Add(new TextBlock { Text = "×", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray });
        sizeRow.Children.Add(_heightBox);
        var applySize = TextButton("应用", "改变图片尺寸（批注会合并进图片）", ApplyTypedSize);
        applySize.Margin = new Thickness(8, 0, 0, 0);
        sizeRow.Children.Add(applySize);

        var turnRow = new WrapPanel();
        turnRow.Children.Add(TextButton("左转 90°", "逆时针旋转 90°", () => Turn(false)));
        turnRow.Children.Add(TextButton("右转 90°", "顺时针旋转 90°", () => Turn(true)));
        turnRow.Children.Add(TextButton("水平镜像", "左右翻转", () => Flip(true)));
        turnRow.Children.Add(TextButton("垂直镜像", "上下翻转", () => Flip(false)));
        _angle.ValueChanged += (_, _) => PreviewAngle();
        var angleRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var angleLabel = Label("角度");
        angleLabel.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(angleLabel, Dock.Left);
        DockPanel.SetDock(_angleValue, Dock.Right);
        _angleValue.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFC8C8C8));
        _angleValue.Text = "0°";
        angleRow.Children.Add(angleLabel);
        angleRow.Children.Add(_angleValue);
        angleRow.Children.Add(_angle);
        var applyAngle = TextButton("应用角度", "按角度旋转，画布随之变大（批注会合并进图片）", ApplyAngle);
        applyAngle.Margin = new Thickness(0, 6, 0, 0);
        applyAngle.HorizontalAlignment = HorizontalAlignment.Left;

        var cutRow = new WrapPanel();
        cutRow.Children.Add(TextButton("矩形裁切", "拖出要保留的区域（批注保持可编辑）", () => StartSession(EditorOverlayMode.Crop)));
        cutRow.Children.Add(TextButton("路径裁切", "沿手绘路径抠图，路径外变透明", () => StartSession(EditorOverlayMode.Path)));
        cutRow.Children.Add(TextButton("梯形校正", "拖动四个角对准倾斜的矩形，校正为正矩形", () => StartSession(EditorOverlayMode.Keystone)));
        cutRow.Children.Add(TextButton("网格形变", "像 PS 的变形：单击图片交叉拆分网格，拖动交点、边角和手柄弯曲图片", () => StartSession(EditorOverlayMode.Mesh)));

        var colour = new StackPanel();
        colour.Children.Add(AdjustSlider("亮度", "Brightness", -100, 100, 0));
        colour.Children.Add(AdjustSlider("对比度", "Contrast", -100, 100, 0));
        colour.Children.Add(AdjustSlider("饱和度", "Saturation", -100, 100, 0));
        colour.Children.Add(AdjustSlider("色相", "Hue", -180, 180, 0));
        colour.Children.Add(AdjustSlider("透明度", "Opacity", 0, 100, 100));
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _invert.Click += (_, _) => AdjustChanged();
        toggles.Children.Add(_invert);
        var curves = TextButton("曲线…", "打开曲线表调整明暗", OpenCurves);
        curves.Margin = new Thickness(0, 0, 6, 0);
        toggles.Children.Add(curves);
        colour.Children.Add(toggles);

        Button Reset(EditGroup group, string tip)
        {
            var button = TextButton("重置", tip, () => ResetGroup(group));
            _resetButtons[group] = button;
            return button;
        }

        var stack = new StackPanel { Margin = new Thickness(10) };
        stack.Children.Add(Section("尺寸", Reset(EditGroup.Size, "回到改尺寸之前（之后做过其他图片操作时不可用，可用 Ctrl+Z 撤销）"), sizeRow, _keepRatio));
        stack.Children.Add(Section("旋转与镜像", Reset(EditGroup.Turn, "回到旋转 / 镜像之前（之后做过其他图片操作时不可用，可用 Ctrl+Z 撤销）"), turnRow, angleRow, applyAngle));
        stack.Children.Add(Section("裁切", Reset(EditGroup.Cut, "回到裁切 / 梯形校正之前（之后做过其他图片操作时不可用，可用 Ctrl+Z 撤销）"), cutRow));
        stack.Children.Add(Section("调色", TextButton("重置", "调色恢复默认", ResetAdjust), colour));
        UiScale.Attach(stack);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = stack };
        var frame = new Border { Width = 262, BorderThickness = new Thickness(1, 0, 0, 0), Child = scroll };
        _panel = frame;
        frame.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF151515));
        frame.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x14FFFFFF));
        UiScale.Changed += () => frame.Width = 262 * UiScale.Factor;
        frame.Width = 262 * UiScale.Factor;
        return frame;
    }

    private FrameworkElement AdjustSlider(string title, string key, double min, double max, double start)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = start, IsMoveToPointEnabled = true };
        var value = new TextBlock { FontSize = 12, Width = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = start.ToString("0", CultureInfo.InvariantCulture) };
        value.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFC8C8C8));
        _sliders[key] = (slider, value);
        slider.ValueChanged += (_, _) =>
        {
            value.Text = Math.Round(slider.Value).ToString("0", CultureInfo.InvariantCulture);
            AdjustChanged();
        };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var label = Label(title);
        label.Width = 48;
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(label);
        row.Children.Add(value);
        row.Children.Add(slider);
        return row;
    }

    private FrameworkElement BuildStatusBar()
    {
        Button Small(string text, string tip, Action click)
        {
            var button = TextButton(text, tip, click);
            button.Height = 24;
            button.Margin = new Thickness(4, 0, 0, 0);
            button.FontSize = 11.5;
            return button;
        }

        _statusSize.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFA8A8A8));
        _statusZoom.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFC8C8C8));
        var bar = new DockPanel { Margin = new Thickness(10, 4, 10, 4) };
        var zoom = new StackPanel { Orientation = Orientation.Horizontal };
        zoom.Children.Add(Small("−", "缩小（Ctrl+-）", () => ZoomStep(1 / 1.25)));
        zoom.Children.Add(_statusZoom);
        zoom.Children.Add(Small("+", "放大（Ctrl++）", () => ZoomStep(1.25)));
        zoom.Children.Add(Small("适应", "适应窗口（Ctrl+0）", Fit));
        zoom.Children.Add(Small("100%", "实际大小（Ctrl+1）", () => SetZoom(1)));
        DockPanel.SetDock(zoom, Dock.Right);
        bar.Children.Add(zoom);
        DockPanel.SetDock(_statusSize, Dock.Left);
        bar.Children.Add(_statusSize);
        _statusHint.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF8A8A8A));
        bar.Children.Add(_statusHint);
        var frame = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
        frame.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x14FFFFFF));
        UiScale.Attach(bar);
        return frame;
    }

    private Border EmptyHint()
    {
        var open = new Button { Content = "打开图片…", Style = (Style)Application.Current.FindResource("PrimaryButton"), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        open.Click += (_, _) => OpenDialog();
        var text = new TextBlock { Text = "把图片拖到这里，或点击打开", FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF9A9A9A));
        var formats = new TextBlock { Text = "PNG · JPEG · WebP · BMP · GIF · TIFF · ICO", FontSize = 12, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        formats.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF6E6E6E));
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(text);
        stack.Children.Add(formats);
        stack.Children.Add(open);
        UiScale.Attach(stack);
        return new Border { Child = stack, Background = Brushes.Transparent };
    }

    private void UpdateTitle() => Title = (_path != null ? Path.GetFileName(_path) + " - " : "") + "Markshot 图片编辑器";

    /// <summary>Puts the current image's numbers into the controls.</summary>
    private void RefreshPanel()
    {
        _syncingControls = true;
        _widthBox.Text = _hasImage ? _baseImage.PixelWidth.ToString(CultureInfo.InvariantCulture) : "";
        _heightBox.Text = _hasImage ? _baseImage.PixelHeight.ToString(CultureInfo.InvariantCulture) : "";
        if (Math.Abs(_angle.Value) > 0.001)
        {
            ImageHost.RenderTransform = Transform.Identity; // a previewed angle that was not applied
        }

        _angle.Value = 0;
        _sliders["Brightness"].Slider.Value = _adjust.Brightness;
        _sliders["Contrast"].Slider.Value = _adjust.Contrast;
        _sliders["Saturation"].Slider.Value = _adjust.Saturation;
        _sliders["Opacity"].Slider.Value = _adjust.Opacity;
        _sliders["Hue"].Slider.Value = _adjust.Hue;
        _invert.IsChecked = _adjust.Invert;
        _syncingControls = false;
        _emptyHint.Visibility = _hasImage ? Visibility.Collapsed : Visibility.Visible;
        _checker.Visibility = _hasImage ? Visibility.Visible : Visibility.Collapsed;
        ImageHost.Visibility = _hasImage ? Visibility.Visible : Visibility.Collapsed;
        if (_panel != null)
        {
            _panel.IsEnabled = _hasImage;
        }

        UpdateResetButtons();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        _statusSize.Text = _hasImage ? $"{_baseImage.PixelWidth} × {_baseImage.PixelHeight} 像素" + (_path != null ? "　·　" + _path : "") : "";
        _statusZoom.Text = $"{_zoom * 100:0}%";
    }

    // ------------------------------------------------------------------ zoom and pan

    /// <summary>Zooms keeping the picture point under <paramref name="anchor"/> (stage DIP; default: the middle of the view) in place.</summary>
    private void SetZoom(double zoom, Point? anchor = null)
    {
        var at = anchor ?? new Point(_stage.ActualWidth / 2, _stage.ActualHeight / 2);
        var before = _zoom;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        RenderOptions.SetBitmapScalingMode(Img, _zoom >= 3 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        var ratio = _zoom / before;
        _pan = new Point(at.X - (at.X - _pan.X) * ratio, at.Y - (at.Y - _pan.Y) * ratio);
        ApplyBounds();
        MoveTo(_pan);
        _textEditor.Refresh();
        var pointer = Mouse.GetPosition(ImageHost);
        UpdateCursor(new Point(pointer.X / _zoom, pointer.Y / _zoom));
        if (_overlay != null)
        {
            _overlay.Zoom = _zoom;
            _overlay.InvalidateVisual();
        }

        UpdateStatus();
    }

    private void ZoomStep(double factor) => SetZoom(_zoom * factor);

    /// <summary>
    /// Fits the picture into the view and puts it in the middle (F). A very long one fits its short
    /// side and starts at its top.
    /// </summary>
    public void Fit()
    {
        if (!_hasImage || _stage.ActualWidth <= 0)
        {
            return;
        }

        var width = Math.Max(50, _stage.ActualWidth - 40);
        var height = Math.Max(50, _stage.ActualHeight - 40);
        double w = _baseImage.PixelWidth, h = _baseImage.PixelHeight;
        var tall = Math.Max(h / w, w / h) > 3;
        var fit = tall ? Math.Max(width / w, height / h) : Math.Min(width / w, height / h);
        _firstFit = false;
        SetZoom(Math.Min(1, fit));
        Center();
        if (tall)
        {
            // A long screenshot starts at its beginning.
            MoveTo(new Point(w * _zoom > _stage.ActualWidth ? 20 : _pan.X, h * _zoom > _stage.ActualHeight ? 20 : _pan.Y));
        }
    }

    /// <summary>The picture in the middle of the view at the current zoom.</summary>
    public void Center() =>
        MoveTo(new Point((_stage.ActualWidth - _baseImage.PixelWidth * _zoom) / 2, (_stage.ActualHeight - _baseImage.PixelHeight * _zoom) / 2));

    /// <summary>Puts the picture's corner at <paramref name="position"/>; a part of it always stays in view.</summary>
    private void MoveTo(Point position)
    {
        const double keep = 48;
        double w = _baseImage.PixelWidth * _zoom, h = _baseImage.PixelHeight * _zoom;
        _pan = new Point(
            Math.Clamp(position.X, Math.Min(0, keep - w), Math.Max(0, _stage.ActualWidth - keep)),
            Math.Clamp(position.Y, Math.Min(0, keep - h), Math.Max(0, _stage.ActualHeight - keep)));
        Canvas.SetLeft(RootGrid, Math.Round(_pan.X));
        Canvas.SetTop(RootGrid, Math.Round(_pan.Y));
        var visible = Rect.Intersect(new Rect(Math.Round(_pan.X), Math.Round(_pan.Y), w, h), new Rect(0, 0, _stage.ActualWidth, _stage.ActualHeight));
        if (visible.IsEmpty)
        {
            _checker.Width = _checker.Height = 0;
            return;
        }

        Canvas.SetLeft(_checker, visible.X);
        Canvas.SetTop(_checker, visible.Y);
        _checker.Width = visible.Width;
        _checker.Height = visible.Height;
    }

    private const double MaxZoom = 32;

    // ------------------------------------------------------------------ the floating annotation bar

    private void PlaceToolbar()
    {
        double width = Toolbar.ActualWidth, height = Toolbar.ActualHeight, areaWidth = _toolbarLayer.ActualWidth, areaHeight = _toolbarLayer.ActualHeight;
        if (areaWidth <= 0 || width <= 0)
        {
            return;
        }

        Canvas.SetLeft(Toolbar, Math.Round(Math.Clamp(_toolbarAnchor.X * areaWidth - width / 2, 4, Math.Max(4, areaWidth - width - 4))));
        Canvas.SetTop(Toolbar, Math.Round(Math.Clamp(areaHeight - _toolbarAnchor.Y - height, 4, Math.Max(4, areaHeight - height - 4))));
    }

    /// <summary>Dragged by its grip: it keeps that place (also when the window is resized or the settings row opens).</summary>
    private void RememberToolbarPlace()
    {
        var areaWidth = Math.Max(1, _toolbarLayer.ActualWidth);
        var left = Canvas.GetLeft(Toolbar);
        var top = Canvas.GetTop(Toolbar);
        _toolbarAnchor = new Point((left + Toolbar.ActualWidth / 2) / areaWidth, _toolbarLayer.ActualHeight - top - Toolbar.ActualHeight);
        PlaceToolbar();
    }

    /// <summary>Where the floating bar is (tests).</summary>
    public Rect ToolbarBounds => new(Canvas.GetLeft(Toolbar), Canvas.GetTop(Toolbar), Toolbar.ActualWidth, Toolbar.ActualHeight);

    public Size ToolbarArea => new(_toolbarLayer.ActualWidth, _toolbarLayer.ActualHeight);

    // ------------------------------------------------------------------ the status bar: what works now

    /// <summary>The keys and mouse actions for what is going on (tool, selection, a crop …).</summary>
    public string Hint => _statusHint.Text;

    private void UpdateHint()
    {
        const string view = "滚轮缩放 · 中键拖动画布 · F 适应窗口";
        string text;
        if (!_hasImage)
        {
            text = "打开或拖入一张图片";
        }
        else if (_overlay != null)
        {
            text = _overlay.Mode switch
            {
                EditorOverlayMode.Crop => "拖出保留区域 · 拖边角调整 · 拖框内移动",
                EditorOverlayMode.Path => "按住沿边缘画一圈 · Shift 直线段",
                EditorOverlayMode.Mesh => _overlay.WarpSplit
                    ? "拖动图片带动最近的交点 · 拖交点 / 边角移动 · 拖蓝色手柄弯曲边线"
                    : "单击图片交叉拆分一次（虚线预览位置），之后只需拖动",
                _ => "拖四个角对准倾斜的矩形"
            } + " · Enter 应用 · Esc 取消 · " + view;
        }
        else if (_textEditor.IsActive)
        {
            text = "输入文字 · Enter 完成 · Shift+Enter 换行 · Alt+滚轮 字号 · Ctrl+B 粗体 · Esc 完成";
        }
        else if (_polyline != null)
        {
            text = "单击放拐点 · 双击 / Enter / 右键结束 · Backspace 撤回 · Shift 45° · Esc 放弃";
        }
        else if (_selected != null)
        {
            text = "拖动移动 · 拖手柄缩放 / 旋转 · Alt+滚轮 大小 · Ctrl+滚轮 透明度 · Ctrl+C / V 复制粘贴 · Delete 删除 · Esc 取消选择";
        }
        else
        {
            text = _tool switch
            {
                AnnotationTool.Rectangle or AnnotationTool.Ellipse => "拖动绘制 · Shift 正方形 / 圆 · Ctrl 圆角 · 按住空格移动 · Alt+滚轮 线宽 · Tab 换样式",
                AnnotationTool.Arrow or AnnotationTool.Line => "拖动绘制 · Shift 45° · E 旋转 · 按住空格移动 · Alt+滚轮 粗细 · Tab 箭头 / 直线",
                AnnotationTool.Polyline => "单击放第一个点 · 也可拖出一段 · Alt+滚轮 粗细",
                AnnotationTool.Pen or AnnotationTool.Highlighter => "拖动书写 · Shift 直线 · Alt+滚轮 笔刷大小 · Ctrl+滚轮 透明度 · Tab 画笔 / 荧光笔",
                AnnotationTool.Mosaic => "拖出打码区域 · Alt+滚轮 程度 · Tab 画笔马赛克",
                AnnotationTool.MosaicBrush => "涂抹打码 · Shift 直线 · Alt+滚轮 笔刷大小 · Tab 区域马赛克",
                AnnotationTool.Text => "单击输入文字 · 单击已有文字编辑 · 拖动移动 · Alt+滚轮 字号",
                AnnotationTool.Watermark => "拖动移动水印 · 双击修改文字 · Alt+滚轮 字号",
                AnnotationTool.Number => "单击放序号 · Alt+滚轮 大小",
                AnnotationTool.Eraser => "拖动擦掉批注 · Alt+滚轮 橡皮大小 · Ctrl+滚轮 擦除强度",
                _ => "单击批注选中 · 拖动移动 · Ctrl+Z 撤销 · Ctrl+S 保存 · Ctrl+T 贴到屏幕"
            } + " · " + view;
        }

        _statusHint.Text = text;
        _statusHint.ToolTip = text;
    }

    /// <summary>The picture's top-left corner on the stage (tests).</summary>
    public Point PanOffset => _pan;

    /// <summary>
    /// The wheel zooms at the mouse. With an element selected or a tool active, Alt + wheel is the
    /// element's / tool's size and Ctrl + wheel its opacity.
    /// </summary>
    private void Stage_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var drawing = _overlay == null && (_textEditor.IsActive || _selected != null || _tool is not (AnnotationTool.None or AnnotationTool.Select));
        if (drawing && (modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) != 0)
        {
            OnMouseWheel(this, e);
            e.Handled = true;
            return;
        }

        ZoomAt(e.Delta, e.GetPosition(_stage));
        e.Handled = true;
    }

    /// <summary>One wheel notch of zoom at a point of the view.</summary>
    public void ZoomAt(int delta, Point at) => SetZoom(_zoom * (delta > 0 ? 1.1 : 1 / 1.1), at);

    private void Stage_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The keyboard goes back to the canvas when it is clicked (the size boxes apply on Enter only).
        if (KeysBelongToUi && (Keyboard.FocusedElement as DependencyObject) != null)
        {
            ReclaimKeyboard();
        }

        if (e.ChangedButton == MouseButton.Middle)
        {
            // Whatever still holds the mouse (a slider of the bar, a drawing) lets go first.
            Mouse.Captured?.ReleaseMouseCapture();
            _panning = true;
            _panStart = e.GetPosition(_stage);
            _panOrigin = _pan;
            _stage.CaptureMouse();
            Mouse.OverrideCursor = Cursors.SizeAll;
            e.Handled = true;
        }
    }

    private void Stage_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        if (e.MiddleButton != MouseButtonState.Pressed)
        {
            EndPan(); // the release happened elsewhere
            return;
        }

        var delta = e.GetPosition(_stage) - _panStart;
        MoveTo(_panOrigin + delta);
        e.Handled = true;
    }

    private void Stage_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning && e.ChangedButton == MouseButton.Middle)
        {
            EndPan();
            e.Handled = true;
        }
    }

    private void EndPan()
    {
        _panning = false;
        Mouse.OverrideCursor = null;
        if (_stage.IsMouseCaptured)
        {
            _stage.ReleaseMouseCapture();
        }
    }

    /// <summary>A middle-button drag of the picture by <paramref name="delta"/> (tests).</summary>
    public void PanBy(Vector delta) => MoveTo(_pan + delta);

    // ------------------------------------------------------------------ the document

    /// <summary>Starts over on an image (opened / dropped); not one undo step.</summary>
    public void LoadImage(BitmapSource image, string? path)
    {
        CancelSession();
        _textEditor.Commit();
        ClearSelection();
        _adjustTimer.Stop();
        _adjustPending = false;
        Editor.Items.Clear();
        _history.Clear();
        _beforeGroup.Clear();
        _lastGroup = null;
        _rotationBase = null;
        _savedChange = _history.ChangeCount;
        _hasImage = true;
        _path = path;
        _waitingPin?.ShowAgain(); // another picture now: the pin it came from is unrelated from here on
        _waitingPin = null;
        _sourcePin = null;
        _unsavedNew = path == null;
        _adjust = new ImageAdjust();
        _proxy = null;
        var normal = ImageOps.Normalize(image);
        _origin = normal;
        SetBitmap(normal);
        Editor.InvalidateVisual();
        UpdateUndoButtons();
        UpdateTitle();
        RefreshPanel();
        Fit();
        UpdateHint();
    }

    /// <summary>The bitmap under the annotations changes (size included); mosaics are rebuilt from it.</summary>
    private void SetBitmap(BitmapSource image)
    {
        _baseImage = image;
        _pixelWidth = image.PixelWidth;
        _pixelHeight = image.PixelHeight;
        Editor.Source = image;
        Img.Source = image;
        foreach (var item in Editor.Items.Where(item => item is MosaicAnnotation or MosaicBrushAnnotation))
        {
            MosaicBuilder.Rebuild(item, image);
        }

        ApplyBounds();
        MoveTo(_pan);
        Editor.InvalidateVisual();
    }

    private void RestoreState(State state)
    {
        _adjustTimer.Stop();
        _adjustPending = false;
        _origin = state.Origin;
        _proxy = null;
        _adjust = state.Adjust;
        SetBitmap(state.Base);
        RefreshPanel();
    }

    /// <summary>
    /// One undoable change of the image's geometry: pending colour changes are finished first, what
    /// comes out becomes the new starting point (the colour sliders return to neutral).
    /// </summary>
    private void GeometryStep(EditGroup group, Func<BitmapSource> change)
    {
        if (!_hasImage)
        {
            return;
        }

        _textEditor.Commit();
        ClearSelection();
        BakeAdjust();
        if (_lastGroup != group)
        {
            // Another kind of change: the earlier groups cannot be put back on their own any more.
            _beforeGroup.Clear();
        }

        if (!_beforeGroup.ContainsKey(group))
        {
            _beforeGroup[group] = (new State(_origin, _baseImage, _adjust), Editor.Items.Select(item => item.Clone()).ToList());
        }

        _lastGroup = group;
        _history.Checkpoint(Editor.Items);
        var result = change();
        _origin = result;
        _proxy = null;
        _adjust = new ImageAdjust();
        SetBitmap(result);
        UpdateUndoButtons();
        RefreshPanel();
    }

    /// <summary>重置 of a group: the picture as it was before that group's changes (one undo step).</summary>
    private void ResetGroup(EditGroup group)
    {
        if (!_beforeGroup.TryGetValue(group, out var before))
        {
            return;
        }

        _textEditor.Commit();
        ClearSelection();
        _history.Checkpoint(Editor.Items);
        _beforeGroup.Clear();
        _lastGroup = null;
        _adjustTimer.Stop();
        _adjustPending = false;
        _origin = before.State.Origin;
        _proxy = null;
        _adjust = before.State.Adjust;
        SetBitmap(before.State.Base);
        Editor.Items.Clear();
        Editor.Items.AddRange(before.Items.Select(item => item.Clone()));
        Editor.InvalidateVisual();
        UpdateUndoButtons();
        RefreshPanel();
        Fit();
    }

    /// <summary>A group's 重置 works while that group was the last to change the picture.</summary>
    public bool CanReset(string group) => Enum.TryParse<EditGroup>(group, out var parsed) && _beforeGroup.ContainsKey(parsed);

    public void Reset(string group)
    {
        if (Enum.TryParse<EditGroup>(group, out var parsed))
        {
            ResetGroup(parsed);
        }
    }

    private void UpdateResetButtons()
    {
        foreach (var (group, button) in _resetButtons)
        {
            button.IsEnabled = _beforeGroup.ContainsKey(group);
        }
    }

    /// <summary>Annotations into the pixels: for operations they cannot follow (they stay undoable).</summary>
    private BitmapSource Flattened()
    {
        var flat = ImageOps.Normalize(base.ComposeEditedImage());
        Editor.Items.Clear();
        return flat;
    }

    private protected override BitmapSource ComposeEditedImage()
    {
        BakeAdjust();
        return base.ComposeEditedImage();
    }

    public void ResizeTo(int width, int height)
    {
        if (width >= 1 && height >= 1 && (width != _baseImage.PixelWidth || height != _baseImage.PixelHeight))
        {
            GeometryStep(EditGroup.Size, () => ImageOps.Resize(Flattened(), width, height));
            Fit();
        }
    }

    private void ApplyTypedSize()
    {
        if (int.TryParse(_widthBox.Text.Trim(), out var width) && int.TryParse(_heightBox.Text.Trim(), out var height) &&
            width is >= 1 and <= 30000 && height is >= 1 and <= 30000)
        {
            ResizeTo(width, height);
        }

        RefreshPanel();
        ReclaimKeyboard();
    }

    private void KeepRatio(TextBox changed)
    {
        if (_syncingControls || _keepRatio.IsChecked != true || !changed.IsKeyboardFocused || !int.TryParse(changed.Text.Trim(), out var value) || value < 1)
        {
            return;
        }

        _syncingControls = true;
        if (ReferenceEquals(changed, _widthBox))
        {
            _heightBox.Text = Math.Max(1, (int)Math.Round(value * (double)_baseImage.PixelHeight / _baseImage.PixelWidth)).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            _widthBox.Text = Math.Max(1, (int)Math.Round(value * (double)_baseImage.PixelWidth / _baseImage.PixelHeight)).ToString(CultureInfo.InvariantCulture);
        }

        _syncingControls = false;
    }

    public void Flip(bool horizontal)
    {
        var op = horizontal ? 'H' : 'V';
        Orient(() => ApplyOrientation(op, record: false));
    }

    public void Turn(bool clockwise)
    {
        Orient(() =>
        {
            if (!clockwise)
            {
                ApplyOrientation('R', record: false);
                return;
            }

            // Three turns the other way (annotations follow each); the picture swings once.
            for (var i = 0; i < 3; i++)
            {
                var width = _baseImage.PixelWidth;
                ApplyPinImageTransform(new RotateTransform(-90), p => new Point(p.Y, width - p.X), _baseImage.PixelHeight, width, 'R');
            }

            AnimateOrientation('R', clockwise: true);
        });
    }

    /// <summary>A flip / quarter turn: annotations follow and stay editable.</summary>
    private void Orient(Action turn)
    {
        GeometryStep(EditGroup.Turn, () =>
        {
            turn();
            return ImageOps.Normalize(_baseImage);
        });
    }

    private void PreviewAngle()
    {
        _angleValue.Text = $"{_angle.Value:0.#}°";
        if (_syncingControls)
        {
            return;
        }

        ImageHost.RenderTransformOrigin = new Point(0.5, 0.5);
        ImageHost.RenderTransform = new RotateTransform(_angle.Value);
    }

    private void ApplyAngle()
    {
        var angle = Math.Round(_angle.Value, 1);
        ImageHost.RenderTransform = Transform.Identity;
        if (Math.Abs(angle) >= 0.05)
        {
            RotateBy(angle);
        }

        RefreshPanel();
    }

    /// <summary>
    /// 应用角度. Several angles in a row turn the picture as it was before the first of them by the
    /// sum, so the canvas only ever holds the turned picture (it used to turn the previous result,
    /// transparent corners included, and grew each time).
    /// </summary>
    public void RotateBy(double degrees)
    {
        var continuing = _rotationBase != null && _history.ChangeCount == _rotationChange;
        var total = (continuing ? _rotationTotal : 0) + degrees;
        total = Math.IEEERemainder(total, 360);
        var from = continuing ? _rotationBase : null;
        GeometryStep(EditGroup.Turn, () =>
        {
            var source = from ?? Flattened();
            _rotationBase = source;
            return Math.Abs(total) < 0.01 ? source : ImageOps.Rotate(source, total);
        });
        _rotationTotal = total;
        _rotationChange = _history.ChangeCount;
        Fit();
    }

    private BitmapSource? _rotationBase;
    private double _rotationTotal;
    private long _rotationChange = -1;

    public void CropTo(Int32Rect rect)
    {
        GeometryStep(EditGroup.Cut, () =>
        {
            var cropped = ImageOps.Crop(_baseImage, rect);
            // The annotations keep their place on the picture and stay editable.
            var moved = AnnotationOps.ToPinSpace(Editor.Items, new Point(rect.X, rect.Y), cropped);
            Editor.Items.Clear();
            Editor.Items.AddRange(moved);
            return cropped;
        });
        Fit();
    }

    public void PathCropTo(IReadOnlyList<Point> path)
    {
        GeometryStep(EditGroup.Cut, () => ImageOps.PathCrop(Flattened(), path, out _));
        Fit();
    }

    public void KeystoneTo(IReadOnlyList<Point> quad)
    {
        GeometryStep(EditGroup.Cut, () => ImageOps.Keystone(Flattened(), quad));
        Fit();
    }

    /// <summary>网格形变 with a warp (annotations go into the pixels); the canvas fits the warped picture.</summary>
    public void WarpTo(WarpMesh mesh)
    {
        GeometryStep(EditGroup.Cut, () => ImageOps.Warp(Flattened(), mesh, out _));
        Fit();
    }

    /// <summary>曲线: the curve window; the picture follows while points are dragged, 取消 puts it back.</summary>
    private void OpenCurves()
    {
        if (!_hasImage)
        {
            return;
        }

        var window = new CurvesWindow(_adjust.Curve, Proxy()) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.CurveChanged += curve => SetAdjust(_adjust with { Curve = ToneCurve.IsStraight(curve) ? null : curve });
        window.ShowDialog();
        RefreshPanel();
    }

    // ------------------------------------------------------------------ colour

    private ImageAdjust AdjustFromControls() => new(
        Math.Round(_sliders["Brightness"].Slider.Value),
        Math.Round(_sliders["Contrast"].Slider.Value),
        Math.Round(_sliders["Saturation"].Slider.Value),
        Math.Round(_sliders["Opacity"].Slider.Value),
        false,
        _invert.IsChecked == true,
        Math.Round(_sliders["Hue"].Slider.Value),
        _adjust.Curve);

    private void AdjustChanged()
    {
        if (_syncingControls || !_hasImage)
        {
            return;
        }

        SetAdjust(AdjustFromControls());
    }

    /// <summary>
    /// Colour settings, always worked out from the image as it was before them (moving a slider back
    /// really restores it). The screen follows at once on a smaller copy; the real pixels come
    /// when the controls rest.
    /// </summary>
    public void SetAdjust(ImageAdjust adjust)
    {
        if (adjust.SameAs(_adjust))
        {
            return;
        }

        if (!_adjustPending)
        {
            _history.Checkpoint(Editor.Items);
            UpdateUndoButtons();
        }

        _adjust = adjust;
        _adjustPending = true;
        Img.Source = ImageOps.Adjust(Proxy(), adjust);
        _adjustTimer.Stop();
        _adjustTimer.Start();
    }

    private BitmapSource Proxy()
    {
        if (_proxy != null)
        {
            return _proxy;
        }

        var side = Math.Max(_origin.PixelWidth, _origin.PixelHeight);
        _proxy = side <= PreviewSide
            ? _origin
            : ImageOps.Resize(_origin, Math.Max(1, _origin.PixelWidth * PreviewSide / side), Math.Max(1, _origin.PixelHeight * PreviewSide / side));
        return _proxy;
    }

    private void BakeAdjust()
    {
        _adjustTimer.Stop();
        if (!_adjustPending)
        {
            return;
        }

        _adjustPending = false;
        SetBitmap(_adjust.IsNeutral ? _origin : ImageOps.Adjust(_origin, _adjust));
    }

    private void ResetAdjust() => SetAdjustAndControls(new ImageAdjust());

    private void SetAdjustAndControls(ImageAdjust adjust)
    {
        SetAdjust(adjust);
        RefreshPanel();
    }

    // ------------------------------------------------------------------ crop / path / keystone sessions

    private void StartSession(EditorOverlayMode mode)
    {
        if (!_hasImage)
        {
            return;
        }

        CancelSession();
        BakeAdjust();
        _textEditor.Commit();
        ClearSelection();
        SetTool(AnnotationTool.None);
        Toolbar.SetTool(AnnotationTool.None);
        _overlay = new EditorOverlay(mode, _baseImage.PixelWidth, _baseImage.PixelHeight, _zoom);
        _overlay.Changed += UpdateSessionBar;
        _overlay.Changed += UpdateHint;
        if (mode == EditorOverlayMode.Mesh)
        {
            // The warp is previewed on a small copy of the picture with its annotations.
            var whole = ImageOps.Normalize(base.ComposeEditedImage());
            var side = Math.Max(whole.PixelWidth, whole.PixelHeight);
            var small = side <= 700 ? whole : ImageOps.Resize(whole, Math.Max(1, whole.PixelWidth * 700 / side), Math.Max(1, whole.PixelHeight * 700 / side));
            var scale = small.PixelWidth / (double)whole.PixelWidth;
            _overlay.WarpPreview = mesh =>
            {
                var image = ImageOps.Warp(small, mesh.Scaled(scale), out var origin, 32);
                return (image, new Rect(origin.X / scale, origin.Y / scale, image.PixelWidth / scale, image.PixelHeight / scale));
            };
        }

        ImageHost.Children.Add(_overlay);
        _sessionText.Text = mode switch
        {
            EditorOverlayMode.Crop => "拖出要保留的区域，拖边角调整。Enter 应用，Esc 取消",
            EditorOverlayMode.Path => "按住鼠标沿要保留的部分画一圈（按住 Shift 走直线）。Enter 应用，Esc 取消",
            EditorOverlayMode.Mesh => "先单击图片交叉拆分一次，然后拖动交点、边角和蓝色手柄弯曲图片。Enter 应用，Esc 取消",
            _ => "拖动四个角对准图中倾斜的矩形。Enter 应用，Esc 取消"
        };
        _sessionBar.Visibility = Visibility.Visible;
        UpdateSessionBar();
        UpdateHint();
    }

    private void UpdateSessionBar() => _sessionApply.IsEnabled = _overlay?.Ready == true;

    private void ApplySession()
    {
        if (_overlay is not { Ready: true } overlay)
        {
            return;
        }

        CancelSession();
        switch (overlay.Mode)
        {
            case EditorOverlayMode.Crop:
            {
                var r = overlay.Crop;
                var left = (int)Math.Round(r.Left);
                var top = (int)Math.Round(r.Top);
                CropTo(new Int32Rect(left, top,
                    Math.Clamp((int)Math.Round(r.Right) - left, 1, _baseImage.PixelWidth - left),
                    Math.Clamp((int)Math.Round(r.Bottom) - top, 1, _baseImage.PixelHeight - top)));
                break;
            }

            case EditorOverlayMode.Path:
                PathCropTo(overlay.PathPoints);
                break;
            case EditorOverlayMode.Mesh:
                WarpTo(overlay.Warp);
                break;
            default:
                KeystoneTo(overlay.Quad);
                break;
        }
    }

    private void CancelSession()
    {
        if (_overlay != null)
        {
            ImageHost.Children.Remove(_overlay);
            _overlay = null;
            _sessionBar.Visibility = Visibility.Collapsed;
            UpdateHint();
        }
    }

    // ------------------------------------------------------------------ keys, menu

    private protected override bool EditorKey(Key key, bool control)
    {
        if (_overlay != null)
        {
            switch (key)
            {
                case Key.Enter:
                    ApplySession();
                    return true;
                case Key.Escape:
                    CancelSession();
                    return true;
            }

            if (!control)
            {
                return true; // tool letters wait until the crop / path / keystone is finished
            }
        }

        if (!control)
        {
            if (key == Key.F && Keyboard.Modifiers == ModifierKeys.None)
            {
                Fit(); // F: back in the middle, fitted
                return true;
            }

            if (key != Key.Escape || _selected != null)
            {
                return false;
            }

            // Esc puts the tool down; it never closes the editor by accident.
            if (_tool is not (AnnotationTool.None or AnnotationTool.Select))
            {
                SetTool(AnnotationTool.None);
                Toolbar.SetTool(AnnotationTool.None);
            }

            return true;
        }

        switch (key)
        {
            case Key.O:
                OpenDialog();
                return true;
            case Key.S:
                Save();
                return true;
            case Key.T:
                PinToScreen();
                return true;
            case Key.D0 or Key.NumPad0:
                Fit();
                return true;
            case Key.D1 or Key.NumPad1:
                SetZoom(1);
                return true;
            case Key.OemPlus or Key.Add:
                ZoomStep(1.25);
                return true;
            case Key.OemMinus or Key.Subtract:
                ZoomStep(1 / 1.25);
                return true;
            default:
                return false;
        }
    }

    private protected override void BuildContextMenu()
    {
        var menu = new ContextMenu();
        void Add(string header, Action action, string gesture)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("打开图片…", OpenDialog, "Ctrl+O");
        Add("保存…", Save, "Ctrl+S");
        Add("复制", Copy, "Ctrl+C");
        Add("贴到屏幕", PinToScreen, "Ctrl+T");
        menu.Items.Add(new Separator());
        Add("适应窗口", Fit, "Ctrl+0");
        Add("实际大小", () => SetZoom(1), "Ctrl+1");
        ContextMenu = menu;
    }

    // ------------------------------------------------------------------ open, drop, save, copy, pin

    private void OpenDialog()
    {
        var dialog = new OpenFileDialog { Title = "打开图片", Filter = ImageFile.OpenFilter };
        if (_path != null && Path.GetDirectoryName(_path) is { } directory && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            OpenFile(dialog.FileName);
        }
    }

    /// <summary>Opens a file here (asks first when the current picture has unsaved edits).</summary>
    public bool OpenFile(string path)
    {
        if (!ConfirmDiscard())
        {
            return false;
        }

        try
        {
            LoadImage(ImageFile.Load(path), path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("打开图片失败：" + path, ex);
            ThemedMessage.Show(this, "提示", "无法打开这个图片：" + ex.Message, "确定");
            return false;
        }
    }

    private static string? DroppedImage(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(ImageFile.IsImage)
            : null;

    private void OnDrag(object sender, DragEventArgs e)
    {
        var ok = DroppedImage(e.Data) != null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        _dropHint.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _dropHint.Visibility = Visibility.Collapsed;
        if (DroppedImage(e.Data) is { } file)
        {
            OpenFile(file);
        }

        e.Handled = true;
    }

    private void MarkKept()
    {
        _unsavedNew = false;
        _savedChange = _history.ChangeCount;
    }

    private protected override void Copy()
    {
        if (_hasImage && ClipboardService.CopyImage(ComposeEditedImage()))
        {
            MarkKept();
        }
    }

    private protected override void Save()
    {
        if (!_hasImage)
        {
            return;
        }

        var name = _path != null && Path.GetExtension(_path).ToLowerInvariant() is not (".gif" or ".ico")
            ? Path.GetFileName(_path)
            : $"Markshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var path = SaveImageDialog.Ask(this, "保存图片", name, _path != null ? Path.GetDirectoryName(_path) : null);
        if (path == null)
        {
            return;
        }

        try
        {
            ImageFile.Save(ComposeEditedImage(), path);
            _path = path;
            MarkKept();
            UpdateTitle();
            UpdateStatus();
            ((App)Application.Current).ShowSavedPrompt(path);
        }
        catch (Exception ex)
        {
            Log.Error("保存图片失败", ex);
            ThemedMessage.Show(this, "保存失败", ex.Message, "确定");
        }
    }

    /// <summary>贴到屏幕: the picture as a pin, its annotations still editable there; the editor stays open.</summary>
    private void PinToScreen()
    {
        if (!_hasImage)
        {
            return;
        }

        _textEditor.Commit();
        var composed = ComposeEditedImage();
        var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
        var work = Monitors.At((int)center.X, (int)center.Y).WorkArea;
        var annotations = Editor.Items.Select(item => item.Clone()).ToList();
        _sourcePin = PinAction != null
            ? PinAction(_baseImage, annotations, composed)
            : ((App)Application.Current).PinFromEditor(_baseImage, annotations, composed, _sourcePin, work);
        _waitingPin = null;
        MarkKept();
    }

    // ------------------------------------------------------------------ closing

    /// <summary>True when the current picture may go (saved, or the user said so).</summary>
    private bool ConfirmDiscard()
    {
        if (!IsDirty)
        {
            return true;
        }

        var answer = ThemedMessage.Show(this, "图片还没有保存", "关闭前要保存这张图片吗？", "保存", "不保存", "取消");
        if (answer is 2 or -1)
        {
            return false;
        }

        if (answer == 0)
        {
            Save();
            return !IsDirty;
        }

        return true;
    }

    private void OnClosingEditor(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceClose && !ConfirmDiscard())
        {
            e.Cancel = true;
        }
    }

    /// <summary>Closes now (the app is shutting down, tests).</summary>
    public void CloseWithoutAsking()
    {
        _forceClose = true;
        Close();
    }

    /// <summary>Undo / redo for callers outside the annotation code (tests, menus).</summary>
    public void UndoStep() => Undo();

    public void RedoStep() => Redo();
}
