using System.Runtime.InteropServices;
using System.Windows.Interop;
using Markshot.Core.Recording;

namespace Markshot.UI;

/// <summary>
/// 长截图: the chosen area is grabbed ten times a second while the user scrolls its content
/// (mouse wheel / scroll bar); <see cref="ScrollStitcher"/> appends what scrolled into view. A
/// frame marks the area (it lets every click and wheel through), a small panel beside it shows
/// the growing image with 完成 / 取消. 完成 copies the long image and opens it as a pin.
/// </summary>
public sealed class ScrollCaptureSession
{
    private readonly Int32Rect _rect;
    private readonly ScrollStitcher _stitcher;
    private readonly DispatcherTimer _timer;
    private readonly ScrollFrameWindow _frame;
    private readonly ScrollPanelWindow _panel;
    private readonly Action<BitmapSource>? _done;
    private int _lastPreviewHeight = -1;
    private long _lastPreviewAt;
    private bool _ended;

    public ScrollCaptureSession(Int32Rect rect, Action<BitmapSource>? done)
    {
        _rect = rect;
        _done = done;
        _stitcher = new ScrollStitcher(rect.Width, rect.Height);
        _frame = new ScrollFrameWindow(rect);
        _panel = new ScrollPanelWindow(this);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Grab();
    }

    /// <summary>For tests: frames from here instead of the screen.</summary>
    public Func<byte[]>? Source { get; init; }

    public int Height => _stitcher.Height;

    public bool Ended => _ended;

    public void Start()
    {
        _frame.Show();
        _panel.Show();
        _panel.PlaceBeside(_rect);
        Grab();
        _timer.Start();
    }

    private void Grab()
    {
        if (_ended)
        {
            return;
        }

        try
        {
            _stitcher.Add(Source?.Invoke() ?? GrabScreen());
        }
        catch (Exception ex)
        {
            Log.Error("长截图抓屏失败", ex);
            return;
        }

        // The preview is rebuilt at most ~3 times a second (a long image is large).
        if (_stitcher.Height != _lastPreviewHeight && Environment.TickCount64 - _lastPreviewAt > 300)
        {
            _lastPreviewHeight = _stitcher.Height;
            _lastPreviewAt = Environment.TickCount64;
            _panel.Show(_stitcher.Height, _stitcher.Full, _stitcher.Result());
        }
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

    public void Finish()
    {
        if (_ended)
        {
            return;
        }

        Grab();
        var image = _stitcher.Result();
        End();
        _done?.Invoke(image);
    }

    public void Cancel() => End();

    private void End()
    {
        _ended = true;
        _timer.Stop();
        _frame.Close();
        _panel.Close();
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
            var border = new Border
            {
                BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                BorderThickness = new Thickness(Line),
                IsHitTestVisible = false
            };
            Content = border;
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

    /// <summary>Preview + 完成 / 取消, beside the area (kept out of the grab when it has to overlap it).</summary>
    private sealed class ScrollPanelWindow : Window
    {
        private readonly ScrollCaptureSession _session;
        private readonly Image _preview = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Bottom };
        private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };

        public ScrollPanelWindow(ScrollCaptureSession session)
        {
            _session = session;
            Title = "Markshot 长截图";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Width = 230;
            Height = 380;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            SetResourceReference(BackgroundProperty, Theme.Key(0xFF1C1C1C));
            _status.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFC8C8C8));
            _status.Text = "在框内滚动鼠标滚轮（或拖滚动条）往下翻，新内容会自动拼接。";
            var title = new TextBlock { Text = "长截图", FontSize = 14, FontWeight = FontWeights.SemiBold };
            title.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFF0F0F0));
            var previewHost = new Border { Child = _preview, CornerRadius = new CornerRadius(4), ClipToBounds = true };
            previewHost.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF151515));
            var done = new Button { Content = "完成", Style = (Style)Application.Current.FindResource("PrimaryButton"), MinWidth = 0, Width = 90 };
            var cancel = new Button { Content = "取消", Style = (Style)Application.Current.FindResource("FooterButton"), MinWidth = 0, Width = 90, Margin = new Thickness(8, 0, 0, 0) };
            done.Click += (_, _) => _session.Finish();
            cancel.Click += (_, _) => _session.Cancel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(done);
            buttons.Children.Add(cancel);
            var root = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(title, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(title);
            root.Children.Add(buttons);
            root.Children.Add(_status);
            root.Children.Add(previewHost);
            var frame = new Border
            {
                Child = root,
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.FindResource("PanelBorder")
            };
            frame.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF1C1C1C));
            Content = frame;
            UiScale.AttachWindow(this);
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    _session.Cancel();
                }
                else if (e.Key == Key.Enter)
                {
                    _session.Finish();
                }
            };
            Closing += (_, _) =>
            {
                if (!_session.Ended)
                {
                    _session.Cancel();
                }
            };
        }

        public void Show(int height, bool full, BitmapSource image)
        {
            _preview.Source = image;
            _status.Text = full
                ? $"已到最大长度（{height} 像素），点「完成」。"
                : $"已拼接 {height} 像素。继续往下滚动，或点「完成」（Enter）；Esc 取消。";
        }

        /// <summary>To the right of the area, else to the left, else over its corner (then left out of the grab).</summary>
        public void PlaceBeside(Int32Rect rect)
        {
            var handle = new WindowInteropHelper(this).Handle;
            Native.GetWindowRect(handle, out var me);
            var work = Monitors.Of(rect).WorkArea;
            var gap = (int)(10 * Monitors.Of(rect).Scale);
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
                RecordNative.ExcludeFromCapture(handle, true);
            }

            var y = Math.Clamp(rect.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - me.Height));
            Native.SetWindowPos(handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
