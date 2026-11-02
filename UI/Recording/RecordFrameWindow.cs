// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Markshot.Core.Recording;

namespace Markshot.UI.Recording;

/// <summary>
/// The frame around the recording area: a transparent window a little larger than the area. The
/// line is drawn just outside the area (so it never ends up in the video); the band around it moves
/// the area, the eight handles resize it (both only before recording). The inside is fully
/// transparent, so clicks reach the programs underneath. Shows the countdown and
/// "正在生成预览…" in the middle.
/// </summary>
internal sealed class RecordFrameWindow : Window
{
    public const double BandOut = 12;
    public const double BandIn = 8;
    public const double Line = 4;
    public const double Handle = 11;

    public static readonly Color Blue = Color.FromRgb(0x4C, 0x8D, 0xFF); // Markshot accent
    public static readonly Color Red = Color.FromRgb(0xFF, 0x4D, 0x4F);
    public static readonly Color Amber = Color.FromRgb(0xF0, 0xAA, 0x2C);

    private readonly RecorderController _controller;
    private readonly FrameView _view;
    private string? _drag;
    private Native.POINT _dragStart;
    private Int32Rect _dragOrigin;

    public RecordFrameWindow(RecorderController controller)
    {
        _controller = controller;
        Title = "Markshot 录屏区域";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        _view = new FrameView(this);
        Content = _view;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, Native.GWL_EXSTYLE);
            SetWindowLong(handle, Native.GWL_EXSTYLE, style | WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };
        controller.Changed += OnChanged;
        controller.Pills.Changed += OnPills;
        _pillTimer.Tick += (_, _) =>
        {
            // Redraw while pills fade out; stop when none is left.
            _view.InvalidateVisual();
            if (_controller.Pills.Snapshot().Count == 0)
            {
                _pillTimer.Stop();
            }
        };
        Closed += (_, _) =>
        {
            controller.Changed -= OnChanged;
            controller.Pills.Changed -= OnPills;
            _pillTimer.Stop();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
    }

    public RecorderController Controller => _controller;

    public bool Dragging => _drag != null;

    public bool Hovering { get; private set; }

    private readonly DispatcherTimer _pillTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _clickThrough;

    /// <summary>A key / mouse press (from the hook thread): show it now and keep redrawing while it fades.</summary>
    private void OnPills() => Dispatcher.BeginInvoke(() =>
    {
        _view.InvalidateVisual();
        _pillTimer.Start();
    });

    private void OnChanged()
    {
        _view.InvalidateVisual();
        if (!_controller.IsOpen)
        {
            return;
        }

        var interactive = _controller.Phase == RecordPhase.Setup;
        _view.IsHitTestVisible = interactive;
        if (!interactive && _drag != null)
        {
            EndDrag();
        }

        // Outside setup nothing here takes the mouse: the pills / countdown must not block clicks.
        SetClickThrough(!interactive);
    }

