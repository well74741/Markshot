using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Markshot.UI;

/// <summary>
/// Small transparent window that carries a pin's annotation toolbar. Dragging the
/// toolbar moves only this window (not the pin). It never activates, so clicking the
/// toolbar keeps keyboard focus — and an open text edit — in the pin.
/// </summary>
internal sealed class ToolbarHostWindow : Window
{
    private const int WS_EX_NOACTIVATE = 0x08000000;

    public ToolbarHostWindow(UIElement content)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Content = content;
        SourceInitialized += (_, _) =>
        {
            var handle = Handle;
            var style = GetWindowLong(handle, Native.GWL_EXSTYLE);
            SetWindowLong(handle, Native.GWL_EXSTYLE, style | WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Moves the window to a screen position in physical pixels.</summary>
    public void MoveTo(int x, int y)
    {
        if (Handle != IntPtr.Zero)
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>Window rectangle in physical pixels.</summary>
    public Native.RECT PixelBounds
    {
        get
        {
            Native.GetWindowRect(Handle, out var rect);
            return rect;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
