using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Markshot.Core;

/// <summary>
/// Lets a slider jump to the clicked track position and continue dragging in
/// the same mouse press, without releasing the thumb first.
/// </summary>
public static class SliderDragBehavior
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(SliderDragBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty DraggingProperty =
        DependencyProperty.RegisterAttached(
            "Dragging",
            typeof(bool),
            typeof(SliderDragBehavior),
            new PropertyMetadata(false));

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
            slider.PreviewMouseMove += OnMouseMove;
            slider.PreviewMouseLeftButtonUp += OnMouseUp;
            slider.LostMouseCapture += OnLostCapture;
        }
        else
        {
            slider.PreviewMouseLeftButtonDown -= OnMouseDown;
            slider.PreviewMouseMove -= OnMouseMove;
            slider.PreviewMouseLeftButtonUp -= OnMouseUp;
            slider.LostMouseCapture -= OnLostCapture;
        }
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || e.OriginalSource is Thumb)
        {
            return;
        }

        SetValueFromPoint(slider, e.GetPosition(slider));
        slider.SetValue(DraggingProperty, true);
        slider.CaptureMouse();
        e.Handled = true;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Slider slider || !(bool)slider.GetValue(DraggingProperty))
        {
            return;
        }

        SetValueFromPoint(slider, e.GetPosition(slider));
        e.Handled = true;
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider)
        {
            return;
        }

        slider.SetValue(DraggingProperty, false);
        if (slider.IsMouseCaptured)
        {
            slider.ReleaseMouseCapture();
        }
    }

    private static void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (sender is Slider slider)
        {
            slider.SetValue(DraggingProperty, false);
        }
    }

    private static void SetValueFromPoint(Slider slider, Point point)
    {
        var range = slider.Maximum - slider.Minimum;
        if (range <= 0)
        {
            return;
        }

        double ratio;
        if (slider.Orientation == Orientation.Vertical)
        {
            var height = Math.Max(1, slider.ActualHeight - 12);
            ratio = 1 - (point.Y - 6) / height;
        }
        else
        {
            var width = Math.Max(1, slider.ActualWidth - 12);
            ratio = (point.X - 6) / width;
        }

        slider.Value = slider.Minimum + Math.Clamp(ratio, 0, 1) * range;
    }
}
