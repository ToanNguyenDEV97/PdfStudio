using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using PdfStudio.Core.Interfaces;
using PdfStudio.Engines.QPdf;
using PdfStudio.Core.Models;

namespace PdfStudio.Engines.IText;

public sealed class ITextWriterEngine : IPdfWriterEngine
{
    private readonly QPdfCliService _qpdf = new();

    public void MergeFiles(IReadOnlyList<string> inputPaths, string outputPath)
    {
        if (inputPaths.Count < 2)
            throw new ArgumentException("Cần chọn ít nhất 2 tệp PDF để gộp.");

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

    public void ExtractPages(string inputPath, string outputPath, IReadOnlyList<int> oneBasedPageNumbers)
    {
        if (oneBasedPageNumbers.Count == 0)
            throw new ArgumentException("Danh sách trang cần tách không được rỗng.");

        using var reader = CreateReader(inputPath);
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

    public void RotatePage(string inputPath, string outputPath, int oneBasedPageNumber, int clockwiseDegrees = 90)
    {
        using var reader = CreateReader(inputPath);
        using var writer = new PdfWriter(outputPath);
        using var pdfDoc = new PdfDocument(reader, writer);

        if (oneBasedPageNumber < 1 || oneBasedPageNumber > pdfDoc.GetNumberOfPages())
            throw new ArgumentOutOfRangeException(nameof(oneBasedPageNumber));

        PdfPage page = pdfDoc.GetPage(oneBasedPageNumber);
        int nextRotation = (page.GetRotation() + clockwiseDegrees) % 360;
        if (nextRotation < 0) nextRotation += 360;
        page.SetRotation(nextRotation);
    }

    public void DeletePage(string inputPath, string outputPath, int oneBasedPageNumber)
    {
        using var reader = CreateReader(inputPath);
        using var writer = new PdfWriter(outputPath);
        using var pdfDoc = new PdfDocument(reader, writer);

        int total = pdfDoc.GetNumberOfPages();
        if (total <= 1)
            throw new InvalidOperationException("Không thể xóa trang cuối cùng của tài liệu.");

        if (oneBasedPageNumber < 1 || oneBasedPageNumber > total)
            throw new ArgumentOutOfRangeException(nameof(oneBasedPageNumber));

        pdfDoc.RemovePage(oneBasedPageNumber);
    }

    public void CompressFile(string inputPath, string outputPath)
    {
        // Ưu tiên qpdf.exe nếu có sẵn trong Tools/, ngược lại nén bằng iText SmartMode
        if (_qpdf.TryCompress(inputPath, outputPath))
            return;

        var writerProps = new WriterProperties()
            .SetFullCompressionMode(true)
            .SetCompressionLevel(CompressionConstants.BEST_COMPRESSION);

        using var reader = CreateReader(inputPath);
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
        bool allowCopy = true)
    {
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

        using var reader = CreateReader(inputPath);
        using var writer = new PdfWriter(outputPath, writerProps);
        using var pdfDoc = new PdfDocument(reader, writer);
    }

    private static PdfReader CreateReader(string path)
    {
        var reader = new PdfReader(path);
        reader.SetUnethicalReading(true);
        return reader;
    }
    public void SaveAnnotations(string inputPath, string outputPath, IReadOnlyList<PdfAnnotationModel> annotations) =>
        ITextAnnotationSaver.Save(inputPath, outputPath, annotations);
}