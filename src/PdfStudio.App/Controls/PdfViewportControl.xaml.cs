using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Models;

namespace PdfStudio.App.Controls;

public partial class PdfViewportControl : UserControl
{
    private const double PageSpacing = 18.0;
    private const double TopPadding = 20.0;

    private DocumentTabViewModel? _currentTab;

    // Trạng thái Bàn tay (Panning)
    private bool _isPanning;
    private Point _panStartPoint;
    private double _panStartHOffset;
    private double _panStartVOffset;

    // Trạng thái Bôi đen văn bản
    private bool _isSelectingText;
    private int _selectingPageIndex = -1;
    private int _selectionAnchorCharIndex = -1;
    private IReadOnlyList<PageCharInfo>? _activePageChars;

    public PdfViewportControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => TriggerVisibleRender();
    }

    public void FocusSearchInput()
    {
        Dispatcher.InvokeAsync(() =>
        {
            SearchInputBox.Focus();
            SearchInputBox.SelectAll();
        });
    }

    private MainViewModel? GetMainVm() => Window.GetWindow(this)?.DataContext as MainViewModel;

    private bool IsHandModeActive()
    {
        var mainVm = GetMainVm();
        return mainVm?.ActiveTool == ViewerToolMode.Hand || Keyboard.IsKeyDown(Key.Space);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_currentTab is not null)
            _currentTab.ScrollToPageRequested -= OnScrollToPageRequested;

        _currentTab = e.NewValue as DocumentTabViewModel;

        if (_currentTab is not null)
        {
            _currentTab.UpdateDpiScale(VisualTreeHelper.GetDpi(this).DpiScaleX);
            _currentTab.ScrollToPageRequested += OnScrollToPageRequested;
            MainScrollViewer.ScrollToVerticalOffset(0);
            Dispatcher.InvokeAsync(TriggerVisibleRender);
        }
    }

    private void OnScrollToPageRequested(int pageIndex)
    {
        if (_currentTab is null || pageIndex < 0 || pageIndex >= _currentTab.Pages.Count)
            return;

        double yOffset = TopPadding;
        for (int i = 0; i < pageIndex; i++)
            yOffset += _currentTab.Pages[i].DisplayHeight + PageSpacing;

        MainScrollViewer.ScrollToVerticalOffset(yOffset);
    }

    private void MainScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        TriggerVisibleRender();

    private void MainScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_currentTab is null) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            double step = e.Delta > 0 ? 15 : -15;
            _currentTab.ZoomPercent = Math.Clamp(_currentTab.ZoomPercent + step, 25, 400);
        }
    }

    // =========================================================
    // CÔNG CỤ BÀN TAY (HAND TOOL / GIỮ SPACE KÉO TRANG)
    // =========================================================
    private void MainScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsHandModeActive()) return;

        _isPanning = true;
        _panStartPoint = e.GetPosition(MainScrollViewer);
        _panStartHOffset = MainScrollViewer.HorizontalOffset;
        _panStartVOffset = MainScrollViewer.VerticalOffset;
        MainScrollViewer.Cursor = Cursors.Hand;
        MainScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void MainScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;

        Point current = e.GetPosition(MainScrollViewer);
        MainScrollViewer.ScrollToHorizontalOffset(_panStartHOffset - (current.X - _panStartPoint.X));
        MainScrollViewer.ScrollToVerticalOffset(_panStartVOffset - (current.Y - _panStartPoint.Y));
    }

    private void MainScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;

        _isPanning = false;
        MainScrollViewer.ReleaseMouseCapture();
        MainScrollViewer.Cursor = Cursors.Arrow;
    }

    // =========================================================
    // CHỌN CHỮ BÔI ĐEN TRỰC TIẾP TRÊN TRANG PDF
    // =========================================================
    private async void PageOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentTab is null || IsHandModeActive()) return;
        if (GetMainVm()?.ActiveTool != ViewerToolMode.SelectText) return;
        if (sender is not Canvas canvas || canvas.Tag is not int pageIndex) return;

        Point pt = e.GetPosition(canvas);
        _currentTab.ClearSelection();

        var chars = await _currentTab.GetPageCharsAsync(pageIndex);
        if (chars.Count == 0) return;

        int hitIndex = FindClosestCharIndex(chars, pt);
        if (hitIndex < 0) return;

        _isSelectingText = true;
        _selectingPageIndex = pageIndex;
        _selectionAnchorCharIndex = hitIndex;
        _activePageChars = chars;
        canvas.CaptureMouse();
    }

    private void PageOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Canvas canvas) return;

        if (!_isSelectingText)
        {
            canvas.Cursor = IsHandModeActive()
                ? Cursors.Hand
                : (GetMainVm()?.ActiveTool == ViewerToolMode.SelectText ? Cursors.IBeam : Cursors.Arrow);
            return;
        }

        if (_currentTab is null || _activePageChars is null || _activePageChars.Count == 0) return;

        Point pt = e.GetPosition(canvas);
        int currentIndex = FindClosestCharIndex(_activePageChars, pt);
        if (currentIndex < 0) return;

        int from = Math.Min(_selectionAnchorCharIndex, currentIndex);
        int to = Math.Max(_selectionAnchorCharIndex, currentIndex);

        var rects = DocumentTabViewModel.MergeCharRectsByLine(_activePageChars, from, to);
        var sb = new StringBuilder(to - from + 1);
        for (int i = from; i <= to; i++)
            sb.Append(_activePageChars[i].Character);

        _currentTab.ApplySelection(_selectingPageIndex, rects, sb.ToString());
    }

    private void PageOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelectingText) return;
        _isSelectingText = false;
        if (sender is Canvas canvas)
            canvas.ReleaseMouseCapture();
    }

    private void ContextCopy_Click(object sender, RoutedEventArgs e) =>
        _currentTab?.CopySelectedText();

    private static int FindClosestCharIndex(IReadOnlyList<PageCharInfo> chars, Point pt)
    {
        int bestIndex = -1;
        double bestDistSq = double.MaxValue;

        for (int i = 0; i < chars.Count; i++)
        {
            var b = chars[i].Bounds;
            if (b.Width <= 0 || chars[i].Character == '\n') continue;

            // Khớp chính xác bên trong khung ký tự
            if (pt.X >= b.X && pt.X <= b.X + b.Width && pt.Y >= b.Y && pt.Y <= b.Y + b.Height)
                return i;

            double cx = b.X + b.Width / 2.0;
            double cy = b.Y + b.Height / 2.0;
            double dx = pt.X - cx;
            double dy = (pt.Y - cy) * 2.5; // Ưu tiên ký tự nằm cùng hàng ngang
            double distSq = dx * dx + dy * dy;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIndex = i;
            }
        }

        return bestDistSq <= 45 * 45 ? bestIndex : -1;
    }

    // =========================================================
    // PHÍM TẮT THANH TÌM KIẾM
    // =========================================================
    private async void SearchInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        var mainVm = GetMainVm();
        if (mainVm is null) return;

        if (e.Key == Key.Escape)
        {
            mainVm.CloseSearchBarCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (string.IsNullOrEmpty(_currentTab?.SearchStatusText))
                await mainVm.ExecuteSearchCommand.ExecuteAsync(null);
            else if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                mainVm.FindPreviousCommand.Execute(null);
            else
                mainVm.FindNextCommand.Execute(null);

            e.Handled = true;
        }
    }

    private void TriggerVisibleRender()
    {
        if (_currentTab is null || _currentTab.Pages.Count == 0) return;

        double viewTop = MainScrollViewer.VerticalOffset;
        double viewBottom = viewTop + Math.Max(MainScrollViewer.ViewportHeight, 700);
        double viewCenter = (viewTop + viewBottom) / 2.0;

        int firstVisible = -1;
        int lastVisible = -1;
        int centerPage = 0;
        double currentY = TopPadding;

        for (int i = 0; i < _currentTab.Pages.Count; i++)
        {
            double pageHeight = _currentTab.Pages[i].DisplayHeight;
            double pageTop = currentY;
            double pageBottom = pageTop + pageHeight;

            if (pageBottom >= viewTop && pageTop <= viewBottom)
            {
                if (firstVisible == -1) firstVisible = i;
                lastVisible = i;
            }

            if (pageTop <= viewCenter && pageBottom >= viewCenter)
                centerPage = i;

            currentY = pageBottom + PageSpacing;
        }

        if (firstVisible == -1)
        {
            firstVisible = 0;
            lastVisible = 0;
        }

        if (_currentTab.CurrentPage != centerPage + 1)
            _currentTab.CurrentPage = centerPage + 1;

        _ = _currentTab.RequestVisiblePagesAsync(firstVisible, lastVisible);
    }
}