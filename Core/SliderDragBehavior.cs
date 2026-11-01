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
/// If the thumb does not pick it up, the slider captures the mouse and tracks
/// it itself until release.
/// </summary>
/// <remarks>
/// The handler is registered with handledEventsToo: with IsMoveToPointEnabled
/// the Slider's own class handler marks the press handled first, which used to
/// swallow it before the hand-off.
/// </remarks>
public static class SliderDragBehavior
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(SliderDragBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    private static readonly MouseButtonEventHandler DownHandler = OnMouseDown;
    private static readonly MouseEventHandler MoveHandler = OnMouseMove;
    private static readonly MouseButtonEventHandler UpHandler = OnMouseUp;
    private static Slider? _tracking;

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
            slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, DownHandler, true);
            slider.AddHandler(UIElement.PreviewMouseMoveEvent, MoveHandler, true);
            slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, UpHandler, true);
            slider.LostMouseCapture += (_, _) =>
            {
                if (ReferenceEquals(_tracking, slider))
                {
                    _tracking = null;
                }
            };
        }
        else
        {
            slider.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, DownHandler);
            slider.RemoveHandler(UIElement.PreviewMouseMoveEvent, MoveHandler);
            slider.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, UpHandler);
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

        SetFromPointer(slider, track, e);
        slider.UpdateLayout();

        // Hand this press to the thumb: it captures the mouse and raises
        // DragDelta for the rest of the gesture, exactly as if it was grabbed.
        if (!thumb.IsDragging)
        {
            thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                Source = thumb
            });
        }

        if (!thumb.IsDragging && slider.CaptureMouse())
        {
            _tracking = slider; // fallback: follow the pointer ourselves
        }

        e.Handled = true;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Slider slider || !ReferenceEquals(_tracking, slider))
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            slider.ReleaseMouseCapture();
            return;
        }

        if (slider.Template?.FindName("PART_Track", slider) is Track track)
        {
            SetFromPointer(slider, track, e);
        }

        e.Handled = true;
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider && ReferenceEquals(_tracking, slider))
        {
            _tracking = null;
            slider.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private static void SetFromPointer(Slider slider, Track track, MouseEventArgs e)
    {
        var value = track.ValueFromPoint(e.GetPosition(track));
        if (double.IsFinite(value))
        {
            slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        }
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
