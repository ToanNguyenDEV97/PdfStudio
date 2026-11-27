using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using PdfStudio.Core.Interfaces;
using PdfStudio.Core.Models;
using PdfStudio.Engines.QPdf;

namespace PdfStudio.Engines.IText;

public sealed class ITextWriterEngine : IPdfWriterEngine
{
    private readonly QPdfCliService _qpdf = new();

    public void MergeFiles(IReadOnlyList<string> inputPaths, string outputPath)
    {
        if (inputPaths.Count < 2)
            throw new ArgumentException("Cần chọn ít nhất 2 tệp PDF để gộp.");

        foreach (string path in inputPaths)
            EnsureDistinctPaths(path, outputPath);

        var writerProps = new WriterProperties().SetFullCompressionMode(true);
        using var writer = new PdfWriter(outputPath, writerProps);
        using var destDoc = new PdfDocument(writer);
        var merger = new PdfMerger(destDoc);

        foreach (string path in inputPaths)
        {
            using var reader = CreateReader(path);
            using var srcDoc = new PdfDocument(reader);
            merger.Merge(srcDoc, 1, srcDoc.GetNumberOfPages());
        }
    }

    public void ExtractPages(string inputPath, string outputPath, IReadOnlyList<int> oneBasedPageNumbers, string? password = null)
    {
        EnsureDistinctPaths(inputPath, outputPath);

        if (oneBasedPageNumbers.Count == 0)
            throw new ArgumentException("Danh sách trang cần tách không được rỗng.");

        using var reader = CreateReader(inputPath, password);
        using var srcDoc = new PdfDocument(reader);
        int total = srcDoc.GetNumberOfPages();

        var validPages = oneBasedPageNumbers
            .Where(p => p >= 1 && p <= total)
            .ToList();

        if (validPages.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(oneBasedPageNumbers), $"Số trang phải nằm trong khoảng 1 đến {total}.");

        using var writer = new PdfWriter(outputPath, new WriterProperties().SetFullCompressionMode(true));
        using var destDoc = new PdfDocument(writer);
        srcDoc.CopyPagesTo(validPages, destDoc);
    }

    public void RotatePage(string inputPath, string outputPath, int oneBasedPageNumber, int clockwiseDegrees = 90, string? password = null)
    {
        EnsureDistinctPaths(inputPath, outputPath);

        using var reader = CreateReader(inputPath, password);
        using var writer = new PdfWriter(outputPath, CreateWriterProperties(password));
        using var pdfDoc = new PdfDocument(reader, writer);

        if (oneBasedPageNumber < 1 || oneBasedPageNumber > pdfDoc.GetNumberOfPages())
            throw new ArgumentOutOfRangeException(nameof(oneBasedPageNumber));

        PdfPage page = pdfDoc.GetPage(oneBasedPageNumber);
        int nextRotation = ((page.GetRotation() + clockwiseDegrees) % 360 + 360) % 360;
        page.SetRotation(nextRotation);
    }

    public void DeletePage(string inputPath, string outputPath, int oneBasedPageNumber, string? password = null)
    {
        EnsureDistinctPaths(inputPath, outputPath);

        using var reader = CreateReader(inputPath, password);
        using var writer = new PdfWriter(outputPath, CreateWriterProperties(password));
        using var pdfDoc = new PdfDocument(reader, writer);

        int total = pdfDoc.GetNumberOfPages();
        if (total <= 1)
            throw new InvalidOperationException("Không thể xóa trang cuối cùng của tài liệu.");

        if (oneBasedPageNumber < 1 || oneBasedPageNumber > total)
            throw new ArgumentOutOfRangeException(nameof(oneBasedPageNumber));

        pdfDoc.RemovePage(oneBasedPageNumber);
    }

    public void CompressFile(string inputPath, string outputPath, string? password = null)
    {
        EnsureDistinctPaths(inputPath, outputPath);

        if (_qpdf.TryCompress(inputPath, outputPath, password))
            return;

        var writerProps = CreateWriterProperties(password, fullCompression: true)
            .SetCompressionLevel(CompressionConstants.BEST_COMPRESSION);

        using var reader = CreateReader(inputPath, password);
        using var writer = new PdfWriter(outputPath, writerProps);
        writer.SetSmartMode(true);
        using var pdfDoc = new PdfDocument(reader, writer);
    }

    public void ProtectWithPassword(
        string inputPath,
        string outputPath,
        string userPassword,
        string ownerPassword,
        bool allowPrint = true,
        bool allowCopy = true,
        string? currentPassword = null)
    {
        EnsureDistinctPaths(inputPath, outputPath);

        if (string.IsNullOrEmpty(userPassword))
            throw new ArgumentException("Mật khẩu mở tệp không được để trống.");

        int permissions = 0;
        if (allowPrint) permissions |= EncryptionConstants.ALLOW_PRINTING;
        if (allowCopy) permissions |= EncryptionConstants.ALLOW_COPY;

        byte[] userBytes = Encoding.UTF8.GetBytes(userPassword);
        byte[] ownerBytes = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(ownerPassword) ? userPassword : ownerPassword);

        var writerProps = new WriterProperties()
            .SetFullCompressionMode(true)
            .SetStandardEncryption(userBytes, ownerBytes, permissions, EncryptionConstants.ENCRYPTION_AES_256);

        using var reader = CreateReader(inputPath, currentPassword);
        using var writer = new PdfWriter(outputPath, writerProps);
        using var pdfDoc = new PdfDocument(reader, writer);
    }

    public void SaveAnnotations(string inputPath, string outputPath, IReadOnlyList<PdfAnnotationModel> annotations, string? password = null)
    {
        EnsureDistinctPaths(inputPath, outputPath);
        ITextAnnotationSaver.Save(inputPath, outputPath, annotations, password);
    }

    private static PdfReader CreateReader(string path, string? password = null)
    {
        var props = new ReaderProperties();
        if (!string.IsNullOrEmpty(password))
            props.SetPassword(Encoding.UTF8.GetBytes(password));

        var reader = new PdfReader(path, props);
        reader.SetUnethicalReading(true);
        return reader;
    }

    private static WriterProperties CreateWriterProperties(string? password = null, bool fullCompression = false)
    {
        var props = new WriterProperties();
        if (fullCompression) props.SetFullCompressionMode(true);

        if (!string.IsNullOrEmpty(password))
        {
            byte[] passBytes = Encoding.UTF8.GetBytes(password);
            props.SetStandardEncryption(
                passBytes,
                passBytes,
                EncryptionConstants.ALLOW_PRINTING | EncryptionConstants.ALLOW_COPY,
                EncryptionConstants.ENCRYPTION_AES_256);
        }

        return props;
    }

    private static void EnsureDistinctPaths(string inputPath, string outputPath)
    {
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Tệp đích không được trùng với tệp PDF gốc đang đọc. Vui lòng chọn tên tệp khác.");
    }
}