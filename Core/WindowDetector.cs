namespace Markshot.Core;

public sealed class WindowTarget
{
    public IntPtr Handle { get; init; }

    /// <summary>Screen coordinates in physical pixels.</summary>
    public Rect Bounds { get; init; }

    public string Label { get; init; } = string.Empty;

    /// <summary>One of Markshot's own windows (settings, editor…): framed whole, never looked into.</summary>
    public bool Own { get; init; }
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
        var foundBounds = Rect.Empty;
        var wrapper = false; // the one found may be only a shadow / border window around the real one

        Native.EnumWindows((handle, _) =>
        {
            if (handle == IntPtr.Zero || !Native.IsWindowVisible(handle) || Native.IsIconic(handle))
            {
                return true;
            }

            Native.GetWindowThreadProcessId(handle, out var pid);
            if ((pid == ownPid && !OwnFramed(handle)) || !Shown(handle))
            {
                return true; // Markshot's capture / pins / buttons, or a window that is not really on the screen
            }

            // The window as drawn (DWM): GetWindowRect includes the invisible resize borders around it (several pixels
            // of whatever lies beside a window were framed with it, and a press there found the wrong window).
            var bounds = GetBounds(handle);
            if (bounds.Width < 8 || bounds.Height < 8 ||
                screenX < bounds.Left || screenX >= bounds.Right || screenY < bounds.Top || screenY >= bounds.Bottom)
            {
                return true;
            }

            var className = GetClassName(handle);
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow")
            {
                return true;
            }

            if (found != IntPtr.Zero)
            {
                // After a wrapper: the real window just inside it (a few dozen pixels of shadow / border around) wins.
                var inset = Math.Max(Math.Max(bounds.Left - foundBounds.Left, foundBounds.Right - bounds.Right),
                                     Math.Max(bounds.Top - foundBounds.Top, foundBounds.Bottom - bounds.Bottom));
                if (inset >= 0 && inset <= 48 && bounds.Width * bounds.Height >= foundBounds.Width * foundBounds.Height * 0.6)
                {
                    found = handle;
                }

                return false;
            }

            found = handle;
            foundBounds = bounds;
            wrapper = IsWrapper(handle);
            return wrapper; // a wrapper: look one window further
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

        Native.GetWindowThreadProcessId(found, out var foundPid);
        return new WindowTarget { Handle = handle2, Bounds = bounds, Label = label, Own = foundPid == ownPid };
    }

    /// <summary>
    /// Markshot's own windows that can be captured like any program's: those with a title bar (settings, editor,
    /// history, text results). The capture itself, pins, toolbars and the floating button have none.
    /// </summary>
    private static bool OwnFramed(IntPtr handle) =>
        (Native.GetWindowLongPtr(handle, -16 /* GWL_STYLE */).ToInt64() & 0x00C00000 /* WS_CAPTION */) == 0x00C00000;

    /// <summary>
    /// A window really on the screen: not cloaked (a suspended store app, a window on another virtual desktop — "visible"
    /// but not shown, and often covering everything), not click-through, not a fully see-through layer.
    /// </summary>
    private static bool Shown(IntPtr handle)
    {
        if (DwmGetWindowAttribute(handle, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        var exStyle = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
        if ((exStyle & Native.WS_EX_TRANSPARENT) != 0)
        {
            return false; // overlays that let every click through (game / recorder HUDs)
        }

        if ((exStyle & WS_EX_LAYERED) != 0 && GetLayeredWindowAttributes(handle, out _, out var alpha, out var flags) &&
            (flags & LWA_ALPHA) != 0 && alpha == 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// A window drawn only as another's shadow / glowing border (WeChat, QQ, DingTalk and others make one around their
    /// window): no title, see-through or a tool window that never takes the focus.
    /// </summary>
    private static bool IsWrapper(IntPtr handle)
    {
        var title = new StringBuilder(8);
        Native.GetWindowText(handle, title, title.Capacity);
        if (title.Length > 0)
        {
            return false;
        }

        var exStyle = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
        return (exStyle & (WS_EX_LAYERED | 0x80 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */)) != 0;
    }

    private const int DWMWA_CLOAKED = 14;
    private const long WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x2;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);

    /// <summary>The window of the program in use, or none when that is the desktop, the taskbar or Markshot itself.</summary>
    public static IntPtr ActiveProgram()
    {
        var handle = Native.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        Native.GetWindowThreadProcessId(handle, out var pid);
        return pid == (uint)Environment.ProcessId || GetClassName(handle) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow"
            ? IntPtr.Zero
            : handle;
    }

    /// <summary>A top-level window's frame as <see cref="Detect"/> reports it (screen pixels).</summary>
    public static Rect BoundsOf(IntPtr handle) => GetBounds(handle);

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
