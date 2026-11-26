using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfStudio.App.Services;
using PdfStudio.Core;
using PdfStudio.Core.Interfaces;
using PdfStudio.Core.Models;
using PdfStudio.Engines.MuPdf;
using PdfStudio.App.Views;

namespace PdfStudio.App.ViewModels;
public enum ZoomFitMode
{
    FitWidth,
    FitPage
}
public partial class DocumentTabViewModel : ObservableObject, IDisposable
{
    private const int MaxCachedPages = 12;

    private IPdfReaderEngine _readerEngine;
    private readonly LinkedList<int> _lruOrder = new();
    private readonly HashSet<int> _lruSet = [];
    private readonly HashSet<int> _renderingPages = [];
    private readonly HashSet<int> _renderingThumbnails = [];
    private readonly ConcurrentDictionary<int, IReadOnlyList<PageCharInfo>> _pageCharsCache = new();
    private readonly List<SearchMatchItem> _searchMatches = [];

    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _zoomDebounceCts;
    private CancellationTokenSource? _searchCts;
    private int _lastFirstVisible;
    private int _lastLastVisible;
    private double _currentDpiScale = 1.0;
    private int _activeSearchMatchIndex = -1;
    private bool _disposed;
    public event Action<int>? ScrollToPageRequested;
    public event Action<ZoomFitMode>? FitModeRequested;

    public UndoRedoManager UndoManager { get; } = new();
    public ObservableCollection<PdfPageViewModel> Pages { get; } = [];

    [ObservableProperty] private string _fileName;
    [ObservableProperty] private string _filePath;
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private double _zoomPercent = 100;
    [ObservableProperty] private string _viewModeText = "1 trang / Liên tục";
    [ObservableProperty] private bool _isDirty;

    // Trạng thái Chọn chữ & Tìm kiếm
    [ObservableProperty] private string _selectedText = string.Empty;
    [ObservableProperty] private string _searchStatusText = string.Empty;

    // Thuộc tính Ghi chú & Định dạng chữ
    [ObservableProperty] private AnnotationItemViewModel? _selectedAnnotation;
    [ObservableProperty] private string _selectedColorHex = "#FACC15";
    [ObservableProperty] private double _selectedStrokeThickness = 2.0;
    [ObservableProperty] private string _selectedFont = "Arial";
    [ObservableProperty] private string _selectedFontSize = "12 pt";
    [ObservableProperty] private double _lineSpacing = 1.15;

    public string TabDisplayName => IsDirty ? $"{FileName} *" : FileName;
    public bool CanUndo => UndoManager.CanUndo;
    public bool CanRedo => UndoManager.CanRedo;

    public DocumentTabViewModel(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);

        _readerEngine = new MuPdfReaderEngine(filePath);
        TotalPages = _readerEngine.PageCount;

        foreach (var meta in _readerEngine.GetAllPageMetadata())
            Pages.Add(new PdfPageViewModel(meta, ZoomPercent));

        if (Pages.Count > 0)
            Pages[0].IsSelected = true;

