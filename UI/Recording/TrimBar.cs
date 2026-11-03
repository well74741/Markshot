// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
namespace Markshot.UI.Recording;

/// <summary>
/// The preview's timeline: the clip as a bar, two triangles for the in / out points (the part
/// outside is dimmed) and the playhead. Click / drag on the bar seeks; dragging a triangle trims.
/// </summary>
internal sealed class TrimBar : FrameworkElement
{
    private const double Pad = 7;
    private static readonly Color Amber = Color.FromRgb(0xF0, 0xAA, 0x2C);
    private string? _drag;

    public TrimBar()
    {
        Height = 26;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public double Duration { get; set; }

    public double In { get; set; }

    public double Out { get; set; }

    public double Position { get; set; }

    /// <summary>Seek to a time (clicking / dragging the bar).</summary>
    public event Action<double>? Seek;

    /// <summary>In / out moved (and the picture should show that frame).</summary>
    public event Action<bool, double>? Trim;

    private double Span => Math.Max(1, ActualWidth - Pad * 2);

    private double X(double t) => Pad + (Duration > 0 ? t / Duration : 0) * Span;

    private double T(double x) => Math.Clamp((x - Pad) / Span, 0, 1) * Duration;

    protected override void OnRender(DrawingContext dc)
    {
        var top = 8.0;
        var height = 10.0;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var clip = new Rect(Pad, top, Span, height);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x8C, 0x4C, 0x8D, 0xFF)), null, clip, 3, 3);
        var dim = new SolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0));
        if (In > 0)
        {
            dc.DrawRectangle(dim, null, new Rect(Pad, top, X(In) - Pad, height));
        }

        if (Out < Duration)
        {
            dc.DrawRectangle(dim, null, new Rect(X(Out), top, Pad + Span - X(Out), height));
        }

        var amber = new SolidColorBrush(Amber);
        foreach (var x in new[] { X(In), X(Out) })
        {
            var triangle = new StreamGeometry();
            using (var g = triangle.Open())
            {
                g.BeginFigure(new Point(x, top + height + 1), true, true);
                g.LineTo(new Point(x - 6, top + height + 8), true, false);
                g.LineTo(new Point(x + 6, top + height + 8), true, false);
            }

            triangle.Freeze();
            dc.DrawGeometry(amber, null, triangle);
            dc.DrawRectangle(amber, null, new Rect(x - 1, top, 2, height));
        }

        var head = X(Math.Clamp(Position, 0, Duration));
        dc.DrawRoundedRectangle(Theme.Brush(0xFFFFFFFF), null, new Rect(head - 1.5, top - 4, 3, height + 8), 1.5, 1.5);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        _drag = p.Y > 17 && Math.Abs(p.X - X(In)) < 8 ? "in"
            : p.Y > 17 && Math.Abs(p.X - X(Out)) < 8 ? "out"
            : "seek";
        CaptureMouse();
        Apply(p);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag != null && e.LeftButton == MouseButtonState.Pressed)
        {
            Apply(e.GetPosition(this));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _drag = null;
        ReleaseMouseCapture();
    }

    private void Apply(Point p)
    {
        var t = T(p.X);
        switch (_drag)
        {
            case "in":
                In = Math.Clamp(t, 0, Math.Max(0, Out - 0.1));
                Trim?.Invoke(true, In);
                break;
            case "out":
                Out = Math.Clamp(t, Math.Min(Duration, In + 0.1), Duration);
                Trim?.Invoke(false, Out);
                break;
            default:
                Seek?.Invoke(Math.Clamp(t, In, Out));
                break;
        }

        InvalidateVisual();
    }
}
