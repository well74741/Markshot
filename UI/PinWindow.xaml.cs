using System.Globalization;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

public partial class PinWindow : Window
{
    private bool _fadingOut;
    private bool _fadedOut;

    // The annotation toolbar and the small window that carries it below the pin.
    private protected readonly AnnotationToolbar Toolbar = new() { Visibility = Visibility.Collapsed };
    private ToolbarHostWindow? _toolbarWindow;
    private PinMagnifier? _magnifier;
    private Point? _toolbarOffset;   // pixels from the image's top-left once the user dragged it
    private bool _placingToolbar;
    private Annotation? _freshSelection;
    private WatermarkAnnotation? _draggingWatermark;
    private Point _watermarkLast;
    private Point _transformPress;
    private Vector _resizeGrab;
    private bool _transformStarted;

    /// <summary>Closes right away, skipping the fade-out (pin limit, app exit).</summary>
    public void CloseImmediately()
    {
        _fadedOut = true;
        Close();
    }

    private protected readonly AnnotationHistory _history = new();
    private protected readonly InlineTextEditor _textEditor;
    private protected BitmapSource _baseImage;

    private protected int _pixelWidth;
    private protected int _pixelHeight;
    private int _left;
    private int _top;
    private protected double _zoom = 1.0;
    private double _opacity = 1.0;
    private bool _locked;
    private bool _clickThrough;

    private bool _dragging;
    private int _dragStartCursorX;
    private int _dragStartCursorY;
    private int _dragStartLeft;
    private int _dragStartTop;

    private protected bool _editMode;
    private protected AnnotationTool _tool = AnnotationTool.Select;
    private Annotation? _active;
    private protected Annotation? _selected;
    private Point _lastPoint;
    private bool _movingSelected;
    private bool _erasing;
    private PenAnnotation? _eraserPath;
    private protected PolylineDraft? _polyline; // 折线 being placed
    private CreationGesture? _gesture;
    // E / R pressed while dragging a selected element: rotate / scale about its centre.
    private bool _dragRotate;
    private bool _dragScale;
    private Rect _dragScaleBounds;
    private Point _dragScalePointer;
    private bool _resizingAnnotation;
    private bool _rotatingAnnotation;
    private int _annotationHandle = -1;
    private Rect _annotationStartBounds;
    private Point _annotationCenter;
    private Point _annotationScaleStartPoint;
    private double _annotationStartRotation;
    private double _annotationRotateStartAngle;
    private readonly BitmapSource _originalImage;

    // Flips / rotations applied to the pin ('H', 'V', 'R' = 90° counter-clockwise), undone by F.
    private readonly List<char> _orientation = new();

    // Selecting recognised text in the pin image (view mode).
    private bool _selectingText;
    private int _textLayerVersion;

    // Middle-button drag moves the pin; a middle click without moving resets the view.
    private bool _middleDragging;
    private bool _middleMoved;

    // In-place translation: the base image before translating, null when showing the original.
    private BitmapSource? _untranslatedBase;
    private CancellationTokenSource? _translateCts;
    private DispatcherTimer? _statusTimer;
    private protected bool _suppressContextMenu;
    private bool _spaceExitGuard;
    private bool _parked; // hidden while the image editor has this pin's picture
    private WatermarkAnnotation? _editingWatermark;

    // Press on an existing text: a click edits it, a drag moves it.
    private TextAnnotation? _textPress;
    private Point _textPressPoint;
    private bool _textPressMoved;
    private UIElement? _textPressCapture;

    // Press inside an already selected text: a click (no drag) opens it for editing.
    private bool _pendingTextClick;
    private bool _dragMoved;
    private Point _pressPoint;
    private int _number = 1;
    private double _thickness = 3;
    private Color _color = Color.FromRgb(0xFF, 0x33, 0x00);
    private bool _fill;
    private double _corner;

    public BitmapSource SourceImage { get; private set; }

    /// <summary>The image editor is this window with its own frame: it never moves, fades or leaves edit mode.</summary>
    private protected virtual bool IsEditor => false;

    private protected virtual double MinZoom => 0.1;

