using System.Runtime.InteropServices;
using System.Windows.Interop;
using Markshot.Core.Recording;

namespace Markshot.UI;

/// <summary>The colour of the long screenshot's state line.</summary>
public enum ScrollState
{
    Idle,
    Good,
    Warn,
    Bad
}

/// <summary>What the user chose at the end of a long screenshot.</summary>
public enum ScrollCaptureAction
{
    Edit,
    Copy,
    Save,
    Cancel
}

/// <summary>
/// 长截图: the chosen area is grabbed ten times a second while the user scrolls its content
/// (down, or sideways); <see cref="ScrollStitcher"/> appends what scrolled into view (sideways:
/// the frames are transposed, stitched the same way and turned back). A frame marks the area (it
/// lets every click and wheel through), a toolbar under it switches 竖向 / 横向 (Tab) and ends with
/// 编辑 / 复制 / 保存 / 取消, a small window beside it previews the growing image.
/// </summary>
public sealed class ScrollCaptureSession
{
    private readonly Int32Rect _rect;
    private readonly DispatcherTimer _timer;
    private readonly ScrollFrameWindow _frame;
    private readonly ScrollPreviewWindow _preview;
    private readonly ScrollBarWindow _bar;
    private readonly Action<ScrollCaptureAction, BitmapSource?>? _done;
    private ScrollStitcher _stitcher;
    private int _lastPreviewLength = -1;
    private long _lastPreviewAt;
    private bool _ended;

    public ScrollCaptureSession(Int32Rect rect, bool horizontal, Action<ScrollCaptureAction, BitmapSource?>? done)
    {
        _rect = rect;
        _done = done;
        Horizontal = horizontal;
        _stitcher = NewStitcher();
        _frame = new ScrollFrameWindow(rect);
        _preview = new ScrollPreviewWindow(rect);
        _bar = new ScrollBarWindow(this, rect);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Grab();
    }

    /// <summary>For tests: frames from here instead of the screen.</summary>
    public Func<byte[]>? Source { get; init; }

    public bool Horizontal { get; private set; }

    /// <summary>Length of the stitched image along the scroll direction.</summary>
    public int Length => _stitcher.Height;

    public bool Ended => _ended;

    /// <summary>Raised when the direction changes (remembered by the caller).</summary>
    public event Action<bool>? DirectionChanged;

    private ScrollStitcher NewStitcher() => Horizontal
        ? new ScrollStitcher(_rect.Height, _rect.Width)
        : new ScrollStitcher(_rect.Width, _rect.Height);

    public void Start()
    {
        _frame.Show();
        _preview.Show();
        _bar.Show();
        _bar.Activate(); // Tab / Enter / Esc
        Grab();
        _timer.Start();
    }

    /// <summary>竖向 ↔ 横向: starts over in the other direction.</summary>
    public void SetHorizontal(bool horizontal)
    {
        if (horizontal == Horizontal || _ended)
        {
            return;
        }

        Horizontal = horizontal;
        _stitcher = NewStitcher();
        _lastPreviewLength = -1;
        _bar.ShowDirection(horizontal);
        DirectionChanged?.Invoke(horizontal);
        Grab();
    }

    private void Grab()
    {
        if (_ended)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        int added;
        try
        {
            var frame = Source?.Invoke() ?? GrabScreen();
            added = _stitcher.Add(Horizontal ? Transpose(frame, _rect.Width, _rect.Height) : frame);
        }
        catch (Exception ex)
        {
            Log.Error("长截图抓屏失败", ex);
            _preview.ShowState(ScrollState.Bad, "抓屏失败：" + ex.Message, "");
            return;
        }

        ReportState(added, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        // The preview is rebuilt at most ~3 times a second (a long image is large).
        if (_stitcher.Height != _lastPreviewLength && Environment.TickCount64 - _lastPreviewAt > 300)
        {
            _lastPreviewLength = _stitcher.Height;
            _lastPreviewAt = Environment.TickCount64;
            try
            {
                _preview.Update(Result(), _stitcher.Full, Horizontal);
            }
            catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or InvalidOperationException or OverflowException)
            {
                Log.Error("长截图预览失败", ex);
                _lastPreviewAt = Environment.TickCount64 + 5000; // not again on every tick
            }
        }
    }

