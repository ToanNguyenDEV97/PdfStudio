using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace PdfStudio.App.Views;

public partial class PromptDialog : Window
{
    private readonly bool _isPassword;
    public string InputValue { get; private set; } = string.Empty;

    public PromptDialog(string title, string message, string defaultValue = "", bool isPassword = false)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageLabel.Text = message;
        _isPassword = isPassword;

        if (_isPassword)
        {
            HeaderIcon.Kind = PackIconMaterialKind.Lock;
            InputTextBox.Visibility = Visibility.Collapsed;
            PasswordBorder.Visibility = Visibility.Visible;
            Loaded += (_, _) => InputPasswordBox.Focus();
        }
        else
        {
            HeaderIcon.Kind = PackIconMaterialKind.Pencil;
            InputTextBox.Text = defaultValue;
            Loaded += (_, _) =>
            {
                InputTextBox.Focus();
                InputTextBox.SelectAll();
            };
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void InputPasswordBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        PasswordBorder.BorderBrush = (Brush)FindResource("AccentCrimsonBrush");

    private void InputPasswordBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        PasswordBorder.BorderBrush = (Brush)FindResource("BorderSubtleBrush");

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        InputValue = _isPassword ? InputPasswordBox.Password : InputTextBox.Text.Trim();
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}