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
    Translate,
    Record,
    Scroll,
    Edit
}

public partial class OverlayWindow : Window
{
    private Annotation? _freshSelection;
    private WatermarkAnnotation? _draggingWatermark;
    private Point _watermarkLast;
    private Point _transformPress;
    private Vector _resizeGrab;
    private bool _transformStarted;

    private enum DragMode
    {
        None,
        Creating,
        Moving,
        Resizing,
        Rounding
    }
    private bool _rightReset;

    /// <summary>Set by the app: creates the pin right away (before this window closes) so nothing flickers.</summary>
    public Func<OverlayWindow, BitmapSource, PinWindow?>? PinNow { get; set; }

    /// <summary>True when the pin was already created through <see cref="PinNow"/>.</summary>
    public bool PinnedDirectly { get; private set; }

    /// <summary>The pin should take its shadow from the image's shape (lasso capture).</summary>
    public bool PinShapedShadow { get; private set; }

    // Corner radius of the capture (canvas px), set by dragging a rounding dot.
    private double _cornerRadius;
    private double _cornerRadiusOrigin;
    private int _roundingCorner;

    private BitmapSource _screen; // what the capture shows: the screenshot, with the pointer when 显示鼠标 is on
    private readonly BitmapSource _screenPlain;
    private BitmapSource? _screenWithCursor;
    private CursorShot? _cursorShot;
    private bool _showCursor;
    private bool _crosshair;
    private Key? _tapKey; // a lone modifier pressed with nothing else (yet)
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
    private PenEyedropper? _eyedropperInstance;

    /// <summary>吸色 with the pen: reads the screen as it is shown (translation included).</summary>
    private PenEyedropper _eyedropper => _eyedropperInstance ??= new PenEyedropper(Editor, point => PenEyedropper.Sample(EffectiveScreen, point));
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
    // Element / window frames under the mouse, smallest first; the wheel picks the level.
    private List<HoverCandidate> _hoverChain = new();
    private int _hoverLevel;
    private (IntPtr Handle, int X, int Y)? _elementWanted;
    private bool _elementBusy;
    private (int X, int Y) _hoverScreenPoint;
    private Rect _hoverFrom;
    private Rect _hoverTo;
    private readonly Stopwatch _hoverClock = new();
    private bool _hoverAnimating;
    private Rect _pendingWindow = Rect.Empty;
    private bool _preferChild;
    private bool _hexColor = true;
    private int _recentRegionIndex = -1;

    private Annotation? _selectedAnnotation;
    private bool _draggingAnnotation;
    private Point _lastAnnotationPoint;
    private bool _erasing;
    private PenAnnotation? _eraserPath;
    private PolylineDraft? _polyline; // 折线 being placed
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

    /// <summary>Start with this area selected (screen pixels).</summary>
    public Int32Rect? InitialSelection { get; init; }

    /// <summary>For pins: the selection without annotations (translation included).</summary>
    public BitmapSource? PinBaseImage { get; private set; }

    /// <summary>For pins: the annotations in pin coordinates, still editable there.</summary>
    public List<Annotation>? PinAnnotations { get; private set; }

    /// <summary>The capture's undo steps in pin coordinates: the pin / editor goes on with them.</summary>
    public AnnotationHistory? PinHistory { get; private set; }

    /// <summary>File written by 保存 / 快速保存 (for the "open folder?" prompt).</summary>
    public string? SavedPath { get; private set; }

    /// <summary>Corner radius (pixels) of a rounded capture handed to the pin.</summary>
    public double PinCornerRadius { get; private set; }

