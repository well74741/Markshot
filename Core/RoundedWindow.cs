using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Markshot.Core;

/// <summary>
/// Round corners for a plain (not see-through) window, the toolbars' look: the system's rounding on Windows 11, a
/// rounded window region everywhere (it follows the window's size). See-through windows cannot be left out of
/// screenshots / recordings, so the bars that must not be in them are plain windows shaped like this.
/// </summary>
public static class RoundedWindow
{
    public static void Apply(Window window, double radius)
    {
        (int Width, int Height) shaped = default;
        void Shape(IntPtr handle, int width, int height)
        {
            if (width <= 0 || height <= 0 || (width, height) == shaped)
            {
                return;
            }

            shaped = (width, height);
            var diameter = (int)Math.Round(radius * 2 * VisualTreeHelper.GetDpi(window).DpiScaleX);
            var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
            if (SetWindowRgn(handle, region, true) == 0)
            {
                DeleteObject(region);
            }
        }

        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
            var none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE: no outline
            DwmSetWindowAttribute(handle, 34 /* DWMWA_BORDER_COLOR */, ref none, sizeof(int));
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int message, IntPtr _, IntPtr lParam, ref bool _) =>
            {
                if (message == 0x0005 /* WM_SIZE */)
                {
                    Shape(hwnd, (int)(lParam.ToInt64() & 0xFFFF), (int)((lParam.ToInt64() >> 16) & 0xFFFF));
                }

                return IntPtr.Zero;
            });
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
