using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfStudio.App.Services;
using PdfStudio.Core.Interfaces;
using PdfStudio.Core.Models;

namespace PdfStudio.App.Views;

public partial class PrintPreviewDialog : Window
{
    private const double PreviewDpi = 120.0;

    private readonly IPdfReaderEngine _readerEngine;
    private readonly string _fileName;
    private readonly int _currentPageOneBased;
    private readonly List<PrinterOption> _printers = [];

    private LocalPrintServer? _printServer;
    private CancellationTokenSource? _previewCts;
    private List<int> _selectedPageIndices = [];
    private int _previewCursor;
    private bool _isInitialized;

    public PrintPreviewDialog(IPdfReaderEngine readerEngine, string fileName, int currentPageOneBased)
    {
        InitializeComponent();

        _readerEngine = readerEngine;
        _fileName = fileName;
        _currentPageOneBased = Math.Clamp(currentPageOneBased, 1, Math.Max(1, readerEngine.PageCount));

        FileNameText.Text = $"{fileName} ({readerEngine.PageCount} trang)";
        CustomRangeTextBox.Text = $"1-{readerEngine.PageCount}";

        LoadPrinters();
        _isInitialized = true;
        RefreshSelectionAndPreview();
    }

    private void LoadPrinters()
    {
        try
        {
            _printServer = new LocalPrintServer();
            string? defaultName = null;
            try { defaultName = _printServer.DefaultPrintQueue?.FullName; } catch { }

            EnumeratedPrintQueueTypes[] flags =
            [
                EnumeratedPrintQueueTypes.Local,
                EnumeratedPrintQueueTypes.Connections
            ];

            foreach (PrintQueue queue in _printServer.GetPrintQueues(flags).OrderBy(q => q.FullName))
            {
                bool isDefault = string.Equals(queue.FullName, defaultName, StringComparison.OrdinalIgnoreCase);
                _printers.Add(new PrinterOption(queue, isDefault));
            }

            PrinterComboBox.ItemsSource = _printers;
            var selected = _printers.FirstOrDefault(p => p.IsDefault) ?? _printers.FirstOrDefault();
            if (selected is not null)
            {
                PrinterComboBox.SelectedItem = selected;
                UpdatePrinterStatus(selected.Queue);
            }
            else
            {
                PrinterStatusText.Text = "Không tìm thấy máy in nào trên máy tính.";
                PrintButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            PrinterStatusText.Text = $"Không đọc được danh sách máy in: {ex.Message}";
            PrintButton.IsEnabled = false;
        }
    }

    private void UpdatePrinterStatus(PrintQueue queue)
    {
        try
        {
            queue.Refresh();
            var status = queue.QueueStatus;
            string label = status == PrintQueueStatus.None ? "Sẵn sàng"
                : status.HasFlag(PrintQueueStatus.Offline) ? "Đang ngoại tuyến (Offline)"
                : status.HasFlag(PrintQueueStatus.Paused) ? "Đang tạm dừng"
                : status.HasFlag(PrintQueueStatus.PaperOut) ? "Hết giấy"
                : status.HasFlag(PrintQueueStatus.Error) ? "Máy in đang báo lỗi"
                : "Sẵn sàng";

            PrinterStatusText.Text = $"Trạng thái: {label}";
        }
        catch
        {
            PrinterStatusText.Text = "Trạng thái: Sẵn sàng";
        }
    }

    private void RefreshSelectionAndPreview()
    {
        if (!_isInitialized) return;

        bool isCustom = PageRangeComboBox.SelectedIndex == 2;
        CustomRangePanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

        _selectedPageIndices = BuildSelectedPageIndices(out string? error);

        if (!string.IsNullOrEmpty(error))
        {
            RangeErrorText.Text = error;
            RangeErrorText.Visibility = Visibility.Visible;
        }
        else
        {
            RangeErrorText.Visibility = Visibility.Collapsed;
        }

        UpdatePaperDimensions();

        bool canPrint = _printers.Count > 0 && _selectedPageIndices.Count > 0 && string.IsNullOrEmpty(error);
        PrintButton.IsEnabled = canPrint;

        int copies = GetCopies();
        int totalSheets = _selectedPageIndices.Count * copies;
        PrintSummaryText.Text = canPrint
            ? $"Tổng cộng: {_selectedPageIndices.Count} trang × {copies} bản = {totalSheets} tờ"
            : "Vui lòng kiểm tra lại phạm vi trang hoặc máy in.";

        _previewCursor = Math.Clamp(_previewCursor, 0, Math.Max(0, _selectedPageIndices.Count - 1));
        _ = RenderCurrentPreviewAsync();
    }

    private List<int> BuildSelectedPageIndices(out string? error)
    {
        error = null;
        int total = _readerEngine.PageCount;
        if (total <= 0) return [];

        return PageRangeComboBox.SelectedIndex switch
        {
            1 => [_currentPageOneBased - 1],
            2 => ParseCustomPages(CustomRangeTextBox.Text, total, out error),
            _ => Enumerable.Range(0, total).ToList()
        };
    }

    private static List<int> ParseCustomPages(string input, int maxPage, out string? error)
    {
        error = null;
        var result = new List<int>();

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Nhập số trang cần in (ví dụ: 1-3, 5).";
            return result;
        }

        foreach (string rawPart in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawPart.Contains('-'))
            {
                string[] bounds = rawPart.Split('-', StringSplitOptions.TrimEntries);
                if (bounds.Length != 2
                    || !int.TryParse(bounds[0], out int start)
                    || !int.TryParse(bounds[1], out int end)
                    || start < 1 || end < 1 || start > maxPage || end > maxPage)
                {
                    error = $"Dải trang \"{rawPart}\" không hợp lệ (chỉ từ 1 đến {maxPage}).";
                    return [];
                }

                int from = Math.Min(start, end);
                int to = Math.Max(start, end);
                for (int p = from; p <= to; p++) result.Add(p - 1);
            }
            else if (int.TryParse(rawPart, out int single) && single >= 1 && single <= maxPage)
            {
                result.Add(single - 1);
            }
            else
            {
                error = $"Trang \"{rawPart}\" không hợp lệ (chỉ từ 1 đến {maxPage}).";
                return [];
            }
        }

