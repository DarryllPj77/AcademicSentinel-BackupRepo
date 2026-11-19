using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Models;

namespace AcademicSentinel.Client.Services
{
    public static class SessionManager
    {
        // This will hold our JWT Token while the app is running
        public static string JwtToken { get; set; } = string.Empty;

        // This holds the info of the currently logged-in Teacher
        public static UserResponseDto CurrentUser { get; set; } = null;

        // A quick check to see if we are logged in
        public static bool IsLoggedIn => !string.IsNullOrEmpty(JwtToken);

        // Local-only clear (legacy callers). Prefer LogoutAsync so the
        // server's single-device lock (User.IsLoggedIn) gets released
        // properly; otherwise the account stays locked until the 8-hour
        // stale-lock window elapses on the server side.
        public static void Logout()
        {
            // Fire-and-forget the server-side logout so existing
            // synchronous callers (BtnLogout_Checked etc.) still
            // release the lock without needing to be refactored.
            // Exceptions are swallowed — local state must always be
            // cleared even if the server call fails (e.g. offline).
            var token = JwtToken;
            if (!string.IsNullOrEmpty(token))
            {
                _ = NotifyServerLogoutAsync(token);
            }
            JwtToken = string.Empty;
            CurrentUser = null;
        }

        /// <summary>
        /// Awaitable variant for callers that want to guarantee the
        /// server-side lock is released before navigating (e.g. when
        /// the next screen tries to /login on the same account).
        /// </summary>
        public static async Task LogoutAsync()
        {
            var token = JwtToken;
            JwtToken = string.Empty;
            CurrentUser = null;
            if (!string.IsNullOrEmpty(token))
                await NotifyServerLogoutAsync(token);
        }

        private static async Task NotifyServerLogoutAsync(string token)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                await http.PostAsync(ApiEndpoints.AuthLogout, content: null);
            }
            catch
            {
                // Best-effort: the local session is already wiped. If the
                // server call fails (offline, server down, etc.) the
                // stale-lock window will eventually release the row.
            }
        }
    }
}
