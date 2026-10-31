using System.Windows.Interop;

namespace Markshot.Core;

public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey, string Text)
{
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

            if (key != Key.None)
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

        return errors;
    }

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
