// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
using System.Runtime.InteropServices;

namespace Markshot.Core.Recording;

/// <summary>
/// The recent key / mouse presses shown as pills in the bottom-left of the recording (操作录制):
/// "Ctrl + C", "左键", "滚轮 ↓"…; the same combo pressed again within 1.2 s becomes "×n";
/// the last 5 stay, each holds 1.5 s and then fades out in 0.4 s.
/// </summary>
public sealed class KeyPills
{
    public const int HoldMs = 1500;
    public const int FadeMs = 400;
    public const double Margin = 18;
    public const double PillHeight = 34;
    public const double FontSize = 16;
    public const double Padding = 13;
    public const double Gap = 8;
    public const double Radius = 8;

    private readonly object _gate = new();
    private readonly List<Pill> _pills = new();
    private readonly Func<long> _now;

    public KeyPills(Func<long>? now = null)
    {
        _now = now ?? (() => Environment.TickCount64);
    }

    public sealed record Pill(string Text, int Count, long Time)
    {
        public string Label => Count > 1 ? $"{Text}  ×{Count}" : Text;
    }

    public event Action? Changed;

    public void Emit(string text)
    {
        lock (_gate)
        {
            var now = _now();
            if (_pills.Count > 0 && _pills[^1].Text == text && now - _pills[^1].Time < 1200)
            {
                _pills[^1] = _pills[^1] with { Count = _pills[^1].Count + 1, Time = now };
            }
            else
            {
                _pills.Add(new Pill(text, 1, now));
                while (_pills.Count > 5)
                {
                    _pills.RemoveAt(0);
                }
            }
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pills.Clear();
        }
    }

    /// <summary>Visible pills (oldest first) with their opacity.</summary>
    public List<(string Label, double Alpha)> Snapshot()
    {
        lock (_gate)
        {
            var now = _now();
            return _pills
                .Select(p => (p.Label, Alpha(now - p.Time)))
                .Where(p => p.Item2 > 0)
                .ToList();
        }
    }

    public static double Alpha(long age) => age < HoldMs ? 1 : Math.Max(0, 1 - (age - HoldMs) / (double)FadeMs);

    /// <summary>Draws the pills into a video frame; <paramref name="k"/> = frame pixels per DIP.</summary>
    public void Draw(System.Drawing.Graphics graphics, double k, int frameHeight)
    {
        var pills = Snapshot();
        if (pills.Count == 0)
        {
            return;
        }

        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new System.Drawing.Font("Microsoft YaHei UI", (float)(FontSize * k), System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        var y = frameHeight - Margin * k;
        var x = Margin * k;
        var h = PillHeight * k;
        for (var i = pills.Count - 1; i >= 0; i--)
        {
            var (label, alpha) = pills[i];
            var textSize = graphics.MeasureString(label, font, System.Drawing.PointF.Empty, System.Drawing.StringFormat.GenericTypographic);
            var w = textSize.Width + Padding * 2 * k;
            y -= h;
            using (var path = RoundedRect(x, y, w, h, Radius * k))
            using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb((int)(214 * alpha), 18, 18, 18)))
            using (var stroke = new System.Drawing.Pen(System.Drawing.Color.FromArgb((int)(56 * alpha), 255, 255, 255), (float)Math.Max(1, k)))
            {
                graphics.FillPath(fill, path);
                graphics.DrawPath(stroke, path);
            }

            using (var text = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb((int)(255 * alpha), 255, 255, 255)))
            {
                graphics.DrawString(label, font, text, (float)(x + Padding * k), (float)(y + (h - textSize.Height) / 2),
                    System.Drawing.StringFormat.GenericTypographic);
            }

