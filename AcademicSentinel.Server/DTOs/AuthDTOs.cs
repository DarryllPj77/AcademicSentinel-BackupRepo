namespace AcademicSentinel.Server.DTOs;

// Used when the app sends us registration data
public class UserRegisterDto
{
    public string FullName { get; set; } = string.Empty; // Added this
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = "Student"; // Default role
}

// Used when the app sends us login data
public class UserLoginDto
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    // Hardware binding for the single-device lock. The WPF client
    // sends Environment.MachineName so AuthController.Login can
    // distinguish a ghost-lock recovery (same machine relaunching
    // after a crash) from a concurrent-login attempt (different
    // machine). Optional for back-compat; an empty value falls
    // through to the strict-block branch.
    public string DeviceId { get; set; } = string.Empty;
}

// Used when the server replies to the app (Notice: No password included!)
public class UserResponseDto
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

public class ForgotPasswordRequestDto
{
    public string Email { get; set; } = string.Empty;
}

public class VerifyResetCodeRequestDto
{
    public string Email { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public class VerifyResetCodeResponseDto
{
    public bool Success { get; set; }
    public string ResetToken { get; set; } = string.Empty;
}

public class ResetPasswordRequestDto
{
    public string Email { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ResetToken { get; set; } = string.Empty;
}

public class UpdateProfileDto
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class ChangePasswordDto
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class ProfileResponseDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? ProfileImageUrl { get; set; }
}