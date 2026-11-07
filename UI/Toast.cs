using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Markshot.UI;

/// <summary>
/// A small notice bubble in the corner above the taskbar (in Markshot's colours): no Windows notification, no sound,
/// it never takes the focus and goes by itself after a moment. One at a time; a new notice replaces the shown one.
/// </summary>
public sealed class Toast : Window
{
    private static Toast? _current;

    private readonly DispatcherTimer _timer;
    private readonly Border _card;
    private bool _leaving;
    private bool _isClosed;

    /// <summary>The bubble on screen (tests).</summary>
    internal static Toast? Current => _current;

    internal string Heading { get; }

    internal string Body { get; }

    public static void Notify(string title, string? text = null)
    {
        if (Application.Current == null)
        {
            return;
        }

        try
        {
            _current?.CloseNow();
            _current = new Toast(title, text ?? "");
            _current.Show();
        }
        catch (Exception ex)
        {
            Log.Error("显示提示失败", ex);
        }
    }

    private Toast(string title, string text)
    {
        Heading = title;
        Body = text;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Left = -32000;
        Top = -32000;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var body = new StackPanel { MaxWidth = 300 };
        var heading = new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFF5F5F5));
        body.Children.Add(heading);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var line = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            line.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF9A9A9A));
            body.Children.Add(line);
        }

        _card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 14, 9),
            Margin = new Thickness(10),
            LayoutTransform = UiScale.Transform,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.35 },
            Child = body,
            Opacity = 0
        };
        _card.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF1E1E1E));
        _card.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x33FFFFFF));
        Content = _card;

        // Long text stays a little longer.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(1800 + (title.Length + text.Length) * 35, 1800, 4500)) };
        _timer.Tick += (_, _) => Leave();
        MouseLeftButtonUp += (_, _) => Leave(); // a click puts it away
        MouseEnter += (_, _) => _timer.Stop(); // being read: it waits
        MouseLeave += (_, _) => _timer.Start();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, new IntPtr(style | Native.WS_EX_TOOLWINDOW | 0x08000000 /* WS_EX_NOACTIVATE */));
        };
        Loaded += (_, _) => Place();
        Closed += (_, _) =>
        {
            _isClosed = true;
            _timer.Stop();
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };
    }

    /// <summary>Bottom right of the work area of the monitor with the mouse (above the taskbar).</summary>
    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var cursor = Native.CursorPosition();
        var work = Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea;
        // Move there first so the size is measured with that monitor's DPI.
        Native.SetWindowPos(handle, IntPtr.Zero, work.Right - 10, work.Bottom - 10, 0, 0, 0x0001 | 0x0004 | Native.SWP_NOACTIVATE);
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var gap = (int)Math.Round(6 * dpi.DpiScaleX);
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, work.Right - width - gap, work.Bottom - height - gap, 0, 0, 0x0001 | Native.SWP_NOACTIVATE);
        Motion.PopIn(_card, fromAbove: false, duration: Motion.Bar);
        _timer.Start();
    }

    private void Leave()
    {
        if (_leaving)
        {
            return;
        }

        _leaving = true;
        _timer.Stop();
        Motion.PopOut(_card, towardsAbove: false, completed: CloseNow, duration: Motion.Bar);
    }

    private void CloseNow()
    {
        _timer.Stop();
        if (!_isClosed)
        {
            Close();
        }
    }
}
