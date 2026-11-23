namespace PdfStudio.Core.Models;

public sealed record RenderedPageResult(
    int Width,
    int Height,
    int Stride,
    byte[] BgraPixels);