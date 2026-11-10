using System.Windows.Interop;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

// Capture window: the selection (lasso, corners, size label, hint, toolbar placement, magnifier).
public partial class OverlayWindow
{
    // Freehand (lasso) capture: path points relative to the capture's top-left at _lassoSize.
    private List<Point>? _lasso;

    private Size _lassoSize;

    private List<Point>? _lassoDraft;

    private bool _lassoing;

    private bool _lassoStraight;

    /// <summary>The lasso as a closed shape over the current capture (canvas pixels), or null.</summary>
    private Geometry? LassoGeometry()
    {
        if (_lasso is not { Count: > 2 } || _lassoSize.Width < 1 || _lassoSize.Height < 1)
        {
            return null;
        }

        var sx = _selection.Width / _lassoSize.Width;
        var sy = _selection.Height / _lassoSize.Height;
        var points = _lasso.Select(p => new Point(_selection.X + p.X * sx, _selection.Y + p.Y * sy)).ToList();
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            context.PolyLineTo(points.Skip(1).ToList(), true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Rounding dot under the point (-1 = none).</summary>
    private int HitCornerDot(Point point)
    {
        if (!_hasSelection || _lasso != null || _selection.Width < 40 || _selection.Height < 40)
        {
            return -1;
        }

        var dots = AnnotationEditor.CornerDots(_selection, _cornerRadius, _scale);
        for (var i = 0; i < dots.Length; i++)
        {
            if ((dots[i] - point).Length <= 7 * _scale)
            {
                return i;
            }
        }

        return -1;
    }

    private void UpdateSelectionVisuals()
    {
        if (_scale <= 0 || Root.ActualWidth <= 1)
        {
            return;
        }

        if (!_hasSelection || _selection.Width <= 0 || _selection.Height <= 0)
        {
            ClearCodes();
            CancelTranslation();
            UpdateTranslationVisuals();
            BrightClip.Rect = new Rect(0, 0, 0, 0);
            UpdateHoverBright(); // before an area is chosen: the framed element is bright
            Editor.Selection = Rect.Empty;
            Editor.ShowSelectionChrome = false;
            Editor.InvalidateVisual();
            ShowToolbar(false);
            HideSizeBox();
            Hint.Visibility = Visibility.Visible;
            PositionHint();
            return;
        }

        BrightClip.Rect = new Rect(
            _selection.X / _scale - 1,
            _selection.Y / _scale - 1,
            _selection.Width / _scale + 2,
            _selection.Height / _scale + 2);
        BrightClip.RadiusX = BrightClip.RadiusY = _cornerRadius / _scale;

        var lassoShape = LassoGeometry();
        if (lassoShape != null)
        {
            var dip = lassoShape.Clone();
            dip.Transform = new ScaleTransform(1 / _scale, 1 / _scale);
            BrightImage.Clip = dip;
        }
        else if (!ReferenceEquals(BrightImage.Clip, BrightClip))
        {
            BrightImage.Clip = BrightClip;
        }

        Editor.SelectionPath = lassoShape;
        Editor.Selection = _selection;
        Editor.SelectionCornerRadius = _cornerRadius;
        Editor.ShowSelectionChrome = true;
        Editor.SelectionDragging = _dragging && _mode is DragMode.Creating or DragMode.Moving or DragMode.Resizing;
        if (AnnotationOps.FindWatermark(Editor.Items) is { } watermark)
        {
            watermark.Rect = _selection;
        }

        Editor.InvalidateVisual();
        UpdateTranslationVisuals();
        if (!SelectionDragging)
        {
            ScheduleCodes();
        }
        else
        {
            HideCodeNow();
        }

        if (_autoTranslate && !SelectionDragging && SnapRect(_selection) != _translatedRect &&
            SnapRect(_selection) != _translatingRect)
        {
            _translateDebounce.Stop();
            _translateDebounce.Start();
        }

        Hint.Visibility = Visibility.Collapsed;
        if (_mode == DragMode.Resizing && _dragging)
        {
            Magnifier.Visibility = Visibility.Visible; // resizing aims at pixels; moving the area does not need the loupe
        }
        else if (_mode == DragMode.Moving && _dragging)
        {
            Magnifier.Visibility = Visibility.Collapsed;
        }

        if (_finishing)
        {
            return;
        }

        ShowSizeBox();
        if (_dragging)
        {
            ShowToolbar(false);
        }
        else
        {
            ShowToolbar(true);
            PositionToolbar();
        }
    }

    /// <summary>The size label (editable) follows the capture; the canvas does not draw its own.</summary>
    private void UpdateSizeBox(Point point)
    {
    }

    private void ShowSizeBox()
    {
        if (!SizeBox.IsKeyboardFocusWithin)
        {
            var rect = SnapRect(_selection);
            SizeWidthBox.Text = ((int)rect.Width).ToString(CultureInfo.InvariantCulture);
            SizeHeightBox.Text = ((int)rect.Height).ToString(CultureInfo.InvariantCulture);
        }

        SizeBox.Visibility = Visibility.Visible;
        PositionSizeBox();
    }

    private void HideSizeBox()
    {
        SizeBox.Visibility = Visibility.Collapsed;
        if (SizeBox.IsKeyboardFocusWithin)
        {
            FocusWindow();
        }
    }

    private void PositionSizeBox()
    {
        SizeBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = _selection.X / _scale;
        var top = _selection.Y / _scale - SizeBox.DesiredSize.Height - 4;
        if (top < 2)
        {
            top = _selection.Y / _scale + 4; // no room above: just inside the top edge
        }

        Canvas.SetLeft(SizeBox, Math.Max(2, left));
        Canvas.SetTop(SizeBox, top);
    }

    private void SizeBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (AnnotationOps.RealKey(e) == Key.Enter)
        {
            ApplyTypedSize();
            e.Handled = true;
        }
        else if (AnnotationOps.RealKey(e) == Key.Escape)
        {
            FocusWindow();
            ShowSizeBox(); // the current numbers back
            e.Handled = true;
        }
    }

    /// <summary>
    /// Gives the keyboard to the window itself. The window is a focus scope: focusing it alone would
    /// hand the focus straight back to the box that had it, so its remembered element is reset first.
    /// </summary>
    private void FocusWindow()
    {
        FocusManager.SetFocusedElement(this, this);
        Keyboard.Focus(this);
    }

    /// <summary>Leaves the size label: applies the typed numbers when they changed, else just drops the focus.</summary>
    private void CommitSizeBox()
    {
        var rect = SnapRect(_selection);
        if (SizeWidthBox.Text.Trim() != ((int)rect.Width).ToString(CultureInfo.InvariantCulture) ||
            SizeHeightBox.Text.Trim() != ((int)rect.Height).ToString(CultureInfo.InvariantCulture))
        {
            ApplyTypedSize();
            return;
        }

        FocusWindow();
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        while (element != null)
        {
            if (ReferenceEquals(element, container))
            {
                return true;
            }

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    /// <summary>Resizes the capture to the typed width × height, keeping its top-left corner.</summary>
    private void ApplyTypedSize()
    {
        if (!int.TryParse(SizeWidthBox.Text.Trim(), out var width) || !int.TryParse(SizeHeightBox.Text.Trim(), out var height) ||
            width < 2 || height < 2)
        {
            FocusWindow();
            ShowSizeBox(); // put the current numbers back
            return;
        }

        var x = Math.Min(_selection.X, Math.Max(0, _screen.PixelWidth - width));
        var y = Math.Min(_selection.Y, Math.Max(0, _screen.PixelHeight - height));
        _selection = ClampToScreen(new Rect(x, y, width, height));
        _cornerRadius = Math.Min(_cornerRadius, Math.Min(_selection.Width, _selection.Height) / 2);
        _lasso = null; // a typed size makes it a rectangle again
        FocusWindow();
        UpdateSelectionVisuals();
    }

    /// <summary>Shows / hides the toolbar with the pop transition; reversing mid-way continues from where it is.</summary>
    private void ShowToolbar(bool show)
    {
        if (show)
        {
            if (Toolbar.Visibility != Visibility.Visible)
            {
                Toolbar.Visibility = Visibility.Visible; // pops in (Motion.PopOnShow)
            }
            else if (Motion.IsHiding(Toolbar))
            {
                Motion.PopIn(Toolbar, duration: Motion.Bar);
            }

            return;
        }

        if (Toolbar.Visibility == Visibility.Visible && !Motion.IsHiding(Toolbar))
        {
            Motion.PopOut(Toolbar, duration: Motion.Bar);
        }
    }

    private readonly List<Border> _extraHints = new();

    /// <summary>The hint sits at the top centre of every monitor (copies follow the first one's visibility).</summary>
    private void PositionHint()
    {
        Hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var monitors = Core.Recording.Monitors.All()
            .Where(m => m.Bounds.X < _bounds.X + _bounds.Width && m.Bounds.X + m.Bounds.Width > _bounds.X &&
                        m.Bounds.Y < _bounds.Y + _bounds.Height && m.Bounds.Y + m.Bounds.Height > _bounds.Y)
            .ToList();
        if (monitors.Count <= 1)
        {
            Canvas.SetLeft(Hint, Math.Max(8, (ActualWidth - Hint.DesiredSize.Width) / 2));
            Canvas.SetTop(Hint, 28);
            return;
        }

        while (_extraHints.Count < monitors.Count - 1)
        {
            var copy = new Border
            {
                Background = Hint.Background,
                BorderBrush = Hint.BorderBrush,
                BorderThickness = Hint.BorderThickness,
                CornerRadius = Hint.CornerRadius,
                Padding = Hint.Padding,
                IsHitTestVisible = false,
                LayoutTransform = UiScale.Transform,
                Child = new TextBlock
                {
                    Foreground = ((TextBlock)Hint.Child).Foreground,
                    FontSize = ((TextBlock)Hint.Child).FontSize
                }
            };
            FillHint((TextBlock)copy.Child);
            copy.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = Hint });
            copy.SetBinding(OpacityProperty, new System.Windows.Data.Binding(nameof(Opacity)) { Source = Hint });
            UiLayer.Children.Insert(UiLayer.Children.IndexOf(Hint) + 1, copy);
            _extraHints.Add(copy);
        }

        for (var i = 0; i < monitors.Count; i++)
        {
            var element = i == 0 ? Hint : _extraHints[i - 1];
            var b = monitors[i].Bounds;
            var left = (b.X - _bounds.X) / _scale;
            var width = b.Width / _scale;
            Canvas.SetLeft(element, left + Math.Max(8, (width - Hint.DesiredSize.Width) / 2));
            Canvas.SetTop(element, (b.Y - _bounds.Y) / _scale + 28);
        }
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

        // Under the capture's left edge: a wider settings row grows to the right and the tool buttons stay put;
        // only when the screen ends does the bar move left.
        var left = selection.Left;
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
        PositionTranslateStatus(); // it keeps out of the bar's way
    }

    private int HitHandle(Point point)
    {
        if (!_hasSelection || _lasso != null)
        {
            return -1; // a lasso capture can be moved, not resized
        }

        var tolerance = 12 / _scale;
        var selection = _selection;
        if (DrawingTool)
        {
            // With a pen / shape in hand the frame is still adjustable, from a narrow band on its inner side
            // (the full band outside): drawing close to the edge keeps working.
            var inner = 5 / _scale;
            var core = new Rect(selection.X + inner, selection.Y + inner,
                Math.Max(0, selection.Width - inner * 2), Math.Max(0, selection.Height - inner * 2));
            if (core.Contains(point))
            {
                return -1;
            }
        }

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

    /// <summary>Any annotation tool in hand (the frame handles still come first at the capture's edge).</summary>
    private bool DrawingTool => _tool is not (AnnotationTool.None or AnnotationTool.Select);

    /// <summary>The cursor for the capture's edge / corner under the point; null when the point is not on one.</summary>
    private Cursor? FrameCursorAt(Point point) => HitHandle(point) switch
    {
        0 or 4 => Cursors.SizeNWSE,
        2 or 6 => Cursors.SizeNESW,
        1 or 5 => Cursors.SizeNS,
        3 or 7 => Cursors.SizeWE,
        _ => null
    };

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

        SelectRegion(local);
    }

    /// <summary>Selects an area (window pixels), starting the capture over from it.</summary>
    private void SelectRegion(Rect local)
    {
        local = Rect.Intersect(local, new Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight));
        if (local.IsEmpty || local.Width < 2 || local.Height < 2)
        {
            return;
        }

        _selection = local;
        _hasSelection = true;
        ApplyRememberedCorner();
        _pendingWindow = Rect.Empty;
        _mode = DragMode.None;
        ClearHover();
        Editor.Items.Clear();
        _history.Clear();
        ClearAnnotationSelection();
        UpdateUndoButtons();
        UpdateSelectionVisuals();
    }