    /// <summary>Keys of the image editor, before the annotation shortcuts. True = used.</summary>
    private protected virtual bool EditorKey(Key key, bool control) => false;

    /// <summary>The keyboard is in one of the editor's own input boxes: not a shortcut.</summary>
    private protected virtual bool KeysBelongToUi => false;

    /// <param name="image">What the pin shows (annotations composed in).</param>
    /// <param name="baseImage">The image under the annotations, when they are handed over editable.</param>
    /// <param name="annotations">Annotations in pin coordinates (from the capture), editable in the pin.</param>
    public PinWindow(BitmapSource image, int screenX, int screenY,
        BitmapSource? baseImage = null, List<Annotation>? annotations = null)
    {
        InitializeComponent();

        // Shortcuts must not be eaten by a Chinese IME; only the text boxes take IME input.
        InputMethod.SetIsInputMethodEnabled(this, false);
        InputMethod.SetIsInputMethodEnabled(TextEditor, true);
        InputMethod.SetIsInputMethodEnabled(WatermarkEditor, true);

        _textEditor = new InlineTextEditor(TextEditor, Editor);
        _textEditor.Ended += OnTextEditEnded;
        _textEditor.StyleChanged += text => Toolbar.SyncFrom(text);
        WatermarkEditor.LostKeyboardFocus += (_, _) => CommitWatermark();

        SourceImage = image;
        _baseImage = baseImage ?? image;
        _originalImage = _baseImage;
        if (baseImage != null && annotations is { Count: > 0 })
        {
            Editor.Items.AddRange(annotations);
            _number = annotations.OfType<NumberAnnotation>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;
        }

        Img.Source = image;
        _pixelWidth = image.PixelWidth;
        _pixelHeight = image.PixelHeight;
        _left = screenX;
        _top = screenY;

        Loaded += OnLoaded;
        Closed += (_, _) => LeaveGroup();
        LocationChanged += (_, _) => PlaceToolbar(); // the toolbar window follows the pin
        // Alt over the pin: the capture magnifier at the pointer.
        if (!IsEditor)
        {
            _magnifier = new PinMagnifier(ImageHost, () => CurrentImage, ToPixels); // the editor uses Alt for Alt + wheel
        }

        Motion.PopOnShow(Toolbar, fromAbove: true);
        Motion.FadeOnShow(PinStatus);
        SourceInitialized += (_, _) => ApplyBounds();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(ApplyBounds));
        Activated += (_, _) => UpdateFrame();
        Deactivated += (_, _) =>
        {
            UpdateFrame();
            _textEditor.Commit();
        };
        Closing += (_, e) =>
        {
            // Exit animation first: fade out (160 ms), then really close.
            if (!IsEditor && !_fadedOut && !Dispatcher.HasShutdownStarted && IsLoaded)
            {
                e.Cancel = true;
                if (!_fadingOut)
                {
                    _fadingOut = true;
                    IsHitTestVisible = false;
                    BeginAnimation(OpacityProperty, new DoubleAnimation(0, Motion.Fast) { EasingFunction = Motion.Ease });
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        _fadedOut = true;
                        Close();
                    };
                    timer.Start();
                }

                return;
            }

