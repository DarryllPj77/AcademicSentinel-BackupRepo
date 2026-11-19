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

        // ============================================================
        // SINGLE-DEVICE SESSION LOCK (HARDWARE-BOUND)
        // ============================================================
        // Refuse a second concurrent login while the account row is
        // already marked IsLoggedIn. Lock identity is now the
        // CurrentDeviceId (Environment.MachineName from the WPF
        // client), not just a boolean — this closes the dashboard
        // gap where the previous SessionParticipants-based check
        // mistook a logged-in-but-idle user for a crashed one.
        //
        // Decision flow on a contested login:
        //   1. Stale-lock window (15 min) elapsed → reclaim silently.
        //   2. Same DeviceId → ghost-lock recovery. The user's
        //      previous session on THIS exact machine crashed; let
        //      them back in and overwrite the lock.
        //   3. Different DeviceId (or empty) → strict 409 refusal.
        //      They must log out of the other device first or wait
        //      out the stale window.
        //
        // Backup defences still in play:
        //   • DisconnectService.HandleDisconnectAsync clears the flag
        //     when the SAC's SignalR session is finalized.
        //   • The 15-minute stale-window backstop above releases
        //     accounts whose disconnect path never fired.
        const int staleLockMinutes = 15;
        bool lockIsStale = user.LastLoginAt.HasValue
            && (DateTime.UtcNow - user.LastLoginAt.Value).TotalMinutes >= staleLockMinutes;

        if (user.IsLoggedIn && !lockIsStale)
        {
            // Same physical machine — ghost recovery.
            bool sameDevice = !string.IsNullOrWhiteSpace(loginDto.DeviceId)
                && string.Equals(loginDto.DeviceId, user.CurrentDeviceId, StringComparison.OrdinalIgnoreCase);

            if (!sameDevice)
            {
                _logger.LogWarning(
                    "Login blocked for {Email} — account locked to device '{CurrentDevice}', attempted from '{NewDevice}'.",
                    user.Email,
                    string.IsNullOrEmpty(user.CurrentDeviceId) ? "(unknown)" : user.CurrentDeviceId,
                    string.IsNullOrEmpty(loginDto.DeviceId) ? "(unknown)" : loginDto.DeviceId);
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    code = "ALREADY_LOGGED_IN",
                    message = "This account is currently active on another device. Please log out of that device first."
                });
            }

            // Same machine — ghost-lock bypass. Logged at Info level
            // so a spike in these events surfaces a higher rate of
            // client crashes.
            _logger.LogInformation(
                "Ghost-lock bypass for {Email} (id={UserId}) — recovered from same device '{DeviceId}'.",
                user.Email, user.Id, loginDto.DeviceId);
        }

        // Claim the lock, bind it to this device, stamp the time.
        // Companion /logout clears all three; the stale-lock window
        // releases accounts that never reached /logout.
        user.IsLoggedIn       = true;
        user.LastLoginAt      = DateTime.UtcNow;
        user.CurrentDeviceId  = string.IsNullOrWhiteSpace(loginDto.DeviceId) ? null : loginDto.DeviceId;
        await _context.SaveChangesAsync();

        // PROTOTYPE: email-verification gate disabled because outbound
        // email delivery is not yet configured (Resend sandbox can
        // only deliver to one inbox until a domain is verified). When
        // SMTP delivery is restored, re-enable the block below.
        //
        // if (!user.IsEmailVerified)
        // {
        //     return StatusCode(StatusCodes.Status403Forbidden, new
        //     {
        //         code = "EMAIL_NOT_VERIFIED",
        //         message = "This account hasn't been verified yet. Please enter the code we sent to your institutional email."
        //     });
        // }

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

    // POST /api/auth/logout
    // Releases the single-device session lock claimed by /login.
    // Required for the new IsLoggedIn flow — without it a clean
    // sign-out would otherwise leave the row locked until the
    // stale-lock window (8h) elapsed. The endpoint is idempotent:
    // calling it twice / when not logged in is still 200 OK.
    // Defends against partial sign-outs by ignoring user-not-found
    // and clearing the flag whatever its current value.
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(userIdString, out var userId))
            return Ok(new { message = "Logged out." });

        var user = await _context.Users.FindAsync(userId);
        if (user != null)
        {
            user.IsLoggedIn      = false;
            user.CurrentDeviceId = null;   // release the hardware binding
            await _context.SaveChangesAsync();
            _logger.LogInformation("User {Email} logged out — single-device lock released.", user.Email);
        }
        return Ok(new { message = "Logged out." });
    }

    // PROTOTYPE: domain allowlist expanded to include gmail.com so
    // testers can register without an institutional account while
    // outbound email is unconfigured. Tighten back to the two
    // institutional domains when the email pipeline is restored.
    //
    // Institutional domains derive the role server-side (immune to
    // client-tampered role values). For the non-institutional
    // prototype domain (gmail.com) the role is honored from the
    // client request after a basic Student/Instructor validation —
    // this is safe ONLY because the gate is intentionally loose for
    // testing.
    private static readonly HashSet<string> _allowedRegistrationDomains =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "fit.edu.ph",
            "feutech.edu.ph",
            "gmail.com", // PROTOTYPE — remove when email is configured
        };

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
        // EMAIL DOMAIN ALLOWLIST (prototype).
        // ============================================================
        // Accepted domains: fit.edu.ph, feutech.edu.ph, gmail.com.
        // - Institutional domains derive the role server-side.
        // - gmail.com (prototype-only) honors the client-sent role
        //   after a Student/Instructor sanity check.
        var domain = normalizedEmail.Split('@').LastOrDefault() ?? string.Empty;
        if (!_allowedRegistrationDomains.Contains(domain))
        {
            return BadRequest(
                "Registration is restricted to @fit.edu.ph, @feutech.edu.ph, or @gmail.com.");
        }

        string derivedRole;
        if (_institutionalRoleByDomain.TryGetValue(domain, out var institutionalRole))
        {
            derivedRole = institutionalRole;
        }
        else
        {
            // Non-institutional prototype path. Trust the client's
            // role pick (validated to one of the two known values)
            // so testers can exercise both Student and Instructor
            // flows from a single @gmail.com test account pool.
            derivedRole = string.Equals(registerDto.Role, "Instructor", StringComparison.OrdinalIgnoreCase)
                ? "Instructor"
                : "Student";
        }

        if (string.IsNullOrWhiteSpace(registerDto.Password) || registerDto.Password.Length < 6)
        {
            return BadRequest("Password must be at least 6 characters long.");
        }

        // Orphan-safe registration:
        //   • verified institutional row → refuse (account already
        //     exists; the caller should sign in or — once email is
        //     configured — use Forgot Password).
        //   • verified @gmail.com row → ROTATE password/name/role on
        //     the SAME row. This is a PROTOTYPE-ONLY recovery path:
        //     SMTP is unconfigured so Forgot Password is disabled,
        //     and testers (defense panel) need a way out of a
        //     forgotten-password lockout without DB surgery. Limited
        //     to the prototype domain (@gmail.com) so institutional
        //     accounts cannot be silently overwritten by a stranger
        //     who guesses the email. Tighten back to the original
        //     "refuse all verified rows" rule when SMTP + Forgot
        //     Password are restored.
        //   • unverified row → rotate the code on the SAME row
        //     (overwrite name/password/code hash/expiry, reset
        //     attempts, restart cooldown). This eliminates the
        //     dead-end where a single transient SMTP failure used
        //     to permanently block self-service registration —
        //     the user just clicks Register again and the row
        //     self-heals with a fresh code.
        //   • no row → fall through to the normal create path.
        var existing = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        bool isPrototypeDomain = string.Equals(domain, "gmail.com", StringComparison.OrdinalIgnoreCase);
        if (existing != null && existing.IsEmailVerified && !isPrototypeDomain)
        {
            // PROTOTYPE: Forgot Password is disabled, so the recovery
            // hint is intentionally absent. Restore the "or use Forgot
            // Password" suffix when the reset flow is re-enabled.
            return BadRequest("This email is already registered. Please sign in instead.");
        }

        // Cooldown protects both the create AND rotate paths from
        // register-spam (which would otherwise let an attacker
        // mail-bomb a victim by hitting Register in a loop).
        if (existing != null
            && existing.LastVerificationCodeSentAt.HasValue
            && (DateTime.UtcNow - existing.LastVerificationCodeSentAt.Value).TotalSeconds < ResendCooldownSeconds)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                message = $"Please wait {ResendCooldownSeconds} seconds before requesting another verification code."
            });
        }

        string passwordHash = BCrypt.Net.BCrypt.HashPassword(registerDto.Password);
        var code = GenerateSixDigitCode();

        // PROTOTYPE: outbound email is not yet configured, so accounts
        // are auto-verified at creation. The verification code fields
        // are left null/zero — when real email delivery is restored,
        // restore the BCrypt.HashPassword(code) + expiry assignments
        // below, set IsEmailVerified back to false, and re-enable the
        // SendEmailVerificationCodeAsync block.
        User user;
        if (existing != null)
        {
            bool wasVerified = existing.IsEmailVerified;
            existing.FullName                    = registerDto.FullName.Trim();
            existing.PasswordHash                = passwordHash;
            existing.Role                        = derivedRole;
            existing.IsEmailVerified             = true;   // PROTOTYPE
            existing.EmailVerificationCodeHash   = null;
            existing.EmailVerificationExpiresAt  = null;
            existing.EmailVerificationAttempts   = 0;
            existing.LastVerificationCodeSentAt  = DateTime.UtcNow;
            user = existing;
            if (wasVerified)
            {
                // Distinct log line for the password-recovery path so a
                // reviewer can audit prototype rotations separately from
                // first-time auto-verifications.
                _logger.LogWarning(
                    "Register (prototype): ROTATING password for verified @gmail.com account {Email} (role={Role}). Prototype-only recovery path.",
                    user.Email, derivedRole);
            }
            else
            {
                _logger.LogInformation(
                    "Register (prototype): auto-verifying existing unverified account {Email} (role={Role}).",
                    user.Email, derivedRole);
            }
        }
        else
        {
            user = new User
            {
                FullName     = registerDto.FullName.Trim(),
                Email        = normalizedEmail,
                PasswordHash = passwordHash,
                Role         = derivedRole,
                CreatedAt    = DateTime.UtcNow,
                IsEmailVerified              = true,        // PROTOTYPE
                EmailVerificationCodeHash    = null,
                EmailVerificationExpiresAt   = null,
                EmailVerificationAttempts    = 0,
                LastVerificationCodeSentAt   = DateTime.UtcNow,
            };
            _context.Users.Add(user);
        }

        await _context.SaveChangesAsync();

        // PROTOTYPE: SMTP send intentionally skipped. The previous
        // SendEmailVerificationCodeAsync(user.Email, code) call lived
        // here. Restore it when outbound email is configured.
        _ = code; // unused in prototype mode

        _logger.LogInformation(
            "User {Email} registered (PROTOTYPE auto-verified, role={Role}).",
            user.Email, derivedRole);

        return Ok(new
        {
            message = "Account ready. You can sign in now.",
            email   = user.Email,
            role    = derivedRole,
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

    // PROTOTYPE: the reset code is delivered by email, which is
    // unavailable in this build. Short-circuit with 503 + a stable
    // machine-readable code so the client can render the prototype
    // message without parsing free text. Re-enable the full body
    // below by removing the early return when SMTP is restored.
    private static IActionResult PrototypeForgotDisabledResponse()
        => new ObjectResult(new
        {
            code    = "PROTOTYPE_FORGOT_DISABLED",
            message = "Password reset is temporarily unavailable in this prototype build."
        })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequestDto dto)
    {
        _logger.LogInformation(
            "ForgotPassword: rejected — PROTOTYPE_FORGOT_DISABLED (outbound email not configured).");
        return PrototypeForgotDisabledResponse();

#pragma warning disable CS0162 // Unreachable code — kept for one-line reactivation when email is configured.
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
            await _emailSender.SendPasswordResetCodeAsync(user.Email, user.FullName, code);
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
#pragma warning restore CS0162
    }

    [HttpPost("verify-reset-code")]
    public async Task<IActionResult> VerifyResetCode([FromBody] VerifyResetCodeRequestDto dto)
    {
        // PROTOTYPE: forgot-password flow is disabled; this endpoint
        // is unreachable through the UI but guarded here too so a
        // direct API caller gets the same clear message.
        _logger.LogInformation(
            "VerifyResetCode: rejected — PROTOTYPE_FORGOT_DISABLED (outbound email not configured).");
        return PrototypeForgotDisabledResponse();

#pragma warning disable CS0162
        var normalizedEmail = NormalizeEmail(dto.Email);
        if (string.IsNullOrWhiteSpace(normalizedEmail) || string.IsNullOrWhiteSpace(dto.Code))
        {
            return BadRequest("Email and code are required.");
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        // Entry-line diagnostic. Logs only non-sensitive metadata: the
        // length of the entered code (NOT the code itself), whether a
        // reset-code hash currently exists for this user, the stored
        // expiry timestamp, and the attempt counter. Together with the
        // per-branch reject logs below this is enough to identify
        // which of the four 401 paths a real failure hits without ever
        // writing the secret to disk.
        _logger.LogInformation(
            "VerifyResetCode: email={Email} codeLength={CodeLength} hashPresent={HashPresent} expiresAt={ExpiresAt:O} attempts={Attempts}",
            normalizedEmail,
            dto.Code?.Length ?? 0,
            user != null && !string.IsNullOrWhiteSpace(user.PasswordResetCodeHash),
            user?.PasswordResetCodeExpiresAt,
            user?.PasswordResetAttempts);

        if (user == null || string.IsNullOrWhiteSpace(user.PasswordResetCodeHash) || user.PasswordResetCodeExpiresAt == null)
        {
            _logger.LogInformation(
                "VerifyResetCode: rejecting {Email} — no active reset state (userFound={UserFound}, hashPresent={HashPresent}, expiryPresent={ExpiryPresent}).",
                normalizedEmail,
                user != null,
                user != null && !string.IsNullOrWhiteSpace(user.PasswordResetCodeHash),
                user?.PasswordResetCodeExpiresAt != null);
            return Unauthorized("Invalid or expired verification code.");
        }

        if (user.PasswordResetCodeExpiresAt < DateTime.UtcNow)
        {
            _logger.LogInformation(
                "VerifyResetCode: rejecting {Email} — code expired at {ExpiresAt:O} (now {Now:O}).",
                user.Email, user.PasswordResetCodeExpiresAt, DateTime.UtcNow);
            user.PasswordResetCodeHash = null;
            user.PasswordResetCodeExpiresAt = null;
            await _context.SaveChangesAsync();
            return Unauthorized("Invalid or expired verification code.");
        }

        if (user.PasswordResetAttempts >= MaxCodeAttempts)
        {
            _logger.LogInformation(
                "VerifyResetCode: rejecting {Email} — attempt counter locked at {Attempts}/{Max}; code cleared, fresh /forgot-password required.",
                user.Email, user.PasswordResetAttempts, MaxCodeAttempts);
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
            _logger.LogInformation(
                "VerifyResetCode: rejecting {Email} — BCrypt mismatch (attempt {Attempt}/{Max}).",
                user.Email, user.PasswordResetAttempts, MaxCodeAttempts);
            return Unauthorized("Invalid or expired verification code.");
        }

        var resetToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        user.PasswordResetToken          = resetToken;
        user.PasswordResetTokenExpiresAt = DateTime.UtcNow.AddMinutes(15);
        user.PasswordResetCodeHash       = null;
        user.PasswordResetCodeExpiresAt  = null;
        user.PasswordResetAttempts       = 0;

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "VerifyResetCode: accepted {Email} — reset token issued (expires {TokenExpiresAt:O}).",
            user.Email, user.PasswordResetTokenExpiresAt);

        return Ok(new VerifyResetCodeResponseDto
        {
            Success = true,
            ResetToken = resetToken
        });
#pragma warning restore CS0162
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequestDto dto)
    {
        // PROTOTYPE: reset flow disabled (see ForgotPassword).
        _logger.LogInformation(
            "ResetPassword: rejected — PROTOTYPE_FORGOT_DISABLED (outbound email not configured).");
        return PrototypeForgotDisabledResponse();

#pragma warning disable CS0162
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
#pragma warning restore CS0162
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