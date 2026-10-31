using System.Globalization;
using System.Windows.Interop;
using Markshot.Annotations;
using Microsoft.Win32;

namespace Markshot.UI;

public partial class PinWindow : Window
{
    private readonly Stack<Annotation> _redo = new();

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
    private Point _annotationStart;
    private Point _lastPoint;
    private bool _movingSelected;
    private bool _erasing;
    private bool _spaceAnnotation;
    private Point _spaceAnnotationAnchor;
    private Point _spaceAnnotationDelta;
    private bool _textActive;
    private Point _textPosition;
    private int _number = 1;
    private double _thickness = 3;
    private Color _color = Color.FromRgb(0xFF, 0x33, 0x00);
    private bool _fill;
    private double _corner;

    public BitmapSource SourceImage { get; private set; }

    public PinWindow(BitmapSource image, int screenX, int screenY)
    {
        InitializeComponent();

        SourceImage = image;
        Img.Source = image;
        _pixelWidth = image.PixelWidth;
        _pixelHeight = image.PixelHeight;
        _left = screenX;
        _top = screenY;

        Loaded += OnLoaded;
        SourceInitialized += (_, _) => ApplyBounds();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(ApplyBounds));
        Activated += (_, _) => UpdateFrame();
        Deactivated += (_, _) => UpdateFrame();
        Closing += (_, _) =>
        {
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
        ContextMenuOpening += (_, _) => BuildContextMenu();

        InitToolbar();
        BuildContextMenu();
        UpdateFrame();
    }

    public bool IsClickThrough => _clickThrough;

    public void ReplaceImage(BitmapSource image)
    {
        SourceImage = image;
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
        _tool = tool;
        ClearSelection();
        ClearAnnotationHover();
        Toolbar.SetSelection(null);
        var active = tool is not AnnotationTool.None and not AnnotationTool.Select;
        InputLayer.IsHitTestVisible = active;
        InputLayer.Cursor = active ? ToolCursors.Get(tool) : Cursors.Arrow;
    }

    private void OnToolbarVariantChanged(AnnotationTool tool)
    {
        if (_tool == AnnotationTool.Select && _selected != null)
        {
            ConvertSelected(tool);
            return;
        }

        _tool = tool;
        var active = tool is not AnnotationTool.None and not AnnotationTool.Select;
        InputLayer.IsHitTestVisible = active;
        InputLayer.Cursor = active ? ToolCursors.Get(tool) : Cursors.Arrow;
        ClearAnnotationHover();
    }

