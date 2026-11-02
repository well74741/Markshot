// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Path = System.Windows.Shapes.Path;
using Markshot.Core.Recording;

namespace Markshot.UI.Recording;

/// <summary>
/// Opening the recorder: the screen around the area darkens for a moment (820 ms: in over the
/// first 20 %, held to 50 %, then out), leading the eye to the frame. Click-through, never active.
/// </summary>
internal sealed class RecordDimWindow : Window
{
    private const int WS_EX_NOACTIVATE = 0x08000000;

    public RecordDimWindow(Int32Rect area)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Opacity = 0;
        var union = Monitors.Union();
        var path = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        Content = path;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, Native.GWL_EXSTYLE);
            SetWindowLong(handle, Native.GWL_EXSTYLE, style | WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);
            Native.SetWindowPos(handle, Native.HWND_TOPMOST, union.X, union.Y, union.Width, union.Height, Native.SWP_NOACTIVATE);
        };
        Loaded += (_, _) =>
        {
            // Physical pixels → this window's DIPs; the area stays clear.
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var outer = new Rect(0, 0, union.Width / dpi, union.Height / dpi);
            var hole = new Rect((area.X - union.X) / dpi, (area.Y - union.Y) / dpi, area.Width / dpi, area.Height / dpi);
            path.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(outer), new RectangleGeometry(hole));
            var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(820) };
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.2), Motion.Ease));
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.5)));
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), Motion.Ease));
            flash.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, flash);
        };
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