    public OverlayWindow(BitmapSource screen, Int32Rect bounds, bool demo = false)
    {
        InitializeComponent();
        Motion.PopOnShow(Toolbar);
        // A click on the toolbar must not leave the keyboard focus somewhere that swallows Tab / Esc.
        Toolbar.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closed && !Toolbar.IsMenuOpen && _textEditor is not { IsActive: true } && _editingWatermark == null &&
                !WatermarkEditor.IsKeyboardFocusWithin && !SizeBox.IsKeyboardFocusWithin)
            {
                FocusWindow();
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
        Editor.SizeLabelVisible = false; // the editable SizeBox is the label
        UiScale.Attach(Hint);
        UiScale.Attach(SizeBox);
        UiScale.Attach(TranslateStatus);
        PreviewMouseRightButtonDown += (_, e) =>
        {
            if (_finishing)
            {
                e.Handled = true;
                return;
            }

            if (_polyline != null)
            {
                FinishPolyline(); // right click ends the polyline
                _suppressContextMenu = true;
                e.Handled = true;
                return;
            }

            if (e.LeftButton == MouseButtonState.Pressed && CancelGesture())
            {
                _suppressContextMenu = true;
                e.Handled = true;
                return;
            }

            if (e.LeftButton == MouseButtonState.Pressed || _active != null || _erasing || _textEditor is { IsActive: true })
            {
                return;
            }

            _suppressContextMenu = true;
            e.Handled = true;
            var point = ToPixels(e.GetPosition(InputLayer));
            if (!_hasSelection)
            {
                // Right-drag draws a freehand (lasso) capture; Shift adds straight segments.
                ClearHover();
                _pendingWindow = Rect.Empty;
                _lassoing = true;
                _lassoStraight = false;
                _lassoDraft = new List<Point> { point };
                Editor.LassoDraft = _lassoDraft;
                Editor.LassoPreview = null;
                Hint.Visibility = Visibility.Collapsed;
                InputLayer.CaptureMouse();
                return;
            }

            _rightReset = true; // released without dragging: the same as Esc
        };
        PreviewMouseMove += (_, e) =>
        {
            if (!_lassoing || _lassoDraft == null)
            {
                return;
            }

            var point = ToPixels(e.GetPosition(InputLayer));
            if (IsShiftDown)
            {
                Editor.LassoPreview = point;
                _lassoStraight = true;
            }
            else if (_lassoStraight || (point - _lassoDraft[^1]).Length >= 1.5 * _scale)
            {
                _lassoDraft.Add(point);
                _lassoStraight = false;
                Editor.LassoPreview = null;
            }

            Editor.InvalidateVisual();
            UpdateMagnifier(point); // the loupe follows the pen
            e.Handled = true;
        };
        PreviewMouseRightButtonUp += (_, e) =>
        {
            if (_lassoing)
            {
                if (IsShiftDown && Editor.LassoPreview is { } end)
                {
                    _lassoDraft?.Add(end);
                }

                FinishLasso();
                e.Handled = true;
            }
            else if (_rightReset)
            {
                // A right click is an Esc: out at once, or a step back (the same setting).
                _rightReset = false;
                HandleEscape();
                e.Handled = true;
            }
        };
        PreviewMouseDown += (_, e) =>
        {
            // Clicking anywhere outside the size label ends typing there (the typed size is applied).
            if (SizeBox.IsKeyboardFocusWithin && e.OriginalSource is DependencyObject source && !IsInside(source, SizeBox))
            {
                CommitSizeBox();
            }

            // Middle click inside the capture: pin it right away.
            if (e.ChangedButton == MouseButton.Middle && _hasSelection && !_finishing &&
                _selection.Contains(ToPixels(e.GetPosition(InputLayer))))
            {
                PinAndClose();
                e.Handled = true;
            }
        };
        Motion.PopOnShow(TranslateStatus);
        Motion.FadeOnShow(TranslatedImage);

        _screen = screen;
        _screenPlain = screen;
        _bounds = bounds;
        FillHint(HintText);
        Motion.PopOnShow(Hint, fromAbove: true); // the hint drops in when the capture opens (and when it comes back)
        PreviewMouseMove += (_, e) => UpdateCrosshair(e.GetPosition(Root));
        PreviewMouseDown += (_, _) => _tapKey = null; // Alt+drag and the like is no tap
        PreviewMouseLeftButtonDown += OnQuickDoubleClick;
        _demo = demo;

        ScreenImage.Source = screen;
        BrightImage.Source = screen;
        Editor.Source = screen;
        Editor.CutoutTarget = BrightImage; // 抠除 holes show the dimmed screen
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
        Loaded += (_, _) =>
        {
            // While capturing: no long blocking garbage collections (a background picture analysis allocates a lot),
            // so dragging the area never stutters. Back to normal when the capture closes.
            _latencyBefore ??= System.Runtime.GCSettings.LatencyMode;
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        };
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) =>
        {
            UpdateScale();
            UpdateSelectionVisuals();
        };
        // Shortcuts (letters, Space) must not be eaten by a Chinese IME; only the
        // text boxes take IME input.
        InputMethod.SetIsInputMethodEnabled(this, false);
        InputMethod.SetIsInputMethodEnabled(InputLayer, false); // not inherited
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
        Toolbar.WatermarkToggled += OnWatermarkToggled;
        Toolbar.VariantChanged += OnToolbarVariantChanged;
        Toolbar.SettingsChanged += ApplyToolbarSettings;
        Toolbar.UndoRequested += Undo;
        Toolbar.RedoRequested += Redo;
        Toolbar.CopyRequested += CopyAndClose;
        Toolbar.SaveRequested += SaveAndClose;
        Toolbar.PinRequested += PinAndClose;
        Toolbar.TranslateRequested += TranslateAndClose;
        Toolbar.RecordRequested += RecordAndClose;
        Toolbar.ScrollRequested += ScrollAndClose;
        Toolbar.EditRequested += EditAndClose;
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
        Toolbar.CloseRequested += CancelCapture;
        Toolbar.ClearRequested += ClearAnnotations;
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
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        // A lone Alt would put the window in menu mode (SC_KEYMENU): the mouse moves stop arriving
        // and the magnifier stands still. There is no menu here, so that never starts.
        HwndSource.FromHwnd(handle)?.AddHook((IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool handled) =>
        {
            if (message == 0x0112 /* WM_SYSCOMMAND */ && (wParam.ToInt64() & 0xFFF0) == 0xF100 /* SC_KEYMENU */)
            {
                handled = true;
            }

            return IntPtr.Zero;
        });
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
        FocusWindow();

