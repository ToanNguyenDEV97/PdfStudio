using System.IO;
using System.Windows;
using System.Windows.Threading;
using PdfStudio.App.Services;

namespace PdfStudio.App;

public partial class App : Application
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfStudio",
        "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string logFile = WriteErrorLog("UI", e.Exception);
        string msg = $"Ứng dụng gặp lỗi không mong muốn:\n{e.Exception.Message}\n\nChi tiết đã được ghi tại:\n{logFile}";

        try
        {
            AppDialog.Error(msg, "PdfStudio - Thông báo lỗi");
        }
        catch
        {
            MessageBox.Show(msg, "PdfStudio - Thông báo lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            WriteErrorLog("Domain", ex);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteErrorLog("BackgroundTask", e.Exception);
        e.SetObserved();
    }

    private static string WriteErrorLog(string source, Exception ex)
    {
        string logPath = Path.Combine(LogDirectory, "error.log");
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}";
            File.AppendAllText(logPath, entry);
        }
        catch
        {
            // Bỏ qua nếu không có quyền ghi đĩa
        }
        return logPath;
    }
}