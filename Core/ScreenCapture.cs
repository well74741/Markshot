using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Drawing = System.Drawing;

namespace Markshot.Core;

/// <summary>Captures the whole virtual desktop (all monitors) into a frozen bitmap.</summary>
public static class ScreenCapture
{
    public static (BitmapSource Image, Int32Rect Bounds) CaptureVirtualScreen()
    {
        int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("无法获取屏幕尺寸。");
        }

        using var bitmap = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            var destination = graphics.GetHdc();
            var source = Native.GetDC(IntPtr.Zero);
            try
            {
                var ok = Native.BitBlt(
                    destination, 0, 0, width, height,
                    source, x, y,
                    Native.SRCCOPY | Native.CAPTUREBLT);

                if (!ok)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "抓屏失败。");
                }
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, source);
                graphics.ReleaseHdc(destination);
            }
        }

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var image = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return (image, new Int32Rect(x, y, width, height));
        }
        finally
        {
            Native.DeleteObject(hBitmap);
        }
    }
}