    /// <summary>
    /// The frames under the mouse, again on every move: the screenshot's pieces (instant once the
    /// window's region is worked out) and the UI Automation elements found last that still contain
    /// the mouse; a new UI Automation lookup runs in the background (newest position wins).
    /// </summary>
    private void UpdateHover(Point point)
    {
        var config = App.Config;
        ElementDetector.MinSide = AppConfig.MinSideFor(config.DetectRange);
        if (_hasSelection || _dragging || !config.DetectAuto || (!config.DetectWindows && !config.DetectElements) ||
            (_tool != AnnotationTool.None && _tool != AnnotationTool.Select))
        {
            ClearHover();
            return;
        }

        var screenX = _bounds.X + (int)Math.Round(point.X);
        var screenY = _bounds.Y + (int)Math.Round(point.Y);
        _hoverScreenPoint = (screenX, screenY);
        var target = _windowFinder != null
            ? _windowFinder(screenX, screenY)
            : WindowDetector.Detect(screenX, screenY, _preferChild && !config.DetectElements);
        var window = target == null ? Rect.Empty : ToLocal(target.Bounds);
        if (target == null || window.IsEmpty)
        {
            ClearHover();
            return;
        }

        if (_hoverTarget?.Handle != target.Handle)
        {
            _uiaElements = new List<DetectedElement>(); // another window: what was found belongs to the old one
            _detailFresh = false;
        }

        _hoverTarget = target;
        // Inside the window only of the program in use when the capture started; any other window is framed whole.
        if (!config.DetectElements || target.Own || (_detailWindow != target.Handle && (config.DetectRange <= 1 || !IsActiveProgram(target.Handle))))
        {
            SetHoverChain(new List<HoverCandidate> { new(window, target.Label) });
            return;
        }

        RefreshHoverChain();
        RequestElements(target.Handle, screenX, screenY);
    }