            y -= Gap * k;
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(double x, double y, double w, double h, double r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = (float)(r * 2);
        path.AddArc((float)x, (float)y, d, d, 180, 90);
        path.AddArc((float)(x + w) - d, (float)y, d, d, 270, 90);
        path.AddArc((float)(x + w) - d, (float)(y + h) - d, d, d, 0, 90);
        path.AddArc((float)x, (float)(y + h) - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// Global keyboard / mouse hooks for 操作录制, on their own thread (a busy UI thread must never
/// make the system mouse lag). Only runs while the key display is on.
/// </summary>
public sealed class InputHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WM_QUIT = 0x0012;

    private readonly KeyPills _pills;
    private readonly HashSet<int> _held = new();
    private HashSet<string>? _pureModifiers;
    private Thread? _thread;
    private uint _threadId;
    private HookProc? _keyboardProc;
    private HookProc? _mouseProc;

    public InputHook(KeyPills pills)
    {
        _pills = pills;
    }

    public bool Running => _thread != null;

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
            _keyboardProc = KeyboardHook;
            _mouseProc = MouseHook;
            var module = GetModuleHandle(null);
            var keyboard = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
            var mouse = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            UnhookWindowsHookEx(keyboard);
            UnhookWindowsHookEx(mouse);
        })
        { IsBackground = true, Name = "Markshot input display" };
        _thread.Start();
        ready.Wait(2000);
    }

    public void Stop()
    {
        if (_thread == null)
        {
            return;
        }

        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
        _held.Clear();
        _pureModifiers = null;
    }

    public void Dispose() => Stop();

    private static string? ModifierName(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => "Shift",
        0x11 or 0xA2 or 0xA3 => "Ctrl",
        0x12 or 0xA4 or 0xA5 => "Alt",
        0x5B or 0x5C => "Win",
        _ => null
    };

    private static readonly string[] ModifierOrder = { "Ctrl", "Shift", "Alt", "Win" };

    private List<string> HeldModifiers() =>
        ModifierOrder.Where(m => _held.Any(vk => ModifierName(vk) == m)).ToList();

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            var message = wParam.ToInt32();
            if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                OnKeyDown(vk);
            }
            else if (message is WM_KEYUP or WM_SYSKEYUP)
            {
                OnKeyUp(vk);
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>A key goes down (internal for tests).</summary>
    internal void OnKeyDown(int vk)
    {
        if (ModifierName(vk) is { } modifier)
        {
            // Only modifiers so far: shown on release ("Ctrl + Shift").
            if (_pureModifiers != null)
            {
                _pureModifiers.Add(modifier);
            }
            else if (!_held.Any(k => ModifierName(k) == null))
            {
                _pureModifiers = new HashSet<string> { modifier };
            }

            _held.Add(vk);
            return;
        }

        if (!_held.Add(vk))
        {
            return; // auto-repeat
        }

        _pureModifiers = null;
        var name = KeyLabel(vk);
        if (name != null)
        {
            _pills.Emit(string.Join(" + ", HeldModifiers().Append(name)));
        }
    }

    internal void OnKeyUp(int vk)
    {
        _held.Remove(vk);
        if (ModifierName(vk) == null)
        {
            return;
        }

        if (HeldModifiers().Count == 0)
        {
            if (_pureModifiers is { Count: > 0 } pure)
            {
                _pills.Emit(string.Join(" + ", ModifierOrder.Where(pure.Contains)));
            }

            _pureModifiers = null;
        }
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var data = Marshal.ReadInt32(lParam, 8); // MSLLHOOKSTRUCT.mouseData
            string? name = message switch
            {
                WM_LBUTTONDOWN => "左键",
                WM_RBUTTONDOWN => "右键",
                WM_MBUTTONDOWN => "中键",
                WM_XBUTTONDOWN => "侧键",
                WM_MOUSEWHEEL => (short)(data >> 16) > 0 ? "滚轮 ↑" : "滚轮 ↓",
                WM_MOUSEHWHEEL => (short)(data >> 16) > 0 ? "滚轮 →" : "滚轮 ←",
                _ => null
            };
            if (name != null)
            {
                _pureModifiers = null;
                _pills.Emit(string.Join(" + ", HeldModifiers().Append(name)));
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>Display name of a key: Esc, Enter, ←, F5, A, 1, Num 1…</summary>
    internal static string? KeyLabel(int vk)
    {
        switch (vk)
        {
            case 0x08: return "Backspace";
            case 0x09: return "Tab";
            case 0x0D: return "Enter";
            case 0x13: return "Pause";
            case 0x14: return "CapsLock";
            case 0x1B: return "Esc";
            case 0x20: return "Space";
            case 0x21: return "PgUp";
            case 0x22: return "PgDn";
            case 0x23: return "End";
            case 0x24: return "Home";
            case 0x25: return "←";
            case 0x26: return "↑";
            case 0x27: return "→";
            case 0x28: return "↓";
            case 0x2C: return "PrtSc";
            case 0x2D: return "Insert";
            case 0x2E: return "Delete";
            case 0x5D: return "Menu";
            case 0x6A: return "Num *";
            case 0x6B: return "Num +";
            case 0x6D: return "Num -";
            case 0x6E: return "Num .";
            case 0x6F: return "Num /";
            case 0x90: return "NumLock";
            case 0x91: return "ScrollLock";
            case 0xBA: return ";";
            case 0xBB: return "=";
            case 0xBC: return ",";
            case 0xBD: return "-";
            case 0xBE: return ".";
            case 0xBF: return "/";
            case 0xC0: return "`";
            case 0xDB: return "[";
            case 0xDC: return "\\";
            case 0xDD: return "]";
            case 0xDE: return "'";
        }

        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)vk).ToString();
        }

        if (vk is >= 0x60 and <= 0x69)
        {
            return "Num " + (vk - 0x60);
        }

        if (vk is >= 0x70 and <= 0x87)
        {
            return "F" + (vk - 0x6F);
        }

        return null; // media keys etc.
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Native.POINT Point;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
