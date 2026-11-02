using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Markshot.Core;

public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey, string Text)
{
    // Mouse buttons as hotkeys (virtual-key codes of the buttons).
    public const uint VkMiddle = 0x04;
    public const uint VkX1 = 0x05;
    public const uint VkX2 = 0x06;

    public const string MiddleName = "鼠标中键";
    public const string X1Name = "鼠标侧键1";
    public const string X2Name = "鼠标侧键2";

    /// <summary>A mouse button (middle / side) rather than a key: needs the mouse hook.</summary>
    public bool IsMouse => VirtualKey is VkMiddle or VkX1 or VkX2;

    /// <summary>The settings box text for a mouse button with the held modifiers, e.g. "Ctrl+鼠标侧键1".</summary>
    public static string FormatMouse(MouseButton button, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(button switch
        {
            MouseButton.XButton1 => X1Name,
            MouseButton.XButton2 => X2Name,
            _ => MiddleName
        });
        return string.Join("+", parts);
    }

    private static uint MouseVirtualKey(string name) => name.ToLowerInvariant() switch
    {
        "鼠标中键" or "中键" or "middlebutton" or "mbutton" or "middle" => VkMiddle,
        "鼠标侧键1" or "侧键1" or "xbutton1" or "mousex1" => VkX1,
        "鼠标侧键2" or "侧键2" or "xbutton2" or "mousex2" => VkX2,
        _ => 0
    };

    public static bool TryParse(string? text, out HotkeyGesture gesture, out string error)
    {
        gesture = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "快捷键为空";
            return false;
        }

        uint modifiers = 0;
        Key key = Key.None;
        uint mouse = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= Native.MOD_CONTROL;
                    continue;
                case "alt":
                    modifiers |= Native.MOD_ALT;
                    continue;
                case "shift":
                    modifiers |= Native.MOD_SHIFT;
                    continue;
                case "win":
                case "windows":
                    modifiers |= Native.MOD_WIN;
                    continue;
            }

            if (MouseVirtualKey(part) is var button and not 0)
            {
                if (key != Key.None || mouse != 0)
                {
                    error = "只能包含一个主键";
                    return false;
                }

                mouse = button;
                continue;
            }

            if (key != Key.None || mouse != 0)
            {
                error = "只能包含一个主键";
                return false;
            }

            if (!TryParseKey(part, out key))
            {
                error = $"无法识别按键 “{part}”";
                return false;
            }
        }

        if (mouse != 0)
        {
            gesture = new HotkeyGesture(modifiers, mouse, Normalize(text));
            return true;
        }

        if (key == Key.None)
        {
            error = "缺少主键，例如 F1 或 Ctrl+1";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key), Normalize(text));
        return true;
    }

    public static bool TryFormat(Key key, ModifierKeys modifiers, out string text)
    {
        var keyName = GetKeyName(key);
        if (keyName == null)
        {
            text = string.Empty;
            return false;
        }

        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(keyName);
        text = string.Join("+", parts);
        return true;
    }

    private static string? GetKeyName(Key key)
    {
        var value = (int)key;

        if (value >= (int)Key.A && value <= (int)Key.Z)
        {
            return ((char)('A' + value - (int)Key.A)).ToString();
        }

        if (value >= (int)Key.D0 && value <= (int)Key.D9)
        {
            return ((char)('0' + value - (int)Key.D0)).ToString();
        }

        var name = key.ToString();

        if (name.Length is 2 or 3 && name[0] == 'F' &&
            int.TryParse(name.AsSpan(1), out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            return name;
        }

        if (name.StartsWith("NumPad", StringComparison.Ordinal))
        {
            return name;
        }

        return key switch
        {
            Key.Escape => "Esc",
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Enter => "Enter",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Snapshot => "PrintScreen",
            Key.Multiply => "Multiply",
            Key.Add => "Add",
            Key.Subtract => "Subtract",
            Key.Decimal => "Decimal",
            Key.Divide => "Divide",
            _ => null
        };
    }

    private static string Normalize(string text)
    {
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => p.ToLowerInvariant() switch
            {
                "control" => "Ctrl",
                "ctrl" => "Ctrl",
                "alt" => "Alt",
                "shift" => "Shift",
                "win" or "windows" => "Win",
                _ => p.Length <= 3 ? p.ToUpperInvariant() : p
            });

        return string.Join("+", parts);
    }

    private static bool TryParseKey(string text, out Key key)
    {
        key = Key.None;

        if (text.Length == 1)
        {
            var c = char.ToUpperInvariant(text[0]);
            if (c is >= 'A' and <= 'Z')
            {
                key = (Key)((int)Key.A + (c - 'A'));
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                key = (Key)((int)Key.D0 + (c - '0'));
                return true;
            }
        }

        switch (text.ToLowerInvariant())
        {
            case "esc":
            case "escape":
                key = Key.Escape;
                return true;
            case "printscreen":
            case "snapshot":
                key = Key.Snapshot;
                return true;
            case "delete":
            case "del":
                key = Key.Delete;
                return true;
            case "insert":
            case "ins":
                key = Key.Insert;
                return true;
            case "pageup":
            case "pgup":
                key = Key.PageUp;
                return true;
            case "pagedown":
            case "pgdn":
                key = Key.PageDown;
                return true;
            case "space":
                key = Key.Space;
                return true;
            case "tab":
                key = Key.Tab;
                return true;
            case "enter":
            case "return":
                key = Key.Enter;
                return true;
            case "backspace":
                key = Key.Back;
                return true;
            case "up":
                key = Key.Up;
                return true;
            case "down":
                key = Key.Down;
                return true;
            case "left":
                key = Key.Left;
                return true;
            case "right":
                key = Key.Right;
                return true;
        }

        if (Enum.TryParse<Key>(text, true, out var parsed) && parsed != Key.None)
        {
            key = parsed;
            return true;
        }

        return false;
    }
}

