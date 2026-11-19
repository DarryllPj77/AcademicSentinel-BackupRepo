namespace AcademicSentinel.Client.Models
{
    public class UserResponseDto
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty; // Restored this!
        public string Role { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string? ProfileImageUrl { get; set; }
    }

    public class UserLoginDto
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        // Hardware binding for the single-device lock — sent as
        // Environment.MachineName so the server can distinguish a
        // ghost-lock recovery (same machine relaunching after a
        // crash) from a concurrent-login attempt (different machine).
        public string DeviceId { get; set; } = string.Empty;
    }

    public class UserRegisterDto
    {
        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Role { get; set; } = "Student";
    }

    public class StudentListItem
    {
        public string StudentId { get; set; } = string.Empty;
        public string StudentName { get; set; } = string.Empty;
        public string StudentYear { get; set; } = string.Empty;
        public string StudentCourse { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string EnrollmentSource { get; set; } = string.Empty;
        public string ParticipationStatus { get; set; } = string.Empty;
    }

    // Restored Profile and Change Password DTOs!
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

    // NEW: Password Reset Feature DTOs (Fixed Naming!)
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
        public bool Success { get; set; } // <-- Add this line!
        public string ResetToken { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class ResetPasswordRequestDto
    {
        public string Email { get; set; } = string.Empty;
        public string ResetToken { get; set; } = string.Empty; // Using ResetToken to match standard feature patterns
        public string NewPassword { get; set; } = string.Empty;
    }
}