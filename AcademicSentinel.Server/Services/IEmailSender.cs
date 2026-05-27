namespace AcademicSentinel.Server.Services;

public interface IEmailSender
{
    /// <summary>
    /// Sends a 6-digit forgot-password verification code. Subject and
    /// body are templated server-side; only the recipient address and
    /// the code are caller-controlled.
    /// </summary>
    Task SendPasswordResetCodeAsync(string toEmail, string code);

    /// <summary>
    /// Sends a 6-digit registration email-verification code. Same
    /// transport, different subject/body so the recipient can tell
    /// the two flows apart in their inbox.
    /// </summary>
    Task SendEmailVerificationCodeAsync(string toEmail, string code);
}
