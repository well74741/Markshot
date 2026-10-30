using System.Windows.Interop;
using Microsoft.Win32;

namespace Markshot.UI;

public partial class PinWindow : Window
{
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;

    private int _left;
    private int _top;
    private double _zoom = 1.0;
    private double _opacity = 1.0;
    private bool _locked;

    private bool _dragging;
    private int _dragStartCursorX;
    private int _dragStartCursorY;
    private int _dragStartLeft;
    private int _dragStartTop;

    public BitmapSource SourceImage { get; }

    public PinWindow(BitmapSource image, int screenX, int screenY)
    {
        InitializeComponent();

        SourceImage = image;
        Img.Source = image;
        _pixelWidth = image.PixelWidth;
        _pixelHeight = image.PixelHeight;
        _left = screenX;
        _top = screenY;

        Loaded += OnLoaded;
        SourceInitialized += (_, _) => ApplyBounds();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(ApplyBounds));
        Activated += (_, _) => UpdateFrame();
        Deactivated += (_, _) => UpdateFrame();

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseWheel += OnMouseWheel;
        MouseDown += OnMouseDown;

        BuildContextMenu();
        UpdateFrame();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var dpi = handle != IntPtr.Zero ? Native.GetDpiForWindow(handle) / 96.0 : 1.0;

        Width = _pixelWidth / dpi;
        Height = _pixelHeight / dpi;
        Left = _left / dpi;
        Top = _top / dpi;

        ApplyBounds();
        Dispatcher.BeginInvoke(new Action(ApplyBounds), DispatcherPriority.Loaded);
    }

    private void ApplyBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var width = Math.Max(16, (int)Math.Round(_pixelWidth * _zoom));
        var height = Math.Max(16, (int)Math.Round(_pixelHeight * _zoom));

        var virtualX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var virtualY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var virtualW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var virtualH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        if (width <= virtualW)
        {
            _left = Math.Clamp(_left, virtualX, virtualX + virtualW - width);
        }

        if (height <= virtualH)
        {
            _top = Math.Clamp(_top, virtualY, virtualY + virtualH - height);
        }

        Native.SetWindowPos(
            handle,
            Topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
            _left,
            _top,
            width,
            height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private void UpdateFrame()
    {
        var color = _locked
            ? Color.FromRgb(0xF5, 0x9E, 0x0B)
            : IsActive
                ? Color.FromRgb(0x3B, 0x82, 0xF6)
                : Color.FromRgb(0x6B, 0x72, 0x80);

        FrameBorder.BorderBrush = new SolidColorBrush(color);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            Close();
            return;
        }

        var cursor = Native.CursorPosition();
        _dragStartCursorX = cursor.X;
        _dragStartCursorY = cursor.Y;
        _dragStartLeft = _left;
        _dragStartTop = _top;
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var cursor = Native.CursorPosition();
        _left = _dragStartLeft + (cursor.X - _dragStartCursorX);
        _top = _dragStartTop + (cursor.Y - _dragStartCursorY);
        ApplyBounds();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && !_locked)
        {
            ResetView();
            e.Handled = true;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_locked)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _opacity = Math.Clamp(_opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.2, 1.0);
            Opacity = _opacity;
        }
        else
        {
            ZoomBy(e.Delta > 0 ? 1.1 : 1 / 1.1);
        }

        e.Handled = true;
    }

    private void ZoomBy(double factor)
    {
        var newZoom = Math.Clamp(_zoom * factor, 0.1, 8.0);
        if (Math.Abs(newZoom - _zoom) < 0.0001)
        {
            return;
        }

        var oldWidth = _pixelWidth * _zoom;
        var oldHeight = _pixelHeight * _zoom;
        var centerX = _left + oldWidth / 2;
        var centerY = _top + oldHeight / 2;

        _zoom = newZoom;
        _left = (int)Math.Round(centerX - _pixelWidth * _zoom / 2);
        _top = (int)Math.Round(centerY - _pixelHeight * _zoom / 2);
        ApplyBounds();
    }

    private void ResetView()
    {
        _zoom = 1.0;
        _opacity = 1.0;
        Opacity = 1.0;
        ApplyBounds();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.C when control:
                Copy();
                e.Handled = true;
                break;
            case Key.S when control:
                Save();
                e.Handled = true;
                break;
            case Key.W when control:
                Close();
                e.Handled = true;
                break;
            case Key.T:
                Topmost = !Topmost;
                ApplyBounds();
                e.Handled = true;
                break;
            case Key.L:
                _locked = !_locked;
                UpdateFrame();
                e.Handled = true;
                break;
        }
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var copy = new MenuItem { Header = "复制" };
        copy.Click += (_, _) => Copy();
        menu.Items.Add(copy);

        var save = new MenuItem { Header = "另存为…" };
        save.Click += (_, _) => Save();
        menu.Items.Add(save);

        var translate = new MenuItem { Header = "翻译" };
        translate.Click += (_, _) => ((App)Application.Current).TranslateImage(SourceImage, this);
        menu.Items.Add(translate);

        menu.Items.Add(new Separator());

        var topmost = new MenuItem { Header = "置顶" };
        topmost.Click += (_, _) =>
        {
            Topmost = !Topmost;
            ApplyBounds();
        };
        menu.Items.Add(topmost);

        var lockItem = new MenuItem { Header = "锁定" };
        lockItem.Click += (_, _) =>
        {
            _locked = !_locked;
            UpdateFrame();
        };
        menu.Items.Add(lockItem);

        var reset = new MenuItem { Header = "重置大小和透明度" };
        reset.Click += (_, _) => ResetView();
        menu.Items.Add(reset);

        menu.Items.Add(new Separator());

        var close = new MenuItem { Header = "关闭" };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);

        ContextMenu = menu;
    }

    private void Copy()
    {
        ClipboardService.CopyImage(SourceImage);
    }

    private void Save()
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存贴图",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg",
            FileName = $"Markshot_pin_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        var initialDirectory = App.Config.ResolveSaveDirectory();
        if (Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        var wasTopmost = Topmost;
        Topmost = false;
        try
        {
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
        }
        finally
        {
            Topmost = wasTopmost;
        }

        try
        {
            BitmapEncoder encoder = Path.GetExtension(dialog.FileName).ToLowerInvariant() is ".jpg" or ".jpeg"
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(SourceImage));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            Log.Error("保存贴图失败", ex);
            MessageBox.Show(this, "保存失败：" + ex.Message, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