    private void SetClickThrough(bool on)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || on == _clickThrough)
        {
            return;
        }

        _clickThrough = on;
        var style = GetWindowLong(handle, Native.GWL_EXSTYLE);
        SetWindowLong(handle, Native.GWL_EXSTYLE, on ? style | Native.WS_EX_TRANSPARENT : style & ~Native.WS_EX_TRANSPARENT);
    }

    /// <summary>Band width in physical pixels for the area's monitor.</summary>
    private int BandPixels => (int)Math.Ceiling(BandOut * _controller.Scale);

    public void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var r = _controller.Rect;
        var band = BandPixels;
        // Moving keeps the stacking order (re-inserting it on top each time made the toolbar over it flicker).
        Native.SetWindowPos(handle, IntPtr.Zero, r.X - band, r.Y - band, r.Width + band * 2, r.Height + band * 2,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        _view.InvalidateVisual();
    }

    /// <summary>The frame settles in when the recorder opens (1.035 → 1, fading in).</summary>
    public void PlayOpen()
    {
        var scale = new ScaleTransform(1.035, 1.035);
        _view.RenderTransformOrigin = new Point(0.5, 0.5);
        _view.RenderTransform = scale;
        _view.Opacity = 0;
        var grow = new DoubleAnimation(1.035, 1, Motion.Base) { EasingFunction = Motion.Ease };
        grow.Completed += (_, _) => _view.RenderTransform = Transform.Identity;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Base) { EasingFunction = Motion.Ease });
    }

    /// <summary>Hidden from the taskbar button: only the opacity changes (hide / show flickers).</summary>
    public void SetHidden(bool hidden)
    {
        _view.BeginAnimation(OpacityProperty, null);
        Opacity = hidden ? 0 : 1;
        IsHitTestVisible = !hidden;
    }

    // ------------------------------------------------------------------ dragging

    public void BeginDrag(string kind)
    {
        _drag = kind;
        _dragStart = Native.CursorPosition();
        _dragOrigin = _controller.Rect;
        _view.CaptureMouse();
        _view.InvalidateVisual();
    }

    public void ContinueDrag()
    {
        if (_drag == null)
        {
            return;
        }

        var c = Native.CursorPosition();
        int dx = c.X - _dragStart.X, dy = c.Y - _dragStart.Y;
        var r0 = _dragOrigin;
        if (_drag == "move")
        {
            _controller.SetRect(new Int32Rect(r0.X + dx, r0.Y + dy, r0.Width, r0.Height));
            return;
        }

        var scale = _controller.Scale;
        var minW = (int)Math.Ceiling(RecorderController.MinWidthDip * scale);
        var minH = (int)Math.Ceiling(RecorderController.MinHeightDip * scale);
        int x = r0.X, y = r0.Y, w = r0.Width, h = r0.Height;
        if (_drag.Contains('w'))
        {
            x = r0.X + dx;
            w = r0.Width - dx;
        }

        if (_drag.Contains('e'))
        {
            w = r0.Width + dx;
        }

        if (_drag.Contains('n'))
        {
            y = r0.Y + dy;
            h = r0.Height - dy;
        }

        if (_drag.Contains('s'))
        {
            h = r0.Height + dy;
        }

        // The opposite edge stays put.
        if (w < minW)
        {
            if (_drag.Contains('w'))
            {
                x = r0.X + r0.Width - minW;
            }

            w = minW;
        }

        if (h < minH)
        {
            if (_drag.Contains('n'))
            {
                y = r0.Y + r0.Height - minH;
            }

            h = minH;
        }

        _controller.SetRect(new Int32Rect(x, y, w, h), clip: true);
    }

    public void EndDrag()
    {
        _drag = null;
        if (_view.IsMouseCaptured)
        {
            _view.ReleaseMouseCapture();
        }

        _view.InvalidateVisual();
    }

    public void SetHover(bool hover)
    {
        if (Hovering != hover)
        {
            Hovering = hover;
            _view.InvalidateVisual();
        }
    }

    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    /// <summary>Draws the frame and handles the mouse in DIPs of the window.</summary>
    private sealed class FrameView : FrameworkElement
    {
        private static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
        private static readonly Pen Outline = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0))), 1));
        private readonly RecordFrameWindow _window;

        public FrameView(RecordFrameWindow window)
        {
            _window = window;
            Focusable = false;
        }

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        /// <summary>The recording area inside this window, in DIPs.</summary>
        private Rect Area
        {
            get
            {
                var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                var band = _window.BandPixels / dpi;
                var r = _window.Controller.Rect;
                return new Rect(band, band, r.Width / dpi, r.Height / dpi);
            }
        }

        private Rect LineRect
        {
            get
            {
                var area = Area;
                area.Inflate(Line / 2, Line / 2);
                return area;
            }
        }

        private IEnumerable<(string Kind, Point Center)> Handles()
        {
            var r = LineRect;
            double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            yield return ("nw", r.TopLeft);
            yield return ("n", new Point(cx, r.Top));
            yield return ("ne", r.TopRight);
            yield return ("e", new Point(r.Right, cy));
            yield return ("se", r.BottomRight);
            yield return ("s", new Point(cx, r.Bottom));
            yield return ("sw", r.BottomLeft);
            yield return ("w", new Point(r.Left, cy));
        }

        protected override void OnRender(DrawingContext dc)
        {
            var controller = _window.Controller;
            var phase = controller.Phase;
            var area = Area;
            var setup = phase == RecordPhase.Setup;
            if (setup)
            {
                // Nearly invisible, but catches the mouse: the band outside and a strip inside the area.
                var outer = new Rect(0, 0, ActualWidth, ActualHeight);
                var inner = area;
                inner.Inflate(-BandIn, -BandIn);
                var ring = inner.Width > 0 && inner.Height > 0
                    ? new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(outer), new RectangleGeometry(inner))
                    : (Geometry)new RectangleGeometry(outer);
                dc.DrawGeometry(HitBrush, null, ring);
            }

            var color = phase switch
            {
                RecordPhase.Recording or RecordPhase.Finishing => Red,
                RecordPhase.Paused => Amber,
                _ => Blue
            };
            var line = LineRect;
            var outline = line;
            outline.Inflate(Line / 2 + 0.5, Line / 2 + 0.5);
            dc.DrawRectangle(null, Outline, outline);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(color), Line), line);

            if (setup && (_window.Hovering || _window.Dragging))
            {
                var fill = new SolidColorBrush(Colors.White);
                var stroke = new Pen(new SolidColorBrush(color), 1.5);
                foreach (var (_, center) in Handles())
                {
                    dc.DrawRectangle(fill, stroke, new Rect(center.X - Handle / 2, center.Y - Handle / 2, Handle, Handle));
                }
            }

            if (controller.KeysOn)
            {
                DrawPills(dc, area, controller.Pills.Snapshot());
            }

            if (phase == RecordPhase.Countdown && controller.Count > 0)
            {
                var size = Math.Clamp(area.Height * 0.35, 48, 160);
                var text = new FormattedText(controller.Count.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var origin = new Point(area.X + (area.Width - text.Width) / 2, area.Y + (area.Height - text.Height) / 2);
                var shadow = text.BuildGeometry(new Point(origin.X + 2, origin.Y + 3));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), null, shadow);
                dc.DrawText(text, origin);
            }
            else if (phase == RecordPhase.Finishing)
            {
                var text = new FormattedText("正在生成预览…", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), 14, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var box = new Rect(area.X + (area.Width - text.Width) / 2 - 14, area.Y + (area.Height - text.Height) / 2 - 8,
                    text.Width + 28, text.Height + 16);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1C)), null, box, 8, 8);
                dc.DrawText(text, new Point(box.X + 14, box.Y + 8));
            }
        }

        /// <summary>
        /// 操作录制: the pills in the area's bottom-left, newest at the bottom. They are inside the
        /// area, so the recording picks them up exactly as shown (same layout as SketchPlayer's keyPills).
        /// </summary>
        private void DrawPills(DrawingContext dc, Rect area, List<(string Label, double Alpha)> pills)
        {
            if (pills.Count == 0)
            {
                return;
            }

            var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var y = area.Bottom - KeyPills.Margin;
            for (var i = pills.Count - 1; i >= 0; i--)
            {
                var (label, alpha) = pills[i];
                var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, KeyPills.FontSize,
                    new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), 255, 255, 255)), pixelsPerDip);
                var box = new Rect(area.X + KeyPills.Margin, y - KeyPills.PillHeight, text.Width + KeyPills.Padding * 2, KeyPills.PillHeight);
                if (box.Top < area.Top)
                {
                    break;
                }

                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(214 * alpha), 18, 18, 18)),
                    new Pen(new SolidColorBrush(Color.FromArgb((byte)(56 * alpha), 255, 255, 255)), 1), box, KeyPills.Radius, KeyPills.Radius);
                dc.DrawText(text, new Point(box.X + KeyPills.Padding, box.Y + (box.Height - text.Height) / 2));
                y = box.Top - KeyPills.Gap;
            }
        }

        private string? HitKind(Point p)
        {
            foreach (var (kind, center) in Handles())
            {
                if (Math.Abs(p.X - center.X) <= Handle && Math.Abs(p.Y - center.Y) <= Handle)
                {
                    return kind;
                }
            }

            var inner = Area;
            inner.Inflate(-BandIn, -BandIn);
            return inner.Contains(p) ? null : "move";
        }

        private static Cursor CursorFor(string? kind) => kind switch
        {
            "nw" or "se" => Cursors.SizeNWSE,
            "ne" or "sw" => Cursors.SizeNESW,
            "n" or "s" => Cursors.SizeNS,
            "e" or "w" => Cursors.SizeWE,
            "move" => Cursors.SizeAll,
            _ => Cursors.Arrow
        };

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_window.Dragging)
            {
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    _window.EndDrag();
                    return;
                }

                _window.ContinueDrag();
                return;
            }

            Cursor = CursorFor(HitKind(e.GetPosition(this)));
            _window.SetHover(true);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (!_window.Dragging)
            {
                _window.SetHover(false);
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (_window.Controller.Phase != RecordPhase.Setup || HitKind(e.GetPosition(this)) is not { } kind)
            {
                return;
            }

            _window.BeginDrag(kind);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_window.Dragging)
            {
                _window.EndDrag();
                e.Handled = true;
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            if (_window.Dragging)
            {
                _window.EndDrag();
            }
        }
    }
}