        UndoManager.StateChanged += () =>
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            IsDirty = Pages.Any(p => p.Annotations.Count > 0);
        };
    }

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(TabDisplayName));

    partial void OnCurrentPageChanged(int value)
    {
        for (int i = 0; i < Pages.Count; i++)
            Pages[i].IsSelected = (i + 1) == value;
    }

    partial void OnZoomPercentChanged(double value)
    {
        foreach (var page in Pages)
            page.UpdateDisplaySize(value);

        _zoomDebounceCts?.Cancel();
        _zoomDebounceCts = new CancellationTokenSource();
        var token = _zoomDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(140, token);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!_disposed)
                        _ = RequestVisiblePagesAsync(_lastFirstVisible, _lastLastVisible, forceRerender: true);
                });
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    public void UpdateDpiScale(double dpiScale)
    {
        if (dpiScale > 0.5) _currentDpiScale = dpiScale;
    }

    public void NavigateToPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        CurrentPage = pageIndex + 1;
        ScrollToPageRequested?.Invoke(pageIndex);
    }
    public void GoToPreviousPage()
    {
        if (CurrentPage > 1)
            NavigateToPage(CurrentPage - 2);
    }

    public void GoToNextPage()
    {
        if (CurrentPage < Pages.Count)
            NavigateToPage(CurrentPage);
    }

    public void RequestFitWidth() => FitModeRequested?.Invoke(ZoomFitMode.FitWidth);

    public void RequestFitPage() => FitModeRequested?.Invoke(ZoomFitMode.FitPage);

    public void ResetZoom() => ZoomPercent = 100;

    // =========================================================
    // 1. RENDER ẢO HÓA + LRU CACHE
    // =========================================================
    public async Task RequestVisiblePagesAsync(int firstVisible, int lastVisible, bool forceRerender = false)
    {
        if (_disposed || Pages.Count == 0) return;

        firstVisible = Math.Clamp(firstVisible, 0, Pages.Count - 1);
        lastVisible = Math.Clamp(lastVisible, firstVisible, Pages.Count - 1);

        _lastFirstVisible = firstVisible;
        _lastLastVisible = lastVisible;

        int start = Math.Max(0, firstVisible - 1);
        int end = Math.Min(Pages.Count - 1, lastVisible + 1);

        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        double targetScale = PdfPageViewModel.DipPerPoint * (ZoomPercent / 100.0) * _currentDpiScale;

        for (int i = start; i <= end; i++)
        {
            if (token.IsCancellationRequested || _disposed) return;

            var pageVm = Pages[i];
            TouchLru(i);

            bool needsRender = forceRerender
                || pageVm.PageImage is null
                || Math.Abs(pageVm.RenderedScale - targetScale) > 0.05;

            if (!needsRender || !_renderingPages.Add(i))
                continue;

            int pageIndex = i;
            try
            {
                var bitmap = await Task.Run(() =>
                {
                    if (token.IsCancellationRequested || _disposed) return null;
                    RenderedPageResult raw = _readerEngine.RenderPage(pageIndex, targetScale);
                    return CreateFrozenBitmap(raw);
                }, token);

                if (bitmap is not null && !token.IsCancellationRequested && !_disposed)
                {
                    pageVm.PageImage = bitmap;
                    pageVm.RenderedScale = targetScale;
                    EvictOverflowPages(start, end);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            finally { _renderingPages.Remove(pageIndex); }
        }
    }

    public async Task RequestVisibleThumbnailsAsync(int firstIndex, int lastIndex)
    {
        if (_disposed || Pages.Count == 0) return;

        int start = Math.Clamp(firstIndex, 0, Pages.Count - 1);
        int end = Math.Clamp(lastIndex, start, Pages.Count - 1);

        for (int i = start; i <= end; i++)
        {
            if (_disposed) return;

            var pageVm = Pages[i];
            if (pageVm.ThumbnailImage is not null || !_renderingThumbnails.Add(i))
                continue;

            int pageIndex = i;
            try
            {
                var thumb = await Task.Run(() =>
                {
                    if (_disposed) return null;
                    RenderedPageResult raw = _readerEngine.RenderThumbnail(pageIndex, 130);
                    return CreateFrozenBitmap(raw);
                });

                if (thumb is not null && !_disposed)
                    pageVm.ThumbnailImage = thumb;
            }
            catch (ObjectDisposedException) { return; }
            finally { _renderingThumbnails.Remove(pageIndex); }
        }
    }

    private void TouchLru(int pageIndex)
    {
        if (_lruSet.Contains(pageIndex)) _lruOrder.Remove(pageIndex);
        else _lruSet.Add(pageIndex);
        _lruOrder.AddFirst(pageIndex);
    }

    private void EvictOverflowPages(int keepStart, int keepEnd)
    {
        while (_lruOrder.Count > MaxCachedPages)
        {
            int oldest = _lruOrder.Last!.Value;
            if (oldest >= keepStart && oldest <= keepEnd) break;

            _lruOrder.RemoveLast();
            _lruSet.Remove(oldest);
            Pages[oldest].PageImage = null;
            Pages[oldest].RenderedScale = 0;
        }
    }

    private static BitmapSource CreateFrozenBitmap(RenderedPageResult raw)
    {
        var bmp = BitmapSource.Create(
            raw.Width, raw.Height, 96, 96,
            PixelFormats.Bgra32, null, raw.BgraPixels, raw.Stride);
        bmp.Freeze();
        return bmp;
    }

    // =========================================================
    // 2. CHỌN CHỮ & COPY (CTRL+C)
    // =========================================================
    public Task<IReadOnlyList<PageCharInfo>> GetPageCharsAsync(int pageIndex)
    {
        if (_disposed || pageIndex < 0 || pageIndex >= Pages.Count)
            return Task.FromResult<IReadOnlyList<PageCharInfo>>([]);

        if (_pageCharsCache.TryGetValue(pageIndex, out var cached))
            return Task.FromResult(cached);

        return Task.Run(() =>
        {
            if (_disposed) return (IReadOnlyList<PageCharInfo>)[];
            var chars = _readerEngine.GetPageChars(pageIndex);
            _pageCharsCache[pageIndex] = chars;
            return chars;
        });
    }

    public void ClearSelection()
    {
        SelectedText = string.Empty;
        foreach (var page in Pages)
        {
            if (page.SelectionRects.Count > 0)
                page.SelectionRects.Clear();
        }
    }

    public void ApplySelection(int pageIndex, IReadOnlyList<PdfRect> rects, string text)
    {
        ClearSelection();
        if (pageIndex < 0 || pageIndex >= Pages.Count || rects.Count == 0) return;

        SelectedText = text;
        var pageVm = Pages[pageIndex];
        foreach (var r in rects)
            pageVm.SelectionRects.Add(new HighlightBoxItem(r.X, r.Y, r.Width, r.Height));
    }

    public void CopySelectedText()
    {
        if (!string.IsNullOrEmpty(SelectedText))
            Clipboard.SetText(SelectedText);
    }

    public static List<PdfRect> MergeCharRectsByLine(IReadOnlyList<PageCharInfo> chars, int startIdx, int endIdx)
    {
        var merged = new List<PdfRect>();
        int from = Math.Max(0, Math.Min(startIdx, endIdx));
        int to = Math.Min(chars.Count - 1, Math.Max(startIdx, endIdx));

        double? curX = null, curY = null, curRight = null, curBottom = null, curBaseline = null;

        for (int i = from; i <= to; i++)
        {
            var ch = chars[i];
            if (ch.Character == '\n' || ch.Bounds.Width <= 0)
            {
                FlushCurrent();
                continue;
            }

            bool sameLine = curBaseline.HasValue
                && Math.Abs(ch.OriginY - curBaseline.Value) <= Math.Max(3.5, ch.FontSize * 0.35)
                && ch.Bounds.X >= curRight!.Value - ch.FontSize
                && ch.Bounds.X <= curRight.Value + ch.FontSize * 2.5;

            if (!sameLine)
            {
                FlushCurrent();
                curX = ch.Bounds.X;
                curY = ch.Bounds.Y;
                curRight = ch.Bounds.X + ch.Bounds.Width;
                curBottom = ch.Bounds.Y + ch.Bounds.Height;
                curBaseline = ch.OriginY;
            }
            else
            {
                curX = Math.Min(curX!.Value, ch.Bounds.X);
                curY = Math.Min(curY!.Value, ch.Bounds.Y);
                curRight = Math.Max(curRight!.Value, ch.Bounds.X + ch.Bounds.Width);
                curBottom = Math.Max(curBottom!.Value, ch.Bounds.Y + ch.Bounds.Height);
            }
        }

        FlushCurrent();
        return merged;

        void FlushCurrent()
        {
            if (curX.HasValue)
            {
                merged.Add(new PdfRect(curX.Value, curY!.Value, curRight!.Value - curX.Value, curBottom!.Value - curY.Value));
                curX = curY = curRight = curBottom = curBaseline = null;
            }
        }
    }

    // =========================================================
    // 3. TÌM KIẾM TOÀN TÀI LIỆU (CTRL+F)
    // =========================================================
    public async Task SearchAsync(string query)
    {
        _searchCts?.Cancel();
        ClearSearchHighlights();

        if (_disposed || string.IsNullOrWhiteSpace(query))
        {
            SearchStatusText = string.Empty;
            return;
        }

        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        string trimmed = query.Trim();

        SearchStatusText = "Đang tìm...";

        var found = await Task.Run(() =>
        {
            var list = new List<SearchMatchItem>();
            for (int p = 0; p < Pages.Count; p++)
            {
                if (token.IsCancellationRequested || _disposed) return list;

                var chars = _pageCharsCache.GetOrAdd(p, idx => _readerEngine.GetPageChars(idx));
                if (chars.Count == 0) continue;

                var sb = new StringBuilder(chars.Count);
                for (int i = 0; i < chars.Count; i++)
                    sb.Append(chars[i].Character == '\n' ? ' ' : chars[i].Character);

                string pageText = sb.ToString();
                int pos = 0;
                while ((pos = pageText.IndexOf(trimmed, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var rects = MergeCharRectsByLine(chars, pos, pos + trimmed.Length - 1);
                    if (rects.Count > 0)
                        list.Add(new SearchMatchItem(p, pos, trimmed.Length, rects));
                    pos += trimmed.Length;
                }
            }
            return list;
        }, token);

        if (token.IsCancellationRequested || _disposed) return;

        _searchMatches.AddRange(found);
        if (_searchMatches.Count == 0)
        {
            SearchStatusText = "0 / 0";
            return;
        }

        _activeSearchMatchIndex = 0;
        RefreshSearchHighlightsAndScroll();
    }

    public void NextSearchMatch()
    {
        if (_searchMatches.Count == 0) return;
        _activeSearchMatchIndex = (_activeSearchMatchIndex + 1) % _searchMatches.Count;
        RefreshSearchHighlightsAndScroll();
    }

    public void PreviousSearchMatch()
    {
        if (_searchMatches.Count == 0) return;
        _activeSearchMatchIndex = (_activeSearchMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        RefreshSearchHighlightsAndScroll();
    }

    public void ClearSearchHighlights()
    {
        _searchMatches.Clear();
        _activeSearchMatchIndex = -1;
        SearchStatusText = string.Empty;

        foreach (var page in Pages)
        {
            if (page.SearchRects.Count > 0)
                page.SearchRects.Clear();
        }
    }

    private void RefreshSearchHighlightsAndScroll()
    {
        foreach (var page in Pages)
        {
            if (page.SearchRects.Count > 0)
                page.SearchRects.Clear();
        }

        for (int i = 0; i < _searchMatches.Count; i++)
        {
            var match = _searchMatches[i];
            bool isActive = i == _activeSearchMatchIndex;
            var pageVm = Pages[match.PageIndex];
            foreach (var r in match.HighlightRects)
                pageVm.SearchRects.Add(new HighlightBoxItem(r.X, r.Y, r.Width, r.Height, isActive));
        }

        SearchStatusText = $"{_activeSearchMatchIndex + 1} / {_searchMatches.Count}";
        NavigateToPage(_searchMatches[_activeSearchMatchIndex].PageIndex);
    }

    // =========================================================
    // 4. IN ẤN & LƯU AN TOÀN KHÔNG KHÓA FILE
    // =========================================================
    public void PrintDocument()
    {
        if (_disposed || Pages.Count == 0) return;

        var dialog = new PrintPreviewDialog(_readerEngine, FileName, CurrentPage)
        {
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.ShowDialog();
    }
    public async Task ApplyInPlaceEditAsync(Action<string, string> editAction, int? pageToFocusAfterReload = null)
    {
        if (_disposed || string.IsNullOrEmpty(FilePath)) return;

        int targetPage = pageToFocusAfterReload ?? CurrentPage;
        string directory = Path.GetDirectoryName(FilePath) ?? Path.GetTempPath();
        string tempFile = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(FilePath)}_{Guid.NewGuid():N}.tmp");

        _renderCts?.Cancel();
        _zoomDebounceCts?.Cancel();
        _searchCts?.Cancel();
        _renderingPages.Clear();
        _renderingThumbnails.Clear();
        _lruOrder.Clear();
        _lruSet.Clear();
        _pageCharsCache.Clear();
        ClearSearchHighlights();
        ClearSelection();
        SelectAnnotation(null);

        _readerEngine.Dispose();

        try
        {
            await Task.Run(() =>
            {
                editAction(FilePath, tempFile);
                File.Move(tempFile, FilePath, overwrite: true);
            });
        }
        finally
        {
            if (File.Exists(tempFile))
                try { File.Delete(tempFile); } catch { }

            _readerEngine = new MuPdfReaderEngine(FilePath);
            TotalPages = _readerEngine.PageCount;

            Pages.Clear();
            foreach (var meta in _readerEngine.GetAllPageMetadata())
                Pages.Add(new PdfPageViewModel(meta, ZoomPercent));

            UndoManager.Clear();
            IsDirty = false;

            int clampedPage = Math.Clamp(targetPage, 1, Math.Max(1, Pages.Count));
            CurrentPage = clampedPage;
            if (Pages.Count > 0)
            {
                Pages[clampedPage - 1].IsSelected = true;
                ScrollToPageRequested?.Invoke(clampedPage - 1);
                _ = RequestVisiblePagesAsync(clampedPage - 1, clampedPage - 1, forceRerender: true);
                _ = RequestVisibleThumbnailsAsync(Math.Max(0, clampedPage - 3), Math.Min(Pages.Count - 1, clampedPage + 3));
            }
        }
    }

    // =========================================================
    // 5. BỘ GHI CHÚ (ANNOTATION) & UNDO / REDO
    // =========================================================
    public IReadOnlyList<PdfAnnotationModel> GetPendingAnnotations() =>
        Pages.SelectMany(p => p.Annotations).Select(a => a.Model).ToList();

    public void SelectAnnotation(AnnotationItemViewModel? item)
    {
        if (SelectedAnnotation is not null)
            SelectedAnnotation.IsSelected = false;

        SelectedAnnotation = item;

        if (item is not null)
        {
            item.IsSelected = true;
            SelectedColorHex = item.ColorHex;
            SelectedStrokeThickness = item.StrokeThickness;
            if (item.IsFreeText)
            {
                SelectedFont = item.FontName;
                SelectedFontSize = $"{item.FontSize:0} pt";
            }
        }
    }

    public void AddAnnotation(PdfAnnotationModel model)
    {
        if (_disposed || model.PageIndex < 0 || model.PageIndex >= Pages.Count) return;

        var vm = new AnnotationItemViewModel(model);
        var page = Pages[model.PageIndex];

        UndoManager.Execute(new DelegateUndoAction(
            execute: () =>
            {
                page.Annotations.Add(vm);
                SelectAnnotation(vm);
            },
            undo: () =>
            {
                page.Annotations.Remove(vm);
                if (SelectedAnnotation == vm) SelectAnnotation(null);
            }));
    }

    public AnnotationItemViewModel? BeginNewFreeText(int pageIndex, double x, double y)
    {
        if (_disposed || pageIndex < 0 || pageIndex >= Pages.Count) return null;

        double fontSize = ParseFontSize(SelectedFontSize, 13);
        var model = new PdfAnnotationModel
        {
            PageIndex = pageIndex,
            Type = AnnotationType.FreeText,
            Bounds = new PdfRect(x, Math.Max(0, y - fontSize * 0.6), 140, fontSize * 1.8 + 10),
            TextContent = string.Empty,
            ColorHex = SelectedColorHex,
            FontName = SelectedFont,
            FontSize = fontSize
        };

        var vm = new AnnotationItemViewModel(model)
        {
            IsNewDraft = true,
            IsEditing = true
        };

        Pages[pageIndex].Annotations.Add(vm);
        SelectAnnotation(vm);
        return vm;
    }

    public void BeginEditFreeText(AnnotationItemViewModel item)
    {
        if (!item.IsFreeText || item.IsEditing) return;

        item.OriginalTextBeforeEdit = item.TextContent;
        item.IsNewDraft = false;
        item.IsEditing = true;
        SelectAnnotation(item);
    }

    public bool CommitActiveFreeTextEdit(bool cancel = false)
    {
        var editing = Pages.SelectMany(p => p.Annotations).FirstOrDefault(a => a.IsEditing);
        if (editing is null) return false;

        CommitFreeTextEdit(editing, cancel);
        return true;
    }

    public void CommitFreeTextEdit(AnnotationItemViewModel item, bool cancel = false)
    {
        if (!item.IsEditing) return;

        item.IsEditing = false;
        var page = Pages[item.PageIndex];
        string finalText = item.TextContent.TrimEnd();

        if (item.IsNewDraft)
        {
            page.Annotations.Remove(item);
            if (SelectedAnnotation == item) SelectAnnotation(null);

            if (!cancel && !string.IsNullOrWhiteSpace(finalText))
            {
                item.IsNewDraft = false;
                item.TextContent = finalText;

                UndoManager.Execute(new DelegateUndoAction(
                    execute: () =>
                    {
                        page.Annotations.Add(item);
                        SelectAnnotation(item);
                    },
                    undo: () =>
                    {
                        page.Annotations.Remove(item);
                        if (SelectedAnnotation == item) SelectAnnotation(null);
                    }));
            }
            return;
        }

        if (cancel)
        {
            item.TextContent = item.OriginalTextBeforeEdit;
            return;
        }

        if (string.IsNullOrWhiteSpace(finalText))
        {
            item.TextContent = item.OriginalTextBeforeEdit;
            SelectAnnotation(item);
            DeleteSelectedAnnotation();
            return;
        }

        string oldText = item.OriginalTextBeforeEdit;
        if (oldText != finalText)
        {
            UndoManager.Execute(new DelegateUndoAction(
                execute: () => item.TextContent = finalText,
                undo: () => item.TextContent = oldText));
        }
    }

    public void DeleteSelectedAnnotation()
    {
        if (SelectedAnnotation is null || SelectedAnnotation.IsEditing) return;

        var target = SelectedAnnotation;
        var page = Pages[target.PageIndex];
        int index = page.Annotations.IndexOf(target);
        if (index < 0) return;

        UndoManager.Execute(new DelegateUndoAction(
            execute: () =>
            {
                page.Annotations.Remove(target);
                if (SelectedAnnotation == target) SelectAnnotation(null);
            },
            undo: () =>
            {
                page.Annotations.Insert(Math.Clamp(index, 0, page.Annotations.Count), target);
                SelectAnnotation(target);
            }));
    }

    public void RecordAnnotationMove(AnnotationItemViewModel item, double oldX, double oldY, double newX, double newY)
    {
        if (Math.Abs(oldX - newX) < 0.5 && Math.Abs(oldY - newY) < 0.5) return;

        UndoManager.Execute(new DelegateUndoAction(
            execute: () => item.MoveTo(newX, newY),
            undo: () => item.MoveTo(oldX, oldY)));
    }

    public void UpdateAnnotationText(AnnotationItemViewModel item, string newText)
    {
        string oldText = item.TextContent;
        if (oldText == newText) return;

        UndoManager.Execute(new DelegateUndoAction(
            execute: () => item.TextContent = newText,
            undo: () => item.TextContent = oldText));
    }

    public void ApplyPropertiesToSelectedAnnotation()
    {
        if (SelectedAnnotation is null) return;
        var target = SelectedAnnotation;

        string oldColor = target.ColorHex;
        double oldStroke = target.StrokeThickness;
        string oldFont = target.FontName;
        double oldSize = target.FontSize;

        string newColor = SelectedColorHex;
        double newStroke = Math.Clamp(SelectedStrokeThickness, 0.75, 14);
        string newFont = SelectedFont;
        double newSize = ParseFontSize(SelectedFontSize, oldSize);

        UndoManager.Execute(new DelegateUndoAction(
            execute: () =>
            {
                target.ColorHex = newColor;
                target.StrokeThickness = newStroke;
                target.FontName = newFont;
                target.FontSize = newSize;
            },
            undo: () =>
            {
                target.ColorHex = oldColor;
                target.StrokeThickness = oldStroke;
                target.FontName = oldFont;
                target.FontSize = oldSize;
            }));
    }

    public async Task SavePendingAnnotationsAsync(IPdfWriterEngine writerEngine)
    {
        CommitActiveFreeTextEdit();
        var pending = GetPendingAnnotations();
        if (pending.Count == 0) return;

        int stayPage = CurrentPage;
        await ApplyInPlaceEditAsync(
            (src, dst) => writerEngine.SaveAnnotations(src, dst, pending),
            pageToFocusAfterReload: stayPage);
    }

    public static double ParseFontSize(string sizeLabel, double fallback = 13.0)
    {
        string digits = sizeLabel.Replace("pt", "", StringComparison.OrdinalIgnoreCase).Trim();
        return double.TryParse(digits, out double val) ? val : fallback;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _zoomDebounceCts?.Cancel();
        _zoomDebounceCts?.Dispose();
        _searchCts?.Cancel();
        _searchCts?.Dispose();

        _pageCharsCache.Clear();
        foreach (var page in Pages)
        {
            page.PageImage = null;
            page.ThumbnailImage = null;
        }

        Pages.Clear();
        _readerEngine.Dispose();
    }

    private sealed class DelegateUndoAction(Action execute, Action undo) : IUndoableAction
    {
        public void Execute() => execute();
        public void Undo() => undo();
    }
}