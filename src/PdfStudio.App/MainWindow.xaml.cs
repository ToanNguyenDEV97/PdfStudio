using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using PdfStudio.App.ViewModels;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace PdfStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isForceClosing;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _viewModel.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSearchBarVisible) && _viewModel.IsSearchBarVisible)
                ViewportControl.FocusSearchInput();
            else if (e.PropertyName == nameof(MainViewModel.ActiveTab))
                await Dispatcher.InvokeAsync(ThumbnailSidebar.RequestVisibleThumbnails);
        };
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var file in files)
        {
            if (string.Equals(Path.GetExtension(file), ".pdf", StringComparison.OrdinalIgnoreCase))
                _viewModel.OpenDocumentFromPath(file);
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool isMax = WindowState == WindowState.Maximized;
        RootBorder.Padding = isMax ? new Thickness(7) : new Thickness(0);
        MaximizeIcon.Data = Geometry.Parse(isMax
            ? "M 0 2 L 8 2 L 8 10 L 0 10 Z M 2 2 L 2 0 L 10 0 L 10 8 L 8 8"
            : "M 0 0 L 10 0 L 10 10 L 0 10 Z");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isForceClosing && _viewModel.HasAnyDirtyTab())
        {
            e.Cancel = true;
            _ = HandleClosingWithDirtyTabsAsync();
            return;
        }

        base.OnClosing(e);
    }

    private async Task HandleClosingWithDirtyTabsAsync()
    {
        if (await _viewModel.ConfirmSaveAllDirtyTabsAsync())
        {
            _isForceClosing = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.DisposeAllTabs();
        base.OnClosed(e);
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();
    private void PageJumpTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.SelectAll();
    }

    private void PageJumpTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;

        if (e.Key == Key.Enter)
        {
            if (_viewModel.ActiveTab is not null && int.TryParse(tb.Text.Trim(), out int targetPage))
            {
                int clamped = Math.Clamp(targetPage, 1, Math.Max(1, _viewModel.ActiveTab.TotalPages));
                _viewModel.ActiveTab.NavigateToPage(clamped - 1);
            }

            BindingOperations.GetBindingExpression(tb, TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            BindingOperations.GetBindingExpression(tb, TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void PageJumpTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            BindingOperations.GetBindingExpression(tb, TextBox.TextProperty)?.UpdateTarget();
    }
}