using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Markshot.Core;

/// <summary>
/// The single easing curve of the UI: cubic-bezier(0.4, 0, 0.2, 1) (slow at both ends).
/// Usable from XAML as &lt;core:StandardEase /&gt;.
/// </summary>
public sealed class StandardEase : EasingFunctionBase
{
    public StandardEase()
    {
        // EaseIn mode applies EaseInCore as-is (the curve is already in/out).
        EasingMode = EasingMode.EaseIn;
    }

    protected override double EaseInCore(double normalizedTime) => Motion.Bezier(normalizedTime);

    protected override Freezable CreateInstanceCore() => new StandardEase();
}

/// <summary>
/// Motion tokens and helpers. One curve, three durations; only opacity and
/// transforms are animated; exits play before the element is hidden; an
/// interrupted animation continues from the current value (no From).
/// </summary>
public static class Motion
{
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(160));

    /// <summary>The annotation bar showing / hiding (snappier than other pop-ins).</summary>
    public static readonly Duration Bar = new(TimeSpan.FromMilliseconds(70));
    public static readonly Duration Base = new(TimeSpan.FromMilliseconds(240));
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(320));
    public static readonly Duration Slide = new(TimeSpan.FromMilliseconds(220));

    /// <summary>Pop-up offset (DIP) and start scale.</summary>
    public const double PopOffset = 6;
    public const double PopScale = 0.97;

    public static readonly IEasingFunction Ease = Freeze(new StandardEase());

    private static readonly DependencyProperty HideTokenProperty =
        DependencyProperty.RegisterAttached("HideToken", typeof(object), typeof(Motion));

    /// <summary>y for x on cubic-bezier(0.4, 0, 0.2, 1).</summary>
    public static double Bezier(double x)
    {
        const double x1 = 0.4, y1 = 0, x2 = 0.2, y2 = 1;
        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        // Solve x(t) = x by Newton, falling back to bisection.
        var t = x;
        for (var i = 0; i < 8; i++)
        {
            var error = Cubic(t, x1, x2) - x;
            var slope = CubicSlope(t, x1, x2);
            if (Math.Abs(error) < 1e-5)
            {
                return Cubic(t, y1, y2);
            }

            if (Math.Abs(slope) < 1e-6)
            {
                break;
            }

            t -= error / slope;
        }

        double lo = 0, hi = 1;
        t = x;
        for (var i = 0; i < 24; i++)
        {
            var value = Cubic(t, x1, x2);
            if (Math.Abs(value - x) < 1e-5)
            {
                break;
            }

            if (value < x)
            {
                lo = t;
            }
            else
            {
                hi = t;
            }

            t = (lo + hi) / 2;
        }

        return Cubic(t, y1, y2);
    }

    /// <summary>Animates opacity to a value from wherever it is now.</summary>
    public static void Fade(UIElement element, double to, Duration? duration = null, Action? completed = null)
    {
        var animation = new DoubleAnimation(to, duration ?? Fast) { EasingFunction = Ease };
        if (completed != null)
        {
            animation.Completed += (_, _) => completed();
        }

        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>
    /// Shows an element with the pop-in: opacity 0→1, offset ±6 DIP→0, scale 0.97→1 (160 ms).
    /// <paramref name="fromAbove"/> = it drops down from above (menus, bars under an anchor).
    /// </summary>
    public static void PopIn(FrameworkElement element, bool fromAbove = true, Duration? duration = null)
    {
        var length = duration ?? Fast;
        element.SetValue(HideTokenProperty, null);
        element.Visibility = Visibility.Visible;
        var (scale, translate) = EnsureTransforms(element);
        var startOpacity = element.Opacity < 0.99 ? element.Opacity : 0;
        var startOffset = startOpacity > 0 ? translate.Y : (fromAbove ? -PopOffset : PopOffset);
        var startScale = startOpacity > 0 ? scale.ScaleX : PopScale;

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(startOpacity, 1, length) { EasingFunction = Ease });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(startOffset, 0, length) { EasingFunction = Ease });
        var scaleAnimation = new DoubleAnimation(startScale, 1, length) { EasingFunction = Ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
    }

    /// <summary>Plays the pop-out, then collapses the element (unless it was shown again meanwhile).</summary>
    public static void PopOut(FrameworkElement element, bool towardsAbove = true, Action? completed = null, Duration? duration = null)
    {
        var length = duration ?? Fast;
        if (element.Visibility != Visibility.Visible || !element.IsLoaded)
        {
            element.Visibility = Visibility.Collapsed;
            completed?.Invoke();
            return;
        }

        var token = new object();
        element.SetValue(HideTokenProperty, token);
        var (scale, translate) = EnsureTransforms(element);
        var opacity = new DoubleAnimation(0, length) { EasingFunction = Ease };
        opacity.Completed += (_, _) =>
        {
            if (!ReferenceEquals(element.GetValue(HideTokenProperty), token))
            {
                return; // shown again in the meantime
            }

            element.Visibility = Visibility.Collapsed;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            completed?.Invoke();
        };
        element.BeginAnimation(UIElement.OpacityProperty, opacity);
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(towardsAbove ? -PopOffset : PopOffset, length) { EasingFunction = Ease });
        var scaleAnimation = new DoubleAnimation(PopScale, length) { EasingFunction = Ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
    }

    /// <summary>True while a PopOut / SetShown(false) is playing (the element is still visible).</summary>
    public static bool IsHiding(UIElement element) => element.GetValue(HideTokenProperty) != null;

    /// <summary>Visibility with a plain fade (base 240 ms for whole blocks, fast for small parts).</summary>
    public static void SetShown(UIElement element, bool shown, Duration? duration = null)
    {
        if (shown)
        {
            element.SetValue(HideTokenProperty, null);
            if (element.Visibility != Visibility.Visible)
            {
                element.Opacity = 0;
                element.Visibility = Visibility.Visible;
            }

            Fade(element, 1, duration);
            return;
        }

        if (element.Visibility != Visibility.Visible)
        {
            return;
        }

        var token = new object();
        element.SetValue(HideTokenProperty, token);
        Fade(element, 0, duration, () =>
        {
            if (ReferenceEquals(element.GetValue(HideTokenProperty), token))
            {
                element.Visibility = Visibility.Collapsed;
            }
        });
    }

    /// <summary>Pops the element in every time it becomes visible (visibility itself stays instant, so layout never waits).</summary>
    public static void PopOnShow(FrameworkElement element, bool fromAbove = true, Duration? duration = null)
    {
        element.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                element.Opacity = 0;
                PopIn(element, fromAbove, duration);
            }
        };
    }

    /// <summary>Fades the element in every time it becomes visible.</summary>
    public static void FadeOnShow(UIElement element, Duration? duration = null)
    {
        element.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                element.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, duration ?? Fast) { EasingFunction = Ease });
            }
        };
    }

    /// <summary>Page switch: the new page fades in while sliding 8 DIP from the right.</summary>
    public static void EnterPage(FrameworkElement page)
    {
        if (page.RenderTransform is not TranslateTransform translate || translate.IsFrozen)
        {
            translate = new TranslateTransform();
            page.RenderTransform = translate;
        }

        page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Fast) { EasingFunction = Ease });
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(8, 0, Base) { EasingFunction = Ease });
    }

    /// <summary>Slides a translate transform to an X offset from where it is now.</summary>
    public static void SlideX(TranslateTransform transform, double x, Duration? duration = null) =>
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, duration ?? Slide) { EasingFunction = Ease });

    private static (ScaleTransform Scale, TranslateTransform Translate) EnsureTransforms(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup { Children.Count: 2 } group &&
            group.Children[0] is ScaleTransform existingScale && group.Children[1] is TranslateTransform existingTranslate &&
            !group.IsFrozen)
        {
            return (existingScale, existingTranslate);
        }

        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        element.RenderTransform = new TransformGroup { Children = { scale, translate } };
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return (scale, translate);
    }

    private static double Cubic(double t, double p1, double p2)
    {
        var u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }

    private static double CubicSlope(double t, double p1, double p2)
    {
        var u = 1 - t;
        return 3 * u * u * p1 + 6 * u * t * (p2 - p1) + 3 * t * t * (1 - p2);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
