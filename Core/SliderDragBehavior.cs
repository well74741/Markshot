using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Markshot.Core;

/// <summary>
/// Lets a slider jump to the clicked track position and keep dragging in the
/// same press. The click sets the value, then the press is handed to the
/// slider's own Thumb, so WPF's native thumb drag takes over from there.
/// </summary>
public static class SliderDragBehavior
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(SliderDragBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    public static void Enable(Slider slider)
    {
        if (!GetEnabled(slider))
        {
            SetEnabled(slider, true);
        }
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            slider.PreviewMouseLeftButtonDown += OnMouseDown;
        }
        else
        {
            slider.PreviewMouseLeftButtonDown -= OnMouseDown;
        }
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled)
        {
            return;
        }

        var track = slider.Template?.FindName("PART_Track", slider) as Track;
        var thumb = track?.Thumb;
        if (track == null || thumb == null || IsWithin(e.OriginalSource as DependencyObject, thumb))
        {
            return; // pressing the thumb itself already drags natively
        }

        slider.Value = Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), slider.Minimum, slider.Maximum);
        slider.UpdateLayout();

        // Hand this press to the thumb: it captures the mouse and raises
        // DragDelta for the rest of the gesture, exactly as if it was grabbed.
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = thumb
        });
        e.Handled = true;
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        while (element != null)
        {
            if (ReferenceEquals(element, ancestor))
            {
                return true;
            }

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }
}
