using PdfStudio.Core.Models;

namespace PdfStudio.Core.Interfaces;

public interface IPdfWriterEngine
{
    void MergeFiles(IReadOnlyList<string> inputPaths, string outputPath);
    void ExtractPages(string inputPath, string outputPath, IReadOnlyList<int> oneBasedPageNumbers);
    void RotatePage(string inputPath, string outputPath, int oneBasedPageNumber, int clockwiseDegrees = 90);
    void DeletePage(string inputPath, string outputPath, int oneBasedPageNumber);
    void CompressFile(string inputPath, string outputPath);
    void ProtectWithPassword(string inputPath, string outputPath, string userPassword, string ownerPassword, bool allowPrint = true, bool allowCopy = true);
    void SaveAnnotations(string inputPath, string outputPath, IReadOnlyList<PdfAnnotationModel> annotations);
}