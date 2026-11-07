using System.Runtime.InteropServices;

namespace Markshot.Core;

internal static class Native
{
    /// <summary>
    /// A window as big as the screen makes Explorer treat it as a full-screen program: the taskbar drops behind it and
    /// comes back (repainting the clock / tray, a visible flash) when it closes. This property opts the window out.
    /// </summary>
    public static void NotFullscreenApp(IntPtr window)
    {
        if (window != IntPtr.Zero)
        {
            SetProp(window, "NonRudeHWND", new IntPtr(1));
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(IntPtr window, string name, IntPtr data);

    /// <summary>
    /// Brings a window to the front with the keyboard, also when Markshot did not get the last input (a click on the
    /// never-activating floating button, a timer): Windows then refuses SetForegroundWindow and Esc went to the other program.
    /// A harmless unassigned key (0xE8) makes this process the last input; failing that, the input queues are joined briefly.
    /// </summary>
    public static bool ForceForeground(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        if (GetForegroundWindow() == window)
        {
            return true;
        }

        var inputs = new[]
        {
            new KeyInput { Type = 1, Vk = 0xE8 },
            new KeyInput { Type = 1, Vk = 0xE8, Flags = 2 /* KEYEVENTF_KEYUP */ }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyInput>());
        if (SetForegroundWindow(window) && GetForegroundWindow() == window)
        {
            return true;
        }

        var front = GetForegroundWindow();
        var frontThread = GetWindowThreadProcessId(front, out _);
        var mine = GetCurrentThreadId();
        if (frontThread == 0 || frontThread == mine)
        {
            return SetForegroundWindow(window);
        }

        AttachThreadInput(mine, frontThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            AttachThreadInput(mine, frontThread, false);
        }

        return GetForegroundWindow() == window;
    }

    // INPUT with a KEYBDINPUT (the union padded to the size of a MOUSEINPUT on 64-bit).
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput
    {
        public uint Type;
        private uint _pad;
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
        private long _pad2;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyInput[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint to, bool on);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    public const uint GA_ROOT = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint CWP_SKIPINVISIBLE = 0x0001;
    public const uint CWP_SKIPDISABLED = 0x0002;
    public const uint CWP_SKIPTRANSPARENT = 0x0004;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr ChildWindowFromPointEx(IntPtr hWnd, POINT point, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    public const int SRCCOPY = 0x00CC0020;
    public const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void EnableDarkTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
        DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    public static POINT CursorPosition()
    {
        GetCursorPos(out var point);
        return point;
    }
}