    private ScrollStitcher.Outcome _shownOutcome = ScrollStitcher.Outcome.First;

    /// <summary>What just happened, in the preview window: placed, waiting, or why it cannot go on.</summary>
    private void ReportState(int added, double milliseconds)
    {
        var outcome = _stitcher.LastOutcome;
        // Stopped after losing the place: that is still the state until a frame fits again.
        if (outcome != ScrollStitcher.Outcome.Same || _shownOutcome is not (ScrollStitcher.Outcome.TooFar or ScrollStitcher.Outcome.Animating or ScrollStitcher.Outcome.Gap))
        {
            _shownOutcome = outcome;
        }

        var step = Math.Abs(_stitcher.LastShift);
        var view = Math.Max(1, _stitcher.ViewHeight);
        var fast = step > view * 0.7;
        var direction = Horizontal ? "向右" : "向下";
        var (state, text) = _shownOutcome switch
        {
            ScrollStitcher.Outcome.First => (ScrollState.Idle, $"等待滚动：在框内{direction}滚动"),
            ScrollStitcher.Outcome.Same => (ScrollState.Idle, "等待滚动（画面没有变化）"),
            ScrollStitcher.Outcome.Placed when added == 0 => (ScrollState.Good, "正在回滚（这段已拼接过）"),
            ScrollStitcher.Outcome.Placed when fast => (ScrollState.Warn, $"滚动较快 +{added} 行：再快就会跟丢"),
            ScrollStitcher.Outcome.Placed => (ScrollState.Good, $"正在拼接 +{added} 行"),
            ScrollStitcher.Outcome.TooFar => (ScrollState.Bad, "跟丢了：滚动太快，两帧之间没有重叠。往回滚一点，再慢一些"),
            ScrollStitcher.Outcome.Animating => (ScrollState.Bad, "无法拼接：画面只有局部在变，内容没有整体移动（动画、视频？或这里滚不动）"),
            ScrollStitcher.Outcome.Gap => (ScrollState.Bad, "跳得太远，中间会缺一段：滚回去一点"),
            _ => (ScrollState.Warn, "已到最大长度")
        };
        _preview.ShowState(state, text,
            $"长度 {_stitcher.Height} · 本帧移动 {step}/{view} 行\n成功 {_stitcher.PlacedFrames} 帧 · 跟丢 {_stitcher.LostFrames} 帧 · 处理 {milliseconds:0} ms");
    }

    /// <summary>The state line shown now (tests).</summary>
    internal string StateText => _preview.StateText;

    /// <summary>The stitched image the right way round.</summary>
    public BitmapSource Result()
    {
        var image = _stitcher.Result();
        if (!Horizontal)
        {
            return image;
        }

        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        var turned = Transpose(pixels, width, height);
        var bitmap = BitmapSource.Create(height, width, 96, 96, PixelFormats.Bgr32, null, turned, height * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Rows become columns: a w × h BGRA buffer turns into h × w.</summary>
    public static byte[] Transpose(byte[] pixels, int width, int height)
    {
        var result = new byte[pixels.Length];
        var source = MemoryMarshal.Cast<byte, uint>(pixels);
        var target = MemoryMarshal.Cast<byte, uint>(result);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                target[x * height + y] = source[y * width + x];
            }
        }

        return result;
    }

    private byte[] GrabScreen()
    {
        using var bitmap = new System.Drawing.Bitmap(_rect.Width, _rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(_rect.X, _rect.Y, 0, 0, new System.Drawing.Size(_rect.Width, _rect.Height));
        }

        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, _rect.Width, _rect.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            var pixels = new byte[_rect.Width * 4 * _rect.Height];
            for (var y = 0; y < _rect.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * _rect.Width * 4, _rect.Width * 4);
            }

            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>Ends with 编辑 / 复制 / 保存 (with the image) or 取消.</summary>
    public void Finish(ScrollCaptureAction action)
    {
        if (_ended)
        {
            return;
        }

        BitmapSource? image = null;
        if (action != ScrollCaptureAction.Cancel)
        {
            Grab();
            image = Result();
        }

        _ended = true;
        _timer.Stop();
        _frame.Close();
        _preview.Close();
        _bar.CloseNow();
        _done?.Invoke(action, image);
    }

