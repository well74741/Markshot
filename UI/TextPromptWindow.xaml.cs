namespace Markshot.UI;

public partial class TextPromptWindow : Window
{
    public string Value => ValueBox.Text.Trim();

    public TextPromptWindow(string prompt, string title, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public static string? Prompt(string prompt, string title, string initialValue = "")
    {
        var window = new TextPromptWindow(prompt, title, initialValue);
        return window.ShowDialog() == true ? window.Value : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueBox.Text))
        {
            MessageBox.Show(this, "名称不能为空。", "Markshot", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