        var handle = new WindowInteropHelper(this).Handle;
        Native.SetForegroundWindow(handle);

        PositionHint();
        StartDimConverge();

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
            if (InitialSelection is { } start)
            {
                SelectRegion(new Rect(start.X - _bounds.X, start.Y - _bounds.Y, start.Width, start.Height));
            }

            UpdateSelectionVisuals();
            WarmUpDetection();
            CodeReader.WarmUp();
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

    /// <summary>The pointer as it was when the capture started (显示鼠标 shows it in the screenshot).</summary>
    public CursorShot? CursorShot
    {
        get => _cursorShot;
        set
        {
            _cursorShot = value;
            _screenWithCursor = null;
            SetShowCursor(App.Config.CaptureShowCursor);
        }
    }

    public bool ShowsCursor => _showCursor;

    public bool ShowsCrosshair => _crosshair;

    /// <summary>显示鼠标: the screenshot with or without the pointer (copy, save, pin, mosaics … all use it).</summary>
    public void SetShowCursor(bool show)
    {
        show &= _cursorShot != null;
        _showCursor = show;
        var screen = show ? _screenWithCursor ??= CursorCapture.Paint(_screenPlain, _cursorShot!) : _screenPlain;
        if (ReferenceEquals(screen, _screen))
        {
            return;
        }

        _screen = screen;
        ScreenImage.Source = screen;
        BrightImage.Source = screen;
        Editor.Source = screen;
        Magnifier.Source = screen;
        _effectiveScreen = null;
        foreach (var mosaic in Editor.Items.Where(item => item is MosaicAnnotation or MosaicBrushAnnotation))
        {
            MosaicBuilder.Rebuild(mosaic, EffectiveScreen);
        }

        Editor.InvalidateVisual();
        Magnifier.InvalidateVisual();
    }

    private void ToggleShowCursor()
    {
        SetShowCursor(!_showCursor);
        App.Config.CaptureShowCursor = _showCursor;
        try
        {
            App.Config.Save();
        }
        catch (IOException ex)
        {
            Log.Error("保存设置失败", ex);
        }
    }

