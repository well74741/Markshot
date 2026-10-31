using System.Windows.Interop;
using Markshot.Annotations;
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

    private readonly Stack<Annotation> _redo = new();

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
    private Point _annotationStart;
    private int _numberCounter = 1;

    private Color _color = Color.FromRgb(0xFF, 0x33, 0x00);
    private double _thickness = 3;
    private bool _fill;
    private double _corner;

    private bool _textActive;
    private Point _textPosition;

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
    private readonly DispatcherTimer _noticeTimer;
    private bool _spaceDrag;
    private Point _spaceAnchor;
    private Rect _spaceSelectionOrigin;
    private Point _spaceDragStartOrigin;
    private bool _spaceAnnotation;
    private Point _spaceAnnotationAnchor;
    private Point _spaceAnnotationDelta;

    public CaptureAction Result { get; private set; } = CaptureAction.None;

    public BitmapSource? OutputImage { get; private set; }

    public Rect SelectionScreenRect { get; private set; }

    public OverlayWindow(BitmapSource screen, Int32Rect bounds, bool demo = false)
    {
        InitializeComponent();

        _screen = screen;
        _bounds = bounds;
        _demo = demo;

        ScreenImage.Source = screen;
        BrightImage.Source = screen;
        Editor.Source = screen;
        Magnifier.Source = screen;

        InitToolbar();

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
        TextEditor.LostKeyboardFocus += (_, _) =>
        {
            if (_textActive && !_closed)
            {
                CommitText();
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
        _tool = tool;
        ClearAnnotationSelection();
        Toolbar.SetSelection(null);
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
        UpdateCursorToPointer();
    }

    private void ApplyToolbarSettings()
    {
        _color = Toolbar.Color;
        _thickness = Toolbar.Thickness;
        _fill = Toolbar.FillEnabled;
        _corner = Toolbar.CornerRadius;

        if (_selectedAnnotation == null)
        {
            return;
        }

        _selectedAnnotation.Color = _color;
        _selectedAnnotation.Thickness = _thickness;
        _selectedAnnotation.Fill = _fill;
        _selectedAnnotation.CornerRadius = _corner;
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
            _selection.X / _scale,
            _selection.Y / _scale,
            _selection.Width / _scale,
            _selection.Height / _scale);

        Editor.Selection = _selection;
        Editor.ShowSelectionChrome = true;
        Editor.InvalidateVisual();

        Hint.Visibility = Visibility.Collapsed;
        Magnifier.Visibility = Visibility.Collapsed;

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
        var mosaic = new MosaicAnnotation { Rect = mosaicRect, Color = Color.FromRgb(0x00, 0xE5, 0xE5), Thickness = 3 };
        mosaic.Pattern = MosaicBuilder.Build(_screen, mosaicRect, 12);
        if (mosaic.Pattern != null)
        {
            Editor.Items.Add(mosaic);
        }

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

        Focus();

        if (TextEditor.Visibility == Visibility.Visible)
        {
            CommitText();
        }

        var point = ToPixels(e.GetPosition(InputLayer));
        _dragStart = point;
        _dragOrigin = _selection;
        _dragging = true;
        _pendingWindow = Rect.Empty;

        if (e.ClickCount == 2 &&
            (_tool == AnnotationTool.None || _tool == AnnotationTool.Select) &&
            _hasSelection && _selection.Contains(point))
        {
            CopyAndClose();
            e.Handled = true;
            return;
        }

        if (_tool == AnnotationTool.Select)
        {
            var hit = HitAnnotation(point);
            if (hit != null)
            {
                SelectAnnotation(hit);
                _draggingAnnotation = true;
                _lastAnnotationPoint = point;
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            ClearAnnotationSelection();
        }

        if (_tool == AnnotationTool.Eraser)
        {
            _erasing = true;
            EraseAt(point);
            InputLayer.CaptureMouse();
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
            _redo.Clear();
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
        _redo.Clear();
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
        if (!_hasSelection)
        {
            UpdateMagnifier(point);
        }
        else
        {
            Magnifier.Visibility = Visibility.Collapsed;
        }

        if (_spaceAnnotation && _active != null)
        {
            var dx = point.X - _spaceAnnotationAnchor.X;
            var dy = point.Y - _spaceAnnotationAnchor.Y;
            _active.Translate(dx - _spaceAnnotationDelta.X, dy - _spaceAnnotationDelta.Y);
            _annotationStart = new Point(
                _annotationStart.X + dx - _spaceAnnotationDelta.X,
                _annotationStart.Y + dy - _spaceAnnotationDelta.Y);
            _spaceAnnotationDelta = new Point(dx, dy);
            Editor.InvalidateVisual();
            return;
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

        if (_active != null)
        {
            UpdateAnnotation(point);
            return;
        }

        if (_erasing)
        {
            EraseAt(point);
            return;
        }

        if (_draggingAnnotation && _selectedAnnotation != null)
        {
            var dx = point.X - _lastAnnotationPoint.X;
            var dy = point.Y - _lastAnnotationPoint.Y;
            if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
            {
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

    private void Input_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        InputLayer.ReleaseMouseCapture();
        _dragging = false;
        _spaceDrag = false;
        _spaceAnnotation = false;

        if (_erasing)
        {
            _erasing = false;
            _mode = DragMode.None;
            UpdateSelectionVisuals();
            UpdateCursor(ToPixels(e.GetPosition(InputLayer)));
            return;
        }

        if (_draggingAnnotation)
        {
            _draggingAnnotation = false;
            _mode = DragMode.None;
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
        if (_hasSelection || _active != null || _closed)
        {
            return;
        }

        // PixPin switches between the window and its child element with the wheel.
        _preferChild = !_preferChild;
        ClearHover();
        UpdateHover(ToPixels(e.GetPosition(InputLayer)));
        e.Handled = true;
    }

    private void Input_MouseLeave(object sender, MouseEventArgs e)
    {
        Magnifier.Visibility = Visibility.Collapsed;
        ClearHover();
    }

    private Annotation? HitAnnotation(Point point)
    {
        var tolerance = 7 / _scale;
        for (var i = Editor.Items.Count - 1; i >= 0; i--)
        {
            if (Editor.Items[i].HitTest(point, tolerance))
            {
                return Editor.Items[i];
            }
        }

        return null;
    }

    private void SelectAnnotation(Annotation annotation)
    {
        _selectedAnnotation = annotation;
        _tool = AnnotationTool.Select;
        Toolbar.SetTool(AnnotationTool.Select);
        Toolbar.SetSelection(annotation);
        Toolbar.SetColor(annotation.Color);
        Toolbar.SetThickness(annotation.Thickness);
        Toolbar.SetFill(annotation.Fill);
        Toolbar.SetCorner(annotation.CornerRadius);
        _color = annotation.Color;
        _thickness = annotation.Thickness;
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
            case (MosaicAnnotation mosaic, AnnotationTool.Watermark):
                replacement = new WatermarkAnnotation
                {
                    Rect = mosaic.Rect, Color = mosaic.Color, Thickness = Math.Max(8, mosaic.Thickness),
                    Opacity = 0.22
                };
                break;
            case (WatermarkAnnotation watermark, AnnotationTool.Mosaic):
                var newMosaic = new MosaicAnnotation
                {
                    Rect = watermark.Rect, Color = watermark.Color, Thickness = watermark.Thickness
                };
                newMosaic.Pattern = MosaicBuilder.Build(_screen, newMosaic.Rect);
                replacement = newMosaic;
                break;
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

    private void ClearAnnotationSelection()
    {
        if (_selectedAnnotation == null && Editor.Selected == null)
        {
            return;
        }

        _selectedAnnotation = null;
        Editor.Selected = null;
        if (Toolbar != null)
        {
            Toolbar.SetSelection(null);
        }

        Editor.InvalidateVisual();
    }

    private void RemoveSelectedAnnotation()
    {
        if (_selectedAnnotation == null)
        {
            return;
        }

        Editor.Items.Remove(_selectedAnnotation);
        _selectedAnnotation = null;
        Editor.Selected = null;
        Toolbar?.SetSelection(null);
        _redo.Clear();
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void EraseAt(Point point)
    {
        var hit = HitAnnotation(point);
        if (hit == null)
        {
            return;
        }

        Editor.Items.Remove(hit);
        if (ReferenceEquals(_selectedAnnotation, hit))
        {
            ClearAnnotationSelection();
        }

        _redo.Clear();
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
        _redo.Clear();
        ClearAnnotationSelection();
        UpdateUndoButtons();
        UpdateSelectionVisuals();
    }

    private void StartAnnotation(Point point)
    {
        _annotationStart = point;
        _mode = DragMode.None;

        switch (_tool)
        {
            case AnnotationTool.Rectangle:
                _active = new RectAnnotation
                {
                    Rect = new Rect(point, point),
                    Color = _color,
                    Thickness = _thickness,
                    Fill = _fill,
                    CornerRadius = _corner
                };
                break;

            case AnnotationTool.Ellipse:
                _active = new EllipseAnnotation
                {
                    Rect = new Rect(point, point),
                    Color = _color,
                    Thickness = _thickness,
                    Fill = _fill
                };
                break;

            case AnnotationTool.Arrow:
                _active = new ArrowAnnotation
                {
                    Start = point,
                    End = point,
                    Color = _color,
                    Thickness = _thickness
                };
                break;

            case AnnotationTool.Line:
                _active = new LineAnnotation
                {
                    Start = point,
                    End = point,
                    Color = _color,
                    Thickness = _thickness
                };
                break;

            case AnnotationTool.Pen:
            {
                var pen = new PenAnnotation { Color = _color, Thickness = _thickness };
                pen.Points.Add(point);
                _active = pen;
                break;
            }

            case AnnotationTool.Highlighter:
            {
                var pen = new PenAnnotation { Color = _color, Thickness = _thickness, Highlight = true };
                pen.Points.Add(point);
                _active = pen;
                break;
            }

            case AnnotationTool.Mosaic:
                _active = new MosaicAnnotation
                {
                    Rect = new Rect(point, point),
                    Color = _color,
                    Thickness = _thickness
                };
                break;

            case AnnotationTool.Watermark:
                _active = new WatermarkAnnotation
                {
                    Rect = new Rect(point, point),
                    Color = _color,
                    Thickness = _thickness,
                    Opacity = 0.22
                };
                break;

            case AnnotationTool.Text:
                BeginText(point);
                return;

            case AnnotationTool.Number:
                _active = new NumberAnnotation
                {
                    Center = point,
                    Number = _numberCounter,
                    Color = _color,
                    Thickness = _thickness,
                    Radius = Math.Max(13, _thickness * 4.5)
                };
                break;
        }

        Editor.Preview = _active;
        Editor.InvalidateVisual();
    }

    private void UpdateAnnotation(Point point)
    {
        switch (_active)
        {
            case RectAnnotation rect:
            {
                var bounds = NormalizeRect(_annotationStart, point);
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    bounds = MakeSquare(_annotationStart, point);
                }

                rect.Rect = ClampToScreen(bounds);
                break;
            }

            case EllipseAnnotation ellipse:
            {
                var bounds = NormalizeRect(_annotationStart, point);
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    bounds = MakeSquare(_annotationStart, point);
                }

                ellipse.Rect = ClampToScreen(bounds);
                break;
            }

            case ArrowAnnotation arrow:
                arrow.End = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                    ? SnapLine(_annotationStart, point)
                    : point;
                break;

            case LineAnnotation line:
                line.End = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                    ? SnapLine(_annotationStart, point)
                    : point;
                break;

            case PenAnnotation pen:
                if (pen.Points.Count == 0 ||
                    (pen.Points[^1] - point).Length > 1.5)
                {
                    pen.Points.Add(point);
                }

                break;

            case MosaicAnnotation mosaic:
                mosaic.Rect = ClampToScreen(NormalizeRect(_annotationStart, point));
                break;

            case WatermarkAnnotation watermark:
                watermark.Rect = ClampToScreen(NormalizeRect(_annotationStart, point));
                break;

            case NumberAnnotation number:
                number.Center = point;
                break;
        }

        Editor.InvalidateVisual();
    }

    private void CommitAnnotation()
    {
        var annotation = _active;
        _active = null;
        Editor.Preview = null;

        if (annotation == null)
        {
            return;
        }

        switch (annotation)
        {
            case RectAnnotation rect when rect.Rect.Width < 2 && rect.Rect.Height < 2:
            case EllipseAnnotation ellipse when ellipse.Rect.Width < 2 && ellipse.Rect.Height < 2:
                Editor.InvalidateVisual();
                return;

            case ArrowAnnotation arrow when (arrow.End - arrow.Start).Length < 2:
                Editor.InvalidateVisual();
                return;

            case LineAnnotation line when (line.End - line.Start).Length < 2:
                Editor.InvalidateVisual();
                return;

            case PenAnnotation pen when pen.Points.Count < 2:
                Editor.InvalidateVisual();
                return;
        }

        if (annotation is MosaicAnnotation mosaic)
        {
            if (mosaic.Rect.Width < 2 || mosaic.Rect.Height < 2)
            {
                Editor.InvalidateVisual();
                return;
            }

            mosaic.Pattern = MosaicBuilder.Build(_screen, mosaic.Rect);
            if (mosaic.Pattern == null)
            {
                Editor.InvalidateVisual();
                return;
            }
        }

        if (annotation is NumberAnnotation)
        {
            _numberCounter++;
        }

        Editor.Items.Add(annotation);
        _redo.Clear();
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void BeginText(Point point)
    {
        _textActive = true;
        _textPosition = point;
        TextEditor.Text = string.Empty;
        TextEditor.Visibility = Visibility.Visible;
        Canvas.SetLeft(TextEditor, point.X / _scale + 4);
        Canvas.SetTop(TextEditor, point.Y / _scale + 4);
        TextEditor.Focus();
        Keyboard.Focus(TextEditor);
    }

    private void CommitText()
    {
        if (!_textActive)
        {
            return;
        }

        _textActive = false;
        var text = TextEditor.Text.Trim();
        TextEditor.Visibility = Visibility.Collapsed;

        if (text.Length > 0 && !_closed)
        {
            Editor.Items.Add(new TextAnnotation
            {
                Position = _textPosition,
                Text = text,
                Color = _color,
                Thickness = _thickness,
                FontSize = Math.Max(15, 10 + _thickness * 2.5)
            });
            _redo.Clear();
            UpdateUndoButtons();
            Editor.InvalidateVisual();
        }

        Focus();
        Keyboard.Focus(this);
    }

    private void CancelText()
    {
        _textActive = false;
        TextEditor.Text = string.Empty;
        TextEditor.Visibility = Visibility.Collapsed;
        Focus();
        Keyboard.Focus(this);
    }

    private void TextEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelText();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            CommitText();
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_closed || _textActive || TextEditor.IsKeyboardFocusWithin)
        {
            return;
        }

        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (e.Key == Key.Space && _dragging && _active != null && !_spaceAnnotation)
        {
            _spaceAnnotation = true;
            var cursor = Native.CursorPosition();
            _spaceAnnotationAnchor = new Point(cursor.X - _bounds.X, cursor.Y - _bounds.Y);
            _spaceAnnotationDelta = new Point();
            InputLayer.Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && _dragging && _mode == DragMode.Creating && !_spaceDrag)
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
            ToolHotkeys.TryMap(e.Key, App.Config, out var toolShortcut))
        {
            SetTool(toolShortcut);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            _hexColor = !_hexColor;
            Magnifier.HexFormat = _hexColor;
            if (Magnifier.Visibility == Visibility.Visible)
            {
                Magnifier.InvalidateVisual();
            }
        }

        if (shift && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            SetTool(e.Key switch
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

        if (!control && e.Key == Key.C)
        {
            CopyCurrentColor();
            e.Handled = true;
            return;
        }

        if (!control && e.Key == Key.R)
        {
            ApplyRecentRegion(!shift);
            e.Handled = true;
            return;
        }

        if (!control && e.Key == Key.V)
        {
            SetTool(AnnotationTool.Select);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
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

        if (control && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
            return;
        }

        if (control && (e.Key == Key.Y || (shift && e.Key == Key.Z)))
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (!_hasSelection)
        {
            return;
        }

        if (_selectedAnnotation != null && e.Key is Key.Delete or Key.Back)
        {
            RemoveSelectedAnnotation();
            e.Handled = true;
            return;
        }

        if (_selectedAnnotation != null &&
            e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var amount = shift ? 10.0 : 1.0;
            var dx = e.Key == Key.Left ? -amount : e.Key == Key.Right ? amount : 0;
            var dy = e.Key == Key.Up ? -amount : e.Key == Key.Down ? amount : 0;
            _selectedAnnotation.Translate(dx, dy);
            Editor.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = shift ? 10.0 : 1.0;
            var selection = _selection;
            switch (e.Key)
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

        if (e.Key == Key.Enter)
        {
            CopyAndClose();
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.C)
        {
            CopyAndClose();
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.S)
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

        if (control && e.Key == Key.A)
        {
            _selection = new Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight);
            _hasSelection = true;
            UpdateSelectionVisuals();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3)
        {
            PinAndClose();
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.Q)
        {
            TranslateAndClose();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
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
        if (e.Key == Key.Space && _spaceAnnotation)
        {
            _spaceAnnotation = false;
            e.Handled = true;
        }

        if (e.Key == Key.Space && _spaceDrag)
        {
            _spaceDrag = false;
            e.Handled = true;
        }
    }

    private void SetTool(AnnotationTool tool)
    {
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

        InputLayer.Cursor = ToolCursors.Get(tool);
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
        if (_tool == AnnotationTool.Select)
        {
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
            InputLayer.Cursor = ToolCursors.Get(_tool);
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
        Toolbar.RefreshUndo(Editor.Items.Count > 0, _redo.Count > 0);
    }

    private void Undo()
    {
        if (Editor.Items.Count == 0)
        {
            return;
        }

        var last = Editor.Items[^1];
        Editor.Items.RemoveAt(Editor.Items.Count - 1);
        _redo.Push(last);
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        Editor.Items.Add(_redo.Pop());
        UpdateUndoButtons();
        Editor.InvalidateVisual();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private BitmapSource Compose()
    {
        var rect = ClampToScreen(_selection);
        var x = (int)Math.Round(rect.X);
        var y = (int)Math.Round(rect.Y);
        var width = Math.Max(1, (int)Math.Round(rect.Width));
        var height = Math.Max(1, (int)Math.Round(rect.Height));

        if (Editor.Items.Count == 0)
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
            foreach (var annotation in Editor.Items)
            {
                annotation.Draw(dc);
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
        if (_textActive)
        {
            CommitText();
        }
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
        CloseWith(CaptureAction.Pin, Compose());
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
        _active = null;
        _textActive = false;
        Editor.Preview = null;
        Editor.Items.Clear();
        Editor.Source = null;
        ScreenImage.Source = null;
        BrightImage.Source = null;
        base.OnClosed(e);
    }
}
