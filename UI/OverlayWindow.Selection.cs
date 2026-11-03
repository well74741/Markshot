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
            CancelTranslation();
            UpdateTranslationVisuals();
            BrightClip.Rect = new Rect(0, 0, 0, 0);
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
        if (e.Key == Key.Enter)
        {
            ApplyTypedSize();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
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
                Motion.PopIn(Toolbar);
            }

            return;
        }

        if (Toolbar.Visibility == Visibility.Visible && !Motion.IsHiding(Toolbar))
        {
            Motion.PopOut(Toolbar);
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
                    Text = ((TextBlock)Hint.Child).Text,
                    Foreground = ((TextBlock)Hint.Child).Foreground,
                    FontSize = ((TextBlock)Hint.Child).FontSize
                }
            };
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

    private int HitHandle(Point point)
    {
        if (!_hasSelection || _lasso != null)
        {
            return -1; // a lasso capture can be moved, not resized
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
