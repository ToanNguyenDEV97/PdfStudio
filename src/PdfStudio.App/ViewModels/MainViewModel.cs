using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PdfStudio.Core.Models;

namespace PdfStudio.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public ObservableCollection<DocumentTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(HasActiveTab))]
    private DocumentTabViewModel? _activeTab;

    [ObservableProperty] private bool _isLeftPaneVisible = true;
    [ObservableProperty] private bool _isRightPaneVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHandTool))]
    [NotifyPropertyChangedFor(nameof(IsSelectTextTool))]
    [NotifyPropertyChangedFor(nameof(IsEditTextMode))]
    private ViewerToolMode _activeTool = ViewerToolMode.SelectText;

    // Thanh Tìm kiếm nổi Ctrl+F
    [ObservableProperty] private bool _isSearchBarVisible;
    [ObservableProperty] private string _searchQuery = string.Empty;

    public bool IsHandTool => ActiveTool == ViewerToolMode.Hand;
    public bool IsSelectTextTool => ActiveTool == ViewerToolMode.SelectText;
    public bool IsEditTextMode
    {
        get => ActiveTool == ViewerToolMode.EditText;
        set => ActiveTool = value ? ViewerToolMode.EditText : ViewerToolMode.SelectText;
    }

    public string[] AvailableFonts { get; } = ["Arial", "Times New Roman", "Calibri", "Tahoma", "Segoe UI"];
    public string[] AvailableFontSizes { get; } = ["10 pt", "11 pt", "12 pt", "14 pt", "16 pt", "18 pt", "24 pt"];

    public bool HasActiveTab => ActiveTab is not null;

    public string WindowTitle => ActiveTab is null
        ? "PdfStudio (Nội bộ)"
        : $"PdfStudio (Nội bộ) - {ActiveTab.FileName}";

    [RelayCommand]
    private void SetHandTool() => ActiveTool = ViewerToolMode.Hand;

    [RelayCommand]
    private void SetSelectTextTool() => ActiveTool = ViewerToolMode.SelectText;

    [RelayCommand]
    private void OpenSearchBar()
    {
        if (ActiveTab is null) return;
        IsSearchBarVisible = true;
    }

    [RelayCommand]
    private void CloseSearchBar()
    {
        IsSearchBarVisible = false;
        SearchQuery = string.Empty;
        ActiveTab?.ClearSearchHighlights();
    }

    [RelayCommand]
    private async Task ExecuteSearchAsync()
    {
        if (ActiveTab is null) return;
        await ActiveTab.SearchAsync(SearchQuery);
    }

    [RelayCommand]
    private void FindNext() => ActiveTab?.NextSearchMatch();

    [RelayCommand]
    private void FindPrevious() => ActiveTab?.PreviousSearchMatch();

    [RelayCommand]
    private void CopySelection() => ActiveTab?.CopySelectedText();

    [RelayCommand]
    private void PrintDocument() => ActiveTab?.PrintDocument();

    [RelayCommand]
    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Mở tài liệu PDF",
            Filter = "PDF Files (*.pdf)|*.pdf|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var path in dialog.FileNames)
                OpenDocumentFromPath(path);
        }
    }

    public void OpenDocumentFromPath(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var existing = Tabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            ActiveTab = existing;
            return;
        }

        try
        {
            var newTab = new DocumentTabViewModel(filePath);
            Tabs.Add(newTab);
            ActiveTab = newTab;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Không thể mở tệp \"{Path.GetFileName(filePath)}\".\nChi tiết: {ex.Message}",
                "Lỗi mở tệp PDF",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void CloseTab(DocumentTabViewModel? tab)
    {
        if (tab is null) return;
        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        tab.Dispose();

        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.Count > 0
                ? Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)]
                : null;
        }
    }

    [RelayCommand]
    private void ToggleLeftPane() => IsLeftPaneVisible = !IsLeftPaneVisible;

    [RelayCommand]
    private void ToggleRightPane() => IsRightPaneVisible = !IsRightPaneVisible;

    [RelayCommand]
    private void ZoomIn()
    {
        if (ActiveTab is not null)
            ActiveTab.ZoomPercent = Math.Min(400, ActiveTab.ZoomPercent + 15);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (ActiveTab is not null)
            ActiveTab.ZoomPercent = Math.Max(25, ActiveTab.ZoomPercent - 15);
    }

    [RelayCommand]
    private void SelectPage(PdfPageViewModel? page)
    {
        if (ActiveTab is not null && page is not null)
            ActiveTab.NavigateToPage(page.PageIndex);
    }

    public void DisposeAllTabs()
    {
        foreach (var tab in Tabs)
            tab.Dispose();
        Tabs.Clear();
    }
}