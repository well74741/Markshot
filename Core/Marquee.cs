using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace Markshot.Core;

/// <summary>
/// 滚动播放: a one-line text that does not fit scrolls slowly to its end and back (pausing at both ends),
/// inside its own bounds. Text that fits stays still. Set <c>core:Marquee.Enabled="True"</c> on a TextBlock.
/// </summary>
public static class Marquee
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Marquee), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(TranslateTransform), typeof(Marquee));

    private static readonly DependencyProperty TrimmingProperty = DependencyProperty.RegisterAttached(
        "Trimming", typeof(TextTrimming?), typeof(Marquee));

    /// <summary>For tests: how far the text currently has to travel (0 = it fits).</summary>
    public static double Overflow(TextBlock block) => block.GetValue(StateProperty) is TranslateTransform { } t && t.HasAnimatedProperties ? Measure(block) : 0;

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block || e.NewValue is not true)
        {
            return;
        }

        block.SizeChanged += (_, _) => Update(block);
        block.MouseEnter += (_, _) => Update(block); // scrolls again on hover
        // The text's changes, heard only while the block is on screen: a property descriptor's listener is held by
        // WPF for good (a static table), and with it the block and its whole window — every capture window and pin
        // that had shown such a line stayed in memory with its screenshots (gigabytes after a day).
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        EventHandler changed = (_, _) => block.Dispatcher.BeginInvoke(new Action(() => Update(block)), System.Windows.Threading.DispatcherPriority.Loaded);
        var listening = false;
        block.Loaded += (_, _) =>
        {
            if (!listening)
            {
                descriptor.AddValueChanged(block, changed);
                listening = true;
            }

            Update(block);
        };
        block.Unloaded += (_, _) =>
        {
            if (listening)
            {
                descriptor.RemoveValueChanged(block, changed);
                listening = false;
            }
        };
    }

    /// <summary>The width the whole line needs beyond what is shown.</summary>
    private static double Measure(TextBlock block)
    {
        if (string.IsNullOrEmpty(block.Text) || block.ActualWidth <= 0)
        {
            return 0;
        }

        var full = new FormattedText(block.Text, System.Globalization.CultureInfo.CurrentUICulture, block.FlowDirection,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace;
        return Math.Max(0, full - Shown(block));
    }

    /// <summary>The width the line has on screen (a TextBlock's ActualWidth is the whole text even when it is cut).</summary>
    private static double Shown(TextBlock block)
    {
        var slot = LayoutInformation.GetLayoutSlot(block).Width - block.Margin.Left - block.Margin.Right;
        var shown = Math.Min(block.ActualWidth, slot > 0 ? slot : double.MaxValue);
        if (!double.IsNaN(block.Width)) shown = Math.Min(shown, block.Width);
        if (!double.IsInfinity(block.MaxWidth)) shown = Math.Min(shown, block.MaxWidth);
        return shown - block.Padding.Left - block.Padding.Right;
    }

    private static void Update(TextBlock block)
    {
        var overflow = Measure(block);
        var effect = block.TextEffects.FirstOrDefault(t => t.Transform == block.GetValue(StateProperty));
        if (overflow < 2)
        {
            if (effect != null)
            {
                ((TranslateTransform)effect.Transform).BeginAnimation(TranslateTransform.XProperty, null);
                block.TextEffects.Remove(effect);
                block.ClearValue(StateProperty);
                if (block.GetValue(TrimmingProperty) is TextTrimming trimming)
                {
                    block.TextTrimming = trimming; // as it was
                }
            }

            return;
        }

        // The whole line, moved inside the block's own bounds (which clip it).
        if (block.GetValue(TrimmingProperty) == null)
        {
            block.SetValue(TrimmingProperty, (TextTrimming?)block.TextTrimming);
        }

        block.TextTrimming = TextTrimming.None;
        block.TextWrapping = TextWrapping.NoWrap;
        block.ClipToBounds = true;
        var move = block.GetValue(StateProperty) as TranslateTransform ?? new TranslateTransform();
        block.SetValue(StateProperty, move);
        if (effect == null)
        {
            block.TextEffects.Add(new TextEffect { Transform = move, PositionStart = 0, PositionCount = int.MaxValue });
        }

        var travel = TimeSpan.FromSeconds(Math.Clamp(overflow / 40, 1.2, 12)); // about 40 DIP a second
        var pause = TimeSpan.FromSeconds(1.6);
        // Twice, then it rests at the start (again when the pointer comes onto it): scrolling for ever kept the whole
        // window drawing 60 times a second — the settings window dragged heavily because of a long path / status line.
        var scroll = new DoubleAnimationUsingKeyFrames { RepeatBehavior = new RepeatBehavior(2) };
        scroll.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scroll.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(pause)));
        scroll.KeyFrames.Add(new EasingDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(pause + travel), new SineEase { EasingMode = EasingMode.EaseInOut }));
        scroll.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(pause + travel + pause)));
        scroll.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(pause + travel + pause + travel), new SineEase { EasingMode = EasingMode.EaseInOut }));
        move.BeginAnimation(TranslateTransform.XProperty, scroll);
    }
}