    // ------------------------------------------------------------------ placement helpers

    private static int Px(Int32Rect rect, double dip) => (int)Math.Round(dip * Monitors.Of(rect).Scale);

    /// <summary>A window placed (physical pixels) before it first shows, so it never jumps there.</summary>
    private static void PlaceBeforeShow(Window window, Func<Native.RECT, (int X, int Y)> where)
    {
        window.Left = -32000;
        window.Top = -32000;
        window.SourceInitialized += (_, _) => Native.SetWindowPos(new WindowInteropHelper(window).Handle, Native.HWND_TOPMOST,
            -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        window.ContentRendered += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            Native.GetWindowRect(handle, out var me);
            var (x, y) = where(me);
            Native.SetWindowPos(handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        };
    }

    /// <summary>The marked area: a line just outside it, every click / wheel goes through.</summary>
    private sealed class ScrollFrameWindow : Window
    {
        private const double Line = 3;

        public ScrollFrameWindow(Int32Rect rect)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Content = new Border
            {
                BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                BorderThickness = new Thickness(Line),
                IsHitTestVisible = false
            };
            SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                var style = GetWindowLong(handle, Native.GWL_EXSTYLE);
                SetWindowLong(handle, Native.GWL_EXSTYLE, style | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | 0x08000000);
                var pad = (int)Math.Ceiling(Line * Monitors.Of(rect).Scale);
                Native.SetWindowPos(handle, Native.HWND_TOPMOST, rect.X - pad, rect.Y - pad, rect.Width + pad * 2, rect.Height + pad * 2,
                    Native.SWP_NOACTIVATE);
            };
        }
    }

    /// <summary>The growing image and a hint, beside the area (kept out of the grab when it has to overlap it).</summary>
    private sealed class ScrollPreviewWindow : Window
    {
        private readonly Image _image = new() { Stretch = Stretch.Uniform };
        private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _state = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBlock _diagnostics = new() { FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

        public string StateText => _state.Text;

        /// <summary>The live state: what the stitcher did with the last frame, and its numbers.</summary>
        public void ShowState(ScrollState state, string text, string diagnostics)
        {
            _state.Text = text;
            _diagnostics.Text = diagnostics;
            _state.Foreground = new SolidColorBrush(state switch
            {
                ScrollState.Good => Color.FromRgb(0x3F, 0xC4, 0x7A),
                ScrollState.Warn => Color.FromRgb(0xF5, 0xA6, 0x23),
                ScrollState.Bad => Color.FromRgb(0xFF, 0x5A, 0x5A),
                _ => Theme.Color(0xFFC8C8C8)
            });
        }

        public ScrollPreviewWindow(Int32Rect rect)
        {
            Title = "Markshot 长截图预览";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Width = 220;
            Height = 400;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            _diagnostics.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF8A8A8A));
            _status.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFC8C8C8));
            _status.Text = "在框内滚动鼠标滚轮（或拖滚动条），新内容会自动拼接。";
            var host = new Border { Child = _image, CornerRadius = new CornerRadius(4), ClipToBounds = true };
            host.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF151515));
            var root = new DockPanel { Margin = new Thickness(10) };
            var texts = new StackPanel { Children = { _state, _diagnostics, _status } };
            DockPanel.SetDock(texts, Dock.Bottom);
            root.Children.Add(texts);
            root.Children.Add(host);
            var frame = new Border { Child = root, BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.FindResource("PanelBorder") };
            frame.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF1C1C1C));
            Content = frame;
            UiScale.AttachWindow(this);
            SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                var style = GetWindowLong(handle, Native.GWL_EXSTYLE);
                SetWindowLong(handle, Native.GWL_EXSTYLE, style | Native.WS_EX_TOOLWINDOW | 0x08000000 /* never activated */);
            };
            PlaceBeforeShow(this, me =>
            {
                // Right of the area, else left, else over its top-right corner (then out of the grab).
                var work = Monitors.Of(rect).WorkArea;
                var gap = Px(rect, 10);
                int x;
                if (rect.X + rect.Width + gap + me.Width <= work.X + work.Width)
                {
                    x = rect.X + rect.Width + gap;
                }
                else if (rect.X - gap - me.Width >= work.X)
                {
                    x = rect.X - gap - me.Width;
                }
                else
                {
                    x = work.X + work.Width - me.Width - gap;
                    RecordNative.ExcludeFromCapture(new WindowInteropHelper(this).Handle, true);
                }

                return (x, Math.Clamp(rect.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - me.Height)));
            });
        }

        public void Update(BitmapSource image, bool full, bool horizontal)
        {
            _image.Source = image;
            var length = horizontal ? image.PixelWidth : image.PixelHeight;
            _status.Text = full
                ? $"已到最大长度（{length} 像素）。"
                : $"已拼接 {image.PixelWidth} × {image.PixelHeight}。Tab 切换横竖。";
        }
    }

    /// <summary>Under the area: 竖向 / 横向 on the left; 编辑 / 复制 / 保存 / 取消 on the right.</summary>
    private sealed class ScrollBarWindow : Window
    {
        private const int TabHotkey = 0xA201;
        private const int EscHotkey = 0xA202;
        private readonly ScrollCaptureSession _session;
        private readonly RadioButton _vertical;
        private readonly RadioButton _horizontal;
        private bool _closing;

        public ScrollBarWindow(ScrollCaptureSession session, Int32Rect rect)
        {
            _session = session;
            Title = "Markshot 长截图";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true; // rounded like the annotation bar (it stays outside the area, never in the shots)
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;

            RadioButton Direction(string style, string tip, bool horizontal)
            {
                var button = new RadioButton
                {
                    Style = (Style)Application.Current.FindResource("IconToolRadio"),
                    GroupName = "ScrollDirection" + GetHashCode(),
                    ToolTip = tip,
                    Content = new Border { Style = (Style)Application.Current.FindResource(style) }
                };
                button.Checked += (_, _) => _session.SetHorizontal(horizontal);
                return button;
            }

            Button Action(string style, string tip, ScrollCaptureAction action)
            {
                var button = new Button
                {
                    Style = (Style)Application.Current.FindResource("IconToolButton"),
                    ToolTip = tip,
                    Content = new Border { Style = (Style)Application.Current.FindResource(style) }
                };
                button.Click += (_, _) => _session.Finish(action);
                return button;
            }

            _vertical = Direction("IconScrollMask", "竖向长截图（Tab 切换）", false);
            _horizontal = Direction("IconScrollHMask", "横向长截图（Tab 切换）", true);
            ShowDirection(session.Horizontal);
            var separator = new Border { Width = 1, Height = 18, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            separator.SetResourceReference(Border.BackgroundProperty, Theme.Key(0x33FFFFFF));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 3, 5, 3) };
            row.Children.Add(_vertical);
            row.Children.Add(_horizontal);
            row.Children.Add(separator);
            row.Children.Add(Action("IconEditMask", "编辑：在图片编辑器里打开（Enter）", ScrollCaptureAction.Edit));
            row.Children.Add(Action("IconCopyMask", "复制", ScrollCaptureAction.Copy));
            row.Children.Add(Action("IconSaveMask", "保存", ScrollCaptureAction.Save));
            row.Children.Add(Action("IconCloseMask", "取消，回到截图（Esc）", ScrollCaptureAction.Cancel));
            // The annotation bar exactly: its plate (colour, corners, padding) and the narrow inner shade along the edge.
            row.Margin = new Thickness(0);
            var plate = new Border { Child = row, CornerRadius = new CornerRadius(9), Padding = new Thickness(5, 3, 5, 3) };
            plate.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xF21E1E1E));
            var shade = new Border
            {
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(3),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0, 0, 0)),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 4, KernelType = System.Windows.Media.Effects.KernelType.Gaussian }
            };
            var shadeClip = new Grid { IsHitTestVisible = false, Children = { shade } };
            shadeClip.SizeChanged += (_, _) => shadeClip.Clip = new RectangleGeometry(new Rect(0, 0, shadeClip.ActualWidth, shadeClip.ActualHeight), 9, 9);
            var bar = new Grid { Children = { plate, shadeClip } };
            UiScale.Attach(bar);
            Content = bar;
            SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                var style = GetWindowLong(handle, Native.GWL_EXSTYLE);
                SetWindowLong(handle, Native.GWL_EXSTYLE, style | Native.WS_EX_TOOLWINDOW);
                // Tab / Esc work while the page being scrolled has the focus (only during this capture).
                Native.RegisterHotKey(handle, TabHotkey, 0, 0x09);
                Native.RegisterHotKey(handle, EscHotkey, 0, 0x1B);
                HwndSource.FromHwnd(handle)?.AddHook((IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool handled) =>
                {
                    if (message == 0x0312 /* WM_HOTKEY */)
                    {
                        if (wParam.ToInt32() == TabHotkey)
                        {
                            _session.SetHorizontal(!_session.Horizontal);
                        }
                        else if (wParam.ToInt32() == EscHotkey)
                        {
                            _session.Finish(ScrollCaptureAction.Cancel);
                        }

                        handled = true;
                    }

                    return IntPtr.Zero;
                });
            };
            Closed += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                Native.UnregisterHotKey(handle, TabHotkey);
                Native.UnregisterHotKey(handle, EscHotkey);
            };
            PlaceBeforeShow(this, me =>
            {
                // Under the area, else above it, else inside along its bottom; centred, on the work area.
                var work = Monitors.Of(rect).WorkArea;
                // Clear of the area and its frame line: under it, above it, else beside it (never inside: it would be in the shots).
                var gap = Px(rect, 10);
                var x = Math.Clamp(rect.X + rect.Width / 2 - me.Width / 2, work.X + 4, Math.Max(work.X + 4, work.X + work.Width - me.Width - 4));
                var y = rect.Y + rect.Height + gap;
                if (y + me.Height <= work.Y + work.Height)
                {
                    return (x, y);
                }

                if (rect.Y - gap - me.Height >= work.Y)
                {
                    return (x, rect.Y - gap - me.Height);
                }

                var bottom = Math.Min(rect.Y + rect.Height, work.Y + work.Height) - me.Height;
                if (rect.X + rect.Width + gap + me.Width <= work.X + work.Width)
                {
                    return (rect.X + rect.Width + gap, bottom);
                }

                if (rect.X - gap - me.Width >= work.X)
                {
                    return (rect.X - gap - me.Width, bottom);
                }

                // No room anywhere (the area covers the screen): along its bottom, kept out of the shots if Windows can.
                RecordNative.ExcludeFromCapture(new WindowInteropHelper(this).Handle, true);
                return (x, rect.Y + rect.Height - me.Height - gap);
            });
            PreviewKeyDown += (_, e) =>
            {
                switch (e.Key)
                {
                    case Key.Tab:
                        _session.SetHorizontal(!_session.Horizontal);
                        e.Handled = true;
                        break;
                    case Key.Escape:
                        _session.Finish(ScrollCaptureAction.Cancel);
                        e.Handled = true;
                        break;
                    case Key.Enter:
                        _session.Finish(ScrollCaptureAction.Edit);
                        e.Handled = true;
                        break;
                    case Key.C when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                        _session.Finish(ScrollCaptureAction.Copy);
                        e.Handled = true;
                        break;
                    case Key.S when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                        _session.Finish(ScrollCaptureAction.Save);
                        e.Handled = true;
                        break;
                }
            };
            Closing += (_, e) =>
            {
                if (!_closing && !_session.Ended)
                {
                    e.Cancel = true;
                    _session.Finish(ScrollCaptureAction.Cancel);
                }
            };
        }

        public void ShowDirection(bool horizontal)
        {
            _vertical.IsChecked = !horizontal;
            _horizontal.IsChecked = horizontal;
        }

        public void CloseNow()
        {
            _closing = true;
            Close();
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
