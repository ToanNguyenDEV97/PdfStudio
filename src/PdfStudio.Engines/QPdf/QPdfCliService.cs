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

    public bool TryCompress(string inputPath, string outputPath, string? password = null)
    {
        if (!IsAvailable) return false;

        var startInfo = new ProcessStartInfo
        {
            FileName = _qpdfPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        if (!string.IsNullOrEmpty(password))
            startInfo.ArgumentList.Add($"--password={password}");

        startInfo.ArgumentList.Add("--object-streams=generate");
        startInfo.ArgumentList.Add("--compress-streams=y");
        startInfo.ArgumentList.Add("--recompress-flate");
        startInfo.ArgumentList.Add("--compression-level=9");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add(outputPath);

        using var proc = Process.Start(startInfo);
        if (proc is null) return false;

        _ = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return false;
        }

        return (proc.ExitCode == 0 || proc.ExitCode == 3) && File.Exists(outputPath);
    }
}