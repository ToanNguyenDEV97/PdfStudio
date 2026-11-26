using MuPDFCore;
using PdfStudio.Core.Interfaces;
using PdfStudio.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

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
        if (pageIndex < 0 || pageIndex >= PageCount)
            return [];

        // Đọc kích thước hiển thị từ MuPDF trước (giữ đúng thứ tự khóa tránh deadlock)
        Rectangle muBounds;
        lock (_syncLock)
        {
            ThrowIfDisposed();
            muBounds = _document!.Pages[pageIndex].Bounds;
        }

        lock (_textLock)
        {
            ThrowIfDisposed();

            _pigDocument ??= PdfDocument.Open(FilePath);
            if (pageIndex + 1 > _pigDocument.NumberOfPages)
                return [];

            Page page = _pigDocument.GetPage(pageIndex + 1);
            PdfRectangle cropBox = page.CropBox.Bounds;
            int rot = ((page.Rotation.Value % 360) + 360) % 360;

            // Tự phát hiện xem PdfPig đã xoay CropBox theo /Rotate chưa
            bool cropBoxSwapped = (rot == 90 || rot == 270)
                && Math.Abs(cropBox.Width - muBounds.Height) < Math.Abs(cropBox.Width - muBounds.Width);

            double visualW = cropBoxSwapped ? cropBox.Height : cropBox.Width;
            double visualH = cropBoxSwapped ? cropBox.Width : cropBox.Height;
            double scaleX = visualW > 0 ? muBounds.Width / visualW : 1.0;
            double scaleY = visualH > 0 ? muBounds.Height / visualH : 1.0;

            var result = new List<PageCharInfo>();
            PageCharInfo? prev = null;

            foreach (Letter letter in page.Letters)
            {
                if (string.IsNullOrEmpty(letter.Value)) continue;

                PdfRectangle glyph = letter.BoundingBox;
                double fontSize = letter.PointSize > 0 ? letter.PointSize : Math.Max(1, letter.FontSize);
                string fontName = letter.FontName ?? string.Empty;

                var (x, y, w, h) = ToVisualRect(glyph, cropBox, rot, cropBoxSwapped, scaleX, scaleY);
                var (originX, originY) = ToVisualPoint(letter.StartBaseLine.X, letter.StartBaseLine.Y, cropBox, rot, cropBoxSwapped, scaleX, scaleY);

                // Tách đều các ký tự ghép (ví dụ ligature "fi", "fl") để Ctrl+F và Ctrl+C đủ chữ
                int len = letter.Value.Length;
                double charWidth = w / len;

                for (int k = 0; k < len; k++)
                {
                    char ch = letter.Value[k];
                    double cx = x + k * charWidth;

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
                        else if (k == 0)
                        {
                            double gap = cx - (prev.Bounds.X + prev.Bounds.Width);
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

                    var info = new PageCharInfo(
                        ch,
                        new PdfRect(cx, y, Math.Max(1, charWidth), h),
                        originX + k * charWidth,
                        originY,
                        fontName,
                        fontSize);

                    result.Add(info);
                    prev = info;
                }
            }

            return result;
        }
    }

    private static (double X, double Y) ToVisualPoint(
        double px, double py, PdfRectangle cb, int rot, bool applyManualRotation, double scaleX, double scaleY)
    {
        double vx, vy;
        if (!applyManualRotation)
        {
            vx = px - cb.Left;
            vy = cb.Top - py;
        }
        else
        {
            (vx, vy) = rot switch
            {
                90 => (py - cb.Bottom, px - cb.Left),
                180 => (cb.Right - px, py - cb.Bottom),
                270 => (cb.Top - py, cb.Right - px),
                _ => (px - cb.Left, cb.Top - py)
            };
        }

        return (vx * scaleX, vy * scaleY);
    }

    private static (double X, double Y, double W, double H) ToVisualRect(
        PdfRectangle glyph, PdfRectangle cb, int rot, bool applyManualRotation, double scaleX, double scaleY)
    {
        var p1 = ToVisualPoint(glyph.Left, glyph.Bottom, cb, rot, applyManualRotation, scaleX, scaleY);
        var p2 = ToVisualPoint(glyph.Right, glyph.Top, cb, rot, applyManualRotation, scaleX, scaleY);

        double minX = Math.Min(p1.X, p2.X);
        double maxX = Math.Max(p1.X, p2.X);
        double minY = Math.Min(p1.Y, p2.Y);
        double maxY = Math.Max(p1.Y, p2.Y);

        return (minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
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