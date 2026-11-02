using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Markshot.Core;

/// <summary>
/// Hosts a block whose size changes (the toolbar's settings row): instead of
/// jumping, the reported size eases from what is on screen now to the new
/// target (Motion.Base, the shared curve), so the surrounding background grows
/// together with the content. Opening slides the content down from under the
/// edge, closing slides it back up; changing the content animates the width.
/// An interrupted transition continues from the current size.
/// </summary>
/// <remarks>
/// The progress is driven per frame (CompositionTarget.Rendering) instead of a
/// WPF animation: an animation reports a stale value until its first tick, and
/// two layout passes in one frame (common when a tool is picked) then restarted
/// the transition from its end — the first open looked like a jump.
/// </remarks>
public sealed class SizeTransitionHost : Decorator
{
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(SizeTransitionHost),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Size _from;
    private Size _to;
    private Size _content;
    private bool _snapNext = true;
    private double _progress = 1;
    private DateTime _start;
    private bool _ticking;

    public SizeTransitionHost()
    {
        ClipToBounds = true;
        // While hidden nothing is measured; whatever changed meanwhile must not play
        // as an animation when it shows again.
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
            {
                _snapNext = true;
            }
        };
        Unloaded += (_, _) => StopTicking();
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>Raised when a close transition finished (the content is fully hidden).</summary>
    public event EventHandler? Closed;

    private Size Current => new(
        _from.Width + (_to.Width - _from.Width) * _progress,
        _from.Height + (_to.Height - _from.Height) * _progress);

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child == null)
        {
            return new Size();
        }

        Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (IsOpen)
        {
            _content = Child.DesiredSize; // keep the last open size so closing shows the same content
        }

        var target = IsOpen ? _content : new Size(0, 0);
        if (_snapNext)
        {
            // First layout or (re)shown after being hidden: no animation.
            _snapNext = false;
            _from = _to = target;
            _progress = 1;
            StopTicking();
            UpdateChildVisibility(target);
            return target;
        }

        if (!SameSize(target, _to))
        {
            _from = Current; // from what is on screen now (interrupting continues)
            _to = target;
            _progress = 0;
            _start = DateTime.UtcNow;
            UpdateChildVisibility(new Size(1, 1));
            StartTicking();
            return _from;
        }

        return Current;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child == null)
        {
            return arrangeSize;
        }

        // The content keeps its full size and rides on the moving bottom edge,
        // so it slides out together with the background.
        var full = _content;
        var offsetY = Math.Min(0, Current.Height - full.Height);
        Child.Arrange(new Rect(new Point(0, offsetY), new Size(Math.Max(full.Width, arrangeSize.Width), full.Height)));
        return arrangeSize;
    }

    private void StartTicking()
    {
        if (!_ticking)
        {
            _ticking = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void StopTicking()
    {
        if (_ticking)
        {
            _ticking = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = (DateTime.UtcNow - _start).TotalMilliseconds / Motion.Base.TimeSpan.TotalMilliseconds;
        if (t >= 1)
        {
            _progress = 1;
            StopTicking();
            if (!IsOpen)
            {
                UpdateChildVisibility(new Size(0, 0));
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            _progress = Motion.Bezier(Math.Max(0, t));
        }

        InvalidateMeasure();
        InvalidateArrange();
    }

    private void UpdateChildVisibility(Size size)
    {
        if (Child != null)
        {
            Child.Opacity = size.Width <= 0 || size.Height <= 0 ? 0 : 1;
            Child.IsHitTestVisible = IsOpen;
        }
    }

    private static bool SameSize(Size a, Size b) => Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;
}
