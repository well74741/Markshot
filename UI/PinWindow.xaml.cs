using System.Globalization;
using System.Windows.Interop;
using Markshot.Annotations;
using Microsoft.Win32;

namespace Markshot.UI;

public partial class PinWindow : Window
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x33, 0x00),
        Color.FromRgb(0xFF, 0xAA, 0x00),
        Color.FromRgb(0xFF, 0xFF, 0x00),
        Color.FromRgb(0x00, 0xEE, 0x44),
        Color.FromRgb(0x00, 0xE5, 0xE5),
        Color.FromRgb(0x1F, 0x5B, 0xFF),
        Color.FromRgb(0xCC, 0x44, 0xFF),
        Colors.White,
        Color.FromRgb(0x20, 0x20, 0x20)
    };

    private readonly List<Border> _swatches = new();
    private readonly Stack<Annotation> _redo = new();
    private readonly Dictionary<AnnotationTool, RadioButton> _toolButtons = new();

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
    private bool _textActive;
    private Point _textPosition;
    private int _number = 1;
    private double _thickness = 3;
    private Color _color = Palette[0];
    private Color _customColor = Color.FromRgb(0x37, 0xD6, 0xC0);

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

        InitToolButtons();
        InitPalette();
        UpdateUndoButtons();
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

    private void InitToolButtons()
    {
        _toolButtons[AnnotationTool.Select] = ToolSelect;
        _toolButtons[AnnotationTool.Rectangle] = ToolRect;
        _toolButtons[AnnotationTool.Ellipse] = ToolEllipse;
        _toolButtons[AnnotationTool.Arrow] = ToolArrow;
        _toolButtons[AnnotationTool.Line] = ToolLine;
        _toolButtons[AnnotationTool.Pen] = ToolPen;
        _toolButtons[AnnotationTool.Highlighter] = ToolHighlighter;
        _toolButtons[AnnotationTool.Mosaic] = ToolMosaic;
        _toolButtons[AnnotationTool.Text] = ToolText;
        _toolButtons[AnnotationTool.Number] = ToolNumber;
        _toolButtons[AnnotationTool.Eraser] = ToolEraser;
    }

    private void InitPalette()
    {
        foreach (var color in Palette)
        {
            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = Cursors.Hand,
                ToolTip = $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            };

            swatch.MouseLeftButtonDown += (_, e) =>
            {
                SetColor(color);
                e.Handled = true;
            };

            _swatches.Add(swatch);
            ColorPanel.Children.Add(swatch);
        }

        UpdateSwatchSelection();
    }

    private void UpdateSwatchSelection()
    {
        for (var i = 0; i < _swatches.Count; i++)
        {
            var selected = Palette[i] == _color;
            _swatches[i].BorderThickness = new Thickness(selected ? 2 : 1);
            _swatches[i].BorderBrush = selected
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        }

        if (CustomColorRing != null)
        {
            var selected = _color == _customColor;
            CustomColorRing.Fill = new SolidColorBrush(_customColor);
            CustomColorRing.Stroke = selected
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
            CustomColorRing.StrokeThickness = selected ? 2 : 1.5;
        }
    }

    private void SetColor(Color color)
    {
        _color = color;
        if (_selected != null)
        {
            _selected.Color = color;
            Editor.InvalidateVisual();
        }

        UpdateSwatchSelection();
    }

    private void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPickerWindow(_color) { Owner = this };
        if (picker.ShowDialog() == true)
        {
            _customColor = picker.SelectedColor;
            SetColor(_customColor);
        }
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

        var width = _editMode ? Math.Max(imageWidth, 1040) : imageWidth;
        var height = imageHeight + (_editMode ? 46 : 0);

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
        if (_editMode || _locked)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            Close();
            return;
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
        if (!_dragging || _editMode)
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
        EditToolbar.Visibility = Visibility.Visible;
        SetTool(AnnotationTool.Select);
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
        EditToolbar.Visibility = Visibility.Collapsed;
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

    private void ApplyEdit_Click(object sender, RoutedEventArgs e) => ExitEditMode(true);

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => ExitEditMode(false);

    private void Copy_Click(object sender, RoutedEventArgs e) => Copy();

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Translate_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).TranslateImage(CurrentImage, this);

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

    private void OnEditKeyDown(KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

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
            Undo_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.Y)
        {
            Redo_Click(this, new RoutedEventArgs());
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

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } &&
            Enum.TryParse<AnnotationTool>(tag, out var tool))
        {
            _tool = tool;
            if (tool != AnnotationTool.Select)
            {
                ClearSelection();
            }

            InputLayer.Cursor = ToolCursors.Get(tool);
        }
    }

    private void SetTool(AnnotationTool tool)
    {
        _tool = tool;
        if (tool != AnnotationTool.Select)
        {
            ClearSelection();
        }

        foreach (var (key, button) in _toolButtons)
        {
            button.IsChecked = key == tool;
        }

        InputLayer.Cursor = ToolCursors.Get(tool);
    }

    private void Width_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _thickness = e.NewValue;
        if (WidthValue != null)
        {
            WidthValue.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
        }

        if (_selected != null)
        {
            _selected.Thickness = e.NewValue;
            Editor.InvalidateVisual();
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
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

    private void Redo_Click(object sender, RoutedEventArgs e)
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
        UndoButton.IsEnabled = Editor.Items.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
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

        if (_tool == AnnotationTool.Select)
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
    }

    private void Input_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        InputLayer.ReleaseMouseCapture();

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
                Rect = new Rect(point, point), Color = _color, Thickness = _thickness
            },
            AnnotationTool.Ellipse => new EllipseAnnotation
            {
                Rect = new Rect(point, point), Color = _color, Thickness = _thickness
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
        Editor.Selected = annotation;
        _color = annotation.Color;
        UpdateSwatchSelection();
        Editor.InvalidateVisual();
    }

    private void ClearSelection()
    {
        _selected = null;
        Editor.Selected = null;
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
        if (_tool == AnnotationTool.Select)
        {
            InputLayer.Cursor = HitAnnotation(point) != null ? Cursors.SizeAll : Cursors.Arrow;
        }
        else
        {
            InputLayer.Cursor = ToolCursors.Get(_tool);
        }
    }

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
