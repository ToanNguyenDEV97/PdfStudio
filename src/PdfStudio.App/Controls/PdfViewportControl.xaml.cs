using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Models;
using PdfStudio.App.Views;
using System.Windows.Shapes;

namespace PdfStudio.App.Controls;

public partial class PdfViewportControl : UserControl
{
    // Trạng thái Vẽ tự do (Ink)
    private bool _isDrawingInk;
    private int _inkPageIndex = -1;
    private Polyline? _activeInkPolyline;
    private readonly List<PdfPoint> _activeInkPoints = [];

    // Trạng thái Kéo di chuyển ghi chú
    private bool _isDraggingAnnotation;
    private AnnotationItemViewModel? _draggingAnnotation;
    private Canvas? _draggingPageCanvas;
    private Point _dragStartMouse;
    private double _dragOrigX;
    private double _dragOrigY;
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
        var mainVm = GetMainVm();
        if (_currentTab is null || mainVm is null || IsHandModeActive()) return;
        if (sender is not Canvas canvas || canvas.Tag is not int pageIndex) return;

        Point pt = e.GetPosition(canvas);
        _currentTab.ClearSelection();
        _currentTab.SelectAnnotation(null);

        switch (mainVm.ActiveTool)
        {
            case ViewerToolMode.SelectText:
            case ViewerToolMode.Highlight:
            case ViewerToolMode.Underline:
            {
                var chars = await _currentTab.GetPageCharsAsync(pageIndex);
                if (chars.Count == 0) return;

                int hitIndex = FindClosestCharIndex(chars, pt);
                if (hitIndex < 0) return;

                _isSelectingText = true;
                _selectingPageIndex = pageIndex;
                _selectionAnchorCharIndex = hitIndex;
                _activePageChars = chars;
                canvas.CaptureMouse();
                break;
            }
            case ViewerToolMode.StickyNote:
            {
                var prompt = new PromptDialog("Thêm Sticky Note", "Nhập nội dung ghi chú:")
                {
                    Owner = Window.GetWindow(this)
                };
                if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.InputValue))
                {
                    _currentTab.AddAnnotation(new PdfAnnotationModel
                    {
                        PageIndex = pageIndex,
                        Type = AnnotationType.StickyNote,
                        Bounds = new PdfRect(pt.X - 11, pt.Y - 11, 22, 22),
                        TextContent = prompt.InputValue,
                        ColorHex = _currentTab.SelectedColorHex
                    });
                }
                break;
            }
            case ViewerToolMode.FreeText:
            {
                var prompt = new PromptDialog("Thêm chữ (FreeText)", "Nhập nội dung văn bản muốn chèn:")
                {
                    Owner = Window.GetWindow(this)
                };
                if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.InputValue))
                {
                    double fontSize = DocumentTabViewModel.ParseFontSize(_currentTab.SelectedFontSize, 13);
                    double estWidth = Math.Clamp(prompt.InputValue.Length * fontSize * 0.62 + 16, 90, 420);
                    double estHeight = fontSize * 1.7 + 8;

                    _currentTab.AddAnnotation(new PdfAnnotationModel
                    {
                        PageIndex = pageIndex,
                        Type = AnnotationType.FreeText,
                        Bounds = new PdfRect(pt.X, pt.Y - fontSize * 0.5, estWidth, estHeight),
                        TextContent = prompt.InputValue,
                        ColorHex = _currentTab.SelectedColorHex,
                        FontName = _currentTab.SelectedFont,
                        FontSize = fontSize
                    });
                }
                break;
            }
            case ViewerToolMode.Ink:
            {
                _isDrawingInk = true;
                _inkPageIndex = pageIndex;
                _activeInkPoints.Clear();
                _activeInkPoints.Add(new PdfPoint(pt.X, pt.Y));

                _activeInkPolyline = canvas.Children.OfType<Polyline>().LastOrDefault();
                if (_activeInkPolyline is not null)
                {
                    try
                    {
                        _activeInkPolyline.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_currentTab.SelectedColorHex));
                    }
                    catch { _activeInkPolyline.Stroke = Brushes.Crimson; }

                    _activeInkPolyline.StrokeThickness = _currentTab.SelectedStrokeThickness;
                    _activeInkPolyline.Points = [pt];
                }
                canvas.CaptureMouse();
                break;
            }
        }
    }

    private void PageOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        var mainVm = GetMainVm();

        if (_isDrawingInk && _activeInkPolyline is not null)
        {
            Point pt = e.GetPosition(canvas);
            var last = _activeInkPoints[^1];
            if ((pt.X - last.X) * (pt.X - last.X) + (pt.Y - last.Y) * (pt.Y - last.Y) >= 2.25)
            {
                _activeInkPoints.Add(new PdfPoint(pt.X, pt.Y));
                _activeInkPolyline.Points.Add(pt);
            }
            return;
        }

        if (!_isSelectingText)
        {
            canvas.Cursor = IsHandModeActive() ? Cursors.Hand
                : mainVm?.ActiveTool is ViewerToolMode.SelectText or ViewerToolMode.Highlight or ViewerToolMode.Underline ? Cursors.IBeam
                : mainVm?.ActiveTool is ViewerToolMode.Ink or ViewerToolMode.StickyNote or ViewerToolMode.FreeText ? Cursors.Cross
                : Cursors.Arrow;
            return;
        }

        if (_currentTab is null || _activePageChars is null || _activePageChars.Count == 0) return;

        Point currentPt = e.GetPosition(canvas);
        int currentIndex = FindClosestCharIndex(_activePageChars, currentPt);
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
        var mainVm = GetMainVm();

        if (_isDrawingInk)
        {
            _isDrawingInk = false;
            if (sender is Canvas inkCanvas) inkCanvas.ReleaseMouseCapture();
            _activeInkPolyline?.Points.Clear();

            if (_currentTab is not null && _activeInkPoints.Count >= 2)
            {
                double pad = Math.Max(4, _currentTab.SelectedStrokeThickness);
                double minX = _activeInkPoints.Min(p => p.X) - pad;
                double minY = _activeInkPoints.Min(p => p.Y) - pad;
                double maxX = _activeInkPoints.Max(p => p.X) + pad;
                double maxY = _activeInkPoints.Max(p => p.Y) + pad;

                _currentTab.AddAnnotation(new PdfAnnotationModel
                {
                    PageIndex = _inkPageIndex,
                    Type = AnnotationType.Ink,
                    Bounds = new PdfRect(minX, minY, maxX - minX, maxY - minY),
                    InkPoints = [.. _activeInkPoints],
                    ColorHex = _currentTab.SelectedColorHex,
                    StrokeThickness = _currentTab.SelectedStrokeThickness
                });
            }
            _activeInkPoints.Clear();
            return;
        }

        if (!_isSelectingText) return;
        _isSelectingText = false;
        if (sender is Canvas canvas) canvas.ReleaseMouseCapture();

        // Nếu đang bật công cụ Highlight hoặc Gạch dưới -> chuyển vùng vừa bôi đen thành Annotation
        if (_currentTab is not null && mainVm?.ActiveTool is ViewerToolMode.Highlight or ViewerToolMode.Underline)
        {
            var pageVm = _currentTab.Pages[_selectingPageIndex];
            if (pageVm.SelectionRects.Count > 0)
            {
                var quads = pageVm.SelectionRects
                    .Select(r => new PdfRect(r.X, r.Y, r.Width, r.Height))
                    .ToList();

                double minX = quads.Min(r => r.X);
                double minY = quads.Min(r => r.Y);
                double maxX = quads.Max(r => r.X + r.Width);
                double maxY = quads.Max(r => r.Y + r.Height);

                _currentTab.AddAnnotation(new PdfAnnotationModel
                {
                    PageIndex = _selectingPageIndex,
                    Type = mainVm.ActiveTool == ViewerToolMode.Underline ? AnnotationType.Underline : AnnotationType.Highlight,
                    Bounds = new PdfRect(minX, minY, maxX - minX, maxY - minY),
                    QuadRects = quads,
                    ColorHex = _currentTab.SelectedColorHex,
                    StrokeThickness = _currentTab.SelectedStrokeThickness
                });
                _currentTab.ClearSelection();
            }
        }
    }

    // =========================================================
    // CHỌN, KÉO DI CHUYỂN VÀ NHÁY ĐÚP SỬA GHI CHÚ
    // =========================================================
    private void Annotation_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentTab is null || IsHandModeActive()) return;
        if (sender is not FrameworkElement el || el.DataContext is not AnnotationItemViewModel annot) return;

        _currentTab.SelectAnnotation(annot);
        e.Handled = true;

        // Nháy đúp để sửa chữ của Sticky Note hoặc FreeText
        if (e.ClickCount == 2 && (annot.IsStickyNote || annot.IsFreeText))
        {
            var prompt = new PromptDialog(
                annot.IsStickyNote ? "Sửa nội dung Sticky Note" : "Sửa văn bản FreeText",
                "Cập nhật nội dung:",
                annot.TextContent)
            {
                Owner = Window.GetWindow(this)
            };
            if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.InputValue))
                _currentTab.UpdateAnnotationText(annot, prompt.InputValue);
            return;
        }

        // Cho phép kéo di chuyển đối tượng
        _draggingPageCanvas = FindAncestorPageCanvas(el);
        if (_draggingPageCanvas is null) return;

        _isDraggingAnnotation = true;
        _draggingAnnotation = annot;
        _dragStartMouse = e.GetPosition(_draggingPageCanvas);
        _dragOrigX = annot.X;
        _dragOrigY = annot.Y;
        el.CaptureMouse();
    }

    private void Annotation_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingAnnotation || _draggingAnnotation is null || _draggingPageCanvas is null) return;

        Point current = e.GetPosition(_draggingPageCanvas);
        double newX = Math.Max(0, _dragOrigX + (current.X - _dragStartMouse.X));
        double newY = Math.Max(0, _dragOrigY + (current.Y - _dragStartMouse.Y));
        _draggingAnnotation.MoveTo(newX, newY);
    }

    private void Annotation_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingAnnotation || _draggingAnnotation is null) return;

        _isDraggingAnnotation = false;
        if (sender is FrameworkElement el) el.ReleaseMouseCapture();

        _currentTab?.RecordAnnotationMove(_draggingAnnotation, _dragOrigX, _dragOrigY, _draggingAnnotation.X, _draggingAnnotation.Y);
        _draggingAnnotation = null;
        _draggingPageCanvas = null;
    }

    private static Canvas? FindAncestorPageCanvas(DependencyObject child)
    {
        DependencyObject? current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is Canvas canvas && canvas.Tag is int)
                return canvas;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
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