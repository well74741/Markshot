using Markshot.Annotations;

namespace Markshot.UI;

public enum AnnotationToolbarMode
{
    Capture,
    Pin,
    Editor
}

public partial class AnnotationToolbar : UserControl
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xD9, 0x4B, 0x38),
        Color.FromRgb(0xE4, 0xC4, 0x5A),
        Color.FromRgb(0x68, 0xB6, 0x6B),
        Color.FromRgb(0x5C, 0x8F, 0xE6),
        Color.FromRgb(0x20, 0x20, 0x20),
        Colors.White
    };

    private enum ToolFamily
    {
        None,
        Select,
        Shape,
        Line,
        Pen,
        Mosaic,
        Watermark,
        Text,
        Number,
        Eraser
    }

    private readonly List<Border> _swatches = new();
    private bool _configuring;
    private AnnotationTool _tool = AnnotationTool.Select;
    private Annotation? _selection;
    private Color _color = Palette[0];
    private Color _customColor = Color.FromRgb(0xD9, 0x4B, 0x38);
    private double _thickness = 3;
    private double _corner;
    private bool _fill;
    private Color _strokeColor = Colors.White;
    private double _strokeWidth;
    private bool _textBold;
    private bool _textItalic;
    private double _mosaicLevel = 12;
    private double _watermarkOpacity = 0.25;
    private double _watermarkAngle = -24;
    private double _eraserOpacity = 1;
    private double _opacity = 1;

    // Each tool remembers its own opacity too (Ctrl+wheel presets it).
    private readonly Dictionary<AnnotationTool, double> _toolOpacity = new();
    private bool _textBackground;
    private bool _dragging;
    private Point _dragStart;
    private double _dragLeft;
    private double _dragTop;
    private AnnotationToolbarMode _mode = AnnotationToolbarMode.Capture;

    // Each tool remembers its own size: a 3px pen must not turn new text into 8px text.
    private readonly Dictionary<AnnotationTool, double> _toolSizes = new()
    {
        [AnnotationTool.Rectangle] = 3,
        [AnnotationTool.Ellipse] = 3,
        [AnnotationTool.Line] = 3,
        [AnnotationTool.Arrow] = 4,
        [AnnotationTool.Polyline] = 3,
        [AnnotationTool.Pen] = 3,
        [AnnotationTool.Highlighter] = 4,
        [AnnotationTool.Text] = 20,
        [AnnotationTool.Number] = 14,
        [AnnotationTool.Mosaic] = 12,
        [AnnotationTool.MosaicBrush] = 24,
        [AnnotationTool.Watermark] = 24,
        [AnnotationTool.Eraser] = 16
    };

    // Variant last chosen per tool button: the button shows it and activates it.
    private readonly Dictionary<ToolFamily, AnnotationTool> _familyChoice = new()
    {
        [ToolFamily.Shape] = AnnotationTool.Rectangle,
        [ToolFamily.Pen] = AnnotationTool.Pen,
        [ToolFamily.Line] = AnnotationTool.Arrow,
        [ToolFamily.Text] = AnnotationTool.Text,
        [ToolFamily.Mosaic] = AnnotationTool.Mosaic
    };

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
    public event Action? RecordRequested;
    public event Action? ScrollRequested;
    public event Action? EditRequested;
    public event Action? OpenRequested;

    /// <summary>The watermark switch: true = show a watermark, false = remove it.</summary>
    public event Action<bool>? WatermarkToggled;
    public event Action<bool>? TranslateToggled;

    public AnnotationToolbar()
    {
        InitializeComponent();
        if (Content is FrameworkElement root)
        {
            UiScale.Attach(root); // the control's own size includes the scale: placement code keeps working
        }

        Theme.Changed += UpdateSwatches;
        Unloaded += (_, _) => Theme.Changed -= UpdateSwatches;
        Loaded += (_, _) =>
        {
            Theme.Changed -= UpdateSwatches;
            Theme.Changed += UpdateSwatches;
        };
        // Presses on the bar's own background stay here (a pin window would take them as a click
        // on empty canvas and drop the selection, closing this row).
        MouseDown += (_, e) => e.Handled = true;
        SliderDragBehavior.Enable(WidthSlider);
        SliderDragBehavior.Enable(CornerSlider);
        SliderDragBehavior.Enable(StrokeSlider);
        SliderDragBehavior.Enable(LevelSlider);
        SliderDragBehavior.Enable(OpacitySlider);
        SliderDragBehavior.Enable(AngleSlider);
        InitPalette();
        SetTool(AnnotationTool.Select, notify: false);
        SetMode(AnnotationToolbarMode.Capture);
        UpdateScrollIcon();
    }

    public AnnotationTool Tool => _tool;

    public Color Color => _color;

    public double Thickness => _thickness;

    public bool FillEnabled => _fill;

    public double CornerRadius => _corner;

    public bool AutoTranslate => TranslateToggle.IsChecked == true;

    public bool StrokeEnabled => _strokeWidth > 0;

    public Color StrokeColor => _strokeColor;

    public double StrokeWidth => _strokeWidth;

    public bool TextBold => _textBold;

    public bool TextItalic => _textItalic;

    /// <summary>Mosaic block size (strength).</summary>
    public double MosaicLevel => _mosaicLevel;

    public double WatermarkOpacity => _watermarkOpacity;

    public double WatermarkAngle => _watermarkAngle;

    /// <summary>How strongly the eraser erases (its opacity), 0.05–1.</summary>
    public double EraserOpacity => _eraserOpacity;

    /// <summary>Opacity of new / selected elements (0.05–1).</summary>
    public double ElementOpacity => _opacity;

    /// <summary>The watermark keeps its own 透明度 slider.</summary>
    private bool WatermarkActive => _selection is WatermarkAnnotation || (_selection == null && _tool == AnnotationTool.Watermark);

    /// <summary>Ctrl+wheel with a tool active: presets the next element's opacity (5 % a notch).</summary>
    public void NudgeOpacity(int delta)
    {
        if (WatermarkActive)
        {
            OpacitySlider.Value = Math.Clamp(Math.Round(_watermarkOpacity * 100) + Math.Sign(delta) * 5, OpacitySlider.Minimum, OpacitySlider.Maximum);
            return;
        }

        SetOpacity(_opacity + Math.Sign(delta) * 0.05);
    }

    /// <summary>Sets the opacity (Ctrl+wheel); remembered for the current tool or follows the selection.</summary>
    public void SetOpacity(double opacity, bool remember = true)
    {
        _opacity = Math.Clamp(Math.Round(opacity, 2), 0.05, 1);
        if (remember && _selection == null && _tool is not AnnotationTool.None and not AnnotationTool.Select)
        {
            _toolOpacity[_tool] = _opacity;
        }

        UpdateSwatches();
    }

    public bool TextBackground => _textBackground;

    public void SetMode(AnnotationToolbarMode mode)
    {
        _mode = mode;
        CaptureActions.Visibility = mode == AnnotationToolbarMode.Capture ? Visibility.Visible : Visibility.Collapsed;
        PinActions.Visibility = mode == AnnotationToolbarMode.Pin ? Visibility.Visible : Visibility.Collapsed;
        EditorActions.Visibility = mode == AnnotationToolbarMode.Editor ? Visibility.Visible : Visibility.Collapsed;
        DragHandle.Visibility = mode == AnnotationToolbarMode.Editor ? Visibility.Collapsed : Visibility.Visible;
        ApplyToolOrderFromConfig();
    }

    public void ApplyToolOrderFromConfig()
    {
        var config = App.Config;
        ApplyToolOrder(
            _mode != AnnotationToolbarMode.Capture ? config.PinToolOrder : config.CaptureToolOrder,
            _mode != AnnotationToolbarMode.Capture ? config.PinToolHidden : config.CaptureToolHidden);
        ApplyActionOrder(CaptureActions, config.CaptureActionOrder, config.CaptureActionHidden, ToolbarCatalog.CaptureActions);
        ApplyActionOrder(PinActions, config.PinActionOrder, config.PinActionHidden, ToolbarCatalog.PinActions);
    }

    // Each tool button (+ its menu arrow) sits in its own panel so it moves as one when reordering.
    private readonly Dictionary<string, StackPanel> _groupPanels = new();
    private ReorderBehavior? _toolsReorder;
    private ReorderBehavior? _captureActionsReorder;
    private ReorderBehavior? _pinActionsReorder;

    public void ApplyToolOrder(string? order, string? hidden)
    {
        if (ToolItems == null)
        {
            return;
        }

        var groups = new Dictionary<string, FrameworkElement[]>
        {
            ["Shape"] = new FrameworkElement[] { ToolShape, ShapeChevron },
            ["Pen"] = new FrameworkElement[] { ToolPen, PenChevron },
            ["Arrow"] = new FrameworkElement[] { ToolArrow, ArrowChevron },
            ["Text"] = new FrameworkElement[] { ToolText, TextChevron },
            ["Number"] = new FrameworkElement[] { ToolNumber },
            ["Mosaic"] = new FrameworkElement[] { ToolMosaic, MosaicChevron },
            ["Eraser"] = new FrameworkElement[] { ToolEraser }
        };
        var hiddenSet = ToolbarCatalog.Set(hidden);

        ToolItems.Children.Clear();
        foreach (var key in ToolbarCatalog.Order(order, ToolbarCatalog.Tools))
        {
            if (!_groupPanels.TryGetValue(key, out var panel))
            {
                panel = new StackPanel { Orientation = Orientation.Horizontal, Tag = key, Background = Brushes.Transparent };
                foreach (var element in groups[key])
                {
                    (element.Parent as Panel)?.Children.Remove(element);
                    element.Visibility = Visibility.Visible;
                    panel.Children.Add(element);
                }

                _groupPanels[key] = panel;
            }

            panel.Visibility = hiddenSet.Contains(key) ? Visibility.Collapsed : Visibility.Visible;
            ToolItems.Children.Add(panel);
        }

        if (_toolsReorder == null)
        {
            // Long-press a tool to pick it up, drag it sideways to move it.
            _toolsReorder = new ReorderBehavior(ToolItems);
            _toolsReorder.Reordered += keys => SaveOrder(keys, tools: true);
            _captureActionsReorder = new ReorderBehavior(CaptureActions);
            _captureActionsReorder.Reordered += keys => SaveOrder(keys, tools: false);
            _pinActionsReorder = new ReorderBehavior(PinActions);
            _pinActionsReorder.Reordered += keys => SaveOrder(keys, tools: false);
        }
    }

    /// <summary>Orders / hides the action buttons (by their Tag).</summary>
    private static void ApplyActionOrder(Panel panel, string? order, string? hidden, string[] defaults)
    {
        var hiddenSet = ToolbarCatalog.Set(hidden);
        var byKey = panel.Children.OfType<FrameworkElement>().ToDictionary(child => child.Tag as string ?? "", child => child);
        panel.Children.Clear();
        foreach (var key in ToolbarCatalog.Order(order, defaults))
        {
            if (byKey.TryGetValue(key, out var element))
            {
                element.Visibility = hiddenSet.Contains(key) ? Visibility.Collapsed : Visibility.Visible;
                panel.Children.Add(element);
            }
        }
    }

    private void SaveOrder(IReadOnlyList<string> keys, bool tools)
    {
        if (App.Config is not { } config)
        {
            return;
        }

        var text = string.Join(",", keys);
        var pin = _mode != AnnotationToolbarMode.Capture;
        if (tools)
        {
            if (pin) config.PinToolOrder = text; else config.CaptureToolOrder = text;
        }
        else
        {
            if (pin) config.PinActionOrder = text; else config.CaptureActionOrder = text;
        }

        config.Save();
    }

    public void SetTool(AnnotationTool tool, bool notify = false)
    {
        _configuring = true;
        _tool = tool;
        ShowChoice(tool);
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
        SettingsHost.IsOpen = false;
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
                SettingsHost.IsOpen = false;
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

    /// <summary>Shows an annotation's style (size, colour, bold…) without raising SettingsChanged.</summary>
    public void SyncFrom(Annotation annotation)
    {
        _configuring = true;
        SyncFromAnnotation(annotation);
        _configuring = false;
    }

    public void SetThickness(double thickness)
    {
        _configuring = true;
        _thickness = thickness;
        RememberSize();
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

    public void SetStroke(bool enabled, Color color, double width)
    {
        _configuring = true;
        _strokeColor = color;
        _strokeWidth = enabled ? Math.Clamp(width, 0.5, 8) : 0;
        StrokeSlider.Value = _strokeWidth;
        StrokeValue.Text = ((int)Math.Round(_strokeWidth)).ToString(CultureInfo.InvariantCulture);
        StrokeColorCircle.Fill = new SolidColorBrush(_strokeColor);
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
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = Cursors.Hand,
                ToolTip = $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            };

            swatch.RenderTransformOrigin = new Point(0.5, 0.5);
            swatch.RenderTransform = new ScaleTransform(1, 1);
            swatch.MouseEnter += (_, _) => HoverScale(swatch, 1.15);
            swatch.MouseLeave += (_, _) => HoverScale(swatch, 1);
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

    private static void HoverScale(FrameworkElement element, double to)
    {
        if (element.RenderTransform is not ScaleTransform scale)
        {
            return;
        }

        var animation = new System.Windows.Media.Animation.DoubleAnimation(to, Motion.Fast) { EasingFunction = Motion.Ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void UpdateSwatches()
    {
        for (var i = 0; i < _swatches.Count; i++)
        {
            var selected = Palette[i] == _color;
            _swatches[i].BorderThickness = new Thickness(selected ? 2 : 1);
            _swatches[i].BorderBrush = Theme.Brush(selected ? 0xFFFFFFFF : 0x5AFFFFFF);
        }

        // The custom button always previews the current colour at the current opacity.
        var custom = !Palette.Contains(_color);
        CustomColorInner.Fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(_opacity * 255), _color.R, _color.G, _color.B));
        var percent = (int)Math.Round(_opacity * 100);
        OpacityHint.Text = $"{percent}%";
        OpacityHint.Visibility = percent < 100 ? Visibility.Visible : Visibility.Collapsed;
        CustomColorButton.ToolTip = $"自定义颜色 #{_color.R:X2}{_color.G:X2}{_color.B:X2} · 透明度 {percent}%（Ctrl+滚轮）";
        CustomColorInner.Stroke = Theme.Brush(custom ? 0xFFFFFFFF : 0x5AFFFFFF);
        CustomColorInner.StrokeThickness = custom ? 2 : 1;
    }

    private void CheckMainButton(AnnotationTool tool)
    {
        var family = FamilyOf(tool);
        ToolShape.IsChecked = family == ToolFamily.Shape;
        ToolPen.IsChecked = family == ToolFamily.Pen;
        ToolArrow.IsChecked = family == ToolFamily.Line;
        ToolText.IsChecked = family is ToolFamily.Text or ToolFamily.Watermark;
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

        if (family is ToolFamily.None or ToolFamily.Select)
        {
            SettingsHost.IsOpen = false; // slides closed with its current content
            return;
        }

        HideVariants();
        SettingsHost.IsOpen = true;
        WidthGroup.Visibility = Visibility.Visible;
        ColorSection.Visibility = Visibility.Visible;
        StrokeGroup.Visibility = Visibility.Collapsed;
        TextStyleGroup.Visibility = Visibility.Collapsed;
        LevelGroup.Visibility = Visibility.Collapsed;
        OpacityGroup.Visibility = Visibility.Collapsed;
        AngleGroup.Visibility = Visibility.Collapsed;
        FillToggle.Visibility = Visibility.Collapsed;
        CornerGroup.Visibility = Visibility.Collapsed;
        WidthLabel.Text = "粗细";
        WidthSlider.Minimum = 1;
        WidthSlider.Maximum = 16;

        switch (family)
        {
            case ToolFamily.None:
            case ToolFamily.Select:
                SettingsHost.IsOpen = false;
                return;

            case ToolFamily.Eraser:
                // Eraser: width and opacity (how strongly it erases).
                WidthSlider.Minimum = 4;
                WidthSlider.Maximum = 80;
                OpacityGroup.Visibility = Visibility.Visible;
                ColorSection.Visibility = Visibility.Collapsed;
                break;

            case ToolFamily.Shape:
                RectVariants.Visibility = Visibility.Visible;
                FillToggle.Visibility = Visibility.Visible;
                CornerGroup.Visibility = Visibility.Visible;
                break;

            case ToolFamily.Line:
                if (annotation is not PolylineAnnotation)
                {
                    LineVariants.Visibility = Visibility.Visible;
                }

                if (tool == AnnotationTool.Arrow || annotation is ArrowAnnotation)
                {
                    ArrowStyleGroup.Visibility = Visibility.Visible;
                }

                break;

            case ToolFamily.Pen:
                PenVariants.Visibility = Visibility.Visible;
                break;

            case ToolFamily.Text:
                if (annotation == null)
                {
                    TextVariants.Visibility = Visibility.Visible; // 文字 / 水印
                }

                WidthLabel.Text = "字号";
                WidthSlider.Minimum = 10;
                WidthSlider.Maximum = 96;
                TextStyleGroup.Visibility = Visibility.Visible;
                StrokeGroup.Visibility = Visibility.Visible;
                break;

            case ToolFamily.Watermark:
                // Watermark: 文字 / 水印, the on / off switch, size, tilt, opacity and colour.
                TextVariants.Visibility = Visibility.Visible;
                WatermarkGroup.Visibility = Visibility.Visible;
                WidthLabel.Text = "大小";
                WidthSlider.Minimum = 10;
                WidthSlider.Maximum = 120;
                AngleGroup.Visibility = Visibility.Visible;
                OpacityGroup.Visibility = Visibility.Visible;
                break;

            case ToolFamily.Mosaic:
                MosaicVariants.Visibility = Visibility.Visible;
                if (tool == AnnotationTool.MosaicBrush || annotation is MosaicBrushAnnotation)
                {
                    WidthLabel.Text = "粗细";
                    WidthSlider.Minimum = 4;
                    WidthSlider.Maximum = 80;
                    LevelGroup.Visibility = Visibility.Visible;
                    ColorSection.Visibility = Visibility.Collapsed;
                }
                else
                {
                    WidthGroup.Visibility = Visibility.Collapsed;
                    LevelGroup.Visibility = Visibility.Visible;
                    ColorSection.Visibility = Visibility.Collapsed;
                }
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
            if (_toolSizes.TryGetValue(tool, out var remembered))
            {
                _thickness = remembered;
            }

            _opacity = _toolOpacity.TryGetValue(tool, out var opacity) ? opacity : 1;

            SyncVariant(tool);
            BoldToggle.IsChecked = _textBold;
            ItalicToggle.IsChecked = _textItalic;
            BackgroundToggle.IsChecked = _textBackground;
            LevelSlider.Value = Math.Clamp(_mosaicLevel, LevelSlider.Minimum, LevelSlider.Maximum);
            LevelValue.Text = ((int)Math.Round(_mosaicLevel)).ToString(CultureInfo.InvariantCulture);
            var shownOpacity = tool == AnnotationTool.Eraser ? _eraserOpacity : _watermarkOpacity;
            OpacitySlider.Value = Math.Round(shownOpacity * 100);
            OpacityValue.Text = $"{Math.Round(shownOpacity * 100)}%";
            AngleSlider.Value = _watermarkAngle;
            AngleValue.Text = $"{Math.Round(_watermarkAngle)}°";
            WidthSlider.Value = Math.Clamp(_thickness, WidthSlider.Minimum, WidthSlider.Maximum);
            WidthValue.Text = ((int)Math.Round(WidthSlider.Value)).ToString(CultureInfo.InvariantCulture);
            CornerSlider.Value = Math.Clamp(_corner, 0, 40);
            CornerValue.Text = ((int)Math.Round(CornerSlider.Value)).ToString(CultureInfo.InvariantCulture);
            FillToggle.IsChecked = _fill;
            UpdateSwatches();
        }
    }

    /// <summary>Shows whether the canvas has a watermark (the switch in the watermark settings).</summary>
    public void SetWatermarkOn(bool on)
    {
        _configuring = true;
        WatermarkToggle.IsChecked = on;
        WatermarkToggle.Content = on ? "开启" : "关闭";
        _configuring = false;
    }

    private void WatermarkToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_configuring)
        {
            return;
        }

        var on = WatermarkToggle.IsChecked == true;
        WatermarkToggle.Content = on ? "开启" : "关闭";
        WatermarkToggled?.Invoke(on);
    }

    private void HideVariants()
    {
        TextVariants.Visibility = Visibility.Collapsed;
        WatermarkGroup.Visibility = Visibility.Collapsed;
        RectVariants.Visibility = Visibility.Collapsed;
        LineVariants.Visibility = Visibility.Collapsed;
        ArrowStyleGroup.Visibility = Visibility.Collapsed;
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
        VariantPolyline.IsChecked = tool == AnnotationTool.Polyline;
        SyncArrowStyle();
        VariantPen.IsChecked = tool == AnnotationTool.Pen;
        VariantHighlighter.IsChecked = tool == AnnotationTool.Highlighter;
        VariantMosaic.IsChecked = tool == AnnotationTool.Mosaic;
        VariantMosaicBrush.IsChecked = tool == AnnotationTool.MosaicBrush;
        VariantText.IsChecked = tool == AnnotationTool.Text;
        VariantWatermark.IsChecked = tool == AnnotationTool.Watermark;
        _configuring = false;
    }

    private void SyncFromAnnotation(Annotation annotation)
    {
        _thickness = annotation switch
        {
            TextAnnotation text => text.FontSize,
            WatermarkAnnotation watermark => watermark.FontSize,
            NumberAnnotation number => number.Radius,
            _ => annotation.Thickness
        };
        _color = annotation.Color;
        _fill = annotation.Fill;
        _corner = annotation.CornerRadius;
        _strokeColor = annotation switch
        {
            TextAnnotation text => text.StrokeColor,
            _ => _strokeColor
        };
        _strokeWidth = annotation switch
        {
            TextAnnotation text => text.StrokeThickness,
            _ => _strokeWidth // only text has an outline; keep the setting
        };

        if (annotation is MosaicAnnotation box)
        {
            _mosaicLevel = box.BlockSize;
        }
        else if (annotation is MosaicBrushAnnotation brush)
        {
            _mosaicLevel = brush.BlockSize;
        }
        else if (annotation is WatermarkAnnotation mark)
        {
            _watermarkOpacity = mark.Opacity;
            _watermarkAngle = mark.Angle;
        }

        if (annotation is not WatermarkAnnotation)
        {
            _opacity = annotation.Opacity;
        }

        AngleSlider.Value = _watermarkAngle;
        AngleValue.Text = $"{Math.Round(_watermarkAngle)}°";

        LevelSlider.Value = Math.Clamp(_mosaicLevel, LevelSlider.Minimum, LevelSlider.Maximum);
        LevelValue.Text = ((int)Math.Round(_mosaicLevel)).ToString(CultureInfo.InvariantCulture);
        OpacitySlider.Value = Math.Round(_watermarkOpacity * 100);
        OpacityValue.Text = $"{Math.Round(_watermarkOpacity * 100)}%";

        if (annotation is TextAnnotation textAnnotation)
        {
            _textBold = textAnnotation.Bold;
            _textItalic = textAnnotation.Italic;
            _textBackground = textAnnotation.Fill;
            ItalicToggle.IsChecked = textAnnotation.Italic;
            BoldToggle.IsChecked = textAnnotation.Bold;
            BackgroundToggle.IsChecked = textAnnotation.Fill;
        }

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
            case ArrowAnnotation arrow:
                VariantArrow.IsChecked = true;
                _arrowStyle = arrow.Style;
                SyncArrowStyle();
                break;
            case PolylineAnnotation:
                VariantPolyline.IsChecked = true;
                break;
            case PenAnnotation pen:
                VariantPen.IsChecked = !pen.Highlight;
                VariantHighlighter.IsChecked = pen.Highlight;
                break;
            case MosaicAnnotation:
                VariantMosaic.IsChecked = true;
                break;
            case MosaicBrushAnnotation:
                VariantMosaicBrush.IsChecked = true;
                break;
        }

        WidthSlider.Value = Math.Clamp(_thickness, WidthSlider.Minimum, WidthSlider.Maximum);
        WidthValue.Text = ((int)Math.Round(WidthSlider.Value)).ToString(CultureInfo.InvariantCulture);
        CornerSlider.Value = Math.Clamp(_corner, 0, 40);
        CornerValue.Text = ((int)Math.Round(CornerSlider.Value)).ToString(CultureInfo.InvariantCulture);
        FillToggle.IsChecked = _fill;
        StrokeSlider.Value = Math.Clamp(_strokeWidth, 0, 8);
        StrokeValue.Text = ((int)Math.Round(StrokeSlider.Value)).ToString(CultureInfo.InvariantCulture);
        StrokeColorCircle.Fill = new SolidColorBrush(_strokeColor);
        UpdateSwatches();
    }

    private static ToolFamily FamilyOf(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Select => ToolFamily.Select,
        AnnotationTool.Rectangle or AnnotationTool.Ellipse => ToolFamily.Shape,
        AnnotationTool.Line or AnnotationTool.Arrow or AnnotationTool.Polyline => ToolFamily.Line,
        AnnotationTool.Pen or AnnotationTool.Highlighter => ToolFamily.Pen,
        AnnotationTool.Mosaic or AnnotationTool.MosaicBrush => ToolFamily.Mosaic,
        AnnotationTool.Watermark => ToolFamily.Watermark,
        AnnotationTool.Text => ToolFamily.Text,
        AnnotationTool.Number => ToolFamily.Number,
        AnnotationTool.Eraser => ToolFamily.Eraser,
        _ => ToolFamily.None
    };

    private static ToolFamily FamilyOf(Annotation annotation) => annotation switch
    {
        RectAnnotation or EllipseAnnotation => ToolFamily.Shape,
        LineAnnotation or ArrowAnnotation or PolylineAnnotation => ToolFamily.Line,
        PenAnnotation => ToolFamily.Pen,
        MosaicAnnotation or MosaicBrushAnnotation => ToolFamily.Mosaic,
        WatermarkAnnotation => ToolFamily.Watermark,
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

        if (_tool == tool || (FamilyOf(_tool) == FamilyOf(tool) && FamilyOf(tool) != ToolFamily.None) ||
            (tool == AnnotationTool.Text && _tool == AnnotationTool.Watermark) || (tool == AnnotationTool.Watermark && _tool == AnnotationTool.Text))
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

    private ArrowStyle _arrowStyle;

    /// <summary>Head of new arrows (and of a selected one when changed).</summary>
    public ArrowStyle ArrowStyle => _arrowStyle;

    private void SyncArrowStyle()
    {
        var configuring = _configuring;
        _configuring = true;
        ArrowSharp.IsChecked = _arrowStyle == ArrowStyle.Sharp;
        ArrowOpen.IsChecked = _arrowStyle == ArrowStyle.Open;
        ArrowTriangle.IsChecked = _arrowStyle == ArrowStyle.Triangle;
        ArrowDouble.IsChecked = _arrowStyle == ArrowStyle.Double;
        _configuring = configuring;
    }

    private void ArrowStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (_configuring || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<ArrowStyle>(tag, out var style))
        {
            return;
        }

        _arrowStyle = style;
        SettingsChanged?.Invoke();
    }

    private void MosaicMenu_Click(object sender, RoutedEventArgs e) =>
        ShowVariantMenu((Button)sender, ("区域马赛克", AnnotationTool.Mosaic), ("画笔马赛克", AnnotationTool.MosaicBrush));

    private void TextMenu_Click(object sender, RoutedEventArgs e) =>
        ShowVariantMenu((Button)sender, ("文字", AnnotationTool.Text), ("水印", AnnotationTool.Watermark));

    private ContextMenu? _openMenu;
    private Button? _menuClosedAnchor;
    private long _menuClosedAt;
    private bool _suppressMenuOpen;
    private readonly HashSet<Button> _menuAnchors = new();

    private void ShowAnchorMenu(Button anchor, IEnumerable<(string Label, bool Checked, Action Pick)> items)
    {
        // The arrow is an open / close switch. A press on it while its menu is open closes the menu;
        // the click that follows must not open it again. Which comes first (the menu's Closed or the
        // button's Click) varies, so the press itself is noted too.
        if (_menuAnchors.Add(anchor))
        {
            anchor.PreviewMouseLeftButtonDown += (_, _) =>
                _suppressMenuOpen = _openMenu != null ||
                                    (ReferenceEquals(_menuClosedAnchor, anchor) && Environment.TickCount64 - _menuClosedAt < 300);
        }

        if (_suppressMenuOpen || (ReferenceEquals(_menuClosedAnchor, anchor) && Environment.TickCount64 - _menuClosedAt < 300))
        {
            _suppressMenuOpen = false;
            _menuClosedAnchor = null;
            _openMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            return;
        }

        var menu = new ContextMenu { PlacementTarget = anchor };
        foreach (var (label, isChecked, pick) in items)
        {
            var item = new MenuItem { Header = label, IsChecked = isChecked };
            item.Click += (_, _) => pick();
            menu.Items.Add(item);
        }

        // While a style menu is open the windows must not take the focus back (it would close it).
        IsMenuOpen = true;
        _openMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openMenu, menu))
            {
                _openMenu = null;
            }

            IsMenuOpen = _openMenu != null;
            _menuClosedAnchor = anchor;
            _menuClosedAt = Environment.TickCount64;
        };
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ShowVariantMenu(Button anchor, params (string Label, AnnotationTool Tool)[] items) =>
        ShowAnchorMenu(anchor, items.Select(i => (i.Label, _tool == i.Tool, (Action)(() => PickVariant(i.Tool)))));

    /// <summary>长截图 direction (the button's icon shows it; Tab switches during the capture too).</summary>
    public bool ScrollHorizontal
    {
        get => App.Config?.ScrollHorizontal == true;
        set
        {
            if (App.Config is { } config)
            {
                config.ScrollHorizontal = value;
                config.Save();
            }

            UpdateScrollIcon();
        }
    }

    private void UpdateScrollIcon()
    {
        if (ScrollIcon != null)
        {
            ScrollIcon.Style = (Style)FindResource(ScrollHorizontal ? "IconScrollHMask" : "IconScrollMask");
        }
    }

    private void ScrollMenu_Click(object sender, RoutedEventArgs e) =>
        ShowAnchorMenu((Button)sender, new (string, bool, Action)[]
        {
            ("竖向长截图", !ScrollHorizontal, () => ScrollHorizontal = false),
            ("横向长截图", ScrollHorizontal, () => ScrollHorizontal = true)
        });

    private void Variant_Checked(object sender, RoutedEventArgs e)
    {
        if (_configuring || sender is not RadioButton { Tag: string tag } ||
            !Enum.TryParse<AnnotationTool>(tag, out var tool))
        {
            return;
        }

        _tool = tool;
        if (_selection == null)
        {
            _configuring = true;
            ConfigureForTool(tool, null);
            _configuring = false;
        }

        ShowChoice(tool);
        VariantChanged?.Invoke(tool);
    }

    /// <summary>
    /// Switches to another style of a button (menu, Tab): the window converts a selected
    /// element of that button, or simply changes the tool.
    /// </summary>
    public bool IsMenuOpen { get; private set; }

    public void PickVariant(AnnotationTool tool)
    {
        SetTool(tool, notify: false);
        VariantChanged?.Invoke(tool);
    }

    /// <summary>The style last chosen for the button <paramref name="tool"/> belongs to (hotkeys use it).</summary>
    public AnnotationTool ChoiceFor(AnnotationTool tool)
    {
        var family = FamilyOf(tool);
        if (family == ToolFamily.Watermark)
        {
            family = ToolFamily.Text;
        }

        return _familyChoice.TryGetValue(family, out var choice) && AnnotationOps.SameVariantFamily(choice, tool) ? choice : tool;
    }

    /// <summary>The tool button of a family shows (and activates) the variant chosen last.</summary>
    private void ShowChoice(AnnotationTool tool)
    {
        var family = FamilyOf(tool);
        if (family == ToolFamily.Watermark)
        {
            family = ToolFamily.Text;
        }

        if (!_familyChoice.ContainsKey(family))
        {
            return;
        }

        _familyChoice[family] = tool;
        var (button, icon, style) = family switch
        {
            ToolFamily.Shape => (ToolShape, ToolShapeIcon, tool == AnnotationTool.Ellipse ? "IconEllipseMask" : "IconRectMask"),
            ToolFamily.Pen => (ToolPen, ToolPenIcon, tool == AnnotationTool.Highlighter ? "IconHighlighterMask" : "IconPenMask"),
            ToolFamily.Line => (ToolArrow, ToolArrowIcon, tool switch
            {
                AnnotationTool.Line => "IconLineMask",
                AnnotationTool.Polyline => "IconPolylineMask",
                _ => "IconArrowMask"
            }),
            ToolFamily.Text => (ToolText, ToolTextIcon, tool == AnnotationTool.Watermark ? "IconWatermarkMask" : "IconTextMask"),
            _ => (ToolMosaic, ToolMosaicIcon, tool == AnnotationTool.MosaicBrush ? "IconMosaicBrushMask" : "IconMosaicMask")
        };
        button.Tag = tool.ToString();
        icon.Style = (Style)FindResource(style);
    }

    private void Width_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_configuring || WidthValue == null)
        {
            return;
        }

        _thickness = e.NewValue;
        RememberSize();
        WidthValue.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
        SettingsChanged?.Invoke();
    }

    private void RememberSize()
    {
        var tool = _selection != null ? ToolOf(_selection) : _tool;
        if (_toolSizes.ContainsKey(tool))
        {
            _toolSizes[tool] = _thickness;
        }
    }

    private static AnnotationTool ToolOf(Annotation annotation) => annotation switch
    {
        RectAnnotation => AnnotationTool.Rectangle,
        EllipseAnnotation => AnnotationTool.Ellipse,
        LineAnnotation => AnnotationTool.Line,
        ArrowAnnotation => AnnotationTool.Arrow,
        PolylineAnnotation => AnnotationTool.Polyline,
        PenAnnotation { Highlight: true } => AnnotationTool.Highlighter,
        PenAnnotation => AnnotationTool.Pen,
        TextAnnotation => AnnotationTool.Text,
        NumberAnnotation => AnnotationTool.Number,
        MosaicAnnotation => AnnotationTool.Mosaic,
        MosaicBrushAnnotation => AnnotationTool.MosaicBrush,
        WatermarkAnnotation => AnnotationTool.Watermark,
        _ => AnnotationTool.None
    };

    private void TextStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_configuring)
        {
            return;
        }

        _textBold = BoldToggle.IsChecked == true;
        _textItalic = ItalicToggle.IsChecked == true;
        _textBackground = BackgroundToggle.IsChecked == true;
        SettingsChanged?.Invoke();
    }

    /// <summary>Ctrl+I while typing or with a text selected.</summary>
    public void ToggleItalic()
    {
        ItalicToggle.IsChecked = ItalicToggle.IsChecked != true;
        TextStyle_Click(ItalicToggle, new RoutedEventArgs());
    }

    private void Level_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_configuring || LevelValue == null)
        {
            return;
        }

        _mosaicLevel = Math.Round(e.NewValue);
        LevelValue.Text = ((int)_mosaicLevel).ToString(CultureInfo.InvariantCulture);
        SettingsChanged?.Invoke();
    }

    private void Angle_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_configuring || AngleValue == null)
        {
            return;
        }

        _watermarkAngle = Math.Round(e.NewValue);
        AngleValue.Text = $"{_watermarkAngle}°";
        SettingsChanged?.Invoke();
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_configuring || OpacityValue == null)
        {
            return;
        }

        if (_tool == AnnotationTool.Eraser && _selection == null)
        {
            _eraserOpacity = Math.Clamp(e.NewValue / 100, 0.05, 1);
        }
        else
        {
            _watermarkOpacity = Math.Clamp(e.NewValue / 100, 0.05, 1);
        }

        OpacityValue.Text = $"{Math.Round(e.NewValue)}%";
        SettingsChanged?.Invoke();
    }

    /// <summary>Ctrl+B while typing or with a text selected.</summary>
    public void ToggleBold()
    {
        BoldToggle.IsChecked = BoldToggle.IsChecked != true;
        TextStyle_Click(BoldToggle, new RoutedEventArgs());
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

    private void Stroke_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_configuring || StrokeValue == null)
        {
            return;
        }

        _strokeWidth = e.NewValue < 0.25 ? 0 : e.NewValue; // 0 = no outline
        StrokeValue.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
        SettingsChanged?.Invoke();
    }

    private void StrokeColor_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var picker = new ColorPickerWindow(_strokeColor);
        if (owner != null)
        {
            picker.Owner = owner;
        }

        picker.ShowDialog();
        if (picker.Accepted)
        {
            _strokeColor = picker.SelectedColor;
            StrokeColorCircle.Fill = new SolidColorBrush(_strokeColor);
            SettingsChanged?.Invoke();
        }
    }

    private void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        // The picker's alpha channel is the element opacity.
        var alpha = WatermarkActive ? _watermarkOpacity : _opacity;
        var picker = new ColorPickerWindow(Color.FromArgb((byte)Math.Round(alpha * 255), _color.R, _color.G, _color.B));
        if (owner != null)
        {
            picker.Owner = owner;
        }

        picker.ShowDialog();
        if (picker.Accepted)
        {
            var chosen = picker.SelectedColor;
            _customColor = Color.FromRgb(chosen.R, chosen.G, chosen.B);
            _color = _customColor;
            if (WatermarkActive)
            {
                OpacitySlider.Value = Math.Clamp(Math.Round(chosen.A / 2.55), OpacitySlider.Minimum, OpacitySlider.Maximum);
            }
            else
            {
                SetOpacity(chosen.A / 255.0);
            }

            UpdateSwatches();
            SettingsChanged?.Invoke();
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();

    private void Redo_Click(object sender, RoutedEventArgs e) => RedoRequested?.Invoke();

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyRequested?.Invoke();

    private void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke();

    private void Pin_Click(object sender, RoutedEventArgs e) => PinRequested?.Invoke();

    private void Record_Click(object sender, RoutedEventArgs e) => RecordRequested?.Invoke();

    private void Scroll_Click(object sender, RoutedEventArgs e) => ScrollRequested?.Invoke();

    private void Edit_Click(object sender, RoutedEventArgs e) => EditRequested?.Invoke();

    private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();

    /// <summary>Bar actions added by the image editor (it has its own Pin handling).</summary>
    public bool IsEditorMode => _mode == AnnotationToolbarMode.Editor;

    private void Translate_Click(object sender, RoutedEventArgs e) => TranslateRequested?.Invoke();

    private void Ocr_Click(object sender, RoutedEventArgs e) => OcrRequested?.Invoke();

    /// <summary>Sets the translate toggle without raising <see cref="TranslateToggled"/>.</summary>
    public void SetTranslateEnabled(bool enabled)
    {
        TranslateToggle.IsChecked = enabled;
    }

    private void TranslateToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = TranslateToggle.IsChecked == true;
        TranslateToggled?.Invoke(enabled);
    }

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
