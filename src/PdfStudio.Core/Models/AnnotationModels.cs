namespace PdfStudio.Core.Models;

public enum AnnotationType
{
    Highlight,
    Underline,
    StickyNote,
    FreeText,
    Ink
}

public sealed record PdfPoint(double X, double Y);

public sealed class PdfAnnotationModel
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int PageIndex { get; set; }
    public AnnotationType Type { get; set; }
    public PdfRect Bounds { get; set; } = new(0, 0, 24, 24);
    public List<PdfRect> QuadRects { get; set; } = [];
    public List<PdfPoint> InkPoints { get; set; } = [];
    public string TextContent { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#FACC15";
    public double StrokeThickness { get; set; } = 2.0;
    public string FontName { get; set; } = "Arial";
    public double FontSize { get; set; } = 13.0;
    public double Opacity { get; set; } = 0.45;
}