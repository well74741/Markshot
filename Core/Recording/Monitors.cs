// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
using System.Runtime.InteropServices;

namespace Markshot.Core.Recording;

/// <summary>A monitor in physical pixels with its scale (1.0 = 96 dpi).</summary>
public readonly record struct MonitorInfo(Int32Rect Bounds, Int32Rect WorkArea, double Scale);

/// <summary>Monitor geometry in physical (virtual-desktop) pixels for the recorder.</summary>
public static class Monitors
{
    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var b = screen.Bounds;
            var w = screen.WorkingArea;
            list.Add(new MonitorInfo(new Int32Rect(b.X, b.Y, b.Width, b.Height), new Int32Rect(w.X, w.Y, w.Width, w.Height),
                ScaleAt(b.X + b.Width / 2, b.Y + b.Height / 2)));
        }

        return list;
    }

    /// <summary>The monitor containing the point, or the nearest one.</summary>
    public static MonitorInfo At(int x, int y)
    {
        var all = All();
        foreach (var m in all)
        {
            if (x >= m.Bounds.X && x < m.Bounds.X + m.Bounds.Width && y >= m.Bounds.Y && y < m.Bounds.Y + m.Bounds.Height)
            {
                return m;
            }
        }

        return all.OrderBy(m => Distance(m.Bounds, x, y)).First();
    }

    public static MonitorInfo Of(Int32Rect rect) => At(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    /// <summary>Bounding box of all monitors.</summary>
    public static Int32Rect Union()
    {
        var all = All();
        var left = all.Min(m => m.Bounds.X);
        var top = all.Min(m => m.Bounds.Y);
        var right = all.Max(m => m.Bounds.X + m.Bounds.Width);
        var bottom = all.Max(m => m.Bounds.Y + m.Bounds.Height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    public static bool Intersects(Int32Rect rect) =>
        All().Any(m => rect.X < m.Bounds.X + m.Bounds.Width && rect.X + rect.Width > m.Bounds.X &&
                       rect.Y < m.Bounds.Y + m.Bounds.Height && rect.Y + rect.Height > m.Bounds.Y);

    private static double Distance(Int32Rect r, int x, int y)
    {
        var dx = Math.Max(0, Math.Max(r.X - x, x - (r.X + r.Width)));
        var dy = Math.Max(0, Math.Max(r.Y - y, y - (r.Y + r.Height)));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static double ScaleAt(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new Native.POINT { X = x, Y = y }, 2);
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 1;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Native.POINT point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
