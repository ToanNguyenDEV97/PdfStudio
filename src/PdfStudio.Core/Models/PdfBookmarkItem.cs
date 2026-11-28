namespace PdfStudio.Core.Models;

public sealed class PdfBookmarkItem
{
    public string Title { get; }
    public int PageIndex { get; }
    public IReadOnlyList<PdfBookmarkItem> Children { get; }

    public bool HasPage => PageIndex >= 0;
    public string PageNumberDisplay => PageIndex >= 0 ? $"Tr. {PageIndex + 1}" : string.Empty;

    public PdfBookmarkItem(string title, int pageIndex, IReadOnlyList<PdfBookmarkItem>? children = null)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "(Không tiêu đề)" : title.Trim();
        PageIndex = pageIndex;
        Children = children ?? [];
    }
}