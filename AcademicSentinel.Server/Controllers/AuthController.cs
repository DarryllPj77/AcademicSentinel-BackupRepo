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
            Role = user.Role,
            // Clean assignment without citation tags
            ProfileImageUrl = user.ProfileImageUrl
        });
    }

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

        if (!HasResolvableDomain(normalizedEmail))
        {
            return BadRequest("Please use a legitimate email domain (e.g., Google, Outlook, or your school domain).");
        }

        if (await _context.Users.AnyAsync(u => u.Email == normalizedEmail))
        {
            return BadRequest("Email is already registered.");
        }

        // 2. Hash the password using BCrypt
        string passwordHash = BCrypt.Net.BCrypt.HashPassword(registerDto.Password);

        // 3. Create the user object
        var user = new User
        {
            FullName = registerDto.FullName.Trim(),
            Email = normalizedEmail,
            PasswordHash = passwordHash,
            Role = registerDto.Role,
            CreatedAt = DateTime.UtcNow
        };

        // 4. Save to Database
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Registration successful!" });
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
        if (user == null)
        {
            return Ok(new { message = "If the account exists, a verification code has been sent." });
        }

        var code = GenerateSixDigitCode();
        user.PasswordResetCodeHash = BCrypt.Net.BCrypt.HashPassword(code);
        user.PasswordResetCodeExpiresAt = DateTime.UtcNow.AddMinutes(10);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiresAt = null;

        await _context.SaveChangesAsync();

        try
        {
            await _emailSender.SendPasswordResetCodeAsync(user.Email, code);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send reset code email to {Email}", user.Email);
            return StatusCode(StatusCodes.Status500InternalServerError, $"Failed to send verification email: {ex.Message}");
        }

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

        if (!BCrypt.Net.BCrypt.Verify(dto.Code, user.PasswordResetCodeHash))
        {
            return Unauthorized("Invalid or expired verification code.");
        }

        var resetToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        user.PasswordResetToken = resetToken;
        user.PasswordResetTokenExpiresAt = DateTime.UtcNow.AddMinutes(15);
        user.PasswordResetCodeHash = null;
        user.PasswordResetCodeExpiresAt = null;

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