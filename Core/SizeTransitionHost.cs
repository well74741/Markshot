using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Markshot.Core;

/// <summary>
/// Hosts a block whose size changes (the toolbar's settings row): instead of
/// jumping, the reported size eases from what is on screen now to the new
/// target (Motion.Base, the shared curve), so the surrounding background grows
/// together with the content. Opening slides the content down from under the
/// edge, closing slides it back up; changing the content animates the width.
/// An interrupted transition continues from the current size.
/// </summary>
public sealed class SizeTransitionHost : Decorator
{
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(SizeTransitionHost),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register("Progress", typeof(double), typeof(SizeTransitionHost),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    private Size _from;
    private Size _to;
    private Size _content;
    private bool _initialized;

    public SizeTransitionHost()
    {
        ClipToBounds = true;
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>Raised when a close transition finished (the content is fully hidden).</summary>
    public event EventHandler? Closed;

    private double Progress => (double)GetValue(ProgressProperty);

    private Size Current
    {
        get
        {
            var t = Progress;
            return new Size(_from.Width + (_to.Width - _from.Width) * t, _from.Height + (_to.Height - _from.Height) * t);
        }
    }

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
        if (!_initialized || !IsLoaded)
        {
            // First layout / not on screen yet: no animation.
            _initialized = IsLoaded;
            _from = _to = target;
            BeginAnimation(ProgressProperty, null);
            SetCurrentValue(ProgressProperty, 1.0);
            UpdateChildVisibility(target);
            return target;
        }

        if (!SameSize(target, _to))
        {
            _from = Current;
            _to = target;
            UpdateChildVisibility(new Size(1, 1));
            var animation = new DoubleAnimation(0, 1, Motion.Base) { EasingFunction = Motion.Ease };
            if (!IsOpen)
            {
                animation.Completed += (_, _) =>
                {
                    if (!IsOpen)
                    {
                        UpdateChildVisibility(new Size(0, 0));
                        Closed?.Invoke(this, EventArgs.Empty);
                    }
                };
            }

            BeginAnimation(ProgressProperty, animation);
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
