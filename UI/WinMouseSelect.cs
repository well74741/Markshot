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
        _keyProc = KeyHook;
    }

    /// <summary>For tests: Win counts as held (the real key is not touched).</summary>
    internal static Func<bool> WinDown = () => (GetAsyncKeyState(VkLWin) & 0x8000) != 0 || (GetAsyncKeyState(VkRWin) & 0x8000) != 0;

    public bool IsRunning => _hook != IntPtr.Zero;

    // The hooks live on a thread of their own with its own message loop: every mouse move and key press in the whole
    // system waits for a low-level hook, so on the UI thread (busy now and then: an animation, a layout) the pointer
    // stalled — "the mouse is held until the toolbar has opened".
    private Thread? _thread;
    private uint _threadId;

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
            ready.Set();
            if (_hook == IntPtr.Zero)
            {
                Log.Error("Win + 鼠标：无法安装鼠标钩子");
                return;
            }

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message == WmInstallKeyHook)
                {
                    if (_keyHook == IntPtr.Zero)
                    {
                        _keyHook = SetWindowsHookEx(WhKeyboardLl, _keyProc, GetModuleHandle(null), 0);
                    }

                    continue;
                }

                if (msg.Message == WmRemoveKeyHook)
                {
                    if (_keyHook != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(_keyHook);
                        _keyHook = IntPtr.Zero;
                    }

                    continue;
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_keyHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyHook);
                _keyHook = IntPtr.Zero;
            }

            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        { IsBackground = true, Name = "Markshot Win + mouse" };
        _thread.Start();
        ready.Wait(2000);
    }

    public void Stop()
    {
        if (_thread != null)
        {
            PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
            _thread = null;
        }

        Application.Current?.Dispatcher.Invoke(Cancel);
    }

    private const int WmInstallKeyHook = 0x0400 + 1; // WM_USER + n: handled by the hook thread's loop
    private const int WmRemoveKeyHook = 0x0400 + 2;

    /// <summary>The keyboard hook (Space, the masked Win release) is installed / removed on the hook thread.</summary>
    private void InstallKeyHook() => PostThreadMessage(_threadId, WmInstallKeyHook, IntPtr.Zero, IntPtr.Zero);

    private void RemoveKeyHook() => PostThreadMessage(_threadId, WmRemoveKeyHook, IntPtr.Zero, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public int Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint thread, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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
            _spaceHeld = false;
            SuppressStartMenu();
            Application.Current?.Dispatcher.BeginInvoke(ShowFrame);
            return true;
        }

        if (_button == null)
        {
            if (_swallowUntilUp is { } held && up == held)
            {
                _swallowUntilUp = null; // the right button that cancelled ends here, unseen
                return true;
            }

            if (_swallowDrawingUp is { } drew && up == drew)
            {
                _swallowDrawingUp = null; // and the button that was drawing
                return true;
            }

            return false;
        }

        if (down == MouseButton.Right && _button != MouseButton.Right)
        {
            // Right click while the area is drawn: it is dropped (as an element being drawn is), nothing is taken.
            var drawing = _button.Value;
            _button = null;
            _spaceHeld = false;
            _swallowUntilUp = MouseButton.Right;
            _swallowDrawingUp = drawing;
            if (!_swallowWin)
            {
                RemoveKeyHook();
            }

            Application.Current?.Dispatcher.BeginInvoke(HideFrame);
            return true;
        }

        if (message == WmMouseMove)
        {
            if (_spaceHeld)
            {
                // Space held (as in the capture): the whole area moves with the pointer.
                var delta = point - _spaceAnchor;
                _start += delta;
                _end += delta;
                _spaceAnchor = point;
            }
            else
            {
                _end = point;
            }

            Application.Current?.Dispatcher.BeginInvoke(PlaceFrame);
            return false; // the pointer itself moves on
        }

        if (up == _button)
        {
            Tap(); // once more at the end: Win may have been held for a while
            if (!_spaceHeld)
            {
                _end = point;
            }

            _spaceHeld = false;
            var button = _button.Value;
            var area = Area;
            _button = null;
            if (!_swallowWin)
            {
                RemoveKeyHook(); // Win already released
            }
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

    private bool _spaceHeld;
    private Point _spaceAnchor;
    private MouseButton? _swallowUntilUp; // a cancelling right click: its release is not passed on (no context menu)
    private MouseButton? _swallowDrawingUp;

    /// <summary>Tests: the drawing was dropped by a right click.</summary>
    internal bool Drawing => _button != null;

    /// <summary>Space while drawing (Win may already be released): down = the area follows the pointer, up = drawing goes on.</summary>
    internal bool HandleSpace(bool down)
    {
        if (_button == null)
        {
            return false;
        }

        if (down && !_spaceHeld)
        {
            _spaceHeld = true;
            GetCursorPos(out var p);
            _spaceAnchor = new Point(p.X, p.Y);
        }
        else if (!down && _spaceHeld)
        {
            _spaceHeld = false;
            GetCursorPos(out var p);
            _spaceAnchor = new Point(p.X, p.Y);
            _end = _spaceAnchor; // the far corner is under the pointer again
        }

        return true; // the program in front never gets the Space
    }

    /// <summary>For tests: Space held at a pointer position.</summary>
    internal void SpaceAt(bool down, Point pointer)
    {
        if (_button == null)
        {
            return;
        }

        _spaceHeld = down;
        _spaceAnchor = pointer;
        if (!down)
        {
            _end = pointer;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private void Cancel()
    {
        _spaceHeld = false;
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

    /// <summary>
    /// Win was used with the mouse: when it is released, Windows must not open Start (it opens only when nothing
    /// came between Win down and Win up, and mouse clicks do not count). A keyboard hook holds the Win-up back and
    /// replays it after a key nobody uses (0xE8), the way AutoHotkey masks Win.
    /// </summary>
    private void SuppressStartMenu()
    {
        _swallowWin = true;
        Tap();
        InstallKeyHook();
    }

    private static void Tap()
    {
        keybd_event(0xE8, 0, 0, UIntPtr.Zero);
        keybd_event(0xE8, 0, 2 /* KEYUP */, UIntPtr.Zero);
    }

    private const int WhKeyboardLl = 13;
    private const int WmKeyUp = 0x0101, WmSysKeyUp = 0x0105;
    private readonly KeyProc _keyProc;
    private IntPtr _keyHook;
    private volatile bool _swallowWin;

    private IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _button != null)
        {
            var message = wParam.ToInt32();
            var vk = Marshal.ReadInt32(lParam);
            if (vk == 0x20 /* SPACE */ && HandleSpace(message is 0x0100 or 0x0104))
            {
                return new IntPtr(1);
            }
        }

        if (code >= 0 && _swallowWin)
        {
            var message = wParam.ToInt32();
            var vk = Marshal.ReadInt32(lParam);
            if (message is WmKeyUp or WmSysKeyUp && vk is VkLWin or VkRWin)
            {
                // The real Win-up is held back; the unused key goes down / up first and then Win comes up again
                // (keys sent from here queue after the event being handled, so sending only the extra key is too late).
                _swallowWin = false;
                Tap();
                keybd_event((byte)vk, 0, 2 /* KEYUP */ | 1 /* EXTENDED */, UIntPtr.Zero);
                if (_button == null)
                {
                    RemoveKeyHook();
                }

                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    private delegate IntPtr KeyProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookEx")]
    private static extern IntPtr SetWindowsHookEx(int hookType, KeyProc callback, IntPtr module, uint threadId);

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
