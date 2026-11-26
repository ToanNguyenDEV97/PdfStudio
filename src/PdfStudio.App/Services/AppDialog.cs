using System.Windows;
using PdfStudio.App.Views;

namespace PdfStudio.App.Services;

public static class AppDialog
{
    public static void Info(string message, string title = "Thông báo") =>
        ShowMessage(title, message, AppDialogType.Info, primaryText: "Đã hiểu");

    public static void Success(string message, string title = "Thành công") =>
        ShowMessage(title, message, AppDialogType.Success, primaryText: "Đóng");

    public static void Warning(string message, string title = "Cảnh báo") =>
        ShowMessage(title, message, AppDialogType.Warning, primaryText: "Đã hiểu");

    public static void Error(string message, string title = "Đã xảy ra lỗi") =>
        ShowMessage(title, message, AppDialogType.Error, primaryText: "Đóng");

    public static bool Confirm(
        string message,
        string title = "Xác nhận",
        string confirmText = "Xác nhận",
        string cancelText = "Hủy")
    {
        var result = ShowMessage(
            title,
            message,
            AppDialogType.Warning,
            primaryText: confirmText,
            cancelText: cancelText);

        return result == MessageBoxResult.Yes;
    }

    public static MessageBoxResult ConfirmSave(string fileName, string actionDescription) =>
        ShowMessage(
            title: "Chưa lưu thay đổi",
            message: $"Tài liệu \"{fileName}\" có ghi chú chưa được lưu.\nBạn có muốn lưu trước khi {actionDescription} không?",
            type: AppDialogType.Question,
            primaryText: "Lưu",
            secondaryText: "Không lưu",
            cancelText: "Hủy");

    public static string? Prompt(string title, string message, string defaultValue = "", bool isPassword = false)
    {
        var dialog = new PromptDialog(title, message, defaultValue, isPassword);
        AttachOwner(dialog);
        return dialog.ShowDialog() == true ? dialog.InputValue : null;
    }

    private static MessageBoxResult ShowMessage(
        string title,
        string message,
        AppDialogType type,
        string primaryText,
        string? secondaryText = null,
        string? cancelText = null)
    {
        var dialog = new MessageDialog(title, message, type, primaryText, secondaryText, cancelText);
        AttachOwner(dialog);
        dialog.ShowDialog();
        return dialog.Result;
    }

    private static void AttachOwner(Window dialog)
    {
        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive && w.IsVisible && w != dialog)
            ?? Application.Current?.MainWindow;

        if (owner is not null && owner != dialog && owner.IsLoaded && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}