    private void ApplyToolbarSettings()
    {
        _color = Toolbar.Color;
        _thickness = Toolbar.Thickness;
        _fill = Toolbar.FillEnabled;
        _corner = Toolbar.CornerRadius;

        if (_selected == null)
        {
            return;
        }

        _selected.Color = _color;
        _selected.Thickness = _thickness;
        _selected.Fill = _fill;
        _selected.CornerRadius = _corner;
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
                newMosaic.Pattern = MosaicBuilder.Build(SourceImage, newMosaic.Rect);
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
            ? Math.Max(imageWidth, toolbarWidth)
            : imageWidth;
        var height = imageHeight + (_editMode ? toolbarHeight + 8 : 0);

        var virtualX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var virtualY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var virtualW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var virtualH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        if (width <= virtualW)
        {
            _left = Math.Clamp(_left, virtualX, virtualX + virtualW - width);
        }

        if (height <= virtualH)
        {
            _top = Math.Clamp(_top, virtualY, virtualY + virtualH - height);
        }

        Native.SetWindowPos(
            handle,
            Topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
            _left,
            _top,
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
                    : Color.FromRgb(0x6B, 0x72, 0x80);

        FrameBorder.BorderBrush = new SolidColorBrush(color);
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
            var hit = HitAnnotation(point);
            if (hit != null)
            {
                SelectAnnotation(hit);
                _movingSelected = true;
                _lastPoint = point;
                CaptureMouse();
                e.Handled = true;
                return;
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
            return;
        }

        var cursor = Native.CursorPosition();
        _left = _dragStartLeft + (cursor.X - _dragStartCursorX);
        _top = _dragStartTop + (cursor.Y - _dragStartCursorY);
        ApplyBounds();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_movingSelected)
        {
            _movingSelected = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
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
        _textActive = false;
        TextEditor.Visibility = Visibility.Collapsed;
        Editor.Preview = null;
        Editor.Selected = null;
        Editor.Items.Clear();
        Editor.Source = SourceImage;
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

        CommitText();
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
        _textActive = false;
        TextEditor.Visibility = Visibility.Collapsed;
        Editor.Preview = null;
        Editor.Selected = null;
        Editor.Items.Clear();
        Editor.Visibility = Visibility.Collapsed;
        InputLayer.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = Visibility.Collapsed;
        _redo.Clear();
        UpdateUndoButtons();
        ApplyBounds();
    }

    private BitmapSource ComposeEditedImage()
    {
        if (Editor.Items.Count == 0)
        {
            return SourceImage;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(SourceImage, new Rect(0, 0, SourceImage.PixelWidth, SourceImage.PixelHeight));
            foreach (var annotation in Editor.Items)
            {
                annotation.Draw(dc);
            }
        }

        var bitmap = new RenderTargetBitmap(
            SourceImage.PixelWidth, SourceImage.PixelHeight, 96, 96, PixelFormats.Pbgra32);
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
        if (_textActive || TextEditor.IsKeyboardFocusWithin)
        {
            return;
        }

        if (_editMode)
        {
            OnEditKeyDown(e);
            return;
        }

        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
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
                EnterEditMode();
                e.Handled = true;
                break;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _spaceAnnotation)
        {
            _spaceAnnotation = false;
            e.Handled = true;
        }
    }

