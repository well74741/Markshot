using System.Windows.Controls;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Markshot.UI;

/// <summary>
/// 曲线: the tone curve of the image editor. Click on the curve to add a point, drag points, drag a
/// point out of the box (or right-click it) to remove it; the ends move up and down only. The picture
/// follows while dragging; 取消 puts the curve back.
/// </summary>
public sealed class CurvesWindow : Window
{
    private const double Side = 256;
    private const double Grip = 6;

    private readonly Canvas _plot = new() { Width = Side, Height = Side, ClipToBounds = false, Background = Brushes.Transparent };
    private readonly Polyline _line = new() { StrokeThickness = 2, IsHitTestVisible = false };
    private readonly List<Point> _points; // curve space: x, y in 0–255 (y up)
    private readonly double[] _histogram;
    private readonly double[] _start;
    private int _dragging = -1;
    private bool _outside;

    public CurvesWindow(double[]? curve, BitmapSource? image)
    {
        Title = "曲线";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        _start = (curve ?? ToneCurve.Straight).ToArray();
        _points = Enumerable.Range(0, _start.Length / 2).Select(i => new Point(_start[i * 2], _start[i * 2 + 1])).OrderBy(p => p.X).ToList();
        _histogram = Histogram(image);

        var heading = new TextBlock { Text = "曲线", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFF5F5F5));
        var hint = new TextBlock { Text = "单击曲线加点，拖动调整；把点拖出框外或右键删除", FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF9A9A9A));

        _line.SetResourceReference(Shape.StrokeProperty, "Accent");
        _plot.MouseLeftButtonDown += Plot_MouseDown;
        _plot.MouseMove += Plot_MouseMove;
        _plot.MouseLeftButtonUp += Plot_MouseUp;
        _plot.MouseRightButtonDown += Plot_RightDown;
        var frame = new Border { BorderThickness = new Thickness(1), Child = _plot, HorizontalAlignment = HorizontalAlignment.Left };
        frame.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x33FFFFFF));
        frame.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF161616));

        var reset = new Button { Content = "重置", Style = (Style)Application.Current.FindResource("FooterButton"), MinWidth = 64 };
        reset.Click += (_, _) =>
        {
            _points.Clear();
            _points.Add(new Point(0, 0));
            _points.Add(new Point(255, 255));
            Changed();
        };
        var ok = new Button { Content = "确定", Style = (Style)Application.Current.FindResource("PrimaryButton"), MinWidth = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            Accepted = true;
            Close();
        };
        var cancel = new Button { Content = "取消", Style = (Style)Application.Current.FindResource("FooterButton"), MinWidth = 72, IsCancel = true };
        cancel.Click += (_, _) => Close();
        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(reset, Dock.Left);
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(ok, Dock.Right);
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var body = new StackPanel();
        body.Children.Add(heading);
        body.Children.Add(hint);
        body.Children.Add(frame);
        body.Children.Add(buttons);
        var card = new Border
        {
            LayoutTransform = UiScale.Transform,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.45 },
            Child = body
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF1E1E1E));
        card.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x33FFFFFF));
        Content = card;
        MouseLeftButtonDown += (_, e) =>
        {
            if (!_plot.IsMouseOver && e.OriginalSource is not Button)
            {
                DragMove();
            }
        };
        Closed += (_, _) =>
        {
            if (!Accepted)
            {
                CurveChanged?.Invoke(_start); // back to the curve it had
            }
        };
        Loaded += (_, _) =>
        {
            card.Opacity = 0;
            Motion.PopIn(card, fromAbove: false);
        };
        Redraw();
    }

    /// <summary>The curve changed (x, y pairs, 0–255); also raised with the old curve when cancelled.</summary>
    public event Action<double[]>? CurveChanged;

    public bool Accepted { get; private set; }

    public double[] Curve => _points.SelectMany(p => new[] { Math.Round(p.X), Math.Round(p.Y) }).ToArray();

    /// <summary>For tests: a point dragged (index, to curve coordinates).</summary>
    public void MovePoint(int index, Point to)
    {
        _dragging = index;
        DragTo(to, removeOutside: false);
        _dragging = -1;
    }

    /// <summary>For tests: a click on the curve at x.</summary>
    public int AddPoint(double x, double y)
    {
        var index = _points.FindIndex(p => p.X > x);
        index = index < 0 ? _points.Count : index;
        _points.Insert(index, new Point(Math.Clamp(x, 1, 254), Math.Clamp(y, 0, 255)));
        Changed();
        return index;
    }

    private static Point ToCurve(Point plot) => new(plot.X / Side * 255, 255 - plot.Y / Side * 255);

    private static Point ToPlot(Point curve) => new(curve.X / 255 * Side, (255 - curve.Y) / 255 * Side);

    private int HitPoint(Point plot)
    {
        for (var i = 0; i < _points.Count; i++)
        {
            if ((ToPlot(_points[i]) - plot).Length <= Grip + 3)
            {
                return i;
            }
        }

        return -1;
    }

    private void Plot_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var at = e.GetPosition(_plot);
        _dragging = HitPoint(at);
        if (_dragging < 0)
        {
            var curve = ToCurve(at);
            _dragging = AddPoint(curve.X, curve.Y);
        }

        _outside = false;
        _plot.CaptureMouse();
        e.Handled = true;
    }

    private void Plot_MouseMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(_plot);
        if (_dragging < 0)
        {
            _plot.Cursor = HitPoint(at) >= 0 ? Cursors.SizeAll : Cursors.Cross;
            return;
        }

        DragTo(ToCurve(at), removeOutside: true, plot: at);
    }

    private void DragTo(Point curve, bool removeOutside, Point? plot = null)
    {
        var index = _dragging;
        var end = index == 0 || index == _points.Count - 1;
        if (removeOutside && !end && plot is { } p && (p.Y < -24 || p.Y > Side + 24 || p.X < -24 || p.X > Side + 24))
        {
            _outside = true; // removed when released out there
            Redraw();
            return;
        }

        _outside = false;
        var y = Math.Clamp(curve.Y, 0, 255);
        double x;
        if (end)
        {
            x = _points[index].X; // the ends stay at the edges
        }
        else
        {
            // Between the neighbours: the curve never turns back on itself.
            x = Math.Clamp(curve.X, _points[index - 1].X + 2, _points[index + 1].X - 2);
        }

        _points[index] = new Point(x, y);
        Changed();
    }

    private void Plot_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging > 0 && _dragging < _points.Count - 1 && _outside)
        {
            _points.RemoveAt(_dragging);
            Changed();
        }

        _dragging = -1;
        _outside = false;
        _plot.ReleaseMouseCapture();
        Redraw();
    }

    private void Plot_RightDown(object sender, MouseButtonEventArgs e)
    {
        var index = HitPoint(e.GetPosition(_plot));
        if (index > 0 && index < _points.Count - 1)
        {
            _points.RemoveAt(index);
            Changed();
        }

        e.Handled = true;
    }

    private void Changed()
    {
        Redraw();
        CurveChanged?.Invoke(Curve);
    }

    private void Redraw()
    {
        _plot.Children.Clear();
        // Histogram of the picture's brightness, then a quarter grid and the straight reference.
        var bars = new StreamGeometry();
        using (var context = bars.Open())
        {
            context.BeginFigure(new Point(0, Side), true, true);
            for (var i = 0; i < 256; i++)
            {
                context.LineTo(new Point(i / 255.0 * Side, Side - _histogram[i] * Side * 0.9), false, false);
            }

            context.LineTo(new Point(Side, Side), false, false);
        }

        var histogram = new Path { Data = bars, IsHitTestVisible = false };
        histogram.SetResourceReference(Shape.FillProperty, Theme.Key(0x22FFFFFF));
        _plot.Children.Add(histogram);
        for (var i = 1; i < 4; i++)
        {
            _plot.Children.Add(GridLine(i * Side / 4, 0, i * Side / 4, Side));
            _plot.Children.Add(GridLine(0, i * Side / 4, Side, i * Side / 4));
        }

        _plot.Children.Add(GridLine(0, Side, Side, 0));
        var table = ToneCurve.Table(Curve);
        _line.Points = new PointCollection(Enumerable.Range(0, 256).Select(x => ToPlot(new Point(x, table[x]))));
        _plot.Children.Add(_line);
        for (var i = 0; i < _points.Count; i++)
        {
            var at = ToPlot(_points[i]);
            var dot = new Ellipse { Width = Grip * 2, Height = Grip * 2, StrokeThickness = 1.5, IsHitTestVisible = false, Opacity = i == _dragging && _outside ? 0.3 : 1 };
            dot.SetResourceReference(Shape.FillProperty, Theme.Key(0xFF1E1E1E));
            dot.SetResourceReference(Shape.StrokeProperty, "Accent");
            Canvas.SetLeft(dot, at.X - Grip);
            Canvas.SetTop(dot, at.Y - Grip);
            _plot.Children.Add(dot);
        }
    }

    private static Line GridLine(double x1, double y1, double x2, double y2)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = 1, IsHitTestVisible = false };
        line.SetResourceReference(Shape.StrokeProperty, Theme.Key(0x1FFFFFFF));
        return line;
    }

    /// <summary>Brightness histogram of the picture, 0–1 (square-rooted so small counts still show).</summary>
    private static double[] Histogram(BitmapSource? image)
    {
        var counts = new double[256];
        if (image == null)
        {
            return counts;
        }

        var (pixels, _, _) = ImageOps.ReadPixels(image);
        for (var i = 0; i < pixels.Length; i += 16) // every 4th pixel is plenty
        {
            counts[(pixels[i] * 29 + pixels[i + 1] * 150 + pixels[i + 2] * 77) >> 8]++;
        }

        var max = counts.Max();
        return max <= 0 ? counts : counts.Select(c => Math.Sqrt(c / max)).ToArray();
    }
}
