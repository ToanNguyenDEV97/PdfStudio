using MuPDFCore;
using PdfStudio.Core.Interfaces;
using PdfStudio.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PdfStudio.Engines.MuPdf;

public sealed class MuPdfReaderEngine : IPdfReaderEngine
{
    private readonly object _syncLock = new();
    private readonly object _textLock = new();
    private MuPDFContext? _context;
    private MuPDFDocument? _document;
    private PdfDocument? _pigDocument;
    private bool _disposed;

    public string FilePath { get; }
    public int PageCount { get; }

    public MuPdfReaderEngine(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Không tìm thấy tệp PDF.", filePath);

        FilePath = filePath;
        _context = new MuPDFContext();
        _document = new MuPDFDocument(_context, filePath);
        PageCount = _document.Pages.Count;
    }

    public IReadOnlyList<PageMetadata> GetAllPageMetadata()
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();
            var list = new List<PageMetadata>(PageCount);

            for (int i = 0; i < PageCount; i++)
            {
                Rectangle bounds = _document!.Pages[i].Bounds;
                list.Add(new PageMetadata(i, bounds.Width, bounds.Height));
            }

            return list;
        }
    }

    public RenderedPageResult RenderPage(int pageIndex, double scaleFactor)
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();
            if (pageIndex < 0 || pageIndex >= PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));

            float zoom = (float)Math.Clamp(scaleFactor, 0.1, 6.0);
            Rectangle bounds = _document!.Pages[pageIndex].Bounds;
            RoundedRectangle rounded = bounds.Round(zoom);

            byte[] pixels = _document.Render(
                pageIndex,
                bounds,
                zoom,
                PixelFormats.BGRA,
                true);

            int width = Math.Max(1, rounded.Width);
            int stride = width * 4;
            int height = Math.Max(1, Math.Min(rounded.Height, pixels.Length / stride));

            return new RenderedPageResult(width, height, stride, pixels);
        }
    }

    public RenderedPageResult RenderThumbnail(int pageIndex, double targetWidthPx = 140)
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();
            Rectangle bounds = _document!.Pages[pageIndex].Bounds;
            double scale = bounds.Width > 0 ? targetWidthPx / bounds.Width : 0.2;
            return RenderPage(pageIndex, scale);
        }
    }

    public IReadOnlyList<PageCharInfo> GetPageChars(int pageIndex)
    {
        lock (_textLock)
        {
            ThrowIfDisposed();
            if (pageIndex < 0 || pageIndex >= PageCount)
                return [];

            // Mở PdfPig theo kiểu lười (chỉ nạp khi người dùng bôi đen hoặc bấm Ctrl+F)
            _pigDocument ??= PdfDocument.Open(FilePath);
            if (pageIndex + 1 > _pigDocument.NumberOfPages)
                return [];

            Page page = _pigDocument.GetPage(pageIndex + 1); // PdfPig đánh số trang từ 1
            var cropBox = page.CropBox.Bounds;
            var result = new List<PageCharInfo>();

            PageCharInfo? prev = null;

            foreach (Letter letter in page.Letters)
            {
                if (string.IsNullOrEmpty(letter.Value)) continue;

                var glyph = letter.GlyphRectangle;
                double fontSize = letter.PointSize > 0 ? letter.PointSize : Math.Max(1, letter.FontSize);
                string fontName = letter.FontName ?? string.Empty;

                // Quy đổi hệ tọa độ PDF (gốc dưới-trái) sang chuẩn Top-Left 72 DPI khớp với ảnh MuPDF
                double x = glyph.Left - cropBox.Left;
                double y = cropBox.Top - glyph.Top;
                double w = Math.Max(1, glyph.Width);
                double h = Math.Max(1, glyph.Height);
                double originX = letter.StartBaseLine.X - cropBox.Left;
                double originY = cropBox.Top - letter.StartBaseLine.Y;

                char ch = letter.Value[0];

                if (prev is not null)
                {
                    bool newLine = Math.Abs(originY - prev.OriginY) > Math.Max(4.0, fontSize * 0.45);
                    if (newLine)
                    {
                        result.Add(new PageCharInfo(
                            '\n',
                            new PdfRect(prev.Bounds.X + prev.Bounds.Width, prev.Bounds.Y, 0, prev.Bounds.Height),
                            prev.OriginX,
                            prev.OriginY,
                            prev.FontName,
                            prev.FontSize));
                    }
                    else
                    {
                        // Tự chèn khoảng trắng nếu file PDF tách từ bằng tọa độ mà không có ký tự space
                        double gap = x - (prev.Bounds.X + prev.Bounds.Width);
                        if (gap > Math.Max(1.8, fontSize * 0.22) && prev.Character != ' ' && ch != ' ')
                        {
                            result.Add(new PageCharInfo(
                                ' ',
                                new PdfRect(prev.Bounds.X + prev.Bounds.Width, y, gap, h),
                                originX,
                                originY,
                                fontName,
                                fontSize));
                        }
                    }
                }

                var info = new PageCharInfo(ch, new PdfRect(x, y, w, h), originX, originY, fontName, fontSize);
                result.Add(info);
                prev = info;
            }

            return result;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_syncLock)
        {
            lock (_textLock)
            {
                if (_disposed) return;
                _disposed = true;

                _pigDocument?.Dispose();
                _pigDocument = null;
                _document?.Dispose();
                _document = null;
                _context?.Dispose();
                _context = null;
            }
        }
    }
}