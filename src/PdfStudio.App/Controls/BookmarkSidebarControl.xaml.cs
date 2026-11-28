using System.Windows;
using System.Windows.Controls;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Models;

namespace PdfStudio.App.Controls;

public partial class BookmarkSidebarControl : UserControl
{
    public BookmarkSidebarControl()
    {
        InitializeComponent();
    }

    private void BookmarkItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PdfBookmarkItem item }
            && item.PageIndex >= 0
            && DataContext is DocumentTabViewModel tab)
        {
            tab.NavigateToPage(item.PageIndex);
        }
    }
}