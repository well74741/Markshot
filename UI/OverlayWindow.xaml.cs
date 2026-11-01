using System.Windows.Interop;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

public enum CaptureAction
{
    None,
    Copy,
    Save,
    Pin,
    Translate
}

public partial class OverlayWindow : Window
{
    private enum DragMode
    {
        None,
        Creating,
        Moving,
        Resizing
    }

    private readonly BitmapSource _screen;
    private readonly Int32Rect _bounds;
    private readonly bool _demo;

    private readonly AnnotationHistory _history = new();
    private readonly InlineTextEditor _textEditor;

    private double _scale = 1.0;
    private Rect _selection = Rect.Empty;
    private bool _hasSelection;
    private bool _closed;

    private DragMode _mode;
    private Point _dragStart;
    private Rect _dragOrigin = Rect.Empty;
    private int _handle = -1;
    private bool _dragging;

    private AnnotationTool _tool = AnnotationTool.Select;
    private Annotation? _active;
    private int _numberCounter = 1;

    private Color _color = Color.FromRgb(0xFF, 0x33, 0x00);
    private double _thickness = 3;
    private bool _fill;
    private double _corner;
    private bool _autoTranslate;

    private WatermarkAnnotation? _editingWatermark;

    // Text tool press on an existing text: a click edits it, a drag moves it.
    private TextAnnotation? _textPress;

    // Press inside an already selected text: a click (no drag) opens it for editing.
    private bool _pendingTextClick;
    private bool _dragMoved;
    private Point _textPressPoint;
    private bool _textPressMoved;

    private readonly byte[] _pixelBuffer = new byte[4];
    private WindowTarget? _hoverTarget;
    private Rect _hoverLocal = Rect.Empty;
    private Rect _pendingWindow = Rect.Empty;
    private bool _preferChild;
    private bool _hexColor = true;
    private int _recentRegionIndex = -1;

    private Annotation? _selectedAnnotation;
    private bool _draggingAnnotation;
    private Point _lastAnnotationPoint;
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
    private readonly DispatcherTimer _noticeTimer;
    private bool _spaceDrag;
    private Point _spaceAnchor;
    private Rect _spaceSelectionOrigin;
    private Point _spaceDragStartOrigin;
    private bool _suppressContextMenu;

    // In-place translation of the selection (Chrome-style).
    private readonly DispatcherTimer _translateDebounce;
    private CancellationTokenSource? _translateCts;
    private BitmapSource? _translated;
    private Rect _translatedRect = Rect.Empty;
    private Rect _translatingRect = Rect.Empty;
    private bool _showOriginal;
    // Languages shown in the translate bar: detected source and the target
    // (the configured one, or English when the text already is Chinese).
    private string? _sourceLabel;
    private string? _targetLabel;
    private BitmapSource? _effectiveScreen;

    public CaptureAction Result { get; private set; } = CaptureAction.None;

    public BitmapSource? OutputImage { get; private set; }

    public Rect SelectionScreenRect { get; private set; }

    /// <summary>For pins: the selection without annotations (translation included).</summary>
    public BitmapSource? PinBaseImage { get; private set; }

    /// <summary>For pins: the annotations in pin coordinates, still editable there.</summary>
    public List<Annotation>? PinAnnotations { get; private set; }

