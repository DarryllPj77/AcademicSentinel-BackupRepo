namespace AcademicSentinel.Client.Constants
{
    public static class ApiEndpoints
    {
        // ========================================================
        // MAIN SERVER URL
        // ========================================================
        //public const string BaseUrl = "http://206.189.82.67";
        public const string BaseUrl = "https://localhost:7123"; // for testing

        // ========================================================
        // AUTHENTICATION
        // ========================================================
        public const string AuthRegister = $"{BaseUrl}/api/auth/register";
        public const string AuthLogin = $"{BaseUrl}/api/auth/login";
        public const string AuthProfile = $"{BaseUrl}/api/auth/profile";
        public const string AuthChangePassword = $"{BaseUrl}/api/auth/change-password";

        // ========================================================
        // EMAIL VERIFICATION (registration)
        // ========================================================
        // These endpoints are called sequentially right after Register
        // returns 200. The user enters the 6-digit code that arrived
        // in their institutional inbox; the account is unusable for
        // login until verify-email-code returns success.
        public const string AuthVerifyEmailCode = $"{BaseUrl}/api/auth/verify-email-code";
        public const string AuthResendVerificationCode = $"{BaseUrl}/api/auth/resend-verification-code";

        // ========================================================
        // PASSWORD RESET
        // ========================================================
        public const string AuthForgotPassword = $"{BaseUrl}/api/auth/forgot-password";
        public const string AuthVerifyResetCode = $"{BaseUrl}/api/auth/verify-reset-code";
        public const string AuthResetPassword = $"{BaseUrl}/api/auth/reset-password";

        // ========================================================
        // OTHER
        // ========================================================
        public const string Rooms = $"{BaseUrl}/api/rooms";

        // Past Sessions Trash — soft-deletes a session archive.
        // Server keeps the row until ArchiveCleanupService purges
        // it after the configured retention window (15 or 30 days).
        // Format: append the sessionId to this prefix.
        public const string RoomsSessionDeletePrefix = $"{BaseUrl}/api/rooms/sessions";
    }
}