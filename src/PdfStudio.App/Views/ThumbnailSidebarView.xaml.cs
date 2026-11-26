using System.Windows.Controls;
using PdfStudio.App.ViewModels;

namespace PdfStudio.App.Views;

public partial class ThumbnailSidebarView : UserControl
{
    public ThumbnailSidebarView()
    {
        InitializeComponent();
    }

    public void RequestVisibleThumbnails()
    {
        if (DataContext is not MainViewModel vm || vm.ActiveTab is null || vm.ActiveTab.Pages.Count == 0)
            return;

        const double itemHeight = 196.0;
        int first = Math.Max(0, (int)(ThumbnailScrollViewer.VerticalOffset / itemHeight) - 1);
        int visibleCount = Math.Max(4, (int)(ThumbnailScrollViewer.ViewportHeight / itemHeight) + 2);
        int last = Math.Min(vm.ActiveTab.Pages.Count - 1, first + visibleCount);

        _ = vm.ActiveTab.RequestVisibleThumbnailsAsync(first, last);
    }

    private void ThumbnailScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        RequestVisibleThumbnails();
}