    private void OnEditKeyDown(KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (e.Key == Key.Space && _active != null && !_spaceAnnotation)
        {
            _spaceAnnotation = true;
            _spaceAnnotationAnchor = ToPixels(Mouse.GetPosition(InputLayer));
            _spaceAnnotationDelta = new Point();
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

        if (e.Key == Key.Escape)
        {
            if (_selected != null)
            {
                ClearSelection();
            }
            else
            {
                ExitEditMode(false);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            ExitEditMode(true);
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.S)
        {
            ExitEditMode(true);
            e.Handled = true;
            return;
        }

        if (!control && e.Key == Key.V)
        {
            SetTool(AnnotationTool.Select);
            e.Handled = true;
            return;
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

        if (_selected != null && e.Key is Key.Delete or Key.Back)
        {
            Editor.Items.Remove(_selected);
            ClearSelection();
            UpdateUndoButtons();
            e.Handled = true;
            return;
        }

        if (_selected != null && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var amount = shift ? 10.0 : 1.0;
            var dx = e.Key == Key.Left ? -amount : e.Key == Key.Right ? amount : 0;
            var dy = e.Key == Key.Up ? -amount : e.Key == Key.Down ? amount : 0;
            _selected.Translate(dx, dy);
            Editor.InvalidateVisual();
            e.Handled = true;
        }
    }

    private void SetTool(AnnotationTool tool)
    {
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
        InputLayer.Cursor = active ? ToolCursors.Get(tool) : Cursors.Arrow;
        ClearAnnotationHover();
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

    private void UpdateUndoButtons()
    {
        Toolbar.RefreshUndo(Editor.Items.Count > 0, _redo.Count > 0);
    }

    private Point ToPixels(Point dip) => new(dip.X / _zoom, dip.Y / _zoom);

    private void Input_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (TextEditor.Visibility == Visibility.Visible)
        {
            CommitText();
        }

        var point = ToPixels(e.GetPosition(InputLayer));
        _annotationStart = point;
        _lastPoint = point;

        if (_tool is not AnnotationTool.None and not AnnotationTool.Select and not AnnotationTool.Eraser)
        {
            var candidate = HitAnnotation(point);
            if (candidate != null && SameFamily(candidate, _tool))
            {
                SelectAnnotation(candidate);
                _movingSelected = true;
                _lastPoint = point;
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
                InputLayer.CaptureMouse();
                e.Handled = true;
                return;
            }

            ClearSelection();
            return;
        }

        if (_tool == AnnotationTool.Eraser)
        {
            _erasing = true;
            EraseAt(point);
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

        if (_active != null)
        {
            UpdateAnnotation(point);
            return;
        }

        if (_movingSelected && _selected != null)
        {
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

        if (_erasing)
        {
            EraseAt(point);
            return;
        }

        UpdateCursor(point);
        UpdateAnnotationHover(point);
    }

    private void Input_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        InputLayer.ReleaseMouseCapture();
        _spaceAnnotation = false;

        if (_erasing)
        {
            _erasing = false;
            return;
        }

        if (_movingSelected)
        {
            _movingSelected = false;
            return;
        }

        if (_active != null)
        {
            CommitAnnotation();
        }
    }

    private void StartAnnotation(Point point)
    {
        _active = _tool switch
        {
            AnnotationTool.Rectangle => new RectAnnotation
            {
                Rect = new Rect(point, point), Color = _color, Thickness = _thickness,
                Fill = _fill, CornerRadius = _corner
            },
            AnnotationTool.Ellipse => new EllipseAnnotation
            {
                Rect = new Rect(point, point), Color = _color, Thickness = _thickness, Fill = _fill
            },
            AnnotationTool.Arrow => new ArrowAnnotation
            {
                Start = point, End = point, Color = _color, Thickness = _thickness
            },
            AnnotationTool.Line => new LineAnnotation
            {
                Start = point, End = point, Color = _color, Thickness = _thickness
            },
            AnnotationTool.Mosaic => new MosaicAnnotation
            {
                Rect = new Rect(point, point), Color = _color, Thickness = _thickness
            },
            AnnotationTool.Watermark => new WatermarkAnnotation
            {
                Rect = new Rect(point, point), Color = _color, Thickness = _thickness, Opacity = 0.22
            },
            AnnotationTool.Number => new NumberAnnotation
            {
                Center = point, Number = _number, Color = _color, Thickness = _thickness,
                Radius = Math.Max(13, _thickness * 4.5)
            },
            _ => null
        };

        if (_tool == AnnotationTool.Text)
        {
            BeginText(point);
            return;
        }

        if (_tool is AnnotationTool.Pen or AnnotationTool.Highlighter)
        {
            var pen = new PenAnnotation
            {
                Color = _color,
                Thickness = _thickness,
                Highlight = _tool == AnnotationTool.Highlighter
            };
            pen.Points.Add(point);
            _active = pen;
        }

        Editor.Preview = _active;
        Editor.InvalidateVisual();
    }

    private void UpdateAnnotation(Point point)
    {
        switch (_active)
        {
            case RectAnnotation rect:
                rect.Rect = Clamp(ConstrainSquare(_annotationStart, point, _tool == AnnotationTool.Rectangle));
                break;
            case EllipseAnnotation ellipse:
                ellipse.Rect = Clamp(ConstrainSquare(_annotationStart, point, true));
                break;
            case ArrowAnnotation arrow:
                arrow.End = ConstrainLine(_annotationStart, point);
                break;
            case LineAnnotation line:
                line.End = ConstrainLine(_annotationStart, point);
                break;
            case PenAnnotation pen:
                if (pen.Points.Count == 0 || (pen.Points[^1] - point).Length > 1.5)
                {
                    pen.Points.Add(point);
                }

                break;
            case MosaicAnnotation mosaic:
                mosaic.Rect = Clamp(Normalize(_annotationStart, point));
                break;
            case WatermarkAnnotation watermark:
                watermark.Rect = Clamp(Normalize(_annotationStart, point));
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
            case ArrowAnnotation arrow when (arrow.End - arrow.Start).Length < 2:
            case LineAnnotation line when (line.End - line.Start).Length < 2:
            case PenAnnotation pen when pen.Points.Count < 2:
                Editor.InvalidateVisual();
                return;
        }

        if (annotation is MosaicAnnotation mosaic)
        {
            mosaic.Pattern = MosaicBuilder.Build(SourceImage, mosaic.Rect);
            if (mosaic.Pattern == null)
            {
                Editor.InvalidateVisual();
                return;
            }
        }

        if (annotation is NumberAnnotation)
        {
            _number++;
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
        Canvas.SetLeft(TextEditor, point.X / _zoom + 4);
        Canvas.SetTop(TextEditor, point.Y / _zoom + 4);
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

        if (text.Length > 0)
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

        InputLayer.Focus();
    }

    private void CancelText()
    {
        _textActive = false;
        TextEditor.Text = string.Empty;
        TextEditor.Visibility = Visibility.Collapsed;
        InputLayer.Focus();
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

    private Annotation? HitAnnotation(Point point)
    {
        var tolerance = 7 / (1.0 / _zoom);
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
        _selected = annotation;
        _tool = AnnotationTool.None;
        Toolbar.SetTool(AnnotationTool.None);
        Toolbar.SetSelection(annotation);
        Toolbar.SetColor(annotation.Color);
        Toolbar.SetThickness(annotation.Thickness);
        Toolbar.SetFill(annotation.Fill);
        Toolbar.SetCorner(annotation.CornerRadius);
        _color = annotation.Color;
        _thickness = annotation.Thickness;
        _fill = annotation.Fill;
        _corner = annotation.CornerRadius;
        InputLayer.IsHitTestVisible = false;
        InputLayer.Cursor = Cursors.Arrow;
        ClearAnnotationHover();
        Editor.Selected = annotation;
        Editor.InvalidateVisual();
    }

    private void ClearSelection()
    {
        _selected = null;
        Editor.Selected = null;
        if (Toolbar != null)
        {
            Toolbar.SetSelection(null);
        }

        ClearAnnotationHover();
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
        if (ReferenceEquals(hit, _selected))
        {
            ClearSelection();
        }

        _redo.Clear();
        UpdateUndoButtons();
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
            InputLayer.Cursor = ToolCursors.Get(_tool);
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
            Editor.HoverLabel = "点击选中并继续编辑";
            Editor.ShowHover = true;
            InputLayer.Cursor = Cursors.Hand;
            Editor.InvalidateVisual();
            return;
        }

        ClearAnnotationHover();
        InputLayer.Cursor = ToolCursors.Get(_tool);
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
        AnnotationTool.Pen or AnnotationTool.Highlighter => annotation is PenAnnotation,
        AnnotationTool.Mosaic or AnnotationTool.Watermark =>
            annotation is MosaicAnnotation or WatermarkAnnotation,
        AnnotationTool.Text => annotation is TextAnnotation,
        AnnotationTool.Number => annotation is NumberAnnotation,
        _ => false
    };

    private Rect Clamp(Rect rect)
    {
        var clamped = Rect.Intersect(rect, new Rect(0, 0, SourceImage.PixelWidth, SourceImage.PixelHeight));
        return clamped.Width < 1 || clamped.Height < 1 ? rect : clamped;
    }

    private static Rect Normalize(Point a, Point b)
    {
        return new Rect(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static Rect ConstrainSquare(Point start, Point end, bool enabled)
    {
        if (!enabled || (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            return Normalize(start, end);
        }

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var size = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return Normalize(
            start,
            new Point(start.X + Math.Sign(dx == 0 ? 1 : dx) * size, start.Y + Math.Sign(dy == 0 ? 1 : dy) * size));
    }

    private static Point ConstrainLine(Point start, Point end)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            return end;
        }

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001)
        {
            return end;
        }

        var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return new Point(start.X + Math.Cos(angle) * length, start.Y + Math.Sin(angle) * length);
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

        var translate = new MenuItem { Header = "翻译" };
        translate.Click += (_, _) => ((App)Application.Current).TranslateImage(CurrentImage, this);
        menu.Items.Add(translate);

        var edit = new MenuItem { Header = _editMode ? "完成标注（空格）" : "编辑标注（空格）" };
        edit.Click += (_, _) => ToggleEditing();
        menu.Items.Add(edit);

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
