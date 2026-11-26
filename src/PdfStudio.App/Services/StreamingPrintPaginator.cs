using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfStudio.Core.Interfaces;
using PdfStudio.Core.Models;

namespace PdfStudio.App.Services;

public sealed class StreamingPrintPaginator : DocumentPaginator
{
    private const double PrintDpi = 200.0;
    private readonly IPdfReaderEngine _readerEngine;
    private readonly IReadOnlyList<int> _pageIndices;
    private readonly bool _fitToPage;
    private readonly bool _isGrayscale;
    private Size _pageSize;

    public StreamingPrintPaginator(
        IPdfReaderEngine readerEngine,
        Size printableAreaSize,
        IReadOnlyList<int> zeroBasedPageIndices,
        bool fitToPage = true,
        bool isGrayscale = false)
    {
        _readerEngine = readerEngine;
        _pageSize = printableAreaSize;
        _fitToPage = fitToPage;
        _isGrayscale = isGrayscale;
        _pageIndices = zeroBasedPageIndices
            .Where(i => i >= 0 && i < readerEngine.PageCount)
            .ToList();
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pageIndices.Count;
    public override IDocumentPaginatorSource? Source => null;

    public override Size PageSize
    {
        get => _pageSize;
        set => _pageSize = value;
    }

    public override DocumentPage GetPage(int pageNumber)
    {
        int actualPageIndex = _pageIndices[pageNumber];
        double renderScale = PrintDpi / 72.0;

        RenderedPageResult raw = _readerEngine.RenderPage(actualPageIndex, renderScale);
        BitmapSource bitmap = BitmapSource.Create(
            raw.Width, raw.Height, PrintDpi, PrintDpi,
            PixelFormats.Bgra32, null, raw.BgraPixels, raw.Stride);

        if (_isGrayscale)
        {
            var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            bitmap = gray;
        }
        else
        {
            bitmap.Freeze();
        }

        var container = new Grid
        {
            Width = _pageSize.Width,
            Height = _pageSize.Height,
            Background = Brushes.White,
            ClipToBounds = true
        };

        var image = new Image
        {
            Source = bitmap,
            Stretch = _fitToPage ? Stretch.Uniform : Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        container.Children.Add(image);

        var rect = new Rect(new Point(0, 0), _pageSize);
        container.Measure(_pageSize);
        container.Arrange(rect);
        container.UpdateLayout();

        return new DocumentPage(container, _pageSize, rect, rect);
    }
}