    public OverlayWindow(BitmapSource screen, Int32Rect bounds, bool demo = false)
    {
        InitializeComponent();
        Motion.PopOnShow(Toolbar);
        Motion.PopOnShow(TranslateStatus);
        Motion.FadeOnShow(TranslatedImage);

        _screen = screen;
        _bounds = bounds;
        _demo = demo;

        ScreenImage.Source = screen;
        BrightImage.Source = screen;
        Editor.Source = screen;
        Magnifier.Source = screen;

        InitToolbar();
        ContextMenuOpening += (_, args) =>
        {
            if (_suppressContextMenu)
            {
                _suppressContextMenu = false;
                args.Handled = true;
            }
        };

        _translateDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _translateDebounce.Tick += (_, _) =>
        {
            _translateDebounce.Stop();
            StartTranslation();
        };

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            Magnifier.Notice = null;
            Magnifier.InvalidateVisual();
        };

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) =>
        {
            UpdateScale();
            UpdateSelectionVisuals();
        };
        // Shortcuts (letters, Space) must not be eaten by a Chinese IME; only the
        // text boxes take IME input.
        InputMethod.SetIsInputMethodEnabled(this, false);
        InputMethod.SetIsInputMethodEnabled(TextEditor, true);
        InputMethod.SetIsInputMethodEnabled(WatermarkEditor, true);

        _textEditor = new InlineTextEditor(TextEditor, Editor);
        _textEditor.Ended += OnTextEditEnded;
        _textEditor.StyleChanged += text => Toolbar.SyncFrom(text);
        WatermarkEditor.LostKeyboardFocus += (_, _) =>
        {
            if (_editingWatermark != null && !_closed)
            {
                CommitWatermark();
            }
        };
    }

    private void InitToolbar()
    {
        Toolbar.SetMode(AnnotationToolbarMode.Capture);
        Toolbar.ToolChanged += OnToolbarToolChanged;
        Toolbar.VariantChanged += OnToolbarVariantChanged;
        Toolbar.SettingsChanged += ApplyToolbarSettings;
        Toolbar.UndoRequested += Undo;
        Toolbar.RedoRequested += Redo;
        Toolbar.CopyRequested += CopyAndClose;
        Toolbar.SaveRequested += SaveAndClose;
        Toolbar.PinRequested += PinAndClose;
        Toolbar.TranslateRequested += TranslateAndClose;
        Toolbar.OcrRequested += OcrAndClose;
        _autoTranslate = App.Config.ImmersiveTranslate;
        Toolbar.SetTranslateEnabled(_autoTranslate);
        Toolbar.TranslateToggled += enabled =>
        {
            _autoTranslate = enabled;
            _showOriginal = false;
            App.Config.ImmersiveTranslate = enabled;
            App.Config.Save();
            if (enabled)
            {
                StartTranslation();
            }
            else
            {
                CancelTranslation();
                UpdateTranslationVisuals();
            }
        };
        Toolbar.CloseRequested += Close;
        Toolbar.SizeChanged += (_, _) =>
        {
            if (!_closed && Toolbar.Visibility == Visibility.Visible)
            {
                PositionToolbar();
            }
        };

        _tool = AnnotationTool.Select;
        Toolbar.SetTool(AnnotationTool.Select);
        Toolbar.SetSelection(null);
    }

    private void OnToolbarToolChanged(AnnotationTool tool)
    {
        _textEditor.Commit();
        _tool = tool;
        _thickness = Toolbar.Thickness;
        ClearAnnotationSelection();
        ClearAnnotationHover();
        Toolbar.SetSelection(null);
        ActivateWatermarkIfNeeded();
        UpdateCursorToPointer();
    }

    private void OnToolbarVariantChanged(AnnotationTool tool)
    {
        if (_tool == AnnotationTool.Select && _selectedAnnotation != null)
        {
            ConvertSelected(tool);
            return;
        }

        _tool = tool;
        _thickness = Toolbar.Thickness;
        ActivateWatermarkIfNeeded();
        UpdateCursorToPointer();
    }

    /// <summary>The watermark tool works on the canvas-wide watermark, creating it on first use.</summary>
    private void ActivateWatermarkIfNeeded()
    {
        if (_tool != AnnotationTool.Watermark || !_hasSelection)
        {
            Editor.HoverWatermark = null;
            return;
        }

        var watermark = AnnotationOps.EnsureWatermark(Editor.Items, SnapRect(_selection), Toolbar, _history);
        Toolbar.SyncFrom(watermark);
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void ApplyToolbarSettings()
    {
        _color = Toolbar.Color;
        _thickness = Toolbar.Thickness;
        _fill = Toolbar.FillEnabled;
        _corner = Toolbar.CornerRadius;
        UpdateCursorToPointer();

        if (_textEditor.Target is { } editing)
        {
            AnnotationStyler.Apply(editing, Toolbar, EffectiveScreen);
            _textEditor.MarkDirty();
            _textEditor.Refresh();
            return;
        }

        if (_tool == AnnotationTool.Watermark && AnnotationOps.FindWatermark(Editor.Items) is { } watermark)
        {
            _history.CheckpointCoalesced(Editor.Items, watermark);
            AnnotationStyler.Apply(watermark, Toolbar, EffectiveScreen);
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            return;
        }

        if (_selectedAnnotation == null)
        {
            return;
        }

        _history.CheckpointCoalesced(Editor.Items, _selectedAnnotation);
        AnnotationStyler.Apply(_selectedAnnotation, Toolbar, EffectiveScreen);
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void UpdateCursorToPointer()
    {
        if (_closed || _scale <= 0)
        {
            return;
        }

        UpdateCursor(ToPixels(Mouse.GetPosition(InputLayer)));
    }
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, new IntPtr(style | 0x00000080));
        ApplyPhysicalBounds();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyPhysicalBounds();
        UpdateScale();

        Activate();
        Focus();
        Keyboard.Focus(this);

        var handle = new WindowInteropHelper(this).Handle;
        Native.SetForegroundWindow(handle);

        PositionHint();

        if (_demo)
        {
            SetupDemo();
        }
        else
        {
            UpdateSelectionVisuals();
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyPhysicalBounds();
            UpdateScale();
            UpdateSelectionVisuals();
        }), DispatcherPriority.Loaded);
    }

    private void ApplyPhysicalBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        Native.SetWindowPos(
            handle,
            Native.HWND_TOPMOST,
            _bounds.X,
            _bounds.Y,
            _bounds.Width,
            _bounds.Height,
            Native.SWP_SHOWWINDOW);
    }

    private void UpdateScale()
    {
        if (Root.ActualWidth <= 1 || Root.ActualHeight <= 1)
        {
            return;
        }

        var scale = _screen.PixelWidth / Root.ActualWidth;
        if (scale <= 0 || double.IsNaN(scale))
        {
            return;
        }

        if (Math.Abs(scale - _scale) > 0.0001)
        {
            _scale = scale;
            Editor.Scale = scale;
            Editor.InvalidateVisual();
        }
    }

    private Point ToPixels(Point dip) => new(dip.X * _scale, dip.Y * _scale);

    private void UpdateSelectionVisuals()
    {
        if (_scale <= 0 || Root.ActualWidth <= 1)
        {
            return;
        }

        if (!_hasSelection || _selection.Width <= 0 || _selection.Height <= 0)
        {
            CancelTranslation();
            UpdateTranslationVisuals();
            BrightClip.Rect = new Rect(0, 0, 0, 0);
            Editor.Selection = Rect.Empty;
            Editor.ShowSelectionChrome = false;
            Editor.InvalidateVisual();
            Toolbar.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Visible;
            PositionHint();
            return;
        }

        BrightClip.Rect = new Rect(
            _selection.X / _scale - 1,
            _selection.Y / _scale - 1,
            _selection.Width / _scale + 2,
            _selection.Height / _scale + 2);

        Editor.Selection = _selection;
        Editor.ShowSelectionChrome = true;
        if (AnnotationOps.FindWatermark(Editor.Items) is { } watermark)
        {
            watermark.Rect = _selection;
        }

        Editor.InvalidateVisual();
        UpdateTranslationVisuals();
        if (_autoTranslate && !SelectionDragging && SnapRect(_selection) != _translatedRect &&
            SnapRect(_selection) != _translatingRect)
        {
            _translateDebounce.Stop();
            _translateDebounce.Start();
        }

        Hint.Visibility = Visibility.Collapsed;
        if (_mode is DragMode.Moving or DragMode.Resizing && _dragging)
        {
            Magnifier.Visibility = Visibility.Visible;
        }

        if (_dragging)
        {
            Toolbar.Visibility = Visibility.Collapsed;
        }
        else
        {
            Toolbar.Visibility = Visibility.Visible;
            PositionToolbar();
        }
    }

    private void PositionHint()
    {
        Hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(Hint, Math.Max(8, (ActualWidth - Hint.DesiredSize.Width) / 2));
        Canvas.SetTop(Hint, 28);
    }

    private void PositionToolbar()
    {
        if (Toolbar.Visibility != Visibility.Visible)
        {
            return;
        }

        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Toolbar.DesiredSize;

        var selection = new Rect(
            _selection.X / _scale,
            _selection.Y / _scale,
            _selection.Width / _scale,
            _selection.Height / _scale);

        var left = selection.Right - size.Width;
        if (left < 8)
        {
            left = selection.Left;
        }

        left = Math.Max(8, Math.Min(left, ActualWidth - size.Width - 8));

        var top = selection.Bottom + 8;
        if (top + size.Height > ActualHeight - 8)
        {
            top = selection.Top - size.Height - 8;
        }

        if (top < 8)
        {
            top = Math.Min(selection.Bottom + 8, ActualHeight - size.Height - 8);
        }

        Canvas.SetLeft(Toolbar, left);
        Canvas.SetTop(Toolbar, top);
    }

    private void SetupDemo()
    {
        var width = _screen.PixelWidth * 0.55;
        var height = _screen.PixelHeight * 0.5;
        _selection = new Rect(
            (_screen.PixelWidth - width) / 2,
            (_screen.PixelHeight - height) / 2,
            width,
            height);
        _hasSelection = true;

        var inner = new Rect(
            _selection.X + 70,
            _selection.Y + 70,
            _selection.Width * 0.42,
            _selection.Height * 0.32);

        Editor.Items.Add(new RectAnnotation
        {
            Rect = inner,
            Color = Color.FromRgb(0xFF, 0x33, 0x00),
            Thickness = 4
        });
        Editor.Items.Add(new ArrowAnnotation
        {
            Start = new Point(inner.Right - 20, inner.Bottom - 10),
            End = new Point(inner.Right + 130, inner.Bottom + 70),
            Color = Color.FromRgb(0xFF, 0x33, 0x00),
            Thickness = 4
        });
        Editor.Items.Add(new TextAnnotation
        {
            Position = new Point(inner.Left, inner.Bottom + 26),
            Text = "Markshot 批注预览",
            Color = Colors.White,
            FontSize = 24
        });

        var mosaicRect = new Rect(inner.Left, inner.Bottom + 84, 260, 78);
        var mosaic = new MosaicAnnotation { Rect = mosaicRect, BlockSize = 12 };
        MosaicBuilder.Rebuild(mosaic, EffectiveScreen);
        Editor.Items.Add(mosaic);

        Editor.Items.Add(new NumberAnnotation
        {
            Center = new Point(inner.Right + 14, inner.Top + 18),
            Number = 1,
            Color = Color.FromRgb(0xFF, 0x33, 0x00),
            Radius = 15
        });

        UpdateSelectionVisuals();
        Editor.InvalidateVisual();
        UpdateUndoButtons();
    }

    private void Input_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        var point = ToPixels(e.GetPosition(InputLayer));

        if (_textEditor.Target is { } editingText)
        {
            var frame = AnnotationEditor.HitFrame(editingText, point, _scale);
            _textEditor.Commit(); // a non-empty text stays selected (OnTextEditEnded)
            if ((frame.IsHandle || frame.Rotate) && ReferenceEquals(_selectedAnnotation, editingText))
            {
                // Grabbing a handle / edge / corner of the text being typed adjusts it like a shape.
                TryBeginAnnotationTransform(point);
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            // A click elsewhere only finishes typing — unless the text tool is
            // pointed at another text, which then opens for editing.
            if (!(_tool == AnnotationTool.Text && HitAnnotation(point) is TextAnnotation other &&
                  !ReferenceEquals(other, editingText)))
            {
                e.Handled = true;
                return;
            }
        }

        Focus();

        if (_editingWatermark != null)
        {
            CommitWatermark();
        }

        _dragStart = point;
        _dragOrigin = _selection;
        _dragging = true;
        _pendingWindow = Rect.Empty;

        if (e.ClickCount == 2 &&
            (_tool == AnnotationTool.None || _tool == AnnotationTool.Select) &&
            HitAnnotation(point) is TextAnnotation doubleClicked)
        {
            _dragging = false;
            _draggingAnnotation = false;
            InputLayer.ReleaseMouseCapture();
            BeginTextEdit(doubleClicked, isNew: false, caretAt: point);
            e.Handled = true;
            return;
        }

        // The adjustment frame of the selected element works with any tool:
        // handles/edges resize, the band outside the corners rotates.
        if (_selectedAnnotation != null && _hasSelection)
        {
            var frame = AnnotationEditor.HitFrame(_selectedAnnotation, point, _scale);
            if (frame.IsHandle || frame.Rotate)
            {
                TryBeginAnnotationTransform(point);
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (frame.Inside && _selectedAnnotation is TextAnnotation &&
                _tool is AnnotationTool.Select or AnnotationTool.None or AnnotationTool.Text)
            {
                // Drag moves the selected text; a plain click opens it for editing.
                TryBeginAnnotationTransform(point);
                _pendingTextClick = true;
                _dragMoved = false;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        if (_tool == AnnotationTool.Text && _hasSelection)
        {
            if (HitAnnotation(point) is TextAnnotation pressed)
            {
                _textPress = pressed;
                _textPressPoint = point;
                _textPressMoved = false;
                _lastAnnotationPoint = point;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (_selection.Contains(point))
            {
                // The mouse-up lands on the new text box, not on this layer.
                _dragging = false;
                BeginTextEdit(AnnotationStyler.NewText(point, Toolbar), isNew: true, caretAt: null);
                e.Handled = true;
                return;
            }
        }

        // Before "double-click = copy": a double-click on a watermark edits its text.
        if (e.ClickCount == 2 && _tool == AnnotationTool.Watermark &&
            AnnotationOps.FindWatermark(Editor.Items) is { } watermark && watermark.HitTest(point, 4 * _scale))
        {
            _dragging = false;
            _draggingAnnotation = false;
            InputLayer.ReleaseMouseCapture();
            BeginWatermarkEdit(watermark);
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2 &&
            (_tool == AnnotationTool.None || _tool == AnnotationTool.Select) &&
            _hasSelection && _selection.Contains(point))
        {
            CopyAndClose();
            e.Handled = true;
            return;
        }

        if (_tool is not AnnotationTool.None and not AnnotationTool.Select and not AnnotationTool.Eraser)
        {
            var candidate = HitAnnotation(point);
            if (candidate != null && SameFamily(candidate, _tool))
            {
                SelectAnnotation(candidate);
                ClearAnnotationHover();
                e.Handled = true;
                return;
            }
        }

        if (_selectedAnnotation != null && (_tool == AnnotationTool.Select || _tool == AnnotationTool.None) &&
            TryBeginAnnotationTransform(point))
        {
            _history.Arm(Editor.Items);
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == AnnotationTool.Select || _tool == AnnotationTool.None)
        {
            var hit = HitAnnotation(point);
            if (hit != null)
            {
                SelectAnnotation(hit);
                _draggingAnnotation = true;
                _dragMoved = false;
                _pendingTextClick = false;
                _lastAnnotationPoint = point;
                _annotationStartBounds = hit.Bounds;
                _annotationScaleStartPoint = point;
                _history.Arm(Editor.Items);
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            ClearAnnotationSelection();
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
            Editor.InvalidateVisual();
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == AnnotationTool.Watermark)
        {
            e.Handled = true;
            return;
        }

        if (_tool is not (AnnotationTool.None or AnnotationTool.Select or AnnotationTool.Eraser) &&
            _hasSelection && _selection.Contains(point))
        {
            StartAnnotation(point);
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_hasSelection)
        {
            var handle = HitHandle(point);
            if (handle >= 0)
            {
                _mode = DragMode.Resizing;
                _handle = handle;
                _recentRegionIndex = -1;
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (_selection.Contains(point))
            {
                _mode = DragMode.Moving;
                _recentRegionIndex = -1;
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        ClearAnnotationSelection();

        // Clicking a highlighted window selects it; dragging starts a manual selection.
        if (_hoverTarget != null && !_hasSelection)
        {
            _pendingWindow = _hoverLocal;
            _mode = DragMode.Creating;
            _handle = -1;
            _selection = Rect.Empty;
            _hasSelection = false;
            _recentRegionIndex = -1;
            Editor.Items.Clear();
            _history.Clear();
            ClearHover();
            UpdateUndoButtons();
            UpdateSelectionVisuals();
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        _mode = DragMode.Creating;
        _handle = -1;
        _pendingWindow = Rect.Empty;
        _recentRegionIndex = -1;
        _selection = new Rect(point, point);
        _hasSelection = false;
        Editor.Items.Clear();
        _history.Clear();
        ClearHover();
        UpdateUndoButtons();
        UpdateSelectionVisuals();
        InputLayer.CaptureMouse();
    }

    private void Input_MouseMove(object sender, MouseEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        var point = ToPixels(e.GetPosition(InputLayer));
        if (ShouldShowMagnifier(point))
        {
            UpdateMagnifier(point);
        }
        else
        {
            Magnifier.Visibility = Visibility.Collapsed;
        }


        if (_spaceDrag && _mode == DragMode.Creating)
        {
            var dx = point.X - _spaceAnchor.X;
            var dy = point.Y - _spaceAnchor.Y;
            var moved = new Rect(
                _spaceSelectionOrigin.X + dx,
                _spaceSelectionOrigin.Y + dy,
                _spaceSelectionOrigin.Width,
                _spaceSelectionOrigin.Height);

            moved.X = Math.Clamp(moved.X, 0, Math.Max(0, _screen.PixelWidth - moved.Width));
            moved.Y = Math.Clamp(moved.Y, 0, Math.Max(0, _screen.PixelHeight - moved.Height));
            _selection = moved;
            _dragStart = new Point(_spaceDragStartOrigin.X + dx, _spaceDragStartOrigin.Y + dy);
            _hasSelection = true;
            UpdateSelectionVisuals();
            return;
        }

        if (_gesture != null)
        {
            _gesture.Move(point, IsShiftDown, IsCtrlDown);
            Editor.InvalidateVisual();
            return;
        }

        if (_textPress != null)
        {
            if (!_textPressMoved && (point - _textPressPoint).Length * 1 / _scale < 3)
            {
                return;
            }

            if (!_textPressMoved)
            {
                _textPressMoved = true;
                _history.CommitArmed();
                UpdateUndoButtons();
                SelectQuietly(_textPress);
                InputLayer.Cursor = Cursors.SizeAll;
            }

            _textPress.Translate(point.X - _lastAnnotationPoint.X, point.Y - _lastAnnotationPoint.Y);
            _lastAnnotationPoint = point;
            Editor.InvalidateVisual();
            return;
        }

        if ((_rotatingAnnotation || _resizingAnnotation || _draggingAnnotation) && _selectedAnnotation != null)
        {
            _history.CommitArmed();
            UpdateUndoButtons();
        }

        if ((_rotatingAnnotation || (_draggingAnnotation && _dragRotate)) && _selectedAnnotation != null)
        {
            var angle = Math.Atan2(point.Y - _annotationCenter.Y, point.X - _annotationCenter.X) * 180 / Math.PI;
            var rotation = _annotationStartRotation + angle - _annotationRotateStartAngle;
            if (IsShiftDown)
            {
                rotation = Math.Round(rotation / 15) * 15;
            }

            _selectedAnnotation.Rotation = ((rotation + 180) % 360 + 360) % 360 - 180;
            Editor.InvalidateVisual();
            return;
        }

        if (_resizingAnnotation && _selectedAnnotation != null)
        {
            var target = AnnotationOps.ResizeFromHandle(
                _annotationStartBounds, _selectedAnnotation.Rotation, _annotationHandle, point, IsShiftDown);
            AnnotationOps.Resize(_selectedAnnotation, target, _annotationHandle);
            Editor.InvalidateVisual();
            return;
        }

        if (_draggingAnnotation && _dragScale && _selectedAnnotation != null)
        {
            var center = new Point(
                _dragScaleBounds.X + _dragScaleBounds.Width / 2,
                _dragScaleBounds.Y + _dragScaleBounds.Height / 2);
            var startDistance = Math.Max(1, (_dragScalePointer - center).Length);
            var factor = Math.Max(0.05, (point - center).Length / startDistance);
            AnnotationOps.Resize(_selectedAnnotation, AnnotationOps.ScaleAround(_dragScaleBounds, center, factor), 4);
            Editor.InvalidateVisual();
            return;
        }

        if (_draggingAnnotation && _selectedAnnotation != null)
        {
            var dx = point.X - _lastAnnotationPoint.X;
            var dy = point.Y - _lastAnnotationPoint.Y;
            if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
            {
                if (!_dragMoved && (point - _dragStart).Length < 3 * _scale)
                {
                    return; // a click on a selected text should not nudge it
                }

                _dragMoved = true;
                ClearAnnotationHover();
                _selectedAnnotation.Translate(dx, dy);
                _lastAnnotationPoint = point;
                Editor.InvalidateVisual();
            }

            return;
        }

        if (!_dragging)
        {
            if (!_hasSelection)
            {
                UpdateHover(point);
            }

            UpdateCursor(point);

            if (_hasSelection && _tool == AnnotationTool.Watermark)
            {
                UpdateWatermarkHover(point);
            }
            else if (_hasSelection && _tool is not AnnotationTool.None and not AnnotationTool.Select and not AnnotationTool.Eraser)
            {
                UpdateAnnotationHover(point);
            }

            return;
        }

        if (_pendingWindow != Rect.Empty)
        {
            if ((point - _dragStart).Length > 4)
            {
                _pendingWindow = Rect.Empty;
                _selection = ClampToScreen(NormalizeRect(_dragStart, point));
                _hasSelection = _selection.Width >= 2 && _selection.Height >= 2;
                UpdateSelectionVisuals();
            }

            return;
        }

        switch (_mode)
        {
            case DragMode.Creating:
            {
                var rect = NormalizeRect(_dragStart, point);
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    rect = MakeSquare(_dragStart, point);
                }

                _selection = ClampToScreen(rect);
                _hasSelection = _selection.Width >= 2 && _selection.Height >= 2;
                UpdateSelectionVisuals();
                break;
            }

            case DragMode.Moving:
            {
                var delta = point - _dragStart;
                var moved = new Rect(
                    _dragOrigin.X + delta.X,
                    _dragOrigin.Y + delta.Y,
                    _dragOrigin.Width,
                    _dragOrigin.Height);
                moved.X = Math.Clamp(moved.X, 0, Math.Max(0, _screen.PixelWidth - moved.Width));
                moved.Y = Math.Clamp(moved.Y, 0, Math.Max(0, _screen.PixelHeight - moved.Height));
                _selection = moved;
                UpdateSelectionVisuals();
                break;
            }

            case DragMode.Resizing:
            {
                _selection = ClampToScreen(ResizeRect(_dragOrigin, _handle, point));
                UpdateSelectionVisuals();
                break;
            }
        }
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
        _dragging = false;
        _spaceDrag = false;
        _suppressContextMenu = true;
        InputLayer.ReleaseMouseCapture();
        Editor.InvalidateVisual();
        e.Handled = true;
    }

    private void Input_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        InputLayer.ReleaseMouseCapture();
        _dragging = false;
        Magnifier.Visibility = Visibility.Collapsed;
        _spaceDrag = false;
        _dragRotate = false;
        _dragScale = false;
        var endedTransform = _resizingAnnotation || _rotatingAnnotation || _draggingAnnotation;
        _resizingAnnotation = false;
        _rotatingAnnotation = false;

        if (_textPress != null)
        {
            var pressed = _textPress;
            _textPress = null;
            _mode = DragMode.None;
            if (_textPressMoved)
            {
                UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            }
            else
            {
                _history.Disarm();
                BeginTextEdit(pressed, isNew: false, caretAt: ToPixels(e.GetPosition(InputLayer)));
            }

            return;
        }

        if (endedTransform)
        {
            _history.Disarm();
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
                if (AnnotationOps.BindEraser(Editor.Items, _eraserPath.Points, _eraserPath.Thickness))
                {
                    _history.Checkpoint(before);
                    UpdateUndoButtons();
                }
            }

            _eraserPath = null;
            Editor.InvalidateVisual();
            _mode = DragMode.None;
            UpdateSelectionVisuals();
            UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            return;
        }

        if (_draggingAnnotation)
        {
            _draggingAnnotation = false;
            _mode = DragMode.None;
            var clicked = _pendingTextClick && !_dragMoved;
            _pendingTextClick = false;
            _dragMoved = false;
            if (clicked && _selectedAnnotation is TextAnnotation selectedText)
            {
                BeginTextEdit(selectedText, isNew: false, caretAt: ToPixels(e.GetPosition(InputLayer)));
                return;
            }

            UpdateSelectionVisuals();
            UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            return;
        }

        if (_active != null)
        {
            CommitAnnotation();
            _mode = DragMode.None;
            UpdateSelectionVisuals();
            UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            return;
        }

        if (_pendingWindow != Rect.Empty)
        {
            _selection = _pendingWindow;
            _hasSelection = true;
            _pendingWindow = Rect.Empty;
            _mode = DragMode.None;
            _handle = -1;
            ClearHover();
            UpdateSelectionVisuals();
            UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            return;
        }

        if (_mode == DragMode.Creating)
        {
            if (_selection.Width < 2 || _selection.Height < 2)
            {
                _hasSelection = false;
                _selection = Rect.Empty;
            }
            else
            {
                _hasSelection = true;
            }
        }

        _mode = DragMode.None;
        _handle = -1;
        ClearHover();
        UpdateSelectionVisuals();
        UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
    }

    private void Input_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_textEditor.IsActive)
        {
            _textEditor.AdjustSize(e.Delta);
            e.Handled = true;
            return;
        }

        if (_hasSelection || _active != null || _closed)
        {
            // Selected element first: wheel = thickness / size, Ctrl+wheel = its opacity.
            // Otherwise with a tool active: the same for the tool's next element.
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (_selectedAnnotation != null)
            {
                AdjustSelectedByWheel(e.Delta, ctrl);
                e.Handled = true;
            }
            else if (_active == null && _tool is not AnnotationTool.None and not AnnotationTool.Select)
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
            }

            return;
        }

        // PixPin switches between the window and its child element with the wheel.
        _preferChild = !_preferChild;
        ClearHover();
        UpdateHover(ToPixels(e.GetPosition(InputLayer)));
        e.Handled = true;
    }

    private bool ShouldShowMagnifier(Point point)
    {
        if (!_hasSelection)
        {
            return true;
        }

        if (_mode is DragMode.Moving or DragMode.Resizing && _dragging)
        {
            return true;
        }

        if (HitHandle(point) >= 0)
        {
            return true;
        }

        return !_selection.Contains(point);
    }

    private void Input_MouseLeave(object sender, MouseEventArgs e)
    {
        Magnifier.Visibility = Visibility.Collapsed;
        ClearHover();
        ClearAnnotationHover();
    }

    private Annotation? HitAnnotation(Point point) => AnnotationOps.HitTest(Editor.Items, point, 7 * _scale);

    private static bool IsShiftDown => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    private static bool IsCtrlDown => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    private double EraserWidth => Math.Max(8, _thickness * 2);

    private void UpdateWatermarkHover(Point point)
    {
        var watermark = AnnotationOps.FindWatermark(Editor.Items);
        var hover = watermark != null && watermark.HitTest(point, 2 * _scale) ? watermark : null;
        InputLayer.Cursor = hover != null ? Cursors.Hand : Cursors.Arrow;
        if (!ReferenceEquals(Editor.HoverWatermark, hover))
        {
            Editor.HoverWatermark = hover;
            Editor.InvalidateVisual();
        }
    }

    private void SelectAnnotation(Annotation annotation)
    {
        _selectedAnnotation = annotation;
        _tool = AnnotationTool.Select;
        Toolbar.SetTool(AnnotationTool.Select);
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
        Editor.Selected = annotation;
        Editor.InvalidateVisual();
    }

    private void ConvertSelected(AnnotationTool variant)
    {
        if (_selectedAnnotation == null)
        {
            return;
        }

        var old = _selectedAnnotation;
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
        var index = Editor.Items.IndexOf(old);
        if (index < 0)
        {
            return;
        }

        _history.Checkpoint(Editor.Items);
        UpdateUndoButtons();

        Editor.Items[index] = replacement;
        _selectedAnnotation = replacement;
        Editor.Selected = replacement;
        Toolbar.SetTool(AnnotationTool.Select);
        Toolbar.SetSelection(replacement);
        Toolbar.SetColor(replacement.Color);
        Toolbar.SetThickness(replacement.Thickness);
        Toolbar.SetFill(replacement.Fill);
        Toolbar.SetCorner(replacement.CornerRadius);
        Editor.InvalidateVisual();
    }

    private bool TryBeginAnnotationTransform(Point point)
    {
        if (_selectedAnnotation == null)
        {
            return false;
        }

        var hit = AnnotationEditor.HitFrame(_selectedAnnotation, point, _scale);
        var bounds = _selectedAnnotation.Bounds;
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        _resizingAnnotation = false;
        _rotatingAnnotation = false;
        _annotationCenter = center;
        _annotationStartBounds = bounds;
        _lastAnnotationPoint = point;

        if (hit.IsHandle)
        {
            _resizingAnnotation = true;
            _annotationHandle = hit.Handle;
            return true;
        }

        if (hit.Rotate)
        {
            _rotatingAnnotation = true;
            _annotationStartRotation = _selectedAnnotation.Rotation;
            _annotationRotateStartAngle = Math.Atan2(point.Y - center.Y, point.X - center.X) * 180 / Math.PI;
            return true;
        }

        if (hit.Inside && AnnotationOps.InsideMoves(_selectedAnnotation, _tool, point, 7 * _scale))
        {
            _draggingAnnotation = true;
            return true;
        }

        return false;
    }

    private void ResizeAnnotation(Annotation annotation, Rect target) => AnnotationOps.Resize(annotation, target, 4);

    private void ClearAnnotationSelection()
    {
        if (_selectedAnnotation == null && Editor.Selected == null)
        {
            return;
        }

        _selectedAnnotation = null;
        Editor.Selected = null;
        _resizingAnnotation = false;
        _rotatingAnnotation = false;
        if (Toolbar != null)
        {
            Toolbar.SetSelection(null);
        }

        ClearAnnotationHover();
        Editor.InvalidateVisual();
    }

    private void RemoveSelectedAnnotation()
    {
        if (_selectedAnnotation == null)
        {
            return;
        }

        _history.Checkpoint(Editor.Items);
        Editor.Items.Remove(_selectedAnnotation);
        _selectedAnnotation = null;
        Editor.Selected = null;
        Toolbar?.SetSelection(null);
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void UpdateHover(Point point)
    {
        if (_hasSelection || _dragging ||
            (_tool != AnnotationTool.None && _tool != AnnotationTool.Select))
        {
            ClearHover();
            return;
        }

        if (_hoverTarget != null && _hoverLocal.Contains(point))
        {
            return;
        }

        var screenX = _bounds.X + (int)Math.Round(point.X);
        var screenY = _bounds.Y + (int)Math.Round(point.Y);
        var target = WindowDetector.Detect(screenX, screenY, _preferChild);

        if (target == null || target.Bounds.Width < 4 || target.Bounds.Height < 4)
        {
            ClearHover();
            return;
        }

        var local = new Rect(
            target.Bounds.X - _bounds.X,
            target.Bounds.Y - _bounds.Y,
            target.Bounds.Width,
            target.Bounds.Height);

        var screenRect = new Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight);
        local = Rect.Intersect(local, screenRect);
        if (local.Width < 4 || local.Height < 4)
        {
            ClearHover();
            return;
        }

        _hoverTarget = target;
        _hoverLocal = local;
        Editor.HoverBounds = local;
        Editor.HoverLabel = $"{target.Label}  {Math.Round(local.Width)} × {Math.Round(local.Height)}";
        Editor.ShowHover = true;
        Editor.InvalidateVisual();
    }

    private void ClearHover()
    {
        if (_hoverTarget == null && !Editor.ShowHover)
        {
            return;
        }

        _hoverTarget = null;
        _hoverLocal = Rect.Empty;
        Editor.ShowHover = false;
        Editor.HoverBounds = Rect.Empty;
        Editor.HoverLabel = null;
        Editor.InvalidateVisual();
    }

    private void UpdateMagnifier(Point point)
    {
        if (point.X < 0 || point.Y < 0 ||
            point.X >= _screen.PixelWidth || point.Y >= _screen.PixelHeight)
        {
            Magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        var x = Math.Clamp((int)point.X, 0, _screen.PixelWidth - 1);
        var y = Math.Clamp((int)point.Y, 0, _screen.PixelHeight - 1);
        _screen.CopyPixels(new Int32Rect(x, y, 1, 1), _pixelBuffer, 4, 0);

        Magnifier.Pixel = point;
        Magnifier.PixelColor = Color.FromRgb(_pixelBuffer[2], _pixelBuffer[1], _pixelBuffer[0]);
        Magnifier.HasPixel = true;
        Magnifier.HexFormat = _hexColor;
        Magnifier.Visibility = Visibility.Visible;
        Magnifier.InvalidateVisual();
        PositionMagnifier(point);
    }

    private void PositionMagnifier(Point point)
    {
        var dip = new Point(point.X / _scale, point.Y / _scale);
        var left = dip.X + 18;
        var top = dip.Y + 18;

        if (left + MagnifierLayer.CardWidth > ActualWidth)
        {
            left = dip.X - MagnifierLayer.CardWidth - 18;
        }

        if (top + MagnifierLayer.CardHeight > ActualHeight)
        {
            top = dip.Y - MagnifierLayer.CardHeight - 18;
        }

        left = Math.Clamp(left, 4, Math.Max(4, ActualWidth - MagnifierLayer.CardWidth - 4));
        top = Math.Clamp(top, 4, Math.Max(4, ActualHeight - MagnifierLayer.CardHeight - 4));
        Canvas.SetLeft(Magnifier, left);
        Canvas.SetTop(Magnifier, top);
    }

    private void CopyCurrentColor()
    {
        if (!Magnifier.HasPixel || Magnifier.Visibility != Visibility.Visible)
        {
            return;
        }

        var color = Magnifier.PixelColor;
        var text = _hexColor
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"{color.R}, {color.G}, {color.B}";

        if (ClipboardService.CopyText(text))
        {
            Magnifier.Notice = $"已复制 {text}";
            Magnifier.InvalidateVisual();
            _noticeTimer.Stop();
            _noticeTimer.Start();
        }
    }

    private void ApplyRecentRegion(bool forward)
    {
        var regions = App.Config.RecentRegions;
        if (regions.Count == 0)
        {
            return;
        }

        _recentRegionIndex = _recentRegionIndex < 0
            ? 0
            : (forward
                ? (_recentRegionIndex + 1) % regions.Count
                : (_recentRegionIndex - 1 + regions.Count) % regions.Count);

        var region = regions[_recentRegionIndex];
        var local = new Rect(
            region.X - _bounds.X,
            region.Y - _bounds.Y,
            region.Width,
            region.Height);

        local = Rect.Intersect(local, new Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight));
        if (local.Width < 2 || local.Height < 2)
        {
            return;
        }

        _selection = local;
        _hasSelection = true;
        _pendingWindow = Rect.Empty;
        _mode = DragMode.None;
        ClearHover();
        Editor.Items.Clear();
        _history.Clear();
        ClearAnnotationSelection();
        UpdateUndoButtons();
        UpdateSelectionVisuals();
    }

    private void StartAnnotation(Point point)
    {
        _mode = DragMode.None;
        ClearAnnotationSelection();
        _active = AnnotationOps.Create(_tool, point, Toolbar, _numberCounter, EffectiveScreen);
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
            _numberCounter++;
        }

        _history.Checkpoint(Editor.Items);
        Editor.Items.Add(annotation);
        UpdateUndoButtons();
        // The new element stays selected (wheel / Ctrl+wheel / frame adjust it right away).
        SelectQuietly(annotation);
    }

    private void BeginTextEdit(TextAnnotation text, bool isNew, Point? caretAt, bool selectAll = false)
    {
        ClearAnnotationSelection();
        ClearAnnotationHover();
        _history.Arm(Editor.Items);
        if (isNew)
        {
            Editor.Items.Add(text);
        }

        _tool = AnnotationTool.Text;
        Toolbar.SetTool(AnnotationTool.Text);
        Toolbar.SyncFrom(text);
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

        if (!_closed)
        {
            Focus();
            Keyboard.Focus(this);
            UpdateCursorToPointer();
        }
    }

    /// <summary>Selects an element without switching tools (the text tool keeps working).</summary>
    private void SelectQuietly(Annotation annotation)
    {
        _selectedAnnotation = annotation;
        Editor.Selected = annotation;
        Toolbar.SyncFrom(annotation);
        ClearAnnotationHover();
        Editor.InvalidateVisual();
    }

    private void BeginWatermarkEdit(WatermarkAnnotation watermark)
    {
        _editingWatermark = watermark;
        WatermarkEditor.Text = watermark.Text;
        WatermarkEditor.Visibility = Visibility.Visible;
        Canvas.SetLeft(WatermarkEditor, watermark.Rect.X / _scale + 4);
        Canvas.SetTop(WatermarkEditor, watermark.Rect.Y / _scale + 4);
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

        if (!_closed)
        {
            Focus();
            Keyboard.Focus(this);
        }
    }

    private void CancelWatermark()
    {
        _editingWatermark = null;
        WatermarkEditor.Visibility = Visibility.Collapsed;
        Focus();
        Keyboard.Focus(this);
    }

    private void WatermarkEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelWatermark();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CommitWatermark();
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_closed || _textEditor.IsActive || _editingWatermark != null || WatermarkEditor.IsKeyboardFocusWithin)
        {
            return;
        }

        var key = AnnotationOps.RealKey(e);
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // While a selected element is being dragged, E rotates and R scales it;
        // every other key belongs to the drag and must not switch tools.
        if (_draggingAnnotation || _resizingAnnotation || _rotatingAnnotation || _textPress != null)
        {
            if (key == Key.E && _draggingAnnotation && !_dragRotate && _selectedAnnotation != null)
            {
                var bounds = _selectedAnnotation.Bounds;
                _annotationCenter = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                _annotationStartRotation = _selectedAnnotation.Rotation;
                _annotationRotateStartAngle = Math.Atan2(_lastAnnotationPoint.Y - _annotationCenter.Y,
                    _lastAnnotationPoint.X - _annotationCenter.X) * 180 / Math.PI;
                _dragScale = false;
                _dragRotate = true;
            }
            else if (key == Key.R && _draggingAnnotation && !_dragScale && _selectedAnnotation != null)
            {
                _dragScaleBounds = _selectedAnnotation.Bounds;
                _dragScalePointer = _lastAnnotationPoint;
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

        if (key == Key.Space && _dragging && _mode == DragMode.Creating && !_spaceDrag)
        {
            _spaceDrag = true;
            var cursor = Native.CursorPosition();
            _spaceAnchor = new Point(cursor.X - _bounds.X, cursor.Y - _bounds.Y);
            _spaceSelectionOrigin = _selection;
            _spaceDragStartOrigin = _dragStart;
            InputLayer.Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (!control && (Keyboard.Modifiers & ModifierKeys.Alt) == 0 &&
            ToolHotkeys.TryMap(key, App.Config, out var toolShortcut))
        {
            SetTool(toolShortcut);
            e.Handled = true;
            return;
        }

        if (key is Key.LeftShift or Key.RightShift)
        {
            _hexColor = !_hexColor;
            Magnifier.HexFormat = _hexColor;
            if (Magnifier.Visibility == Visibility.Visible)
            {
                Magnifier.InvalidateVisual();
            }
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

        if (!control && key == Key.C)
        {
            CopyCurrentColor();
            e.Handled = true;
            return;
        }

        if (!control && key == Key.R)
        {
            ApplyRecentRegion(!shift);
            e.Handled = true;
            return;
        }

        if (!control && key == Key.V)
        {
            SetTool(AnnotationTool.Select);
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            if (_selectedAnnotation != null)
            {
                ClearAnnotationSelection();
            }
            else if (_tool != AnnotationTool.None && _tool != AnnotationTool.Select)
            {
                SetTool(AnnotationTool.Select);
            }
            else if (_hasSelection)
            {
                _hasSelection = false;
                _selection = Rect.Empty;
                ClearAnnotationSelection();
                UpdateSelectionVisuals();
            }
            else
            {
                Close();
            }

            e.Handled = true;
            return;
        }

        if (control && key == Key.Z)
        {
            Undo();
            e.Handled = true;
            return;
        }

        if (control && (key == Key.Y || (shift && key == Key.Z)))
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (!_hasSelection)
        {
            return;
        }

        if (control && key == Key.B && _selectedAnnotation is TextAnnotation)
        {
            Toolbar.ToggleBold();
            e.Handled = true;
            return;
        }

        if (_selectedAnnotation is TextAnnotation enterText && key == Key.F2)
        {
            BeginTextEdit(enterText, isNew: false, caretAt: null, selectAll: true);
            e.Handled = true;
            return;
        }

        if (_selectedAnnotation != null && key is Key.Delete or Key.Back)
        {
            RemoveSelectedAnnotation();
            e.Handled = true;
            return;
        }

        if (_selectedAnnotation != null &&
            key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var amount = shift ? 10.0 : 1.0;
            var dx = key == Key.Left ? -amount : key == Key.Right ? amount : 0;
            var dy = key == Key.Up ? -amount : key == Key.Down ? amount : 0;
            _history.CheckpointCoalesced(Editor.Items, ("nudge", _selectedAnnotation));
            _selectedAnnotation.Translate(dx, dy);
            UpdateUndoButtons();
            Editor.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = shift ? 10.0 : 1.0;
            var selection = _selection;
            switch (key)
            {
                case Key.Left:
                    selection.X = Math.Max(0, selection.X - step);
                    break;
                case Key.Right:
                    selection.X = Math.Min(_screen.PixelWidth - selection.Width, selection.X + step);
                    break;
                case Key.Up:
                    selection.Y = Math.Max(0, selection.Y - step);
                    break;
                case Key.Down:
                    selection.Y = Math.Min(_screen.PixelHeight - selection.Height, selection.Y + step);
                    break;
            }

            _selection = selection;
            UpdateSelectionVisuals();
            e.Handled = true;
            return;
        }

        if (key == Key.Enter)
        {
            CopyAndClose();
            e.Handled = true;
            return;
        }

        if (control && key == Key.C)
        {
            CopyAndClose();
            e.Handled = true;
            return;
        }

        if (control && key == Key.S)
        {
            if (shift)
            {
                QuickSaveAndClose();
            }
            else
            {
                SaveAndClose();
            }

            e.Handled = true;
            return;
        }

        if (control && key == Key.A)
        {
            _selection = new Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight);
            _hasSelection = true;
            UpdateSelectionVisuals();
            e.Handled = true;
            return;
        }

        if (key == Key.F3)
        {
            PinAndClose();
            e.Handled = true;
            return;
        }

        if (control && key == Key.Q)
        {
            TranslateAndClose();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.O:
                    SetTool(AnnotationTool.Ellipse);
                    e.Handled = true;
                    break;
                case Key.A:
                    SetTool(AnnotationTool.Arrow);
                    e.Handled = true;
                    break;
                case Key.L:
                    SetTool(AnnotationTool.Line);
                    e.Handled = true;
                    break;
                case Key.E:
                    SetTool(AnnotationTool.Eraser);
                    e.Handled = true;
                    break;
                case Key.P:
                    SetTool(AnnotationTool.Pen);
                    e.Handled = true;
                    break;
                case Key.H:
                    SetTool(AnnotationTool.Highlighter);
                    e.Handled = true;
                    break;
                case Key.M:
                    SetTool(AnnotationTool.Mosaic);
                    e.Handled = true;
                    break;
                case Key.T:
                    SetTool(AnnotationTool.Text);
                    e.Handled = true;
                    break;
                case Key.N:
                    SetTool(AnnotationTool.Number);
                    e.Handled = true;
                    break;
            }
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = AnnotationOps.RealKey(e);
        var pointer = ToPixels(Mouse.GetPosition(InputLayer));

        if (_gesture != null)
        {
            if (key == Key.Space)
            {
                _gesture.EndMove();
                InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color);
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

        if (key == Key.E && _dragRotate)
        {
            // Continue moving from here without a jump.
            _dragRotate = false;
            _lastAnnotationPoint = pointer;
            e.Handled = true;
        }

        if (key == Key.R && _dragScale)
        {
            _dragScale = false;
            _lastAnnotationPoint = pointer;
            e.Handled = true;
        }

        if (key == Key.Space && _spaceDrag)
        {
            _spaceDrag = false;
            e.Handled = true;
        }
    }

    private void SetTool(AnnotationTool tool)
    {
        _textEditor.Commit();
        _tool = tool;
        Toolbar.SetTool(tool);
        if (tool != AnnotationTool.Select)
        {
            ClearAnnotationSelection();
        }
        else if (_selectedAnnotation != null)
        {
            Toolbar.SetSelection(_selectedAnnotation);
        }

        _thickness = Toolbar.Thickness;
        ActivateWatermarkIfNeeded();
        InputLayer.Cursor = ToolCursors.Get(tool, _thickness, _color);
    }

    private void UpdateAnnotationHover(Point point)
    {
        var hit = HitAnnotation(point);
        if (hit != null && SameFamily(hit, _tool))
        {
            var bounds = hit.Bounds;
            bounds.Inflate(4 / _scale, 4 / _scale);
            Editor.HoverBounds = bounds;
            Editor.HoverLabel = _tool == AnnotationTool.Text ? "单击编辑 · 拖动移动" : "点击选中并继续编辑";
            Editor.ShowHover = true;
            InputLayer.Cursor = _tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Hand;
            Editor.InvalidateVisual();
            return;
        }

        ClearAnnotationHover();
        InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color);
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
        if (_selectedAnnotation == null)
        {
            return false;
        }

        var hit = AnnotationEditor.HitFrame(_selectedAnnotation, point, _scale);
        if (hit.IsHandle)
        {
            InputLayer.Cursor = AnnotationEditor.ResizeCursor(hit.Handle, _selectedAnnotation.Rotation);
            return true;
        }

        if (hit.Rotate)
        {
            InputLayer.Cursor = ToolCursors.GetRotationCursor(true);
            return true;
        }

        if (hit.Inside && AnnotationOps.InsideMoves(_selectedAnnotation, _tool, point, 7 * _scale))
        {
            InputLayer.Cursor = Cursors.SizeAll;
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
            _ => Math.Clamp(_thickness + step * 0.5, 1, 16)
        };

        _thickness = value;
        Toolbar.SetThickness(value);
        UpdateCursorToPointer();
    }

    private void AdjustSelectedByWheel(int delta, bool opacity)
    {
        if (_selectedAnnotation == null)
        {
            return;
        }

        _history.CheckpointCoalesced(Editor.Items, (opacity ? "opacity" : "wheel", _selectedAnnotation));
        UpdateUndoButtons();
        if (opacity)
        {
            if (_selectedAnnotation is WatermarkAnnotation)
            {
                _selectedAnnotation.Opacity = Math.Clamp(Math.Round(_selectedAnnotation.Opacity + Math.Sign(delta) * 0.05, 2), 0.05, 1);
                Toolbar.SyncFrom(_selectedAnnotation);
            }
            else
            {
                _selectedAnnotation.Opacity = AnnotationOps.WheelOpacity(_selectedAnnotation.Opacity, delta);
                Toolbar.SetOpacity(_selectedAnnotation.Opacity, remember: false);
            }
        }
        else if (AnnotationOps.WheelSize(_selectedAnnotation, delta, EffectiveScreen) is { } size)
        {
            _thickness = size;
            Toolbar.SetThickness(size);
        }
        else
        {
            Toolbar.SyncFrom(_selectedAnnotation);
        }

        Editor.InvalidateVisual();
    }

    private int HitHandle(Point point)
    {
        if (!_hasSelection)
        {
            return -1;
        }

        var tolerance = 12 / _scale;
        var selection = _selection;

        var nearLeft = Math.Abs(point.X - selection.Left) <= tolerance;
        var nearRight = Math.Abs(point.X - selection.Right) <= tolerance;
        var nearTop = Math.Abs(point.Y - selection.Top) <= tolerance;
        var nearBottom = Math.Abs(point.Y - selection.Bottom) <= tolerance;

        if (nearLeft && nearTop) return 0;
        if (nearRight && nearTop) return 2;
        if (nearRight && nearBottom) return 4;
        if (nearLeft && nearBottom) return 6;

        if (nearTop) return 1;
        if (nearBottom) return 5;
        if (nearLeft) return 7;
        if (nearRight) return 3;

        return -1;
    }

    private void UpdateCursor(Point point)
    {
        if (_hasSelection)
        {
            var frameHandle = HitHandle(point);
            if (frameHandle >= 0)
            {
                InputLayer.Cursor = frameHandle switch
                {
                    0 or 4 => Cursors.SizeNWSE,
                    2 or 6 => Cursors.SizeNESW,
                    1 or 5 => Cursors.SizeNS,
                    3 or 7 => Cursors.SizeWE,
                    _ => Cursors.SizeAll
                };
                return;
            }
        }

        if (_tool == AnnotationTool.Select || _tool == AnnotationTool.None)
        {
            if (_selectedAnnotation != null && UpdateAnnotationTransformCursor(point))
            {
                return;
            }

            if (HitAnnotation(point) != null)
            {
                InputLayer.Cursor = Cursors.SizeAll;
                return;
            }

            var selectedHandle = HitHandle(point);
            if (selectedHandle >= 0)
            {
                InputLayer.Cursor = selectedHandle switch
                {
                    0 or 4 => Cursors.SizeNWSE,
                    2 or 6 => Cursors.SizeNESW,
                    1 or 5 => Cursors.SizeNS,
                    3 or 7 => Cursors.SizeWE,
                    _ => Cursors.SizeAll
                };
                return;
            }

            InputLayer.Cursor = _hasSelection && _selection.Contains(point)
                ? Cursors.SizeAll
                : _hasSelection ? Cursors.Arrow : Cursors.Cross;
            return;
        }

        if (_tool != AnnotationTool.None && _hasSelection && _selection.Contains(point))
        {
            InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color);
            return;
        }

        var handle = HitHandle(point);
        InputLayer.Cursor = handle switch
        {
            0 or 4 => Cursors.SizeNWSE,
            2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS,
            3 or 7 => Cursors.SizeWE,
            _ => _hasSelection && _selection.Contains(point) ? Cursors.SizeAll : Cursors.Cross
        };
    }

    private static Rect NormalizeRect(Point a, Point b)
    {
        return new Rect(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));
    }

    private static Rect MakeSquare(Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var size = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Rect(
            dx < 0 ? start.X - size : start.X,
            dy < 0 ? start.Y - size : start.Y,
            size,
            size);
    }

    private static Point SnapLine(Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
        {
            return end;
        }

        var angle = Math.Atan2(dy, dx);
        var step = Math.PI / 4;
        var snapped = Math.Round(angle / step) * step;
        return new Point(
            start.X + Math.Cos(snapped) * length,
            start.Y + Math.Sin(snapped) * length);
    }

    private Rect ClampToScreen(Rect rect)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, _screen.PixelWidth - 2));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, _screen.PixelHeight - 2));
        var width = Math.Min(rect.Width, _screen.PixelWidth - x);
        var height = Math.Min(rect.Height, _screen.PixelHeight - y);
        return new Rect(x, y, Math.Max(1, width), Math.Max(1, height));
    }

    private static Rect ResizeRect(Rect original, int handle, Point point)
    {
        var left = original.Left;
        var top = original.Top;
        var right = original.Right;
        var bottom = original.Bottom;

        switch (handle)
        {
            case 0:
                left = point.X;
                top = point.Y;
                break;
            case 1:
                top = point.Y;
                break;
            case 2:
                right = point.X;
                top = point.Y;
                break;
            case 3:
                right = point.X;
                break;
            case 4:
                right = point.X;
                bottom = point.Y;
                break;
            case 5:
                bottom = point.Y;
                break;
            case 6:
                left = point.X;
                bottom = point.Y;
                break;
            case 7:
                left = point.X;
                break;
        }

        return new Rect(
            Math.Min(left, right),
            Math.Min(top, bottom),
            Math.Abs(right - left),
            Math.Abs(bottom - top));
    }

    private void UpdateUndoButtons()
    {
        Toolbar.RefreshUndo(_history.CanUndo, _history.CanRedo);
    }

    private void Undo() => ApplyHistory(_history.Undo);

    private void Redo() => ApplyHistory(_history.Redo);

    private void ApplyHistory(Func<List<Annotation>, bool> step)
    {
        _textEditor.Commit();
        ClearAnnotationSelection();
        if (!step(Editor.Items))
        {
            return;
        }

        // Restored items are copies; references held for the selection are stale.
        _numberCounter = Editor.Items.OfType<NumberAnnotation>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private BitmapSource Compose() => Compose(includeAnnotations: true);

    private BitmapSource Compose(bool includeAnnotations)
    {
        var rect = ClampToScreen(_selection);
        var x = (int)Math.Round(rect.X);
        var y = (int)Math.Round(rect.Y);
        var width = Math.Max(1, (int)Math.Round(rect.Width));
        var height = Math.Max(1, (int)Math.Round(rect.Height));

        var translation = VisibleTranslation();
        if ((!includeAnnotations || Editor.Items.Count == 0) && translation == null)
        {
            var cropped = new CroppedBitmap(_screen, new Int32Rect(x, y, width, height));
            cropped.Freeze();
            return cropped;
        }

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-x, -y));
            dc.PushClip(new RectangleGeometry(new Rect(x, y, width, height)));
            dc.DrawImage(_screen, new Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight));
            if (translation != null)
            {
                dc.DrawImage(translation, _translatedRect);
            }

            if (includeAnnotations)
            {
                foreach (var annotation in Editor.Items)
                {
                    AnnotationEditor.DrawItem(dc, annotation);
                }
            }

            dc.Pop();
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void CommitPendingText()
    {
        _textEditor.Commit();
        CommitWatermark();
    }

    private void CopyAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        Log.Info($"CopyAndClose：{_selection.Width}x{_selection.Height}，批注 {Editor.Items.Count} 个");
        var image = Compose();
        if (!ClipboardService.CopyImage(image))
        {
            MessageBox.Show(this, "复制失败：剪贴板被其他程序占用，请重试。", "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CloseWith(CaptureAction.Copy, image);
    }

    private void SaveAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        var image = Compose();
        var dialog = new SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg",
            FileName = $"Markshot_{DateTime.Now:yyyyMMdd_HHmmss}.png"
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
            SaveToFile(image, dialog.FileName);
            CloseWith(CaptureAction.Save, image);
        }
        catch (Exception ex)
        {
            Log.Error("保存截图失败", ex);
            MessageBox.Show(this, "保存失败：" + ex.Message, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void QuickSaveAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        var image = Compose();
        try
        {
            var directory = App.Config.ResolveSaveDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"Markshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            SaveToFile(image, path);
            CloseWith(CaptureAction.Save, image);
        }
        catch (Exception ex)
        {
            Log.Error("快速保存失败", ex);
            MessageBox.Show(this, "快速保存失败：" + ex.Message, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PinAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        // Hand the pin the clean selection plus the annotations so they stay editable there.
        var rect = ClampToScreen(_selection);
        var origin = new Point(Math.Round(rect.X), Math.Round(rect.Y));
        PinBaseImage = Compose(includeAnnotations: false);
        PinAnnotations = AnnotationOps.ToPinSpace(Editor.Items, origin, PinBaseImage);
        CloseWith(CaptureAction.Pin, Compose());
    }

    /// <summary>True while the selection itself is being drawn, moved or resized.</summary>
    private bool SelectionDragging =>
        _dragging && (_mode is DragMode.Creating or DragMode.Moving or DragMode.Resizing || _pendingWindow != Rect.Empty);

    /// <summary>Selection snapped to whole pixels: the unit translation results are keyed by.</summary>
    private Rect SnapRect(Rect rect)
    {
        var clamped = ClampToScreen(rect);
        return new Rect(
            Math.Round(clamped.X), Math.Round(clamped.Y),
            Math.Max(1, Math.Round(clamped.Width)), Math.Max(1, Math.Round(clamped.Height)));
    }

    /// <summary>The translated pixels when they match the current selection and are not toggled off.</summary>
    private BitmapSource? VisibleTranslation() =>
        _autoTranslate && !_showOriginal && _translated != null && _hasSelection &&
        SnapRect(_selection) == _translatedRect
            ? _translated
            : null;

    /// <summary>
    /// The screen as currently shown (with the translation), used as the pixel
    /// source for the eraser and mosaic so they act on what the user sees.
    /// </summary>
    private BitmapSource EffectiveScreen
    {
        get
        {
            var translation = VisibleTranslation();
            if (translation == null)
            {
                return _screen;
            }

            if (_effectiveScreen == null)
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawImage(_screen, new Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight));
                    dc.DrawImage(translation, _translatedRect);
                }

                var bitmap = new RenderTargetBitmap(_screen.PixelWidth, _screen.PixelHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                bitmap.Freeze();
                _effectiveScreen = bitmap;
            }

            return _effectiveScreen;
        }
    }

    private async void StartTranslation()
    {
        if (!_autoTranslate || !_hasSelection || _closed || SelectionDragging)
        {
            return;
        }

        var rect = SnapRect(_selection);
        if (rect == _translatedRect && _translated != null)
        {
            UpdateTranslationVisuals();
            return;
        }

        CancelTranslation();
        var cts = new CancellationTokenSource();
        _translateCts = cts;
        _translatingRect = rect;
        _translated = null;
        _translatedRect = Rect.Empty;
        _effectiveScreen = null;
        _showOriginal = false;

        var cropped = new CroppedBitmap(_screen, new Int32Rect(
            (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height));
        cropped.Freeze();

        ShowTranslateStatus("正在识别文字…", busy: true);
        var progress = new Progress<ImmersiveTranslator.Update>(update =>
        {
            if (cts.IsCancellationRequested || _closed)
            {
                return;
            }

            // Show partial results as they arrive, like a browser filling in a page.
            _translated = update.Image;
            _translatedRect = rect;
            _effectiveScreen = null;
            _sourceLabel = update.Source ?? _sourceLabel;
            _targetLabel = update.Target ?? _targetLabel;
            UpdateTranslationVisuals();
            var partial = update.Done && update.Translated < update.Total;
            ShowTranslateStatus(update.Done && !partial && update.Translated > 0 ? null : update.Status,
                busy: !update.Done, error: partial, retry: partial);
        });

        try
        {
            Log.Info($"原位翻译开始：{rect.Width}x{rect.Height}");
            await ImmersiveTranslator.TranslateAsync(cropped, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("原位翻译失败", ex);
            if (!cts.IsCancellationRequested && !_closed)
            {
                ShowTranslateStatus("翻译失败：" + ex.Message, busy: false, error: true, retry: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_translateCts, cts))
            {
                _translatingRect = Rect.Empty;
            }
        }
    }

    private void CancelTranslation()
    {
        _translateDebounce.Stop();
        if (_translateCts != null)
        {
            _translateCts.Cancel();
            _translateCts.Dispose();
            _translateCts = null;
        }

        _translatingRect = Rect.Empty;
    }

    private void UpdateTranslationVisuals()
    {
        if (_closed)
        {
            return;
        }

        var translation = VisibleTranslation();
        if (translation == null || SelectionDragging)
        {
            TranslatedImage.Visibility = Visibility.Collapsed;
        }
        else
        {
            TranslatedImage.Source = translation;
            Canvas.SetLeft(TranslatedImage, _translatedRect.X / _scale);
            Canvas.SetTop(TranslatedImage, _translatedRect.Y / _scale);
            TranslatedImage.Width = _translatedRect.Width / _scale;
            TranslatedImage.Height = _translatedRect.Height / _scale;
            TranslatedImage.Visibility = Visibility.Visible;
        }

        if (!_autoTranslate || !_hasSelection)
        {
            TranslateStatus.Visibility = Visibility.Collapsed;
        }
        else
        {
            PositionTranslateStatus();
        }
    }

    /// <summary>Updates the translate bar; <paramref name="text"/> null hides the status (only the switch shows).</summary>
    private void ShowTranslateStatus(string? text, bool busy, bool error = false, bool retry = false)
    {
        TranslateStatusText.Text = text ?? string.Empty;
        TranslateStatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        TranslateDot.Fill = error
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B))
            : busy
                ? (Brush)FindResource("Accent")
                : new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8A));
        TranslateRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        UpdateLanguageSwitch();
        TranslateStatus.Visibility = _autoTranslate && _hasSelection ? Visibility.Visible : Visibility.Collapsed;
        PositionTranslateStatus();
    }

    /// <summary>[source | target]: the highlighted half is the language currently shown.</summary>
    private void UpdateLanguageSwitch()
    {
        var known = _sourceLabel != null && _targetLabel != null && _sourceLabel != _targetLabel;
        LanguageSwitch.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        if (!known)
        {
            return;
        }

        SourceLanguageButton.Content = _sourceLabel;
        TargetLanguageButton.Content = _targetLabel;
        SourceLanguageButton.IsChecked = _showOriginal;
        TargetLanguageButton.IsChecked = !_showOriginal;
    }

    private void PositionTranslateStatus()
    {
        if (TranslateStatus.Visibility != Visibility.Visible)
        {
            return;
        }

        TranslateStatus.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = TranslateStatus.DesiredSize;
        var selection = new Rect(_selection.X / _scale, _selection.Y / _scale,
            _selection.Width / _scale, _selection.Height / _scale);
        // Top-right corner above the selection; inside it when there is no room.
        var left = Math.Clamp(selection.Right - size.Width, 4, Math.Max(4, ActualWidth - size.Width - 4));
        var top = selection.Top - size.Height - 6;
        if (top < 4)
        {
            top = selection.Top + 6;
        }

        Canvas.SetLeft(TranslateStatus, left);
        Canvas.SetTop(TranslateStatus, top);
    }

    private void SourceLanguage_Click(object sender, RoutedEventArgs e)
    {
        // Show the original text.
        _showOriginal = true;
        _effectiveScreen = null;
        UpdateLanguageSwitch();
        UpdateTranslationVisuals();
        Focus();
    }

    private void TargetLanguage_Click(object sender, RoutedEventArgs e)
    {
        _showOriginal = false;
        _effectiveScreen = null;
        UpdateLanguageSwitch();
        UpdateTranslationVisuals();
        Focus();
    }

    private void TranslateRetry_Click(object sender, RoutedEventArgs e)
    {
        _translatedRect = Rect.Empty;
        StartTranslation();
        Focus();
    }

    private void TranslateAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        var rect = ClampToScreen(_selection);
        var cropped = new CroppedBitmap(
            _screen,
            new Int32Rect(
                (int)Math.Round(rect.X),
                (int)Math.Round(rect.Y),
                Math.Max(1, (int)Math.Round(rect.Width)),
                Math.Max(1, (int)Math.Round(rect.Height))));
        cropped.Freeze();
        CloseWith(CaptureAction.Translate, cropped);
    }

    private static void SaveToFile(BitmapSource image, string path)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg"
            ? new JpegBitmapEncoder { QualityLevel = 92 }
            : new PngBitmapEncoder();

        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void OcrAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        var image = Compose();
        CloseWith(CaptureAction.None, image);
        ((App)Application.Current).ShowOcr(image);
    }

    private void CloseWith(CaptureAction action, BitmapSource image)
    {
        Log.Info($"CloseWith：{action}，{image.PixelWidth}x{image.PixelHeight}");
        Result = action;
        OutputImage = image;
        SelectionScreenRect = new Rect(
            _bounds.X + _selection.X,
            _bounds.Y + _selection.Y,
            _selection.Width,
            _selection.Height);
        Close();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyAndClose();

    private void Save_Click(object sender, RoutedEventArgs e) => SaveAndClose();

    private void Pin_Click(object sender, RoutedEventArgs e) => PinAndClose();

    private void Translate_Click(object sender, RoutedEventArgs e) => TranslateAndClose();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _noticeTimer.Stop();
        CancelTranslation();
        _active = null;
        Editor.Preview = null;
        Editor.Items.Clear();
        Editor.Source = null;
        ScreenImage.Source = null;
        BrightImage.Source = null;
        base.OnClosed(e);
    }
}