public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();

    // Mouse-button hotkeys: a low-level mouse hook (RegisterHotKey only knows keys).
    private readonly List<(HotkeyGesture Gesture, Action Action)> _mouseBindings = new();
    private readonly HashSet<uint> _swallowUp = new();
    private LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHook;
    private int _nextId = 0xA100;
    private bool _disposed;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("Markshot.HotkeyWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW
            ParentWindow = new IntPtr(-3)     // HWND_MESSAGE
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers all bindings, replacing existing ones. Returns human-readable errors.</summary>
    public List<string> RegisterAll(params (string Gesture, Action Action)[] bindings)
    {
        UnregisterAll();
        var errors = new List<string>();

        foreach (var (gestureText, action) in bindings)
        {
            if (!HotkeyGesture.TryParse(gestureText, out var gesture, out var error))
            {
                errors.Add($"{gestureText}：{error}");
                continue;
            }

            if (gesture.IsMouse)
            {
                _mouseBindings.Add((gesture, () => Application.Current?.Dispatcher.BeginInvoke(action)));
                continue;
            }

            var id = _nextId++;
            var modifiers = gesture.Modifiers | Native.MOD_NOREPEAT;
            if (!Native.RegisterHotKey(_source.Handle, id, modifiers, gesture.VirtualKey))
            {
                errors.Add($"{gesture.Text}：已被其他程序占用");
                continue;
            }

            _handlers[id] = () =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    dispatcher.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            };
        }

        if (_mouseBindings.Count > 0)
        {
            _mouseProc = MouseHook;
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
            {
                errors.Add("鼠标按键快捷键：无法安装鼠标钩子");
                _mouseBindings.Clear();
            }
        }

        return errors;
    }

    private const int WhMouseLl = 14;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            if (message is WmMButtonDown or WmMButtonUp or WmXButtonDown or WmXButtonUp)
            {
                var data = Marshal.PtrToStructure<MouseHookData>(lParam);
                var vk = message is WmMButtonDown or WmMButtonUp
                    ? HotkeyGesture.VkMiddle
                    : (data.MouseData >> 16) == 1 ? HotkeyGesture.VkX1 : HotkeyGesture.VkX2;
                if (message is WmMButtonUp or WmXButtonUp)
                {
                    if (_swallowUp.Remove(vk))
                    {
                        return new IntPtr(1);
                    }
                }
                else
                {
                    var modifiers = CurrentModifiers();
                    foreach (var (gesture, action) in _mouseBindings)
                    {
                        if (gesture.VirtualKey == vk && gesture.Modifiers == modifiers)
                        {
                            // Take the click (and its release) so the window below does not see it.
                            _swallowUp.Add(vk);
                            action();
                            return new IntPtr(1);
                        }
                    }
                }
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private static uint CurrentModifiers()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        uint modifiers = 0;
        if (Down(0x11)) modifiers |= Native.MOD_CONTROL;
        if (Down(0x12)) modifiers |= Native.MOD_ALT;
        if (Down(0x10)) modifiers |= Native.MOD_SHIFT;
        if (Down(0x5B) || Down(0x5C)) modifiers |= Native.MOD_WIN;
        return modifiers;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _handlers.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }

        return IntPtr.Zero;
    }

    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys)
        {
            Native.UnregisterHotKey(_source.Handle, id);
        }

        _handlers.Clear();
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _mouseBindings.Clear();
        _swallowUp.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
