namespace AcademicSentinel.Server.Services;

public interface IEmailSender
{
    /// <summary>
    /// Sends a 6-digit forgot-password reset code. Subject and body
    /// are templated server-side; the recipient address, the
    /// recipient's display name (rendered at the top of the body) and
    /// the code are caller-controlled.
    /// </summary>
    Task SendPasswordResetCodeAsync(string toEmail, string fullName, string code);

    /// <summary>
    /// Sends a 6-digit registration email-verification code. Same
    /// transport, different subject/body so the recipient can tell
    /// the two flows apart in their inbox.
    /// </summary>
    Task SendEmailVerificationCodeAsync(string toEmail, string code);
}
