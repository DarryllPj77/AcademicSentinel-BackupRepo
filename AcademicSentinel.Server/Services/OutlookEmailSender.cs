using System.Net;
using System.Net.Mail;

namespace AcademicSentinel.Server.Services;

public class OutlookEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;

    public OutlookEmailSender(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public Task SendPasswordResetCodeAsync(string toEmail, string code) =>
        SendCodeEmailAsync(
            toEmail,
            "AcademicSentinel Password Reset Code",
            $"Your AcademicSentinel password-reset code is: {code}\n\n" +
            $"Enter this code in the AcademicSentinel app to continue resetting your password.\n" +
            $"This code will expire in 10 minutes. If you did not request a password reset, you can ignore this email.");

    public Task SendEmailVerificationCodeAsync(string toEmail, string code) =>
        SendCodeEmailAsync(
            toEmail,
            "AcademicSentinel Email Verification Code",
            $"Your AcademicSentinel email-verification code is: {code}\n\n" +
            $"Enter this code in the AcademicSentinel app to finish creating your account.\n" +
            $"This code will expire in 10 minutes. If you did not start a registration, you can ignore this email.");

    /// <summary>
    /// Shared SMTP transport used by both flows. Mail credentials are
    /// read from configuration (env vars or appsettings):
    ///   Email:SmtpHost / Email:SmtpPort
    ///   Email:From     — visible sender address
    ///   Email:Username / Email:Password
    /// On send failure, throws InvalidOperationException with the
    /// SMTP host/port + the underlying SmtpException message so the
    /// controller can decide whether to fail the API call.
    /// </summary>
    private async Task SendCodeEmailAsync(string toEmail, string subject, string body)
    {
        var configuredHost = _configuration["Email:SmtpHost"];
        var from = _configuration["Email:From"] ?? string.Empty;
        var username = _configuration["Email:Username"] ?? string.Empty;
        var password = _configuration["Email:Password"] ?? string.Empty;
        var portValue = _configuration["Email:SmtpPort"];

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Email settings are not configured. Set Email:From, Email:Username and Email:Password via User Secrets or environment variables.");
        }

        var host = string.IsNullOrWhiteSpace(configuredHost)
            ? ResolveSmtpHostFromEmail(username)
            : configuredHost;

        int port = 587;
        if (!string.IsNullOrWhiteSpace(portValue) && int.TryParse(portValue, out var parsedPort))
        {
            port = parsedPort;
        }

        using var message = new MailMessage(from, toEmail)
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };

        using var smtp = new SmtpClient(host, port)
        {
            EnableSsl = true,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(username, password)
        };

        try
        {
            await smtp.SendMailAsync(message);
        }
        catch (SmtpException smtpEx)
        {
            throw new InvalidOperationException(
                $"SMTP authentication failed for user '{username}' on host '{host}:{port}'. " +
                $"Ensure you're using a Gmail App Password, not your regular password. " +
                $"Details: {smtpEx.Message}", smtpEx);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to send email via SMTP. Host: {host}:{port}, From: {from}, To: {toEmail}. " +
                $"Error: {ex.Message}", ex);
        }
    }

    private static string ResolveSmtpHostFromEmail(string email)
    {
        var domain = email.Split('@').LastOrDefault()?.ToLowerInvariant() ?? string.Empty;

        return domain switch
        {
            "gmail.com" => "smtp.gmail.com",
            "outlook.com" or "hotmail.com" or "live.com" or "office365.com" => "smtp.office365.com",
            _ => "smtp.office365.com"
        };
    }
}
