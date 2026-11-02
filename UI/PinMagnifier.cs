using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Markshot.UI;

/// <summary>
/// The capture magnifier card for a pin: shown next to the cursor while Alt is held over
/// the pin, hidden when Alt is released or the pointer leaves. Polls the Alt key so it works
/// whether or not the pin is the active window. Click-through and never activated.
/// </summary>
internal sealed class PinMagnifier
{
    private const int VK_MENU = 0x12;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly FrameworkElement _host;
    private readonly Func<BitmapSource> _image;
    private readonly Func<Point, Point> _toPixel;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly MagnifierLayer _layer = new() { Width = MagnifierLayer.CardWidth, Height = MagnifierLayer.CardHeight };
    private Window? _window;
    private BitmapSource? _source;
    private readonly byte[] _pixel = new byte[4];

    /// <param name="host">Element showing the image (pointer position is taken relative to it).</param>
    /// <param name="image">The picture as shown (read once per Alt press).</param>
    /// <param name="toPixel">Host DIP point → image pixel.</param>
    public PinMagnifier(FrameworkElement host, Func<BitmapSource> image, Func<Point, Point> toPixel)
    {
        _host = host;
        _image = image;
        _toPixel = toPixel;
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

    public void Hide()
    {
        _source = null;
        _window?.Hide();
    }

    private void Update()
    {
        var altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        if (!altDown || !IsPointerOverHost() || !_host.IsVisible)
        {
            Hide();
            if (!IsPointerOverHost())
            {
                _timer.Stop();
            }

            return;
        }

        if (_source == null)
        {
            var image = _image();
            _source = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32
                ? image
                : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            if (_source.CanFreeze && !_source.IsFrozen)
            {
                _source.Freeze();
            }

            _layer.Source = _source;
        }

        Native.GetCursorPos(out var cursor);
        var pixel = _toPixel(_host.PointFromScreen(new Point(cursor.X, cursor.Y)));
        var x = (int)Math.Clamp(pixel.X, 0, _source.PixelWidth - 1);
        var y = (int)Math.Clamp(pixel.Y, 0, _source.PixelHeight - 1);
        _source.CopyPixels(new Int32Rect(x, y, 1, 1), _pixel, 4, 0);
        _layer.Pixel = new Point(x, y);
        _layer.PixelColor = Color.FromRgb(_pixel[2], _pixel[1], _pixel[0]);
        _layer.HasPixel = true;
        _layer.InvalidateVisual();

        EnsureWindow();
        if (!_window!.IsVisible)
        {
            _window.Show();
        }

        Place(cursor.X, cursor.Y);
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
            SizeToContent = SizeToContent.WidthAndHeight,
            IsHitTestVisible = false,
            Content = _layer
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
        }
    }

    /// <summary>Card at the lower right of the cursor, flipped when it would leave the screen.</summary>
    private void Place(int cursorX, int cursorY)
    {
        var handle = new WindowInteropHelper(_window!).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(_window!).DpiScaleX;
        var width = (int)Math.Round(MagnifierLayer.CardWidth * dpi);
        var height = (int)Math.Round(MagnifierLayer.CardHeight * dpi);
        var gap = (int)Math.Round(18 * dpi);
        var left = cursorX + gap;
        var top = cursorY + gap;
        var right = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN) + Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var bottom = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN) + Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (left + width > right)
        {
            left = cursorX - gap - width;
        }

        if (top + height > bottom)
        {
            top = cursorY - gap - height;
        }

        Native.SetWindowPos(handle, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
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
