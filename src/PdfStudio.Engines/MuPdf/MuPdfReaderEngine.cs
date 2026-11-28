using System.Text;
using MuPDFCore;
using PdfStudio.Core;
using PdfStudio.Core.Interfaces;
using PdfStudio.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using ITextBadPasswordException = iText.Kernel.Exceptions.BadPasswordException;
using ITextPdfDocument = iText.Kernel.Pdf.PdfDocument;
using ITextPdfReader = iText.Kernel.Pdf.PdfReader;
using ITextPdfWriter = iText.Kernel.Pdf.PdfWriter;
using ITextReaderProperties = iText.Kernel.Pdf.ReaderProperties;
using ITextPdfDestination = iText.Kernel.Pdf.Navigation.PdfDestination;
using ITextPdfDictionary = iText.Kernel.Pdf.PdfDictionary;
using ITextPdfName = iText.Kernel.Pdf.PdfName;
using ITextPdfNumber = iText.Kernel.Pdf.PdfNumber;
using ITextPdfObject = iText.Kernel.Pdf.PdfObject;
using ITextPdfOutline = iText.Kernel.Pdf.PdfOutline;

namespace PdfStudio.Engines.MuPdf;

public sealed class MuPdfReaderEngine : IPdfReaderEngine
{
    private readonly object _syncLock = new();
    private readonly object _textLock = new();
    private MuPDFContext? _context;
    private MuPDFDocument? _document;
    private PdfDocument? _pigDocument;
    private bool _disposed;
    private byte[]? _decryptedBytes;

    public string FilePath { get; }
    public int PageCount { get; }

    public MuPdfReaderEngine(string filePath, string? password = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Không tìm thấy tệp PDF.", filePath);

        FilePath = filePath;
        _decryptedBytes = CheckOrDecryptPdf(filePath, password);

        try
        {
            _context = new MuPDFContext();
            _document = _decryptedBytes is not null
                ? new MuPDFDocument(_context, _decryptedBytes, InputFileTypes.PDF)
                : new MuPDFDocument(_context, filePath);
            PageCount = _document.Pages.Count;
        }
        catch
        {
            _document?.Dispose();
            _document = null;
            _context?.Dispose();
            _context = null;
            _decryptedBytes = null;
            throw;
        }
    }

    private static byte[]? CheckOrDecryptPdf(string filePath, string? password)
    {
        var readerProps = new ITextReaderProperties();
        if (!string.IsNullOrEmpty(password))
            readerProps.SetPassword(Encoding.UTF8.GetBytes(password));

        try
        {
            if (password is null)
            {
                using var checkReader = new ITextPdfReader(filePath, readerProps);
                checkReader.SetUnethicalReading(true);
                using var checkDoc = new ITextPdfDocument(checkReader);
                return null;
            }

            using var ms = new MemoryStream();
            using (var reader = new ITextPdfReader(filePath, readerProps))
            {
                reader.SetUnethicalReading(true);
                using var writer = new ITextPdfWriter(ms);
                writer.SetCloseStream(false);
                using var doc = new ITextPdfDocument(reader, writer);
            }
            return ms.ToArray();
        }
        catch (ITextBadPasswordException ex)
        {
            throw new PdfPasswordRequiredException(isIncorrectPassword: password is not null, ex);
        }
        catch when (password is null)
        {
            // Nếu tệp không mã hóa nhưng có lỗi cấu trúc nhẹ với iText, vẫn để MuPDF mở trực tiếp
            return null;
        }
    }
    private static bool IsPasswordError(Exception ex)
    {
        string typeName = ex.GetType().Name;
        string msg = ex.Message ?? string.Empty;
        return typeName.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("password", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("authenticate", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("encrypted", StringComparison.OrdinalIgnoreCase);
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

            _pigDocument ??= _decryptedBytes is not null
                ? PdfDocument.Open(_decryptedBytes)
                : PdfDocument.Open(FilePath);
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
    public IReadOnlyList<PdfBookmarkItem> GetBookmarks()
    {
        byte[]? decryptedCopy;
        string path;
        int totalPages;

        lock (_syncLock)
        {
            ThrowIfDisposed();
            decryptedCopy = _decryptedBytes;
            path = FilePath;
            totalPages = PageCount;
        }

        try
        {
            using Stream? memStream = decryptedCopy is not null
                ? new MemoryStream(decryptedCopy, writable: false)
                : null;

            using var reader = memStream is not null
                ? new ITextPdfReader(memStream)
                : new ITextPdfReader(path);
            reader.SetUnethicalReading(true);

            using var doc = new ITextPdfDocument(reader);
            ITextPdfOutline? root = doc.GetOutlines(false);
            if (root is null) return [];

            var namedDests = doc.GetCatalog().GetNameTree(ITextPdfName.Dests).GetNames();
            return ReadOutlineChildren(root, doc, namedDests, totalPages, depth: 0);
        }
        catch
        {
            return [];
        }
    }

    private static List<PdfBookmarkItem> ReadOutlineChildren<TNameKey>(
        ITextPdfOutline parent,
        ITextPdfDocument doc,
        IDictionary<TNameKey, ITextPdfObject> namedDests,
        int totalPages,
        int depth)
    {
        var list = new List<PdfBookmarkItem>();
        if (depth > 20) return list;

        IList<ITextPdfOutline>? children = parent.GetAllChildren();
        if (children is null || children.Count == 0) return list;

        foreach (ITextPdfOutline child in children)
        {
            if (child is null) continue;

            int pageIndex = ResolveOutlinePageIndex(child, doc, namedDests, totalPages);
            var subItems = ReadOutlineChildren(child, doc, namedDests, totalPages, depth + 1);
            list.Add(new PdfBookmarkItem(child.GetTitle() ?? string.Empty, pageIndex, subItems));
        }

        return list;
    }

    private static int ResolveOutlinePageIndex<TNameKey>(
        ITextPdfOutline outline,
        ITextPdfDocument doc,
        IDictionary<TNameKey, ITextPdfObject> namedDests,
        int totalPages)
    {
        try
        {
            ITextPdfDestination? dest = outline.GetDestination();

            if (dest is null)
            {
                ITextPdfDictionary? action = outline.GetContent()?.GetAsDictionary(ITextPdfName.A);
                if (action is not null && ITextPdfName.GoTo.Equals(action.GetAsName(ITextPdfName.S)))
                {
                    ITextPdfObject? dObj = action.Get(ITextPdfName.D);
                    if (dObj is not null)
                        dest = ITextPdfDestination.MakeDestination(dObj);
                }
            }

            if (dest is null) return -1;

            // Gọi động với dictionary chuẩn của phiên bản iText đang cài
            ITextPdfObject? pageObj = dest.GetDestinationPage((dynamic)namedDests);

            int oneBasedPage = pageObj switch
            {
                ITextPdfDictionary pageDict => doc.GetPageNumber(pageDict),
                ITextPdfNumber pageNum => pageNum.IntValue() + 1,
                _ => 0
            };

            return oneBasedPage >= 1 && oneBasedPage <= totalPages
                ? oneBasedPage - 1
                : -1;
        }
        catch
        {
            return -1;
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
                _decryptedBytes = null;
            }
        }
    }
}