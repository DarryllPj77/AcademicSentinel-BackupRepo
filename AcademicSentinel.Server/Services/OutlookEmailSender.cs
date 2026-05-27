using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;

namespace AcademicSentinel.Server.Services;

/// <summary>
/// SMTP email sender for AcademicSentinel transactional mail
/// (registration verification codes + password-reset codes).
///
/// Configuration is read from <see cref="IConfiguration"/>, so the
/// values can be supplied either by appsettings.json (dev) or by
/// environment variables (PaaS / DigitalOcean droplet). The expected
/// keys mirror what a Linux env file would set:
///
///   Email__From       — "Display Name &lt;addr@example.com&gt;" or just "addr@example.com"
///   Email__Username   — SMTP login (for Gmail, the same Gmail address)
///   Email__Password   — SMTP password (for Gmail, an APP password — NOT the account password)
///   Email__Host       — SMTP server hostname (e.g., smtp.gmail.com)
///   Email__Port       — SMTP port (587 for STARTTLS, 465 for implicit TLS)
///   Email__EnableSsl  — "true"/"false" (defaults to true)
///
/// Legacy aliases <c>Email:SmtpHost</c> / <c>Email:SmtpPort</c> are
/// still honoured so existing deployments don't break on upgrade —
/// the new keys take precedence when both are present.
///
/// The class name (OutlookEmailSender) is historical and unchanged
/// to keep the DI registration in Program.cs stable; it now sends
/// through Gmail (or any other SMTP server) just as happily.
/// </summary>
public class OutlookEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<OutlookEmailSender> _logger;

    // ILogger<T> is auto-resolved by the framework — no Program.cs
    // change needed beyond the existing AddTransient<IEmailSender>.
    public OutlookEmailSender(IConfiguration configuration, ILogger<OutlookEmailSender> logger)
    {
        _configuration = configuration;
        _logger        = logger;
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
    /// Shared SMTP transport for both verification and reset flows.
    /// Errors are surfaced as <see cref="InvalidOperationException"/>
    /// with deliberately sanitised messages — the SMTP password is
    /// never included, and the username is only included for the
    /// auth-failure branch where the caller genuinely needs to know
    /// which account couldn't log in.
    /// </summary>
    private async Task SendCodeEmailAsync(string toEmail, string subject, string body)
    {
        var from     = _configuration["Email:From"]     ?? string.Empty;
        var username = _configuration["Email:Username"] ?? string.Empty;
        var password = _configuration["Email:Password"] ?? string.Empty;

        // Prefer the new key names but accept the legacy SmtpHost /
        // SmtpPort spellings so a half-migrated deployment still works.
        var hostConfig = _configuration["Email:Host"] ?? _configuration["Email:SmtpHost"];
        var portConfig = _configuration["Email:Port"] ?? _configuration["Email:SmtpPort"];
        var sslConfig  = _configuration["Email:EnableSsl"];

        if (string.IsNullOrWhiteSpace(from)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(password)
            || string.IsNullOrWhiteSpace(hostConfig))
        {
            throw new InvalidOperationException(
                "Email settings are not configured. Set Email__From, Email__Username, " +
                "Email__Password and Email__Host via environment variables (see .env.example).");
        }

        if (!int.TryParse(portConfig, out int port) || port <= 0)
        {
            port = 587; // STARTTLS default — what Gmail recommends.
        }

        bool enableSsl = true;
        if (!string.IsNullOrWhiteSpace(sslConfig)
            && bool.TryParse(sslConfig, out var parsedSsl))
        {
            enableSsl = parsedSsl;
        }

        // From may be "Display <addr@host>" or just "addr@host". Parse
        // it through MailAddress so the display name (e.g.
        // "AcademicSentinel") flows through to the recipient's inbox.
        // If the value can't be parsed we fail loudly rather than send
        // mail from a malformed sender.
        MailAddress fromAddress;
        try
        {
            fromAddress = new MailAddress(from);
        }
        catch (FormatException fe)
        {
            throw new InvalidOperationException(
                "Email__From is malformed. Expected one of:\n" +
                "  AcademicSentinel <AcademicSentinel4@gmail.com>\n" +
                "  AcademicSentinel4@gmail.com\n" +
                $"Parser said: {fe.Message}");
        }

        using var message = new MailMessage
        {
            From       = fromAddress,
            Subject    = subject,
            Body       = body,
            IsBodyHtml = false
        };
        message.To.Add(toEmail);

        using var smtp = new SmtpClient(hostConfig, port)
        {
            EnableSsl             = enableSsl,
            DeliveryMethod        = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials           = new NetworkCredential(username, password),
        };

        try
        {
            _logger.LogInformation(
                "SMTP send attempt: subject='{Subject}' to='{To}' host='{Host}:{Port}' ssl={Ssl}",
                subject, toEmail, hostConfig, port, enableSsl);

            await smtp.SendMailAsync(message);

            _logger.LogInformation(
                "SMTP send OK: subject='{Subject}' to='{To}' host='{Host}:{Port}' — Gmail relay accepted the message.",
                subject, toEmail, hostConfig, port);
        }
        catch (SmtpException smtpEx)
        {
            // Sanitised: include host/port + the SMTP layer's message,
            // but NEVER the password. Username is allowed because Gmail
            // auth failures are usually "wrong app password for this
            // account" and the user needs to know which account.
            throw new InvalidOperationException(
                $"SMTP send failed for user '{username}' on host '{hostConfig}:{port}'. " +
                $"If you're using Gmail, make sure Email__Password is an APP password " +
                $"(not your regular Gmail login). Details: {smtpEx.Message}", smtpEx);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to send email via SMTP. Host: {hostConfig}:{port}, To: {toEmail}. " +
                $"Error: {ex.Message}", ex);
        }
    }
}
