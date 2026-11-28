using PdfStudio.Core.Models;

namespace PdfStudio.Core.Interfaces;

public interface IPdfReaderEngine : IDisposable
{
    string FilePath { get; }
    int PageCount { get; }

    IReadOnlyList<PageMetadata> GetAllPageMetadata();
    RenderedPageResult RenderPage(int pageIndex, double scaleFactor);
    RenderedPageResult RenderThumbnail(int pageIndex, double targetWidthPx = 140);
    IReadOnlyList<PageCharInfo> GetPageChars(int pageIndex);
    IReadOnlyList<PdfBookmarkItem> GetBookmarks();
}