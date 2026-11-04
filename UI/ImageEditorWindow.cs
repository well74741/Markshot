using System.Windows.Controls.Primitives;
using Markshot.Annotations;
using Markshot.Core.Recording;
using Microsoft.Win32;

namespace Markshot.UI;

/// <summary>
/// 图片编辑器: a normal window around the pin's annotation canvas — all annotation tools work the
/// same as on a pin. The canvas is transparent and has its own size (画布尺寸, 矩形裁切); the opened
/// picture is an element on it (<see cref="ImageAnnotation"/>) next to pasted pictures and annotations,
/// in any layer order. Angle, 调节框, colours, path cut-out, keystone and warp work on the selected
/// picture (or the first one). Every operation is one undo step. Opens files, takes dropped files,
/// saves in the common formats and pins the result back onto the screen.
/// </summary>
public sealed class ImageEditorWindow : PinWindow
{
    private const int PreviewSide = 1400;

    /// <summary>What is undone together with the elements: the canvas (and, for the memory budget, the pictures' bitmaps).</summary>
    private sealed record State(BitmapSource Base, IReadOnlyList<BitmapSource> Pictures);

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
    private readonly Border _cropSize = new() { CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 1, 3, 1), Visibility = Visibility.Collapsed };
    private readonly TextBox _cropWidth = new();
    private readonly TextBox _cropHeight = new();
    private (State State, List<Annotation> Items)? _initial; // the picture as it was opened (重置所有修改)
    private readonly TextBlock _statusZoom = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Width = 48, TextAlignment = TextAlignment.Center };
    private readonly TextBox _widthBox = new();
    private readonly TextBox _heightBox = new();
    private readonly CheckBox _keepRatio = new() { Content = "锁定比例", IsChecked = true, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Slider _angle = new() { Minimum = -180, Maximum = 180, IsMoveToPointEnabled = true };
    private readonly TextBlock _angleValue = new() { FontSize = 12, Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, (Slider Slider, TextBlock Value)> _sliders = new();
    private readonly ToggleButton _invert;
    private readonly Dictionary<EditGroup, Button> _resetButtons = new();
    // The picture before the first change of each group, while only that group has changed it since.
    private readonly Dictionary<EditGroup, (State State, List<Annotation> Items)> _beforeGroup = new();
    private EditGroup? _lastGroup;
    private readonly DispatcherTimer _adjustTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private BitmapSource? _proxy;
    private BitmapSource? _proxyOf;
    private ImageAnnotation? _adjustTarget; // whose colours are being moved (finished when the controls rest)
    private ImageAnnotation? _sessionPicture; // what a 调节框 / path / keystone / warp works on
    private ImageAnnotation? _panelPicture; // whose numbers the panel shows
    private ImageAnnotation? _anglePicture; // turned live while the angle slider moves
    private double _angleBase;
    private long _noticeUntil;
    private readonly List<Annotation> _group = new(); // 多选 (two or more; one element is the plain selection)
    private bool _marqueeOn;
    private Point _marqueeStart;
    private int _marqueeMode; // 0 replace, 1 add (Shift), 2 remove (Ctrl)
    private bool _groupMoving;
    private bool _groupMoved;
    private Point _groupLast;
    private List<Annotation>? _sessionItems; // what a session works on when it is not one picture (merged for it)
    private ImageAnnotation? _sessionMerged;
    private Annotation? _pendingPaste; // pasted, not placed yet: Esc takes it back, a click elsewhere keeps it
    private int _pasteDepth;
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
        _path = path;
        _sourcePin = sourcePin;
        _waitingPin = sourcePin;
        _hasImage = hasImage;
        // Not saved anywhere yet (a capture, a long screenshot): closing asks first.
        _unsavedNew = hasImage && path == null && sourcePin == null;
        if (hasImage)
        {
            // The picture is an element at the bottom of the stack, on a transparent canvas of its size.
            Editor.Items.Insert(0, new ImageAnnotation { Image = image, Rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight) });
            _baseImage = ImageOps.Blank(image.PixelWidth, image.PixelHeight);
            Editor.Source = _baseImage;
            Img.Source = _baseImage;
        }

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
        _checker.Fill = CheckerBrush();
        Theme.Changed += OnThemeChanged;
        _stage = new Canvas { ClipToBounds = true, Background = Brushes.Transparent, Focusable = false };
        _stage.Children.Add(_checker);
        _stage.Children.Add(canvas);
        _stage.PreviewMouseWheel += Stage_PreviewMouseWheel;
        _stage.PreviewMouseDown += Stage_PreviewMouseDown;
        _stage.PreviewMouseMove += Stage_PreviewMouseMove;
        _stage.PreviewMouseUp += Stage_PreviewMouseUp;
        _stage.PreviewMouseLeftButtonDown += Stage_SelectDown;
        _stage.PreviewMouseMove += Stage_SelectMove;
        _stage.PreviewMouseLeftButtonUp += Stage_SelectUp;
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
        BuildCropSize();
        _toolbarLayer.Children.Add(_cropSize);
        // Double-click on the picture (no tool, not on an annotation): the 调节框 for size and angle.
        ImageHost.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2 || _overlay != null || !_hasImage || _textEditor.IsActive || _tool is not (AnnotationTool.None or AnnotationTool.Select))
            {
                return;
            }

            var at = e.GetPosition(ImageHost);
            // On a picture (or the empty canvas: the first picture), not on an annotation.
            var pixel = new Point(at.X / _zoom, at.Y / _zoom);
            var hit = AnnotationOps.HitTest(Editor.Items, pixel, 7 / _zoom);
            if (_group.Count > 1 && (hit == null || InGroup(pixel)))
            {
                StartSession(EditorOverlayMode.Transform); // the box around all of them
                e.Handled = true;
            }
            else if (hit is not (null or ImageAnnotation or TextAnnotation or WatermarkAnnotation))
            {
                ClearGroup();
                SelectQuietly(hit); // an annotation gets the box too
                StartSession(EditorOverlayMode.Transform);
                e.Handled = true;
            }
            else if (hit is null or ImageAnnotation)
            {
                ClearGroup();
                if (hit is ImageAnnotation picture)
                {
                    SelectQuietly(picture);
                }

                StartSession(EditorOverlayMode.Transform);
                e.Handled = true;
            }
        };
        DockPanel.SetDock(status, Dock.Bottom);
        DockPanel.SetDock(panel, Dock.Right);
        _layout.Children.Add(status);
        _layout.Children.Add(panel);
        _layout.Children.Add(middle);
        _layout.SetResourceReference(Panel.BackgroundProperty, Theme.Key(0xFF1A1A1A));
        Content = _layout;

        Toolbar.Visibility = Visibility.Visible;
        Toolbar.OpenRequested += OpenDialog;
        Toolbar.PasteRequested += PasteImage;
        Toolbar.PinRequested += PinToScreen;
        Toolbar.ToolChanged += _ => CancelSession();
        Toolbar.ToolChanged += _ => ClearGroup();
        Toolbar.ToolChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateHint), DispatcherPriority.Input);
        Toolbar.VariantChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateHint), DispatcherPriority.Input);
        PreviewMouseUp += (_, _) => Dispatcher.BeginInvoke(new Action(AfterInput), DispatcherPriority.Input);
        PreviewKeyUp += (_, _) => Dispatcher.BeginInvoke(new Action(AfterInput), DispatcherPriority.Input);
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
            return CurrentState();
        };
        _history.RestoreExtra = extra =>
        {
            // The elements are new copies now: nothing may hold on to the old ones.
            CancelSession();
            ClearGroup();
            _pendingPaste = null;
            _adjustPending = false;
            _adjustTarget = null;
            _anglePicture = null;
            if (extra is State state)
            {
                RestoreState(state);
            }
            else
            {
                RefreshPanel(); // a capture's step: same canvas
            }

            // Undo / redo: which group changed the picture last is not known any more.
            _beforeGroup.Clear();
            _lastGroup = null;
            UpdateResetButtons();
        };
        _history.ExtraSize = extra => extra is State s
            ? s.Pictures.Append(s.Base).Select(bitmap => ((object)bitmap, bitmap.PixelWidth * 4L * bitmap.PixelHeight)).ToArray()
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
        PreviewMouseDown += (_, e) =>
        {
            if (KeysBelongToUi && e.OriginalSource is DependencyObject source && !InsideTextBox(source))
            {
                Dispatcher.BeginInvoke(new Action(ReclaimKeyboard), DispatcherPriority.Input);
            }
        };
        Closed += (_, _) =>
        {
            Theme.Changed -= OnThemeChanged;
            _adjustTimer.Stop();
            _waitingPin?.ShowAgain(); // not re-pinned: the pin it came from comes back as it was
        };
        _initial = (CurrentState(), Editor.Items.Select(item => item.Clone()).ToList());
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

    /// <summary>The elements that are no pictures.</summary>
    public int AnnotationCount => Editor.Items.Count(item => item is not ImageAnnotation);

    /// <summary>What the panel's picture operations work on: the selected picture, else the first one.</summary>
    private ImageAnnotation? Target => _sessionPicture ?? _selected as ImageAnnotation ?? _group.OfType<ImageAnnotation>().FirstOrDefault() ??
                                       Editor.Items.OfType<ImageAnnotation>().FirstOrDefault();

    /// <summary>Where that picture is on the canvas (tests).</summary>
    public Rect PictureRect => Target?.Rect ?? Rect.Empty;

    public double PictureAngle => Target?.Rotation ?? 0;

    private ImageAdjust _adjust => Target?.Adjust ?? new ImageAdjust();

    private State CurrentState() => new(_baseImage, Editor.Items.OfType<ImageAnnotation>()
        .SelectMany(picture => new[] { picture.Image, picture.Origin }).OfType<BitmapSource>().Distinct().ToList());

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
        var applySize = TextButton("应用", "改变画布尺寸：内容居中不缩放，多出的地方透明（图片本身的大小用双击图片的调节框）", ApplyTypedSize);
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
        var applyAngle = TextButton("应用角度", "转动选中的图片（没选中时是第一张）；画布不变，批注不受影响", ApplyAngle);
        applyAngle.Margin = new Thickness(0, 6, 0, 0);
        applyAngle.HorizontalAlignment = HorizontalAlignment.Left;

        var cutRow = new WrapPanel();
        cutRow.Children.Add(TextButton("矩形裁切", "把画布裁成拖出的区域（图片和批注保持可编辑）", () => StartSession(EditorOverlayMode.Crop)));
        cutRow.Children.Add(TextButton("路径裁切", "沿手绘路径抠选中的图片，路径外变透明", () => StartSession(EditorOverlayMode.Path)));
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
        stack.Children.Add(Section("画布尺寸", Reset(EditGroup.Size, "回到改尺寸之前（之后做过其他图片操作时不可用，可用 Ctrl+Z 撤销）"), sizeRow, _keepRatio));
        stack.Children.Add(Section("旋转与镜像", Reset(EditGroup.Turn, "回到旋转 / 镜像之前（之后做过其他图片操作时不可用，可用 Ctrl+Z 撤销）"), turnRow, angleRow, applyAngle));
        stack.Children.Add(Section("裁切与变形", Reset(EditGroup.Cut, "回到裁切 / 变形之前（之后做过其他图片操作时不可用，可用 Ctrl+Z 撤销）"), cutRow));
        stack.Children.Add(Section("调色", TextButton("重置", "调色恢复默认", ResetAdjust), colour));
        var resetAll = TextButton("重置所有修改", "回到打开时的样子（图片和批注；可用 Ctrl+Z 撤销）", ResetAll);
        resetAll.HorizontalAlignment = HorizontalAlignment.Stretch;
        resetAll.Margin = new Thickness(0, 4, 0, 4);
        stack.Children.Add(resetAll);
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
        if (_anglePicture != null)
        {
            _anglePicture.Rotation = _angleBase; // a previewed angle that was not applied
            _anglePicture = null;
            Editor.InvalidateVisual();
        }

        _angle.Value = 0;
        _panelPicture = Target;
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
        _statusSize.Text = _hasImage ? $"画布 {_baseImage.PixelWidth} × {_baseImage.PixelHeight} 像素" + (_path != null ? "　·　" + _path : "") : "";
        _statusZoom.Text = $"{_zoom * 100:0}%";
    }

    // ------------------------------------------------------------------ zoom and pan

    /// <summary>Zooms keeping the picture point under <paramref name="anchor"/> (stage DIP; default: the middle of the view) in place.</summary>
    private void SetZoom(double zoom, Point? anchor = null)
    {
        var at = anchor ?? new Point(_stage.ActualWidth / 2, _stage.ActualHeight / 2);
        var before = _zoom;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Editor.PictureScaling = _zoom >= 3 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
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
        Dispatcher.BeginInvoke(new Action(PlaceCropSize), DispatcherPriority.Render);
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
        if (Environment.TickCount64 < _noticeUntil)
        {
            return; // a short notice is on show
        }

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
                EditorOverlayMode.Transform => "拖四角等比缩放（Shift 自由）· 框边外拖动旋转（Shift 15°）· 点击远处应用",
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
        else if (_pendingPaste != null && ReferenceEquals(_selected, _pendingPaste))
        {
            text = "粘贴中：拖动摆放 · 拖手柄缩放 / 旋转 · Enter 或点击空白处完成 · Esc 取消粘贴";
        }
        else if (_group.Count > 1)
        {
            text = $"已选 {_group.Count} 个元素 · 拖动一起移动 · 方向键微调 · Delete 删除 · Shift 加选 / Ctrl 减选 · 双击调节框 · 裁切与变形作用于选中元素";
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
                AnnotationTool.Pen or AnnotationTool.Highlighter => "拖动书写 · 按住 Alt 单击吸色 · Shift 直线 · Alt+滚轮 笔刷大小 · Ctrl+滚轮 透明度 · Tab 画笔 / 荧光笔",
                AnnotationTool.Mosaic => "拖出打码区域 · Alt+滚轮 程度 · Tab 画笔马赛克",
                AnnotationTool.MosaicBrush => "涂抹打码 · Shift 直线 · Alt+滚轮 笔刷大小 · Tab 区域马赛克",
                AnnotationTool.Text => "单击输入文字 · 单击已有文字编辑 · 拖动移动 · Alt+滚轮 字号",
                AnnotationTool.Watermark => "拖动移动水印 · 双击修改文字 · Alt+滚轮 字号",
                AnnotationTool.Number => "单击放序号 · Alt+滚轮 大小",
                AnnotationTool.Eraser => "拖动擦掉批注 · Alt+滚轮 橡皮大小 · Ctrl+滚轮 擦除强度 · Tab 抠除",
                AnnotationTool.Cutout => "拖动把图片和批注一起擦成透明 · Alt+滚轮 大小 · Tab 擦除",
                _ => "选择：单击选中 · 空白处拖动框选 · Shift 加选 · Ctrl 减选 · Ctrl+A 全选 · 双击调节框 · 右键调整层级 · Ctrl+V 粘贴图片"
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
        _initial = null; // set when the new picture is in
        _savedChange = _history.ChangeCount;
        _hasImage = true;
        _path = path;
        ClearGroup();
        _pendingPaste = null;
        _waitingPin?.ShowAgain(); // another picture now: the pin it came from is unrelated from here on
        _waitingPin = null;
        _sourcePin = null;
        _unsavedNew = path == null;
        _proxy = null;
        _proxyOf = null;
        _adjustTarget = null;
        _anglePicture = null;
        var normal = ImageOps.Normalize(image);
        Editor.Items.Add(new ImageAnnotation { Image = normal, Rect = new Rect(0, 0, normal.PixelWidth, normal.PixelHeight) });
        SetBitmap(ImageOps.Blank(normal.PixelWidth, normal.PixelHeight));
        Editor.InvalidateVisual();
        UpdateUndoButtons();
        UpdateTitle();
        RefreshPanel();
        _initial = (CurrentState(), Editor.Items.Select(item => item.Clone()).ToList());
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
        RebuildMosaics();
        ApplyBounds();
        MoveTo(_pan);
        Editor.InvalidateVisual();
    }

    private void RestoreState(State state)
    {
        _adjustTimer.Stop();
        _adjustPending = false;
        _adjustTarget = null;
        _anglePicture = null;
        SetBitmap(state.Base);
        RefreshPanel();
    }

    private BitmapSource? _mosaicSource;
    private string? _mosaicKey;

    private string PictureKey() => string.Join(";", Editor.Items.OfType<ImageAnnotation>().Select(p =>
        $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Image)},{p.Rect},{p.Rotation},{p.Opacity},{p.Hidden}")) + $"|{_baseImage.PixelWidth}x{_baseImage.PixelHeight}";

    private protected override BitmapSource EyedropSource => MosaicSource;

    /// <summary>What a mosaic blurs: the pictures as they lie on the canvas.</summary>
    private protected override BitmapSource MosaicSource
    {
        get
        {
            var key = PictureKey();
            if (_mosaicSource == null || _mosaicKey != key)
            {
                _mosaicKey = key;
                _mosaicSource = ImageOps.Render(_baseImage.PixelWidth, _baseImage.PixelHeight, dc =>
                {
                    foreach (var picture in Editor.Items.OfType<ImageAnnotation>())
                    {
                        AnnotationEditor.DrawItem(dc, picture);
                    }
                });
            }

            return _mosaicSource;
        }
    }

    private void RebuildMosaics()
    {
        foreach (var item in Editor.Items.Where(item => item is MosaicAnnotation or MosaicBrushAnnotation))
        {
            MosaicBuilder.Rebuild(item, MosaicSource);
        }
    }

    /// <summary>After a click or a key: the panel follows the selection, mosaics what moved under them.</summary>
    private void AfterInput()
    {
        if (_pendingPaste != null && !ReferenceEquals(_selected, _pendingPaste))
        {
            _pendingPaste = null; // placed: a click elsewhere keeps it
        }

        UpdateHint();
        if (_overlay != null)
        {
            return;
        }

        if (_anglePicture != null && !ReferenceEquals(Target, _anglePicture))
        {
            ApplyAngle(); // another picture chosen: the angle stays with the one it was set for
        }

        if (!ReferenceEquals(Target, _panelPicture))
        {
            RefreshPanel();
        }

        if (_mosaicKey != PictureKey() && Editor.Items.Any(item => item is MosaicAnnotation or MosaicBrushAnnotation))
        {
            RebuildMosaics();
            Editor.InvalidateVisual();
        }
    }

    /// <summary>What is selected now (a 多选, the one element, or nothing).</summary>
    public IReadOnlyList<Annotation> SelectedElements => _group.Count > 0 ? _group : _selected != null ? new[] { _selected } : Array.Empty<Annotation>();

    private static bool Selectable(Annotation item) => item is not (CutoutAnnotation or WatermarkAnnotation) && !item.Hidden;

    /// <summary>Selects these (one: the plain selection with its handles; several: a 多选).</summary>
    public void SelectElements(IEnumerable<Annotation> items)
    {
        var list = items.Where(Selectable).Distinct().ToList();
        ClearGroup();
        if (list.Count == 1)
        {
            SelectAnnotation(list[0]);
        }
        else
        {
            ClearSelection();
            if (list.Count > 1)
            {
                _group.AddRange(Editor.Items.Where(list.Contains)); // in layer order
                Editor.Group = _group;
            }
        }

        Editor.InvalidateVisual();
        UpdateHint();
    }

    private void ClearGroup()
    {
        if (_group.Count == 0)
        {
            return;
        }

        _group.Clear();
        Editor.Group = null;
        Editor.InvalidateVisual();
    }

    private bool InGroup(Point pixel) => _group.Any(member => AnnotationEditor.RotatedBounds(member).Contains(pixel));

    /// <summary>框选 done: what the rectangle touches replaces (0), joins (1, Shift) or leaves (2, Ctrl) the selection.</summary>
    public void MarqueeSelect(Rect area, int mode)
    {
        var touched = Editor.Items.Where(item => Selectable(item) && AnnotationEditor.RotatedBounds(item).IntersectsWith(area)).ToList();
        var current = SelectedElements.ToList();
        SelectElements(mode switch
        {
            1 => current.Concat(touched),
            2 => current.Where(item => !touched.Contains(item)),
            _ => touched
        });
    }

    private void Stage_SelectDown(object sender, MouseButtonEventArgs e)
    {
        if (!_hasImage || _overlay != null || e.ClickCount != 1 || _textEditor.IsActive || _polyline != null ||
            _tool is not (AnnotationTool.None or AnnotationTool.Select))
        {
            return;
        }

        var at = e.GetPosition(ImageHost);
        var point = new Point(at.X / _zoom, at.Y / _zoom);
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (!shift && !ctrl && _selected != null)
        {
            // The handles / rotation of the one selected element: the canvas handles them.
            var frame = AnnotationEditor.HitFrame(_selected, point, 1 / _zoom);
            if (frame.IsHandle || frame.Rotate)
            {
                return;
            }
        }

        var hit = HitAnnotation(point);
        if (shift || ctrl)
        {
            _textEditor.Commit();
            if (hit != null)
            {
                var list = SelectedElements.ToList();
                if (shift && !list.Contains(hit))
                {
                    list.Add(hit);
                }
                else if (ctrl)
                {
                    list.Remove(hit);
                }

                SelectElements(list);
            }
            else
            {
                BeginMarquee(point, shift ? 1 : 2);
            }

            e.Handled = true;
            return;
        }

        if (_group.Count > 1 && InGroup(point))
        {
            // Inside the 多选: they move together.
            _groupMoving = true;
            _groupMoved = false;
            _groupLast = point;
            _history.Arm(Editor.Items);
            _stage.CaptureMouse();
            e.Handled = true;
            return;
        }

        ClearGroup();
        if (hit != null)
        {
            return; // one element: selected and moved as before
        }

        _textEditor.Commit();
        ClearSelection();
        BeginMarquee(point, 0);
        e.Handled = true;
    }

    private void BeginMarquee(Point point, int mode)
    {
        _marqueeOn = true;
        _marqueeMode = mode;
        _marqueeStart = point;
        Editor.Marquee = new Rect(point, point);
        _stage.CaptureMouse();
        Editor.InvalidateVisual();
    }

    private void Stage_SelectMove(object sender, MouseEventArgs e)
    {
        if (!_marqueeOn && !_groupMoving)
        {
            return;
        }

        var at = e.GetPosition(ImageHost);
        var point = new Point(at.X / _zoom, at.Y / _zoom);
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndSelectDrag(point);
            return;
        }

        if (_marqueeOn)
        {
            Editor.Marquee = new Rect(_marqueeStart, point);
        }
        else
        {
            var delta = point - _groupLast;
            if (!_groupMoved && delta.Length * _zoom < 2)
            {
                return;
            }

            if (!_groupMoved)
            {
                _groupMoved = true;
                _history.CommitArmed();
                UpdateUndoButtons();
            }

            foreach (var member in _group)
            {
                member.Translate(delta.X, delta.Y);
            }

            _groupLast = point;
        }

        Editor.InvalidateVisual();
        e.Handled = true;
    }

    private void Stage_SelectUp(object sender, MouseButtonEventArgs e)
    {
        if (!_marqueeOn && !_groupMoving)
        {
            return;
        }

        var at = e.GetPosition(ImageHost);
        EndSelectDrag(new Point(at.X / _zoom, at.Y / _zoom));
        e.Handled = true;
    }

    private void EndSelectDrag(Point point)
    {
        if (_stage.IsMouseCaptured)
        {
            _stage.ReleaseMouseCapture();
        }

        if (_marqueeOn)
        {
            _marqueeOn = false;
            var area = new Rect(_marqueeStart, point);
            Editor.Marquee = null;
            if (area.Width * _zoom >= 3 || area.Height * _zoom >= 3)
            {
                MarqueeSelect(area, _marqueeMode);
            }

            Editor.InvalidateVisual();
        }

        _groupMoving = false;
    }

    private void DeleteGroup()
    {
        if (_group.Count == 0)
        {
            return;
        }

        _history.Checkpoint(Editor.Items);
        Editor.Items.RemoveAll(item => _group.Contains(item));
        ClearGroup();
        UpdateUndoButtons();
        RefreshPanel();
        RebuildMosaics();
        Editor.InvalidateVisual();
    }

    /// <summary>A 多选 to the top / bottom of the stack (their order among themselves stays).</summary>
    private void MoveGroupLayer(bool top)
    {
        var ordered = Editor.Items.Where(_group.Contains).ToList();
        _history.Checkpoint(Editor.Items);
        Editor.Items.RemoveAll(item => _group.Contains(item));
        if (top)
        {
            Editor.Items.AddRange(ordered);
        }
        else
        {
            Editor.Items.InsertRange(0, ordered);
        }

        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    /// <summary>Pasted (Ctrl+V / 粘贴图片): it stays selected to be placed; Esc takes it back, Enter or a click elsewhere keeps it.</summary>
    private protected override void OnPasted(Annotation element, int depth)
    {
        ClearGroup();
        _pendingPaste = element;
        _pasteDepth = depth;
        UpdateHint();
    }

    public bool PastePending => _pendingPaste != null;

    /// <summary>Enter (or a click on the empty space) while placing a paste: it stays where it is.</summary>
    public void FinishPaste()
    {
        if (_pendingPaste == null)
        {
            return;
        }

        _pendingPaste = null;
        ClearSelection();
        UpdateHint();
    }

    /// <summary>Esc while placing a paste: gone, no undo step left behind.</summary>
    public void CancelPaste()
    {
        if (_pendingPaste is not { } pasted)
        {
            return;
        }

        _pendingPaste = null;
        ClearSelection();
        Editor.Items.Remove(pasted);
        _history.TrimTo(_pasteDepth);
        UpdateUndoButtons();
        RebuildMosaics();
        RefreshPanel();
        Editor.InvalidateVisual();
        Notice("已取消粘贴");
    }

    /// <summary>A line in the status bar for a moment.</summary>
    private void Notice(string text)
    {
        _noticeUntil = Environment.TickCount64 + 2500;
        _statusHint.Text = text;
        _statusHint.ToolTip = text;
    }

    /// <summary>
    /// One undoable change of the canvas or a picture: pending colours and angle are finished first.
    /// A selected picture stays selected (the elements stay elements).
    /// </summary>
    private void GeometryStep(EditGroup group, Action change)
    {
        if (!_hasImage)
        {
            return;
        }

        if (_anglePicture != null)
        {
            ApplyAngle();
        }

        var keep = _selected as ImageAnnotation;
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
            _beforeGroup[group] = (CurrentState(), Editor.Items.Select(item => item.Clone()).ToList());
        }

        _lastGroup = group;
        _history.Checkpoint(Editor.Items);
        change();
        RebuildMosaics();
        Editor.InvalidateVisual();
        UpdateUndoButtons();
        if (keep != null && Editor.Items.Contains(keep))
        {
            SelectQuietly(keep);
        }

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
        ClearGroup();
        _adjustTimer.Stop();
        _adjustPending = false;
        _adjustTarget = null;
        Editor.Items.Clear();
        Editor.Items.AddRange(before.Items.Select(item => item.Clone()));
        SetBitmap(before.State.Base);
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

    private protected override BitmapSource ComposeEditedImage()
    {
        if (_sessionPicture is { Hidden: true })
        {
            CancelSession(); // saved or copied with a box open: the picture as it is
        }

        BakeAdjust();
        return base.ComposeEditedImage();
    }

    private void ApplyTypedSize()
    {
        if (int.TryParse(_widthBox.Text.Trim(), out var width) && int.TryParse(_heightBox.Text.Trim(), out var height) &&
            width is >= 1 and <= 30000 && height is >= 1 and <= 30000)
        {
            CanvasTo(width, height);
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

    /// <summary>The whole canvas turns (pictures and annotations follow, all stay elements).</summary>
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
            SetBitmap(ImageOps.Normalize(_baseImage));
        });
    }

    /// <summary>The slider turns the target picture live; 应用角度 makes it an undo step.</summary>
    private void PreviewAngle()
    {
        _angleValue.Text = $"{_angle.Value:0.#}°";
        if (_syncingControls || Target is not { } picture)
        {
            return;
        }

        if (_anglePicture == null)
        {
            _anglePicture = picture;
            _angleBase = picture.Rotation;
        }

        _anglePicture.Rotation = Math.IEEERemainder(_angleBase + _angle.Value, 360);
        Editor.InvalidateVisual();
    }

    private void ApplyAngle()
    {
        if (_anglePicture is { } picture)
        {
            var angle = Math.Round(_angle.Value, 1);
            picture.Rotation = _angleBase;
            _anglePicture = null;
            if (Math.Abs(angle) >= 0.05)
            {
                GeometryStep(EditGroup.Turn, () => picture.Rotation = Math.IEEERemainder(picture.Rotation + angle, 360));
            }
        }

        RefreshPanel();
    }

    /// <summary>任意角度: the picture element turns; the canvas keeps its size, what leaves it is only out of view.</summary>
    public void RotateBy(double degrees)
    {
        if (Target is { } picture)
        {
            GeometryStep(EditGroup.Turn, () => picture.Rotation = Math.IEEERemainder(picture.Rotation + degrees, 360));
        }
    }

    /// <summary>画布尺寸: the background grows / shrinks around its content (centred, nothing scaled or cut for good; all stay elements).</summary>
    public void CanvasTo(int width, int height)
    {
        if (width < 1 || height < 1 || (width == _baseImage.PixelWidth && height == _baseImage.PixelHeight))
        {
            return;
        }

        var offset = new Point(Math.Round((width - _baseImage.PixelWidth) / 2.0), Math.Round((height - _baseImage.PixelHeight) / 2.0));
        ChangeCanvas(EditGroup.Size, width, height, new Point(-offset.X, -offset.Y));
    }

    /// <summary>矩形裁切: the canvas becomes that part of itself; pictures and annotations keep their place and stay elements.</summary>
    public void CropTo(Int32Rect rect) => ChangeCanvas(EditGroup.Cut, rect.Width, rect.Height, new Point(rect.X, rect.Y));

    private void ChangeCanvas(EditGroup group, int width, int height, Point origin)
    {
        GeometryStep(group, () =>
        {
            // In place: the elements stay the same objects (a selected picture stays selected).
            foreach (var item in Editor.Items)
            {
                item.Translate(-origin.X, -origin.Y);
                if (item is WatermarkAnnotation watermark)
                {
                    watermark.Rect = new Rect(0, 0, width, height);
                }
            }

            SetBitmap(ImageOps.Blank(width, height)); // the mosaics are rebuilt on it
        });
        Fit();
    }

    /// <summary>A canvas point in the picture's own pixels.</summary>
    private static Point ToLocal(ImageAnnotation picture, Point point)
    {
        var r = picture.Rect;
        var turned = AnnotationEditor.RotatePoint(point, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), -picture.Rotation);
        return new Point((turned.X - r.X) * picture.Image!.PixelWidth / r.Width, (turned.Y - r.Y) * picture.Image.PixelHeight / r.Height);
    }

    private static Point FromLocal(ImageAnnotation picture, Point local)
    {
        var r = picture.Rect;
        var flat = new Point(r.X + local.X * r.Width / picture.Image!.PixelWidth, r.Y + local.Y * r.Height / picture.Image.PixelHeight);
        return AnnotationEditor.RotatePoint(flat, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), picture.Rotation);
    }

    /// <summary>The picture's pixels replaced by what lies at <paramref name="local"/> of the old ones (at the same scale and angle).</summary>
    private static void PlaceLocal(ImageAnnotation picture, Rect local, BitmapSource result)
    {
        var r = picture.Rect;
        var scaleX = r.Width / picture.Image!.PixelWidth;
        var scaleY = r.Height / picture.Image.PixelHeight;
        var inner = new Rect(r.X + local.X * scaleX, r.Y + local.Y * scaleY, local.Width * scaleX, local.Height * scaleY);
        var centre = AnnotationEditor.RotatePoint(new Point(inner.X + inner.Width / 2, inner.Y + inner.Height / 2), new Point(r.X + r.Width / 2, r.Y + r.Height / 2), picture.Rotation);
        picture.Rect = new Rect(centre.X - inner.Width / 2, centre.Y - inner.Height / 2, inner.Width, inner.Height);
        picture.Image = result;
        picture.Origin = null; // what comes out is the new starting point: colours back to neutral
        picture.Adjust = null;
    }

    /// <summary>路径裁切: the target picture, along a path on the canvas; the canvas stays.</summary>
    public void PathCropTo(IReadOnlyList<Point> path)
    {
        if (Target is not { Image: not null } picture)
        {
            return;
        }

        GeometryStep(EditGroup.Cut, () =>
        {
            Realize(picture);
            var result = ImageOps.PathCrop(picture.Image!, path.Select(point => ToLocal(picture, point)).ToList(), out var origin);
            PlaceLocal(picture, new Rect(origin.X, origin.Y, result.PixelWidth, result.PixelHeight), result);
        });
    }

    /// <summary>梯形校正: the target picture; the straightened result stays where the corners were.</summary>
    public void KeystoneTo(IReadOnlyList<Point> quad)
    {
        if (Target is not { Image: not null } picture)
        {
            return;
        }

        GeometryStep(EditGroup.Cut, () =>
        {
            Realize(picture);
            var corners = quad.Select(point => ToLocal(picture, point)).ToList();
            var result = ImageOps.Keystone(picture.Image!, corners);
            var x = (corners.Min(p => p.X) + corners.Max(p => p.X)) / 2;
            var y = (corners.Min(p => p.Y) + corners.Max(p => p.Y)) / 2;
            PlaceLocal(picture, new Rect(x - result.PixelWidth / 2.0, y - result.PixelHeight / 2.0, result.PixelWidth, result.PixelHeight), result);
        });
    }

    /// <summary>Several elements (an annotation) drawn into one picture element, at the canvas' scale; not inserted yet.</summary>
    private ImageAnnotation Merge(IReadOnlyList<Annotation> items)
    {
        var bounds = Rect.Empty;
        foreach (var item in items)
        {
            var b = AnnotationEditor.RotatedBounds(item);
            if (item is not ImageAnnotation)
            {
                b.Inflate(item.Thickness * 2 + 4, item.Thickness * 2 + 4); // strokes, outlines
            }

            bounds.Union(b);
        }

        var x = Math.Floor(bounds.X);
        var y = Math.Floor(bounds.Y);
        var width = Math.Clamp((int)Math.Ceiling(bounds.Right - x), 1, 30000);
        var height = Math.Clamp((int)Math.Ceiling(bounds.Bottom - y), 1, 30000);
        var ordered = Editor.Items.Where(items.Contains).ToList(); // in their layer order
        var image = ImageOps.Render(width, height, dc =>
        {
            dc.PushTransform(new TranslateTransform(-x, -y));
            foreach (var item in ordered)
            {
                AnnotationEditor.DrawItem(dc, item);
            }

            dc.Pop();
        });
        return new ImageAnnotation { Image = image, Rect = new Rect(x, y, width, height) };
    }

    /// <summary>A merged picture of the session takes the place of what it was made of (at the topmost one's layer).</summary>
    private void Realize(ImageAnnotation picture)
    {
        if (!ReferenceEquals(picture, _sessionMerged) || _sessionItems == null || Editor.Items.Contains(picture))
        {
            return;
        }

        var at = _sessionItems.Max(item => Editor.Items.IndexOf(item));
        Editor.Items.Insert(at + 1, picture);
        Editor.Items.RemoveAll(item => _sessionItems.Contains(item));
    }

    /// <summary>矩形裁切 of a selection: the picture keeps what lies in <paramref name="area"/> (canvas pixels), cut straight.</summary>
    public void CropPicture(ImageAnnotation picture, Rect area)
    {
        if (picture.Image == null || Rect.Intersect(AnnotationEditor.RotatedBounds(picture), area) is var inside && inside.IsEmpty || inside.Width < 1 || inside.Height < 1)
        {
            Notice("裁切区域没有碰到选中的元素");
            return;
        }

        GeometryStep(EditGroup.Cut, () =>
        {
            Realize(picture);
            if (Math.Abs(picture.Rotation) < 0.01)
            {
                // Straight picture: cut in its own pixels (no resampling).
                var r = picture.Rect;
                var sx = picture.Image!.PixelWidth / r.Width;
                var sy = picture.Image.PixelHeight / r.Height;
                var left = Math.Clamp((int)Math.Round((inside.X - r.X) * sx), 0, picture.Image.PixelWidth - 1);
                var top = Math.Clamp((int)Math.Round((inside.Y - r.Y) * sy), 0, picture.Image.PixelHeight - 1);
                var right = Math.Clamp((int)Math.Round((inside.Right - r.X) * sx), left + 1, picture.Image.PixelWidth);
                var bottom = Math.Clamp((int)Math.Round((inside.Bottom - r.Y) * sy), top + 1, picture.Image.PixelHeight);
                var cut = new Int32Rect(left, top, right - left, bottom - top);
                PlaceLocal(picture, new Rect(cut.X, cut.Y, cut.Width, cut.Height), ImageOps.Crop(picture.Image, cut));
                return;
            }

            // Turned: what it shows, cut straight (the angle goes into the pixels).
            var (shown, place) = RenderedPicture(picture);
            var x = Math.Clamp((int)Math.Round(inside.X - place.X), 0, shown.PixelWidth - 1);
            var y = Math.Clamp((int)Math.Round(inside.Y - place.Y), 0, shown.PixelHeight - 1);
            var w = Math.Clamp((int)Math.Round(inside.Width), 1, shown.PixelWidth - x);
            var h = Math.Clamp((int)Math.Round(inside.Height), 1, shown.PixelHeight - y);
            picture.Image = ImageOps.Crop(shown, new Int32Rect(x, y, w, h));
            picture.Origin = null;
            picture.Adjust = null;
            picture.Rotation = 0;
            picture.Rect = new Rect(place.X + x, place.Y + y, w, h);
        });
    }

    /// <summary>调节框 of a selection: each element moves, scales and turns with the box (they stay editable).</summary>
    public void TransformItems(IReadOnlyList<Annotation> items, Rect from, Rect to, double angle)
    {
        if (from.Width < 0.5 || from.Height < 0.5)
        {
            return;
        }

        var sx = to.Width / from.Width;
        var sy = to.Height / from.Height;
        var pivot = new Point(to.X + to.Width / 2, to.Y + to.Height / 2);
        GeometryStep(Math.Abs(angle) > 0.05 ? EditGroup.Turn : EditGroup.Cut, () =>
        {
            foreach (var item in items)
            {
                var b = item.Bounds;
                var centre = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
                var placed = AnnotationEditor.RotatePoint(new Point(to.X + (centre.X - from.X) * sx, to.Y + (centre.Y - from.Y) * sy), pivot, angle);
                if (Math.Abs(sx - 1) > 0.001 || Math.Abs(sy - 1) > 0.001)
                {
                    AnnotationOps.Resize(item, new Rect(placed.X - b.Width * sx / 2, placed.Y - b.Height * sy / 2, Math.Max(1, b.Width * sx), Math.Max(1, b.Height * sy)), 4);
                }

                var now = item.Bounds;
                item.Translate(placed.X - (now.X + now.Width / 2), placed.Y - (now.Y + now.Height / 2));
                if (Math.Abs(angle) > 0.05)
                {
                    item.Rotation = Math.IEEERemainder(item.Rotation + angle, 360);
                }
            }
        });
    }

    /// <summary>The picture as it lies on the canvas (turned and scaled), for the warp, and where that is.</summary>
    private static (BitmapSource Image, Rect Place) RenderedPicture(ImageAnnotation picture)
    {
        var r = picture.Rect;
        if (Math.Abs(picture.Rotation) < 0.01 && Math.Abs(r.Width - picture.Image!.PixelWidth) < 0.5 && Math.Abs(r.Height - picture.Image.PixelHeight) < 0.5)
        {
            return (picture.Image!, r);
        }

        var bounds = AnnotationEditor.RotatedBounds(picture);
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        var image = ImageOps.Render(width, height, dc =>
        {
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            dc.PushTransform(new RotateTransform(picture.Rotation, r.X + r.Width / 2, r.Y + r.Height / 2));
            dc.DrawImage(picture.Image, r);
            dc.Pop();
            dc.Pop();
        });
        return (image, new Rect(bounds.X, bounds.Y, width, height));
    }

    /// <summary>网格形变 of the target picture (a warp in canvas coordinates); it may reach past the canvas.</summary>
    public void WarpTo(WarpMesh mesh)
    {
        if (Target is not { Image: not null } picture)
        {
            return;
        }

        GeometryStep(EditGroup.Cut, () =>
        {
            Realize(picture);
            var (source, place) = RenderedPicture(picture);
            var image = ImageOps.Warp(source, mesh.Translated(-place.X, -place.Y), out var origin);
            picture.Image = image;
            picture.Origin = null;
            picture.Adjust = null;
            picture.Rotation = 0;
            picture.Rect = new Rect(place.X + origin.X, place.Y + origin.Y, image.PixelWidth, image.PixelHeight);
        });
    }

    /// <summary>曲线: the curve window; the picture follows while points are dragged, 取消 puts it back.</summary>
    private void OpenCurves()
    {
        if (!_hasImage)
        {
            return;
        }

        if (Target is not { } picture)
        {
            return;
        }

        var window = new CurvesWindow(_adjust.Curve, Proxy((picture.Origin ?? picture.Image)!)) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
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
        if (Target is not { Image: not null } picture || adjust.SameAs(picture.Adjust ?? new ImageAdjust()))
        {
            return;
        }

        if (_adjustPending && !ReferenceEquals(_adjustTarget, picture))
        {
            BakeAdjust();
        }

        if (!_adjustPending)
        {
            _history.Checkpoint(Editor.Items);
            UpdateUndoButtons();
        }

        picture.Origin ??= picture.Image;
        picture.Adjust = adjust;
        _adjustTarget = picture;
        _adjustPending = true;
        picture.Image = ImageOps.Adjust(Proxy(picture.Origin), adjust);
        Editor.InvalidateVisual();
        _adjustTimer.Stop();
        _adjustTimer.Start();
    }

    private BitmapSource Proxy(BitmapSource origin)
    {
        if (_proxy != null && ReferenceEquals(_proxyOf, origin))
        {
            return _proxy;
        }

        var side = Math.Max(origin.PixelWidth, origin.PixelHeight);
        _proxyOf = origin;
        _proxy = side <= PreviewSide
            ? origin
            : ImageOps.Resize(origin, Math.Max(1, origin.PixelWidth * PreviewSide / side), Math.Max(1, origin.PixelHeight * PreviewSide / side));
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
        if (_adjustTarget is { Origin: not null, Adjust: not null } picture)
        {
            picture.Image = picture.Adjust.IsNeutral ? picture.Origin : ImageOps.Adjust(picture.Origin, picture.Adjust);
        }

        _adjustTarget = null;
        RebuildMosaics();
        Editor.InvalidateVisual();
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
        // A previewed angle is applied first: the tool works on the turned picture (it used to be dropped).
        if (_anglePicture != null)
        {
            ApplyAngle();
        }

        // On the selection: one picture as it is; several elements / an annotation merged into a picture
        // for it (the 调节框 moves and scales them each, they stay what they are). Nothing selected: the
        // canvas (矩形裁切) or the first picture.
        _sessionItems = null;
        _sessionMerged = null;
        var subject = _group.Count > 1 ? _group.ToList() : _selected is { } one and not WatermarkAnnotation ? new List<Annotation> { one } : null;
        ImageAnnotation? picture;
        if (subject is [ImageAnnotation single])
        {
            picture = single;
        }
        else if (subject != null)
        {
            _sessionItems = subject;
            picture = _sessionMerged = Merge(subject);
        }
        else
        {
            picture = mode == EditorOverlayMode.Crop ? null : Target;
        }

        if (mode != EditorOverlayMode.Crop && picture?.Image == null)
        {
            Notice("画布上没有图片");
            return;
        }

        BakeAdjust();
        _textEditor.Commit();
        ClearSelection();
        var keepGroup = _sessionItems;
        SetTool(AnnotationTool.None);
        Toolbar.SetTool(AnnotationTool.None);
        _group.Clear();
        Editor.Group = null;
        _sessionItems = keepGroup;
        _sessionPicture = picture;
        _overlay = new EditorOverlay(mode, _baseImage.PixelWidth, _baseImage.PixelHeight, _zoom);
        _overlay.Changed += UpdateSessionBar;
        _overlay.Changed += UpdateHint;
        _overlay.Changed += PlaceCropSize;
        _overlay.CommitRequested += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_overlay?.Ready == true)
            {
                ApplySession();
            }
            else
            {
                CancelSession();
            }
        }));
        _overlay.ShowSizeLabel = false;
        if (mode == EditorOverlayMode.Transform)
        {
            var whole = picture!.Image!;
            var side = Math.Max(whole.PixelWidth, whole.PixelHeight);
            _overlay.TransformPreview = side <= 1400 ? whole
                : ImageOps.Resize(whole, Math.Max(1, whole.PixelWidth * 1400 / side), Math.Max(1, whole.PixelHeight * 1400 / side));
            _overlay.TransformRect = _overlay.TransformStart = picture.Rect;
            _overlay.TransformAngle = _overlay.TransformStartAngle = picture.Rotation;
            // The box draws the picture now; the element is hidden so the uncovered parts show the
            // checkerboard, as they will be transparent. The canvas itself never changes here.
            picture.Hidden = true;
            Editor.InvalidateVisual();
        }

        if (mode == EditorOverlayMode.Crop && picture != null)
        {
            // The crop starts on the selection (it cuts the selected elements, not the canvas).
            var around = Rect.Intersect(AnnotationEditor.RotatedBounds(picture), new Rect(0, 0, _baseImage.PixelWidth, _baseImage.PixelHeight));
            if (!around.IsEmpty)
            {
                _overlay.Crop = around;
            }
        }

        if (_sessionItems != null && mode is EditorOverlayMode.Transform or EditorOverlayMode.Mesh)
        {
            foreach (var item in _sessionItems)
            {
                item.Hidden = true; // the overlay shows them meanwhile
            }

            Editor.InvalidateVisual();
        }

        if (mode == EditorOverlayMode.Keystone)
        {
            // The corners start a little inside the picture, wherever it lies on the canvas.
            var w = (double)picture!.Image!.PixelWidth;
            var h = (double)picture.Image.PixelHeight;
            var inset = Math.Min(w, h) * 0.08;
            _overlay.Quad[0] = FromLocal(picture, new Point(inset, inset));
            _overlay.Quad[1] = FromLocal(picture, new Point(w - inset, inset));
            _overlay.Quad[2] = FromLocal(picture, new Point(w - inset, h - inset));
            _overlay.Quad[3] = FromLocal(picture, new Point(inset, h - inset));
        }

        if (mode == EditorOverlayMode.Mesh)
        {
            // The warp is edited where the picture lies, previewed on a small copy of it.
            var (whole, place) = RenderedPicture(picture!);
            _overlay.Warp = new WarpMesh(place.Width, place.Height).Translated(place.X, place.Y);
            var side = Math.Max(whole.PixelWidth, whole.PixelHeight);
            var small = side <= 700 ? whole : ImageOps.Resize(whole, Math.Max(1, whole.PixelWidth * 700 / side), Math.Max(1, whole.PixelHeight * 700 / side));
            var scale = small.PixelWidth / (double)whole.PixelWidth;
            _overlay.WarpPreview = mesh =>
            {
                var image = ImageOps.Warp(small, mesh.Translated(-place.X, -place.Y).Scaled(scale), out var origin, 32);
                return (image, new Rect(place.X + origin.X / scale, place.Y + origin.Y / scale, image.PixelWidth / scale, image.PixelHeight / scale));
            };
            picture!.Hidden = true;
            Editor.InvalidateVisual();
        }

        ImageHost.Children.Add(_overlay);
        _sessionText.Text = mode switch
        {
            EditorOverlayMode.Crop => picture != null ? "拖出选中元素要保留的区域，拖边角调整。Enter 应用，Esc 取消" : "拖出画布要保留的区域，拖边角调整。Enter 应用，Esc 取消",
            EditorOverlayMode.Path => "按住鼠标沿要保留的部分画一圈（按住 Shift 走直线）。Enter 应用，Esc 取消",
            EditorOverlayMode.Mesh => "先单击图片交叉拆分一次，然后拖动交点、边角和蓝色手柄弯曲图片。Enter 应用，Esc 取消",
            EditorOverlayMode.Transform => "拖四角等比缩放（Shift 自由），框边外拖动旋转（Shift 15°）。Enter 或点击远处应用，Esc 取消",
            _ => "拖动四个角对准图中倾斜的矩形。Enter 应用，Esc 取消"
        };
        _sessionBar.Visibility = Visibility.Visible;
        UpdateSessionBar();
        UpdateHint();
        PlaceCropSize(force: true);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Activate();
            Keyboard.Focus(this);
        }), DispatcherPriority.Input);
    }

    private void UpdateSessionBar() => _sessionApply.IsEnabled = _overlay?.Ready == true;

    private void ApplySession()
    {
        if (_overlay is not { Ready: true } overlay)
        {
            return;
        }

        var picture = _sessionPicture;
        var items = _sessionItems;
        var merged = _sessionMerged;
        CancelSession();
        _sessionPicture = picture; // the panel's Target while it is applied
        _sessionItems = items;
        _sessionMerged = merged;
        if (items != null && overlay.Mode == EditorOverlayMode.Transform)
        {
            TransformItems(items, overlay.TransformStart, overlay.TransformRect, overlay.TransformAngle);
            _sessionPicture = null;
            _sessionItems = null;
            _sessionMerged = null;
            SelectElements(items);
            return;
        }

        switch (overlay.Mode)
        {
            case EditorOverlayMode.Crop when picture != null:
                CropPicture(picture, overlay.Crop);
                break;
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
            case EditorOverlayMode.Transform:
                TransformTo(overlay.TransformRect, overlay.TransformAngle);
                break;
            default:
                KeystoneTo(overlay.Quad);
                break;
        }

        _sessionPicture = null;
        _sessionItems = null;
        _sessionMerged = null;
        if (merged != null && Editor.Items.Contains(merged))
        {
            SelectQuietly(merged); // the merged result stays selected
            RefreshPanel();
            return;
        }

        KeepWorkingOn(picture);
    }

    /// <summary>After a session on a picture other than the first: it stays the one the panel works on.</summary>
    private void KeepWorkingOn(ImageAnnotation? picture)
    {
        if (picture != null && Editor.Items.Contains(picture) && !ReferenceEquals(picture, Editor.Items.OfType<ImageAnnotation>().First()))
        {
            SelectQuietly(picture);
        }

        RefreshPanel();
    }

    private void CancelSession()
    {
        if (_overlay != null)
        {
            ImageHost.Children.Remove(_overlay);
            _overlay = null;
            _sessionBar.Visibility = Visibility.Collapsed;
            _cropSize.Visibility = Visibility.Collapsed;
            var picture = _sessionPicture;
            var items = _sessionItems;
            _sessionPicture = null;
            _sessionItems = null;
            _sessionMerged = null;
            if (items != null)
            {
                foreach (var item in items)
                {
                    item.Hidden = false;
                }

                Editor.InvalidateVisual();
                SelectElements(items); // the selection comes back
            }
            else if (picture != null)
            {
                picture.Hidden = false;
                Editor.InvalidateVisual();
                KeepWorkingOn(picture);
            }

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

        if (!control && key == Key.Escape && _pendingPaste != null && ReferenceEquals(_selected, _pendingPaste))
        {
            CancelPaste();
            return true;
        }

        if (!control && key == Key.Enter && _pendingPaste != null && ReferenceEquals(_selected, _pendingPaste))
        {
            FinishPaste();
            return true;
        }

        if (_group.Count > 1)
        {
            switch (key)
            {
                case Key.Escape when !control:
                    ClearGroup();
                    UpdateHint();
                    return true;
                case Key.Delete or Key.Back when !control:
                    DeleteGroup();
                    return true;
                case Key.Left or Key.Right or Key.Up or Key.Down when !control:
                {
                    var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                    _history.CheckpointCoalesced(Editor.Items, _group);
                    foreach (var member in _group)
                    {
                        member.Translate(key == Key.Left ? -step : key == Key.Right ? step : 0, key == Key.Up ? -step : key == Key.Down ? step : 0);
                    }

                    UpdateUndoButtons();
                    Editor.InvalidateVisual();
                    return true;
                }
            }
        }

        if (control && key == Key.A && _hasImage)
        {
            SelectElements(Editor.Items); // 全选
            return true;
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

        // On an element (or with one selected, no tool in hand): its own menu first (layers, copy, delete).
        var pointer = Mouse.GetPosition(ImageHost);
        if (_hasImage && _overlay == null && _group.Count > 1)
        {
            Add($"置于顶层（{_group.Count} 个）", () => MoveGroupLayer(true), "");
            Add($"置于底层（{_group.Count} 个）", () => MoveGroupLayer(false), "");
            Add("删除选中的元素", DeleteGroup, "Delete");
            menu.Items.Add(new Separator());
        }

        if (_hasImage && _overlay == null && _group.Count <= 1 && _tool is AnnotationTool.None or AnnotationTool.Select &&
            (HitAnnotation(new Point(pointer.X / _zoom, pointer.Y / _zoom)) ?? _selected) is { } element)
        {
            SelectAnnotation(element);
            var index = Editor.Items.IndexOf(element);
            var top = Editor.Items.Count - 1;
            if (AnnotationClipboard.CanCopy(element))
            {
                Add("复制元素", () => AnnotationClipboard.Copy(element), "Ctrl+C");
            }

            Add("删除元素", () =>
            {
                _history.Checkpoint(Editor.Items);
                Editor.Items.Remove(element);
                ClearSelection();
                UpdateUndoButtons();
                RefreshPanel();
                Editor.InvalidateVisual();
            }, "Delete");
            menu.Items.Add(new Separator());
            void Layer(string header, int to, bool enabled)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled };
                item.Click += (_, _) => MoveLayer(element, to);
                menu.Items.Add(item);
            }

            Layer("上移一层", index + 1, index < top);
            Layer("下移一层", index - 1, index > 0);
            Layer("置于顶层", top, index < top);
            Layer("置于底层", 0, index > 0);
            menu.Items.Add(new Separator());
        }

        if (_hasImage && (ClipboardService.HasImage || AnnotationClipboard.HasContent))
        {
            Add("粘贴", PasteAnnotation, "Ctrl+V");
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

    /// <summary>The bar's 粘贴图片 button: always the picture in the clipboard.</summary>
    public void PasteImage()
    {
        if (!PastePicture(force: true))
        {
            Notice("剪贴板里没有图片");
        }
    }

    /// <summary>Into an empty editor a pasted picture comes in like an opened one.</summary>
    private protected override bool AcceptPastedPicture(BitmapSource picture)
    {
        if (_hasImage)
        {
            return false;
        }

        LoadImage(picture, null);
        return true;
    }

    /// <summary>A capture's undo steps know nothing of the picture element: each gets it (at the bottom).</summary>
    public override void AdoptHistory(AnnotationHistory history)
    {
        if (Editor.Items.OfType<ImageAnnotation>().FirstOrDefault() is { } picture)
        {
            history.ForEachStep(step =>
            {
                if (!step.OfType<ImageAnnotation>().Any())
                {
                    step.Insert(0, picture.Clone());
                }
            });
        }

        base.AdoptHistory(history);
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
        // A pin has one picture under its annotations: everything up to the topmost picture goes into
        // it, what lies above stays editable there (the holes of 抠除 always do).
        var lastPicture = Editor.Items.FindLastIndex(item => item is ImageAnnotation);
        var flat = ImageOps.Render(_baseImage.PixelWidth, _baseImage.PixelHeight, dc =>
        {
            foreach (var item in Editor.Items.Take(lastPicture + 1))
            {
                AnnotationEditor.DrawItem(dc, item);
            }
        });
        var annotations = Editor.Items.Where((item, i) => i > lastPicture || item is CutoutAnnotation).Select(item => item.Clone()).ToList();
        _sourcePin = PinAction != null
            ? PinAction(flat, annotations, composed)
            : ((App)Application.Current).PinFromEditor(flat, annotations, composed, _sourcePin, work);
        _waitingPin = null;
        MarkKept();
        CloseWithoutAsking(); // pinned: the editor's job is done
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
        if (_closedWindow)
        {
            return;
        }

        _forceClose = true;
        Close();
    }

    private bool _closedWindow;

    /// <summary>Settings saved: the floating bar's order / visibility again.</summary>
    public void ApplyToolbarOrder() => Toolbar.ApplyToolOrderFromConfig();

    private static bool InsideTextBox(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is TextBox)
            {
                return true;
            }

            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closedWindow = true;
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------ 调节框, crop size, reset all, checkerboard

    /// <summary>调节框 applied: the picture element gets that place, size and angle (one undo step; the canvas stays as it is).</summary>
    public void TransformTo(Rect rect, double angle)
    {
        if (Target is not { } picture)
        {
            return;
        }

        var turned = Math.Abs(angle - picture.Rotation) > 0.05;
        GeometryStep(turned ? EditGroup.Turn : EditGroup.Cut, () =>
        {
            picture.Rect = new Rect(rect.X, rect.Y, Math.Clamp(rect.Width, 1, 30000), Math.Clamp(rect.Height, 1, 30000));
            picture.Rotation = angle;
        });
    }

    /// <summary>The target picture's size on the canvas (its top-left stays; the canvas does not change).</summary>
    public void ResizeTo(int width, int height)
    {
        if (Target is { } picture && width >= 1 && height >= 1)
        {
            TransformTo(new Rect(picture.Rect.X, picture.Rect.Y, width, height), picture.Rotation);
        }
    }

    /// <summary>重置所有修改: the picture and annotations as they were when opened (one undo step).</summary>
    public void ResetAll()
    {
        if (_initial is not { } initial || !_hasImage)
        {
            return;
        }

        CancelSession();
        _textEditor.Commit();
        ClearSelection();
        _history.Checkpoint(Editor.Items);
        _beforeGroup.Clear();
        _lastGroup = null;
        ClearGroup();
        Editor.Items.Clear();
        Editor.Items.AddRange(initial.Items.Select(item => item.Clone()));
        RestoreState(initial.State);
        Editor.InvalidateVisual();
        UpdateUndoButtons();
        RefreshPanel();
        Fit();
    }

    /// <summary>The checkerboard behind see-through parts, in the theme's colours.</summary>
    private static Brush CheckerBrush()
    {
        var (light, dark) = Theme.IsLight
            ? (Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xE3, 0xE3, 0xE3))
            : (Color.FromRgb(0x2F, 0x2F, 0x2F), Color.FromRgb(0x3C, 0x3C, 0x3C));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(light), null, new RectangleGeometry(new Rect(0, 0, 12, 12))));
        var squares = new GeometryGroup();
        squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 6, 6)));
        squares.Children.Add(new RectangleGeometry(new Rect(6, 6, 6, 6)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(dark), null, squares));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 12, 12), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    private void OnThemeChanged() => _checker.Fill = CheckerBrush();

    /// <summary>The crop's size label, typed into like the capture's: Enter (or clicking elsewhere) applies.</summary>
    private void BuildCropSize()
    {
        _cropSize.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xCD141414));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var box in new[] { _cropWidth, _cropHeight })
        {
            box.Style = (Style)Application.Current.FindResource("SizeLabelField");
            box.ToolTip = ReferenceEquals(box, _cropWidth) ? "宽（像素），可直接修改，Enter 应用" : "高（像素），可直接修改，Enter 应用";
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    ApplyCropSize();
                    Keyboard.Focus(this); // the next Enter applies the crop / 调节框
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    PlaceCropSize(force: true);
                    ReclaimKeyboard();
                    e.Handled = true;
                }
            };
            box.LostKeyboardFocus += (_, _) => ApplyCropSize();
        }

        row.Children.Add(_cropWidth);
        row.Children.Add(new TextBlock { Text = "×", Foreground = Brushes.LightGray, FontSize = 12, Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_cropHeight);
        _cropSize.Child = row;
        UiScale.Attach(_cropSize);
    }

    private void PlaceCropSize() => PlaceCropSize(force: false);

    private void PlaceCropSize(bool force)
    {
        if (_overlay is not { Mode: EditorOverlayMode.Crop or EditorOverlayMode.Transform } overlay)
        {
            _cropSize.Visibility = Visibility.Collapsed;
            return;
        }

        // Crop: its rectangle; 调节框: the size the picture gets, placed above the (turned) box.
        var transform = overlay.Mode == EditorOverlayMode.Transform;
        var crop = transform ? overlay.TransformRect : overlay.Crop;
        var place = transform ? overlay.TransformBounds : crop;
        if (force || !_cropWidth.IsKeyboardFocusWithin)
        {
            _cropWidth.Text = ((int)Math.Round(crop.Width)).ToString(CultureInfo.InvariantCulture);
        }

        if (force || !_cropHeight.IsKeyboardFocusWithin)
        {
            _cropHeight.Text = ((int)Math.Round(crop.Height)).ToString(CultureInfo.InvariantCulture);
        }

        _cropSize.Visibility = Visibility.Visible;
        _cropSize.UpdateLayout();
        var corner = ImageHost.TranslatePoint(new Point(place.X * _zoom, place.Y * _zoom), _toolbarLayer);
        var top = corner.Y - _cropSize.ActualHeight - 4;
        Canvas.SetLeft(_cropSize, Math.Round(corner.X));
        Canvas.SetTop(_cropSize, Math.Round(top < 2 ? corner.Y + 4 : top));
    }

    /// <summary>The typed crop size: from the crop's top-left corner, kept inside the picture.</summary>
    public void ApplyCropSize()
    {
        if (_overlay is not { Mode: EditorOverlayMode.Crop or EditorOverlayMode.Transform } overlay)
        {
            return;
        }

        if (overlay.Mode == EditorOverlayMode.Transform)
        {
            // 调节框: the typed size, around the box's centre.
            if (int.TryParse(_cropWidth.Text.Trim(), out var w) && int.TryParse(_cropHeight.Text.Trim(), out var h) && w >= 1 && h >= 1)
            {
                var r = overlay.TransformRect;
                w = Math.Min(w, 30000);
                h = Math.Min(h, 30000);
                overlay.TransformRect = new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
                overlay.InvalidateVisual();
                UpdateSessionBar();
            }

            PlaceCropSize(force: true);
            return;
        }

        var crop = overlay.Crop;
        if (int.TryParse(_cropWidth.Text.Trim(), out var width) && int.TryParse(_cropHeight.Text.Trim(), out var height))
        {
            width = Math.Clamp(width, 2, (int)Math.Max(2, _baseImage.PixelWidth - crop.X));
            height = Math.Clamp(height, 2, (int)Math.Max(2, _baseImage.PixelHeight - crop.Y));
            overlay.Crop = new Rect(crop.X, crop.Y, width, height);
            overlay.InvalidateVisual();
            UpdateSessionBar();
        }

        PlaceCropSize(force: true);
    }

    public void TypeCropSize(int width, int height)
    {
        _cropWidth.Text = width.ToString(CultureInfo.InvariantCulture);
        _cropHeight.Text = height.ToString(CultureInfo.InvariantCulture);
        ApplyCropSize();
    }

    /// <summary>Undo / redo for callers outside the annotation code (tests, menus).</summary>
    public void UndoStep() => Undo();

    public void RedoStep() => Redo();
}
