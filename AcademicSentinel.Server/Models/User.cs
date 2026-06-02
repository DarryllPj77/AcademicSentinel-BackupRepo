namespace AcademicSentinel.Server.Models;

public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    //We now store a Hash, never the real password!
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "Student";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Profile image storage
    public string? ProfileImageUrl { get; set; } // URL to stored image
    public string? ProfileImagePath { get; set; } // Local file path
    public string? ProfileImageContentType { get; set; } // MIME type (image/png, image/jpeg, etc.)
    public long? ProfileImageSize { get; set; } // File size in bytes
    public DateTime? ProfileImageUploadedAt { get; set; } // When the image was uploaded

    // Forgot-password flow fields
    public string? PasswordResetCodeHash { get; set; }
    public DateTime? PasswordResetCodeExpiresAt { get; set; }
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetTokenExpiresAt { get; set; }
    public int PasswordResetAttempts { get; set; } = 0;
    public DateTime? LastResetCodeSentAt { get; set; }

    // Email-verification flow fields (registration → enter code → verified).
    // Login is blocked while IsEmailVerified == false. Code is stored
    // BCrypt-hashed; attempts are counted and capped per code; cooldown
    // between resends is enforced via LastVerificationCodeSentAt.
    public bool IsEmailVerified { get; set; } = false;
    public string? EmailVerificationCodeHash { get; set; }
    public DateTime? EmailVerificationExpiresAt { get; set; }
    public int EmailVerificationAttempts { get; set; } = 0;
    public DateTime? LastVerificationCodeSentAt { get; set; }

    // Single-device session lock.
    //   IsLoggedIn   — set true on a successful /api/auth/login,
    //                  cleared by /api/auth/logout.
    //   LastLoginAt  — UTC stamp of the last successful login.
    //                  Used as a stale-lock safety: if a client crashed
    //                  without calling /logout, the row would otherwise
    //                  be permanently locked. Once LastLoginAt is older
    //                  than the JWT lifetime (8h), the next /login can
    //                  reclaim the row because any token previously
    //                  issued has already expired.
    // The Login endpoint enforces the lock; the RoomsController layer
    // adds defence-in-depth by refusing /request-join when the
    // participant row is already Connected.
    public bool IsLoggedIn { get; set; } = false;
    public DateTime? LastLoginAt { get; set; }
}
