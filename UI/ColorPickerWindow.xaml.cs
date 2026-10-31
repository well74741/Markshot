using System.Globalization;

namespace Markshot.UI;

public partial class ColorPickerWindow : Window
{
    private bool _updating;

    public Color SelectedColor { get; private set; }

    public ColorPickerWindow(Color color)
    {
        InitializeComponent();

        SelectedColor = color;
        _updating = true;
        RSlider.Value = color.R;
        GSlider.Value = color.G;
        BSlider.Value = color.B;
        _updating = false;
        UpdatePreview();
    }

    private Color CurrentColor => Color.FromRgb(
        (byte)Math.Round(RSlider.Value),
        (byte)Math.Round(GSlider.Value),
        (byte)Math.Round(BSlider.Value));

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !IsLoaded || Preview == null)
        {
            return;
        }

        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var color = CurrentColor;
        Preview.Background = new SolidColorBrush(color);
        RValue.Text = color.R.ToString(CultureInfo.InvariantCulture);
        GValue.Text = color.G.ToString(CultureInfo.InvariantCulture);
        BValue.Text = color.B.ToString(CultureInfo.InvariantCulture);
        HexBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void ParseHex()
    {
        var text = HexBox.Text.Trim();
        if (!text.StartsWith('#') || text.Length != 7 ||
            !int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            HexBox.Text = $"#{CurrentColor.R:X2}{CurrentColor.G:X2}{CurrentColor.B:X2}";
            return;
        }

        _updating = true;
        RSlider.Value = (value >> 16) & 0xFF;
        GSlider.Value = (value >> 8) & 0xFF;
        BSlider.Value = value & 0xFF;
        _updating = false;
        UpdatePreview();
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

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ParseHex();
        SelectedColor = CurrentColor;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
