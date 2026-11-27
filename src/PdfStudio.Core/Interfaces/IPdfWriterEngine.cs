using PdfStudio.Core.Models;

namespace PdfStudio.Core.Interfaces;

public interface IPdfWriterEngine
{
    void MergeFiles(IReadOnlyList<string> inputPaths, string outputPath);
    void ExtractPages(string inputPath, string outputPath, IReadOnlyList<int> oneBasedPageNumbers, string? password = null);
    void RotatePage(string inputPath, string outputPath, int oneBasedPageNumber, int clockwiseDegrees = 90, string? password = null);
    void DeletePage(string inputPath, string outputPath, int oneBasedPageNumber, string? password = null);
    void CompressFile(string inputPath, string outputPath, string? password = null);
    void ProtectWithPassword(
        string inputPath,
        string outputPath,
        string userPassword,
        string ownerPassword,
        bool allowPrint = true,
        bool allowCopy = true,
        string? currentPassword = null);
    void SaveAnnotations(string inputPath, string outputPath, IReadOnlyList<PdfAnnotationModel> annotations, string? password = null);
}