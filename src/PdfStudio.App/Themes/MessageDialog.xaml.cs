using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace PdfStudio.App.Views;

public enum AppDialogType
{
    Info,
    Success,
    Warning,
    Error,
    Question
}

public partial class MessageDialog : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

    public MessageDialog(
        string title,
        string message,
        AppDialogType type = AppDialogType.Info,
        string primaryText = "Đã hiểu",
        string? secondaryText = null,
        string? cancelText = null)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;

        if (!string.IsNullOrEmpty(secondaryText))
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrEmpty(cancelText))
        {
            CancelButton.Content = cancelText;
            CancelButton.Visibility = Visibility.Visible;
        }

        ApplyVisualStyle(type);
        Loaded += (_, _) => PrimaryButton.Focus();
    }

    private void ApplyVisualStyle(AppDialogType type)
    {
        var (kind, fgHex, bgHex) = type switch
        {
            AppDialogType.Success  => (PackIconMaterialKind.CheckCircle, "#16A34A", "#F0FDF4"),
            AppDialogType.Warning  => (PackIconMaterialKind.Alert,       "#D97706", "#FFFBEB"),
            AppDialogType.Error    => (PackIconMaterialKind.AlertCircle, "#DC2626", "#FEF2F2"),
            AppDialogType.Question => (PackIconMaterialKind.HelpCircle,  "#B91C1C", "#FEF2F2"),
            _                      => (PackIconMaterialKind.Information, "#2563EB", "#EFF6FF")
        };

        StatusIcon.Kind = kind;
        StatusIcon.Foreground = CreateBrush(fgHex);
        IconBadge.Background = CreateBrush(bgHex);
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        Result = SecondaryButton.Visibility == Visibility.Visible || CancelButton.Visibility == Visibility.Visible
            ? MessageBoxResult.Yes
            : MessageBoxResult.OK;
        DialogResult = true;
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.No;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.Cancel;
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}