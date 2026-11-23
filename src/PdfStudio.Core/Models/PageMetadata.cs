namespace PdfStudio.Core.Models;

public sealed record PageMetadata(
    int PageIndex,
    double WidthPoints,
    double HeightPoints)
{
    public int PageNumber => PageIndex + 1;
}