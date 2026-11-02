using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Markshot.UI;

/// <summary>
/// Long-press-and-drag reordering for a horizontal row of buttons (the annotation bar and the
/// settings preview of it). Holding an item for a moment picks it up — it wiggles — and dragging
/// it left / right slides the neighbours aside to make room; releasing drops it there. A short
/// click stays a normal click. Children must carry their key in <c>Tag</c>.
/// </summary>
public sealed class ReorderBehavior
{
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(420);

    private readonly Panel _panel;
    private readonly DispatcherTimer _hold = new() { Interval = HoldTime };
    private FrameworkElement? _pressed;
    private Point _pressPoint;
    private bool _dragging;
    private List<(FrameworkElement Item, double Left, double Width)> _slots = new();
    private int _from;
    private int _target;

    public ReorderBehavior(Panel panel)
    {
        _panel = panel;
        _hold.Tick += (_, _) => BeginDrag();
        panel.PreviewMouseLeftButtonDown += OnDown;
        panel.PreviewMouseMove += OnMove;
        panel.PreviewMouseLeftButtonUp += OnUp;
        panel.LostMouseCapture += (_, _) =>
        {
            if (_dragging)
            {
                EndDrag(commit: true);
            }
        };
    }

    /// <summary>The keys of all children (hidden ones included) in their new order.</summary>
    public event Action<IReadOnlyList<string>>? Reordered;

    /// <summary>True right after a drag, so the release is not taken as a click.</summary>
    public bool JustDragged { get; private set; }

    public bool IsDragging => _dragging;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        JustDragged = false;
        _pressed = ItemAt(e.OriginalSource as DependencyObject);
        if (_pressed == null)
        {
            return;
        }

        _pressPoint = e.GetPosition(_panel);
        _hold.Stop();
        _hold.Start();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(_panel);
        if (!_dragging)
        {
            // Moving before the hold time: an ordinary press (e.g. dragging a slider), no reorder.
            if (_pressed != null && (point - _pressPoint).Length > 6)
            {
                _hold.Stop();
                _pressed = null;
            }

            return;
        }

        var dx = point.X - _pressPoint.X;
        var dragged = _slots[_from];
        Translate(dragged.Item).X = dx;
        var center = dragged.Left + dragged.Width / 2 + dx;

        // Where it would land among the other visible items.
        var target = _from;
        for (var i = 0; i < _slots.Count; i++)
        {
            var slotCenter = _slots[i].Left + _slots[i].Width / 2;
            if (i < _from && center < slotCenter)
            {
                target = Math.Min(target, i);
            }
            else if (i > _from && center > slotCenter)
            {
                target = Math.Max(target, i);
            }
        }

        if (target == _target)
        {
            return;
        }

        _target = target;
        for (var i = 0; i < _slots.Count; i++)
        {
            if (i == _from)
            {
                continue;
            }

            var shift = i > _from && i <= target ? -dragged.Width
                : i < _from && i >= target ? dragged.Width
                : 0;
            Motion.SlideX(Translate(_slots[i].Item), shift, Motion.Fast);
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _hold.Stop();
        if (_dragging)
        {
            e.Handled = true;
            EndDrag(commit: true);
        }

        _pressed = null;
    }

    private void BeginDrag()
    {
        _hold.Stop();
        if (_pressed == null || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _slots = _panel.Children.OfType<FrameworkElement>()
            .Where(child => child.Visibility == Visibility.Visible && child.ActualWidth > 0)
            .Select(child => (child, child.TranslatePoint(new Point(0, 0), _panel).X, child.ActualWidth))
            .ToList();
        _from = _slots.FindIndex(slot => ReferenceEquals(slot.Item, _pressed));
        if (_from < 0)
        {
            return;
        }

        _target = _from;
        _dragging = true;
        _panel.CaptureMouse(); // the button loses its press: no click on release
        Panel.SetZIndex(_pressed, 10);
        Wiggle(_pressed, true);
    }

    private void EndDrag(bool commit)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        JustDragged = true;
        var dragged = _slots[_from].Item;
        Wiggle(dragged, false);
        Panel.SetZIndex(dragged, 0);
        foreach (var (item, _, _) in _slots)
        {
            var translate = Translate(item);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.X = 0;
        }

        if (_panel.IsMouseCaptured)
        {
            _panel.ReleaseMouseCapture();
        }

        if (commit && _target != _from)
        {
            // Move among the visible items; hidden (collapsed) children keep their own places.
            var landing = _slots[_target].Item;
            var children = _panel.Children;
            children.Remove(dragged);
            var index = children.IndexOf(landing);
            children.Insert(_target > _from ? index + 1 : index, dragged);
            Reordered?.Invoke(children.OfType<FrameworkElement>().Select(child => child.Tag as string ?? "").ToList());
        }
    }

    private FrameworkElement? ItemAt(DependencyObject? source)
    {
        while (source != null && !ReferenceEquals(source, _panel))
        {
            var parent = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            if (ReferenceEquals(parent, _panel))
            {
                return source as FrameworkElement;
            }

            source = parent;
        }

        return null;
    }

    private static TranslateTransform Translate(FrameworkElement item)
    {
        if (item.RenderTransform is TransformGroup { Children.Count: 2 } group && group.Children[1] is TranslateTransform translate)
        {
            return translate;
        }

        var created = new TranslateTransform();
        item.RenderTransformOrigin = new Point(0.5, 0.5);
        item.RenderTransform = new TransformGroup { Children = { new RotateTransform(0), created } };
        return created;
    }

    /// <summary>The picked-up item wiggles until it is dropped.</summary>
    private static void Wiggle(FrameworkElement item, bool on)
    {
        Translate(item);
        var rotate = (RotateTransform)((TransformGroup)item.RenderTransform).Children[0];
        if (!on)
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            rotate.Angle = 0;
            item.Opacity = 1;
            return;
        }

        var wiggle = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(260) };
        wiggle.KeyFrames.Add(new LinearDoubleKeyFrame(-5, KeyTime.FromPercent(0.25)));
        wiggle.KeyFrames.Add(new LinearDoubleKeyFrame(5, KeyTime.FromPercent(0.75)));
        wiggle.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        rotate.BeginAnimation(RotateTransform.AngleProperty, wiggle);
        item.Opacity = 0.85;
    }
}
