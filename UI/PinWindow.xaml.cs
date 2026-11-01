using System.Globalization;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

public partial class PinWindow : Window
{
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
    private int _number = 1;
    private double _thickness = 3;
    private Color _color = Color.FromRgb(0xFF, 0x33, 0x00);
    private bool _fill;
    private double _corner;

    public BitmapSource SourceImage { get; private set; }

    public PinWindow(BitmapSource image, int screenX, int screenY)
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
        _baseImage = image;
        _originalImage = image;
        Img.Source = image;
        _pixelWidth = image.PixelWidth;
        _pixelHeight = image.PixelHeight;
        _left = screenX;
        _top = screenY;

        Loaded += OnLoaded;
        SourceInitialized += (_, _) => ApplyBounds();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(ApplyBounds));
        Activated += (_, _) => UpdateFrame();
        Deactivated += (_, _) =>
        {
            UpdateFrame();
            _textEditor.Commit();
        };
        Closing += (_, _) =>
        {
            _translateCts?.Cancel();
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
                Dispatcher.BeginInvoke(new Action(ApplyBounds), DispatcherPriority.Loaded);
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
        _tool = tool;
        _thickness = Toolbar.Thickness;
        ClearSelection();
        ClearAnnotationHover();
        Toolbar.SetSelection(null);
        var active = tool is not AnnotationTool.None and not AnnotationTool.Select;
        InputLayer.IsHitTestVisible = active;
        InputLayer.Cursor = active ? ToolCursors.Get(tool, _thickness, _color) : Cursors.Arrow;
        ActivateWatermarkIfNeeded();
    }

    private void OnToolbarVariantChanged(AnnotationTool tool)
    {
        if (_tool == AnnotationTool.Select && _selected != null)
        {
            ConvertSelected(tool);
            return;
        }

        _tool = tool;
        _thickness = Toolbar.Thickness;
        var active = tool is not AnnotationTool.None and not AnnotationTool.Select;
        InputLayer.IsHitTestVisible = active;
        InputLayer.Cursor = active ? ToolCursors.Get(tool, _thickness, _color) : Cursors.Arrow;
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
        var watermark = AnnotationOps.EnsureWatermark(Editor.Items, canvas, Toolbar, _history);
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
        if (_tool is not AnnotationTool.None and not AnnotationTool.Select)
        {
            InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color);
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
        var index = Editor.Items.IndexOf(old);
        if (index < 0)
        {
            return;
        }

        _history.Checkpoint(Editor.Items);
        UpdateUndoButtons();

        Editor.Items[index] = replacement;
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
        var handle = new WindowInteropHelper(this).Handle;
        var dpi = handle != IntPtr.Zero ? Native.GetDpiForWindow(handle) / 96.0 : 1.0;

        Width = _pixelWidth / dpi;
        Height = _pixelHeight / dpi;
        Left = _left / dpi;
        Top = _top / dpi;

        ApplyBounds();
        Dispatcher.BeginInvoke(new Action(ApplyBounds), DispatcherPriority.Loaded);
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
        Editor.Width = imageWidth;
        Editor.Height = imageHeight;
        Editor.Scale = 1.0 / _zoom;

        var toolbarWidth = 0;
        var toolbarHeight = 0;
        if (_editMode)
        {
            Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            toolbarWidth = (int)Math.Ceiling(Toolbar.DesiredSize.Width);
            toolbarHeight = (int)Math.Ceiling(Toolbar.DesiredSize.Height);
        }

        var width = _editMode
            ? Math.Max(imageWidth + 32, toolbarWidth + 32)
            : imageWidth + 32;
        var height = imageHeight + 32 + (_editMode ? toolbarHeight + 8 : 0);
        var windowLeft = _left - 16;
        var windowTop = _top - 16;

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
            ImageShadowLayer.Background = new SolidColorBrush(
                Color.FromArgb(IsActive ? (byte)200 : (byte)150, color.R, color.G, color.B));
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked || (_editMode && _tool != AnnotationTool.None && _tool != AnnotationTool.Select))
        {
            return;
        }

        if (e.ClickCount == 2 && !_editMode)
        {
            Close();
            return;
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
                if (_textEditor.IsActive)
                {
                    _textEditor.Commit();
                    e.Handled = true;
                    return;
                }

                if (_selected != null && TryBeginAnnotationTransform(point))
                {
                    _history.Arm(Editor.Items);
                    CaptureMouse();
                    e.Handled = true;
                    return;
                }

                // Text in a pin reads like a document: a click opens it for
                // editing with everything selected, a drag moves it.
                if (HitAnnotation(point) is TextAnnotation pressedText)
                {
                    BeginTextPress(pressedText, point, this);
                    e.Handled = true;
                    return;
                }

                var hit = HitAnnotation(point);
                if (hit != null)
                {
                    SelectAnnotation(hit);
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
        if (UpdateTextPress(ToPixels(e.GetPosition(ImageHost))))
        {
            return;
        }

        if ((_rotatingAnnotation || _resizingAnnotation || _movingSelected) && _selected != null)
        {
            _history.CommitArmed();
            UpdateUndoButtons();
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
                _selected.Translate(dx, dy);
                _lastPoint = point;
                Editor.InvalidateVisual();
            }

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
        _left = _dragStartLeft + (cursor.X - _dragStartCursorX);
        _top = _dragStartTop + (cursor.Y - _dragStartCursorY);
        ApplyBounds();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
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
        if (_editMode)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Middle && !_locked)
        {
            ResetView();
            e.Handled = true;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_textEditor.IsActive)
        {
            _textEditor.AdjustSize(e.Delta);
            e.Handled = true;
            return;
        }

        if (_editMode && _selected != null)
        {
            AdjustSelectedByWheel(e.Delta);
            e.Handled = true;
            return;
        }

        if (_editMode && _active == null && _tool is not AnnotationTool.None and not AnnotationTool.Select)
        {
            AdjustActiveToolByWheel(e.Delta);
            e.Handled = true;
            return;
        }

        if (_locked || _editMode)
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
        _active = null;
        _selected = null;
        Editor.Preview = null;
        Editor.Selected = null;
        Editor.Source = _baseImage;
        Img.Source = _baseImage;
        Editor.Scale = 1.0 / _zoom;
        Editor.Visibility = Visibility.Visible;
        InputLayer.Visibility = Visibility.Visible;
        Toolbar.Visibility = Visibility.Visible;
        SetTool(AnnotationTool.None);
        Toolbar.SetSelection(null);
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
        Toolbar.Visibility = Visibility.Collapsed;
        _history.Clear();
        UpdateUndoButtons();
        ApplyBounds();
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
            dc.DrawImage(_baseImage, new Rect(0, 0, _baseImage.PixelWidth, _baseImage.PixelHeight));
            foreach (var annotation in Editor.Items)
            {
                AnnotationEditor.DrawItem(dc, annotation);
            }
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

        if (!_editMode &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) == 0 &&
            ToolHotkeys.TryMap(key, App.Config, out var hotkeyTool))
        {
            EnterEditMode();
            OnToolbarToolChanged(hotkeyTool);
            e.Handled = true;
            return;
        }

        if (MatchesPinKey(App.Config.PinResetHotkey, key) &&
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
                Close();
                e.Handled = true;
                break;
            case Key.C when control:
                Copy();
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

    private double EraserWidth => Math.Max(8, _thickness * 2);

    /// <summary>Rotate / resize / E-rotate / R-scale of the selected element. True when it consumed the move.</summary>
    private bool TransformSelected(Point point)
    {
        if (_selected == null)
        {
            return false;
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
                _annotationStartBounds, _selected.Rotation, _annotationHandle, point, IsShiftDown);
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
            SetTool(toolShortcut);
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
        InputLayer.Cursor = active ? ToolCursors.Get(tool, _thickness, _color) : Cursors.Arrow;
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

    private void Input_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = ToPixels(e.GetPosition(InputLayer));
        if (_textEditor.Target is { } editingText)
        {
            _textEditor.Commit();
            if (!(_tool == AnnotationTool.Text && HitAnnotation(point) is TextAnnotation other &&
                  !ReferenceEquals(other, editingText)))
            {
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

        if (_gesture != null)
        {
            _gesture.Move(point, IsShiftDown, IsCtrlDown);
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
            return;
        }

        if (_movingSelected)
        {
            _movingSelected = false;
            _history.Disarm();
            return;
        }

        if (_active != null)
        {
            CommitAnnotation();
        }
    }

    private void StartAnnotation(Point point)
    {
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
        Editor.InvalidateVisual();
    }

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
        InputLayer.Cursor = ToolCursors.Get(AnnotationTool.Text, _thickness, _color);
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
        if (_editMode)
        {
            InputLayer.Focus();
        }
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

        if (hit.IsHandle)
        {
            _resizingAnnotation = true;
            _annotationHandle = hit.Handle;
            return true;
        }

        if (hit.Rotate)
        {
            _rotatingAnnotation = true;
            _annotationStartRotation = _selected.Rotation;
            _annotationRotateStartAngle = Math.Atan2(point.Y - center.Y, point.X - center.X) * 180 / Math.PI;
            return true;
        }

        if (hit.Inside)
        {
            _movingSelected = true;
            return true;
        }

        return false;
    }

    private void ClearSelection()
    {
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
            InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color);
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
        if (_selected == null)
        {
            return false;
        }

        var hit = AnnotationEditor.HitFrame(_selected, point, 1 / _zoom);
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

        if (hit.Inside)
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
            _ => Math.Clamp(_thickness + step * 0.5, 1, 16)
        };

        _thickness = value;
        Toolbar.SetThickness(value);
        InputLayer.Cursor = ToolCursors.Get(_tool, _thickness, _color);
    }

    private void AdjustSelectedByWheel(int delta)
    {
        if (_selected == null)
        {
            return;
        }

        var step = Math.Sign(delta);
        _history.CheckpointCoalesced(Editor.Items, ("wheel", _selected));
        UpdateUndoButtons();
        switch (_selected)
        {
            case TextAnnotation text:
                text.FontSize = Math.Clamp(text.FontSize + step * 2, 8, 120);
                Toolbar.SetThickness(text.FontSize);
                _thickness = text.FontSize;
                break;
            case NumberAnnotation number:
                number.Radius = Math.Clamp(number.Radius + step * 2, 8, 80);
                Toolbar.SetThickness(number.Radius);
                _thickness = number.Radius;
                break;
            case MosaicAnnotation mosaic:
                // Wheel changes the mosaic strength.
                mosaic.BlockSize = Math.Clamp(mosaic.BlockSize + step * 2, 4, 40);
                MosaicBuilder.Rebuild(mosaic, _baseImage);
                Toolbar.SyncFrom(mosaic);
                break;
            default:
                _selected.Thickness = Math.Clamp(_selected.Thickness + step * 0.5, 1, 24);
                _thickness = _selected.Thickness;
                Toolbar.SetThickness(_thickness);
                break;
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

    private void FlipPinImage(bool horizontal)
    {
        var oldWidth = SourceImage.PixelWidth;
        var oldHeight = SourceImage.PixelHeight;
        var transform = horizontal
            ? (Transform)new ScaleTransform(-1, 1)
            : new ScaleTransform(1, -1);
        Point Map(Point p) => horizontal
            ? new Point(oldWidth - p.X, p.Y)
            : new Point(p.X, oldHeight - p.Y);
        ApplyPinImageTransform(transform, Map, oldWidth, oldHeight);
    }

    private void RotatePinImageCounterClockwise()
    {
        var oldWidth = SourceImage.PixelWidth;
        var oldHeight = SourceImage.PixelHeight;
        var transform = new RotateTransform(-90);
        Point Map(Point p) => new Point(p.Y, oldWidth - p.X);
        ApplyPinImageTransform(transform, Map, oldHeight, oldWidth);
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

    private void ResetPinImageTransform()
    {
        CancelInPlaceTranslation();
        _baseImage = _originalImage;
        SourceImage = _originalImage;
        _pixelWidth = _originalImage.PixelWidth;
        _pixelHeight = _originalImage.PixelHeight;
        _zoom = 1.0;
        _opacity = 1.0;
        Img.Opacity = 1.0;
        Editor.Items.Clear();
        _history.Clear();
        Editor.Selected = null;
        _selected = null;
        Editor.Source = _baseImage;
        Img.Source = _baseImage;
        Toolbar.SetSelection(null);
        Editor.InvalidateVisual();
        ApplyBounds();
    }

    private void ApplyPinImageTransform(Transform transform, Func<Point, Point> map, int newWidth, int newHeight)
    {
        CancelInPlaceTranslation();
        var transformed = new TransformedBitmap(SourceImage, transform);
        transformed.Freeze();

        foreach (var annotation in Editor.Items)
        {
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
                    text.Position = map(text.Position);
                    break;
                case NumberAnnotation number:
                    number.Center = map(number.Center);
                    break;
                case WatermarkAnnotation watermark:
                    watermark.Rect = MapRect(watermark.Rect, map);
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
        SourceImage = transformed;
        _pixelWidth = newWidth;
        _pixelHeight = newHeight;
        Editor.Source = transformed;
        Img.Source = transformed;
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
        var menu = new ContextMenu();

        var copy = new MenuItem { Header = "复制" };
        copy.Click += (_, _) => Copy();
        menu.Items.Add(copy);

        var save = new MenuItem { Header = "另存为…" };
        save.Click += (_, _) => Save();
        menu.Items.Add(save);

        var inPlace = new MenuItem
        {
            Header = _translateCts != null ? "正在原位翻译…" : _untranslatedBase != null ? "显示原文" : "原位翻译",
            IsEnabled = _translateCts == null
        };
        inPlace.Click += (_, _) =>
        {
            if (_untranslatedBase != null)
            {
                ShowOriginal();
            }
            else
            {
                TranslateInPlace();
            }
        };
        menu.Items.Add(inPlace);

        var translate = new MenuItem { Header = "识别并翻译文字…" };
        translate.Click += (_, _) => ((App)Application.Current).TranslateImage(CurrentImage, this);
        menu.Items.Add(translate);

        var edit = new MenuItem { Header = _editMode ? "完成标注（空格）" : "编辑标注（空格）" };
        edit.Click += (_, _) => ToggleEditing();
        menu.Items.Add(edit);

        var settings = new MenuItem { Header = "设置…" };
        settings.Click += (_, _) => ((App)Application.Current).ShowSettings();
        menu.Items.Add(settings);

        var closeAll = new MenuItem { Header = "关闭所有贴图" };
        closeAll.Click += (_, _) => ((App)Application.Current).CloseAllPins();
        menu.Items.Add(closeAll);

        menu.Items.Add(new Separator());

        var topmost = new MenuItem { Header = "置顶" };
        topmost.Click += (_, _) =>
        {
            Topmost = !Topmost;
            ApplyBounds();
        };
        menu.Items.Add(topmost);

        var lockItem = new MenuItem { Header = "锁定" };
        lockItem.Click += (_, _) =>
        {
            _locked = !_locked;
            UpdateFrame();
        };
        menu.Items.Add(lockItem);

        var clickThroughItem = new MenuItem
        {
            Header = "鼠标穿透",
            IsCheckable = true,
            IsChecked = _clickThrough
        };
        clickThroughItem.Click += (_, _) => SetClickThrough(!_clickThrough);
        menu.Items.Add(clickThroughItem);

        var reset = new MenuItem { Header = "重置大小和透明度" };
        reset.Click += (_, _) => ResetView();
        menu.Items.Add(reset);

        menu.Items.Add(new Separator());

        var close = new MenuItem { Header = "关闭" };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);

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
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            Log.Error("保存贴图失败", ex);
            MessageBox.Show(this, "保存失败：" + ex.Message, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
