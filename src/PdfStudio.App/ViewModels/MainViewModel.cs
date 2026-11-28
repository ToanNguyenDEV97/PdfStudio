using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PdfStudio.App.Services;
using PdfStudio.Core.Interfaces;
using PdfStudio.Core.Models;
using PdfStudio.Engines.IText;
using PdfStudio.Core;

namespace PdfStudio.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IPdfWriterEngine _writerEngine = new ITextWriterEngine();
    private CancellationTokenSource? _searchDebounceCts;

    public ObservableCollection<DocumentTabViewModel> Tabs { get; } = [];
    public ObservableCollection<RecentFileItem> RecentFiles { get; } = [];
    public bool HasRecentFiles => RecentFiles.Count > 0;

    public MainViewModel()
    {
        RecentFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentFiles));
        foreach (var item in RecentFilesService.Load())
            RecentFiles.Add(item);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(HasActiveTab))]
    private DocumentTabViewModel? _activeTab;

    [ObservableProperty] private bool _isLeftPaneVisible = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThumbnailsTabActive))]
    private bool _isBookmarksTabActive;

    public bool IsThumbnailsTabActive => !IsBookmarksTabActive;
    [ObservableProperty] private bool _isRightPaneVisible = true;
    [ObservableProperty] private bool _isSearchBarVisible;
    [ObservableProperty] private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHandTool))]
    [NotifyPropertyChangedFor(nameof(IsSelectTextTool))]
    [NotifyPropertyChangedFor(nameof(IsSelectObjectTool))]
    [NotifyPropertyChangedFor(nameof(IsEditTextMode))]
    [NotifyPropertyChangedFor(nameof(IsHighlightTool))]
    [NotifyPropertyChangedFor(nameof(IsUnderlineTool))]
    [NotifyPropertyChangedFor(nameof(IsStickyNoteTool))]
    [NotifyPropertyChangedFor(nameof(IsFreeTextTool))]
    [NotifyPropertyChangedFor(nameof(IsInkTool))]
    private ViewerToolMode _activeTool = ViewerToolMode.SelectText;

    public bool IsHandTool => ActiveTool == ViewerToolMode.Hand;
    public bool IsSelectTextTool => ActiveTool == ViewerToolMode.SelectText;
    public bool IsSelectObjectTool => ActiveTool == ViewerToolMode.SelectObject;
    public bool IsHighlightTool => ActiveTool == ViewerToolMode.Highlight;
    public bool IsUnderlineTool => ActiveTool == ViewerToolMode.Underline;
    public bool IsStickyNoteTool => ActiveTool == ViewerToolMode.StickyNote;
    public bool IsFreeTextTool => ActiveTool == ViewerToolMode.FreeText;
    public bool IsInkTool => ActiveTool == ViewerToolMode.Ink;

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

    partial void OnActiveTabChanging(DocumentTabViewModel? oldValue, DocumentTabViewModel? newValue) =>
        oldValue?.CommitActiveFreeTextEdit();

    partial void OnSearchQueryChanged(string value)
    {
        if (!IsSearchBarVisible || ActiveTab is null) return;

        _searchDebounceCts?.Cancel();
        _searchDebounceCts = new CancellationTokenSource();
        var token = _searchDebounceCts.Token;

        _ = DebounceSearchAsync(token);
    }

    private async Task DebounceSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
            if (!token.IsCancellationRequested && ActiveTab is not null)
                await ActiveTab.SearchAsync(SearchQuery);
        }
        catch (OperationCanceledException) { }
    }

    // =========================================================
    // 1. QUẢN LÝ TỆP & TAB (MỞ, LƯU, ĐÓNG CÓ CẢNH BÁO)
    // =========================================================
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
        if (!File.Exists(filePath))
        {
            var stale = RecentFiles.FirstOrDefault(r =>
                string.Equals(r.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

            if (stale is not null)
            {
                RecentFiles.Remove(stale);
                RecentFilesService.Save(RecentFiles);
                AppDialog.Warning(
                    $"Tệp \"{stale.FileName}\" không còn tồn tại hoặc đã bị di chuyển.\nĐã tự động xóa khỏi danh sách gần đây.",
                    "Không tìm thấy tệp");
            }
            return;
        }

        var existing = Tabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            ActiveTab = existing;
            RecordRecentFile(filePath);
            return;
        }

        string fileName = Path.GetFileName(filePath);
        string? password = null;

        while (true)
        {
            try
            {
                var newTab = new DocumentTabViewModel(filePath, password);
                Tabs.Add(newTab);
                ActiveTab = newTab;
                RecordRecentFile(filePath);
                return;
            }
            catch (PdfPasswordRequiredException ex)
            {
                string promptMsg = ex.IsIncorrectPassword
                    ? $"Mật khẩu không đúng.\nVui lòng nhập lại mật khẩu cho tệp \"{fileName}\":"
                    : $"Tệp \"{fileName}\" được bảo vệ bằng mật khẩu.\nNhập mật khẩu để mở tài liệu:";

                password = AppDialog.Prompt("Yêu cầu mật khẩu PDF", promptMsg, isPassword: true);
                if (password is null)
                    return;
            }
            catch (Exception ex)
            {
                AppDialog.Error(
                    $"Không thể mở tệp \"{fileName}\".\nChi tiết: {ex.Message}",
                    "Lỗi mở tệp PDF");
                return;
            }
        }
    }
    private void RecordRecentFile(string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        var existing = RecentFiles.FirstOrDefault(r =>
            string.Equals(r.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
            RecentFiles.Remove(existing);

        RecentFiles.Insert(0, new RecentFileItem
        {
            FilePath = fullPath,
            FileName = Path.GetFileName(fullPath),
            DirectoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty,
            LastOpenedUtc = DateTime.UtcNow
        });

        while (RecentFiles.Count > RecentFilesService.MaxItems)
            RecentFiles.RemoveAt(RecentFiles.Count - 1);

        RecentFilesService.Save(RecentFiles);
    }

    [RelayCommand]
    private void OpenRecentFile(RecentFileItem? item)
    {
        if (item is not null)
            OpenDocumentFromPath(item.FilePath);
    }

    [RelayCommand]
    private void RemoveRecentFile(RecentFileItem? item)
    {
        if (item is null) return;
        RecentFiles.Remove(item);
        RecentFilesService.Save(RecentFiles);
    }

    [RelayCommand]
    private void ClearRecentFiles()
    {
        if (RecentFiles.Count == 0) return;
        RecentFiles.Clear();
        RecentFilesService.Save(RecentFiles);
    }

    [RelayCommand]
    private async Task SaveDocumentAsync()
    {
        if (ActiveTab is null) return;
        ActiveTab.CommitActiveFreeTextEdit();

        if (!ActiveTab.IsDirty)
        {
            AppDialog.Info("Không có ghi chú hay thay đổi mới cần lưu.", "Lưu tài liệu");
            return;
        }

        try
        {
            await ActiveTab.SavePendingAnnotationsAsync(_writerEngine);
        }
        catch (Exception ex)
        {
            AppDialog.Error($"Không thể lưu ghi chú vào tệp PDF.\nChi tiết: {ex.Message}", "Lỗi lưu tệp");
        }
    }

    [RelayCommand]
    private async Task SaveCopyAsync()
    {
        if (ActiveTab is null || string.IsNullOrEmpty(ActiveTab.FilePath)) return;
        ActiveTab.CommitActiveFreeTextEdit();

        var dialog = new SaveFileDialog
        {
            Title = "Lưu thành bản sao",
            Filter = "PDF Files (*.pdf)|*.pdf",
            FileName = $"{Path.GetFileNameWithoutExtension(ActiveTab.FileName)}_copy.pdf"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            string sourcePath = ActiveTab.FilePath;
            var pending = ActiveTab.GetPendingAnnotations();

            await Task.Run(() =>
            {
                if (pending.Count > 0)
                    _writerEngine.SaveAnnotations(sourcePath, dialog.FileName, pending, ActiveTab.Password);
                else
                    File.Copy(sourcePath, dialog.FileName, overwrite: true);
            });

            AppDialog.Success("Đã lưu bản sao thành công.", "Lưu bản sao");
        }
        catch (Exception ex)
        {
            AppDialog.Error($"Lỗi lưu tệp: {ex.Message}", "Lỗi lưu tệp");
        }
    }

    [RelayCommand]
    private async Task CloseTabAsync(DocumentTabViewModel? tab)
    {
        if (tab is null) return;
        if (!await EnsureTabSavedIfDirtyAsync(tab, "đóng tab này"))
            return;

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

    public bool HasAnyDirtyTab()
    {
        foreach (var tab in Tabs)
            tab.CommitActiveFreeTextEdit();
        return Tabs.Any(t => t.IsDirty);
    }

    public async Task<bool> ConfirmSaveAllDirtyTabsAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            if (!await EnsureTabSavedIfDirtyAsync(tab, "thoát ứng dụng"))
                return false;
        }
        return true;
    }

    private async Task<bool> EnsureTabSavedIfDirtyAsync(DocumentTabViewModel tab, string actionDescription)
    {
        tab.CommitActiveFreeTextEdit();
        if (!tab.IsDirty) return true;

        var choice = AppDialog.ConfirmSave(tab.FileName, actionDescription);
        if (choice == MessageBoxResult.Cancel) return false;

        if (choice == MessageBoxResult.Yes)
        {
            try
            {
                await tab.SavePendingAnnotationsAsync(_writerEngine);
            }
            catch (Exception ex)
            {
                AppDialog.Error($"Không thể lưu \"{tab.FileName}\".\nChi tiết: {ex.Message}", "Lỗi lưu tệp");
                return false;
            }
        }

        return true;
    }

    public void DisposeAllTabs()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();

        foreach (var tab in Tabs)
            tab.Dispose();
        Tabs.Clear();
    }

    // =========================================================
    // 2. ĐIỀU HƯỚNG, ZOOM, TÌM KIẾM & IN ẤN
    // =========================================================
    [RelayCommand] private void ToggleLeftPane() => IsLeftPaneVisible = !IsLeftPaneVisible;
    [RelayCommand] private void ToggleRightPane() => IsRightPaneVisible = !IsRightPaneVisible;
    [RelayCommand] private void CopySelection() => ActiveTab?.CopySelectedText();
    [RelayCommand] private void PrintDocument() => ActiveTab?.PrintDocument();
    [RelayCommand] private void PreviousPage() => ActiveTab?.GoToPreviousPage();
    [RelayCommand] private void NextPage() => ActiveTab?.GoToNextPage();
    [RelayCommand] private void FitWidth() => ActiveTab?.RequestFitWidth();
    [RelayCommand] private void FitPage() => ActiveTab?.RequestFitPage();
    [RelayCommand] private void ResetZoom() => ActiveTab?.ResetZoom();

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

    [RelayCommand]
    private void OpenSearchBar()
    {
        if (ActiveTab is null) return;
        IsSearchBarVisible = true;
    }

    [RelayCommand]
    private void CloseSearchBar()
    {
        _searchDebounceCts?.Cancel();
        IsSearchBarVisible = false;
        SearchQuery = string.Empty;
        ActiveTab?.ClearSearchHighlights();
    }

    [RelayCommand]
    private async Task ExecuteSearchAsync()
    {
        if (ActiveTab is null) return;
        _searchDebounceCts?.Cancel();
        await ActiveTab.SearchAsync(SearchQuery);
    }

    [RelayCommand] private void FindNext() => ActiveTab?.NextSearchMatch();
    [RelayCommand] private void FindPrevious() => ActiveTab?.PreviousSearchMatch();
    [RelayCommand]
    private void ShowThumbnailsPane()
    {
        IsLeftPaneVisible = true;
        IsBookmarksTabActive = false;
    }

    [RelayCommand]
    private void ShowBookmarksPane()
    {
        IsLeftPaneVisible = true;
        IsBookmarksTabActive = true;
    }

    [RelayCommand]
    private void NavigateToBookmark(PdfBookmarkItem? item)
    {
        if (ActiveTab is not null && item is not null && item.PageIndex >= 0)
            ActiveTab.NavigateToPage(item.PageIndex);
    }

    // =========================================================
    // 3. CÔNG CỤ GHI CHÚ, THUỘC TÍNH & UNDO / REDO
    // =========================================================
    [RelayCommand] private void SetHandTool() => ActiveTool = ViewerToolMode.Hand;
    [RelayCommand] private void SetSelectTextTool() => ActiveTool = ViewerToolMode.SelectText;
    [RelayCommand] private void SetSelectObjectTool() => ActiveTool = ViewerToolMode.SelectObject;

    [RelayCommand]
    private void SetHighlightTool()
    {
        ActiveTool = ViewerToolMode.Highlight;
        if (ActiveTab is not null) ActiveTab.SelectedColorHex = "#FACC15";
    }

    [RelayCommand]
    private void SetUnderlineTool()
    {
        ActiveTool = ViewerToolMode.Underline;
        if (ActiveTab is not null) ActiveTab.SelectedColorHex = "#16A34A";
    }

    [RelayCommand]
    private void SetStickyNoteTool()
    {
        ActiveTool = ViewerToolMode.StickyNote;
        if (ActiveTab is not null) ActiveTab.SelectedColorHex = "#F59E0B";
    }

    [RelayCommand]
    private void SetFreeTextTool()
    {
        ActiveTool = ViewerToolMode.FreeText;
        if (ActiveTab is not null) ActiveTab.SelectedColorHex = "#111827";
    }

    [RelayCommand]
    private void SetInkTool()
    {
        ActiveTool = ViewerToolMode.Ink;
        if (ActiveTab is not null) ActiveTab.SelectedColorHex = "#B91C1C";
    }

    [RelayCommand]
    private void SelectColor(string? hex)
    {
        if (ActiveTab is null || string.IsNullOrWhiteSpace(hex)) return;
        ActiveTab.SelectedColorHex = hex;
        if (ActiveTab.SelectedAnnotation is not null)
            ActiveTab.ApplyPropertiesToSelectedAnnotation();
    }

    [RelayCommand] private void ApplyPropertiesToSelection() => ActiveTab?.ApplyPropertiesToSelectedAnnotation();
    [RelayCommand] private void Undo() => ActiveTab?.UndoManager.Undo();
    [RelayCommand] private void Redo() => ActiveTab?.UndoManager.Redo();
    [RelayCommand] private void DeleteSelectedAnnotation() => ActiveTab?.DeleteSelectedAnnotation();

    // =========================================================
    // 4. TỔ CHỨC TRANG & BẢO MẬT (ITEXT / QPDF)
    // =========================================================
    [RelayCommand]
    private async Task MergeFilesAsync()
    {
        var openDialog = new OpenFileDialog
        {
            Title = "Chọn các tệp PDF muốn gộp (giữ Ctrl để chọn nhiều file)",
            Filter = "PDF Files (*.pdf)|*.pdf",
            Multiselect = true
        };

        if (openDialog.ShowDialog() != true) return;
        if (openDialog.FileNames.Length < 2)
        {
            AppDialog.Warning("Vui lòng chọn ít nhất 2 tệp PDF để gộp.", "Gộp PDF");
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "Lưu tệp PDF sau khi gộp",
            Filter = "PDF Files (*.pdf)|*.pdf",
            FileName = "Tai_lieu_da_gop.pdf"
        };

        if (saveDialog.ShowDialog() != true) return;

        try
        {
            await Task.Run(() => _writerEngine.MergeFiles(openDialog.FileNames, saveDialog.FileName));
            OpenDocumentFromPath(saveDialog.FileName);
        }
        catch (Exception ex)
        {
            AppDialog.Error($"Không thể gộp tệp PDF.\nChi tiết: {ex.Message}", "Lỗi gộp PDF");
        }
    }

    [RelayCommand]
    private async Task SplitPagesAsync()
    {
        if (ActiveTab is null) return;

        string? rangeInput = AppDialog.Prompt(
            "Tách trang PDF",
            $"Nhập các trang muốn tách (Tổng: {ActiveTab.TotalPages} trang).\nVí dụ: 1-3, 5, 8",
            $"{ActiveTab.CurrentPage}");

        if (string.IsNullOrWhiteSpace(rangeInput)) return;

        var pages = ParsePageRanges(rangeInput, ActiveTab.TotalPages);
        if (pages.Count == 0)
        {
            AppDialog.Warning("Định dạng dải trang không hợp lệ.", "Tách trang");
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "Lưu tệp PDF đã tách",
            Filter = "PDF Files (*.pdf)|*.pdf",
            FileName = $"{Path.GetFileNameWithoutExtension(ActiveTab.FileName)}_tach.pdf"
        };

        if (saveDialog.ShowDialog() != true) return;

        try
        {
            string sourcePath = ActiveTab.FilePath;
            await Task.Run(() => _writerEngine.ExtractPages(sourcePath, saveDialog.FileName, pages, ActiveTab.Password));
            OpenDocumentFromPath(saveDialog.FileName);
        }
        catch (Exception ex)
        {
            AppDialog.Error($"Không thể tách trang.\nChi tiết: {ex.Message}", "Lỗi tách trang");
        }
    }

    [RelayCommand]
    private async Task RotateCurrentPageAsync()
    {
        if (ActiveTab is null) return;
        if (!await EnsureTabSavedIfDirtyAsync(ActiveTab, "xoay trang")) return;

        int pageNum = ActiveTab.CurrentPage;
        try
        {
            await ActiveTab.ApplyInPlaceEditAsync(
                (src, dst) => _writerEngine.RotatePage(src, dst, pageNum, 90, ActiveTab.Password),
                pageToFocusAfterReload: pageNum);
        }
        catch (Exception ex)
        {
            AppDialog.Error($"Không thể xoay trang {pageNum}.\nChi tiết: {ex.Message}", "Lỗi xoay trang");
        }
    }

    [RelayCommand]
    private async Task DeleteCurrentPageAsync()
    {
        if (ActiveTab is null) return;
        if (ActiveTab.TotalPages <= 1)
        {
            AppDialog.Warning("Không thể xóa trang cuối cùng của tài liệu.", "Xóa trang");
            return;
        }

        if (!await EnsureTabSavedIfDirtyAsync(ActiveTab, "xóa trang")) return;

        int pageNum = ActiveTab.CurrentPage;
        if (!AppDialog.Confirm(
            $"Bạn có chắc chắn muốn xóa vĩnh viễn Trang {pageNum} khỏi tệp \"{ActiveTab.FileName}\" không?",
            "Xác nhận xóa trang",
            confirmText: "Xóa trang",
            cancelText: "Hủy"))
            return;

        try
        {
            await ActiveTab.ApplyInPlaceEditAsync(
                (src, dst) => _writerEngine.DeletePage(src, dst, pageNum, ActiveTab.Password),
                pageToFocusAfterReload: Math.Max(1, pageNum - 1));
        }
        catch (Exception ex)
        {
            AppDialog.Error($"Không thể xóa trang {pageNum}.\nChi tiết: {ex.Message}", "Lỗi xóa trang");
        }
    }

    [RelayCommand]
    private async Task CompressCurrentFileAsync()
    {
        if (ActiveTab is null) return;

        var saveDialog = new SaveFileDialog
        {
            Title = "Lưu tệp PDF sau khi nén",
            Filter = "PDF Files (*.pdf)|*.pdf",
            FileName = $"{Path.GetFileNameWithoutExtension(ActiveTab.FileName)}_nen.pdf"
        };

        if (saveDialog.ShowDialog() != true) return;

        try
        {
            string sourcePath = ActiveTab.FilePath;
            long beforeBytes = new FileInfo(sourcePath).Length;

            await Task.Run(() => _writerEngine.CompressFile(sourcePath, saveDialog.FileName, ActiveTab.Password));

            long afterBytes = new FileInfo(saveDialog.FileName).Length;
            double savedPercent = beforeBytes > 0 ? (1.0 - (double)afterBytes / beforeBytes) * 100.0 : 0;

            AppDialog.Success(
                $"Đã nén xong!\nDung lượng cũ: {beforeBytes / 1024.0:N0} KB\nDung lượng mới: {afterBytes / 1024.0:N0} KB (giảm {savedPercent:0.#}%).",
                "Nén dung lượng PDF");

            OpenDocumentFromPath(saveDialog.FileName);
        }
        catch (Exception ex)
        {
            AppDialog.Error($"Lỗi khi nén tệp.\nChi tiết: {ex.Message}", "Lỗi nén PDF");
        }
    }

    [RelayCommand]
    private async Task ProtectWithPasswordAsync()
    {
        if (ActiveTab is null) return;

        string? password = AppDialog.Prompt(
            "Đặt mật khẩu bảo vệ PDF (AES-256)",
            "Nhập mật khẩu yêu cầu khi mở tệp PDF mới:",
            isPassword: true);

        if (string.IsNullOrEmpty(password)) return;

        var saveDialog = new SaveFileDialog
        {
            Title = "Lưu tệp PDF đã mã hóa",
            Filter = "PDF Files (*.pdf)|*.pdf",
            FileName = $"{Path.GetFileNameWithoutExtension(ActiveTab.FileName)}_baomat.pdf"
        };

        if (saveDialog.ShowDialog() != true) return;

        try
        {
            string sourcePath = ActiveTab.FilePath;
            await Task.Run(() => _writerEngine.ProtectWithPassword(sourcePath, saveDialog.FileName, password, password, currentPassword: ActiveTab.Password));

            AppDialog.Success("Đã tạo tệp PDF bảo vệ bằng mật khẩu AES-256 thành công.", "Bảo mật PDF");
        }
        catch (Exception ex)
        {
            AppDialog.Error($"Không thể mã hóa tệp.\nChi tiết: {ex.Message}", "Lỗi bảo mật");
        }
    }

    private static List<int> ParsePageRanges(string input, int maxPage)
    {
        var result = new List<int>();
        foreach (string rawPart in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawPart.Contains('-'))
            {
                string[] bounds = rawPart.Split('-', StringSplitOptions.TrimEntries);
                if (bounds.Length == 2
                    && int.TryParse(bounds[0], out int start)
                    && int.TryParse(bounds[1], out int end))
                {
                    int from = Math.Clamp(Math.Min(start, end), 1, maxPage);
                    int to = Math.Clamp(Math.Max(start, end), 1, maxPage);
                    for (int p = from; p <= to; p++) result.Add(p);
                }
            }
            else if (int.TryParse(rawPart, out int single) && single >= 1 && single <= maxPage)
            {
                result.Add(single);
            }
        }
        return result;
    }
}