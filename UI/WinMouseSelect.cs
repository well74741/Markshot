using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Markshot.UI;

/// <summary>
/// Win + 鼠标键: with the Win key held, a press of the left / middle / right button starts drawing an area straight on
/// the screen (no capture window, no dimming, no hints: just the frame), and the release hands the area over.
/// A low-level mouse hook swallows those presses, so the program under the pointer never sees them; a dummy key is
/// sent so that releasing Win afterwards does not open the Start menu.
/// </summary>
public sealed class WinMouseSelect
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201, WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204, WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207, WmMButtonUp = 0x0208;
    private const int VkLWin = 0x5B, VkRWin = 0x5C;

    private readonly Action<MouseButton, Int32Rect> _done;
    private readonly HookProc _proc;
    private IntPtr _hook;
    private MouseButton? _button;
    private Point _start;
    private Point _end;
    private Window? _frame;

    public WinMouseSelect(Action<MouseButton, Int32Rect> done)
    {
        _done = done;
        _proc = Hook;
    }

    /// <summary>For tests: Win counts as held (the real key is not touched).</summary>
    internal static Func<bool> WinDown = () => (GetAsyncKeyState(VkLWin) & 0x8000) != 0 || (GetAsyncKeyState(VkRWin) & 0x8000) != 0;

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_hook == IntPtr.Zero)
        {
            _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Log.Error("Win + 鼠标：无法安装鼠标钩子");
            }
        }
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        Cancel();
    }

    private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<HookData>(lParam);
            var point = new Point(data.X, data.Y);
            if (Handle(message, point))
            {
                return new IntPtr(1); // swallowed: the program under the pointer never sees it
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>One mouse message (screen pixels); true = it belongs to the area being drawn.</summary>
    internal bool Handle(int message, Point point)
    {
        MouseButton? down = message switch
        {
            WmLButtonDown => MouseButton.Left,
            WmMButtonDown => MouseButton.Middle,
            WmRButtonDown => MouseButton.Right,
            _ => null
        };
        MouseButton? up = message switch
        {
            WmLButtonUp => MouseButton.Left,
            WmMButtonUp => MouseButton.Middle,
            WmRButtonUp => MouseButton.Right,
            _ => null
        };

        if (_button == null && down is { } pressed && WinDown())
        {
            _button = pressed;
            _start = _end = point;
            SuppressStartMenu();
            Application.Current?.Dispatcher.BeginInvoke(ShowFrame);
            return true;
        }

        if (_button == null)
        {
            return false;
        }

        if (message == WmMouseMove)
        {
            _end = point;
            Application.Current?.Dispatcher.BeginInvoke(PlaceFrame);
            return false; // the pointer itself moves on
        }

        if (up == _button)
        {
            _end = point;
            var button = _button.Value;
            var area = Area;
            _button = null;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                HideFrame();
                // The frame is gone from the screen before the area is taken.
                var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                later.Tick += (_, _) =>
                {
                    later.Stop();
                    _done(button, area);
                };
                later.Start();
            });
            return true;
        }

        return down != null || up != null; // other buttons while drawing: ignored
    }

    /// <summary>The area drawn so far (screen pixels).</summary>
    internal Int32Rect Area
    {
        get
        {
            var x = (int)Math.Min(_start.X, _end.X);
            var y = (int)Math.Min(_start.Y, _end.Y);
            return new Int32Rect(x, y, (int)Math.Abs(_end.X - _start.X), (int)Math.Abs(_end.Y - _start.Y));
        }
    }

    private void Cancel()
    {
        _button = null;
        HideFrame();
    }

    // ---- the frame: a small click-through window exactly over the area (sized in pixels, so any DPI fits)

    private void ShowFrame()
    {
        if (_frame == null)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0x4C, 0x8D, 0xFF))
            };
            _frame = new Window
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
                Content = border
            };
            _frame.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(_frame).Handle;
                var style = GetWindowLong(handle, Native.GWL_EXSTYLE);
                SetWindowLong(handle, Native.GWL_EXSTYLE, style | 0x08000000 /* NOACTIVATE */ | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);
            };
        }

        if (!_frame.IsVisible)
        {
            _frame.Show();
        }

        PlaceFrame();
    }

    private void PlaceFrame()
    {
        if (_frame is not { IsVisible: true } || _button == null)
        {
            return;
        }

        var area = Area;
        var handle = new WindowInteropHelper(_frame).Handle;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, area.X, area.Y, Math.Max(2, area.Width), Math.Max(2, area.Height),
            Native.SWP_NOACTIVATE | 0x0040 /* SHOWWINDOW */);
    }

    private void HideFrame() => _frame?.Hide();

    /// <summary>A key nobody uses goes down and up while Win is held: Windows then does not open Start on its release.</summary>
    private static void SuppressStartMenu()
    {
        keybd_event(0xE8, 0, 0, UIntPtr.Zero);
        keybd_event(0xE8, 0, 2 /* KEYUP */, UIntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HookData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
