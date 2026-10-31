using Markshot.Annotations;

namespace Markshot.UI;

public partial class PinEditWindow : Window
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x4D, 0x4F),
        Color.FromRgb(0xFF, 0xA9, 0x40),
        Color.FromRgb(0xFF, 0xD6, 0x66),
        Color.FromRgb(0x52, 0xC4, 0x1A),
        Color.FromRgb(0x4C, 0x8D, 0xFF),
        Color.FromRgb(0xB3, 0x7F, 0xEB),
        Colors.White,
        Color.FromRgb(0x20, 0x20, 0x20)
    };

    private readonly BitmapSource _source;
    private readonly Dictionary<AnnotationTool, RadioButton> _toolButtons = new();
    private readonly List<Border> _colorSwatches = new();
    private readonly Stack<Annotation> _redo = new();

    private double _scale = 1.0;
    private double _thickness = 3;
    private Color _color = Palette[0];

    private AnnotationTool _tool = AnnotationTool.Select;
    private Annotation? _active;
    private Annotation? _selected;
    private Point _annotationStart;
    private Point _lastPoint;
    private bool _movingSelected;
    private bool _erasing;
    private int _number = 1;

    private bool _textActive;
    private Point _textPosition;

    public BitmapSource? ResultImage { get; private set; }

    public PinEditWindow(BitmapSource image)
    {
        InitializeComponent();

        _source = image;
        BackgroundImage.Source = image;
        Editor.Source = image;

        InitToolButtons();
        InitPalette();
        UpdateUndoButtons();

        Loaded += OnLoaded;
        TextEditor.LostKeyboardFocus += (_, _) =>
        {
            if (_textActive)
            {
                CommitText();
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        var maxWidth = Math.Max(320, work.Width - 100);
        var maxHeight = Math.Max(240, work.Height - 200);

        var fit = Math.Min(1.0, Math.Min(maxWidth / _source.PixelWidth, maxHeight / _source.PixelHeight));
        var displayWidth = _source.PixelWidth * fit;
        var displayHeight = _source.PixelHeight * fit;

        _scale = _source.PixelWidth / displayWidth;
        Editor.Scale = _scale;
        Editor.Width = displayWidth;
        Editor.Height = displayHeight;

        BackgroundImage.Width = displayWidth;
        BackgroundImage.Height = displayHeight;
        CanvasHost.Width = displayWidth;
        CanvasHost.Height = displayHeight;

        Width = Math.Max(displayWidth + 40, 1040);
        Height = displayHeight + 110;

        InputLayer.Focus();
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

            _colorSwatches.Add(swatch);
            ColorPanel.Children.Add(swatch);
        }

        UpdateSwatchSelection();
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

    private void UpdateSwatchSelection()
    {
        for (var i = 0; i < _colorSwatches.Count; i++)
        {
            var selected = Palette[i] == _color;
            _colorSwatches[i].BorderThickness = new Thickness(selected ? 2 : 1);
            _colorSwatches[i].BorderBrush = selected
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        }
    }

    private Point ToPixels(Point dip) => new(dip.X * _scale, dip.Y * _scale);

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
                rect.Rect = Clamp(Normalize(_annotationStart, point));
                break;
            case EllipseAnnotation ellipse:
                ellipse.Rect = Clamp(Normalize(_annotationStart, point));
                break;
            case ArrowAnnotation arrow:
                arrow.End = point;
                break;
            case LineAnnotation line:
                line.End = point;
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
            mosaic.Pattern = MosaicBuilder.Build(_source, mosaic.Rect);
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
            InputLayer.Cursor = Cursors.Cross;
        }
    }

    private Rect Clamp(Rect rect)
    {
        var clamped = Rect.Intersect(rect, new Rect(0, 0, _source.PixelWidth, _source.PixelHeight));
        return clamped.Width < 1 || clamped.Height < 1 ? rect : clamped;
    }

    private static Rect Normalize(Point a, Point b)
    {
        return new Rect(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
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

            InputLayer.Cursor = tool == AnnotationTool.Select ? Cursors.Arrow : Cursors.Cross;
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

        InputLayer.Cursor = tool == AnnotationTool.Select ? Cursors.Arrow : Cursors.Cross;
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

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        CommitText();

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_source, new Rect(0, 0, _source.PixelWidth, _source.PixelHeight));
            foreach (var annotation in Editor.Items)
            {
                annotation.Draw(dc);
            }
        }

        var bitmap = new RenderTargetBitmap(
            _source.PixelWidth, _source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        ResultImage = bitmap;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_textActive || TextEditor.IsKeyboardFocusWithin)
        {
            return;
        }

        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (e.Key == Key.Escape)
        {
            if (_selected != null)
            {
                ClearSelection();
            }
            else if (_tool != AnnotationTool.Select)
            {
                SetTool(AnnotationTool.Select);
            }
            else
            {
                DialogResult = false;
            }

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
            Apply_Click(this, new RoutedEventArgs());
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
}
