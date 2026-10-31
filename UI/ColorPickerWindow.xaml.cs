using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Markshot.UI;

public partial class ColorPickerWindow : Window
{
    private bool _updating;
    private bool _draggingSv;
    private DispatcherTimer? _pickTimer;
    private bool _pickWaitRelease = true;
    private double _hue;
    private double _saturation;
    private double _value;
    private double _alpha = 1;

    public Color SelectedColor { get; private set; }

    public bool Accepted { get; private set; }

    public ColorPickerWindow(Color color)
    {
        InitializeComponent();

        SelectedColor = color;
        SetFromColor(color);
        Loaded += (_, _) =>
        {
            UpdateUi();
            Dispatcher.BeginInvoke(new Action(UpdateUi), DispatcherPriority.Loaded);
        };
    }

    private Color CurrentColor
    {
        get
        {
            var (r, g, b) = HsvToRgb(_hue, _saturation, _value);
            return Color.FromArgb((byte)Math.Round(_alpha * 255), r, g, b);
        }
    }

    private void SetFromColor(Color color)
    {
        var (h, s, v) = RgbToHsv(color.R, color.G, color.B);
        _hue = h;
        _saturation = s;
        _value = v;
        _alpha = color.A / 255.0;
        _updating = true;
        HueSlider.Value = _hue;
        AlphaSlider.Value = _alpha;
        _updating = false;
        UpdateUi();
    }

    private void UpdateUi()
    {
        if (!IsLoaded)
        {
            return;
        }

        var (r, g, b) = HsvToRgb(_hue, 1, 1);
        var pureHue = Color.FromRgb(r, g, b);
        SatLayer.Fill = new LinearGradientBrush(Colors.White, pureHue, 0);

        var (cr, cg, cb) = HsvToRgb(_hue, _saturation, _value);
        var opaque = Color.FromRgb(cr, cg, cb);
        AlphaSlider.Background = new LinearGradientBrush(
            Color.FromArgb(0, cr, cg, cb),
            opaque,
            0);

        var width = Math.Max(1, SvHost.ActualWidth);
        var height = Math.Max(1, SvHost.ActualHeight);
        Canvas.SetLeft(SvCursor, _saturation * width - SvCursor.Width / 2);
        Canvas.SetTop(SvCursor, (1 - _value) * height - SvCursor.Height / 2);

        HexBox.Text = $"#{cr:X2}{cg:X2}{cb:X2}";
        AlphaPercentBox.Text = $"{Math.Round(_alpha * 100)}%";
    }

    private void Sv_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = true;
        SvHost.CaptureMouse();
        UpdateSv(e.GetPosition(SvHost));
        e.Handled = true;
    }

    private void Sv_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingSv)
        {
            UpdateSv(e.GetPosition(SvHost));
        }
    }

    private void Sv_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = false;
        SvHost.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateSv(Point point)
    {
        var width = Math.Max(1, SvHost.ActualWidth);
        var height = Math.Max(1, SvHost.ActualHeight);
        _saturation = Math.Clamp(point.X / width, 0, 1);
        _value = Math.Clamp(1 - point.Y / height, 0, 1);
        UpdateUi();
    }

    private void Hue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !IsLoaded)
        {
            return;
        }

        _hue = e.NewValue;
        UpdateUi();
    }

    private void Alpha_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !IsLoaded)
        {
            return;
        }

        _alpha = Math.Clamp(e.NewValue, 0, 1);
        UpdateUi();
    }

    private void ParseHex()
    {
        var text = HexBox.Text.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length is not (6 or 8) ||
            !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            UpdateUi();
            return;
        }

        var r = (byte)((value >> 16) & 0xFF);
        var g = (byte)((value >> 8) & 0xFF);
        var b = (byte)(value & 0xFF);
        var (h, s, v) = RgbToHsv(r, g, b);
        _hue = h;
        _saturation = s;
        _value = v;
        if (text.Length == 8)
        {
            _alpha = ((value >> 24) & 0xFF) / 255.0;
        }

        _updating = true;
        HueSlider.Value = _hue;
        AlphaSlider.Value = _alpha;
        _updating = false;
        UpdateUi();
    }

    private void ParseAlphaPercent()
    {
        var text = AlphaPercentBox.Text.Trim().TrimEnd('%');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            UpdateUi();
            return;
        }

        _alpha = Math.Clamp(percent / 100.0, 0, 1);
        _updating = true;
        AlphaSlider.Value = _alpha;
        _updating = false;
        UpdateUi();
    }

    private void HexBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ParseHex();
            e.Handled = true;
        }
    }

    private void HexBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ParseHex();

    private void AlphaBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ParseAlphaPercent();
            e.Handled = true;
        }
    }

    private void AlphaBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ParseAlphaPercent();

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Eyedropper_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _pickWaitRelease = true;
        _pickTimer?.Stop();
        _pickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _pickTimer.Tick += (_, _) =>
        {
            var down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (_pickWaitRelease)
            {
                if (!down)
                {
                    _pickWaitRelease = false;
                }

                return;
            }

            if (!down || !GetCursorPos(out var point))
            {
                return;
            }

            _pickTimer?.Stop();
            _pickTimer = null;
            var hdc = GetDC(IntPtr.Zero);
            var colorRef = GetPixel(hdc, point.X, point.Y);
            ReleaseDC(IntPtr.Zero, hdc);
            var color = Color.FromRgb(
                (byte)(colorRef & 0xFF),
                (byte)((colorRef >> 8) & 0xFF),
                (byte)((colorRef >> 16) & 0xFF));
            SetFromColor(color);
            Show();
            Activate();
        };
        _pickTimer.Start();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ParseHex();
        ParseAlphaPercent();
        SelectedColor = CurrentColor;
        Accepted = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }

    private static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        var rf = r / 255.0;
        var gf = g / 255.0;
        var bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;

        double h = 0;
        if (delta > 0.00001)
        {
            if (max == rf) h = 60 * (((gf - bf) / delta) % 6);
            else if (max == gf) h = 60 * ((bf - rf) / delta + 2);
            else h = 60 * ((rf - gf) / delta + 4);
        }

        if (h < 0) h += 360;
        var s = max <= 0 ? 0 : delta / max;
        return (h, s, max);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return ((byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
    }
}