        if (result.Count == 0)
            error = "Không tìm thấy trang hợp lệ nào.";

        return result;
    }

    private void UpdatePaperDimensions()
    {
        double baseWidth = 793.7;  // A4 chuẩn tại 96 DPI (210mm)
        double baseHeight = 1122.5; // A4 chuẩn tại 96 DPI (297mm)

        if (PrinterComboBox.SelectedItem is PrinterOption opt)
        {
            try
            {
                var media = opt.Queue.UserPrintTicket?.PageMediaSize ?? opt.Queue.DefaultPrintTicket?.PageMediaSize;
                if (media?.Width is double w && media.Height is double h && w > 100 && h > 100)
                {
                    baseWidth = Math.Min(w, h);
                    baseHeight = Math.Max(w, h);
                }
            }
            catch { }
        }

        bool isLandscape = OrientationComboBox.SelectedIndex == 1;
        PaperSheetGrid.Width = isLandscape ? baseHeight : baseWidth;
        PaperSheetGrid.Height = isLandscape ? baseWidth : baseHeight;
        PreviewImage.Stretch = ScaleComboBox.SelectedIndex == 0 ? Stretch.Uniform : Stretch.None;
    }

    private async Task RenderCurrentPreviewAsync()
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        if (_selectedPageIndices.Count == 0)
        {
            PreviewImage.Source = null;
            PreviewPageIndicator.Text = "0 / 0";
            PrevPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            return;
        }

        int pageIndex = _selectedPageIndices[_previewCursor];
        bool isGrayscale = ColorComboBox.SelectedIndex == 1;

        PreviewPageIndicator.Text = $"Tờ {_previewCursor + 1} / {_selectedPageIndices.Count}  (Tr. {pageIndex + 1})";
        PrevPageButton.IsEnabled = _previewCursor > 0;
        NextPageButton.IsEnabled = _previewCursor < _selectedPageIndices.Count - 1;
        PreviewLoadingText.Visibility = Visibility.Visible;

        try
        {
            BitmapSource? bitmap = await Task.Run(() =>
            {
                if (token.IsCancellationRequested) return null;

                double scale = PreviewDpi / 72.0;
                RenderedPageResult raw = _readerEngine.RenderPage(pageIndex, scale);

                BitmapSource bmp = BitmapSource.Create(
                    raw.Width, raw.Height, PreviewDpi, PreviewDpi,
                    PixelFormats.Bgra32, null, raw.BgraPixels, raw.Stride);

                if (isGrayscale)
                {
                    var gray = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
                    gray.Freeze();
                    return (BitmapSource)gray;
                }

                bmp.Freeze();
                return bmp;
            }, token);

            if (!token.IsCancellationRequested && bitmap is not null)
                PreviewImage.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (!token.IsCancellationRequested)
                PreviewLoadingText.Visibility = Visibility.Collapsed;
        }
    }

    private int GetCopies() =>
        int.TryParse(CopiesTextBox.Text, out int c) ? Math.Clamp(c, 1, 99) : 1;

    private void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        if (PrinterComboBox.SelectedItem is not PrinterOption selectedPrinter || _selectedPageIndices.Count == 0)
            return;

        bool isLandscape = OrientationComboBox.SelectedIndex == 1;
        bool fitToPage = ScaleComboBox.SelectedIndex == 0;
        bool isGrayscale = ColorComboBox.SelectedIndex == 1;
        int copies = GetCopies();

        try
        {
            PrintButton.IsEnabled = false;
            var queue = selectedPrinter.Queue;

            var printDialog = new PrintDialog { PrintQueue = queue };
            var baseTicket = queue.UserPrintTicket?.Clone() ?? queue.DefaultPrintTicket?.Clone() ?? new PrintTicket();
            baseTicket.CopyCount = copies;
            baseTicket.PageOrientation = isLandscape ? PageOrientation.Landscape : PageOrientation.Portrait;
            baseTicket.OutputColor = isGrayscale ? OutputColor.Monochrome : OutputColor.Color;

            try
            {
                var validated = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, baseTicket);
                printDialog.PrintTicket = validated.ValidatedPrintTicket ?? baseTicket;
            }
            catch
            {
                printDialog.PrintTicket = baseTicket;
            }

            var printableSize = new Size(printDialog.PrintableAreaWidth, printDialog.PrintableAreaHeight);
            var paginator = new StreamingPrintPaginator(
                _readerEngine,
                printableSize,
                _selectedPageIndices,
                fitToPage,
                isGrayscale);

            printDialog.PrintDocument(paginator, $"PdfStudio - {_fileName}");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            AppDialog.Error($"Không thể gửi lệnh in.\nChi tiết: {ex.Message}", "Lỗi in tài liệu");
            PrintButton.IsEnabled = true;
        }
    }

    private void PrinterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        if (PrinterComboBox.SelectedItem is PrinterOption opt)
            UpdatePrinterStatus(opt.Queue);
        RefreshSelectionAndPreview();
    }

    private void Settings_Changed(object sender, SelectionChangedEventArgs e) =>
        RefreshSelectionAndPreview();

    private void CustomRangeTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshSelectionAndPreview();

    private void CopiesTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshSelectionAndPreview();

    private void DecreaseCopies_Click(object sender, RoutedEventArgs e) =>
        CopiesTextBox.Text = Math.Max(1, GetCopies() - 1).ToString();

    private void IncreaseCopies_Click(object sender, RoutedEventArgs e) =>
        CopiesTextBox.Text = Math.Min(99, GetCopies() + 1).ToString();

    private void PrevPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewCursor <= 0) return;
        _previewCursor--;
        _ = RenderCurrentPreviewAsync();
    }

    private void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewCursor >= _selectedPageIndices.Count - 1) return;
        _previewCursor++;
        _ = RenderCurrentPreviewAsync();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _printServer?.Dispose();
        base.OnClosed(e);
    }

    private sealed class PrinterOption(PrintQueue queue, bool isDefault)
    {
        public PrintQueue Queue { get; } = queue;
        public bool IsDefault { get; } = isDefault;
        public string DisplayName => isDefault ? $"{queue.FullName} (Mặc định)" : queue.FullName;
    }
}