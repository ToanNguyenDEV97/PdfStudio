using System.Windows;

namespace PdfStudio.App.Views;

public partial class PromptDialog : Window
{
    private readonly bool _isPassword;
    public string InputValue { get; private set; } = string.Empty;

    public PromptDialog(string title, string message, string defaultValue = "", bool isPassword = false)
    {
        InitializeComponent();
        Title = title;
        MessageLabel.Text = message;
        _isPassword = isPassword;

        if (_isPassword)
        {
            InputTextBox.Visibility = Visibility.Collapsed;
            InputPasswordBox.Visibility = Visibility.Visible;
            Loaded += (_, _) => InputPasswordBox.Focus();
        }
        else
        {
            InputTextBox.Text = defaultValue;
            Loaded += (_, _) =>
            {
                InputTextBox.Focus();
                InputTextBox.SelectAll();
            };
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        InputValue = _isPassword ? InputPasswordBox.Password : InputTextBox.Text.Trim();
        DialogResult = true;
    }
}