    /// <summary>辅助线 on / off; it follows the mouse until switched off (or the capture ends).</summary>
    public void SetCrosshair(bool on)
    {
        _crosshair = on;
        if (!on)
        {
            CrossH.Visibility = Visibility.Collapsed;
            CrossV.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateCrosshair(Mouse.GetPosition(Root));
    }

    private void UpdateCrosshair(Point dip)
    {
        if (!_crosshair)
        {
            return;
        }

        // One physical pixel thick, on the pixel under the mouse.
        var pixel = 1 / Math.Max(0.1, _scale);
        var x = Math.Floor(dip.X * _scale) / _scale;
        var y = Math.Floor(dip.Y * _scale) / _scale;
        CrossH.Width = Math.Max(0, Root.ActualWidth);
        CrossH.Height = pixel;
        CrossV.Width = pixel;
        CrossV.Height = Math.Max(0, Root.ActualHeight);
        Canvas.SetLeft(CrossH, 0);
        Canvas.SetTop(CrossH, y);
        Canvas.SetLeft(CrossV, x);
        Canvas.SetTop(CrossV, 0);
        CrossH.Visibility = Visibility.Visible;
        CrossV.Visibility = Visibility.Visible;
    }

    /// <summary>The capture window's own keys (显示鼠标, 辅助线) on press. True = used.</summary>
    private bool CaptureKeyDown(Key key, bool repeat)
    {
        if (key is Key.LeftAlt or Key.RightAlt && PenEyedropper.IsPen(_tool))
        {
            // With the pen, Alt is 吸色 (not a 辅助线 tap).
            _tapKey = null;
            _eyedropper.Update(_tool, ToPixels(Mouse.GetPosition(InputLayer)), _gesture != null);
            return true;
        }

        var config = App.Config;
        var modifiers = Keyboard.Modifiers;
        foreach (var configured in new[] { config.CrosshairKey, config.CaptureCursorKey })
        {
            var modifier = CaptureKey.ModifierOf(configured);
            if (modifier != ModifierKeys.None && CaptureKey.IsModifierKey(key, modifier))
            {
                if (!repeat)
                {
                    _tapKey = key; // decided on release
                }

                return false;
            }
        }

        if (!AnnotationOps.IsModifier(key))
        {
            _tapKey = null;
        }

        if (CaptureKey.Matches(config.CrosshairKey, key, modifiers))
        {
            SetCrosshair(!_crosshair);
            return true;
        }

        if (CaptureKey.Matches(config.CaptureCursorKey, key, modifiers))
        {
            ToggleShowCursor();
            return true;
        }

        return false;
    }

    /// <summary>A lone modifier released with nothing else pressed: its switch.</summary>
    private bool CaptureKeyUp(Key key)
    {
        if (key is Key.LeftAlt or Key.RightAlt)
        {
            _eyedropper.Hide();
        }

        if (_tapKey != key)
        {
            return false;
        }

        _tapKey = null;
        var config = App.Config;
        var used = false;
        if (CaptureKey.IsModifierKey(key, CaptureKey.ModifierOf(config.CrosshairKey)))
        {
            SetCrosshair(!_crosshair);
            used = true;
        }

        if (CaptureKey.IsModifierKey(key, CaptureKey.ModifierOf(config.CaptureCursorKey)))
        {
            ToggleShowCursor();
            used = true;
        }

        return used;
    }

    private System.Runtime.GCLatencyMode? _latencyBefore;

    private static readonly Dictionary<string, ImageBrush> MouseIcons = new();

    /// <summary>The capture hint: each mouse action with its mouse icon (left / right button, wheel).</summary>
    private static void FillHint(TextBlock target)
    {
        var config = App.Config;
        var parts = new List<(string? Icon, string Text)>
        {
            ("mouse_left", "拖拽选择区域"),
            ("mouse_right", "右键拖动手绘截图（Shift 直线）"),
            ("mouse_left", "点击窗口 / 元素直接选中")
        };
        if (config.DetectAuto && (config.DetectElements || config.DetectWindows))
        {
            parts.Add(("mouse_wheel", "滚轮 调整识别范围"));
        }

        parts.Add((null, $"{KeyLabel(config.CrosshairKey)} 辅助线"));
        parts.Add((null, $"{KeyLabel(config.CaptureCursorKey)} 显示鼠标"));
        parts.Add((null, "R 上次区域"));
        parts.Add(("mouse_left", "双击复制"));
        parts.Add((null, "Esc 取消"));
        target.Inlines.Clear();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                target.Inlines.Add(new System.Windows.Documents.Run("　·　"));
            }

            if (parts[i].Icon is { } name)
            {
                target.Inlines.Add(new System.Windows.Documents.InlineUIContainer(MouseIcon(name, target)) { BaselineAlignment = BaselineAlignment.Center });
            }

            target.Inlines.Add(new System.Windows.Documents.Run(parts[i].Text));
        }
    }

    /// <summary>A mouse icon in the text's colour (follows the theme).</summary>
    private static FrameworkElement MouseIcon(string name, TextBlock owner)
    {
        if (!MouseIcons.TryGetValue(name, out var mask))
        {
            mask = new ImageBrush(new BitmapImage(new Uri($"pack://application:,,,/Markshot;component/Assets/Icons/{name}.png")))
            {
                Stretch = Stretch.Uniform
            };
            mask.Freeze();
            MouseIcons[name] = mask;
        }

        var icon = new Border { Width = 15, Height = 15, Margin = new Thickness(0, 0, 4, 1), OpacityMask = mask };
        icon.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(TextBlock.Foreground)) { Source = owner });
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        return icon;
    }

    private static string KeyLabel(string? configured) => string.IsNullOrWhiteSpace(configured) ? "（未设置）" : configured.Trim();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        CancelQueuedVisuals();
        if (_latencyBefore is { } latency)
        {
            System.Runtime.GCSettings.LatencyMode = latency;
            _latencyBefore = null;
        }

        StopHoverAnimation();
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
