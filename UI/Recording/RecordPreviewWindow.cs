// 录屏 — from SketchPlayer: ported from the screen recorder of the author's SketchPlayer project
// (see SketchPlayer docs/技术文档_录屏.md), rebuilt for WPF / .NET.
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Markshot.UI.Recording;

/// <summary>
/// After stopping, the area turns into the recording: plays in place (looping between the trim
/// points, at the chosen speed). A click plays / pauses; holding (300 ms) or moving the mouse
/// moves the whole preview with its toolbar.
/// </summary>
internal sealed class RecordPreviewWindow : Window
{
    private readonly RecorderController _controller;
    private readonly MediaElement _media;
    private readonly DispatcherTimer _loop;
    private readonly DispatcherTimer _hold;
    private Native.POINT _pressPoint;
    private Int32Rect _pressRect;
    private bool _pressed;
    private bool _moving;
    private bool _closing;

    public RecordPreviewWindow(RecorderController controller)
    {
        _controller = controller;
        Title = "Markshot 录屏预览";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Black;
        _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            Stretch = Stretch.Uniform,
            Source = new Uri(controller.Take!.File)
        };
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = _media,
            Cursor = Cursors.Hand,
            ToolTip = "单击播放 / 暂停，按住拖动可移动"
        };
        In = 0;
        Out = controller.Take.Duration;
        _media.MediaOpened += (_, _) =>
        {
            // The length stays ffmpeg's: Windows' player sometimes reports a rounded / shorter one
            // (taking it trimmed the end of the take).
            Seek(0);
            PlaybackChanged?.Invoke();
        };
        _media.MediaFailed += (_, e) =>
        {
            Log.Error("录屏预览无法播放", e.ErrorException);
            PlaybackError = "这台电脑无法播放预览（缺少系统媒体组件），仍可以直接保存。";
            PlaybackChanged?.Invoke();
        };
        _loop = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _loop.Tick += (_, _) =>
        {
            var t = _media.Position.TotalSeconds;
            if (IsPlaying && (t >= Out - 0.02 || t < In - 0.05))
            {
                _media.Position = TimeSpan.FromSeconds(In); // loop the kept part
            }

            PlaybackChanged?.Invoke();
        };
        _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            if (_pressed)
            {
                _moving = true;
            }
        };
        PreviewMouseLeftButtonDown += OnDown;
        PreviewMouseMove += OnMove;
        PreviewMouseLeftButtonUp += OnUp;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, Native.GWL_EXSTYLE);
            SetWindowLong(handle, Native.GWL_EXSTYLE, style | Native.WS_EX_TOOLWINDOW);
            Place(); // over the area before it first shows
        };
        Loaded += (_, _) =>
        {
            _media.Play();
            _media.Pause();
            _loop.Start();
        };
        Closing += (_, e) =>
        {
            if (!_closing)
            {
                e.Cancel = true; // Alt+F4 goes through the recorder (asks when unsaved)
                _controller.RequestClose();
            }
        };
        Closed += (_, _) =>
        {
            _loop.Stop();
            _media.Close();
        };
    }

    public event Action? PlaybackChanged;

    public bool IsPlaying { get; private set; }

    public string? PlaybackError { get; private set; }

    public double In { get; set; }

    public double Out { get; set; }

    public double Position => _media.Position.TotalSeconds;

    public double Speed
    {
        get => _media.SpeedRatio;
        set => _media.SpeedRatio = value;
    }

    public bool Muted
    {
        get => _media.IsMuted;
        set => _media.IsMuted = value;
    }

    public WindowMove? Target()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var r = _controller.Rect;
        return handle == IntPtr.Zero || !IsVisible ? null : new WindowMove(handle, r.X, r.Y, r.Width, r.Height, true);
    }

    public void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var r = _controller.Rect;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, Native.SWP_NOACTIVATE);
    }

    public void TogglePlay()
    {
        if (IsPlaying)
        {
            _media.Pause();
            IsPlaying = false;
        }
        else
        {
            if (Position >= Out - 0.05 || Position < In)
            {
                _media.Position = TimeSpan.FromSeconds(In);
            }

            _media.Play();
            IsPlaying = true;
        }

        PlaybackChanged?.Invoke();
    }

    public void Seek(double seconds)
    {
        _media.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        PlaybackChanged?.Invoke();
    }

    public void SetHidden(bool hidden)
    {
        if (hidden)
        {
            Hide();
        }
        else
        {
            Show();
            Place();
        }
    }

    public void CloseNow()
    {
        _closing = true;
        Close();
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _moving = false;
        _pressPoint = Native.CursorPosition();
        _pressRect = _controller.Rect;
        _hold.Start();
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        var c = Native.CursorPosition();
        int dx = c.X - _pressPoint.X, dy = c.Y - _pressPoint.Y;
        if (!_moving && Math.Sqrt(dx * dx + dy * dy) > 5)
        {
            _hold.Stop();
            _moving = true;
        }

        if (_moving)
        {
            _controller.MoveTo(_pressRect, dx, dy);
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        _hold.Stop();
        ReleaseMouseCapture();
        if (!_moving)
        {
            TogglePlay();
        }

        _moving = false;
        e.Handled = true;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
