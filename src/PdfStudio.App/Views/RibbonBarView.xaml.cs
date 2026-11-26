using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PdfStudio.App.Views;

public partial class RibbonBarView : UserControl
{
    public RibbonBarView()
    {
        InitializeComponent();
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

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) =>
        Window.GetWindow(this)?.Close();
}