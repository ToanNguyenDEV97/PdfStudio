using System.Diagnostics;

namespace PdfStudio.Engines.QPdf;

public sealed class QPdfCliService
{
    private readonly string _qpdfPath;

    public QPdfCliService()
    {
        _qpdfPath = Path.Combine(AppContext.BaseDirectory, "Tools", "qpdf.exe");
    }

    public bool IsAvailable => File.Exists(_qpdfPath);

    public bool TryCompress(string inputPath, string outputPath)
    {
        if (!IsAvailable) return false;

        var startInfo = new ProcessStartInfo
        {
            FileName = _qpdfPath,
            ArgumentList =
            {
                "--object-streams=generate",
                "--compress-streams=y",
                "--recompress-flate",
                "--compression-level=9",
                inputPath,
                outputPath
            },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(startInfo);
        if (proc is null) return false;

        // Xả StandardError song song để tránh đầy buffer gây treo WaitForExit
        _ = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return false;
        }

        // qpdf trả mã 0 (thành công) hoặc 3 (thành công kèm cảnh báo nhẹ)
        return (proc.ExitCode == 0 || proc.ExitCode == 3) && File.Exists(outputPath);
    }
}