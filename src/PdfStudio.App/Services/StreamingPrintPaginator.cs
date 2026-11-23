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
    private readonly int _fromPageIndex;
    private readonly int _pageCount;
    private Size _pageSize;

    public StreamingPrintPaginator(
        IPdfReaderEngine readerEngine,
        Size printableAreaSize,
        int fromPageIndex,
        int toPageIndex)
    {
        _readerEngine = readerEngine;
        _pageSize = printableAreaSize;
        _fromPageIndex = Math.Clamp(fromPageIndex, 0, Math.Max(0, readerEngine.PageCount - 1));
        int safeTo = Math.Clamp(toPageIndex, _fromPageIndex, Math.Max(0, readerEngine.PageCount - 1));
        _pageCount = readerEngine.PageCount == 0 ? 0 : (safeTo - _fromPageIndex + 1);
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pageCount;
    public override IDocumentPaginatorSource? Source => null;

    public override Size PageSize
    {
        get => _pageSize;
        set => _pageSize = value;
    }

    public override DocumentPage GetPage(int pageNumber)
    {
        int actualPageIndex = _fromPageIndex + pageNumber;
        double renderScale = PrintDpi / 72.0;

        RenderedPageResult raw = _readerEngine.RenderPage(actualPageIndex, renderScale);
        var bitmap = BitmapSource.Create(
            raw.Width, raw.Height, PrintDpi, PrintDpi,
            PixelFormats.Bgra32, null, raw.BgraPixels, raw.Stride);
        bitmap.Freeze();

        var container = new Grid
        {
            Width = _pageSize.Width,
            Height = _pageSize.Height,
            Background = Brushes.White
        };

        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
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