    /// <summary>For tests: the window under a screen point (instead of the desktop's).</summary>
#pragma warning disable CS0649 // set by tests
    private Func<int, int, WindowTarget?>? _windowFinder;
#pragma warning restore CS0649

    /// <summary>The UI Automation elements of the last lookup (screen pixels).</summary>
    private List<DetectedElement> _uiaElements = new();
    private bool _preparing;

    /// <summary>The frames at the mouse from what is known now.</summary>
    private void RefreshHoverChain()
    {
        if (_closed || _hoverTarget == null || _hasSelection || _dragging)
        {
            return;
        }

        var window = ToLocal(_hoverTarget.Bounds);
        var region = new Int32Rect((int)window.X, (int)window.Y, (int)window.Width, (int)window.Height);
        var (sx, sy) = _hoverScreenPoint;
        var pieces = new List<Int32Rect>();
        // While the picture is being worked out the UI thread does not touch it (asking would wait for the lock).
        if (!_preparing && !_segmenterFailed)
        {
            if (_segmenter is { } segmenter && segmenter.IsReady(region))
            {
                pieces = segmenter.At(region, sx - _bounds.X, sy - _bounds.Y);
            }
            else
            {
                PrepareRegion(region);
            }
        }

        var mouse = new Point(sx, sy);
        var elements = _uiaElements.Where(element => element.Bounds.Contains(mouse)).ToList();
        SetHoverChain(BuildHoverChain(pieces, elements));
    }

