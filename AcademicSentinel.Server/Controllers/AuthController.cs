using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AcademicSentinel.Server.Data;
using AcademicSentinel.Server.Models;
using AcademicSentinel.Server.DTOs;
using AcademicSentinel.Server.Services;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace AcademicSentinel.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<AuthController> _logger;

    public AuthController(AppDbContext context, IConfiguration configuration, IEmailSender emailSender, ILogger<AuthController> logger)
    {
        _context = context;
        _configuration = configuration;
        _emailSender = emailSender;
        _logger = logger;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] UserLoginDto loginDto)
    {
        var normalizedEmail = NormalizeEmail(loginDto.Email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return Unauthorized("Invalid email or password.");
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        if (user == null || !BCrypt.Net.BCrypt.Verify(loginDto.Password, user.PasswordHash))
        {
            return Unauthorized("Invalid email or password.");
        }

        // Block login until the registration code has been verified.
        // 403 (distinct from the 401 above) lets the client recognise
        // this specific state and route the user to the
        // verify-email-code screen instead of telling them their
        // credentials are bad.
        if (!user.IsEmailVerified)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "EMAIL_NOT_VERIFIED",
                message = "This account hasn't been verified yet. Please enter the code we sent to your institutional email."
            });
        }

        var authClaims = new List<Claim>
    {
        new Claim(ClaimTypes.Name, user.Email),
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Role, user.Role),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
    };

        var authSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            expires: DateTime.Now.AddHours(8), // spec v4: 8-hour token lifetime
            claims: authClaims,
            signingCredentials: new SigningCredentials(authSigningKey, SecurityAlgorithms.HmacSha256)
        );

        return Ok(new
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            Id = user.Id,
            Email = user.Email,
            // FullName must be returned verbatim from the User row.
            // Previously this field was missing from the Login response
            // — the client's UserResponseDto then deserialized FullName
            // as "" and both dashboards (Student + Teacher) fell back
            // to Email.Split('@')[0], which produced names like
            // "student" or "test" instead of the actual registered
            // "student test" / "teacher test". Storage and the
            // /profile endpoint were always correct; only this
            // response shape was incomplete.
            FullName = user.FullName,
            Role = user.Role,
            // Clean assignment without citation tags
            ProfileImageUrl = user.ProfileImageUrl
        });
    }

    // Institutional-only registration allowlist. Anything outside
    // these two domains is rejected at the API boundary regardless of
    // what the client sends.
    //   @fit.edu.ph     → Student
    //   @feutech.edu.ph → Teacher (Instructor in our existing Role
    //                    vocabulary — the rest of the codebase expects
    //                    "Student" or "Instructor" so we map here.)
    private static readonly Dictionary<string, string> _institutionalRoleByDomain =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "fit.edu.ph",     "Student" },
            { "feutech.edu.ph", "Instructor" },
        };

    private const int VerificationCodeTtlMinutes = 10;
    private const int ResetCodeTtlMinutes        = 10;
    private const int MaxCodeAttempts            = 5;
    private const int ResendCooldownSeconds      = 30;

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] UserRegisterDto registerDto)
    {
        if (string.IsNullOrWhiteSpace(registerDto.FullName))
        {
            return BadRequest("Full name is required.");
        }

        var normalizedEmail = NormalizeEmail(registerDto.Email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return BadRequest("Please enter a valid email address.");
        }

        // ============================================================
        // INSTITUTIONAL EMAIL ENFORCEMENT.
        // ============================================================
        // Only @fit.edu.ph (Student) and @feutech.edu.ph (Instructor)
        // are accepted. The role is DERIVED from the domain server-
        // side; the role field the client sends is ignored to prevent
        // privilege-escalation by tampered clients.
        var domain = normalizedEmail.Split('@').LastOrDefault() ?? string.Empty;
        if (!_institutionalRoleByDomain.TryGetValue(domain, out var derivedRole))
        {
            return BadRequest(
                "Registration is restricted to institutional emails (@fit.edu.ph or @feutech.edu.ph).");
        }

        if (string.IsNullOrWhiteSpace(registerDto.Password) || registerDto.Password.Length < 6)
        {
            return BadRequest("Password must be at least 6 characters long.");
        }

        // Don't reveal whether the email is already registered to a
        // verified account vs. left in a pending-verification state.
        // If a row already exists, refuse the registration generically.
        if (await _context.Users.AnyAsync(u => u.Email == normalizedEmail))
        {
            return BadRequest("This email cannot be registered. If you already started, check your inbox for a verification code.");
        }

        string passwordHash = BCrypt.Net.BCrypt.HashPassword(registerDto.Password);

        var code = GenerateSixDigitCode();
        var user = new User
        {
            FullName     = registerDto.FullName.Trim(),
            Email        = normalizedEmail,
            PasswordHash = passwordHash,
            Role         = derivedRole,
            CreatedAt    = DateTime.UtcNow,
            // Email-verification: account starts unverified. Login is
            // gated on IsEmailVerified == true; verify-email-code is
            // the only path that flips it.
            IsEmailVerified              = false,
            EmailVerificationCodeHash    = BCrypt.Net.BCrypt.HashPassword(code),
            EmailVerificationExpiresAt   = DateTime.UtcNow.AddMinutes(VerificationCodeTtlMinutes),
            EmailVerificationAttempts    = 0,
            LastVerificationCodeSentAt   = DateTime.UtcNow,
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Send AFTER the row is committed so a transient SMTP failure
        // doesn't leave an orphan user we can't roll back to. If the
        // send fails we still keep the row — the user can request a
        // resend via /resend-verification-code.
        try
        {
            await _emailSender.SendEmailVerificationCodeAsync(user.Email, code);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send verification email to {Email}", user.Email);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Account created but the verification email could not be sent. Use Resend Code from the verification screen."
            });
        }

        _logger.LogInformation("User {Email} registered (pending verification, role={Role}).",
            user.Email, derivedRole);

        return Ok(new
        {
            message = "Verification code sent. Enter it in the AcademicSentinel app to finish registration.",
            email   = user.Email,
            role    = derivedRole,
            verificationExpiresAt = user.EmailVerificationExpiresAt
        });
    }

    [HttpPost("verify-email-code")]
    public async Task<IActionResult> VerifyEmailCode([FromBody] VerifyEmailCodeRequestDto dto)
    {
        var normalizedEmail = NormalizeEmail(dto?.Email);
        if (string.IsNullOrWhiteSpace(normalizedEmail) || string.IsNullOrWhiteSpace(dto?.Code))
        {
            return BadRequest("Email and verification code are required.");
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        if (user == null
            || user.IsEmailVerified
            || string.IsNullOrWhiteSpace(user.EmailVerificationCodeHash)
            || user.EmailVerificationExpiresAt == null)
        {
            // Same generic response for "no such pending account" and
            // "bad code" to avoid leaking which emails are registered.
            return Unauthorized("Invalid or expired verification code.");
        }

        if (user.EmailVerificationExpiresAt < DateTime.UtcNow)
        {
            user.EmailVerificationCodeHash  = null;
            user.EmailVerificationExpiresAt = null;
            user.EmailVerificationAttempts  = 0;
            await _context.SaveChangesAsync();
            return Unauthorized("Invalid or expired verification code.");
        }

        if (user.EmailVerificationAttempts >= MaxCodeAttempts)
        {
            // Lock the current code; require a resend.
            user.EmailVerificationCodeHash  = null;
            user.EmailVerificationExpiresAt = null;
            user.EmailVerificationAttempts  = 0;
            await _context.SaveChangesAsync();
            return Unauthorized("Too many incorrect attempts. Request a new verification code.");
        }

        if (!BCrypt.Net.BCrypt.Verify(dto.Code, user.EmailVerificationCodeHash))
        {
            user.EmailVerificationAttempts++;
            await _context.SaveChangesAsync();
            return Unauthorized("Invalid or expired verification code.");
        }

        // Code valid → flip the row to verified, scrub all code state.
        user.IsEmailVerified             = true;
        user.EmailVerificationCodeHash   = null;
        user.EmailVerificationExpiresAt  = null;
        user.EmailVerificationAttempts   = 0;
        await _context.SaveChangesAsync();

        _logger.LogInformation("User {Email} verified email.", user.Email);

        return Ok(new { success = true, message = "Email verified. You can now sign in." });
    }

    [HttpPost("resend-verification-code")]
    public async Task<IActionResult> ResendVerificationCode([FromBody] ResendVerificationCodeRequestDto dto)
    {
        var normalizedEmail = NormalizeEmail(dto?.Email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return BadRequest("Email is required.");
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        // Generic response — never confirm whether the email exists or
        // whether the account is already verified.
        var genericResponse = Ok(new { message = "If the account exists and is pending verification, a new code has been sent." });

        if (user == null || user.IsEmailVerified) return genericResponse;

        // Resend cooldown — prevent code-flooding attacks and inbox abuse.
        if (user.LastVerificationCodeSentAt.HasValue
            && (DateTime.UtcNow - user.LastVerificationCodeSentAt.Value).TotalSeconds < ResendCooldownSeconds)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                message = $"Please wait {ResendCooldownSeconds} seconds between resend requests."
            });
        }

        var code = GenerateSixDigitCode();
        user.EmailVerificationCodeHash   = BCrypt.Net.BCrypt.HashPassword(code);
        user.EmailVerificationExpiresAt  = DateTime.UtcNow.AddMinutes(VerificationCodeTtlMinutes);
        user.EmailVerificationAttempts   = 0; // fresh code resets the attempt counter
        user.LastVerificationCodeSentAt  = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        try
        {
            await _emailSender.SendEmailVerificationCodeAsync(user.Email, code);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resend verification email to {Email}", user.Email);
            // Still return the generic response so we don't leak email
            // existence via a different status code.
            return genericResponse;
        }

        return genericResponse;
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequestDto dto)
    {
        var normalizedEmail = NormalizeEmail(dto.Email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return BadRequest("Email is required.");
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        // Always return generic success to avoid account enumeration.
        // Three internal log branches differentiate "no user" / "cooldown"
        // / "sent" for the server operator without changing the response
        // shape the client sees. The logs are info-level so they appear
        // in the same `dotnet run` console where the rest of the auth
        // diagnostics live.
        if (user == null)
        {
            _logger.LogInformation(
                "ForgotPassword: no user matched {Email} — generic 200 returned, no email sent.",
                normalizedEmail);
            return Ok(new { message = "If the account exists, a verification code has been sent." });
        }

        // Resend cooldown — prevent code-flooding attacks against the
        // reset endpoint. We DON'T branch the response on this so that
        // a hammering attacker can't use the 429 to confirm an email
        // exists; we just silently swallow the request. The log line
        // below makes the swallow VISIBLE to the operator so a tester
        // who clicks submit five times in a row understands why only
        // the first attempt produced an email.
        if (user.LastResetCodeSentAt.HasValue
            && (DateTime.UtcNow - user.LastResetCodeSentAt.Value).TotalSeconds < ResendCooldownSeconds)
        {
            _logger.LogInformation(
                "ForgotPassword: cooldown active for {Email} — silently swallowed (last sent at {LastSent:O}, {Elapsed:F1}s ago, threshold {Threshold}s).",
                normalizedEmail,
                user.LastResetCodeSentAt,
                (DateTime.UtcNow - user.LastResetCodeSentAt.Value).TotalSeconds,
                ResendCooldownSeconds);
            return Ok(new { message = "If the account exists, a verification code has been sent." });
        }

        var code = GenerateSixDigitCode();
        user.PasswordResetCodeHash       = BCrypt.Net.BCrypt.HashPassword(code);
        user.PasswordResetCodeExpiresAt  = DateTime.UtcNow.AddMinutes(ResetCodeTtlMinutes);
        user.PasswordResetAttempts       = 0;
        user.LastResetCodeSentAt         = DateTime.UtcNow;
        user.PasswordResetToken          = null;
        user.PasswordResetTokenExpiresAt = null;

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "ForgotPassword: sending reset code to {Email} (expires {ExpiresAt:O}).",
            user.Email, user.PasswordResetCodeExpiresAt);

        try
        {
            await _emailSender.SendPasswordResetCodeAsync(user.Email, code);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send reset code email to {Email}", user.Email);
            return StatusCode(StatusCodes.Status500InternalServerError, $"Failed to send verification email: {ex.Message}");
        }

        // Positive end-of-flow log. Together with the existing
        // "sending reset code" + "SMTP send attempt" + "SMTP send OK"
        // logs, this proves the controller reached the 200 OK return
        // without any swallowed exception between the SMTP call and
        // the response. After this line is reached, anything that
        // goes wrong is outside the application boundary — Gmail's
        // outbound delivery layer, FIT's Defender filter, the
        // recipient's mailbox rules.
        _logger.LogInformation(
            "ForgotPassword: end of happy path for {Email} — controller returning 200; mail handed off to Gmail relay.",
            user.Email);

        return Ok(new { message = "If the account exists, a verification code has been sent." });
    }

    [HttpPost("verify-reset-code")]
    public async Task<IActionResult> VerifyResetCode([FromBody] VerifyResetCodeRequestDto dto)
    {
        var normalizedEmail = NormalizeEmail(dto.Email);
        if (string.IsNullOrWhiteSpace(normalizedEmail) || string.IsNullOrWhiteSpace(dto.Code))
        {
            return BadRequest("Email and code are required.");
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        if (user == null || string.IsNullOrWhiteSpace(user.PasswordResetCodeHash) || user.PasswordResetCodeExpiresAt == null)
        {
            return Unauthorized("Invalid or expired verification code.");
        }

        if (user.PasswordResetCodeExpiresAt < DateTime.UtcNow)
        {
            user.PasswordResetCodeHash = null;
            user.PasswordResetCodeExpiresAt = null;
            await _context.SaveChangesAsync();
            return Unauthorized("Invalid or expired verification code.");
        }

        if (user.PasswordResetAttempts >= MaxCodeAttempts)
        {
            // Lock current code; require a new /forgot-password call.
            user.PasswordResetCodeHash      = null;
            user.PasswordResetCodeExpiresAt = null;
            user.PasswordResetAttempts      = 0;
            await _context.SaveChangesAsync();
            return Unauthorized("Too many incorrect attempts. Request a new reset code.");
        }

        if (!BCrypt.Net.BCrypt.Verify(dto.Code, user.PasswordResetCodeHash))
        {
            user.PasswordResetAttempts++;
            await _context.SaveChangesAsync();
            return Unauthorized("Invalid or expired verification code.");
        }

        var resetToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        user.PasswordResetToken          = resetToken;
        user.PasswordResetTokenExpiresAt = DateTime.UtcNow.AddMinutes(15);
        user.PasswordResetCodeHash       = null;
        user.PasswordResetCodeExpiresAt  = null;
        user.PasswordResetAttempts       = 0;

        await _context.SaveChangesAsync();

        return Ok(new VerifyResetCodeResponseDto
        {
            Success = true,
            ResetToken = resetToken
        });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequestDto dto)
    {
        var normalizedEmail = NormalizeEmail(dto.Email);
        if (string.IsNullOrWhiteSpace(normalizedEmail) || string.IsNullOrWhiteSpace(dto.NewPassword) || string.IsNullOrWhiteSpace(dto.ResetToken))
        {
            return BadRequest("Email, new password, and reset token are required.");
        }

        if (dto.NewPassword.Length < 6)
        {
            return BadRequest("Password must be at least 6 characters long.");
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        if (user == null || string.IsNullOrWhiteSpace(user.PasswordResetToken) || user.PasswordResetTokenExpiresAt == null)
        {
            return Unauthorized("Invalid or expired reset session.");
        }

        if (!string.Equals(user.PasswordResetToken, dto.ResetToken, StringComparison.Ordinal) || user.PasswordResetTokenExpiresAt < DateTime.UtcNow)
        {
            return Unauthorized("Invalid or expired reset session.");
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiresAt = null;

        await _context.SaveChangesAsync();

        return Ok(new { message = "Password reset successful." });
    }

    // ----------------------------------------------------------------
    // PROFILE ENDPOINTS (require authenticated user)
    // ----------------------------------------------------------------

    [Authorize]
    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null) return NotFound("User not found.");

        return Ok(new ProfileResponseDto
        {
            Id = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Role = user.Role,
            CreatedAt = user.CreatedAt,
            ProfileImageUrl = user.ProfileImageUrl
        });
    }

    [Authorize]
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileDto dto)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(dto.FullName))
            return BadRequest("Full name is required.");

        var normalizedEmail = NormalizeEmail(dto.Email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
            return BadRequest("Please enter a valid email address.");

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null) return NotFound("User not found.");

        // If email is changing, ensure it isn't taken by someone else.
        if (!string.Equals(user.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase))
        {
            bool taken = await _context.Users.AnyAsync(u => u.Email == normalizedEmail && u.Id != user.Id);
            if (taken) return BadRequest("That email address is already in use.");
        }

        user.FullName = dto.FullName.Trim();
        user.Email = normalizedEmail;

        await _context.SaveChangesAsync();

        _logger.LogInformation("User {UserId} updated profile.", user.Id);

        return Ok(new ProfileResponseDto
        {
            Id = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Role = user.Role,
            CreatedAt = user.CreatedAt,
            ProfileImageUrl = user.ProfileImageUrl
        });
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(dto.CurrentPassword) || string.IsNullOrWhiteSpace(dto.NewPassword))
            return BadRequest("Current and new passwords are required.");

        if (dto.NewPassword.Length < 6)
            return BadRequest("New password must be at least 6 characters long.");

        if (string.Equals(dto.CurrentPassword, dto.NewPassword, StringComparison.Ordinal))
            return BadRequest("New password must be different from the current password.");

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null) return NotFound("User not found.");

        if (!BCrypt.Net.BCrypt.Verify(dto.CurrentPassword, user.PasswordHash))
            return Unauthorized("Current password is incorrect.");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);

        // Invalidate any pending reset tokens to prevent stale-token reuse.
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiresAt = null;
        user.PasswordResetCodeHash = null;
        user.PasswordResetCodeExpiresAt = null;

        await _context.SaveChangesAsync();

        _logger.LogInformation("User {UserId} changed their password.", user.Id);

        return Ok(new { message = "Password changed successfully." });
    }

    private int? GetCurrentUserId()
    {
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(idClaim, out var id) ? id : null;
    }

    private static string GenerateSixDigitCode()
    {
        // Generates values from 100000 to 999999
        return RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
    }

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        try
        {
            var parsed = new MailAddress(email.Trim());
            return parsed.Address.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static bool HasResolvableDomain(string email)
    {
        try
        {
            var domain = email.Split('@').LastOrDefault();
            if (string.IsNullOrWhiteSpace(domain))
            {
                return false;
            }

            _ = Dns.GetHostEntry(domain);
            return true;
        }
        catch
        {
            return false;
        }
    }
}