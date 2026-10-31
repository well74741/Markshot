using Markshot.Annotations;

namespace Markshot.UI;

public enum AnnotationToolbarMode
{
    Capture,
    Pin
}

public partial class AnnotationToolbar : UserControl
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x33, 0x00),
        Color.FromRgb(0xFF, 0xAA, 0x00),
        Color.FromRgb(0xFF, 0xFF, 0x00),
        Color.FromRgb(0x00, 0xEE, 0x44),
        Color.FromRgb(0x00, 0xE5, 0xE5),
        Color.FromRgb(0x1F, 0x5B, 0xFF),
        Color.FromRgb(0xCC, 0x44, 0xCC),
        Colors.White,
        Color.FromRgb(0x20, 0x20, 0x20)
    };

    private enum ToolFamily
    {
        None,
        Select,
        Shape,
        Line,
        Pen,
        Mosaic,
        Text,
        Number,
        Eraser
    }

    private readonly List<Border> _swatches = new();
    private bool _configuring;
    private AnnotationTool _tool = AnnotationTool.Select;
    private Annotation? _selection;
    private Color _color = Palette[0];
    private Color _customColor = Color.FromRgb(0x37, 0xD6, 0xC0);
    private double _thickness = 3;
    private double _corner;
    private bool _fill;
    private bool _dragging;
    private Point _dragStart;
    private double _dragLeft;
    private double _dragTop;

    public event Action<AnnotationTool>? ToolChanged;
    public event Action<AnnotationTool>? VariantChanged;
    public event Action? SettingsChanged;
    public event Action? UndoRequested;
    public event Action? RedoRequested;
    public event Action? CopyRequested;
    public event Action? SaveRequested;
    public event Action? PinRequested;
    public event Action? TranslateRequested;
    public event Action? OcrRequested;
    public event Action? CloseRequested;
    public event Action? ApplyRequested;
    public event Action<bool>? TranslateToggled;

    public AnnotationToolbar()
    {
        InitializeComponent();
        InitPalette();
        SetTool(AnnotationTool.Select, notify: false);
        SetMode(AnnotationToolbarMode.Capture);
    }

    public AnnotationTool Tool => _tool;

    public Color Color => _color;

    public double Thickness => _thickness;

    public bool FillEnabled => _fill;

    public double CornerRadius => _corner;

    public bool AutoTranslate => TranslateToggle.IsChecked == true;

    public void SetMode(AnnotationToolbarMode mode)
    {
        CaptureActions.Visibility = mode == AnnotationToolbarMode.Capture ? Visibility.Visible : Visibility.Collapsed;
        PinActions.Visibility = mode == AnnotationToolbarMode.Pin ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetTool(AnnotationTool tool, bool notify = false)
    {
        _configuring = true;
        _tool = tool;
        CheckMainButton(tool);
        ConfigureForTool(tool, _selection);
        _configuring = false;

        if (notify)
        {
            ToolChanged?.Invoke(tool);
        }
    }

    public void DeactivateTools()
    {
        _configuring = true;
        _tool = AnnotationTool.None;
        ToolShape.IsChecked = false;
        ToolPen.IsChecked = false;
        ToolArrow.IsChecked = false;
        ToolText.IsChecked = false;
        ToolNumber.IsChecked = false;
        ToolMosaic.IsChecked = false;
        ToolEraser.IsChecked = false;
        SettingsBar.Visibility = Visibility.Collapsed;
        _configuring = false;
        ToolChanged?.Invoke(AnnotationTool.None);
    }

    public void SetSelection(Annotation? annotation)
    {
        _selection = annotation;
        _configuring = true;
        if (_tool is AnnotationTool.Select or AnnotationTool.None)
        {
            if (annotation == null)
            {
                SettingsBar.Visibility = Visibility.Collapsed;
            }
            else
            {
                ConfigureForTool(AnnotationTool.Select, annotation);
            }
        }

        _configuring = false;
    }

    public void SetColor(Color color)
    {
        _color = color;
        UpdateSwatches();
    }

    public void SetThickness(double thickness)
    {
        _configuring = true;
        _thickness = thickness;
        WidthSlider.Value = Math.Clamp(thickness, WidthSlider.Minimum, WidthSlider.Maximum);
        WidthValue.Text = ((int)Math.Round(_thickness)).ToString(CultureInfo.InvariantCulture);
        _configuring = false;
    }

    public void SetFill(bool fill)
    {
        _configuring = true;
        _fill = fill;
        FillToggle.IsChecked = fill;
        _configuring = false;
    }

    public void SetCorner(double corner)
    {
        _configuring = true;
        _corner = corner;
        CornerSlider.Value = Math.Clamp(corner, CornerSlider.Minimum, CornerSlider.Maximum);
        CornerValue.Text = ((int)Math.Round(_corner)).ToString(CultureInfo.InvariantCulture);
        _configuring = false;
    }

    public void RefreshUndo(bool canUndo, bool canRedo)
    {
        UndoButton.IsEnabled = canUndo;
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
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            };

            swatch.MouseLeftButtonDown += (_, e) =>
            {
                _color = color;
                UpdateSwatches();
                SettingsChanged?.Invoke();
                e.Handled = true;
            };

            _swatches.Add(swatch);
            ColorPanel.Children.Add(swatch);
        }

        UpdateSwatches();
    }

    private void UpdateSwatches()
    {
        for (var i = 0; i < _swatches.Count; i++)
        {
            var selected = Palette[i] == _color;
            _swatches[i].BorderThickness = new Thickness(selected ? 2 : 1);
            _swatches[i].BorderBrush = selected
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        }

        var custom = !Palette.Contains(_color);
        CustomColorButton.BorderThickness = new Thickness(custom ? 2 : 0);
        CustomColorButton.BorderBrush = custom ? Brushes.White : Brushes.Transparent;
    }

    private void CheckMainButton(AnnotationTool tool)
    {
        var family = FamilyOf(tool);
        ToolShape.IsChecked = family == ToolFamily.Shape;
        ToolPen.IsChecked = family == ToolFamily.Pen;
        ToolArrow.IsChecked = family == ToolFamily.Line;
        ToolText.IsChecked = family == ToolFamily.Text;
        ToolNumber.IsChecked = family == ToolFamily.Number;
        ToolMosaic.IsChecked = family == ToolFamily.Mosaic;
        ToolEraser.IsChecked = family == ToolFamily.Eraser;
    }

    private void ConfigureForTool(AnnotationTool tool, Annotation? annotation)
    {
        var family = FamilyOf(tool);
        if (family == ToolFamily.Select && annotation != null)
        {
            family = FamilyOf(annotation);
        }

        HideVariants();
        SettingsBar.Visibility = Visibility.Visible;
        WidthGroup.Visibility = Visibility.Visible;
        ColorSection.Visibility = Visibility.Visible;
        FillToggle.Visibility = Visibility.Collapsed;
        CornerGroup.Visibility = Visibility.Collapsed;
        WidthLabel.Text = "粗细";
        WidthSlider.Minimum = 1;
        WidthSlider.Maximum = 16;

        switch (family)
        {
            case ToolFamily.None:
            case ToolFamily.Eraser:
            case ToolFamily.Select:
                SettingsBar.Visibility = Visibility.Collapsed;
                return;

            case ToolFamily.Shape:
                RectVariants.Visibility = Visibility.Visible;
                FillToggle.Visibility = Visibility.Visible;
                CornerGroup.Visibility = Visibility.Visible;
                break;

            case ToolFamily.Line:
                LineVariants.Visibility = Visibility.Visible;
                break;

            case ToolFamily.Pen:
                PenVariants.Visibility = Visibility.Visible;
                break;

            case ToolFamily.Mosaic:
                MosaicVariants.Visibility = Visibility.Visible;
                WidthLabel.Text = "块大小";
                WidthSlider.Minimum = 4;
                WidthSlider.Maximum = 40;
                break;

            case ToolFamily.Text:
                WidthLabel.Text = "字号";
                WidthSlider.Minimum = 8;
                WidthSlider.Maximum = 64;
                break;

            case ToolFamily.Number:
                WidthLabel.Text = "大小";
                WidthSlider.Minimum = 8;
                WidthSlider.Maximum = 40;
                break;
        }

        if (annotation != null)
        {
            SyncFromAnnotation(annotation);
        }
        else
        {
            SyncVariant(tool);
            WidthSlider.Value = Math.Clamp(_thickness, WidthSlider.Minimum, WidthSlider.Maximum);
            WidthValue.Text = ((int)Math.Round(WidthSlider.Value)).ToString(CultureInfo.InvariantCulture);
            CornerSlider.Value = Math.Clamp(_corner, 0, 40);
            CornerValue.Text = ((int)Math.Round(CornerSlider.Value)).ToString(CultureInfo.InvariantCulture);
            FillToggle.IsChecked = _fill;
            UpdateSwatches();
        }
    }

    private void HideVariants()
    {
        RectVariants.Visibility = Visibility.Collapsed;
        LineVariants.Visibility = Visibility.Collapsed;
        PenVariants.Visibility = Visibility.Collapsed;
        MosaicVariants.Visibility = Visibility.Collapsed;
    }

    private void SyncVariant(AnnotationTool tool)
    {
        _configuring = true;
        VariantRect.IsChecked = tool == AnnotationTool.Rectangle;
        VariantEllipse.IsChecked = tool == AnnotationTool.Ellipse;
        VariantLine.IsChecked = tool == AnnotationTool.Line;
        VariantArrow.IsChecked = tool == AnnotationTool.Arrow;
        VariantPen.IsChecked = tool == AnnotationTool.Pen;
        VariantHighlighter.IsChecked = tool == AnnotationTool.Highlighter;
        VariantMosaic.IsChecked = tool == AnnotationTool.Mosaic;
        VariantWatermark.IsChecked = tool == AnnotationTool.Watermark;
        _configuring = false;
    }

    private void SyncFromAnnotation(Annotation annotation)
    {
        _thickness = annotation.Thickness;
        _color = annotation.Color;
        _fill = annotation.Fill;
        _corner = annotation.CornerRadius;

        switch (annotation)
        {
            case RectAnnotation:
                VariantRect.IsChecked = true;
                break;
            case EllipseAnnotation:
                VariantEllipse.IsChecked = true;
                break;
            case LineAnnotation:
                VariantLine.IsChecked = true;
                break;
            case ArrowAnnotation:
                VariantArrow.IsChecked = true;
                break;
            case PenAnnotation pen:
                VariantPen.IsChecked = !pen.Highlight;
                VariantHighlighter.IsChecked = pen.Highlight;
                break;
            case MosaicAnnotation:
                VariantMosaic.IsChecked = true;
                break;
            case WatermarkAnnotation:
                VariantWatermark.IsChecked = true;
                break;
        }

        WidthSlider.Value = Math.Clamp(_thickness, WidthSlider.Minimum, WidthSlider.Maximum);
        WidthValue.Text = ((int)Math.Round(WidthSlider.Value)).ToString(CultureInfo.InvariantCulture);
        CornerSlider.Value = Math.Clamp(_corner, 0, 40);
        CornerValue.Text = ((int)Math.Round(CornerSlider.Value)).ToString(CultureInfo.InvariantCulture);
        FillToggle.IsChecked = _fill;
        UpdateSwatches();
    }

    private static ToolFamily FamilyOf(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Select => ToolFamily.Select,
        AnnotationTool.Rectangle or AnnotationTool.Ellipse => ToolFamily.Shape,
        AnnotationTool.Line or AnnotationTool.Arrow => ToolFamily.Line,
        AnnotationTool.Pen or AnnotationTool.Highlighter => ToolFamily.Pen,
        AnnotationTool.Mosaic or AnnotationTool.Watermark => ToolFamily.Mosaic,
        AnnotationTool.Text => ToolFamily.Text,
        AnnotationTool.Number => ToolFamily.Number,
        AnnotationTool.Eraser => ToolFamily.Eraser,
        _ => ToolFamily.None
    };

    private static ToolFamily FamilyOf(Annotation annotation) => annotation switch
    {
        RectAnnotation or EllipseAnnotation => ToolFamily.Shape,
        LineAnnotation or ArrowAnnotation => ToolFamily.Line,
        PenAnnotation => ToolFamily.Pen,
        MosaicAnnotation or WatermarkAnnotation => ToolFamily.Mosaic,
        TextAnnotation => ToolFamily.Text,
        NumberAnnotation => ToolFamily.Number,
        _ => ToolFamily.None
    };

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (_configuring || sender is not RadioButton { Tag: string tag } ||
            !Enum.TryParse<AnnotationTool>(tag, out var tool))
        {
            return;
        }

        if (_tool == tool)
        {
            DeactivateTools();
            return;
        }

        SetTool(tool, notify: true);
    }

    private void ShapeMenu_Click(object sender, RoutedEventArgs e) =>
        ShowVariantMenu((Button)sender, ("矩形", AnnotationTool.Rectangle), ("椭圆", AnnotationTool.Ellipse));

    private void PenMenu_Click(object sender, RoutedEventArgs e) =>
        ShowVariantMenu((Button)sender, ("画笔", AnnotationTool.Pen), ("荧光笔", AnnotationTool.Highlighter));

    private void ArrowMenu_Click(object sender, RoutedEventArgs e) =>
        ShowVariantMenu((Button)sender, ("箭头", AnnotationTool.Arrow), ("直线", AnnotationTool.Line));

    private void MosaicMenu_Click(object sender, RoutedEventArgs e) =>
        ShowVariantMenu((Button)sender, ("马赛克", AnnotationTool.Mosaic), ("水印", AnnotationTool.Watermark));

    private void TextMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (Button)sender };
        foreach (var (label, size) in new[] { ("小号 16", 16.0), ("中号 24", 24.0), ("大号 36", 36.0) })
        {
            var item = new MenuItem { Header = label, IsChecked = Math.Abs(_thickness - size) < 0.1 };
            var value = size;
            item.Click += (_, _) =>
            {
                if (_tool != AnnotationTool.Text)
                {
                    SetTool(AnnotationTool.Text, notify: true);
                }

                SetThickness(value);
                SettingsChanged?.Invoke();
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void ShowVariantMenu(Button anchor, params (string Label, AnnotationTool Tool)[] items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        foreach (var (label, tool) in items)
        {
            var item = new MenuItem { Header = label, IsChecked = FamilyOf(tool) == FamilyOf(_tool) && _tool == tool };
            var target = tool;
            item.Click += (_, _) =>
            {
                SetTool(target, notify: false);
                ToolChanged?.Invoke(target);
            };
            menu.Items.Add(item);
        }

        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void Variant_Checked(object sender, RoutedEventArgs e)
    {
        if (_configuring || sender is not RadioButton { Tag: string tag } ||
            !Enum.TryParse<AnnotationTool>(tag, out var tool))
        {
            return;
        }

        _tool = tool;
        VariantChanged?.Invoke(tool);
    }

    private void Width_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_configuring || WidthValue == null)
        {
            return;
        }

        _thickness = e.NewValue;
        WidthValue.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
        SettingsChanged?.Invoke();
    }

    private void Corner_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_configuring || CornerValue == null)
        {
            return;
        }

        _corner = e.NewValue;
        CornerValue.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
        SettingsChanged?.Invoke();
    }

    private void Fill_Click(object sender, RoutedEventArgs e)
    {
        if (_configuring)
        {
            return;
        }

        _fill = FillToggle.IsChecked == true;
        SettingsChanged?.Invoke();
    }

    private void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var picker = new ColorPickerWindow(_color);
        if (owner != null)
        {
            picker.Owner = owner;
        }

        if (picker.ShowDialog() == true)
        {
            _customColor = picker.SelectedColor;
            _color = _customColor;
            UpdateSwatches();
            SettingsChanged?.Invoke();
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();

    private void Redo_Click(object sender, RoutedEventArgs e) => RedoRequested?.Invoke();

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyRequested?.Invoke();

    private void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke();

    private void Pin_Click(object sender, RoutedEventArgs e) => PinRequested?.Invoke();

    private void Translate_Click(object sender, RoutedEventArgs e) => TranslateRequested?.Invoke();

    private void Ocr_Click(object sender, RoutedEventArgs e) => OcrRequested?.Invoke();

    private void TranslateToggle_Click(object sender, RoutedEventArgs e) =>
        TranslateToggled?.Invoke(TranslateToggle.IsChecked == true);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyRequested?.Invoke();

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Parent is not Canvas parent)
        {
            Window.GetWindow(this)?.DragMove();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(parent);
        _dragLeft = Canvas.GetLeft(this);
        _dragTop = Canvas.GetTop(this);
        if (double.IsNaN(_dragLeft)) _dragLeft = 0;
        if (double.IsNaN(_dragTop)) _dragTop = 0;
        DragHandle.CaptureMouse();
        e.Handled = true;
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || Parent is not Canvas parent)
        {
            return;
        }

        var point = e.GetPosition(parent);
        Canvas.SetLeft(this, _dragLeft + point.X - _dragStart.X);
        Canvas.SetTop(this, _dragTop + point.Y - _dragStart.Y);
    }

    private void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        DragHandle.ReleaseMouseCapture();
        e.Handled = true;
    }
}