    /// <summary>A frame offered on hover: an element or the window, in this window's pixels.</summary>
    private sealed record HoverCandidate(Rect Local, string Label)
    {
        /// <summary>The whole monitor / all monitors: reached with the wheel only, never kept as a 识别范围.</summary>
        public bool Screen { get; init; }
    }

    private List<Rect>? _monitorRects;

    /// <summary>
    /// The chain with the screen above the window: the monitor under the pointer, then all monitors when there are
    /// several (when windows cover everything, the wheel still reaches the whole screen).
    /// </summary>
    private List<HoverCandidate> WithScreens(List<HoverCandidate> chain)
    {
        if (chain.Count == 0 || chain[^1].Screen)
        {
            return chain;
        }

        var whole = new Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight);
        if (_monitorRects == null)
        {
            try
            {
                _monitorRects = Markshot.Core.Recording.Monitors.All()
                    .Select(m => ToLocal(new Rect(m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height)))
                    .Where(r => !r.IsEmpty)
                    .ToList();
            }
            catch (Exception)
            {
                _monitorRects = new List<Rect>();
            }
        }

        var (sx, sy) = _hoverScreenPoint;
        var pointer = new Point(sx - _bounds.X, sy - _bounds.Y);
        var monitor = _monitorRects.FirstOrDefault(r => r.Contains(pointer));
        var result = new List<HoverCandidate>(chain);
        void Add(Rect rect, string label)
        {
            if (!rect.IsEmpty && rect.Width >= 4 && !result.Any(c => Close(c.Local, rect)))
            {
                result.Add(new HoverCandidate(rect, label) { Screen = true });
            }
        }

        if (!monitor.IsEmpty && !Close(monitor, whole))
        {
            Add(monitor, "整个屏幕");
            Add(whole, "全部屏幕");
        }
        else
        {
            Add(whole, "整个屏幕");
        }

