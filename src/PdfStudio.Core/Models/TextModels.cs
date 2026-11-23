namespace PdfStudio.Core.Models;

public enum ViewerToolMode
{
    SelectText,
    Hand,
    EditText
}

public sealed record PdfRect(double X, double Y, double Width, double Height);

public sealed record PageCharInfo(
    char Character,
    PdfRect Bounds,
    double OriginX,
    double OriginY,
    string FontName,
    double FontSize);

public sealed record SearchMatchItem(
    int PageIndex,
    int CharStartIndex,
    int CharLength,
    IReadOnlyList<PdfRect> HighlightRects);