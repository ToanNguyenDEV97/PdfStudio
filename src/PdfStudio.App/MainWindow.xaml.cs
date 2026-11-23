using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PdfStudio.App.ViewModels;

namespace PdfStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _viewModel.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSearchBarVisible) && _viewModel.IsSearchBarVisible)
                ViewportControl.FocusSearchInput();
            else if (e.PropertyName == nameof(MainViewModel.SearchQuery) && _viewModel.IsSearchBarVisible)
                await _viewModel.ExecuteSearchCommand.ExecuteAsync(null);
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

    private void FileMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.DataContext = DataContext;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = PlacementMode.Bottom;
            btn.ContextMenu.VerticalOffset = 4;
            btn.ContextMenu.IsOpen = true;
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
        // Chống tràn viền khi phóng to trên Windows 10/11
        RootBorder.Padding = isMax ? new Thickness(7) : new Thickness(0);
        MaximizeIcon.Data = Geometry.Parse(isMax
            ? "M 0 2 L 8 2 L 8 10 L 0 10 Z M 2 2 L 2 0 L 10 0 L 10 8 L 8 8"
            : "M 0 0 L 10 0 L 10 10 L 0 10 Z");
    }
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.DisposeAllTabs();
        base.OnClosed(e);
    }

    private void ThumbnailScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel.ActiveTab is null || _viewModel.ActiveTab.Pages.Count == 0) return;

        // Mỗi ô Thumbnail cao xấp xỉ 196px (162px ảnh + chữ + margin)
        const double itemHeight = 196.0;
        int first = Math.Max(0, (int)(ThumbnailScrollViewer.VerticalOffset / itemHeight) - 1);
        int visibleCount = Math.Max(4, (int)(ThumbnailScrollViewer.ViewportHeight / itemHeight) + 2);
        int last = Math.Min(_viewModel.ActiveTab.Pages.Count - 1, first + visibleCount);

        _ = _viewModel.ActiveTab.RequestVisibleThumbnailsAsync(first, last);
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();
}