        return result;
    }

    /// <summary>A screen rectangle (physical pixels) in this window's pixels, cut to the screen; empty when too small.</summary>
    private Rect ToLocal(Rect screen)
    {
        var local = Rect.Intersect(new Rect(screen.X - _bounds.X, screen.Y - _bounds.Y, screen.Width, screen.Height),
            new Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight));
        return local.IsEmpty || local.Width < 4 || local.Height < 4 ? Rect.Empty : local;
    }

    /// <summary>Looks up the elements off the UI thread; only the latest mouse position is looked up next.</summary>
    private void RequestElements(IntPtr handle, int x, int y)
    {
        _elementWanted = (handle, x, y);
        if (!_elementBusy)
        {
            RunElementDetection();
        }
    }

    /// <summary>
    /// Looks up the UI Automation elements at the newest wanted position on a worker thread and
    /// hands them to this window's thread (explicitly: they touch the window, whatever context started the lookup).
    /// </summary>
    private void RunElementDetection()
    {
        if (_closed || _elementWanted is not { } wanted)
        {
            _elementBusy = false;
            return;
        }

        _elementBusy = true;
        _elementWanted = null;
        Task.Run(() =>
        {
            try
            {
                return ElementDetector.Detect(wanted.Handle, wanted.X, wanted.Y);
            }
            catch (Exception ex)
            {
                Log.Error("元素识别失败", ex);
                return new List<DetectedElement>();
            }
        }).ContinueWith(lookup => Dispatcher.BeginInvoke(() =>
        {
            if (_hoverTarget?.Handle == wanted.Handle)
            {
                _uiaElements = lookup.Result;
                RefreshHoverChain();
            }

            RunElementDetection(); // the position wanted meanwhile, if any
        }), TaskScheduler.Default);
    }

    private ScreenSegmenter? _segmenter;
    private readonly object _segmenterGate = new();
    private bool _segmenterFailed;

    /// <summary>Picks pieces out of the screenshot (without the pointer drawn on it).</summary>
    private ScreenSegmenter Segmenter
    {
        get
        {
            lock (_segmenterGate)
            {
                if (_segmenter == null)
                {
                    var (pixels, width, height) = ImageOps.ReadPixels(_screenPlain);
                    _segmenter = new ScreenSegmenter(pixels, width, height, _scale, App.Config.DetectRange);
                }

                return _segmenter;
            }
        }
    }

    /// <summary>Works out a window's pieces on a worker thread, then looks again.</summary>
    private void PrepareRegion(Int32Rect region)
    {
        if (!_screenPlain.IsFrozen)
        {
            _ = Segmenter; // only this thread can read it
        }

        _preparing = true;
        Task.Run(() =>
        {
            var thread = Thread.CurrentThread;
            var priority = thread.Priority;
            try
            {
                thread.Priority = ThreadPriority.BelowNormal; // never in the way of dragging the area
                Segmenter.Prepare(region);
            }
            catch (Exception ex)
            {
                _segmenterFailed = true; // not again on every move
                Log.Error("画面识别失败", ex);
            }
            finally
            {
                thread.Priority = priority;
            }
        }).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            _preparing = false;
            RefreshHoverChain();
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// When the capture opens: the program in use is looked at ahead of the first move, so the first frame is
    /// there at once (the screenshot's pieces, and the first contact with UI Automation, which can take a while).
    /// </summary>
    private void WarmUpDetection()
    {
        var config = App.Config;
        if (_demo || _hasSelection || !config.DetectAuto || !config.DetectElements || config.DetectRange <= 1 || ActiveWindow == IntPtr.Zero)
        {
            return;
        }

        var window = ToLocal(WindowDetector.BoundsOf(ActiveWindow));
        if (window.IsEmpty)
        {
            return;
        }

        ElementDetector.MinSide = AppConfig.MinSideFor(config.DetectRange);
        PrepareRegion(new Int32Rect((int)window.X, (int)window.Y, (int)window.Width, (int)window.Height));
        var handle = ActiveWindow;
        var cursor = Native.CursorPosition();
        Task.Run(() =>
        {
            try
            {
                ElementDetector.Detect(handle, cursor.X, cursor.Y);
            }
            catch (Exception ex)
            {
                Log.Info("元素识别预热失败：" + ex.Message);
            }
        });
    }

    // ---- the wheel: what the frame takes, said in a small tip

    private DispatcherTimer? _wheelTipTimer;

    /// <summary>The tip at the bottom middle of the screen the pointer is on (it does not follow the pointer); it goes by itself.</summary>
    private void ShowWheelTip(string text)
    {
        WheelTipText.Text = text;
        Motion.SetShown(WheelTip, true, Motion.Fast);
        WheelTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = WheelTip.DesiredSize;
        var scale = Math.Max(0.01, _scale);
        var screen = new Rect(0, 0, ActualWidth, ActualHeight);
        try
        {
            var (sx, sy) = _hoverScreenPoint;
            var b = Core.Recording.Monitors.At(sx, sy).Bounds;
            var local = Rect.Intersect(new Rect((b.X - _bounds.X) / scale, (b.Y - _bounds.Y) / scale, b.Width / scale, b.Height / scale), screen);
            if (!local.IsEmpty && local.Width > size.Width)
            {
                screen = local;
            }
        }
        catch (Exception)
        {
            // no monitor list: the bottom of the capture
        }

        _wheelTipCenter = screen.X + screen.Width / 2;
        _wheelTipBottom = screen.Bottom;
        PlaceWheelTip(size);
        if (!_wheelTipTracked)
        {
            // Centred on its real size once laid out (a longer or shorter label than measured kept the old middle).
            _wheelTipTracked = true;
            WheelTip.SizeChanged += (_, e) => PlaceWheelTip(e.NewSize);
        }
        if (_wheelTipTimer == null)
        {
            _wheelTipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _wheelTipTimer.Tick += (_, _) => HideWheelTip();
        }

        _wheelTipTimer.Stop();
        _wheelTipTimer.Start();
    }

    private double _wheelTipCenter;
    private double _wheelTipBottom;
    private bool _wheelTipTracked;

    private void PlaceWheelTip(Size size)
    {
        var left = _wheelTipCenter - size.Width / 2;
        var top = _wheelTipBottom - size.Height - 72;
        Canvas.SetLeft(WheelTip, Math.Max(4, Math.Min(left, Math.Max(4, ActualWidth - size.Width - 4))));
        Canvas.SetTop(WheelTip, Math.Max(4, Math.Min(top, Math.Max(4, ActualHeight - size.Height - 4))));
    }

    private void HideWheelTip()
    {
        _wheelTipTimer?.Stop();
        if (WheelTip.Visibility == Visibility.Visible)
        {
            Motion.SetShown(WheelTip, false, Motion.Fast);
        }
    }

    /// <summary>The 识别范围 a frame belongs to: the smallest range that would take it first.</summary>
    private int RangeOf(Rect frame, Rect window)
    {
        if (window.IsEmpty || Close(frame, window))
        {
            return 1;
        }

        for (var range = 2; range <= 3; range++)
        {
            if (AppConfig.StartsAt(range, frame, window, _scale))
            {
                return range;
            }
        }

        return 4;
    }

    private static string RangeName(int range) => range switch { 1 => "识别范围：窗口", 2 => "识别范围：大区块", 4 => "识别范围：细小元素", _ => "识别范围：常规元素" };

    /// <summary>The wheel changed the 识别范围 (saved when the capture closes).</summary>
    public bool RangeChanged { get; private set; }

    /// <summary>The 识别范围 when the capture started (put back afterwards when it is not to be remembered).</summary>
    public int RangeAtStart { get; } = App.Config?.DetectRange ?? 3;

    private static string ShortLabel(string label)
    {
        var at = label.IndexOf(" · ", StringComparison.Ordinal);
        return at > 0 ? label[..at] : label;
    }

    private IntPtr _detailWindow; // a window the wheel went into (its elements are offered although it is not the program in use)
    private bool _detailFresh;

    /// <summary>
    /// The wheel over a frame with nothing else to step to (the 窗口 range, another program's window):
    /// down goes into the window, up says it is the largest already. False: no window under the pointer.
    /// </summary>
    private bool WheelInWindow(int delta)
    {
        if (_hoverTarget is not { } target)
        {
            return false;
        }

        var name = _hoverChain.Count > 0 ? ShortLabel(_hoverChain[^1].Label) : "窗口";
        if (delta > 0)
        {
            ShowWheelTip($"已是最大范围：{name}");
            return true;
        }

        var opened = _detailWindow == target.Handle || (App.Config.DetectRange > 1 && IsActiveProgram(target.Handle));
        if (opened || !App.Config.DetectElements || target.Own)
        {
            ShowWheelTip("这里没有更小的元素");
            return true;
        }

        _detailWindow = target.Handle;
        _detailFresh = true;
        if (App.Config.DetectRange <= 1)
        {
            App.Config.DetectRange = 2; // into the window: the range is kept from here on
            RangeChanged = true;
        }

        ShowWheelTip("正在识别窗口里的元素…");
        var (x, y) = _hoverScreenPoint;
        RefreshHoverChain();
        RequestElements(target.Handle, x, y);
        return true;
    }

    /// <summary>Everything found under the mouse, smallest first, each size once; the window last.</summary>
    private List<HoverCandidate> BuildHoverChain(IReadOnlyList<Int32Rect> pieces, IReadOnlyList<DetectedElement> elements)
    {
        var window = _hoverTarget == null ? Rect.Empty : ToLocal(_hoverTarget.Bounds);
        var found = new List<HoverCandidate>();
        foreach (var element in elements)
        {
            var local = ToLocal(element.Bounds);
            if (!local.IsEmpty && !window.IsEmpty)
            {
                local = Rect.Intersect(local, window);
            }

            found.Add(new HoverCandidate(local, element.Label));
        }

        foreach (var piece in pieces)
        {
            var local = new Rect(piece.X, piece.Y, piece.Width, piece.Height);
            var textLike = piece.Height <= 44 * _scale && piece.Width >= piece.Height * 1.5;
            found.Add(new HoverCandidate(local, textLike ? "文字" : "区域"));
        }

        var chain = new List<HoverCandidate>();
        foreach (var candidate in found.Where(c => !c.Local.IsEmpty && c.Local.Width >= 4 && c.Local.Height >= 4)
                     .OrderBy(c => c.Local.Width * c.Local.Height))
        {
            if (!chain.Any(seen => Close(seen.Local, candidate.Local)) && !Close(candidate.Local, window))
            {
                chain.Add(candidate);
            }
        }

        if (App.Config.DetectWindows && !window.IsEmpty && _hoverTarget != null)
        {
            chain.Add(new HoverCandidate(window, _hoverTarget.Label));
        }

        return chain;
    }

    /// <summary>The window of the program that was in front when the capture started (its elements are offered).</summary>
    private bool IsActiveProgram(IntPtr window)
    {
        if (ActiveWindow == IntPtr.Zero)
        {
            return true; // not known (tests, the desktop): as before
        }

        var root = Native.GetAncestor(window, 2 /* GA_ROOT */);
        return root == ActiveWindow || window == ActiveWindow;
    }

    /// <summary>The window in front before the capture opened (set by the app; the capture itself is not it).</summary>
    public IntPtr ActiveWindow { get; set; }

    /// <summary>Two frames the user could not tell apart.</summary>
    private static bool Close(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) <= 3 && Math.Abs(a.Y - b.Y) <= 3 && Math.Abs(a.Right - b.Right) <= 3 && Math.Abs(a.Bottom - b.Bottom) <= 3;

    private int _startLevel;

    /// <summary>The first frame at the 识别范围: the smallest that is big enough (the window when none is).</summary>
    private int StartLevel(List<HoverCandidate> chain)
    {
        var window = _hoverTarget == null ? Rect.Empty : ToLocal(_hoverTarget.Bounds);
        var range = App.Config.DetectRange;
        // The screen frames above the window are for the wheel only: never where a frame starts.
        var count = chain.Count(c => !c.Screen);
        if (count == 0)
        {
            _startLevel = 0;
            return 0;
        }

        if (_detailFresh && count >= 2)
        {
            // The wheel went into the window: one step in from it.
            _detailFresh = false;
            _startLevel = -1;
            var window2 = _hoverTarget == null ? Rect.Empty : ToLocal(_hoverTarget.Bounds);
            var inner = count - 2;
            for (var i = 0; i < count - 1; i++)
            {
                if (AppConfig.StartsAt(App.Config.DetectRange, chain[i].Local, window2, _scale))
                {
                    inner = i; // the kept range, as far as this window has it
                    break;
                }
            }

            ShowWheelTip($"{RangeName(App.Config.DetectRange)} · {ShortLabel(chain[inner].Label)} · 滚轮：上 变大，下 变小");
            return inner;
        }

        for (var i = 0; i < count; i++)
        {
            if (window.IsEmpty || Close(chain[i].Local, window) || AppConfig.StartsAt(range, chain[i].Local, window, _scale))
            {
                _startLevel = i;
                return i;
            }
        }

        _startLevel = count - 1;
        return _startLevel;
    }

    /// <summary>New frames under the mouse. The level the wheel chose stays when its frame is still offered.</summary>
    /// <summary>A new element under the pointer: the frame goes there at once.</summary>
    private void SetHoverChain(List<HoverCandidate> chain) => ApplyHoverChain(chain);

    private void ApplyHoverChain(List<HoverCandidate> chain)
    {
        chain = WithScreens(chain);
        var shown = _hoverLevel < _hoverChain.Count ? _hoverChain[_hoverLevel].Local : Rect.Empty;
        _hoverChain = chain;
        if (chain.Count == 0)
        {
            HideHoverFrame();
            return;
        }

        var keep = chain.FindIndex(candidate => ElementDetector.Same(candidate.Local, shown));
        _hoverLevel = !_detailFresh && keep >= 0 && _hoverLevel > _startLevel ? keep : StartLevel(chain);
        if (Editor.ShowHover && ElementDetector.Same(chain[_hoverLevel].Local, shown))
        {
            return; // the same frame: nothing to redraw
        }

        ShowHoverLevel();
    }

    /// <summary>Wheel over a hover frame: up = the element around it, down = back in. False when there is no choice.</summary>
    private bool StepHoverLevel(int delta)
    {
        if (_hoverChain.Count < 2)
        {
            return false;
        }

        // Only the window below the screen: down goes into the window (WheelInWindow).
        var windowLocal = _hoverTarget == null ? Rect.Empty : ToLocal(_hoverTarget.Bounds);
        if (delta < 0 && _hoverLevel == 0 && !windowLocal.IsEmpty && Close(_hoverChain[0].Local, windowLocal))
        {
            return false;
        }

        var before = _hoverLevel;
        var fromScreen = _hoverChain[Math.Clamp(before, 0, _hoverChain.Count - 1)].Screen;
        _hoverLevel = Math.Clamp(_hoverLevel + (delta > 0 ? 1 : -1), 0, _hoverChain.Count - 1);
        _startLevel = -1; // the wheel's choice is kept while the pointer stays
        var frame = _hoverChain[_hoverLevel];
        var name = ShortLabel(frame.Label);
        if (before == _hoverLevel)
        {
            ShowWheelTip(delta > 0 ? $"已是最大范围：{name}" : $"已是最小范围：{name}");
        }
        else if (frame.Screen)
        {
            ShowWheelTip($"{name}（临时，不记住档位）· 滚轮：下 回到窗口");
        }
        else
        {
            // The range of what is framed now is kept: the next element, the next capture start from it.
            // No window under the pointer: nothing to measure the range against, so nothing is kept;
            // coming back from the screen leaves the kept range as it was.
            if (_hoverTarget == null || fromScreen)
            {
                ShowWheelTip($"{name} · 滚轮：上 变大，下 变小");
            }
            else
            {
                var range = RangeOf(frame.Local, ToLocal(_hoverTarget.Bounds));
                if (range != App.Config.DetectRange)
                {
                    App.Config.DetectRange = range;
                    RangeChanged = true;
                }

                ShowWheelTip($"{RangeName(range)}{(App.Config.DetectRangeRemember ? "（已记住）" : "")} · {name} · 滚轮：上 变大，下 变小");
            }
        }

        ShowHoverLevel();
        return true;
    }

    private void ShowHoverLevel()
    {
        var candidate = _hoverChain[_hoverLevel];
        _hoverLocal = candidate.Local;
        Editor.HoverLabel = $"{candidate.Label}  {Math.Round(candidate.Local.Width)} × {Math.Round(candidate.Local.Height)}";
        var from = Editor.HoverBounds;
        if (!Editor.ShowHover || from.IsEmpty || ElementDetector.Same(from, candidate.Local))
        {
            StopHoverAnimation();
            Editor.HoverBounds = candidate.Local;
            Editor.ShowHover = true;
            UpdateHoverBright();
            Editor.InvalidateVisual();
            return;
        }

        // The frame slides / resizes to the new element.
        _hoverFrom = from;
        _hoverTo = candidate.Local;
        _hoverClock.Restart();
        if (!_hoverAnimating)
        {
            _hoverAnimating = true;
            CompositionTarget.Rendering += OnHoverFrame;
        }
    }

    private const double HoverMs = 140;

    /// <summary>The automatically framed area is shown bright (undimmed), like a chosen one; the rest stays dim.</summary>
    private void UpdateHoverBright()
    {
        if (_hasSelection || _scale <= 0)
        {
            Editor.HoverCornerRadius = 0; // from here on the hover frame marks annotations
            return;
        }

        var hover = Editor.ShowHover ? Editor.HoverBounds : Rect.Empty;
        // Previewed with the rounded corners the area will get when clicked.
        var corner = hover.IsEmpty ? 0 : Math.Min(Math.Max(0, App.Config.CaptureCornerRadius), Math.Min(hover.Width, hover.Height) / 2);
        Editor.HoverCornerRadius = corner;
        var bright = hover.IsEmpty || hover.Width <= 0
            ? Rect.Empty
            : new Rect(hover.X / _scale, hover.Y / _scale, hover.Width / _scale, hover.Height / _scale);
        if (bright.IsEmpty && !_brightShown.IsEmpty)
        {
            FadeBright(_brightShown, BrightClip.RadiusX); // pressed / left the element: it dims gradually
        }
        else if (!bright.IsEmpty)
        {
            StopBrightFade();
        }

        _brightShown = bright;
        BrightClip.RadiusX = BrightClip.RadiusY = corner / _scale;
        BrightClip.Rect = bright.IsEmpty ? new Rect(0, 0, 0, 0) : bright;
        if (!ReferenceEquals(BrightImage.Clip, BrightClip))
        {
            BrightImage.Clip = BrightClip;
        }
    }

    private Rect _brightShown = Rect.Empty;
    private object? _brightFadeToken;

    /// <summary>What was bright fades to the dim of the rest (240 ms).</summary>
    private void FadeBright(Rect area, double radius)
    {
        var token = new object();
        _brightFadeToken = token;
        BrightFadeClip.Rect = area;
        BrightFadeClip.RadiusX = BrightFadeClip.RadiusY = radius;
        BrightFade.Source = BrightImage.Source;
        BrightFade.Visibility = Visibility.Visible;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, Motion.Base) { EasingFunction = Motion.Ease };
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(_brightFadeToken, token))
            {
                StopBrightFade();
            }
        };
        BrightFade.BeginAnimation(OpacityProperty, fade);
    }

    private void StopBrightFade()
    {
        _brightFadeToken = null;
        if (BrightFade.Visibility == Visibility.Visible)
        {
            BrightFade.BeginAnimation(OpacityProperty, null);
            BrightFade.Visibility = Visibility.Collapsed;
        }
    }

    private void OnHoverFrame(object? sender, EventArgs e)
    {
        var t = Motion.Bezier(Math.Min(1, _hoverClock.Elapsed.TotalMilliseconds / AppConfig.HoverSlide));
        Editor.HoverBounds = new Rect(
            _hoverFrom.X + (_hoverTo.X - _hoverFrom.X) * t,
            _hoverFrom.Y + (_hoverTo.Y - _hoverFrom.Y) * t,
            _hoverFrom.Width + (_hoverTo.Width - _hoverFrom.Width) * t,
            _hoverFrom.Height + (_hoverTo.Height - _hoverFrom.Height) * t);
        UpdateHoverBright();
        Editor.InvalidateVisual();
        if (t >= 1)
        {
            StopHoverAnimation();
        }
    }

    private void StopHoverAnimation()
    {
        if (_hoverAnimating)
        {
            _hoverAnimating = false;
            CompositionTarget.Rendering -= OnHoverFrame;
        }
    }

    private void HideHoverFrame()
    {
        StopHoverAnimation();
        _hoverLocal = Rect.Empty;
        if (!Editor.ShowHover)
        {
            return;
        }

        Editor.ShowHover = false;
        Editor.HoverBounds = Rect.Empty;
        UpdateHoverBright();
        Editor.HoverLabel = null;
        Editor.InvalidateVisual();
    }

    private void ClearHover()
    {
        if (_hasSelection)
        {
            HideWheelTip(); // an area was chosen
        }

        _elementWanted = null;
        if (_hoverTarget == null && !Editor.ShowHover)
        {
            return;
        }

        _hoverTarget = null;
        _hoverChain = new List<HoverCandidate>();
        _hoverLevel = 0;
        _startLevel = 0; // the wheel's choice ends with the frame
        _detailFresh = false;
        _uiaElements = new List<DetectedElement>();
        HideHoverFrame();
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

    private bool ShouldShowMagnifier(Point point)
    {
        if (!_hasSelection)
        {
            return true;
        }

        if (_dragging && _mode == DragMode.Moving)
        {
            return false; // moving the area: no loupe
        }

        if (_mode == DragMode.Resizing && _dragging)
        {
            return true;
        }

        if (HitHandle(point) >= 0)
        {
            return true;
        }

        return !_selection.Contains(point);
    }

    private void FinishLasso()
    {
        _lassoing = false;
        InputLayer.ReleaseMouseCapture();
        var points = _lassoDraft ?? new List<Point>();
        _lassoDraft = null;
        Editor.LassoDraft = null;
        Editor.LassoPreview = null;
        var bounds = points.Count > 0 ? new Rect(points[0], points[0]) : Rect.Empty;
        foreach (var p in points)
        {
            bounds.Union(p);
        }

        if (points.Count < 3 || bounds.Width < 4 * _scale || bounds.Height < 4 * _scale)
        {
            // Just a right-click: leave the capture.
            Editor.InvalidateVisual();
            CancelCapture();
            return;
        }

        bounds = ClampToScreen(bounds);
        _lasso = points.Select(p => new Point(p.X - bounds.X, p.Y - bounds.Y)).ToList();
        _lassoSize = bounds.Size;
        _cornerRadius = 0;
        _selection = bounds;
        _hasSelection = true;
        _mode = DragMode.None;
        UpdateSelectionVisuals();
        UpdateCursorToPointer();
    }

    /// <summary>Selection snapped to whole pixels: the unit translation results are keyed by.</summary>
    private Rect SnapRect(Rect rect)
    {
        var clamped = ClampToScreen(rect);
        return new Rect(
            Math.Round(clamped.X), Math.Round(clamped.Y),
            Math.Max(1, Math.Round(clamped.Width)), Math.Max(1, Math.Round(clamped.Height)));
    }

    /// <summary>True while the selection itself is being drawn, moved or resized.</summary>
    private bool SelectionDragging =>
        _dragging && (_mode is DragMode.Creating or DragMode.Moving or DragMode.Resizing || _pendingWindow != Rect.Empty);
}
