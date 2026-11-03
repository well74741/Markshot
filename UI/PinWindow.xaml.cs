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
    private readonly AnnotationToolbar Toolbar = new() { Visibility = Visibility.Collapsed };
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

    private readonly AnnotationHistory _history = new();
    private readonly InlineTextEditor _textEditor;
    private BitmapSource _baseImage;

    private int _pixelWidth;
    private int _pixelHeight;
    private int _left;
    private int _top;
    private double _zoom = 1.0;
    private double _opacity = 1.0;
    private bool _locked;
    private bool _clickThrough;

    private bool _dragging;
    private int _dragStartCursorX;
    private int _dragStartCursorY;
    private int _dragStartLeft;
    private int _dragStartTop;

    private bool _editMode;
    private AnnotationTool _tool = AnnotationTool.Select;
    private Annotation? _active;
    private Annotation? _selected;
    private Point _lastPoint;
    private bool _movingSelected;
    private bool _erasing;
    private PenAnnotation? _eraserPath;
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
    private bool _suppressContextMenu;
    private bool _spaceExitGuard;
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
        LocationChanged += (_, _) => PlaceToolbar(); // the toolbar window follows the pin
        // Alt over the pin: the capture magnifier at the pointer.
        _magnifier = new PinMagnifier(ImageHost, () => CurrentImage, ToPixels);
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
            if (!_fadedOut && !Dispatcher.HasShutdownStarted && IsLoaded)
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

    private void InitToolbar()
    {
        Toolbar.SetMode(AnnotationToolbarMode.Pin);
        Toolbar.ToolChanged += OnToolbarToolChanged;
        Toolbar.WatermarkToggled += OnWatermarkToggled;
        Toolbar.VariantChanged += OnToolbarVariantChanged;
        Toolbar.SettingsChanged += ApplyToolbarSettings;
        Toolbar.UndoRequested += Undo;
        Toolbar.RedoRequested += Redo;
        Toolbar.CopyRequested += Copy;
        Toolbar.SaveRequested += Save;
        Toolbar.TranslateRequested += Translate;
        Toolbar.ApplyRequested += () => ExitEditMode(true);
        Toolbar.RefreshUndo(false, false);
        Toolbar.SizeChanged += (_, _) =>
        {
            if (_editMode)
            {
                Dispatcher.BeginInvoke(new Action(PlaceToolbar), DispatcherPriority.Loaded);
            }
        };

        _tool = AnnotationTool.None;
        Toolbar.SetTool(AnnotationTool.None);
        Toolbar.SetSelection(null);
        InputLayer.IsHitTestVisible = false;
        InputLayer.Cursor = Cursors.Arrow;
    }

    private void OnToolbarToolChanged(AnnotationTool tool)
    {
        _textEditor.Commit();
        _watermarkOff = false; // choosing the watermark tool means: show one
        _tool = tool;
        _thickness = Toolbar.Thickness;
        ClearSelection();
        ClearAnnotationHover();
        Toolbar.SetSelection(null);
        var active = tool is not AnnotationTool.None and not AnnotationTool.Select;
        InputLayer.IsHitTestVisible = active;
        InputLayer.Cursor = active ? ToolCursors.Get(tool, _thickness, _color, CursorScale) : Cursors.Arrow;
        ActivateWatermarkIfNeeded();
    }

    private void OnToolbarVariantChanged(AnnotationTool tool)
    {
        // A selected element of that button switches to the picked style.
        if (_selected != null && AnnotationOps.CanConvert(_selected, tool))
        {
            ConvertSelected(tool);
            return;
        }

        if (!AnnotationOps.SameVariantFamily(_tool, tool))
        {
            OnToolbarToolChanged(tool);
            return;
        }

        _tool = tool;
        _thickness = Toolbar.Thickness;
        var active = tool is not AnnotationTool.None and not AnnotationTool.Select;
        InputLayer.IsHitTestVisible = active;
        InputLayer.Cursor = active ? ToolCursors.Get(tool, _thickness, _color, CursorScale) : Cursors.Arrow;
        ClearAnnotationHover();
        ActivateWatermarkIfNeeded();
    }

    /// <summary>The watermark tool works on the canvas-wide watermark, creating it on first use.</summary>
    private void ActivateWatermarkIfNeeded()
    {
        if (_tool != AnnotationTool.Watermark)
        {
            Editor.HoverWatermark = null;
            return;
        }

        var canvas = new Rect(0, 0, _baseImage.PixelWidth, _baseImage.PixelHeight);
        if (_watermarkOff)
        {
            Toolbar.SetWatermarkOn(AnnotationOps.FindWatermark(Editor.Items) != null);
            return;
        }

        var watermark = AnnotationOps.EnsureWatermark(Editor.Items, canvas, Toolbar, _history);
        Toolbar.SyncFrom(watermark);
        Toolbar.SetWatermarkOn(true);
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private bool _watermarkOff;

    /// <summary>The watermark switch: off takes the watermark away (undoable), on brings one back.</summary>
    private void OnWatermarkToggled(bool on)
    {
        _watermarkOff = !on;
        if (!on && AnnotationOps.FindWatermark(Editor.Items) is { } watermark)
        {
            _history.Checkpoint(Editor.Items);
            Editor.Items.Remove(watermark);
            Editor.HoverWatermark = null;
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            return;
        }

        ActivateWatermarkIfNeeded();
    }

    private void ApplyToolbarSettings()
    {
        _color = Toolbar.Color;
        _thickness = Toolbar.Thickness;
        _fill = Toolbar.FillEnabled;
        _corner = Toolbar.CornerRadius;
        if (_tool is not AnnotationTool.None and not AnnotationTool.Select)
        {
            InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color, CursorScale);
        }

        if (_textEditor.Target is { } editing)
        {
            AnnotationStyler.Apply(editing, Toolbar, _baseImage);
            _textEditor.MarkDirty();
            _textEditor.Refresh();
            return;
        }

        if (_tool == AnnotationTool.Watermark && AnnotationOps.FindWatermark(Editor.Items) is { } watermark)
        {
            _history.CheckpointCoalesced(Editor.Items, watermark);
            AnnotationStyler.Apply(watermark, Toolbar, _baseImage);
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            return;
        }

        if (_selected == null)
        {
            return;
        }

        _history.CheckpointCoalesced(Editor.Items, _selected);
        AnnotationStyler.Apply(_selected, Toolbar, _baseImage);
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void PasteAnnotation()
    {
        if (AnnotationClipboard.Paste(new Rect(0, 0, _baseImage.PixelWidth, _baseImage.PixelHeight)) is not { } pasted)
        {
            return;
        }

        _history.Checkpoint(Editor.Items);
        if (pasted is MosaicAnnotation mosaic)
        {
            MosaicBuilder.Rebuild(mosaic, _baseImage);
        }

        Editor.Items.Add(pasted);
        if (pasted is NumberAnnotation)
        {
            _number++;
        }

        UpdateUndoButtons();
        SelectQuietly(pasted);
    }

    private void ConvertSelected(AnnotationTool variant)
    {
        if (_selected == null)
        {
            return;
        }

        var old = _selected;
        Annotation? replacement = null;

        switch (old, variant)
        {
            case (RectAnnotation rect, AnnotationTool.Ellipse):
                replacement = new EllipseAnnotation
                {
                    Rect = rect.Rect, Color = rect.Color, Thickness = rect.Thickness, Fill = rect.Fill
                };
                break;
            case (EllipseAnnotation ellipse, AnnotationTool.Rectangle):
                replacement = new RectAnnotation
                {
                    Rect = ellipse.Rect, Color = ellipse.Color, Thickness = ellipse.Thickness,
                    Fill = ellipse.Fill, CornerRadius = _corner
                };
                break;
            case (LineAnnotation line, AnnotationTool.Arrow):
                replacement = new ArrowAnnotation
                {
                    Start = line.Start, End = line.End, Color = line.Color, Thickness = line.Thickness
                };
                break;
            case (ArrowAnnotation arrow, AnnotationTool.Line):
                replacement = new LineAnnotation
                {
                    Start = arrow.Start, End = arrow.End, Color = arrow.Color, Thickness = arrow.Thickness
                };
                break;
            case (PenAnnotation pen, AnnotationTool.Pen):
                pen.Highlight = false;
                SelectAnnotation(pen);
                return;
            case (PenAnnotation pen, AnnotationTool.Highlighter):
                pen.Highlight = true;
                SelectAnnotation(pen);
                return;
        }

        if (replacement == null)
        {
            return;
        }

        replacement.Fill = _fill;
        replacement.CornerRadius = _corner;
        replacement.Opacity = old.Opacity;
        replacement.Rotation = old.Rotation;
        var index = Editor.Items.IndexOf(old);
        if (index < 0)
        {
            return;
        }

        _history.Checkpoint(Editor.Items);
        UpdateUndoButtons();

        Editor.Items[index] = replacement;
        if (_tool is not AnnotationTool.None and not AnnotationTool.Select)
        {
            // Drawing with the button: keep drawing in the new style, the element stays selected.
            _tool = variant;
            Toolbar.SetTool(variant);
            InputLayer.Cursor = ToolCursors.Get(variant, _thickness, _color, CursorScale);
            SelectQuietly(replacement);
            _freshSelection = replacement;
            return;
        }

        _selected = replacement;
        Editor.Selected = replacement;
        Toolbar.SetTool(AnnotationTool.Select);
        Toolbar.SetSelection(replacement);
        Toolbar.SetColor(replacement.Color);
        Toolbar.SetThickness(replacement.Thickness);
        Toolbar.SetFill(replacement.Fill);
        Toolbar.SetCorner(replacement.CornerRadius);
        Editor.InvalidateVisual();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
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

    /// <summary>OCRs what the pin shows so its text can be selected and copied.</summary>
    private async void RefreshTextLayer()
    {
        var version = ++_textLayerVersion;
        TextLayer.Reset();
        var image = SourceImage;
        try
        {
            var blocks = await OcrService.RecognizeBlocksAsync(image, App.Config.OcrLanguage);
            if (version == _textLayerVersion && ReferenceEquals(image, SourceImage))
            {
                TextLayer.SetLines(blocks);
            }
        }
        catch (Exception ex)
        {
            Log.Error("贴图文字层识别失败", ex);
        }
    }

    private void ApplyBounds()
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
        Editor.SourceCornerRadius = CornerRadius;
        Editor.Width = imageWidth;
        Editor.Height = imageHeight;
        Editor.Scale = 1.0 / _zoom;
        TextLayer.Width = imageWidth;
        TextLayer.Height = imageHeight;
        TextLayer.Scale = 1.0 / _zoom;
        TextLayer.InvalidateVisual();

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
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        PlaceToolbar();
    }

    /// <summary>Screen rectangle of the image in physical pixels.</summary>
    private Native.RECT ImageScreenRect()
    {
        Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var window);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var inset = (int)Math.Round(Pad * dpi);
        return new Native.RECT
        {
            Left = window.Left + inset,
            Top = window.Top + inset,
            Right = window.Right - inset,
            Bottom = window.Bottom - inset
        };
    }

    /// <summary>
    /// Puts the toolbar window under the image (above it when there is no room below), or
    /// where the user dragged it relative to the image.
    /// </summary>
    private void PlaceToolbar()
    {
        if (!_editMode || _toolbarWindow is not { IsVisible: true } host || host.Handle == IntPtr.Zero)
        {
            return;
        }

        var image = ImageScreenRect();
        var bar = host.PixelBounds;
        int x, y;
        if (_toolbarOffset is { } offset)
        {
            x = image.Left + (int)offset.X;
            y = image.Top + (int)offset.Y;
        }
        else
        {
            var gap = (int)Math.Round(6 * VisualTreeHelper.GetDpi(this).DpiScaleY);
            x = image.Left;
            y = image.Bottom + gap;
            var screenBottom = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN) + Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (y + bar.Height > screenBottom)
            {
                y = image.Top - gap - bar.Height;
            }
        }

        _placingToolbar = true;
        host.MoveTo(x, y);
        _placingToolbar = false;
    }

    private void ShowToolbarWindow()
    {
        if (_toolbarWindow == null)
        {
            _toolbarWindow = new ToolbarHostWindow(Toolbar) { Owner = this };
            // Should it ever get the keys itself, they still belong to the pin.
            _toolbarWindow.PreviewKeyDown += (_, e) => OnPreviewKeyDown(this, e);
            Toolbar.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(new Action(ReclaimKeyboard), DispatcherPriority.Input);
            _toolbarWindow.LocationChanged += (_, _) =>
            {
                // Dragged by its handle: remember where, relative to the image.
                if (_placingToolbar || !_editMode || !_toolbarWindow.IsVisible)
                {
                    return;
                }

                var image = ImageScreenRect();
                var bar = _toolbarWindow.PixelBounds;
                _toolbarOffset = new Point(bar.Left - image.Left, bar.Top - image.Top);
            };
        }

        _toolbarWindow.Show();
        PlaceToolbar();
    }

    /// <summary>
    /// After a click on the toolbar (its own, never-activated window) the keys — Tab, Esc,
    /// tool letters — must reach the pin: make sure it is the active window and the canvas has
    /// the keyboard focus (unless a text is being typed).
    /// </summary>
    private void ReclaimKeyboard()
    {
        if (!_editMode || _textEditor.IsActive || _editingWatermark != null || Toolbar.IsMenuOpen)
        {
            return;
        }

        if (!IsActive)
        {
            Activate();
        }

        InputLayer.Focus();
        Keyboard.Focus(InputLayer);
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
        if (_locked || (_editMode && _tool != AnnotationTool.None && _tool != AnnotationTool.Select))
        {
            return;
        }

        if (!_editMode)
        {
            var pixel = ToPixels(e.GetPosition(ImageHost));
            if (!IsTextBypassHeld() && TextLayer.IsOverText(pixel))
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

        var cursor = Native.CursorPosition();
        _dragStartCursorX = cursor.X;
        _dragStartCursorY = cursor.Y;
        _dragStartLeft = _left;
        _dragStartTop = _top;
        _dragging = true;
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

        if (_resizingAnnotation || _rotatingAnnotation || _movingSelected)
        {
            _history.Disarm();
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
        if (e.ChangedButton != MouseButton.Middle || _locked)
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

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
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

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
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

    private void ZoomBy(double factor)
    {
        var newZoom = Math.Clamp(_zoom * factor, 0.1, 8.0);
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

    private void ToggleEditing()
    {
        if (_editMode)
        {
            ExitEditMode(true);
        }
        else
        {
            EnterEditMode();
        }
    }

    private void EnterEditMode()
    {
        if (_editMode || _locked || _clickThrough)
        {
            return;
        }

        _editMode = true;
        TextLayer.ClearSelection();
        TextLayer.Visibility = Visibility.Collapsed;
        _active = null;
        _selected = null;
        Editor.Preview = null;
        Editor.Selected = null;
        Editor.Source = _baseImage;
        Img.Source = _baseImage;
        Editor.Scale = 1.0 / _zoom;
        Editor.Visibility = Visibility.Visible;
        InputLayer.Visibility = Visibility.Visible;
        // Reset the tool while the toolbar is hidden, so it opens in its final shape.
        SetTool(AnnotationTool.None);
        Toolbar.SetSelection(null);
        if (Motion.IsHiding(Toolbar))
        {
            Motion.PopIn(Toolbar); // still fading out: turn around from where it is
        }

        Toolbar.Visibility = Visibility.Visible;
        ShowToolbarWindow();
        UpdateUndoButtons();
        ApplyBounds();
        InputLayer.Focus();
    }

    private void ExitEditMode(bool apply)
    {
        if (!_editMode)
        {
            return;
        }

        if (_active != null)
        {
            CommitAnnotation();
        }

        _textEditor.Commit();
        CommitWatermark();
        if (apply)
        {
            var edited = ComposeEditedImage();
            SourceImage = edited;
            Img.Source = edited;
            _pixelWidth = edited.PixelWidth;
            _pixelHeight = edited.PixelHeight;
        }

        _editMode = false;
        _active = null;
        _selected = null;
        Editor.Preview = null;
        Editor.Selected = null;
        Editor.Visibility = Visibility.Collapsed;
        InputLayer.Visibility = Visibility.Collapsed;
        Motion.PopOut(Toolbar, completed: () =>
        {
            if (!_editMode)
            {
                _toolbarWindow?.Hide();
            }
        });
        _history.Clear();
        UpdateUndoButtons();
        ApplyBounds();
        TextLayer.Visibility = Visibility.Visible;
        if (apply)
        {
            RefreshTextLayer();
        }
    }

    private BitmapSource ComposeEditedImage()
    {
        if (Editor.Items.Count == 0)
        {
            return _baseImage;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Rounded pins keep their corners transparent, annotations included.
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, _baseImage.PixelWidth, _baseImage.PixelHeight), CornerRadius, CornerRadius));
            dc.DrawImage(_baseImage, new Rect(0, 0, _baseImage.PixelWidth, _baseImage.PixelHeight));
            foreach (var annotation in AnnotationEditor.DrawOrder(Editor.Items))
            {
                AnnotationEditor.DrawItem(dc, annotation);
            }

            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(
            _baseImage.PixelWidth, _baseImage.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private BitmapSource CurrentImage => _editMode ? ComposeEditedImage() : SourceImage;

    private void Translate()
    {
        ((App)Application.Current).TranslateImage(CurrentImage, this);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_textEditor.IsActive || _editingWatermark != null)
        {
            return;
        }

        var key = AnnotationOps.RealKey(e);

        if (MatchesPinKey(App.Config.PinResetHotkey, key) &&
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
                    Close();
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
                Close();
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
                _left += key == Key.Left ? -step : key == Key.Right ? step : 0;
                _top += key == Key.Up ? -step : key == Key.Down ? step : 0;
                ApplyBounds();
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

    /// <summary>Rotate / resize / E-rotate / R-scale of the selected element. True when it consumed the move.</summary>
    private bool TransformSelected(Point point)
    {
        if (_selected == null)
        {
            return false;
        }

        if ((_rotatingAnnotation || _resizingAnnotation) && !_transformStarted)
        {
            // A click on a handle / grip without a real drag changes nothing.
            if ((point - _transformPress).Length * _zoom < 3)
            {
                return true;
            }

            _transformStarted = true;
        }

        if (_rotatingAnnotation || _resizingAnnotation || (_movingSelected && (_dragRotate || _dragScale)))
        {
            _history.CommitArmed();
            UpdateUndoButtons();
        }

        if (_rotatingAnnotation || (_movingSelected && _dragRotate))
        {
            var angle = Math.Atan2(point.Y - _annotationCenter.Y, point.X - _annotationCenter.X) * 180 / Math.PI;
            var rotation = _annotationStartRotation + angle - _annotationRotateStartAngle;
            if (IsShiftDown)
            {
                rotation = Math.Round(rotation / 15) * 15;
            }

            _selected.Rotation = ((rotation + 180) % 360 + 360) % 360 - 180;
            Editor.InvalidateVisual();
            return true;
        }

        if (_resizingAnnotation)
        {
            var target = AnnotationOps.ResizeFromHandle(
                _annotationStartBounds, _selected.Rotation, _annotationHandle, point + _resizeGrab, IsShiftDown);
            AnnotationOps.Resize(_selected, target, _annotationHandle);
            Editor.InvalidateVisual();
            return true;
        }

        if (_movingSelected && _dragScale)
        {
            var center = new Point(
                _dragScaleBounds.X + _dragScaleBounds.Width / 2,
                _dragScaleBounds.Y + _dragScaleBounds.Height / 2);
            var startDistance = Math.Max(1, (_dragScalePointer - center).Length);
            var factor = Math.Max(0.05, (point - center).Length / startDistance);
            AnnotationOps.Resize(_selected, AnnotationOps.ScaleAround(_dragScaleBounds, center, factor), 4);
            Editor.InvalidateVisual();
            return true;
        }

        return false;
    }

    private void OnEditKeyDown(KeyEventArgs e)
    {
        var key = AnnotationOps.RealKey(e);
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // While a selected element is being dragged, E rotates and R scales it;
        // every other key belongs to the drag and must not switch tools.
        if (_movingSelected || _resizingAnnotation || _rotatingAnnotation || _textPress != null)
        {
            if (key == Key.E && _movingSelected && !_dragRotate && _selected != null)
            {
                var bounds = _selected.Bounds;
                _annotationCenter = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                _annotationStartRotation = _selected.Rotation;
                _annotationRotateStartAngle = Math.Atan2(_lastPoint.Y - _annotationCenter.Y,
                    _lastPoint.X - _annotationCenter.X) * 180 / Math.PI;
                _dragScale = false;
                _dragRotate = true;
            }
            else if (key == Key.R && _movingSelected && !_dragScale && _selected != null)
            {
                _dragScaleBounds = _selected.Bounds;
                _dragScalePointer = _lastPoint;
                _dragRotate = false;
                _dragScale = true;
            }

            if (!AnnotationOps.IsModifier(key))
            {
                e.Handled = true;
            }

            return;
        }

        // While drawing: Space moves the element, E rotates it about its start
        // point, Shift/Ctrl refresh the constrained shape immediately.
        if (_gesture != null)
        {
            if (key == Key.Space)
            {
                _gesture.BeginMove();
                InputLayer.Cursor = Cursors.SizeAll;
            }
            else if (key == Key.E)
            {
                _gesture.BeginRotate();
            }
            else if (AnnotationOps.IsModifier(key))
            {
                _gesture.Move(_gesture.LastPointer, shift, control);
                Editor.InvalidateVisual();
                return;
            }

            e.Handled = true;
            return;
        }

        if (!control && (Keyboard.Modifiers & ModifierKeys.Alt) == 0 &&
            ToolHotkeys.TryMap(key, App.Config, out var toolShortcut))
        {
            SetTool(Toolbar.ChoiceFor(toolShortcut));
            e.Handled = true;
            return;
        }

        if (key == Key.Tab && !control)
        {
            var current = _selected != null && _tool is AnnotationTool.None or AnnotationTool.Select
                ? AnnotationOps.ToolOf(_selected)
                : _tool;
            if (AnnotationOps.NextVariant(current) is { } next)
            {
                Toolbar.PickVariant(next);
            }

            e.Handled = true;
            return;
        }

        if (control && key == Key.C)
        {
            if (_selected != null && AnnotationClipboard.CanCopy(_selected))
            {
                AnnotationClipboard.Copy(_selected);
            }
            else
            {
                ClipboardService.CopyImage(ComposeEditedImage());
            }

            e.Handled = true;
            return;
        }

        if (control && key == Key.V && AnnotationClipboard.HasContent)
        {
            PasteAnnotation();
            e.Handled = true;
            return;
        }

        if (key is Key.Delete or Key.Back && _selected == null && _tool == AnnotationTool.Watermark &&
            AnnotationOps.FindWatermark(Editor.Items) is { } watermarkToDelete)
        {
            _history.Checkpoint(Editor.Items);
            Editor.Items.Remove(watermarkToDelete);
            Editor.HoverWatermark = null;
            SetTool(AnnotationTool.Select);
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            if (_selected != null)
            {
                ClearSelection();
            }
            else
            {
                ExitEditMode(true);
            }

            e.Handled = true;
            return;
        }

        if (key == Key.Space)
        {
            _spaceExitGuard = true;

            ExitEditMode(true);
            e.Handled = true;
            return;
        }

        if (control && key == Key.Z)
        {
            Undo();
            e.Handled = true;
            return;
        }

        if (control && key == Key.Y)
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (control && key == Key.S)
        {
            ExitEditMode(true);
            e.Handled = true;
            return;
        }

        if (!control && key == Key.V)
        {
            SetTool(AnnotationTool.Select);
            e.Handled = true;
            return;
        }

        if (shift && key >= Key.D1 && key <= Key.D9)
        {
            SetTool(key switch
            {
                Key.D1 => AnnotationTool.Rectangle,
                Key.D2 => AnnotationTool.Line,
                Key.D3 => AnnotationTool.Arrow,
                Key.D4 => AnnotationTool.Number,
                Key.D5 => AnnotationTool.Pen,
                Key.D6 => AnnotationTool.Highlighter,
                Key.D7 => AnnotationTool.Mosaic,
                Key.D8 => AnnotationTool.Text,
                Key.D9 => AnnotationTool.Eraser,
                _ => AnnotationTool.Select
            });
            e.Handled = true;
            return;
        }

        if (control && key == Key.B && _selected is TextAnnotation)
        {
            Toolbar.ToggleBold();
            e.Handled = true;
            return;
        }

        if (_selected != null && key is Key.Delete or Key.Back)
        {
            _history.Checkpoint(Editor.Items);
            Editor.Items.Remove(_selected);
            ClearSelection();
            UpdateUndoButtons();
            e.Handled = true;
            return;
        }

        if (_selected != null && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var amount = shift ? 10.0 : 1.0;
            var dx = key == Key.Left ? -amount : key == Key.Right ? amount : 0;
            var dy = key == Key.Up ? -amount : key == Key.Down ? amount : 0;
            _history.CheckpointCoalesced(Editor.Items, ("nudge", _selected));
            _selected.Translate(dx, dy);
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            e.Handled = true;
        }
    }

    private void SetTool(AnnotationTool tool)
    {
        _textEditor.Commit();
        _tool = tool;
        Toolbar.SetTool(tool);
        if (tool != AnnotationTool.Select && tool != AnnotationTool.None)
        {
            ClearSelection();
        }
        else if (_selected != null)
        {
            Toolbar.SetSelection(_selected);
        }

        var active = tool is not AnnotationTool.None and not AnnotationTool.Select;
        InputLayer.IsHitTestVisible = active;
        InputLayer.Cursor = active ? ToolCursors.Get(tool, _thickness, _color, CursorScale) : Cursors.Arrow;
        ClearAnnotationHover();
    }

    private void Undo() => ApplyHistory(_history.Undo);

    private void Redo() => ApplyHistory(_history.Redo);

    private void ApplyHistory(Func<List<Annotation>, bool> step)
    {
        _textEditor.Commit();
        ClearSelection();
        if (!step(Editor.Items))
        {
            return;
        }

        _number = Editor.Items.OfType<NumberAnnotation>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void UpdateUndoButtons()
    {
        Toolbar.RefreshUndo(_history.CanUndo, _history.CanRedo);
    }

    private Point ToPixels(Point dip) => new(dip.X / _zoom, dip.Y / _zoom);

    /// <summary>Screen pixels per image pixel (zoom × display scaling): brush cursors use it to match the stroke.</summary>
    private double CursorScale => _zoom * VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void Input_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = ToPixels(e.GetPosition(InputLayer));
        if (_textEditor.Target is { } editingText)
        {
            // The frame of the text being typed moves / resizes / rotates it; typing continues.
            if (_textEditor.TryBeginFrameDrag(point))
            {
                e.Handled = true;
                return;
            }

            _textEditor.Commit();

            if (!(_tool == AnnotationTool.Text && HitAnnotation(point) is TextAnnotation other &&
                  !ReferenceEquals(other, editingText)))
            {
                e.Handled = true;
                return;
            }
        }

        if (_selected != null)
        {
            var frame = AnnotationOps.SelectionHit(_selected, IsFreshSelection, _tool, point, 1 / _zoom);
            if (frame.IsHandle || frame.Rotate || (frame.Inside && _selected is TextAnnotation && _tool == AnnotationTool.Text))
            {
                TryBeginAnnotationTransform(point);
                _pendingTextClick = _movingSelected && _selected is TextAnnotation;
                _dragMoved = false;
                _pressPoint = point;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        Focus();
        CommitWatermark();

        _lastPoint = point;

        if (_tool == AnnotationTool.Watermark)
        {
            if (e.ClickCount == 2 && AnnotationOps.FindWatermark(Editor.Items) is { } watermark &&
                watermark.HitTest(point, 4 / _zoom))
            {
                BeginWatermarkEdit(watermark);
            }
            else if (AnnotationOps.FindWatermark(Editor.Items) is { } dragged)
            {
                // Press and drag moves the watermark pattern.
                _draggingWatermark = dragged;
                _watermarkLast = point;
                _pressPoint = point;
                _dragMoved = false;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
            }

            e.Handled = true;
            return;
        }

        if (_tool == AnnotationTool.Text)
        {
            if (HitAnnotation(point) is TextAnnotation pressed)
            {
                BeginTextPress(pressed, point, InputLayer);
            }
            else
            {
                BeginTextEdit(AnnotationStyler.NewText(point, Toolbar), isNew: true, caretAt: null);
            }

            e.Handled = true;
            return;
        }

        if (_tool is not AnnotationTool.None and not AnnotationTool.Select and not AnnotationTool.Eraser)
        {
            var candidate = HitAnnotation(point);
            if (candidate != null && SameFamily(candidate, _tool))
            {
                SelectAnnotation(candidate);
                _movingSelected = true;
                _lastPoint = point;
                _history.Arm(Editor.Items);
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        if (_tool == AnnotationTool.Select || _tool == AnnotationTool.None)
        {
            var hit = HitAnnotation(point);
            if (hit != null)
            {
                SelectAnnotation(hit);
                _movingSelected = true;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            ClearSelection();
            return;
        }

        if (_tool == AnnotationTool.Eraser)
        {
            // The stroke erases live and is bound to the elements it touched on release.
            _eraserPath = new PenAnnotation { Thickness = EraserWidth };
            _eraserPath.Points.Add(point);
            _gesture = new CreationGesture(_eraserPath, point);
            _erasing = true;
            Editor.LiveEraser = _eraserPath.Points;
            Editor.LiveEraserWidth = _eraserPath.Thickness;
            Editor.LiveEraserStrength = Toolbar.EraserOpacity;
            Editor.InvalidateVisual();
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        StartAnnotation(point);
        InputLayer.CaptureMouse();
        e.Handled = true;
    }

    private void Input_MouseMove(object sender, MouseEventArgs e)
    {
        var point = ToPixels(e.GetPosition(InputLayer));
        if (UpdateTextPress(point))
        {
            return;
        }

        if (e.LeftButton == MouseButtonState.Released && _textEditor.FrameCursorAt(point) is { } frameCursor)
        {
            InputLayer.Cursor = frameCursor; // frame of the text being typed
            return;
        }

        if (_gesture != null)
        {
            _gesture.Move(point, IsShiftDown, IsCtrlDown);
            Editor.InvalidateVisual();
            return;
        }

        if (_draggingWatermark != null)
        {
            if (!_dragMoved && (point - _pressPoint).Length * _zoom < 3)
            {
                return;
            }

            if (!_dragMoved)
            {
                _dragMoved = true;
                _history.CommitArmed();
                UpdateUndoButtons();
                InputLayer.Cursor = Cursors.SizeAll;
            }

            _draggingWatermark.Translate(point.X - _watermarkLast.X, point.Y - _watermarkLast.Y);
            _watermarkLast = point;
            Editor.InvalidateVisual();
            return;
        }

        if (TransformSelected(point))
        {
            return;
        }

        if (_movingSelected && _selected != null)
        {
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
                _history.CommitArmed();
                UpdateUndoButtons();
                _selected.Translate(dx, dy);
                _lastPoint = point;
                Editor.InvalidateVisual();
            }

            return;
        }

        UpdateCursor(point);
        if (_tool == AnnotationTool.Watermark)
        {
            var watermark = AnnotationOps.FindWatermark(Editor.Items);
            var hover = watermark != null && watermark.HitTest(point, 2 / _zoom) ? watermark : null;
            InputLayer.Cursor = hover != null ? Cursors.Hand : Cursors.Arrow;
            if (!ReferenceEquals(Editor.HoverWatermark, hover))
            {
                Editor.HoverWatermark = hover;
                Editor.InvalidateVisual();
            }

            return;
        }

        UpdateAnnotationHover(point);
    }

    /// <summary>
    /// Right-click while the left button is still down undoes the gesture in progress
    /// (drawing, erasing, moving / resizing / rotating, dragging a text or the watermark).
    /// </summary>
    private bool CancelGesture()
    {
        if (_textEditor.IsFraming)
        {
            _textEditor.CancelFrameDrag();
            return true;
        }

        if (_active != null || _erasing)
        {
            Editor.Preview = null;
            Editor.LiveEraser = null;
            _active = null;
            _gesture = null;
            _erasing = false;
            _eraserPath = null;
            InputLayer.ReleaseMouseCapture();
            Editor.InvalidateVisual();
            return true;
        }

        if (_resizingAnnotation || _rotatingAnnotation || _movingSelected || _textPress != null || _draggingWatermark != null)
        {
            var target = _selected ?? _textPress;
            var index = target != null ? Editor.Items.IndexOf(target) : -1;
            var reverted = _history.CancelGesture(Editor.Items);
            _resizingAnnotation = false;
            _rotatingAnnotation = false;
            _movingSelected = false;
            _textPress = null;
            _draggingWatermark = null;
            _dragRotate = false;
            _dragScale = false;
            _pendingTextClick = false;
            _dragMoved = false;
            InputLayer.ReleaseMouseCapture();
            ReleaseMouseCapture();
            if (reverted)
            {
                ClearSelection();
                if (index >= 0 && index < Editor.Items.Count && Editor.Items[index] is not WatermarkAnnotation)
                {
                    SelectQuietly(Editor.Items[index]);
                }
            }

            UpdateUndoButtons();
            Editor.InvalidateVisual();
            return true;
        }

        return false;
    }

    private void Input_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_active == null && !_erasing)
        {
            return;
        }

        // Right click while drawing cancels the element being drawn.
        Editor.Preview = null;
        Editor.LiveEraser = null;
        _active = null;
        _gesture = null;
        _erasing = false;
        _eraserPath = null;
        _suppressContextMenu = true;
        InputLayer.ReleaseMouseCapture();
        Editor.InvalidateVisual();
        e.Handled = true;
    }

    private void Input_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (EndTextPress(ToPixels(e.GetPosition(InputLayer)), selectAll: false))
        {
            return;
        }

        InputLayer.ReleaseMouseCapture();
        _dragRotate = false;
        _dragScale = false;

        if (_draggingWatermark != null)
        {
            _draggingWatermark = null;
            _history.Disarm();
            _dragMoved = false;
            return;
        }

        if (_erasing)
        {
            _erasing = false;
            _gesture = null;
            Editor.LiveEraser = null;
            if (_eraserPath != null)
            {
                // The state before the stroke becomes the undo step, but only if it erased something.
                var before = Editor.Items.Select(item => item.Clone()).ToList();
                if (AnnotationOps.BindEraser(Editor.Items, _eraserPath.Points, _eraserPath.Thickness, Toolbar.EraserOpacity))
                {
                    _history.Checkpoint(before);
                    UpdateUndoButtons();
                }
            }

            _eraserPath = null;
            Editor.InvalidateVisual();
            return;
        }

        if (_resizingAnnotation || _rotatingAnnotation)
        {
            _resizingAnnotation = false;
            _rotatingAnnotation = false;
            _history.Disarm();
            return;
        }

        if (_movingSelected)
        {
            _movingSelected = false;
            _history.Disarm();
            var clicked = _pendingTextClick && !_dragMoved;
            _pendingTextClick = false;
            _dragMoved = false;
            if (clicked && _selected is TextAnnotation selectedText)
            {
                BeginTextEdit(selectedText, isNew: false, caretAt: ToPixels(e.GetPosition(InputLayer)));
            }

            return;
        }

        if (_active != null)
        {
            CommitAnnotation();
        }
    }

    private void StartAnnotation(Point point)
    {
        ClearSelection();
        _active = AnnotationOps.Create(_tool, point, Toolbar, _number, _baseImage);
        if (_active == null)
        {
            return;
        }

        _gesture = new CreationGesture(_active, point);
        _gesture.Apply();
        Editor.Preview = _active;
        Editor.InvalidateVisual();
    }

    private void CommitAnnotation()
    {
        var annotation = _active;
        _active = null;
        _gesture = null;
        Editor.Preview = null;

        if (annotation == null || AnnotationOps.IsTooSmall(annotation))
        {
            Editor.InvalidateVisual();
            return;
        }

        if (annotation is NumberAnnotation)
        {
            _number++;
        }

        _history.Checkpoint(Editor.Items);
        Editor.Items.Add(annotation);
        UpdateUndoButtons();
        // The new element stays selected (wheel / Ctrl+wheel / frame adjust it right away);
        // freehand strokes don't — the next stroke usually follows immediately.
        if (annotation is not PenAnnotation and not MosaicBrushAnnotation)
        {
            SelectQuietly(annotation);
            _freshSelection = annotation;
        }
        else
        {
            Editor.InvalidateVisual();
        }
    }

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

    private void OnTextEditEnded(TextAnnotation text, bool removed)
    {
        if (_textEditor.Dirty && !(removed && _textEditor.IsNew))
        {
            _history.CommitArmed();
        }
        else
        {
            _history.Disarm();
        }

        UpdateUndoButtons();
        if (!removed && Editor.Items.Contains(text))
        {
            // Keep the finished text selected so its frame can be adjusted right away.
            SelectQuietly(text);
        }

        if (_editMode)
        {
            InputLayer.Focus();
        }
    }

    /// <summary>Selects an element without switching tools (the text tool keeps working).</summary>
    private void SelectQuietly(Annotation annotation)
    {
        _selected = annotation;
        Editor.Selected = annotation;
        Toolbar.SyncFrom(annotation);
        ClearAnnotationHover();
        Editor.InvalidateVisual();
    }

    private void BeginTextPress(TextAnnotation text, Point point, UIElement capture)
    {
        _textPress = text;
        _textPressPoint = point;
        _textPressMoved = false;
        _lastPoint = point;
        _textPressCapture = capture;
        _history.Arm(Editor.Items);
        capture.CaptureMouse();
    }

    /// <returns>True while a text press is in progress (the move is consumed).</returns>
    private bool UpdateTextPress(Point point)
    {
        if (_textPress == null)
        {
            return false;
        }

        if (!_textPressMoved && (point - _textPressPoint).Length * _zoom < 3)
        {
            return true;
        }

        if (!_textPressMoved)
        {
            _textPressMoved = true;
            _history.CommitArmed();
            UpdateUndoButtons();
            SelectQuietly(_textPress);
        }

        _textPress.Translate(point.X - _lastPoint.X, point.Y - _lastPoint.Y);
        _lastPoint = point;
        Editor.InvalidateVisual();
        return true;
    }

    private bool EndTextPress(Point point, bool selectAll)
    {
        if (_textPress is not { } pressed)
        {
            return false;
        }

        _textPress = null;
        _textPressCapture?.ReleaseMouseCapture();
        _textPressCapture = null;
        if (!_textPressMoved)
        {
            _history.Disarm();
            BeginTextEdit(pressed, isNew: false, caretAt: selectAll ? null : point, selectAll: selectAll);
        }

        return true;
    }

    private void BeginWatermarkEdit(WatermarkAnnotation watermark)
    {
        _editingWatermark = watermark;
        WatermarkEditor.Text = watermark.Text;
        WatermarkEditor.Visibility = Visibility.Visible;
        Canvas.SetLeft(WatermarkEditor, watermark.Rect.X * _zoom + 4);
        Canvas.SetTop(WatermarkEditor, watermark.Rect.Y * _zoom + 4);
        WatermarkEditor.Focus();
        Keyboard.Focus(WatermarkEditor);
        WatermarkEditor.SelectAll();
    }

    private void CommitWatermark()
    {
        if (_editingWatermark is not { } watermark)
        {
            return;
        }

        _editingWatermark = null;
        var text = WatermarkEditor.Text.Trim();
        WatermarkEditor.Visibility = Visibility.Collapsed;
        if (text.Length > 0 && text != watermark.Text)
        {
            _history.Checkpoint(Editor.Items);
            watermark.Text = text;
            UpdateUndoButtons();
            Editor.InvalidateVisual();
        }

        InputLayer.Focus();
    }

    private void WatermarkEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _editingWatermark = null;
            WatermarkEditor.Visibility = Visibility.Collapsed;
            InputLayer.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CommitWatermark();
            e.Handled = true;
        }
    }

    private Annotation? HitAnnotation(Point point) => AnnotationOps.HitTest(Editor.Items, point, 7 / _zoom);

    private void SelectAnnotation(Annotation annotation)
    {
        _selected = annotation;
        _tool = AnnotationTool.None;
        Toolbar.SetTool(AnnotationTool.None);
        Toolbar.SetSelection(annotation);
        Toolbar.SetColor(annotation.Color);
        var size = annotation switch
        {
            TextAnnotation text => text.FontSize,
            WatermarkAnnotation watermark => watermark.FontSize,
            NumberAnnotation number => number.Radius,
            _ => annotation.Thickness
        };
        Toolbar.SetThickness(size);
        Toolbar.SetFill(annotation.Fill);
        Toolbar.SetCorner(annotation.CornerRadius);
        if (annotation is TextAnnotation selectedText)
        {
            Toolbar.SetStroke(selectedText.StrokeThickness > 0, selectedText.StrokeColor, selectedText.StrokeThickness);
        }

        _color = annotation.Color;
        _thickness = size;
        _fill = annotation.Fill;
        _corner = annotation.CornerRadius;
        InputLayer.IsHitTestVisible = false;
        InputLayer.Cursor = Cursors.Arrow;
        ImageHost.Cursor = Cursors.Arrow;
        ClearAnnotationHover();
        Editor.Selected = annotation;
        Editor.InvalidateVisual();
    }

    private bool TryBeginAnnotationTransform(Point point)
    {
        if (_selected == null)
        {
            return false;
        }

        var hit = AnnotationEditor.HitFrame(_selected, point, 1 / _zoom);
        var bounds = _selected.Bounds;
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        _resizingAnnotation = false;
        _rotatingAnnotation = false;
        _annotationCenter = center;
        _annotationStartBounds = bounds;
        _lastPoint = point;
        _transformPress = point;
        _transformStarted = false;

        if (hit.IsHandle)
        {
            _resizingAnnotation = true;
            _annotationHandle = hit.Handle;
            _resizeGrab = AnnotationOps.ResizeGrab(bounds, _selected.Rotation, hit.Handle, point);
            return true;
        }

        if (hit.Rotate)
        {
            _rotatingAnnotation = true;
            _annotationStartRotation = _selected.Rotation;
            _annotationRotateStartAngle = Math.Atan2(point.Y - center.Y, point.X - center.X) * 180 / Math.PI;
            return true;
        }

        if (hit.Inside && AnnotationOps.InsideMoves(_selected, _tool, point, 7 / _zoom))
        {
            _movingSelected = true;
            return true;
        }

        return false;
    }

    private void ClearSelection()
    {
        _freshSelection = null;
        _selected = null;
        Editor.Selected = null;
        _resizingAnnotation = false;
        _rotatingAnnotation = false;
        if (Toolbar != null)
        {
            Toolbar.SetSelection(null);
        }

        ImageHost.Cursor = Cursors.Arrow;
        ClearAnnotationHover();
        Editor.InvalidateVisual();
    }

    private void UpdateCursor(Point point)
    {
        if (_tool is AnnotationTool.Select or AnnotationTool.None)
        {
            InputLayer.Cursor = HitAnnotation(point) != null ? Cursors.SizeAll : Cursors.Arrow;
        }
        else
        {
            InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color, CursorScale);
        }
    }

    private void UpdateAnnotationHover(Point point)
    {
        if (_tool is AnnotationTool.None or AnnotationTool.Select or AnnotationTool.Eraser)
        {
            ClearAnnotationHover();
            return;
        }

        var hit = HitAnnotation(point);
        if (hit != null && SameFamily(hit, _tool))
        {
            var bounds = hit.Bounds;
            bounds.Inflate(4 / (1.0 / _zoom), 4 / (1.0 / _zoom));
            Editor.HoverBounds = bounds;
            Editor.HoverLabel = _tool == AnnotationTool.Text ? "单击编辑 · 拖动移动" : "点击选中并继续编辑";
            Editor.ShowHover = true;
            InputLayer.Cursor = _tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Hand;
            Editor.InvalidateVisual();
            return;
        }

        ClearAnnotationHover();
        InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color, CursorScale);
    }

    private void ClearAnnotationHover()
    {
        if (!Editor.ShowHover && Editor.HoverBounds == Rect.Empty)
        {
            return;
        }

        Editor.ShowHover = false;
        Editor.HoverBounds = Rect.Empty;
        Editor.HoverLabel = null;
        Editor.InvalidateVisual();
    }

    private static bool SameFamily(Annotation annotation, AnnotationTool tool) => tool switch
    {
        AnnotationTool.Rectangle or AnnotationTool.Ellipse =>
            annotation is RectAnnotation or EllipseAnnotation,
        AnnotationTool.Line or AnnotationTool.Arrow =>
            annotation is LineAnnotation or ArrowAnnotation,
        AnnotationTool.Pen or AnnotationTool.Highlighter => false,
        AnnotationTool.Mosaic or AnnotationTool.Watermark =>
            annotation is MosaicAnnotation or WatermarkAnnotation,
        AnnotationTool.Text => annotation is TextAnnotation,
        AnnotationTool.Number => annotation is NumberAnnotation,
        _ => false
    };

    private bool UpdateAnnotationTransformCursor(Point point)
    {
        if (_selected == null)
        {
            return false;
        }

        var hit = AnnotationOps.SelectionHit(_selected, IsFreshSelection, _tool, point, 1 / _zoom);
        if (hit.IsHandle)
        {
            ImageHost.Cursor = AnnotationEditor.ResizeCursor(hit.Handle, _selected.Rotation);
            return true;
        }

        if (hit.Rotate)
        {
            ImageHost.Cursor = ToolCursors.GetRotationCursor(true);
            return true;
        }

        if (hit.Inside && _tool is AnnotationTool.None or AnnotationTool.Select)
        {
            ImageHost.Cursor = Cursors.SizeAll;
            return true;
        }

        return false;
    }

    private void AdjustActiveToolByWheel(int delta)
    {
        var step = Math.Sign(delta) * (_tool is AnnotationTool.Text or AnnotationTool.Number ? 2 : 1);
        var value = _tool switch
        {
            AnnotationTool.Text => Math.Clamp(_thickness + step, 8, 64),
            AnnotationTool.Number => Math.Clamp(_thickness + step, 8, 40),
            AnnotationTool.MosaicBrush => Math.Clamp(_thickness + step * 2, 4, 80),
            AnnotationTool.Watermark => Math.Clamp(_thickness + step * 2, 10, 120),
            AnnotationTool.Eraser => Math.Clamp(_thickness + step * 2, 4, 80),
            _ => Math.Clamp(_thickness + step * 0.5, 1, 16)
        };

        _thickness = value;
        Toolbar.SetThickness(value);
        InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color, CursorScale);
    }

    private void AdjustSelectedByWheel(int delta, bool opacity)
    {
        if (_selected == null)
        {
            return;
        }

        _history.CheckpointCoalesced(Editor.Items, (opacity ? "opacity" : "wheel", _selected));
        UpdateUndoButtons();
        if (opacity)
        {
            if (_selected is WatermarkAnnotation)
            {
                _selected.Opacity = Math.Clamp(Math.Round(_selected.Opacity + Math.Sign(delta) * 0.05, 2), 0.05, 1);
                Toolbar.SyncFrom(_selected);
            }
            else
            {
                _selected.Opacity = AnnotationOps.WheelOpacity(_selected.Opacity, delta);
                Toolbar.SetOpacity(_selected.Opacity);
            }
        }
        else if (AnnotationOps.WheelSize(_selected, delta, _baseImage) is { } size)
        {
            _thickness = size;
            Toolbar.SetThickness(size);
        }
        else
        {
            Toolbar.SyncFrom(_selected);
        }

        Editor.InvalidateVisual();
    }

    private static bool IsTextBypassHeld()
    {
        var setting = App.Config.TextBypassModifier ?? "Alt";
        var modifiers = Keyboard.Modifiers;
        return (setting.Contains("Alt", StringComparison.OrdinalIgnoreCase) && (modifiers & ModifierKeys.Alt) != 0)
               || (setting.Contains("Ctrl", StringComparison.OrdinalIgnoreCase) && (modifiers & ModifierKeys.Control) != 0)
               || (setting.Contains("Shift", StringComparison.OrdinalIgnoreCase) && (modifiers & ModifierKeys.Shift) != 0);
    }

    private void FlipPinImage(bool horizontal) => ApplyOrientation(horizontal ? 'H' : 'V', record: true);

    private void RotatePinImageCounterClockwise() => ApplyOrientation('R', record: true);

    /// <summary>Flips ('H', 'V') or rotates 90° counter-clockwise ('R') the image under the annotations.</summary>
    private void ApplyOrientation(char op, bool record)
    {
        var width = _baseImage.PixelWidth;
        var height = _baseImage.PixelHeight;
        switch (op)
        {
            case 'H':
                ApplyPinImageTransform(new ScaleTransform(-1, 1), p => new Point(width - p.X, p.Y), width, height, op);
                break;
            case 'V':
                ApplyPinImageTransform(new ScaleTransform(1, -1), p => new Point(p.X, height - p.Y), width, height, op);
                break;
            default:
            {
                // Turn about the pin's centre: move the top-left so the centre stays where it is.
                var oldWidth = width * _zoom;
                var oldHeight = height * _zoom;
                _left += (int)Math.Round((oldWidth - oldHeight) / 2);
                _top += (int)Math.Round((oldHeight - oldWidth) / 2);
                ApplyPinImageTransform(new RotateTransform(-90), p => new Point(p.Y, width - p.X), height, width, op);
                break;
            }
        }

        if (record)
        {
            _orientation.Add(op);
        }

        AnimateOrientation(op);
    }

    /// <summary>
    /// A quick turn after a flip / rotation: the new image starts where the old one was
    /// (mirrored, or turned back 90°) and swings into place (240 ms, shared curve).
    /// </summary>
    private void AnimateOrientation(char op)
    {
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        ImageHost.RenderTransformOrigin = new Point(0.5, 0.5);
        ImageHost.RenderTransform = new TransformGroup { Children = { scale, rotate } };
        var duration = new Duration(TimeSpan.FromMilliseconds(150));
        switch (op)
        {
            case 'H':
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(-1, 1, duration) { EasingFunction = Motion.Ease });
                break;
            case 'V':
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(-1, 1, duration) { EasingFunction = Motion.Ease });
                break;
            default:
                rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(90, 0, duration) { EasingFunction = Motion.Ease });
                break;
        }
    }

    /// <summary>
    /// A slanted band of light crosses the pin once, left to right (fast), clipped to its outline.
    /// </summary>
    private void PlayLightSweep()
    {
        var width = ImageHost.ActualWidth;
        var height = ImageHost.ActualHeight;
        if (width < 8 || height < 8)
        {
            return;
        }

        const double tilt = 22; // degrees from vertical
        var bandWidth = Math.Clamp(width * 0.18, 26, 120);
        var bandHeight = height / Math.Cos(tilt * Math.PI / 180) + bandWidth * 2;
        var band = new System.Windows.Shapes.Rectangle
        {
            Width = bandWidth,
            Height = bandHeight,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0, 255, 255, 255), 0),
                    new(Color.FromArgb(120, 255, 255, 255), 0.5),
                    new(Color.FromArgb(0, 255, 255, 255), 1)
                },
                new Point(0, 0.5), new Point(1, 0.5))
        };
        var move = new TranslateTransform();
        band.RenderTransform = new TransformGroup
        {
            Children = { new RotateTransform(tilt, bandWidth / 2, bandHeight / 2), move }
        };
        var layer = new Canvas
        {
            IsHitTestVisible = false,
            Width = width,
            Height = height,
            // Never outside the pin (rounded corners included).
            Clip = new RectangleGeometry(new Rect(0, 0, width, height), CornerRadius * _zoom, CornerRadius * _zoom)
        };
        if (ShapedShadow && Img.Source is { } picture)
        {
            // A freehand (lasso) pin: the light only crosses the cut-out shape, not its bounding box.
            layer.OpacityMask = new ImageBrush(picture) { Stretch = Stretch.Fill };
        }
        Canvas.SetTop(band, (height - bandHeight) / 2);
        layer.Children.Add(band);
        ImageHost.Children.Add(layer);

        var reach = bandHeight * Math.Sin(tilt * Math.PI / 180) / 2 + bandWidth;
        var sweep = new DoubleAnimation(-reach, width + reach, TimeSpan.FromMilliseconds(Math.Clamp(200 + width * 0.12, 220, 340)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        sweep.Completed += (_, _) => ImageHost.Children.Remove(layer);
        move.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private async void TranslateInPlace()
    {
        if (_translateCts != null)
        {
            return;
        }

        if (_editMode)
        {
            ExitEditMode(true);
        }

        var original = _baseImage;
        var cts = new CancellationTokenSource();
        _translateCts = cts;
        ShowPinStatus("正在识别文字…", hideAfter: null);
        var progress = new Progress<ImmersiveTranslator.Update>(update =>
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(_translateCts, cts))
            {
                return;
            }

            if (update.Translated > 0)
            {
                _untranslatedBase ??= original;
                SetBaseImage(update.Image);
            }

            ShowPinStatus(update.Status, hideAfter: update.Done ? TimeSpan.FromSeconds(2.5) : null);
        });

        try
        {
            await ImmersiveTranslator.TranslateAsync(original, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("贴图原位翻译失败", ex);
            if (ReferenceEquals(_translateCts, cts))
            {
                ShowPinStatus("翻译失败：" + ex.Message, hideAfter: TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            if (ReferenceEquals(_translateCts, cts))
            {
                _translateCts = null;
            }

            cts.Dispose();
        }
    }

    private void ShowOriginal()
    {
        if (_untranslatedBase == null)
        {
            return;
        }

        if (_editMode)
        {
            ExitEditMode(true);
        }

        SetBaseImage(_untranslatedBase);
        _untranslatedBase = null;
        ShowPinStatus("已显示原文", hideAfter: TimeSpan.FromSeconds(1.5));
    }

    private void CancelInPlaceTranslation()
    {
        _translateCts?.Cancel();
        _translateCts = null;
        _untranslatedBase = null;
    }


    /// <summary>Swaps the image under the annotations and re-composes what the pin shows.</summary>
    private void SetBaseImage(BitmapSource image)
    {
        _baseImage = image;
        Editor.Source = image;
        var composed = ComposeEditedImage();
        SourceImage = composed;
        Img.Source = composed;
        if (!_editMode)
        {
            RefreshTextLayer();
        }
    }

    private void ShowPinStatus(string text, TimeSpan? hideAfter)
    {
        PinStatusText.Text = text;
        PinStatus.Visibility = Visibility.Visible;
        _statusTimer?.Stop();
        if (hideAfter is { } delay)
        {
            _statusTimer ??= new DispatcherTimer();
            _statusTimer.Interval = delay;
            _statusTimer.Tick -= HidePinStatus;
            _statusTimer.Tick += HidePinStatus;
            _statusTimer.Start();
        }
    }

    private void HidePinStatus(object? sender, EventArgs e)
    {
        _statusTimer?.Stop();
        PinStatus.Visibility = Visibility.Collapsed;
    }

    /// <summary>F: back to 100%, opaque and the original orientation. Annotations are kept.</summary>
    private void ResetPinImageTransform()
    {
        for (var i = _orientation.Count - 1; i >= 0; i--)
        {
            switch (_orientation[i])
            {
                case 'R':
                    // Undo a 90° counter-clockwise turn with three more.
                    ApplyOrientation('R', record: false);
                    ApplyOrientation('R', record: false);
                    ApplyOrientation('R', record: false);
                    break;
                default:
                    ApplyOrientation(_orientation[i], record: false); // flips undo themselves
                    break;
            }
        }

        _orientation.Clear();
        ResetView();
    }

    /// <summary>Removes every annotation from the pin (undoable while editing).</summary>
    private void ClearAnnotations()
    {
        if (Editor.Items.Count == 0)
        {
            return;
        }

        _textEditor.Commit();
        if (_editMode)
        {
            _history.Checkpoint(Editor.Items);
        }

        Editor.Items.Clear();
        ClearSelection();
        UpdateUndoButtons();
        Editor.InvalidateVisual();
        if (!_editMode)
        {
            SourceImage = _baseImage;
            Img.Source = _baseImage;
            RefreshTextLayer();
        }
    }

    private void ApplyPinImageTransform(Transform transform, Func<Point, Point> map, int newWidth, int newHeight, char op)
    {
        CancelInPlaceTranslation();
        _textEditor.Commit();
        // Transform the image under the annotations (not the composed one) and map
        // every element, so re-editing never shows the annotations twice.
        var transformed = new TransformedBitmap(_baseImage, transform);
        transformed.Freeze();

        foreach (var annotation in Editor.Items)
        {
            AnnotationOps.ReorientMasks(annotation, op);
            if (op is 'H' or 'V')
            {
                annotation.Rotation = -annotation.Rotation; // a mirrored rotated box turns the other way
            }

            switch (annotation)
            {
                case RectAnnotation rect:
                    rect.Rect = MapRect(rect.Rect, map);
                    break;
                case EllipseAnnotation ellipse:
                    ellipse.Rect = MapRect(ellipse.Rect, map);
                    break;
                case LineAnnotation line:
                    line.Start = map(line.Start);
                    line.End = map(line.End);
                    break;
                case ArrowAnnotation arrow:
                    arrow.Start = map(arrow.Start);
                    arrow.End = map(arrow.End);
                    break;
                case PenAnnotation pen:
                    for (var i = 0; i < pen.Points.Count; i++)
                    {
                        pen.Points[i] = map(pen.Points[i]);
                    }
                    break;
                case TextAnnotation text:
                {
                    // Text stays readable: move its centre, do not mirror the glyphs.
                    var bounds = text.Bounds;
                    var center = map(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
                    text.Translate(center.X - (bounds.X + bounds.Width / 2), center.Y - (bounds.Y + bounds.Height / 2));
                    break;
                }
                case NumberAnnotation number:
                    number.Center = map(number.Center);
                    break;
                case WatermarkAnnotation watermark:
                    watermark.Rect = new Rect(0, 0, newWidth, newHeight);
                    break;
                case MosaicAnnotation mosaic:
                    mosaic.Rect = MapRect(mosaic.Rect, map);
                    MosaicBuilder.Rebuild(mosaic, transformed);
                    break;
                case MosaicBrushAnnotation brush:
                    for (var i = 0; i < brush.Points.Count; i++)
                    {
                        brush.Points[i] = map(brush.Points[i]);
                    }

                    MosaicBuilder.Rebuild(brush, transformed);
                    break;
            }
        }

        _baseImage = transformed;
        _pixelWidth = newWidth;
        _pixelHeight = newHeight;
        Editor.Source = transformed;
        SourceImage = ComposeEditedImage();
        Img.Source = _editMode ? transformed : SourceImage;
        if (!_editMode)
        {
            RefreshTextLayer();
        }
        _selected = null;
        Editor.Selected = null;
        Toolbar.SetSelection(null);
        Editor.InvalidateVisual();
        ApplyBounds();
    }

    private static Rect MapRect(Rect rect, Func<Point, Point> map)
    {
        var a = map(rect.TopLeft);
        var b = map(rect.BottomRight);
        return new Rect(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
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

    private void BuildContextMenu()
    {
        // Only the entries that do something in the current state.
        var menu = new ContextMenu();
        void Add(string header, Action action, bool? isChecked = null, string? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            if (isChecked is { } value)
            {
                item.IsCheckable = true;
                item.IsChecked = value;
            }

            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        void Separator()
        {
            if (menu.Items.Count > 0 && menu.Items[^1] is not System.Windows.Controls.Separator)
            {
                menu.Items.Add(new Separator());
            }
        }

        if (TextLayer.HasSelection)
        {
            Add("复制选中文字", () => ClipboardService.CopyText(TextLayer.SelectedText), gesture: "Ctrl+C");
        }

        Add("复制图片", Copy, gesture: TextLayer.HasSelection ? null : "Ctrl+C");
        Add("另存为…", Save, gesture: "Ctrl+S");
        Separator();

        if (_translateCts != null)
        {
            menu.Items.Add(new MenuItem { Header = "正在原位翻译…", IsEnabled = false });
        }
        else if (_untranslatedBase != null)
        {
            Add("显示原文", ShowOriginal);
        }
        else
        {
            Add("原位翻译", TranslateInPlace);
        }

        Add("文字识别 / 翻译…", () => ((App)Application.Current).TranslateImage(CurrentImage, this));
        Separator();

        Add(_editMode ? "完成标注" : "编辑标注", ToggleEditing, gesture: "Space");
        if (Editor.Items.Count > 0)
        {
            Add("清除批注内容", ClearAnnotations);
        }

        Separator();
        Add("置顶", () =>
        {
            Topmost = !Topmost;
            ApplyBounds();
        }, Topmost, "T");
        Add("锁定", () =>
        {
            _locked = !_locked;
            UpdateFrame();
        }, _locked, "L");
        Add("鼠标穿透", () => SetClickThrough(!_clickThrough), _clickThrough);
        if (Math.Abs(_zoom - 1) > 0.001 || _opacity < 0.999 || _orientation.Count > 0)
        {
            Add("重置大小和方向", ResetPinImageTransform, gesture: "F");
        }

        Separator();
        Add("设置…", () => ((App)Application.Current).ShowSettings());
        if (((App)Application.Current).PinCount > 1)
        {
            Add("关闭所有贴图", () => ((App)Application.Current).CloseAllPins());
        }

        Add("关闭", Close, gesture: "Esc");
        ContextMenu = menu;
    }

    public void SetClickThrough(bool enabled)
    {
        if (_clickThrough == enabled)
        {
            return;
        }

        _clickThrough = enabled;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            var style = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
            var updated = enabled
                ? style | Native.WS_EX_TRANSPARENT
                : style & ~(long)Native.WS_EX_TRANSPARENT;

            Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, new IntPtr(updated));
            Native.SetWindowPos(
                handle,
                Topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
                0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        }

        UpdateFrame();
    }

    private void Copy()
    {
        ClipboardService.CopyImage(CurrentImage);
    }

    private void Save()
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存贴图",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg",
            FileName = $"Markshot_pin_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        var initialDirectory = App.Config.ResolveSaveDirectory();
        if (Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        var wasTopmost = Topmost;
        Topmost = false;
        try
        {
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
        }
        finally
        {
            Topmost = wasTopmost;
        }

        try
        {
            BitmapEncoder encoder = Path.GetExtension(dialog.FileName).ToLowerInvariant() is ".jpg" or ".jpeg"
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(CurrentImage));
            using (var stream = File.Create(dialog.FileName))
            {
                encoder.Save(stream);
            }

            ((App)Application.Current).ShowSavedPrompt(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error("保存贴图失败", ex);
            MessageBox.Show(this, "保存失败：" + ex.Message, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
