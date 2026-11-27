namespace PdfStudio.Core;

public sealed class PdfPasswordRequiredException : Exception
{
    public bool IsIncorrectPassword { get; }

    public PdfPasswordRequiredException(bool isIncorrectPassword, Exception? innerException = null)
        : base(isIncorrectPassword ? "Mật khẩu không chính xác." : "Tài liệu PDF yêu cầu mật khẩu để mở.", innerException)
    {
        IsIncorrectPassword = isIncorrectPassword;
    }
}