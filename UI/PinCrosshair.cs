using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Markshot.UI;

/// <summary>
/// 辅助线 for a pin: while Alt is held over the pin, a crosshair through the pointer across the whole monitor
/// (the same lines as in the capture). Polls the Alt key so it works whether or not the pin is the active window.
/// Click-through and never activated.
/// </summary>
internal sealed class PinCrosshair
{
    private const int VK_MENU = 0x12;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly FrameworkElement _host;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Rectangle _horizontal = Line();
    private readonly Rectangle _vertical = Line();
    private readonly Canvas _canvas = new() { IsHitTestVisible = false, Background = Brushes.Transparent };
    private Window? _window;
    private Int32Rect _monitor;

    public PinCrosshair(FrameworkElement host)
    {
        _host = host;
        _canvas.Children.Add(_horizontal);
        _canvas.Children.Add(_vertical);
        _timer.Tick += (_, _) => Update();
        host.MouseEnter += (_, _) => _timer.Start();
        host.MouseLeave += (_, _) =>
        {
            if (!IsPointerOverHost())
            {
                Hide();
                _timer.Stop();
            }
        };
    }

    /// <summary>True while Alt belongs to something else (the pen's 吸色).</summary>
    public Func<bool>? Paused { get; set; }

    /// <summary>The lines are on screen.</summary>
    public bool IsShown => _window?.IsVisible == true;

    public void Hide()
    {
        _window?.Hide();
        _monitor = default;
    }

    private static Rectangle Line() => new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(0xB3, 0x4C, 0x8D, 0xFF)),
        Width = 1,
        Height = 1,
        IsHitTestVisible = false,
        SnapsToDevicePixels = true
    };

    /// <summary>One look at the keyboard and the pointer (the timer's tick; tests call it directly).</summary>
    internal void Update()
    {
        var altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        // Alt + drag moves the pin: no lines while a mouse button is held.
        var buttonDown = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (!altDown || buttonDown || Paused?.Invoke() == true || !IsPointerOverHost() || !_host.IsVisible)
        {
            Hide();
            if (!IsPointerOverHost())
            {
                _timer.Stop();
            }

            return;
        }

        Native.GetCursorPos(out var cursor);
        ShowAt(cursor.X, cursor.Y);
    }

    /// <summary>The lines through a screen point (physical pixels) across its monitor.</summary>
    internal void ShowAt(int x, int y)
    {
        EnsureWindow();
        if (!_window!.IsVisible)
        {
            _window.Show();
        }

        var monitor = Core.Recording.Monitors.At(x, y).Bounds;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle != IntPtr.Zero && !monitor.Equals(_monitor))
        {
            _monitor = monitor;
            Native.SetWindowPos(handle, Native.HWND_TOPMOST, monitor.X, monitor.Y, monitor.Width, monitor.Height,
                Native.SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        var dpi = Math.Max(0.5, VisualTreeHelper.GetDpi(_window).DpiScaleX);
        _horizontal.Width = monitor.Width / dpi;
        _vertical.Height = monitor.Height / dpi;
        Canvas.SetTop(_horizontal, Math.Floor((y - monitor.Y) / dpi));
        Canvas.SetLeft(_vertical, Math.Floor((x - monitor.X) / dpi));
    }

    private void EnsureWindow()
    {
        if (_window != null)
        {
            return;
        }

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Left = -32000,
            Top = -32000,
            Width = 1,
            Height = 1,
            IsHitTestVisible = false,
            Content = _canvas
        };
        _window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(_window).Handle;
            var style = GetWindowLong(handle, Native.GWL_EXSTYLE);
            SetWindowLong(handle, Native.GWL_EXSTYLE, style | WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);
        };
        if (Window.GetWindow(_host) is { } owner)
        {
            _window.Owner = owner;
            owner.Closed += (_, _) => _window.Close();
        }
    }

    private bool IsPointerOverHost()
    {
        if (!_host.IsVisible || PresentationSource.FromVisual(_host) == null)
        {
            return false;
        }

        Native.GetCursorPos(out var cursor);
        var local = _host.PointFromScreen(new Point(cursor.X, cursor.Y));
        return local.X >= 0 && local.Y >= 0 && local.X < _host.ActualWidth && local.Y < _host.ActualHeight;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