            _translateCts?.Cancel();
            _magnifier?.Hide();
            if (_editMode)
            {
                ExitEditMode(true);
            }
        };

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseWheel += OnMouseWheel;
        MouseDown += OnMouseDown;
        PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            if (_dragging)
            {
                // Right-click while dragging the pin: back to where it was.
                _dragging = false;
                _left = _dragStartLeft;
                _top = _dragStartTop;
                ReleaseMouseCapture();
                ApplyBounds();
                MoveGroup(0, 0);
                EndGroupDrag(moved: true);
                _suppressContextMenu = true;
                e.Handled = true;
                return;
            }

            if (_editMode && CancelGesture())
            {
                _suppressContextMenu = true;
                e.Handled = true;
            }
        };
        MouseUp += OnMouseUp;
        ContextMenuOpening += (_, args) =>
        {
            if (_suppressContextMenu)
            {
                _suppressContextMenu = false;
                args.Handled = true;
                return;
            }

            BuildContextMenu();
        };

        InitToolbar();
        BuildContextMenu();
        UpdateFrame();
    }

    public bool IsClickThrough => _clickThrough;

    /// <summary>Corner radius in image pixels (a pin made from a rounded capture).</summary>
    public double CornerRadius { get; init; }

    /// <summary>The shadow follows the image's own shape (a lasso capture with transparent surroundings).</summary>
    public bool ShapedShadow { get; init; }

    /// <summary>Made from a capture: appears at once (no fade, the capture shows the same pixels) with a light sweep.</summary>
    public bool FromCapture { get; init; }

    // Shadow settings being previewed from the settings window (null = the saved ones).
    private (double Size, string Color, double Opacity)? _shadowPreview;

    private double ShadowSize => _shadowPreview?.Size ?? App.Config?.PinShadowSize ?? 12;

    /// <summary>Live preview while the shadow sliders move; null restores the saved settings.</summary>
    public void PreviewShadow((double Size, string Color, double Opacity)? shadow)
    {
        _shadowPreview = shadow;
        UpdateFrame();
        ApplyBounds();
    }

    /// <summary>Everything needed to reopen this pin exactly as it was (image, editable annotations, place, zoom, opacity).</summary>
    public PinSnapshot Snapshot() => new(
        SourceImage,
        _baseImage,
        Editor.Items.Select(item => item.Clone()).ToList(),
        _left,
        _top,
        _zoom,
        _opacity);

    /// <summary>Re-applies the zoom and opacity of a snapshot (position comes from the constructor).</summary>
    public void RestoreView(double zoom, double opacity)
    {
        _zoom = Math.Clamp(zoom, 0.1, 8.0);
        _opacity = Math.Clamp(opacity, 0.2, 1.0);
        Img.Opacity = _opacity;
        ApplyBounds();
    }

    public void ReplaceImage(BitmapSource image)
    {
        SourceImage = image;
        _baseImage = image;
        Img.Source = image;
        _pixelWidth = image.PixelWidth;
        _pixelHeight = image.PixelHeight;
        ApplyBounds();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsEditor)
        {
            return;
        }

        UpdateFrame(); // ShapedShadow / CornerRadius are set after the constructor drew the first shadow

        var handle = new WindowInteropHelper(this).Handle;
        var dpi = handle != IntPtr.Zero ? Native.GetDpiForWindow(handle) / 96.0 : 1.0;

        Width = _pixelWidth / dpi;
        Height = _pixelHeight / dpi;
        Left = _left / dpi;
        Top = _top / dpi;

        ApplyBounds();
        Dispatcher.BeginInvoke(new Action(ApplyBounds), DispatcherPriority.Loaded);
        if (FromCapture)
        {
            // Same pixels as the capture underneath: no fade (that flickered), just the light sweep.
            Dispatcher.BeginInvoke(new Action(PlayLightSweep), DispatcherPriority.Loaded);
        }
        else
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Fast) { EasingFunction = Motion.Ease });
        }
        RefreshTextLayer();
    }

    private protected void ApplyBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var imageWidth = Math.Max(16, (int)Math.Round(_pixelWidth * _zoom));
        var imageHeight = Math.Max(16, (int)Math.Round(_pixelHeight * _zoom));

        ImageHost.Width = imageWidth;
        ImageHost.Height = imageHeight;
        // Shadow and frame follow the rounded corners of a rounded capture.
        var corner = new CornerRadius(CornerRadius * _zoom);
        ImageShadowLayer.CornerRadius = corner;
        FrameBorder.CornerRadius = corner;
        GroupMark.RadiusX = GroupMark.RadiusY = CornerRadius * _zoom;
        Editor.SourceCornerRadius = CornerRadius;
        Editor.Width = imageWidth;
        Editor.Height = imageHeight;
        Editor.Scale = 1.0 / _zoom;
        TextLayer.Width = imageWidth;
        TextLayer.Height = imageHeight;
        TextLayer.Scale = 1.0 / _zoom;
        TextLayer.InvalidateVisual();
        if (IsEditor)
        {
            RootGrid.Margin = new Thickness(0); // laid out by the editor's scroll area
            return;
        }

        // Room around the image for the shadow (grows with the shadow size setting).
        var pad = Pad;
        RootGrid.Margin = new Thickness(pad);
        var width = imageWidth + pad * 2;
        var height = imageHeight + pad * 2;
        var windowLeft = _left - pad;
        var windowTop = _top - pad;

        var virtualX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var virtualY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var virtualW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var virtualH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        if (width <= virtualW)
        {
            windowLeft = Math.Clamp(windowLeft, virtualX, virtualX + virtualW - width);
        }

        if (height <= virtualH)
        {
            windowTop = Math.Clamp(windowTop, virtualY, virtualY + virtualH - height);
        }

        Native.SetWindowPos(
            handle,
            Topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
            windowLeft,
            windowTop,
            width,
            height,
            Native.SWP_NOACTIVATE | (_parked ? 0 : Native.SWP_SHOWWINDOW));
        PlaceToolbar();
    }

    private static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? fallback : (Color)ColorConverter.ConvertFromString(text.Trim());
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    /// <summary>Margin around the image: enough for the configured shadow.</summary>
    private int Pad => Math.Max(16, (int)Math.Ceiling(ShadowSize) + 4);

    /// <summary>Settings changed: re-apply the shadow (size, colour, opacity) and the margin it needs.</summary>
    public void ApplyShadowSettings()
    {
        UpdateFrame();
        ApplyBounds();
        Toolbar.ApplyToolOrderFromConfig(); // button order / visibility from the settings
    }

    private void UpdateFrame()
    {
        if (IsEditor)
        {
            ImageShadowLayer.Visibility = Visibility.Collapsed;
            return;
        }

        var color = _clickThrough
            ? Color.FromRgb(0x2F, 0xC7, 0x6B)
            : _locked
                ? Color.FromRgb(0xF5, 0x9E, 0x0B)
                : IsActive
                    ? Color.FromRgb(0x3B, 0x82, 0xF6)
                    : Color.FromRgb(0x00, 0x00, 0x00);

        FrameBorder.BorderBrush = new SolidColorBrush(color);
        if (ImageShadowLayer != null)
        {
            // Shadow from the settings (size / colour / opacity); the active, locked and
            // click-through states tint it with their colour so the state stays visible.
            // While annotating, the pin counts as active: clicks on its toolbar window (which never
            // activates) used to toggle the state and made the shadow flicker.
            var active = IsActive || _editMode;
            var config = App.Config;
            var size = Math.Clamp(ShadowSize, 0, 60);
            var opacity = Math.Clamp(_shadowPreview?.Opacity ?? config?.PinShadowOpacity ?? 0.47, 0, 1);
            var stateColor = _clickThrough || _locked || active;
            var shadow = stateColor
                ? (_clickThrough || _locked ? color : Color.FromRgb(0x3B, 0x82, 0xF6))
                : ParseColor(_shadowPreview?.Color ?? config?.PinShadowColor, Color.FromRgb(0x6A, 0x6A, 0x6A));
            if (active)
            {
                opacity = Math.Min(1, opacity * 1.25);
            }

            var visible = size >= 0.5 && opacity >= 0.01;
            if (ShapedShadow)
            {
                // Lasso pins: the shadow traces the shape itself.
                ImageShadowLayer.Visibility = Visibility.Collapsed;
                Img.Effect = visible
                    ? new DropShadowEffect { Color = shadow, BlurRadius = size * 1.6, ShadowDepth = 0, Opacity = opacity, RenderingBias = RenderingBias.Quality }
                    : null;
            }
            else
            {
                ShadowBlur.Radius = size;
                ImageShadowLayer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                ImageShadowLayer.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), shadow.R, shadow.G, shadow.B));
            }
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsEditor && !ImageHost.IsMouseOver)
        {
            return;
        }

        if (_locked || (_editMode && _tool != AnnotationTool.None && _tool != AnnotationTool.Select))
        {
            return;
        }

        if (!_editMode)
        {
            if (!IsEditor && GroupPress(Keyboard.Modifiers))
            {
                Focus();
                e.Handled = true;
                return;
            }

            var pixel = ToPixels(e.GetPosition(ImageHost));
            var picking = (Keyboard.Modifiers & ModifierKeys.Shift) != 0; // Shift+press picks / drags pins, not text
            if (!picking && !IsTextBypassHeld() && TextLayer.IsOverText(pixel))
            {
                // Over recognised text: drag selects it, double-click selects the line.
                if (e.ClickCount == 2)
                {
                    TextLayer.SelectLineAt(pixel);
                }
                else
                {
                    TextLayer.BeginSelection(pixel);
                    _selectingText = true;
                    CaptureMouse();
                }

                Focus();
                e.Handled = true;
                return;
            }

            TextLayer.ClearSelection();
            if (e.ClickCount == 2)
            {
                Close();
                return;
            }
        }

        if (_editMode && (_tool == AnnotationTool.None || _tool == AnnotationTool.Select))
        {
            var point = ToPixels(e.GetPosition(ImageHost));
            var bypassText = IsTextBypassHeld();
            if (bypassText)
            {
                ClearSelection();
            }
            else
            {
                if (_textEditor.Target is not null)
                {
                    if (_textEditor.TryBeginFrameDrag(point))
                    {
                        e.Handled = true;
                        return;
                    }

                    _textEditor.Commit();
                    e.Handled = true;
                    return;
                }

                if (e.ClickCount == 2 && HitAnnotation(point) is TextAnnotation doubleClicked)
                {
                    _movingSelected = false;
                    ReleaseMouseCapture();
                    BeginTextEdit(doubleClicked, isNew: false, caretAt: point);
                    e.Handled = true;
                    return;
                }

                if (_selected != null && TryBeginAnnotationTransform(point))
                {
                    // Inside a selected text: a drag moves it, a click edits it.
                    _pendingTextClick = _movingSelected && _selected is TextAnnotation;
                    _dragMoved = false;
                    _pressPoint = point;
                    _history.Arm(Editor.Items);
                    CaptureMouse();
                    e.Handled = true;
                    return;
                }

                var hit = HitAnnotation(point);
                if (hit != null)
                {
                    SelectAnnotation(hit);
                    _pendingTextClick = false;
                    _dragMoved = false;
                    _pressPoint = point;
                    _movingSelected = true;
                    _lastPoint = point;
                    _annotationStartBounds = hit.Bounds;
                    _annotationScaleStartPoint = point;
                    _history.Arm(Editor.Items);
                    CaptureMouse();
                    e.Handled = true;
                    return;
                }

                ClearSelection();
            }
        }

        if (IsEditor)
        {
            return; // the window does not move with the image
        }

        var cursor = Native.CursorPosition();
        _dragStartCursorX = cursor.X;
        _dragStartCursorY = cursor.Y;
        _dragStartLeft = _left;
        _dragStartTop = _top;
        _dragging = true;
        BeginGroupDrag();
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_editMode && e.LeftButton == MouseButtonState.Released &&
            _textEditor.FrameCursorAt(ToPixels(e.GetPosition(ImageHost))) is { } frameCursor)
        {
            ImageHost.Cursor = frameCursor; // frame of the text being typed
            return;
        }

        if (_middleDragging)
        {
            var middleCursor = Native.CursorPosition();
            var (dx, dy) = Straighten(middleCursor.X - _dragStartCursorX, middleCursor.Y - _dragStartCursorY);
            if (_middleMoved || Math.Abs(dx) + Math.Abs(dy) > 3)
            {
                _middleMoved = true;
                _left = _dragStartLeft + dx;
                _top = _dragStartTop + dy;
                ApplyBounds();
            }

            return;
        }

        if (UpdateTextPress(ToPixels(e.GetPosition(ImageHost))))
        {
            return;
        }

        if (TransformSelected(ToPixels(e.GetPosition(ImageHost))))
        {
            return;
        }

        if (_movingSelected && _selected != null)
        {
            var point = ToPixels(e.GetPosition(ImageHost));
            var dx = point.X - _lastPoint.X;
            var dy = point.Y - _lastPoint.Y;
            if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
            {
                if (!_dragMoved && (point - _pressPoint).Length * _zoom < 3)
                {
                    return;
                }

                _dragMoved = true;
                ClearAnnotationHover();
                _selected.Translate(dx, dy);
                _lastPoint = point;
                Editor.InvalidateVisual();
            }

            return;
        }

        if (_selectingText)
        {
            TextLayer.ExtendSelection(ToPixels(e.GetPosition(ImageHost)));
            return;
        }

        if (!_dragging && !_editMode)
        {
            var pixel = ToPixels(e.GetPosition(ImageHost));
            ImageHost.Cursor = !IsTextBypassHeld() && TextLayer.IsOverText(pixel) ? Cursors.IBeam : Cursors.Arrow;
            return;
        }

        if (!_dragging)
        {
            if (_editMode && _tool == AnnotationTool.None)
            {
                var point = ToPixels(e.GetPosition(ImageHost));
                if (!IsTextBypassHeld() &&
                    (HitAnnotation(point) is TextAnnotation or WatermarkAnnotation))
                {
                    ImageHost.Cursor = Cursors.IBeam;
                }
                else if (_selected != null)
                {
                    UpdateAnnotationTransformCursor(point);
                }
                else
                {
                    ImageHost.Cursor = Cursors.Arrow;
                }
            }

            return;
        }

        var cursor = Native.CursorPosition();
        var (moveX, moveY) = Straighten(cursor.X - _dragStartCursorX, cursor.Y - _dragStartCursorY);
        _left = _dragStartLeft + moveX;
        _top = _dragStartTop + moveY;
        ApplyBounds();
        MoveGroup(moveX, moveY); // pins picked with this one come along
    }

    /// <summary>With Shift held a pin moves only horizontally or only vertically (the larger direction).</summary>
    private static (int X, int Y) Straighten(int dx, int dy)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            return (dx, dy);
        }

        return Math.Abs(dx) >= Math.Abs(dy) ? (dx, 0) : (0, dy);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectingText)
        {
            _selectingText = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (EndTextPress(ToPixels(e.GetPosition(ImageHost)), selectAll: true))
        {
            e.Handled = true;
            return;
        }

        var transformed = _resizingAnnotation || _rotatingAnnotation;
        if (_resizingAnnotation || _rotatingAnnotation || _movingSelected)
        {
            _history.Disarm();
        }

        if (transformed && IsMouseCaptured)
        {
            // A resize / rotation by the frame captured the window: let go, or nothing else gets a click.
            ReleaseMouseCapture();
        }

        _dragRotate = false;
        _dragScale = false;
        _resizingAnnotation = false;
        _rotatingAnnotation = false;
        if (_movingSelected)
        {
            _movingSelected = false;
            var clicked = _pendingTextClick && !_dragMoved;
            _pendingTextClick = false;
            _dragMoved = false;
            if (clicked && _selected is TextAnnotation selectedText)
            {
                ReleaseMouseCapture();
                BeginTextEdit(selectedText, isNew: false, caretAt: ToPixels(e.GetPosition(ImageHost)));
                e.Handled = true;
                return;
            }

            ReleaseMouseCapture();
            RefreshTransformCursor(e.GetPosition(ImageHost));
            e.Handled = true;
            return;
        }

        if (!_dragging)
        {
            RefreshTransformCursor(e.GetPosition(ImageHost));
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        EndGroupDrag(_left != _dragStartLeft || _top != _dragStartTop);
        RefreshTransformCursor(e.GetPosition(ImageHost));
        e.Handled = true;
    }

    private void RefreshTransformCursor(Point pointer)
    {
        if (_editMode && _tool == AnnotationTool.None && _selected != null)
        {
            UpdateAnnotationTransformCursor(ToPixels(pointer));
        }
        else if (!_editMode || _tool == AnnotationTool.None)
        {
            ImageHost.Cursor = Cursors.Arrow;
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _locked || IsEditor)
        {
            return;
        }

        // Middle drag moves the pin in view and edit mode alike.
        var cursor = Native.CursorPosition();
        _dragStartCursorX = cursor.X;
        _dragStartCursorY = cursor.Y;
        _dragStartLeft = _left;
        _dragStartTop = _top;
        _middleDragging = true;
        _middleMoved = false;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_middleDragging)
        {
            return;
        }

        _middleDragging = false;
        ReleaseMouseCapture();
        if (!_middleMoved)
        {
            ResetView();
        }

        e.Handled = true;
    }

    private protected void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsEditor && !ImageHost.IsMouseOver && (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) == 0)
        {
            return;
        }

        if (_textEditor.IsActive)
        {
            _textEditor.AdjustSize(e.Delta);
            e.Handled = true;
            return;
        }

        // A selected element takes the wheel (size; Ctrl = its opacity), then an active drawing
        // tool (presets for the next element); otherwise the wheel zooms the pin and Ctrl+wheel
        // sets the pin's opacity.
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var toolActive = _editMode && _tool is not AnnotationTool.None and not AnnotationTool.Select;
        if (_editMode && _selected != null)
        {
            AdjustSelectedByWheel(e.Delta, ctrl);
            e.Handled = true;
            return;
        }

        if (toolActive && _active == null)
        {
            if (ctrl)
            {
                Toolbar.NudgeOpacity(e.Delta);
            }
            else
            {
                AdjustActiveToolByWheel(e.Delta);
            }

            e.Handled = true;
            return;
        }

        if (_locked)
        {
            return;
        }

        if (!IsEditor && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _opacity = Math.Clamp(_opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.2, 1.0);
            Img.Opacity = _opacity;
        }
        else
        {
            ZoomBy(e.Delta > 0 ? 1.1 : 1 / 1.1);
            _textEditor.Refresh();
        }

        e.Handled = true;
    }

    private protected void ZoomBy(double factor)
    {
        var newZoom = Math.Clamp(_zoom * factor, MinZoom, 8.0);
        if (Math.Abs(newZoom - _zoom) < 0.0001)
        {
            return;
        }

        var oldWidth = _pixelWidth * _zoom;
        var oldHeight = _pixelHeight * _zoom;
        var centerX = _left + oldWidth / 2;
        var centerY = _top + oldHeight / 2;

        _zoom = newZoom;
        _left = (int)Math.Round(centerX - _pixelWidth * _zoom / 2);
        _top = (int)Math.Round(centerY - _pixelHeight * _zoom / 2);
        ApplyBounds();
    }

    private void ResetView()
    {
        _zoom = 1.0;
        _opacity = 1.0;
        Img.Opacity = 1.0;
        ApplyBounds();
    }

    private BitmapSource CurrentImage => _editMode ? ComposeEditedImage() : SourceImage;

    private void Translate()
    {
        ((App)Application.Current).TranslateImage(CurrentImage, this);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_textEditor.IsActive || _editingWatermark != null || KeysBelongToUi)
        {
            return;
        }

        var key = AnnotationOps.RealKey(e);

        if (!IsEditor && MatchesPinKey(App.Config.PinResetHotkey, key) &&
            (!_editMode || _tool is AnnotationTool.None or AnnotationTool.Select) &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) == 0)
        {
            ResetPinImageTransform();
            e.Handled = true;
            return;
        }

        if (!_editMode &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) == 0)
        {
            var config = App.Config;
            if (MatchesPinKey(config.PinFlipHorizontalHotkey, key))
            {
                FlipPinImage(true);
                e.Handled = true;
                return;
            }

            if (MatchesPinKey(config.PinFlipVerticalHotkey, key))
            {
                FlipPinImage(false);
                e.Handled = true;
                return;
            }

            if (MatchesPinKey(config.PinRotateHotkey, key))
            {
                RotatePinImageCounterClockwise();
                e.Handled = true;
                return;
            }

            // Tool letters open the annotation toolbar — except the pin's own T (topmost) / L (lock).
            if (key is not (Key.T or Key.L) && ToolHotkeys.TryMap(key, config, out var hotkeyTool))
            {
                EnterEditMode();
                OnToolbarToolChanged(Toolbar.ChoiceFor(hotkeyTool));
                e.Handled = true;
                return;
            }
        }

        if (_editMode)
        {
            OnEditKeyDown(e);
            return;
        }

        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (key)
        {
            case Key.Escape:
                if (TextLayer.HasSelection)
                {
                    TextLayer.ClearSelection();
                }
                else
                {
                    CloseWithGroup();
                }

                e.Handled = true;
                break;
            case Key.C when control:
                if (TextLayer.HasSelection)
                {
                    ClipboardService.CopyText(TextLayer.SelectedText);
                }
                else
                {
                    Copy();
                }

                e.Handled = true;
                break;
            case Key.A when control && TextLayer.HasText:
                TextLayer.SelectAll();
                e.Handled = true;
                break;
            case Key.S when control:
                Save();
                e.Handled = true;
                break;
            case Key.W when control:
                CloseWithGroup();
                e.Handled = true;
                break;
            case Key.T:
                Topmost = !Topmost;
                ApplyBounds();
                e.Handled = true;
                break;
            case Key.L:
                _locked = !_locked;
                UpdateFrame();
                e.Handled = true;
                break;
            case Key.Space:
                if (_spaceExitGuard)
                {
                    e.Handled = true;
                    return;
                }

                EnterEditMode();
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when !_locked && !control:
            {
                // Arrow keys nudge the pin by a pixel (Shift: 10).
                var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                NudgeWithGroup(key == Key.Left ? -step : key == Key.Right ? step : 0, key == Key.Up ? -step : key == Key.Down ? step : 0);
                e.Handled = true;
                break;
            }
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = AnnotationOps.RealKey(e);
        if (key == Key.Space)
        {
            _spaceExitGuard = false;
        }

        if (_gesture != null)
        {
            if (key == Key.Space)
            {
                _gesture.EndMove();
                InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color, CursorScale);
            }
            else if (key == Key.E)
            {
                _gesture.EndRotate();
            }
            else if (AnnotationOps.IsModifier(key))
            {
                _gesture.Move(_gesture.LastPointer, IsShiftDown, IsCtrlDown);
                Editor.InvalidateVisual();
            }

            e.Handled = !AnnotationOps.IsModifier(key);
            return;
        }

        var pointer = ToPixels(Mouse.GetPosition(ImageHost));
        if (key == Key.E && _dragRotate)
        {
            _dragRotate = false;
            _lastPoint = pointer;
            e.Handled = true;
        }

        if (key == Key.R && _dragScale)
        {
            _dragScale = false;
            _lastPoint = pointer;
            e.Handled = true;
        }
    }

    private static bool IsShiftDown => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    private static bool IsCtrlDown => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    private double EraserWidth => Math.Max(2, _thickness);

    /// <summary>Screen pixels per image pixel (zoom × display scaling): brush cursors use it to match the stroke.</summary>
    private double CursorScale => _zoom * VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>The selection is the element just created (see AnnotationOps.SelectionHit).</summary>
    private bool IsFreshSelection => _freshSelection != null && ReferenceEquals(_freshSelection, _selected);

    private void BeginTextEdit(TextAnnotation text, bool isNew, Point? caretAt, bool selectAll = false)
    {
        ClearSelection();
        ClearAnnotationHover();
        _history.Arm(Editor.Items);
        if (isNew)
        {
            Editor.Items.Add(text);
        }

        Toolbar.SetTool(AnnotationTool.Text);
        Toolbar.SyncFrom(text);
        _tool = AnnotationTool.Text;
        InputLayer.IsHitTestVisible = true;
        InputLayer.Cursor = ToolCursors.Get(AnnotationTool.Text, _thickness, _color, CursorScale);
        _textEditor.Begin(text, isNew, caretAt, selectAll);
    }

    private static bool MatchesPinKey(string? configured, Key key)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        return string.Equals(configured.Trim(), key.ToString(), StringComparison.OrdinalIgnoreCase)
               || (configured.Trim().Length == 1
                   && char.ToUpperInvariant(configured.Trim()[0]) == (char)('A' + ((int)key - (int)Key.A)));
    }

    private Rect Clamp(Rect rect)
    {
        var clamped = Rect.Intersect(rect, new Rect(0, 0, SourceImage.PixelWidth, SourceImage.PixelHeight));
        return clamped.Width < 1 || clamped.Height < 1 ? rect : clamped;
    }
}

/// <summary>A closed pin, kept in memory so it can be restored exactly.</summary>
public sealed record PinSnapshot(
    BitmapSource Image,
    BitmapSource BaseImage,
    List<Annotation> Annotations,
    int Left,
    int Top,
    double Zoom,
    double Opacity);
