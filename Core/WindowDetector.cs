namespace Markshot.Core;

public sealed class WindowTarget
{
    public IntPtr Handle { get; init; }

    /// <summary>Screen coordinates in physical pixels.</summary>
    public Rect Bounds { get; init; }

    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// Finds the top-level window (or child control) under the cursor while ignoring
/// Markshot's own overlay/pin windows.
/// </summary>
public static class WindowDetector
{
    public static WindowTarget? Detect(int screenX, int screenY, bool preferChild)
    {
        var ownPid = (uint)Environment.ProcessId;
        var found = IntPtr.Zero;

        Native.EnumWindows((handle, _) =>
        {
            if (handle == IntPtr.Zero || !Native.IsWindowVisible(handle) || Native.IsIconic(handle))
            {
                return true;
            }

            Native.GetWindowThreadProcessId(handle, out var pid);
            if (pid == ownPid)
            {
                return true;
            }

            if (!Native.GetWindowRect(handle, out var rect) ||
                rect.Width < 8 || rect.Height < 8)
            {
                return true;
            }

            if (screenX < rect.Left || screenX >= rect.Right ||
                screenY < rect.Top || screenY >= rect.Bottom)
            {
                return true;
            }

            var className = GetClassName(handle);
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow")
            {
                return true;
            }

            found = handle;
            return false;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero)
        {
            return null;
        }

        var handle2 = found;
        var bounds = GetBounds(found);
        var label = GetLabel(found);

        if (preferChild)
        {
            var point = new Native.POINT { X = screenX, Y = screenY };
            if (Native.ScreenToClient(found, ref point))
            {
                var child = Native.ChildWindowFromPointEx(
                    found, point,
                    Native.CWP_SKIPINVISIBLE | Native.CWP_SKIPDISABLED | Native.CWP_SKIPTRANSPARENT);

                if (child != IntPtr.Zero && child != found)
                {
                    var childBounds = GetBounds(child, useDwm: false);
                    if (childBounds.Width >= 8 && childBounds.Height >= 8)
                    {
                        handle2 = child;
                        bounds = childBounds;
                        label = GetLabel(child, fallback: classNameFallback(found));
                    }
                }
            }
        }

        return new WindowTarget { Handle = handle2, Bounds = bounds, Label = label };
    }

    private static Rect GetBounds(IntPtr handle, bool useDwm = true)
    {
        if (useDwm &&
            Native.DwmGetWindowAttribute(
                handle,
                Native.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var frame,
                System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) == 0 &&
            frame.Width > 0 && frame.Height > 0)
        {
            return new Rect(frame.Left, frame.Top, frame.Width, frame.Height);
        }

        if (Native.GetWindowRect(handle, out var rect) && rect.Width > 0 && rect.Height > 0)
        {
            return new Rect(rect.Left, rect.Top, rect.Width, rect.Height);
        }

        return Rect.Empty;
    }

    private static string GetLabel(IntPtr handle, string fallback = "窗口")
    {
        var buffer = new StringBuilder(256);
        Native.GetWindowText(handle, buffer, buffer.Capacity);
        var title = buffer.ToString().Trim();

        if (string.IsNullOrEmpty(title))
        {
            title = GetClassName(handle);
        }

        if (string.IsNullOrEmpty(title))
        {
            title = fallback;
        }

        return title.Length > 48 ? title[..48] + "…" : title;
    }

    private static string GetClassName(IntPtr handle)
    {
        var buffer = new StringBuilder(128);
        Native.GetClassName(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string classNameFallback(IntPtr handle)
    {
        var name = GetClassName(handle);
        return string.IsNullOrEmpty(name) ? "元素" : name;
    }
}
